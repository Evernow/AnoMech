using System;
using Dalamud.Bindings.ImGui;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// ImGui panel rendered in the main window's "Scenario config" pane when this
// scenario is active. Owns the StateOverrides instance and writes user choices into
// it. See UmadP4KefkaSaysSettingsWindow for the canonical shape.
public sealed class UmadP3BlackHoleSettingsWindow
{
    public UmadP3BlackHoleStateOverrides Overrides { get; } = new();

    public void Draw()
    {
        if (ImGui.Button("Auto")) ResetAll();
        if (SettingsGrid.Begin("##umadp3blackhole"))
        {
            DrawLineNumber();
            DrawAccretion();
#if DEBUG
            DrawFirstSlap();
            DrawFirstSlapTarget();
#endif
            SettingsGrid.End();
        }
        // Thunder III plan is NOT drawn here -- UmadP3BlackHoleScenario.DrawMultiplayerSettings
        // calls DrawThunderIIIPlan directly so it stays editable while Multiplayer is open.
    }

    // Forces the player into the slot carrying that line number (Auto = random).
    private void DrawLineNumber()
    {
        var v = Overrides.LineNumber;
        SettingsGrid.Row("Line:");
        if (ImGui.RadioButton("Auto##line",   v == null)) Overrides.LineNumber = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("First##line",  v == 1))    Overrides.LineNumber = 1;
        ImGui.SameLine();
        if (ImGui.RadioButton("Second##line", v == 2))    Overrides.LineNumber = 2;
        ImGui.SameLine();
        if (ImGui.RadioButton("Third##line",  v == 3))    Overrides.LineNumber = 3;
    }

    // Auto = random; Yes is ignored for tanks and third-in-line (they never get Accretion).
    private void DrawAccretion()
    {
        var v = Overrides.Accretion;
        SettingsGrid.Row("Accretion:");
        if (ImGui.RadioButton("Auto##accretion", v == null))  Overrides.Accretion = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("Yes##accretion",  v == true))  Overrides.Accretion = true;
        ImGui.SameLine();
        if (ImGui.RadioButton("No##accretion",   v == false)) Overrides.Accretion = false;
    }

#if DEBUG
    private void DrawFirstSlap()
    {
        var v = Overrides.FirstSlap;
        SettingsGrid.Row("1st Slap:");
        if (ImGui.RadioButton("Auto##firstslap",  v == null))                     Overrides.FirstSlap = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("Left##firstslap",  v == ActionId.SlapHappy_Left))  Overrides.FirstSlap = ActionId.SlapHappy_Left;
        ImGui.SameLine();
        if (ImGui.RadioButton("Right##firstslap", v == ActionId.SlapHappy_Right)) Overrides.FirstSlap = ActionId.SlapHappy_Right;
    }

    private void DrawFirstSlapTarget()
    {
        var v = Overrides.FirstSlapAllOnPlayer;
        SettingsGrid.Row("1st Slap Target:");
        if (ImGui.RadioButton("Auto##firstslaptarget",   v == null)) Overrides.FirstSlapAllOnPlayer = null;
        ImGui.SameLine();
        if (ImGui.RadioButton("Player##firstslaptarget", v == true)) Overrides.FirstSlapAllOnPlayer = true;
    }
#endif

    // Only matters when at least one tank slot ends up bot-driven -- plans in advance which
    // invuln (if any) it uses for each of the sim's two Thunder III sets (see
    // ThunderIIIAssignment and RunThunder). Only 2 rows, not the real fight's 5, since this
    // sim's timeline only casts Thunder III twice. DrawThunderIIIOption validates cross-set
    // resource conflicts (see its own doc comment). Called from
    // UmadP3BlackHoleScenario.DrawMultiplayerSettings, not this class's own Draw().
    public void DrawThunderIIIPlan()
    {
        ImGui.Separator();
        // Only the host's plan is ever read -- a peer's local pick gets overwritten by the
        // host's next LobbyStateMessage regardless.
        var mpGuest = Plugin.MultiplayerInstance is { IsConnected: true, IsHost: false };
        ImGui.TextUnformatted("Thunder III plan (planning tank bots will follow):");
        ImGui.BeginDisabled(mpGuest);
        DrawThunderIIIRow("##thunder1", "Set 1 (~42.6s):", Overrides.ThunderSet1, Overrides.ThunderSet2, v => Overrides.ThunderSet1 = v);
        DrawThunderIIIRow("##thunder2", "Set 2 (~83.9s):", Overrides.ThunderSet2, Overrides.ThunderSet1, v => Overrides.ThunderSet2 = v);
        ImGui.EndDisabled();
        if (mpGuest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Only the host's plan is used in multiplayer.");
    }

    private static void DrawThunderIIIRow(string idSuffix, string label, ThunderIIIAssignment current, ThunderIIIAssignment otherSet, Action<ThunderIIIAssignment> set)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        DrawThunderIIIOption("MT invulns both" + idSuffix, ThunderIIIAssignment.MtInvulnsBoth, current, otherSet, set);
        ImGui.SameLine();
        DrawThunderIIIOption("OT invulns both" + idSuffix, ThunderIIIAssignment.OtInvulnsBoth, current, otherSet, set);
        ImGui.SameLine();
        DrawThunderIIIOption("Share, MT first" + idSuffix, ThunderIIIAssignment.ShareMtFirst, current, otherSet, set);
        ImGui.SameLine();
        DrawThunderIIIOption("Share, OT first" + idSuffix, ThunderIIIAssignment.ShareOtFirst, current, otherSet, set);
    }

    // Two independent resource pools per tank -- an invuln use and a Share use only compete
    // with a repeat of themselves, not each other:
    //   - Invuln: consumes only that tank's real invuln (240-420s cd), blocks the same
    //     invuln-both option repeating in the other set (~41s gap, no invuln recovers).
    //   - Share: needs each tank's big self-mit cooldowns (120s/90s), which also don't
    //     recover in ~41s -- either Share variant in one set blocks both in the other.
    private static void DrawThunderIIIOption(string label, ThunderIIIAssignment option, ThunderIIIAssignment current, ThunderIIIAssignment otherSet, Action<ThunderIIIAssignment> set)
    {
        var isShare = option is ThunderIIIAssignment.ShareMtFirst or ThunderIIIAssignment.ShareOtFirst;
        var otherIsShare = otherSet is ThunderIIIAssignment.ShareMtFirst or ThunderIIIAssignment.ShareOtFirst;
        var blocked = isShare ? otherIsShare : option == otherSet;
        if (blocked) ImGui.BeginDisabled();
        if (ImGui.RadioButton(label, current == option) && !blocked) set(option);
        if (blocked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(isShare
                    ? "Both tanks already shared the other set -- their own mitigation kit's big cooldowns (120s/90s) can't recover in ~41s, so neither can reach the required mitigation again."
                    : "That tank already invulns the other set -- no tank invuln recovers in ~41s, so they can't do both.");
        }
    }

    private void ResetAll()
    {
        Overrides.LineNumber = null;
        Overrides.Accretion = null;
#if DEBUG
        Overrides.FirstSlap = null;
        Overrides.FirstSlapAllOnPlayer = null;
#endif
        Overrides.ThunderSet1 = ThunderIIIAssignment.MtInvulnsBoth;
        Overrides.ThunderSet2 = ThunderIIIAssignment.ShareMtFirst;
    }
}