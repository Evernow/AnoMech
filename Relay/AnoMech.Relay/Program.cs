using System.Collections.Concurrent;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AnoMech.Relay;

// Dumb session-scoped broadcast relay. Knows nothing about AnoMech's message protocol --
// forwards whatever frame one socket sends, verbatim and with its original type, to every
// other socket in the same session code. All app-level meaning lives in the plugin.
//
// Exists because a Dalamud client is firewalled off from FFXIV's own server traffic during
// a scenario (see ZoneSession), and most players sit behind NAT, so direct P2P isn't viable.
//
// Meant to be runnable as a public service (anyone can point a plugin at it, not just people
// you've personally shared a URL with) -- see Relay/README.md's Security notes for the full
// threat model this is designed against.
internal static class Program
{
    // ---- Tunables (defaults; all overridable via CLI flags, see ParseArgs) -------------

    // Hard cap per session so one room can't be griefed into an unbounded fan-out.
    private static int MaxPeersPerSession = 8;

    // Hard cap on live rooms process-wide -- without this, spamming /host costs nothing and
    // grows Sessions unbounded.
    private static int MaxTotalSessions = 500;

    // One logical message can't exceed this once reassembled from fragments -- otherwise a
    // client that never sends EndOfMessage (or sends gigabytes of it) can OOM the process.
    private static long MaxMessageBytes = 1 * 1024 * 1024;

    // Live sockets allowed from one source address at once, across every room. Sized with
    // slack for legitimate NAT/CGNAT sharing (mobile carriers, corporate networks) -- a
    // public relay sees much more of this than a friend-only one, so don't set this too tight.
    private static int MaxConnectionsPerIp = 64;

    // Brute-force guard shared by every kind of guessable secret (session codes, --token,
    // --admin-token): this many failures from one IP inside the window trips a lockout, so
    // guessing at scale isn't free.
    private static int MaxFailedJoinsPerWindow = 10;
    private static readonly TimeSpan FailedJoinWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan JoinLockoutDuration = TimeSpan.FromMinutes(5);

    // Per-connection message rate cap -- well above any legitimate send rate (position/
    // snapshot updates top out in the tens of Hz), but bounds a connection sending far faster
    // than any real client would (packet flooding / gameplay-command-spam class of abuse).
    private static int MaxMessagesPerSecond = 5000;

    // Fragments allowed while assembling ONE message, independent of MaxMessageBytes -- a
    // real client's sends arrive as whatever chunk size the OS socket buffer gives, nowhere
    // near this many frames even for a large message; this only bounds someone deliberately
    // sending many tiny frames to burn CPU on ReceiveAsync round-trips while staying under
    // the byte cap.
    private static int MaxFragmentsPerMessage = 2000;

    // A stalled WS handshake or a message that takes too long to fully arrive gets abandoned
    // instead of held open indefinitely (slowloris-style).
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MessageAssemblyTimeout = TimeSpan.FromSeconds(30);

    // How many rejected connections inside one ReapInterval trips an [ALERT] log line and
    // shows as elevated in the admin dashboard. Not a hard block -- purely a "go look at this"
    // signal, since a real distributed attack won't be stopped by anything in this process
    // anyway (see README's Security notes on volumetric/botnet attacks).
    private const long AlertRejectionThresholdPerTick = 50;

    // Optional shared secret gating /host and /session/<code> -- null means anyone can connect
    // (the original friend-relay default). Set via --token; compared with FixedTimeEquals so
    // response timing can't leak how much of a guess was right.
    private static string? AccessToken;

    // Separate secret gating /admin/stats. Deliberately independent from AccessToken -- the
    // people you hand the relay's join token to are not necessarily people who should see
    // live abuse counters and IP-level state. Endpoint is 404 (not "401 with an empty check")
    // when unset, so it doesn't even reveal it exists on a relay nobody enabled it for.
    private static string? AdminToken;

    // Independent of AccessToken/AdminToken -- a relay with no password at all still carries
    // session codes and full match state, which an operator may want encrypted end-to-end
    // regardless of whether anyone's protecting a secret. AccessToken/AdminToken being set
    // already forces this on (see IsRequestEncrypted's call sites); this flag lets an operator
    // require it even with no token in play.
    private static bool RequireTls;

    // Sent once right after a socket joins, so a client can detect an old/narrower relay
    // before relying on a behavior it doesn't have (see RelayClient.cs). Not an MpMessage --
    // the relay stays protocol-agnostic. A named capability set, not one version int, so a
    // client only checks for what it actually needs; never remove/rename a shipped entry.
    private const int RelayVersion = 3;
    private static readonly string[] RelayCapabilities = ["binaryCompression", "senderIdentity"];
    private static readonly string RelayCapabilitiesJson = string.Join(",", RelayCapabilities.Select(c => $"\"{c}\""));

    // The relay owns the room namespace, so it's the only party that can guarantee no
    // collision (vs. a client picking one locally and hoping). Hosting goes through /host
    // (no code in the URL); the relay picks a free one and hands it back in the greeting.
    // Cryptographically random (not System.Random) -- on a public relay, a stranger who's
    // observed a few issued codes must not be able to predict a future one and race the real
    // host into a session before they've even shared its code.
    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I, 32 chars
    private const int CodeLength = 6;

    // A room idle this long is considered abandoned (crashed host, dead sockets that never
    // closed cleanly). Comfortably above MultiplayerManager's 2s ping, so any live host
    // keeps its room for free.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReapInterval = TimeSpan.FromSeconds(2);

    // How often ReapLoop also emits a summary line, independent of idle-session sweeps --
    // ambient "is this thing alive and how loaded" visibility in the console/journal without
    // needing the admin endpoint.
    private static readonly TimeSpan SummaryLogInterval = TimeSpan.FromMinutes(1);

    private sealed class Room
    {
        public readonly List<WebSocket> Peers = new();
        // Guards this room's own Peers/LastActivityUtc only -- NOT the Sessions table below.
        // One lock per room (not one relay-wide lock) so unrelated sessions never contend with
        // each other on join/leave/broadcast; only two operations touching the SAME session
        // ever serialize against one another. See TryJoin/Leave for why Sessions removal also
        // has to happen while holding this lock, not the table's own (lock-free) operations.
        public readonly object Lock = new();
        // Whoever created the room, tagged onto every broadcast from them (see
        // BroadcastAsync) so a receiving client can tell a real host message from a joined
        // peer forging one.
        public WebSocket? HostSocket;
        public DateTime LastActivityUtc = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<string, Room> Sessions = new();

