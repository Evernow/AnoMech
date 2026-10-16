using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.EnemyActions;

// The remote players' own clients, as the host reaches them.
public interface IRemoteViews
{
    // A remote player, whose client answers Request.
    bool Reports(SimCharacter member);

    // Each remote client answers for its own player: where they stand once its screen shows the
    // world as it is now, the ask arriving behind everything sent before it.
    void Request(uint id);
}

// A status as a judgment read it; a sourced one is past FindStatus's reach.
internal readonly record struct HeldStatus(ushort StatusId, ushort Stacks, bool Sourced)
{
    public static List<HeldStatus> Of(SimCharacter who)
        => who.ActiveStatuses.Select(s => new HeldStatus(s.StatusId, s.Stacks, s.SourceObject != default)).ToList();
}

// One resolve's moment: when it was, where everyone stood, and the Judge that takes its targets.
internal sealed class Moment(float at, Action<Moment, IReadOnlyList<SimCharacter>, bool> judge, Action judged)
{
    public float At { get; } = at;
    // What its effects need of every target at once; a part per member otherwise.
    public bool Shared { get; init; }

    // Where each character stood at the moment; a remote player's own report replaces theirs.
    public Dictionary<SimCharacter, Placement> Placements { get; } = new();
    public Dictionary<SimCharacter, Placement> Reported { get; } = new();
    // Each member's statuses as of the moment, moved on only by their own timeline since.
    public Dictionary<SimCharacter, List<HeldStatus>> Held { get; } = new();
    public HashSet<SimCharacter> Awaited { get; } = [];
    // The remote caster and target, where the area stands: every part waits for theirs.
    public HashSet<SimCharacter> Anchors { get; } = [];
    public int PartsLeft { get; set; }

    // `once`: the part that also runs the effects that act on no party member.
    public void Judge(IReadOnlyList<SimCharacter> members, bool once) => judge(this, members, once);

    public void Judged() => judged();
}

// Each party member is judged at their own moment of a resolve: the host's player and bots at the
// host's, a remote player when their own screen showed it, which the host learns a round trip later
// from their report. Every player stands where their own screen had them, an area cast by or on one
// of them too, so a fight played the same on each screen resolves the same at any latency. What
// judges each target alone takes the host's own targets at once and each remote player on their
// report; what needs every target together waits for all the reports. No part lands ahead of an
// earlier one that shares a member, so each member's hits land in the order of their moments. A
// report that never comes is replaced, past ReportTimeout, by where the host last had them.
//
// A member is judged on the statuses they had at the moment, plus what their own timeline added
// since: a remote player's own presses before their screen showed it, and the earlier hits judged
// on them meanwhile. What the host changed since is after the moment on everyone's screen.
internal sealed class Judgments(SimWorld world)
{
    // Beyond any trip a fight is played over: a client this late has dropped.
    private const float ReportTimeout = 3f;

    public IRemoteViews? Views { get; set; }

    // A run isn't over while a part of it still waits.
    public bool Waiting => pending.Count > 0;

    private uint lastRequest;
    private readonly List<Part> pending = [];
    private readonly Dictionary<uint, Moment> awaiting = new();
    private bool draining;
    private bool drainAgain;

    private sealed record Part(Moment Moment, IReadOnlyList<SimCharacter> Members, bool Once)
    {
        public bool Ready => !Members.Any(Moment.Awaited.Contains) && !Moment.Anchors.Any(Moment.Awaited.Contains);
    }

    // `members`: the living party. `placed`: every character whose placement the judging reads.
    public void Resolve(Moment moment, SimCharacter caster, SimCharacter? target, IReadOnlyList<SimCharacter> members, IEnumerable<SimCharacter> placed)
    {
        var reporting = Views is { } views ? members.Where(views.Reports).ToList() : [];
        if (reporting.Count == 0 && pending.Count == 0)
        {
            moment.Judge(members, true);
            moment.Judged();
            return;
        }
        var remote = reporting.Where(m => m != caster).ToList();
        foreach (var who in placed) moment.Placements[who] = who.Placement();
        foreach (var who in members) moment.Held[who] = HeldStatus.Of(who);
        if (reporting.Count > 0)
        {
            var id = ++lastRequest;
            awaiting[id] = moment;
            moment.Awaited.UnionWith(reporting);
            moment.Anchors.UnionWith(reporting.Where(m => m == caster || m == target));
            Views!.Request(id);
        }
        if (moment.Shared) Queue(new Part(moment, members, true));
        else
        {
            Queue(new Part(moment, members.Except(remote).ToList(), true));
            foreach (var who in remote) Queue(new Part(moment, [who], false));
        }
        Drain();
    }

