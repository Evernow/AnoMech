using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static AnoMech.Core.Game.Game;

namespace AnoMech.Windows;

// Vertical-slice multiplayer lobby. Which scenario is hosted is whatever's
// currently selected in MainWindow (see MultiplayerManager.SupportedScenarios /
// StartScenario) -- this window has no scenario picker of its own, it only
// shows the name. Host picks a role, shares the session code + relay URL out
// of band, up to 7 others join and claim the remaining roles; anything left
// unclaimed stays an AI bot exactly like solo play.
public class MultiplayerWindow : Window, IDisposable
{
    private static readonly string[] RoleLabels = ["MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

    private readonly Plugin plugin;
    private readonly MultiplayerManager mp;
    private string relayUrl;
    private string joinCode = "";
    private string displayName = "Player";
    private bool namePrefilled;

    public MultiplayerWindow(Plugin plugin) : base("AnoMech Multiplayer###AnoMechMultiplayer")
    {
        this.plugin = plugin;
        mp = plugin.Multiplayer;
        Size = new Vector2(420, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        relayUrl = plugin.Configuration.RelayServerUrl;
        // ObjectTable.LocalPlayer is main-thread-only, and Dalamud constructs
        // plugins off-thread -- reading it here throws "Not on main thread!"
        // and kills the whole plugin load. Draw() always runs on the main
        // thread, so prefill lazily there instead, once.
    }

    public void Dispose() { }

    // Before Start, Session.ScenarioIndex is only meaningful once the host has
    // actually clicked Start (StartScenario populates it) -- showing it any
    // earlier would display whatever scenario happens to sit at index 0 in
    // Game.Scenarios, not what's actually about to be hosted. So: once
    // Started, the session's own (broadcast, authoritative for everyone
    // including peers) scenario is shown; before that, only the host's own
    // live MainWindow selection is meaningful, and a peer just sees "not
    // chosen yet" since they have no visibility into what the host intends.
    private string CurrentScenarioLabel()
    {
        if (mp.Session.Started) return DisplayName(Plugin.GameInstance.Scenarios[mp.Session.ScenarioIndex]);
        if (mp.IsHost && Plugin.MainWindow.SelectedScenario is { } scenario) return DisplayName(scenario);
        return "not chosen yet";
    }

    public override void Draw()
    {
        if (!namePrefilled)
        {
            if (Plugin.ObjectTable.LocalPlayer?.Name.TextValue is { Length: > 0 } name)
                displayName = name;
            namePrefilled = true;
        }

        WindowName = $"AnoMech Multiplayer ({CurrentScenarioLabel()})###AnoMechMultiplayer";

        ImGui.TextWrapped(
            $"Vertical-slice multiplayer for {CurrentScenarioLabel()}. One host runs the real " +
            "simulation; up to 7 others join and take over bot slots.");
        if (mp.SessionCode == null
            && (Plugin.MainWindow.SelectedScenario is not { } sel || !MultiplayerManager.SupportedScenarios.Contains(sel.GetType())))
        {
            // Not gated on mp.IsHost: that flag defaults to false and is never
            // reset back to false on leaving a session (see LeaveSessionInternal),
            // so gating on it meant this warning only ever showed for a *returning*
            // host, never a first-time one. Harmless to show before joining too --
            // it's specifically about hosting, and a would-be joiner can just
            // ignore it.
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f),
                "Select a multiplayer-supported scenario in the main window before hosting.");
        }
        ImGui.Separator();

        // Gated on SessionCode (cleared only by LeaveSession), not IsConnected --
        // a brief relay drop mid-session must keep showing the roster (with a
        // Reconnecting indicator) rather than yanking the user back to the
        // connect form, which would look like they'd been kicked out entirely.
        if (mp.SessionCode == null)
            DrawConnectPanel();
        else
            DrawConnectedPanel();
    }

    // No relay is baked into the plugin -- every group runs its own (see
    // Relay/README.md), so nothing here proceeds until the user has typed a
    // URL that at least looks like one. This isn't reachability validation
    // (only an actual connect attempt proves that), just "did you paste an
    // actual ws(s):// URL" so a blank/garbled field fails fast with an
    // in-window hint instead of a silent, confusing connect failure.
    private static bool IsPlausibleRelayUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss";

