using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.EnemyActions;

// Runs EnemyActions for one caster, an enemy or a party member: the visuals go out as its
// SimCast's packets, everything mechanical goes on the scenario's EventScheduler.
internal sealed class EnemyActionHandler(SimCharacter caster, SimCast cast, SimWorld world)
{
    private string CasterName => caster switch
    {
        SimEnemy enemy => enemy.DisplayName,
        ISimPartyMember member => member.Role.ToString(),
        _ => caster.GetType().Name,
    };

    private EnemyActionCast? casting;
    private EnemyActionCast? releasing;

    // From a cast's start until its ActionEffect: the bar ends ActionEffectOffset before it, and
    // the caster stays put through that gap.
    public bool AwaitingRelease => releasing is not null;

    public EnemyActionCast Start(EnemyAction action, CastTarget at, byte animationVariation)
    {
        var handle = new EnemyActionCast();
        if (caster.Dead)
        {
            handle.Cancelled();
            return handle;
        }
        var id = action.ActionId;
        var target = at.Character;
        var location = at.Location;
        var sheetRow = Natives.Data.Action(id);
        var sheetCastTime = sheetRow?.CastSeconds ?? 0f;
        var castTime = action.Cast.CastSecondsOverride ?? MathF.Max(0f, sheetCastTime - CastSpec.ActionEffectOffset);
        // Only a ground-target action goes out without a target; any other action aimed at a point
        // still targets its caster, or the client never applies the effect (no flytext until despawn).
        GameObjectId? castTarget = location is not null && sheetRow?.TargetArea == true ? null : (target ?? caster).GameObjectId;
        DiagnosticLog.Info(
            $"[EnemyAction] Cast: {Natives.Data.ActionName(id)} ({id}) by {CasterName} from ({caster.Position.X:F1},{caster.Position.Z:F1}) castSeconds={castTime:F2}.");

        if (castTime > 0f)
        {
            casting = handle;
            releasing = handle;
            cast.NativeCast(id, ActionType.Action, action.Cast.OmenDelay, castTime, interruptible: false,
                rotation: CastRotation(action, target, location), position: location, targetId: castTarget);
        }

        // Resolve goes first: an instant action runs both inline, and the ActionEffect carries the hits.
        var delivery = new Delivery();
        var resolveAt = castTime + action.Timing.ResolveSnapshotOffset;
        var releaseAt = action.Cast.CastSecondsOverride is { } bar ? bar + CastSpec.ActionEffectOffset : sheetCastTime;
        if (action.Effects.Count > 0)
            Schedule(resolveAt, () =>
            {
                if (handle.IsCancelled) return;
                if (casting == handle) casting = null;
                Resolve(action, target, location, handle, delivery, MathF.Max(0f, releaseAt - resolveAt));
            });
        Schedule(releaseAt, () =>
        {
            if (releasing == handle) releasing = null;
            if (handle.IsCancelled) return;
            if (casting == handle) casting = null;
            Release(action, target, location, castTarget, animationVariation, delivery);
        });
        return handle;
    }

    public void CancelCast(CastCancelReason reason)
    {
        if (casting is not { } handle) return;
        casting = null;
        if (releasing == handle) releasing = null;
        handle.Cancelled();
        cast.Cancel(reason);
    }

    // The server aims a cast on another character at it, whichever way the caster faces.
    private float CastRotation(EnemyAction action, SimCharacter? target, Vector3? location)
    {
        if (location is null && target is not null && target != caster) return caster.Placement().Face(target.Position).Rotation;
        return caster.Rotation + action.Cast.Rotation;
    }

