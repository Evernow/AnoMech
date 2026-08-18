using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using static AnoMech.Scenarios.Umad.UmadConstants;

namespace AnoMech.Scenarios.Umad.P3BlackHole;

public sealed class UmadP3BlackHoleAi : IScenarioAi<UmadP3BlackHoleState>
{
    public string Name => "Black Hole (WIP)";

    private UmadP3BlackHoleState state = null!;
    private SimWorld world = null!;

    public void Run(UmadP3BlackHoleState stateParam, SimWorld worldParam)
    {
        state = stateParam;
        world = worldParam;
        assignedHole.Clear();
        var ai = new AiManager(world);

        // Point the tether ordering at each wave's Kefka direction so
        // ScenarioObjects.Tethers below reads already sorted clockwise from
        // it. Lives here (not the scenario) since the AI is this data's only
        // reader -- keeping it self-contained is what lets a peer's local
        // debug-bot replay (see MultiplayerManager) reproduce it by calling
        // this same Run, without needing the scenario's own timeline at all.
        world.Events.Add(25.17f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[0]);
        world.Events.Add(55.70f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[1]);
        world.Events.Add(89.95f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[2]);
        world.Events.Add(123.34f, () => state.ScenarioObjects.TetherSortFrom = state.KefkaPosition[3]);

        ai.Move(7f, StackCentreTanksHoldBossesCentred);
        ai.Move(11f, StackCentre);
        ai.Move(18f, () => DodgeSlap(slapIndex: 0, kefkaIndex: 0), arrivalTime: 24.81f);
        ai.Move(26f, StackCentre);
        world.Events.Add(26.2f, () => GrabTether(tetherIndex: 0, playerIndex: 4));
        world.Events.Add(28.2f, () => PullTether(playerIndex: 4));
        world.Events.Add(32.4f, () => GrabTether(tetherIndex: 0, playerIndex: 4));
        world.Events.Add(32.4f, () => GrabTether(tetherIndex: 1, playerIndex: 0));
        world.Events.Add(34.4f, () => PullTether(playerIndex: 4));
        world.Events.Add(34.4f, () => PullTether(playerIndex: 0));
        ai.GiveInvuln(38f, PartyRole.OffTank);
        ai.Move(40.5f, ResolveFirstThunder);
        ai.Move(46.5f, DodgeEdict);
        ai.Move(50.6f, () => DodgeSlap(slapIndex: 1, kefkaIndex: 1), arrivalTime: 55.26f);
        ai.Move(56f, StackCentre);
        world.Events.Add(58.1f, () => GrabTether(tetherIndex: 0, playerIndex: 4));
        world.Events.Add(58.1f, () => GrabTether(tetherIndex: 1, playerIndex: 0));
        world.Events.Add(58.1f, () => GrabTether(tetherIndex: 2, playerIndex: 3));
        world.Events.Add(60.1f, () => PullTether(playerIndex: 4));
        world.Events.Add(60.1f, () => PullTether(playerIndex: 0));
        world.Events.Add(60.1f, () => PullTether(playerIndex: 3));
        world.Events.Add(64f, () => GrabTether(tetherIndex: 0, playerIndex: 5, intercept: 1f));
        world.Events.Add(66f, () => ReturnToMiddle(playerIndex: 4));
        world.Events.Add(69f, () => GrabTether(tetherIndex: 1, playerIndex: 1, intercept: 1f));
        world.Events.Add(71f, () => ReturnToMiddle(playerIndex: 0));
        ai.Move(74f, DodgeEdictAndLookUpon);
        ai.Move(80f, StackCentre);
        ai.Move(82f, ResolveSecondThunder);
        ai.Move(84.5f, SwapSecondThunderTanks);
        ai.Move(88f, StackCentre);
        world.Events.Add(92.3f, () => GrabTether(tetherIndex: 0, playerIndex: 5));
        world.Events.Add(92.3f, () => GrabTether(tetherIndex: 1, playerIndex: 1));
        world.Events.Add(92.3f, () => GrabTether(tetherIndex: 2, playerIndex: 7));
        world.Events.Add(94.3f, () => PullTether(playerIndex: 5));
        world.Events.Add(94.3f, () => PullTether(playerIndex: 1));
        world.Events.Add(94.3f, () => PullTether(playerIndex: 7));
        world.Events.Add(98f, () => GrabTether(tetherIndex: 0, playerIndex: 6, intercept: 1f));
        world.Events.Add(100f, () => ReturnToMiddle(playerIndex: 5));
        world.Events.Add(103f, () => GrabTether(tetherIndex: 1, playerIndex: 2, intercept: 1f));
        world.Events.Add(105f, () => ReturnToMiddle(playerIndex: 1));
        // playerIndex 7 (puller of tetherIndex 2, never handed off) and 6/2 (the
        // intercept:1f handoff holders for tetherIndex 0/1, taking over from 5/1
        // above) never got a return call here -- unlike 5 and 1, they just sat
        // wherever their tether job left them, holding position for however
        // long that took. That's correct while their hole's Nothingness volleys
        // are still live (RunActiveBlackHole's 3-shot schedule for this wave
        // resolves ~97-107): walking back to centre early would drag a still-
        // dangerous, still-tethered hole's next Nothingness cast (which faces
        // whoever it's tethered to) right onto the rest of the party stacked
        // there. But by ~109.3 the wave's holes and tethers have all auto-
        // despawned (RunActiveBlackHole's own scheduled Despawn calls) regardless
        // of what any player does, so there's nothing left to drag -- and nothing
        // was sending these three back afterward either. Confirmed via
        // AnoMech-DamageDebug dumps: a player left standing wherever their tether
        // job ended (up to ~20y out) had no way to cover that distance in the
        // ~2s DodgeImplosion leaves before Implosion's own unconditionally-lethal
        // Shockwave resolves, and died to it outright. Recalling them once it's
        // actually safe to move, staggered with enough lead time before that
        // window, closes the gap without reintroducing the drag risk.
        world.Events.Add(109.5f, () => ReturnToMiddle(playerIndex: 7));
        world.Events.Add(110.5f, () => ReturnToMiddle(playerIndex: 6));
        world.Events.Add(111.5f, () => ReturnToMiddle(playerIndex: 2));
        world.Events.Add(110f, () => AnchorMtForImplosion(kefkaIndex: 3));
        // arrivalTime values here are UmadP3BlackHoleScenario's own actual resolve
        // times for each hit (Run_Chaos_4000414D's two Shockwave casts at 119.09/
        // 121.11, Run_Kefka_400040E7_1's wave-2 RunSlapCone at 123.25) -- see
        // AiManager.Move's doc comment for what giving it these buys: a role whose
        // required speed exceeds RunSpeed but not SprintSpeed now sprints instead
        // of arriving late. Confirmed via AnoMech-DamageDebug dumps that the wave-2
        // DodgeSlap transition specifically can demand ~9y/s depending on how far
        // DodgeImplosion's (independently computed) landing spot lands a given
        // role's DodgeSlap target from wherever the party actually is.
        ai.Move(117f, () => DodgeImplosion(shockwaveIndex: 0, slapIndex: 2, slapKefkaIndex: 3), jitter: 0f, arrivalTime: 119.09f);
        ai.Move(119.2f, () => DodgeImplosion(shockwaveIndex: 1, slapIndex: 2, slapKefkaIndex: 3), jitter: 0f, arrivalTime: 121.11f);
        ai.Move(121.3f, () => DodgeSlap(slapIndex: 2, kefkaIndex: 3), arrivalTime: 123.25f);
        ai.Move(124f, StackCentre);
        world.Events.Add(125.9f, () => GrabTether(tetherIndex: 0, playerIndex: 6));
        world.Events.Add(125.9f, () => GrabTether(tetherIndex: 1, playerIndex: 2));
        world.Events.Add(127.9f, () => PullTether(playerIndex: 6));
        world.Events.Add(127.9f, () => PullTether(playerIndex: 2));
        world.Events.Add(132f, () => GrabTether(tetherIndex: 0, playerIndex: 2));
        world.Events.Add(134f, () => DodgeLookUponSplit(tetherPlayerIndex: 2, lookKefkaIndex: 4));
        ai.Move(139f, PrepositionForStomp);
        ai.Move(147f, StompBlizzardCorners);
        ai.Move(149.8f, StompStackAndTowers);
        ai.Move(152.4f, StompSwapWest);
        ai.Move(153.7f, StompSwapEast);
    }

