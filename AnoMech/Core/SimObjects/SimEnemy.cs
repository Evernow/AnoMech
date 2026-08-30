using AnoMech.Core.Game;
using AnoMech.Helpers;
using AnoMech.Pointers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace AnoMech.Core.SimObjects;

// Placement.Position is scenario-local (offset from SimWorld.ScenarioOrigin), same
// coordinate space as the rest of the SimXxx API: +X = east, +Z = south.
// Placement.Rotation is absolute radians: 0 = south, π/2 = east, π = north, -π/2 = west.
// ModelCharaId (non-zero) overrides the BNpcBase visual, e.g. a no-shield variant.
// Hitbox radius = BNpcBase.Scale × ModelChara's unscaled radius, unless HitboxRadius
// (non-zero) overrides it — decoupling the clickable/targetable hitbox from Scale.

// Whether a SimEnemy shows in the _EnemyList HUD (read each frame by EnmityHud.Refresh).
// Always          — listed while alive.
// OnlyWhenVisible — follows the engine's DrawObject.IsVisible; for adds that warp
//                   in/out. Don't combine with SetModelState (its rebuild briefly
//                   DisableDraws and flaps the list); transforming bosses use Always.
// Never           — never listed (AOE-source dummies, tether endpoints).
// Manual          — scenario drives it via SetInEnemyList(bool); default false.
public enum EnemyListMode
{
    Always,
    OnlyWhenVisible,
    Never,
    Manual,
}

public record struct EnemySpawnConfig(
    uint BNpcBaseId,
    uint NameId = 0,
    byte Level = 0,
    bool Targetable = false,
    EnemyListMode EnemyList = EnemyListMode.Always,
    bool IsVisible = true,
    Placement Placement = default,
    uint ModelCharaId = 0,
    float Scale = 0f,    // 0 = use BNpcBase.Scale
    float HitboxRadius = 0f,    // 0 = ModelChara unscaled radius × Scale
    byte? InitialModeAttributeFlags = null); // null = leave at engine default (0x00); set when the boss's canonical idle sub-mesh variant differs (e.g. Omega-M = 0x10)

public sealed unsafe class SimEnemy : SimNpc
{
    // Cast bar, action-effect release, omen telegraph, and animation lock live in
    // SimCast. SimEnemy just converts target coords to world space and reads IsBusy.
    private readonly SimCast cast;

    // Peer-only smoothing for positions received via ApplyNetworkPosition (mirrors
    // SimNetworkPuppet's CatchUpSpeed/SnapThreshold -- same reasoning, same values).
    // Distances beyond NetworkSnapThreshold (a scripted teleport/repositioning, a
    // lag spike) skip interpolation and snap immediately rather than gliding across
    // the arena. No effect on host-driven enemies: nothing calls
    // ApplyNetworkPosition there.
    // Raised from 12f (~sprint speed): that value was tuned to "just barely don't
    // fall behind," which is the wrong bias for a tank reading boss position in
    // real time -- it's supposed to be a smoothing floor over per-snapshot jitter,
    // not a second source of lag on top of MultiplayerManager's own snapshot
    // interval. 20f keeps real headroom above any realistic host-side movement
    // speed so the catch-up step essentially never becomes the bottleneck itself.
    private const float NetworkCatchUpSpeed = 20f;
    private const float NetworkSnapThreshold = 15f;
    private const ushort NetworkRunTimelineId = 22; // mirrors Game.Movement.RunTimelineId

    // NetworkCatchUpSpeed is a floor, not the pacing itself, once the real snapshot
    // interval is known -- a fixed speed catches up to a stale target early and then
    // idles, jumping or snapping once the next update lands. estimatedNetworkUpdateInterval
    // (an EMA of the real gap) lets the step below pace across the whole remaining window.
    private const float NetworkIntervalSmoothingFactor = 0.3f;
    private const float MinNetworkPacingWindowSeconds = 0.05f;
    private float timeSinceLastNetworkUpdate;
    private float estimatedNetworkUpdateInterval = 0.05f;

    // Pacing still leaves the doppel frozen between updates; extrapolating from the
    // last observed velocity keeps the visual glide advancing instead of idling.
    // Capped short so a stale/bad velocity sample stops influencing the guess quickly.
    private const float MaxNetworkExtrapolationSeconds = 1f;
    private Vector3 networkVelocity;

