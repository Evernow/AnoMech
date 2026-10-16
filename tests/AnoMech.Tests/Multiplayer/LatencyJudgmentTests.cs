using System.Numerics;
using AnoMech.Core.EnemyActions;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;

namespace AnoMech.Tests;

// A peer's player is judged where their own screen had them when it showed the host's resolve,
// however far the host is: every hit an area deals a peer is checked against the spot that peer's
// own client had them at that moment, and a step into or out of an area just before it lands
// counts though the host still has the player on the other side of its edge. Every multiplayer
// fight, so a resolve a new scenario judges some other way shows up as it lands.
public class LatencyJudgmentTests
{
    private const PartyRole HostSeat = PartyRole.MeleeDpsA;
    private const int Seed = 1;

    // The trip each way and its jitter: a client's clock keeps the time of what it's shown, give or
    // take that jitter.
    private static readonly ConnectionProfile[] Links =
    [
        Link(0.1, 0.02),
        Link(0.3, 0.06),
        Link(0.5, 0.1),
    ];

    private static ConnectionProfile Link(double oneWay, double jitter)
        => TestRelay.Perfect with { Name = $"{oneWay * 1000:F0} ms each way", UploadLatency = oneWay, DownloadLatency = oneWay, DownloadJitter = jitter };

    // A spot this far either side of an edge is on that side for the whole window.
    private const float EdgeMargin = 0.05f;

    private static IEnumerable<TestCaseData> FightsOverLinks() =>
        from link in Links
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        select new TestCaseData(scenario.GetType(), link).SetArgDisplayNames(link.Name, scenario.FullName());

    private static IEnumerable<TestCaseData> Fights() =>
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        select new TestCaseData(scenario.GetType()).SetArgDisplayNames(scenario.FullName());

    [TestCaseSource(nameof(FightsOverLinks))]
    public void EveryHitOnAPeerLandsWhereItsOwnScreenHadIt(Type scenarioType, ConnectionProfile link)
    {
        var played = Play(scenarioType, link, peer => new PlayerSeat(peer));
        Assert.That(played.Run.Failure, Is.Null);
        var window = (float)link.DownloadJitter + 0.05f;
        var problems = new List<string>();
        var checkedHits = 0;
        foreach (var part in played.Parts.Where(p => p.Area is not null))
        foreach (var seat in part.Members.Where(played.Screens.ContainsKey))
        {
            var spots = played.Screens[seat].Where(s => MathF.Abs(s.Clock - part.At) <= window).ToList();
            if (spots.Count == 0) continue;
            var edge = spots.Select(s => part.Area!.Value.SignedDistance(new Vector3(s.At.X, 0f, s.At.Y)) ?? float.NaN).ToList();
            if (edge.Any(float.IsNaN)) continue;
            var hit = part.Hits.Contains(seat);
            if (edge.All(d => d < -EdgeMargin))
            {
                checkedHits++;
                if (!hit && !part.Filtered) problems.Add($"{Name(part)} missed {seat}, inside it on its own screen ({edge.Max():F2}y)");
            }
            else if (edge.All(d => d > EdgeMargin))
            {
                checkedHits++;
                if (hit) problems.Add($"{Name(part)} hit {seat}, clear of it on its own screen ({edge.Min():F2}y)");
            }
        }
        TestContext.Out.WriteLine($"{checkedHits} peer judgments checked, {played.Run.Deaths.Count} deaths");
        foreach (var problem in problems.Take(30)) TestContext.Out.WriteLine(problem);
        Assert.That(checkedHits, Is.GreaterThan(0), "no area judged a peer");
        Assert.That(problems, Is.Empty);
    }

    // At 300 ms each way the host has a peer where it was 0.6 s before its own screen's moment: a
    // step that late is on the far side of the edge for the host, and still has to count.
    private static readonly ConnectionProfile StepLink = Link(0.3, 0.06);
    private const float StepLead = 0.15f;
    private const float HeldInside = 1.2f;
    private const float StaysAfter = 0.3f;

    [TestCaseSource(nameof(Fights))]
    public void APeerSteppingOutOfAnAreaJustBeforeItLandsIsClearOfIt(Type scenarioType) => Step(scenarioType, stepIn: false);

    [TestCaseSource(nameof(Fights))]
    public void APeerSteppingIntoAnAreaJustBeforeItLandsIsHitByIt(Type scenarioType) => Step(scenarioType, stepIn: true);

