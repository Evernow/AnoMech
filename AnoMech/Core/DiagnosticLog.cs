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

    // Every line that's ever fallen out of the live ring buffer above -- either
    // evicted one at a time as Add() keeps it at Capacity, or moved over wholesale
    // by Clear() at the start of a new run -- oldest-first, byte-capped at
    // ArchiveCapacityBytes. Without routing ring-buffer eviction through here too
    // (not just Clear()), a run that logs heavily enough to blow through Capacity
    // on its own (P3 Black Hole's native MapEffect tracing alone can do this)
    // would silently lose its own early lines before Clear() ever got a chance to
    // preserve them -- which is exactly what happened to a Reset+Leave repro that
    // scrolled out of the live buffer under later combat logging, long before the
    // next scenario start ever triggered an archive.
    // Stored as a queue of already-formatted lines rather than one big string, so
    // trimming is O(lines dropped) -- an O(archive size) rebuild on every single
    // Add() call once the ring buffer is in steady eviction would make this a
    // real cost on the hot logging path.
    private const int ArchiveCapacityBytes = 10 * 1024 * 1024;
    private static readonly Queue<string> archiveLines = new();
    private static long archiveBytes;

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
            while (lines.Count > Capacity)
                ArchiveLine(lines.Dequeue());
        }
    }

    // Not its own lock -- both call sites (Add and Clear) already hold `gate`.
    private static void ArchiveLine(string line)
    {
        archiveLines.Enqueue(line);
        archiveBytes += line.Length + 1;
        while (archiveBytes > ArchiveCapacityBytes && archiveLines.Count > 0)
            archiveBytes -= archiveLines.Dequeue().Length + 1;
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
                ArchiveLine($"=== Run ended {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                while (lines.Count > 0)
                    ArchiveLine(lines.Dequeue());
            }
        }
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (gate) return lines.ToArray();
    }

    // Everything that's fallen out of the live buffer this session (see
    // ArchiveLine), oldest-first. May start mid-run rather than at a clean
    // "=== Run ended" marker if trimming cut through one -- byte-accurate
    // trimming (never silently dropping more than necessary) matters more here
    // than always starting on a run boundary.
    public static string ArchivedHistory()
    {
        lock (gate) return archiveLines.Count == 0 ? "" : string.Join('\n', archiveLines) + '\n';
    }
}
