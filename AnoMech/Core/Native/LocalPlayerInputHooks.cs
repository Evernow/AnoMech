using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace AnoMech.Core.Native;

// Hooks the native action and movement input paths so the simulator can stun
// the local player when a mechanic kills them. Status-row writes don't enforce
// anything (the server overwrites StatusManager._status[] on every packet); the
// real lockout is two booleans this class exposes — the detours read them every
// frame and short-circuit the original calls. Owned by Plugin (session-lifetime);
// SimPlayer is the sole writer of the two flags, reconciling them each tick from
// its own Dead / Movement.IsMoving state.
//
// Signatures and detour shapes lifted from FFXIV-RaidsRewritten's
// PlayerMovementOverride.cs / ActionManagerEx.cs (which themselves credit
// awgil's vnavmesh + bossmod). If a future patch breaks a sig, both projects
// will need to rev them together.
public sealed unsafe class LocalPlayerInputHooks : IDisposable
{
    internal const uint SprintActionId = 3;
    private const ushort SprintStatusId = 50;
    private const float SprintDuration = 10f; 
    internal const ushort SprintStatusParam = 30;

    public bool DisableAllActions { get; set; }
    public bool ZeroMovement { get; set; }

    // --- Player activity signals (read by SimPlayer to drive Party.Player.IsMoving/IsActing) ---
    // Movement is the engine's own per-frame movement sample taken in RMIWalkDetour — the same
    // signal bossmod's MovementOverride.IsMoving() reads — captured as the player's true input
    // intent *before* the stun-zeroing. This is intent/input based (matches cast-cancel semantics),
    // not a position delta. Holds its last value on frames where RMIWalk doesn't fire.
    public bool MovementInputActive { get; private set; }

    // True while the player's weapon auto-attack is swinging.
    public bool IsAutoAttacking => UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking;

    // True while the player is in a jump arc (CONDITION_JUMP). State poll like
    // IsAutoAttacking — a jump is always self-initiated, so the state flag is
    // equivalent to input intent here (nothing can force the player airborne).
    public bool IsJumping => Plugin.Condition[ConditionFlag.Jumping];

    // Latched whenever the player actually fires a real action; drained once per frame by SimPlayer
    // (PollActionUsed) so a same-frame action press is still observable to a snapshot mechanic.
    private bool actionUsedSincePoll;
    public bool PollActionUsed()
    {
        var used = actionUsedSincePoll;
        actionUsedSincePoll = false;
        return used;
    }

    // Debug aid for pinning down a real action id -- every UseAction attempt lands here,
    // newest last; DebugMenu reads this so a press in-game shows its real id.
    private const int RecentActionsCapacity = 50;
    private readonly Queue<(uint ActionId, ActionType Type)> recentActions = new();
    public IReadOnlyCollection<(uint ActionId, ActionType Type)> RecentActions => recentActions;

    private void RecordRecentAction(uint actionId, ActionType type)
    {
        // GeneralAction 1 is the auto-attack engage/re-engage action -- fires constantly, pure noise here.
        if (type == ActionType.GeneralAction && actionId == 1) return;
        recentActions.Enqueue((actionId, type));
        while (recentActions.Count > RecentActionsCapacity) recentActions.Dequeue();
        var jobId = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId;
        Core.DiagnosticLog.Info($"[LocalPlayerInputHooks] Action pressed: {actionId} ({type}) -- {Core.ActionLookup.Name(actionId)} (job={jobId}).");
    }

    // Edge-triggered gain/loss logging, not one line per frame. Reads the local player
    // directly (not Game.Player) so it works with no scenario running.
    private readonly HashSet<ushort> lastLoggedStatusIds = new();

