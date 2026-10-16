using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.SimObjects;
using AnoMech.Core.UserActions;

namespace AnoMech.Core.EnemyActions;

// A hit whose survival rests on the target's mitigation. Judged: the target's own statuses decide
// it; a bot's are never looked at. At: the resolve's moment; Statuses: those it judged on.
internal readonly record struct MitigationNeed(uint ActionId, SimCharacter Target, DamageKind Kind, float Required, bool Judged, float At,
    IReadOnlyList<ushort> Statuses);

internal static class DamageCheck
{
    // Every hit judged on mitigation, a bot's included, for the headless test harness.
    public static event Action<MitigationNeed>? NeedsMitigation;

    // Null when the hit is survivable, otherwise why it kills ("" when it simply does).
    public static string? LethalCause(SimCharacter target, VulnSpec spec, Severity severity, DamageKind kind, EnemyActionContext ctx)
    {
        var statuses = ctx.StatusesOf(target);
        if (spec.Protections.Any(id => Held(statuses, id) != null)) return null;
        if (severity.Kind == SeverityKind.Lethal) return MissingProtection(spec) ?? "";
        var vuln = CarriedVulnerability(target, spec, statuses);
        if (vuln is { RequiredMitigation: null }) return "had vuln up debuff";
        if (severity.Kind == SeverityKind.TankBuster && !IsTank(target)) return "tank buster";
        var required = MathF.Max(severity.MinMitigation, vuln?.RequiredMitigation ?? 0f);
        if (required > 0f)
            NeedsMitigation?.Invoke(new MitigationNeed(ctx.Action.ActionId, target, kind, required, ChecksMitigation(target, ctx.Party), ctx.At,
                statuses.Select(s => s.StatusId).ToList()));
        if (Survives(target, statuses, ctx, required, kind)) return null;
        return vuln != null ? "not enough mitigation for a hit with vuln up" : "not enough mitigation";
    }

    // As FindStatus finds it: the first of that id with no source object.
    private static HeldStatus? Held(IReadOnlyList<HeldStatus> statuses, ushort statusId)
    {
        foreach (var status in statuses)
            if (status.StatusId == statusId && !status.Sourced) return status;
        return null;
    }

    private static string? MissingProtection(VulnSpec spec)
        => spec.Protections.Count > 0 ? $"no {Natives.Data.StatusName(spec.Protections[0])}" : null;

    public static bool IsTank(SimCharacter target) => target is ISimPartyMember { Role: PartyRole.OffTank or PartyRole.MainTank };

    private static Vulnerability? CarriedVulnerability(SimCharacter target, VulnSpec spec, IReadOnlyList<HeldStatus> statuses)
    {
        foreach (var vuln in spec.Vulnerabilities)
        {
            if (Held(statuses, vuln.StatusId) is not { } status || status.Stacks < vuln.MinStacks) continue;
            DiagnosticLog.Info($"[EnemyAction] {(target as ISimPartyMember)?.Role} is hit carrying vuln up {vuln.StatusId}: needs {(vuln.RequiredMitigation is { } required ? $"{required:P0} mitigation" : "nothing, always lethal")}.");
            return vuln;
        }
        return null;
    }

    // Only a human's own statuses are ever checked: a bot always passes. Spends the target's shields.
    private static bool Survives(SimCharacter target, IReadOnlyList<HeldStatus> statuses, EnemyActionContext ctx, float requiredMitigation, DamageKind kind)
    {
        if (requiredMitigation <= 0f || !ChecksMitigation(target, ctx.Party)) return true;
        var effective = Mitigation.Effective(statuses.Select(s => s.StatusId), kind);
        ctx.SpendShields(target);
        var survives = effective >= requiredMitigation - 0.0005f;
        DiagnosticLog.Info($"[EnemyAction] Mitigation check: {(target as ISimPartyMember)?.Role} has {effective:P1} {kind}, needs {requiredMitigation:P1} -- {(survives ? "survives" : "dies")}.");
        return survives;
    }

    private static bool ChecksMitigation(SimCharacter target, SimParty party)
        => Mitigation.Required && target is ISimPartyMember && !party.IsBotDriven(target);
}
