using AlbionDataAvalonia.Locations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

/// <summary>
/// Local Mists camp-slot data and Abbey geometry/possible POIs from game templates. Abbey also has a bundled
/// terrain image; room rectangles remain the fallback when the image cannot be loaded.
/// </summary>
internal static partial class MistsMapData
{
    internal const string AbbeyImageFile = "avares://AlbionDataAvalonia/Assets/Maps/knightfall-abbey.webp";
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

    // An exact fixed Abbey exit identifies the master/turn when its placement is unique.
    // Chest spawns and player arrival points are not evidence of the orientation.
    public static MistsLayoutPlacement? RecognizeAbbey(IReadOnlyCollection<ActivityWorldPosition> landmarks)
    {
        var observations = landmarks.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).Distinct().ToArray();
        MistsLayoutPlacement? best = null;
        int bestScore = 0, runnerUp = 0;
        foreach (var (layout, dungeon) in Dungeons)
        {
            for (var turns = 0; turns < 4; turns++)
            {
                var score = dungeon.Exits.Count(point =>
                {
                    var (x, y) = point;
                    for (var turn = 0; turn < turns; turn++) (x, y) = (y, -x);
                    return observations.Any(p => Math.Abs(x - p.X) <= 1.5 && Math.Abs(y - p.Y) <= 1.5);
                });
                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score;
                    best = new(layout, turns);
                }
                else if (score > runnerUp) runnerUp = score;
            }
        }
        // Abbey exit sets at the four turns are disjoint in this master, unlike outdoor Mists.
        // One exact exit can therefore identify a unique placement; repeated packets cannot break a tie.
        return bestScore >= 1 && runnerUp < bestScore && runnerUp * 2 <= bestScore ? best : null;
    }

    /// <summary>The layout's camp slots or Abbey's possible bosses, room rewards and other static points.</summary>
    public static IReadOnlyList<MapPoi> Pois(string layout)
    {
        if (AbbeyPoints.TryGetValue(layout, out var points)) return points;
        if (Dungeons.TryGetValue(layout, out var dungeon))
            return dungeon.Chests.Select(chest => new MapPoi(MapPoiKind.Chest, "Boss chest", 3, new MapPoint(chest.X, chest.Z))).ToArray();
        return Slots.TryGetValue(layout, out var slots)
            ? slots.Select(slot => new MapPoi(MapPoiKind.Camp, slot.Medium ? "Possible medium site" : "Possible small site", slot.Medium ? 2 : 1,
                new MapPoint(slot.X, slot.Z), slot.Medium ? MediumChestRadius : SmallChestRadius,
                Notes: ["The instance can select a camp, resources or another variant here", "The circle bounds possible camp chest positions, not the full resource area", "Not a confirmed camp or chest"])).ToArray()
            : [];
    }

    /// <summary>Match an explicitly observed compound to a fixed slot centre in a confirmed layout/rotation.</summary>
    public static bool IsSlotCenter(string? layout, int? turns, double x, double y)
    {
        if (layout is null || turns is null || !Slots.TryGetValue(layout, out var slots)) return false;
        return slots.Any(slot =>
        {
            var point = new MapPoint(slot.X, slot.Z);
            for (var turn = 0; turn < turns.Value % 4; turn++) point = new(point.Y, -point.X);
            return Math.Abs(point.X - x) <= 0.1 && Math.Abs(point.Y - y) <= 0.1;
        });
    }

    /// <summary>An Abbey zone with its local terrain image, or a room schematic when no image is bundled.</summary>
    public static MapZone? DungeonZone(string layout) =>
        Dungeons.TryGetValue(layout, out var dungeon)
            ? new MapZone(layout, DungeonName, layout == "MISTS_DNG_UND_01" ? AbbeyImageFile : "", "mistsdungeon", "", null, new MapPoint(dungeon.Min.X, dungeon.Min.Z),
                new MapPoint(dungeon.Max.X, dungeon.Max.Z), dungeon.Exits.Select(exit => new MapZoneExit("", new MapPoint(exit.X, exit.Z))).ToArray(),
                dungeon.Rooms.Select(room => new MapRect(new MapPoint(room.MinX, room.MinZ), new MapPoint(room.MaxX, room.MaxZ))).ToArray(), ImageUsesWorldCoordinates: layout == "MISTS_DNG_UND_01")
            : null;

    private sealed record DungeonLayout((double X, double Z) Min, (double X, double Z) Max,
        (double MinX, double MinZ, double MaxX, double MaxZ)[] Rooms, (double X, double Z)[] Chests, (double X, double Z)[] Exits);
}
