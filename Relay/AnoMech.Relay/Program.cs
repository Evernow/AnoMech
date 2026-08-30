using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace AnoMech.Relay;

// Dumb session-scoped broadcast relay. Knows nothing about AnoMech's message
// protocol -- it just forwards whatever frame (Text or Binary; the plugin uses
// Binary for Brotli-compressed messages, see RelayClient.cs) one socket sends,
// verbatim and with its original type, to every other socket connected under
// the same session code. All app-level meaning (host/peer, role claims, world
// snapshots, whether a given frame happens to be compressed) lives entirely in
// the plugin.
//
// Exists because a Dalamud client is firewalled off from FFXIV's own server
// traffic while a scenario runs (see ZoneSession), and most players sit behind
// NAT, so direct peer-to-peer isn't viable -- this is the same role Mare
// Synchronos/Lightless's relay plays for cosmetic sync.
internal static class Program
{
    // Hard cap per session so one room can't be griefed into an unbounded fan-out.
    private const int MaxPeersPerSession = 8;

    // Sent once to every socket right after it joins, letting a connecting client
    // detect an old relay (which never sends this at all, or an older one with a
    // narrower feature list) before it ever risks relying on a behavior that
    // relay doesn't have -- see RelayClient.cs's own read of this. Deliberately
    // not shaped as an MpMessage: the relay stays fully protocol-agnostic (see the
    // class doc comment), so this is its own tiny, independent greeting, not a
    // fake application message.
    //
    // A named capability set rather than a single version int deliberately: a
    // bare "protocol >= 2" check ties a client's every feature check to one
    // shared number, so adding *any* new relay-side behavior forces bumping it
    // for everyone even if a given client only cares about one specific thing.
    // Each entry here is a self-contained yes/no a client can check for whatever
    // it actually needs -- add a new one alongside any future relay-side
    // behavior a client needs to detect ahead of time, never remove/rename an
    // existing one once shipped (an older client may still be checking for it).
    private const int RelayVersion = 2;
    private static readonly string[] RelayCapabilities = ["binaryCompression"];
    private static readonly string RelayCapabilitiesJson = string.Join(",", RelayCapabilities.Select(c => $"\"{c}\""));

    // Codes were previously rolled client-side (a local Random.Shared pick in
    // MultiplayerManager) -- fine odds, but the relay is what actually owns the
    // room namespace below, so it's the only party that can actually GUARANTEE no
    // collision rather than just hope for one. Hosting now goes through /host (no
    // code in the URL at all); the relay picks a free one itself and hands it back
    // in the greeting -- see CreateSession.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I
    private const int CodeLength = 6;
    private static readonly Random CodeRng = new();

    // A room with no broadcast traffic for this long is considered abandoned -- a
    // host who requested a code via /host but crashed/closed before ever sending a
    // single message (not even their own periodic ping), or a session whose
    // sockets all went half-dead without a clean close. Comfortably above
    // MultiplayerManager's own 2s ping interval, so any actually-connected host
    // keeps their room alive for free just by existing; this only reaps rooms
    // where literally nothing has moved through in 5x that interval.
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

        // A single logical WebSocket message (e.g. a large WorldSnapshotMessage,
        // which grows with live enemy/tether count) can arrive split across many
        // frames -- ReceiveAsync only fills one frame per call and reports
        // EndOfMessage=false until the last one. Broadcasting after every single
        // ReceiveAsync (the old behaviour) forwarded each fragment standalone,
        // silently truncating/corrupting any message bigger than one read: fine
        // early in a fight when snapshots are small, but as soon as enough adds
        // are alive to push a snapshot past one frame, every later snapshot
        // arrived at peers as broken JSON and got dropped. Buffer until
        // EndOfMessage before forwarding anything.
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

    // Peer-join only -- hosting no longer creates a room implicitly (see
    // CreateSession), so a code nobody actually hosted is now rejected immediately
    // instead of silently vivifying an empty room a peer would otherwise sit in
    // until their own client-side "no host responded" timeout gave up on it.
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

    // How long a single peer's send may take before we give up on it. Broad
    // enough to absorb ordinary latency/jitter, tight enough that one stalled
    // connection can't noticeably delay the rest of the party for long.
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

        // Parallel, not sequential: with a party's worth of peers in one session,
        // a single slow/stalled connection must not delay delivery to everyone
        // else -- the old sequential foreach blocked the whole broadcast (every
        // message type, not just world snapshots) behind whichever peer happened
        // to be unresponsive.
        await Task.WhenAll(targets.Select(target => SendOneAsync(target, bytes, type)));
    }

    // Sends to one target with a hard timeout. A timeout is treated as fatal
    // for that connection (aborted, not just skipped this round): cancelling a
    // WebSocket send mid-flight can leave a half-written frame sitting in the
    // OS send buffer, and reusing the connection for the next broadcast risks
    // interleaving a fresh frame with that leftover partial one -- corrupting
    // the whole message stream for that peer from then on, not just this one
    // message. Aborting is a clean hard reset instead, and lets both sides'
    // own receive loops notice and clean up the normal way: this relay's own
    // per-connection loop (HandleConnectionAsync) sees its ReceiveAsync fail
    // and calls Leave(); the client's RelayClient sees its ReceiveAsync fail
    // and fires Disconnected, which starts its own reconnect-with-backoff loop.
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
