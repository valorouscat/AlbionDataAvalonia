using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Crafting;

public sealed record CraftingIngredient(string ItemId, decimal Count, decimal? MaxReturnPerCraft, string? Name = null);
public sealed record CraftingRecipe(string Label, decimal OutputCount, decimal Silver, IReadOnlyList<CraftingIngredient> Ingredients);
public sealed record CraftableItem(string ItemId, string Name, string SearchText, string Category, int Tier, int Enchantment,
    int MaxQuality, IReadOnlyList<CraftingRecipe> Recipes, string RussianName = "")
{
    public bool HasRussianName => !string.IsNullOrWhiteSpace(RussianName);
    public string Label => $"{Name} · T{Tier}.{Enchantment}";
}
public sealed record CraftingInput(CraftingIngredient Ingredient, decimal? UnitPrice);
public sealed record CraftingMaterialCost(string ItemId, decimal Required, decimal Returned, decimal Consumed, decimal? UnitPrice)
{
    public decimal? Cost => UnitPrice * Consumed;
}
public sealed record CraftingResult(decimal Crafts, decimal Output, IReadOnlyList<CraftingMaterialCost> Materials,
    decimal? UpfrontCost, decimal? EffectiveCost, decimal? NetRevenue, decimal? Profit, decimal? BreakEvenPrice)
{
    public decimal? CostPerItem => EffectiveCost / Output;
    public decimal? Roi => EffectiveCost > 0 ? Profit / EffectiveCost * 100 : null;
}

public static class CraftingCalculator
{
    public static CraftingResult Calculate(CraftingRecipe recipe, decimal desiredOutput, decimal returnPercent,
        decimal stationFeePerCraft, decimal salesTaxPercent, decimal setupFeePercent, decimal? salePrice,
        IReadOnlyList<CraftingInput> inputs, decimal buySetupFeePercent = 0)
    {
        if (recipe.OutputCount <= 0 || desiredOutput is <= 0 or > 1_000_000 || returnPercent is < 0 or >= 100
            || stationFeePerCraft < 0 || salesTaxPercent < 0 || setupFeePercent < 0 || salesTaxPercent + setupFeePercent >= 100
            || buySetupFeePercent is < 0 or >= 100 || salePrice < 0 || inputs.Any(x => x.UnitPrice < 0) || inputs.Count != recipe.Ingredients.Count
            || inputs.Where((x, i) => x.Ingredient != recipe.Ingredients[i]).Any())
            throw new ArgumentOutOfRangeException(nameof(desiredOutput), "Check quantities, prices and percentage values.");
        var crafts = Math.Ceiling(desiredOutput / recipe.OutputCount);
        var output = crafts * recipe.OutputCount;
        var materials = inputs.Select(x =>
        {
            var required = x.Ingredient.Count * crafts;
            var returned = Math.Min(required * returnPercent / 100, x.Ingredient.MaxReturnPerCraft is { } cap ? cap * crafts : required);
            return new CraftingMaterialCost(x.Ingredient.ItemId, required, returned, required - returned, x.UnitPrice);
        }).ToArray();
        var fees = (recipe.Silver + stationFeePerCraft) * crafts;
        if (materials.All(x => x.UnitPrice.HasValue)) fees += materials.Sum(x => x.Required * x.UnitPrice!.Value) * buySetupFeePercent / 100;
        var priced = materials.All(x => x.UnitPrice.HasValue);
        decimal? upfront = priced ? materials.Sum(x => x.Required * x.UnitPrice!.Value) + fees : null;
        decimal? effective = priced ? materials.Sum(x => x.Cost!.Value) + fees : null;
        var saleFactor = 1 - (salesTaxPercent + setupFeePercent) / 100;
        var revenue = salePrice * output * saleFactor;
        return new(crafts, output, materials, upfront, effective, revenue, revenue - effective, effective / (output * saleFactor));
    }
}