    // `member`'s own client had them at `at` when its screen showed request `id`'s moment.
    public void Report(SimCharacter member, uint id, Placement at)
    {
        if (!awaiting.TryGetValue(id, out var moment) || !moment.Awaited.Remove(member)) return;
        moment.Reported[member] = at;
        if (moment.Awaited.Count == 0) awaiting.Remove(id);
        Drain();
    }

    // A remote player's own press, which their screen showed before any moment still waiting on
    // their report.
    public void OwnPress(SimCharacter member, Action press)
    {
        var before = HeldStatus.Of(member);
        press();
        Carry(member, before, pending.Where(p => p.Moment.Awaited.Contains(member)));
    }

    // On the run clock, so a paused run waits with it.
    public void Tick()
    {
        foreach (var (id, moment) in awaiting.ToList())
            if (world.Events.Elapsed - moment.At >= ReportTimeout) TimeOut(id, moment);
    }

    // A run that ends drops what still waits.
    public void Clear()
    {
        pending.Clear();
        awaiting.Clear();
    }

    private void Queue(Part part)
    {
        part.Moment.PartsLeft++;
        pending.Add(part);
    }

    private void TimeOut(uint id, Moment moment)
    {
        awaiting.Remove(id);
        foreach (var who in moment.Awaited)
        {
            DiagnosticLog.Warn($"[Judgments] No report for request #{id} from {(who as ISimPartyMember)?.Role} within {ReportTimeout:F0}s -- judged where the host last had them.");
            moment.Reported[who] = who.Placement();
        }
        moment.Awaited.Clear();
        Drain();
    }

    // A part judged here may cast a follow-up whose own resolve queues behind it.
    private void Drain()
    {
        if (draining)
        {
            drainAgain = true;
            return;
        }
        draining = true;
        try
        {
            do
            {
                drainAgain = false;
                for (var i = 0; i < pending.Count; i++)
                {
                    var part = pending[i];
                    if (!part.Ready || Blocked(i)) continue;
                    pending.RemoveAt(i);
                    Judge(part);
                    drainAgain = true;
                    break;
                }
            } while (drainAgain);
        }
        finally
        {
            draining = false;
        }
    }

    // What it does to its members is on their own timelines: their later moments still to be
    // judged see it.
    private void Judge(Part part)
    {
        var before = part.Members.ToDictionary(m => m, HeldStatus.Of);
        part.Moment.Judge(part.Members, part.Once);
        foreach (var member in part.Members)
            Carry(member, before[member], pending.Where(p => p.Members.Contains(member)));
        if (--part.Moment.PartsLeft == 0) part.Moment.Judged();
    }

    private bool Blocked(int index)
    {
        var members = pending[index].Members;
        for (var i = 0; i < index; i++)
            if (pending[i].Members.Any(members.Contains)) return true;
        return false;
    }

    // What changed on `member` since `before`, into what each of `later`'s moments holds of them,
    // status by status as FindStatus tells them apart.
    private static void Carry(SimCharacter member, List<HeldStatus> before, IEnumerable<Part> later)
    {
        var moments = later.Select(p => p.Moment).Distinct().Where(m => m.Held.ContainsKey(member)).ToList();
        if (moments.Count == 0) return;
        var after = HeldStatus.Of(member);
        foreach (var key in before.Concat(after).Select(s => (s.StatusId, s.Sourced)).Distinct())
        {
            var was = First(before, key);
            var now = First(after, key);
            if (was == now) continue;
            foreach (var moment in moments)
            {
                var held = moment.Held[member];
                var index = held.FindIndex(s => (s.StatusId, s.Sourced) == key);
                if (now is not { } status)
                {
                    if (index >= 0) held.RemoveAt(index);
                }
                else if (index >= 0) held[index] = status;
                else held.Add(status);
            }
        }
    }

    private static HeldStatus? First(List<HeldStatus> statuses, (ushort StatusId, bool Sourced) key)
    {
        foreach (var status in statuses)
            if ((status.StatusId, status.Sourced) == key) return status;
        return null;
    }
}
