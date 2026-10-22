using AlbionDataAvalonia.Crafting;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class CraftingViewModel : ViewModelBase
{
    private readonly CraftingCatalog catalog;
    private readonly CraftingPriceService prices;
    private readonly UiDebouncer searchDelay;
    private readonly UiDebouncer priceDelay;
    private readonly LatestLoad loads = new();
    private IReadOnlyList<CraftableItem> allItems = [];
    private CancellationTokenSource? priceCancellation;
    private bool active, initializing, applyingSale, manualSale;
    public CraftingViewModel(CraftingCatalog catalog, CraftingPriceService prices)
    {
        this.catalog = catalog; this.prices = prices;
        searchDelay = new(TimeSpan.FromMilliseconds(150), FilterItems);
        priceDelay = new(TimeSpan.FromMilliseconds(250), () => _ = LoadPrices(false));
    }

    public IReadOnlyList<string> Regions { get; } = ["Europe", "Americas", "Asia"];
    public IReadOnlyList<string> Cities { get; } = ["Bridgewatch", "Caerleon", "Fort Sterling", "Lymhurst", "Martlock", "Thetford", "Brecilien"];
    public IReadOnlyList<string> Categories { get; } = ["All recipes", "Refining", "Equipment", "Consumables", "Other crafting"];
    public IReadOnlyList<Option<int>> Qualities { get; } = [new(1, "Normal"), new(2, "Good"), new(3, "Outstanding"), new(4, "Excellent"), new(5, "Masterpiece")];
    public IReadOnlyList<string> BuyModes { get; } = ["Buy instantly", "Buy orders"];
    public IReadOnlyList<string> SellModes { get; } = ["Sell orders", "Sell instantly"];
    public ReplaceableCollection<CraftableItem> Items { get; } = new();
    public ReplaceableCollection<CraftingMaterialViewModel> Materials { get; } = new();
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string selectedCategory = "All recipes";
    [ObservableProperty] private string selectedRegion = "Europe";
    [ObservableProperty] private string selectedCity = "Martlock";
    [ObservableProperty] private string selectedBuyMode = "Buy instantly";
    [ObservableProperty] private string selectedSellMode = "Sell orders";
    [ObservableProperty] private Option<int> selectedQuality = new(1, "Normal");
    [ObservableProperty] private CraftableItem? selectedItem;
    [ObservableProperty] private CraftingRecipe? selectedRecipe;
    [ObservableProperty] private IReadOnlyList<CraftingRecipe> recipes = [];
    [ObservableProperty] private decimal? quantity = 100;
    [ObservableProperty] private decimal? returnPercent = 0;
    [ObservableProperty] private decimal? stationFee = 0;
    [ObservableProperty] private decimal? salesTax = 4;
    [ObservableProperty] private decimal? setupFee = 2.5m;
    [ObservableProperty] private decimal? buySetupFee = 2.5m;
    [ObservableProperty] private decimal? salePrice;
    [ObservableProperty] private string saleSource = "No price";
    [ObservableProperty] private string catalogStatus = "Loading recipes…";
    [ObservableProperty] private string priceStatus = "Prices: Albion Online Data Project. Select an item to load a snapshot.";
    [ObservableProperty] private string calculationStatus = "Select a recipe.";
    [ObservableProperty] private bool isLoadingPrices;
    [ObservableProperty] private string upfrontText = UiText.None;
    [ObservableProperty] private string costText = UiText.None;
    [ObservableProperty] private string revenueText = UiText.None;
    [ObservableProperty] private string profitText = UiText.None;
    [ObservableProperty] private string breakEvenText = UiText.None;
    [ObservableProperty] private string roiText = UiText.None;
    [ObservableProperty] private bool isLoss;
    public bool HasSelection => SelectedItem is not null;
    public bool CanChooseQuality => SelectedItem?.MaxQuality > 1;

    public async void Activate()
    {
        active = true;
        if (allItems.Count > 0) { QueuePrices(); return; }
        if (initializing) return;
        initializing = true;
        try
        {
            allItems = await catalog.LoadAsync();
            FilterItems();
            if (SelectedItem is null) SelectedItem = allItems.FirstOrDefault(x => x.ItemId == "T4_STONEBLOCK");
        }
        catch (Exception failure) { Log.Warning(failure, "Could not load crafting recipes"); CatalogStatus = "Couldn't load the bundled crafting recipes. Check the application installation."; }
        finally { initializing = false; }
    }
    public void Deactivate()
    {
        active = false; searchDelay.Cancel(); priceDelay.Cancel(); loads.Invalidate();
        priceCancellation?.Cancel(); IsLoadingPrices = false;
    }
    private void FilterItems()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = allItems.Where(x => (SelectedCategory == "All recipes" || x.Category == SelectedCategory)
            && terms.All(t => x.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        var selection = SelectedItem;
        Items.ReplaceAll(matches.Take(150));
        if (selection is not null && Items.Contains(selection)) SelectedItem = selection;
        CatalogStatus = $"{UiFormat.Whole(matches.Length)} recipes · showing up to 150. Search by English/Russian name or item ID.";
    }
    partial void OnSearchTextChanged(string value) => searchDelay.Trigger();
    partial void OnSelectedCategoryChanged(string value) => searchDelay.Trigger();
    partial void OnSelectedItemChanged(CraftableItem? value)
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(CanChooseQuality));
        Recipes = value?.Recipes ?? []; SelectedRecipe = Recipes.FirstOrDefault();
        if (value?.MaxQuality <= 1) SelectedQuality = Qualities[0];
    }
    partial void OnSelectedRecipeChanged(CraftingRecipe? value)
    {
        foreach (var row in Materials) row.PropertyChanged -= MaterialChanged;
        Materials.ReplaceAll(value?.Ingredients.Select(x => new CraftingMaterialViewModel(x, x.Name ?? x.ItemId)) ?? []);
        foreach (var row in Materials) row.PropertyChanged += MaterialChanged;
        ResetSale(); Recalculate(); QueuePrices();
    }
    partial void OnSelectedRegionChanged(string value) => MarketChanged();
    partial void OnSelectedCityChanged(string value) => MarketChanged();
    partial void OnSelectedQualityChanged(Option<int> value) => MarketChanged();
    partial void OnSelectedBuyModeChanged(string value) => MarketChanged();
    partial void OnSelectedSellModeChanged(string value) { ResetSale(); Recalculate(); QueuePrices(); }
    private void MarketChanged()
    {
        foreach (var row in Materials) row.ApplyPrice(null, "No price for this market");
        ResetSale(); Recalculate(); QueuePrices();
    }
    private void ResetSale()
    {
        manualSale = false; applyingSale = true; SalePrice = null; applyingSale = false; SaleSource = "No price";
    }
    partial void OnSalePriceChanged(decimal? value)
    {
        if (!applyingSale) { manualSale = true; SaleSource = "Manual price"; }
        Recalculate();
    }
    partial void OnQuantityChanged(decimal? value) => Recalculate();
    partial void OnReturnPercentChanged(decimal? value) => Recalculate();
    partial void OnStationFeeChanged(decimal? value) => Recalculate();
    partial void OnSalesTaxChanged(decimal? value) => Recalculate();
    partial void OnSetupFeeChanged(decimal? value) => Recalculate();
    partial void OnBuySetupFeeChanged(decimal? value) => Recalculate();
    private void MaterialChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(CraftingMaterialViewModel.UnitPrice)) Recalculate(); }
    private void QueuePrices()
    {
        loads.Invalidate(); priceCancellation?.Cancel(); IsLoadingPrices = false;
        if (active && SelectedItem is not null) priceDelay.Trigger();
    }
    [RelayCommand] private Task RefreshPrices() { priceDelay.Cancel(); return LoadPrices(true); }
    [RelayCommand]
    private Task UseMarketPrices()
    {
        foreach (var row in Materials) row.ResetPrice();
        ResetSale(); Recalculate(); return RefreshPrices();
    }
    private async Task LoadPrices(bool refresh)
    {
        if (!active || SelectedItem is not { } item || SelectedRecipe is null) return;
        var ticket = loads.Begin(); priceCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); priceCancellation = cancellation;
        var rows = Materials.ToArray(); var buyInstant = SelectedBuyMode == BuyModes[0]; var sellOrders = SelectedSellMode == SellModes[0];
        var quality = CanChooseQuality ? SelectedQuality.Value : 1;
        IsLoadingPrices = true; PriceStatus = "Loading AODP market snapshot…";
        try
        {
            var quotes = await prices.GetAsync(SelectedRegion, SelectedCity, rows.Select(x => x.ItemId).Append(item.ItemId), quality, refresh, cancellation.Token);
            if (!loads.IsCurrent(ticket)) return;
            foreach (var row in rows)
            {
                var quote = quotes.FirstOrDefault(x => x.ItemId == row.ItemId && x.Quality == 1);
                var price = buyInstant ? quote?.SellPrice : quote?.BuyPrice;
                var date = buyInstant ? quote?.SellDate : quote?.BuyDate;
                row.ApplyPrice(price > 0 ? price : null, Source(price, date));
            }
            if (!manualSale)
            {
                var quote = quotes.FirstOrDefault(x => x.ItemId == item.ItemId && x.Quality == quality);
                var price = sellOrders ? quote?.SellPrice : quote?.BuyPrice;
                var date = sellOrders ? quote?.SellDate : quote?.BuyDate;
                applyingSale = true; SalePrice = price > 0 ? price : null; applyingSale = false; SaleSource = Source(price, date);
            }
            PriceStatus = "AODP snapshot loaded. Prices may be stale or unavailable; edit unit prices to override. Refresh preserves manual prices.";
            Recalculate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!loads.IsCurrent(ticket)) return;
            Log.Warning(failure, "Could not load crafting market prices");
            PriceStatus = "Couldn't load AODP prices. Check the connection, refresh, or enter prices manually. Existing prices are retained.";
        }
        finally
        {
            if (ReferenceEquals(priceCancellation, cancellation)) priceCancellation = null;
            if (loads.IsCurrent(ticket)) IsLoadingPrices = false;
        }
    }
    private static string Source(long? price, DateTime? date) => price is not > 0 ? "No market price"
        : date is not { } at ? "AODP · time unknown" : $"AODP · {UiFormat.Ago(at, DateTime.UtcNow)}{(DateTime.UtcNow - at > TimeSpan.FromHours(24) ? " · stale" : "")}";
    private void Recalculate()
    {
        UpfrontText = CostText = RevenueText = ProfitText = BreakEvenText = RoiText = UiText.None; IsLoss = false;
        if (SelectedRecipe is not { } recipe) { CalculationStatus = "Select a recipe."; return; }
        if (Quantity is null || ReturnPercent is null || StationFee is null || SalesTax is null || SetupFee is null || BuySetupFee is null)
        { CalculationStatus = "Fill in quantity, return rate and fees."; return; }
        try
        {
            var result = CraftingCalculator.Calculate(recipe, Quantity.Value, ReturnPercent.Value, StationFee.Value, SalesTax.Value,
                SelectedSellMode == SellModes[0] ? SetupFee.Value : 0, SalePrice, Materials.Select(x => new CraftingInput(x.Ingredient, x.UnitPrice)).ToArray(),
                SelectedBuyMode == BuyModes[1] ? BuySetupFee.Value : 0);
            for (var i = 0; i < Materials.Count; i++) Materials[i].Update(result.Materials[i]);
            static string Money(decimal? n) => n is { } value ? UiFormat.Decimal(value) : UiText.None;
            UpfrontText = Money(result.UpfrontCost); CostText = Money(result.CostPerItem); RevenueText = Money(result.NetRevenue);
            ProfitText = Money(result.Profit); BreakEvenText = Money(result.BreakEvenPrice); RoiText = result.Roi is { } roi ? UiFormat.Decimal(roi) + "%" : UiText.None;
            IsLoss = result.Profit < 0;
            var missing = Materials.Count(x => x.UnitPrice is null) + (SalePrice is null ? 1 : 0);
            CalculationStatus = $"{UiFormat.Whole((double)result.Crafts)} crafts → {UiFormat.Whole((double)result.Output)} items. "
                + (missing > 0 ? $"{missing} prices missing; complete them to calculate profit. " : "")
                + "Returns are an expected value credited at the input price. Bring the full required materials; returned materials are not recrafted.";
        }
        catch (ArgumentOutOfRangeException) { CalculationStatus = "Use a positive quantity, nonnegative prices/fees, return below 100%, and combined selling fees below 100%."; }
    }
}
