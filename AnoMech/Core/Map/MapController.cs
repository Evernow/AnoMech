using AnoMech.Helpers;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnoMech.Core.Map;

// Unified entry point for zone loading and map effects. Owned by SimWorld as
// world.Map. Zone and effects state are reset by Reset(); zone hooks are
// released by Dispose().
public sealed class MapController : IDisposable
{
    private readonly MapEffects effects = new();
    private readonly ZoneSession zone = new();

    // Collider-deactivation state. Zone-load is async (resources stream in over
    // several frames), so each pending drop re-tries DisableSpawnAreaColliders
    // each frame until at least one SharedGroup is found near its center, or it
    // times out. Holds the spawn-ring barrier (armed by TryLoad) plus any arena
    // points a scenario requested via ArmColliderDrops.
    private readonly List<PendingColliderDrop> pendingColliderDrops = new();
    private const int BarrierDropMaxFrames = 300; // ~5s at 60fps
    private const float ColliderDropRadius = 10f; // per-point radius (matches spawn barrier)

    private struct PendingColliderDrop
    {
        public Vector3 Center;
        public float Radius;
        public int FramesLeft;
    }

    // AddEffect/DirectorUpdate calls that couldn't apply immediately (zone still
    // async-loading -- same race as the collider drops above) get retried here
    // each Tick until they land or time out. Needed because a peer's zone load
    // consistently lags the host's by a few seconds, and a scenario's early
    // world.Events.Add(0..3f, ...) calls land well inside that window.
    private readonly List<PendingMapEffect> pendingEffects = new();
    private readonly List<PendingDirectorUpdate> pendingDirectorUpdates = new();

    private struct PendingMapEffect
    {
        public uint PacketFlags;
        public byte Index;
        public int FramesLeft;
    }

    private struct PendingDirectorUpdate
    {
        public uint Category, Arg1, Arg2, Arg3, Arg4, Arg5, Arg6;
        public int FramesLeft;
    }

    // ── Zone ─────────────────────────────────────────────────────────────────

    // True while a scenario was started by loading a client-side zone.
    // Cleared by Unload() and Reset().
    public bool IsInInstance { get; private set; }

    public bool IsZoneLoaded => zone.IsActive;
    public bool IsInInn() => ZoneSession.IsInInn();

    // Load the target territory client-side. Must be called from the Inn.
    public void Load(uint territoryId, Vector3 playerPosition, byte levelSync, ushort itemLevelSync) => zone.Enter(territoryId, playerPosition, levelSync, itemLevelSync);

    // Apply weather after a zone load (1-second delayed to let the engine settle).
    public void ApplyWeather(byte weatherId) => zone.ApplyWeather(weatherId);

    // Fired whenever SetWeather actually applies, purely so MultiplayerManager can mirror
    // it to peers without this class needing to know or care whether multiplayer is even
    // active -- same reasoning as EffectApplied/DirectorUpdated above. Scenarios call
    // world.SetWeather directly mid-fight (e.g. P2 Forsaken's arena-transform weather cue
    // at its "gold->black" moment) as a host/solo-only Run() event; with no relay, a peer's
    // client never received it at all -- every SharedGroup/BgPart involved could report
    // full success (state, readiness, activity all correct) while the environment's own
    // weather-driven lighting/effects stayed on the untransformed default.
    public event Action<byte, float>? WeatherChanged;

    // Immediately change the active weather (mid-scenario). transition = fade seconds.
    public void SetWeather(byte weatherId, float transition = 0.5f)
    {
        zone.SetWeather(weatherId, transition);
        WeatherChanged?.Invoke(weatherId, transition);
    }

    // Revert to the saved inn territory and restore position.
    public void Unload()
    {
        zone.Revert(false);
        IsInInstance = false;
        pendingColliderDrops.Clear();
    }

