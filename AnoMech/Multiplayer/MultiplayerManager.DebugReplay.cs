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
    // ---- Debug: bot-controlled peer replay ----------------------------------

    // Host-only, edge-triggered once per run. LastState isn't guaranteed set on
    // the very first Tick() after StartScenario -- RunScenarioAsHost's actual
    // work (including constructing the scenario's *State) is deferred via
    // Plugin.Framework.Run, same reasoning as peerEnteredInstance below -- so
    // this polls instead of reading it synchronously right after the call.
    // Sent unconditionally: the host never knows or cares which peers, if any,
    // are using it locally. One case per IScenario.SupportsMultiplayer scenario --
    // add a new one here alongside a new *AiReplayStateMessage and
    // *State.FromNetworkReplay when porting another scenario.
    private void TrySendAiReplayState()
    {
        switch (Plugin.GameInstance.Scenarios[Session.ScenarioIndex])
        {
            case UmadP3BlackHoleScenario p3Scenario:
                if (p3Scenario.LastState is not { } p3State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new AiReplayStateMessage(
                    p3State.Roles.List, p3State.StackTargets.List, p3State.SlapAttacks.ToArray(),
                    p3State.KefkaPosition.Select(d => d.RadiansFromNorth).ToArray(), p3State.ImplosionAttack));
                break;
            case UmadP2ForsakenScenario p2Scenario:
                if (p2Scenario.LastState is not { } p2State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new P2AiReplayStateMessage(
                    p2State.EndAttacks, p2State.NewNorth.RadiansFromNorth, p2State.Rotation, p2State.Lockons));
                break;
            case UmadP4KefkaSaysScenario p4Scenario:
                if (p4Scenario.LastState is not { } p4State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new P4AiReplayStateMessage(
                    p4State.Mystery.Select(m => m.BlizzardOffset).ToArray(),
                    p4State.Mystery.Select(m => m.LightningOffset).ToArray(),
                    p4State.Mystery.Select(m => m.LightningOrientation).ToArray(),
                    p4State.Wave1First, p4State.Wave1.List, p4State.Wave1True,
                    p4State.Wave2.List, p4State.Wave2True,
                    p4State.InfernoMystery.IsTrue, p4State.TsunamiMystery.IsTrue,
                    p4State.Wave3.List, p4State.Wounds,
                    p4State.Antilights[0].Antilight == Antilight.White,
                    p4State.NeoExdeathDirection.RadiansFromNorth));
                break;
            case UmadP5ExaflaresScenario p5Scenario:
                if (p5Scenario.LastState is not { } p5State) return;
                aiReplayStateSent = true;
                DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
                _ = relay!.SendAsync(new P5AiReplayStateMessage(p5State.LeftOrder.ToArray(), p5State.RightOrder.ToArray()));
                break;
        }
    }

    // Guards the AiStrats[Session.SelectedAi] indexing every multi-strat
    // scenario's replay branch does below. StartScenario/MultiplayerWindow
    // already refuse to broadcast an out-of-range SelectedAi (see
    // MainWindow.HasStartableStrat), but a peer has no independent way to
    // verify what the host sent, so this is the last line of defense against
    // an IndexOutOfRangeException here.
    private bool IsValidAiIndex(IScenario scenario) =>
        Session.SelectedAi >= 0 && Session.SelectedAi < scenario.AiStrats.Count;

    // Peer-only, idempotent, edge-triggered once per run: fires once both the
    // host's *AiReplayStateMessage has arrived (pending*AiReplayState) and our
    // own zone/party is actually ready (peerEnteredInstance) -- arrival order
    // between those two isn't guaranteed, so both call sites (Dispatch and
    // Tick) funnel through here. A no-op entirely unless debug-bot mode is on.
    // Branches on the active scenario the same way TrySendAiReplayState does.
    private void TryStartDebugBotReplay()
    {
        if (!debugBotControlled || debugBotReplayStarted) return;
        if (!peerEnteredInstance) return;
        if (MyClaimedRole is not { } myRole) return;
        var world = Plugin.GameInstance.World;

        switch (Plugin.GameInstance.Scenarios[Session.ScenarioIndex])
        {
            case UmadP3BlackHoleScenario when pendingAiReplayState is { } msg:
            {
                debugBotReplayStarted = true;
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                var shadowState = UmadP3BlackHoleState.FromNetworkReplay(
                    world, msg.Roles, msg.StackTargets, msg.SlapAttacks, msg.KefkaPositionRadians, msg.ImplosionAttack);
                // Chaos/Exdeath might not have been replicated yet (WorldSnapshot
                // and this message are independent flows) -- OnWorldSnapshotReceived
                // keeps retrying this resolution against debugShadowState below as
                // new enemies come in, so a still-null boss here isn't a lost
                // cause, just not needed until each choreography step that reads
                // it, tens of seconds into the fight at the earliest.
                shadowState.ScenarioObjects.Chaos = peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.ChaosP3);
                shadowState.ScenarioObjects.Exdeath = peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.Exdeath);
                debugShadowState = shadowState;
                DebugBotControl.Enabled = true;
                new UmadP3BlackHoleAi().Run(shadowState, world);
                break;
            }
            case UmadP2ForsakenScenario p2Scenario when pendingP2AiReplayState is { } p2Msg:
            {
                // Belt-and-suspenders: StartScenario/MultiplayerWindow already refuse
                // to broadcast an out-of-range SelectedAi (see HasStartableStrat), but
                // marking replay "started" either way (rather than just `break`) stops
                // this from silently re-attempting -- and re-logging nothing -- every
                // single frame if it's ever somehow reached anyway.
                debugBotReplayStarted = true;
                if (!IsValidAiIndex(p2Scenario))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {p2Scenario.Name} ({p2Scenario.AiStrats.Count} strats) -- skipping debug-bot replay.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                var shadowState = UmadP2ForsakenState.FromNetworkReplay(
                    p2Msg.EndAttacks, p2Msg.NewNorthRadians, p2Msg.Rotation, p2Msg.Lockons);
                debugShadowStateP2 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP2ForsakenState>)p2Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
            case UmadP4KefkaSaysScenario p4Scenario when pendingP4AiReplayState is { } p4Msg:
            {
                debugBotReplayStarted = true;
                if (!IsValidAiIndex(p4Scenario))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {p4Scenario.Name} ({p4Scenario.AiStrats.Count} strats) -- skipping debug-bot replay.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                var shadowState = UmadP4KefkaSaysState.FromNetworkReplay(
                    world.Party, p4Msg.MysteryBlizzardOffset, p4Msg.MysteryLightningOffset, p4Msg.MysteryLightningOrientation,
                    p4Msg.Wave1First, p4Msg.Wave1, p4Msg.Wave1True, p4Msg.Wave2, p4Msg.Wave2True,
                    p4Msg.InfernoIsTrue, p4Msg.TsunamiIsTrue, p4Msg.Wave3, p4Msg.Wounds,
                    p4Msg.Antilight0IsWhite, p4Msg.NeoExdeathDirectionRadians);
                debugShadowStateP4 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP4KefkaSaysState>)p4Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
            case UmadP5ExaflaresScenario p5Scenario when pendingP5AiReplayState is { } p5Msg:
            {
                debugBotReplayStarted = true;
                if (!IsValidAiIndex(p5Scenario))
                {
                    DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {p5Scenario.Name} ({p5Scenario.AiStrats.Count} strats) -- skipping debug-bot replay.");
                    break;
                }
                DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
                // A fresh, peer-owned EventScheduler -- NOT the host's -- since
                // UmadP5ExaflaresAi schedules its dodges directly onto it, and
                // nothing would ever drive one shared with anything else. See
                // MultiplayerManager.Tick's P5-specific branch just below for
                // what actually ticks it every frame (mirrors UmadP5ExaflaresScenario.Tick,
                // which normally does this but never runs on a peer).
                var shadowState = UmadP5ExaflaresState.FromNetworkReplay(p5Msg.LeftOrder, p5Msg.RightOrder, new EventScheduler());
                debugShadowStateP5 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP5ExaflaresState>)p5Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
        }
    }

    // Clears just the current run's replay state, not the debugBotControlled
    // toggle itself (a sticky lobby preference) -- called whenever running
    // stops for any reason, so a debug-bot peer's real character always
    // regains normal control the instant the fight ends rather than staying
    // bot-driven while standing in an empty arena or back in the inn.
    private void StopDebugBotReplay()
    {
        if (debugBotReplayStarted) DiagnosticLog.Info("[Multiplayer] Peer: stopping debug-bot replay.");
        DebugBotControl.Enabled = false;
        pendingAiReplayState = null;
        pendingP2AiReplayState = null;
        pendingP4AiReplayState = null;
        pendingP5AiReplayState = null;
        debugShadowState = null;
        debugShadowStateP2 = null;
        debugShadowStateP4 = null;
        debugShadowStateP5 = null;
        debugBotReplayStarted = false;
    }

}
