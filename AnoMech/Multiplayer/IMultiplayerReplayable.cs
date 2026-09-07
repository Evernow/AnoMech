using System.Collections.Generic;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;

namespace AnoMech.Multiplayer;

// Implemented by a scenario that owns its own debug-bot-replay wiring, instead of
// MultiplayerManager switching on its concrete type. The generic dispatch calling these lives
// in MultiplayerManager.DebugReplay.cs/.Dispatch.cs/.HostSnapshot.cs/.PeerSnapshot.cs/.Tick.cs
// and needs no changes to support one more scenario.
public interface IMultiplayerReplayable : IScenario
{
    // Host-only, polled once per tick until non-null (LastState isn't set on the very first
    // post-Start tick). Null also if something the message needs hasn't resolved yet (e.g. a
    // live-RNG tie-break the Ai hasn't rolled).
    MpMessage? BuildReplayStateMessage();

    // Peer-only, called once with the broadcast message and which role is being replayed.
    // Reconstruct a shadow state and start the given Ai strat against it. Return the shadow
    // state (opaque -- only this scenario's own methods below ever see it again) so the caller
    // can enable debug-bot control; null if `message`/`aiIndex` don't check out.
    object? StartReplay(MpMessage message, int aiIndex, PartyRole myRole, SimWorld world);

    // Optional, host-only, polled once per tick after the initial replay state -- for something
    // only resolved mid-run, not knowable in BuildReplayStateMessage's one-shot broadcast. Track
    // your own "last broadcast value" to stay edge-triggered. Default: nothing to re-sync.
    MpMessage? BuildMidRunUpdateMessage() => null;

    // Optional, peer-only, called against the active shadow state whenever a
    // BuildMidRunUpdateMessage-produced message arrives. Default: no mid-run updates expected.
    void ApplyMidRunUpdate(object shadowState, MpMessage message) { }

    // Optional, peer-only, called against the active shadow state every snapshot -- re-resolve
    // a live SimEnemy handle that might not have replicated yet when StartReplay first ran, or
    // rebuild other world-derived data the replay depends on. Default: nothing to refresh.
    void RefreshLiveHandles(object shadowState, IReadOnlyDictionary<int, SimEnemy> peerEnemies) { }

    // Optional, peer-only, called every frame while a replay is active -- for an Ai that
    // schedules onto its own private EventScheduler rather than world.Events (which already
    // ticks for peers). Default: nothing to drive.
    void TickReplay(object shadowState, float deltaSeconds) { }
}
