using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P5Omega;

public sealed record MonitorSide(int Mul, uint ActionId)
{
    public static readonly MonitorSide Left = new(1, TopConstants.ActionId.OversampledWaveCannonLeft);
    public static readonly MonitorSide Right = new(-1, TopConstants.ActionId.OversampledWaveCannonRight);
}

public sealed class TopP5OmegaState
{
    private readonly Rng rng = new();
    
    public RoleList HelloWorldTargets { get; }
    public RoleList DoubleDynamicTargets { get; }
    // Was resolved live inside TopP5OmegaAi (a per-client rng.Next + Shuffle) -- a peer's own
    // replay independently rerolled it, so which two roles landed on the fixed monitor-soak
    // coordinates could disagree with the host's real bots. Moved here since it only depends
    // on the two RoleLists above (already resolved by this point), so it broadcasts for free
    // through the existing replay-state message instead of needing one of its own.
    public RoleList MonitorTargets { get; }

    public IReadOnlyList<Direction> AttackDirections { get; }
    public IReadOnlyList<OmegaAttack> OmegaAttacks { get; } 
    public Direction BettleSpawnDirection { get; }
    public bool FirstWaveCannonFront { get; }

    public MonitorSide MonitorSide { get; }

    public TopP5OmegaState(SimParty party, TopP5OmegaStateOverrides overrides)
    {
        var firstAttackDirection = rng.NextIntercardinal();
        var secondAttackDirection = firstAttackDirection.Rotate(rng.NextSign() * 2);
        AttackDirections = [firstAttackDirection, firstAttackDirection.Flip(), secondAttackDirection, secondAttackDirection.Flip()];
        HelloWorldTargets = new RoleListBuilder
        {
            Size = 4,
            ForcePlayerIndex = (overrides.HelloWorldOrder, overrides.HelloWorldType) switch
            {
                (HelloWorldOrderOption.Auto,   HelloWorldTypeOption.Near) => [0, 2],
                (HelloWorldOrderOption.Auto,   HelloWorldTypeOption.Far)  => [1, 3],
                (HelloWorldOrderOption.Any,    HelloWorldTypeOption.Auto) => [0, 1, 2, 3],
                (HelloWorldOrderOption.Any,    HelloWorldTypeOption.Near) => [0, 2],
                (HelloWorldOrderOption.Any,    HelloWorldTypeOption.Far)  => [1, 3],
                (HelloWorldOrderOption.First,  HelloWorldTypeOption.Auto) => [0, 1],
                (HelloWorldOrderOption.First,  HelloWorldTypeOption.Near) => [0],
                (HelloWorldOrderOption.First,  HelloWorldTypeOption.Far)  => [1],
                (HelloWorldOrderOption.Second, HelloWorldTypeOption.Auto) => [2, 3],
                (HelloWorldOrderOption.Second, HelloWorldTypeOption.Near) => [2],
                (HelloWorldOrderOption.Second, HelloWorldTypeOption.Far)  => [3],
                _ => [],
            },
            IncludePlayer = overrides.HelloWorldOrder == HelloWorldOrderOption.None ? false : null,
        }.Build(party);
        DoubleDynamicTargets = new RoleListBuilder
        {
            Size = 4,
            IncludePlayer = overrides.ExtraDynamis,
        }.Build(party);
        MonitorTargets = new RoleList(party, ResolveMonitorTargets());
        BettleSpawnDirection = overrides.BettleSpawnDirection ?? rng.NextCardinal();
        MonitorSide = overrides.MonitorSide ?? rng.NextObj(MonitorSide.Left, MonitorSide.Right);
        FirstWaveCannonFront = overrides.FirstWaveCannonFront ?? rng.NextBool();
        var firstFAttack = overrides.FirstFAttack ?? RandomFAttack();
        var firstMAttack = overrides.FirstMAttack ?? RandomMAttack();
        OmegaAttack secondFAttack;
        OmegaAttack secondMAttack;
        while (true)
        {
            secondFAttack = overrides.SecondFAttack ?? RandomFAttack();
            secondMAttack = overrides.SecondMAttack ?? RandomMAttack();
            if ((firstFAttack, firstMAttack) != (secondFAttack, secondMAttack)) break;
            // Both seconds user-set to match firsts: trust the user.
            if (overrides is { SecondFAttack: not null, SecondMAttack: not null }) break;
        }
        OmegaAttacks = [firstFAttack, firstMAttack, secondFAttack, secondMAttack];
    }

