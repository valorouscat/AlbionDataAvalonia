using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Maps;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;

namespace AlbionDataAvalonia.ViewModels;

[Flags]
public enum ActivityMapLayers
{
    None = 0, Path = 1, Kills = 2, Loot = 4, Gathering = 8, Deaths = 16, Features = 32, Resources = 64, ResourceSpawns = 128,
    Pois = 256, RoamingBosses = 512, HostileAlert = 1024,
    All = Path | Kills | Loot | Gathering | Deaths | Features | Resources | ResourceSpawns | Pois | RoamingBosses | HostileAlert
}

/// <summary>A zone's danger level, which colors its exits.</summary>
public enum MapZoneKind { Blue, Yellow, Red, Black, Other }

public sealed record ActivityMapExit(MapPoint Position, string Label, MapZoneKind Zone);

/// <summary>A game icon, or a world boss whose portrait (from its Mob) is loaded when first drawn.</summary>
public sealed record ActivityMapIcon(MapPoint Position, string Label, Bitmap? Image, double Scale = 1, string? Mob = null);

/// <summary>
/// Everything the map canvas draws for one frame. ViewKey changes when a different zone, run or mode is shown,
/// which resets zoom and panning; the rest can change every frame. Frame is the in-game paper frame drawn around the zone.
/// ImageQuarterTurns turns the minimap clockwise about its centre, as a Mists instance turns its layout.
/// Resources are the live map's resource nodes, already filtered by tier and kind and sorted best first; ResourceMobs are
/// the mobs that yield resources, filtered the same way and sorted best last. ResourceSpawns are the zone's possible
/// resource spots from the game files, filtered the same way and sorted best last. Pois are the zone's possible
/// loot chest spots from the game files, sorted best last. Rooms outline a zone drawn without a minimap, such as a Mists
/// dungeon, and Outline is the rough shape of its ground under them, such as a Depths floor's; the layout never turns them. RoamingBosses are the live map's roaming bosses in
/// view, sorted best last. Hostiles are the hostile players who came into view in the last few seconds, which the
/// map flags over the character since the game gives no position for them.
/// </summary>
public sealed record ActivityMapScene(
    string ViewKey,
    MapImageGeometry Geometry,
    Bitmap? Image,
    Bitmap? Frame,
    IReadOnlyList<ActivityPathPoint> Path,
    IReadOnlyList<ActivityMapMarker> Markers,
    IReadOnlyList<ActivityMapExit> Exits,
    IReadOnlyList<ActivityMapIcon> Icons,
    ActivityPathPoint? Position,
    double? Heading,
    bool IsLive,
    DateTime NowUtc,
    double FadeSeconds,
    DateTime? StartUtc,
    DateTime? UntilUtc,
    ActivityMapLayers Layers,
    int ImageQuarterTurns = 0,
    IReadOnlyList<ActivityResourceNode>? Resources = null,
    IReadOnlyList<ActivityResourceMob>? ResourceMobs = null,
    IReadOnlyList<MapResourceSpawn>? ResourceSpawns = null,
    IReadOnlyList<MapPoi>? Pois = null,
    IReadOnlyList<MapRect>? Rooms = null,
    IReadOnlyList<MapRect>? Outline = null,
    IReadOnlyList<ActivityLiveActor>? RoamingBosses = null,
    IReadOnlyList<ActivityHostileSighting>? Hostiles = null,
    double ResourceIconScale = 1,
    double ResourceIconOpacity = 1,
    double ResourceClusterRadius = 16,
    double ResourceSpawnIconOpacity = 0.5);
