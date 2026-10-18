using System.Globalization;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios;

namespace AnoMech.Tests;

// How every client shows the other players moving, over good and bad links: no snap a fast move of
// their own doesn't explain, no standing still while they walk, no rushing to catch up, never far
// behind, and standing where they stopped. Each player is a debug bot on their own client, so their
// real path is what their own client has them do. Every other client's copy is drawn from the
// packets that client's game was sent, the way its ground controller walks them (ModelSync): at
// each ActorMove's speed byte toward its spot, and a jump at each ActorSetPos. The fake's own model
// glides every ActorMove over a second, which hides a copy stopping and starting.
public class RemotePlayerMotionTests
{
    private const PartyRole HostSeat = PartyRole.MeleeDpsA;

    // A long line; a VPN at half a second's ping, as players have played through; bad Wi-Fi; a
    // host whose frame rate spaces its poses out.
    private static readonly ConnectionProfile LongLine = TestRelay.Perfect with
    {
        Name = "250 ms ping", UploadLatency = 0.125, DownloadLatency = 0.125, DownloadJitter = 0.03,
    };

    private static readonly ConnectionProfile Vpn = new("500 ms ping over a VPN",
        UploadRate: 1_000_000, SendBufferBytes: 64 * 1024, UploadLatency: 0.25,
        UploadStallEvery: 6, UploadStallMin: 0.1, UploadStallMax: 0.3,
        DownloadRate: 2_000_000, DownloadLatency: 0.25, DownloadJitter: 0.1,
        DownloadStallEvery: 6, DownloadStallMin: 0.1, DownloadStallMax: 0.4,
        PeerHitchEvery: 8, PeerHitchMin: 0.08, PeerHitchMax: 0.2);

    private static readonly ConnectionProfile SlowHost = LongLine with { Name = "20 fps host, 250 ms ping", HostFps = 20 };

    private static readonly ConnectionProfile[] Links = [TestRelay.Perfect, LongLine, Vpn, BadConnections.BadWifiPeer, SlowHost];

    private static IEnumerable<TestCaseData> Cases() =>
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        from link in Links
        select new TestCaseData(scenario.GetType(), link).SetArgDisplayNames(scenario.FullName(), link.Name);

    [TestCaseSource(nameof(Cases))]
    public void EveryClientShowsTheOtherPlayersMovingSmoothly(Type scenarioType, ConnectionProfile link)
    {
        // Every frame, the copies' walks before the run included.
        var frames = new Dictionary<string, List<double>>();
        var runStarts = new Dictionary<string, double>();
        var owners = new Dictionary<PartyRole, List<(double At, Vector3 Position)>>();
        // A frame starts where the last one left the player: that one's frame drew it.
        var lastFrame = new Dictionary<string, double>();
        var peers = Enum.GetValues<PartyRole>().Where(r => r != HostSeat).Select(r => new PlayerSeat(r)).ToList();
        var hostLink = TestRelay.Perfect with { HostFps = link.HostFps };
        var run = MultiplayerRun.Play(scenarioType, 0, 1, new PlayerSeat(HostSeat), peers, hostLink: hostLink, peerLink: link,
            configure: client => client.Fake.UserActions.Enabled = true,
            beforeFrame: client =>
            {
                var at = (double)client.Fake.Timeline.Clock();
                Track(frames, client.Name).Add(at);
                var inRun = client.Game.ActiveScenario is not null;
                if (inRun && lastFrame.TryGetValue(client.Name, out var drawnAt))
                    Track(owners, client.Seat).Add((drawnAt, client.Fake.BattleCharas.Player.Position));
                if (inRun) runStarts.TryAdd(client.Name, at);
                lastFrame[client.Name] = at;
            });
        Assert.That(run.Failure, Is.Null);

        var report = Motion.Measure(run, link, frames, runStarts, owners);
        foreach (var line in report.Lines) TestContext.Out.WriteLine(line);
        Assert.That(report.Problems, Is.Empty, string.Join(Environment.NewLine, report.Problems.Take(20)));
    }