    private static IAiMove StackCentreTanksHoldBossesCentred() =>
        AiMove.Create(
            new(6.0f, 0f),
            new(-3.8f, 0f),
            new(0f, 0f),
            new(0f, 0f),
            new(0f, 0f),
            new(0f, 0f),
            new(0f, 0f),
            new(0f, 0f)).NaturalOrder();

    private static IAiMove StackCentre() => AiMove.All(new(0f, 0f));

    // Right's 8 slots are grouped by role (roles 0-1, 2-3, 4-7 = NaturalOrder tanks,
    // healers, dps), not just spread apart -- see UmadP3BlackHoleScenario.RunSlapCone /
    // NextConeTargets: each wave individually targets exactly one tank, one healer, one dps
    // with its own cone, and every OTHER member of that same role shares the target's
    // position on purpose, so a cone aimed at the marked tank also lands on their co-tank
    // (etc.) as an intentional shared soak, not a spread-and-dodge mechanic.
    private IAiMove DodgeSlap(int slapIndex, int kefkaIndex)
    {
        var direction = state.SlapAttacks[slapIndex] == ActionId.SlapHappy_Right
                            ? state.KefkaPosition[kefkaIndex].Flip()
                            : state.KefkaPosition[kefkaIndex];

        IAiMove move = state.SlapAttacks[slapIndex] == ActionId.SlapHappy_Left
                   ? AiMove.All(new(9f, 0f)).ApplyPositions(direction.Apply).ApplyPositions(p => p.Multiply(1, 9f/7f))
                   : (IAiMove)AiMove.Create(
                                        new(7f, 7f), new(9f, 9f),
                                        new(9f, 0f), new(9f, 0f),
                                        new(7f, -7f), new(7f, -7f), new(7f, -7f), new(7f, -7f))
                                    .NaturalOrder()
                                    .ApplyPositions(direction.Apply);
        for (int i = 0; i < 8; i++)
        {
            var member = world.Party.Get(i);
            if (member is null || !member.IsAlive()) continue;
            AnoMech.Core.DiagnosticLog.Info(
                $"[UmadP3BlackHoleAi] DodgeSlap({slapIndex},{kefkaIndex}): role{i} from ({member.Position.X:F1},{member.Position.Z:F1}) -> target {move[i]}.");
        }
        return move;
    }

