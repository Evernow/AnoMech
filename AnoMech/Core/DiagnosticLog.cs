using System;
using System.Collections.Generic;

namespace AnoMech.Core;

// Ring buffer mirroring recent log lines from across the sim -- scripted moves
// (AiManager.Move), every AOE resolve (DamageSolver.Resolve), every boss cast
// (SimEnemy.Cast), every death/invuln-save (Game.Kill), plus scenario-specific
// tracing (UMAD P3 Black Hole's tether grab/pull, Nothingness escalation, ...).
// Lives outside any #if DEBUG block since producers (engine/scenario code) build
// in every configuration; only the DEBUG-only DamageDebugWindow ever reads it,
// folding the buffer into its dump file so a bug report is one file instead of a
// screenshot-plus-pasted-log. Info/Warn/Debug mirror Plugin.Log's own three
// levels so call sites read the same either way -- swap Plugin.Log.X(msg) for
// DiagnosticLog.X(msg) and the message keeps showing up in the normal Dalamud
// log exactly as before, just also captured here. Capacity is generous (5000)
// because a full run now logs heavily; a late-fight death should still have its
// early-fight causes in the buffer instead of them being evicted.
internal static class DiagnosticLog
{
    private const int Capacity = 5000;
    private static readonly Queue<string> lines = new();
    private static readonly object gate = new();

    public static void Info(string message)
    {
        Plugin.Log.Information(message);
        Add(message);
    }

    public static void Warn(string message)
    {
        Plugin.Log.Warning(message);
        Add(message);
    }

    public static void Debug(string message)
    {
        Plugin.Log.Debug(message);
        Add(message);
    }

    private static void Add(string message)
    {
        lock (gate)
        {
            lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {message}");
            while (lines.Count > Capacity) lines.Dequeue();
        }
    }

    // Cleared at the start of each scenario run so a dump never mixes lines from
    // an earlier attempt in with the current one.
    public static void Clear()
    {
        lock (gate) lines.Clear();
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (gate) return lines.ToArray();
    }
}
