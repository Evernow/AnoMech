using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using AnoMech.Core.Map;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using AnoMech.Scenarios;
using static AnoMech.Core.Game.Game;

namespace AnoMech.Windows;

public unsafe class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool _leftPanelOpen = true;
    internal IScenario? SelectedScenario => _selectedScenario;
    private IScenario? _selectedScenario;

    internal PartyRole? SelectedRoleOverride => _roleOverride;
    private PartyRole? _roleOverride;

    // Index into the selected scenario's AiStrats; reset to the first strat whenever the
    // selected scenario changes. Passed to RunScenario as selectedAi on a (non-solo) Start.
    // -1 when a grouped scenario's selected region has no strats (Start is then gated off).
    internal int SelectedStrat => _selectedStrat;
    private int _selectedStrat;

    // Index into the selected scenario's WaymarkPresets; reset to the first preset when the
    // selected scenario changes. Passed to RunScenario as selectedWaymark on Start. Ignored
    // by scenarios that declare no presets.
    internal int SelectedWaymark => _selectedWaymark;
    private int _selectedWaymark;

    // The region/group label currently selected in the strat picker, for scenarios that
    // declare StratGroups. Null until a grouped scenario is drawn (then it snaps to the
    // first group); stays null for ungrouped scenarios. Filters AiStrats under the buttons.
    private string? _selectedStratGroup;

    // Remembers the last region the user picked per grouped scenario. On a scenario switch
    // _selectedStratGroup is restored from here instead of being reset, so coming back to a
    // scenario keeps its previously selected region rather than snapping to the first.
    private readonly Dictionary<IScenario, string> _stratGroupMemory = new();

    // Index 0 = Auto (null override); indices 1..8 map to (PartyRole)(idx - 1).
    // Labels are the canonical raid role abbreviations: MT/OT tanks, H1/H2 healers
    // (H1 = regen), M1/M2 melee DPS, R1/R2 ranged DPS (R1 = phys).
    private static readonly string[] RoleLabels =
        ["Auto", "MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

#if DEBUG
    private readonly DebugMenu debugMenu;
#endif

    // <Version> from AnoMech.csproj flows into the assembly version; surface it,
    // plus the plugin build checksum (PluginBuildInfo -- the same value the
    // multiplayer handshake compares to catch a host/peer on different
    // builds), in the title bar. Use a ### id so the window identity stays
    // "MainWindow" across versions.
    private static string TitleWithVersion()
        => $"AnoMech v{PluginBuildInfo.Version} ({PluginBuildInfo.ShortChecksum})###MainWindow";

    public MainWindow(Plugin plugin)
        : base(TitleWithVersion())
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(220, 80),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Flags |= ImGuiWindowFlags.AlwaysAutoResize;

        this.plugin = plugin;
        IsOpen = false;

        // Small gear in the title bar opens the settings window (same toggle as /anomech config).
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2f, 1f),
            Click = _ => plugin.ToggleConfigUi(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
#if DEBUG
        debugMenu = new DebugMenu(plugin);
#endif
    }

    public void Dispose() { }

    // Hidden while the fake-zone instance is loaded -- this is a full scenario
    // picker with no reason to stay on screen mid-fight, and RunningSimWindow's
    // compact Start/Reset/Leave/Leave-session substitute (see its own doc
    // comment) covers everything this window's controls would otherwise be
    // needed for. Reopened automatically once back out of the instance, but
    // only if we're the one who closed it (hiddenByUs) -- a user who closed
    // it themselves mid-fight shouldn't have it pop back open on them.
    private bool hiddenByUs;

    public override void PreOpenCheck()
    {
        if (plugin.Game.World.Map.IsInInstance)
        {
            if (IsOpen) hiddenByUs = true;
            IsOpen = false;
        }
        else if (hiddenByUs)
        {
            hiddenByUs = false;
            IsOpen = true;
        }
    }

    public override void Draw()
    {
        var leftWidth = _leftPanelOpen ? ScenarioPanelWidth() : 30f;

        if (ImGui.BeginTable("##layout", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("##left", ImGuiTableColumnFlags.WidthFixed, leftWidth);
            ImGui.TableSetupColumn("##right", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawScenariosPanel();
            ImGui.TableSetColumnIndex(1);
            DrawMainContent();
            ImGui.EndTable();
        }
    }

    // Size the left panel to the widest scenario label so names never clip as scenarios are added.
    private float ScenarioPanelWidth()
    {
        var style = ImGui.GetStyle();
        var widest = 0f;
        foreach (var zone in plugin.Game.Zones)
        {
            widest = Math.Max(widest, ImGui.CalcTextSize(zone.Name).X);
            foreach (var phase in plugin.Game.PhasesOf(zone))
                foreach (var scenario in plugin.Game.ScenariosOf(phase))
                    widest = Math.Max(widest, ImGui.CalcTextSize(DisplayName(scenario)).X);
        }
        var measured = widest + style.FramePadding.X * 2 + style.CellPadding.X * 2;
        return Math.Max(180f, measured);
    }

    private void DrawScenariosPanel()
    {
        if (_leftPanelOpen)
        {
            ImGui.TextUnformatted("Scenarios");
            ImGui.SameLine();
            if (ImGui.SmallButton("<##collapse")) _leftPanelOpen = false;
            ImGui.Separator();

            foreach (var zone in plugin.Game.Zones)
            {
                if (!ImGui.CollapsingHeader(zone.Name, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                ImGui.Indent();
                var mpWindowOpen = plugin.MultiplayerWindow.IsOpen;
                var mpConnected = plugin.Multiplayer.IsConnected;
                foreach (var phase in plugin.Game.PhasesOf(zone))
                    foreach (var scenario in plugin.Game.ScenariosOf(phase))
                    {
                        var selected = _selectedScenario == scenario;
                        var mpUnsupported = (mpWindowOpen || mpConnected)
                            && !MultiplayerManager.SupportedScenarios.Contains(scenario.GetType());
                        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
                        ImGui.PushID(scenario.Name);
                        ImGui.BeginDisabled(mpUnsupported);
                        if (ImGui.Button(DisplayName(scenario), new Vector2(-1, 0)))
                            SelectScenario(scenario);
                        ImGui.EndDisabled();
                        if (mpUnsupported && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            ImGui.SetTooltip($"This scenario doesn't support multiplayer. {MpDisabledReason(mpWindowOpen, mpConnected)}");
                        ImGui.PopID();
                        if (selected) ImGui.PopStyleColor();
                    }
                ImGui.Unindent();
            }
        }
        else
        {
            if (ImGui.Button(">##expand")) _leftPanelOpen = true;
        }
    }

    // Select a scenario and reset its per-scenario UI state (strat, waymark, remembered region).
    private void SelectScenario(IScenario scenario)
    {
        _selectedScenario = scenario;
        _selectedStrat = 0;
        _selectedWaymark = 0;
        // Restore the last region picked for this scenario; null self-heals to its first region when drawn.
        _selectedStratGroup = _stratGroupMemory.GetValueOrDefault(scenario);
    }

    // Explains a control disabled for multiplayer reasons -- shared by every BeginDisabled
    // site keyed on the Multiplayer window being open and/or an active connection, so the
    // wording (and which of the two actually applies) stays consistent everywhere it appears.
    private static string MpDisabledReason(bool windowOpen, bool connected) => (windowOpen, connected) switch
    {
        (true, true) => "Disabled: the Multiplayer window is open and you're connected to a multiplayer session.",
        (true, false) => "Disabled while the Multiplayer window is open.",
        (false, true) => "Disabled while connected to a multiplayer session.",
        _ => "",
    };

    // Distinct, ordered region labels from the strats' IScenarioAi.Group; empty = ungrouped.
    private static IReadOnlyList<string> StratGroups(IScenario scenario)
    {
        var groups = new List<string>();
        foreach (var ai in scenario.AiStrats)
            if (ai.Group is { } g && !groups.Contains(g)) groups.Add(g);
        return groups;
    }

    private void DrawMainContent()
    {
        if (_selectedScenario == null)
        {
            ImGui.TextDisabled("Select a scenario");
            return;
        }

        var game = plugin.Game;

        ImGui.TextUnformatted(FullName(_selectedScenario));
        if (MultiplayerManager.SupportedScenarios.Contains(_selectedScenario.GetType()))
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Multiplayer...")) plugin.MultiplayerWindow.Toggle();
        }
        ImGui.Separator();
        DrawLocationHint();

        // Role is claimed via the Multiplayer window's own buttons once connected, not this
        // selector (which picks which slot the real player occupies in solo play) -- disabled
        // for host and guest alike so it can't drift out of sync with the actual claim. Forced
        // back to Auto (not just disabled) so a role picked before connecting can't silently
        // keep applying underneath the real multiplayer role claim.
        var mpConnectedForRole = plugin.Multiplayer.IsConnected;
        if (mpConnectedForRole) _roleOverride = null;
        ImGui.BeginDisabled(mpConnectedForRole);
        ImGui.BeginGroup();
        DrawRoleSelector();
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpConnectedForRole && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Role is claimed via the Multiplayer window instead. " + MpDisabledReason(false, true));

        // Region/strat only matter as the HOST's choice -- that's what gets broadcast and
        // actually run (see MultiplayerManager.StartScenario). A guest's own local selection
        // here does nothing but could confuse them into thinking they're choosing something.
        // Forced back to the first region/strat (same defaults SelectScenario sets) rather than
        // just disabled, so a guest's dropdown doesn't sit frozen on whatever they'd picked
        // right before someone else started the run.
        var mpGuest = plugin.Multiplayer.IsConnected && !plugin.Multiplayer.IsHost;
        if (mpGuest)
        {
            _selectedStrat = 0;
            _selectedStratGroup = null;
        }
        ImGui.BeginDisabled(mpGuest);
        ImGui.BeginGroup();
        DrawStratSelector();
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpGuest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Only the host's selection is used in multiplayer. " + MpDisabledReason(false, true));
        DrawWaymarkSelector();

        DrawSoloStartButton();
        ImGui.SameLine();
        DrawResetLeaveButtons();

        var inInn = ZoneSession.IsInInn();
        var envReady = inInn && !ZoneSession.IsPlayerBusy();
        var mpBlocked = MultiplayerManager.SupportedScenarios.Contains(_selectedScenario.GetType()) && plugin.Multiplayer.IsConnected;
        if (_selectedScenario.SupportsSolo)
        {
            ImGui.BeginDisabled(!envReady || mpBlocked);
            if (ImGui.Button("Start Solo")) game.RunScenario(_selectedScenario, _roleOverride, selectedAi: null, _selectedWaymark);
            ImGui.EndDisabled();
            if ((!envReady || mpBlocked) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(mpBlocked
                    ? "Connected to a multiplayer session -- use Start in the Multiplayer window instead."
                    : !inInn
                        ? "Scenarios can only be started from an inn."
                        : "Cannot start while you are busy (cutscene, NPC event, crafting, trading, zoning, etc.).");
            }
        }

        // God mode, the speed toggle, and Scenario config all tune solo-play behavior that a
        // multiplayer session doesn't use (see MultiplayerManager.StartScenario/RunScenarioAsHost)
        // -- disabled both while setting up a session (Multiplayer window open) and once one's
        // actually live (IsConnected), not just one or the other.
        var mpWindowOpen = plugin.MultiplayerWindow.IsOpen;
        var mpConnected = plugin.Multiplayer.IsConnected;
        var mpActive = mpWindowOpen || mpConnected;
        // Forced to its default (not just disabled) so a value left on from before the
        // Multiplayer window opened / a session connected can't keep silently applying.
        if (mpActive) game.GodMode = false;
        ImGui.BeginDisabled(mpActive);
        var god = game.GodMode;
        if (ImGui.Checkbox("God mode", ref god)) game.GodMode = god;
        ImGui.EndDisabled();
        if (mpActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(MpDisabledReason(mpWindowOpen, mpConnected));

#if DEBUG
        if (mpActive) game.EventTimeScale = 1f;
        ImGui.BeginDisabled(mpActive);
        ImGui.BeginGroup();
        debugMenu.DrawSpeedControl();
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(MpDisabledReason(mpWindowOpen, mpConnected));
#endif

        if (game.Paused) ImGui.TextDisabled("(scenario paused — press Reset to clear)");

        ImGui.Spacing();
        ImGui.BeginDisabled(mpActive);
        ImGui.BeginGroup();
        if (ImGui.CollapsingHeader("Scenario config", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            _selectedScenario.DrawSettings();
            ImGui.Unindent();
        }
        ImGui.EndGroup();
        ImGui.EndDisabled();
        if (mpActive && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(MpDisabledReason(mpWindowOpen, mpConnected));

#if DEBUG
        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Debug"))
        {
            debugMenu.DrawDebugContent();
        }
#endif
    }

    // Solo/AI Start button. Self-contained (recomputes its own env/strat checks
    // rather than taking them as parameters) so RunningSimWindow can call this
    // directly while a sim is running, not just DrawMainContent above.
    internal void DrawSoloStartButton()
    {
        if (_selectedScenario == null) return;
        var inInn = ZoneSession.IsInInn();
        var busy = ZoneSession.IsPlayerBusy();
        var envReady = inInn && !busy;
        var hasStrat = HasStartableStrat();
        // This calls game.RunScenario directly -- the plain solo/AI path, entirely
        // bypassing MultiplayerManager. While connected to a multiplayer session for
        // this scenario, that path must be blocked: the host clicking this instead
        // of the Multiplayer window's own Start button would run a real fight
        // locally without ever sending a StartMessage, leaving every guest stuck on
        // "waiting for the host to start" forever; a peer clicking it would start a
        // second, fully independent local simulation instead of waiting for the
        // host's broadcast.
        var mpBlocked = MultiplayerManager.SupportedScenarios.Contains(_selectedScenario.GetType()) && plugin.Multiplayer.IsConnected;
        var canStart = envReady && hasStrat && !mpBlocked;
        ImGui.BeginDisabled(!canStart);
        if (ImGui.Button("Start")) plugin.Game.RunScenario(_selectedScenario, _roleOverride, _selectedStrat, _selectedWaymark);
        ImGui.EndDisabled();
        if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(mpBlocked
                ? "Connected to a multiplayer session -- use Start in the Multiplayer window instead."
                : !inInn
                    ? "Scenarios can only be started from an inn."
                    : busy
                        ? "Cannot start while you are busy (cutscene, NPC event, crafting, trading, zoning, etc.)."
                        : "No strat available for this region yet.");
        }
    }

    // Reset, plus (while in-instance) Leave -- redirecting through the host for a
    // connected peer exactly like MultiplayerWindow's own Leave-session button
    // does. Self-contained like DrawSoloStartButton above, for the same reason.
    internal void DrawResetLeaveButtons()
    {
        var game = plugin.Game;
        // A peer's own Game.Reset() would only clear their own local view --
        // route through the host instead so a reset reaches the whole group.
        // The host's own click needs no such redirect: it's already
        // authoritative and already propagates via Tick()/EndMessage.
        if (ImGui.Button("Reset"))
        {
            if (plugin.Multiplayer.IsConnected && !plugin.Multiplayer.IsHost)
                plugin.Multiplayer.RequestReset();
            else
                game.Reset();
        }
        if (game.World.Map.IsInInstance)
        {
            ImGui.SameLine();
            // Same redirect reasoning as Reset above: a peer's own Game.Leave()
            // would only unload their own local zone, leaving the host still
            // simulating/broadcasting to a puppet-driven world they've since
            // torn down. Routing through the host ends the run for the whole
            // group (see MultiplayerManager.RequestLeaveInstance) while
            // leaving the session itself intact.
            if (ImGui.Button("Leave"))
            {
                if (plugin.Multiplayer.IsConnected && !plugin.Multiplayer.IsHost)
                    plugin.Multiplayer.RequestLeaveInstance();
                else
                {
                    game.Leave();
                    // A prior Reset already consumed the one-shot Tick() edge trigger
                    // that would normally broadcast this -- without an explicit call
                    // here, peers never learn the host left and get stuck in-instance.
                    // See MultiplayerManager.NotifyLeftInstance's doc comment.
                    plugin.Multiplayer.NotifyLeftInstance();
                }
            }
        }
    }

    // Drawn below the strat picker for scenarios that declare WaymarkPresets. _selectedWaymark
    // is the index passed to RunScenario on Start; changing it while a scenario is loaded
    // re-places the markers immediately (same live-feedback loop as the position readout).
    private void DrawWaymarkSelector()
    {
        if (_selectedScenario is null) return;
        var presets = _selectedScenario.Phase.Zone.WaymarkPresets;
        if (presets.Count == 0) return;
        if (_selectedWaymark < 0 || _selectedWaymark >= presets.Count) _selectedWaymark = 0;

        var labels = new string[presets.Count];
        for (var i = 0; i < presets.Count; i++) labels[i] = presets[i].Name;

        ImGui.TextUnformatted("Waymarks:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("##waymarks", ref _selectedWaymark, labels, labels.Length)
            && plugin.Game.World.Map.IsInInstance)
            plugin.Game.World.PlaceWaymarks(presets[_selectedWaymark].Markers);
    }

    private void DrawRoleSelector()
    {
        var idx = _roleOverride is { } role ? (int)role + 1 : 0;
        ImGui.TextUnformatted("Select your Role:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo("##role", ref idx, RoleLabels, RoleLabels.Length))
            _roleOverride = idx == 0 ? null : (PartyRole)(idx - 1);
    }

    // Only meaningful when a scenario offers more than one strat; hidden otherwise.
    // When the scenario declares StratGroups, a region-button row is drawn above the
    // dropdown and the dropdown is filtered to the selected region.
    private void DrawStratSelector()
    {
        if (_selectedScenario is null) return;
        var strats = _selectedScenario.AiStrats;
        var groups = StratGroups(_selectedScenario);
        if (groups.Count > 0)
        {
            DrawGroupedStratSelector(strats, groups);
            return;
        }

        if (strats.Count <= 1) return;
        _selectedStrat = Math.Clamp(_selectedStrat, 0, strats.Count - 1);
        var labels = new string[strats.Count];
        for (var i = 0; i < strats.Count; i++) labels[i] = strats[i].Name;
        ImGui.TextUnformatted("Select Strat:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280);
        ImGui.Combo("##strat", ref _selectedStrat, labels, labels.Length);
    }

    // Region buttons + a region-filtered strat dropdown. _selectedStrat stays an
    // absolute index into AiStrats (what RunScenario consumes); it is reconciled here
    // each frame to the selected region, or set to -1 when that region has no strats.
    private void DrawGroupedStratSelector(IReadOnlyList<IScenarioAi> strats, IReadOnlyList<string> groups)
    {
        if (!GroupsContain(groups, _selectedStratGroup))
            _selectedStratGroup = groups[0];

        ImGui.TextUnformatted("Region:");
        for (var i = 0; i < groups.Count; i++)
        {
            ImGui.SameLine();
            var group = groups[i];
            var selected = _selectedStratGroup == group;
            if (selected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));
            ImGui.PushID($"region{i}");
            if (ImGui.Button(group))
            {
                _selectedStratGroup = group;
                _stratGroupMemory[_selectedScenario!] = group; // remember across scenario switches
            }
            ImGui.PopID();
            if (selected) ImGui.PopStyleColor();
        }

        var filtered = new List<int>();
        for (var i = 0; i < strats.Count; i++)
            if (strats[i].Group == _selectedStratGroup) filtered.Add(i);

        ImGui.TextUnformatted("Select Strat:");
        ImGui.SameLine();
        if (filtered.Count == 0)
        {
            _selectedStrat = -1;
            ImGui.TextDisabled("(no strats for this region yet)");
            return;
        }

        if (!filtered.Contains(_selectedStrat)) _selectedStrat = filtered[0];
        var localIdx = filtered.IndexOf(_selectedStrat);
        var labels = new string[filtered.Count];
        for (var i = 0; i < filtered.Count; i++) labels[i] = strats[filtered[i]].Name;
        ImGui.SetNextItemWidth(280);
        if (ImGui.Combo("##strat", ref localIdx, labels, labels.Length))
            _selectedStrat = filtered[localIdx];
    }

    // True when Start may run a strat: ungrouped scenarios are always fine; grouped
    // scenarios require the current selection to be a real strat in the active region.
    // internal (not private): MultiplayerManager.StartScenario reuses this exact check
    // before broadcasting SelectedAi -- an out-of-range/-1 index would throw when a
    // debug-bot peer later indexes AiStrats[SelectedAi] (see TryStartDebugBotReplay).
    internal bool HasStartableStrat()
    {
        if (_selectedScenario is not { } scenario) return false;
        if (StratGroups(scenario).Count == 0) return true;
        var strats = scenario.AiStrats;
        return _selectedStrat >= 0 && _selectedStrat < strats.Count
            && strats[_selectedStrat].Group == _selectedStratGroup;
    }

    private static bool GroupsContain(IReadOnlyList<string> groups, string? group)
    {
        if (group is null) return false;
        for (var i = 0; i < groups.Count; i++)
            if (groups[i] == group) return true;
        return false;
    }

    private void DrawLocationHint()
    {
        if (ZoneSession.IsInInn()) return;
        ImGui.TextDisabled("Scenarios only run in an inn");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Scenarios can only be started from an inn — return to one to run a scenario.");
    }
}