    private static void Step(Type scenarioType, bool stepIn)
    {
        var reference = Play(scenarioType, TestRelay.Perfect, peer => new PlayerSeat(peer));
        Assert.That(reference.Run.Failure, Is.Null);
        var tried = new List<string>();
        var window = (float)StepLink.DownloadJitter + 0.05f;
        foreach (var (seat, part, inside) in Candidates(reference, stepIn).DistinctBy(c => (c.Part.ActionId, c.Part.At)).Take(6))
        {
            var walk = reference.Walks[seat];
            var (from, to) = Window(part, stepIn);
            var played = Play(scenarioType, StepLink, peer => new PlayerSeat(peer, BotControlled: peer != seat),
                client => { if (client.Seat == seat) Walk(client, walk, from, to, inside); });
            if (played.Run.Failure is { } failure)
            {
                tried.Add($"{seat} at {Name(part)}: the run failed: {failure}");
                continue;
            }
            // A judgment that waited on a slow link can run a follow-up a little later.
            var landed = played.Parts.Where(p => p.ActionId == part.ActionId && MathF.Abs(p.At - part.At) < 0.5f && p.Members.Contains(seat) && p.Area is not null)
                .MinBy(p => MathF.Abs(p.At - part.At));
            if (landed is null)
            {
                tried.Add($"{seat} at {Name(part)}: the fight went another way, it never landed there");
                continue;
            }
            // Against the area that landed, which can sit a little apart from the reference's.
            var area = landed.Area!.Value;
            var own = played.Screens[seat].Where(s => MathF.Abs(s.Clock - landed.At) <= window).Select(s => Edge(area, s.At)).ToList();
            if (own.Count == 0 || (stepIn ? own.Any(d => !(d < -EdgeMargin)) : own.Any(d => !(d > EdgeMargin))))
            {
                tried.Add($"{seat} at {Name(part)}: its own screen didn't have it {(stepIn ? "inside" : "clear")} as it landed");
                continue;
            }
            var hostHad = played.HostPoses[seat].LastOrDefault(p => p.Clock <= landed.At);
            var hostSide = Edge(area, hostHad.At);
            if (stepIn ? !(hostSide > EdgeMargin) : !(hostSide < -EdgeMargin))
            {
                tried.Add($"{seat} at {Name(part)}: the host had it on the same side, {hostSide:F2}y from the edge");
                continue;
            }
            TestContext.Out.WriteLine($"{seat} {(stepIn ? "into" : "out of")} {Name(part)}: the host had it {hostSide:F2}y from the edge; "
                                      + (landed.Hits.Contains(seat) ? "hit" : "clear"));
            Assert.That(landed.Hits.Contains(seat), Is.EqualTo(stepIn), stepIn
                ? $"{seat} stood in {Name(part)} on its own screen, but the host judged it where it had last seen it"
                : $"{seat} had left {Name(part)} on its own screen, but the host judged it where it had last seen it");
            return;
        }
        Assert.Fail($"no area to step {(stepIn ? "into" : "out of")} worked out: {string.Join("; ", tried.DefaultIfEmpty("none found"))}");
    }

    // Areas a peer's bot cleared on its own screen, with a spot inside them clear of every other
    // area around then, and for a step in, where it was a trip earlier clear of it too. Placed by no
    // player first, as those land where they did.
    private static IEnumerable<(PartyRole Seat, Seen Part, Vector2 Inside)> Candidates(Played reference, bool stepIn)
    {
        var trip = (float)(StepLink.UploadLatency + StepLink.DownloadLatency);
        foreach (var part in reference.Parts.Where(p => p is { Area: not null, Filtered: false } && p.At > HeldInside + 1f).OrderBy(p => p.OnPlayer))
        foreach (var seat in part.Members.Where(reference.Walks.ContainsKey))
        {
            var walk = reference.Walks[seat];
            var area = part.Area!.Value;
            var at = Flat(walk.At(part.At).Position, reference);
            if (Edge(area, at) <= 1f) continue;
            if (stepIn && Enumerable.Range(0, 6).Any(i => Edge(area, Flat(walk.At(part.At - trip - 0.15f + i * 0.04f).Position, reference)) <= 1f)) continue;
            // While it stands on the spot, every other area lands on it as on its own path: a stack or
            // tower it was in still counts it, anything else misses it, so the fight goes on as it did.
            var (from, to) = Window(part, stepIn);
            var near = reference.Parts.Where(p => p.Area is not null && !SameResolve(p, part) && p.At > from - 0.15f && p.At < to + 0.15f)
                .Select(p => (Area: p.Area!.Value, Keep: p.Shared && Edge(p.Area!.Value, Flat(walk.At(p.At).Position, reference)) < -1f)).ToList();
            if (InsideSpot(area, at, near) is { } inside) yield return (seat, part, inside);
        }
    }

    private static Vector2? InsideSpot(AoeQuery area, Vector2 from, IReadOnlyList<(AoeQuery Area, bool Keep)> near)
    {
        for (var r = 0.5f; r <= 15f; r += 0.5f)
        for (var k = 0; k < 16; k++)
        {
            var angle = k * MathF.PI / 8f;
            var spot = from + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
            if (Edge(area, spot) < -1f && near.All(p => p.Keep ? Edge(p.Area, spot) < -1f : Edge(p.Area, spot) > 1f)) return spot;
        }
        return null;
    }