    // Angular counterpart to NetworkCatchUpSpeed -- previously missing entirely,
    // which meant Rotation was written raw from whatever the latest snapshot said
    // rather than stepped toward it: an enemy tracking a moving target (Follow(),
    // Face() during a slow re-aim, etc.) held one facing for up to a full snapshot
    // interval then snapped straight to the next one, over and over, while its
    // position glided smoothly the whole time -- a smooth-body/strobing-facing
    // mismatch that reads as "laggy rotation" even though position was fine.
    // Raised from a half-turn-in-1/8s: same reasoning as NetworkCatchUpSpeed above
    // -- a tank tracking boss facing needs this to be a jitter filter, not a second
    // lag source. A half-turn in 1/20s keeps a full re-aim inside roughly one
    // snapshot interval at the current 24Hz rate; a genuine snap-to-target commit
    // (Face() right before a cone/cast resolves) still reads as fast/decisive
    // rather than a slow wind-up.
    private const float NetworkAngularCatchUpSpeed = MathF.PI * 20f;

    private Vector3? networkTargetPosition;
    private float networkTargetRotation;
    private bool networkInterpAnimActive;
    private bool networkMoving;

    // Tolerance below which two consecutive network positions read as "the same
    // spot" rather than motion -- filters quantization/floating-point noise
    // between snapshots that are otherwise identical.
    private const float NetworkMovementEpsilon = 0.01f;

    // Records the latest position/rotation broadcast by the host for this enemy --
    // see MultiplayerManager.OnWorldSnapshotReceived, the only caller. The actual
    // position write happens in Tick so the doppel steps toward it smoothly with a
    // run animation playing, instead of teleporting once per WorldSnapshotMessage.
    // networkMoving is read off whether the host's *reported* position is actually
    // advancing between updates, not off local interpolation state -- see
    // TickNetworkPosition's doc comment for why that distinction is the whole fix.
    public void ApplyNetworkPosition(Vector3 position, float rotation)
    {
        if (networkTargetPosition is { } previous)
        {
            networkMoving = Vector3.DistanceSquared(previous, position) > NetworkMovementEpsilon * NetworkMovementEpsilon;
            estimatedNetworkUpdateInterval += (timeSinceLastNetworkUpdate - estimatedNetworkUpdateInterval) * NetworkIntervalSmoothingFactor;
            networkVelocity = timeSinceLastNetworkUpdate > MinNetworkPacingWindowSeconds
                ? (position - previous) / timeSinceLastNetworkUpdate
                : Vector3.Zero;
        }
        networkTargetPosition = position;
        networkTargetRotation = rotation;
        timeSinceLastNetworkUpdate = 0f;
    }

    // Driving the run animation off networkInterpAnimActive alone (matched against
    // "did local interpolation finish catching up this tick") was broken two
    // different ways, confirmed via AnoMech-DamageDebug dumps showing bosses never
    // animating for peers at all:
    //   1. Movement.Tick() -- already run this frame via base.Tick() -- calls
    //      StopAnim() and bails whenever AnimationLock (cast.IsBusy) holds,
    //      resetting the native run animation with no way for this class to know
    //      it happened. Since a boss in these fights is casting constantly,
    //      networkInterpAnimActive goes stale (still says "already playing")
    //      almost immediately, and the guard below then never re-requests
    //      PlayActionTimeline for the rest of the fight.
    //   2. Once snapshots arrive every host frame instead of on a slower fixed
    //      interval (see MultiplayerManager's snapshot broadcast), the gap between
    //      consecutive targets shrinks to the point that "dist <= step" -- meant
    //      to mean "we've essentially arrived" -- became true almost every tick
    //      instead of rarely, which would have kept re-triggering the same
    //      reset-then-restart cycle.
    // networkMoving sidesteps both: it's derived purely from whether the host's
    // reported position is actually advancing (see ApplyNetworkPosition), which
    // stays correct regardless of what Movement.Tick() resets natively or how
    // fine-grained the per-tick position steps get -- the host's own position is
    // just as frozen during its cast (its own Movement.Tick() pauses the same way
    // for the same reason), so this self-corrects without needing a separate
    // AnimationLock check here at all.
    private void TickNetworkPosition(float deltaSeconds)
    {
        if (networkTargetPosition is not { } rawTarget) return;
        timeSinceLastNetworkUpdate += deltaSeconds;

        var target = rawTarget + networkVelocity * MathF.Min(timeSinceLastNetworkUpdate, MaxNetworkExtrapolationSeconds);
        var basePos = Position;
        var delta = target - basePos;
        var dist = delta.Length();
        var remainingWindow = MathF.Max(estimatedNetworkUpdateInterval - timeSinceLastNetworkUpdate, MinNetworkPacingWindowSeconds);
        var step = MathF.Max(dist / remainingWindow, NetworkCatchUpSpeed) * deltaSeconds;
        var nextRotation = MathUtil.StepRotation(Rotation, networkTargetRotation, NetworkAngularCatchUpSpeed * deltaSeconds);
        if (dist > NetworkSnapThreshold || dist <= step)
            SetPosition(new Placement(target, nextRotation));
        else
            SetPosition(new Placement(basePos + delta / dist * step, nextRotation));

        if (networkMoving && !networkInterpAnimActive)
        {
            PlayActionTimeline(NetworkRunTimelineId, baseOverride: NetworkRunTimelineId);
            networkInterpAnimActive = true;
        }
        else if (!networkMoving && networkInterpAnimActive)
        {
            ResetActionTimeline();
            networkInterpAnimActive = false;
        }
    }

