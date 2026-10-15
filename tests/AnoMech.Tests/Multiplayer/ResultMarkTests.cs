using System.Globalization;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Scenarios;

namespace AnoMech.Tests;

// The run's success and failure marks on every player, as every client shows them: each client
// plays its own player's on its own player and the other players' on their seats, in every
// multiplayer scenario. A run the bots clear gives every player one success mark; a run whose
// players never move, under god mode, gives every player a failure mark each time the host's own
// player gets one.
public class ResultMarkTests
{
    private static readonly PlayerSeat[] Seats = [new(PartyRole.MeleeDpsA), new(PartyRole.MainTank), new(PartyRole.CasterDps)];

    private static IEnumerable<TestCaseData> Scenarios() =>
        from scenario in ScenarioCatalog.Create().Scenarios.Where(s => s.SupportsMultiplayer)
        select new TestCaseData(scenario.GetType()).SetArgDisplayNames(scenario.FullName());

    [TestCaseSource(nameof(Scenarios))]
    public void EveryPlayerSeesEveryPlayersSuccessMark(Type scenarioType)
    {
        var run = Play(scenarioType, bots: true);
        Assert.That(run.HostOutcome, Is.EqualTo(SessionStatus.Succeeded));
        Verify(run, ScenarioSession.MechanicSuccessActionId, 1);
        Verify(run, ScenarioSession.MechanicFailureActionId, 0);
    }

    [TestCaseSource(nameof(Scenarios))]
    public void EveryPlayerSeesEveryPlayersFailureMarks(Type scenarioType)
    {
        var run = Play(scenarioType, bots: false);
        var mistakes = Marks(run.Host, run.Host.Seat, ScenarioSession.MechanicFailureActionId);
        Assume.That(mistakes, Is.Positive, "nobody standing still made a mistake");
        Verify(run, ScenarioSession.MechanicFailureActionId, mistakes);
        Verify(run, ScenarioSession.MechanicSuccessActionId, 0);
    }

    private static MultiplayerRun Play(Type scenarioType, bool bots)
    {
        var seats = Seats.Select(s => s with { BotControlled = bots }).ToArray();
        var run = MultiplayerRun.Play(scenarioType, 0, 1, seats[0], seats[1..], immortal: true,
            configure: client => client.Config.EnableMechanicResultMarks = true);
        Assert.That(run.Failure, Is.Null);
        return run;
    }

    private static void Verify(MultiplayerRun run, uint mark, int perPlayer)
    {
        var wrong = new List<string>();
        foreach (var client in run.Peers.Prepend(run.Host))
        foreach (var seat in Seats.Select(s => s.Role))
            if (Marks(client, seat, mark) is var count && count != perPlayer)
                wrong.Add($"{client.Name} showed {count} of mark {mark} on {seat}, not {perPlayer}");
        Assert.That(wrong, Is.Empty);
    }

    private static int Marks(TestClient client, PartyRole seat, uint mark)
    {
        var id = mark.ToString(CultureInfo.InvariantCulture);
        var count = 0;
        foreach (var row in client.Fake.Timeline.Rows)
        {
            if (row["kind"] != "ability" || row["id"] != id || row.GetValueOrDefault("actor") is not { Length: > 0 } actor) continue;
            var at = double.Parse(row["t"], CultureInfo.InvariantCulture);
            if (client.NameAt(uint.Parse(actor, NumberStyles.HexNumber), at) == seat.ToString()) count++;
        }
        return count;
    }
}