    // Faces a target first: the packet carries the caster's rotation. A ground location doesn't turn
    // the caster, since the game aims some lines from behind it, and an action with Cast.FaceTarget
    // off keeps its facing for any target. Some actions animate only when delivered to a target, but one outside
    // CharacterManager null-derefs ApplyAll.
    private void Release(EnemyAction action, SimCharacter? target, Vector3? location, GameObjectId? castTarget, byte animationVariation, Delivery delivery)
    {
        var id = action.ActionId;
        if (target == caster) target = null;
        var aim = location ?? target?.Position;
        // The client plays the release along the caster's model facing, not the packet's rotation, and
        // a turn reaches the model only on the next flush, so an unseen helper snaps to its target.
        if (action.Cast.FaceTarget && target is not null)
        {
            if (caster is SimEnemy { SpawnConfig.Visibility: SpawnVisibility.InvisibleHelper } helper)
                helper.SetRotation(caster.Placement().Face(target.Position).Rotation);
            else
                caster.Face(target.Position);
        }
        var outcomes = delivery.Outcomes.Where(o => o.Damage is not null || o.Push is not null || o.Dodged || o.StatusGains.Count > 0).ToList();
        var listed = new List<SimCharacter>();
        if (target is not null) listed.Add(target);
        listed.AddRange(outcomes.Select(o => o.Target));
        var targets = new List<ActionEffectTarget>();
        var confirmations = new List<(SimCharacter Target, byte Index)>();
        foreach (var who in listed.Distinct())
        {
            if (!Natives.BattleCharas.IsInCharacterManager(who.GameObjectId.ObjectId)) continue;
            var outcome = outcomes.FirstOrDefault(o => o.Target == who);
            var effects = new List<ActionEffectEntry>();
            if (outcome?.Damage is { } damage)
            {
                effects.Add(ActionEffectEntry.Damage(damage.Amount, damage.Kind.AttackType()));
                confirmations.Add((who, (byte)targets.Count));
            }
            // A push that deals nothing still gets a typeless zero hit, and no EffectResult; an
            // NPC's gets only its Knockback entry.
            else if (outcome?.Push is not null && who is ISimPartyMember)
                effects.Add(ActionEffectEntry.Damage(0, 0));
            else if (outcome?.Dodged == true)
                effects.Add(ActionEffectEntry.Dodge());
            foreach (var (statusId, entryParam) in outcome?.StatusGains.Take(MaxEffectsPerTarget - 3) ?? [])
                effects.Add(ActionEffectEntry.StatusGain(statusId, entryParam));
            if (outcome?.Push?.KnockbackRow is { } row)
                effects.Add(ActionEffectEntry.Knockback(row));
            effects.Add(ActionEffectEntry.ActionMarker(id));
            targets.Add(new ActionEffectTarget(who.GameObjectId, effects));
        }
        delivery.Sequence = cast.NativeActionEffect(
            id, action.Cast.AnimationLock, (ushort)id, animationVariation, ActionType.Action, 0,
            rotation: caster.Rotation + action.Cast.Rotation, position: aim ?? caster.Position, animationTargetId: castTarget, targets: targets);
        delivery.Released = true;
        delivery.Confirmations = confirmations;
        if (delivery.ImpactDue) ConfirmDamage(delivery);
    }

    // Who it hits is judged by Judgments, each party member at their own moment of it.
    private void Resolve(EnemyAction action, SimCharacter? target, Vector3? location, EnemyActionCast handle, Delivery delivery, float releaseDelay)
    {
        var moment = new Moment(world.Events.Elapsed,
            (m, members, once) => Judge(m, action, target, location, members, once, handle, delivery, releaseDelay), handle.Judged)
        {
            Shared = action.Area.ExcludeCaster || action.Area.AdjustTargets is not null || action.Effects.Any(e => e.Scope == EffectScope.AllTargets),
        };
        var members = world.Party.ActiveMembers().ToList();
        world.Judgments.Resolve(moment, caster, target, members, members.Append(caster).Concat(target is null ? [] : [target]).Distinct().ToList());
    }

    private Placement Origin(EnemyAction action, SimCharacter? target, Vector3? location)
        => target is not null && target != caster && IsDirectional(action)
            ? caster.Placement().Face(target.Position)
            : target?.Placement()
              ?? (location is { } at ? new Placement(at, caster.Rotation) : caster.Placement());

