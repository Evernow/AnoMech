using System;
using System.Collections.Generic;
using System.Numerics;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Core.Game.Ai;

// Drives slot-ordered party movement from a scenario's position functions.
// Owns jitter, run speed, and event scheduling. Position functions return an
// AiMove whose entries are scenario-local XZ coords — same space MoveTo
// consumes, so AiManager forwards them as-is. Eye-spawn flip and slot
// reordering are handled inside the AiMove before it reaches here.
public sealed class AiManager
{
    // Measured in-game.
    private const float RunSpeed = 6.5f;
    private const float SprintSpeed = 8.3f;
    // Mirrors LocalPlayerInputHooks' real-Sprint values so a bot's simulated
    // sprint status looks identical to a real player pressing the real button --
    // that class can't be reused directly (it's scoped to hooking the local
    // player's own keypress, a different concern), so these are duplicated here
    // deliberately rather than reached into.
    private const ushort SprintStatusId = 50;
    private const int SprintStatusParam = 30;
    private const float DefaultJitter = 0.3f;

    private readonly SimWorld world;
    private readonly Random rng = new();

    public AiManager(SimWorld world)
    {
        this.world = world;
    }

    // Schedule a slot-move at `time`; null AiMove entries are skipped.
    // `arrivalTime` set: freeze until the last safe moment, then walk/sprint to
    // land exactly on it. Unset: go now, no sprint consideration. `sprint`
    // (only meaningful without `arrivalTime`) forces SprintSpeed and sizes the
    // Sprint status off distance instead of a deadline.
    //
    // Every MoveTo except the deferred freeze-then-walk path fires via
    // PromptMoveDelay instead of the same EventScheduler.Tick() pass that
    // scheduled it: Add() computes a new entry's time as elapsed + offset, so
    // a 0-delay entry added while that same Tick() is still iterating gets
    // swept into its own while loop and runs immediately, same frame. A
    // positive delay guarantees it lands on a later tick instead.
    private const float PromptMoveDelay = 0.3f;

    public void Move(float time, Func<IAiMove> positions, float jitter = DefaultJitter, float? arrivalTime = null, bool sprint = false)
    {
        world.Events.Add(time, () =>
        {
            var move = positions();
            for (int i = 0; i < 8; i++)
            {
                if (move[i] is not { } local) continue;
                var member = world.Party.Get(i);
                if (member == null || !member.IsAlive()) continue;
                var target = Jitter(new Vector3(local.X, 0f, local.Y), jitter);
                var role = (member as ISimPartyMember)?.Role.ToString() ?? $"slot{i}";
                var dx = target.X - member.Position.X;
                var dz = target.Z - member.Position.Z;
                var dist = MathF.Sqrt(dx * dx + dz * dz);

                if (arrivalTime is not { } deadline)
                {
                    if (sprint)
                    {
                        member.AddStatus(SprintStatusId, dist / SprintSpeed, SprintStatusParam);
                        AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) sprinting -- {dist:F1}y.");
                        world.Events.Add(PromptMoveDelay, () => member.MoveTo(target, speed: SprintSpeed));
                    }
                    else
                    {
                        AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}).");
                        world.Events.Add(PromptMoveDelay, () => member.MoveTo(target, speed: RunSpeed));
                    }
                    continue;
                }

                var available = deadline - time;
                var neededSpeed = available > 0f ? dist / available : float.PositiveInfinity;

                if (neededSpeed > RunSpeed && neededSpeed <= SprintSpeed)
                {
                    member.AddStatus(SprintStatusId, available, SprintStatusParam);
                    AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) sprinting -- {dist:F1}y in {available:F2}s needs {neededSpeed:F2}y/s.");
                    world.Events.Add(PromptMoveDelay, () => member.MoveTo(target, speed: SprintSpeed));
                    continue;
                }

                var delay = available - dist / RunSpeed;
                if (delay > 0f)
                {
                    AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) deferred {delay:F2}s (arrive {deadline:F1}).");
                    world.Events.Add(delay, () => member.MoveTo(target, speed: RunSpeed));
                    continue;
                }

                member.AddStatus(SprintStatusId, available, SprintStatusParam);
                AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) can't make deadline {deadline:F1} even sprinting ({neededSpeed:F2}y/s needed) -- sprinting anyway, leaving now.");
                world.Events.Add(PromptMoveDelay, () => member.MoveTo(target, speed: SprintSpeed));
            }
        });
    }

    // Schedule temporary death-immunity for `role` at scenario-time `time`, lasting
    // `seconds` (default 10). Wraps SimParty.GiveInvuln so AI strats can read top-to-
    // bottom alongside Move/Automarker, e.g. ai.GiveInvuln(28f, PartyRole.OffTank).
    public void GiveInvuln(float time, PartyRole role, float seconds = 10f)
        => world.Events.Add(time, () => world.Party.GiveInvuln(role, seconds));

    public void Automarker(float time, Func<Dictionary<PartyRole, Sign>> mapping)
    {
        world.Events.Add(time, () =>
        {
            Markings.ClearAll();
            foreach (var (role, sign) in mapping())
                if (world.Party.Get(role) is { } member && member.IsAlive())
                    Markings.Set(sign, member.GameObjectId);
        });
    }

    private Vector3 Jitter(Vector3 target, float radius)
    {
        var theta = rng.NextDouble() * 2.0 * Math.PI;
        var r = radius * MathF.Sqrt((float)rng.NextDouble());
        return new Vector3(
            target.X + r * MathF.Cos((float)theta),
            target.Y,
            target.Z + r * MathF.Sin((float)theta));
    }
}
