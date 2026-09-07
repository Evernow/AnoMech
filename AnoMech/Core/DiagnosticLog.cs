using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using AnoMech.Multiplayer;

namespace AnoMech.Core;

// Mirrors Plugin.Log's three levels -- swap Plugin.Log.X for DiagnosticLog.X and it still
// reaches the normal Dalamud log, just also captured here.
//
// Two independent halves:
//  - An in-memory "this run" buffer (currentRunLines), capped by byte size, read synchronously
//    by DamageDebugWindow's dump so a periodic mid-run dump never hitches reading a big file.
//  - A disk-backed, async rotating log: a bounded channel feeds one background writer task, so
//    Info/Warn/Debug never block on I/O. The active segment gzips/rotates at SegmentMaxBytes or
//    on RotateNow() (Game.Leave forces a fresh segment on leave). Archives are pruned
//    oldest-first past TotalArchiveCapBytes. A leftover unrotated active file from a previous
//    session (crash, plugin reload) is rotated at startup instead of being overwritten.
internal static class DiagnosticLog
{
    // ---- In-memory "this run" view (DamageDebugWindow's Snapshot) -----------------------

    private const long RunBufferCapBytes = 10 * 1024 * 1024;
    private static readonly Queue<string> currentRunLines = new();
    private static long currentRunBytes;
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

    // A multi-line structured block (DamageDebugWindow's party/AOE/enemy state), delimited so
    // it reads distinctly from per-tick lines. Routed through the same Add() path as
    // Info/Warn/Debug rather than a separate file. Skips Plugin.Log -- this can fire every few
    // seconds mid-run and shouldn't spam the normal Dalamud log window.
    public static void LogSnapshot(string label, string content)
        => Add($"=== {label} ==={Environment.NewLine}{content}{Environment.NewLine}=== end {label} ===");

    private static void Add(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        lock (gate)
        {
            currentRunLines.Enqueue(line);
            currentRunBytes += line.Length + 1;
            // Queue.Dequeue is O(1), so sustained per-tick eviction stays cheap.
            while (currentRunBytes > RunBufferCapBytes && currentRunLines.Count > 0)
                currentRunBytes -= currentRunLines.Dequeue().Length + 1;
        }
        if (ShouldPersistToDisk()) EnqueueForDisk(line);
    }

    // Disk logging only happens while the plugin is actually doing something (main window open,
    // an active sim, or a multiplayer session) -- otherwise idle time in the overworld generates
    // log I/O for nothing. The in-memory buffer above is unaffected either way. Null-checked
    // since this can run before Plugin's constructor has assigned these statics.
    private static bool ShouldPersistToDisk()
        => (Plugin.MainWindow?.IsOpen ?? false)
           || Plugin.GameInstance?.ActiveScenario != null
           || Plugin.MultiplayerInstance?.SessionCode != null;

    // Called at the start of each scenario run so a dump never mixes in an earlier attempt's
    // lines. Only resets the in-memory view -- the disk log has no notion of "runs", it just
    // keeps flowing and rotates on its own triggers.
    public static void Clear()
    {
        var marker = $"{DateTime.Now:HH:mm:ss.fff} === New run ===";
        lock (gate)
        {
            currentRunLines.Clear();
            currentRunLines.Enqueue(marker);
            currentRunBytes = marker.Length + 1;
        }
        if (ShouldPersistToDisk()) EnqueueForDisk(marker);
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (gate) return currentRunLines.ToArray();
    }

    // ---- Disk-backed rotating log ---------------------------------------------------------

    private const long SegmentMaxBytes = 10L * 1024 * 1024;
    private const long TotalArchiveCapBytes = 20L * 1024 * 1024;
    private const string ActiveFileName = "AnoMech-active.log";
    private const string ArchivePrefix = "AnoMech-Debug-";
    private const string ArchiveSuffix = ".log.gz";

    private readonly record struct LogCommand(string? Line, bool Rotate);

    private static Channel<LogCommand>? channel;
    private static Task? writerTask;
    private static string? logDir;
    private static bool initialized;

    // Called once from Plugin's constructor. Safe to call more than once (no-ops after the
    // first) -- the only synchronous work is creating a directory; the rest runs on the
    // background writer task.
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;

        try
        {
            var baseDir = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
            if (baseDir == null)
            {
                Plugin.Log.Warning("[DiagnosticLog] No plugin assembly directory -- disk logging disabled, in-memory buffer still works.");
                return;
            }
            logDir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(logDir);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[DiagnosticLog] Failed to set up log directory: {e.Message}");
            return;
        }

        channel = Channel.CreateBounded<LogCommand>(new BoundedChannelOptions(20_000)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        writerTask = Task.Run(RunWriterLoopAsync);
    }

    private static void EnqueueForDisk(string line) => channel?.Writer.TryWrite(new LogCommand(line, false));

    // Forces the active segment to compress/archive now regardless of size -- Game.Leave calls
    // this so leaving a session always starts a fresh segment. Non-blocking like every call here.
    public static void RotateNow() => channel?.Writer.TryWrite(new LogCommand(null, true));

    // Called once from Plugin.Dispose(). Completes the channel and waits briefly for the writer
    // to flush and close the active file, so an unload/reload doesn't lose the queued tail.
    public static void Shutdown()
    {
        channel?.Writer.TryComplete();
        try
        {
            writerTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort -- an unusually slow disk shouldn't hang plugin teardown.
        }
    }

