using AlbionDataAvalonia.Crafting;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class CraftingMaterialViewModel(CraftingIngredient ingredient, string name) : ObservableObject
{
    public CraftingIngredient Ingredient { get; } = ingredient;
    public string ItemId => Ingredient.ItemId;
    public string Name { get; } = name;
    [ObservableProperty] private decimal? unitPrice;
    [ObservableProperty] private string source = "No price";
    [ObservableProperty] private string requiredText = "";
    [ObservableProperty] private string returnedText = "";
    [ObservableProperty] private string consumedText = "";
    [ObservableProperty] private string costText = UiText.None;
    public bool IsManual { get; private set; }
    private bool applying;
    partial void OnUnitPriceChanged(decimal? value)
    {
        if (!applying) { IsManual = true; Source = "Manual price"; }
    }
    public void ApplyPrice(decimal? price, string source)
    {
        if (IsManual) return;
        applying = true;
        try { UnitPrice = price; Source = source; }
        finally { applying = false; }
    }
    public void ResetPrice() { IsManual = false; ApplyPrice(null, "No price"); }
    public void Update(CraftingMaterialCost value)
    {
        RequiredText = UiFormat.Decimal(value.Required);
        ReturnedText = UiFormat.Decimal(value.Returned);
        ConsumedText = UiFormat.Decimal(value.Consumed);
        CostText = value.Cost is { } cost ? UiFormat.Decimal(cost) : UiText.None;
    }
}
