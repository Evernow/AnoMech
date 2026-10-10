using System;

namespace AnoMech.Core.Native.Implementations;

// What the plugin wrote into each slot of a game-owned table, so a clear leaves alone a slot it
// never wrote and one the game has written since.
internal sealed class PlacedSlots<T>(int count) where T : struct, IEquatable<T>
{
    private readonly T?[] placed = new T?[count];

    public void Note(int slot, T value) => placed[slot] = value;

    // True when `current` is still the noted value; the slot is forgotten either way.
    public bool Release(int slot, T current)
    {
        if (placed[slot] is not { } ours) return false;
        placed[slot] = null;
        return ours.Equals(current);
    }
}