    private static List<T> Track<TKey, T>(Dictionary<TKey, List<T>> into, TKey key) where TKey : notnull
    {
        if (!into.TryGetValue(key, out var list)) into[key] = list = [];
        return list;
    }

    // The measures and their limits.
    private static class Motion
    {
        // The copies' own placement as each client's run starts.
        private const double SettleSeconds = 1;
        // Faster than any run: a knockback, a carry, a teleport. A snap or a rush showing one is
        // fine until it has had time to reach the viewer.
        private const float FastSpeed = 10f;
        private const double FastMoveWindow = 2.5;
        // A player who has walked this long at this speed should be shown walking.
        private const float WalkSpeed = 3f;
        private const double WalkWindow = 0.6;
        private const float StillSpeed = 0.5f;

        // Shares of the time the player walks, past a stall or two's worth.
        private const double MaxStill = 0.1;
        private const double MaxRushing = 0.2;
        private const double StallSeconds = 0.5;
        // Behind where the player was a path delay ago; at most, a stall's worth of sprinting
        // besides.
        private const float MaxLagP95 = 3f;
        private const float MaxLag = 8f;
        // A player standing this long has every copy standing on their spot.
        private const double RestSeconds = 1.5;
        private const float MaxRestError = 0.1f;

        public sealed record Report(List<string> Lines, List<string> Problems);

        private sealed record Packet(double At, string Kind, Vector3 Position, float Speed);

        private sealed record Pair(string Viewer, PartyRole Seat, int Snaps, double Still, double Rushing, float LagP95, float LagMax, double LagMaxAt,
            float RestError, double RestErrorAt);

