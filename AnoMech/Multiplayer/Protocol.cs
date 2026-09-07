using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Top;
using AnoMech.Scenarios.Umad.P2Forsaken;
using AnoMech.Scenarios.Umad.P3BlackHole;

namespace AnoMech.Multiplayer;

// Wire format for AnoMech's multiplayer relay (see Relay/README.md -- the relay itself is a
// dumb per-session broadcaster with no knowledge of any of this). Position/rotation are
// always flattened to floats rather than Vector3/Placement: System.Text.Json's reflection
// serializer doesn't handle Vector3's public fields without extra converter plumbing.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(LobbyStateMessage), "lobby")]
[JsonDerivedType(typeof(ClaimRoleMessage), "claim")]
[JsonDerivedType(typeof(ReleaseRoleMessage), "release")]
[JsonDerivedType(typeof(StartMessage), "start")]
[JsonDerivedType(typeof(StartCheckMessage), "startCheck")]
[JsonDerivedType(typeof(StartCheckResponseMessage), "startCheckResponse")]
[JsonDerivedType(typeof(SelfPoseMessage), "pose")]
[JsonDerivedType(typeof(WorldSnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(RolesSnapshotMessage), "rolesSnapshot")]
[JsonDerivedType(typeof(RoleKilledMessage), "killed")]
[JsonDerivedType(typeof(KnockbackMessage), "knockback")]
[JsonDerivedType(typeof(SpawnOmenMessage), "spawnOmen")]
[JsonDerivedType(typeof(EndMessage), "end")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(PeerStatusMessage), "status")]
[JsonDerivedType(typeof(SessionEndedMessage), "sessionEnded")]
[JsonDerivedType(typeof(ResetRequestMessage), "resetRequest")]
[JsonDerivedType(typeof(LeaveRequestMessage), "leaveRequest")]
[JsonDerivedType(typeof(AiReplayStateMessage), "aiReplayState")]
[JsonDerivedType(typeof(P2AiReplayStateMessage), "p2AiReplayState")]
[JsonDerivedType(typeof(P2LockonsUpdateMessage), "p2LockonsUpdate")]
[JsonDerivedType(typeof(MapEffectMessage), "mapEffect")]
[JsonDerivedType(typeof(MapDirectorUpdateMessage), "mapDirectorUpdate")]
[JsonDerivedType(typeof(SetWeatherMessage), "setWeather")]
[JsonDerivedType(typeof(P4AiReplayStateMessage), "p4AiReplayState")]
[JsonDerivedType(typeof(P5AiReplayStateMessage), "p5AiReplayState")]
[JsonDerivedType(typeof(TopP6WaveCannon2AiReplayStateMessage), "topP6Wc2AiReplayState")]
[JsonDerivedType(typeof(TopP2PartySynergyAiReplayStateMessage), "topP2PsAiReplayState")]
[JsonDerivedType(typeof(TopP5SigmaAiReplayStateMessage), "topP5SigmaAiReplayState")]
[JsonDerivedType(typeof(TopP5OmegaAiReplayStateMessage), "topP5OmegaAiReplayState")]
[JsonDerivedType(typeof(UltimatePredationAiReplayStateMessage), "ultimatePredationAiReplayState")]
[JsonDerivedType(typeof(TopP5DeltaAiReplayStateMessage), "topP5DeltaAiReplayState")]
[JsonDerivedType(typeof(TopP5DeltaBeyondDefenseUpdateMessage), "topP5DeltaBeyondDefenseUpdate")]
[JsonDerivedType(typeof(TopP5OmegaHelloWorld2UpdateMessage), "topP5OmegaHelloWorld2Update")]
[JsonDerivedType(typeof(SelfMitigationMessage), "selfMitigation")]
[JsonDerivedType(typeof(PeerAppliedEnemyStatusMessage), "peerAppliedEnemyStatus")]
[JsonDerivedType(typeof(PeerAppliedRoleStatusMessage), "peerAppliedRoleStatus")]
public abstract record MpMessage;

