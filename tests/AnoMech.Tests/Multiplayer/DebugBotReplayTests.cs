using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios;

namespace AnoMech.Tests;

// Every seat but the host's played by a bot-controlled peer, each client rolling its own seed: a
// peer's Ai runs on the rolls the host broadcast, never on its own RNG, and on its own copy of the
// world, built from what its client was shown. Each has to head for the spot the host's Ai
// planned for that seat, and like any strat clear the fight. Every multiplayer scenario and strat,
// so a roll a replay message leaves out or garbles, or an actor a peer was never shown, shows up
// in whichever seat reads it. Every client plays with the plugin's defaults, user actions and tank
// mitigation on, so a hit that judges a player differently from a bot judges these seats too: a
// bot is a bot on whichever client it runs. Over a slow link each peer's client shows the fight a
// trip late and its moves reach the host a trip late, so a strat that acts on what the client
// shows has to wait for it to show. The host still picks targets and runs its continuous checks on
// where it has each peer, a round trip behind that peer's screen, so the fight has to clear only
// over a link whose trip strats timed to the frame can take; LatencyJudgmentTests holds every hit
// to each peer's own screen however long the trip.
public partial class DebugBotReplayTests
{
    private const PartyRole HostSeat = PartyRole.MeleeDpsA;
    // The spot before AiManager's jitter, which each client rolls itself; logged to 0.01y.
    private const float SpotTolerance = 0.05f;
    // Over a slow link a spot read off a moving actor drifts by what it moved in the trip; a
    // different decision lands yalms away.
    private const float SlowLinkSpotTolerance = 1f;

    private static IEnumerable<TestCaseData> Strats() =>
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        from strat in scenario.AiStrats.Select((ai, index) => (ai, index))
        select new TestCaseData(scenario.GetType(), strat.index).SetArgDisplayNames(scenario.FullName(), strat.ai.Name);

    [TestCaseSource(nameof(Strats))]
    public void EveryBotPeerWalksWhereTheHostsAiSendsItsSeat(Type scenarioType, int strat) => Verify(scenarioType, strat, runs: 1);

    // Links players have to the host, the trip each way and its jitter.
    private static readonly ConnectionProfile[] SlowLinks =
    [
        TestRelay.Perfect with { Name = "100 ms each way", UploadLatency = 0.1, DownloadLatency = 0.1, DownloadJitter = 0.02 },
    ];

    private static IEnumerable<TestCaseData> SlowLinkStrats() =>
        from link in SlowLinks
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        from strat in scenario.AiStrats.Select((ai, index) => (ai, index))
        select new TestCaseData(scenario.GetType(), strat.index, link).SetArgDisplayNames(link.Name, scenario.FullName(), strat.ai.Name);

    [TestCaseSource(nameof(SlowLinkStrats))]
    public void EveryBotPeerWalksWhereTheHostsAiSendsItsSeatOverASlowLink(Type scenarioType, int strat, ConnectionProfile link)
        => Verify(scenarioType, strat, runs: 1, link, SlowLinkSpotTolerance);

    private static IEnumerable<TestCaseData> FirstStrats() =>
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        select new TestCaseData(scenario.GetType()).SetArgDisplayNames(scenario.FullName());

    // The stay's next run: each peer's bot starts over on that run's rolls and clock.
    [TestCaseSource(nameof(FirstStrats))]
    public void EveryBotPeerWalksWhereTheHostsAiSendsItsSeatAfterAReset(Type scenarioType) => Verify(scenarioType, 0, runs: 2);

    private static void Verify(Type scenarioType, int strat, int runs, ConnectionProfile? link = null, float spotTolerance = SpotTolerance)
    {
        var peers = Enum.GetValues<PartyRole>().Where(r => r != HostSeat).Select(r => new PlayerSeat(r)).ToList();
        var run = MultiplayerRun.Play(scenarioType, strat, 1, new PlayerSeat(HostSeat), peers, peerLink: link, runs: runs,
            configure: client => client.Fake.UserActions.Enabled = true);
        Assert.That(run.Failure, Is.Null);

        var wrong = new List<string>();
        foreach (var peer in run.Peers)
        {
            var planned = Moves(run.Host, peer.Seat);
            var walked = Moves(peer, peer.Seat);
            if (planned.Count != walked.Count)
                wrong.Add($"{peer.Seat}: the host's Ai moved it {planned.Count} times, its own peer {walked.Count}");
            for (var i = 0; i < Math.Min(planned.Count, walked.Count); i++)
                if (planned[i].At != walked[i].At || Vector2.Distance(planned[i].To, walked[i].To) > spotTolerance)
                    wrong.Add($"{peer.Seat}: the host's Ai sent it to {planned[i]}, its peer went to {walked[i]}");
        }
        foreach (var line in wrong.Take(20)) TestContext.Out.WriteLine(line);
        Assert.That(wrong, Is.Empty, "seats a peer's Ai played differently from the host's");
        Assert.That(run.Deaths, Is.Empty, () => string.Join(", ", run.Deaths.Select(d => $"{d.Role}: {d.Cause} at {d.Time:F1}s")));
    }

    private sealed record Move(string At, Vector2 To)
    {
        public override string ToString() => $"({To.X:F2},{To.Y:F2}) at {At}s";
    }

    // The scheduled time as AiManager logs it, so both clients' lines for one move name it alike.
    private static List<Move> Moves(TestClient client, PartyRole seat)
    {
        var moves = new List<Move>();
        foreach (var line in client.Log.Lines)
            if (AiMove().Match(line) is { Success: true } move && move.Groups["role"].Value == seat.ToString())
                moves.Add(new Move(move.Groups["at"].Value,
                    new Vector2(float.Parse(move.Groups["x"].Value, CultureInfo.InvariantCulture), float.Parse(move.Groups["z"].Value, CultureInfo.InvariantCulture))));
        return moves;
    }

    [GeneratedRegex(@"\[AiManager\] Move@(?<at>[\d.]+): (?<role>\w+) \([-\d.]+,[-\d.]+\) -> \([-\d.]+,[-\d.]+\) for \((?<x>-?[\d.]+),(?<z>-?[\d.]+)\)")]
    private static partial Regex AiMove();
}
