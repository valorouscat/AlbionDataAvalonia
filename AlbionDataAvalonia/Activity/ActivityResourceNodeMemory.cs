using AlbionDataAvalonia.Activity.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AlbionDataAvalonia.Activity;

/// <summary>
/// The resource nodes seen on each open-world map, kept between visits and restarts so the live map can show what
/// stood where last time: kind, tier, enchantment, units left and when it was seen, or that it was gathered out.
/// The game only sends nodes near the character, so a remembered node is what was there when last seen, not what is
/// there now. Object ids change every visit, so a node is known by its map and position. The caller holds the lock.
/// </summary>
public sealed class ActivityResourceNodeMemory
{
    // Nodes stand on fixed spots; one closer than this to a remembered node is the same node.
    private const double SameSpot = 1.5;
    private const double CellSize = 2;
    private const int MaxNodesPerMap = 4000;
    private const int MaxMaps = 400;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string path;
    private readonly Dictionary<string, MapNodes> maps = new(StringComparer.OrdinalIgnoreCase);
    private bool loaded;
    private bool dirty;
    private DateTime dirtySince;

    public ActivityResourceNodeMemory(string path) => this.path = path;

    /// <summary>Notes a node seen with this many units left (zero when the game didn't say), or just gathered out.</summary>
    public void Note(string mapKey, string family, int tier, int enchantment, double x, double y, int charges, bool gatheredOut, DateTime at)
    {
        var nodes = Map(mapKey);
        var known = nodes.Find(x, y);
        var gathered = gatheredOut ? at : (DateTime?)null;
        // A gathered-out note only follows a sighting, so it keeps the kind already known for the spot.
        if (known is not null && gatheredOut)
        {
            if (known.GatheredAtUtc is not null) return;
            nodes.Replace(known, known with { Charges = 0, GatheredAtUtc = gathered, SeenAtUtc = at });
        }
        else if (known is not null)
            nodes.Replace(known, known with { Family = family, Tier = tier, Enchantment = enchantment, X = x, Y = y, Charges = charges,
                SeenAtUtc = at, GatheredAtUtc = null });
        else nodes.Add(new RememberedNode(family, tier, enchantment, x, y, Math.Max(0, charges), at, gathered));
        Touch(at);
    }

    /// <summary>The nodes remembered on a map, as the live map draws them.</summary>
    public IEnumerable<ActivityResourceNode> Remembered(string mapKey)
    {
        Load();
        if (!maps.TryGetValue(mapKey, out var nodes)) yield break;
        foreach (var node in nodes.All)
            yield return new ActivityResourceNode(0, node.Family, node.Tier, node.Enchantment, node.X, node.Y, node.Charges, node.SeenAtUtc,
                ActivityResourceNodePackets.ItemUniqueName(node.Family, node.Tier, node.Enchantment), Live: false, GatheredAtUtc: node.GatheredAtUtc);
    }

    /// <summary>Forgets every node remembered on a map.</summary>
    public bool Forget(string mapKey)
    {
        Load();
        if (!maps.Remove(mapKey)) return false;
        Touch(DateTime.UtcNow);
        return true;
    }

    /// <summary>Writes the file once changes have settled, or now when forced (on exit).</summary>
    public void Flush(DateTime now, bool force = false)
    {
        if (!dirty || !force && now - dirtySince < SaveDelay) return;
        dirty = false;
        try
        {
            var file = new MemoryFile(maps.Select(x => new MemoryMap(x.Key, x.Value.All.ToList())).ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Couldn't save remembered resource nodes to {Path}", path);
        }
    }

    private MapNodes Map(string mapKey)
    {
        Load();
        if (maps.TryGetValue(mapKey, out var nodes)) return nodes;
        if (maps.Count >= MaxMaps) maps.Remove(maps.MinBy(x => x.Value.LastSeenUtc).Key);
        return maps[mapKey] = new MapNodes();
    }

    private void Touch(DateTime at)
    {
        if (!dirty) dirtySince = at;
        dirty = true;
    }

    // Read once, on first use; nodes older than the retention are dropped then.
    private void Load()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(path)) return;
            var file = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(path), JsonOptions);
            var oldest = DateTime.UtcNow - Retention;
            foreach (var map in file?.Maps ?? [])
            {
                if (string.IsNullOrWhiteSpace(map.Key) || map.Nodes is null) continue;
                var nodes = new MapNodes();
                foreach (var node in map.Nodes.Where(x => x.SeenAtUtc >= oldest && x.Family is { Length: > 0 }).OrderBy(x => x.SeenAtUtc))
                    nodes.Add(node with { SeenAtUtc = DateTime.SpecifyKind(node.SeenAtUtc, DateTimeKind.Utc),
                        GatheredAtUtc = node.GatheredAtUtc is { } gathered ? DateTime.SpecifyKind(gathered, DateTimeKind.Utc) : null });
                if (nodes.Count > 0) maps[map.Key] = nodes;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning(ex, "Couldn't read remembered resource nodes from {Path}", path);
        }
    }

    public sealed record RememberedNode(string Family, int Tier, int Enchantment, double X, double Y, int Charges, DateTime SeenAtUtc,
        DateTime? GatheredAtUtc);

    private sealed record MemoryMap(string Key, List<RememberedNode> Nodes);

    private sealed record MemoryFile(List<MemoryMap> Maps);

    // A map's nodes, filed in a grid so a sighting finds its spot without scanning the whole map.
    private sealed class MapNodes
    {
        private readonly Dictionary<(int X, int Y), List<RememberedNode>> grid = new();

        public int Count { get; private set; }
        public DateTime LastSeenUtc { get; private set; }
        public IEnumerable<RememberedNode> All => grid.Values.SelectMany(x => x);

        public RememberedNode? Find(double x, double y)
        {
            var (cellX, cellY) = Cell(x, y);
            RememberedNode? best = null;
            var bestDistance = SameSpot;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((cellX + dx, cellY + dy), out var cell))
                        foreach (var node in cell)
                        {
                            var distance = Math.Sqrt((node.X - x) * (node.X - x) + (node.Y - y) * (node.Y - y));
                            if (distance > bestDistance) continue;
                            best = node;
                            bestDistance = distance;
                        }
            return best;
        }

        public void Add(RememberedNode node)
        {
            if (Count >= MaxNodesPerMap) Remove(All.MinBy(x => x.SeenAtUtc)!);
            var key = Cell(node.X, node.Y);
            if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<RememberedNode>();
            cell.Add(node);
            Count++;
            if (node.SeenAtUtc > LastSeenUtc) LastSeenUtc = node.SeenAtUtc;
        }

        public void Replace(RememberedNode old, RememberedNode next)
        {
            Remove(old);
            Add(next);
        }

        private void Remove(RememberedNode node)
        {
            var key = Cell(node.X, node.Y);
            if (!grid.TryGetValue(key, out var cell) || !cell.Remove(node)) return;
            Count--;
            if (cell.Count == 0) grid.Remove(key);
        }

        private static (int, int) Cell(double x, double y) => ((int)Math.Floor(x / CellSize), (int)Math.Floor(y / CellSize));
    }
}
