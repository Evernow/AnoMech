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

// Multiplayer lobby. Which scenario is hosted is whatever's currently selected in
// MainWindow -- this window has no scenario picker of its own, only shows the name. Host
// picks a role, shares the session code + relay URL out of band; unclaimed roles stay AI bots.
public class MultiplayerWindow : Window, IDisposable
{
    private static readonly string[] RoleLabels = ["MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

    private readonly Plugin plugin;
    private readonly MultiplayerManager mp;
    private string relayUrl;
    private string relayToken;
    private string joinCode = "";
    private string displayName = "Player";
    private bool namePrefilled;
    // null = not checked yet (or the check failed/relay is unreachable -- assume no token
    // needed rather than block the UI on it). Re-checked whenever relayUrl actually changes;
    // see DrawConnectPanel.
    private bool? relayRequiresToken;
    private string? relayInfoCheckedForUrl;

    public MultiplayerWindow(Plugin plugin) : base("AnoMech Multiplayer###AnoMechMultiplayer")
    {
        this.plugin = plugin;
        mp = plugin.Multiplayer;
        Size = new Vector2(420, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        IsOpen = false;
        relayUrl = plugin.Configuration.RelayServerUrl;
        relayToken = plugin.Configuration.RelayAccessToken;
        // ObjectTable.LocalPlayer is main-thread-only, but Dalamud constructs plugins
        // off-thread -- prefill lazily in Draw() instead, once.
    }

    public void Dispose() { }

    // Hidden while the fake-zone instance is loaded -- see MainWindow's PreOpenCheck.
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

    // Session.ScenarioIndex is only meaningful once Started (StartScenario populates it) --
    // before that, showing it would just display whatever sits at index 0.
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
            && (Plugin.MainWindow.SelectedScenario is not { } sel || !sel.SupportsMultiplayer))
        {
            // Not gated on mp.IsHost: that flag is never reset on leaving a session, so
            // gating on it would only show this to a returning host. Harmless before
            // joining too -- a would-be joiner can just ignore it.
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f),
                "Select a multiplayer-supported scenario in the main window before hosting.");
        }
        ImGui.Separator();

        // Gated on SessionCode, not IsConnected -- a brief relay drop must keep showing the
        // roster (with a Reconnecting indicator), not yank the user back to the connect form.
        if (mp.SessionCode == null)
            DrawConnectPanel();
        // SessionCode alone doesn't mean a host actually exists -- it's set synchronously on
        // JoinSession, before any confirmation. Without this, a joiner briefly sees the full
        // lobby for a mistyped/dead code before IsSessionNotFound kicks them back out.
        else if (!mp.IsHost && !mp.EverHeardFromHost)
            DrawJoiningPanel();
        else
            DrawConnectedPanel();
    }

    private void DrawJoiningPanel()
    {
        ImGui.TextUnformatted($"Connecting to session {mp.SessionCode}...");
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Waiting for the host to respond.");
        ImGui.Spacing();
        if (ImGui.Button("Cancel"))
            mp.LeaveSession();
    }

    // Just enough validation to fail fast on a blank/garbled field, not reachability.
    // "://" is checked directly rather than handed straight to Uri.TryCreate -- a bare
    // "host:port" like sim.example.com:8443 otherwise parses as an absolute URI on its
    // own (scheme "sim.example.com", opaque part "8443"), since it has no "//".
    private static bool IsPlausibleRelayUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var explicitUri)
                && explicitUri.Scheme is "ws" or "wss" or "http" or "https";
        return Uri.TryCreate($"ws://{trimmed}", UriKind.Absolute, out var probe) && !string.IsNullOrEmpty(probe.Host);
    }

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
                    ? "Enter your relay's address, e.g. relay.example.com or 203.0.113.5:7890"
                    : "Doesn't look like a valid relay address.");
        }
        // Re-checked once per distinct valid URL (not every frame/keystroke) via a plain HTTP
        // GET, no token needed to ask -- see RelayClient.FetchInfoAsync. Unreachable/old-relay
        // failures default to "no token needed" rather than blocking the form on it.
        else if (relayInfoCheckedForUrl != relayUrl)
        {
            relayInfoCheckedForUrl = relayUrl;
            relayRequiresToken = null;
            var urlSnapshot = relayUrl;
            _ = RelayClient.FetchInfoAsync(urlSnapshot).ContinueWith(t =>
            {
                if (t.Result is { } info)
                    Plugin.Framework.Run(() => { if (relayInfoCheckedForUrl == urlSnapshot) relayRequiresToken = info.RequiresToken; });
            });
        }

        if (relayRequiresToken == true)
        {
            ImGui.SetNextItemWidth(300);
            if (ImGui.InputText("Relay password##relayToken", ref relayToken, 128, ImGuiInputTextFlags.Password))
            {
                plugin.Configuration.RelayAccessToken = relayToken;
                plugin.Configuration.Save();
            }
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

        var missingRequiredToken = relayRequiresToken == true && string.IsNullOrEmpty(relayToken);

        ImGui.Spacing();
        ImGui.BeginDisabled(!validUrl || missingRequiredToken);
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
        ImGui.BeginDisabled(!validUrl || missingRequiredToken || string.IsNullOrWhiteSpace(joinCode));
        if (ImGui.Button("Join session"))
        {
            mp.DisplayName = displayName;
            mp.JoinSession(relayUrl.Trim(), joinCode);
        }
        ImGui.EndDisabled();
    }

    private void DrawConnectedPanel()
    {
        // Live socket-state check, re-read every frame, so a relay drop goes red immediately.
        var stable = mp.IsConnected;
        if (stable)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "● Connected to relay");
        else if (mp.IsReconnecting)
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"● Reconnecting to relay... (attempt {mp.ReconnectAttempt})");
        else
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "● Not connected to relay");
        if (!stable && mp.ConnectionError is { } connErr)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), $"Last error: {connErr}");

        if (stable)
        {
            ImGui.SameLine();
            ImGui.TextColored(mp.IsEncrypted ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.7f, 0.3f, 1f),
                mp.IsEncrypted ? "(encrypted)"
                : mp.FellBackToUnencrypted ? "(NOT encrypted -- this relay doesn't support wss://)"
                : "(NOT encrypted)");
        }
        if (stable && !mp.SupportsCompression)
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "This relay does not support compression.");

        if (mp.IsHost && mp.SessionCode == null)
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Requesting a session code from the relay...");
        else if (mp.IsHost && mp.SessionCode != null)
        {
            ImGui.TextUnformatted("Session code:");
            ImGui.SetWindowFontScale(2f);
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), mp.SessionCode);
            ImGui.SetWindowFontScale(1f);
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy")) ImGui.SetClipboardText(mp.SessionCode);
            ImGui.TextWrapped("Share this code and your relay URL with whoever is joining.");
        }
        else
        {
            ImGui.TextUnformatted(mp.Session.Started ? "Running." : "Connected -- waiting for the host to start.");
        }

        // Surfaced directly with both checksums, rather than leaving it as a silently
        // rejected Claim click. Mismatches vs. any claimed peer are covered per-row below.
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
            // The host doesn't ping itself, so its row is tracked separately, via
            // time-since-last-broadcast rather than a fabricated 0ms ping.
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

        // Session.Names gets an entry as soon as Hello lands, before a role is claimed --
        // otherwise a connected friend with no slot yet is invisible in this window.
        var unclaimed = mp.Session.Names.Keys
            .Where(id => id != mp.MyPeerId && !mp.Session.ClaimedBy.ContainsValue(id))
            .Select(id => mp.Session.NameOf(id) + (mp.IsVersionMismatched(id) ? " (version mismatch)" : ""))
            .ToList();
        if (unclaimed.Count > 0)
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Connected, no role yet: {string.Join(", ", unclaimed)}");

        // Locked once Started -- the choreography only makes sense replayed from a fresh Start.
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
            DrawStartButton();
            ImGui.SameLine();
        }

        DrawLeaveSessionButton();
    }

    // Self-contained (no params) so RunningSimWindow can call this directly while running.
    internal void DrawStartButton()
    {
        var stable = mp.IsConnected;
        if (mp.IsHost)
        {
            if (mp.IsStartCheckPending)
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Checking everyone's ready...");
            else if (mp.StartCheckFailureReason is { } startFail)
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), startFail);

            var anyMismatch = mp.Session.ClaimedBy.Values.Any(mp.IsVersionMismatched);
            var hasSupportedScenario = Plugin.MainWindow.SelectedScenario is { } sel2
                && sel2.SupportsMultiplayer;
            // Mirrors MainWindow's own solo-Start gate and StartScenario's own check.
            var hasStrat = hasSupportedScenario && Plugin.MainWindow.HasStartableStrat();
            // Unclaimed roles are fine (fall back to an AI bot), but a connected person who
            // hasn't picked a role is a spectator about to get left behind at Start.
            var claimedPeerIds = mp.Session.ClaimedBy.Values.ToHashSet();
            var everyoneHasClaimed = mp.Session.Names.Keys.All(claimedPeerIds.Contains);
            var canStart = stable && mp.MyClaimedRole != null && !anyMismatch && !mp.IsStartCheckPending && hasStrat && everyoneHasClaimed;
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
                                : mp.MyClaimedRole == null
                                    ? "Claim a role for yourself first."
                                    : "Everyone connected needs to claim a role first.");
        }
        else
        {
            ImGui.BeginDisabled();
            ImGui.Button("Start (controlled by host)");
            ImGui.EndDisabled();
        }
    }

    internal void DrawLeaveSessionButton()
    {
        if (ImGui.Button("Leave session"))
        {
            mp.LeaveSession();
            // IsInInstance guard, same as MainWindow's Leave button: Leave() -> Unload()
            // assumes a zone was actually entered (it restores the saved position).
            if (plugin.Game.World.Map.IsInInstance) plugin.Game.Leave();
        }
    }

    // Green/red only, no "fair" band -- this is time-since-last-broadcast, not a latency
    // measurement, so a three-way split would imply precision that isn't there.
    private static void DrawHostStatusDot(bool stale, float secondsSince)
    {
        var color = stale ? new Vector4(1f, 0.35f, 0.35f, 1f) : new Vector4(0.4f, 0.9f, 0.4f, 1f);
        ImGui.TextColored(color, "●");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(stale
                ? $"No message from the host in {secondsSince:F0}s -- likely disconnected."
                : $"Host -- last message {secondsSince:F0}s ago.");
    }

    // Grey/red cover the two "no number to show" cases so the dot never silently reads as a
    // suspiciously good 0ms.
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