    // Per-IP abuse tracking, separate lock since it's touched on a different cadence
    // (every connection/join attempt) than the session table.
    private static readonly Dictionary<IPAddress, int> ConnectionsByIp = new();
    private static readonly Dictionary<IPAddress, Queue<DateTime>> FailedJoinsByIp = new();
    private static readonly Dictionary<IPAddress, DateTime> JoinLockoutUntilByIp = new();
    private static readonly object AbuseLock = new();

    // ---- Abuse-relevant counters, all lifetime totals exposed via /admin/stats. Interlocked,
    // not lock-guarded -- each is an independent running total, no cross-field consistency
    // needed. recentRejections resets every ReapInterval (see ReapLoop) and drives the alert
    // threshold above.
    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;
    private static long totalConnectionsAccepted;
    private static long totalMessagesBroadcast;
    private static long totalBytesBroadcast;
    private static long recentRejections;
    private static long rejectedOrigin, rejectedIpCap, rejectedJoinLockout, rejectedRelayFull,
        rejectedSessionFull, rejectedSessionNotFound, rejectedBadToken, rejectedHandshakeTimeout,
        rejectedMessageTooLarge, rejectedMessageTimeout, rejectedMessageRate, rejectedUnencrypted, rejectedTooManyFragments;

    private static void CountRejection(ref long counter)
    {
        Interlocked.Increment(ref counter);
        Interlocked.Increment(ref recentRejections);
    }

    // For rejection reasons that otherwise leave no individual trace anywhere (only the
    // aggregate counter) -- logs one Detail line (file only; see RelayLog) per rejection so
    // "what happened to this one connection attempt" is answerable later, without spamming the
    // console for high-volume abuse (the [ALERT] summary in ReapLoop covers that).
    private static void CountRejection(ref long counter, string reason, IPAddress ip, string sessionTag)
    {
        CountRejection(ref counter);
        RelayLog.Detail($"[{sessionTag}] rejected ({reason}) from {ip}");
    }

    private static async Task Main(string[] args)
    {
        if (args.Contains("--admin"))
        {
            await RunAdminDashboardAsync(args);
            return;
        }

        if (args.Contains("--session-log"))
        {
            var code = GetArg(args, "--session-log");
            var dir = GetArg(args, "--log-dir") ?? Path.Combine(AppContext.BaseDirectory, "logs");
            if (string.IsNullOrEmpty(code))
            {
                Console.Error.WriteLine("--session-log requires a session code, e.g. --session-log ABCD23");
                return;
            }
            RelayLog.PrintSessionLog(dir, code);
            return;
        }

        var port = int.TryParse(GetArg(args, "--port", "-p"), out var p) ? p : 7890;
        // Env var fallback, CLI flag wins if both are set -- a CLI arg is visible to any other
        // local user via a process listing (ps/tasklist) and often ends up in shell history;
        // an env var isn't immune to a sufficiently privileged local reader either, but doesn't
        // leak through either of those two common paths.
        AccessToken = NullIfEmpty(GetArg(args, "--token")) ?? NullIfEmpty(Environment.GetEnvironmentVariable("ANOMECH_RELAY_TOKEN"));
        AdminToken = NullIfEmpty(GetArg(args, "--admin-token")) ?? NullIfEmpty(Environment.GetEnvironmentVariable("ANOMECH_RELAY_ADMIN_TOKEN"));
        if (int.TryParse(GetArg(args, "--max-sessions"), out var mts)) MaxTotalSessions = mts;
        if (int.TryParse(GetArg(args, "--max-connections-per-ip"), out var mcpi)) MaxConnectionsPerIp = mcpi;
        if (long.TryParse(GetArg(args, "--max-message-bytes"), out var mmb)) MaxMessageBytes = mmb;
        if (int.TryParse(GetArg(args, "--max-failed-joins"), out var mfj)) MaxFailedJoinsPerWindow = mfj;
        if (int.TryParse(GetArg(args, "--max-peers-per-session"), out var mpps)) MaxPeersPerSession = mpps;
        if (int.TryParse(GetArg(args, "--max-messages-per-second"), out var mmps)) MaxMessagesPerSecond = mmps;
        if (int.TryParse(GetArg(args, "--max-fragments-per-message"), out var mfpm)) MaxFragmentsPerMessage = mfpm;
        RequireTls = args.Contains("--require-tls");

        // Refuse to start rather than just warn -- same "enforce, don't just recommend"
        // stance as the TLS requirement below. A short token is still guessable within the
        // lockout's own budget given enough patience or rotating IPs; there's no usability
        // cost to requiring length here since this is a generated secret, not a memorized one.
        const int minTokenLength = 16;
        if (AccessToken is { Length: < minTokenLength } || AdminToken is { Length: < minTokenLength })
        {
            Console.Error.WriteLine($"--token/--admin-token must be at least {minTokenLength} characters -- " +
                                     "a short shared secret is still guessable over time even with the lockout in place. " +
                                     "Generate one with e.g. `openssl rand -hex 16`.");
            return;
        }

        // Configured before the listener even tries to bind, so a bind failure still gets a
        // file record -- useful under systemd/journald where the console output of a crashed
        // service is easy to lose. Defaults to a directory next to the executable (not the
        // working directory, which varies by how the process was launched).
        if (!args.Contains("--no-file-log"))
        {
            var logDir = GetArg(args, "--log-dir") ?? Path.Combine(AppContext.BaseDirectory, "logs");
            var maxLogBytes = long.TryParse(GetArg(args, "--log-max-bytes"), out var mlb) ? mlb : 5L * 1024 * 1024 * 1024;
            RelayLog.Configure(logDir, maxLogBytes);
            // Log writes are buffered and flushed roughly once a second (see RelayLog) -- catch
            // a graceful shutdown (systemd stop, Ctrl+C) so the last stretch isn't silently lost.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RelayLog.FlushOnShutdown();
            RelayLog.Info($"[AnoMech.Relay] Logging to {logDir} (compressed, capped at {maxLogBytes / (1024.0 * 1024 * 1024):F1} GB). " +
                           "Use --session-log <code> to read back one session's lines.");
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            RelayLog.Warn($"Failed to bind port {port}: {e.Message}");
            RelayLog.Warn("On Windows, binding a non-loopback prefix needs either an admin " +
                           "process or a URL ACL grant: netsh http add urlacl url=http://+:" +
                           $"{port}/ user=Everyone");
            return;
        }

        RelayLog.Info($"[AnoMech.Relay] Listening on port {port}. Host a session at /host, join one at /session/<code>.");
        RelayLog.Info($"[AnoMech.Relay] Access token: {(AccessToken != null ? "required" : "not set -- anyone can connect")}. " +
                      $"Admin endpoint: {(AdminToken != null ? "enabled" : "disabled (no --admin-token)")}.");
        if (RequireTls || AccessToken != null || AdminToken != null)
            RelayLog.Info($"[AnoMech.Relay] {(RequireTls ? "--require-tls is set" : "A token is set")}, so every connection now " +
                          "REQUIRES a TLS-terminating reverse proxy in front (X-Forwarded-Proto: https) -- see " +
                          "Relay/README.md. Unencrypted connections will be rejected with 426, including plain local testing.");
        _ = ReapLoop();

        while (true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception e)
            {
                // Any exception here used to be assumed to mean "listener.Stop() was called" --
                // but that's only true for HttpListenerException specifically. Anything else
                // (an unexpected edge case triggered by one malformed request) would otherwise
                // propagate out of Main uncaught and take the whole process down for every
                // connected session at once. IsListening is the actual "was this a real
                // shutdown" signal.
                if (!listener.IsListening) break;
                RelayLog.Warn($"[AnoMech.Relay] Unexpected error accepting a connection: {e.Message} -- continuing.");
                continue;
            }
            _ = HandleConnectionAsync(ctx);
        }
    }

