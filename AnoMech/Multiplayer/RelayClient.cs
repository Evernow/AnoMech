using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnoMech.Core;

namespace AnoMech.Multiplayer;

// Thin ClientWebSocket wrapper talking to AnoMech.Relay (see Relay/README.md).
// The relay only forwards opaque frames within a session code -- every message's
// meaning lives in MpMessage/Protocol.cs, not here. The frame's own WebSocket
// message type doubles as the compression flag: Text = raw UTF-8 JSON (small
// messages, where Brotli's own framing overhead would cost more than it saves --
// confirmed via measurement: a 108-byte SelfPoseMessage came out *larger* after
// gzip), Binary = Brotli-compressed JSON (everything at or above
// CompressionThresholdBytes, where a verbose/repetitive payload like a multi-enemy
// WorldSnapshotMessage compresses 5-15x). No new field on MpMessage needed -- the
// relay is a byte-and-type-preserving pipe either way, see Program.cs.
public sealed class RelayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int CompressionThresholdBytes = 256;

    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource cts = new();
    // ClientWebSocket allows only one outstanding SendAsync at a time; MultiplayerManager
    // fires sends from independent tick-rate timers (pose vs snapshot) that could otherwise
    // overlap, plus bursts of many sequential sends from a single native event (e.g. a
    // MapEffect hook firing once per changed tile). A SemaphoreSlim gate here does NOT
    // guarantee FIFO release order for waiters queued up behind it -- confirmed via
    // AnoMech-DamageDebug dumps: a 9-call MapEffect burst sent host-side in order 1-8,0
    // arrived peer-side as 7,1,3,0,2,5,4,6,8, leaving a peer's replicated arena-color
    // transition (P2 Forsaken's gold->black swap) visually wrong even though every
    // individual native call did eventually fire. A Channel's writer queue is strictly
    // FIFO: the enqueue in SendAsync below is synchronous, so callers invoking it
    // sequentially (as the MapEffect hook does, once per native call in the same frame)
    // get a deterministic wire order regardless of how many race to enqueue.
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource Completion)> sendQueue =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, TaskCompletionSource)>();
    // WorldSnapshotMessage alone can be large enough to noticeably hog the socket's
    // one-at-a-time send slot; a separate queue, drained only once sendQueue is
    // empty, keeps small/urgent messages from queuing behind it.
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource Completion)> bulkSendQueue =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, TaskCompletionSource)>();
    private bool disposed;

    public event Action<MpMessage>? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => socket.State == WebSocketState.Open;

    // Known purely from which scheme this connection actually dialed -- the relay
    // itself has no way to confirm this (see Program.cs: a wss:// setup terminates
    // TLS in a reverse proxy sitting in front of the relay process, invisible to
    // it), so this is the client's own knowledge, not something the relay attests to.
    public bool IsEncrypted { get; private set; }

    // Learned from the relay's own greeting (see ReadGreetingAsync). A named set
    // rather than a single "protocol version" number: any feature check a client
    // needs can ask for the one capability it actually cares about, rather than
    // every feature sharing one number that has to be bumped for all of them
    // together. An old relay (or one that just lacks a given feature) never
    // advertises it, so a missing entry always means "assume not supported" --
    // see SupportsCompression below for the one concrete check built on this today.
    public IReadOnlySet<string> RelayCapabilities { get; private set; } = new HashSet<string>();
    public bool HasRelayCapability(string name) => RelayCapabilities.Contains(name);
    public bool SupportsCompression => HasRelayCapability("binaryCompression");

    // Peer join, and every reconnect (host's own included -- see
    // MultiplayerManager.ReconnectLoopAsync, which always rejoins a known code
    // regardless of whether the original connection was made here or via
    // ConnectAndHostAsync below).
    public Task ConnectAsync(string baseUrl, string sessionCode) =>
        ConnectCoreAsync(BuildUri(baseUrl, $"session/{Uri.EscapeDataString(sessionCode)}"));

    // Requests a brand-new, relay-assigned session code instead of supplying one --
    // see Program.cs's /host endpoint. The relay owns the room namespace (Sessions),
    // so it's the only party that can actually guarantee no collision with an
    // existing session, unlike picking one locally at random and hoping. Returns
    // the assigned code, or null if the connect or greeting failed -- Disconnected
    // already fires in that case exactly like ConnectAsync, so callers only need to
    // treat a null return as "didn't work," not handle the failure separately.
    public Task<string?> ConnectAndHostAsync(string baseUrl) => ConnectCoreAsync(BuildUri(baseUrl, "host"));

    private static Uri BuildUri(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");

    private async Task<string?> ConnectCoreAsync(Uri uri)
    {
        IsEncrypted = uri.Scheme == "wss";
        try
        {
            await socket.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
            DiagnosticLog.Info($"[RelayClient] Connected to {uri}.");
        }
        catch (Exception e)
        {
            // Callers (MultiplayerManager.HostSession/JoinSession) fire this
            // fire-and-forget -- without catching here, a failed handshake
            // (wrong ws/wss scheme, relay down, TLS misconfig) surfaced only as
            // an "Unobserved exception in Task" on the finalizer thread, with
            // no in-game feedback at all. Route it through Disconnected instead
            // so MultiplayerManager/UI can show it like any other drop.
            DiagnosticLog.Warn($"[RelayClient] Connect to {uri} failed: {e.Message}");
            Disconnected?.Invoke(e);
            return null;
        }
        var assignedCode = await ReadGreetingAsync().ConfigureAwait(false);
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(SendLoopAsync);
        return assignedCode;
    }

    private sealed record RelayGreeting(int RelayVersion, string[]? Capabilities, string? SessionCode);

    private const int GreetingTimeoutMs = 5000;

    // Reads the relay's one-shot greeting (see Program.cs), sent immediately after
    // any successful join/host -- deliberately not an MpMessage (the relay stays
    // fully protocol-agnostic), so this is read and consumed here, once, before
    // ReceiveLoopAsync ever starts; that loop never sees this frame. An old relay
    // that predates the greeting never sends one at all -- timing out just means
    // "assume no advertised capabilities" (the oldest, safest assumption for
    // every feature check) rather than blocking a connect indefinitely.
    private async Task<string?> ReadGreetingAsync()
    {
        var buffer = new byte[1024];
        try
        {
            var receiveTask = socket.ReceiveAsync(buffer, cts.Token);
            if (await Task.WhenAny(receiveTask, Task.Delay(GreetingTimeoutMs, cts.Token)).ConfigureAwait(false) != receiveTask)
            {
                DiagnosticLog.Warn("[RelayClient] No greeting from the relay within timeout -- assuming an old relay with no advertised capabilities.");
                return null;
            }
            var result = await receiveTask.ConfigureAwait(false);
            var greeting = JsonSerializer.Deserialize<RelayGreeting>(buffer.AsSpan(0, result.Count), JsonOptions);
            if (greeting is null) return null;
            RelayCapabilities = greeting.Capabilities is { } caps ? new HashSet<string>(caps) : new HashSet<string>();
            DiagnosticLog.Info($"[RelayClient] Relay version {greeting.RelayVersion}, capabilities: [{string.Join(", ", RelayCapabilities)}].");
            return greeting.SessionCode;
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Failed to read the relay's greeting: {e.Message} -- assuming an old relay with no advertised capabilities.");
            return null;
        }
    }

    public async Task SendAsync(MpMessage message)
    {
        if (!IsConnected) return;
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        var (bytes, type) = SupportsCompression && jsonBytes.Length >= CompressionThresholdBytes
            ? (Compress(jsonBytes), WebSocketMessageType.Binary)
            : (jsonBytes, WebSocketMessageType.Text);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // TryWrite on an unbounded channel is synchronous and never fails while the
        // channel is open, so sequential callers land in order. Ordering only holds
        // within a queue, not across the two -- see bulkSendQueue's comment.
        var queue = message is WorldSnapshotMessage ? bulkSendQueue : sendQueue;
        if (!queue.Writer.TryWrite((bytes, type, completion))) return;
        await completion.Task.ConfigureAwait(false);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest))
            brotli.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    // A stuck send otherwise has no upper bound (ClientWebSocket allows only one
    // outstanding at a time, with no built-in timeout). Aborting on timeout faults
    // ReceiveLoopAsync's pending read too, which is what fires Disconnected and
    // triggers reconnect. Longer than PeerStaleTimeoutMs since one send's duration
    // depends on that message's own size, not overall connection health.
    private const int SendTimeoutMs = 10_000;

    // Always checks the priority queue before bulkSendQueue -- see its comment.
    private async Task SendLoopAsync()
    {
        try
        {
            while (true)
            {
                if (sendQueue.Reader.TryRead(out var entry) || bulkSendQueue.Reader.TryRead(out entry))
                {
                    await SendOneAsync(entry.Bytes, entry.Type, entry.Completion).ConfigureAwait(false);
                    continue;
                }
                // Nothing ready -- wait for whichever queue fills first, then loop
                // back around so the priority check above runs again.
                var prioritySignal = sendQueue.Reader.WaitToReadAsync(cts.Token).AsTask();
                var bulkSignal = bulkSendQueue.Reader.WaitToReadAsync(cts.Token).AsTask();
                await Task.WhenAny(prioritySignal, bulkSignal).ConfigureAwait(false);
                // Both readers complete once Dispose() finishes both queues.
                if (sendQueue.Reader.Completion.IsCompleted && bulkSendQueue.Reader.Completion.IsCompleted) return;
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose() cancelled us -- not a failure.
        }
    }

    private async Task SendOneAsync(byte[] bytes, WebSocketMessageType type, TaskCompletionSource completion)
    {
        try
        {
            var sendTask = socket.SendAsync(bytes, type, true, cts.Token);
            if (await Task.WhenAny(sendTask, Task.Delay(SendTimeoutMs, cts.Token)).ConfigureAwait(false) != sendTask)
            {
                DiagnosticLog.Warn($"[RelayClient] Send stuck for over {SendTimeoutMs / 1000}s -- treating the connection as dead.");
                try { socket.Abort(); } catch { /* best-effort; ReceiveLoopAsync's fault is what actually matters */ }
                completion.SetException(new TimeoutException($"Send timed out after {SendTimeoutMs}ms."));
                return;
            }
            await sendTask.ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Send failed: {e.Message}");
            completion.SetException(e);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        Exception? failure = null;
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        DiagnosticLog.Info($"[RelayClient] Received close frame: status={result.CloseStatus}, description=\"{result.CloseStatusDescription}\".");
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                MpMessage? message;
                try
                {
                    var raw = ms.ToArray();
                    if (result.MessageType == WebSocketMessageType.Binary) raw = Decompress(raw);
                    message = JsonSerializer.Deserialize<MpMessage>(raw, JsonOptions);
                }
                // Broader than just JsonException: a relay-side bug, a mid-stream
                // protocol version mismatch, or plain bit-rot on a bad connection
                // can all surface as other exception types out of the polymorphic
                // deserializer. One bad message should never take the whole
                // connection down -- log and keep reading.
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    DiagnosticLog.Warn($"[RelayClient] Malformed message dropped: {e.Message}");
                    continue;
                }
                if (message != null) MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose() cancelled us -- not a failure.
        }
        catch (Exception e)
        {
            failure = e;
            var code = e is WebSocketException wse ? $" (WebSocketErrorCode={wse.WebSocketErrorCode})" : "";
            DiagnosticLog.Warn($"[RelayClient] Receive loop faulted: {e}{code}");
        }
        finally
        {
            DiagnosticLog.Debug($"[RelayClient] Receive loop exiting -- final socket state {socket.State}.");
            Disconnected?.Invoke(failure);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        DiagnosticLog.Debug($"[RelayClient] Dispose() -- socket state was {socket.State}.");
        // Completed before Cancel: cts.Cancel() can make SendLoopAsync's ReadAllAsync
        // throw immediately without draining what's left, which would otherwise leave
        // any already-enqueued SendAsync callers awaiting a TaskCompletionSource that
        // never gets set -- hanging them forever instead of letting them observe the
        // disconnect like a normal failed send.
        sendQueue.Writer.TryComplete();
        bulkSendQueue.Writer.TryComplete();
        while (sendQueue.Reader.TryRead(out var pending))
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(RelayClient)));
        while (bulkSendQueue.Reader.TryRead(out var pending))
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(RelayClient)));
        cts.Cancel();
        try { socket.Abort(); } catch { /* best-effort teardown */ }
        socket.Dispose();
        cts.Dispose();
    }
}
