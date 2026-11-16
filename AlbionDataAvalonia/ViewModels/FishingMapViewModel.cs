using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.ViewModels;

/// <summary>Fishing filters and a summary of retained, confirmed catches on the displayed map.</summary>
public sealed partial class FishingMapViewModel : ObservableObject
{
    private readonly UserSettings settings;
    private readonly Action rebuild;
    private FishingCatchRow[] previousRows = [];

    public FishingMapViewModel(UserSettings settings, Action rebuild)
    {
        this.settings = settings;
        this.rebuild = rebuild;
        showSchools = (settings.MapFishingKinds & 1) != 0;
        showTreasure = (settings.MapFishingKinds & 2) != 0;
        showSharks = (settings.MapFishingKinds & 4) != 0;
        showWater = (settings.MapFishingKinds & 8) != 0;
        showOther = (settings.MapFishingKinds & 16) != 0;
        waterIndex = settings.MapFishingWater;
        includeDynamic = settings.MapFishingDynamic;
        showCatches = settings.MapFishingShowCatches;
        showLocations = settings.MapFishingShowLocations;
    }

    public IReadOnlyList<Option<int>> WaterOptions { get; } = [new(0, "All water"), new(1, "Freshwater"), new(2, "Saltwater")];
    [ObservableProperty] private int waterIndex;
    [ObservableProperty] private bool showSchools;
    [ObservableProperty] private bool showTreasure;
    [ObservableProperty] private bool showSharks;
    [ObservableProperty] private bool showWater;
    [ObservableProperty] private bool showOther;
    [ObservableProperty] private bool includeDynamic;
    [ObservableProperty] private bool showCatches;
    [ObservableProperty] private bool showLocations;
    [ObservableProperty] private FishingMapSummary summary = FishingMapSummary.Empty;
    [ObservableProperty] private string coverageText = "Waiting for map data.";
    [ObservableProperty] private string nearestText = "Waiting for your position.";
    [ObservableProperty] private string spotSummary = "";
    [ObservableProperty] private string catchScopeText = "";
    [ObservableProperty] private bool hasCatches;
    public ReplaceableCollection<FishingCatchRow> Catches { get; } = new();

    partial void OnWaterIndexChanged(int value) => Save();
    partial void OnShowSchoolsChanged(bool value) => Save();
    partial void OnShowTreasureChanged(bool value) => Save();
    partial void OnShowSharksChanged(bool value) => Save();
    partial void OnShowWaterChanged(bool value) => Save();
    partial void OnShowOtherChanged(bool value) => Save();
    partial void OnIncludeDynamicChanged(bool value) => Save();
    partial void OnShowCatchesChanged(bool value) => Save();
    partial void OnShowLocationsChanged(bool value) => Save();

    private void Save()
    {
        settings.MapFishingKinds = (ShowSchools ? 1 : 0) | (ShowTreasure ? 2 : 0) | (ShowSharks ? 4 : 0) | (ShowWater ? 8 : 0) | (ShowOther ? 16 : 0);
        settings.MapFishingWater = WaterIndex;
        settings.MapFishingDynamic = IncludeDynamic;
        settings.MapFishingShowCatches = ShowCatches;
        settings.MapFishingShowLocations = ShowLocations;
        rebuild();
    }

    public IReadOnlyList<MapFishingSpot> Filter(IReadOnlyList<MapFishingSpot> spots) => spots.Where(spot =>
        (IncludeDynamic || !spot.Dynamic) && (WaterIndex == 0 || WaterIndex == 1 && spot.IsFreshwater || WaterIndex == 2 && spot.IsOcean)
        && (spot.Kind switch
        {
            MapFishingSpotKind.School => ShowSchools,
            MapFishingSpotKind.Treasure => ShowTreasure,
            MapFishingSpotKind.Shark => ShowSharks,
            MapFishingSpotKind.Water => ShowWater,
            _ => ShowOther
        })).ToArray();