        public static Report Measure(MultiplayerRun run, ConnectionProfile link, Dictionary<string, List<double>> frames,
            Dictionary<string, double> runStarts, Dictionary<PartyRole, List<(double At, Vector3 Position)>> owners)
        {
            var problems = new List<string>();
            var pairs = new List<Pair>();
            // While the fight runs as it should: a death changes what everyone does next.
            var until = run.Deaths.Select(d => run.StartedAt + d.Time).Append(run.EndedAt).Min();
            foreach (var viewer in run.Peers.Prepend(run.Host))
            foreach (var (seat, owner) in owners.Where(o => o.Key != viewer.Seat).OrderBy(o => o.Key))
            {
                var from = runStarts[viewer.Name] + SettleSeconds;
                var packets = Packets(viewer, seat);
                var drawn = Draw(packets, frames[viewer.Name]);
                if (drawn.Count < 2)
                {
                    problems.Add($"{viewer.Name} never drew {seat}");
                    continue;
                }
                var fast = FastMoves(owner);
                var delay = Delay(link, viewer.Name, seat);
                var snaps = packets.Where(p => p.Kind == "set_pos" && p.At >= from && p.At < until && !Explained(p.At, fast)).Select(p => p.At).ToList();
                double walking = 0, still = 0, rushing = 0;
                var stillStretch = new Stretch();
                var rushStretch = new Stretch();
                var lags = new List<(float Lag, double At)>();
                var (restError, restErrorAt) = (0f, 0.0);
                for (var i = 1; i < drawn.Count; i++)
                {
                    var (at, position) = drawn[i];
                    var dt = at - drawn[i - 1].At;
                    var then = at - delay;
                    if (dt <= 0 || at < from || at >= until || then < owner[0].At || Explained(at, fast)) continue;
                    lags.Add((Vector3.Distance(position, PositionAt(owner, then)), at));
                    if (StandingThrough(owner, then - RestSeconds, then) && Vector3.Distance(position, PositionAt(owner, then)) is var off && off > restError)
                        (restError, restErrorAt) = (off, at);
                    if (!WalkingThrough(owner, then - WalkWindow, then)) continue;
                    walking += dt;
                    var speed = Vector3.Distance(position, drawn[i - 1].Position) / dt;
                    var isStill = speed < StillSpeed;
                    var isRushing = !isStill && speed > 2 * SpeedOver(owner, then - 0.2, then);
                    if (isStill) still += dt;
                    if (isRushing) rushing += dt;
                    stillStretch.Add(isStill, at, dt);
                    rushStretch.Add(isRushing, at, dt);
                }
                lags.Sort((a, b) => a.Lag.CompareTo(b.Lag));
                var pair = new Pair(viewer.Name, seat, snaps.Count, walking > 0 ? still / walking : 0, walking > 0 ? rushing / walking : 0,
                    lags.Count > 0 ? lags[(int)(lags.Count * 0.95)].Lag : 0, lags.Count > 0 ? lags[^1].Lag : 0, lags.Count > 0 ? lags[^1].At : 0,
                    restError, restErrorAt);
                pairs.Add(pair);
                var who = $"{viewer.Name}'s copy of {seat}";
                foreach (var at in snaps.Take(3)) problems.Add($"{who} snapped at {at - run.StartedAt:F2}s with no fast move of theirs to show");
                if (pair.Still > MaxStill && still > StallSeconds)
                    problems.Add($"{who} stood still {still:F1}s of the {walking:F1}s they walked, {stillStretch.Longest:F2}s on end at {stillStretch.LongestAt - run.StartedAt:F1}s");
                if (pair.Rushing > MaxRushing && rushing > StallSeconds)
                    problems.Add($"{who} rushed {rushing:F1}s of the {walking:F1}s they walked, {rushStretch.Longest:F2}s on end at {rushStretch.LongestAt - run.StartedAt:F1}s");
                if (pair.LagP95 > MaxLagP95) problems.Add($"{who} was {pair.LagP95:F1} y behind them at p95");
                if (pair.LagMax > MaxLag) problems.Add($"{who} fell {pair.LagMax:F1} y behind them at {pair.LagMaxAt - run.StartedAt:F1}s");
                if (pair.RestError > MaxRestError) problems.Add($"{who} stood {pair.RestError:F2} y from where they stood at {pair.RestErrorAt - run.StartedAt:F1}s");
            }
            var lines = new List<string>
            {
                $"{pairs.Count} copies: {pairs.Sum(p => p.Snaps)} snaps, still up to {pairs.Max(p => p.Still):P1}, rushing up to {pairs.Max(p => p.Rushing):P1}, "
                + $"lag p95 up to {pairs.Max(p => p.LagP95):F2} y, lag up to {pairs.Max(p => p.LagMax):F2} y, off their standing spot up to {pairs.Max(p => p.RestError):F2} y",
            };
            lines.AddRange(pairs.OrderByDescending(p => p.LagMax).Take(5)
                .Select(p => $"  {p.Viewer} sees {p.Seat}: still {p.Still:P1}, rushing {p.Rushing:P1}, lag p95 {p.LagP95:F2} y, max {p.LagMax:F2} y at {p.LagMaxAt - run.StartedAt:F1}s"));
            return new Report(lines, problems);
        }

        // The longest run of frames something held for.
        private sealed class Stretch
        {
            private double current, currentFrom;
            public double Longest { get; private set; }
            public double LongestAt { get; private set; }

            public void Add(bool holds, double at, double dt)
            {
                if (!holds)
                {
                    current = 0;
                    return;
                }
                if (current == 0) currentFrom = at - dt;
                current += dt;
                if (current > Longest) (Longest, LongestAt) = (current, currentFrom);
            }
        }

        // Each client's own part of the path: its poses up to the host, the host's down to it.
        private static double Delay(ConnectionProfile link, string viewer, PartyRole seat)
        {
            var fromOwner = seat == HostSeat ? 0 : link.UploadLatency;
            var toViewer = viewer == "Host" ? 0 : link.DownloadLatency;
            return fromOwner + toViewer;
        }

        private static List<Packet> Packets(TestClient viewer, PartyRole seat)
        {
            var packets = new List<Packet>();
            foreach (var row in viewer.Fake.Timeline.Rows)
            {
                if (row["kind"] is not ("spawn_doppel" or "set_pos" or "move") || row.GetValueOrDefault("actor") is not { Length: > 0 } actor) continue;
                var at = Number(row["t"]);
                if (viewer.NameAt(uint.Parse(actor, NumberStyles.HexNumber), at) != seat.ToString()) continue;
                var position = new Vector3((float)Number(row["x"]), (float)Number(row["y"]), (float)Number(row["z"]));
                packets.Add(new Packet(at, row["kind"], position, row["kind"] == "move" ? SpeedByte(row["detail"]) : 0f));
            }
            return packets;
        }

