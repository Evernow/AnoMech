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
    private const float RunSpeed = 6f;
    // Real Sprint gives +25% (6 * 1.25 = 7.5y/s), but this mirrors the ~9y/s figure
    // already used elsewhere in this codebase as the reference sprint speed (see
    // SimEnemy/SimNetworkPuppet's NetworkCatchUpSpeed doc comments) rather than
    // introducing a second, inconsistent number.
    private const float SprintSpeed = 9f;
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

    // Schedule a slot-move at `time`. `positions` is evaluated at fire-time;
    // null entries in the returned AiMove are skipped (no movement that slot).
    // When `arrivalTime` > 0, each member's MoveTo is deferred so they arrive at
    // the destination at scenario-time `arrivalTime` (running at RunSpeed) --
    // unless a plain walk can't close the gap in time but sprinting could, in
    // which case they pop Sprint (status, for the party-list icon, replicated to
    // every host/peer the same way any other AddStatus call already is) and
    // leave immediately at SprintSpeed instead of waiting to see if they're late.
    // Confirmed via AnoMech-DamageDebug dumps: UMAD P3's wave-2 DodgeSlap can
    // require ~9y/s to make a role's target in the ~2s window after DodgeImplosion
    // -- comfortably above RunSpeed, comfortably within reach of a sprint. If even
    // sprinting isn't enough, sprinting wouldn't help and just adds visual noise,
    // so that case still falls through to the plain "leave now, arrive late" path.
    //
    // `leaveImmediately` opts a caller out of the defer-until-the-last-moment
    // part of that: the member still walks/sprints at whatever speed makes
    // `arrivalTime`, but departs after LeaveImmediatelyDelay instead of standing
    // frozen at the old spot until the last possible moment then dashing.
    // Confirmed via dump: UMAD P2 Forsaken's tower reassignments have enough
    // slack that the old default made bots visibly freeze for 5-6s then snap
    // to the next spot in under a second. Off by default -- this only flips for
    // P2 Forsaken's own Move calls so P3/P4/TOP's already-verified timing (where
    // the deferred wait is presumably intentional/tuned) is untouched.
    //
    // The departure itself is delayed by LeaveImmediatelyDelay rather than fired
    // in the same tick the event scheduler wakes: a peer-controlled role's
    // Position (used by the host's own damage.Resolve, e.g. UmadP2Forsaken's
    // AllThingsEnding cone check) comes from that peer's last broadcast pose
    // snapshot, not a host-local simulation -- moving in the exact same frame an
    // event fires risks the host resolving a cone/hitbox check against a stale
    // pre-move snapshot for that role before its next pose broadcast lands.
    private const float LeaveImmediatelyDelay = 0.3f;

    public void Move(float time, Func<IAiMove> positions, float jitter = DefaultJitter, float arrivalTime = 0f, bool leaveImmediately = false)
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

                if (arrivalTime > 0f)
                {
                    var dx = target.X - member.Position.X;
                    var dz = target.Z - member.Position.Z;
                    var dist = MathF.Sqrt(dx * dx + dz * dz);
                    var available = arrivalTime - time;
                    var neededSpeed = available > 0f ? dist / available : float.PositiveInfinity;

                    if (neededSpeed > RunSpeed && neededSpeed <= SprintSpeed)
                    {
                        member.AddStatus(SprintStatusId, available, SprintStatusParam);
                        AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) sprinting -- {dist:F1}y in {available:F2}s needs {neededSpeed:F2}y/s.");
                        member.MoveTo(target, speed: SprintSpeed);
                        continue;
                    }

                    if (leaveImmediately)
                    {
                        AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) leaving in {LeaveImmediatelyDelay:F2}s (arrive {arrivalTime:F1}).");
                        world.Events.Add(LeaveImmediatelyDelay, () => member.MoveTo(target));
                        continue;
                    }

                    var delay = available - dist / RunSpeed;
                    if (delay > 0f)
                    {
                        AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}) deferred {delay:F2}s (arrive {arrivalTime:F1}).");
                        world.Events.Add(delay, () => member.MoveTo(target));
                        continue;
                    }
                }
                AnoMech.Core.DiagnosticLog.Info($"[AiManager] Move@{time:F1}: {role} from ({member.Position.X:F1},{member.Position.Z:F1}) -> ({target.X:F1},{target.Z:F1}).");
                member.MoveTo(target);
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