    public void Update(ActivityMapScene? scene, IReadOnlyList<MapFishingSpot> allSpots, bool dataAvailable)
    {
        CoverageText = scene is null ? "Waiting for a map and position data."
            : !dataAvailable ? "Fishing layout data is unavailable for this map. Recorded catches can still appear."
            : allSpots.Count == 0 ? "This layout has no fishing locations in its map data."
            : "Possible locations from map files. Current fish, depletion and respawn are unknown.";
        var spots = scene?.FishingSpots ?? [];
        SpotSummary = $"{UiFormat.Count(spots.Count, "location")} shown / {UiFormat.Whole(allSpots.Count)} in layout";
        if (scene?.Position is { } position)
        {
            var nearest = spots.Where(x => x.Kind != MapFishingSpotKind.Water).MinBy(x =>
                Math.Pow(x.Position.X - position.X, 2) + Math.Pow(x.Position.Y - position.Y, 2));
            NearestText = nearest is null ? "No matching fishing spots. Change the filters."
                : $"{nearest.Label} · {nearest.WaterLabel} · {UiFormat.Whole(Math.Sqrt(Math.Pow(nearest.Position.X - position.X, 2) + Math.Pow(nearest.Position.Y - position.Y, 2)))} map units away (straight line, not a route)";
        }
        else NearestText = scene?.IsLive == false ? "Nearest location is available in Live mode." : "Move in game to find the nearest possible location.";

        // Respect the map's fade and replay cursor. Scope is explicitly personal, even with Party enabled.
        var markers = (scene?.Markers ?? []).Where(x => x.IsFishing && x.IsOwn
            && (scene?.UntilUtc is not { } until || x.CapturedAtUtc <= until)
            && (scene?.IsLive != true || scene.FadeSeconds <= 0 || (scene.NowUtc - x.CapturedAtUtc).TotalSeconds < scene.FadeSeconds)).ToArray();
        var rows = markers.GroupBy(x => (x.ItemUniqueName, x.Subject, x.Quality)).Select(group =>
            new FishingCatchRow(group.Key.ItemUniqueName, group.Key.Subject, group.Key.Quality,
                group.Sum(x => (decimal)x.Quantity), group.Sum(x => (decimal)(x.Value ?? 0)), group.Count(x => x.Value is null),
                group.Max(x => x.CapturedAtUtc)))
            .OrderByDescending(x => x.KnownValue).ThenBy(x => x.Name).ToArray();
        if (!previousRows.SequenceEqual(rows))
        {
            previousRows = rows;
            Catches.ReplaceAll(rows);
        }
        HasCatches = rows.Length > 0;
        var catches = markers.Select(x => x.EventId).Distinct().Count();
        var knownValue = markers.Sum(x => (decimal)(x.Value ?? 0));
        var unpriced = markers.Count(x => x.Value is null);
        Summary = new(UiFormat.Whole(catches), UiFormat.Whole((double)markers.Sum(x => (decimal)x.Quantity)),
            UiFormat.Whole(rows.Length), markers.Length == 0 || unpriced == markers.Length ? UiText.Unavailable : UiFormat.Compact((double)knownValue),
            unpriced > 0 ? $"{UiFormat.Count(unpriced, "reward")} without a price; priced subtotal only" : "Estimated market value, before fees",
            markers.Length == 0 ? UiText.None : UiFormat.LocalShortTime(markers.Max(x => x.CapturedAtUtc)));
        CatchScopeText = scene?.IsLive == true
            ? "Your retained catch rewards on this map, within the selected fade window. Not a session total."
            : "Your recorded catch rewards on this visit, up to the replay cursor.";
    }

    [RelayCommand]
    private void OpenGuide() => SystemLauncher.TryOpenUrl("https://allthingsalbion.com/en/activities/fishing/");
}

public sealed record FishingMapSummary(string Rewards, string Items, string Types, string Value, string PriceDetail, string LastCatch)
{
    public static FishingMapSummary Empty { get; } = new("0", "0", "0", UiText.Unavailable, "", UiText.None);
}

public sealed record FishingCatchRow(string UniqueName, string Name, int Quality, decimal Quantity, decimal KnownValue, int Unpriced, DateTime LastAtUtc)
{
    public string Detail => $"×{UiFormat.Whole((double)Quantity)} · {(Unpriced > 0 ? "priced subtotal " : "EMV ")}{(KnownValue == 0 && Unpriced > 0 ? UiText.Unavailable : UiFormat.Compact((double)KnownValue))} · {UiFormat.LocalShortTime(LastAtUtc)}";
}
