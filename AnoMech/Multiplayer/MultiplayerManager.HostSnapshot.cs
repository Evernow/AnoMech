using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AnoMech.Core;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Geometry;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Umad.P2Forsaken;
using AnoMech.Scenarios.Umad.P3BlackHole;
using AnoMech.Scenarios.Umad.P4KefkaSays;
using AnoMech.Scenarios.Umad.P5Exaflares;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Multiplayer;

public sealed partial class MultiplayerManager
{
    // ---- Host: sampling the live simulation --------------------------------

    // No throttle: Tick already runs at most once per Framework update, so any
    // interval below the actual frame time is a no-op, and every peer benefits
    // from a boss position/facing that's as fresh as the host's own frame rate
    // allows. Bandwidth/CPU scales with host FPS instead of being capped --
    // fine for a handful of peers.
    private void SampleAndBroadcastSnapshot()
    {
        var world = Plugin.GameInstance.World;

        // UMAD P2 only (harmless no-op elsewhere -- LastState is only non-null for
        // the scenario that's actually running). See P2LockonsUpdateMessage's doc
        // comment: UmadP2ForsakenScenario.ReapplyLockons reassigns state.Lockons
        // dynamically as towers resolve, host-only, so a peer's replay-start
        // snapshot of it goes stale the first time that happens. Re-broadcast
        // whenever it actually changes, keyed by a sorted string (Dictionary has
        // no value equality) so this doesn't spam a message every tick.
        if (Plugin.GameInstance.Scenarios[Session.ScenarioIndex] is UmadP2ForsakenScenario { LastState: { } p2State })
        {
            var lockonsKey = string.Join(",", p2State.Lockons.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));
            if (hostLastBroadcastP2Lockons != lockonsKey)
            {
                hostLastBroadcastP2Lockons = lockonsKey;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting P2 Lockons update -- [{lockonsKey}].");
                _ = relay!.SendAsync(new P2LockonsUpdateMessage(new Dictionary<PartyRole, uint>(p2State.Lockons)));
            }
        }

        var liveEnemies = world.Children.OfType<SimEnemy>().Where(e => e.IsActive).ToList();
        foreach (var stale in hostEnemyNetIds.Keys.Where(e => !liveEnemies.Contains(e)).ToList())
        {
            DiagnosticLog.Debug($"[Multiplayer] Host: enemy NetId {hostEnemyNetIds[stale]} ({stale.BNpcBaseId}) no longer active -- dropping from broadcast.");
            hostEnemyNetIds.Remove(stale);
            hostEnemyLastLoggedModelState.Remove(stale);
            hostEnemyLastLoggedStatuses.Remove(stale);
            hostEnemyLastLoggedAnimationTimeline.Remove(stale);
        }

