using AnoMech.Core.Game;
using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using System;
using System.Numerics;

namespace AnoMech.Core.SimObjects;

// Drives a single simulated cast on a SimCharacter's BattleChara. Writing
// CastInfo and ticking CurrentCastTime ourselves (instead of calling
// Character::StartCast) is what lets the simulator replay arbitrary boss
// abilities; on completion SimCast fires a synthetic ActionEffectHandler.Receive
// with a server-shaped header so the release animation/VFX play. It owns the
// post-release animation lock that roots the caster.
//
// One SimCast per caster, constructed once and reused. Start() begins a cast (or
// fires instantly when the cast time resolves to <= 0). The
// owning SimEnemy reads IsCasting/Progress/ActionId for the cast-bar HUD and
// IsBusy to decide when to root a following boss. All target coordinates handled
// here are world-space — the SimEnemy adapter converts from scenario-local.
public sealed unsafe class SimCast : ISimObject
{
    private readonly SimCharacter parent;
    private readonly Coordinates coordinates;

    private bool casting;
    private float elapsed;
    private float total;
    private float omenDelay;
    private float fireDelay;
    private float fireDelayElapsed;
    private Vector3? targetLocation;   // scenario-local coords
    private GameObjectId? targetId;
    private byte animationVariation;

    private float animationLock;
    private float remainingAnimationLock;

    public bool IsCasting => parent.BattleCharaPtr != null && parent.BattleCharaPtr->CastInfo.IsCasting;

    public uint ActionId { get; private set; }
    public float Progress => total <= 0f ? 0f : Math.Clamp(elapsed / total, 0f, 1f);

    // Scenario-local ground target for the current cast, if it's ground-targeted --
    // read by MultiplayerManager's host-side sampler so a peer's replayed Cast()
    // can pass the same targetLocation instead of defaulting to the caster's own
    // position (see NativeCast's `position ?? parent.Position` fallback).
    public Vector3? TargetLocation => targetLocation;

    // The entity target for the current cast, if it's entity-targeted (e.g. UMAD P3's
    // Thunder III tankbuster, cast with targetId: target?.GameObjectId so the hit
    // lands on -- and the native hit-react animation plays on -- that specific tank).
    // A raw GameObjectId isn't portable across the network: host and peer each spawn
    // their own local party doppels, so the same role's GameObjectId differs between
    // them. MultiplayerManager's host-side sampler resolves this to a PartyRole or
    // enemy NetId instead (same approach as TetherState's A/B ends), and a peer
    // resolves that back to whichever local SimCharacter/SimEnemy actually holds that
    // role/NetId on its own side before replaying Cast(). Omitting this entirely (as
    // the original TargetLocation-only fix did) leaves the peer's replayed Cast()
    // with no entity target at all -- NativeActionEffect's NumTargets goes to 0, and
    // the animation that plays off whoever got hit never appears on a peer's screen.
    public GameObjectId? TargetId => targetId;

    // The actual cast duration this cast is running with -- whatever Start() resolved
    // castTime to, whether that was the caller's explicit override or the Lumina sheet
    // lookup. Read by MultiplayerManager's host-side sampler for the same reason as
    // TargetLocation above: a peer's replayed Cast() must pass this through explicitly
    // instead of re-deriving it. Re-deriving isn't just "possibly a different number" --
    // many of these scenario-scripted casts run on synthetic helper-enemy action IDs
    // that either aren't in the real Action sheet at all (Start()'s sheet lookup then
    // fails outright and the peer never casts anything) or whose sheet Cast100ms simply
    // doesn't match the duration the scenario actually scripted, so the peer's telegraph
    // runs on borrowed timing that has nothing to do with when the host's damage
    // actually resolves.
    public float Total => total;

    // The omenDelay this cast started with -- read by MultiplayerManager's host-side
    // sampler for the same reason as TargetLocation/Total above. Left at its 0f default
    // (peer never passes one through), a cast like UMAD P3's Damning Edict -- which is
    // scripted with omenDelay: 4.1f so its ground telegraph only appears for the final
    // ~0.9s of its 5s cast -- would instead show that telegraph for the whole 5s on a
    // peer's screen, since the native ActorCastPacket controls exactly when the omen
    // fades in and nothing else about the cast conveys that.
    public float OmenDelay => omenDelay;

