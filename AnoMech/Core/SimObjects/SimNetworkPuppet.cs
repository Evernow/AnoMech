using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.SimObjects;

// A party slot occupied by a real remote player (a multiplayer peer, or the
// host's own view of one) rather than a local AI bot. Visually identical to
// SimPartyNpc -- same Lalafell-doppel spawn path in PartyCreator -- but its
// Movement is a no-op (NetworkPuppetMovement) so scenario/AI code that
// addresses party slots uniformly (AiManager.Move calls .MoveTo on every
// slot, including the real local player's) harmlessly skips it exactly the
// way it already skips SimPlayer. The only writer of this slot's position is
// ApplyNetworkPose, driven by poses received from the peer who owns it.
//
// Deliberately not a SimPartyNpc subclass: SimPartyNpc is sealed, and the two
// only share ~a dozen lines (Dead/OnKilled/Knockback), so a sibling class is
// simpler than lifting the seal on tested engine code for one extra caller.
public sealed unsafe class SimNetworkPuppet : SimNpc, ISimPartyMember
{
    public PartyRole Role { get; set; }
    public bool Dead { get; private set; }
    public byte ClassJob { get; }
    public string DisplayName { get; }

    internal SimNetworkPuppet(int index, Coordinates coordinates, PartyRole role, byte classJob, string name) : base(index, coordinates)
    {
        Role = role;
        ClassJob = classJob;
        DisplayName = name;
    }

    private protected override Movement Movement => field ??= new NetworkPuppetMovement(this);

    // Applies a pose reported by the owning peer's real client. Bypasses
    // Movement entirely (SetPosition is a plain SimCharacter write) so this
    // slot's position always reflects that peer's true position -- what
    // DamageSolver's spatial queries (stacks, gazes, cleaves) read on host.
    public void ApplyNetworkPose(Vector3 position, float rotation)
        => SetPosition(new Placement(position, rotation));

    public void Knockback(Vector3 source, float distance, float speed) => Movement.Knockback(source, distance, speed);

    public void OnKilled()
    {
        Dead = true;
        StopMoving();
        var bc = BattleCharaPtr;
        if (bc == null) return;
        bc->Health = 0;
        bc->Mana = 0;
        bc->Mode = CharacterModes.Dead;
        this.PlayKoActionTimeline();
    }
}