    // First Lightning III from Exdeath: a stack-radius tank buster snapshotted twice,
    // 3s apart, onto whoever is closest to Exdeath. The OT eats both by standing in the
    // middle of Exdeath's hitbox (so it stays the closest); everyone else clears the
    // blast by sliding straight out from Exdeath's centre along their own bearing.
    private const float ThunderBusterRadius = 8f;     // blast radius non-OTs must clear
    private const float ThunderClearDistance = 10f;   // where they park, just past it

    private IAiMove ResolveFirstThunder()
    {
        var exdeath = state.ScenarioObjects.Exdeath;
        if (exdeath is null) return AiMove.Create().NaturalOrder();

        var centre = new Vector2(exdeath.Position.X, exdeath.Position.Z);
        var coords = new Vector2?[8];
        for (int i = 0; i < 8; i++)
        {
            var member = world.Party.Get(i);
            if (member is null || !member.IsAlive()) continue;

            if (i == (int)PartyRole.OffTank)
            {
                coords[i] = centre;
                continue;
            }

            var away = new Vector2(member.Position.X, member.Position.Z) - centre;
            if (away.LengthSquared() >= ThunderBusterRadius * ThunderBusterRadius) continue;
            var dir = away.LengthSquared() > 1e-4f
                          ? Vector2.Normalize(away)
                          : new Vector2(MathF.Sin(i * MathF.Tau / 8f), MathF.Cos(i * MathF.Tau / 8f));
            coords[i] = centre + dir * ThunderClearDistance;
        }
        return AiMove.Create(coords).NaturalOrder();
    }

    // Second Lightning III from Exdeath: a two-hit tank-swap buster. The OT eats the first
    // hit in the middle of Exdeath's hitbox (closest, so it's the one snapshotted); the MT
    // waits exactly 8y out, perpendicular to the stack axis so it's clear of both the blast
    // and the party. Everyone else stacks at centre, shoved straight away from Exdeath (along
    // the Exdeath->centre axis) far enough to clear the blast when Exdeath sits near middle.
    private IAiMove ResolveSecondThunder()
    {
        var exdeath = state.ScenarioObjects.Exdeath;
        if (exdeath is null) return AiMove.Create().NaturalOrder();

        var e = new Vector2(exdeath.Position.X, exdeath.Position.Z);
        var away = e.LengthSquared() > 1e-4f ? Vector2.Normalize(-e) : new Vector2(0f, -1f);
        var stack = e.Length() >= ThunderBusterRadius ? Vector2.Zero : e + away * ThunderClearDistance;
        var mtSeat = e + RotateVec(away, MathF.PI / 2f) * ThunderBusterRadius;

        var coords = new Vector2?[8];
        for (int i = 0; i < 8; i++)
        {
            if (world.Party.Get(i) is not { } m || !m.IsAlive()) continue;
            coords[i] = i switch
            {
                (int)PartyRole.OffTank  => e,
                (int)PartyRole.MainTank => mtSeat,
                _                       => stack,
            };
        }
        return AiMove.Create(coords).NaturalOrder();
    }

