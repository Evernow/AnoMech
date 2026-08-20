using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnoMech.Core;

namespace AnoMech.Multiplayer;

// Thin ClientWebSocket wrapper talking to AnoMech.Relay (see Relay/README.md).
// The relay only forwards opaque text frames within a session code -- every
// message's meaning lives in MpMessage/Protocol.cs, not here.
public sealed class RelayClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

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
    private readonly Channel<(byte[] Bytes, TaskCompletionSource Completion)> sendQueue =
        Channel.CreateUnbounded<(byte[], TaskCompletionSource)>();
    private bool disposed;

    public event Action<MpMessage>? MessageReceived;
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => socket.State == WebSocketState.Open;

    public async Task ConnectAsync(string baseUrl, string sessionCode)
    {
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/session/{Uri.EscapeDataString(sessionCode)}");
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
            return;
        }
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(SendLoopAsync);
    }

    public async Task SendAsync(MpMessage message)
    {
        if (!IsConnected) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // TryWrite on an unbounded channel is synchronous and never fails while the
        // channel is open -- the enqueue itself happens before this method's first
        // await, so sequential callers land in the queue in the exact order they called
        // SendAsync, however many of them race to get here.
        if (!sendQueue.Writer.TryWrite((bytes, completion))) return;
        await completion.Task.ConfigureAwait(false);
    }

    // Single consumer draining sendQueue strictly in enqueue order -- this is what
    // actually makes SendAsync's ordering guarantee hold, not just the enqueue step.
    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var (bytes, completion) in sendQueue.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                    completion.SetResult();
                }
                catch (Exception e)
                {
                    DiagnosticLog.Warn($"[RelayClient] Send failed: {e.Message}");
                    completion.SetException(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Dispose() cancelled us -- not a failure.
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
                    message = JsonSerializer.Deserialize<MpMessage>(ms.ToArray(), JsonOptions);
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
        while (sendQueue.Reader.TryRead(out var pending))
            pending.Completion.TrySetException(new ObjectDisposedException(nameof(RelayClient)));
        cts.Cancel();
        try { socket.Abort(); } catch { /* best-effort teardown */ }
        socket.Dispose();
        cts.Dispose();
    }
}
