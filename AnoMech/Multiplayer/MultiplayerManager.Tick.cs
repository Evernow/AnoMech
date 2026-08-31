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

    // Host-only local bookkeeping for "my own run just ended" -- shared by normal in-Tick
    // detection (ActiveScenario went null, still connected, broadcasts EndMessage next) and
    // the disconnection-timeout branch below (can't broadcast; peers already gave up on
    // their own). Doesn't touch the relay/session/roster.
    private void EndHostRunLocally()
    {
        running = false;
        DebugBotControl.Enabled = false; // mirrors StopDebugBotReplay's peer-side reset
        Plugin.GameInstance.PartyMemberKilled -= OnPartyMemberKilledHost;
        Session.Started = false; // otherwise the Start button (gated on !Started) stays dead
    }

    // Host-only: finalize the local run and tell every peer it ended. Factored out because
    // Tick()'s own edge-trigger only fires once per run (hostScenarioStarted) -- a Reset
    // followed by a separate Leave needs this called explicitly too, or the Leave half of
    // that sequence never reaches peers at all.
    //
    // returnedToInn is caller-supplied, not read from World.Map.IsInInstance here: Leave()/
    // Reset() do their real work inside a later Plugin.Framework.Run callback, so a caller
    // invoking this right after Leave() would still see the pre-Leave IsInInstance==true and
    // broadcast the wrong verb (Reset instead of Leave). The Tick() edge-trigger call site is
    // the one exception where reading IsInInstance directly is safe, since it only fires
    // after ActiveScenario has already gone null on a later tick.
    private void BroadcastRunEnded(bool returnedToInn)
    {
        EndHostRunLocally();
        _ = relay?.SendAsync(Session.ToMessage());
        DiagnosticLog.Info($"[Multiplayer] Run ended (ReturnedToInn={returnedToInn}) -- broadcasting EndMessage.");
        _ = relay?.SendAsync(new EndMessage(ReturnedToInn: returnedToInn));
        LobbyChanged?.Invoke();

        // Arms Tick()'s resend loop -- the send above is fire-and-forget, so back it with a
        // few redundant re-sends instead of trusting the one shot.
        pendingEndResendReturnedToInn = returnedToInn;
        endResendsRemaining = EndMessageResendCount;
        endResendTimer = 0f;
    }

    // Called from the host's own Leave button right after Plugin.GameInstance.Leave() -- see
    // BroadcastRunEnded's comment for why the Tick() edge trigger can't be relied on here.
    public void NotifyLeftInstance()
    {
        if (!IsHost || relay is not { IsConnected: true }) return;
        BroadcastRunEnded(returnedToInn: true);
    }

    // Subscribed lazily on first Tick (Plugin.GameInstance isn't set at field-init time),
    // once, since World.Map is a single long-lived instance. Handlers gate on IsHost, so
    // this is a harmless no-op for a peer or solo play.
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

        // Checked before the IsConnected early-return below -- this IS the "relay down" case.
        // disconnectedSinceMs is nulled immediately after so this doesn't re-fire every tick.
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

        // Connection-quality tracking runs continuously, so roster status is live before
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
            // No host-broadcast ever arrived -- likely a mistyped/nonexistent code. Fails
            // fast on the shorter NoHostFoundTimeoutMs rather than waiting out
            // PeerStaleTimeoutMs, which is tuned for a host that WAS present going silent.
            DiagnosticLog.Warn($"[Multiplayer] No host responded within {NoHostFoundTimeoutMs / 1000}s of joining session {SessionCode} -- session not found.");
            LeaveSession();
            SessionEndReason = "Session not found.";
            LobbyChanged?.Invoke();
            return;
        }
        else if (IsHostStale)
        {
            // A clean Leave reaches peers via SessionEndedMessage well within
            // PeerStaleTimeoutMs, so this only fires for a host that vanished without
            // warning (crash, alt-F4, hard network drop). IsInInstance guard: Leave() ->
            // Unload() assumes a zone was actually entered; `running` can briefly be true
            // before that deferred entry completes.
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
            // hostScenarioStarted gates "ActiveScenario is null" from meaning "run ended"
            // until it's actually been seen non-null once -- RunScenarioAsHost's completion
            // is deferred a frame past Start.
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
                // Reset/Leave clears ActiveScenario -- stop broadcasting once the run has
                // ended. Safe to read IsInInstance here (unlike the explicit call sites,
                // see BroadcastRunEnded): by now Reset()/Leave()'s deferred work is done.
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
                TryStartDebugBotReplay(); // idempotent, guarded internally on debugBotReplayStarted
                // P5 only: a peer never runs IScenario.Tick, so nothing else drives
                // debugShadowStateP5.Timeline. Mirrors UmadP5ExaflaresScenario.Tick's two
                // calls, with the same frame-gap cap against a hitch/alt-tab frame dumping
                // every queued event at once.
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
                // Zone load queued by RunScenarioAsPeer hasn't run yet -- wait rather than
                // tearing down a run that hasn't truly started.
                return;
            }
            SendSelfPose();
        }
    }

}
