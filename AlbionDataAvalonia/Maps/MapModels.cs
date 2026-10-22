using System;
using System.Collections.Generic;

namespace AlbionDataAvalonia.Maps;

public readonly record struct MapPoint(double X, double Y);

/// <summary>A connection to another zone, at its position in this zone's local coordinates. A Mists dungeon's exits
/// back to the Mists and a Depths floor's exits have no TargetId; Label, when set, names such an exit.</summary>
public sealed record MapZoneExit(string TargetId, MapPoint Position, string? Label = null);

/// <summary>
/// One of the game's own minimap icons (banks, markets, outposts, Roads chests) in a zone, or a world boss, which
/// has no icon and is drawn with its mob's portrait instead.
/// </summary>
public sealed record MapZoneIcon(string? MarkerType, string Label, MapPoint Position, double Scale = 1, string? Mob = null);

/// <summary>
/// A spot where the game's zone layout can grow a resource, from AlbionWorld's map features: a node (Wood, Stone, Fiber,
/// Hide or Ore, as ActivityResourceIdentity names them) or, when Mob is set, a mob that yields one, such as a hide animal.
/// Candidate is one of the many spots the game picks a zone's ordinary nodes from; the rest are nodes the layout places
/// itself. It says what can stand there, not what stands there now; the game picks the enchantment when the node spawns.
/// </summary>
public sealed record MapResourceSpawn(string Family, int Tier, MapPoint Position, bool Mob = false, bool Candidate = false);

/// <summary>The kinds of point of interest the map shows in one layer, each drawn its own way.</summary>
public enum MapPoiKind { Chest, Boss, Shrine, Camp, Wisp, Key, Coffer, Altar, ExitUp, ExitDown }

/// <summary>
/// A point of interest from the game files, a spot where something can stand rather than what stands there now:
/// <list type="bullet">
/// <item>Chest: a loot chest the zone's layout places (open-world camp, faction and static chests, Corrupted Dungeon and
/// Hellgate chests, a Mists dungeon's boss chests) or, when Radius is set, a Mists camp slot whose chest stands within
/// Radius of it if the instance puts a camp there. Roads chests are left out, since the game gives them an icon.</item>
/// <item>Boss: where the layout spawns a boss or miniboss.</item>
/// <item>Shrine: a Corrupted Dungeon's shrine and other shrines.</item>
/// <item>Camp: one of the spots the game picks a zone's mob camps from.</item>
/// <item>Wisp: where a Mists wisp can appear.</item>
/// <item>Key: where a Depths key can appear.</item>
/// <item>Coffer: where a Depths coffer can spawn.</item>
/// <item>Altar: a Depths demon altar, where the floor's monsters spawn.</item>
/// <item>ExitUp and ExitDown: a Depths exit out or a floor deeper.</item>
/// </list>
/// Grade orders the spots of a kind: 1 small, 2 medium, 3 boss, large or high-tier. Notes, when set, are the spot's
/// tooltip lines in place of its kind's.
/// </summary>
public sealed record MapPoi(MapPoiKind Kind, string Label, int Grade, MapPoint Position, double Radius = 0, IReadOnlyList<string>? Notes = null);

/// <summary>A zone's minimap icons, resource spawn spots and points of interest, read from one AlbionWorld feature file.</summary>
public sealed record MapZoneFeatures(IReadOnlyList<MapZoneIcon> Icons, IReadOnlyList<MapResourceSpawn> ResourceSpawns,
    IReadOnlyList<MapPoi> Pois)
{
    public static MapZoneFeatures Empty { get; } = new([], [], []);
}

/// <summary>A rectangle in a zone's local coordinates, such as one of a dungeon's rooms.</summary>
public readonly record struct MapRect(MapPoint Min, MapPoint Max);

/// <summary>A zone from the AlbionWorld catalog the website also uses, or a Mists dungeon or Depths floor from the game
/// files, which has no minimap (ImageFile is empty) and is drawn as its Rooms on a grid, over the rough Outline of
/// its ground when the game files give one.</summary>
public sealed record MapZone(string Id, string DisplayName, string ImageFile, string MapCategory, string PvpCategory, int? Tier,
    MapPoint BoundsMin, MapPoint BoundsMax, IReadOnlyList<MapZoneExit> Exits, IReadOnlyList<MapRect>? Rooms = null,
    IReadOnlyList<MapRect>? Outline = null)
{
    public MapImageGeometry? Geometry { get; } = MapImageGeometry.From(BoundsMin, BoundsMax);
}

/// <summary>The in-game zone map frame AlbionWorld publishes, drawn around a minimap.</summary>
public static class MapFrame
{
    /// <summary>The frame is a square image centred on the minimap, which fills this share of its side
    /// (<c>mapScale</c> in AlbionWorld's map-features manifest; the website uses the same number).</summary>
    public const double MapScale = 0.84;
}

/// <summary>
/// The generated minimaps are the zone's local rectangle rotated by -45 degrees on an expanded canvas
/// (Pillow's centred rotate with expand). This mirrors the website's map-image functions.
/// </summary>
public sealed class MapImageGeometry
{
    private const double AngleRadians = -45 * Math.PI / 180;
    private static readonly double Cosine = Math.Cos(AngleRadians);
    private static readonly double Sine = Math.Sin(AngleRadians);

    private MapImageGeometry(MapPoint min, MapPoint max, double canvasWidth, double canvasHeight)
    {
        Min = min;
        Max = max;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
    }

    public MapPoint Min { get; }
    public MapPoint Max { get; }
    public double CanvasWidth { get; }
    public double CanvasHeight { get; }

    public static MapImageGeometry? From(MapPoint min, MapPoint max)
    {
        var sourceWidth = max.X - min.X;
        var sourceHeight = max.Y - min.Y;
        if (!double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || sourceWidth <= 0 || sourceHeight <= 0) return null;

        // Pillow rounds its affine coefficients to 15 decimals before measuring the expanded canvas.
        var cosine = Math.Round(Cosine, 15);
        var sine = Math.Round(Sine, 15);
        var rotatedWidth = Math.Abs(sourceWidth * cosine) + Math.Abs(sourceHeight * sine);
        var rotatedHeight = Math.Abs(sourceWidth * sine) + Math.Abs(sourceHeight * cosine);
        return new(min, max,
            Math.Ceiling((sourceWidth + rotatedWidth) / 2) - Math.Floor((sourceWidth - rotatedWidth) / 2),
            Math.Ceiling((sourceHeight + rotatedHeight) / 2) - Math.Floor((sourceHeight - rotatedHeight) / 2));
    }

    /// <summary>Where a local point lands on the minimap, as a fraction of its width and height.</summary>
    public MapPoint ToImageFraction(double x, double y)
    {
        var offsetX = x - (Min.X + Max.X) / 2;
        var offsetY = y - (Min.Y + Max.Y) / 2;
        var rotatedX = offsetX * Cosine - offsetY * Sine;
        var rotatedY = offsetX * Sine + offsetY * Cosine;
        return new((rotatedX + CanvasWidth / 2) / CanvasWidth, (CanvasHeight / 2 - rotatedY) / CanvasHeight);
    }
}
