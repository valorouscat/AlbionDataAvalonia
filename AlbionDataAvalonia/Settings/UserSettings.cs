namespace AlbionDataAvalonia.Settings;

using Serilog;
using Serilog.Events;
using System;
using System.ComponentModel;

public enum StartupWindowMode
{
    ShowMainWindow,
    MinimizedToTaskbar,
    HiddenToTray
}

public class UserSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _startHidden = true;
    public bool StartHidden
    {
        get => _startHidden;
        set
        {
            if (_startHidden != value)
            {
                _startHidden = value;
                if (value)
                {
                    StartupWindowMode = StartupWindowMode.HiddenToTray;
                }
                else if (StartupWindowMode == StartupWindowMode.HiddenToTray)
                {
                    StartupWindowMode = StartupWindowMode.ShowMainWindow;
                }

                OnPropertyChanged(nameof(StartHidden));
                Log.Information("Start hidden set to {StartHidden}", _startHidden);
            }
        }
    }

    private bool startWithWindows = true;
    public bool StartWithWindows
    {
        get => startWithWindows;
        set
        {
            if (startWithWindows != value)
            {
                startWithWindows = value;
                OnPropertyChanged(nameof(StartWithWindows));
                Log.Information("Start with Windows set to {StartWithWindows}", startWithWindows);
            }
        }
    }

    private bool minimizeButtonMinimizesToTray = false;
    public bool MinimizeButtonMinimizesToTray
    {
        get => minimizeButtonMinimizesToTray;
        set
        {
            if (minimizeButtonMinimizesToTray != value)
            {
                minimizeButtonMinimizesToTray = value;
                OnPropertyChanged(nameof(MinimizeButtonMinimizesToTray));
                Log.Information("Minimize button minimizes to tray set to {MinimizeButtonMinimizesToTray}", minimizeButtonMinimizesToTray);
            }
        }
    }

    private StartupWindowMode startupWindowMode = StartupWindowMode.ShowMainWindow;
    public StartupWindowMode StartupWindowMode
    {
        get => startupWindowMode;
        set
        {
            if (startupWindowMode != value)
            {
                startupWindowMode = value;
                var nextStartHidden = startupWindowMode == StartupWindowMode.HiddenToTray;
                if (_startHidden != nextStartHidden)
                {
                    _startHidden = nextStartHidden;
                    OnPropertyChanged(nameof(StartHidden));
                }

                OnPropertyChanged(nameof(StartupWindowMode));
                Log.Information("Startup window mode set to {StartupWindowMode}", startupWindowMode);
            }
        }
    }

    private bool shutDownOnClose = false;
    public bool ShutDownOnClose
    {
        get => shutDownOnClose;
        set
        {
            if (shutDownOnClose != value)
            {
                shutDownOnClose = value;
                OnPropertyChanged(nameof(ShutDownOnClose));
                Log.Information("Shut down on close set to {ShutDownOnClose}", shutDownOnClose);
            }
        }
    }

    private bool joinBetaProgram = false;
    public bool JoinBetaProgram
    {
        get => joinBetaProgram;
        set
        {
            if (joinBetaProgram != value)
            {
                joinBetaProgram = value;
                OnPropertyChanged(nameof(JoinBetaProgram));
                Log.Information("Join beta program set to {JoinBetaProgram}", joinBetaProgram);
            }
        }
    }

    private int _desiredThreadCount = 1;
    public int DesiredThreadCount
    {
        get => _desiredThreadCount;
        set
        {
            if (_desiredThreadCount != value)
            {
                _desiredThreadCount = value;
                if (_desiredThreadCount > MaxThreadCount)
                {
                    _desiredThreadCount = MaxThreadCount;
                }
                else if (_desiredThreadCount < 1)
                {
                    _desiredThreadCount = 1;
                }
                OnPropertyChanged(nameof(DesiredThreadCount));
                Log.Information("Desired thread count set to {DesiredThreadCount}", _desiredThreadCount);
            }
        }
    }

    public int MaxThreadCount => Environment.ProcessorCount;

    private int maxLogCount = 1000;
    public int MaxLogCount
    {
        get => maxLogCount;
        set
        {
            if (maxLogCount != value)
            {
                maxLogCount = value;
                OnPropertyChanged(nameof(MaxLogCount));
                Log.Information("Max log count set to {MaxLogCount}", maxLogCount);
            }
        }
    }

    private int tradesToShow = 1000;
    public int TradesToShow
    {
        get => tradesToShow;
        set
        {
            if (tradesToShow != value)
            {
                tradesToShow = value;
                OnPropertyChanged(nameof(TradesToShow));
                Log.Information("Trades to show set to {TradesToShow}", tradesToShow);
            }
        }
    }

    private bool uploadToAfmOnly;
    public bool UploadToAfmOnly
    {
        get => uploadToAfmOnly;
        set
        {
            if (uploadToAfmOnly == value) return;
            uploadToAfmOnly = value;
            OnPropertyChanged(nameof(UploadToAfmOnly));
            Log.Information("Private Mode set to {Enabled}", value);
        }
    }

    private bool contributeToPublic;
    public bool ContributeToPublic
    {
        get => contributeToPublic;
        set
        {
            if (contributeToPublic == value) return;
            contributeToPublic = value;
            OnPropertyChanged(nameof(ContributeToPublic));
            Log.Information("Share with Other AFM Users set to {Enabled}", value);
        }
    }

    private bool shareWithFriends;
    public bool ShareWithFriends
    {
        get => shareWithFriends;
        set
        {
            if (shareWithFriends == value) return;
            shareWithFriends = value;
            OnPropertyChanged(nameof(ShareWithFriends));
            Log.Information("Share with Friends set to {Enabled}", value);
        }
    }

    private bool uploadSpecsToAfm = true;
    public bool UploadSpecsToAfm
    {
        get => uploadSpecsToAfm;
        set
        {
            if (uploadSpecsToAfm != value)
            {
                uploadSpecsToAfm = value;
                OnPropertyChanged(nameof(UploadSpecsToAfm));
                Log.Information("Upload specs to AFM set to {UploadSpecsToAfm}", uploadSpecsToAfm);
            }
        }
    }

    private bool afmIslandTrackerEnabled = true;
    public bool AfmIslandTrackerEnabled
    {
        get => afmIslandTrackerEnabled;
        set
        {
            if (afmIslandTrackerEnabled != value)
            {
                afmIslandTrackerEnabled = value;
                OnPropertyChanged(nameof(AfmIslandTrackerEnabled));
                Log.Information("AFM island tracker enabled set to {AfmIslandTrackerEnabled}", afmIslandTrackerEnabled);
            }
        }
    }

    private bool activityRecordingEnabled = true;
    public bool ActivityRecordingEnabled
    {
        get => activityRecordingEnabled;
        set
        {
            if (activityRecordingEnabled == value) return;
            activityRecordingEnabled = value;
            OnPropertyChanged(nameof(ActivityRecordingEnabled));
            Log.Information("Activity recording enabled set to {ActivityRecordingEnabled}", activityRecordingEnabled);
        }
    }

    // The streamer window shows only the sections chosen here.
    private bool streamerShowSilver = true;
    public bool StreamerShowSilver { get => streamerShowSilver; set => SetStreamerOption(ref streamerShowSilver, value, nameof(StreamerShowSilver)); }

    private bool streamerShowCombat = true;
    public bool StreamerShowCombat { get => streamerShowCombat; set => SetStreamerOption(ref streamerShowCombat, value, nameof(StreamerShowCombat)); }

    private bool streamerShowGathering;
    public bool StreamerShowGathering { get => streamerShowGathering; set => SetStreamerOption(ref streamerShowGathering, value, nameof(StreamerShowGathering)); }

    private bool streamerShowChart;
    public bool StreamerShowChart { get => streamerShowChart; set => SetStreamerOption(ref streamerShowChart, value, nameof(StreamerShowChart)); }

    private bool streamerPartyTotals;
    public bool StreamerPartyTotals { get => streamerPartyTotals; set => SetStreamerOption(ref streamerPartyTotals, value, nameof(StreamerPartyTotals)); }

    private bool streamerChromaKey;
    public bool StreamerChromaKey { get => streamerChromaKey; set => SetStreamerOption(ref streamerChromaKey, value, nameof(StreamerChromaKey)); }

    // 0 combat, 1 gathering, 2 fame, matching the activity chart modes.
    private int streamerChartModeIndex;
    public int StreamerChartModeIndex
    {
        get => streamerChartModeIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (streamerChartModeIndex == clamped) return;
            streamerChartModeIndex = clamped;
            OnPropertyChanged(nameof(StreamerChartModeIndex));
            Log.Information("Streamer chart mode set to {StreamerChartModeIndex}", streamerChartModeIndex);
        }
    }

    // 0 small (headline numbers only), 1 medium, 2 large (with breakdowns).
    private int streamerSizeIndex = 1;
    public int StreamerSizeIndex
    {
        get => streamerSizeIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (streamerSizeIndex == clamped) return;
            streamerSizeIndex = clamped;
            OnPropertyChanged(nameof(StreamerSizeIndex));
            Log.Information("Streamer size set to {StreamerSizeIndex}", streamerSizeIndex);
        }
    }

    // Where the main and streamer windows were last closed; none opens them at the default spot.
    private WindowPlacement? mainWindowPlacement;
    public WindowPlacement? MainWindowPlacement
    {
        get => mainWindowPlacement;
        set
        {
            if (mainWindowPlacement == value) return;
            mainWindowPlacement = value;
            OnPropertyChanged(nameof(MainWindowPlacement));
        }
    }

    private bool streamerWindowOpen;
    // Remembered so the streamer window reopens on the next start; quitting the app keeps it set.
    public bool StreamerWindowOpen
    {
        get => streamerWindowOpen;
        set
        {
            if (streamerWindowOpen == value) return;
            streamerWindowOpen = value;
            OnPropertyChanged(nameof(StreamerWindowOpen));
        }
    }

    private WindowPlacement? streamerWindowPlacement;
    public WindowPlacement? StreamerWindowPlacement
    {
        get => streamerWindowPlacement;
        set
        {
            if (streamerWindowPlacement == value) return;
            streamerWindowPlacement = value;
            OnPropertyChanged(nameof(StreamerWindowPlacement));
        }
    }

    private bool mapWindowOpen;
    // Remembered so the map window reopens on the next start; quitting the app keeps it set.
    public bool MapWindowOpen
    {
        get => mapWindowOpen;
        set
        {
            if (mapWindowOpen == value) return;
            mapWindowOpen = value;
            OnPropertyChanged(nameof(MapWindowOpen));
        }
    }

    private WindowPlacement? mapWindowPlacement;
    public WindowPlacement? MapWindowPlacement
    {
        get => mapWindowPlacement;
        set
        {
            if (mapWindowPlacement == value) return;
            mapWindowPlacement = value;
            OnPropertyChanged(nameof(MapWindowPlacement));
        }
    }

    // The map shows the party's events too, not only the character's own; the path is always the character's.
    private bool mapPartyEvents;
    public bool MapPartyEvents
    {
        get => mapPartyEvents;
        set
        {
            if (mapPartyEvents == value) return;
            mapPartyEvents = value;
            OnPropertyChanged(nameof(MapPartyEvents));
            Log.Information("Map party events set to {MapPartyEvents}", mapPartyEvents);
        }
    }

    // How long the live map keeps showing your path and events; 0 keeps them for the whole visit.
    private int mapFadeMinutes = 5;
    public int MapFadeMinutes
    {
        get => mapFadeMinutes;
        set
        {
            var clamped = Math.Clamp(value, 0, 120);
            if (mapFadeMinutes == clamped) return;
            mapFadeMinutes = clamped;
            OnPropertyChanged(nameof(MapFadeMinutes));
            Log.Information("Map fade set to {MapFadeMinutes} minutes", mapFadeMinutes);
        }
    }

    // The live map's resource nodes are opt-in: they come from packets the game sends about nodes near the character.
    private bool mapShowResources;
    public bool MapShowResources
    {
        get => mapShowResources;
        set
        {
            if (mapShowResources == value) return;
            mapShowResources = value;
            OnPropertyChanged(nameof(MapShowResources));
            Log.Information("Map resources set to {MapShowResources}", mapShowResources);
        }
    }

    // Whether the map window's legend and filters are folded down to a single button.
    private bool mapLegendCollapsed;
    public bool MapLegendCollapsed
    {
        get => mapLegendCollapsed;
        set
        {
            if (mapLegendCollapsed == value) return;
            mapLegendCollapsed = value;
            OnPropertyChanged(nameof(MapLegendCollapsed));
        }
    }

    // The lowest tier of resource node the live map shows; 1 shows every tier.
    private int mapResourceMinTier = 1;
    public int MapResourceMinTier
    {
        get => mapResourceMinTier;
        set
        {
            var clamped = Math.Clamp(value, 1, 8);
            if (mapResourceMinTier == clamped) return;
            mapResourceMinTier = clamped;
            OnPropertyChanged(nameof(MapResourceMinTier));
            Log.Information("Map resource minimum tier set to {MapResourceMinTier}", mapResourceMinTier);
        }
    }

    private int mapResourceSpawnMinTier = 1;
    public int MapResourceSpawnMinTier
    {
        get => mapResourceSpawnMinTier;
        set
        {
            var clamped = Math.Clamp(value, 1, 8);
            if (mapResourceSpawnMinTier == clamped) return;
            mapResourceSpawnMinTier = clamped;
            OnPropertyChanged(nameof(MapResourceSpawnMinTier));
        }
    }

    private int mapResourceLowerTiers = 0;
    public int MapResourceLowerTiers
    {
        get => mapResourceLowerTiers;
        set
        {
            var clamped = Math.Clamp(value, 0, 7);
            if (mapResourceLowerTiers == clamped) return;
            mapResourceLowerTiers = clamped;
            OnPropertyChanged(nameof(MapResourceLowerTiers));
        }
    }

    private int mapResourceLowerTierMinEnchantment = 1;
    public int MapResourceLowerTierMinEnchantment
    {
        get => mapResourceLowerTierMinEnchantment;
        set
        {
            var clamped = Math.Clamp(value, 0, 4);
            if (mapResourceLowerTierMinEnchantment == clamped) return;
            mapResourceLowerTierMinEnchantment = clamped;
            OnPropertyChanged(nameof(MapResourceLowerTierMinEnchantment));
        }
    }

    private int mapResourceIconSize = 100;
    public int MapResourceIconSize
    {
        get => mapResourceIconSize;
        set
        {
            var clamped = Math.Clamp(value, 50, 200);
            if (mapResourceIconSize == clamped) return;
            mapResourceIconSize = clamped;
            OnPropertyChanged(nameof(MapResourceIconSize));
        }
    }

    private int mapResourceIconOpacity = 100;
    public int MapResourceIconOpacity
    {
        get => mapResourceIconOpacity;
        set
        {
            var clamped = Math.Clamp(value, 25, 100);
            if (mapResourceIconOpacity == clamped) return;
            mapResourceIconOpacity = clamped;
            OnPropertyChanged(nameof(MapResourceIconOpacity));
        }
    }

    private int mapResourceClusterRadius = 16;
    public int MapResourceClusterRadius
    {
        get => mapResourceClusterRadius;
        set
        {
            var clamped = Math.Clamp(value, 0, 64);
            if (mapResourceClusterRadius == clamped) return;
            mapResourceClusterRadius = clamped;
            OnPropertyChanged(nameof(MapResourceClusterRadius));
        }
    }

    // The kinds of resource the live map leaves out, comma separated (Wood, Stone, Fiber, Hide, Ore); empty shows every kind.
    private string mapResourceHiddenFamilies = "";
    public string MapResourceHiddenFamilies
    {
        get => mapResourceHiddenFamilies;
        set
        {
            var normalized = value ?? "";
            if (mapResourceHiddenFamilies == normalized) return;
            mapResourceHiddenFamilies = normalized;
            OnPropertyChanged(nameof(MapResourceHiddenFamilies));
            Log.Information("Map hidden resource kinds set to {MapResourceHiddenFamilies}", mapResourceHiddenFamilies);
        }
    }

    // Whether the live map also shows the mobs that yield resources, such as hide animals and ore critters.
    private bool mapShowResourceMobs = true;
    public bool MapShowResourceMobs
    {
        get => mapShowResourceMobs;
        set
        {
            if (mapShowResourceMobs == value) return;
            mapShowResourceMobs = value;
            OnPropertyChanged(nameof(MapShowResourceMobs));
            Log.Information("Map resource mobs set to {MapShowResourceMobs}", mapShowResourceMobs);
        }
    }

    // Whether the map shows where the zone can grow resources, from the game files.
    private bool mapShowResourceSpawns;
    public bool MapShowResourceSpawns
    {
        get => mapShowResourceSpawns;
        set
        {
            if (mapShowResourceSpawns == value) return;
            mapShowResourceSpawns = value;
            OnPropertyChanged(nameof(MapShowResourceSpawns));
            Log.Information("Map resource spawns set to {MapShowResourceSpawns}", mapShowResourceSpawns);
        }
    }

    // Whether the map shows the zone's points of interest (chests, bosses, shrines, camps, wisps), from the game files.
    private bool mapShowPois;
    public bool MapShowPois
    {
        get => mapShowPois;
        set
        {
            if (mapShowPois == value) return;
            mapShowPois = value;
            OnPropertyChanged(nameof(MapShowPois));
            Log.Information("Map points of interest set to {MapShowPois}", mapShowPois);
        }
    }

    // Whether the live map shows the roaming bosses near the character, read from packets.
    private bool mapShowRoamingBosses;
    public bool MapShowRoamingBosses
    {
        get => mapShowRoamingBosses;
        set
        {
            if (mapShowRoamingBosses == value) return;
            mapShowRoamingBosses = value;
            OnPropertyChanged(nameof(MapShowRoamingBosses));
            Log.Information("Map roaming bosses set to {MapShowRoamingBosses}", mapShowRoamingBosses);
        }
    }

    // Whether the live map flashes a warning over the character when a hostile player comes into view.
    private bool mapShowHostileAlert;
    public bool MapShowHostileAlert
    {
        get => mapShowHostileAlert;
        set
        {
            if (mapShowHostileAlert == value) return;
            mapShowHostileAlert = value;
            OnPropertyChanged(nameof(MapShowHostileAlert));
            Log.Information("Map hostile alert set to {MapShowHostileAlert}", mapShowHostileAlert);
        }
    }

    private void SetStreamerOption(ref bool field, bool value, string propertyName)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(propertyName);
        Log.Information("{Setting} set to {Value}", propertyName, value);
    }

    private bool disableAwakeningItemsTracker;
    public bool DisableAwakeningItemsTracker
    {
        get => disableAwakeningItemsTracker;
        set
        {
            if (disableAwakeningItemsTracker != value)
            {
                disableAwakeningItemsTracker = value;
                OnPropertyChanged(nameof(DisableAwakeningItemsTracker));
                Log.Information(
                    "Disable awakening items tracker set to {DisableAwakeningItemsTracker}",
                    disableAwakeningItemsTracker);
            }
        }
    }

    private LogEventLevel logLevel = LogEventLevel.Information;
    public LogEventLevel LogLevel
    {
        get => logLevel;
        set
        {
            if (logLevel != value)
            {
                logLevel = value;
                Log.Information("Log level set to {LogLevel}", logLevel);
                OnPropertyChanged(nameof(LogLevel));
                OnPropertyChanged(nameof(LogLevelIndex));
            }
        }
    }

    public int LogLevelIndex
    {
        get => (int)logLevel;
        set
        {
            var clamped = Math.Clamp(value, 0, 5);
            var nextLevel = (LogEventLevel)clamped;
            if (logLevel != nextLevel)
            {
                LogLevel = nextLevel;
            }
        }
    }

    // The network adapter capture is limited to, by driver name or display name; empty listens on every adapter
    // and keeps the first one that carries game traffic. Pinning helps when a VPN routes the game through its tunnel.
    private string captureAdapterName = string.Empty;
    public string CaptureAdapterName
    {
        get => captureAdapterName;
        set
        {
            var next = value?.Trim() ?? string.Empty;
            if (captureAdapterName == next) return;
            captureAdapterName = next;
            OnPropertyChanged(nameof(CaptureAdapterName));
            Log.Information("Capture adapter set to {CaptureAdapterName}", next.Length == 0 ? "automatic" : next);
        }
    }

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void UpdateFrom(UserSettings newSettings)
    {
        var properties = typeof(UserSettings).GetProperties();
        foreach (var property in properties)
        {
            if (property.CanWrite)
            {
                var value = property.GetValue(newSettings);
                property.SetValue(this, value);
            }
        }
    }
}
