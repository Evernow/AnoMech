using System;
using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.SimObjects;

// A party slot occupied by a real remote player (a multiplayer peer, or the
// host's own view of one) rather than a local AI bot. Visually identical to
// SimPartyNpc -- same Lalafell-doppel spawn path in PartyCreator -- but its
// Movement is a no-op (NetworkPuppetMovement) so scenario/AI code that
// addresses party slots uniformly (AiManager.Move calls .MoveTo on every
// slot, including the real local player's) harmlessly skips it exactly the
// way it already skips SimPlayer. The only writer of this slot's position is
// ApplyNetworkPose, driven by poses received from the peer who owns it.
//
// Deliberately not a SimPartyNpc subclass: SimPartyNpc is sealed, and the two
// only share ~a dozen lines (Dead/OnKilled/Knockback), so a sibling class is
// simpler than lifting the seal on tested engine code for one extra caller.
public sealed unsafe class SimNetworkPuppet : SimNpc, ISimPartyMember
{
    // Sanity cap on how fast we step toward a newly received pose. Distances beyond
    // SnapThreshold (spawn placement, a lag spike, a genuine teleport) skip
    // interpolation entirely rather than visibly gliding across the arena.
    // See SimEnemy.NetworkCatchUpSpeed's doc comment for why this was raised from
    // 12f -- same reasoning: it's meant to be a jitter filter over
    // MultiplayerManager's snapshot interval, not a second lag source stacked on
    // top of it.
    private const float CatchUpSpeed = 20f;
    private const float SnapThreshold = 15f;
    // Angular counterpart to CatchUpSpeed -- see SimEnemy.NetworkAngularCatchUpSpeed's
    // doc comment (same reasoning, same value) for why rotation needs stepping too,
    // and why this was raised from a half-turn-in-1/8s.
    private const float AngularCatchUpSpeed = MathF.PI * 20f;
    // Mirrors Game.Movement.RunTimelineId -- can't reference it by type name here
    // since the Movement property below shadows the Movement type in this scope.
    private const ushort RunTimelineId = 22;

    private Vector3? targetPosition;
    private float targetRotation;
    private bool interpAnimActive;
    private bool poseMoving;

    // Tolerance below which two consecutive poses read as "the same spot" rather
    // than motion -- filters quantization/floating-point noise between updates
    // that are otherwise identical. See SimEnemy.NetworkMovementEpsilon (same
    // reasoning, same value).
    private const float PoseMovementEpsilon = 0.01f;

    // Mechanic resolution (AoeQuery, stack/distance checks, gaze facing, ...) must
    // see where the peer's real character actually is right now, not wherever the
    // model has smoothly interpolated to -- ticking mechanics off the render-lagged
    // position would judge a correctly-positioned player as being in the wrong spot
    // (or vice versa) for as long as the catch-up step hasn't landed. base.Position
    // (native transform) is still what Tick's interpolation drives and what the
    // player visually sees; this only redirects what game logic reads.
    public override Vector3 Position => targetPosition ?? base.Position;

    public PartyRole Role { get; set; }
    public bool Dead { get; private set; }
    public byte ClassJob { get; }
    public string DisplayName { get; }

    internal SimNetworkPuppet(int index, Coordinates coordinates, PartyRole role, byte classJob, string name) : base(index, coordinates)
    {
        Role = role;
        ClassJob = classJob;
        DisplayName = name;
    }

    private protected override Movement Movement => field ??= new NetworkPuppetMovement(this);

    // Records the latest pose reported by the owning peer's real client. The
    // actual position write happens in Tick (see below) so the model steps
    // toward it smoothly with the run animation playing, instead of teleporting
    // once per network update. poseMoving is read off whether the reported
    // position is actually advancing between updates, not off local
    // interpolation state -- see SimEnemy.TickNetworkPosition's doc comment for
    // why that's the more robust signal (this class doesn't hit the specific
    // AnimationLock desync that motivated it there -- AnimationLock is always
    // false for a puppet -- but the same "gap between targets can shrink to
    // nothing once updates arrive fast enough" risk applies here too).
    public void ApplyNetworkPose(Vector3 position, float rotation)
    {
        if (targetPosition is { } previous)
            poseMoving = Vector3.DistanceSquared(previous, position) > PoseMovementEpsilon * PoseMovementEpsilon;
        targetPosition = position;
        targetRotation = rotation;
    }

    public override void Tick(float deltaSeconds)
    {
        base.Tick(deltaSeconds);
        if (Dead || targetPosition is not { } target) return;

        // Interpolate the visual/native transform, not the overridden Position
        // (which already reports `target` directly) -- basePos is where the
        // rendered model currently sits.
        var basePos = base.Position;
        var delta = target - basePos;
        var dist = delta.Length();
        var step = CatchUpSpeed * deltaSeconds;
        var nextRotation = MathUtil.StepRotation(Rotation, targetRotation, AngularCatchUpSpeed * deltaSeconds);
        if (dist > SnapThreshold || dist <= step)
            SetPosition(new Placement(target, nextRotation));
        else
            SetPosition(new Placement(basePos + delta / dist * step, nextRotation));

        if (poseMoving && !interpAnimActive)
        {
            PlayActionTimeline(RunTimelineId, baseOverride: RunTimelineId);
            interpAnimActive = true;
        }
        else if (!poseMoving && interpAnimActive)
        {
            ResetActionTimeline();
            interpAnimActive = false;
        }
    }

    public void Knockback(Vector3 source, float distance, float speed) => Movement.Knockback(source, distance, speed);

    public void OnKilled()
    {
        Dead = true;
        StopMoving();
        interpAnimActive = false;
        var bc = BattleCharaPtr;
        if (bc == null) return;
        bc->Health = 0;
        bc->Mana = 0;
        bc->Mode = CharacterModes.Dead;
        this.PlayKoActionTimeline();
    }
}
