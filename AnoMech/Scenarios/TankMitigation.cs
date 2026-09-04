using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Scenarios;

// A tankbuster a scenario wants the multiplayer host to be able to pre-plan mitigation for.
// Id is a stable per-scenario key (e.g. "p3-thunder3-1") the host's plan and AiReplayState
// messages are keyed on -- not an index that shifts if a cast is reordered. RawDamage is the
// unmitigated hit size in actual HP, a FIXED number like a real tankbuster -- mitigating it
// down to something survivable is the tank's job. 0f means "any tank survives," the old behavior.
public readonly record struct TankBusterCastInfo(string Id, string Label, float RawDamage);

// Shared tankbuster-survival resolution any scenario can call instead of DamageSolver's old
// pure role gate (any tank survives any hit, no matter what they did).
//
// A human's real mitigation press is INTERCEPTED, not observed (LocalPlayerInputHooks.
// UseActionDetour) -- the real ability is never touched, so there's nothing to reset. In its
// place, interception applies a synthetic status via AddStatus and fakes the hotbar's
// cooldown sweep. A bot doppel gets the same AddStatus call from ApplyPlannedMitigationIfBot,
// so both read identically via ActiveTrackedStatusIds. The one exception: a multiplayer
// peer's own intercepted press lands on their own client, not the host's puppet copy -- see
// SelfMitigationMessage for how that crosses the network.
public static unsafe class TankMitigation
{
    // Derived from TankMitigationChart -- only entries with a confirmed status id and a
    // target-side percentage (SourceSide entries like Reprisal debuff the enemy, not the
    // tank, so ComputeMitigation below can't check them yet). See the chart for sourcing
    // notes and everything still missing a status id.
    public static readonly IReadOnlyDictionary<ushort, float> Percent = TankMitigationChart.All
        .Where(a => a.StatusId != 0 && !a.SourceSide && a.Percent is > 0f)
        .ToDictionary(a => a.StatusId, a => a.Percent!.Value);

    // The enemy-side counterpart to Percent -- SourceSide entries like Reprisal debuff the
    // enemy dealing the hit rather than buffing the tank. Checked separately in
    // SurvivalFraction against whichever SimEnemy a caller identifies as the source.
    public static readonly IReadOnlyDictionary<ushort, float> SourceSidePercent = TankMitigationChart.All
        .Where(a => a.StatusId != 0 && a.SourceSide && a.Percent is > 0f)
        .ToDictionary(a => a.StatusId, a => a.Percent!.Value);

    // Interception lookup -- an ability needs a known Percent OR an explicit Shield opt-in to
    // be intercepted; otherwise it's a no-op illusion (real ability blocked, nothing gained),
    // so it's left alone. Shield covers a no-Percent absorption ability (Divine Veil) that
    // still needs blocking so it doesn't eat its real cooldown outside the sim's control.
    public static readonly IReadOnlyDictionary<uint, TankMitigationAbility> ByActionId = TankMitigationChart.All
        .Where(a => a.ActionId != 0 && a.StatusId != 0 && (a.Percent is > 0f || a.Shield))
        .ToDictionary(a => a.ActionId);

    public static bool IsInvuln(ushort statusId) => Percent.TryGetValue(statusId, out var pct) && pct >= 1f;

    // Public so callers outside this class (MultiplayerManager's peer self-report, the
    // interception point, DebugMenu) can read the same real-status list this resolves
    // mitigation from, without duplicating the native pointer walk.
    public static IReadOnlyList<ushort> ActiveTrackedStatusIds(SimCharacter member)
    {
        var bc = member.BattleCharaPtr;
        if (bc == null) return [];
        var result = new List<ushort>();
        foreach (var status in bc->StatusManager.Status)
            if (status.StatusId != 0 && Percent.ContainsKey(status.StatusId))
                result.Add(status.StatusId);
        return result;
    }

    // 1f = no mitigation, 0f = fully invulned. Real mitigation stacks multiplicatively
    // (Rampart 20% + Reprisal 10% is 1 - 0.8*0.9 = 28% total, not 30%), not additively.
    private static float SurvivalFraction(IEnumerable<ushort> activeStatusIds)
    {
        var fraction = 1f;
        foreach (var id in activeStatusIds)
        {
            if (!Percent.TryGetValue(id, out var pct)) continue;
            if (pct >= 1f) return 0f; // invuln short-circuits regardless of anything else active
            fraction *= 1f - pct;
        }
        return fraction;
    }