    // Visibility runs through the DrawObject lifecycle: SetVisible records a desired
    // state; Tick's reconciler fires EnableDraw/DisableDraw once per change, gated on
    // IsReadyToDraw so toggles can't race the async model load. RenderFlags writes
    // were tried and don't reliably keep enemies visible — only this path does.
    // currentVisible starts true only as a label for "spawned visible"; ReconcileVisibility
    // always performs one explicit native write on the first tick regardless, since that
    // starting value was never a verified read of the actual DrawObject flag.
    private bool desiredVisible = true;
    private bool currentVisible = true;
    private bool loggedInitialVisibility;

    // Diagnostic-only: EnableDraw/DrawObject.IsVisible only gate the draw object itself,
    // not whether CharacterBase's per-slot equipment/body models finished streaming in --
    // a peer's reconstructed doppel was observed rendering weapon/shadow/VFX but never the
    // body, with both of the above already confirmed correct. Logs CharacterBase's
    // HasModelInSlotLoaded bitmask (temporary during load, 0 once every slot is done) and
    // each slot's Models[] pointer, once shortly after spawn and once a few seconds later,
    // to see whether a slot is genuinely stuck rather than just slow.
    private int slotCheckFrames;
    private bool slotCheckDone;
    private bool slotReloadAttempted;

    public uint BNpcBaseId { get; }

    // The config this enemy was spawned with. Read by MultiplayerManager's host-side
    // sampler so a peer can reconstruct the same doppel locally via world.SpawnEnemy —
    // avoids inventing a parallel spawn-description format for network replication.
    public EnemySpawnConfig SpawnConfig { get; internal set; }


    // Live-read via GameObject::GetName() (vfunc 6, resolves NameId -> BNpcName) —
    // same path the target bar uses, so engine-driven renames mid-fight propagate
    // (e.g. TOP P5 Sigma Omega: 1DD3 -> 1DD4 -> 1E0F -> 2FE2). Reading the
    // GameObject.Name[] buffer directly does NOT work for doppels — the engine never
    // refreshes it on rename. Falls back to the spawn-time name mid-despawn.
    public string DisplayName
    {
        get
        {
            var chara = BattleCharaPtr;
            if (chara == null) return field;
            var name = ((GameObject*)chara)->GetName().ToString();
            return string.IsNullOrEmpty(name) ? field : name;
        }
    }

    public EnemyListMode EnemyListMode { get; }
    private bool manualInEnemyList;

    // OnlyWhenVisible reads the live DrawObject.IsVisible flag, so any draw-lifecycle
    // toggle is reflected without extra plumbing; Manual lets the scenario drive it.
    public bool InEnemyList => EnemyListMode switch
    {
        EnemyListMode.Always          => true,
        EnemyListMode.Never           => false,
        EnemyListMode.Manual          => manualInEnemyList,
        EnemyListMode.OnlyWhenVisible => IsEngineVisible(),
        _ => false,
    };

