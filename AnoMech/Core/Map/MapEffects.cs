using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node;

namespace AnoMech.Core.Map;

// Hook on ProcessMapEffect (sig from Hyperborea/ECommons).
// Logs every call at Debug level: [MapEffect] index=0x?? state=0x???? flags=0x??
// Apply() replays a known effect by calling the native function directly.
//
// The "module" arg (EventFramework+0x158) resolves to DirectorModule.ActiveContentDirector.
// ProcessMapEffectEx (the network-packet batch variant) routes through this same function
// internally, so there is no separate commit step — this is the correct and only endpoint.
//
// Encoding: packetFlags high16 = State, low8 = Flags
// State: selects the SGB animation mode on the FIRST call to a slot; ignored on subsequent calls.
// Flags: triggers a specific animation action (0x01 show, 0x02 spawn, 0x04 hide, 0x08 despawn,
//        0x10 eyelid-close-instant, 0x20 eyelid-close-anim, 0x40/0x80 charge anim).
internal sealed unsafe class MapEffects : IDisposable
{
    public bool Loaded { get; set; } = false;

    private delegate long ProcessMapEffectDelegate(long module, uint index, ushort state, ushort flags);
    private readonly Hook<ProcessMapEffectDelegate> hook;

    internal MapEffects()
    {
        var addr = Plugin.SigScanner.ScanText(
            "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B FA 41 0F B7 E8");
        hook = Plugin.GameInterop.HookFromAddress<ProcessMapEffectDelegate>(addr, Detour);
        hook.Enable();
    }

    private long Detour(long module, uint index, ushort state, ushort flags)
    {
        Plugin.LogManager.LogMapEffect(index, state, flags);
        // Was Plugin.Log.Info (invisible in dumps). This only fires for calls that reach
        // ProcessMapEffect through the REAL hooked entry point -- i.e. the native
        // engine/packet path, not our own Apply() below (which calls hook.Original
        // directly and bypasses this detour entirely, logging separately as "native
        // call:"). "REAL native call" distinguishes the two in a dump: a host's SGB was
        // observed carrying flag bits (0x10/0x20/0x40/0x80) neither Apply() nor
        // AddEffect's callers ever send, present before our own replayed calls ever
        // touch that index -- if some other native/packet-driven path is setting them
        // (e.g. as a side effect of normal zone-load pop-in a peer's client-side zone
        // reconstruction doesn't equally trigger), this is the only place that would
        // ever observe it.
        var director = (ContentDirector*)module;
        var before = ReadMapEffectItem(director, index);
        var result = hook.Original(module, index, state, flags);
        var after = ReadMapEffectItem(director, index);
        AnoMech.Core.DiagnosticLog.Info(
            $"[MapEffect] REAL native call: index=0x{index:X} state=0x{state:X} flags=0x{flags:X} module=0x{module:X} "
            + $"item before=({Format(before)}) after=({Format(after)}).");
        return result;
    }

    // packetFlags: high16=State, low8=Flags (ACT type-257 raw value).
    // Returns false when the zone/director isn't ready yet (async load still in
    // flight) so MapController can retry instead of silently losing the call.
    internal bool Apply(uint packetFlags, byte index)
    {
        if (!Loaded) return false;
        var module = *(nint*)((nint)EventFramework.Instance() + 344);
        if (module == 0) return false;
        var state = (ushort)(packetFlags >> 16);
        var flags = (ushort)(packetFlags & 0xFF);
        // DiagnosticLog (dump-visible), placed here rather than in Detour: Apply()
        // calls hook.Original directly, bypassing Detour, so this is the only place
        // that actually observes our own replayed calls reaching the engine. Confirms
        // the native call landed at all -- if a peer's arena is still wrong despite
        // this line matching the host's, the remaining gap is downstream of this call
        // (e.g. a missing/stale SGB), not the call itself failing or being skipped.
        // Return value captured (previously discarded) specifically to test whether it
        // signals "target SGB not found/not ready" -- Apply() unconditionally returned
        // true here regardless, so a peer whose zone-load left this specific SGB still
        // streaming in (a narrower readiness gap than the Loaded/module checks above
        // catch) would have this call silently no-op at the native level while every
        // layer of our own code believed it succeeded. Logged whether or not it differs
        // from the host's so a host/peer dump pair can be compared directly.
        // ContentDirector.MapEffects (verified via the local FFXIVClientStructs checkout,
        // Client/Game/InstanceContent/ContentDirector.cs) is the actual per-index array
        // ProcessMapEffect looks up: ContentDirector.MapEffectItem.LayoutId is the target SharedGroup's
        // layout ID, .State/.Flags mirror what was last successfully applied. Read before
        // AND after the native call so a host/guest dump pair can show, per index: whether
        // LayoutId is even populated (0 would mean this slot's SGB was never resolved on
        // that client at all -- the call has nothing real to act on regardless of what it
        // returns) and whether .State/.Flags actually changed to match what we just sent.
        var director = (ContentDirector*)module;
        var before = ReadMapEffectItem(director, index);
        var result = hook.Original(module, index, state, flags);
        var after = ReadMapEffectItem(director, index);
        AnoMech.Core.DiagnosticLog.Info(
            $"[MapEffect] native call: index=0x{index:X} state=0x{state:X} flags=0x{flags:X} module=0x{module:X} result=0x{result:X} "
            + $"item before=({Format(before)}) after=({Format(after)}).");
        // PlayMapEffectTimeline tested and ruled out (returned True, no state change, on both
        // host and guest identically -- see prior dumps). ContentDirector.MapEffectItem.LayoutId is only an ID
        // into ContentDirector's own bookkeeping table, not the actual renderable object --
        // LayoutWorld.GetLayoutInstance(SharedGroup, layoutId) (verified via the local
        // FFXIVClientStructs checkout, Client/LayoutEngine/LayoutWorld.cs) resolves that ID to
        // the real Client::LayoutEngine::Group::SharedGroupLayoutInstance, which carries its
        // own independent load-status fields (PrefabFlags1: "0x1 = load started; 0x3 = load
        // failed or contents added; 0x4 = failed to add contents") and readiness methods
        // (IsPrimaryReady/IsPrimaryLoaded/HavePrimary) -- none of which ContentDirector's own
        // bookkeeping table can see. Every check so far (call order, LayoutId, State
        // transitions, native return codes, PlayMapEffectTimeline) has come back byte-identical
        // between host and guest despite the guest's arena staying visually wrong, so the
        // remaining gap has to be in something downstream of ContentDirector -- this is that.
        var sgState = ReadSharedGroupInstanceState(after.LayoutId);
        AnoMech.Core.DiagnosticLog.Info($"[MapEffect] SharedGroupLayoutInstance for index=0x{index:X} LayoutId=0x{after.LayoutId:X}: {sgState}.");
        return true;
    }

