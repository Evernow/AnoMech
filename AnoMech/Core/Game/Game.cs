using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Map;
using AnoMech.Core.Native;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Top.P2PartySynergy;
using AnoMech.Scenarios.Top.P5Delta;
using AnoMech.Scenarios.Top.P5Omega;
using AnoMech.Scenarios.Top.P5Sigma;
using AnoMech.Scenarios.Top.P6WaveCannon2;
using AnoMech.Scenarios.Umad;
using AnoMech.Scenarios.Umad.P2Forsaken;
using AnoMech.Scenarios.Umad.P3BlackHole;
using AnoMech.Scenarios.Umad.P4KefkaSays;
using AnoMech.Scenarios.Umad.P5Exaflares;
using AnoMech.Scenarios.Uwu.UltimatePredation;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AnoMech.Core.Game;

// High-level orchestrator: owns the World, holds the scenario catalog, drives
// the active scenario's lifecycle, and is the single entry point UI talks to.
public sealed class Game : IDisposable
{
    // EventObj for the duty Exit portal — hidden on every scenario start so the
    // teleport-out interactable doesn't sit inside the simulated arena.
    private const uint ExitObjectBaseId = 2000139;

    public EventScheduler Events { get; } = new();
    public SimWorld World { get; }
    public SimPlayer? Player => World.Party.Player;
    // Null once Reset/Leave clears it -- MultiplayerManager's host-side tick reads
    // this to stop broadcasting once the multiplayer run has ended locally.
    public IScenario? ActiveScenario => activeScenario;
    // Flat registry; the zone -> phase -> scenario tree is derived from it in
    // first-appearance order.
    public IReadOnlyList<IScenario> Scenarios { get; }
    public IReadOnlyList<IZone> Zones { get; }
    private readonly Dictionary<IZone, List<IPhase>> phasesByZone = new();
    private readonly Dictionary<IPhase, List<IScenario>> scenariosByPhase = new();
    public Bgm Bgm { get; } = new();

    // Fixed scenario-local player spawn (16y south of centre).
    public static readonly Vector3 PlayerSpawnLocal = new(0f, 0f, 16f);

    // Multiplier applied only to the EventScheduler's delta. Intentionally does not
    // scale enemy/party/tether/status ticks so cast bars, animations, and movement
    // run at real time — only the timeline of scheduled events stretches/compresses.
    public float EventTimeScale { get; set; } = 1f;

    // Set by Game.Kill once the post-first-death freeze timer fires. While true,
    // Tick is a no-op so scenario events, scheduler, and world all stop.
    public bool Paused { get; set; }

    // When true, Game.Kill still posts the chat line for learning but skips every
    // gameplay side effect (HP=0, KO timeline, stun hooks, freeze timer).
    public bool GodMode { get; set; }

    private IScenario? activeScenario;
    private float scenarioElapsed;
    private bool firstDeathScheduled;
    private bool firstFreezeScheduled;
    private readonly OpcodeUpdater opcodeUpdater;

#if DEBUG
    // Keeps AnoMech-DamageDebug.txt reflecting near-live state through Tick, not
    // just the one auto-freeze on first death -- a run where nobody dies but
    // something still visibly went wrong needs the same trace available. Tracked
    // independently of activeScenario since peers never get one (see isPeer guard
    // in RunScenarioInternal) but still need their own local dump kept fresh.
    private const float PeriodicDumpInterval = 3f;
    private float periodicDumpTimer;
    private bool peerScenarioRunning;
#endif

