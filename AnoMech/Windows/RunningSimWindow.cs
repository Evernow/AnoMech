using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AnoMech.Windows;

// Small always-on-top-ish substitute for MainWindow/MultiplayerWindow while a sim
// is actually running -- those two are hidden for the same duration (see each
// window's own PreOpenCheck), since a full scenario picker/lobby UI has no
// reason to stay on screen mid-fight, but Start/Reset/Leave/Leave-session still
// need to be reachable without alt-tabbing back to a window that's deliberately
// hidden. Every button here just calls straight into the exact same methods
// MainWindow/MultiplayerWindow use for their own copies, so behavior is
// identical -- this window only changes where those buttons are drawn from.
public sealed class RunningSimWindow : Window
{
    private readonly Plugin plugin;

    public RunningSimWindow(Plugin plugin) : base("Running sim###AnoMechRunningSim")
    {
        this.plugin = plugin;
        Flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreOpenCheck()
    {
        IsOpen = plugin.Game.World.Map.IsInInstance;
    }

    public override void Draw()
    {
        var mp = plugin.Multiplayer;
        var inSession = mp.SessionCode != null;

        // ActiveScenario is never set for a peer, so IsRunning is the only signal
        // that works for every seat; Paused (a post-wipe freeze) is checked
        // separately since it doesn't clear either one.
        var scenarioActive = inSession ? mp.IsRunning : plugin.Game.ActiveScenario != null;
        var running = scenarioActive && !plugin.Game.Paused;
        ImGui.TextColored(
            running ? new Vector4(0.4f, 0.9f, 0.4f, 1f) : new Vector4(1f, 0.4f, 0.4f, 1f),
            running ? "Running sim" : "Sim paused, wiped, reset, or leave");

        if (inSession)
        {
            // Mirrors MultiplayerWindow.DrawConnectedPanel's own !Started guard --
            // once a run has actually started, Start disappears there too.
            if (!mp.Session.Started)
            {
                plugin.MultiplayerWindow.DrawStartButton();
                ImGui.SameLine();
            }
        }
        else
        {
            Plugin.MainWindow.DrawSoloStartButton();
            ImGui.SameLine();
        }

        Plugin.MainWindow.DrawResetLeaveButtons();

        if (inSession)
            plugin.MultiplayerWindow.DrawLeaveSessionButton();
    }
}
