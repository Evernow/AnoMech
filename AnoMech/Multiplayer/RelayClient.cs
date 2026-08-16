using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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
    // fires sends from independent tick-rate timers (pose vs snapshot) that could otherwise overlap.
    private readonly SemaphoreSlim sendGate = new(1, 1);
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
        }
        catch (Exception e)
        {
            // Callers (MultiplayerManager.HostSession/JoinSession) fire this
            // fire-and-forget -- without catching here, a failed handshake
            // (wrong ws/wss scheme, relay down, TLS misconfig) surfaced only as
            // an "Unobserved exception in Task" on the finalizer thread, with
            // no in-game feedback at all. Route it through Disconnected instead
            // so MultiplayerManager/UI can show it like any other drop.
            Plugin.Log.Warning($"[RelayClient] Connect to {uri} failed: {e.Message}");
            Disconnected?.Invoke(e);
            return;
        }
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendAsync(MpMessage message)
    {
        if (!IsConnected) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        await sendGate.WaitAsync(cts.Token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[RelayClient] Send failed: {e.Message}");
        }
        finally
        {
            sendGate.Release();
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
                    if (result.MessageType == WebSocketMessageType.Close) return;
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
                    Plugin.Log.Warning($"[RelayClient] Malformed message dropped: {e.Message}");
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
        }
        finally
        {
            Disconnected?.Invoke(failure);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        try { socket.Abort(); } catch { /* best-effort teardown */ }
        socket.Dispose();
        cts.Dispose();
        sendGate.Dispose();
    }
}