// Marker for a message type only the host ever legitimately sends. RelayClient tags each
// received message with whether the relay says it came from the room's host; DispatchCore
// drops one of these if that tag says otherwise, so a peer who joined a session can't forge
// host-authoritative state. Best-effort -- a relay predating "senderIdentity" can't attest to
// this at all, so the tag defaults to trusted then (see RelayClient.SupportsSenderIdentity).
internal interface IHostOnlyMessage;

// Marker for a scenario's initial replay-state broadcast (every *AiReplayStateMessage type
// implements this) -- lets MultiplayerManager.Dispatch.cs route any of them with one generic
// case instead of one per scenario. See IMultiplayerReplayable.
internal interface IScenarioReplayStateMessage : IHostOnlyMessage;

// Same idea, for a scenario's optional mid-run re-sync message -- see
// IMultiplayerReplayable.BuildMidRunUpdateMessage.
internal interface IScenarioMidRunUpdateMessage : IHostOnlyMessage;

// Peer -> host, sent once right after connecting to register a display name before any role
// is claimed. Version/Checksum identify the sender's plugin build so the host can catch a
// mismatch before it desyncs.
public sealed record HelloMessage(Guid PeerId, string DisplayName, string Version, string Checksum) : MpMessage;

public sealed record PeerBuildInfo(string Version, string Checksum)
{
    public string ShortChecksum => Checksum.Length >= 6 ? Checksum[..6] : Checksum;
}

// Host -> everyone, full-state (not delta) so a client that missed an update self-heals on
// the next broadcast. ScenarioIndex/SelectedAi/SelectedWaymark are carried here (not only in
// StartMessage) so a late joiner who missed the original StartMessage still resolves the
// same scenario/strat/waymark.
public sealed record LobbyStateMessage(
    Guid HostId,
    Dictionary<PartyRole, Guid> ClaimedBy,
    Dictionary<Guid, string> Names,
    Dictionary<Guid, PeerBuildInfo> Builds,
    bool Started,
    int ScenarioIndex,
    int SelectedAi,
    int SelectedWaymark,
    Dictionary<string, ushort> TankBusterPlan) : MpMessage, IHostOnlyMessage;

public sealed record ClaimRoleMessage(Guid PeerId, PartyRole Role) : MpMessage;
public sealed record ReleaseRoleMessage(Guid PeerId) : MpMessage;

// Host -> everyone. Each client independently calls RunScenarioAsHost/AsPeer on receipt --
// no tight clock sync needed, since host-authoritative mechanic resolution is what keeps
// outcomes consistent regardless of a little start skew.
public sealed record StartMessage : MpMessage, IHostOnlyMessage;

// Host -> everyone, sent before Session.Started/StartMessage. RunScenarioInternal silently
// no-ops if a client isn't in an inn -- this round-trip surfaces that to the host instead.
// Every claimed peer answers with StartCheckResponseMessage.
public sealed record StartCheckMessage : MpMessage, IHostOnlyMessage;

public sealed record StartCheckResponseMessage(Guid PeerId, bool Ready, string? Reason) : MpMessage;

// Peer -> host, at PoseSendRate. Host applies it to that peer's SimNetworkPuppet and
// republishes it in the next WorldSnapshot's Roles list.
public sealed record SelfPoseMessage(Guid PeerId, float X, float Y, float Z, float Rotation) : MpMessage;

// One SimEnemy as the host currently has it. NetId is a host-assigned, per-run id (not the
// game's EntityId) correlating this enemy across snapshots; spawn-config fields let a peer
// reconstruct the same doppel via world.SpawnEnemy on first sight. ModelState/Statuses mirror
// direct native writes (SetModelState, AddStatus/RemoveStatus) a peer has no other way to
// learn about. Cast* / LastInstantCast* mirror SimCast's fields, needed so a peer's replayed
// Cast() gets the right target location/duration/omen delay/target instead of guessing from a
// Lumina sheet lookup that doesn't match a scenario's synthetic helper-enemy action IDs.
// CastSeq/LastInstantCastSeq are edge-triggered counters (not the IsCasting rising edge) since
// an instant cast never sets IsCasting at all. CastTargetEnemyNetId/Role (and their
// LastInstantCast* siblings) resolve an entity target the same way TetherState's ends do,
// since a raw GameObjectId means nothing across the host/peer boundary.
public sealed record EnemyStatusState(ushort StatusId, ushort Stacks, float RemainingTime);

