using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using AnoMech.Core;

namespace AnoMech.Multiplayer;

// Identifies exactly which build of the plugin is running, so a host/peer mismatch (stale
// update, local dev build sharing a version number) can be caught before it desyncs. Version
// alone can't catch that -- Checksum hashes the DLL's own bytes so it can.
internal static class PluginBuildInfo
{
    public static string Version { get; } = ComputeVersion();
    public static string Checksum { get; } = ComputeChecksum();
    public static string ShortChecksum { get; } = Checksum.Length >= 6 ? Checksum[..6] : Checksum;

    private static string ComputeVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }

    private static string ComputeChecksum()
    {
        try
        {
            // Assembly.GetExecutingAssembly().Location is always "" -- Dalamud loads plugin
            // DLLs via Assembly.Load(byte[]), not from a file path. PluginInterface
            // .AssemblyLocation is Dalamud's own answer to where the DLL actually lives.
            var path = Plugin.PluginInterface.AssemblyLocation.FullName;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "unknown";
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream))[..16]; // first 16 hex chars of the SHA-256
        }
        catch (Exception e)
        {
            DiagnosticLog.Warn($"[Multiplayer] Failed to checksum plugin DLL: {e.Message}");
            return "unknown";
        }
    }
}
