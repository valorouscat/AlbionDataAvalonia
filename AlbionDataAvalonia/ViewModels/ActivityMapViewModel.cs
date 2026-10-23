using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Audio;
using AlbionDataAvalonia.Ui.Services;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui;
using AlbionDataAvalonia.Views;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.ViewModels;

public sealed record ActivityMapRunOption(Guid Id, string Label);

public sealed record ActivityMapRunMapOption(string RawMapId, string Label, DateTime StartUtc, DateTime EndUtc,
    IReadOnlyList<ActivityPathPoint> Points, IReadOnlyList<ActivityMapMarker> Markers, string? Layout = null, int? LayoutTurns = null);

/// <summary>Feeds the map window: the live map follows the tracker; history replays one recorded run.</summary>
public sealed partial class ActivityMapViewModel : ObservableObject, IDisposable
{
    private const double TimelineMax = 1000;
    private static readonly TimeSpan ReplayLength = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FadeRefresh = TimeSpan.FromSeconds(1);
    // Without a minimap, the path is shown on a grid at least this many units across.
    private const double MinimumFallbackSize = 160;

    private readonly ActivityTrackerService tracker;
    private readonly ActivityPersistence persistence;
    private readonly MapDataService maps;
    private readonly UserSettings settings;
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer replayTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private ActivityLiveMap live = ActivityLiveMap.Empty;
    private long liveVersion = -1;
    private DateTime liveRenderedAt;
    private ZoneView? liveZone;
    private string liveZoneRequest = "";
    private (double MinX, double MinY, double MaxX, double MaxY)? liveFallbackBounds;
    private ActivityRunMap? runMap;
    private ZoneView? historyZone;
    private Guid? requestedRunId;
    private bool runsLoaded;
    private bool disposed;
    private readonly LatestLoad loads = new();

    public ActivityMapViewModel(ActivityTrackerService tracker, ActivityPersistence persistence, MapDataService maps, UserSettings settings,
        IFileDialogService? files = null, HostileAlertSoundService? sound = null, ResourceAlertSoundService? resourceSound = null)
    {
        this.tracker = tracker;
        this.persistence = persistence;
        this.maps = maps;
        this.settings = settings;
        soundFiles = files;
        hostileSoundService = sound;
        hostileSoundEnabled = settings.MapHostileSoundEnabled;
        hostileSoundVolume = settings.MapHostileSoundVolume;
        hostileSoundFile = settings.MapHostileSoundFile;
        hostileSoundError = hostileSoundService?.Error ?? "";
        if (hostileSoundService is not null) hostileSoundService.ErrorChanged += SoundErrorChanged;
        resourceSoundService = resourceSound;
        resourceSoundEnabled = settings.MapResourceSoundEnabled;
        resourceSoundVolume = settings.MapResourceSoundVolume;
        resourceSoundFile = settings.MapResourceSoundFile;
        resourceSoundError = resourceSoundService?.Error ?? "";
        if (resourceSoundService is not null) resourceSoundService.ErrorChanged += ResourceSoundErrorChanged;
        selectedScopeIndex = settings.MapPartyEvents ? 1 : 0;
        selectedFadeIndex = Math.Max(0, Array.FindIndex(FadeOptionValues, x => x == settings.MapFadeMinutes));
        if (FadeOptionValues[selectedFadeIndex] != settings.MapFadeMinutes) selectedFadeIndex = 1;
        showResources = settings.MapShowResources;
        showResourceMobs = settings.MapShowResourceMobs;
        showResourceSpawns = settings.MapShowResourceSpawns;
        showPois = settings.MapShowPois;
        showRoamingBosses = settings.MapShowRoamingBosses;
        showHostileAlert = settings.MapShowHostileAlert;
        legendCollapsed = settings.MapLegendCollapsed;
        var hidden = HiddenFamilies(settings.MapResourceHiddenFamilies);
        showWood = !hidden.Contains("Wood");
        showStone = !hidden.Contains("Stone");
        showFiber = !hidden.Contains("Fiber");
        showHide = !hidden.Contains("Hide");
        showOre = !hidden.Contains("Ore");
        selectedResourceTier = UiOptions.Find(ResourceTierOptions, settings.MapResourceMinTier);
        selectedResourceSpawnTier = UiOptions.Nearest(ResourceTierOptions, settings.MapResourceSpawnMinTier);
        selectedLowerTiers = UiOptions.Nearest(LowerTierOptions, settings.MapResourceLowerTiers);
        selectedIconSize = UiOptions.Nearest(IconSizeOptions, settings.MapResourceIconSize);
        selectedIconOpacity = UiOptions.Nearest(IconOpacityOptions, settings.MapResourceIconOpacity);
        selectedSpawnIconOpacity = UiOptions.Nearest(IconOpacityOptions, settings.MapResourceSpawnIconOpacity);
        selectedClusterRadius = UiOptions.Nearest(ClusterRadiusOptions, settings.MapResourceClusterRadius);
        UpdateResourceTierFilters();
        liveTimer.Tick += (_, _) => RefreshLive();
        replayTimer.Tick += (_, _) => AdvanceReplay();
    }

    private static readonly int[] FadeOptionValues = [1, 5, 15, 0];
    public IReadOnlyList<Option<int>> ModeOptions { get; } = [new(0, "Live"), new(1, "History")];
    public IReadOnlyList<Option<int>> FadeOptions { get; } = [new(1, "1 min"), new(5, "5 min"), new(15, "15 min"), new(0, "Never")];
    public IReadOnlyList<Option<int>> ScopeOptions { get; } = [new(0, "Personal"), new(1, "Party")];
    public IReadOnlyList<Option<int>> ResourceTierOptions { get; } =
        [new(1, "All tiers"), new(2, "T2+"), new(3, "T3+"), new(4, "T4+"), new(5, "T5+"), new(6, "T6+"), new(7, "T7+"), new(8, "T8")];