    // Per-frame poll. Called from SimWorld.Tick.
    internal void Tick()
    {
        for (int i = pendingColliderDrops.Count - 1; i >= 0; i--)
        {
            var drop = pendingColliderDrops[i];
            var disabled = DirectorFunctions.DisableSpawnAreaColliders(drop.Center, drop.Radius);
            if (disabled > 0) { pendingColliderDrops.RemoveAt(i); continue; }
            drop.FramesLeft--;
            if (drop.FramesLeft <= 0)
            {
                Plugin.Log.Warning($"[BarrierDrop] Gave up after {BarrierDropMaxFrames} frames — no SGs found near ({drop.Center.X:F2},{drop.Center.Y:F2},{drop.Center.Z:F2})");
                pendingColliderDrops.RemoveAt(i);
            }
            else
            {
                pendingColliderDrops[i] = drop;
            }
        }

        // Forward/insertion order, unlike pendingColliderDrops above -- collider
        // drops are position-independent so retry order doesn't matter, but
        // MapEffects are not: per MapEffects.cs, an SGB slot's State is locked in
        // by whichever call reaches it FIRST, so if two calls to the same index
        // (different State) are both stuck pending at once, retrying newest-first
        // would let the later call win the lock instead of the earlier one --
        // silently producing the wrong arena visual state on whichever client hit
        // the retry path (typically a peer whose zone-load lagged the host's).
        for (int i = 0; i < pendingEffects.Count; i++)
        {
            var pending = pendingEffects[i];
            if (effects.Apply(pending.PacketFlags, pending.Index)) { pendingEffects.RemoveAt(i); i--; continue; }
            pending.FramesLeft--;
            if (pending.FramesLeft <= 0)
            {
                DiagnosticLog.Warn($"[MapEffect] Gave up applying packetFlags=0x{pending.PacketFlags:X8} index=0x{pending.Index:X} after {BarrierDropMaxFrames} frames — zone likely never finished loading.");
                pendingEffects.RemoveAt(i);
                i--;
            }
            else
            {
                pendingEffects[i] = pending;
            }
        }

        for (int i = 0; i < pendingDirectorUpdates.Count; i++)
        {
            var pending = pendingDirectorUpdates[i];
            if (InstanceContentDirectorHelper.ProcessDirectorUpdate(pending.Category, pending.Arg1, pending.Arg2, pending.Arg3, pending.Arg4, pending.Arg5, pending.Arg6))
            {
                pendingDirectorUpdates.RemoveAt(i);
                i--;
                continue;
            }
            pending.FramesLeft--;
            if (pending.FramesLeft <= 0)
            {
                DiagnosticLog.Warn($"[MapEffect] Gave up applying DirectorUpdate category=0x{pending.Category:X8} after {BarrierDropMaxFrames} frames — zone likely never finished loading.");
                pendingDirectorUpdates.RemoveAt(i);
                i--;
            }
            else
            {
                pendingDirectorUpdates[i] = pending;
            }
        }
    }

    // Enter the scenario's target instance if conditions are met.
    // Sets IsInInstance when the zone is already active or the Inn load succeeds.
    // No-op (IsInInstance stays false) when target is null or the player isn't in the Inn.
    public void TryLoad(TargetInstance? target, byte levelSync, ushort itemLevelSync)
    {
        if (target == null) return;
        // Fresh load only when no zone is active yet (must be in the Inn). When a
        // zone is already loaded we're switching scenarios within the same
        // territory — skip the reload but still fall through to re-apply weather.
        bool freshLoad = false;
        if (!IsZoneLoaded)
        {
            if (!IsInInn()) return;
            Load(target.TerritoryId, target.PlayerPosition, levelSync, itemLevelSync);
            freshLoad = true;
        }
        if (target.WeatherId is { } wid)
        {
            if (freshLoad) ApplyWeather(wid);   // fresh load: delay so the engine settles
            else SetWeather(wid);               // restart/switch in a loaded zone: apply now
        }
        IsInInstance = true;
        effects.Loaded = true;
        InstanceContentDirectorHelper.Commence();
        ArmBarrierDrop(target.PlayerPosition, 10f);
        // freshLoad=false (reusing an already-loaded zone from an earlier run this
        // session) is the one case AddEffect's own async-load retry can't see or
        // account for -- effects.Loaded flips true here either way, so a stale SGB
        // left over from the PREVIOUS run's map state wouldn't show up as a retry/
        // failure at all, just a native call that "succeeds" without visibly
        // changing anything. Worth knowing which case a run was in when diagnosing
        // an arena that still looks wrong despite MapEffectMessage replication and
        // the native hook both checking out.
        DiagnosticLog.Info($"[MapController] TryLoad: freshLoad={freshLoad}, territoryId={target.TerritoryId}.");
    }