    // After the first hit the tanks trade spots, so the MT slides into the middle and is the
    // closest player for the second hit while the OT takes over the 8y seat. Reads live
    // positions (Exdeath is frozen across both hits) so it's a literal swap of where they
    // each stand.
    private IAiMove SwapSecondThunderTanks()
    {
        var mt = world.Party.Get(PartyRole.MainTank);
        var ot = world.Party.Get(PartyRole.OffTank);
        if (mt is null || ot is null) return AiMove.Create().NaturalOrder();

        var coords = new Vector2?[8];
        coords[(int)PartyRole.OffTank]  = new Vector2(mt.Position.X, mt.Position.Z);
        coords[(int)PartyRole.MainTank] = new Vector2(ot.Position.X, ot.Position.Z);
        return AiMove.Create(coords).NaturalOrder();
    }

    // Dodge the edict by tucking just behind the casting boss, but as close to arena
    // center as the rect allows so the follow-up slap dodge is a short hop. The edict
    // is a forward rect, so the whole half-plane behind the boss is safe; take the
    // point in it nearest center.
    private IAiMove DodgeEdict()
    {
        var boss = state.ScenarioObjects.Chaos;
        if (boss is null) return StackCentre();

        var bossPos = new Vector2(boss.Position.X, boss.Position.Z);
        var fwd = new Vector2(MathF.Sin(boss.Rotation), MathF.Cos(boss.Rotation));
        const float margin = 3f;   // clearance behind the boss (the rect's back edge)

        // Nearest point to center in the safe half-plane (P - bossPos)·fwd <= -margin.
        var s = margin - Vector2.Dot(bossPos, fwd);
        var target = s > 0f ? -fwd * s : Vector2.Zero;
        AnoMech.Core.DiagnosticLog.Info(
            $"[UmadP3BlackHoleAi] DodgeEdict: boss at ({bossPos.X:F1},{bossPos.Y:F1}) rot={boss.Rotation:F3} -> target ({target.X:F1},{target.Y:F1}).");
        return AiMove.All(target);
    }

    // Damning Edict (rect 60x80 projected from the boss) and Look Upon Me and Despair
    // (rect 100x16 along the KefkaPosition[2] axis through center) snapshot only ~1.4s
    // apart — too tight to dodge in two hops. Hold one spot safe from both: behind the
    // edict boss and clear of the Look-Upon corridor.
    private IAiMove DodgeEdictAndLookUpon()
    {
        var theta = state.KefkaPosition[2].RadiansFromNorth;
        var axis = new Vector2(-MathF.Sin(theta), MathF.Cos(theta));     // Look-Upon line direction
        var lookRight = new Vector2(MathF.Cos(theta), MathF.Sin(theta)); // perpendicular to it
        const float lookSafe = 11f;   // past the 8y half-width, with margin
        const float behind = 8f;      // distance to stand behind the boss

        var boss = state.ScenarioObjects.Chaos;
        if (boss is null) return AiMove.All(lookRight * lookSafe);

        var bossPos = new Vector2(boss.Position.X, boss.Position.Z);
        var fwd = new Vector2(MathF.Sin(boss.Rotation), MathF.Cos(boss.Rotation));
        var denom = Vector2.Dot(axis, fwd);

        // A corridor edge offset by sign*lookSafe (always clear of Look-Upon), slid
        // along the line to sit `behind` the boss. Falls back to the edge midpoint when
        // the boss faces across the line (sliding can't change how far behind it sits).
        Vector2 Seat(float sign)
        {
            var edge = lookRight * (sign * lookSafe);
            if (MathF.Abs(denom) < 0.1f) return edge;
            var t = (-behind - Vector2.Dot(edge - bossPos, fwd)) / denom;
            return edge + axis * t;
        }

        var a = Seat(1f);
        var b = Seat(-1f);
        float Fwd(Vector2 p) => Vector2.Dot(p - bossPos, fwd);   // < 0 = behind the boss
        bool aOk = Fwd(a) < -1f, bOk = Fwd(b) < -1f;
        if (aOk == bOk) return AiMove.All(a.LengthSquared() <= b.LengthSquared() ? a : b);
        return AiMove.All(aOk ? a : b);
    }

    // Implosion fires two +-45deg Shockwave cones from Chaos, the axis rotating 90deg between
    // the two shockwaves, so the only bearings safe from both are the four diagonals at 45deg
    // to the cone axis. Of those, take the diagonal whose arena-border end is deepest into the
    // slap-safe half (farthest from the slap-dangerous edge). Resolve where that diagonal crosses
    // the line parallel to the slap divide that sits 10y into the safe half; if the diagonal
    // never reaches that line inside the arena, hug the border (1y in) instead. Then nudge each
    // shockwave a few degrees off the diagonal toward the perpendicular of its own cone so neither
    // dodge stands on the (hit-counting) cone edge. Read Chaos live, like the edict.
    private const float ArenaRadius = 20f;             // ~outer Black Hole ring (z=-17), tune in-game
    private const float ImplosionSlapSafeMargin = 10f; // park this far past the slap divide, into the safe half
    private const float ImplosionBorderInset = 1f;     // fallback: this far inside the arena border
    private const float ImplosionConeLean = 0.18f;     // ~10deg off the cone edge

