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

// Thin ClientWebSocket wrapper talking to AnoMech.Relay (see Relay/README.md). The relay
// only forwards opaque frames; message meaning lives in MpMessage/Protocol.cs. The frame's
// WebSocket message type doubles as the compression flag: Text = raw UTF-8 JSON, Binary =
// Brotli-compressed JSON (used once a message hits CompressionThresholdBytes -- below that,
// Brotli's own framing overhead costs more than it saves).
public sealed class RelayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const int CompressionThresholdBytes = 256;

    // Not readonly -- ConnectCoreAsync replaces this with a fresh instance per fallback
    // attempt, since a ClientWebSocket can only ConnectAsync once.
    private ClientWebSocket socket = new();
    private readonly CancellationTokenSource cts = new();
    // Two queues so a large WorldSnapshotMessage can't hog ClientWebSocket's one send slot
    // ahead of small/urgent messages. sendQueue is drained first; bulkSendQueue only when
    // it's empty. Each queue's own order is FIFO; order isn't preserved across the two.
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource Completion)> sendQueue =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, TaskCompletionSource)>();
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type, TaskCompletionSource Completion)> bulkSendQueue =
        Channel.CreateUnbounded<(byte[], WebSocketMessageType, TaskCompletionSource)>();
    private bool disposed;

    public event Action<MpMessage>? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => socket.State == WebSocketState.Open;

    // Which scheme this connection actually dialed -- the relay can't attest to this itself
    // (wss:// terminates in a reverse proxy in front of it, see Program.cs).
    public bool IsEncrypted { get; private set; }

    // True only when a bare host's wss:// attempt failed and it landed on ws:// instead --
    // distinct from IsEncrypted being false because someone typed ws:// on purpose.
    public bool FellBackToUnencrypted { get; private set; }

    // From the relay's greeting (ReadGreetingAsync). A named set rather than one version
    // number, so a missing entry always just means "not supported."
    public IReadOnlySet<string> RelayCapabilities { get; private set; } = new HashSet<string>();
    public bool HasRelayCapability(string name) => RelayCapabilities.Contains(name);
    public bool SupportsCompression => HasRelayCapability("binaryCompression");

    public Task ConnectAsync(string relayUrl, string sessionCode) =>
        ConnectCoreAsync(relayUrl, $"session/{Uri.EscapeDataString(sessionCode)}");

    // Requests a relay-assigned session code (Program.cs's /host endpoint) instead of
    // picking one locally, since only the relay can guarantee no collision.
    public Task<string?> ConnectAndHostAsync(string relayUrl) => ConnectCoreAsync(relayUrl, "host");

    // A bare host is tried encrypted first, falling back to plain ws:// only on failure --
    // there's no way to ask a server "do you speak TLS" other than trying. Default ports
    // match Relay/README.md's two setups (443 for wss:// behind Caddy, 7890 direct ws://).
    // An explicit ws://\wss://\http://\https:// is respected literally, tried once, no fallback.
    private static IReadOnlyList<Uri> ResolveCandidateUris(string relayUrl, string path)
    {
        var trimmed = relayUrl.Trim();
        var explicitScheme = trimmed.IndexOf("://", StringComparison.Ordinal) is var idx && idx > 0
            ? trimmed[..idx].ToLowerInvariant()
            : null;
        if (explicitScheme is not null)
        {
            var mapped = explicitScheme switch { "https" => "wss", "http" => "ws", _ => explicitScheme };
            return [BuildUri($"{mapped}://{trimmed[(idx + 3)..]}", path)];
        }

        var (host, explicitPort) = SplitHostPort(trimmed);
        return
        [
            BuildUri($"wss://{host}:{explicitPort ?? 443}", path),
            BuildUri($"ws://{host}:{explicitPort ?? 7890}", path),
        ];
    }

    // Probes with a scheme Uri has no built-in default port for -- "ws://" itself resolves
    // an unspecified port to 80 (mirroring http), which made a bare host silently get sent
    // to port 80 on both candidates instead of 443/7890.
    private static (string Host, int? Port) SplitHostPort(string hostAndOptionalPort)
    {
        if (!Uri.TryCreate($"anomech-probe://{hostAndOptionalPort}", UriKind.Absolute, out var probe))
            return (hostAndOptionalPort, null);
        return (probe.Host, probe.Port < 0 ? null : probe.Port);
    }

    private static Uri BuildUri(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");

    private async Task<string?> ConnectCoreAsync(string relayUrl, string path)
    {
        var candidates = ResolveCandidateUris(relayUrl, path);
        Exception? lastFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i > 0)
            {
                socket.Dispose();
                socket = new ClientWebSocket();
            }
            var uri = candidates[i];
            IsEncrypted = uri.Scheme == "wss";
            try
            {
                await socket.ConnectAsync(uri, cts.Token).ConfigureAwait(false);
                DiagnosticLog.Info($"[RelayClient] Connected to {uri}.");
                if (i > 0)
                {
                    FellBackToUnencrypted = true;
                    DiagnosticLog.Warn($"[RelayClient] {candidates[0]} wasn't reachable -- fell back to {uri}, unencrypted.");
                }
                var assignedCode = await ReadGreetingAsync().ConfigureAwait(false);
                _ = Task.Run(ReceiveLoopAsync);
                _ = Task.Run(SendLoopAsync);
                return assignedCode;
            }
            catch (Exception e)
            {
                lastFailure = e;
                DiagnosticLog.Info($"[RelayClient] {uri} failed: {e.Message}"
                    + (i < candidates.Count - 1 ? " -- trying the next candidate." : ""));
            }
        }
        // Callers fire this fire-and-forget; without routing through Disconnected a failed
        // handshake only surfaced as an unobserved Task exception, with no UI feedback.
        DiagnosticLog.Warn($"[RelayClient] Connect failed: {lastFailure?.Message}");
        Disconnected?.Invoke(lastFailure);
        return null;
    }

    private sealed record RelayGreeting(int RelayVersion, string[]? Capabilities, string? SessionCode);

    private const int GreetingTimeoutMs = 5000;

    // Reads the relay's one-shot greeting, sent right after join/host and not an MpMessage
    // (the relay stays protocol-agnostic) -- consumed here, once, before ReceiveLoopAsync
    // starts. An old relay that never sends one just times out to "no capabilities."
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

    // ClientWebSocket allows only one outstanding send with no built-in timeout. Aborting on
    // timeout also faults ReceiveLoopAsync's pending read, which triggers Disconnected/reconnect.
    private const int SendTimeoutMs = 10_000;

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
                var prioritySignal = sendQueue.Reader.WaitToReadAsync(cts.Token).AsTask();
                var bulkSignal = bulkSendQueue.Reader.WaitToReadAsync(cts.Token).AsTask();
                await Task.WhenAny(prioritySignal, bulkSignal).ConfigureAwait(false);
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
                // One bad message shouldn't take the whole connection down.
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
        // Complete before Cancel, so any already-enqueued SendAsync caller observes a normal
        // failed send instead of hanging on a TaskCompletionSource that never gets set.
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