    // Instant casts (castTimeValue <= 0, see Start() below) fire and reset ActionId/
    // TargetLocation back to their empty state within the same tick they ran in --
    // CastInfo.IsCasting (what IsCasting reads) never goes true for them either, since
    // NativeCast is skipped entirely for instants. That makes them structurally
    // invisible to a level-sampled snapshot: MultiplayerManager's peer replay is a
    // rising-edge check on IsCasting, and there is no edge to catch, and even if there
    // were, ActionId/TargetLocation would already be cleared by the time the next
    // snapshot samples them. LastInstantCast* mirrors AnimationTimelineId/LastLockonVfxId's
    // own answer to this same shape of problem: a monotonically-incrementing counter the
    // peer can edge-trigger on instead of a level value, paired with a snapshot of what
    // that particular instant cast actually was (taken here, before ResetCastState wipes
    // it) so the peer has something to replay. Confirmed via AnoMech-DamageDebug dumps:
    // Nothingness (an instant cast) never appears anywhere in a peer's own diagnostic
    // log, not even with wrong timing -- it just never ran there at all.
    public int LastInstantCastSeq { get; private set; }
    public uint LastInstantCastActionId { get; private set; }
    public Vector3? LastInstantCastTargetLocation { get; private set; }
    public GameObjectId? LastInstantCastTargetId { get; private set; }

    // True while the cast bar is up or the release animation is still playing. A
    // following boss roots itself while busy so the action animation finishes in
    // place instead of sliding.
    public bool IsBusy => IsCasting || remainingAnimationLock > 0f;

    // SimCast is a persistent subsystem of its caster: the owning SimEnemy holds it
    // as a direct field and ticks/despawns it explicitly, never reaping it by
    // liveness. Always active while it exists.
    public bool IsActive => true;

    internal SimCast(SimCharacter parent, Coordinates coordinates)
    {
        this.parent = parent;
        this.coordinates = coordinates;
    }

    // Begins a cast of `actionId`. When castSeconds resolves to <= 0 (passed
    // explicitly or read as Cast100ms=0 from the sheet) the action fires
    // immediately with no cast bar. localTargetLocation (scenario-local, converted
    // to world only where native fields demand it) drives the AOE landing point and
    // the pre-fire facing snap; targetId, if set, makes the packet carry NumTargets=1
    // (some actions only animate on the caster when an entity target is delivered).
    // omenRotate is an offset added to the caster's facing (0 = aligned with parent.Rotation).
    public bool Start(uint actionId, Vector3? localTargetLocation, float? castTime, GameObjectId? targetId, float omenDelay, float omenRotate, byte animationVariation, float animationLock, float? fireDelay = null)
    {
        var chara = parent.BattleCharaPtr;
        if (chara == null) return false;


        if (castTime == null)
        {
            var actionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();

            if (!actionSheet.TryGetRow(actionId, out var action))
            {
                Plugin.Log.Warning($"[SimCast.Start] Action Row {actionId} not found");
                return false;
            }

            castTime = action.Cast100ms / 10f;
        }

        this.animationLock = animationLock;
        var castTimeValue = castTime.Value;


        // Instant actions (castTimeValue <= 0): retail sends only the ActionEffect, never a
        // StartCasting packet (verified against the Dancing Mad replay — 0 cast packets for the
        // auto-attack 0xC252). Dispatching a cast-begin (HandleActorCastPacket) on the same frame
        // as the release clobbers the action's body animation — invisible on VFX/cast abilities,
        // but it's the whole show for a VFX-less auto-attack, so the boss never swings. Skip the
        // cast packet entirely for instants and fire the effect directly below.
        if (castTimeValue > 0)
        {
            var target = targetId ?? chara->GetGameObjectId();
            NativeCast(actionId, ActionType.Action, omenDelay, castTimeValue, false, parent.Rotation + omenRotate, localTargetLocation, target);
            total = chara->CastInfo.TotalCastTime;
        }
        else
        {
            total = 0f;
        }

        elapsed = 0f;

        casting = true;
        targetLocation = localTargetLocation;
        this.targetId = targetId;
        this.animationVariation = animationVariation;
        ActionId = actionId;
        this.omenDelay = omenDelay;
        this.fireDelay = fireDelay ?? 0;

        if (castTimeValue <= 0)
        {
            FaceTarget(chara);
            FireActionEffect(chara, actionId, ActionType.Action, animationLock, targetLocation, targetId, animationVariation);
            LastInstantCastActionId = actionId;
            LastInstantCastTargetLocation = targetLocation;
            LastInstantCastTargetId = targetId;
            LastInstantCastSeq++;
            ResetCastState();
        }

        return true;
    }