    public IReadOnlyList<Option<int>> LowerTierOptions { get; } =
        [new(0, "Off"), new(1, "1 tier below"), new(2, "2 tiers below"), new(3, "3 tiers below"),
         new(4, "4 tiers below"), new(5, "5 tiers below"), new(6, "6 tiers below"), new(7, "7 tiers below")];
    public IReadOnlyList<Option<MapResourceEnchantmentFilter>> EnchantmentOptions { get; } =
        [new(new(0), ".0+"), new(new(1), ".1+"), new(new(2), ".2+"), new(new(3), ".3+"), new(new(4), ".4+")];
    public IReadOnlyList<Option<int>> IconSizeOptions { get; } =
        [new(50, "50%"), new(75, "75%"), new(100, "100%"), new(125, "125%"), new(150, "150%"), new(200, "200%")];
    public IReadOnlyList<Option<int>> IconOpacityOptions { get; } =
        [new(25, "25%"), new(50, "50%"), new(75, "75%"), new(100, "100%")];
    public IReadOnlyList<Option<int>> ClusterRadiusOptions { get; } =
        [new(0, "Off"), new(8, "8 px"), new(12, "12 px"), new(16, "16 px"), new(24, "24 px"), new(32, "32 px"), new(48, "48 px"), new(64, "64 px")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLive), nameof(IsHistory), nameof(ShowFollow), nameof(ShowResourceTier), nameof(ShowResourceFilters), nameof(ShowHostileSoundSettings), nameof(ShowResourceSoundSettings))]
    private int selectedModeIndex;

    [ObservableProperty] private int selectedFadeIndex;
    [ObservableProperty] private int selectedScopeIndex;
    [ObservableProperty] private bool follow = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFollow))]
    private ActivityMapScene? scene;
    [ObservableProperty] private string title = "Map";
    [ObservableProperty] private string subtitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string note = "";

    [ObservableProperty] private bool showPath = true;
    [ObservableProperty] private bool showKills = true;
    [ObservableProperty] private bool showLoot = true;
    [ObservableProperty] private bool showGathering = true;
    [ObservableProperty] private bool showDeaths = true;
    [ObservableProperty] private bool showFeatures = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResourceTier), nameof(ShowResourceFilters), nameof(ShowResourceSoundSettings))]
    private bool showResources;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResourceFilters))]
    private bool showResourceSpawns;
    [ObservableProperty] private bool showPois;
    [ObservableProperty] private bool showRoamingBosses;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHostileSoundSettings))]
    private bool showHostileAlert;
    [ObservableProperty] private Option<int> selectedResourceSpawnTier;
    [ObservableProperty] private Option<int> selectedLowerTiers;
    [ObservableProperty] private IReadOnlyList<ActivityMapResourceTierFilterViewModel> resourceTierFilters = [];
    [ObservableProperty] private Option<int> selectedIconSize;
    [ObservableProperty] private Option<int> selectedIconOpacity;
    [ObservableProperty] private Option<int> selectedSpawnIconOpacity;
    [ObservableProperty] private Option<int> selectedClusterRadius;
    [ObservableProperty] private Option<int> selectedResourceTier;
    [ObservableProperty] private bool showResourceMobs;
    // The kinds of resource shown; the choice is saved as the kinds left out, so a kind added later starts shown.
    [ObservableProperty] private bool showWood;
    [ObservableProperty] private bool showStone;
    [ObservableProperty] private bool showFiber;
    [ObservableProperty] private bool showHide;
    [ObservableProperty] private bool showOre;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuns))]
    private IReadOnlyList<ActivityMapRunOption> runs = [];

    [ObservableProperty] private ActivityMapRunOption? selectedRun;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleRunMaps))]
    private IReadOnlyList<ActivityMapRunMapOption> runMaps = [];

    [ObservableProperty] private ActivityMapRunMapOption? selectedRunMap;
    [ObservableProperty] private double timelineValue = TimelineMax;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    [NotifyPropertyChangedFor(nameof(PlayButtonName))]
    private bool isPlaying;

    [ObservableProperty] private string timelineStartText = "";
    [ObservableProperty] private string timelineEndText = "";
    [ObservableProperty] private string timelineNowText = "";
    [ObservableProperty] private string runSummaryText = "";
    [ObservableProperty] private IReadOnlyList<ActivityMapTick> ticks = [];

    public bool IsLive => SelectedModeIndex == 0;
    public bool IsHistory => SelectedModeIndex == 1;
    public bool ShowFollow => IsLive && Scene?.Position is not null;
    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool ShowResourceTier => IsLive && ShowResources;
    // Kinds are shared; each layer has its own tier threshold.
    public bool ShowResourceFilters => ShowResourceTier || ShowResourceSpawns;
    public bool HasRuns => Runs.Count > 0;
    public bool HasMultipleRunMaps => RunMaps.Count > 1;
    public string PlayButtonText => IsPlaying ? "❚❚" : "▶";
    public string PlayButtonName => IsPlaying ? "Pause replay" : "Play replay";
    public double TimelineMaximum => TimelineMax;

    private ActivityMapLayers Layers =>
        (ShowPath ? ActivityMapLayers.Path : 0) | (ShowKills ? ActivityMapLayers.Kills : 0) | (ShowLoot ? ActivityMapLayers.Loot : 0)
        | (ShowGathering ? ActivityMapLayers.Gathering : 0) | (ShowDeaths ? ActivityMapLayers.Deaths : 0) | (ShowFeatures ? ActivityMapLayers.Features : 0)
        | (ShowResources ? ActivityMapLayers.Resources : 0) | (ShowResourceSpawns ? ActivityMapLayers.ResourceSpawns : 0)
        | (ShowPois ? ActivityMapLayers.Pois : 0) | (ShowRoamingBosses ? ActivityMapLayers.RoamingBosses : 0)
        | (ShowHostileAlert ? ActivityMapLayers.HostileAlert : 0);

    private double FadeSeconds => FadeOptionValues[Math.Clamp(SelectedFadeIndex, 0, FadeOptionValues.Length - 1)] * 60d;

    public void Start()
    {
        liveTimer.Start();
        RefreshLive();
    }

    /// <summary>Opens history on a run, for "Show on map" in the Activity tab.</summary>
    public void ShowRun(Guid runId)
    {
        requestedRunId = runId;
        runsLoaded = false;
        if (IsHistory) _ = LoadRunsAsync();
        else SelectedModeIndex = 1;
    }

    partial void OnSelectedModeIndexChanged(int value)
    {
        StopReplay();
        if (IsHistory && !runsLoaded) _ = LoadRunsAsync();
        Rebuild();
    }

    partial void OnSelectedFadeIndexChanged(int value)
    {
        settings.MapFadeMinutes = FadeOptionValues[Math.Clamp(value, 0, FadeOptionValues.Length - 1)];
        Rebuild();
    }

    partial void OnSelectedScopeIndexChanged(int value)
    {
        settings.MapPartyEvents = value == 1;
        if (SelectedRunMap is { } option) UpdateHistoryDetails(option);
        Rebuild();
    }

    [ObservableProperty] private bool legendCollapsed;

    partial void OnLegendCollapsedChanged(bool value) => settings.MapLegendCollapsed = value;

    [RelayCommand]
    private void ToggleLegend() => LegendCollapsed = !LegendCollapsed;

    partial void OnShowPathChanged(bool value) => Rebuild();
    partial void OnShowKillsChanged(bool value) => Rebuild();
    partial void OnShowLootChanged(bool value) => Rebuild();
    partial void OnShowGatheringChanged(bool value) => Rebuild();
    partial void OnShowDeathsChanged(bool value) => Rebuild();
    partial void OnShowFeaturesChanged(bool value) => Rebuild();

    partial void OnShowResourcesChanged(bool value)
    {
        settings.MapShowResources = value;
        Rebuild();
    }

    partial void OnShowResourceSpawnsChanged(bool value)
    {
        settings.MapShowResourceSpawns = value;
        Rebuild();
    }

    partial void OnShowPoisChanged(bool value)
    {
        settings.MapShowPois = value;
        Rebuild();
    }

    partial void OnShowRoamingBossesChanged(bool value)
    {
        settings.MapShowRoamingBosses = value;
        Rebuild();
    }

    partial void OnShowHostileAlertChanged(bool value)
    {
        settings.MapShowHostileAlert = value;
        Rebuild();
    }

    partial void OnShowResourceMobsChanged(bool value)
    {
        settings.MapShowResourceMobs = value;
        Rebuild();
    }

    partial void OnShowWoodChanged(bool value) => ResourceFamiliesChanged();
    partial void OnShowStoneChanged(bool value) => ResourceFamiliesChanged();
    partial void OnShowFiberChanged(bool value) => ResourceFamiliesChanged();
    partial void OnShowHideChanged(bool value) => ResourceFamiliesChanged();
    partial void OnShowOreChanged(bool value) => ResourceFamiliesChanged();

    private void ResourceFamiliesChanged()
    {
        settings.MapResourceHiddenFamilies = string.Join(",", ResourceFamilies.Where(x => !ShowsFamily(x)));
        Rebuild();
    }

    private static readonly string[] ResourceFamilies = ["Wood", "Stone", "Fiber", "Hide", "Ore"];

    private static HashSet<string> HiddenFamilies(string? saved) =>
        new((saved ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    // A kind outside the five, should the game add one, shows with them.
    private bool ShowsFamily(string family) => family switch
    {
        "Wood" => ShowWood,
        "Stone" => ShowStone,
        "Fiber" => ShowFiber,
        "Hide" => ShowHide,
        "Ore" => ShowOre,
        _ => true
    };

    partial void OnSelectedResourceTierChanged(Option<int> value)
    {
        settings.MapResourceMinTier = value.Value;
        UpdateResourceTierFilters();
        Rebuild();
    }

    partial void OnSelectedResourceSpawnTierChanged(Option<int> value)
    {
        settings.MapResourceSpawnMinTier = value.Value;
        Rebuild();
    }

    partial void OnSelectedLowerTiersChanged(Option<int> value)
    {
        settings.MapResourceLowerTiers = value.Value;
        UpdateResourceTierFilters();
        Rebuild();
    }

    private void UpdateResourceTierFilters()
    {
        var count = Math.Min(SelectedLowerTiers.Value, SelectedResourceTier.Value - 1);
        ResourceTierFilters = Enumerable.Range(1, count).Select(offset =>
            new ActivityMapResourceTierFilterViewModel(offset, SelectedResourceTier.Value - offset, EnchantmentOptions,
                settings.GetMapResourceLowerTierEnchantment(offset), filter =>
                {
                    var saved = new Dictionary<int, MapResourceEnchantmentFilter>(settings.MapResourceLowerTierEnchantments)
                    {
                        [offset] = filter
                    };
                    settings.MapResourceLowerTierEnchantments = saved;
                    Rebuild();
                })).ToArray();
    }

    partial void OnSelectedIconSizeChanged(Option<int> value)
    {
        settings.MapResourceIconSize = value.Value;
        Rebuild();
    }

    partial void OnSelectedIconOpacityChanged(Option<int> value)
    {
        settings.MapResourceIconOpacity = value.Value;
        Rebuild();
    }

    partial void OnSelectedSpawnIconOpacityChanged(Option<int> value)
    {
        settings.MapResourceSpawnIconOpacity = value.Value;
        Rebuild();
    }

    partial void OnSelectedClusterRadiusChanged(Option<int> value)
    {
        settings.MapResourceClusterRadius = value.Value;
        Rebuild();
    }

    partial void OnSelectedRunChanged(ActivityMapRunOption? value)
    {
        StopReplay();
        if (value is not null) _ = LoadRunAsync(value.Id);
    }

    partial void OnSelectedRunMapChanged(ActivityMapRunMapOption? value)
    {
        StopReplay();
        if (value is not null) _ = LoadHistoryZoneAsync(value);
    }

    partial void OnTimelineValueChanged(double value)
    {
        if (IsHistory) RebuildHistory();
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopReplay();
            return;
        }

        if (SelectedRunMap is null) return;
        if (TimelineValue >= TimelineMax) TimelineValue = 0;
        IsPlaying = true;
        replayTimer.Start();
    }

    [RelayCommand]
    private void FollowMe() => Follow = true;

    private void AdvanceReplay()
    {
        var next = TimelineValue + TimelineMax * replayTimer.Interval.TotalSeconds / ReplayLength.TotalSeconds;
        TimelineValue = Math.Min(TimelineMax, next);
        if (TimelineValue >= TimelineMax) StopReplay();
    }

    private void StopReplay()
    {
        replayTimer.Stop();
        IsPlaying = false;
    }

    private void Rebuild()
    {
        if (IsLive) RebuildLive();
        else RebuildHistory();
    }

    private bool liveRefreshFailed;
    private void RefreshLive()
    {
        try
        {
            RefreshLiveCore();
            liveRefreshFailed = false;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            if (!liveRefreshFailed) Log.Error(error, "Could not refresh the live map for {MapId}; the next update will retry", live.RawMapId);
            liveRefreshFailed = true;
            liveVersion = -1;
            Scene = null;
            Note = "Couldn't refresh the map. Waiting for the next update.";
        }
    }

    private void RefreshLiveCore()
    {
        if (disposed || !IsLive) return;
        var version = tracker.LiveMapVersion;
        if (version != liveVersion)
        {
            liveVersion = version;
            live = tracker.GetLiveMap();
            // A Mists layout can arrive after the map itself; the path so far stays on the same grid.
            var request = $"{live.RawMapId}|{live.Layout}";
            if (request != liveZoneRequest)
            {
                if (!liveZoneRequest.StartsWith(live.RawMapId + "|", StringComparison.Ordinal)) liveFallbackBounds = null;
                liveZoneRequest = request;
                liveZone = null;
                if (live.RawMapId.Length > 0) _ = LoadLiveZoneAsync(request, live.RawMapId, live.Layout, live.MapName);
            }

            RebuildLive();
        }
        // A hostile player's warning goes once its few seconds are up.
        else if (Scene?.Hostiles is { Count: > 0 } hostiles && hostiles.Any(x => DateTime.UtcNow - x.SeenAtUtc >= ActivityTrackerService.HostileAlertLength))
            RebuildLive();
        // Fading moves on even when nothing new happens.
        else if (FadeSeconds > 0 && DateTime.UtcNow - liveRenderedAt >= FadeRefresh) RebuildLive();
    }

    private async Task LoadLiveZoneAsync(string request, string rawMapId, string? layout, string mapName)
    {
        var view = await LoadZoneAsync(rawMapId, layout, mapName);
        if (disposed || request != liveZoneRequest) return;
        liveZone = view;
        RebuildLive();
    }

    private void RebuildLive()
    {
        if (!IsLive) return;
        liveRenderedAt = DateTime.UtcNow;
        var zone = liveZone is { } view && view.RawMapId == live.RawMapId ? view : null;
        Title = zone?.Title ?? (string.IsNullOrWhiteSpace(live.MapName) ? "Map" : live.MapName);
        Subtitle = zone?.Subtitle ?? "";
        if (live.RawMapId.Length == 0)
        {
            Note = "Waiting for the game. The map follows you once you enter a zone.";
            Scene = null;
            return;
        }

        var geometry = zone?.Geometry ?? LiveFallbackGeometry();
        Note = live.Position is null ? "Waiting for your position. Take a few steps in game."
            : zone is { Failed: true } ? "Couldn't load map data. Check your connection; your path still shows."
            : zone is { Zone: null } && live.Layout is null && AlbionLocations.ResolveActivityLocation(live.RawMapId).Kind == ActivityMapKind.Mists
                ? "Finding this Mists layout. The map shows once a wisp or exit comes into view."
            : zone is { Image: null, Zone.Outline.Count: > 0 } ? "No minimap for this zone. Its rough outline and rooms come from the game files."
            : zone is { Image: null, Zone.Rooms.Count: > 0 } ? "No minimap for this zone. Its rooms come from the game files."
            : zone is { Image: null } ? "No map image for this zone. Showing your path only."
            : "";
        if (geometry is null)
        {
            Scene = null;
            return;
        }

        var turns = LayoutTurns(zone, live.Layout, live.LayoutTurns);
        Scene = new ActivityMapScene($"live:{live.RawMapId}:{zone?.Zone?.Id}", geometry, zone?.Image, zone?.Frame, live.Trail, Shown(live.Markers),
            Turned(zone?.Exits ?? [], turns), Turned(zone?.Icons ?? [], turns), live.Position, live.Heading, true, DateTime.UtcNow, FadeSeconds,
            null, null, Layers, turns, ShownResources(live.Resources), ShownResourceMobs(live.ResourceMobs),
            ShownResourceSpawns(Turned(zone?.ResourceSpawns ?? [], turns)), ShownPois(Turned(zone?.Pois ?? [], turns)),
            zone?.Zone?.Rooms, zone?.Zone?.Outline, ShowRoamingBosses ? (live.RoamingBosses ?? []).OrderBy(x => x.Kind == ActivityLiveActorKind.Boss).ThenBy(x => x.Tier).ToArray() : [],
            ShownHostiles(live.Hostiles, zone?.Zone), ResourceIconScale: SelectedIconSize.Value / 100d,
            ResourceIconOpacity: SelectedIconOpacity.Value / 100d, ResourceClusterRadius: SelectedClusterRadius.Value,
            ResourceSpawnIconOpacity: SelectedSpawnIconOpacity.Value / 100d);
    }

    // No one can attack the character in a blue zone, so a player there is no threat.
    private IReadOnlyList<ActivityHostileSighting> ShownHostiles(IReadOnlyList<ActivityHostileSighting>? hostiles, MapZone? zone)
    {
        if (!ShowHostileAlert || hostiles is not { Count: > 0 } || zone?.PvpCategory == "blue") return [];
        var now = DateTime.UtcNow;
        return hostiles.Where(x => now - x.SeenAtUtc < ActivityTrackerService.HostileAlertLength).ToArray();
    }

    // Grows with the path so the view stays steady while walking a zone without a minimap.
    private MapImageGeometry? LiveFallbackGeometry()
    {
        var points = live.Trail.Select(x => (x.X, x.Y)).Concat(Shown(live.Markers).Select(x => (x.X, x.Y))).ToArray();
        if (points.Length == 0) return null;
        var bounds = (MinX: points.Min(x => x.X), MinY: points.Min(x => x.Y), MaxX: points.Max(x => x.X), MaxY: points.Max(x => x.Y));
        if (liveFallbackBounds is { } known)
            bounds = (Math.Min(known.MinX, bounds.MinX), Math.Min(known.MinY, bounds.MinY), Math.Max(known.MaxX, bounds.MaxX), Math.Max(known.MaxY, bounds.MaxY));
        liveFallbackBounds = bounds;
        return FallbackGeometry(bounds);
    }

    private static MapImageGeometry? FallbackGeometry((double MinX, double MinY, double MaxX, double MaxY) bounds)
    {
        var centerX = (bounds.MinX + bounds.MaxX) / 2;
        var centerY = (bounds.MinY + bounds.MaxY) / 2;
        var half = Math.Max(MinimumFallbackSize, Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY) + 60) / 2;
        return MapImageGeometry.From(new(centerX - half, centerY - half), new(centerX + half, centerY + half));
    }

    private async Task LoadRunsAsync()
    {
        var version = loads.Begin();
        try
        {
            var mapped = await persistence.GetMappedRunsAsync();
            if (disposed || !loads.IsCurrent(version)) return;
            runsLoaded = true;
            var requested = requestedRunId;
            var options = mapped.Select(x => new ActivityMapRunOption(x.Id, RunLabel(x))).ToList();
            // A run shown from the Activity tab is listed even if it recorded no positions yet.
            if (requested is { } id && options.All(x => x.Id != id) && await persistence.GetSessionAsync(id) is { } run)
                options.Insert(0, new ActivityMapRunOption(run.Id, RunLabel(run)));
            if (disposed || !loads.IsCurrent(version)) return;
            Runs = options;
            requestedRunId = null;
            SelectedRun = options.FirstOrDefault(x => x.Id == requested) ?? options.FirstOrDefault();
            if (SelectedRun is null)
            {
                Note = "No runs with positions yet. Runs record your path from now on.";
                Scene = null;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Warning(error, "Could not load runs for the map.");
            Note = "Couldn't load your runs. Try again.";
        }
    }

    private async Task LoadRunAsync(Guid runId)
    {
        var version = loads.Begin();
        try
        {
            var loaded = await persistence.GetRunMapAsync(runId);
            if (disposed || !loads.IsCurrent(version)) return;
            runMap = loaded;
            historyZone = null;
            if (loaded is null)
            {
                RunMaps = [];
                SelectedRunMap = null;
                Note = "This run no longer exists.";
                Scene = null;
                return;
            }

            var visits = VisitDetails(loaded.Run);
            var points = loaded.Segments.SelectMany(x => x.ReadPoints().Select(point => (x.RawMapId, Point: point)))
                .GroupBy(x => x.RawMapId).ToDictionary(x => x.Key, x => (IReadOnlyList<ActivityPathPoint>)x.Select(y => y.Point).OrderBy(y => y.CapturedAtUtc).ToArray());
            var markers = loaded.Markers.GroupBy(x => x.RawMapId).ToDictionary(x => x.Key, x => (IReadOnlyList<ActivityMapMarker>)x.ToArray());
            var options = points.Keys.Union(markers.Keys).Select(id =>
            {
                var path = points.GetValueOrDefault(id) ?? [];
                var placed = markers.GetValueOrDefault(id) ?? [];
                var times = path.Select(x => x.CapturedAtUtc).Concat(placed.Select(x => x.CapturedAtUtc)).ToArray();
                var visit = visits.GetValueOrDefault(id);
                return new ActivityMapRunMapOption(id, visit.Name ?? loaded.Run.LocationName, times.Min(), times.Max(), path, placed, visit.Layout, visit.LayoutTurns);
            }).OrderBy(x => x.StartUtc).ToArray();
            RunMaps = options;
            SelectedRunMap = options.FirstOrDefault();
            if (SelectedRunMap is null)
            {
                RunSummaryText = "";
                Ticks = [];
                Note = "This run recorded no positions. Runs record your path from now on.";
                Scene = null;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Warning(error, "Could not load the run map.");
            Note = "Couldn't load this run. Try again.";
        }
    }

    private async Task LoadHistoryZoneAsync(ActivityMapRunMapOption option)
    {
        historyZone = null;
        TimelineStartText = UiFormat.LocalShortTime(option.StartUtc);
        TimelineEndText = UiFormat.LocalShortTime(option.EndUtc);
        UpdateHistoryDetails(option);
        TimelineValue = TimelineMax;
        RebuildHistory();
        var view = await LoadZoneAsync(option.RawMapId, option.Layout, option.Label);
        if (disposed || SelectedRunMap != option) return;
        historyZone = view;
        RebuildHistory();
    }

    private void RebuildHistory()
    {
        if (!IsHistory) return;
        if (runMap is null || SelectedRunMap is not { } option)
        {
            Title = "History";
            Subtitle = "";
            Scene = null;
            return;
        }

        var zone = historyZone is { } view && view.RawMapId == option.RawMapId ? view : null;
        Title = zone?.Title ?? option.Label;
        Subtitle = zone?.Subtitle ?? "";
        var until = TimelineValue >= TimelineMax ? (DateTime?)null
            : option.StartUtc + TimeSpan.FromSeconds((option.EndUtc - option.StartUtc).TotalSeconds * TimelineValue / TimelineMax);
        TimelineNowText = until is { } moment ? UiFormat.LocalTime(moment) : "";
        var markers = Shown(option.Markers);
        var points = option.Points.Select(x => (x.X, x.Y)).Concat(markers.Select(x => (x.X, x.Y))).ToArray();
        var geometry = zone?.Geometry ?? (points.Length == 0 ? null
            : FallbackGeometry((points.Min(x => x.X), points.Min(x => x.Y), points.Max(x => x.X), points.Max(x => x.Y))));
        Note = zone is { Failed: true } ? "Couldn't load map data. Check your connection; your path still shows."
            : zone is { Image: null, Zone.Outline.Count: > 0 } ? "No minimap for this zone. Its rough outline and rooms come from the game files."
            : zone is { Image: null, Zone.Rooms.Count: > 0 } ? "No minimap for this zone. Its rooms come from the game files."
            : zone is { Image: null } ? "No map image for this zone. Showing your path only."
            : "";
        if (geometry is null)
        {
            Scene = null;
            return;
        }

        var turns = LayoutTurns(zone, option.Layout, option.LayoutTurns);
        Scene = new ActivityMapScene($"history:{runMap.Run.Id}:{option.RawMapId}:{zone?.Zone?.Id}", geometry, zone?.Image, zone?.Frame, option.Points,
            markers, Turned(zone?.Exits ?? [], turns), Turned(zone?.Icons ?? [], turns), null, null, false, DateTime.UtcNow, 0, option.StartUtc, until,
            Layers, turns, ResourceSpawns: ShownResourceSpawns(Turned(zone?.ResourceSpawns ?? [], turns)),
            Pois: ShownPois(Turned(zone?.Pois ?? [], turns)), Rooms: zone?.Zone?.Rooms, Outline: zone?.Zone?.Outline, ResourceIconScale: SelectedIconSize.Value / 100d,
            ResourceIconOpacity: SelectedIconOpacity.Value / 100d, ResourceClusterRadius: SelectedClusterRadius.Value,
            ResourceSpawnIconOpacity: SelectedSpawnIconOpacity.Value / 100d);
    }

    // Only a minimap found by its layout follows the instance's turn.
    private static int LayoutTurns(ZoneView? zone, string? layout, int? turns) =>
        zone?.Zone is { } shown && string.Equals(shown.Id, layout, StringComparison.OrdinalIgnoreCase) ? turns ?? 0 : 0;

    // A layout's exits and icons are in its own coordinates; a turned instance places them where the game does.
    private static IReadOnlyList<ActivityMapExit> Turned(IReadOnlyList<ActivityMapExit> exits, int turns) =>
        turns % 4 == 0 ? exits : exits.Select(exit => exit with { Position = Turn(exit.Position, turns) }).ToArray();

    private static IReadOnlyList<ActivityMapIcon> Turned(IReadOnlyList<ActivityMapIcon> icons, int turns) =>
        turns % 4 == 0 ? icons : icons.Select(icon => icon with { Position = Turn(icon.Position, turns) }).ToArray();

    private static IReadOnlyList<MapResourceSpawn> Turned(IReadOnlyList<MapResourceSpawn> spawns, int turns) =>
        turns % 4 == 0 ? spawns : spawns.Select(spawn => spawn with { Position = Turn(spawn.Position, turns) }).ToArray();

    private static IReadOnlyList<MapPoi> Turned(IReadOnlyList<MapPoi> chests, int turns) =>
        turns % 4 == 0 ? chests : chests.Select(chest => chest with { Position = Turn(chest.Position, turns) }).ToArray();

    private static MapPoint Turn(MapPoint point, int turns)
    {
        for (var turn = 0; turn < turns % 4; turn++) point = new MapPoint(point.Y, -point.X);
        return point;
    }

    private async Task<ZoneView> LoadZoneAsync(string rawMapId, string? layout, string fallbackName)
    {
        try
        {
            var zone = await maps.FindZoneAsync(rawMapId, layout);
            if (zone is null) return new ZoneView(rawMapId, null, null, null, [], [], [], [], fallbackName, "", null, false);
            var imageTask = maps.GetImageAsync(zone);
            var featuresTask = maps.GetFeaturesAsync(zone);
            var frameTask = maps.GetFrameImageAsync();
            var exits = new List<ActivityMapExit>();
            foreach (var exit in zone.Exits)
            {
                var target = await maps.ZoneAsync(exit.TargetId);
                // A Mists dungeon's exits lead back to the Mists, which has no zone of its own; a Depths floor names its own.
                var label = exit.Label ?? (exit.TargetId.Length == 0 ? "Exit to the Mists" : $"To {target?.DisplayName ?? exit.TargetId}");
                exits.Add(new ActivityMapExit(exit.Position, label, Zone(target?.PvpCategory)));
            }

            var features = await featuresTask;
            var icons = features.Icons;
            var iconImages = new Dictionary<string, Bitmap?>();
            foreach (var type in icons.Select(x => x.MarkerType).OfType<string>().Distinct()) iconImages[type] = await maps.GetIconImageAsync(type);
            // The game draws a bank, market or city portal on its exit instead of an exit marker; so does the map,
            // and the icon keeps the exit's "To …" label.
            var shownIcons = icons.Select(icon =>
            {
                var exit = exits.FirstOrDefault(x => OnSpot(x.Position, icon.Position));
                return new ActivityMapIcon(icon.Position, exit?.Label ?? icon.Label, icon.MarkerType is { } type ? iconImages.GetValueOrDefault(type) : null,
                    icon.Scale, icon.Mob);
            }).ToArray();
            exits.RemoveAll(exit => icons.Any(icon => icon.MarkerType is not null && OnSpot(exit.Position, icon.Position)));
            // Without its minimap a zone still has its name, exits and icons; the path draws on a grid.
            Bitmap? image = null;
            var failed = false;
            try { image = await imageTask; }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.IO.IOException)
            {
                Log.Warning(error, "Could not load the minimap for {RawMapId}", rawMapId);
                failed = true;
            }
            return new ZoneView(rawMapId, zone, image, await frameTask, exits, shownIcons, features.ResourceSpawns, features.Pois, zone.DisplayName,
                ZoneDescription(zone), zone.Geometry, failed);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.IO.IOException or JsonException)
        {
            Log.Warning(error, "Could not load map data for {RawMapId}", rawMapId);
            return new ZoneView(rawMapId, null, null, null, [], [], [], [], fallbackName, "", null, true);
        }
        // The callers start this without waiting, so any other failure would vanish and leave the map empty.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Error(error, "Could not build the map for {RawMapId}", rawMapId);
            return new ZoneView(rawMapId, null, null, null, [], [], [], [], fallbackName, "", null, true);
        }
    }

    // The lower-tier exception never changes which nodes meet the main threshold.
    private bool ShowsSeenTier(int tier, int enchantment) => ActivityResourceFilter.ShowsTier(settings, tier, enchantment);

    private IReadOnlyList<ActivityResourceNode> ShownResources(IReadOnlyList<ActivityResourceNode>? nodes) =>
        !ShowResources || nodes is not { Count: > 0 } ? [] : nodes
            .Where(x => x.Charges > 0 && ShowsSeenTier(x.Tier, x.Enchantment) && ShowsFamily(x.Family))
            .OrderByDescending(x => x.Tier).ThenByDescending(x => x.Enchantment).ThenByDescending(x => x.Charges).ToArray();

    private IReadOnlyList<ActivityResourceMob> ShownResourceMobs(IReadOnlyList<ActivityResourceMob>? mobs) =>
        !ShowResources || !ShowResourceMobs || mobs is not { Count: > 0 } ? [] : mobs
            .Where(x => ShowsSeenTier(x.Tier, x.Enchantment) && ShowsFamily(x.Family))
            .OrderBy(x => x.Tier).ThenBy(x => x.Enchantment).ToArray();

    private IReadOnlyList<MapResourceSpawn> ShownResourceSpawns(IReadOnlyList<MapResourceSpawn> spawns) =>
        !ShowResourceSpawns || spawns.Count == 0 ? [] : spawns
            .Where(x => x.Tier >= SelectedResourceSpawnTier.Value && ShowsFamily(x.Family))
            .OrderBy(x => x.Tier).ThenBy(x => x.Mob).ToArray();

    // Points of interest follow only their own switch. Wisps and camp spots, the most numerous, are drawn first and
    // bosses and chests last, the best of each kind on top.
    private IReadOnlyList<MapPoi> ShownPois(IReadOnlyList<MapPoi> pois) =>
        !ShowPois || pois.Count == 0 ? [] : pois.OrderBy(x => PoiOrder(x.Kind)).ThenBy(x => x.Grade).ToArray();

    private static int PoiOrder(MapPoiKind kind) => kind switch
    {
        MapPoiKind.Wisp => 0,
        MapPoiKind.Camp => 1,
        MapPoiKind.Shrine or MapPoiKind.Altar => 2,
        MapPoiKind.Key or MapPoiKind.Coffer => 3,
        MapPoiKind.Chest => 3,
        MapPoiKind.ExitUp or MapPoiKind.ExitDown => 5,
        _ => 4
    };

    // The path is always the character's own; events follow the Personal or Party choice.
    private IReadOnlyList<ActivityMapMarker> Shown(IReadOnlyList<ActivityMapMarker> markers) =>
        SelectedScopeIndex == 1 ? markers : markers.Where(x => x.IsOwn).ToArray();

    private void UpdateHistoryDetails(ActivityMapRunMapOption option)
    {
        var span = Math.Max(1, (option.EndUtc - option.StartUtc).TotalSeconds);
        Ticks = Shown(option.Markers).Select(x => new ActivityMapTick(Math.Clamp((x.CapturedAtUtc - option.StartUtc).TotalSeconds / span, 0, 1), x.Kind)).ToArray();
        if (runMap is not null) RunSummaryText = RunSummary(runMap, RunMaps, Shown(runMap.Markers));
    }

    // AlbionWorld places an exit's icon at the exit's own coordinates.
    private static bool OnSpot(MapPoint a, MapPoint b) => Math.Abs(a.X - b.X) <= 0.01 && Math.Abs(a.Y - b.Y) <= 0.01;

    private static MapZoneKind Zone(string? pvpCategory) => pvpCategory switch
    {
        "blue" => MapZoneKind.Blue,
        "yellow" => MapZoneKind.Yellow,
        "red" => MapZoneKind.Red,
        "black" => MapZoneKind.Black,
        _ => MapZoneKind.Other
    };

    private static string ZoneDescription(MapZone zone)
    {
        var kind = zone.MapCategory switch
        {
            "openworld" => "Open world",
            "city" or "startingcity" or "portalcity" => "City",
            "roads" => "Roads of Avalon",
            "dungeon" => "Dungeon",
            "corrupted" => "Corrupted dungeon",
            "mistsdungeon" => "Mists dungeon",
            "helldungeon" => "Depths",
            "hellden" => "Hellgate",
            "hideout" => "Hideout area",
            "expedition" => "Expedition",
            "arena" => "Arena",
            "island" => "Island",
            "passage" => "Passage",
            _ => ""
        };
        var zoneColor = zone.PvpCategory switch
        {
            "blue" => "Blue zone",
            "yellow" => "Yellow zone",
            "red" => "Red zone",
            "orange" => "Orange zone",
            "black" => "Black zone",
            _ => ""
        };
        return string.Join(" · ", new[] { kind, zone.Tier is > 0 ? $"T{zone.Tier}" : "", zoneColor }.Where(x => x.Length > 0));
    }

    private static Dictionary<string, (string? Name, string? Layout, int? LayoutTurns)> VisitDetails(ActivitySession run)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<ActivityVisit>>(run.VisitsJson) ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.RawMapId))
                .GroupBy(x => x.RawMapId).ToDictionary(x => x.Key, x => (
                    x.Select(y => y.LocationName).FirstOrDefault(y => !string.IsNullOrWhiteSpace(y)),
                    x.Select(y => y.Metadata?.Layout).FirstOrDefault(y => !string.IsNullOrWhiteSpace(y)),
                    x.Select(y => y.Metadata?.LayoutTurns).FirstOrDefault(y => y is not null)));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string RunLabel(ActivitySession run)
    {
        var started = run.StartedAtUtc.ToLocalTime();
        var ended = (run.ClosedAtUtc ?? run.LastObservedAtUtc).ToLocalTime();
        var day = started.Date == DateTime.Today ? "Today" : started.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : started.ToString("d", CultureInfo.CurrentCulture);
        var title = string.IsNullOrWhiteSpace(run.Title) ? run.LocationName : run.Title;
        return $"{title} · {day} {started.ToString("t", CultureInfo.CurrentCulture)} – {ended.ToString("t", CultureInfo.CurrentCulture)}";
    }

    private static string RunSummary(ActivityRunMap run, IReadOnlyList<ActivityMapRunMapOption> mapsInRun, IReadOnlyList<ActivityMapMarker> markers)
    {
        var duration = (run.Run.ClosedAtUtc ?? run.Run.LastObservedAtUtc) - run.Run.StartedAtUtc;
        var parts = new List<string>
        {
            UiFormat.Count(mapsInRun.Count, "map"),
            UiFormat.Duration(duration)
        };
        var kills = markers.Count(x => x.Kind == ActivityMapMarkerKind.Kill);
        if (kills > 0) parts.Add(UiFormat.Count(kills, "kill"));
        var loot = markers.Count(x => x.Kind == ActivityMapMarkerKind.Loot);
        if (loot > 0) parts.Add(UiFormat.Count(loot, "pickup"));
        return string.Join(" · ", parts);
    }

    public void Dispose()
    {
        disposed = true;
        if (hostileSoundService is not null) hostileSoundService.ErrorChanged -= SoundErrorChanged;
        if (resourceSoundService is not null) resourceSoundService.ErrorChanged -= ResourceSoundErrorChanged;
        liveTimer.Stop();
        replayTimer.Stop();
    }

    private sealed record ZoneView(string RawMapId, MapZone? Zone, Bitmap? Image, Bitmap? Frame, IReadOnlyList<ActivityMapExit> Exits,
        IReadOnlyList<ActivityMapIcon> Icons, IReadOnlyList<MapResourceSpawn> ResourceSpawns, IReadOnlyList<MapPoi> Pois, string Title, string Subtitle, MapImageGeometry? Geometry, bool Failed);
}