    public bool IsCasting => cast.IsCasting;
    public int CastSeq => cast.CastSeq;
    public uint CastActionId => cast.ActionId;
    public float CastProgress => cast.Progress;
    public Vector3? CastTargetLocation => cast.TargetLocation;
    public GameObjectId? CastTargetId => cast.TargetId;
    public float CastTotalSeconds => cast.Total;
    public float CastOmenDelay => cast.OmenDelay;
    public int LastInstantCastSeq => cast.LastInstantCastSeq;
    public uint LastInstantCastActionId => cast.LastInstantCastActionId;
    public Vector3? LastInstantCastTargetLocation => cast.LastInstantCastTargetLocation;
    public GameObjectId? LastInstantCastTargetId => cast.LastInstantCastTargetId;

    // The last value passed to SetVisible -- read by MultiplayerManager's host-side
    // sampler. Not the same as IsEngineVisible (that lags behind async model load).
    public bool Visible => desiredVisible;

    internal SimEnemy(int index, uint bNpcBaseId, string displayName, EnemyListMode enemyListMode, Coordinates coordinates) : base(index, coordinates)
    {
        BNpcBaseId = bNpcBaseId;
        DisplayName = displayName;
        EnemyListMode = enemyListMode;
        cast = new SimCast(this, coordinates);
    }

    // Allocates a BattleChara, configures it as a BattleNpc per the supplied
    // config, and returns a SimEnemy wrapping it. Caller is responsible for
    // registering the result in the world's children list (so reset/teardown
    // covers it). Returns null on missing LocalPlayer, BNpcBase miss, or
    // CreateBattleChara failure.
    internal static SimEnemy? Spawn(EnemySpawnConfig config, SimWorld world)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        var bnpcSheet = Plugin.DataManager.GetExcelSheet<BNpcBase>();
        if (!bnpcSheet.TryGetRow(config.BNpcBaseId, out var bnpc))
        {
            Plugin.Log.Warning($"BNpcBase row {config.BNpcBaseId} (0x{config.BNpcBaseId:X}) not found");
            return null;
        }

        var modelCharaId = config.ModelCharaId != 0 ? config.ModelCharaId : bnpc.ModelChara.RowId;
        var modelCharaSheet = Plugin.DataManager.GetExcelSheet<ModelChara>();
        if (!modelCharaSheet.TryGetRow(modelCharaId, out var modelChara))
        {
            Plugin.Log.Warning($"ModelChara row {config.BNpcBaseId} (0x{config.BNpcBaseId:X}) not found");
            return null;
        }

        if (!CharacterManagerHelper.CreateCharacter(out var idx, out var obj)) return null;

        var gameObj = (GameObject*)obj;
        var chara = (BattleChara*)obj;
        // Engine's canonical BNpc initializer — populates ModelContainer from BNpcBase,
        // including ModeAttributeFlags (body sub-mesh, e.g. Omega-M's shield). Must run
        // before our overrides below.
        chara->CharacterSetup.SetupBNpc(config.BNpcBaseId, config.NameId);
        chara->ObjectKind = ObjectKind.BattleNpc;
        chara->Position = world.Coordinates.ToGlobal(config.Placement.Position);
        chara->SetRotation(MathUtil.NormalizeRotation(config.Placement.Rotation));
        var scale = config.Scale > 0f ? config.Scale : bnpc.Scale;
        chara->Scale = scale;
        chara->ModelContainer.ModelCharaId = (int)modelCharaId;
        chara->SEPack = bnpc.SEPack;

        var nativeHitbox = true;

        // From Client::Game::Character::CharacterSetupContainer_SetupRaw
        switch (modelChara.Type)
        {
            case 1:
                // TODO: This Type in the game's .exe is a bit complex, for now we just fallback to the previous solving method
                var hitboxRadius = config.HitboxRadius > 0f ? config.HitboxRadius : ResolveHitboxRadius(modelCharaId, scale);
                chara->HitboxRadius = hitboxRadius;
                nativeHitbox = false;
                break;
            case 2:
                chara->ModelContainer.ModelSkeletonId = modelChara.Model + 10000;
                break;
            case 3:
                chara->ModelContainer.ModelSkeletonId = modelChara.Model;
                break;
        }