    public Game()
    {
        World = new SimWorld(Events);
        opcodeUpdater = new OpcodeUpdater();
        Scenarios = new IScenario[]
        {
            new UmadP2ForsakenScenario(),
            new UmadP3BlackHoleScenario(),
            new UmadP4KefkaSaysScenario(),
            new UmadP5ExaflaresScenario(),
            new UmadP5ForsakenNull(),
            new TopP2PartySynergyScenario(),
            new TopP5DeltaScenario(),
            new TopP5SigmaScenario(),
            new TopP5OmegaScenario(),
            new TopP6WaveCannon2Scenario(),
            new UltimatePredationScenario()
        };

        // Derive the zone tree from the flat registry (first-appearance order).
        var zoneOrder = new List<IZone>();
        foreach (var scenario in Scenarios)
        {
            var phase = scenario.Phase;
            var zone = phase.Zone;
            if (!phasesByZone.TryGetValue(zone, out var phases))
            {
                phases = new List<IPhase>();
                phasesByZone[zone] = phases;
                zoneOrder.Add(zone);
            }
            if (!phases.Contains(phase)) phases.Add(phase);
            if (!scenariosByPhase.TryGetValue(phase, out var phaseScenarios))
            {
                phaseScenarios = new List<IScenario>();
                scenariosByPhase[phase] = phaseScenarios;
            }
            phaseScenarios.Add(scenario);
        }
        Zones = zoneOrder;
    }

    // Derived zone-tree accessors, in registry order.
    public IReadOnlyList<IPhase> PhasesOf(IZone zone) => phasesByZone[zone];
    public IReadOnlyList<IScenario> ScenariosOf(IPhase phase) => scenariosByPhase[phase];

    // selectedAi: index into the scenario's AiStrats of the strat to run, or null for
    // solo (no doppels, no AI). Defaults to 0 = run the first strat with a full party.
    // selectedWaymark: index into the scenario's WaymarkPresets; ignored when it has none.
    public void RunScenario(IScenario scenario, PartyRole? roleOverride = null, int? selectedAi = 0, int selectedWaymark = 0)
    {
        Plugin.Framework.Run(() => RunScenarioInternal(scenario, roleOverride, selectedAi, selectedWaymark, null, isPeer: false));
    }

    // Multiplayer host: same as RunScenario but `networkRoles` (claimed by joined
    // peers) get a SimNetworkPuppet instead of an AI bot. The host still runs the
    // full scenario/AI/DamageSolver simulation unmodified — see MultiplayerManager.
    public void RunScenarioAsHost(IScenario scenario, PartyRole roleOverride, int selectedAi, int selectedWaymark, IReadOnlySet<PartyRole> networkRoles)
    {
        Plugin.Framework.Run(() => RunScenarioInternal(scenario, roleOverride, selectedAi, selectedWaymark, networkRoles, isPeer: false));
    }

    // Multiplayer peer: loads the same cosmetic zone/party/waymarks as the host but
    // never calls zone.Run/phase.Run/scenario.Run — no local RNG, AI, or DamageSolver.
    // Every slot other than the peer's own is a SimNetworkPuppet driven entirely by
    // WorldSnapshot messages from the host (see MultiplayerManager).
    public void RunScenarioAsPeer(IScenario scenario, PartyRole roleOverride, int selectedWaymark, IReadOnlySet<PartyRole> networkRoles)
    {
        Plugin.Framework.Run(() => RunScenarioInternal(scenario, roleOverride, null, selectedWaymark, networkRoles, isPeer: true));
    }

    // Fired whenever Kill actually takes a party slot down (gameplay side effects ran,
    // i.e. the same condition that makes Kill return true) — host-side hook for
    // MultiplayerManager to broadcast RoleKilled to peers. Not raised by a peer's own
    // reactive Kill calls (peers only ever call Kill in response to a received
    // RoleKilled, so re-broadcasting it would just echo the message back).
    public event Action<PartyRole, string>? PartyMemberKilled;

    // The selected preset, or [0] as the default.
    private static IReadOnlyList<Waymark> ResolveWaymarks(IZone zone, int selectedWaymark)
    {
        var presets = zone.WaymarkPresets;
        if (selectedWaymark >= 0 && selectedWaymark < presets.Count)
            return presets[selectedWaymark].Markers;
        return presets[0].Markers;
    }