        var enemies = new List<EnemyState>(liveEnemies.Count);
        foreach (var enemy in liveEnemies)
        {
            if (!hostEnemyNetIds.TryGetValue(enemy, out var netId))
            {
                netId = nextEnemyNetId++;
                hostEnemyNetIds[enemy] = netId;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting new enemy NetId {netId} -- BNpcBase {enemy.BNpcBaseId}, pos {enemy.Position}, visible {enemy.Visible}.");
            }
            var cfg = enemy.SpawnConfig;
            var modelState = enemy.ModelState;
            if (!hostEnemyLastLoggedModelState.TryGetValue(enemy, out var lastLogged) || lastLogged != modelState)
            {
                hostEnemyLastLoggedModelState[enemy] = modelState;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) ModelState -> 0x{modelState:X2}.");
            }
            var statusSnapshot = enemy.ActiveStatusSnapshot;
            var statusKey = string.Join(",", statusSnapshot.Select(s => $"{s.StatusId}:{s.Stacks}"));
            if (!hostEnemyLastLoggedStatuses.TryGetValue(enemy, out var lastStatusKey) || lastStatusKey != statusKey)
            {
                hostEnemyLastLoggedStatuses[enemy] = statusKey;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) statuses -> [{statusKey}].");
            }
            if (enemy.AnimationTimelineId is { } timelineId
                && (!hostEnemyLastLoggedAnimationTimeline.TryGetValue(enemy, out var lastTimeline) || lastTimeline != timelineId))
            {
                hostEnemyLastLoggedAnimationTimeline[enemy] = timelineId;
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) AnimationTimelineId -> 0x{timelineId:X4}.");
            }
            var newLockonVfxIds = enemy.DrainPendingLockonVfxIds();
            if (newLockonVfxIds.Count > 0)
                DiagnosticLog.Info($"[Multiplayer] Host: enemy NetId {netId} (BNpcBase {enemy.BNpcBaseId}) NewLockonVfxIds -> [{string.Join(",", newLockonVfxIds)}].");
            var (castTargetEnemyNetId, castTargetRole) = ResolveTargetId(world, enemy.CastTargetId);
            var (instantTargetEnemyNetId, instantTargetRole) = ResolveTargetId(world, enemy.LastInstantCastTargetId);
            enemies.Add(new EnemyState(
                netId, enemy.BNpcBaseId, cfg.NameId, cfg.Level, cfg.Targetable, enemy.EnemyListMode,
                cfg.ModelCharaId, cfg.Scale, cfg.HitboxRadius, cfg.InitialModeAttributeFlags, enemy.Visible, modelState,
                statusSnapshot.Select(s => new EnemyStatusState(s.StatusId, s.Stacks, s.RemainingTime)).ToList(),
                enemy.AnimationTimelineId, newLockonVfxIds,
                enemy.Position.X, enemy.Position.Y, enemy.Position.Z, enemy.Rotation,
                enemy.IsCasting, enemy.CastSeq, enemy.CastActionId, enemy.CastTotalSeconds, enemy.CastOmenDelay,
                enemy.CastTargetLocation?.X, enemy.CastTargetLocation?.Y, enemy.CastTargetLocation?.Z,
                castTargetEnemyNetId, castTargetRole,
                enemy.LastInstantCastSeq, enemy.LastInstantCastActionId,
                enemy.LastInstantCastTargetLocation?.X, enemy.LastInstantCastTargetLocation?.Y, enemy.LastInstantCastTargetLocation?.Z,
                instantTargetEnemyNetId, instantTargetRole));
        }

        var liveTethers = world.Children.OfType<SimTether>().Where(t => t.IsActive).ToList();
        foreach (var stale in hostTetherNetIds.Keys.Where(t => !liveTethers.Contains(t)).ToList())
        {
            DiagnosticLog.Debug($"[Multiplayer] Host: tether NetId {hostTetherNetIds[stale]} no longer active -- dropping from broadcast.");
            hostTetherNetIds.Remove(stale);
        }

        var tethers = new List<TetherState>(liveTethers.Count);
        foreach (var tether in liveTethers)
        {
            var (aEnemy, aRole) = ResolveEnd(world, tether.A);
            var (bEnemy, bRole) = ResolveEnd(world, tether.B);
            if (!hostTetherNetIds.TryGetValue(tether, out var netId))
            {
                netId = nextTetherNetId++;
                hostTetherNetIds[tether] = netId;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting new tether NetId {netId} (TetherId {tether.TetherId}) -- A={(aEnemy is { } ae ? $"enemy#{ae}" : aRole?.ToString() ?? "null")}, B={(bEnemy is { } be ? $"enemy#{be}" : bRole?.ToString() ?? "null")}.");
            }
            tethers.Add(new TetherState(netId, tether.TetherId, aEnemy, aRole, bEnemy, bRole));
        }

        var roles = new List<RoleState>(8);
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            var member = world.Party.Get(role);
            var dead = member is ISimPartyMember { Dead: true };
            IReadOnlyList<EnemyStatusState> statuses = [];
            IReadOnlyList<uint> newLockonVfxIds = [];
            if (member != null)
            {
                var statusSnapshot = member.ActiveStatusSnapshot;
                var statusKey = string.Join(",", statusSnapshot.Select(s => $"{s.StatusId}:{s.Stacks}"));
                if (!hostRoleLastLoggedStatuses.TryGetValue(role, out var lastStatusKey) || lastStatusKey != statusKey)
                {
                    hostRoleLastLoggedStatuses[role] = statusKey;
                    DiagnosticLog.Info($"[Multiplayer] Host: role {role} statuses -> [{statusKey}].");
                }
                newLockonVfxIds = member.DrainPendingLockonVfxIds();
                if (newLockonVfxIds.Count > 0)
                    DiagnosticLog.Info($"[Multiplayer] Host: role {role} NewLockonVfxIds -> [{string.Join(",", newLockonVfxIds)}].");
                statuses = statusSnapshot.Select(s => new EnemyStatusState(s.StatusId, s.Stacks, s.RemainingTime)).ToList();
            }
            else
            {
                hostRoleLastLoggedStatuses.Remove(role);
            }
            roles.Add(new RoleState(role, member != null, dead,
                member?.Position.X ?? 0f, member?.Position.Y ?? 0f, member?.Position.Z ?? 0f, member?.Rotation ?? 0f,
                statuses, newLockonVfxIds));
        }

        var liveEventObjects = world.Children.OfType<SimEventObject>().Where(o => o.IsActive).ToList();
        foreach (var stale in hostEventObjectNetIds.Keys.Where(o => !liveEventObjects.Contains(o)).ToList())
        {
            DiagnosticLog.Debug($"[Multiplayer] Host: event object NetId {hostEventObjectNetIds[stale]} (EObj 0x{stale.EObjRowId:X}) no longer active -- dropping from broadcast.");
            hostEventObjectNetIds.Remove(stale);
        }

        var eventObjects = new List<EventObjectState>(liveEventObjects.Count);
        foreach (var eo in liveEventObjects)
        {
            if (!hostEventObjectNetIds.TryGetValue(eo, out var netId))
            {
                netId = nextEventObjectNetId++;
                hostEventObjectNetIds[eo] = netId;
                DiagnosticLog.Info($"[Multiplayer] Host: broadcasting new event object NetId {netId} -- EObj 0x{eo.EObjRowId:X}, pos {eo.Position}, state {eo.CurrentState}.");
            }
            eventObjects.Add(new EventObjectState(
                netId, eo.EObjRowId, eo.VisibleState, eo.CurrentState,
                eo.Position.X, eo.Position.Y, eo.Position.Z, eo.Rotation));
        }

        _ = relay!.SendAsync(new WorldSnapshotMessage(enemies, tethers, roles, eventObjects));
    }

    private (int? enemyNetId, PartyRole? role) ResolveEnd(SimWorld world, SimCharacter? c)
    {
        if (c is null) return (null, null);
        if (c is SimEnemy e) return hostEnemyNetIds.TryGetValue(e, out var id) ? (id, null) : (null, null);
        foreach (var role in Enum.GetValues<PartyRole>())
            if (ReferenceEquals(world.Party.Get(role), c)) return (null, role);
        return (null, null);
    }

    // Same job as ResolveEnd, but for a Cast() target: SimCast only ever stores the raw
    // GameObjectId it was given (see SimCast.TargetId's doc comment for why that number
    // means nothing to a peer on its own), so this resolves by ID equality against the
    // host's own local party/enemy set instead of ResolveEnd's reference equality.
    private (int? enemyNetId, PartyRole? role) ResolveTargetId(SimWorld world, GameObjectId? targetId)
    {
        if (targetId is not { } id) return (null, null);
        foreach (var role in Enum.GetValues<PartyRole>())
            if (world.Party.Get(role)?.GameObjectId == id) return (null, role);
        foreach (var (enemy, netId) in hostEnemyNetIds)
            if (enemy.GameObjectId == id) return (netId, null);
        return (null, null);
    }

    private void OnPartyMemberKilledHost(PartyRole role, string cause)
        => _ = relay?.SendAsync(new RoleKilledMessage(role, cause));

    // True once a claimed peer hasn't been heard from for PeerStaleTimeoutMs.
    // Host reads its own ground-truth peerLastSeenMs; a peer reads the same
    // number as last relayed by the host (PeerStatusMessage) -- either way
    // this surfaces in MultiplayerWindow so a dropped connection shows as
    // something other than "their puppet just stopped moving."
    public bool IsPeerStale(Guid peerId) => IsHost
        ? peerLastSeenMs.TryGetValue(peerId, out var lastSeen) && Environment.TickCount64 - lastSeen > PeerStaleTimeoutMs
        : peerStatuses.TryGetValue(peerId, out var entry) && entry.SecondsSinceLastSeen * 1000f > PeerStaleTimeoutMs;

    // Host-only, every PingIntervalSeconds regardless of running: pings every
    // claimed peer, rebuilds the display-ready status snapshot from whatever
    // was last measured (a full cycle behind the very latest Pong, which is
    // fine for a coarse indicator), and broadcasts it so peers' rosters match.
    private void SendPingAndRefreshStatuses()
    {
        var nowMs = Environment.TickCount64;
        _ = relay!.SendAsync(new PingMessage(nowMs));

        peerStatuses.Clear();
        foreach (var peerId in Session.ClaimedBy.Values.Distinct())
        {
            if (peerId == MyPeerId || !peerLastSeenMs.TryGetValue(peerId, out var lastSeen)) continue;
            var latency = peerLatencyMs.TryGetValue(peerId, out var ms) ? ms : (float?)null;
            peerStatuses[peerId] = new PeerStatusEntry(latency, (nowMs - lastSeen) / 1000f);
        }
        _ = relay.SendAsync(new PeerStatusMessage(new Dictionary<Guid, PeerStatusEntry>(peerStatuses)));

        CheckPeerLiveness();
    }

    private void CheckPeerLiveness()
    {
        foreach (var (role, peerId) in Session.ClaimedBy)
        {
            if (peerId == MyPeerId) continue;
            var stale = IsPeerStale(peerId);
            if (stale && warnedStalePeers.Add(peerId))
            {
                DiagnosticLog.Warn($"[Multiplayer] {Session.NameOf(peerId)} ({role}) hasn't reported in over {PeerStaleTimeoutMs / 1000}s -- likely disconnected.");
                // Mid-fight, a silently-vanished party member dooms the run the same
                // way an explicit "Leave session" click does (see RemovePeer's mid-
                // fight branch) -- but unlike that click, going stale isn't
                // necessarily permanent (a network blip, not a deliberate leave), so
                // only end the run here; their role claim and roster slot are left
                // alone so a reconnect (their own client already retries the same
                // relay/session automatically -- see BeginReconnect) drops them back
                // into a normal lobby instead of finding their role already handed
                // to someone else. IsInInstance guard: Leave() -> Unload() assumes a
                // zone was actually entered, same reasoning as everywhere else this
                // guard appears in this file.
                if (running && Plugin.GameInstance.World.Map.IsInInstance)
                {
                    DiagnosticLog.Info($"[Multiplayer] Ending the run because {Session.NameOf(peerId)} went stale mid-fight.");
                    Plugin.GameInstance.Leave();
                }
            }
            else if (!stale && warnedStalePeers.Remove(peerId))
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(peerId)} ({role}) is reporting in again.");
        }
    }

}
