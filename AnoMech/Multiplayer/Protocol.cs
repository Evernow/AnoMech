using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Multiplayer;

// Wire format for AnoMech's multiplayer relay (see Relay/README.md — the relay
// itself is a dumb per-session broadcaster with no knowledge of any of this).
// Position/rotation are always flattened to floats rather than reusing
// Vector3/Placement: System.Text.Json's reflection serializer doesn't handle
// Vector3's public fields (only properties) without extra converter plumbing,
// and flattening keeps every message a trivial value bag.
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
[JsonDerivedType(typeof(RoleKilledMessage), "killed")]
[JsonDerivedType(typeof(EndMessage), "end")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(PeerStatusMessage), "status")]
[JsonDerivedType(typeof(SessionEndedMessage), "sessionEnded")]
[JsonDerivedType(typeof(ResetRequestMessage), "resetRequest")]
[JsonDerivedType(typeof(LeaveRequestMessage), "leaveRequest")]
[JsonDerivedType(typeof(AiReplayStateMessage), "aiReplayState")]
public abstract record MpMessage;

// Peer -> host, sent once right after connecting so the host can register a
// display name for the lobby roster before any role is claimed. Version/
// Checksum identify the sender's exact plugin build (see PluginBuildInfo) so
// the host can catch a mismatch before it turns into a silent desync.
public sealed record HelloMessage(Guid PeerId, string DisplayName, string Version, string Checksum) : MpMessage;

// One peer's plugin build as declared in their Hello. Checksum is what
// actually gets compared (see MultiplayerManager.IsVersionMismatched);
// Version is only for the human-readable message.
public sealed record PeerBuildInfo(string Version, string Checksum)
{
    // Enough to eyeball-compare between two players without wrapping; the
    // full Checksum is still what mismatch detection actually compares.
    public string ShortChecksum => Checksum.Length >= 6 ? Checksum[..6] : Checksum;
}

// Host -> everyone, full-state (not delta) so a client that missed an update
// self-heals on the next broadcast instead of drifting from a merge bug.
public sealed record LobbyStateMessage(
    Guid HostId,
    Dictionary<PartyRole, Guid> ClaimedBy,
    Dictionary<Guid, string> Names,
    Dictionary<Guid, PeerBuildInfo> Builds,
    bool Started) : MpMessage;

public sealed record ClaimRoleMessage(Guid PeerId, PartyRole Role) : MpMessage;
public sealed record ReleaseRoleMessage(Guid PeerId) : MpMessage;

// Host -> everyone. Each client independently calls RunScenarioAsHost/AsPeer on
// receipt rather than the host driving remote starts directly -- there's no
// tight clock sync here, just "start now, roughly together"; host-authoritative
// mechanic resolution is what keeps everyone's *outcomes* consistent regardless
// of a few hundred ms of start skew (see MultiplayerManager).
public sealed record StartMessage : MpMessage;

// Host -> everyone, sent when the host clicks Start, *before* Session.Started
// is set or StartMessage goes out. Game.RunScenarioInternal hard-gates on
// ZoneSession.IsInInn() and silently no-ops (just a local log line) if it
// fails -- without this round-trip, a peer who isn't in an inn would just
// never enter the instance with no signal to the host beyond them eventually
// going "stale". Every claimed peer answers with StartCheckResponseMessage;
// the host only proceeds once everyone's confirmed (see MultiplayerManager).
public sealed record StartCheckMessage : MpMessage;

// Peer -> host, reply to StartCheckMessage. Reason is null when Ready is
// true; otherwise a short human-readable explanation ("not in an inn",
// "busy") surfaced verbatim in the host's "can't start" summary.
public sealed record StartCheckResponseMessage(Guid PeerId, bool Ready, string? Reason) : MpMessage;

// Peer -> host, sent at PoseSendRate for the sender's own real character. Host
// applies it to that peer's SimNetworkPuppet and republishes it as part of the
// next WorldSnapshot's Roles list (see MultiplayerManager.SampleAndBroadcast).
public sealed record SelfPoseMessage(Guid PeerId, float X, float Y, float Z, float Rotation) : MpMessage;

// One SimEnemy as host currently has it. NetId is a host-assigned, per-run
// stable id (not the game's own EntityId) used only to correlate this enemy
// across snapshot ticks. The spawn-config fields let a peer reconstruct the
// same doppel locally via world.SpawnEnemy on first sight of a NetId.
// ModelState mirrors SimNpc.SetModelState -- a scenario's mid-fight model-swap
// calls (Kefka's grow transformation, Omega-M's phase changes, etc.) are a
// native Timeline write with no other signal a peer could pick up; without
// this field a peer's doppel stays on its spawn-time model forever regardless
// of what the host's own model actually transforms into.
// Statuses mirrors SimCharacter.AddStatus/RemoveStatus -- direct StatusManager
// writes (see Statuses.Apply) that never go through a real server packet, so a
// peer's doppel has no other way to learn about them. This is how UMAD P3's
// "Max" status (506 stacks, applied purely to drive Kefka's VFX grow effect)
// reaches a peer -- without it Kefka renders on the peer at its un-grown base
// size despite Position/Visible/ModelState all matching the host correctly.
public sealed record EnemyStatusState(ushort StatusId, ushort Stacks);

public sealed record EnemyState(
    int NetId, uint BNpcBaseId, uint NameId, byte Level, bool Targetable,
    EnemyListMode EnemyList, uint ModelCharaId, float Scale, float HitboxRadius,
    byte? InitialModeAttributeFlags, bool Visible, byte ModelState,
    IReadOnlyList<EnemyStatusState> Statuses,
    float X, float Y, float Z, float Rotation,
    bool IsCasting, uint CastActionId);