public sealed record EnemyState(
    int NetId, uint BNpcBaseId, uint NameId, byte Level, bool Targetable,
    EnemyListMode EnemyList, uint ModelCharaId, float Scale, float HitboxRadius,
    byte? InitialModeAttributeFlags, bool Visible, byte ModelState,
    IReadOnlyList<EnemyStatusState> Statuses, ushort? AnimationTimelineId, int AnimationTimelineSeq, IReadOnlyList<uint> NewLockonVfxIds,
    int? AnimationStateArg2, int? AnimationStateArg3, int AnimationStateSeq,
    float X, float Y, float Z, float Rotation,
    bool IsCasting, int CastSeq, uint CastActionId, float CastSeconds, float CastOmenDelay,
    float? CastTargetX, float? CastTargetY, float? CastTargetZ,
    int? CastTargetEnemyNetId, PartyRole? CastTargetRole,
    int LastInstantCastSeq, uint LastInstantCastActionId,
    float? LastInstantCastTargetX, float? LastInstantCastTargetY, float? LastInstantCastTargetZ,
    int? LastInstantCastTargetEnemyNetId, PartyRole? LastInstantCastTargetRole);

// A GrabbyTether-style link. Each end resolves to a live enemy (by NetId) or a party role; a
// peer reconstructs it via world.Tether for the real VFX plumbing.
public sealed record TetherState(int NetId, ushort TetherId, int? AEnemyNetId, PartyRole? ARole, int? BEnemyNetId, PartyRole? BRole);

// Statuses/NewLockonVfxIds mirror EnemyState -- not self-authoritative even for a peer's own
// role, since a peer runs no scenario logic. CurrentHp/MaxHp are the same story:
// TankMitigation/TankHpRegen only ever run on the host, so without broadcasting these a
// peer's own puppet for another role would sit at spawn-default HP forever.
public sealed record RoleState(
    PartyRole Role, bool Filled, bool Dead, float X, float Y, float Z, float Rotation,
    IReadOnlyList<EnemyStatusState> Statuses, IReadOnlyList<uint> NewLockonVfxIds,
    uint CurrentHp, uint MaxHp);

// One SimEventObject (or SimTower) as the host has it. Without this message type, event
// objects don't replicate at all -- the enemy sampler only ever walks SimEnemy.
// LayoutId drives which SharedGroup the engine attaches for this EObj (see
// EventObjectSpawnConfig.LayoutId) -- without it, a peer's reconstructed config defaulted to
// LayoutId 0 and requested the wrong SharedGroup. Root cause of an Ultimate Predation arena
// piece staying invisible on a peer, previously misread as a streaming race.
public sealed record EventObjectState(
    int NetId, uint EObjId, ushort TimelineState, ushort CurrentState,
    float X, float Y, float Z, float Rotation, uint LayoutId);

// Host -> everyone, at SnapshotSendRate. Full-state, so a dropped frame is one tick of
// staleness, not a permanently wrong reconstruction.
public sealed record WorldSnapshotMessage(
    List<EnemyState> Enemies, List<TetherState> Tethers,
    List<EventObjectState> EventObjects) : MpMessage, IHostOnlyMessage;

// Host -> everyone, paced independently of WorldSnapshotMessage (see RelayClient's priority
// queue) -- role positions are small and urgent, enemy data can be large.
public sealed record RolesSnapshotMessage(List<RoleState> Roles) : MpMessage, IHostOnlyMessage;

// Host -> everyone, one per Game.PartyMemberKilled. The recipient calls Game.Kill on whatever
// currently occupies that role locally.
public sealed record RoleKilledMessage(PartyRole Role, string Cause) : MpMessage, IHostOnlyMessage;

// Host -> everyone, one per SimNetworkPuppet.Knockback (polled via PendingNetworkKnockback --
// see its doc comment). Recipient applies the same Knockback to whatever occupies that role
// locally, same as RoleKilledMessage.
public sealed record KnockbackMessage(PartyRole Role, float SourceX, float SourceY, float SourceZ, float Distance, float Speed) : MpMessage, IHostOnlyMessage;

