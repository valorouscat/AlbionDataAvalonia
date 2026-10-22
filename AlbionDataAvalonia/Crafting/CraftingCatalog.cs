using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Crafting;

/// <summary>Reads the game's bundled recipe snapshot, including alternate recipes and enchantments, on first use.</summary>
public sealed class CraftingCatalog
{
    private readonly Lazy<Task<IReadOnlyList<CraftableItem>>> items = new(() => Task.Run(Load));
    public Task<IReadOnlyList<CraftableItem>> LoadAsync() => items.Value;

    private static JsonDocument Open(string file)
    {
        using var packed = typeof(CraftingCatalog).Assembly.GetManifestResourceStream("ReferenceData." + file + ".gz")
            ?? throw new FileNotFoundException("The bundled crafting catalog is missing.");
        using var json = new GZipStream(packed, CompressionMode.Decompress);
        return JsonDocument.Parse(json);
    }

    private static IReadOnlyList<CraftableItem> Load()
    {
        using var namesJson = Open("items.json");
        var names = namesJson.RootElement.EnumerateArray().Where(x => x.TryGetProperty("UniqueName", out _))
            .GroupBy(x => x.GetProperty("UniqueName").GetString()!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x =>
            {
                var item = x.First();
                var localized = item.TryGetProperty("LocalizedNames", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
                string Name(string language) => localized.ValueKind == JsonValueKind.Object && localized.TryGetProperty(language, out var name)
                    ? name.GetString() ?? x.Key : x.Key;
                return (English: Name("EN-US"), Russian: Name("RU-RU"));
            }, StringComparer.OrdinalIgnoreCase);
        string Display(string id) => names.TryGetValue(id, out var n) ? n.English : id;
        using var data = Open("processed_items.json");
        var result = new List<CraftableItem>();
        foreach (var group in data.RootElement.EnumerateObject())
            foreach (var item in Many(group.Value))
            {
                var id = Text(item, "@uniquename");
                if (id.Length == 0) continue;
                Add(item, (int)Number(item, "@enchantmentlevel"));
                if (item.TryGetProperty("enchantments", out var enchantments) && enchantments.TryGetProperty("enchantment", out var variants))
                    foreach (var variant in Many(variants)) Add(variant, (int)Number(variant, "@enchantmentlevel"));

                void Add(JsonElement variant, int enchantment)
                {
                    if (!variant.TryGetProperty("craftingrequirements", out var raw)) return;
                    var recipes = new List<CraftingRecipe>();
                    foreach (var req in Many(raw))
                    {
                        // Exchanges, currencies, standing gates and product-return recipes need a different cost model.
                        if (req.TryGetProperty("currency", out _) || req.TryGetProperty("standing", out _)
                            || req.TryGetProperty("playerfactionstanding", out _) || req.TryGetProperty("@swaptransaction", out _)
                            || Number(req, "@gold") > 0 || Text(req, "@returnproductnotresource") == "true"
                            || !req.TryGetProperty("craftresource", out var resources)) continue;
                        var ingredients = Many(resources).Select(x => new CraftingIngredient(
                            MarketId(Text(x, "@uniquename"), (int)Number(x, "@enchantmentlevel")), Number(x, "@count"),
                            x.TryGetProperty("@maxreturnamount", out _) ? Number(x, "@maxreturnamount") : null,
                            Display(MarketId(Text(x, "@uniquename"), (int)Number(x, "@enchantmentlevel"))))).ToArray();
                        if (ingredients.Length == 0 || ingredients.Any(x => x.ItemId.Length == 0 || x.Count <= 0)) continue;
                        var output = Number(req, "@amountcrafted", 1);
                        if (output <= 0) continue;
                        var label = string.Join(" + ", ingredients.Select(x => $"{x.Count:0.##} × {Display(x.ItemId)}")) + $" → {output:0.##}";
                        recipes.Add(new(label, output, Number(req, "@silver"), ingredients));
                    }
                    if (recipes.Count == 0) return;
                    var marketId = MarketId(id, enchantment);
                    var name = Display(marketId);
                    var category = Text(item, "@shopsubcategory1") == "refinedresources" ? "Refining"
                        : group.Name is "weapon" or "equipmentitem" ? "Equipment"
                        : group.Name is "consumableitem" or "consumablefrominventoryitem" ? "Consumables" : "Other crafting";
                    var russian = names.TryGetValue(marketId, out var n) ? n.Russian
                        : names.TryGetValue(id, out var baseName) ? baseName.Russian : "";
                    if ((string.IsNullOrWhiteSpace(russian) || russian == marketId) && names.TryGetValue(id, out var baseLocalized))
                        russian = baseLocalized.Russian;
                    if (russian == marketId || russian == id) russian = "";
                    var tier = (int)Number(item, "@tier");
                    var search = CraftingSearch.Normalize($"{name} {marketId} {russian} T{tier}.{enchantment}");
                    result.Add(new(marketId, name, search, category, tier, enchantment,
                        (int)Number(item, "@maxqualitylevel", 1), recipes, russian));
                }
            }
        if (result.Count == 0) throw new InvalidDataException("No supported crafting recipes were found.");
        return result.GroupBy(x => x.ItemId).Select(x => x.First()).OrderBy(x => x.Name).ThenBy(x => x.Tier).ThenBy(x => x.Enchantment).ToArray();
    }

    private static string MarketId(string id, int enchantment) => enchantment > 0 && !id.Contains('@') ? id + "@" + enchantment : id;
    private static IEnumerable<JsonElement> Many(JsonElement element) => element.ValueKind == JsonValueKind.Array
        ? element.EnumerateArray() : element.ValueKind == JsonValueKind.Object ? new[] { element } : [];
    private static string Text(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value)
        ? value.ToString() : "";
    private static decimal Number(JsonElement item, string name, decimal fallback = 0) => decimal.TryParse(Text(item, name),
        System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;
}