    // When the step has the player on the inside spot, on their own clock.
    private static (float From, float To) Window(Seen part, bool stepIn)
        => stepIn ? (part.At - StepLead, part.At + StaysAfter) : (part.At - HeldInside, part.At - StepLead);

    // One resolve is judged in parts, one per peer.
    private static bool SameResolve(Seen a, Seen b)
        => a.ActionId == b.ActionId && a.At == b.At && a.Area!.Value.Source == b.Area!.Value.Source;

    private static float Edge(AoeQuery area, Vector2 spot) => area.SignedDistance(new Vector3(spot.X, 0f, spot.Y)) ?? float.NaN;

    // The walk records the client's own world-space model; areas are scenario-local.
    private static Vector2 Flat(Vector3 world, Played played)
    {
        var local = world - played.Origin;
        return new Vector2(local.X, local.Z);
    }

    // The player's own input: the recorded walk, but standing on `inside` from `from` to `to`.
    private static void Walk(TestClient client, StratWalk walk, float from, float to, Vector2 inside)
    {
        walk.Replay(client);
        var clock = client.Game.Events.Elapsed;
        if (client.Game.ActiveScenario is null || client.Game.Player is not { Dead: false } || clock < from || clock > to) return;
        var character = client.Fake.BattleCharas.Player;
        character.Position = client.Game.World.Coordinates.ToGlobal(new Vector3(inside.X, 0f, inside.Y));
        client.Fake.PlayerInput.MovementInputActive = true;
    }

    // ── One session ─────────────────────────────────────────────────────────

    private sealed record Seen(uint ActionId, float At, IReadOnlyList<PartyRole> Members, IReadOnlyList<PartyRole> Hits, AoeQuery? Area,
        bool Filtered, bool OnPlayer, bool Shared);

    private sealed record Played(MultiplayerRun Run, IReadOnlyList<Seen> Parts, Vector3 Origin,
        IReadOnlyDictionary<PartyRole, List<(float Clock, Vector2 At)>> Screens,
        IReadOnlyDictionary<PartyRole, List<(float Clock, Vector2 At)>> HostPoses,
        IReadOnlyDictionary<PartyRole, StratWalk> Walks);

    // Every non-host seat a peer; each peer's own player recorded on its own clock, every peer seat
    // as the host has it on the host's, and what the host judged.
    private static Played Play(Type scenarioType, ConnectionProfile link, Func<PartyRole, PlayerSeat> seatOf, Action<TestClient>? drive = null)
    {
        var parts = new List<Seen>();
        var peers = Enum.GetValues<PartyRole>().Where(r => r != HostSeat).ToList();
        var screens = peers.ToDictionary(r => r, _ => new List<(float, Vector2)>());
        var hostPoses = peers.ToDictionary(r => r, _ => new List<(float, Vector2)>());
        var walks = peers.ToDictionary(r => r, _ => new StratWalk());
        var origin = Vector3.Zero;
        void Judged(EnemyActionHandler.JudgedPart part)
        {
            if (Plugin.Multiplayer is not { IsHost: true }) return;
            parts.Add(new Seen(part.ActionId, part.At, Roles(part.Members), Roles(part.Hits), part.Area, part.Filtered, part.OnPlayer, part.Shared));
        }

        EnemyActionHandler.Judged += Judged;
        try
        {
            var run = MultiplayerRun.Play(scenarioType, 0, Seed, new PlayerSeat(HostSeat), peers.Select(seatOf).ToList(), peerLink: link,
                configure: client => client.Fake.UserActions.Enabled = true,
                beforeFrame: client =>
                {
                    if (client.Game.ActiveScenario is null) return;
                    var clock = client.Game.Events.Elapsed;
                    if (client.IsHost)
                    {
                        origin = client.Game.World.ScenarioOrigin;
                        foreach (var peer in peers)
                            if (client.Game.World.Party.Get(peer) is { } puppet)
                                hostPoses[peer].Add((clock, new Vector2(puppet.Position.X, puppet.Position.Z)));
                        return;
                    }
                    drive?.Invoke(client);
                    walks[client.Seat].Record(client);
                    if (client.Game.Player is { } player)
                        screens[client.Seat].Add((clock, new Vector2(player.Position.X, player.Position.Z)));
                });
            return new Played(run, parts, origin, screens, hostPoses, walks);
        }
        finally
        {
            EnemyActionHandler.Judged -= Judged;
        }
    }

    private static IReadOnlyList<PartyRole> Roles(IEnumerable<SimCharacter> characters)
        => characters.OfType<ISimPartyMember>().Select(m => m.Role).ToList();

    private static string Name(Seen part)
        => $"{Natives.Data.ActionName(part.ActionId)} ({part.ActionId}) at {part.At:F2}s";
}
