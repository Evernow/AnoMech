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
    // ---- Message pump -------------------------------------------------------

    // Exists for ordering, not just marshalling onto the Framework thread. RelayClient's
    // receive loop invokes MessageReceived synchronously in wire order, but firing a
    // separate Plugin.Framework.Run per message does NOT preserve that order once queued --
    // Dalamud's ThreadBoundTaskScheduler doesn't run pending tasks in insertion order
    // (confirmed via AnoMech-DamageDebug dumps: a MapEffectMessage burst arrived wire-ordered
    // but executed scrambled). Draining this queue once per Tick sidesteps that scheduler.
    private readonly ConcurrentQueue<MpMessage> pendingMessages = new();

    private void OnMessageReceivedOffThread(MpMessage message) => pendingMessages.Enqueue(message);

    // Skips a WorldSnapshot/RolesSnapshot when the next queued item is another of the same
    // type -- under a bad connection these can back up and replay in a burst. Only drops an
    // earlier same-type entry for a newer one right behind it; cross-type order is untouched.
    private void DrainPendingMessages()
    {
        while (pendingMessages.TryDequeue(out var message))
        {
            if ((message is WorldSnapshotMessage && pendingMessages.TryPeek(out var nextSnap) && nextSnap is WorldSnapshotMessage)
                || (message is RolesSnapshotMessage && pendingMessages.TryPeek(out var nextRoles) && nextRoles is RolesSnapshotMessage))
                continue;
            Dispatch(message);
        }
    }

    // `source` is compared against the current `relay` so a stale event from an
    // already-torn-down or superseded client is ignored. Also doubles as the "was this
    // intentional" check: LeaveSession always nulls `relay` before its own Disconnected(null)
    // can be dispatched, so a manual Leave never reaches past this guard.
    private void OnDisconnectedOffThread(RelayClient source, Exception? failure)
        => Plugin.Framework.Run(() =>
        {
            if (!ReferenceEquals(relay, source)) return;
            source.Dispose();
            relay = null;
            disconnectedSinceMs ??= Environment.TickCount64;
            if (failure != null)
            {
                DiagnosticLog.Warn($"[Multiplayer] Disconnected: {failure.Message}");
                ConnectionError = failure.Message;
            }
            else
            {
                DiagnosticLog.Info("[Multiplayer] Disconnected (no failure reported -- socket just closed).");
            }
            LobbyChanged?.Invoke();
            BeginReconnect();
        });

    private void Dispatch(MpMessage message)
    {
        // One malformed message must not take down the shared framework tick pump.
        try
        {
            DispatchCore(message);
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[Multiplayer] Error handling {message.GetType().Name}: {e}");
        }
    }

    private void DispatchCore(MpMessage message)
    {
        // Every message type the host actually broadcasts (not a fellow peer's request the
        // relay's fan-out happens to deliver to us too) -- drives lastHostMessageMs; keep in
        // sync with the `when !IsHost` cases below. SessionEndedMessage excluded: any peer
        // can send it, so it isn't reliably a host message.
        if (!IsHost && message is LobbyStateMessage or StartMessage or WorldSnapshotMessage or RolesSnapshotMessage
            or RoleKilledMessage or EndMessage or PingMessage or PeerStatusMessage
            or AiReplayStateMessage or P2AiReplayStateMessage or P4AiReplayStateMessage or P5AiReplayStateMessage)
        {
            lastHostMessageMs = Environment.TickCount64;
            everHeardFromHost = true;
        }

        switch (message)
        {
            // Host-authoritative: only the host acts on requests other clients send.
            case HelloMessage hello when IsHost:
                peerLastSeenMs[hello.PeerId] = Environment.TickCount64;
                Session.Names[hello.PeerId] = hello.DisplayName;
                Session.Builds[hello.PeerId] = new PeerBuildInfo(hello.Version, hello.Checksum);
                DiagnosticLog.Info($"[Multiplayer] Hello from {hello.PeerId} ({hello.DisplayName}), build {hello.Version} ({new PeerBuildInfo(hello.Version, hello.Checksum).ShortChecksum}), mismatch={IsVersionMismatched(hello.PeerId)}.");
                BroadcastLobbyState();
                break;
            case ClaimRoleMessage claim when IsHost:
                peerLastSeenMs[claim.PeerId] = Environment.TickCount64;
                ApplyClaim(claim.PeerId, claim.Role);
                break;
            case ReleaseRoleMessage release when IsHost:
                peerLastSeenMs[release.PeerId] = Environment.TickCount64;
                ApplyRelease(release.PeerId);
                break;
            case SelfPoseMessage pose when IsHost:
                peerLastSeenMs[pose.PeerId] = Environment.TickCount64;
                OnSelfPoseReceived(pose);
                break;
            case PongMessage pong when IsHost:
                peerLastSeenMs[pong.PeerId] = Environment.TickCount64;
                peerLatencyMs[pong.PeerId] = Environment.TickCount64 - pong.SentAtMs;
                break;

            // Peer-facing broadcasts from the host.
            case LobbyStateMessage lobby when !IsHost:
                Session.ApplyLobbyState(lobby);
                LobbyChanged?.Invoke();
                // The host never re-sends Start to an already-open connection -- without
                // this, a late join or a rejoin mid-fight would sit forever on "waiting for
                // the host to start." OnStartReceived is idempotent, so this is also safe on
                // a normal fresh start (arrives just before StartMessage).
                if (lobby.Started && MyClaimedRole != null)
                    OnStartReceived();
                break;
            case StartMessage when !IsHost:
                OnStartReceived();
                break;
            case StartCheckMessage when !IsHost:
            {
                var reason = CheckOwnStartReadiness();
                _ = relay?.SendAsync(new StartCheckResponseMessage(MyPeerId, reason == null, reason));
                break;
            }
            case StartCheckResponseMessage resp when IsHost:
                DiagnosticLog.Info($"[Multiplayer] StartCheck reply from {Session.NameOf(resp.PeerId)}: ready={resp.Ready}{(resp.Reason is { } r ? $" ({r})" : "")}.");
                // false means a duplicate/stale reply, or the timeout already gave up on this peer.
                if (pendingStartResponses == null || !pendingStartResponses.Remove(resp.PeerId)) break;
                if (!resp.Ready) startCheckFailures[resp.PeerId] = resp.Reason ?? "not ready";
                if (pendingStartResponses.Count == 0) FinishStartCheck();
                break;
            case WorldSnapshotMessage snap when !IsHost:
                OnWorldSnapshotReceived(snap);
                break;
            case RolesSnapshotMessage rolesSnap when !IsHost:
                OnRolesSnapshotReceived(rolesSnap);
                break;
            case RoleKilledMessage killed when !IsHost:
                OnRoleKilledReceived(killed);
                break;
            case EndMessage end when !IsHost:
                OnEndReceived(end);
                break;
            case PingMessage ping when !IsHost:
                _ = relay?.SendAsync(new PongMessage(MyPeerId, ping.SentAtMs));
                break;
            case PeerStatusMessage status when !IsHost:
                peerStatuses.Clear();
                foreach (var (id, entry) in status.Statuses)
                    peerStatuses[id] = entry;
                break;
            // No `when !IsHost` guard -- when the HOST leaves it ends the session for
            // everyone, so this must run regardless of the recipient's role. A departing
            // peer only shrinks the roster (see RemovePeer); the group keeps going.
            case SessionEndedMessage ended when ended.PeerId == Session.HostId:
            {
                // Read the sender's name before LeaveSessionInternal wipes Session out from under it.
                var who = Session.NameOf(ended.PeerId);
                DiagnosticLog.Info($"[Multiplayer] Host {who} left -- session ending for the whole group.");
                // IsInInstance guard, not running -- a Reset earlier in the session can clear
                // running while leaving the group stuck in-instance (see LeaveRequestMessage).
                if (Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
                LeaveSessionInternal(notifyOthers: false);
                SessionEndReason = $"{who} left -- session ended.";
                LobbyChanged?.Invoke();
                break;
            }
            case SessionEndedMessage ended when IsHost:
                RemovePeer(ended.PeerId);
                break;
            case ResetRequestMessage req when IsHost:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested a reset.");
                Plugin.GameInstance.Reset();
                break;
            // IsInInstance guard, not running -- Leave() must still work after a Reset, which
            // clears running while leaving the group stuck in-instance.
            case LeaveRequestMessage req when IsHost:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested to leave the instance.");
                if (Plugin.GameInstance.World.Map.IsInInstance)
                    Plugin.GameInstance.Leave();
                // Unconditional, even if IsInInstance was already false -- otherwise the
                // requesting peer's own Leave button waits forever for a response.
                BroadcastRunEnded(returnedToInn: true);
                break;
            case AiReplayStateMessage state when !IsHost:
                pendingAiReplayState = state;
                TryStartDebugBotReplay();
                break;
            case P2AiReplayStateMessage p2State when !IsHost:
                pendingP2AiReplayState = p2State;
                TryStartDebugBotReplay();
                break;
            // Re-syncs the one field in P2AiReplayStateMessage's snapshot that changes
            // mid-fight -- see P2LockonsUpdateMessage. Dropped if the shadow state doesn't
            // exist yet: only happens if this raced ahead of replay starting, in which case
            // the initial snapshot already carries the same value.
            case P2LockonsUpdateMessage lockonsUpdate when !IsHost:
                if (debugShadowStateP2 is { } p2Shadow)
                    p2Shadow.Lockons = lockonsUpdate.Lockons;
                break;
            // Pure replays of the host-side call. Can't loop into a re-broadcast: the
            // SubscribeMapEventsOnce handlers gate on IsHost, so a peer's own local
            // AddEffect/DirectorUpdate call is a no-op there.
            case MapEffectMessage effect when !IsHost:
                DiagnosticLog.Info($"[Multiplayer] Peer: applying MapEffect packetFlags=0x{effect.PacketFlags:X8} index=0x{effect.Index:X}.");
                Plugin.GameInstance.World.Map.AddEffect(effect.PacketFlags, effect.Index);
                break;
            case MapDirectorUpdateMessage directorUpdate when !IsHost:
                DiagnosticLog.Info($"[Multiplayer] Peer: applying MapDirectorUpdate category=0x{directorUpdate.Category:X8}.");
                Plugin.GameInstance.World.Map.DirectorUpdate(
                    directorUpdate.Category, directorUpdate.Arg1, directorUpdate.Arg2,
                    directorUpdate.Arg3, directorUpdate.Arg4, directorUpdate.Arg5, directorUpdate.Arg6);
                break;
            case SetWeatherMessage weather when !IsHost:
                DiagnosticLog.Info($"[Multiplayer] Peer: applying SetWeather weatherId={weather.WeatherId} transition={weather.Transition}.");
                Plugin.GameInstance.World.Map.SetWeather(weather.WeatherId, weather.Transition);
                break;
            case P4AiReplayStateMessage p4State when !IsHost:
                pendingP4AiReplayState = p4State;
                TryStartDebugBotReplay();
                break;
            case P5AiReplayStateMessage p5State when !IsHost:
                pendingP5AiReplayState = p5State;
                TryStartDebugBotReplay();
                break;
        }
    }
}
