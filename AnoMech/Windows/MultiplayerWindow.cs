using System;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Multiplayer;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnoMech.Windows;

// Vertical-slice multiplayer lobby, scoped to UMAD P3 Black Hole only (see
// MultiplayerManager). Host picks a role, shares the session code + relay URL
// out of band, up to 7 others join and claim the remaining roles; anything
// left unclaimed stays an AI bot exactly like solo play.
public class MultiplayerWindow : Window, IDisposable
{
    private static readonly string[] RoleLabels = ["MT", "OT", "H1", "H2", "M1", "M2", "R1", "R2"];

    private readonly Plugin plugin;
    private readonly MultiplayerManager mp;
    private string relayUrl;
    private string joinCode = "";
    private string displayName = "Player";
    private bool namePrefilled;

    public MultiplayerWindow(Plugin plugin) : base("AnoMech Multiplayer (P3 Black Hole)###AnoMechMultiplayer")
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

    public override void Draw()
    {
        if (!namePrefilled)
        {
            if (Plugin.ObjectTable.LocalPlayer?.Name.TextValue is { Length: > 0 } name)
                displayName = name;
            namePrefilled = true;
        }

        ImGui.TextWrapped(
            "Vertical-slice multiplayer for UMAD P3 Black Hole only. One host runs the real " +
            "simulation; up to 7 others join and take over bot slots.");
        ImGui.Separator();

        if (!mp.IsConnected)
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
                           "one up (a few minutes on a small VPS, or free on your own PC for a LAN test).");

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

        ImGui.Separator();
        ImGui.TextUnformatted("Roles:");
        for (var i = 0; i < 8; i++)
        {
            var role = (PartyRole)i;
            var claimed = mp.Session.ClaimedBy.TryGetValue(role, out var peerId);
            var mine = claimed && peerId == mp.MyPeerId;
            var label = claimed ? mp.Session.NameOf(peerId) : "(open, bot)";

            ImGui.TextUnformatted(RoleLabels[i]);
            ImGui.SameLine(50);
            ImGui.TextUnformatted(label);
            ImGui.SameLine(220);

            ImGui.PushID(i);
            ImGui.BeginDisabled(mp.Session.Started || (claimed && !mine));
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

        ImGui.Separator();
        if (mp.IsHost && !mp.Session.Started)
        {
            ImGui.BeginDisabled(mp.MyClaimedRole == null);
            if (ImGui.Button("Start")) mp.StartScenario();
            ImGui.EndDisabled();
            if (mp.MyClaimedRole == null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Claim a role for yourself first.");
            ImGui.SameLine();
        }

        if (ImGui.Button("Leave session"))
        {
            mp.LeaveSession();
            plugin.Game.Leave();
        }
    }
}