        if (nativeHitbox)
        {
            chara->ModelContainer.UnscaledRadius = ModelContainerPointers.CalculateUnscaledRadius(&chara->ModelContainer);
            chara->HitboxRadius = chara->Scale * chara->ModelContainer.UnscaledRadius; // From Client::Game::Character::ModelContainer_UpdateHitboxRadius
        }

        // Engine-resolved name (vfunc 6), same source as the nameplate, so the Name[]
        // buffer we stamp below stays consistent with the rest of the UI.
        var displayName = gameObj->GetName().ToString();
        if (string.IsNullOrEmpty(displayName)) displayName = $"BNpc {config.BNpcBaseId:X}";
        GameObjectHelper.WriteName(gameObj, displayName);
        obj->RenderFlags = 0;

        chara->CharacterSetup.CopyFromCharacter((Character*)chara, CharacterSetupContainer.CopyFlags.None);

        chara->BattleNpcSubKind = BattleNpcSubKind.Combatant;
        chara->MaxHealth = 1_000_000;
        chara->Health = 1_000_000;
        chara->Battalion = 4;
        chara->IsHostile = true;
        chara->InCombat = true;
        chara->CombatTagType = 1;
        chara->CombatTaggerId = ((GameObject*)player.Address)->GetGameObjectId();
        chara->Mode = CharacterModes.Normal;
        chara->ModeParam = 0;
        if (config.InitialModeAttributeFlags is { } maf)
            chara->ModelContainer.ModeAttributeFlags = maf;
        chara->CastInfo.IsCasting = false;
        if (config.NameId != 0) chara->NameId = config.NameId;
        if (config.Level != 0) chara->Level = config.Level;

