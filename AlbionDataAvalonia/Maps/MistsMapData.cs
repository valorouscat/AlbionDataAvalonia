using AlbionDataAvalonia.Locations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

/// <summary>
/// Mists map data that AlbionWorld's map features leave out, read from the game files. The camp slots of the Mists
/// layouts: each slot gets a section the game picks when the instance opens, and when that is a PvE camp, its chest
/// stands within the slot's radius of its centre. And the Mists dungeon (Knightfall Abbey), which AlbionWorld has no map
/// for: its rooms, the boss chest many rooms hold and its exits back to the Mists, drawn on a grid without a minimap.
/// </summary>
internal static partial class MistsMapData
{
    private const string DungeonName = "Knightfall Abbey";

    // The game can identify the instance as @MISTSDUNGEON@ without announcing a master name.
    // Use a default only while the bundled Abbey catalog has exactly one layout.
    public static MapZone? AbbeyZone(string rawMapId, string? layout)
    {
        foreach (var candidate in (layout ?? "").Split('@', StringSplitOptions.RemoveEmptyEntries)
            .Concat(rawMapId.Split('@', StringSplitOptions.RemoveEmptyEntries)))
        {
            if (DungeonZone(candidate) is { } exact) return exact;
            var mapped = ActivityInstanceMetadata.FromTransition(ActivityMapKind.MistsDungeon, candidate, null).Layout;
            if (mapped is not null && DungeonZone(mapped) is { } alias) return alias;
        }
        return string.IsNullOrWhiteSpace(layout) && AlbionLocations.ResolveActivityLocation(rawMapId).Kind == ActivityMapKind.MistsDungeon
            && Dungeons.Count == 1 ? DungeonZone(Dungeons.Keys.Single()) : null;
    }

    /// <summary>The layout's camp slots and, for a Mists dungeon, its boss chest spots.</summary>
    public static IReadOnlyList<MapPoi> Chests(string layout)
    {
        if (Dungeons.TryGetValue(layout, out var dungeon))
            return dungeon.Chests.Select(chest => new MapPoi(MapPoiKind.Chest, "Boss chest", 3, new MapPoint(chest.X, chest.Z))).ToArray();
        return Slots.TryGetValue(layout, out var slots)
            ? slots.Select(slot => new MapPoi(MapPoiKind.Chest, slot.Medium ? "Medium camp chest" : "Small camp chest", slot.Medium ? 2 : 1,
                new MapPoint(slot.X, slot.Z), slot.Medium ? MediumChestRadius : SmallChestRadius)).ToArray()
            : [];
    }

    /// <summary>A zone without a minimap for a Mists dungeon layout, or null for any other layout.</summary>
    public static MapZone? DungeonZone(string layout) =>
        Dungeons.TryGetValue(layout, out var dungeon)
            ? new MapZone(layout, DungeonName, "", "mistsdungeon", "", null, new MapPoint(dungeon.Min.X, dungeon.Min.Z),
                new MapPoint(dungeon.Max.X, dungeon.Max.Z), dungeon.Exits.Select(exit => new MapZoneExit("", new MapPoint(exit.X, exit.Z))).ToArray(),
                dungeon.Rooms.Select(room => new MapRect(new MapPoint(room.MinX, room.MinZ), new MapPoint(room.MaxX, room.MaxZ))).ToArray())
            : null;

    private sealed record DungeonLayout((double X, double Z) Min, (double X, double Z) Max,
        (double MinX, double MinZ, double MaxX, double MaxZ)[] Rooms, (double X, double Z)[] Chests, (double X, double Z)[] Exits);
}