// Host -> everyone, one per SimWorld.OmenSpawned -- a peer has no other way to see a
// scenario's manually-spawned AOE telegraphs. Recipient replays the identical SpawnOmen call.
public sealed record SpawnOmenMessage(string Path, float X, float Y, float Z, float Rotation, float ScaleX, float ScaleY, float ScaleZ, float DurationSeconds) : MpMessage, IHostOnlyMessage;

// Host -> everyone, sent whenever the host's run ends for any reason. ReturnedToInn
// distinguishes Reset() (stays in-zone) from Leave()/a natural finish (unloads to the inn) --
// both clear ActiveScenario identically, so the host reads World.Map.IsInInstance at
// broadcast time to tell them apart.
public sealed record EndMessage(bool ReturnedToInn) : MpMessage, IHostOnlyMessage;

// Host -> everyone, every PingIntervalSeconds, decoupled from Started so connection quality
// is live in the lobby before Start. SentAtMs is the host's own clock; only the host ever
// compares it, so there's no cross-machine sync to worry about.
public sealed record PingMessage(long SentAtMs) : MpMessage, IHostOnlyMessage;

public sealed record PongMessage(Guid PeerId, long SentAtMs) : MpMessage;

public sealed record PeerStatusEntry(float? LatencyMs, float SecondsSinceLastSeen);

public sealed record PeerStatusMessage(Dictionary<Guid, PeerStatusEntry> Statuses) : MpMessage, IHostOnlyMessage;

// Sent by whoever clicks Leave session, to every other client. Only ends the session for the
// whole group when the host is who left; a departing peer just gets dropped from the roster.
// PeerId is how the recipient tells the two cases apart (PeerId == Session.HostId).
public sealed record SessionEndedMessage(Guid PeerId) : MpMessage;

// Peer -> host: reset the encounter. The peer doesn't reset locally -- the host resets its
// own authoritative run, which reaches everyone (including the requester) via EndMessage.
public sealed record ResetRequestMessage(Guid PeerId) : MpMessage;

// Peer -> host: end the run and send everyone to the inn (distinct from SessionEndedMessage,
// which disconnects the sender entirely). Same single-code-path reasoning as
// ResetRequestMessage -- the session itself is untouched, only the current run ends.
public sealed record LeaveRequestMessage(Guid PeerId) : MpMessage;

// Host -> everyone, broadcast once per run with the host's randomized per-run assignments.
// Carries only the subset UmadP3BlackHoleAi reads (see UmadP3BlackHoleState.FromNetworkReplay).
// ThunderSet1/ThunderSet2 are the exception: a peer under debug-bot control needs the host's
// REAL plan (not FromNetworkReplay's standing-default fallback) to self-apply the right kit.
public sealed record AiReplayStateMessage(
    PartyRole[] Roles, PartyRole[] StackTargets, uint[] SlapAttacks,
    float[] KefkaPositionRadians, uint ImplosionAttack,
    ThunderIIIAssignment ThunderSet1, ThunderIIIAssignment ThunderSet2) : MpMessage, IScenarioReplayStateMessage;

// P2 Forsaken's version of AiReplayStateMessage. Unlike P3, carries the state's entire public
// surface rather than a curated subset -- every field is a plain value, and P2 has 7 debug-bot
// Ai variants, so there's no single "what does the Ai read" answer to trim against.
public sealed record P2AiReplayStateMessage(
    EndAttack[] EndAttacks, float NewNorthRadians, int Rotation, Dictionary<PartyRole, uint> Lockons) : MpMessage, IScenarioReplayStateMessage;

// P2AiReplayStateMessage.Lockons is a one-time snapshot, but UmadP2ForsakenScenario
// .ReapplyLockons reassigns it dynamically as towers resolve (host-only). Without a way to
// re-sync it, a peer's stale replay lookup can miss and throw inside EventScheduler.Tick,
// permanently breaking every later scheduled move. Host -> everyone, sent whenever
// ReapplyLockons actually changes something.
public sealed record P2LockonsUpdateMessage(Dictionary<PartyRole, uint> Lockons) : MpMessage, IScenarioMidRunUpdateMessage;

