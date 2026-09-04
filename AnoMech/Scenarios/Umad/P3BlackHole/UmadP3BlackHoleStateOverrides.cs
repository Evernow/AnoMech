using AnoMech.Core.Game.Party;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

// One Thunder III set's two hits (see UmadP3BlackHoleScenario.RunThunder), planned in
// advance for whichever tank slot(s) end up bot-driven. Doesn't matter when both tanks are
// real players -- see UmadP3BlackHoleSettingsWindow's planner. No "auto/unset" option --
// ThunderSet1/2 always carry an explicit plan, so every consumer switches on all four cases.
public enum ThunderIIIAssignment
{
    MtInvulnsBoth,  // MT stands closest for both hits in the set and pops an invuln before the first
    OtInvulnsBoth,  // mirror of MtInvulnsBoth
    ShareMtFirst,   // hits split -- MT takes the first (needs its own + party mitigation, no invuln), OT takes the second
    ShareOtFirst,   // mirror of ShareMtFirst
}

// Shared between UmadP3BlackHoleAi (positioning) and UmadP3BlackHoleScenario (invuln-granting,
// mitigation-plan population) so every consumer agrees on the same role assignment for a given
// plan -- disagreement between them caused this whole system's original bugs.
public static class ThunderIIIPlanning
{
    // Which role stands closest to Exdeath (and so takes the hit, since targeting is purely
    // party.Find.Closest) for a set's first strike, and -- for a Share plan -- which role
    // swaps in for the second.
    public static (PartyRole First, PartyRole? Second) Roles(ThunderIIIAssignment plan) => plan switch
    {
        ThunderIIIAssignment.MtInvulnsBoth => (PartyRole.MainTank, null),
        ThunderIIIAssignment.OtInvulnsBoth => (PartyRole.OffTank, null),
        ThunderIIIAssignment.ShareMtFirst  => (PartyRole.MainTank, PartyRole.OffTank),
        ThunderIIIAssignment.ShareOtFirst  => (PartyRole.OffTank, PartyRole.MainTank),
        _                                   => throw new System.ArgumentOutOfRangeException(nameof(plan), plan, null),
    };

    // Which role (if any) needs a REAL scripted invuln for this set -- only the two
    // "InvulnsBoth" plans do; a Share relies entirely on TankMitigation's mitigated
    // fixed-HP survival check (UmadP3BlackHoleScenario.ApplyPlannedThunderMitigation), never
    // a hard invuln.
    public static PartyRole? InvulnRole(ThunderIIIAssignment plan) => plan switch
    {
        ThunderIIIAssignment.MtInvulnsBoth => PartyRole.MainTank,
        ThunderIIIAssignment.OtInvulnsBoth => PartyRole.OffTank,
        _                                    => null,
    };
}

// User-controlled overrides for UmadP3BlackHoleState's randomized fields. Bound by
// the scenario's settings UI; null/default values leave the field randomized at
// scenario start. The state ctor consumes this directly.
// See UmadP4KefkaSaysStateOverrides for the canonical shape.
public sealed class UmadP3BlackHoleStateOverrides
{
    public int? LineNumber { get; set; }            // null = random; 1/2/3 = First/Second/Third in line (forces the player into that slot)
    public bool? Accretion { get; set; }            // null = random; true = give the player Accretion, false = keep it off them.
                                                    //   Yes is ignored for tanks and third-in-line (they never get Accretion in the fight).
    public uint? FirstSlap { get; set; }            // null = random; else ActionId.SlapHappy_Left / .SlapHappy_Right (debug-only UI)
    public bool? FirstSlapAllOnPlayer { get; set; } // null = random targets; true = aim every first-slap cone at the player (debug-only UI)

    // See ThunderIIIAssignment -- one plan per Thunder III set. Defaults match the sim's
    // standing default (MT solo-tanks Set 1 behind an invuln, Set 2 shared MT-first).
    public ThunderIIIAssignment ThunderSet1 { get; set; } = ThunderIIIAssignment.MtInvulnsBoth;
    public ThunderIIIAssignment ThunderSet2 { get; set; } = ThunderIIIAssignment.ShareMtFirst;
}