    // Who of `members` the resolve hits, and what it does to them. Off its moment, everyone stands
    // where it had them, a remote player where their own screen did, the area on its caster and
    // target among them, and is judged on the statuses they had then.
    private void Judge(Moment moment, EnemyAction action, SimCharacter? target, Vector3? location, IReadOnlyList<SimCharacter> members, bool once,
        EnemyActionCast handle, Delivery delivery, float releaseDelay)
    {
        var now = world.Events.Elapsed;
        var held = members.Where(moment.Held.ContainsKey).ToDictionary(m => m, m => moment.Held[m]);
        foreach (var (who, at) in moment.Placements) who.JudgeAt(at);
        foreach (var (who, at) in moment.Reported) who.JudgeAt(at);
        EnemyActionContext ctx;
        Placement origin;
        IReadOnlyList<SimCharacter> hits;
        List<Vector3> positions;
        try
        {
            origin = Origin(action, target, location);
            var casterHitbox = origin.Position == caster.Position ? caster.HitboxRadius : 0f;
            ctx = new EnemyActionContext(action, caster, target, origin, world.Party, releaseDelay)
            {
                At = moment.At,
                Held = held,
                AfterActionEffect = delivery.Released,
                Late = now - moment.At,
            };
            // A party member's own action never hits them.
            var area = IsSingleTarget(action) ? (AoeQuery?)null : AreaOf(action, origin, casterHitbox);
            hits = area is { } query
                ? query.Run(new CharacterFind<SimCharacter>(members.ToList())).Where(h => h != caster).ToList()
                : members.Where(m => ReferenceEquals(m, target)).ToList();
            if (action.Area.ExcludeCaster
                && hits.MinBy(h => DistanceXZ(h.Position, caster.Position)) is { } baiter
                && DistanceXZ(baiter.Position, caster.Position) <= ExcludeCasterThreshold)
                hits = hits.Where(h => h != baiter).ToList();
            if (action.Area.AdjustTargets is { } adjust) hits = adjust(ctx, hits);
            ctx.Hits = hits;
            if (once && !IsSingleTarget(action))
                ctx.NpcHits = NpcAreaHits(action, origin, casterHitbox);
            positions = hits.Select(h => h.Position).ToList();
            if (now <= moment.At || hits.Count > 0)
                DiagnosticLog.Info(
                    $"[EnemyAction] Resolve: {Natives.Data.ActionName(action.ActionId)} at ({origin.Position.X:F1},{origin.Position.Z:F1}) rot={origin.Rotation:F3} -- {hits.Count} target(s): "
                    + string.Join(", ", hits.Select(t => (t as ISimPartyMember)?.Role.ToString() ?? "?"))
                    + (now > moment.At ? $" (judged {now - moment.At:F2}s on)" : ""));

            Judged?.Invoke(new JudgedPart(action.ActionId, moment.At, members, hits, area,
                action.Area.ExcludeCaster || action.Area.AdjustTargets is not null, caster is ISimPartyMember || target is ISimPartyMember, moment.Shared));

            foreach (var effect in action.Effects)
                if (once || effect.Scope != EffectScope.Once) effect.Apply(ctx, hits);
        }
        finally
        {
            foreach (var who in moment.Placements.Keys) who.JudgeAt(null);
            foreach (var who in moment.Reported.Keys) who.JudgeAt(null);
        }
        handle.Resolve(origin, hits.Select((h, i) => new EnemyActionCast.Hit(h, positions[i], ctx.IsKilled(h))).ToList());
        Deliver(moment, action, ctx, delivery);
    }

    // Timed from the moment, so a part judged late lands when it would have: what's already due
    // goes now. What's judged once the ActionEffect is out had no entries in it, so no flytext, and
    // a knockback its client would have slid by goes as the push the server sends instead.
    private void Deliver(Moment moment, EnemyAction action, EnemyActionContext ctx, Delivery delivery)
    {
        var late = delivery.Released;
        if (!late) delivery.Outcomes.AddRange(ctx.Outcomes);
        // Ahead of the deaths it shares a time with, so the killing hit lands on a living target.
        if (!late && !delivery.ConfirmScheduled && ctx.Outcomes.Any(o => o.Damage is not null))
        {
            delivery.ConfirmScheduled = true;
            At(moment, action.Timing.DamageDelay, () => ConfirmDamage(delivery));
        }
        foreach (var (status, revealAt) in ctx.HiddenStatuses)
            At(moment, revealAt, status.Reveal);
        foreach (var (who, statusId) in ctx.ImpactRemovals)
            At(moment, action.Timing.DamageDelay, () =>
            {
                if (who.IsAlive()) who.RemoveStatus(statusId);
            });
        foreach (var outcome in ctx.Outcomes)
        {
            var who = outcome.Target;
            if (outcome.Push is { } push)
            {
                // A player's client slides itself by the row; an NPC goes as far as the server lets it.
                At(moment, push.Delay ?? action.Timing.DamageDelay, () =>
                {
                    if (push.KnockbackRow is { } row && who is ISimPartyMember) who.Knockback(push.From, row, inActionEffect: !late);
                    else if (push.Speed > 0f)
                    {
                        var distance = who is ISimPartyMember ? push.Distance : world.NpcPushDistance(who.Position, push.From, push.Distance);
                        who.Push(push.From, distance, distance / push.Speed);
                    }
                });
            }
            if (outcome.Killed)
                At(moment, action.Timing.DamageDelay, () => who.Die(action.ActionId, Explain(action.DeathExplanation, outcome.Explanation)));
        }
        foreach (var run in ctx.AfterResolveActions)
            run();
    }

