using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P2Forsaken.Ai;

// Movement choreography for the EU "p3Z Buddy Meow" strat — an isolated fork of the NA
// "Kroxy-Rinon 341 (melee flex)" helper (UmadP2ForsakenRinonAiHelper), free to diverge
// without touching the shared NA helper. See that helper for the framework design notes.
public sealed class UmadP2ForsakenP3ZBuddyMeowAiHelper
{
    private readonly Vector2?[] oddCoords;
    private readonly Vector2?[] evenCoords;
    private readonly Action<UmadP2ForsakenP3ZBuddyMeowAiHelper, int, IList<PartyRole>> reorderActive;

    private UmadP2ForsakenState state = null!;

    private IReadOnlyList<PartyRole> alphaInitial = [];
    private IReadOnlyList<PartyRole> betaInitial = [];
    private List<PartyRole> alpha = [];
    private List<PartyRole> beta = [];

    public UmadP2ForsakenP3ZBuddyMeowAiHelper(
        Vector2?[] oddCoords,
        Vector2?[] evenCoords,
        Action<UmadP2ForsakenP3ZBuddyMeowAiHelper, int, IList<PartyRole>> reorderActive)
    {
        this.oddCoords = oddCoords;
        this.evenCoords = evenCoords;
        this.reorderActive = reorderActive;
    }

    public void Run(UmadP2ForsakenState s, SimWorld world)
    {
        state = s;
        var ai = new AiManager(world);
        Init();

        ai.Move(1f, InitialLineup);
        ai.Move(10.16f, TowerPositions(0), jitter: .0f, sprint: true);
        ai.Move(25.17f, TowerPositions(1), jitter: .0f, sprint: true);
        ai.Move(33f, AllThingsEndsBait(0, 2), sprint: true);
        ai.Move(39.21f, TowerPositions(2), jitter: .0f, sprint: true);
        ai.Move(47.22f, TowerPositions(3), jitter: .0f, sprint: true);
        ai.Move(54f, AllThingsEndsBait(1, 4), sprint: true);
        ai.Move(59.26f, TowerPositions(4), jitter: .0f, sprint: true);
        ai.Move(65.27f, TowerPositions(5), jitter: .0f, sprint: true);
        ai.Move(75f, AllThingsEndsBait(2, 6), sprint: true);
        ai.Move(81.31f, TowerPositions(6), jitter: .0f, sprint: true);
        ai.Move(90.32f, TowerPositions(7), jitter: .0f, sprint: true);
        // Occurrence 3 (t~88.36 castbar) has no "upcoming" tower to bisect against --
        // Tower(7) is the last one, already resolved by 95.82. Unlike 0/1/2, there's no
        // subsequent tower move to collide with (nothing else moves the party again),
        // so this is the one occurrence with room for the real two-step mechanic:
        // gather "between the last towers" (tower 7's own NewNorthAt(7) bisector) the
        // moment Tower(7) resolves, then -- only once the AllThingsEnding castbar
        // itself starts (the clones' simultaneous cast at 101.16, the earliest of the
        // 4 casts) -- relocate across for Future's End. This isn't a race against the
        // cast: the boss's facing locks in at its own Face(party.Player) call and
        // Resolve() never re-reads party.Player, only the boss's now-fixed rotation,
        // so moving away during the 5s cast-to-resolve window (101.16 to 106.16) is
        // exactly what makes Future's End safe -- the cone stays aimed at the
        // now-vacated between-towers spot. Past's End's second move lands on the same
        // spot as the first (AllThingsEndsBait's existing Past branch), a same-spot
        // no-op MoveTo, matching "stay there." First leg gets a relaxed window since
        // there's no rush before the castbar; second leg's worst case (~26.7y,
        // PastMeleeFromCenter+FutureMeleeFromCenter apart on opposite sides of center)
        // still fits comfortably inside the 5s cast window even with sprint.
        ai.Move(95.83f, BetweenLastTowers(), sprint: true);
        ai.Move(101.16f, AllThingsEndsBait(3, 7), sprint: true);
    }

