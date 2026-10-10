using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AnoMech.Core.Native.Interfaces;

// Party head-marker signs, written client-side only (no party broadcast).
public interface IMarkings
{
    void Set(Sign sign, GameObjectId target);

    // Both clear only signs Set wrote that still point where it set them: the game's own stay,
    // e.g. a real duty's when the plugin unloads there.
    void Clear(Sign sign);
    void ClearPlaced();
}
