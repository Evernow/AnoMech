using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace AnoMech.Relay;

// Dumb session-scoped broadcast relay. Knows nothing about AnoMech's message protocol --
// forwards whatever frame one socket sends, verbatim and with its original type, to every
// other socket in the same session code. All app-level meaning lives in the plugin.
//
// Exists because a Dalamud client is firewalled off from FFXIV's own server traffic during
// a scenario (see ZoneSession), and most players sit behind NAT, so direct P2P isn't viable.
internal static class Program
{
    // Hard cap per session so one room can't be griefed into an unbounded fan-out.
    private const int MaxPeersPerSession = 8;

    // Sent once right after a socket joins, so a client can detect an old/narrower relay
    // before relying on a behavior it doesn't have (see RelayClient.cs). Not an MpMessage --
    // the relay stays protocol-agnostic. A named capability set, not one version int, so a
    // client only checks for what it actually needs; never remove/rename a shipped entry.
    private const int RelayVersion = 2;
    private static readonly string[] RelayCapabilities = ["binaryCompression"];
    private static readonly string RelayCapabilitiesJson = string.Join(",", RelayCapabilities.Select(c => $"\"{c}\""));

    // The relay owns the room namespace, so it's the only party that can guarantee no
    // collision (vs. a client picking one locally and hoping). Hosting goes through /host
    // (no code in the URL); the relay picks a free one and hands it back in the greeting.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    private const int CodeLength = 6;
    private static readonly Random CodeRng = new();

    // A room idle this long is considered abandoned (crashed host, dead sockets that never
    // closed cleanly). Comfortably above MultiplayerManager's 2s ping, so any live host
    // keeps its room for free.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(2);

    private sealed class Room
    {
        public readonly List<WebSocket> Peers = new();
        public DateTime LastActivityUtc = DateTime.UtcNow;
    }

    private static readonly Dictionary<string, Room> Sessions = new();
    private static readonly object SessionsLock = new();