    public void NativeCast(uint actionId, ActionType actionType, float omenDelay, float castTime, bool interruptible, float? rotation = null, Vector3? position = null, GameObjectId? targetId = null, GameObjectId? ballistaId = null)
    {
        var omenDelayByte = (byte)(omenDelay * 10);

        var rot = rotation ?? parent.Rotation;
        var qRotation = MathUtil.QuantizeRotation(rot);

        var animationTargetId = targetId == null ? 0xE0000000 : targetId.Value.ObjectId;
        var ballistaTargetId = ballistaId == null ? 0xE0000000 : ballistaId.Value.ObjectId;

        var localPos = position ?? parent.Position;
        var globalPos = coordinates.ToGlobal(localPos);

        var qPosX = MathUtil.QuantizePosition(globalPos.X);
        var qPosY = MathUtil.QuantizePosition(globalPos.Y);
        var qPosZ = MathUtil.QuantizePosition(globalPos.Z);

        var actorCastPacket = new ActorCastPacket
        {
            ActionId = (ushort)actionId,
            ActionType = (byte)actionType,
            OmenDelay = omenDelayByte,
            ActionId_2 = actionId,
            CastTime = castTime,
            TargetEntityId = animationTargetId,
            RotationInt = qRotation,
            Interruptible = interruptible,
            BallistaEntityId = ballistaTargetId,
            PositionX = qPosX,
            PositionY = qPosY,
            PositionZ = qPosZ,
        };

        PacketDispatcherPointers.HandleActorCastPacket(parent.GameObjectId.ObjectId, &actorCastPacket);
    }

    public void NativeActionEffect(uint actionId, float animationLock, ushort spellId, byte animationVariaton, ActionType actionType, byte flags, float? rotation = null, Vector3? position = null, GameObjectId? animationTargetId = null, GameObjectId? actionTargetId = null, GameObjectId? ballistaId = null)
    {
        const uint NullObjectId = 0xE0000000;

        var chara = parent.BattleCharaPtr;

        if (chara == null)
        {
            return;
        }

        var nullActionTarget = actionTargetId == null;

        var animationTarget = animationTargetId == null ? new GameObjectId { ObjectId = NullObjectId, Type = 0 } : animationTargetId!.Value;
        var actionTarget = nullActionTarget ? new GameObjectId { ObjectId = NullObjectId, Type = 0 } : actionTargetId!.Value;
        var ballistaTarget = ballistaId == null ? NullObjectId : ballistaId.Value.ObjectId;

        var rot = rotation ?? parent.Rotation;
        var qRotation = MathUtil.QuantizeRotation(rot);

        var localPos = position ?? Vector3.Zero;
        var globalPos = coordinates.ToGlobal(localPos);

        var header = new ActionEffectHandler.Header
        {
            AnimationTargetId = animationTarget,
            ActionId = actionId,
            GlobalSequence = 0,
            AnimationLock = animationLock,
            BallistaEntityId = ballistaTarget,
            SourceSequence = 0,
            RotationInt = qRotation,
            SpellId = spellId,
            AnimationVariation = animationVariaton,
            ActionType = (byte)actionType,
            Flags = flags,
            NumTargets = (byte)(nullActionTarget ? 0 : 1)
        };

        var targetEffects = new ActionEffectHandler.TargetEffects();

        ActionEffectHandler.Receive(
            parent.GameObjectId.ObjectId,
            (Character*)chara,
            &globalPos,
            &header,
            &targetEffects,
            &actionTarget
            );

        remainingAnimationLock = animationLock;
    }