    private static string? GetArg(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (names.Contains(args[i]))
                return args[i + 1];
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static async Task HandleConnectionAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath.Trim('/') ?? "";

        // Plain-HTTP endpoints, no WebSocket upgrade -- handled entirely separately from the
        // relay/join flow below.
        if (path == "info" && !ctx.Request.IsWebSocketRequest)
        {
            await ServeInfoAsync(ctx);
            return;
        }
        if (path == "admin/stats" && !ctx.Request.IsWebSocketRequest)
        {
            await ServeAdminStatsAsync(ctx);
            return;
        }

        var isHostRequest = path == "host";
        var joinCode = isHostRequest ? null : ExtractSessionCode(path);
        if ((!isHostRequest && joinCode is null) || !ctx.Request.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        var ip = ctx.Request.RemoteEndPoint?.Address ?? IPAddress.None;
        // Best available session identity before a room necessarily exists yet -- lets a
        // rejection that happens pre-join (bad token, IP cap, lockout...) still show up under
        // --session-log for the code someone was trying to reach. "host" requests don't have
        // a real code to attach to until TryCreateSession succeeds.
        var sessionTag = isHostRequest ? "host" : joinCode!;

        // A password is worthless if it's sent in the clear -- once one is set, every
        // connection must be TLS-terminated in front of us (see IsRequestEncrypted).
        // --require-tls forces the same regardless, even with no token at all.
        if ((RequireTls || AccessToken != null) && !IsRequestEncrypted(ctx))
        {
            CountRejection(ref rejectedUnencrypted, "unencrypted", ip, sessionTag);
            ctx.Response.StatusCode = 426; // Upgrade Required
            ctx.Response.Close();
            return;
        }

        // Real Dalamud clients never send Origin; a browser tab always does. Rejecting it
        // outright closes off drive-by abuse from an arbitrary webpage with no allow-list to maintain.
        if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"]))
        {
            CountRejection(ref rejectedOrigin, "origin header present", ip, sessionTag);
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }

        // One shared lockout bucket for every kind of failed auth from this address --
        // guessing session codes, guessing --token, all cost the same budget. Checked before
        // the token comparison itself so a locked-out address can't keep spending CPU on
        // repeated guesses in the meantime.
        if (IsAuthLockedOut(ip))
        {
            CountRejection(ref rejectedJoinLockout, "auth locked out", ip, sessionTag);
            ctx.Response.StatusCode = 429;
            ctx.Response.Close();
            return;
        }

        if (AccessToken != null && !IsValidToken(ctx.Request.Headers["X-AnoMech-Relay-Token"], AccessToken))
        {
            CountRejection(ref rejectedBadToken, "bad token", ip, sessionTag);
            RecordFailedAuth(ip);
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }

        if (!TryReserveConnectionSlot(ip))
        {
            CountRejection(ref rejectedIpCap, "ip connection cap", ip, sessionTag);
            ctx.Response.StatusCode = 429;
            ctx.Response.Close();
            return;
        }

        WebSocket socket;
        try
        {
            var acceptTask = ctx.AcceptWebSocketAsync(subProtocol: null);
            if (await Task.WhenAny(acceptTask, Task.Delay(HandshakeTimeout)) != acceptTask)
            {
                CountRejection(ref rejectedHandshakeTimeout);
                RelayLog.Warn($"[{sessionTag}] WebSocket handshake from {ip} stalled past {HandshakeTimeout.TotalSeconds:F0}s -- abandoning.");
                ReleaseConnectionSlot(ip);
                try { ctx.Response.Abort(); } catch { /* best effort */ }
                return;
            }
            socket = (await acceptTask).WebSocket;
        }
        catch (Exception e)
        {
            RelayLog.Warn($"[{sessionTag}] WebSocket handshake from {ip} failed: {e.Message}");
            ReleaseConnectionSlot(ip);
            return;
        }

        Interlocked.Increment(ref totalConnectionsAccepted);

        string sessionCode;
        if (isHostRequest)
        {
            if (!TryCreateSession(socket, out sessionCode!))
            {
                CountRejection(ref rejectedRelayFull, "relay full", ip, sessionTag);
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "relay full", CancellationToken.None);
                ReleaseConnectionSlot(ip);
                return;
            }
        }
        else
        {
            sessionCode = joinCode!;
            if (!TryJoin(sessionCode, socket, out var reason))
            {
                // "session full" is a real code, just a full room -- doesn't count as a
                // guessing signal the way "not found" does, so it alone shouldn't burn
                // toward the lockout the way repeated wrong guesses should.
                if (reason == "session full") CountRejection(ref rejectedSessionFull, reason, ip, sessionTag);
                else
                {
                    CountRejection(ref rejectedSessionNotFound, reason, ip, sessionTag);
                    RecordFailedAuth(ip);
                }
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
                ReleaseConnectionSlot(ip);
                return;
            }
        }

        // Everything from here down (including the greeting send) is inside the same
        // try/finally as the message loop -- Leave/ReleaseConnectionSlot must run no matter
        // where between "joined" and "socket closed" something throws, or a peer that faults
        // mid-greeting would leak its room membership and per-IP connection slot forever.
        try
        {
            RelayLog.Info($"[{sessionCode}] {(isHostRequest ? "session created" : "peer joined")} from {ip} ({CountPeers(sessionCode)} connected)");
            await SendGreetingAsync(socket, isHostRequest ? sessionCode : null);

            // A large message (e.g. WorldSnapshotMessage) can arrive split across several
            // frames; buffer until EndOfMessage before forwarding, or fragments get broadcast
            // standalone and peers see truncated/corrupt JSON once a snapshot outgrows one frame.
            var readBuffer = new byte[16 * 1024];
            using var messageBuffer = new MemoryStream();
            // Per-connection, not per-IP -- a single connection sending far faster than any
            // legitimate client (position/snapshot updates top out well under this) gets cut
            // off regardless of which address it's coming from.
            var recentMessageTimes = new Queue<DateTime>();
            while (socket.State == WebSocketState.Open)
            {
                messageBuffer.SetLength(0);
                using var messageCts = new CancellationTokenSource(MessageAssemblyTimeout);
                WebSocketReceiveResult result;
                var fragmentCount = 0;
                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(readBuffer, messageCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            goto closed;
                        }
                        messageBuffer.Write(readBuffer, 0, result.Count);
                        if (messageBuffer.Length > MaxMessageBytes)
                        {
                            CountRejection(ref rejectedMessageTooLarge);
                            RelayLog.Warn($"[{sessionCode}] Message from {ip} exceeded {MaxMessageBytes} bytes -- aborting.");
                            socket.Abort();
                            goto closed;
                        }
                        // Bounds fragment COUNT, not just total bytes -- a real client never
                        // sends anywhere near this many frames for one message; this only
                        // catches something deliberately splitting a message into many tiny
                        // frames to burn ReceiveAsync round-trips while staying under the size cap.
                        if (++fragmentCount > MaxFragmentsPerMessage)
                        {
                            CountRejection(ref rejectedTooManyFragments);
                            RelayLog.Warn($"[{sessionCode}] Message from {ip} exceeded {MaxFragmentsPerMessage} fragments -- aborting.");
                            socket.Abort();
                            goto closed;
                        }
                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException)
                {
                    CountRejection(ref rejectedMessageTimeout);
                    RelayLog.Warn($"[{sessionCode}] Message from {ip} took over {MessageAssemblyTimeout.TotalSeconds:F0}s to arrive -- aborting.");
                    socket.Abort();
                    goto closed;
                }

                var now = DateTime.UtcNow;
                recentMessageTimes.Enqueue(now);
                while (recentMessageTimes.Count > 0 && now - recentMessageTimes.Peek() > TimeSpan.FromSeconds(1))
                    recentMessageTimes.Dequeue();
                if (recentMessageTimes.Count > MaxMessagesPerSecond)
                {
                    CountRejection(ref rejectedMessageRate);
                    RelayLog.Warn($"[{sessionCode}] {ip} exceeded {MaxMessagesPerSecond} messages/sec -- aborting.");
                    socket.Abort();
                    goto closed;
                }

                var messageBytes = messageBuffer.ToArray();
                var reachedPeers = await BroadcastAsync(sessionCode, socket, messageBytes, result.MessageType);
                // File-only (see RelayLog.Detail) -- this is the highest-volume event the relay
                // sees, and console-echoing it would drown out everything else. Never the
                // message body itself, only shape/size/routing, matching the relay's "we don't
                // log what you said" stance (see README's Security notes).
                RelayLog.Detail($"[{sessionCode}] broadcast from {ip} type={result.MessageType} bytes={messageBytes.Length} " +
                                 $"fragments={fragmentCount} -> {reachedPeers} peer(s)");
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
            ReleaseConnectionSlot(ip);
            RelayLog.Info($"[{sessionCode}] peer left ({CountPeers(sessionCode)} connected)");
        }
    }

