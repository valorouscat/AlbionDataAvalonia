using AlbionDataAvalonia.Crafting;
using AlbionDataAvalonia.Market;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class MarketViewModel : ViewModelBase
{
    private readonly MarketCatalog catalog;
    private readonly CraftingPriceService prices;
    private readonly CharacterMasteryService mastery;
    private readonly UiDebouncer searchDelay;
    private readonly UiDebouncer priceDelay;
    private readonly LatestLoad loads = new();
    private bool updatingSearchItems;
    private IReadOnlyList<MarketFamily> allItems = [];
    private IReadOnlyList<MarketQuote> quotes = [];
    private CancellationTokenSource? priceCancellation;
    private bool active, initializing;
    public MarketViewModel(MarketCatalog catalog, CraftingPriceService prices, CharacterMasteryService mastery)
    {
        this.catalog = catalog; this.prices = prices; this.mastery = mastery;
        CityFilters = [.. Cities.Select(x => new MarketCityViewModel(x, true, Recalculate)), new("Black Market", false, Recalculate)];
        searchDelay = new(TimeSpan.FromMilliseconds(150), FilterItems);
        priceDelay = new(TimeSpan.FromMilliseconds(300), () => _ = LoadPrices(false));
    }
    public IReadOnlyList<Option<string>> Regions { get; } = [new("Europe", "Europe"), new("Americas", "Americas"), new("Asia", "Asia")];
    public static IReadOnlyList<string> Cities { get; } = ["Bridgewatch", "Caerleon", "Fort Sterling", "Lymhurst", "Martlock", "Thetford", "Brecilien"];
    public IReadOnlyList<MarketCityViewModel> CityFilters { get; }
    private static IReadOnlyList<string> PriceCities { get; } = [.. Cities, "Black Market"];
    public IReadOnlyList<Option<int>> Intervals { get; } = [new(25, "25 IP"), new(50, "50 IP"), new(100, "100 IP")];
    public IReadOnlyList<Option<MarketResultOrder>> SortOrders { get; } = [new(MarketResultOrder.ItemPower, "Item power"), new(MarketResultOrder.TotalPrice, "Total price"), new(MarketResultOrder.PricePerPower, "Price / IP")];
    public IReadOnlyList<Option<bool>> ResultViews { get; } = [new(true, "IP ranges"), new(false, "By city")];
    public ReplaceableCollection<MarketPowerCardViewModel> PowerCards { get; } = new();
    [ObservableProperty] private Option<int> selectedInterval = new(50, "50 IP");
    [ObservableProperty] private Option<MarketResultOrder> selectedSortOrder = new(MarketResultOrder.ItemPower, "Item power");
    [ObservableProperty] private Option<bool> selectedResultView = new(true, "IP ranges");
    public bool ShowPowerCards => HasPower && SelectedResultView.Value;
    public bool ShowCityResults => !ShowPowerCards;
    public bool HasPowerCards => PowerCards.Count > 0;
    partial void OnSelectedIntervalChanged(Option<int> value) => Recalculate();
    partial void OnSelectedSortOrderChanged(Option<MarketResultOrder> value) => Recalculate();
    partial void OnSelectedResultViewChanged(Option<bool> value) { OnPropertyChanged(nameof(ShowPowerCards)); OnPropertyChanged(nameof(ShowCityResults)); }
    public IReadOnlyList<Option<MarketAction>> Actions { get; } = [new(MarketAction.BuyInstantly, "Buy instantly"), new(MarketAction.BuyOrder, "Place buy orders"),
        new(MarketAction.SellInstantly, "Sell instantly"), new(MarketAction.SellOrder, "Place sell orders")];
    public IReadOnlyList<Option<int>> Tiers => UiOptions.ItemTiers;
    public IReadOnlyList<Option<int>> Enchantments => UiOptions.ItemEnchantments;
    public IReadOnlyList<Option<int>> Qualities => UiOptions.ItemQualities;
    public IReadOnlyList<Option<int>> Ages { get; } = [new(1, "1 hour"), new(6, "6 hours"), new(24, "24 hours"), new(72, "3 days"), new(0, "Any age / unknown")];
    public ReplaceableCollection<MarketFamily> Items { get; } = new();
    public ReplaceableCollection<MarketRowViewModel> Results { get; } = new();
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private Option<string> selectedRegion = new("Europe", "Europe");
    [ObservableProperty] private Option<MarketAction> selectedAction = new(MarketAction.BuyInstantly, "Buy instantly");
    [ObservableProperty] private Option<int> selectedTier = new(0, "Any tier");
    [ObservableProperty] private Option<int> selectedEnchantment = new(-1, "Any enchantment");
    [ObservableProperty] private Option<int> selectedQuality = new(0, "Any quality");
    [ObservableProperty] private Option<int> selectedAge = new(24, "24 hours");
    [ObservableProperty] private MarketFamily? selectedItem;
    [ObservableProperty] private decimal? quantity = 1;
    [ObservableProperty] private decimal? minimumPower = 0;
    [ObservableProperty] private bool personalPower;
    [ObservableProperty] private decimal? skillBonus = 0;
    [ObservableProperty] private bool manualSkills;
    [ObservableProperty] private IReadOnlyList<Option<string>> profiles = [];
    [ObservableProperty] private Option<string>? selectedProfile;
    [ObservableProperty] private string profileStatus = "Join a character with capture enabled to receive its Destiny Board levels.";
    [ObservableProperty] private decimal? salesTax = 4;
    [ObservableProperty] private decimal? setupFee = 2.5m;
    [ObservableProperty] private bool isLoadingPrices;
    [ObservableProperty] private string catalogStatus = "Loading item catalog…";
    [ObservableProperty] private string priceStatus = "Select an item to compare AODP prices across cities.";
    [ObservableProperty] private string calculationStatus = "";
    [ObservableProperty] private string bestCity = UiText.None;
    [ObservableProperty] private string bestTotal = UiText.None;
    [ObservableProperty] private string bestItem = "";
    public bool HasSelection => SelectedItem is not null;
    public bool HasPower => SelectedItem?.HasPower == true;
    public bool HasQuality => SelectedItem?.HasQuality == true;
    public bool ShowCharacterOptions => HasPower && PersonalPower;
    public bool ShowSkillBonus => HasPower && PersonalPower && ManualSkills;
    public bool ShowProfilePicker => HasPower && PersonalPower && !ManualSkills;
    public string TotalLabel => MarketCalculator.IsBuying(SelectedAction.Value) ? "Lowest total cost" : "Highest net proceeds";
    public string PriceHint => SelectedAction.Value switch
    {
        MarketAction.BuyInstantly => "Buy from the lowest sell listing. No buyer tax or order setup fee.",
        MarketAction.BuyOrder => "Match the highest buy order, including setup fee. Filling the order is not guaranteed.",
        MarketAction.SellInstantly => "Sell to the highest buy order, after sales tax.",
        _ => "Match the lowest sell listing, after sales tax and setup fee. Filling the order is not guaranteed."
    };
    public async void Activate()
    {
        if (!active) mastery.Changed += MasteryChanged;
        active = true;
        RefreshProfiles();
        if (allItems.Count > 0) { InitializeItems(); QueuePrices(); return; }
        if (initializing) return;
        initializing = true;
        try
        {
            await mastery.InitializeAsync();
            allItems = await catalog.LoadAsync();
            if (!active) return;
            InitializeItems();
        }
        catch (Exception failure) { Log.Warning(failure, "Could not load market item catalog"); CatalogStatus = "Couldn't load the bundled item catalog. Check the installation."; }
        finally { initializing = false; }
    }
    private void InitializeItems()
    {
        var preferred = allItems.FirstOrDefault(x => x.Key == "MAIN_SWORD");
        if (SelectedItem is null && preferred is not null && MatchesSearch(preferred, CraftingSearch.Terms(SearchText))) SelectedItem = preferred;
        FilterItems();
        SelectedItem ??= Items.FirstOrDefault();
    }
    public void Deactivate()
    {
        mastery.Changed -= MasteryChanged;
        active = false; searchDelay.Cancel(); priceDelay.Cancel(); loads.Invalidate();
        priceCancellation?.Cancel(); IsLoadingPrices = false;
    }
    private void FilterItems()
    {
        var terms = CraftingSearch.Terms(SearchText);
        var previous = Items.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var found = allItems.Select(family =>
        {
            var variants = MatchingVariants(family, terms);
            if (variants.Length == 0) return null;
            var representative = variants.FirstOrDefault(x => x.Tier == 4 && x.Enchantment == 0) ?? variants[0];
            var name = variants.Length == 1 ? representative.Label : family.Name;
            var row = family with { Name = name, RussianName = representative.RussianName, Variants = variants, DisplayItemId = representative.ItemId };
            return previous.TryGetValue(family.Key, out var old) && old.Name == row.Name && old.RussianName == row.RussianName
                && old.DisplayItemId == row.DisplayItemId && old.Variants.SequenceEqual(variants) ? old : row;
        }).OfType<MarketFamily>().ToArray();
        var before = SelectedItem;
        var selected = found.FirstOrDefault(x => x.Key == before?.Key);
        var visible = found.Take(150).ToList();
        if (selected is not null && found.Contains(selected) && !visible.Contains(selected)) visible[^1] = selected;
        // A ListBox can temporarily clear selection during Reset. Keep dependent inputs until the final selection is known.
        updatingSearchItems = true;
        try
        {
            if (!Items.SequenceEqual(visible)) Items.ReplaceAll(visible);
            SelectedItem = selected is not null && visible.Contains(selected) ? selected : null;
        }
        finally { updatingSearchItems = false; }
        if (SelectedItem != before) OnSelectedItemChanged(SelectedItem);
        CatalogStatus = $"{UiFormat.Whole(found.Length)} item families · up to 150 shown. Search in English, Russian or by ID.";
    }
    partial void OnSearchTextChanged(string value) => searchDelay.Trigger();
    partial void OnSelectedItemChanged(MarketFamily? value)
    {
        if (updatingSearchItems) return;
        OnPropertyChanged(nameof(ShowPowerCards)); OnPropertyChanged(nameof(ShowCityResults));
        OnPropertyChanged(nameof(ShowCharacterOptions));
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(HasPower)); OnPropertyChanged(nameof(HasQuality)); OnPropertyChanged(nameof(ShowSkillBonus)); OnPropertyChanged(nameof(ShowProfilePicker));
        QueuePrices();
    }
    partial void OnSelectedRegionChanged(Option<string> value) { RefreshProfiles(); QueuePrices(); }
    partial void OnSelectedTierChanged(Option<int> value) { FilterItems(); QueuePrices(); }
    partial void OnSelectedEnchantmentChanged(Option<int> value) { FilterItems(); QueuePrices(); }
    partial void OnSelectedQualityChanged(Option<int> value) { FilterItems(); QueuePrices(); }
    partial void OnMinimumPowerChanged(decimal? value) => Recalculate();
    partial void OnPersonalPowerChanged(bool value) { OnPropertyChanged(nameof(ShowSkillBonus)); OnPropertyChanged(nameof(ShowProfilePicker)); OnPropertyChanged(nameof(ShowCharacterOptions)); Recalculate(); }
    partial void OnManualSkillsChanged(bool value) { OnPropertyChanged(nameof(ShowSkillBonus)); OnPropertyChanged(nameof(ShowProfilePicker)); Recalculate(); }
    partial void OnSelectedProfileChanged(Option<string>? value) { RefreshProfileStatus(); Recalculate(); }
    private void MasteryChanged()
    {
        if (UiDispatch.Repost(MasteryChanged)) return;
        if (!active) return;
        RefreshProfiles(); Recalculate();
    }
    private void RefreshProfiles()
    {
        var previous = SelectedProfile?.Value;
        Profiles = mastery.Profiles(SelectedRegion.Value).Select(x => new Option<string>(x.Key, x.Character)).ToArray();
        SelectedProfile = Profiles.FirstOrDefault(x => x.Value == previous) ?? Profiles.FirstOrDefault();
        RefreshProfileStatus();
    }
    private void RefreshProfileStatus()
    {
        ProfileStatus = SelectedProfile is { } selected && mastery.Find(selected.Value) is { } profile
            ? $"{profile.Character} · {profile.Region} · Destiny Board captured {UiFormat.Ago(profile.CapturedAtUtc, DateTime.UtcNow)}. Rejoin the game to refresh levels."
            : "Join a character with capture enabled to receive its Destiny Board levels, or enter a bonus manually.";
    }
    private decimal? ItemPower(MarketItem item, int quality)
    {
        if (!PersonalPower) return item.Power(quality);
        var bonus = ManualSkills ? SkillBonus ?? 0 : SelectedProfile is { } selected && mastery.Find(selected.Value) is { } profile ? mastery.Bonus(profile, item) : 0;
        return item.EffectivePower(quality, true, bonus);
    }
    partial void OnSkillBonusChanged(decimal? value) => Recalculate();
    partial void OnSelectedAgeChanged(Option<int> value) => Recalculate();
    partial void OnQuantityChanged(decimal? value) => Recalculate();
    partial void OnSalesTaxChanged(decimal? value) => Recalculate();
    partial void OnSetupFeeChanged(decimal? value) => Recalculate();
    partial void OnSelectedActionChanged(Option<MarketAction> value)
    {
        OnPropertyChanged(nameof(TotalLabel)); OnPropertyChanged(nameof(PriceHint)); Recalculate();
    }
    private MarketItem[] MatchingVariants(MarketFamily family, string[] terms) => family.Variants.Where(item =>
    {
        if (!MatchesSearchFilters(item)) return false;
        if (terms.Length == 0) return true;
        var text = CraftingSearch.Normalize($"{item.Name} {item.RussianName} {item.ItemId} T{item.Tier}.{item.Enchantment} {family.Key} {family.Name}");
        return terms.All(term => text.Contains(term, StringComparison.Ordinal));
    }).ToArray();
    private bool MatchesSearch(MarketFamily item, string[] terms) => MatchingVariants(item, terms).Length > 0;
    private bool MatchesSearchFilters(MarketItem item) => (SelectedTier.Value == 0 || item.Tier == SelectedTier.Value)
        && (SelectedEnchantment.Value < 0 || item.Enchantment == SelectedEnchantment.Value)
        && (SelectedQuality.Value == 0 || item.MaxQuality >= SelectedQuality.Value);
    // Fetch the variant/quality set once. IP, cities, fees, quantity and grouping only filter those snapshots locally.
    private MarketCandidate[] PriceCandidates() => SelectedItem is null ? [] : SelectedItem.Variants.Where(MatchesSearchFilters)
        .SelectMany(x => Enumerable.Range(1, x.MaxQuality).Where(q => !HasQuality || SelectedQuality.Value == 0 || q == SelectedQuality.Value)
            .Select(q => new MarketCandidate(x, q))).ToArray();
    private MarketCandidate[] Candidates() => !PowerValid() ? [] : PriceCandidates()
        .Select(x => x with { ItemPower = HasPower ? ItemPower(x.Item, x.Quality) : null })
        .Where(x => !HasPower || x.ItemPower >= MinimumPower).ToArray();
    private bool PowerValid() => !HasPower || MinimumPower is >= 0 and <= 5000 && (!PersonalPower || (ManualSkills ? SkillBonus is >= 0 and <= 5000 : SelectedProfile is { } selected && mastery.Find(selected.Value) is not null));
    private void QueuePrices()
    {
        loads.Invalidate(); priceCancellation?.Cancel(); priceDelay.Cancel(); IsLoadingPrices = false;
        quotes = []; Recalculate();
        PriceStatus = "Select an item to compare prices.";
        if (active && SelectedItem is not null) priceDelay.Trigger();
    }
    [RelayCommand] private Task RefreshPrices() { priceDelay.Cancel(); return LoadPrices(true); }
    private async Task LoadPrices(bool refresh)
    {
        if (!active || SelectedItem is null) return;
        var candidates = PriceCandidates();
        if (candidates.Length == 0) { PriceStatus = "No variants match the selected filters."; return; }
        var ticket = loads.Begin(); priceCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); priceCancellation = cancellation;
        IsLoadingPrices = true; PriceStatus = "Loading AODP market snapshots…";
        try
        {
            var loaded = await prices.GetMarketsAsync(SelectedRegion.Value, PriceCities, candidates.Select(x => x.Item.ItemId),
                candidates.Select(x => x.Quality), refresh, cancellation.Token);
            if (!active || !loads.IsCurrent(ticket)) return;
            quotes = loaded; Recalculate();
            PriceStatus = "AODP snapshots loaded. City, IP and calculation changes use these snapshots. Refresh to request newer prices.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!loads.IsCurrent(ticket)) return;
            quotes = []; Recalculate();
            Log.Warning(failure, "Could not compare city market prices");
            PriceStatus = CraftingPriceService.ErrorText(failure);
            CalculationStatus = "Market data could not be loaded; price comparisons are unavailable.";
        }
        finally
        {
            if (ReferenceEquals(priceCancellation, cancellation)) priceCancellation = null;
            if (loads.IsCurrent(ticket)) IsLoadingPrices = false;
        }
    }
    private void Recalculate()
    {
        BestCity = BestTotal = UiText.None; BestItem = "";
        if (Quantity is null || SalesTax is null || SetupFee is null || !PowerValid())
        { ClearResults(); CalculationStatus = "Enter quantity, fees and a valid IP target / skill bonus."; return; }
        try
        {
            var candidates = Candidates();
            var now = DateTime.UtcNow;
            var cities = CityFilters.Where(x => x.IsSelected).Select(x => x.Name).ToArray();
            var result = MarketCalculator.Calculate(cities, candidates, quotes, SelectedAction.Value, Quantity.Value, SalesTax.Value, SetupFee.Value,
                SelectedAge.Value == 0 ? null : TimeSpan.FromHours(SelectedAge.Value), now);
            var best = result.FirstOrDefault(x => x.Total.HasValue);
            Results.ReplaceAll(result.Select(x => new MarketRowViewModel(x, best is not null && x.Total == best.Total, now)));
            if (best is not null)
            {
                BestCity = best.City; BestTotal = UiFormat.Decimal(best.Total!.Value);
                BestItem = $"{best.Candidate!.Item.Label} · {MarketRowViewModel.QualityName(best.Candidate.Quality)}";
            }
            if (HasPower)
            {
                var cards = MarketCalculator.ByPower(cities, candidates, quotes, SelectedAction.Value, Quantity.Value, SalesTax.Value, SetupFee.Value,
                    SelectedAge.Value == 0 ? null : TimeSpan.FromHours(SelectedAge.Value), now, SelectedInterval.Value)
                    .Select(x => new MarketPowerCardViewModel(x, SelectedAction.Value, now));
                cards = SelectedSortOrder.Value switch
                {
                    MarketResultOrder.TotalPrice => cards.OrderBy(x => !x.HasPrice).ThenBy(x => MarketCalculator.IsBuying(SelectedAction.Value) ? x.SortPrice : -x.SortPrice).ThenBy(x => x.Minimum),
                    MarketResultOrder.PricePerPower => cards.OrderBy(x => x.SortPerPower is null).ThenBy(x => MarketCalculator.IsBuying(SelectedAction.Value) ? x.SortPerPower : -x.SortPerPower).ThenBy(x => x.Minimum),
                    _ => cards.OrderBy(x => x.Minimum)
                };
                PowerCards.ReplaceAll(cards); OnPropertyChanged(nameof(HasPowerCards));
            }
            else { PowerCards.ReplaceAll([]); OnPropertyChanged(nameof(HasPowerCards)); }
            CalculationStatus = cities.Length == 0 ? "Choose at least one city." : candidates.Length == 0 ? "No variants match these tier, enchantment, quality and IP filters."
                : $"{UiFormat.Whole(candidates.Length)} matching variants · {result.Count(x => x.Total.HasValue)} / {cities.Length} markets with prices";
        }
        catch (ArgumentOutOfRangeException) { ClearResults(); CalculationStatus = "Use a whole quantity from 1 to 1,000,000, nonnegative fees, and combined selling fees below 100%."; }
    }

    private void ClearResults()
    {
        Results.ReplaceAll([]); PowerCards.ReplaceAll([]); OnPropertyChanged(nameof(HasPowerCards));
    }
}