    // Base point is "opposite the upcoming towers" for Future's End, "between the
    // towers" for Past's End -- both via the same NewNorthAt(2*i+2) bisector the
    // upcoming tower pair straddles (GetTowers places them at north.Rotate(-1)/
    // north.Rotate(1)), reusing the exact reference frame the towers themselves are
    // placed in.
    //
    // Future and Past use different distances now, not a shared magnitude flipped by
    // sign: confirmed (see the timing analysis for occurrence-by-occurrence gaps
    // between an AllThingsEnding resolve and the next tower's own resolve) that
    // Future's End's own subsequent tower-transition is geometrically infeasible in
    // this scenario's fixed timeline regardless of distance -- even the closest
    // possible tower assignment needs more than max sprint speed to reach in time.
    // Since there's no walkability tradeoff left to protect by staying close for
    // Future, FutureMeleeFromCenter is pushed further out than Past's own margin, to
    // improve the odds of clearing the All Things Ending cone via range as well as
    // angle (the cone tracks party.Player, who is part of this same shared stack,
    // so angle alone isn't a guarantee -- see prior investigation). Both casters
    // (boss and clones alike) sit fixed at world origin for every resolve, so this
    // is also the exact distance from whichever one is casting. PastMeleeFromCenter
    // keeps the tighter hitbox+6 margin since Past's cone points away from the
    // stack regardless of exact distance, and staying closer gives its own
    // (already tight) tower transition a better chance.
    private const float PastMeleeFromCenter = 9.7f;
    private const float FutureMeleeFromCenter = 11f;

    private Func<IAiMove> AllThingsEndsBait(int i, int northIndex)
    {
        var distance = state.EndAttacks[i] == EndAttack.PastsEnd ? -PastMeleeFromCenter : FutureMeleeFromCenter;
        return () => AiMove.All(new(0, distance))
                           .ApplyPositions(state.NewNorthAt(northIndex).Apply);
    }

    // Unconditional "between the towers" landing spot for occurrence 3's first leg --
    // same formula AllThingsEndsBait uses for Past's End, but not conditioned on the
    // variant since both variants start here (see AllThingsEndsBait(3, 7) for the
    // variant-dependent second leg).
    private Func<IAiMove> BetweenLastTowers()
    {
        return () => AiMove.All(new(0, -PastMeleeFromCenter))
                           .ApplyPositions(state.NewNorthAt(7).Apply);
    }

    private void Init()
    {
        var stacks = state.Lockons.Where(pair => pair.Value == LockonId.ForsakenStack).Select(pair => pair.Key)
                          .ToList();
        var supportPair = (PartyRole)(((int)stacks[0] + 2) % 4);
        var dpsPair = (PartyRole)((((int)stacks[1] - 2) % 4) + 4);
        var list = new List<PartyRole>([stacks[0], stacks[1], supportPair, dpsPair]);
        list.Sort();
        (list[0], list[1]) = (list[1], list[0]); // swap tank and healer
        alpha = list;
        alphaInitial = new List<PartyRole>(alpha);
        list = Enum.GetValues<PartyRole>()
                   .Where(role => !list.Contains(role))
                   .ToList();
        (list[0], list[1]) = (list[1], list[0]); // swap tank and healer
        beta = list;
        betaInitial = new List<PartyRole>(beta);
    }


    public PartyRole ActiveRole(uint mechanic, int towerId, int order)
    {
        var array = towerId is < 3 or 7 ? alpha : beta;
        try
        {
            return array
                   .Where(role => state.Lockons[role] == mechanic)
                   .Skip(order)
                   .First();
        }
        catch (InvalidOperationException)
        {
            DiagnosticLog.Warn($"Lockons {string.Join(",", state.Lockons)}");
            DiagnosticLog.Warn($"Can't find {mechanic}.{order}, for {towerId}, for {string.Join(",", array)}");
            throw;
        }
    }

    private PartyRole PassiveRole(int towerId, int order)
    {
        var array = towerId is < 3 or 7 ? betaInitial : alphaInitial;
        return array[order];
    }

    private IAiMove InitialLineup()
    {
        return AiMove.Create(
            new(-6.2f, -1.7f),  // MT (mirror of M1)
            new(-5.4f, 3.3f),   // OT (mirror of M2)
            new(-5.4f, -3.6f),  // H1 (mirror of R1)
            new(-3.1f, 5.3f),   // H2 (mirror of R2)
            new(6.2f, -1.7f),   // M1
            new(5.4f, 3.3f),    // M2
            new(5.4f, -3.6f),   // R1
            new(3.1f, 5.3f)     // R2
        );
    }

    private Func<IAiMove> TowerPositions(int i)
    {
        return () =>
        {
            var move = i % 2 == 1 ? EvenTower(i) : OddTower(i); // odd/even flipped because 0 indexing teehee
            reorderActive(this, i, i is < 3 or 7 ? alpha : beta); // plug point: same active-group rule as ActiveRole
            return move;
        };
    }