    private void RunScenarioInternal(IScenario scenario, PartyRole? roleOverride, int? selectedAi, int selectedWaymark, IReadOnlySet<PartyRole>? networkRoles, bool isPeer)
    {
        var solo = selectedAi is null;
        var phase = scenario.Phase;
        var zone = phase.Zone;
        // Hard gate: scenarios are only ever run from an inn. Everything
        // downstream (CharacterManager registration, zone load, doppel spawn)
        // assumes that invariant.
        if (!ZoneSession.IsInInn())
        {
            Plugin.Log.Warning("Game: scenarios can only run from an inn; aborting.");
            return;
        }

        ResetInternal();

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            Plugin.Log.Warning("Game: no local player; aborting scenario start");
            return;
        }

        // Captured before TryLoad: false only on the first start from the inn (a true
        // zone entry), true for any restart/switch within the already-loaded zone.
        var freshLoad = !World.Map.IsZoneLoaded;

        World.HideObject(ExitObjectBaseId);
        World.Map.TryLoad(
            new TargetInstance(zone.TerritoryId, zone.Origin, zone.Origin + PlayerSpawnLocal, phase.Weather),
            zone.Level, zone.ItemLevel);
        World.ScenarioOrigin = zone.Origin;
        World.Map.ArmColliderDrops(zone.ColliderRemovalPoints.Select(World.Coordinates.ToGlobal));
        World.PlaceWaymarks(ResolveWaymarks(zone, selectedWaymark));
        World.CreateParty(player.ClassJob.RowId, roleOverride, solo, networkRoles);
#if DEBUG
        // Cleared here (not just inside scenario.Run, which peers skip below) so a
        // peer's own diagnostic dump starts fresh per run too, same as the host's.
        AnoMech.Core.DiagnosticLog.Clear();
#endif
        // A peer runs no scenario logic at all (no RNG, AI, or DamageSolver) — its
        // arena boundary, boss timeline, and mechanic resolution all come from the
        // host via WorldSnapshot/RoleKilled instead. zone.Run creates the
        // SimArenaBoundary the out-of-arena check below reads, so peers skip that
        // check too (TeleportPlayerToSpawnIfOutsideArena no-ops with no boundary).
        if (!isPeer)
        {
            zone.Run(World);
            phase.Run(World);
            scenario.Run(World, selectedAi);
        }
        // Outside the isPeer guard above: RunInstanceEvents carries no RNG/AI/
        // DamageSolver dependency, so both host and peer schedule it locally
        // (host doesn't get it a second time -- Run doesn't call it itself).
        scenario.RunInstanceEvents(World);
        // Entering the zone always starts at spawn; a restart only recenters the player
        // if they're standing outside the arena ring (otherwise they keep their position).
        if (freshLoad)
            TeleportPlayerToSpawn();
        else
            TeleportPlayerToSpawnIfOutsideArena();
        ResetSprintCooldown();
        if (!isPeer)
        {
            activeScenario = scenario;
            scenarioElapsed = 0f;
        }
#if DEBUG
        else
        {
            // Peers never get activeScenario (Tick must not call scenario.Tick locally
            // for them -- see the isPeer guard above), but the periodic dump still
            // needs to know a run is in progress; this tracks that independently.
            peerScenarioRunning = true;
        }
#endif

        // Reconcile BGM to the new scenario. Bgm.Play is idempotent, so switching
        // between same-track scenarios (e.g. the P5 phases) keeps playing without
        // restarting the song; a different track swaps; suppressed/no-track reverts.
        if (Plugin.Config.SuppressBgm || phase.Bgm == 0)
            Bgm.Reset();
        else
            Bgm.Play(phase.Bgm);

