using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace AlbionDataAvalonia.Maps;

/// <summary>Local game-file candidates, not live nodes. Random slave sections are represented only by possible areas.</summary>
internal static class MistsResourceData
{
    private static readonly string[] Families = ["Wood", "Stone", "Fiber", "Hide", "Ore"];
    private static readonly Lazy<Catalog> CatalogData = new(Load);
    private static Catalog Data => CatalogData.Value;
    private static readonly ConcurrentDictionary<(string Layout, string Context), MapResourceSpawn[]> Cache = new();

    public static bool HasLayout(string? layout) => layout is not null && layout.StartsWith("MISTS_", StringComparison.Ordinal) && Data.Layouts.ContainsKey(layout);

    public static MapZone? Zone(string? layout) => HasLayout(layout) && Data.Layouts.TryGetValue(layout!, out var data)
        ? new(layout!, "The Mists", "", "mists", "", null, new(data.Bounds[0], data.Bounds[1]),
            new(data.Bounds[2], data.Bounds[3]), []) : null;

    public static IReadOnlyList<MapResourceSpawn> Spawns(string layout, string rawMapId)
    {
        if (!HasLayout(layout)) return [];
        var context = rawMapId.Split('@').Select(x => x.Trim().ToUpperInvariant()).FirstOrDefault(Data.Contexts.ContainsKey) ?? "";
        if (context.Length > 0 && Data.Contexts[context].Layout != layout) return [];
        return Cache.GetOrAdd((layout, context), key => Build(key.Layout, key.Context));
    }

    /// <summary>Local tier sets take precedence over an exact-tier CDN duplicate; keep unrelated CDN spots outside random slots.</summary>
    public static IReadOnlyList<MapResourceSpawn> Merge(string layout, string rawMapId, IReadOnlyList<MapResourceSpawn> remote)
    {
        if (!HasLayout(layout)) return remote;
        var data = Data.Layouts[layout];
        var context = rawMapId.Split('@').Select(x => x.Trim().ToUpperInvariant()).FirstOrDefault(Data.Contexts.ContainsKey);
        if (context is not null && Data.Contexts[context].Layout != layout) return [];
        static (string Family, bool Mob, double X, double Y, double Half) Key(MapResourceSpawn spawn) =>
            (spawn.Family, spawn.Mob, Math.Round(spawn.Position.X, 1), Math.Round(spawn.Position.Y, 1), spawn.AreaHalfSize);
        var result = Spawns(layout, rawMapId).ToDictionary(Key);
        foreach (var group in remote.Where(x => !data.Slots.Any(s =>
                     Math.Abs(x.Position.X - s[0]) <= s[2] && Math.Abs(x.Position.Y - s[1]) <= s[2])).GroupBy(Key))
        {
            if (result.ContainsKey(group.Key)) continue;
            var best = group.MaxBy(x => x.Tier)!;
            result[group.Key] = group.Count() == 1 ? best : best with { PossibleTiers = group.Aggregate(0, (mask, x) => mask | x.TierMask) };
        }
        return result.Values.ToArray();
    }

    private static MapResourceSpawn[] Build(string layout, string contextId)
    {
        var data = Data.Layouts[layout];
        Context[] contexts = contextId.Length > 0 ? [Data.Contexts[contextId]]
            : Data.Contexts.Values.Where(x => x.Layout == layout).ToArray();
        if (contexts.Length == 0) return [];
        // If only landmarks identify a layout, use positions shared by every possible biome. Never guess a biome.
        var biomes = contexts.Select(x => x.Biome).Distinct().ToArray();
        var nodes = data.Variants[biomes[0]].Select(NodeKey).ToHashSet();
        foreach (var biome in biomes.Skip(1)) nodes.IntersectWith(data.Variants[biome].Select(NodeKey));
        var result = new List<MapResourceSpawn>();
        foreach (var (family, bucket, x, y) in nodes.OrderBy(x => x))
        {
            var mask = contexts.Aggregate(0, (value, c) => value | c.Masks[family][bucket]);
            if (mask != 0) result.Add(Spawn(family, mask, x, y));
        }
        foreach (var slot in data.Slots)
        {
            var size = slot[2] > 40 ? "medium" : "small";
            for (var family = 0; family < Families.Length; family++)
            {
                var mask = contexts.Aggregate(0, (value, c) => value | (c.Areas.TryGetValue(size, out var tiers) ? tiers[family] : 0));
                if (mask != 0) result.Add(Spawn(family, mask, slot[0], slot[1], slot[2]));
            }
        }
        return result.GroupBy(x => (x.Family, x.Position, x.AreaHalfSize)).Select(group =>
        {
            var mask = group.Aggregate(0, (value, spawn) => value | spawn.TierMask);
            return Spawn(Array.IndexOf(Families, group.Key.Family), mask, group.Key.Position.X, group.Key.Position.Y, group.Key.AreaHalfSize);
        }).ToArray();
    }

    private static (int Family, int Bucket, double X, double Y) NodeKey(double[] row) => ((int)row[0], (int)row[1], row[2], row[3]);
    private static MapResourceSpawn Spawn(int family, int mask, double x, double y, double halfSize = 0) =>
        new(Families[family], Enumerable.Range(1, 8).Last(t => (mask & (1 << t)) != 0), new(x, y),
            Candidate: true, PossibleTiers: mask, AreaHalfSize: halfSize);

    private static Catalog Load()
    {
        using var stream = typeof(MistsResourceData).Assembly.GetManifestResourceStream("ReferenceData.mists-resource-spawns.json.gz")
            ?? throw new InvalidOperationException("Mists resource spawn catalog is missing.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Catalog>(gzip, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Mists resource spawn catalog is invalid.");
    }

    private sealed class Catalog
    {
        public Dictionary<string, Layout> Layouts { get; set; } = [];
        public Dictionary<string, Context> Contexts { get; set; } = [];
    }
    private sealed class Layout
    {
        public double[] Bounds { get; set; } = [];
        public double[][] Slots { get; set; } = [];
        public Dictionary<string, double[][]> Variants { get; set; } = [];
    }
    private sealed class Context
    {
        public string Layout { get; set; } = "";
        public string Biome { get; set; } = "";
        public int[][] Masks { get; set; } = [];
        public Dictionary<string, int[]> Areas { get; set; } = [];
    }
}