    private IAiMove OddTower(int i)
    {
        var (rightStack, leftStack) = StackSpots(i);
        return AiMove.Create(oddCoords)
                     .Assignments([
                         rightStack,
                         ActiveRole(LockonId.ForsakenCone, i, 0),
                         leftStack,
                         ActiveRole(LockonId.ForsakenChariot, i, 0),
                         PassiveRole(i, 0),
                         PassiveRole(i, 1),
                         PassiveRole(i, 2),
                         PassiveRole(i, 3)
                     ])
                     .ApplyPositions(state.NewNorthAt(i).Apply);
    }

    // Which stack-holder takes the right [0] spot vs the left [2] spot on an S tower.
    // First tower set only (i == 0): the cone group's stack-holder takes the right spot,
    // the chariot group's stack-holder the left. Every later tower reverts to the standard
    // alpha-order ruling (#0 -> right, #1 -> left). This is the p3Z Buddy Meow change; the
    // reorder plug-point can't express it because it fires after each tower's move is built.
    private (PartyRole right, PartyRole left) StackSpots(int i)
    {
        var first = ActiveRole(LockonId.ForsakenStack, i, 0);
        var second = ActiveRole(LockonId.ForsakenStack, i, 1);
        if (i != 0) return (first, second);
        return GroupLockon(first) == LockonId.ForsakenCone ? (first, second) : (second, first);
    }

    // The Cone/Chariot telegraph carried by stackRole's group (supports = roles 0-3,
    // DPS = 4-7). The stack-holder's own lockon is Stack, so read a same-group non-stack
    // member to recover whether that group drew cones or chariots.
    private uint GroupLockon(PartyRole stackRole)
    {
        var support = (int)stackRole < 4;
        foreach (var (role, lockon) in state.Lockons)
            if (((int)role < 4) == support && lockon != LockonId.ForsakenStack)
                return lockon;
        return 0;
    }

    private IAiMove EvenTower(int i)
    {
        return AiMove.Create(evenCoords)
                     .Assignments([
                         ActiveRole(LockonId.ForsakenCone, i, 0),
                         ActiveRole(LockonId.ForsakenChariot, i, 0),
                         ActiveRole(LockonId.ForsakenCone, i, 1),
                         ActiveRole(LockonId.ForsakenChariot, i, 1),
                         PassiveRole(i, 0),
                         PassiveRole(i, 1),
                         PassiveRole(i, 2),
                         PassiveRole(i, 3),
                     ])
                     .ApplyPositions(state.NewNorthAt(i).Apply);
    }

    // --- Coordinate sets ---
    // 16 scenario-local XZ coords per set: 8 for odd-index towers, 8 for even-index
    // towers (active 4 + passive 4 each). Passed into the constructor by the strats.
    // AiMove copies these on Create, so its per-move rotation never mutates the set.

    // EU "p3Z Buddy Meow" S-tower layout — hand-placed spots (authoring frame, north up).
    // 4 active (stack/cone/stack/chariot) + 4 passive baits; [6]/[7] share one in-stack spot.
    public static readonly Vector2?[] StandardOdd =
    [
        // active group
        new(4.8f, -4.4f),   // right stack
        new(3.3f, -8.4f),   // right cone
        new(-2.9f, -7.3f),  // left stack
        new(-7.7f, -2.6f),  // left chariot
        // passive group
        new(4.1f, -10.2f),  // right cone baiter
        new(1.5f, -2.7f),   // right stack baiter
        new(-1.6f, -8.3f),  // left stack baiter
        new(-1.6f, -8.3f)   // left stack baiter (shares spot)
    ];

    public static readonly Vector2?[] DiamonMarkersEven =
    [
        // active group
        new(4.8f, -2.2f),  // cone1
        new(6.3f, -9.2f),  // chariot1
        new(-4.8f, -2.2f), // cone2
        new(-6.3f, -9.2f), // chariot2
        // passive group
        new(10.5f, 0f),  // cone bait1
        new(3.5f, 4.9f),   // clone bait1
        new(-3.5f, 4.9f),  // clone bait2
        new(-10.5f, 0f)  // cone bait2
    ];

    // --- Reorder behavior ---
    // The strat's per-tower plug point, invoked from TowerPositions with the helper
    // instance + the active 4-role group. p3Z Buddy Meow doesn't reorder (no-op); the
    // first-tower stack swap lives in StackSpots instead.
    public static void KroxyReorder(UmadP2ForsakenP3ZBuddyMeowAiHelper helper, int index, IList<PartyRole> active)
    {
    }
}