    private void ArmBarrierDrop(Vector3 center, float radius)
    {
        pendingColliderDrops.Add(new PendingColliderDrop
        {
            Center = center,
            Radius = radius,
            FramesLeft = BarrierDropMaxFrames,
        });
    }

    // Arm collider drops at scenario-provided arena points (already converted to
    // world coordinates). Same async-load retry as the spawn-ring barrier.
    public void ArmColliderDrops(IEnumerable<Vector3> worldCenters)
    {
        foreach (var center in worldCenters)
            ArmBarrierDrop(center, ColliderDropRadius);
    }

    // ── Map effects ───────────────────────────────────────────────────────────

    // Fired after each native call below actually applies, purely so
    // MultiplayerManager (Core.Map has no business knowing Multiplayer exists)
    // can mirror it to peers without this class needing to know or care whether
    // multiplayer is even active -- see the events' own doc comments for why a
    // peer needs this at all: these are native, this-client-only calls with no
    // other replication path (unlike SimEnemy/SimEventObject/party-role state,
    // none of this flows through a SimObject the existing snapshot sync walks).
    public event Action<uint, byte>? EffectApplied;
    public event Action<uint, uint, uint, uint, uint, uint, uint>? DirectorUpdated;

    // Replay a single MapEffect state change. packetFlags: high16=State, low8=Flags.
    // Queued for retry (see pendingEffects) if the zone isn't ready to accept it
    // yet -- otherwise a peer whose async zone-load lags the host's by even a
    // couple seconds silently loses any effect called in that window, since
    // there's no other way to know it was missed and no packet to re-request it.
    public void AddEffect(uint packetFlags, byte index)
    {
        if (!effects.Apply(packetFlags, index))
        {
            DiagnosticLog.Warn($"[MapEffect] packetFlags=0x{packetFlags:X8} index=0x{index:X} not ready yet -- queued for retry.");
            pendingEffects.Add(new PendingMapEffect { PacketFlags = packetFlags, Index = index, FramesLeft = BarrierDropMaxFrames });
        }
        EffectApplied?.Invoke(packetFlags, index);
    }

    // Replay a native DirectorUpdate event (instance progress / state sync) — the
    // server-side InstanceContentDirector message a scenario timeline replays. Thin
    // forwarder so scenarios address it through world.Map alongside AddEffect.
    // Same retry-if-not-ready treatment as AddEffect, and for the same reason.
    public void DirectorUpdate(uint category, uint arg1 = 0, uint arg2 = 0, uint arg3 = 0, uint arg4 = 0, uint arg5 = 0, uint arg6 = 0)
    {
        if (!InstanceContentDirectorHelper.ProcessDirectorUpdate(category, arg1, arg2, arg3, arg4, arg5, arg6))
        {
            DiagnosticLog.Warn($"[MapEffect] DirectorUpdate category=0x{category:X8} not ready yet -- queued for retry.");
            pendingDirectorUpdates.Add(new PendingDirectorUpdate { Category = category, Arg1 = arg1, Arg2 = arg2, Arg3 = arg3, Arg4 = arg4, Arg5 = arg5, Arg6 = arg6, FramesLeft = BarrierDropMaxFrames });
        }
        DirectorUpdated?.Invoke(category, arg1, arg2, arg3, arg4, arg5, arg6);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        effects.Dispose();
        zone.Dispose();
    }
}