    private const float ExcludeCasterThreshold = 0.01f;

    // The ActionEffect's limit; damage, knockback and the marker take three.
    private const int MaxEffectsPerTarget = 8;

    private static AoeQuery AreaOf(EnemyAction action, Placement origin, float casterHitboxRadius)
    {
        var query = new AoeQuery(action.ActionId, origin, action.Cast.Rotation + action.Area.Rotation, action.Area.Size, action.Area.CastTypeOverride, casterHitboxRadius);
#if DEBUG
        AnoMech.Windows.DamageDebugWindow.Instance?.Record(query);
#endif
        return query;
    }

    // Every part a resolve judges, for the headless test harness. Area: null for a single target.
    // Filtered: some of those the area caught are left out. OnPlayer: cast by or at a party member.
    // Shared: what it does turns on who else it caught.
    internal sealed record JudgedPart(uint ActionId, float At, IReadOnlyList<SimCharacter> Members, IReadOnlyList<SimCharacter> Hits, AoeQuery? Area,
        bool Filtered, bool OnPlayer, bool Shared);

    internal static event Action<JudgedPart>? Judged;

    private IReadOnlyList<SimEnemy> NpcAreaHits(EnemyAction action, Placement origin, float casterHitboxRadius)
    {
        var query = new AoeQuery(action.ActionId, origin, action.Cast.Rotation + action.Area.Rotation, action.Area.Size, action.Area.CastTypeOverride, casterHitboxRadius);
        return query.Run(new CharacterFind<SimEnemy>(() => world.Children.OfType<SimEnemy>().Where(e => e != caster && e.IsAlive())));
    }

    private static float DistanceXZ(Vector3 a, Vector3 b)
        => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // No area, only the cast target is hit.
    private static bool IsSingleTarget(EnemyAction action)
        => (action.Area.CastTypeOverride ?? Natives.Data.Action(action.ActionId)?.CastType) is CastType.SingleTarget;

    // Cones and lines.
    private static bool IsDirectional(EnemyAction action)
        => (action.Area.CastTypeOverride ?? Natives.Data.Action(action.ActionId)?.CastType)
            is CastType.Cone2 or CastType.Cone or CastType.Rectangle2 or CastType.Rectangle or CastType.Charge;

    private static string? Explain(string? action, string? hit)
        => action is null ? hit : hit is null ? action : $"{action}; {hit}";

    // The EffectResult that makes the client apply a target's damage entry, flytext included. An
    // impact due before the ActionEffect went out waits for it.
    private static void ConfirmDamage(Delivery delivery)
    {
        delivery.ImpactDue = delivery.Sequence == 0;
        if (delivery.ImpactDue) return;
        foreach (var (who, index) in delivery.Confirmations)
            if (who.Proxy is { Exists: true } chara)
                chara.ReceiveEffectResult(delivery.Sequence, index, chara.Health);
    }

    // Resolve's outcomes, for the ActionEffect; the ActionEffect's ids, for the EffectResults.
    private sealed class Delivery
    {
        public readonly List<EnemyActionContext.TargetOutcome> Outcomes = [];
        public uint Sequence;
        public IReadOnlyList<(SimCharacter Target, byte Index)> Confirmations = [];
        public bool ImpactDue;
        public bool Released;
        public bool ConfirmScheduled;
    }

    // Anything due now runs inline: an instant action resolves inside the Cast() call, so casts
    // issued in one timeline event see each other's statuses in call order.
    private void Schedule(float delay, Action run)
    {
        if (delay <= 0f) run();
        else world.Events.Add(delay, run);
    }

    // `delay` past the moment, inline as Schedule once due.
    private void At(Moment moment, float delay, Action run)
    {
        var time = moment.At + MathF.Max(0f, delay);
        if (time <= world.Events.Elapsed) run();
        else world.Events.At(time, run);
    }
}