    public void Tick(float deltaSeconds)
    {
        if (remainingAnimationLock > 0f)
        {
            remainingAnimationLock = MathF.Max(0f, remainingAnimationLock - deltaSeconds);
        }

        if (!casting)
        {
            return;
        }

        var chara = parent.BattleCharaPtr;
        if (chara == null)
        {
            casting = false;
            return;
        }

        var castInfo = chara->CastInfo;
        elapsed = castInfo.CurrentCastTime;

        if (elapsed >= total)
        {
            var fire = true;

            if (fireDelay > 0)
            {
                fireDelayElapsed += deltaSeconds;

                if (fireDelayElapsed < fireDelay)
                {
                    fire = false;
                }
            }

            if (fire)
            {
                FaceTarget(chara);
                FireActionEffect(chara, ActionId, ActionType.Action, animationLock, targetLocation, targetId, animationVariation);
                ResetCastState();
            }
        }
    }

    // Teardown for caster despawn: drop the telegraph, stop any pending delayed
    // spawn, and clear CastInfo. Clearing CastInfo matters because despawn deletes
    // the BattleChara via DeleteObjectByIndex -> Character::Terminate, whose
    // scheduler teardown reads a still-live cast/action timeline and crashes on
    // freed state (C0000005 at TimelineGroup.PlayAction; see crash dump
    // 20260529_193455).
    public void Despawn()
    {
        var chara = parent.BattleCharaPtr;
        if (chara != null)
        {
            chara->CastInfo.IsCasting = false;
            chara->CastInfo.ActionId = 0;
            chara->CastInfo.ActionType = 0;
        }
        casting = false;
    }

    private void ResetCastState()
    {
        casting = false;
        targetLocation = null;
        targetId = null;
        animationVariation = 0;
        ActionId = 0;
        fireDelay = 0;
        fireDelayElapsed = 0;
    }

    // targetLocation is stored scenario-local; lift to world only for native
    // ActionEffect delivery (Receive / CastInfo expect world coords).
    private Vector3? WorldTargetLocation => targetLocation is { } loc ? coordinates.ToGlobal(loc) : null;

    // Targeted casts (ground location now; entity targets later) snap to face the target
    // on the final tick so the release animation plays in the intended direction even if
    // the target moved during the cast. FireActionEffect snapshots Rotation into the
    // packet header, so this must run first.
    private void FaceTarget(BattleChara* chara)
    {
        if (WorldTargetLocation is not { } loc) return;
        var dx = loc.X - chara->Position.X;
        var dz = loc.Z - chara->Position.Z;
        if (dx * dx + dz * dz < 1e-6f) return;
        chara->Rotation = MathUtil.NormalizeRotation(MathF.Atan2(dx, dz));
    }

    // Mimics the server's ActionEffect packet so the game plays the action's release
    // animation/VFX on the caster. When deliverTo is set, the packet carries
    // NumTargets=1 with that GameObjectId and a zeroed no-op effect block; some
    // actions only animate on the caster if the engine sees at least one target to
    // deliver to. When deliverTo is null, NumTargets=0 (used for self-targeted
    // casts and cast releases without an entity target) — the release animation
    // still plays.
    private void FireActionEffect(BattleChara* chara, uint actionId, ActionType actionType, float animationLock, Vector3? localTargetLocation = null, GameObjectId? deliverTo = null, byte animationVariation = 0)
    {
        if (deliverTo is { } id)
        {
            var characterManager = CharacterManager.Instance();
            var deliverToId = id.ObjectId;

            if (characterManager == null || characterManager->LookupBattleCharaByEntityId(deliverToId) == null)
            {
                Plugin.Log.Warning(
                    $"FireActionEffect: target {deliverToId:X} for action {actionId:X} on caster {chara->EntityId:X} not in CharacterManager._battleCharas; dropping deliverTo to avoid ApplyAll null-deref");
                deliverTo = null;
            }
        }

        var pos = localTargetLocation ?? parent.Position;
        NativeActionEffect(actionId, animationLock, (ushort)actionId, animationVariation, actionType, 0, chara->Rotation, pos, deliverTo, deliverTo);
    }
}
