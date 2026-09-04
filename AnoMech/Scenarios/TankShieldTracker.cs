using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios;

// Sim-only "how much absorbable damage is currently banked" bookkeeping for a barrier/shield
// mitigation -- a depletable pool, unlike TankMitigation.Percent's flat-%-while-active math.
// Tracked as a fraction of the shielded role's own max HP.
//
// Reset alongside the rest of a run's bookkeeping (Game.ResetInternal). ClearAllVisuals is a
// SEPARATE step -- Reset only clears this class's own data, not the native ShieldValue byte it
// wrote onto a real BattleChara, which otherwise keeps showing a fake shield bar in real
// content forever for the local player (who persists past the sim, unlike a doppel).
public static class TankShieldTracker
{
    private readonly record struct ShieldChunk(float FractionOfMaxHp, long ExpiresAtMs);

    // Marks a chunk that only SetFromPeerReport replaces, never a timer -- used for the host's
    // mirror of a peer's own self-shield (see SetFromPeerReport's own doc comment).
    private const long NoExpiry = long.MaxValue;

    private static readonly Dictionary<PartyRole, List<ShieldChunk>> chunksByRole = new();

    public static void Reset() => chunksByRole.Clear();

    // Adds on top of whatever's already banked -- real shields from different sources stack
    // additively, so this never replaces. Local grants go through Grant + RefreshVisual
    // directly (see LocalPlayerInputHooks).
    public static void Grant(PartyRole role, float fractionOfMaxHp, float durationSeconds)
    {
        if (fractionOfMaxHp <= 0f) return;
        if (!chunksByRole.TryGetValue(role, out var list)) chunksByRole[role] = list = [];
        list.Add(new ShieldChunk(fractionOfMaxHp, Environment.TickCount64 + (long)(durationSeconds * 1000f)));
    }

    // Host-only: overwrites this role's ENTIRE banked shield with a peer's self-reported
    // total -- a current-state snapshot, not an incremental grant. NoExpiry because the
    // peer's NEXT report (including 0f) is what clears this, not an independent host timer.
    public static void SetFromPeerReport(PartyRole role, float fractionOfMaxHp)
    {
        chunksByRole[role] = fractionOfMaxHp > 0f ? [new ShieldChunk(fractionOfMaxHp, NoExpiry)] : [];
    }

    private static void DropExpired(List<ShieldChunk> list)
    {
        var now = Environment.TickCount64;
        list.RemoveAll(c => c.ExpiresAtMs <= now);
    }

    public static float RemainingFraction(PartyRole role)
    {
        if (!chunksByRole.TryGetValue(role, out var list)) return 0f;
        DropExpired(list);
        return list.Sum(c => c.FractionOfMaxHp);
    }

    // Drains oldest-first; returns how much was actually absorbed (capped by what's banked).
    // Called once per hit from ApplyTankBusterDamage, after the flat-% mitigation math.
    public static float Consume(PartyRole role, float fractionOfDamage)
    {
        if (fractionOfDamage <= 0f) return 0f;
        if (!chunksByRole.TryGetValue(role, out var list)) return 0f;
        DropExpired(list);
        var remaining = fractionOfDamage;
        var absorbed = 0f;
        for (var i = 0; i < list.Count && remaining > 0f; i++)
        {
            var take = Math.Min(list[i].FractionOfMaxHp, remaining);
            absorbed += take;
            remaining -= take;
            list[i] = list[i] with { FractionOfMaxHp = list[i].FractionOfMaxHp - take };
        }
        list.RemoveAll(c => c.FractionOfMaxHp <= 0f);
        return absorbed;
    }

    // Writes the native visual for one role -- BattleChara.ShieldValue drives the gold overlay
    // on both the HP bar and party list. Clamped to 0-100 even though the tracked fraction can
    // exceed 1.0 (stacked shields) -- purely cosmetic, absorption math always reads above.
    public static unsafe void RefreshVisual(SimCharacter member, PartyRole role)
    {
        var bc = member.BattleCharaPtr;
        if (bc == null) return;
        var pct = RemainingFraction(role) * 100f;
        bc->ShieldValue = (byte)Math.Clamp(pct, 0f, 100f);
    }

    // Explicit rather than implied by Reset -- call on every sim exit, same call sites as
    // RestoreGaugeIllusion.
    public static unsafe void ClearAllVisuals(SimParty party)
    {
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            if (party.Get(role) is not { } member) continue;
            var bc = member.BattleCharaPtr;
            if (bc != null) bc->ShieldValue = 0;
        }
    }
}
