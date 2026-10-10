using System.Numerics;
using AnoMech.Core.Game;

namespace AnoMech.Core.Native.Interfaces;

// Field markers, written client-side only.
public interface IWaymarks
{
    // World space. False when the MarkingController isn't available.
    bool Set(WaymarkSlot slot, Vector3 position);

    // Only markers Set wrote that are still in place: the game's own stay, e.g. a real duty's when
    // the plugin unloads there.
    void ClearPlaced();
}