    private static async Task Main(string[] args)
    {
        var port = 7890;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var p))
                port = p;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            Console.Error.WriteLine($"Failed to bind port {port}: {e.Message}");
            Console.Error.WriteLine("On Windows, binding a non-loopback prefix needs either an admin " +
                                     "process or a URL ACL grant: netsh http add urlacl url=http://+:" +
                                     $"{port}/ user=Everyone");
            return;
        }

        Console.WriteLine($"[AnoMech.Relay] Listening on port {port}. Host a session at /host, join one at /session/<code>.");
        _ = ReapIdleSessionsLoop();

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break; // listener stopped
            }
            _ = HandleConnectionAsync(ctx);
        }
    }

    private static async Task HandleConnectionAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.Trim('/') ?? "";
        var isHostRequest = path == "host";
        var joinCode = isHostRequest ? null : ExtractSessionCode(path);
        if ((!isHostRequest && joinCode is null) || !ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        WebSocket socket;
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
            socket = wsCtx.WebSocket;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"WebSocket handshake failed: {e.Message}");
            return;
        }

        string sessionCode;
        if (isHostRequest)
        {
            sessionCode = CreateSession(socket);
        }
        else
        {
            sessionCode = joinCode!;
            if (!TryJoin(sessionCode, socket, out var reason))
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
                return;
            }
        }

        Console.WriteLine($"[{sessionCode}] {(isHostRequest ? "session created" : "peer joined")} ({CountPeers(sessionCode)} connected)");
        await SendGreetingAsync(socket, isHostRequest ? sessionCode : null);

        // A large message (e.g. WorldSnapshotMessage) can arrive split across several
        // frames; buffer until EndOfMessage before forwarding, or fragments get broadcast
        // standalone and peers see truncated/corrupt JSON once a snapshot outgrows one frame.
        var readBuffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(readBuffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        goto closed;
                    }
                    messageBuffer.Write(readBuffer, 0, result.Count);
                } while (!result.EndOfMessage);

                await BroadcastAsync(sessionCode, socket, messageBuffer.ToArray(), result.MessageType);
            }
            closed: ;
        }
        catch (WebSocketException)
        {
            // Peer dropped without a clean close handshake -- fall through to Leave.
        }
        finally
        {
            Leave(sessionCode, socket);
            Console.WriteLine($"[{sessionCode}] peer left ({CountPeers(sessionCode)} connected)");
        }
    }

    // Path is already trimmed of leading/trailing slashes -- see HandleConnectionAsync.
    private static string? ExtractSessionCode(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 2 && parts[0] == "session" && parts[1].Length is > 0 and <= 32
            ? parts[1]
            : null;
    }

    // Peer-join only -- a code nobody actually hosted is rejected immediately instead of
    // silently vivifying an empty room.
    private static bool TryJoin(string sessionCode, WebSocket socket, out string reason)
    {
        lock (SessionsLock)
        {
            if (!Sessions.TryGetValue(sessionCode, out var room))
            {
                reason = "session not found";
                return false;
            }
            if (room.Peers.Count >= MaxPeersPerSession)
            {
                reason = "session full";
                return false;
            }
            room.Peers.Add(socket);
            room.LastActivityUtc = DateTime.UtcNow;
            reason = "";
            return true;
        }
    }

    private static string CreateSession(WebSocket hostSocket)
    {
        lock (SessionsLock)
        {
            string code;
            do { code = GenerateCode(); } while (Sessions.ContainsKey(code));
            var room = new Room();
            room.Peers.Add(hostSocket);
            Sessions[code] = room;
            return code;
        }
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[CodeRng.Next(CodeAlphabet.Length)];
        return new string(chars);
    }

    private static void Leave(string sessionCode, WebSocket socket)
    {
        lock (SessionsLock)
        {
            if (!Sessions.TryGetValue(sessionCode, out var room)) return;
            room.Peers.Remove(socket);
            if (room.Peers.Count == 0) Sessions.Remove(sessionCode);
        }
    }

    private static int CountPeers(string sessionCode)
    {
        lock (SessionsLock)
            return Sessions.TryGetValue(sessionCode, out var room) ? room.Peers.Count : 0;
    }

    private static async Task SendGreetingAsync(WebSocket socket, string? assignedSessionCode)
    {
        var json = assignedSessionCode is null
            ? $$"""{"relayVersion":{{RelayVersion}},"capabilities":[{{RelayCapabilitiesJson}}]}"""
            : $$"""{"relayVersion":{{RelayVersion}},"capabilities":[{{RelayCapabilitiesJson}}],"sessionCode":"{{assignedSessionCode}}"}""";
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Socket died before we could greet it -- its own receive loop will
            // fault and clean up via Leave() the normal way.
        }
    }

    // Runs for the process's whole lifetime, alongside the accept loop in Main.
    private static async Task ReapIdleSessionsLoop()
    {
        while (true)
        {
            await Task.Delay(ReapInterval);
            List<(string Code, List<WebSocket> Peers)> dead = new();
            lock (SessionsLock)
            {
                var cutoff = DateTime.UtcNow - IdleTimeout;
                foreach (var (code, room) in Sessions.Where(kv => kv.Value.LastActivityUtc < cutoff).ToList())
                {
                    dead.Add((code, new List<WebSocket>(room.Peers)));
                    Sessions.Remove(code);
                }
            }
            foreach (var (code, peers) in dead)
            {
                Console.WriteLine($"[{code}] idle for over {IdleTimeout.TotalSeconds:F0}s -- disbanding ({peers.Count} connected).");
                foreach (var peer in peers)
                {
                    try { await peer.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "session idle timeout", CancellationToken.None); }
                    catch (WebSocketException)
                    {
                        // Already dead -- its own HandleConnectionAsync will notice
                        // and clean up via Leave() once its ReceiveAsync faults.
                    }
                }
            }
        }
    }

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private static async Task BroadcastAsync(string sessionCode, WebSocket sender, byte[] bytes, WebSocketMessageType type)
    {
        List<WebSocket> targets;
        lock (SessionsLock)
        {
            if (!Sessions.TryGetValue(sessionCode, out var room)) return;
            room.LastActivityUtc = DateTime.UtcNow;
            targets = room.Peers.Where(p => !ReferenceEquals(p, sender) && p.State == WebSocketState.Open).ToList();
        }

        // Parallel, not sequential -- one slow peer must not delay delivery to everyone else.
        await Task.WhenAll(targets.Select(target => SendOneAsync(target, bytes, type)));
    }

    // A timed-out send is treated as fatal for that connection (aborted, not skipped): a
    // cancelled send can leave a half-written frame in the OS buffer, and reusing the
    // connection risks interleaving a fresh frame with that leftover -- corrupting the
    // stream from then on. Abort lets both sides' own receive loops notice and clean up.
    private static async Task SendOneAsync(WebSocket target, byte[] bytes, WebSocketMessageType type)
    {
        using var cts = new CancellationTokenSource(SendTimeout);
        try
        {
            await target.SendAsync(bytes, type, endOfMessage: true, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"[Relay] Send to a peer timed out after {SendTimeout.TotalSeconds:F0}s -- aborting that connection.");
            target.Abort();
        }
        catch (WebSocketException)
        {
            // Dead socket -- its own receive loop will observe the failure and Leave().
        }
    }
}
