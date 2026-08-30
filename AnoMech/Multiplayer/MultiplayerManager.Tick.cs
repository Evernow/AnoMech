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
    // ---- Per-frame tick (framework thread; see Plugin.OnFrameworkUpdate) ----

    // Host-only local bookkeeping for "my own run just ended," shared by the
    // normal in-Tick detection (ActiveScenario went null via Reset/Leave/a
    // natural finish, still connected -- broadcasts EndMessage right after
    // this) and Tick()'s own-disconnection-timeout branch above (can't
    // broadcast anything, every peer has already reached the same conclusion
    // independently). Does NOT touch the relay/session/roster -- only that
    // the host's own local run bookkeeping is consistent again.
    private void EndHostRunLocally()
    {
        running = false;
        // Mirrors StopDebugBotReplay's peer-side reset -- without this, a
        // debug-bot host would stay bot-driven (or, on a fresh non-multiplayer
        // solo run afterward, only by luck not still be) past the fight ending.
        DebugBotControl.Enabled = false;
        Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
        // Without this, Session.Started stays true forever once a run ends --
        // the Start button in MultiplayerWindow is gated on !Started, so the
        // lobby would be permanently stuck past its first run with no way to
        // retry short of leaving and re-hosting a brand new session/code.
        Session.Started = false;
    }

    // Host-only: finalize the local run and tell every peer it ended. Shared by
    // Tick()'s own ActiveScenario-null edge detection and the explicit callers
    // below -- factored out because that edge only fires ONCE per run ending
    // (hostScenarioStarted), so a Reset followed by a separate Leave click
    // previously left the second transition (still-in-zone -> left-the-zone)
    // completely unobserved: nothing ever told peers the host had actually left,
    // stranding them in-instance with a Leave button whose LeaveRequestMessage
    // the host would then also silently drop (see the handler below, gated on
    // IsInInstance which was by then already false).
    // returnedToInn is a caller-supplied fact, not something re-derived from
    // World.Map.IsInInstance here -- Game.Leave()/Reset() both do their actual
    // work inside a Plugin.Framework.Run(...) callback, which runs on a later
    // frame, not synchronously. A caller invoking this method right after
    // Leave() (NotifyLeftInstance, the LeaveRequestMessage handler) would
    // observe IsInInstance still true -- the pre-Leave state -- and wrongly
    // broadcast ReturnedToInn=False, telling every peer to Reset() instead of
    // Leave(). This exact race was why guests stayed stuck in-instance after
    // the host clicked Leave: the host's own Leave() eventually ran a few
    // frames later, but the broadcast describing it had already gone out
    // tagged as a Reset. The Tick() edge-trigger call site below is the one
    // exception where reading IsInInstance is safe -- it only fires once
    // ActiveScenario has already gone null on some later tick, by which point
    // any deferred Leave()/Reset() from earlier has genuinely finished.
    private void BroadcastRunEnded(bool returnedToInn)
    {
        EndHostRunLocally();
        _ = relay?.SendAsync(Session.ToMessage());
        DiagnosticLog.Info($"[Multiplayer] Run ended (ReturnedToInn={returnedToInn}) -- broadcasting EndMessage.");
        _ = relay?.SendAsync(new EndMessage(ReturnedToInn: returnedToInn));
        LobbyChanged?.Invoke();

        // Arms Tick()'s resend loop below -- this send is fire-and-forget over a
        // possibly-bad connection, so back it with a few redundant re-sends instead
        // of trusting the one shot.
        pendingEndResendReturnedToInn = returnedToInn;
        endResendsRemaining = EndMessageResendCount;
        endResendTimer = 0f;
    }

    // Called from the host's own "Leave" button (MainWindow) right after
    // Plugin.GameInstance.Leave() -- see BroadcastRunEnded's doc comment for why
    // the Tick()-driven edge trigger can't be relied on here: if the host
    // Reset() first, that edge already fired and consumed itself, so this
    // explicit call is the only thing that will ever tell peers about the
    // Leave() that follows it.
    public void NotifyLeftInstance()
    {
        if (!IsHost || relay is not { IsConnected: true }) return;
        BroadcastRunEnded(returnedToInn: true);
    }

    // Subscribed lazily on first Tick rather than in the field initializer that
    // constructs this class (Plugin.GameInstance isn't set yet at that point) --
    // one-shot, since Plugin.GameInstance.World (and so World.Map) is a single
    // long-lived instance for the plugin's lifetime, never recreated per run.
    // Handlers themselves gate on IsHost so this is a harmless no-op for a peer
    // or during solo play; see MapEffectMessage/MapDirectorUpdateMessage.
    private bool mapEventsSubscribed;

    private void SubscribeMapEventsOnce()
    {
        if (mapEventsSubscribed) return;
        mapEventsSubscribed = true;
        Plugin.GameInstance.World.Map.EffectApplied += (packetFlags, index) =>
        {
            if (!IsHost || relay is not { IsConnected: true }) return;
            DiagnosticLog.Info($"[Multiplayer] Host: broadcasting MapEffect packetFlags=0x{packetFlags:X8} index=0x{index:X}.");
            _ = relay.SendAsync(new MapEffectMessage(packetFlags, index));
        };
        Plugin.GameInstance.World.Map.DirectorUpdated += (category, arg1, arg2, arg3, arg4, arg5, arg6) =>
        {
            if (!IsHost || relay is not { IsConnected: true }) return;
            DiagnosticLog.Info($"[Multiplayer] Host: broadcasting MapDirectorUpdate category=0x{category:X8}.");
            _ = relay.SendAsync(new MapDirectorUpdateMessage(category, arg1, arg2, arg3, arg4, arg5, arg6));
        };
        Plugin.GameInstance.World.Map.WeatherChanged += (weatherId, transition) =>
        {
            if (!IsHost || relay is not { IsConnected: true }) return;
            DiagnosticLog.Info($"[Multiplayer] Host: broadcasting SetWeather weatherId={weatherId} transition={transition}.");
            _ = relay.SendAsync(new SetWeatherMessage(weatherId, transition));
        };
    }

    public void Tick(float deltaSeconds)
    {
        SubscribeMapEventsOnce();
        DrainPendingMessages();

        // Checked before the IsConnected early-return below (this is exactly the
        // case where relay is down) -- see disconnectedSinceMs's own doc comment.
        // One-shot: nulled out immediately so this doesn't re-fire (and re-call
        // Leave()) every subsequent tick while still disconnected/reconnecting.
        if (IsHost && disconnectedSinceMs is { } since && running && Plugin.GameInstance.World.Map.IsInInstance
            && Environment.TickCount64 - since > PeerStaleTimeoutMs)
        {
            DiagnosticLog.Warn($"[Multiplayer] Disconnected from the relay for over {PeerStaleTimeoutMs / 1000}s while running -- leaving the zone myself (every peer has likely already given up waiting and left on their own).");
            Plugin.GameInstance.Leave();
            EndHostRunLocally();
            disconnectedSinceMs = null;
            LobbyChanged?.Invoke();
        }

        if (relay is not { IsConnected: true }) return;

        // Connection-quality tracking runs continuously -- lobby and mid-fight
        // alike -- so the roster's status indicators are already live before
        // anyone clicks Start, not just once running is true below.
        if (IsHost)
        {
            pingTimer += deltaSeconds;
            if (pingTimer >= PingIntervalSeconds)
            {
                pingTimer = 0f;
                SendPingAndRefreshStatuses();
            }

            if (pendingStartResponses is { Count: > 0 } pending)
            {
                startCheckTimer += deltaSeconds;
                if (startCheckTimer >= StartCheckTimeoutSeconds)
                {
                    DiagnosticLog.Info($"[Multiplayer] StartCheck timed out waiting on: {string.Join(", ", pending.Select(Session.NameOf))}.");
                    foreach (var peerId in pending)
                        startCheckFailures[peerId] = "no response";
                    FinishStartCheck();
                }
            }

            if (pendingEndResendReturnedToInn is { } returnedToInn)
            {
                endResendTimer += deltaSeconds;
                if (endResendTimer >= EndMessageResendIntervalSeconds)
                {
                    endResendTimer = 0f;
                    endResendsRemaining--;
                    DiagnosticLog.Info($"[Multiplayer] Re-broadcasting EndMessage (ReturnedToInn={returnedToInn}), {endResendsRemaining} retries left.");
                    _ = relay?.SendAsync(Session.ToMessage());
                    _ = relay?.SendAsync(new EndMessage(ReturnedToInn: returnedToInn));
                    if (endResendsRemaining <= 0) pendingEndResendReturnedToInn = null;
                }
            }
        }
        else if (IsSessionNotFound)
        {
            // No host-broadcast message ever arrived since joining -- the relay's
            // room for this code is either empty (mistyped/nonexistent session) or
            // has only non-host peers in it. Fails fast rather than sitting on
            // NoHostFoundTimeoutMs's much shorter window than PeerStaleTimeoutMs's
            // grace period below, which is tuned for a host that WAS confirmed
            // present going silent -- a worse, rarer case that deserves more benefit
            // of the doubt against transient lag than a bad code does.
            DiagnosticLog.Warn($"[Multiplayer] No host responded within {NoHostFoundTimeoutMs / 1000}s of joining session {SessionCode} -- session not found.");
            LeaveSession();
            SessionEndReason = "Session not found.";
            LobbyChanged?.Invoke();
            return;
        }
        else if (IsHostStale)
        {
            // A clean "Leave session" click reaches peers via SessionEndedMessage
            // well within PeerStaleTimeoutMs, so this only fires for a host that
            // vanished without warning -- a crash, alt-F4, or hard network drop,
            // where no goodbye message was ever possible. Same end state either
            // way: nobody's left to resume the fight with, so leave the zone (if
            // mid-fight) and the session both, rather than sitting on a frozen
            // roster or a stale zone forever. IsInInstance guard: Leave() ->
            // Unload() assumes a zone was actually entered (it restores the
            // real character to the position ZoneSession.Enter() saved) --
            // running can briefly be true before that deferred entry actually
            // completes, and calling it too early teleports the real character
            // to garbage coordinates instead.
            DiagnosticLog.Warn($"[Multiplayer] Lost contact with the host (no message in {SecondsSinceHostMessage:F1}s, threshold {PeerStaleTimeoutMs / 1000}s) -- leaving.");
            if (running && Plugin.GameInstance.World.Map.IsInInstance) Plugin.GameInstance.Leave();
            LeaveSession();
            SessionEndReason = "Lost contact with the host.";
            LobbyChanged?.Invoke();
            return;
        }

        if (!running) return;

        if (IsHost)
        {
            // Edge-triggered against ActiveScenario becoming non-null (see
            // hostScenarioStarted) before "it's null" is allowed to mean "the
            // run ended" -- RunScenarioAsHost's deferred completion hasn't
            // necessarily set it yet on the first Tick() after Start.
            if (Plugin.GameInstance.ActiveScenario != null)
            {
                hostScenarioStarted = true;
            }
            else if (!hostScenarioStarted)
            {
                return;
            }
            else
            {
                // Reset/Leave clears ActiveScenario -- stop broadcasting once the local
                // run has ended rather than spamming empty snapshots (or, worse, a
                // later unrelated solo run) to peers who are still connected.
                // Safe to read IsInInstance here (unlike the explicit call sites) --
                // this only fires once ActiveScenario has gone null on some later
                // tick, by which point Reset()/Leave()'s deferred work has finished.
                BroadcastRunEnded(!Plugin.GameInstance.World.Map.IsInInstance);
                return;
            }
            if (!aiReplayStateSent) TrySendAiReplayState();
            if (pendingSnapshotSend is null or { IsCompleted: true })
                pendingSnapshotSend = SampleAndBroadcastSnapshot();
            if (pendingRolesSend is null or { IsCompleted: true })
                pendingRolesSend = SampleAndBroadcastRoles();
        }
        else
        {
            if (Plugin.GameInstance.World.Map.IsInInstance)
            {
                if (!peerEnteredInstance) DiagnosticLog.Info("[Multiplayer] Peer's deferred zone entry completed -- now sending SelfPose.");
                peerEnteredInstance = true;
                // Cheap and idempotent past the first successful call (guarded
                // internally on debugBotReplayStarted) -- simpler than a second
                // edge-trigger flag alongside peerEnteredInstance.
                TryStartDebugBotReplay();
                // P5 Exaflares only: UmadP5ExaflaresAi schedules its dodges onto
                // debugShadowStateP5.Timeline (a private EventScheduler), which the
                // real scenario normally drives every frame from its own Tick (see
                // UmadP5ExaflaresScenario.Tick) -- but a peer never runs
                // IScenario.Tick at all (Game.RunScenarioInternal never sets
                // ActiveScenario for isPeer:true), so nothing else would ever
                // advance it. Mirrors that method's two calls exactly, using the
                // same raw (EventTimeScale-independent) deltaSeconds this Tick
                // already receives from Plugin.OnFrameworkUpdate -- capped the
                // same way UmadP5ExaflaresScenario.Tick caps its own wall-clock
                // delta, so a hitch/alt-tab frame skips this tick instead of
                // dumping every still-queued event out at once.
                if (debugShadowStateP5 is { } p5Shadow && deltaSeconds > 0f && deltaSeconds <= P5ReplayFrameGapCapSeconds)
                {
                    p5Shadow.Timeline.Tick(deltaSeconds);
                    p5Shadow.SpreadTick?.Invoke(deltaSeconds);
                }
            }
            else if (peerEnteredInstance)
            {
                DiagnosticLog.Info("[Multiplayer] Peer's zone was unloaded out from under the run (IsInInstance went false) -- stopping locally.");
                running = false;
                StopDebugBotReplay();
                return;
            }
            else
            {
                // Zone load queued by RunScenarioAsPeer hasn't run yet -- wait
                // rather than tearing down a run that hasn't truly started.
                return;
            }
            SendSelfPose();
        }
    }

}
