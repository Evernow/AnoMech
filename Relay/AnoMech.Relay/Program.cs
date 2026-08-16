using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace AnoMech.Relay;

// Dumb session-scoped broadcast relay. Knows nothing about AnoMech's message
// protocol -- it just forwards whatever text frame one socket sends to every
// other socket connected under the same session code. All app-level meaning
// (host/peer, role claims, world snapshots) lives entirely in the plugin.
//
// Exists because a Dalamud client is firewalled off from FFXIV's own server
// traffic while a scenario runs (see ZoneSession), and most players sit behind
// NAT, so direct peer-to-peer isn't viable -- this is the same role Mare
// Synchronos/Lightless's relay plays for cosmetic sync.
internal static class Program
{
    // Hard cap per session so one room can't be griefed into an unbounded fan-out.
    private const int MaxPeersPerSession = 8;

    private static readonly Dictionary<string, List<WebSocket>> Sessions = new();
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

        Console.WriteLine($"[AnoMech.Relay] Listening on port {port}. Sessions are addressed as /session/<code>.");

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
        var sessionCode = ExtractSessionCode(ctx.Request.Url?.AbsolutePath);
        if (sessionCode is null || !ctx.Request.IsWebSocketRequest)
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

        if (!TryJoin(sessionCode, socket, out var reason))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
            return;
        }

        Console.WriteLine($"[{sessionCode}] peer joined ({CountPeers(sessionCode)} connected)");

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

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var text = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
                await BroadcastAsync(sessionCode, socket, text);
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

    private static string? ExtractSessionCode(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Trim('/').Split('/');
        return parts.Length == 2 && parts[0] == "session" && parts[1].Length is > 0 and <= 32
            ? parts[1]
            : null;
    }

    private static bool TryJoin(string sessionCode, WebSocket socket, out string reason)
    {
        lock (SessionsLock)
        {
            if (!Sessions.TryGetValue(sessionCode, out var peers))
            {
                peers = new List<WebSocket>();
                Sessions[sessionCode] = peers;
            }
            if (peers.Count >= MaxPeersPerSession)
            {
                reason = "session full";
                return false;
            }
            peers.Add(socket);
            reason = "";
            return true;
        }
    }

    private static void Leave(string sessionCode, WebSocket socket)
    {
        lock (SessionsLock)
        {
            if (!Sessions.TryGetValue(sessionCode, out var peers)) return;
            peers.Remove(socket);
            if (peers.Count == 0) Sessions.Remove(sessionCode);
        }
    }

    private static int CountPeers(string sessionCode)
    {
        lock (SessionsLock)
            return Sessions.TryGetValue(sessionCode, out var peers) ? peers.Count : 0;
    }

    private static async Task BroadcastAsync(string sessionCode, WebSocket sender, string text)
    {
        List<WebSocket> targets;
        lock (SessionsLock)
        {
            if (!Sessions.TryGetValue(sessionCode, out var peers)) return;
            targets = peers.Where(p => !ReferenceEquals(p, sender) && p.State == WebSocketState.Open).ToList();
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        foreach (var target in targets)
        {
            try
            {
                await target.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // Dead socket -- its own receive loop will observe the failure and Leave().
            }
        }
    }
}