    private void DrawConnectPanel()
    {
        ImGui.TextWrapped("Point this at a relay server you or someone in your group is running " +
                           "-- there is no default/public one. See Relay/README.md for how to stand " +
                           "one up.");

        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("Relay URL##relayUrl", ref relayUrl, 256))
        {
            plugin.Configuration.RelayServerUrl = relayUrl;
            plugin.Configuration.Save();
        }
        var validUrl = IsPlausibleRelayUrl(relayUrl);
        if (!validUrl)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.4f, 1f),
                string.IsNullOrWhiteSpace(relayUrl)
                    ? "Enter your relay's address, e.g. ws://203.0.113.5:7890 or wss://relay.example.com"
                    : "Doesn't look like a ws:// or wss:// URL.");
        }

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Display name", ref displayName, 64);

        if (mp.ConnectionError is { } err)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Connection failed: {err}");
        }
        else if (mp.SessionEndReason is { } endReason)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), endReason);
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(!validUrl);
        if (ImGui.Button("Host new session"))
        {
            mp.DisplayName = displayName;
            mp.HostSession(relayUrl.Trim());
        }
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.SetNextItemWidth(140);
        ImGui.InputText("##joincode", ref joinCode, 16, ImGuiInputTextFlags.CharsUppercase);
        ImGui.SameLine();
        ImGui.BeginDisabled(!validUrl || string.IsNullOrWhiteSpace(joinCode));
        if (ImGui.Button("Join session"))
        {
            mp.DisplayName = displayName;
            mp.JoinSession(relayUrl.Trim(), joinCode);
        }
        ImGui.EndDisabled();
    }

    private void DrawConnectedPanel()
    {
        // IsConnected here is a live socket-state check (RelayClient.IsConnected),
        // re-read every frame -- if the relay drops between opening this window
        // and clicking Start, this goes red before the host can start a run
        // peers can't actually receive.
        var stable = mp.IsConnected;
        if (stable)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "● Connected to relay");
        else if (mp.IsReconnecting)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"● Reconnecting to relay... (attempt {mp.ReconnectAttempt})");
        else
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "● Not connected to relay");
        if (!stable && mp.ConnectionError is { } connErr)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), $"Last error: {connErr}");

        if (mp.IsHost && mp.SessionCode != null)
        {
            ImGui.TextUnformatted("Session code:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), mp.SessionCode);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(mp.SessionCode);
            ImGui.TextWrapped("Share this code and your relay URL with whoever is joining.");
        }
        else
        {
            ImGui.TextUnformatted(mp.Session.Started ? "Running." : "Connected -- waiting for the host to start.");
        }

        // Surfaced directly to whoever needs to act (update their plugin) --
        // both checksums right here, not just a generic "different build"
        // that would leave them guessing without hovering the host's row.
        // Rather than leaving it as a silently-rejected Claim click they'd
        // have to guess the reason for. Host mismatches vs. any claimed peer
        // are covered per-row below since the host can't be "wrong" relative
        // to itself.
        var myMismatchVsHost = !mp.IsHost && mp.IsVersionMismatched(mp.Session.HostId);
        if (myMismatchVsHost)
        {
            var hostBuild = mp.Session.Builds.GetValueOrDefault(mp.Session.HostId);
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.15f, 1f),
                "⚠ Different AnoMech build than the host -- update before claiming a role.");
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.15f, 1f),
                $"Yours: {PluginBuildInfo.Version} ({PluginBuildInfo.ShortChecksum})   Host: {hostBuild?.Version ?? "?"} ({hostBuild?.ShortChecksum ?? "?"})");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Roles:");
        for (var i = 0; i < 8; i++)
        {
            var role = (PartyRole)i;
            var claimed = mp.Session.ClaimedBy.TryGetValue(role, out var peerId);
            var mine = claimed && peerId == mp.MyPeerId;
            // The host never appears in the ping-based peerStatuses broadcast
            // (it doesn't ping itself), so its row is tracked separately --
            // via time-since-last-broadcast rather than a fabricated 0ms ping.
            var isHostRow = claimed && !mine && peerId == mp.Session.HostId;
            var stale = claimed && !mine && (isHostRow ? mp.IsHostStale : mp.IsPeerStale(peerId));
            var mismatched = claimed && !mine && mp.IsVersionMismatched(peerId);
            var label = claimed
                ? mp.Session.NameOf(peerId) + (mine ? " (you)" : "") + (stale ? " (disconnected?)" : "") + (mismatched ? " (version mismatch!)" : "")
                : "(open, bot)";

            ImGui.TextUnformatted(RoleLabels[i]);
            ImGui.SameLine(50);
            if (claimed && !mine)
            {
                if (isHostRow)
                    DrawHostStatusDot(mp.IsHostStale, mp.SecondsSinceHostMessage);
                else
                    DrawStatusDot(mp.GetPeerStatus(peerId), stale);
                ImGui.SameLine();
            }
            if (mismatched)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.15f, 1f), label);
            else if (stale)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), label);
            else
                ImGui.TextUnformatted(label);
            if (mismatched && ImGui.IsItemHovered())
            {
                var theirs = mp.Session.Builds.GetValueOrDefault(peerId);
                ImGui.SetTooltip($"Different plugin build than yours.\nYours: {PluginBuildInfo.Version} ({PluginBuildInfo.ShortChecksum})\nTheirs: {theirs?.Version ?? "?"} ({theirs?.ShortChecksum ?? "?"})\nUpdate to matching versions before starting.");
            }
            ImGui.SameLine(240);

            ImGui.PushID(i);
            ImGui.BeginDisabled(mp.Session.Started || (claimed && !mine) || (!claimed && myMismatchVsHost));
            if (mine)
            {
                if (ImGui.SmallButton("Release")) mp.ReleaseRole();
            }
            else if (ImGui.SmallButton("Claim"))
            {
                mp.ClaimRole(role);
            }
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        // Session.Names gets an entry the moment someone's Hello lands, well
        // before they've claimed a role -- without this, a connected friend
        // who hasn't picked a slot yet is invisible anywhere in this window,
        // leaving "did the invite even work?" with no positive answer.
        var unclaimed = mp.Session.Names.Keys
            .Where(id => id != mp.MyPeerId && !mp.Session.ClaimedBy.ContainsValue(id))
            .Select(id => mp.Session.NameOf(id) + (mp.IsVersionMismatched(id) ? " (version mismatch)" : ""))
            .ToList();
        if (unclaimed.Count > 0)
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Connected, no role yet: {string.Join(", ", unclaimed)}");

        // Testing aid, available to host and peer alike (see
        // MultiplayerManager.SetDebugBotControlled). Locked once Started so it
        // can't flip mid-fight; the choreography only makes sense replayed
        // from a fresh Start.
        {
            var botControlled = mp.DebugBotControlled;
            ImGui.BeginDisabled(mp.Session.Started);
            if (ImGui.Checkbox("Debug AI bot", ref botControlled))
                mp.SetDebugBotControlled(botControlled);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Testing aid -- your own claimed role gets driven locally by the " +
                                  "same AI a host-side bot in that role would use, instead of you. " +
                                  "Entirely client-side; the host sees no difference. Locked once the " +
                                  "fight starts.");
        }

        ImGui.Separator();
        if (!mp.Session.Started)
        {
            if (mp.IsHost)
            {
                if (mp.IsStartCheckPending)
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Checking everyone's ready...");
                else if (mp.StartCheckFailureReason is { } startFail)
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), startFail);

                var anyMismatch = mp.Session.ClaimedBy.Values.Any(mp.IsVersionMismatched);
                var hasSupportedScenario = Plugin.MainWindow.SelectedScenario is { } sel2
                    && MultiplayerManager.SupportedScenarios.Contains(sel2.GetType());
                // A grouped scenario (e.g. P2 Forsaken's NA/EU strats) can leave
                // SelectedStrat at -1 when the selected region has no strats --
                // mirrors MainWindow's own solo-Start gate (HasStartableStrat),
                // and matches the same check StartScenario itself enforces.
                var hasStrat = hasSupportedScenario && Plugin.MainWindow.HasStartableStrat();
                var canStart = stable && mp.MyClaimedRole != null && !anyMismatch && !mp.IsStartCheckPending && hasStrat;
                ImGui.BeginDisabled(!canStart);
                if (ImGui.Button("Start")) mp.StartScenario();
                ImGui.EndDisabled();
                if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(!stable
                        ? "Not connected to the relay."
                        : !hasSupportedScenario
                            ? "Select a multiplayer-supported scenario in the main window first."
                            : !hasStrat
                                ? "No strat available for the selected scenario/region."
                                : anyMismatch
                                ? "One or more players are on a different plugin build -- everyone needs to match before starting."
                                : mp.IsStartCheckPending
                                    ? "Waiting for players to confirm they're ready..."
                                    : "Claim a role for yourself first.");
            }
            else
            {
                ImGui.BeginDisabled();
                ImGui.Button("Start (controlled by host)");
                ImGui.EndDisabled();
            }
            ImGui.SameLine();
        }

        if (ImGui.Button("Leave session"))
        {
            mp.LeaveSession();
            // Game.Leave() -> World.Map.Unload() assumes a zone was actually
            // entered (it restores the real character to the position saved by
            // ZoneSession.Enter()) -- calling it having never clicked Start
            // means that save was never populated, and it teleports the real
            // character to garbage/default coordinates in whatever zone
            // they're actually in. Mirrors the same IsInInstance gate
            // MainWindow's own "Leave" button already uses.
            if (plugin.Game.World.Map.IsInInstance) plugin.Game.Leave();
        }
    }

    // The host doesn't ping itself, so there's no round-trip number to show
    // for its row -- just whether we've heard from it recently. Green/red
    // only (no yellow "fair" band): unlike a peer's ping, this is time-since-
    // last-broadcast, not a latency measurement, so a three-way split would
    // imply precision that isn't there.
    private static void DrawHostStatusDot(bool stale, float secondsSince)
    {
        var color = stale ? new Vector4(1f, 0.35f, 0.35f, 1f) : new Vector4(0.4f, 0.9f, 0.4f, 1f);
        ImGui.TextColored(color, "●");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(stale
                ? $"No message from the host in {secondsSince:F0}s -- likely disconnected."
                : $"Host -- last message {secondsSince:F0}s ago.");
    }

    // Ping color bands per the below thresholds; grey/red cover the two
    // "no number to show" cases (no Pong yet vs. flagged stale) so the dot
    // never silently reads as a suspiciously good 0ms.
    private static void DrawStatusDot(PeerStatusEntry? status, bool stale)
    {
        Vector4 color;
        string tooltip;
        if (stale)
        {
            color = new Vector4(1f, 0.35f, 0.35f, 1f);
            tooltip = "No message received in a while -- likely disconnected.";
        }
        else if (status is not { } s)
        {
            color = new Vector4(0.6f, 0.6f, 0.6f, 1f);
            tooltip = "Connected -- waiting for a status update...";
        }
        else if (s.LatencyMs is not { } ms)
        {
            color = new Vector4(0.6f, 0.6f, 0.6f, 1f);
            tooltip = "Connected -- measuring ping...";
        }
        else if (ms < 100f)
        {
            color = new Vector4(0.4f, 0.9f, 0.4f, 1f);
            tooltip = $"Ping: {ms:F0}ms (good)";
        }
        else if (ms <= 300f)
        {
            color = new Vector4(0.95f, 0.85f, 0.3f, 1f);
            tooltip = $"Ping: {ms:F0}ms (fair)";
        }
        else
        {
            color = new Vector4(1f, 0.4f, 0.4f, 1f);
            tooltip = $"Ping: {ms:F0}ms (poor)";
        }
        if (status is { } shown)
            tooltip += $"\nLast message: {shown.SecondsSinceLastSeen:F0}s ago";

        ImGui.TextColored(color, "●");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }
}
