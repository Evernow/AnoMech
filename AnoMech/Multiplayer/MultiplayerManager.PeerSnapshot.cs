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
    // ---- Peer: reporting our own pose --------------------------------------

    // No throttle, same reasoning as SampleAndBroadcastSnapshot: this feeds
    // the host's belief about where a real player is (mechanic checks, other
    // peers' view of them), and it's one GUID + 4 floats, cheap enough to send
    // every Tick regardless of peer count.
    private void SendSelfPose()
    {
        var player = Plugin.GameInstance.World.Party.Player;
        if (player == null) return;
        _ = relay!.SendAsync(new SelfPoseMessage(MyPeerId, player.Position.X, player.Position.Y, player.Position.Z, player.Rotation));
    }

    // ---- Host: applying a peer's reported pose to their puppet -------------

    private void OnSelfPoseReceived(SelfPoseMessage msg)
    {
        if (!IsHost) return;
        if (Session.RoleOf(msg.PeerId) is not { } role)
        {
            DiagnosticLog.Debug($"[Multiplayer] SelfPose from {msg.PeerId} but they hold no claimed role -- dropping.");
            return;
        }
        if (Plugin.GameInstance.World.Party.Get(role) is SimNetworkPuppet puppet)
            puppet.ApplyNetworkPose(new Vector3(msg.X, msg.Y, msg.Z), msg.Rotation);
        else
            DiagnosticLog.Debug($"[Multiplayer] SelfPose from {Session.NameOf(msg.PeerId)} ({role}) but that slot isn't a SimNetworkPuppet -- dropping.");
    }

    // ---- Peer: applying a world snapshot ------------------------------------

    private void OnWorldSnapshotReceived(WorldSnapshotMessage snap)
    {
        if (IsHost) return;
        // Gated on peerEnteredInstance for the same reason TryStartDebugBotReplay
        // already is: RunScenarioAsPeer's real zone entry (MapController.TryLoad)
        // is deferred, so a snapshot can arrive and spawn enemies into
        // Plugin.GameInstance.World before that load has actually happened -- and
        // the load that follows moments later tears the zone down and rebuilds it,
        // destroying those doppels out from under peerEnemies without clearing its
        // bookkeeping. Every NetId is now considered "already spawned" (it's a key
        // in peerEnemies), so the "first snapshot -- spawning local doppel" branch
        // below never fires for them again for the rest of the run: the enemies
        // are gone from the peer's world permanently. Confirmed via an
        // AnoMech-DamageDebug dump (UMAD P4 Kefka Says, three-person session): the
        // three bosses spawned once from a snapshot that arrived 16ms before
        // "TryLoad: freshLoad=True", then the peer's dump over a minute later
        // showed "Enemies currently in world: (none)" for the entire rest of the
        // fight. Snapshots arrive continuously (multiple times a second), so
        // dropping the ones that land in this narrow pre-load window costs nothing
        // -- the very next one after zone entry completes finds peerEnemies still
        // empty and spawns everything fresh, into the world that's actually going
        // to stick around.
        if (!peerEnteredInstance) return;
        var world = Plugin.GameInstance.World;

        var seenEnemyIds = new HashSet<int>();
        foreach (var e in snap.Enemies)
        {
            seenEnemyIds.Add(e.NetId);
            if (!peerEnemies.TryGetValue(e.NetId, out var enemy))
            {
                var config = new EnemySpawnConfig(
                    e.BNpcBaseId, e.NameId, e.Level, e.Targetable, e.EnemyList, e.Visible,
                    new Placement(new Vector3(e.X, e.Y, e.Z), e.Rotation),
                    e.ModelCharaId, e.Scale, e.HitboxRadius, e.InitialModeAttributeFlags);
                DiagnosticLog.Info($"[Multiplayer] Peer: first snapshot of enemy NetId {e.NetId} -- BNpcBase {e.BNpcBaseId}, pos ({e.X:F2},{e.Y:F2},{e.Z:F2}), rot {e.Rotation:F2}, visible {e.Visible} -- spawning local doppel.");
                enemy = world.SpawnEnemy(config);
                if (enemy == null)
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SpawnEnemy returned null for NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) -- skipping this enemy.");
                    continue;
                }
                peerEnemies[e.NetId] = enemy;
            }
            // Smoothed in Tick rather than teleported here -- see SimEnemy.
            // ApplyNetworkPosition/TickNetworkPosition. A hard SetPosition every
            // snapshot made boss movement visibly stutter for peers; the first
            // snapshot's "spawn" branch above already places the doppel
            // exactly here via EnemySpawnConfig.Placement, so this call is a no-op
            // distance-wise on that first tick.
            enemy.ApplyNetworkPosition(new Vector3(e.X, e.Y, e.Z), e.Rotation);
            enemy.SetVisible(e.Visible);
            // Re-issued only on an actual change -- SetModelState's native rebuild
            // briefly disables/re-enables drawing, so calling it every snapshot
            // even when unchanged would flicker the model. A scenario's
            // mid-fight SetModelState calls (Kefka's grow transformation, Omega-M's
            // phase swaps, etc.) are otherwise a purely local Timeline write the
            // host never has any other reason to tell peers about -- without this a
            // peer's doppel just stays on whatever model it first spawned with.
            if (!peerEnemyModelState.TryGetValue(e.NetId, out var lastModelState) || lastModelState != e.ModelState)
            {
                peerEnemyModelState[e.NetId] = e.ModelState;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) ModelState -> 0x{e.ModelState:X2}.");
                enemy.SetModelState(e.ModelState);
            }
            // Reconciled against the host's set every snapshot -- AddStatus/RemoveStatus
            // are cheap direct StatusManager writes (no model rebuild involved), unlike
            // ModelState above, so there's no need to gate the actual application on a
            // change; only the log line below is edge-triggered. Without this, a
            // scenario's stack-based statuses (e.g. UMAD P3's "Max" grow status) never
            // reach a peer's doppel at all -- it stays Position/Visible/ModelState-correct
            // but visually un-grown.
            var currentStatuses = enemy.ActiveStatusSnapshot;
            foreach (var target in e.Statuses)
            {
                if (currentStatuses.Any(s => s.StatusId == target.StatusId && s.Stacks == target.Stacks)) continue;
                enemy.AddStatus(target.StatusId, duration: target.RemainingTime, stacks: target.Stacks, overrideStacks: true);
            }
            foreach (var current in currentStatuses)
            {
                if (e.Statuses.Any(s => s.StatusId == current.StatusId)) continue;
                enemy.RemoveStatus(current.StatusId);
            }
            var statusKey = string.Join(",", e.Statuses.Select(s => $"{s.StatusId}:{s.Stacks}"));
            if (!peerEnemyLastLoggedStatuses.TryGetValue(e.NetId, out var lastStatusKey) || lastStatusKey != statusKey)
            {
                peerEnemyLastLoggedStatuses[e.NetId] = statusKey;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) statuses -> [{statusKey}].");
            }
            // Rising-edge trigger: reuses the real SimCast pipeline (cast bar +
            // omen VFX) rather than faking either, so timing/placement come from
            // the same code path solo play already exercises. targetLocation is
            // threaded through for ground-targeted casts (e.g. BlizzardIII spread
            // markers) -- omitting it would anchor the telegraph at the caster's
            // own position instead of the intended ground spot (see NativeCast).
            // castSeconds is threaded through for the same reason: leaving it null
            // makes SimCast.Start fall back to a Lumina sheet lookup, which for a
            // scenario's synthetic helper-enemy action IDs either resolves to a
            // duration that has nothing to do with what the host scripted (the
            // telegraph runs too short, out of sync with the host's real damage
            // timing) or isn't in the sheet at all (Start() logs a warning and the
            // cast never begins on the peer -- the animation silently never plays).
            // omenDelay is threaded through too -- left at its 0f default, a cast
            // like Damning Edict (scripted with omenDelay: 4.1f so its telegraph
            // only shows for the last ~0.9s of a 5s cast) would instead show that
            // telegraph for the entire cast on a peer's screen.
            // targetId is resolved via ResolvePeerEnd (same helper tethers already
            // use) from the host's role/enemy-NetId translation of its own raw
            // GameObjectId -- see SimCast.TargetId's doc comment for why the ID
            // itself can't just cross the network. Omitting it entirely (as the
            // original TargetLocation-only fix did) left entity-targeted casts like
            // UMAD P3's Thunder III tankbuster with no target at all on a peer, so
            // NativeActionEffect's NumTargets went to 0 and the hit-react animation
            // that's supposed to play on the tank being hit never showed up there.
            // Dedupes off CastSeq actually changing rather than off IsCasting's rising
            // edge (host casting && my own doppel isn't) -- see EnemyState.CastSeq's
            // doc comment for why comparing the host's real cast timer against the
            // peer's own independently-running replayed one let the exact same cast
            // replay twice. Guarded on seq > 0 for the same first-connect reason as
            // LastInstantCastSeq below (don't replay the zero-value default).
            if (e.CastSeq > 0
                && (!peerEnemyLastCastSeq.TryGetValue(e.NetId, out var lastCastSeq) || lastCastSeq != e.CastSeq))
            {
                peerEnemyLastCastSeq[e.NetId] = e.CastSeq;
                // Cast()'s omen/telegraph placement (and its own log line) both read
                // Position/Rotation directly, but ApplyNetworkPosition above only
                // recorded this snapshot's pose as an interpolation target -- the
                // fields themselves only catch up gradually, once per frame, in
                // TickNetworkPosition. A boss that repositions/reorients and casts in
                // the same host tick (e.g. UMAD P3's Black Hole Face()-then-Cast) would
                // otherwise have its replayed telegraph drawn from wherever the peer's
                // doppel was still interpolating from -- see the LastInstantCastSeq
                // branch below for the observed symptom. Snap to the snapshot's
                // authoritative pose right before replaying so the telegraph always
                // matches what the host actually cast, at the cost of skipping one
                // frame of smoothing exactly when a cast fires.
                enemy.SetPosition(new Placement(new Vector3(e.X, e.Y, e.Z), e.Rotation));
                var targetLocation = e.CastTargetX is { } tx && e.CastTargetY is { } ty && e.CastTargetZ is { } tz
                    ? new Vector3(tx, ty, tz)
                    : (Vector3?)null;
                var targetId = ResolvePeerEnd(world, e.CastTargetEnemyNetId, e.CastTargetRole)?.GameObjectId;
                enemy.Cast(e.CastActionId, targetLocation: targetLocation, castSeconds: e.CastSeconds, omenDelay: e.CastOmenDelay, targetId: targetId);
            }
            // Edge-triggered on a monotonic counter rather than IsCasting's rising
            // edge -- an instant cast (e.g. Nothingness) never makes IsCasting go
            // true at all (see SimCast.LastInstantCastSeq's doc comment), so this is
            // the only signal a peer has that one happened. Guarded on seq > 0 so a
            // peer that just connected doesn't replay the zero-value default the
            // instant it sees its first snapshot for this enemy.
            if (e.LastInstantCastSeq > 0
                && (!peerEnemyLastInstantCastSeq.TryGetValue(e.NetId, out var lastInstantSeq) || lastInstantSeq != e.LastInstantCastSeq))
            {
                peerEnemyLastInstantCastSeq[e.NetId] = e.LastInstantCastSeq;
                // Same stale-pose race as the rising-edge branch above, but far more
                // visible here: this is exactly the path UMAD P3's Black Hole uses
                // (Face(tether) 0.1s before an instant Cast(Nothingness), a rect 125x6
                // line AOE) -- a guest's doppel hadn't finished rotating to the new
                // tether target yet when the cast replayed, so Nothingness fired along
                // the doppel's old facing instead, observed as a line AOE seemingly
                // cutting through the arena center that the host never actually cast
                // that way.
                enemy.SetPosition(new Placement(new Vector3(e.X, e.Y, e.Z), e.Rotation));
                var instantTargetLocation = e.LastInstantCastTargetX is { } itx && e.LastInstantCastTargetY is { } ity && e.LastInstantCastTargetZ is { } itz
                    ? new Vector3(itx, ity, itz)
                    : (Vector3?)null;
                var instantTargetId = ResolvePeerEnd(world, e.LastInstantCastTargetEnemyNetId, e.LastInstantCastTargetRole)?.GameObjectId;
                enemy.Cast(e.LastInstantCastActionId, targetLocation: instantTargetLocation, castSeconds: 0f, targetId: instantTargetId);
            }
            // Edge-triggered like ModelState -- PlayAnimationTimeline/AttachLockonVfx
            // are one-shot cues, so re-issuing them every snapshot even when
            // unchanged would restart the same animation/VFX on a loop.
            if (e.AnimationTimelineId is { } timelineId
                && (!peerEnemyAnimationTimeline.TryGetValue(e.NetId, out var lastTimeline) || lastTimeline != timelineId))
            {
                peerEnemyAnimationTimeline[e.NetId] = timelineId;
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) AnimationTimelineId -> 0x{timelineId:X4}.");
                enemy.PlayAnimationTimeline(timelineId);
            }
            if (e.NewLockonVfxIds.Count > 0)
            {
                DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {e.NetId} (BNpcBase {e.BNpcBaseId}) NewLockonVfxIds -> [{string.Join(",", e.NewLockonVfxIds)}].");
                foreach (var lockonId in e.NewLockonVfxIds)
                    enemy.AttachLockonVfx(lockonId, persistent: false);
            }
        }
        foreach (var staleId in peerEnemies.Keys.Where(id => !seenEnemyIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] Peer: enemy NetId {staleId} no longer in snapshot -- despawning local doppel.");
            peerEnemies[staleId].Despawn();
            peerEnemies.Remove(staleId);
            peerEnemyModelState.Remove(staleId);
            peerEnemyLastLoggedStatuses.Remove(staleId);
            peerEnemyAnimationTimeline.Remove(staleId);
            peerEnemyLastInstantCastSeq.Remove(staleId);
            peerEnemyLastCastSeq.Remove(staleId);
        }

        // UMAD P3 only (harmless no-op elsewhere -- no enemy will ever match
        // BlackHole's BNpcBaseId in another scenario). A peer never runs the
        // scenario's own Run_BlackHoleObstacles, so without this a debug-bot
        // peer's own MoveTo pathing has no avoidance data and can cut straight
        // through a black hole's damage radius mid-transit -- which the host
        // would then apply DamageDown for, since the peer's own reported
        // position is self-authoritative. Rebuilt from the already-synced
        // peerEnemies rather than replaying the scenario's RNG state, so it
        // can never drift from whatever the host is actually showing.
        world.Obstacles.Clear();
        // Diagnostic-only, paired with UmadP3BlackHoleScenario.Tick's own "Near black
        // hole" log: that one is the host's belief (built off this peer's last
        // self-reported pose, via SimNetworkPuppet.Position); this is the peer's own,
        // true local position at the same real moment. If a future DamageDown dump
        // shows the host's line but not this one at a comparable timestamp, the pose
        // report was stale when it mattered -- if both show it, the peer's own
        // pathing genuinely cut it close. localPlayer is null on the host (it drives
        // its own bots directly, no self-pose loop), so this is peer-only already.
        var localPlayer = Plugin.GameInstance.World.Party.Player;
        foreach (var (netId, bh) in peerEnemies.Where(kvp => kvp.Value.BNpcBaseId == BNpcBaseId.BlackHole))
        {
            world.Obstacles.Add(new CircleObstacle(new Vector2(bh.Position.X, bh.Position.Z), UmadP3BlackHoleScenario.BlackHoleAvoidRadius));
            if (localPlayer is null) continue;
            var distSq = localPlayer.Placement().DistanceSq(bh.Position);
            if (distSq < UmadP3BlackHoleScenario.NearBlackHoleLogRadius * UmadP3BlackHoleScenario.NearBlackHoleLogRadius)
                DiagnosticLog.Info(
                    $"[Multiplayer] Peer: local position ({localPlayer.Position.X:F2},{localPlayer.Position.Z:F2}) is {MathF.Sqrt(distSq):F2}y from black hole NetId {netId} at ({bh.Position.X:F2},{bh.Position.Z:F2}).");
        }

        // Debug-bot replay: Chaos/Exdeath might not have been replicated yet
        // when TryStartDebugBotReplay first resolved them (WorldSnapshot and
        // AiReplayStateMessage are independent flows) -- keep retrying here on
        // every snapshot until each is found, cheap once it's no longer null.
        if (debugShadowState is { } shadow)
        {
            shadow.ScenarioObjects.Chaos ??= peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.ChaosP3);
            shadow.ScenarioObjects.Exdeath ??= peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.Exdeath);
        }

        var seenTetherIds = new HashSet<int>();
        foreach (var t in snap.Tethers)
        {
            seenTetherIds.Add(t.NetId);
            var a = ResolvePeerEnd(world, t.AEnemyNetId, t.ARole);
            var b = ResolvePeerEnd(world, t.BEnemyNetId, t.BRole);
            if (a == null && b == null) continue;
            // Re-create on any endpoint change (e.g. a grabby tether's B going from
            // unattached to a role once someone actually grabs it) -- SimTether's
            // endpoints are fixed at construction, so there's no in-place update.
            // Without this, a peer's local tether object -- and anything reading it,
            // like UmadP3BlackHoleAi.PullTether's ReferenceEquals(t.B, player) check
            // -- stays frozen at whatever endpoints were true the instant this NetId
            // was first seen, for the rest of the run.
            var aDesc = t.AEnemyNetId is { } aId ? $"enemy#{aId}" : t.ARole?.ToString() ?? "null";
            var bDesc = t.BEnemyNetId is { } bId ? $"enemy#{bId}" : t.BRole?.ToString() ?? "null";
            if (peerTethers.TryGetValue(t.NetId, out var existing))
            {
                if (ReferenceEquals(existing.A, a) && ReferenceEquals(existing.B, b)) continue;
                DiagnosticLog.Info($"[Multiplayer] Peer: tether NetId {t.NetId} endpoint changed -- recreating (A={aDesc}, B={bDesc}).");
                existing.Despawn();
            }
            else
            {
                DiagnosticLog.Info($"[Multiplayer] Peer: first snapshot of tether NetId {t.NetId} (TetherId {t.TetherId}) -- A={aDesc}, B={bDesc}.");
            }
            peerTethers[t.NetId] = world.Tether(a, b, t.TetherId);
        }
        foreach (var staleId in peerTethers.Keys.Where(id => !seenTetherIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] Peer: tether NetId {staleId} no longer in snapshot -- despawning.");
            peerTethers[staleId].Despawn();
            peerTethers.Remove(staleId);
        }

        var myRole = MyClaimedRole;
        foreach (var r in snap.Roles)
        {
            // Position is self-authoritative for our own claimed role (a real
            // SimPlayer reports its own pose via SelfPoseMessage, never network-driven
            // here) -- but statuses/lockon VFX are NOT: we never run any scenario
            // logic ourselves, so nothing else would ever call AddStatus/
            // AttachLockonVfx against our own real character. Reconcile those for
            // every role, including our own.
            if (r.Role != myRole && world.Party.Get(r.Role) is SimNetworkPuppet puppet)
                puppet.ApplyNetworkPose(new Vector3(r.X, r.Y, r.Z), r.Rotation);

            if (world.Party.Get(r.Role) is not { } member) continue;
            var currentStatuses = member.ActiveStatusSnapshot;
            if (!peerRoleReconciledStatusIds.TryGetValue(r.Role, out var reconciledIds))
                peerRoleReconciledStatusIds[r.Role] = reconciledIds = new HashSet<ushort>();
            foreach (var target in r.Statuses)
            {
                // Tracked regardless of whether AddStatus actually needs to run this
                // tick, so the removal loop below recognizes it as host-managed even
                // on a snapshot where nothing changed.
                reconciledIds.Add(target.StatusId);
                if (currentStatuses.Any(s => s.StatusId == target.StatusId && s.Stacks == target.Stacks)) continue;
                member.AddStatus(target.StatusId, duration: target.RemainingTime, stacks: target.Stacks, overrideStacks: true);
            }
            // Only ever removes a statusId THIS reconciliation previously added --
            // never diffs against the character's full ActiveStatusSnapshot the way
            // the enemy path does, since a peer's own real character can carry
            // statuses nothing here put there (see peerRoleReconciledStatusIds).
            foreach (var trackedId in reconciledIds.ToList())
            {
                if (r.Statuses.Any(s => s.StatusId == trackedId)) continue;
                member.RemoveStatus(trackedId);
                reconciledIds.Remove(trackedId);
            }
            var statusKey = string.Join(",", r.Statuses.Select(s => $"{s.StatusId}:{s.Stacks}"));
            if (!peerRoleLastLoggedStatuses.TryGetValue(r.Role, out var lastStatusKey) || lastStatusKey != statusKey)
            {
                peerRoleLastLoggedStatuses[r.Role] = statusKey;
                DiagnosticLog.Info($"[Multiplayer] Peer: role {r.Role} statuses -> [{statusKey}].");
            }
            if (r.NewLockonVfxIds.Count > 0)
            {
                DiagnosticLog.Info($"[Multiplayer] Peer: role {r.Role} NewLockonVfxIds -> [{string.Join(",", r.NewLockonVfxIds)}].");
                foreach (var lockonId in r.NewLockonVfxIds)
                    member.AttachLockonVfx(lockonId, persistent: false);
            }
        }

        var seenEventObjectIds = new HashSet<int>();
        foreach (var o in snap.EventObjects)
        {
            seenEventObjectIds.Add(o.NetId);
            if (!peerEventObjects.TryGetValue(o.NetId, out var eo))
            {
                var config = new EventObjectSpawnConfig
                {
                    EObjId = o.EObjId,
                    Placement = new Placement(new Vector3(o.X, o.Y, o.Z), o.Rotation),
                    TimelineState = o.TimelineState,
                    SpawnVisible = true,
                };
                DiagnosticLog.Info($"[Multiplayer] Peer: first snapshot of event object NetId {o.NetId} -- EObj 0x{o.EObjId:X}, pos ({o.X:F2},{o.Y:F2},{o.Z:F2}), state {o.CurrentState} -- spawning local copy.");
                eo = world.SpawnEventObject(config);
                if (eo == null)
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SpawnEventObject returned null for NetId {o.NetId} (EObj 0x{o.EObjId:X}) -- skipping.");
                    continue;
                }
                peerEventObjects[o.NetId] = eo;
            }
            eo.SetPosition(new Placement(new Vector3(o.X, o.Y, o.Z), o.Rotation));
            // Edge-triggered like ModelState -- SetState is a plain field write with
            // no rebuild to worry about, but re-issuing it every snapshot even
            // when unchanged is pointless churn.
            if (!peerEventObjectState.TryGetValue(o.NetId, out var lastState) || lastState != o.CurrentState)
            {
                peerEventObjectState[o.NetId] = o.CurrentState;
                DiagnosticLog.Info($"[Multiplayer] Peer: event object NetId {o.NetId} (EObj 0x{o.EObjId:X}) CurrentState -> {o.CurrentState}.");
                eo.SetState(o.CurrentState);
            }
        }
        foreach (var staleId in peerEventObjects.Keys.Where(id => !seenEventObjectIds.Contains(id)).ToList())
        {
            DiagnosticLog.Info($"[Multiplayer] Peer: event object NetId {staleId} no longer in snapshot -- despawning local copy.");
            peerEventObjects[staleId].Despawn();
            peerEventObjects.Remove(staleId);
            peerEventObjectState.Remove(staleId);
        }
    }

    private SimCharacter? ResolvePeerEnd(SimWorld world, int? enemyNetId, PartyRole? role)
    {
        if (enemyNetId is { } id) return peerEnemies.GetValueOrDefault(id);
        if (role is { } r) return world.Party.Get(r);
        return null;
    }

    private void OnRoleKilledReceived(RoleKilledMessage msg)
    {
        if (IsHost) return;
        DiagnosticLog.Info($"[Multiplayer] {msg.Role} killed: {msg.Cause}");
        if (Plugin.GameInstance.World.Party.Get(msg.Role) is ISimPartyMember member)
            Plugin.GameInstance.Kill(member, msg.Cause);
        else
            DiagnosticLog.Debug($"[Multiplayer] RoleKilled for {msg.Role} but that slot isn't an ISimPartyMember locally -- dropping.");
    }

    private void OnEndReceived(EndMessage msg)
    {
        if (IsHost) return;
        // Not gated on `running`: OnWorldSnapshotReceived spawns/tracks enemies for
        // any non-host peer regardless of role or running state (e.g. a spectator who
        // joined but never claimed a role -- see the MyClaimedRole check in
        // OnStartReceived, which leaves running false for them for the whole run).
        // That peer still has live snapshot-spawned doppels/enmity-list entries to
        // tear down here; bailing on !running left them stuck on screen for exactly
        // that case. The IsInInstance check below already safely no-ops this for a
        // peer who hasn't actually entered the zone yet.
        DiagnosticLog.Info($"[Multiplayer] Peer received EndMessage (ReturnedToInn={msg.ReturnedToInn}).");
        running = false;
        StopDebugBotReplay();
        // If our own deferred zone entry (RunScenarioAsPeer) hasn't actually
        // completed yet, there's nothing to leave or reset -- and calling
        // Leave() here specifically would be actively harmful: Unload()
        // assumes a zone was entered (it restores the real character to the
        // position ZoneSession.Enter() saved) and would instead teleport them
        // to garbage coordinates. Reset() has no equivalent issue (peers never
        // set ActiveScenario, so its own teleport-back-if-needed check never
        // fires), but skip it too here for symmetry -- there's truly nothing
        // to reset.
        if (!Plugin.GameInstance.World.Map.IsInInstance)
        {
            DiagnosticLog.Info("[Multiplayer] EndMessage received before our own deferred zone entry completed -- nothing to leave/reset.");
            return;
        }
        // Mirror whichever the host actually did -- Leave() if they left the
        // zone entirely, or Reset() to match them staying in-zone (ready for a
        // quick re-Start) instead of always hard-kicking to the inn.
        if (msg.ReturnedToInn)
            Plugin.GameInstance.Leave();
        else
            Plugin.GameInstance.Reset();
    }

}