    private IAiMove DodgeImplosion(int shockwaveIndex, int slapIndex, int slapKefkaIndex)
    {
        var boss = state.ScenarioObjects.Chaos;
        if (boss is null) return StackCentre();

        var c = new Vector2(boss.Position.X, boss.Position.Z);
        var offset = state.ImplosionAttack == ActionId.LongitudinalImplosion ? 0f : MathF.PI / 2f;
        var baseAxis = boss.Rotation + offset;

        // Slap divide runs through arena center perpendicular to safeDir; dot(P, safeDir) is the
        // signed distance into the slap-safe half. Danger edge is the arena rim on the unsafe side.
        var safeDir = SlapSafeDir(slapIndex, slapKefkaIndex);
        var dangerEdge = -safeDir * ArenaRadius;

        // Of the four 45deg diagonals, keep the one whose outward arena-border point is farthest
        // from that danger edge - deepest into the slap-safe half, robust to Chaos off-center.
        var bestDir = Vector2.Zero;
        var bestBorderDist = 0f;
        var bestDist = float.MinValue;
        for (var k = 0; k < 4; k++)
        {
            var theta = baseAxis + MathF.PI / 4f + k * (MathF.PI / 2f);
            var dir = new Vector2(MathF.Sin(theta), MathF.Cos(theta));
            var borderDist = RayToArenaBorder(c, dir);
            var dist = Vector2.DistanceSquared(c + dir * borderDist, dangerEdge);
            if (dist > bestDist) { bestDist = dist; bestDir = dir; bestBorderDist = borderDist; }
        }

        // Where the diagonal crosses the safe-side parallel line (dot(P, safeDir) == margin):
        // c + t*bestDir lies on it at t = (margin - dot(c, safeDir)) / dot(bestDir, safeDir).
        // If that crossing is ahead of Chaos and inside the arena, dodge around it; otherwise
        // the diagonal leaves the arena before reaching the line, so hug the border instead.
        var denom = Vector2.Dot(bestDir, safeDir);
        var length = bestBorderDist - ImplosionBorderInset;
        if (denom > 1e-4f)
        {
            var t = (ImplosionSlapSafeMargin - Vector2.Dot(c, safeDir)) / denom;
            if (t > 0f && t <= bestBorderDist) length = t;
        }

        // Lean off the diagonal toward the perpendicular of THIS shockwave's cone axis, so we
        // sit just inside the 90deg safe band instead of on its edge.
        var coneAxisAngle = baseAxis + shockwaveIndex * (MathF.PI / 2f);
        var coneAxis = new Vector2(MathF.Sin(coneAxisAngle), MathF.Cos(coneAxisAngle));
        var perp = RotateVec(coneAxis, MathF.PI / 2f);
        if (Vector2.Dot(perp, bestDir) < 0f) perp = -perp;
        var lean = MathF.Sign(bestDir.X * perp.Y - bestDir.Y * perp.X) * ImplosionConeLean;

        return AiMove.All(c + RotateVec(bestDir, lean) * length);
    }

    // Distance from `from` (inside the arena) along unit `dir` to the arena-circle border.
    private static float RayToArenaBorder(Vector2 from, Vector2 dir)
    {
        var proj = Vector2.Dot(from, dir);
        return -proj + MathF.Sqrt(proj * proj + ArenaRadius * ArenaRadius - from.LengthSquared());
    }