        Plugin.ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.SystemMessage,
            Message = new SeStringBuilder().AddText($"[AnoMech] Starting: {FullName(scenario)}{(solo ? " (Solo)" : "")}").Build(),
        });
    }

    // Sprint goes on cooldown when the player presses it inside a scenario
    // (LocalPlayerInputHooks lets Original run so the recast starts). Clear it
    // here so each scenario starts with Sprint ready, regardless of whether
    // the player pressed it just before clicking Start.
    private static unsafe void ResetSprintCooldown()
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        var group = am->GetRecastGroup((int)ActionType.Action, LocalPlayerInputHooks.SprintActionId);
        if (group < 0) return;
        var detail = am->GetRecastGroupDetail(group);
        if (detail == null) return;
        detail->IsActive = false;
        detail->Elapsed = 0f;
    }

    public void Tick(float deltaSeconds)
    {
        if (Paused) return;
        Events.Tick(deltaSeconds * EventTimeScale);
        World.Tick(deltaSeconds);
        if (activeScenario != null)
        {
            scenarioElapsed += deltaSeconds;
            activeScenario.Tick(deltaSeconds, scenarioElapsed);
        }
#if DEBUG
        // Runs for host (activeScenario) and peer (peerScenarioRunning) alike, so
        // both sides of a multiplayer test keep their own local dump file fresh --
        // see RunScenarioInternal for why peers never get an activeScenario.
        if (activeScenario != null || peerScenarioRunning)
        {
            periodicDumpTimer += deltaSeconds;
            if (periodicDumpTimer >= PeriodicDumpInterval)
            {
                periodicDumpTimer = 0f;
                AnoMech.Windows.DamageDebugWindow.Instance?.DumpToFile();
            }
        }
#endif
    }

    // Godmode preview: how long a swallowed-death HP-bar drop stays down before healing back.
    private const float GodmodeHealSeconds = 1.2f;

    // Single entry point for "this character died". Always posts the cause
    // to chat and, on the first call of a run, fires the on-screen overlay
    // — both happen even in godmode so the user can learn what would have
    // killed them. Gameplay side effects (OnKilled, which flips Dead, plus
    // the 5s freeze) only run outside godmode; the freeze fires once per run
    // on the first non-godmode death.
    //
    // Returns true only when the member actually went down (OnKilled ran):
    // false when it was already dead, invulnerable (GiveInvuln), or godmode
    // swallowed it. Callers that run extra on-death logic should gate on this
    // so an invuln'd/godmode'd "death" doesn't trigger gameplay consequences.
    public bool Kill(ISimPartyMember target, string cause)
    {
        if (target == null) return false;
        if (target.Dead) return false;
        if (target is SimCharacter sc && sc.HasStatus(SimParty.InvulnStatusId))
        {
            Plugin.Log.Info($"[Invuln] {DescribeName(target)} survived: {cause}");
            AnoMech.Core.DiagnosticLog.Info($"[Game] Kill: {target.Role} survived via Invuln -- {cause}");
            return false;
        }

        AnoMech.Core.DiagnosticLog.Warn(
            $"[Game] Kill: {target.Role} died at ({(target as IPositioned)?.Position.X:F1},{(target as IPositioned)?.Position.Z:F1}) -- {cause}");
        PrintDeath(target, cause);
        if (!firstDeathScheduled)
        {
            firstDeathScheduled = true;
            ShowFirstDeathOverlay(target, cause);
        }

        if (GodMode)
        {
            // Godmode swallows the death but still previews it: drop the player's bar and heal it
            // back a beat later. Done here rather than OnKilled (which godmode skips) so every
            // scenario gets it. Rides Game.Events; the ~1.2s restore is cosmetic, so scaling is moot.
            if (target is SimPlayer player)
            {
                player.DropHpBar();
                Events.Add(GodmodeHealSeconds, player.RestoreHpBar);
            }
            return false;
        }
        target.OnKilled();
        PartyMemberKilled?.Invoke(target.Role, cause);
        if (!firstFreezeScheduled)
        {
            firstFreezeScheduled = true;
#if DEBUG
            AnoMech.Windows.DamageDebugWindow.Instance?.Freeze();
#endif
            Events.Add(5f, () => Paused = true);
        }
        return true;
    }

    private static void PrintDeath(ISimPartyMember target, string cause)
    {
        Plugin.ChatGui.Print(new XivChatEntry
        {
            Type = XivChatType.SystemMessage,
            Message = new SeStringBuilder().AddText($"[AnoMech] {DescribeName(target)} died: {cause}").Build(),
        });
    }

    private static string DescribeName(ISimPartyMember target) => target switch
    {
        SimPlayer => "You",
        SimPartyNpc pm => pm.DisplayName,
        SimNetworkPuppet pm => pm.DisplayName,
        _ => "Character",
    };

    private static unsafe void ShowFirstDeathOverlay(ISimPartyMember target, string cause)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        ui->ShowErrorText($"{DescribeName(target)} died: {cause}", true);
    }

    public void Reset() => Plugin.Framework.Run(() =>
    {
        if (activeScenario is not null)
            TeleportPlayerToSpawnIfOutsideArena();
        ResetInternal();
        Bgm.Reset();
    });

    // Pull the player back to the scenario's spawn point only if they're standing
    // outside the arena ring (e.g. knocked out of bounds, or wandered off). No-op
    // when the scenario enforces no boundary. Reads the live game-object position:
    // at scenario start the SimPlayer was just created and hasn't ticked, so its
    // cached Position is still zero. At reset this must run before ResetInternal
    // clears Party / ScenarioOrigin.
    private void TeleportPlayerToSpawnIfOutsideArena()
    {
        var lp = Plugin.ObjectTable.LocalPlayer;
        if (lp == null) return;
        if (!World.IsOutsideArena(World.Coordinates.ToLocal(lp.Position))) return;
        TeleportPlayerToSpawn();
    }

    // ScenarioOrigin must already be set (SetPosition resolves local -> world through it).
    private void TeleportPlayerToSpawn() => Player?.SetPosition(PlayerSpawnLocal);

    // Menu label, e.g. "P5 Delta".
    public static string DisplayName(IScenario scenario)
    {
        var phase = scenario.Phase;
        return string.IsNullOrEmpty(phase.Name) ? scenario.Name : $"{phase.Name} {scenario.Name}";
    }

    public static string FullName(IScenario scenario)
        => $"{scenario.Phase.Zone.Name} — {DisplayName(scenario)}";

    // Leave returns to the inn. Only meaningful when IsInInstance is true.
    // Resets the encounter first, then reverts the zone — Reset stays in-zone.
    public void Leave()
    {
        Plugin.Framework.Run(() =>
        {
            ResetInternal();
            Bgm.Reset();
            World.Map.Unload();
        });
    }

    private void ResetInternal()
    {
        activeScenario = null;
        scenarioElapsed = 0f;
        Events.Clear();
        World.Despawn();
        // BGM is owned by the callers: a scenario start reconciles it to the new
        // track (keeping it playing when unchanged); Reset/Leave stop it. Resetting
        // here would force a same-track restart on every scenario switch.

        Paused = false;
        firstDeathScheduled = false;
        firstFreezeScheduled = false;
#if DEBUG
        periodicDumpTimer = 0f;
        peerScenarioRunning = false;
        AnoMech.Windows.DamageDebugWindow.Instance?.ResetFreeze();
#endif
        // Input-lock flags are owned by SimPlayer (reconciled each tick, cleared on
        // its Despawn during World.Reset above) — nothing to clear here.
    }

    // Plugin.Dispose is invoked on the framework thread during unload — run
    // teardown synchronously here. The previous Framework.Run wrapper queued
    // the lambda for the *next* tick, which never fired during shutdown and
    // leaked all six LocalPlayerInputHooks hooks.
    public void Dispose()
    {
        activeScenario = null;
        Events.Clear();
        Bgm.Dispose();
        World.Dispose();
        opcodeUpdater.Dispose();
    }
}