    private static async Task RunWriterLoopAsync()
    {
        var activePath = Path.Combine(logDir!, ActiveFileName);
        FileStream activeStream;
        StreamWriter activeWriter;
        long activeBytes;

        try
        {
            // A leftover unrotated active file (reload or crash) -- preserve it instead of
            // letting the fresh FileStream below truncate it.
            await RotateActiveFileAsync(activePath);
            (activeStream, activeWriter, activeBytes) = OpenFreshActiveFile(activePath);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[DiagnosticLog] Failed to open active log file -- disk logging disabled for this session: {e.Message}");
            return;
        }

        await foreach (var cmd in channel!.Reader.ReadAllAsync())
        {
            // Per-command try/catch -- one failed write/rotation shouldn't kill the writer.
            try
            {
                if (cmd.Line is { } line)
                {
                    await activeWriter.WriteLineAsync(line);
                    activeBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                }

                if ((cmd.Rotate || activeBytes >= SegmentMaxBytes) && activeBytes > 0)
                {
                    await activeWriter.FlushAsync();
                    await activeWriter.DisposeAsync();
                    await activeStream.DisposeAsync();
                    await RotateActiveFileAsync(activePath);
                    (activeStream, activeWriter, activeBytes) = OpenFreshActiveFile(activePath);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Warning($"[DiagnosticLog] Writer loop error (continuing): {e.Message}");
            }
        }

        await activeWriter.DisposeAsync();
        await activeStream.DisposeAsync();
    }

    private static (FileStream Stream, StreamWriter Writer, long Bytes) OpenFreshActiveFile(string activePath)
    {
        var stream = new FileStream(activePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        // AutoFlush off -- StreamWriter's own buffer flushes once full, so at-risk data between
        // explicit flushes stays tiny; flushing every line would make each log call synchronous.
        var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };
        // Stamped so a later rotation can recover which build wrote this segment, rather than
        // mislabeling it with whatever build happens to be running when it's finally rotated.
        var header = $"# AnoMech build={PluginBuildInfo.Checksum} started={DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        writer.WriteLine(header);
        writer.Flush();
        return (stream, writer, Encoding.UTF8.GetByteCount(header) + Environment.NewLine.Length);
    }

    // Moves the active file aside first, then compresses it -- so even if compression fails,
    // the original survives instead of being clobbered by the fresh file the caller opens right
    // after. No-op if nothing's there.
    private static async Task RotateActiveFileAsync(string activePath)
    {
        if (!File.Exists(activePath)) return;
        if (new FileInfo(activePath).Length == 0)
        {
            try { File.Delete(activePath); } catch { /* harmless leftover, ignore */ }
            return;
        }

        var quarantinePath = Path.Combine(logDir!, $"AnoMech-active-pending-{Guid.NewGuid():N}.log");
        try
        {
            File.Move(activePath, quarantinePath);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[DiagnosticLog] Failed to move active log aside for rotation: {e.Message} (left in place).");
            return;
        }

        await CompressAndArchiveAsync(quarantinePath);
    }

    private static async Task CompressAndArchiveAsync(string sourcePath)
    {
        try
        {
            var checksum = ReadHeaderChecksum(sourcePath);
            var shortChecksum = checksum.Length >= 6 ? checksum[..6] : checksum;
            var archivePath = NextArchivePath(shortChecksum);

            var openedSource = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using (openedSource)
            {
                var openedDest = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await using (openedDest)
                {
                    var gzip = new GZipStream(openedDest, CompressionLevel.Optimal);
                    await using (gzip)
                        await openedSource.CopyToAsync(gzip);
                }
            }

            File.Delete(sourcePath);
            EnforceTotalArchiveCap();
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[DiagnosticLog] Failed to compress log segment {sourcePath}: {e.Message} (left uncompressed on disk).");
        }
    }

    private static string ReadHeaderChecksum(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            const string prefix = "# AnoMech build=";
            if (reader.ReadLine() is { } first && first.StartsWith(prefix))
            {
                var afterBuild = first[prefix.Length..];
                var spaceIdx = afterBuild.IndexOf(' ');
                return spaceIdx >= 0 ? afterBuild[..spaceIdx] : afterBuild;
            }
        }
        catch
        {
            // Fall through -- an unreadable/missing header just means an older or damaged file.
        }
        return "unknown";
    }

    private static string NextArchivePath(string shortChecksum)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var basePath = Path.Combine(logDir!, $"{ArchivePrefix}{stamp}-{shortChecksum}{ArchiveSuffix}");
        if (!File.Exists(basePath)) return basePath;
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(logDir!, $"{ArchivePrefix}{stamp}-{shortChecksum}-{i}{ArchiveSuffix}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    // Parses the "yyyyMMdd-HHmmss" stamp from "AnoMech-Debug-{stamp}-{checksum}[-N].log.gz".
    // Null for anything that doesn't match, so the caller can fall back to filesystem metadata.
    private static DateTime? ParseArchiveTimestamp(string fileName)
    {
        if (!fileName.StartsWith(ArchivePrefix)) return null;
        var rest = fileName[ArchivePrefix.Length..];
        if (rest.Length < 15) return null; // "yyyyMMdd-HHmmss" is 15 chars
        var stamp = rest[..15];
        return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", null,
            System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static void EnforceTotalArchiveCap()
    {
        try
        {
            // Sorted by the filename's embedded timestamp, not filesystem CreationTime, which a
            // copy/move can reset. Falls back to CreationTimeUtc for a non-matching name.
            var files = new DirectoryInfo(logDir!)
                .GetFiles($"{ArchivePrefix}*{ArchiveSuffix}")
                .OrderBy(f => ParseArchiveTimestamp(f.Name) ?? f.CreationTimeUtc)
                .ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= TotalArchiveCapBytes) break;
                try
                {
                    total -= file.Length;
                    file.Delete();
                }
                catch (Exception e)
                {
                    Plugin.Log.Warning($"[DiagnosticLog] Failed to delete old log archive {file.Name}: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[DiagnosticLog] Failed to enforce archive size cap: {e.Message}");
        }
    }
}