    // Path is already trimmed of leading/trailing slashes -- see HandleConnectionAsync.
    // Validated against the real alphabet/length, not just a length ceiling, so scanner/bot
    // garbage gets rejected as a bad request instead of doing a session lookup at all.
    private static string? ExtractSessionCode(string path)
    {
        var parts = path.Split('/');
        if (parts.Length != 2 || parts[0] != "session") return null;
        var code = parts[1];
        return code.Length == CodeLength && code.All(CodeAlphabet.Contains) ? code : null;
    }

    // The relay has no TLS of its own -- wss:// is always a reverse proxy terminating TLS in
    // front of us (see README), so X-Forwarded-Proto is the only signal we have for "was the
    // real client connection actually encrypted." A well-behaved proxy (Caddy, nginx, per the
    // README's own setup) sets this to "https" for a TLS-terminated request; a direct,
    // unproxied ws:// request carries no such header at all, which this correctly treats as
    // unencrypted too rather than trusting an absent header by default.
    private static bool IsRequestEncrypted(HttpListenerContext ctx)
        => string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);

    // Timing-safe: a naive string comparison returns early on the first mismatched byte,
    // which lets a remote attacker recover the token one byte at a time from response timing.
    private static bool IsValidToken(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        // Compare a fixed-size hash of each instead of the raw (different-length) values --
        // FixedTimeEquals itself requires equal-length inputs, and short-circuiting on a
        // length check first would leak length the same way a naive compare leaks content.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));
    }

    private static bool TryReserveConnectionSlot(IPAddress ip)
    {
        lock (AbuseLock)
        {
            var count = ConnectionsByIp.GetValueOrDefault(ip);
            if (count >= MaxConnectionsPerIp) return false;
            ConnectionsByIp[ip] = count + 1;
            return true;
        }
    }

    private static void ReleaseConnectionSlot(IPAddress ip)
    {
        lock (AbuseLock)
        {
            if (!ConnectionsByIp.TryGetValue(ip, out var count)) return;
            if (count <= 1) ConnectionsByIp.Remove(ip);
            else ConnectionsByIp[ip] = count - 1;
        }
    }

    private static bool IsAuthLockedOut(IPAddress ip)
    {
        lock (AbuseLock)
            return JoinLockoutUntilByIp.TryGetValue(ip, out var until) && until > DateTime.UtcNow;
    }

    // Sliding window of recent failures (session-code guesses, bad relay/admin tokens);
    // crossing the threshold inside it trips a lockout covering all of them together.
    private static void RecordFailedAuth(IPAddress ip)
    {
        var justTripped = false;
        lock (AbuseLock)
        {
            if (!FailedJoinsByIp.TryGetValue(ip, out var attempts))
                FailedJoinsByIp[ip] = attempts = new Queue<DateTime>();
            var now = DateTime.UtcNow;
            attempts.Enqueue(now);
            while (attempts.Count > 0 && now - attempts.Peek() > FailedJoinWindow) attempts.Dequeue();
            if (attempts.Count >= MaxFailedJoinsPerWindow)
            {
                justTripped = !(JoinLockoutUntilByIp.TryGetValue(ip, out var until) && until > now);
                JoinLockoutUntilByIp[ip] = now + JoinLockoutDuration;
            }
        }
        // Logged once at the moment it trips, not on every renewal while already locked out --
        // a real lockout is rare and worth an operator's attention live; a locked-out address
        // still hammering the endpoint is not new information.
        if (justTripped)
            RelayLog.Warn($"[AnoMech.Relay] {ip} locked out for {JoinLockoutDuration.TotalMinutes:F0}m after " +
                          $"{MaxFailedJoinsPerWindow}+ failed auth attempts in {FailedJoinWindow.TotalSeconds:F0}s.");
    }

    // Peer-join only -- a code nobody actually hosted is rejected immediately instead of
    // silently vivifying an empty room. Loops rather than a single TryGetValue+lock because
    // Leave() can retire (empty + remove) this exact room between the lookup and acquiring its
    // lock; the re-check inside the lock catches that race and retries against whatever's
    // actually current instead of joining a room that's already been thrown away.
    private static bool TryJoin(string sessionCode, WebSocket socket, out string reason)
    {
        while (true)
        {
            if (!Sessions.TryGetValue(sessionCode, out var room))
            {
                reason = "session not found";
                return false;
            }
            lock (room.Lock)
            {
                if (!Sessions.TryGetValue(sessionCode, out var current) || !ReferenceEquals(current, room))
                    continue; // retired (or replaced) concurrently -- retry against the live one
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
    }

    private static bool TryCreateSession(WebSocket hostSocket, out string sessionCode)
    {
        // A soft cap now, not a hard one -- a burst of concurrent /host requests right at the
        // ceiling could transiently overshoot it by a few. MaxTotalSessions exists to bound
        // resource usage, not as a security invariant, so this is an acceptable trade for not
        // needing a relay-wide lock on every session creation.
        if (Sessions.Count >= MaxTotalSessions)
        {
            sessionCode = "";
            return false;
        }
        var room = new Room { HostSocket = hostSocket };
        room.Peers.Add(hostSocket);
        string code;
        do { code = GenerateCode(); } while (!Sessions.TryAdd(code, room));
        sessionCode = code;
        return true;
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        // CodeAlphabet.Length (32) divides 256 evenly, so byte % 32 is exactly uniform --
        // no rejection sampling needed.
        var bytes = RandomNumberGenerator.GetBytes(CodeLength);
        for (var i = 0; i < CodeLength; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }

    private static void Leave(string sessionCode, WebSocket socket)
    {
        if (!Sessions.TryGetValue(sessionCode, out var room)) return;
        // Removal from Sessions happens while still holding this room's own lock -- the one
        // point that has to agree with TryJoin's re-check above, so a peer can never be added
        // to a room in the instant between it going empty and being removed from the table.
        lock (room.Lock)
        {
            room.Peers.Remove(socket);
            if (room.Peers.Count == 0) Sessions.TryRemove(new KeyValuePair<string, Room>(sessionCode, room));
        }
    }

    private static int CountPeers(string sessionCode)
    {
        if (!Sessions.TryGetValue(sessionCode, out var room)) return 0;
        lock (room.Lock) return room.Peers.Count;
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

    // Unauthenticated by design -- a client must learn whether a token is needed BEFORE it
    // has one, or the plugin's "only show the password box if required" UI has no way to
    // decide. Nothing here is sensitive (it's the same info the WS greeting already sends,
    // minus a live session code).
    private static async Task ServeInfoAsync(HttpListenerContext ctx)
    {
        // /info itself carries no secret, but --require-tls means no exceptions -- keeps the
        // policy simple (nothing talks to this relay unencrypted) rather than case-by-case.
        if (RequireTls && !IsRequestEncrypted(ctx))
        {
            var infoIp = ctx.Request.RemoteEndPoint?.Address ?? IPAddress.None;
            CountRejection(ref rejectedUnencrypted, "unencrypted", infoIp, "info");
            ctx.Response.StatusCode = 426;
            ctx.Response.Close();
            return;
        }
        var json = $$"""{"relayVersion":{{RelayVersion}},"capabilities":[{{RelayCapabilitiesJson}}],"requiresToken":{{(AccessToken != null ? "true" : "false")}}}""";
        await WriteJsonResponseAsync(ctx, json);
    }

    private static async Task ServeAdminStatsAsync(HttpListenerContext ctx)
    {
        // 404, not 401 -- a relay with no --admin-token set shouldn't even reveal this
        // endpoint exists.
        if (AdminToken is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        var ip = ctx.Request.RemoteEndPoint?.Address ?? IPAddress.None;
        if (!IsRequestEncrypted(ctx))
        {
            CountRejection(ref rejectedUnencrypted, "unencrypted", ip, "admin");
            ctx.Response.StatusCode = 426;
            ctx.Response.Close();
            return;
        }
        if (IsAuthLockedOut(ip))
        {
            CountRejection(ref rejectedJoinLockout, "auth locked out", ip, "admin");
            ctx.Response.StatusCode = 429;
            ctx.Response.Close();
            return;
        }
        if (!IsValidToken(ctx.Request.Headers["X-AnoMech-Admin-Token"], AdminToken))
        {
            CountRejection(ref rejectedBadToken, "bad admin token", ip, "admin");
            RecordFailedAuth(ip);
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }
        var json = JsonSerializer.Serialize(BuildAdminStats());
        await WriteJsonResponseAsync(ctx, json);
    }

    private static async Task WriteJsonResponseAsync(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        try
        {
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    private sealed record RejectionCounts(
        long Origin, long IpCap, long JoinLockout, long RelayFull, long SessionFull,
        long SessionNotFound, long BadToken, long HandshakeTimeout, long MessageTooLarge, long MessageTimeout,
        long MessageRate, long Unencrypted, long TooManyFragments);

    private sealed record AdminStats(
        double UptimeSeconds, int RelayVersion, int Sessions, int TotalPeers, int ConnectionsByIpCount,
        int ActiveJoinLockouts, long TotalConnectionsAccepted, long TotalMessagesBroadcast, long TotalBytesBroadcast,
        RejectionCounts Rejections, long RecentRejections, long MemoryBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections);

    private static AdminStats BuildAdminStats()
    {
        var sessionCount = Sessions.Count;
        var totalPeers = Sessions.Values.Sum(r => { lock (r.Lock) return r.Peers.Count; });
        int ipCount, lockoutCount;
        lock (AbuseLock)
        {
            ipCount = ConnectionsByIp.Count;
            lockoutCount = JoinLockoutUntilByIp.Count(kv => kv.Value > DateTime.UtcNow);
        }
        return new AdminStats(
            (DateTime.UtcNow - StartedAtUtc).TotalSeconds, RelayVersion, sessionCount, totalPeers, ipCount, lockoutCount,
            Interlocked.Read(ref totalConnectionsAccepted), Interlocked.Read(ref totalMessagesBroadcast), Interlocked.Read(ref totalBytesBroadcast),
            new RejectionCounts(
                Interlocked.Read(ref rejectedOrigin), Interlocked.Read(ref rejectedIpCap), Interlocked.Read(ref rejectedJoinLockout),
                Interlocked.Read(ref rejectedRelayFull), Interlocked.Read(ref rejectedSessionFull), Interlocked.Read(ref rejectedSessionNotFound),
                Interlocked.Read(ref rejectedBadToken), Interlocked.Read(ref rejectedHandshakeTimeout), Interlocked.Read(ref rejectedMessageTooLarge),
                Interlocked.Read(ref rejectedMessageTimeout), Interlocked.Read(ref rejectedMessageRate), Interlocked.Read(ref rejectedUnencrypted),
                Interlocked.Read(ref rejectedTooManyFragments)),
            Interlocked.Read(ref recentRejections), GC.GetTotalMemory(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    // Runs for the process's whole lifetime, alongside the accept loop in Main. Also prunes
    // stale per-IP abuse-tracking entries so a long-running process doesn't accumulate one
    // dictionary entry per distinct attacker IP forever, and periodically logs a summary line
    // plus an [ALERT] if rejections spiked -- see AlertRejectionThresholdPerTick.
    private static async Task ReapLoop()
    {
        var sinceLastSummary = TimeSpan.Zero;
        while (true)
        {
            await Task.Delay(ReapInterval);
            List<(string Code, List<WebSocket> Peers)> dead = new();
            // Enumerating a ConcurrentDictionary while calling TryRemove on it is safe (unlike
            // a plain Dictionary) -- no snapshot copy needed first.
            var cutoff = DateTime.UtcNow - IdleTimeout;
            foreach (var (code, room) in Sessions)
            {
                lock (room.Lock)
                {
                    if (room.LastActivityUtc >= cutoff) continue;
                    dead.Add((code, new List<WebSocket>(room.Peers)));
                    Sessions.TryRemove(new KeyValuePair<string, Room>(code, room));
                }
            }
            foreach (var (code, peers) in dead)
            {
                RelayLog.Info($"[{code}] idle for over {IdleTimeout.TotalSeconds:F0}s -- disbanding ({peers.Count} connected).");
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

            lock (AbuseLock)
            {
                var now = DateTime.UtcNow;
                foreach (var ip in JoinLockoutUntilByIp.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                    JoinLockoutUntilByIp.Remove(ip);
                foreach (var (ip, attempts) in FailedJoinsByIp.ToList())
                {
                    while (attempts.Count > 0 && now - attempts.Peek() > FailedJoinWindow) attempts.Dequeue();
                    if (attempts.Count == 0) FailedJoinsByIp.Remove(ip);
                }
            }

            var recent = Interlocked.Exchange(ref recentRejections, 0);
            if (recent >= AlertRejectionThresholdPerTick)
                RelayLog.Warn($"[ALERT] {recent} rejected connections in the last {ReapInterval.TotalSeconds:F0}s -- possible abuse in progress.");

            sinceLastSummary += ReapInterval;
            if (sinceLastSummary >= SummaryLogInterval)
            {
                sinceLastSummary = TimeSpan.Zero;
                var stats = BuildAdminStats();
                RelayLog.Info($"[Summary] {stats.Sessions} sessions, {stats.TotalPeers} peers, {stats.ConnectionsByIpCount} distinct IPs live, "
                    + $"{stats.ActiveJoinLockouts} active lockouts, {stats.TotalConnectionsAccepted} connections accepted lifetime.");
            }
        }
    }

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    // Returns how many peers it actually reached -- the caller logs that alongside the message
    // shape (see the Detail call in HandleConnectionAsync) without needing its own session lookup.
    private static async Task<int> BroadcastAsync(string sessionCode, WebSocket sender, byte[] bytes, WebSocketMessageType type)
    {
        if (!Sessions.TryGetValue(sessionCode, out var room)) return 0;
        List<WebSocket> targets;
        bool isFromHost;
        // Only THIS room's lock, not a relay-wide one -- unrelated sessions broadcasting at the
        // same time never contend with each other here, only two sends to the same session would.
        lock (room.Lock)
        {
            room.LastActivityUtc = DateTime.UtcNow;
            isFromHost = ReferenceEquals(sender, room.HostSocket);
            targets = room.Peers.Where(p => !ReferenceEquals(p, sender) && p.State == WebSocketState.Open).ToList();
        }

        // One leading byte tagging the ORIGINAL sender as this room's host or not -- lets a
        // receiving client (RelayClient.cs) tell a genuine host broadcast from a joined peer
        // forging one, without the relay ever looking past that one byte. Added once here,
        // not per-target. Requires "senderIdentity" capability support on the receiver.
        var tagged = new byte[bytes.Length + 1];
        tagged[0] = (byte)(isFromHost ? 1 : 0);
        Buffer.BlockCopy(bytes, 0, tagged, 1, bytes.Length);

        Interlocked.Increment(ref totalMessagesBroadcast);
        Interlocked.Add(ref totalBytesBroadcast, tagged.Length * (long)targets.Count);

        // One shared CancellationTokenSource for the whole fan-out instead of one per target --
        // every send below starts at essentially the same instant (WhenAll launches them all
        // before awaiting), so a shared deadline is functionally identical to a per-target one
        // while costing O(1) timer/CTS allocations per broadcast instead of O(peers). At
        // hundreds of sessions this adds up fast otherwise (see README's Security notes).
        using var cts = new CancellationTokenSource(SendTimeout);
        // Parallel, not sequential -- one slow peer must not delay delivery to everyone else.
        await Task.WhenAll(targets.Select(target => SendOneAsync(target, tagged, type, cts.Token)));
        return targets.Count;
    }

    // A timed-out send is treated as fatal for that connection (aborted, not skipped): a
    // cancelled send can leave a half-written frame in the OS buffer, and reusing the
    // connection risks interleaving a fresh frame with that leftover -- corrupting the
    // stream from then on. Abort lets both sides' own receive loops notice and clean up.
    private static async Task SendOneAsync(WebSocket target, byte[] bytes, WebSocketMessageType type, CancellationToken timeout)
    {
        try
        {
            await target.SendAsync(bytes, type, endOfMessage: true, timeout);
        }
        catch (OperationCanceledException)
        {
            RelayLog.Warn($"[Relay] Send to a peer timed out after {SendTimeout.TotalSeconds:F0}s -- aborting that connection.");
            target.Abort();
        }
        catch (WebSocketException)
        {
            // Dead socket -- its own receive loop will observe the failure and Leave().
        }
    }

    // ---- Admin CLI: `AnoMech.Relay --admin --host <url> --admin-token <token>` -----------
    // Polls a running relay's /admin/stats and renders a live text dashboard. No new
    // dependency for the TUI -- periodic clear + rewrite is simple and sufficient here.

    private static async Task RunAdminDashboardAsync(string[] args)
    {
        var port = int.TryParse(GetArg(args, "--port", "-p"), out var p) ? p : 7890;
        var host = (GetArg(args, "--host") ?? $"http://localhost:{port}").TrimEnd('/');
        var adminToken = GetArg(args, "--admin-token");
        if (string.IsNullOrEmpty(adminToken))
        {
            Console.Error.WriteLine("--admin-token is required in --admin mode.");
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("X-AnoMech-Admin-Token", adminToken);
        var uri = new Uri($"{host}/admin/stats");

        while (true)
        {
            try
            {
                var json = await http.GetStringAsync(uri);
                var stats = JsonSerializer.Deserialize<AdminStats>(json);
                RenderDashboard(stats, host, null);
            }
            catch (Exception e)
            {
                RenderDashboard(null, host, e.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static void RenderDashboard(AdminStats? s, string host, string? error)
    {
        Console.Clear();
        Console.WriteLine($"AnoMech.Relay admin -- {host}  (refreshes every 2s, Ctrl+C to exit)");
        Console.WriteLine(new string('-', 70));
        if (error != null)
        {
            Console.WriteLine($"Failed to fetch stats: {error}");
            return;
        }
        if (s == null) { Console.WriteLine("No data."); return; }

        var uptime = TimeSpan.FromSeconds(s.UptimeSeconds);
        Console.WriteLine($"Uptime:                  {(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s");
        Console.WriteLine($"Relay version:           {s.RelayVersion}");
        Console.WriteLine($"Active sessions:         {s.Sessions}");
        Console.WriteLine($"Connected peers:         {s.TotalPeers}");
        Console.WriteLine($"Distinct IPs live:       {s.ConnectionsByIpCount}");
        Console.WriteLine($"Active join lockouts:    {s.ActiveJoinLockouts}");
        Console.WriteLine();
        Console.WriteLine($"Connections accepted:    {s.TotalConnectionsAccepted}");
        Console.WriteLine($"Messages broadcast:      {s.TotalMessagesBroadcast}");
        Console.WriteLine($"Bytes broadcast:         {s.TotalBytesBroadcast:N0}");
        Console.WriteLine();
        Console.WriteLine("Rejections (lifetime):");
        Console.WriteLine($"  Origin header present:    {s.Rejections.Origin}");
        Console.WriteLine($"  Per-IP connection cap:    {s.Rejections.IpCap}");
        Console.WriteLine($"  Join lockout:             {s.Rejections.JoinLockout}");
        Console.WriteLine($"  Relay full:               {s.Rejections.RelayFull}");
        Console.WriteLine($"  Session full:             {s.Rejections.SessionFull}");
        Console.WriteLine($"  Session not found:        {s.Rejections.SessionNotFound}");
        Console.WriteLine($"  Bad/missing token:        {s.Rejections.BadToken}");
        Console.WriteLine($"  Handshake timeout:        {s.Rejections.HandshakeTimeout}");
        Console.WriteLine($"  Message too large:        {s.Rejections.MessageTooLarge}");
        Console.WriteLine($"  Message assembly timeout: {s.Rejections.MessageTimeout}");
        Console.WriteLine($"  Message rate exceeded:    {s.Rejections.MessageRate}");
        Console.WriteLine($"  Unencrypted (token set):  {s.Rejections.Unencrypted}");
        Console.WriteLine($"  Too many fragments:       {s.Rejections.TooManyFragments}");
        Console.WriteLine();
        var elevated = s.RecentRejections >= AlertRejectionThresholdPerTick;
        Console.WriteLine($"Rejections in the last ~2s: {s.RecentRejections}{(elevated ? "  [ELEVATED -- possible abuse]" : "")}");
        Console.WriteLine();
        Console.WriteLine($"Memory: {s.MemoryBytes / 1024 / 1024:N0} MB   GC collections: gen0={s.Gen0Collections} gen1={s.Gen1Collections} gen2={s.Gen2Collections}");
    }
}

// File logging: mirrors console-worthy events to disk plus much higher-volume per-message
// detail, rotated and gzip-compressed, held under a total size cap. Never logs message
// CONTENTS -- only metadata (who, when, which session, how big, how many peers it reached),
// same stance as the README's existing "the relay doesn't log message contents" note.
//
// A plain rotating/compressed log directory rather than a database: this is meant to run as a
// single, dependency-free binary an operator can drop on a box (see Program's own header
// comment on that goal) -- a database would mean standing up and separately securing another
// service just to hold logs, for a data volume this design already keeps well within one
// process's own housekeeping.
internal static class RelayLog
{
    private static string? logDir;
    private static long maxTotalBytes;
    private static StreamWriter? activeWriter;
    private static string? activeFilePath;
    private static long activeBytesWritten;

    // Segment size before a file is compressed and a new one started. Well under the total
    // cap -- keeps the always-uncompressed "live" segment a small, bounded slice of the
    // budget, and keeps each rotation's compression work modest.
    private const long RotateThresholdBytes = 64 * 1024 * 1024;

    // Producers (Info/Warn/Detail, called from every connection's own async flow) only ever
    // enqueue a string -- no lock, no disk I/O, on that path. A single background task is the
    // only thing that ever touches the file, so it needs no locking either. At hundreds of
    // sessions all broadcasting, this is what keeps logging from becoming the actual bottleneck
    // (it used to be a synchronous, AutoFlush=true write under one relay-wide lock, shared by
    // literally every connection).
    // Bounded, not unbounded: a stuck/full disk should drop log lines rather than let the queue
    // grow without limit and eventually pressure the process's own memory. Capacity is generous
    // relative to realistic burst rates -- dropping is the rare, "something's already wrong" case.
    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(20_000) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    private static long droppedLines;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    private static DateTime lastFlushUtc = DateTime.UtcNow;

    public static void Configure(string directory, long maxBytes)
    {
        logDir = directory;
        maxTotalBytes = maxBytes;
        Directory.CreateDirectory(logDir);
        OpenNewActiveFile();
        _ = RunWriterLoopAsync();
    }

    // The only place that touches activeWriter/activeBytesWritten/rotation/compression --
    // single-reader by construction (see Queue above), so none of that needs its own lock.
    private static async Task RunWriterLoopAsync()
    {
        await foreach (var line in Queue.Reader.ReadAllAsync())
        {
            activeWriter!.WriteLine(line);
            activeBytesWritten += line.Length + 2;

            // Batches flushes instead of one disk write per line (what AutoFlush did) --
            // still bounds how stale the file can be to ~1s, without paying a syscall per
            // message broadcast under real load.
            if (DateTime.UtcNow - lastFlushUtc >= FlushInterval)
            {
                activeWriter.Flush();
                lastFlushUtc = DateTime.UtcNow;
            }

            if (activeBytesWritten >= RotateThresholdBytes)
                Rotate();
        }
    }

    private static void OpenNewActiveFile()
    {
        activeFilePath = Path.Combine(logDir!, $"relay-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log");
        // FileShare.ReadWrite (not StreamWriter's own default of Read-only sharing) so
        // --session-log can open and read the still-active segment while the relay keeps
        // writing to it, instead of hitting a sharing-violation IOException.
        var stream = new FileStream(activeFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        activeWriter = new StreamWriter(stream, Encoding.UTF8);
        activeBytesWritten = 0;
    }

    // Console + file. Events an operator should see live: startup, session lifecycle,
    // rejections severe enough to already carry their own explicit message, alerts, summaries.
    public static void Info(string message)
    {
        Console.WriteLine(message);
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Console.Error.WriteLine(message);
        Write("WARN", message);
    }

    // File only. For anything high-volume enough that echoing it to the console would drown
    // out the events actually worth watching live: individual rejection reasons, every broadcast.
    public static void Detail(string message) => Write("DETAIL", message);

    private static void Write(string level, string message)
    {
        if (logDir == null) return; // file logging disabled (--no-file-log) -- console-only.
        var line = $"{DateTime.UtcNow:O} [{level}] {message}";
        if (!Queue.Writer.TryWrite(line))
            Interlocked.Increment(ref droppedLines);
    }

    // Called from the writer loop only.
    private static void Rotate()
    {
        activeWriter!.Dispose();
        var finished = activeFilePath!;
        OpenNewActiveFile();
        var dropped = Interlocked.Exchange(ref droppedLines, 0);
        if (dropped > 0) activeWriter!.WriteLine($"{DateTime.UtcNow:O} [WARN] {dropped} log line(s) dropped -- write queue was full.");
        CompressAndDelete(finished);
        EnforceCap();
    }

    // Best-effort flush for a graceful shutdown (see Main's ProcessExit hook) -- anything still
    // sitting in the queue at the instant of a hard kill is lost either way, same tradeoff any
    // buffered logger makes.
    public static void FlushOnShutdown()
    {
        try { activeWriter?.Flush(); } catch { /* best effort */ }
    }

    private static void CompressAndDelete(string path)
    {
        try
        {
            using (var input = File.OpenRead(path))
            using (var output = File.Create(path + ".gz"))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                input.CopyTo(gzip);
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort -- an uncompressed leftover segment still counts toward EnforceCap's
            // total below, so it still ages out via oldest-first deletion even if compression
            // itself failed for some reason (e.g. disk full).
        }
    }

    // Deletes the oldest completed (.gz) segments until the directory is back under the cap.
    // Never touches the currently-active segment.
    private static void EnforceCap()
    {
        var completed = new DirectoryInfo(logDir!).GetFiles("relay-*.log.gz").OrderBy(f => f.Name).ToList();
        var activeSize = File.Exists(activeFilePath) ? new FileInfo(activeFilePath!).Length : 0;
        var total = activeSize + completed.Sum(f => f.Length);
        foreach (var file in completed)
        {
            if (total <= maxTotalBytes) break;
            total -= file.Length;
            try { file.Delete(); } catch (IOException) { /* best effort */ }
        }
    }

    // `AnoMech.Relay --session-log <CODE> --log-dir <dir>` -- scans every segment (live and
    // compressed), oldest first, for lines tagged with that session code. The tag format
    // ("[CODE] ...") is the same one every session-scoped log line already uses, so this needs
    // no separate structured format to stay useful.
    public static void PrintSessionLog(string directory, string sessionCode)
    {
        var tag = $"[{sessionCode}]";
        if (!Directory.Exists(directory))
        {
            Console.Error.WriteLine($"No log directory at {directory}.");
            return;
        }
        var files = new DirectoryInfo(directory).GetFiles("relay-*.log*").OrderBy(f => f.Name).ToList();
        var found = 0;
        foreach (var file in files)
        {
            // ReadWrite sharing -- the currently-active segment is still open for writing by
            // a live relay process (see OpenNewActiveFile) when this runs alongside it.
            var raw = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = file.Extension == ".gz"
                ? new StreamReader(new GZipStream(raw, CompressionMode.Decompress))
                : new StreamReader(raw);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains(tag)) continue;
                Console.WriteLine(line);
                found++;
            }
        }
        if (found == 0) Console.WriteLine($"No log lines found for session {sessionCode} under {directory}.");
    }
}
