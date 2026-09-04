using System.Collections.Generic;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios;

// The mechanic timeline for one encounter fragment. Shared identity lives on the owning
// IZone (via Phase.Zone) and IPhase; a scenario declares only what is its own.
public interface IScenario
{
    string Name { get; }

    // Its phase — usually `=> TopZone.P5`.
    IPhase Phase { get; }

    bool SupportsSolo => false;

    // True once a scenario has the matching multiplayer plumbing in
    // MultiplayerManager/Protocol.cs (per-scenario *AiReplayStateMessage,
    // shadow-state field, dispatch case) -- core replication works for any
    // IScenario automatically, but debug-bot AI replay doesn't.
    bool SupportsMultiplayer => false;

    // Selectable strats. Run's selectedAi indexes this (null = solo); region buttons derive
    // from each strat's IScenarioAi.Group.
    IReadOnlyList<IScenarioAi> AiStrats { get; }

    // Tankbusters this scenario wants the multiplayer host to be able to pre-plan
    // mitigation for (see TankMitigation) -- empty for scenarios that don't use it yet.
    IReadOnlyList<TankBusterCastInfo> TankBusters => [];

    // Null (default): tank doppels/puppets and a real player in a tank role spawn at the
    // generic doppel HP. A scenario wanting tanks to carry a specific reference HP instead
    // (e.g. so TankMitigation's fixed-HP tankbuster numbers land against a real tank's real
    // max HP) overrides this with that value.
    uint? TankMaxHealth => null;

    void Run(SimWorld world, int? selectedAi);
    void Tick(float delta, float elapsed) { }
    void DrawSettings() { }

    // Settings that stay editable while the Multiplayer window is open, unlike DrawSettings
    // (solo-only overrides get disabled there) -- e.g. pre-planning a bot tank's behavior
    // (UmadP3BlackHoleSettingsWindow.DrawThunderIIIPlan), which only matters in multiplayer.
    void DrawMultiplayerSettings() { }

    // Deterministic, RNG-independent instance-progress replay (native
    // DirectorUpdate/AddEffect calls) a scenario may schedule alongside its
    // boss timeline. Unlike Run, this has no dependency on host-only state
    // (random assignments, AI, DamageSolver), so Game.RunScenarioInternal
    // calls it for a peer too, not just the host.
    void RunInstanceEvents(SimWorld world) { }
}