// Host -> everyone, mirroring MapController.AddEffect/DirectorUpdate 1:1. Scenarios call these
// directly from their own event schedule (host-only) to drive native, client-local instance
// state (arena color/lighting, tower reveals, director flags) that's outside any SimObject and
// so outside the normal snapshot sync. Pure replays -- a peer never computes these itself.
public sealed record MapEffectMessage(uint PacketFlags, byte Index) : MpMessage, IHostOnlyMessage;
public sealed record MapDirectorUpdateMessage(
    uint Category, uint Arg1, uint Arg2, uint Arg3, uint Arg4, uint Arg5, uint Arg6) : MpMessage, IHostOnlyMessage;

// Pure replay of a host-side world.SetWeather call -- scenarios use this mid-fight for
// arena-transform lighting cues, not just initial spawn weather.
public sealed record SetWeatherMessage(byte WeatherId, float Transition) : MpMessage, IHostOnlyMessage;

// Carries only the subset UmadP4KefkaSaysAi reads (see UmadP4KefkaSaysState.FromNetworkReplay)
// -- MysteryCast reduced to its three scalar fields, ChaosMystery.Cast reconstructed from a
// fixed constant per element.
public sealed record P4AiReplayStateMessage(
    int[] MysteryBlizzardOffset, int[] MysteryLightningOffset, float[] MysteryLightningOrientation,
    bool Wave1First, PartyRole[] Wave1, bool Wave1True, PartyRole[] Wave2, bool Wave2True,
    bool InfernoIsTrue, bool TsunamiIsTrue, PartyRole[] Wave3, bool[] Wounds,
    bool Antilight0IsWhite, float NeoExdeathDirectionRadians) : MpMessage, IScenarioReplayStateMessage;

// UmadP5ExaflaresState's entire meaningful surface is LeftOrder/RightOrder -- Timeline/
// SpreadTick are plumbing the peer builds locally.
public sealed record P5AiReplayStateMessage(int[] LeftOrder, int[] RightOrder) : MpMessage, IScenarioReplayStateMessage;

// TopP6WaveCannon2Ai's entire read set is InFirst -- ProteanOrder/WildChargeTarget only feed
// the scenario's own (host-only) damage resolution.
public sealed record TopP6WaveCannon2AiReplayStateMessage(bool InFirst) : MpMessage, IScenarioReplayStateMessage;

// Carries the entire TopP2PartySynergyState surface -- the Ai reads all of it. GlitchType
// carries a Predicate<SimTether> (not JSON-friendly); both it and OmegaAttack are instead
// carried as a bool identifying which named static instance was chosen.
public sealed record TopP2PartySynergyAiReplayStateMessage(
    PartyRole[] Order, PartyRole[] Stacks, float NewNorthARadians, float NewNorthBRadians,
    float AttackDirRadians, bool GlitchIsFar, bool AttackMIsSword, bool AttackFIsStaff) : MpMessage, IScenarioReplayStateMessage;

// Carries the subset TopP5SigmaAi reads -- WaveCannonTargets/Towers aren't read, omitted.
// GlitchType/OmegaAttack/Rotation carried as bools identifying which named static instance
// was chosen (see TopP5SigmaState.FromNetworkReplay).
public sealed record TopP5SigmaAiReplayStateMessage(
    PartyRole[] Order, PartyRole[] DynamisTargets, PartyRole[] HelloWorldTargets, PartyRole[] HandBait,
    float NewNorthARadians, float NewNorthBRadians, bool TowerNorthFlipped, bool GlitchIsFar,
    bool SpinnerIsClockwise, bool OmegaFIsStaff, int FirstMissing, int SecondMissing) : MpMessage, IScenarioReplayStateMessage;

// Carries the subset TopP5OmegaAi reads. OmegaAttack has no delegate field, so it round-trips
// through JSON directly; MonitorSide is carried as a bool identifying which named static
// instance was chosen. MonitorTargets is the host's already-resolved pick (see
// TopP5OmegaState.MonitorTargets), not re-derived on a peer.
public sealed record TopP5OmegaAiReplayStateMessage(
    PartyRole[] HelloWorldTargets, PartyRole[] DoubleDynamicTargets, PartyRole[] MonitorTargets, float[] AttackDirectionsRadians,
    OmegaAttack[] OmegaAttacks, float BettleSpawnDirectionRadians, bool FirstWaveCannonFront,
    bool MonitorIsLeft) : MpMessage, IScenarioReplayStateMessage;

