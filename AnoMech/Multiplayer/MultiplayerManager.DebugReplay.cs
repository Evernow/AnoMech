using AnoMech.Core;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;

namespace AnoMech.Multiplayer;

public sealed partial class MultiplayerManager
{
    // ---- Debug: bot-controlled peer replay ----------------------------------

    // Host-only, edge-triggered once per run. Polls rather than reading synchronously since
    // LastState isn't guaranteed set on the very first Tick() after StartScenario
    // (RunScenarioAsHost's real work is deferred a frame).
    private void TrySendAiReplayState()
    {
        if (Plugin.GameInstance.Scenarios[Session.ScenarioIndex] is not IMultiplayerReplayable replayable) return;
        if (replayable.BuildReplayStateMessage() is not { } msg) return;
        aiReplayStateSent = true;
        DiagnosticLog.Info("[Multiplayer] Host: broadcasting AiReplayState for this run.");
        _ = relay!.SendAsync(msg);
    }

    // Guards AiStrats[Session.SelectedAi] indexing below -- StartScenario already refuses to
    // broadcast an out-of-range SelectedAi, but a peer can't verify what the host sent.
    private bool IsValidAiIndex(IScenario scenario) =>
        Session.SelectedAi >= 0 && Session.SelectedAi < scenario.AiStrats.Count;

    // Peer-only, idempotent, edge-triggered once per run: fires once both the host's replay
    // message has arrived and our own zone/party is ready (peerEnteredInstance) -- arrival
    // order isn't guaranteed, so both call sites (Dispatch and Tick) funnel through here.
    // No-op unless debug-bot mode is on.
    private void TryStartDebugBotReplay()
    {
        if (!debugBotControlled || debugBotReplayStarted) return;
        if (!peerEnteredInstance) return;
        if (MyClaimedRole is not { } myRole) return;
        if (pendingGenericReplayState is not { } msg) return;
        if (Plugin.GameInstance.Scenarios[Session.ScenarioIndex] is not IMultiplayerReplayable replayable) return;
        var world = Plugin.GameInstance.World;

        debugBotReplayStarted = true;
        if (!IsValidAiIndex(replayable))
        {
            DiagnosticLog.Warn($"[Multiplayer] Peer: SelectedAi {Session.SelectedAi} is out of range for {replayable.Name} ({replayable.AiStrats.Count} strats) -- skipping debug-bot replay.");
            return;
        }
        DiagnosticLog.Info($"[Multiplayer] Peer: starting debug-bot replay for {myRole}.");
        var shadowState = replayable.StartReplay(msg, Session.SelectedAi, myRole, world);
        if (shadowState != null)
        {
            debugShadowStateGeneric = shadowState;
            DebugBotControl.Enabled = true;
        }
    }

    // Clears the current run's replay state, not the sticky debugBotControlled toggle --
    // called whenever running stops, so a debug-bot peer regains normal control immediately.
    private void StopDebugBotReplay()
    {
        if (debugBotReplayStarted) DiagnosticLog.Info("[Multiplayer] Peer: stopping debug-bot replay.");
        DebugBotControl.Enabled = false;
        pendingGenericReplayState = null;
        debugShadowStateGeneric = null;
        debugBotReplayStarted = false;
    }

}
