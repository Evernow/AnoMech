using System;
using System.Collections.Generic;
using System.Text;

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

    // Every completed run's lines this session, oldest-first, so
    // AnoMech-DamageDebug.txt keeps a history across scenario switches instead of
    // Clear() silently discarding whatever the previous run logged the moment a
    // different scenario starts -- previously the only way to catch an
    // intermittent bug was to dump before ever starting anything else. Trimmed
    // from the oldest end (whole runs at a time, via the "=== Run ended" markers)
    // once it exceeds ArchiveCapacityBytes, so the file still favors the most
    // recent history over unbounded growth.
    private const int ArchiveCapacityBytes = 10 * 1024 * 1024;
    private static readonly StringBuilder archive = new();

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

    // Cleared at the start of each scenario run -- the outgoing run's lines are
    // folded into the archive first (see ArchiveCapacityBytes) so a dump still
    // includes every run this session, not just the one currently in progress.
    public static void Clear()
    {
        lock (gate)
        {
            if (lines.Count > 0)
            {
                archive.Append("=== Run ended ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" ===\n");
                foreach (var line in lines)
                    archive.Append(line).Append('\n');
                TrimArchive();
            }
            lines.Clear();
        }
    }

    // Drops whole runs from the oldest end (never mid-run) until back under the
    // cap, so the archive text always starts cleanly at a "=== Run ended" marker.
    private static void TrimArchive()
    {
        if (archive.Length <= ArchiveCapacityBytes) return;
        var text = archive.ToString();
        var cutAt = text.IndexOf("=== Run ended ", text.Length - ArchiveCapacityBytes, StringComparison.Ordinal);
        if (cutAt < 0) cutAt = text.IndexOf("=== Run ended ", StringComparison.Ordinal);
        archive.Clear();
        if (cutAt > 0) archive.Append(text, cutAt, text.Length - cutAt);
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (gate) return lines.ToArray();
    }

    // Every completed run's lines this session (see Clear()), oldest-first.
    // Empty until the first scenario switch.
    public static string ArchivedHistory()
    {
        lock (gate) return archive.ToString();
    }
}