    private void ScanAndLogActiveStatuses()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        var bc = (BattleChara*)localPlayer.Address;
        if (bc == null) return;
        var jobId = localPlayer.ClassJob.RowId;
        var current = new Dictionary<ushort, float>();
        foreach (var status in bc->StatusManager.Status)
            if (status.StatusId != 0) current[status.StatusId] = status.RemainingTime;
        foreach (var (gained, remaining) in current)
            if (!lastLoggedStatusIds.Contains(gained))
                Core.DiagnosticLog.Info($"[LocalPlayerInputHooks] Status gained: {gained} -- {Core.StatusLookup.Name(gained)} (job={jobId}, duration={remaining:F1}).");
        foreach (var lost in lastLoggedStatusIds.Except(current.Keys))
            Core.DiagnosticLog.Info($"[LocalPlayerInputHooks] Status lost: {lost} -- {Core.StatusLookup.Name(lost)} (job={jobId}).");
        lastLoggedStatusIds.Clear();
        foreach (var id in current.Keys) lastLoggedStatusIds.Add(id);
    }

    private delegate void RMIWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D")]
    private Hook<RMIWalkDelegate> rmiWalkHook = null!;

    private enum KeybindType
    {
        StrafeLeft = 325,
        StrafeRight = 326,
    }

    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool CheckStrafeKeybindDelegate(IntPtr ptr, KeybindType keybind);
    [Signature("E8 ?? ?? ?? ?? 84 C0 74 04 41 C6 06 01 BA 44 01 00 00")]
    private Hook<CheckStrafeKeybindDelegate> checkStrafeKeybindHook = null!;

    private readonly Hook<InputData.Delegates.IsInputIdPressed> isInputIdPressedHook;
    private readonly Hook<ActionManager.Delegates.Update> updateHook;
    private readonly Hook<ActionManager.Delegates.UseAction> useActionHook;
    private readonly Hook<ActionManager.Delegates.UseActionLocation> useActionLocationHook;

    public LocalPlayerInputHooks(IGameInteropProvider hook)
    {
        hook.InitializeFromAttributes(this);

        isInputIdPressedHook = hook.HookFromAddress<InputData.Delegates.IsInputIdPressed>(
            InputData.Addresses.IsInputIdPressed.Value, IsInputIdPressedDetour);
        updateHook = hook.HookFromAddress<ActionManager.Delegates.Update>(
            ActionManager.Addresses.Update.Value, UpdateDetour);
        useActionHook = hook.HookFromAddress<ActionManager.Delegates.UseAction>(
            ActionManager.Addresses.UseAction.Value, UseActionDetour);
        useActionLocationHook = hook.HookFromAddress<ActionManager.Delegates.UseActionLocation>(
            ActionManager.Addresses.UseActionLocation.Value, UseActionLocationDetour);

        rmiWalkHook.Enable();
        checkStrafeKeybindHook.Enable();
        isInputIdPressedHook.Enable();
        updateHook.Enable();
        useActionHook.Enable();
        useActionLocationHook.Enable();
    }

    public void Dispose()
    {
        // Game.Dispose() doesn't route through ResetInternal -- restore here too, or a
        // mid-sim unload leaves the real gauge/shield stuck faked forever.
        RestoreGaugeIllusion();
        if (Plugin.GameInstance is { } game) TankShieldTracker.ClearAllVisuals(game.World.Party);
        rmiWalkHook?.Dispose();
        checkStrafeKeybindHook?.Dispose();
        isInputIdPressedHook?.Dispose();
        updateHook?.Dispose();
        useActionHook?.Dispose();
        useActionLocationHook?.Dispose();
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        rmiWalkHook.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        // Capture the engine's movement sample as the player's true movement intent, before any
        // stun-zeroing below. (self is a MoveControllerSubMemberForMine*; the sums are its move vector.)
        MovementInputActive = *sumLeft != 0 || *sumForward != 0;
        if (!ZeroMovement) return;
        *sumLeft = 0;
        *sumForward = 0;
        *haveBackwardOrStrafe = 0;
    }

    private bool CheckStrafeKeybindDetour(IntPtr ptr, KeybindType keybind)
    {
        if (ZeroMovement && (keybind == KeybindType.StrafeLeft || keybind == KeybindType.StrafeRight))
            return false;
        return checkStrafeKeybindHook.Original(ptr, keybind);
    }

    private bool IsInputIdPressedDetour(InputData* inputData, InputId inputId)
    {
        if (ZeroMovement && (inputId == InputId.JUMP || inputId == InputId.PAD_JUMPANDCANCELCAST))
            return false;
        return isInputIdPressedHook.Original(inputData, inputId);
    }

    // Drains queued auto-attacks while DisableAllActions is set so the player
    // doesn't keep swinging mid-stun; mirrors raid-rewritten's UpdateDetour.
    private void UpdateDetour(ActionManager* self)
    {
        updateHook.Original(self);
        ScanAndLogActiveStatuses();
        UpdateGaugeIllusion();
        RefreshShieldVisuals();
        if (!DisableAllActions) return;
        var autosOn = UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking;
        if (autosOn) self->UseAction(ActionType.GeneralAction, 1);
    }

    // Runs every frame so a shield's visual stays honest as it depletes or expires --
    // TankShieldTracker only answers "how much is left" when asked, it never pushes updates.
    // Also clears every visual the instant IsInInstance goes false (leave/reset).
    private static void RefreshShieldVisuals()
    {
        if (Plugin.GameInstance is not { } game) return;
        var party = game.World.Party;
        if (!game.World.Map.IsInInstance)
        {
            TankShieldTracker.ClearAllVisuals(party);
            return;
        }
        foreach (var role in Enum.GetValues<PartyRole>())
            if (party.Get(role) is { } member)
                TankShieldTracker.RefreshVisual(member, role);
    }

    // ---- Gauge illusion (client-side only) -----------------------------------
    // Interception never checks gauge itself (see TryInterceptTankMitigation), but the real
    // hotbar icon reads the real job gauge to decide whether to render as available -- so a
    // gauge-gated mitigation (Holy Sheltron needs 50 Oath) would look disabled. Tops the gauge
    // up purely for display; saves the real value once and restores it the moment the sim ends.
    private byte? savedOathGauge;
    private const byte PaladinClassJobId = 19;

    private void UpdateGaugeIllusion()
    {
        if (Plugin.GameInstance is not { } game || !game.World.Map.IsInInstance)
        {
            RestoreGaugeIllusion();
            return;
        }
        // Only Holy Sheltron (Paladin/Oath) needs this today -- extend per-job as more
        // gauge-gated mitigations get wired into TankMitigationChart.
        if (Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId != PaladinClassJobId) return;
        var gauge = (PaladinGauge*)Plugin.JobGauges.Address;
        if (gauge == null) return;
        savedOathGauge ??= gauge->OathGauge;
        gauge->OathGauge = 100;
    }

    // Also called explicitly from Game.ResetInternal (not just the IsInInstance-flip branch
    // above) so the restore is immediate on Reset/Leave rather than waiting for next frame.
    public void RestoreGaugeIllusion()
    {
        if (savedOathGauge is not { } saved) return;
        if (Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId == PaladinClassJobId)
        {
            var gauge = (PaladinGauge*)Plugin.JobGauges.Address;
            if (gauge != null) gauge->OathGauge = saved;
        }
        savedOathGauge = null;
    }

    private bool UseActionDetour(ActionManager* self, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        RecordRecentAction(actionId, actionType);
        if (DisableAllActions && !IsStopAutosAction(actionType, actionId)) return false;
        if (actionType == ActionType.Action && TryInterceptTankMitigation(actionId, targetId))
        {
            actionUsedSincePoll = true;
            return true;
        }
        var result = useActionHook.Original(self, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
        // Record a real action use for Party.Player.IsActing — but ignore the auto-attack-cancel
        // general action that UpdateDetour issues while stunned.
        if (result && !IsStopAutosAction(actionType, actionId))
            actionUsedSincePoll = true;
        if (result && actionType == ActionType.Action && actionId == SprintActionId)
            Plugin.GameInstance?.Player?.AddStatus(SprintStatusId, SprintDuration, SprintStatusParam);
        return result;
    }

    // Blocks a tracked mitigation's real UseAction while a sim runs, so the real ability (and
    // its real recast group) never actually gets touched. In its place: applies our own
    // synthetic status, fakes the hotbar sweep (mirrors Game.ResetSprintCooldown, inverted),
    // and records the use on TankMitigationTracker's own sim-only cooldown. Returns false for
    // anything untracked or outside an instance; a press still on our sim-cooldown is
    // swallowed silently (true, no effect).
    private bool TryInterceptTankMitigation(uint actionId, ulong targetId)
    {
        if (Plugin.GameInstance is not { } game || !game.World.Map.IsInInstance) return false;
        if (!TankMitigation.ByActionId.TryGetValue(actionId, out var ability)) return false;
        var party = game.World.Party;
        var role = party.PlayerRole;
        if (party.Player is not { } player) return false;

        if (!TankMitigationTracker.IsAvailable(role, ability.StatusId, ability.Charges))
            return true; // still on the sim's own cooldown -- swallow the press, nothing happens

        var appliedRoles = new List<PartyRole>();
        if (ability.SourceSide)
        {
            var affected = ApplySourceSideMitigation(game.World, player, ability);
            // A peer's own enemy doppel is cosmetic-only -- report so the host's authoritative
            // copy gets the debuff too (no-op on host/solo).
            Plugin.MultiplayerInstance?.ReportAppliedEnemyStatus(affected, ability.StatusId, ability.Duration ?? 0f);
        }
        else if (ability.Scope == MitigationScope.Party)
        {
            foreach (var r in Enum.GetValues<PartyRole>())
            {
                if (party.Get(r) is not { } member) continue;
                member.AddStatus(ability.StatusId, ability.Duration ?? 0f);
                appliedRoles.Add(r);
            }
        }
        else if (ability.Scope == MitigationScope.Ally)
        {
            var targetRole = ResolveTargetRole(party, targetId);
            if (targetRole is { } r && party.Get(r) is { } member)
            {
                member.AddStatus(ability.StatusId, ability.Duration ?? 0f);
                appliedRoles.Add(r);
            }
            else
            {
                Core.DiagnosticLog.Warn($"[LocalPlayerInputHooks] {ability.Name} pressed with no resolvable party-member target -- swallowed, nothing applied.");
            }
        }
        else
        {
            // Self-scope needs no report here -- SendSelfMitigationIfChanged already polls
            // and reports the caster's own real statuses to the host separately.
            player.AddStatus(ability.StatusId, ability.Duration ?? 0f);
            appliedRoles.Add(role);
        }
        // Banks + visualizes any shield component this ability carries; no-op if it has none.
        var shieldFraction = GrantShield(party, player, ability, appliedRoles);

        // Party/Ally scope can touch roles other than the caster -- same cosmetic-puppet
        // reasoning as the SourceSide report above, plus the granted shieldFraction.
        if (ability.Scope is MitigationScope.Party or MitigationScope.Ally)
            Plugin.MultiplayerInstance?.ReportAppliedRoleStatus(appliedRoles, ability.StatusId, ability.Duration ?? 0f, shieldFraction);

        TankMitigationTracker.RecordUse(role, ability.StatusId, ability.Cooldown ?? 0f);
        ForceRecastSweep(actionId, ability.Cooldown ?? 0f);
        var jobId = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId;
        Core.DiagnosticLog.Info($"[LocalPlayerInputHooks] Intercepted {ability.Name} (actionId={actionId}) for {role} (job={jobId}), scope={ability.Scope} -- applied synthetic status {ability.StatusId} to [{string.Join(",", appliedRoles)}], real ability never touched.");
        return true;
    }

    // Reprisal-style mitigations debuff nearby enemies rather than buffing the caster --
    // applies the synthetic status to every active enemy within the ability's own radius
    // (see TankMitigationChart), centered on the caster, matching its real self-centered AoE.
    // Returns the affected enemies so the caller can report them across the network too.
    private static List<SimEnemy> ApplySourceSideMitigation(SimWorld world, SimCharacter caster, TankMitigationAbility ability)
    {
        var affected = new List<SimEnemy>();
        var radius = ability.Radius ?? 0f;
        if (radius <= 0f) return affected;
        var radiusSq = radius * radius;
        foreach (var enemy in world.Children.OfType<SimEnemy>())
        {
            if (!enemy.IsActive) continue;
            if (Vector3.DistanceSquared(caster.Position, enemy.Position) > radiusSq) continue;
            enemy.AddStatus(ability.StatusId, ability.Duration ?? 0f);
            affected.Add(enemy);
        }
        return affected;
    }

    // Banks the shield `ability` carries onto every role in appliedRoles and writes the
    // visual immediately. Returns the granted fraction (0f if none) for the caller's
    // multiplayer report. Shield % is always of the CASTER's max HP, so it's converted to an
    // absolute HP amount once, then re-expressed as a fraction of each recipient's own max HP.
    private static float GrantShield(SimParty party, SimCharacter caster, TankMitigationAbility ability, IReadOnlyList<PartyRole> appliedRoles)
    {
        var casterPercent = ability.ShieldPercentOfMaxHp
            ?? (ability.ShieldPotency is { } potency ? TankShieldEstimate.PercentOfCasterMaxHp(potency) : (float?)null);
        if (casterPercent is not { } percent) return 0f; // this ability carries no shield component at all
        var casterBc = caster.BattleCharaPtr;
        if (casterBc == null) return 0f;
        var casterShieldHp = casterBc->MaxHealth * percent;

        var lastGrantedFraction = 0f;
        foreach (var role in appliedRoles)
        {
            if (party.Get(role) is not { } member) continue;
            var recipientBc = member.BattleCharaPtr;
            if (recipientBc == null || recipientBc->MaxHealth == 0) continue;
            var fraction = casterShieldHp / recipientBc->MaxHealth;
            if (fraction <= 0f) continue;
            TankShieldTracker.Grant(role, fraction, ability.Duration ?? 0f);
            TankShieldTracker.RefreshVisual(member, role);
            lastGrantedFraction = fraction;
        }
        return lastGrantedFraction;
    }

    // Resolves a raw targetId (as received by UseAction) to whichever party role that
    // GameObjectId belongs to, for an Ally-scope mitigation (Oblation, Heart of Corundum,
    // Intervention, Nascent Flash) -- these are cast ON a specific party member, so the
    // interception needs to know who was actually targeted, not just who pressed the button.
    private static PartyRole? ResolveTargetRole(SimParty party, ulong targetId)
    {
        if (targetId == 0) return null;
        foreach (var role in Enum.GetValues<PartyRole>())
            if (party.Get(role) is { } member && (ulong)member.GameObjectId == targetId)
                return role;
        return null;
    }

    // Forces the recast-group sweep active without ever calling the real UseAction. Total must
    // be set explicitly (unlike Game.ResetSprintCooldown) -- a never-started group can have a
    // stale/zero Total, so IsActive=true alone renders no visible sweep.
    private static void ForceRecastSweep(uint actionId, float cooldownSeconds)
    {
        var am = ActionManager.Instance();
        if (am == null) return;
        var group = am->GetRecastGroup((int)ActionType.Action, actionId);
        if (group < 0) return;
        var detail = am->GetRecastGroupDetail(group);
        if (detail == null) return;
        detail->IsActive = true;
        detail->Elapsed = 0f;
        detail->Total = cooldownSeconds;
    }

    private bool UseActionLocationDetour(ActionManager* self, ActionType actionType, uint actionId, ulong targetId, Vector3* location, uint extraParam, byte a7)
    {
        if (DisableAllActions && !IsStopAutosAction(actionType, actionId)) return false;
        var result = useActionLocationHook.Original(self, actionType, actionId, targetId, location, extraParam, a7);
        if (result) actionUsedSincePoll = true;
        return result;
    }

    // Lets the auto-cancel UseAction from UpdateDetour through; everything else
    // bounces while autos are still firing.
    private static bool IsStopAutosAction(ActionType actionType, uint actionId)
    {
        if (!UIState.Instance()->WeaponState.AutoAttackState.IsAutoAttacking) return false;
        return actionType == ActionType.GeneralAction && actionId == 1;
    }
}