        // As BattleCharaProxy.Move puts it on the wire: tenths of a y/s in a byte.
        private static float SpeedByte(string detail)
        {
            var speed = (float)Number(detail[(detail.IndexOf("speed=", StringComparison.Ordinal) + "speed=".Length)..]);
            return ((int)MathF.Round(speed * 10f) & 0xFF) / 10f;
        }

        private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

        // Timeline rows carry milliseconds.
        private const double RowRounding = 0.0005;

        // The copy as each of the viewer's frames draws it, from its spawn on. A frame takes its
        // packets, then walks the copy the frame's time, as the client's own update does.
        private static List<(double At, Vector3 Position)> Draw(List<Packet> packets, List<double> frames)
        {
            var drawn = new List<(double At, Vector3 Position)>();
            var placed = false;
            Vector3 shown = default, target = default;
            var speed = 0f;
            var next = 0;
            for (var i = 0; i < frames.Count; i++)
            {
                while (next < packets.Count && packets[next].At <= frames[i] + RowRounding)
                {
                    var packet = packets[next++];
                    if (packet.Kind == "move")
                    {
                        target = packet.Position;
                        speed = packet.Speed;
                    }
                    else
                    {
                        shown = target = packet.Position;
                        speed = 0f;
                        placed = true;
                    }
                }
                if (i > 0)
                {
                    var step = speed * (float)(frames[i] - frames[i - 1]);
                    var away = target - shown;
                    var distance = away.Length();
                    shown = distance <= step ? target : shown + away / distance * step;
                }
                if (placed) drawn.Add((frames[i], shown));
            }
            return drawn;
        }

        private static List<double> FastMoves(List<(double At, Vector3 Position)> path)
        {
            var at = new List<double>();
            for (var i = 1; i < path.Count; i++)
            {
                var dt = path[i].At - path[i - 1].At;
                if (dt > 0 && Vector3.Distance(path[i].Position, path[i - 1].Position) / dt > FastSpeed) at.Add(path[i].At);
            }
            return at;
        }

        private static bool Explained(double at, List<double> fast)
        {
            var i = fast.BinarySearch(at + 0.1);
            if (i < 0) i = ~i;
            return i > 0 && fast[i - 1] >= at - FastMoveWindow;
        }

        private static Vector3 PositionAt(List<(double At, Vector3 Position)> path, double at)
        {
            int lo = 0, hi = path.Count - 1;
            if (at <= path[lo].At) return path[lo].Position;
            if (at >= path[hi].At) return path[hi].Position;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (path[mid].At <= at) lo = mid;
                else hi = mid;
            }
            var (a, b) = (path[lo], path[hi]);
            return Vector3.Lerp(a.Position, b.Position, (float)((at - a.At) / (b.At - a.At)));
        }

        private static float SpeedOver(List<(double At, Vector3 Position)> path, double from, double to)
            => (float)(Vector3.Distance(PositionAt(path, to), PositionAt(path, from)) / (to - from));

        // Standing all through: never more than a hair from where they end up.
        private static bool StandingThrough(List<(double At, Vector3 Position)> path, double from, double to)
        {
            if (from < path[0].At) return false;
            var end = PositionAt(path, to);
            for (var t = from; t < to; t += 0.1)
                if (Vector3.Distance(PositionAt(path, t), end) > 0.01f) return false;
            return true;
        }

        // Walking all through: every tenth of a second of it at walking speed.
        private static bool WalkingThrough(List<(double At, Vector3 Position)> path, double from, double to)
        {
            if (from < path[0].At) return false;
            for (var t = from; t < to - 1e-9; t += 0.1)
                if (SpeedOver(path, t, Math.Min(t + 0.1, to)) < WalkSpeed) return false;
            return true;
        }
    }
}