    // The role-based entry point scenario code should use -- reads real native statuses for a
    // local player/bot doppel, and the peer's self-reported set for a multiplayer puppet
    // (whose native StatusManager never sees the owning peer's own press). mitigationSource,
    // when given, folds in that enemy's own SourceSide debuffs.
    public static float SurvivalFraction(SimParty party, PartyRole role, SimEnemy? mitigationSource = null)
    {
        var member = party.Get(role);
        if (member == null) return 1f;
        IEnumerable<ushort> activeIds = member is SimNetworkPuppet && Plugin.MultiplayerInstance is { IsHost: true } mp
            ? mp.PeerMitigationStatusIds(role)
            : ActiveTrackedStatusIds(member);
        var fraction = SurvivalFraction(activeIds);
        if (mitigationSource != null) fraction *= SourceSideFraction(mitigationSource);
        return fraction;
    }

    // No invuln case expected among SourceSide debuffs, so this only ever multiplies down --
    // unlike the target-side SurvivalFraction, nothing here short-circuits to 0f.
    private static float SourceSideFraction(SimEnemy source)
    {
        var bc = source.BattleCharaPtr;
        if (bc == null) return 1f;
        var fraction = 1f;
        foreach (var status in bc->StatusManager.Status)
            if (status.StatusId != 0 && SourceSidePercent.TryGetValue(status.StatusId, out var pct))
                fraction *= 1f - pct;
        return fraction;
    }

    // Observed real-game variance on a tankbuster's own unmitigated hit -- +/-5%, uniform.
    // Applied once per resolved hit in ApplyTankBusterDamage, on top of whatever fixed
    // rawDamage a scenario declares, the same "roll a real number, don't just use the flat
    // constant" idea TankHpRegen already applies to its own heal amount.
    private const float RawDamageVarianceFraction = 0.05f;
    private static readonly Random rawDamageRng = new();

    // rawDamage is the unmitigated hit size, in actual HP -- a FIXED number, like a real
    // tankbuster, rolled ±5% (RawDamageVarianceFraction). Checked against the target's
    // CURRENT HP, not a fresh 100%-of-max baseline -- so two close hits genuinely stack, with
    // TankHpRegen only closing the gap if there's time. 0f damage always survives trivially.
    //
    // Order matches real FFXIV: flat-% mitigation reduces the hit first, THEN a shield
    // absorbs what's left, THEN real HP is spent. TankShieldTracker banks/consumes in
    // fraction-of-max-HP terms, so the hit is converted to a fraction just for that call and
    // back to HP immediately after. This is also where the shield is actually spent and
    // bc->Health actually written (called once per resolved hit, from DamageSolver.CheckLethal).
    public static unsafe bool ApplyTankBusterDamage(SimParty party, PartyRole role, float rawDamage, SimEnemy? mitigationSource = null)
    {
        var bc = party.Get(role)?.BattleCharaPtr;
        if (bc == null) return true;
        var beforeHp = bc->Health;
        var variance = 1f + (float)(rawDamageRng.NextDouble() * 2.0 - 1.0) * RawDamageVarianceFraction;
        var rolledDamage = rawDamage * variance;
        var fraction = SurvivalFraction(party, role, mitigationSource);
        var afterPercentMitigation = rolledDamage * fraction;
        var absorbedFraction = TankShieldTracker.Consume(role, afterPercentMitigation / bc->MaxHealth);
        var landingHp = afterPercentMitigation - absorbedFraction * bc->MaxHealth;
        var survives = landingHp < beforeHp;
        if (survives) bc->Health -= (uint)landingHp;
        DiagnosticLog.Info(
            $"[TankMitigation] ApplyTankBusterDamage: {role} baseRawDamage={rawDamage:F0} rolledDamage={rolledDamage:F0} mitigationFraction={fraction:F3} "
            + $"shieldAbsorbed={absorbedFraction * bc->MaxHealth:F0}hp landingHp={landingHp:F0} maxHp={bc->MaxHealth} hp {beforeHp}->{(survives ? bc->Health : 0)} "
            + $"survives={survives}");
        return survives;
    }

    // True for party slots nothing real is pressing buttons for: a plain AI bot doppel, or
    // the local player's own character while DebugBotControl puppets its movement (that flag
    // only drives MoveTo/Intercept, never presses). A normal player and a peer's puppet both
    // have their own intercepted/reported statuses instead (see SurvivalFraction).
    public static bool IsBotDriven(SimParty party, SimCharacter member)
        => (!ReferenceEquals(member, party.Player) || DebugBotControl.Enabled) && member is not SimNetworkPuppet;

    // Called right before resolving a tankbuster, so a bot-driven target's mitigation
    // reflects the host's plan for this cast, the same way an intercepted human press
    // applies its own synthetic status. No-op for a real player or a network puppet.
    public static void ApplyPlannedMitigationIfBot(SimParty party, SimCharacter target, string castId)
    {
        if (!IsBotDriven(party, target)) return;
        if (Plugin.MultiplayerInstance?.Session.TankBusterPlan.GetValueOrDefault(castId) is not { } statusId || statusId == 0)
            return;
        target.AddStatus(statusId, duration: 5f);
    }
}
