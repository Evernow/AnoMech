using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnoMech.Core;

namespace AnoMech.Multiplayer;

// Thrown when the relay explicitly rejects a connection with a WS close frame right after the
// upgrade (see ReadGreetingAsync) -- almost always "session not found" because the relay
// restarted and forgot every room. Distinct from a transient/network failure: retrying the
// exact same session code will never succeed, so a caller (see MultiplayerManager's reconnect
// loop) should stop and tell the user, not keep backing off forever.
internal sealed class RelaySessionRejectedException(string reason) : Exception(reason);

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

    // bool is IsFromHost -- see IHostOnlyMessage's own doc comment.
    public event Action<MpMessage, bool>? MessageReceived;
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
    public bool SupportsSenderIdentity => HasRelayCapability("senderIdentity");

    public Task ConnectAsync(string relayUrl, string sessionCode, string? accessToken = null) =>
        ConnectCoreAsync(relayUrl, $"session/{Uri.EscapeDataString(sessionCode)}", accessToken);

    // Requests a relay-assigned session code (Program.cs's /host endpoint) instead of
    // picking one locally, since only the relay can guarantee no collision.
    public Task<string?> ConnectAndHostAsync(string relayUrl, string? accessToken = null) =>
        ConnectCoreAsync(relayUrl, "host", accessToken);

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

    // Same host/port resolution as ResolveCandidateUris, mapped to https/http instead of
    // wss/ws -- the relay's plain-HTTP /info endpoint lives on the exact same host:port a WS
    // connect would use (a TLS-terminating reverse proxy in front forwards both the same way).
    private static IReadOnlyList<Uri> ResolveInfoCandidateUris(string relayUrl)
    {
        var trimmed = relayUrl.Trim();
        var explicitScheme = trimmed.IndexOf("://", StringComparison.Ordinal) is var idx && idx > 0
            ? trimmed[..idx].ToLowerInvariant()
            : null;
        if (explicitScheme is not null)
        {
            var mapped = explicitScheme switch { "wss" => "https", "ws" => "http", _ => explicitScheme };
            return [BuildUri($"{mapped}://{trimmed[(idx + 3)..]}", "info")];
        }
        var (host, explicitPort) = SplitHostPort(trimmed);
        return
        [
            BuildUri($"https://{host}:{explicitPort ?? 443}", "info"),
            BuildUri($"http://{host}:{explicitPort ?? 7890}", "info"),
        ];
    }

    private sealed record RelayInfo(int RelayVersion, string[]? Capabilities, bool RequiresToken);

    private static readonly HttpClient InfoHttpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Plain HTTP GET, not a WS connection -- lets the UI learn whether a relay needs an
    // access token BEFORE the user has one to offer, so the password box in
    // MultiplayerWindow only shows up when it's actually needed. Best-effort: null means
    // either candidate failed (unreachable, or an old relay with no /info at all) -- callers
    // should fall back to "assume no token needed" rather than block on this.
    public static async Task<(int RelayVersion, bool RequiresToken)?> FetchInfoAsync(string relayUrl, CancellationToken ct = default)
    {
        foreach (var uri in ResolveInfoCandidateUris(relayUrl))
        {
            try
            {
                var json = await InfoHttpClient.GetStringAsync(uri, ct).ConfigureAwait(false);
                var info = JsonSerializer.Deserialize<RelayInfo>(json, JsonOptions);
                if (info != null) return (info.RelayVersion, info.RequiresToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                DiagnosticLog.Info($"[RelayClient] /info fetch from {uri} failed: {e.Message}");
            }
        }
        return null;
    }

    private async Task<string?> ConnectCoreAsync(string relayUrl, string path, string? accessToken)
    {
        var candidates = ResolveCandidateUris(relayUrl, path);
        // A password is worthless sent in the clear -- never attempt (let alone fall back to)
        // a ws:// candidate once one is set, whether that's the auto-detect fallback or an
        // explicit ws:// the user typed. Matches the relay's own enforcement (see
        // Program.cs's IsRequestEncrypted), so this is defense-in-depth, not the only guard.
        if (!string.IsNullOrEmpty(accessToken))
        {
            var encryptedOnly = candidates.Where(u => u.Scheme == "wss").ToList();
            if (encryptedOnly.Count == 0)
            {
                var reason = "A relay password is set, but this connection isn't encrypted (wss://) -- refusing to send it in plaintext.";
                DiagnosticLog.Warn($"[RelayClient] {reason}");
                Disconnected?.Invoke(new InvalidOperationException(reason));
                return null;
            }
            candidates = encryptedOnly;
        }
        Exception? lastFailure = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (i > 0)
            {
                socket.Dispose();
                socket = new ClientWebSocket();
            }
            // Options only take effect before ConnectAsync, so this has to be set again on
            // every fresh ClientWebSocket instance, not once outside the loop.
            if (!string.IsNullOrEmpty(accessToken))
                socket.Options.SetRequestHeader("X-AnoMech-Relay-Token", accessToken);
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
        WebSocketReceiveResult result;
        try
        {
            var receiveTask = socket.ReceiveAsync(buffer, cts.Token);
            if (await Task.WhenAny(receiveTask, Task.Delay(GreetingTimeoutMs, cts.Token)).ConfigureAwait(false) != receiveTask)
            {
                DiagnosticLog.Warn("[RelayClient] No greeting from the relay within timeout -- assuming an old relay with no advertised capabilities.");
                return null;
            }
            result = await receiveTask.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[RelayClient] Failed to read the relay's greeting: {e.Message} -- assuming an old relay with no advertised capabilities.");
            return null;
        }

        // The relay accepts the WS upgrade BEFORE it validates the session code (see
        // Program.cs's HandleConnectionAsync -- TryJoin/TryCreateSession run after
        // AcceptWebSocketAsync, not before), so a rejected /session/<code> or /host arrives
        // HERE as a close frame, not as malformed JSON. Most commonly this means the relay
        // itself restarted and no longer has any memory of the session at all -- a real,
        // permanent rejection, not "an old relay with no greeting." Surfacing it distinctly
        // (rather than falling into the same catch-all as a genuine JSON parse failure) is
        // what lets the reconnect loop tell "give up and say so" apart from "keep retrying."
        if (result.MessageType == WebSocketMessageType.Close)
            throw new RelaySessionRejectedException(socket.CloseStatusDescription ?? "the relay closed the connection");

        try
        {
            var greeting = JsonSerializer.Deserialize<RelayGreeting>(buffer.AsSpan(0, result.Count), JsonOptions);
            if (greeting is null) return null;
            RelayCapabilities = greeting.Capabilities is { } caps ? new HashSet<string>(caps) : new HashSet<string>();
            DiagnosticLog.Info($"[RelayClient] Relay version {greeting.RelayVersion}, capabilities: [{string.Join(", ", RelayCapabilities)}].");
            return greeting.SessionCode;
        }
        catch (JsonException e)
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

    // Bounded manually -- CopyTo has no output-size limit, so a small malicious/corrupted
    // payload claiming a huge decompressed size (a compression bomb) could otherwise exhaust
    // memory in the game's own process, not just this connection. 64 MB is far past anything
    // a real WorldSnapshotMessage produces even compressed at CompressionThresholdBytes.
    private const int MaxDecompressedBytes = 64 * 1024 * 1024;

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = brotli.Read(chunk, 0, chunk.Length)) > 0)
        {
            output.Write(chunk, 0, read);
            if (output.Length > MaxDecompressedBytes)
                throw new InvalidDataException($"Decompressed message exceeded {MaxDecompressedBytes} bytes.");
        }
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
                var isFromHost = true;
                try
                {
                    var raw = ms.ToArray();
                    // Leading byte is the relay's host-tag (see Relay/Program.cs BroadcastAsync)
                    // -- only present when the relay actually advertised support for it.
                    if (SupportsSenderIdentity)
                    {
                        if (raw.Length == 0) throw new InvalidDataException("empty frame with senderIdentity active.");
                        isFromHost = raw[0] == 1;
                        raw = raw[1..];
                    }
                    if (result.MessageType == WebSocketMessageType.Binary) raw = Decompress(raw);
                    message = JsonSerializer.Deserialize<MpMessage>(raw, JsonOptions);
                }
                // One bad message shouldn't take the whole connection down.
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    DiagnosticLog.Warn($"[RelayClient] Malformed message dropped: {e.Message}");
                    continue;
                }
                if (message != null) MessageReceived?.Invoke(message, isFromHost);
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
