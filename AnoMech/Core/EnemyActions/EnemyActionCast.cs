using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.EnemyActions;

// One SimEnemy.Cast(EnemyAction, ...), for a later timeline event to read what it did. Filled at
// resolve; an action with no effects never resolves.
public sealed class EnemyActionCast
{
    public bool IsResolved { get; private set; }
    public bool IsCancelled { get; private set; }
    public Placement Origin { get; private set; }

    // At = where Who stood at resolve. Killed is decided at resolve; they die TimingSpec.DamageDelay later.
    public sealed record Hit(SimCharacter Who, Vector3 At, bool Killed);

    // Nearest to Origin first. A remote player joins once judged, a round trip after the host's
    // own targets (Judgments).
    public IReadOnlyList<Hit> Hits { get; private set; } = [];

    // Every target judged.
    public bool IsJudged { get; private set; }

    private List<Action<EnemyActionCast>>? whenJudged;

    // `then` once every target is judged; at once if they are.
    public void WhenJudged(Action<EnemyActionCast> then)
    {
        if (IsJudged) then(this);
        else (whenJudged ??= []).Add(then);
    }

    internal void Cancelled() => IsCancelled = true;

    internal void Resolve(Placement origin, IReadOnlyList<Hit> hits)
    {
        IsResolved = true;
        Origin = origin;
        Hits = Hits.Count == 0 ? hits : Hits.Concat(hits).OrderBy(h => Vector2.DistanceSquared(new(h.At.X, h.At.Z), new(origin.Position.X, origin.Position.Z))).ToList();
    }

    internal void Judged()
    {
        IsJudged = true;
        var then = whenJudged;
        whenJudged = null;
        foreach (var run in then ?? []) run(this);
    }
}
