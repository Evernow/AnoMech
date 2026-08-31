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

    // Host-only, edge-triggered once per run. LastState isn't guaranteed set on the very
    // first Tick() after StartScenario (RunScenarioAsHost's real work is deferred a frame),
    // so this polls instead of reading synchronously. One case per multiplayer scenario --
    // add a sibling here alongside a new *AiReplayStateMessage/*State.FromNetworkReplay.
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

    // Guards AiStrats[Session.SelectedAi] indexing below -- StartScenario already refuses to
    // broadcast an out-of-range SelectedAi, but a peer can't verify what the host sent.
    private bool IsValidAiIndex(IScenario scenario) =>
        Session.SelectedAi >= 0 && Session.SelectedAi < scenario.AiStrats.Count;

    // Peer-only, idempotent, edge-triggered once per run: fires once both the host's
    // *AiReplayStateMessage has arrived and our own zone/party is ready
    // (peerEnteredInstance) -- arrival order isn't guaranteed, so both call sites
    // (Dispatch and Tick) funnel through here. No-op unless debug-bot mode is on.
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
                // Chaos/Exdeath may not be replicated yet (WorldSnapshot is an independent
                // flow) -- OnWorldSnapshotReceived keeps retrying this as enemies arrive.
                shadowState.ScenarioObjects.Chaos = peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.ChaosP3);
                shadowState.ScenarioObjects.Exdeath = peerEnemies.Values.FirstOrDefault(e => e.BNpcBaseId == BNpcBaseId.Exdeath);
                debugShadowState = shadowState;
                DebugBotControl.Enabled = true;
                new UmadP3BlackHoleAi().Run(shadowState, world);
                break;
            }
            case UmadP2ForsakenScenario p2Scenario when pendingP2AiReplayState is { } p2Msg:
            {
                // Mark "started" regardless, so an out-of-range SelectedAi doesn't retry
                // (and re-log) every frame -- StartScenario already guards against this.
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
                // A fresh, peer-owned EventScheduler that UmadP5ExaflaresAi schedules its
                // dodges onto -- MultiplayerManager.Tick's P5 branch ticks it every frame,
                // mirroring UmadP5ExaflaresScenario.Tick, which never runs on a peer.
                var shadowState = UmadP5ExaflaresState.FromNetworkReplay(p5Msg.LeftOrder, p5Msg.RightOrder, new EventScheduler());
                debugShadowStateP5 = shadowState;
                DebugBotControl.Enabled = true;
                ((IScenarioAi<UmadP5ExaflaresState>)p5Scenario.AiStrats[Session.SelectedAi]).Run(shadowState, world);
                break;
            }
        }
    }

    // Clears the current run's replay state, not the sticky debugBotControlled toggle --
    // called whenever running stops, so a debug-bot peer regains normal control immediately.
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
