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
[JsonDerivedType(typeof(SelfPoseMessage), "pose")]
[JsonDerivedType(typeof(WorldSnapshotMessage), "snapshot")]
[JsonDerivedType(typeof(RoleKilledMessage), "killed")]
public abstract record MpMessage;

// Peer -> host, sent once right after connecting so the host can register a
// display name for the lobby roster before any role is claimed.
public sealed record HelloMessage(Guid PeerId, string DisplayName) : MpMessage;

// Host -> everyone, full-state (not delta) so a client that missed an update
// self-heals on the next broadcast instead of drifting from a merge bug.
public sealed record LobbyStateMessage(
    Guid HostId,
    Dictionary<PartyRole, Guid> ClaimedBy,
    Dictionary<Guid, string> Names,
    bool Started) : MpMessage;

public sealed record ClaimRoleMessage(Guid PeerId, PartyRole Role) : MpMessage;
public sealed record ReleaseRoleMessage(Guid PeerId) : MpMessage;

// Host -> everyone. Each client independently calls RunScenarioAsHost/AsPeer on
// receipt rather than the host driving remote starts directly -- there's no
// tight clock sync here, just "start now, roughly together"; host-authoritative
// mechanic resolution is what keeps everyone's *outcomes* consistent regardless
// of a few hundred ms of start skew (see MultiplayerManager).
public sealed record StartMessage : MpMessage;

// Peer -> host, sent at PoseSendRate for the sender's own real character. Host
// applies it to that peer's SimNetworkPuppet and republishes it as part of the
// next WorldSnapshot's Roles list (see MultiplayerManager.SampleAndBroadcast).
public sealed record SelfPoseMessage(Guid PeerId, float X, float Y, float Z, float Rotation) : MpMessage;

// One SimEnemy as host currently has it. NetId is a host-assigned, per-run
// stable id (not the game's own EntityId) used only to correlate this enemy
// across snapshot ticks. The spawn-config fields let a peer reconstruct the
// same doppel locally via world.SpawnEnemy on first sight of a NetId.
public sealed record EnemyState(
    int NetId, uint BNpcBaseId, uint NameId, byte Level, bool Targetable,
    EnemyListMode EnemyList, uint ModelCharaId, float Scale, float HitboxRadius,
    byte? InitialModeAttributeFlags, bool Visible,
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
