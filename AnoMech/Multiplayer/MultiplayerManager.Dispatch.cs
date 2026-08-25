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

    // Ordering, not just marshalling onto the Framework thread, is why this exists.
    // RelayClient.ReceiveLoopAsync invokes MessageReceived synchronously and
    // sequentially, one message fully handled before the next ReceiveAsync -- so this
    // enqueue always happens in true wire-arrival order. But firing a separate
    // Plugin.Framework.Run(() => Dispatch(message)) per message (the old approach)
    // does NOT preserve that order once queued: Dalamud's Framework.Run schedules
    // onto ThreadBoundTaskScheduler, whose Run() iterates a ConcurrentDictionary of
    // pending tasks -- not insertion order. Confirmed via AnoMech-DamageDebug dumps:
    // a burst of MapEffectMessages sent host-side in order 1-8,0 (and by then
    // already verified arriving wire-ordered peer-side, per RelayClient's own FIFO
    // send queue) still executed out of order once each hit its own Framework.Run,
    // scrambling a peer's replicated arena-color transition (P2 Forsaken's
    // gold->black swap). Draining this queue once per Tick (already called
    // unconditionally every frame from Plugin.OnFrameworkUpdate) sidesteps that
    // scheduler entirely instead of fighting its ordering.
    private readonly ConcurrentQueue<MpMessage> pendingMessages = new();

    private void OnMessageReceivedOffThread(MpMessage message) => pendingMessages.Enqueue(message);

    private void DrainPendingMessages()
    {
        while (pendingMessages.TryDequeue(out var message))
            Dispatch(message);
    }

    // `source` is the specific RelayClient instance this event came from --
    // compared against the current `relay` field so a stale event from a
    // client we've already torn down (LeaveSession) or superseded (a
    // newer reconnect attempt winning the race) is ignored rather than
    // reprocessed or double-triggering another reconnect loop. This also
    // doubles as the "was this intentional" check: LeaveSession always nulls
    // `relay` before the corresponding Disconnected event can be dispatched
    // (Dispose's cancellation unwinds on a background thread and this handler
    // is itself marshalled to run on a later framework tick), so a manual
    // Leave's own Disconnected(null) never reaches past this guard.
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
        // A single malformed-but-parseable message (an unexpected null, a role
        // enum out of range, whatever) must not take down the framework tick
        // pump that every other plugin system also shares -- log and move on
        // rather than letting one bad packet cascade.
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
        // Every message type the host actually broadcasts to everyone (as
        // opposed to a fellow peer's request that the relay's dumb fan-out
        // happens to deliver to us too, e.g. another peer's ClaimRoleMessage --
        // this switch just never matches those on a non-host client). Used to
        // drive the host's own roster-row liveness (see lastHostMessageMs);
        // must be kept in sync with the `when !IsHost` cases below.
        // SessionEndedMessage deliberately excluded -- unlike everything else
        // here it isn't guaranteed to have come from the host (any peer can
        // send it). When it *is* the host leaving, it's moot anyway since
        // receiving it tears the whole session down a few lines later
        // regardless; when it's a departing peer instead, it plainly isn't a
        // host message at all and must not be mistaken for one.
        if (!IsHost && message is LobbyStateMessage or StartMessage or WorldSnapshotMessage
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
                // The host never re-sends Start to an already-open connection --
                // only a fresh StartMessage triggers OnStartReceived. Without this,
                // a peer who connects after Start already fired (a late join, or a
                // manual rejoin after their connection dropped mid-fight) would sit
                // forever on "Connected -- waiting for the host to start" even
                // though the fight is already running. OnStartReceived is itself
                // idempotent, so this is safe to also fall through for it on a
                // normal fresh start (arrives just before StartMessage does).
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
                // Remove(...) returning false means either a duplicate/stale
                // reply or one that arrived after the timeout already gave up
                // on this peer -- either way there's nothing left to do with it.
                if (pendingStartResponses == null || !pendingStartResponses.Remove(resp.PeerId)) break;
                if (!resp.Ready) startCheckFailures[resp.PeerId] = resp.Reason ?? "not ready";
                if (pendingStartResponses.Count == 0) FinishStartCheck();
                break;
            case WorldSnapshotMessage snap when !IsHost:
                OnWorldSnapshotReceived(snap);
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
            // No `when !IsHost` guard -- when the HOST leaves it ends the
            // session for the whole group (including any other peers), so
            // that branch has to run regardless of the recipient's role. A
            // departing peer, by contrast, only ever shrinks the roster (see
            // RemovePeer) -- the rest of the group keeps going.
            case SessionEndedMessage ended when ended.PeerId == Session.HostId:
            {
                // Read the sender's name before LeaveSessionInternal wipes Session out from under it.
                var who = Session.NameOf(ended.PeerId);
                DiagnosticLog.Info($"[Multiplayer] Host {who} left -- session ending for the whole group.");
                // IsInInstance guard, not running -- same reasoning as
                // LeaveRequestMessage below: a Reset earlier in the session (e.g. the
                // peer clicking Reset before the host later leaves) clears running
                // while leaving the group stuck in-instance, so gating on running here
                // would skip Leave() and strand this peer in-instance with no session
                // left to recover through. IsInInstance is only ever true once a zone
                // was genuinely entered (MapController.TryLoad), which is the actual
                // precondition Leave() -> Unload() needs (it restores the real
                // character to the position ZoneSession.Enter() saved).
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
            // IsInInstance guard (not running -- Leave() must still work after
            // a Reset, which clears running while leaving the group stuck
            // in-instance with nothing to show for it and no way back short of
            // disbanding the whole session): Leave() -> Unload() assumes a zone
            // was actually entered (it restores the real character to the
            // position ZoneSession.Enter() saved), and IsInInstance is only
            // ever set true once that has genuinely happened (MapController.
            // TryLoad), so it alone is the correct signal here.
            case LeaveRequestMessage req when IsHost:
                DiagnosticLog.Info($"[Multiplayer] {Session.NameOf(req.PeerId)} requested to leave the instance.");
                if (Plugin.GameInstance.World.Map.IsInInstance)
                    Plugin.GameInstance.Leave();
                // Unconditional, even when IsInInstance was already false above (e.g.
                // the host left via their own Leave button, possibly after an earlier
                // Reset, before this request arrived) -- the old code silently dropped
                // the request in exactly that case, leaving the requesting peer's own
                // Leave button waiting forever for a response the host never sent.
                // Always returnedToInn: true -- this handler's whole purpose is
                // granting a leave request, regardless of whether Leave() above was
                // just queued (its deferred World.Map.Unload() hasn't run yet, so
                // reading IsInInstance here would still see the pre-Leave state).
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
            // Re-syncs the one field in P2AiReplayStateMessage's snapshot that
            // actually changes mid-fight -- see P2LockonsUpdateMessage's own doc
            // comment. Dropped if the shadow state doesn't exist yet: that only
            // happens if this update raced ahead of the replay actually starting,
            // in which case the initial P2AiReplayStateMessage.Lockons snapshot
            // (sent before any tower has had a chance to resolve and reassign
            // anything) already carries the same value.
            case P2LockonsUpdateMessage lockonsUpdate when !IsHost:
                if (debugShadowStateP2 is { } p2Shadow)
                    p2Shadow.Lockons = lockonsUpdate.Lockons;
                break;
            // Pure replays of the exact host-side call -- see
            // MapEffectMessage/MapDirectorUpdateMessage's doc comment. The
            // handlers subscribed in SubscribeMapEventsOnce gate on IsHost, so
            // this can't loop back into a re-broadcast: AddEffect/DirectorUpdate
            // fire the same MapController events on a peer's own local call too,
            // but that peer's own handler is a no-op since IsHost is false there.
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
