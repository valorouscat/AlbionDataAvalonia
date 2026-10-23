using AlbionDataAvalonia.Crafting;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Market;

public sealed record MarketItem(string ItemId, string Name, string RussianName, int Tier, int Enchantment, int MaxQuality, int? BasePower, decimal MasteryModifier = 0)
{
    public int? Power(int quality) => BasePower + (quality switch { 2 => 20, 3 => 40, 4 => 60, 5 => 100, _ => 0 });
    public decimal? EffectivePower(int quality, bool personal, decimal skillBonus) => Power(quality) + (personal ? skillBonus * (1 + MasteryModifier) : 0);
    public string Label => $"{Name} · T{Tier}.{Enchantment}";
}

public sealed record MarketFamily(string Key, string Name, string RussianName, string SearchText, IReadOnlyList<MarketItem> Variants)
{
    public bool HasPower => Variants.Any(x => x.BasePower.HasValue);
    public bool HasQuality => Variants.Any(x => x.MaxQuality > 1);
}

/// <summary>Marketable items, including raw resources and drops without crafting recipes.</summary>
public sealed class MarketCatalog
{
    private readonly Lazy<Task<IReadOnlyList<MarketFamily>>> items = new(() => Task.Run(Load));
    public Task<IReadOnlyList<MarketFamily>> LoadAsync() => items.Value;
    private static JsonDocument Open(string name)
    {
        using var packed = typeof(MarketCatalog).Assembly.GetManifestResourceStream("ReferenceData." + name + ".json.gz")
            ?? throw new FileNotFoundException("The bundled item catalog is missing.");
        using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }
    private static IReadOnlyList<MarketFamily> Load()
    {
        using var data = Open("processed_items");
        var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var group in data.RootElement.EnumerateObject())
            foreach (var item in Many(group.Value))
                if (Text(item, "@uniquename") is { Length: > 0 } id) definitions.TryAdd(id, item);
        using var localized = Open("items");
        var variants = new List<(string Family, MarketItem Item)>();
        foreach (var entry in localized.RootElement.EnumerateArray())
        {
            var id = Text(entry, "UniqueName");
            var baseId = id.Split('@')[0];
            if (!definitions.TryGetValue(baseId, out var definition) || Text(definition, "@showinmarketplace") == "false") continue;
            var enchantment = id.Contains('@') && int.TryParse(id.Split('@')[1], out var level) ? level : Number(definition, "@enchantmentlevel");
            var variant = definition;
            if (enchantment > 0 && definition.TryGetProperty("enchantments", out var enchants) && enchants.TryGetProperty("enchantment", out var list))
                variant = Many(list).FirstOrDefault(x => Number(x, "@enchantmentlevel") == enchantment);
            var power = Number(variant, "@itempower");
            // Read actual per-variant IP: artifacts and special items don't all share the same tier formula.
            int? itemPower = power > 0 ? power : null;
            var names = entry.TryGetProperty("LocalizedNames", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
            string Name(string language) => names.ValueKind == JsonValueKind.Object ? Text(names, language) : "";
            var english = Name("EN-US");
            if (english.Length == 0) english = id;
            var family = baseId.Length > 3 && baseId[0] == 'T' && char.IsAsciiDigit(baseId[1]) && baseId[2] == '_' ? baseId[3..] : baseId;
            if (enchantment > 0 && family.EndsWith("_LEVEL" + enchantment, StringComparison.Ordinal)) family = family[..^("_LEVEL" + enchantment).Length];
            variants.Add((family, new(id, english, Name("RU-RU"), Number(definition, "@tier"), enchantment,
                Math.Clamp(Number(definition, "@maxqualitylevel", 1), 1, 5), itemPower,
                decimal.TryParse(Text(definition, "@masterymodifier"), NumberStyles.Any, CultureInfo.InvariantCulture, out var modifier) ? modifier : 0)));
        }
        return variants.GroupBy(x => x.Family).Select(group =>
        {
            var all = group.Select(x => x.Item).DistinctBy(x => x.ItemId).OrderBy(x => x.Tier).ThenBy(x => x.Enchantment).ToArray();
            var representative = all.FirstOrDefault(x => x.Tier == 4 && x.Enchantment == 0) ?? all[0];
            var name = group.Key switch
            {
                "WOOD" => "Logs", "ROCK" => "Stone", "ORE" => "Ore", "FIBER" => "Fiber", "HIDE" => "Hide",
                "PLANKS" => "Planks", "STONEBLOCK" => "Stone blocks", "METALBAR" => "Metal bars", "CLOTH" => "Cloth", "LEATHER" => "Leather",
                _ => representative.Name
            };
            foreach (var rank in new[] { "Beginner's ", "Novice's ", "Journeyman's ", "Adept's ", "Expert's ", "Master's ", "Grandmaster's ", "Elder's " })
                if (name.StartsWith(rank, StringComparison.Ordinal)) { name = name[rank.Length..]; break; }
            return new MarketFamily(group.Key, name, representative.RussianName,
                CraftingSearch.Normalize(string.Join(' ', all.Select(x => $"{x.Name} {x.RussianName} {x.ItemId} T{x.Tier}.{x.Enchantment}"))), all);
        }).OrderBy(x => x.Name).ToArray();
    }
    private static IEnumerable<JsonElement> Many(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray()
        : value.ValueKind == JsonValueKind.Object ? [value] : [];
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field.ToString() : "";
    private static int Number(JsonElement value, string name, int fallback = 0) => int.TryParse(Text(value, name), NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var number) ? number : fallback;
}