        // Was Plugin.Log.Info (invisible in dumps) -- upgraded plus the actually-resolved
        // values (not just the BNpcBase sheet defaults) so a host/guest dump pair can be
        // diffed directly for a config mismatch, e.g. a peer resolving a different
        // modelCharaId/scale/skeleton than the host used for the same enemy.
        DiagnosticLog.Info($"[SimEnemy.Spawn] BNpcBase {config.BNpcBaseId}: resolved modelCharaId={modelCharaId} (sheet default {bnpc.ModelChara.RowId}), scale={scale} (sheet default {bnpc.Scale}), hitboxRadius={chara->HitboxRadius} (nativeHitbox={nativeHitbox}), modelChara.Type={modelChara.Type}, ModelSkeletonId={chara->ModelContainer.ModelSkeletonId}, ModeAttributeFlags=0x{chara->ModelContainer.ModeAttributeFlags:X2} -- at index {idx}, goid {gameObj->GetGameObjectId()}, pos {config.Placement.Position}, visible {config.IsVisible}.");
        var enemy = new SimEnemy(idx, config.BNpcBaseId, displayName, config.EnemyList, world.Coordinates)
        {
            SpawnConfig = config,
        };
        // Mirror the native position/rotation writes above into the C#-side fields.
        enemy.SetPosition(config.Placement);
        enemy.SetTargetable(config.Targetable);
        if (!config.IsVisible) enemy.SetVisible(false);
        return enemy;
    }

    private static float ResolveHitboxRadius(uint modelCharaId, float scale)
    {
        const float DefaultUnscaledRadius = 0.5f;
        var sheet = Plugin.DataManager.GetExcelSheet<ModelChara>();
        var unscaled = DefaultUnscaledRadius;
        if (sheet.TryGetRow(modelCharaId, out var row) && row.Unknown0 > 0f)
            unscaled = row.Unknown0;
        return unscaled * scale;
    }

    public override void Despawn()
    {
        Movement.Follow(null);
        cast.Despawn();
        base.Despawn();
    }

    /// <summary>
    /// Sets the targetable status of this <see cref="SimEnemy"/>, which will reflect in their Nameplate and in the Enemy List (if visible there).
    /// </summary>
    /// <param name="targetable">
    /// If <see langword="true"/>, then the Nameplate will be visible, and able to target them using the Enemy List.
    /// If <see langword="false"/>, then the Nameplate will not be visible, and not able to target them using the Enemy List.
    /// </param>
    public void SetTargetable(bool targetable)
    {
        var chara = BattleCharaPtr;
        if (chara == null) return;
        if (targetable)
        {
            chara->TargetableStatus |= (ObjectTargetableFlags)1 | ObjectTargetableFlags.IsTargetable;
        }
        else
        {
            chara->TargetableStatus &= ~((ObjectTargetableFlags)1 | ObjectTargetableFlags.IsTargetable);
        }
    }

    /// <summary>
    /// Only executed when <see cref="EnemyListMode"/> is <see cref="EnemyListMode.Manual"/>
    /// </summary>
    /// <param name="inEnemyList">Will make the Enemy appear or not in the Enemy List (Enmity List)</param>
    public void SetVisibleInEnemyList(bool inEnemyList)
    {
        if (EnemyListMode != EnemyListMode.Manual)
        {
            Plugin.Log.Warning($"SetInEnemyList({inEnemyList}) ignored: SimEnemy {DisplayName} has mode {EnemyListMode}; declare EnemyListMode.Manual in EnemySpawnConfig to use explicit toggles.");
            return;
        }
        manualInEnemyList = inEnemyList;
    }

    /// <summary>
    /// Sets the target of this <see cref="SimEnemy"/>.
    /// </summary>
    /// <remarks>For now, this is purely visual and does not contain any logic relating to auto-attacks or similar.</remarks>
    /// <param name="target">The <see cref="SimCharacter.GameObjectId"/> will be retrieved and used as the TargetId. If <see langword="null"/>, then the target is cleared.</param>
    /// <param name="follow">If <paramref name="target"/> is valid, this will determine if the <see cref="SimEnemy"/> should now follow <paramref name="target"/> or not.</param>
    /// <param name="speed">If <paramref name="target"/> is valid and <paramref name="follow"/> is <see langword="true"/>, this will be the speed that the <see cref="SimEnemy"/> will follow the <paramref name="target"/></param>
    public void SetTarget(SimCharacter? target, bool follow = true, float speed = 6f)
    {
        if (target == null)
        {
            BattleCharaPtr->TargetId = 0xE0000000;
        }
        else
        {
            BattleCharaPtr->TargetId = target.GameObjectId;

            if (follow)
            {
                Follow(target);
            }
        }
    }

    public void SetVisible(bool visible) => desiredVisible = visible;

    // Read side of PlayAnimationTimeline -- MultiplayerManager samples this so a
    // scenario's discrete, one-shot animation cues (Kefka's WarpOut/Spawn teleport,
    // etc.) replicate to peers. A byte-for-byte sibling of ModelState/edge-triggered
    // application, not the inherited PlayActionTimeline: that base method is also
    // what Movement uses internally to drive the locomotion run-cycle every time this
    // enemy starts/stops moving (see Movement.StartAnim), and TickNetworkPosition
    // above independently re-triggers that same run-cycle on a peer while smoothing
    // toward a broadcast position -- tracking/replicating every PlayActionTimeline
    // call indiscriminately would make those two "who's driving the animation right
    // now" mechanisms fight each other every movement tick. PlayAnimationTimeline is
    // therefore a separate, deliberate method scenarios call ONLY for a real
    // scenario-authored animation cue, never used by Movement.
    public ushort? AnimationTimelineId { get; private set; }

    public void PlayAnimationTimeline(ushort timelineId, ushort loopId = 0, ushort baseOverride = 0)
    {
        AnimationTimelineId = timelineId;
        PlayActionTimeline(timelineId, loopId, baseOverride);
    }

    public void Follow(SimCharacter? target = null, float speed = 6f) => Movement.Follow(target, speed);

    private void ReconcileVisibility()
    {
        var firstTick = !loggedInitialVisibility;
        if (firstTick)
        {
            loggedInitialVisibility = true;
            var chara = BattleCharaPtr;
            DiagnosticLog.Info($"[SimEnemy.ReconcileVisibility] {DisplayName} (BNpcBase {BNpcBaseId}, goid {GameObjectId}) first tick: desiredVisible={desiredVisible} currentVisible={currentVisible} DrawObject={(chara == null ? "no BattleChara" : chara->DrawObject == null ? "null" : "present")}.");
        }

        // currentVisible defaulting true was only an assumption that the initial
        // EnableDraw (base SimNpc.Tick) leaves the native DrawObject visible -- observed
        // false for a peer's reconstructed doppel (host's own boss rendered fine,
        // identical config, same IsVisible: true from spawn) even though our own
        // desiredVisible/currentVisible already agreed, so ReconcileVisibility never
        // wrote the native flag at all. Forcing one explicit write on the first tick,
        // regardless of that agreement, closes the gap without touching the
        // already-correct explicit-reveal case (spawn IsVisible: false, SetVisible(true)
        // later) that exercised this write path before and masked the bug.
        if (!firstTick && desiredVisible == currentVisible)
        {
            return;
        }

        var obj = BattleCharaPtr;
        if (obj == null || obj->DrawObject == null)
        {
            return;
        }

        obj->DrawObject->IsVisible = desiredVisible;
        currentVisible = desiredVisible;

        // GameObjectId, not just DisplayName, since multiple simultaneous enemies can
        // share the exact same display name (UMAD spawns several "Kefka"-named BNpcs at
        // once, only one of which is the actual scaled-up model) -- without a stable ID
        // here, two independent machines' logs can't be matched up to confirm they're
        // even talking about the same enemy.
        DiagnosticLog.Info($"[SimEnemy.ReconcileVisibility] {DisplayName} (BNpcBase {BNpcBaseId}, goid {GameObjectId})'s visibility was set to {desiredVisible} at pos {Position}");
    }

    // Authoritative draw state (DrawObject.Flags bits 0 and 3, set by Enable/DisableDraw).
    // False during the async model-load window where DrawObject is still null.
    private bool IsEngineVisible()
    {
        var obj = BattleCharaPtr;
        if (obj == null) return false;
        var draw = obj->DrawObject;
        return draw != null && draw->IsVisible;
    }

    // Engine doesn't expose post-action animation-lock duration via EXD — the
    // real value only ships in the server's ActionEffect packet. 0.6s is a
    // reasonable approximation for most boss abilities; if a scenario needs
    // tighter timing we can derive per-action values from captured ACT logs.
    public bool Cast(uint actionId, Vector3? targetLocation = null, float? castSeconds = null, GameObjectId? targetId = null, float omenDelay = 0f, float omenRotate = 0f, byte animationVariation = 0, float animationLock = 0.6f, float? fireDelay = null)
    {
        Core.DiagnosticLog.Info(
            $"[SimEnemy] Cast: {Core.ActionLookup.Name(actionId)} ({actionId}) from ({Position.X:F1},{Position.Z:F1}) rot={Rotation:F3} castSeconds={castSeconds?.ToString("F2") ?? "default"}.");
        // targetLocation stays scenario-local; SimCast lifts to world at native boundaries.
        return cast.Start(actionId, targetLocation, castSeconds, targetId, omenDelay, omenRotate, animationVariation, animationLock, fireDelay);
    }

    public void NativeCast(uint actionId, ActionType actionType, float omenDelay, float castTime, bool interruptible, float? rotation = null, Vector3? position = null, GameObjectId? targetId = null, GameObjectId? ballistaId = null)
    {
        cast.NativeCast(actionId, actionType, omenDelay, castTime, interruptible, rotation, position, targetId, ballistaId);
    }

    public void NativeActionEffect(uint actionId, float animationLock, ushort spellId, byte animationVariaton, ActionType actionType, byte flags, float? rotation = null, Vector3? position = null, GameObjectId? animationTargetId = null, GameObjectId? actionTargetId = null, GameObjectId? ballistaId = null)
    {
        cast.NativeActionEffect(actionId, animationLock, spellId, animationVariaton, actionType, flags, rotation, position, animationTargetId, actionTargetId, ballistaId);
    }

    public override bool AnimationLock => cast.IsBusy;

    public override void Tick(float deltaSeconds)
    {
        base.Tick(deltaSeconds);
        ReconcileVisibility();
        cast.Tick(deltaSeconds);
        TickNetworkPosition(deltaSeconds);

        if (!slotCheckDone && desiredVisible)
        {
            slotCheckFrames++;
            if (slotCheckFrames == 1) LogModelSlotState("+1 frame");
            else if (slotCheckFrames == 5) LogModelSlotState("+5 frames");
            else if (slotCheckFrames == 210)
            {
                var anyStuck = LogModelSlotState("+210 frames (~3.5s)");
                // A stuck slot here means the load attempt already gave up (HasModelInSlotLoaded
                // cleared to 0) without ever populating Models[] -- confirmed via local
                // FFXIVClientStructs source, this is a failed load, not just a slow one, so
                // waiting longer won't help. ReloadModel's DisableDraw->pendingDraw->EnableDraw
                // cycle is the same mechanism SetModeAttributeFlags already uses to force a full
                // sub-mesh rebuild; retried once here to give the slot a second load attempt.
                if (anyStuck && !slotReloadAttempted)
                {
                    slotReloadAttempted = true;
                    DiagnosticLog.Warn($"[SimEnemy] {DisplayName} (goid {GameObjectId}) has a model slot stuck unloaded after 3.5s -- forcing one ReloadModel retry.");
                    ReloadModel();
                    slotCheckFrames = 0;
                }
                else
                {
                    slotCheckDone = true;
                }
            }
        }
    }

    // Returns true if any slot is still unloaded.
    private unsafe bool LogModelSlotState(string label)
    {
        var chara = BattleCharaPtr;
        if (chara == null) return false;
        var draw = (CharacterBase*)chara->DrawObject;
        if (draw == null)
        {
            DiagnosticLog.Info($"[SimEnemy.LogModelSlotState] {DisplayName} (goid {GameObjectId}) {label}: DrawObject null.");
            return false;
        }
        var slotLoaded = Enumerable.Range(0, draw->SlotCount).Select(i => draw->ModelsSpan[i].Value != null).ToList();
        var slots = string.Join(",", slotLoaded.Select((loaded, i) => loaded ? $"{i}:loaded" : $"{i}:null"));
        DiagnosticLog.Info($"[SimEnemy.LogModelSlotState] {DisplayName} (goid {GameObjectId}) {label}: SlotCount={draw->SlotCount} HasModelInSlotLoaded=0x{draw->HasModelInSlotLoaded:X} HasModelFilesInSlotLoaded=0x{draw->HasModelFilesInSlotLoaded:X} slots=[{slots}].");

        // Deeper than Models[]/HasModelInSlotLoaded: PerSlotStagingArea is the actual
        // in-progress load record (staging.Flags/ModelResourceHandle), and the resource
        // handle it points at carries the real file path being loaded plus the native
        // engine's own LoadState/ReadState/LastIOResult -- this is what actually tells us
        // WHERE in the pipeline a stuck slot stopped (never requested vs. requested but the
        // read/IO never finished vs. read finished but never committed to Models[]), rather
        // than just that it's stuck. ResolveMdlPath gives the path the engine intends to use
        // for the slot, independent of whether a load was ever kicked off for it.
        for (var i = 0; i < draw->SlotCount; i++)
        {
            string resolvedPath;
            try { resolvedPath = draw->ResolveMdlPath((uint)i); }
            catch (Exception ex) { resolvedPath = $"<ResolveMdlPath threw: {ex.Message}>"; }

            if (draw->PerSlotStagingArea == null)
            {
                DiagnosticLog.Info($"[SimEnemy.LogModelSlotState] {DisplayName} slot {i} {label}: PerSlotStagingArea=null, ResolveMdlPath={resolvedPath}.");
                continue;
            }
            var staging = draw->PerSlotStagingArea[i];
            if (staging.ModelResourceHandle == null)
            {
                DiagnosticLog.Info($"[SimEnemy.LogModelSlotState] {DisplayName} slot {i} {label}: staging.Flags={staging.Flags}, ModelResourceHandle=null, ResolveMdlPath={resolvedPath}.");
                continue;
            }
            var rh = (ResourceHandle*)staging.ModelResourceHandle;
            DiagnosticLog.Info($"[SimEnemy.LogModelSlotState] {DisplayName} slot {i} {label}: staging.Flags={staging.Flags}, handle.FileName=\"{rh->FileName}\", LoadState={rh->LoadState}, ReadState={rh->ReadState}, OtherState={rh->OtherState}, LastIOResult={rh->LastIOResult}, RefCount={rh->RefCount}, ResolveMdlPath={resolvedPath}.");
        }

        return slotLoaded.Any(loaded => !loaded);
    }

    public CharacterFind<T> Find<T>(List<T> targets) where T : IPositioned
    {
        return new CharacterFind<T>(targets);
    }
}
