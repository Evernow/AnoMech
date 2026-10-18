using AnoMech.Core.Native.Implementations.Pointers;
using AnoMech.Core.Native.Interfaces;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace AnoMech.Core.Native.Implementations.Interop;

internal static unsafe class InstanceContentDirectorHelper
{
    // Returns false when the zone/director isn't ready yet (async load still in
    // flight) so MapController can retry instead of silently losing the call.
    public static bool ProcessDirectorUpdate(uint category, uint arg1 = 0, uint arg2 = 0, uint arg3 = 0, uint arg4 = 0, uint arg5 = 0, uint arg6 = 0)
    {
        // Outside a sim this resolves the director of whatever instance the player is really
        // in -- a live duty -- and drives its state machine. Nothing here may run there.
        if (Plugin.Game?.World.Map.IsInInstance != true)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.ProcessDirectorUpdate] no sim in progress -- ignoring.");
            return false;
        }

        var eventFramework = EventFramework.Instance();

        if (eventFramework == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.Commence] EventFramework.Instance() was null");
            return false;
        }

        var director = eventFramework->GetInstanceContentDirector();

        if (director == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.Commence] eventFramework->GetInstanceContentDirector() was null");
            return false;
        }

        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null) return false;

        if (category == BossLineCategory) LogBossLine(arg1);
        PacketDispatcher.HandleActorControlPacket(localPlayer.EntityId, DirectorUpdateCategory, director->GetEventId(),
            category, arg1, arg2, arg3, arg4, arg5, arg6, NoTarget, false);
        if (category == BossLineCategory) WatchBattleTalk(arg1);
        return true;
    }

    // For the log: what the dialogue box makes of a boss line over its first seconds, from the
    // HUD's queue to the box's own text.
    private static void WatchBattleTalk(uint line)
    {
        LogBattleTalk(line, 0);
        foreach (var ms in new[] { 100, 500, 2000 })
            Plugin.Framework.RunOnTick(() => LogBattleTalk(line, ms), TimeSpan.FromMilliseconds(ms));
    }

    private static void LogBattleTalk(uint line, int afterMs)
    {
        var queued = new List<string>();
        var hud = AgentHUD.Instance();
        if (hud != null)
            foreach (ref var talk in hud->QueuedBattleTalks)
                if (talk.IsPending) queued.Add($"\"{talk.Name}\": \"{talk.Text}\" image {talk.Image} sound {talk.Sound}");
        var stage = AtkStage.Instance();
        var strings = new List<string>();
        var array = stage == null ? null : stage->GetStringArrayData(StringArrayType.BattleTalk);
        if (array != null)
            for (var i = 0; i < array->Size; i++)
                if (array->StringArray[i] is { HasValue: true } value && value.ToString() is { Length: > 0 } text)
                    strings.Add($"{i}: \"{text}\"");
        var shown = new List<string>();
        var box = stage == null ? null : stage->RaptureAtkUnitManager->GetAddonByName("_BattleTalk");
        if (box != null)
            foreach (var node in box->UldManager.Nodes)
                if (node.Value != null && node.Value->Type == NodeType.Text && ((AtkTextNode*)node.Value)->NodeText.ToString() is { Length: > 0 } text)
                    shown.Add($"\"{text}\"");
        var state = box == null ? "not set up" : box->IsVisible ? "shown" : "hidden";
        AnoMech.Core.DiagnosticLog.Info($"[BattleTalk] Boss line 0x{line:X} +{afterMs} ms: queued [{string.Join(", ", queued)}]; strings [{string.Join(", ", strings)}]; box {state} [{string.Join(", ", shown)}].");
    }

    // A boss's line in the dialogue box: a row of the duty's own text, as the director placed it.
    private const uint BossLineCategory = 0x80000027;

    private static void LogBossLine(uint line)
    {
        var row = TextRowOf(line);
        var exd = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()->ExdModule;
        var sheet = exd == null ? null : exd->GetSheetByName("InstanceContentTextData");
        var loaded = row is { } id && sheet != null && exd->GetRowBySheetAndRowId(sheet, id) != null;
        AnoMech.Core.DiagnosticLog.Info($"[Director] Boss line 0x{line:X}: text row {row?.ToString() ?? "unknown"} {(loaded ? "loaded" : "not loaded")}; {DescribeText()}; strings: {Natives.Rsv.Describe()}.");
    }

    // For the log: where the director looks for the duty's text, as it copied the duty's data.
    public static string DescribeText()
    {
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
        return director == null ? "no director"
            : $"director text: objectives {director->InstanceContentTextDataObjectiveStart}..{director->InstanceContentTextDataObjectiveEnd}, "
              + $"boss {director->InstanceContentTextDataBossStart}..{director->InstanceContentTextDataBossEnd}";
    }

    // The duty's objective text, which its lines index into.
    private static uint? TextRowOf(uint line)
    {
        var world = LayoutWorld.Instance();
        var territory = world == null || world->ActiveLayout == null ? 0u : world->ActiveLayout->TerritoryTypeId;
        var content = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory)?.ContentFinderCondition.ValueNullable?.Content.RowId;
        if (content is not { } contentId) return null;
        return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>().GetRowOrDefault(contentId)?.InstanceContentTextDataObjectiveStart.RowId + line;
    }

    // ActorControlSelf: p1 director id, p2 variable, p3..p8 its values.
    private const uint DirectorUpdateCategory = 0x6D;
    private const uint NoTarget = 0xE0000000;

    // 0 when no director is loaded.
    public static uint DirectorEventId()
    {
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();
        return director == null ? 0 : director->GetEventId();
    }

    public static void SetDirectorData(byte sequence, byte unknown, ReadOnlySpan<byte> unionData)
    {
        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDirector();

        if (director == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDirectorData] no InstanceContentDirector");
            return;
        }

        const int length = PacketDispatcherPointers.DirectorVarsDataLength;
        var data = stackalloc byte[length];
        var span = new Span<byte>(data, length);
        span.Clear();
        unionData[..Math.Min(unionData.Length, length)].CopyTo(span);

        PacketDispatcherPointers.HandleDirectorVarsPacket(director->GetEventId(), sequence, unknown, data, length);
    }

    public static void SetDutyData(ContentFinderCondition contentFinderCondition)
    {
        var eventFramework = EventFramework.Instance();

        if (eventFramework == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] EventFramework.Instance() was null");
            return;
        }

        var director = eventFramework->GetInstanceContentDirector();

        if (director == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] eventFramework->GetInstanceContentDirector() was null");
            return;
        }

        var uiState = UIState.Instance();

        if (uiState == null)
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] UIState.Instance() was null");
            return;
        }

        var contentTypeRef = contentFinderCondition.ContentType;

        if (contentTypeRef.IsValid)
        {
            director->ContentTypeRowId = (byte)contentFinderCondition.ContentType.RowId;
            director->IconId = contentTypeRef.Value.IconDutyFinder;
        }
        else
        {
            Plugin.Log.Debug("[EventFrameworkHelper.SetDutyData] contentFinderCondition.ContentType was not valid, skipping director->ContentTypeRowId and director->IconId.");
        }

        using var str = new Utf8String(contentFinderCondition.Name);

        director->Title.SetString(str);
        uiState->DirectorTodo.Title.SetString(str);
        uiState->DirectorTodo.IsShown = true;
    }

    // Fire the three DirectorUpdate events the server sends at instance Commence
    // Note: these IDs/params are TOP-specific — other content fires different values
    // (e.g. T=1045 uses 40000007 / 40000001-E10 / 80000004-E0F). Future work to parameterize.
    public static void Commence()
    {
        ProcessDirectorUpdate(0x4000000C);
        ProcessDirectorUpdate(0x40000001, 0x1C20);
        ProcessDirectorUpdate(0x80000004, 0x1C1F);
    }
}