// HelloWorld2 carries no value at run start -- the host only resolves it live, at t=46s
// (a status-stack read + shuffle). Sent once, right then, same pattern as
// TopP5DeltaBeyondDefenseUpdateMessage.
public sealed record TopP5OmegaHelloWorld2UpdateMessage(PartyRole[] Roles) : MpMessage, IScenarioMidRunUpdateMessage;

// Carries the subset TopP5DeltaAi reads. Side/NorthSouth have private constructors (two named
// static instances each, no delegate), carried as bools. BeyondDefenseTarget isn't included --
// the host doesn't resolve it until t=35.3s, well after run start -- see
// TopP5DeltaBeyondDefenseUpdateMessage for the follow-up that carries it.
public sealed record TopP5DeltaAiReplayStateMessage(
    PartyRole[] TetherOrder, uint[] FistColors, int PlayerMonitorIndex, bool PlayerMonitorSideIsLeft,
    bool OmegaMonitorSideIsLeft, bool EyeSpawnIsNorth, bool SwivelCannonSideIsLeft, bool[] ArmHandednessIsLeft,
    PartyRole FarWorldRole, PartyRole NearWorldRole, int FarWorldTetherIndex) : MpMessage, IScenarioReplayStateMessage;

// BeyondDefenseTarget carries no value at run start -- the host only resolves it live, at
// t=35.3s (TopP5DeltaScenario.FireBeyondDefenseAoe). Sent once, right then, mirroring Umad P2
// Forsaken's P2LockonsUpdateMessage.
public sealed record TopP5DeltaBeyondDefenseUpdateMessage(PartyRole BeyondDefenseTarget) : MpMessage, IScenarioMidRunUpdateMessage;

// Carries the four boss placements, plus the three positions the Ai's live tie-break RNG
// resolved on the host (see ResolvedSafeCardinal) -- without those, a peer's replayed Ai would
// re-roll and could land on a different-but-valid safe spot than the host's bots.
public sealed record UltimatePredationAiReplayStateMessage(
    float GarudaX, float GarudaY, float GarudaZ, float GarudaRotation,
    float TitanX, float TitanY, float TitanZ, float TitanRotation,
    float IfritX, float IfritY, float IfritZ, float IfritRotation,
    float UltimaX, float UltimaY, float UltimaZ, float UltimaRotation,
    float SafeCardinalX, float SafeCardinalY, float SafeCardinalZ, float SafeCardinalRotation,
    float SafeFirstSetX, float SafeFirstSetY, float SafeFirstSetZ, float SafeFirstSetRotation,
    float SafeSecondSetX, float SafeSecondSetY, float SafeSecondSetZ, float SafeSecondSetRotation) : MpMessage, IScenarioReplayStateMessage;

// Peer -> host, event-driven. Reports which tracked mitigation status ids are active on the
// sender's own real character -- a real Rampart/invuln press never touches the host's puppet
// copy, so this is the only way DamageSolver can see it. SelfShieldFraction is a current-total
// snapshot (TankShieldTracker.SetFromPeerReport), not an incremental grant.
public sealed record SelfMitigationMessage(Guid PeerId, List<ushort> ActiveMitigationStatusIds, float SelfShieldFraction = 0f) : MpMessage;

// Peer -> host: reports enemies whose SourceSide mitigation (Reprisal) was applied locally --
// a peer's own enemy doppel is cosmetic-only, so this is how the host's authoritative enemy
// actually receives the debuff.
public sealed record PeerAppliedEnemyStatusMessage(Guid PeerId, List<int> EnemyNetIds, ushort StatusId, float Duration) : MpMessage;

// Peer -> host: the Party/Ally-scope counterpart to PeerAppliedEnemyStatusMessage above -- a
// party-wide/ally-targeted mitigation can touch roles other than the caster, whose puppets are
// cosmetic-only. Self-scope presses don't send this (SelfMitigationMessage covers those).
// ShieldFraction is the shield component of what was applied, 0f if none.
public sealed record PeerAppliedRoleStatusMessage(Guid PeerId, List<PartyRole> Roles, ushort StatusId, float Duration, float ShieldFraction = 0f) : MpMessage;
