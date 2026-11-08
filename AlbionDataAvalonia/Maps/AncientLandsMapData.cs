using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

/// <summary>Bundled Ancient Lands geometry and candidate positions. Observed objects are a separate live layer.</summary>
internal static partial class AncientLandsMapData
{
    public const string Master = "DRAGON_001";
    public const string ImageFile = "avares://AlbionDataAvalonia/Assets/Maps/ancient-lands-schematic.webp";
    private sealed record Variant(string Layer, int Tier, string Pvp);

    public static string? Layout(string rawMapId, string? layout)
    {
        // Preserve an unknown explicit layout rather than applying today's geometry to a future map.
        if (!string.IsNullOrWhiteSpace(layout))
            return layout.Equals(Master, StringComparison.OrdinalIgnoreCase) || Variants.ContainsKey(layout) ? layout : null;
        var parts = rawMapId.Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.FirstOrDefault(Variants.ContainsKey)
            ?? (parts.Any(x => x.Equals("DRAGONAREA", StringComparison.OrdinalIgnoreCase) || x.Equals(Master, StringComparison.OrdinalIgnoreCase)) ? Master : null);
    }

    public static MapZone? Zone(string rawMapId, string? layout)
    {
        var id = Layout(rawMapId, layout);
        if (id is null) return null;
        Variants.TryGetValue(id, out var variant);
        return new(id, "Ancient Lands", ImageFile, "dragonarea", variant?.Pvp ?? "", variant?.Tier,
            BoundsMin, BoundsMax, [], Outline: Ground, ImageUsesWorldCoordinates: true);
    }

    public static IReadOnlyList<MapPoi> Pois(string layout)
        => layout.Equals(Master, StringComparison.OrdinalIgnoreCase) ? Points["all"]
            : Variants.TryGetValue(layout, out var variant) ? Points[LayerAliases[variant.Layer]] : [];

    public static bool IsAltar(double x, double y) => Points["all"].Any(p => p.Kind == MapPoiKind.Altar
        && Math.Abs(p.Position.X - x) <= 1.5 && Math.Abs(p.Position.Y - y) <= 1.5);
}
