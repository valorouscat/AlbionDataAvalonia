using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

/// <summary>
/// The Depths (the game's hell dungeons), which AlbionWorld has no map for, read from the game files. Each Depths map is
/// one floor of a fixed master layout: a rough outline of where it has ground, the camp slots its sections fill, its
/// loot chest, coffer and key spots, resurrection shrines and exits, drawn on a grid without a minimap.
/// </summary>
internal static partial class DepthsMapData
{
    private const string Name = "The Depths";

    /// <summary>The floor's master layout for a Depths map id ("DUO_ORANGE_HELL_DUNGEON_02") or master name, trying the
    /// layout the client reports first and then each '@' part of the map id; null when none is a Depths floor.</summary>
    public static string? Master(string rawMapId, string? layout)
    {
        foreach (var candidate in new[] { layout ?? "" }.Concat(rawMapId.Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (Floors.ContainsKey(candidate)) return candidate;
            if (Maps.TryGetValue(candidate, out var master)) return master;
        }

        return null;
    }

    /// <summary>The master layout of the Depths floor at this level, counted from 1 at the entrance, or null.</summary>
    public static string? FloorLayout(int level) => Floors.FirstOrDefault(x => x.Value.Level == level).Key;

    public static int FloorCount => Floors.Count;

    /// <summary>A zone without a minimap for a Depths floor, or null for any other layout.</summary>
    public static MapZone? Zone(string master)
    {
        if (!Floors.TryGetValue(master, out var floor)) return null;
        // The exits are points of interest, coloured by where they lead.
        return new MapZone(master, $"{Name}, level {floor.Level}", "", "helldungeon", "black", null,
            new MapPoint(floor.Min.X, floor.Min.Z), new MapPoint(floor.Max.X, floor.Max.Z), [],
            floor.Slots.Select(slot => new MapRect(new MapPoint(slot.X - slot.Half, slot.Z - slot.Half), new MapPoint(slot.X + slot.Half, slot.Z + slot.Half))).ToArray(),
            floor.Outline.Select(cell => new MapRect(new MapPoint(cell.MinX, cell.MinZ), new MapPoint(cell.MaxX, cell.MaxZ))).ToArray());
    }

    /// <summary>The floor's camp slots, demon altars, loot chest, coffer and key spots, resurrection shrines and exits.</summary>
    public static IReadOnlyList<MapPoi> Pois(string master)
    {
        if (!Floors.TryGetValue(master, out var floor)) return [];
        static MapPoint At((double X, double Z) at) => new(at.X, at.Z);
        return
        [
            .. floor.Slots.Select(slot => new MapPoi(MapPoiKind.Camp, slot.Half > 100 ? "Large demon camp" : "Demon camp", slot.Half > 100 ? 3 : 2,
                new MapPoint(slot.X, slot.Z), Notes: ["A camp section the game picks for this slot when the floor opens; its chests and exits vary", slot.Note])),
            .. floor.Chests.Select(x => new MapPoi(MapPoiKind.Chest, "Treasure chest", 2, At(x),
                Notes: ["A spot where the floor's treasure chest can spawn, from the game files", "One of these spots gets it"])),
            .. floor.Altars.Select(x => new MapPoi(MapPoiKind.Altar, x.Grade switch { 3 => "Large demon altar", 2 => "Demon altar", _ => "Small demon altar" },
                x.Grade, new MapPoint(x.X, x.Z), Notes: ["A spot where the floor's monsters spawn, from the game files"])),
            .. floor.Coffers.Select(x => new MapPoi(MapPoiKind.Coffer, "Coffer", 1, At(x),
                Notes: ["A spot where a coffer can spawn, from the game files", "A few of these spots get one"])),
            .. floor.Keys.Select(x => new MapPoi(MapPoiKind.Key, "Key spawn", 2, At(x),
                Notes: ["A spot where a key can appear some minutes into the run, from the game files"])),
            .. floor.Shrines.Select(x => new MapPoi(MapPoiKind.Shrine, "Resurrection shrine", 2, At(x))),
            .. floor.Up.Select(x => new MapPoi(MapPoiKind.ExitUp, "Exit up", 2, At(x), Notes: ["Leaves the Depths"])),
            .. floor.SoulExits.Select(x => new MapPoi(MapPoiKind.ExitUp, "Soul exit", 3, At(x), Notes: ["Leaves the Depths once its souls are brought"])),
            .. floor.Down.Select(x => new MapPoi(MapPoiKind.ExitDown, "Exit down", 2, At(x), Notes: ["Leads a floor deeper, if it opens this run"])),
        ];
    }

    private sealed record Floor(int Level, (double X, double Z) Min, (double X, double Z) Max,
        (double X, double Z, double Half, string Note)[] Slots, (double MinX, double MinZ, double MaxX, double MaxZ)[] Outline,
        (double X, double Z)[] Chests, (double X, double Z)[] Coffers, (double X, double Z)[] Keys, (double X, double Z)[] Shrines,
        (double X, double Z)[] Up, (double X, double Z)[] Down, (double X, double Z)[] SoulExits, (double X, double Z, int Grade)[] Altars);
}
