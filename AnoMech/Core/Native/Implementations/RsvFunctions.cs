using System.Collections.Generic;
using System.Linq;
using System.Text;
using AnoMech.Core.Native.Implementations.Pointers;
using AnoMech.Core.Native.Interfaces;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;

namespace AnoMech.Core.Native.Implementations;

internal sealed unsafe class RsvFunctions : IRsvFunctions
{
    private const int KeyLength = 0x30;
    private const int HeaderLength = 4 + KeyLength;

    private readonly HashSet<string> added = [];
    // Since the last report.
    private int skipped;

    // RsvReceive dereferences the LayoutWorld singleton, so it must be up. Best-effort like RSF.
    public void Add(byte[] record)
    {
        var fn = ResolvedStringPointers.RsvReceive;
        if (fn == null || record == null || record.Length < HeaderLength || LayoutWorld.Instance() == null)
        {
            skipped++;
            return;
        }
        added.Add(Encoding.ASCII.GetString(record, 4, KeyLength).Split('\0')[0]);
        fixed (byte* p = record)
            fn(p);
    }

    public string Describe()
    {
        var since = $"{skipped} not sent since the last report"
                    + (ResolvedStringPointers.RsvReceive == null ? " (no receive handler found)" : "");
        skipped = 0;
        var world = LayoutWorld.Instance();
        if (world == null) return $"no layout world; {since}";
        var territory = world->ActiveLayout == null ? 0 : world->ActiveLayout->TerritoryTypeId;
        var held = world->RsvMap == null ? "no table" : $"{world->RsvMap->LongCount} held";
        var resolving = added.Count(key => world->ResolveRsvString(key) is { HasValue: true } value && value.ToString() != key);
        return $"{held}, {resolving} of the {added.Count} added resolve, territory {territory}; {since}";
    }
}
