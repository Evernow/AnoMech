using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node;

namespace AnoMech.Core.Native;

// Shared by anything that spawns/toggles a native SharedGroup (MapEffects' arena pieces,
// SimEventObject's EObj scenery): describes whether a layout id's SharedGroupLayoutInstance
// has actually finished streaming in on this client. Extracted out of MapEffects since the
// same asset-streaming race affects both.
internal static unsafe class LayoutInstanceDiagnostics
{
    // HavePrimary/IsPrimaryLoaded/IsPrimaryReady only say the object loaded, not whether the
    // engine considers it active or draws it -- IsActive/WantToBeActive are the real
    // activation state; GetGraphics() null despite IsPrimaryReady=True means the renderable
    // scene object was never created.
    public static string Describe(uint layoutId)
    {
        var world = LayoutWorld.Instance();
        if (world == null) return "LayoutWorld null";
        var instance = world->GetLayoutInstance(InstanceType.SharedGroup, layoutId);
        if (instance == null) return "GetLayoutInstance returned null (no such SharedGroup instance on this client)";
        var sg = (SharedGroupLayoutInstance*)instance;
        var graphics = instance->GetGraphics();
        var graphics2 = instance->GetGraphics2();
        // Whether the reveal/hide is an animated timeline transition rather than an instant
        // flip. TimelineObject null means no timeline data; IsTimelinePlaying false despite a
        // just-issued "show" means the animation never started.
        var timelinePlaying = sg->IsTimelinePlaying(sg->PlayingTimelineIndex);
        // Graphics/Graphics2 on the parent can read null even when it renders fine -- the real
        // mesh usually belongs to child instances (a prefab's placed BgParts). A child
        // diverging from the parent is the gap the parent's own fields can't show. Capped at
        // 16 (engine's own FixedSizeArray16 convention) and recurses one level into nested
        // SharedGroup children.
        var childCount = sg->Instances.Instances.Count;
        var childSummaries = new List<string>();
        for (var i = 0; i < childCount && i < 16; i++)
        {
            var child = (ChildNodeInstance*)sg->Instances.Instances[i].Value;
            var childInstance = child != null ? child->Instance : null;
            childSummaries.Add($"[{i}]={FormatChild(childInstance, depth: 1)}");
        }
        return $"HavePrimary={instance->HavePrimary()} IsPrimaryLoaded={instance->IsPrimaryLoaded()} IsPrimaryReady={instance->IsPrimaryReady()} "
             + $"IsActive={instance->IsActive} WantToBeActive={instance->WantToBeActive()} Graphics=0x{(nint)graphics:X} Graphics2=0x{(nint)graphics2:X} "
             + $"TimelineObject=0x{(nint)sg->TimelineObject:X} PlayingTimelineIndex=0x{sg->PlayingTimelineIndex:X} IsTimelinePlaying={timelinePlaying} "
             + $"PrefabFlags1=0x{sg->PrefabFlags1:X} PrefabFlags2=0x{sg->PrefabFlags2:X} ChildCount={childCount} Children=[{string.Join("; ", childSummaries)}]";
    }

    // True once the instance and all direct children report fully loaded -- the simple
    // "done yet?" signal; Describe() above is the full detail dump.
    public static bool IsFullyLoaded(uint layoutId)
    {
        var world = LayoutWorld.Instance();
        if (world == null) return false;
        var instance = world->GetLayoutInstance(InstanceType.SharedGroup, layoutId);
        if (instance == null) return false;
        if (!instance->HavePrimary() || !instance->IsPrimaryLoaded()) return false;
        var sg = (SharedGroupLayoutInstance*)instance;
        var childCount = sg->Instances.Instances.Count;
        for (var i = 0; i < childCount && i < 16; i++)
        {
            var child = (ChildNodeInstance*)sg->Instances.Instances[i].Value;
            var childInstance = child != null ? child->Instance : null;
            if (childInstance != null && childInstance->HavePrimary() && !childInstance->IsPrimaryLoaded())
                return false;
        }
        return true;
    }

    private static string FormatChild(ILayoutInstance* childInstance, int depth)
    {
        if (childInstance == null) return "null";
        var childGraphics = childInstance->GetGraphics();
        var summary = $"Type={childInstance->Id.Type} HavePrimary={childInstance->HavePrimary()} "
            + $"IsPrimaryLoaded={childInstance->IsPrimaryLoaded()} IsActive={childInstance->IsActive} Graphics=0x{(nint)childGraphics:X}";
        if (depth <= 0 || childInstance->Id.Type != InstanceType.SharedGroup) return summary;
        var nested = (SharedGroupLayoutInstance*)childInstance;
        var nestedCount = nested->Instances.Instances.Count;
        var nestedSummaries = new List<string>();
        for (var i = 0; i < nestedCount && i < 16; i++)
        {
            var nestedChild = (ChildNodeInstance*)nested->Instances.Instances[i].Value;
            var nestedInstance = nestedChild != null ? nestedChild->Instance : null;
            nestedSummaries.Add($"[{i}]={FormatChild(nestedInstance, depth: depth - 1)}");
        }
        return $"{summary} NestedChildCount={nestedCount} NestedChildren=[{string.Join("; ", nestedSummaries)}]";
    }
}