// A GrabbyTether-style link. Each end resolves to either a live enemy (by
// NetId) or a party role; a peer reconstructs it locally via world.Tether so
// it gets the real VFX plumbing, not a fake beam.
public sealed record TetherState(int NetId, ushort TetherId, int? AEnemyNetId, PartyRole? ARole, int? BEnemyNetId, PartyRole? BRole);

public sealed record RoleState(PartyRole Role, bool Filled, bool Dead, float X, float Y, float Z, float Rotation);

// Host -> everyone, at SnapshotSendRate. Stateless/full-state by design (see
// LobbyStateMessage) -- a dropped frame just means one tick of staleness, not
// a permanently wrong reconstruction.
public sealed record WorldSnapshotMessage(List<EnemyState> Enemies, List<TetherState> Tethers, List<RoleState> Roles) : MpMessage;

// Host -> everyone, one per Game.PartyMemberKilled. A receiving client calls
// Game.Kill on whatever currently occupies that role locally -- their own real
// SimPlayer if it's their claimed role, otherwise that role's local puppet.
public sealed record RoleKilledMessage(PartyRole Role, string Cause) : MpMessage;

// Host -> everyone, sent whenever the host's own run ends for any reason
// (Reset, Leave, or the scenario finishing) -- ResetInternal clears
// ActiveScenario the same way regardless of which button triggered it, so
// MultiplayerManager.Tick detects all of them from that one place. Without
// this, a peer whose host left had no way to find out and just kept running
// their local copy of the zone forever.
//
// ReturnedToInn distinguishes Game.Reset() (despawns but deliberately stays
// in-zone, ready for a quick re-Start) from Game.Leave()/a natural finish
// (unloads back to the inn) -- both clear ActiveScenario identically, so the
// host reads World.Map.IsInInstance at broadcast time to tell them apart.
// Without this a peer always got hard-kicked to the inn even when the host
// only meant to reset in place.
public sealed record EndMessage(bool ReturnedToInn) : MpMessage;

// Host -> everyone, every PingIntervalSeconds -- decoupled from Started so
// connection-quality info is already live in the lobby before anyone clicks
// Start. SentAtMs is the host's own Environment.TickCount64; only the host
// ever compares it against a later reading of that same clock (on the
// matching Pong), so there's no cross-machine clock sync to worry about.
public sealed record PingMessage(long SentAtMs) : MpMessage;

// Peer -> host, sent immediately on receiving a PingMessage, echoing SentAtMs
// back unchanged so the host can compute round-trip time on its own clock.
public sealed record PongMessage(Guid PeerId, long SentAtMs) : MpMessage;

// One peer's connection quality as last measured by the host. LatencyMs is
// null until that peer's first Pong comes back.
public sealed record PeerStatusEntry(float? LatencyMs, float SecondsSinceLastSeen);

// Host -> everyone, alongside each PingMessage. Full-state (every claimed
// peer, not just whoever changed) so a peer's roster UI matches the host's
// without peers needing their own liveness bookkeeping.
public sealed record PeerStatusMessage(Dictionary<Guid, PeerStatusEntry> Statuses) : MpMessage;

// Sent by whoever clicks "Leave session" -- host or peer alike -- to every
// other client (as opposed to Reset or a natural scenario end, see
// EndMessage, which keep the session alive for a re-Start). Only ends the
// session for the whole group when the host is who left (every recipient
// reverts to the inn if mid-fight and fully disconnects too, rather than the
// group splintering into "some people still in, some out"); a departing peer
// instead just gets dropped from the roster (see MultiplayerManager.Dispatch)
// -- the rest of the group keeps going. PeerId is who left: how the recipient
// tells the two cases apart (PeerId == Session.HostId) and, for the
// session-ending case, who to name in the goodbye message.
public sealed record SessionEndedMessage(Guid PeerId) : MpMessage;

// Peer -> host: "please reset the encounter for the group." The peer doesn't
// reset itself directly -- the host resets its own authoritative run instead,
// which naturally reaches everyone (including the requester) via the
// existing EndMessage broadcast, so there's one single code path for "the
// run reset" regardless of who asked for it.
public sealed record ResetRequestMessage(Guid PeerId) : MpMessage;

// Peer -> host: "please end the run and send everyone back to the inn" --
// the ordinary Leave button (distinct from SessionEndedMessage/"Leave
// session", which disconnects the sender from the group entirely). The peer
// doesn't leave locally itself -- the host leaves its own authoritative run
// instead, which naturally reaches everyone (including the requester) via
// the existing EndMessage broadcast, same single-code-path reasoning as
// ResetRequestMessage. The session (roster, relay connection) is untouched;
// only the current run ends.
public sealed record LeaveRequestMessage(Guid PeerId) : MpMessage;

// Host -> everyone, broadcast once per run as soon as the host's own
// randomized per-run assignments are available after Start -- sent
// unconditionally, regardless of whether anyone's actually using debug-bot
// mode, so the host's own logic never needs to know or care who is. Carries
// only the subset UmadP3BlackHoleAi actually reads (see
// UmadP3BlackHoleState.FromNetworkReplay); everything else that state holds
// only feeds the scenario's own damage/VFX resolution, which peers never run
// regardless of debug-bot mode. A peer with debug-bot mode on uses this to
// locally replay the exact choreography a host-side bot in that role would
// produce, entirely client-side -- see MultiplayerManager.
public sealed record AiReplayStateMessage(
    PartyRole[] Roles, PartyRole[] StackTargets, uint[] SlapAttacks,
    float[] KefkaPositionRadians, uint ImplosionAttack) : MpMessage;
