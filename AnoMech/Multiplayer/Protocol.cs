using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios.Umad.P2Forsaken;

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
public abstract record MpMessage;

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
    int SelectedWaymark) : MpMessage;

public sealed record ClaimRoleMessage(Guid PeerId, PartyRole Role) : MpMessage;
public sealed record ReleaseRoleMessage(Guid PeerId) : MpMessage;

// Host -> everyone. Each client independently calls RunScenarioAsHost/AsPeer on receipt --
// no tight clock sync needed, since host-authoritative mechanic resolution is what keeps
// outcomes consistent regardless of a little start skew.
public sealed record StartMessage : MpMessage;

// Host -> everyone, sent before Session.Started/StartMessage. RunScenarioInternal silently
// no-ops if a client isn't in an inn -- this round-trip surfaces that to the host instead.
// Every claimed peer answers with StartCheckResponseMessage.
public sealed record StartCheckMessage : MpMessage;

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
    IReadOnlyList<EnemyStatusState> Statuses, ushort? AnimationTimelineId, IReadOnlyList<uint> NewLockonVfxIds,
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

// Statuses/NewLockonVfxIds mirror EnemyState, same reasoning, for a party-role member.
// Unlike position (self-authoritative for a peer's own claimed role), these are not
// self-authoritative even then -- a peer runs no scenario logic itself.
public sealed record RoleState(
    PartyRole Role, bool Filled, bool Dead, float X, float Y, float Z, float Rotation,
    IReadOnlyList<EnemyStatusState> Statuses, IReadOnlyList<uint> NewLockonVfxIds);

// One SimEventObject (or SimTower) as the host has it. Without this message type, event
// objects don't replicate at all -- the enemy sampler only ever walks SimEnemy.
public sealed record EventObjectState(
    int NetId, uint EObjId, ushort TimelineState, ushort CurrentState,
    float X, float Y, float Z, float Rotation);

// Host -> everyone, at SnapshotSendRate. Full-state, so a dropped frame is one tick of
// staleness, not a permanently wrong reconstruction.
public sealed record WorldSnapshotMessage(
    List<EnemyState> Enemies, List<TetherState> Tethers,
    List<EventObjectState> EventObjects) : MpMessage;

// Host -> everyone, paced independently of WorldSnapshotMessage (see RelayClient's priority
// queue) -- role positions are small and urgent, enemy data can be large.
public sealed record RolesSnapshotMessage(List<RoleState> Roles) : MpMessage;

// Host -> everyone, one per Game.PartyMemberKilled. The recipient calls Game.Kill on whatever
// currently occupies that role locally.
public sealed record RoleKilledMessage(PartyRole Role, string Cause) : MpMessage;

// Host -> everyone, sent whenever the host's run ends for any reason. ReturnedToInn
// distinguishes Reset() (stays in-zone) from Leave()/a natural finish (unloads to the inn) --
// both clear ActiveScenario identically, so the host reads World.Map.IsInInstance at
// broadcast time to tell them apart.
public sealed record EndMessage(bool ReturnedToInn) : MpMessage;

// Host -> everyone, every PingIntervalSeconds, decoupled from Started so connection quality
// is live in the lobby before Start. SentAtMs is the host's own clock; only the host ever
// compares it, so there's no cross-machine sync to worry about.
public sealed record PingMessage(long SentAtMs) : MpMessage;

public sealed record PongMessage(Guid PeerId, long SentAtMs) : MpMessage;

public sealed record PeerStatusEntry(float? LatencyMs, float SecondsSinceLastSeen);

public sealed record PeerStatusMessage(Dictionary<Guid, PeerStatusEntry> Statuses) : MpMessage;

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

// Host -> everyone, broadcast once per run once the host's randomized per-run assignments are
// available, sent unconditionally regardless of whether any peer is using debug-bot mode.
// Carries only the subset UmadP3BlackHoleAi actually reads (see
// UmadP3BlackHoleState.FromNetworkReplay) -- everything else only feeds damage/VFX resolution,
// which peers never run.
public sealed record AiReplayStateMessage(
    PartyRole[] Roles, PartyRole[] StackTargets, uint[] SlapAttacks,
    float[] KefkaPositionRadians, uint ImplosionAttack) : MpMessage;

// P2 Forsaken's version of AiReplayStateMessage. Unlike P3, carries the state's entire public
// surface rather than a curated subset -- every field is a plain value, and P2 has 7 debug-bot
// Ai variants, so there's no single "what does the Ai read" answer to trim against.
public sealed record P2AiReplayStateMessage(
    EndAttack[] EndAttacks, float NewNorthRadians, int Rotation, Dictionary<PartyRole, uint> Lockons) : MpMessage;

// P2AiReplayStateMessage.Lockons is a one-time snapshot, but UmadP2ForsakenScenario
// .ReapplyLockons reassigns it dynamically as towers resolve (host-only). Without a way to
// re-sync it, a peer's stale replay lookup can miss and throw inside EventScheduler.Tick,
// permanently breaking every later scheduled move. Host -> everyone, sent whenever
// ReapplyLockons actually changes something.
public sealed record P2LockonsUpdateMessage(Dictionary<PartyRole, uint> Lockons) : MpMessage;

// Host -> everyone, mirroring MapController.AddEffect/DirectorUpdate 1:1. Scenarios call these
// directly from their own event schedule (host-only) to drive native, client-local instance
// state (arena color/lighting, tower reveals, director flags) that's outside any SimObject and
// so outside the normal snapshot sync. Pure replays -- a peer never computes these itself.
public sealed record MapEffectMessage(uint PacketFlags, byte Index) : MpMessage;
public sealed record MapDirectorUpdateMessage(
    uint Category, uint Arg1, uint Arg2, uint Arg3, uint Arg4, uint Arg5, uint Arg6) : MpMessage;

// Pure replay of a host-side world.SetWeather call -- scenarios use this mid-fight for
// arena-transform lighting cues, not just initial spawn weather.
public sealed record SetWeatherMessage(byte WeatherId, float Transition) : MpMessage;

// P4 Kefka Says' version of AiReplayStateMessage. Carries only the subset UmadP4KefkaSaysAi
// reads (see UmadP4KefkaSaysState.FromNetworkReplay) -- MysteryCast reduced to its three
// scalar fields, ChaosMystery.Cast reconstructed from a fixed constant per element.
public sealed record P4AiReplayStateMessage(
    int[] MysteryBlizzardOffset, int[] MysteryLightningOffset, float[] MysteryLightningOrientation,
    bool Wave1First, PartyRole[] Wave1, bool Wave1True, PartyRole[] Wave2, bool Wave2True,
    bool InfernoIsTrue, bool TsunamiIsTrue, PartyRole[] Wave3, bool[] Wounds,
    bool Antilight0IsWhite, float NeoExdeathDirectionRadians) : MpMessage;

// P5 Exaflares' version of AiReplayStateMessage. UmadP5ExaflaresState's entire meaningful
// surface is LeftOrder/RightOrder -- Timeline/SpreadTick are plumbing the peer builds locally.
public sealed record P5AiReplayStateMessage(int[] LeftOrder, int[] RightOrder) : MpMessage;