    // Rotate an XZ vector by `radians` (CCW in the X->Z plane).
    private static Vector2 RotateVec(Vector2 v, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    // Unit vector toward the safe side of a Slap Happy (the side away from the r=13
    // circles, which sit at +-10 along the KefkaPosition axis on the `mul` side).
    private Vector2 SlapSafeDir(int slapIndex, int kefkaIndex)
    {
        var phi = state.KefkaPosition[kefkaIndex].RadiansFromNorth;
        var slapX = new Vector2(MathF.Cos(phi), MathF.Sin(phi));
        var mul = state.SlapAttacks[slapIndex] == ActionId.SlapHappy_Left ? -1f : 1f;
        return slapX * -mul;
    }

    // Park the MT 45 degrees clockwise from Kefka's facing (Kefka sits centre, oriented
    // to KefkaPosition[kefkaIndex]) so Chaos, which the MT holds, is dragged to that known
    // angle before the implosion cast locks in its cone axis. MT looks at Chaos on arrival.
    private void AnchorMtForImplosion(int kefkaIndex)
    {
        var mt = world.Party.Get(PartyRole.MainTank);
        if (mt is null) return;
        var spot = state.KefkaPosition[kefkaIndex].Rotate(1).Apply(new Vector3(0f, 0f, -5f));
        mt.MoveTo(spot);
    }

    private void ReturnToMiddle(int playerIndex) =>
        state.Roles.Get(playerIndex)?.MoveTo(new Vector3(0f, 0f, 0f));

    // Look Upon (rect along the KefkaPosition[lookKefkaIndex] axis through centre, 16y
    // wide) split. The tether holder rides the last black hole's tether out to the arena
    // edge, but the hole's bearing may sit in the Look-Upon corridor; nudge it ±45° to the
    // side that clears the line and send the holder there. The rest take the opposite edge
    // (180°), which the centre-symmetric corridor leaves equally clear.
    private void DodgeLookUponSplit(int tetherPlayerIndex, int lookKefkaIndex)
    {
        var theta = state.KefkaPosition[lookKefkaIndex].RadiansFromNorth;   // Look-Upon line bearing

        // Bearing centre→black hole the holder is tethered to. Falls back to the line's
        // perpendicular (always clear) if no active tether is readable.
        var holder = state.Roles.Get(tetherPlayerIndex);
        var blackHole = (TetherHeldBy(holder) ?? state.ScenarioObjects.Tethers.FirstOrDefault())?.A;
        var alpha = blackHole is { } bh
                        ? MathF.Atan2(bh.Position.X, -bh.Position.Z)
                        : theta + MathF.PI / 2f;

        // Of alpha±45°, at least one clears the 8y half-width corridor (edge perpendicular
        // = edge·|sin(β-θ)|, ≥ 12.7y even with the hole on the line). Take the side farther
        // from the line — the larger |sin(β-θ)| — which is always the safe one.
        var plus = alpha + MathF.PI / 4f;
        var minus = alpha - MathF.PI / 4f;
        var beta = MathF.Abs(MathF.Sin(plus - theta)) >= MathF.Abs(MathF.Sin(minus - theta)) ? plus : minus;

        const float edge = 18f;   // ride out to the arena edge, past the hole's r=17 ring
        var holderSpot = new Vector3(edge * MathF.Sin(beta), 0f, -edge * MathF.Cos(beta));
        for (int i = 0; i < 8; i++)
            state.Roles.Get(i)?.MoveTo(i == tetherPlayerIndex ? holderSpot : -holderSpot);
    }

    // playerIndex -> the specific black hole (tether.A) that player's most recent
    // GrabTether call sent them toward. Needed because PassableEnd (the tether's dynamic
    // holder resolution, see TetherEnd.cs) seeds an UNRELATED hole's tether with a random
    // alive party member the instant it's created -- so a player can find themselves
    // briefly holding a hole nobody sent them to, purely by bad luck, well before their
    // own Intercept walk toward their ASSIGNED hole ever lands. PullTether must pull based
    // on this recorded assignment, not "whatever tether the player happens to currently
    // hold" -- see PullTether for what goes wrong otherwise.
    //
    // Deliberately the HOLE, not the SimTether object itself: on a peer, MultiplayerManager
    // .OnWorldSnapshotReceived recreates its local SimTether (new instance) whenever that
    // tether's endpoint changes over the network -- a cached SimTether reference goes stale
    // the moment the real grab actually lands, permanently failing the match below and
    // stranding the player with no MoveTo ever issued. The hole itself (a SimEnemy) is never
    // recreated that way, only position-updated in place, so re-resolving "today's tether for
    // this hole" from ScenarioObjects.Tethers each time stays correct on both host and peer.
    private readonly Dictionary<int, SimCharacter?> assignedHole = new();

    private void GrabTether(int tetherIndex, int playerIndex, float intercept = 3f)
    {
        var player = state.Roles.Get(playerIndex);
        var tether = state.ScenarioObjects.Tethers.ElementAtOrDefault(tetherIndex);
        assignedHole[playerIndex] = tether?.A;
        var role = (player as ISimPartyMember)?.Role.ToString() ?? $"player#{playerIndex}";
        if (tether is null)
        {
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] GrabTether: tetherIndex {tetherIndex} not found in ScenarioObjects.Tethers ({state.ScenarioObjects.Tethers.Count} known) -- {role} sent nowhere.");
            return;
        }
        var bh = tether.A is { } a ? new Vector2(a.Position.X, a.Position.Z) : Vector2.Zero;
        var from = player is null ? "(no player)" : $"({player.Position.X:F1},{player.Position.Z:F1})";
        AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] GrabTether: {role} intercepting tetherIndex {tetherIndex} (black hole at ({bh.X:F1},{bh.Y:F1})) from {from}, margin={intercept}.");
        player?.Intercept(tether, intercept);
    }

    // Grab and Pull are scheduled as two separate fixed-time events, but Grab only
    // *starts* the walk to the tether (Intercept) -- how long that walk actually
    // takes depends on live distance (a black hole can spawn ~17y out), which the
    // schedule has no way to know in advance. A Pull that lands before the walk
    // finishes used to just silently no-op, permanently stranding that player
    // wherever they'd gotten to (often still near the party's own stack point) for
    // the rest of the wave -- exactly the wrong place once the hole starts firing
    // AT whoever it's tethered to. Retrying a few times, a second apart, covers the
    // gap between "scheduled" and "actually arrived" without needing each wave's
    // call sites to hand-tune a delay against a distance they don't know either.
    private const int PullTetherMaxRetries = 4;

    private void PullTether(int playerIndex, int retriesLeft = PullTetherMaxRetries)
    {
        var player = state.Roles.Get(playerIndex);
        var role = (player as ISimPartyMember)?.Role.ToString() ?? $"player#{playerIndex}";
        // Must be holding the tether for the SPECIFIC hole the matching GrabTether sent
        // them toward -- not just any tether TetherHeldBy would find. A random PassableEnd
        // seed can hand this player an unrelated hole's tether before their real walk
        // lands; pulling on THAT would drag them away from (and, via Movement's
        // stale-Intercept cancel, abandon) the hole they were actually assigned, instead of
        // just waiting for the legitimate one to resolve the same way the "still
        // mid-Intercept" case already does. Re-resolved fresh each call (see assignedHole)
        // rather than cached, since a peer's local tether object for that hole can be a
        // brand new instance since the last time this ran.
        var hole = assignedHole.GetValueOrDefault(playerIndex);
        var assigned = hole is null ? null : state.ScenarioObjects.Tethers.FirstOrDefault(t => ReferenceEquals(t.A, hole));
        if (assigned is not { A: { } blackHole, B: { } held } || !ReferenceEquals(held, player))
        {
            AnoMech.Core.DiagnosticLog.Warn($"[UmadP3BlackHoleAi] PullTether: {role} does not hold their assigned tether yet -- pull skipped (still mid-Intercept, grab never happened, or a different hole's random seed briefly gave them someone else's). Current pos ({player?.Position.X:F1},{player?.Position.Z:F1}). Retries left: {retriesLeft}.");
            if (retriesLeft > 0)
                world.Events.Add(1f, () => PullTether(playerIndex, retriesLeft - 1));
            return;
        }
        var bhPos = new Vector2(blackHole.Position.X, blackHole.Position.Z);
        var heldPos = new Vector2(held.Position.X, held.Position.Z);
        // Pull spot, then nudged 1.5y farther from the black hole along the bh→player axis.
        var rawSpot = CardinalClockwise(bhPos) + Vector2.Normalize(heldPos - bhPos) * 1.5f;
        // CardinalClockwise only reasons about the ACTIVE hole's own bearing -- it has no
        // idea where this wave's passive/decorative black holes (also in world.Obstacles,
        // see Run_BlackHoleObstacleWave) sit, and TetherPullRadius (14y) happens to land
        // very close to their own ~13.5y radius from centre. A raw spot that coincides with
        // one doesn't just clip the avoid radius -- Movement's per-tick ClampOutside keeps
        // reprojecting to that same boundary while Steer's tangent-glide fights to actually
        // arrive there, so the bot can stall for several real seconds standing on top of it.
        // Push the spot out to a clearance clearly past that boundary up front instead.
        var spot = world.Obstacles.ClampOutside(rawSpot, margin: 2f);
        AnoMech.Core.DiagnosticLog.Info($"[UmadP3BlackHoleAi] PullTether: {role} held at ({heldPos.X:F1},{heldPos.Y:F1}), black hole at ({bhPos.X:F1},{bhPos.Y:F1}) -- moving to ({spot.X:F1},{spot.Y:F1}) (dist {Vector2.Distance(heldPos, spot):F1}y){(spot != rawSpot ? $" [nudged from ({rawSpot.X:F1},{rawSpot.Y:F1}) to clear an obstacle]" : "")}.");
        player?.MoveTo(new Vector3(spot.X, 0f, spot.Y));
    }

    private SimTether? TetherHeldBy(SimCharacter? player) =>
        player is null
            ? null
            : state.ScenarioObjects.Tethers.FirstOrDefault(t => ReferenceEquals(t.B, player));

    // Non-tether players sit at arena centre for the whole of a Nothingness wave (no
    // move away from StackCentre until well after the last shot) -- a pulled black
    // hole needs to clear Nothingness's own hit radius from THAT point, not just from
    // wherever the holder parks. 8y (the old radius) repeatedly still tagged the
    // center-stacked group; 14y gives real separation while staying well inside
    // ArenaRadius (20y).
    private const float TetherPullRadius = 14f;

    private static Vector2 CardinalClockwise(Vector2 cardinal)
    {
        var dir = Vector2.Normalize(cardinal);
        var c = MathF.Cos(MathF.PI / 3f);
        var s = MathF.Sin(MathF.PI / 3f);
        return new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c) * TetherPullRadius;
    }

    // Stomp a Mole resolves in Kefka's spawn frame (KefkaPosition[4]): the two towers
    // sit ±10 along Kefka's east-west, the spreads/stacks along its north-south. All the
    // formations below are authored in that frame and rotated onto it. Roles split into a
    // support group (0-3) and a DPS group (4-7); within each, even indices are the "west"
    // pair and odd indices the "east" pair, so a role's side is consistent from the
    // preposition through the corner spread to the towers.
    private static readonly int[] SupportGroup = [0, 1, 2, 3];
    private static readonly int[] DpsGroup = [4, 5, 6, 7];
    private const float StompTowerX = 10f;        // tower offset along Kefka's east-west axis
    private const float StompTowerPairGap = 1.5f; // the two soakers straddle the tower centre

    // Whichever group owns the first stack marker (StackTargets[0], always one support +
    // one DPS) stacks first; the other group takes the towers first, then they trade.
    private bool SupportsStackFirst() => !state.StackTargets[0].IsDps();

    // Supports gather north of centre, DPS south, ready for the first spread.
    private IAiMove PrepositionForStomp() =>
        AiMove.Create(new(0, -5), new (0, -5), new (0, -5), new (0, -5),
                    new (0, 5), new(0, 5), new (0, 5), new (0, 5))
              .NaturalOrder()
              .ApplyPositions(state.KefkaPosition[4].Apply);

    // First BlizzardIII spreads drop on the prepositioned spots; step out to the four
    // intercardinal corners two-per-corner (supports north, DPS south, west pair vs east).
    private IAiMove StompBlizzardCorners() =>
        AiMove.Create(
                  new(-8f, -10f), new(8f, -10f), new(-10f, -8f), new(10f, -8f),
                  new(-8f, 10f), new(8f, 10f), new(-10f, 8f), new(10f, 8f))
              .NaturalOrder()
              .ApplyPositions(state.KefkaPosition[4].Apply);

    // Second spread has dropped on the corners: the stacking group collapses to centre
    // for the first knockback stack, the other group pairs onto the two towers.
    private IAiMove StompStackAndTowers()
    {
        var coords = new Vector2?[8];
        foreach (var r in SupportsStackFirst() ? SupportGroup : DpsGroup) coords[r] = Vector2.Zero;
        PlaceOnTowers(coords, SupportsStackFirst() ? DpsGroup : SupportGroup);
        return AiMove.Create(coords).NaturalOrder().ApplyPositions(state.KefkaPosition[4].Apply);
    }

    // The swap is staggered by side because the west tower resolves ~1.3s before the east:
    // the west pairs trade the moment the first stack and the west tower are done, the east
    // pairs only once the east tower has resolved (StompSwapEast). Each half sends the old
    // tower pair back to centre for the second stack and the old stack pair out to its tower.
    private IAiMove StompSwapWest() => StompSwapHalf(0, 2, -StompTowerX);
    private IAiMove StompSwapEast() => StompSwapHalf(1, 3, StompTowerX);

    // pairA/pairB are the two group-array indices making up this side's pair; towerX is
    // the tower they trade onto/off of.
    private IAiMove StompSwapHalf(int pairA, int pairB, float towerX)
    {
        var newTower = SupportsStackFirst() ? SupportGroup : DpsGroup; // old stackers now soak
        var newStack = SupportsStackFirst() ? DpsGroup : SupportGroup; // old soakers now stack
        var coords = new Vector2?[8];
        coords[newTower[pairA]] = new Vector2(towerX, -StompTowerPairGap);
        coords[newTower[pairB]] = new Vector2(towerX, StompTowerPairGap);
        coords[newStack[pairA]] = Vector2.Zero;
        coords[newStack[pairB]] = Vector2.Zero;
        return AiMove.Create(coords).NaturalOrder().ApplyPositions(state.KefkaPosition[4].Apply);
    }

    private static void PlaceOnTowers(Vector2?[] coords, int[] group)
    {
        coords[group[0]] = new Vector2(-StompTowerX, -StompTowerPairGap);
        coords[group[2]] = new Vector2(-StompTowerX, StompTowerPairGap);
        coords[group[1]] = new Vector2(StompTowerX, -StompTowerPairGap);
        coords[group[3]] = new Vector2(StompTowerX, StompTowerPairGap);
    }
}
