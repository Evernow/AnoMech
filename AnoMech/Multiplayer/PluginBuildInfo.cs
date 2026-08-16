using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace AnoMech.Multiplayer;

// Identifies exactly which build of the plugin is running. A host and peer on
// different code -- a stale update, a local dev build that happens to share a
// version number, a hotfix -- can silently desync in exactly the ways several
// rounds of multiplayer bugfixes this project has already chased (mismatched
// scenario timelines, differing message shapes, ...). Version alone can't
// catch that (two different builds can share a version number); Checksum
// hashes the DLL's own bytes so it can.
internal static class PluginBuildInfo
{
    public static string Version { get; } = ComputeVersion();
    public static string Checksum { get; } = ComputeChecksum();

    private static string ComputeVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }

    private static string ComputeChecksum()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "unknown";
            using var stream = File.OpenRead(path);
            // First 16 hex chars (64 bits) of the DLL's SHA-256 -- plenty to tell
            // builds apart without bloating every Hello/lobby broadcast with a
            // full 64-char hash nobody needs to read in full.
            return Convert.ToHexString(SHA256.HashData(stream))[..16];
        }
        catch (Exception e)
        {
            Plugin.Log.Warning($"[Multiplayer] Failed to checksum plugin DLL: {e.Message}");
            return "unknown";
        }
    }
}