    private OmegaAttack RandomFAttack() => rng.NextObj(OmegaAttack.Legs, OmegaAttack.Staff);
    private OmegaAttack RandomMAttack() => rng.NextObj(OmegaAttack.Shield, OmegaAttack.Sword);

    private List<PartyRole> ResolveMonitorTargets()
    {
        List<PartyRole> mustTakeMonitor = [];
        List<PartyRole> canTakeMonitor = [];
        foreach (var role in Enum.GetValues<PartyRole>())
        {
            if (HelloWorldTargets[0] == role || HelloWorldTargets[1] == role) continue;
            if (!DoubleDynamicTargets.Contains(role)) continue;
            if (HelloWorldTargets[2] == role || HelloWorldTargets[3] == role)
                mustTakeMonitor.Add(role);
            else
                canTakeMonitor.Add(role);
        }
        while (mustTakeMonitor.Count < 2)
        {
            var selected = canTakeMonitor[rng.NextInt(canTakeMonitor.Count)];
            canTakeMonitor.Remove(selected);
            mustTakeMonitor.Add(selected);
        }
        return rng.Shuffle(mustTakeMonitor.ToArray()).ToList();
    }

    // Network-replay constructor: reconstructs only the fields TopP5OmegaAi reads --
    // HelloWorldTargets, DoubleDynamicTargets, MonitorTargets, OmegaAttacks, AttackDirections,
    // FirstWaveCannonFront, BettleSpawnDirection, MonitorSide. OmegaAttack has no delegate
    // field (unlike GlitchType), so it round-trips through JSON directly; MonitorSide is
    // carried as a bool identifying which of its two named static instances was chosen.
    // MonitorTargets is carried as-is (already resolved by the host) rather than re-derived --
    // see its own doc comment.
    private TopP5OmegaState(
        SimParty party, PartyRole[] helloWorldTargets, PartyRole[] doubleDynamicTargets, PartyRole[] monitorTargets,
        float[] attackDirectionsRadians, OmegaAttack[] omegaAttacks, float bettleSpawnDirectionRadians,
        bool firstWaveCannonFront, bool monitorIsLeft)
    {
        HelloWorldTargets = new RoleList(party, helloWorldTargets);
        DoubleDynamicTargets = new RoleList(party, doubleDynamicTargets);
        MonitorTargets = new RoleList(party, monitorTargets);
        AttackDirections = attackDirectionsRadians.Select(r => new Direction(r)).ToList();
        OmegaAttacks = omegaAttacks;
        BettleSpawnDirection = new Direction(bettleSpawnDirectionRadians);
        FirstWaveCannonFront = firstWaveCannonFront;
        MonitorSide = monitorIsLeft ? MonitorSide.Left : MonitorSide.Right;
    }

    public static TopP5OmegaState FromNetworkReplay(
        SimParty party, PartyRole[] helloWorldTargets, PartyRole[] doubleDynamicTargets, PartyRole[] monitorTargets,
        float[] attackDirectionsRadians, OmegaAttack[] omegaAttacks, float bettleSpawnDirectionRadians,
        bool firstWaveCannonFront, bool monitorIsLeft)
        => new(party, helloWorldTargets, doubleDynamicTargets, monitorTargets, attackDirectionsRadians, omegaAttacks,
               bettleSpawnDirectionRadians, firstWaveCannonFront, monitorIsLeft);

    // Resolved live at t=46s (a status-stack read + shuffle) -- not knowable at run start, and
    // re-rolling it independently on a peer's own replay could diverge from the host's real
    // assignment. Broadcast via TopP5OmegaHelloWorld2UpdateMessage once resolved, same pattern
    // as TopP5DeltaState.BeyondDefenseTarget.
    public PartyRole[]? HelloWorld2;
}