    private static ContentDirector.MapEffectItem ReadMapEffectItem(ContentDirector* director, uint index)
    {
        var list = director->MapEffects;
        if (list == null || index >= list->ItemCount) return default;
        return list->Items[(int)index];
    }

    private static string Format(ContentDirector.MapEffectItem item) => $"LayoutId=0x{item.LayoutId:X} State=0x{item.State:X} Flags=0x{item.Flags:X}";

    private static string ReadSharedGroupInstanceState(uint layoutId)
    {
        var world = LayoutWorld.Instance();
        if (world == null) return "LayoutWorld null";
        var instance = world->GetLayoutInstance(InstanceType.SharedGroup, layoutId);
        if (instance == null) return "GetLayoutInstance returned null (no such SharedGroup instance on this client)";
        var sg = (SharedGroupLayoutInstance*)instance;
        // HavePrimary/IsPrimaryLoaded/IsPrimaryReady only say the object exists and finished
        // loading -- they say nothing about whether the engine actually considers it active or
        // draws it. IsActive (ILayoutInstance.Flags3 bit4) and WantToBeActive() are the real
        // activation state; GetGraphics() returning null despite IsPrimaryReady=True would mean
        // the renderable scene object was never actually created, which no prior check here
        // could have caught.
        var graphics = instance->GetGraphics();
        var graphics2 = instance->GetGraphics2();
        // Every static field above (State/Flags/readiness/IsActive/WantToBeActive) has come back
        // identical between host and guest across multiple test pairs despite the guest's arena
        // staying visually wrong -- so the remaining gap has to be in something none of those
        // fields represent: whether the reveal/hide is an ANIMATED transition (via this object's
        // own timeline) that has to actually play frame-by-frame to become visible, as opposed to
        // an instant property flip. TimelineObject null would mean this SGB has no timeline data
        // at all; IsTimelinePlaying(PlayingTimelineIndex) false despite a just-issued "show" call
        // would mean the state was set but the animation that visually reveals it never started.
        var timelinePlaying = sg->IsTimelinePlaying(sg->PlayingTimelineIndex);
        // Graphics/Graphics2 on the top-level SharedGroupLayoutInstance read null even on the
        // host where the arena renders fine -- the actual renderable mesh belongs to the CHILD
        // instances nested in Instances (a prefab's placed BgParts etc.), which no check above
        // has ever looked at. If a child's own HavePrimary/IsPrimaryLoaded/Graphics diverges
        // between host and guest despite the parent SharedGroup matching exactly, that's the
        // real gap -- the parent can report full success while an individual child silently
        // fails to load/render.
        // Capped at 6 previously -- index 0x0 (the big early container piece) has 14 children
        // and none of the first 6 were the actual BgPart geometry type, while 4 of the 6 were
        // themselves nested SharedGroups whose own children were never inspected. Raised to 16
        // (matches the engine's own FixedSizeArray16 convention elsewhere) and now recurses one
        // level into any SharedGroup child, since that nested level is exactly where this
        // container's real geometry pieces most likely live.
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

    public void Dispose()
    {
        hook.Disable();
        hook.Dispose();
    }
}
