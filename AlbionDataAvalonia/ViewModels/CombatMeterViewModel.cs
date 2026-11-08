using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui;
using AlbionDataAvalonia.Ui.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.ViewModels;

public sealed record CombatMeterDisplayRow(int Rank, CombatMeterRow Value, string RateUnit = "")
{
    public string Name => Value.Name;
    public string Amount => UiFormat.Whole(Value.Amount);
    public string Rate => UiFormat.Whole(Value.Rate);
    public string Share => $"{Value.Share:N1}%";
    public double ShareValue => Value.Share;
    public string Hits => UiFormat.Whole(Value.Hits);
    public bool IsPersonal => Value.IsPersonal;
    public string CompactAmount => UiFormat.Compact(Value.Amount);
    public string RateLabel => $"{Rate} {RateUnit}";
    public string ShareLabel => $"{Share} of total";
    public string DetailTip => $"{Amount} total · {Hits} events";
}

public sealed record CombatMeterContributionHeader(string PersonalTotal, string PersonalDetail,
    string GroupTotal, string GroupDetail, string ScopeTotal, string ScopeDetail)
{
    public static CombatMeterContributionHeader Empty { get; } = new("—", "", "—", "", "—", "");
}

public sealed partial class CombatMeterViewModel : ViewModelBase, IDisposable
{
    private readonly ActivityTrackerService tracker;
    private readonly UserSettings settings;
    private readonly IClipboardService clipboard;
    private readonly DispatcherTimer timer;
    private readonly UiDebouncer searchDebouncer;
    private bool initialized, disposed;
    private CombatMeterSnapshot? snapshot;
    private readonly HashSet<string> persisted = new(StringComparer.Ordinal)
    {
        nameof(SelectedScope), nameof(SelectedMetric), nameof(SelectedTargets), nameof(SelectedPeriod),
        nameof(SelectedLimit), nameof(SelectedRefresh), nameof(CustomMinutes), nameof(IncludeSelfHealing),
        nameof(ActiveRate), nameof(IsOverlay), nameof(Pinned), nameof(ClickThrough), nameof(OverlayOpacity)
    };

    public static IReadOnlyList<Option<CombatMeterScope>> Scopes { get; } =
    [new(CombatMeterScope.Personal, "Me"), new(CombatMeterScope.Party, "Group (including me)"),
        new(CombatMeterScope.Faction, "My city faction"), new(CombatMeterScope.AllObserved, "All observed players")];
    public static IReadOnlyList<Option<CombatMeterMetric>> Metrics { get; } =
    [new(CombatMeterMetric.Damage, "Damage dealt"), new(CombatMeterMetric.Healing, "Healing done"),
        new(CombatMeterMetric.DamageReceived, "Damage received"), new(CombatMeterMetric.HealingReceived, "Healing received")];
    public static IReadOnlyList<Option<CombatMeterTargets>> TargetOptions { get; } =
    [new(CombatMeterTargets.All, "All counterparts"), new(CombatMeterTargets.Players, "Players only"), new(CombatMeterTargets.Mobs, "Mobs only")];
    public static IReadOnlyList<Option<int>> Periods { get; } =
    [new(0, "Since reset"), new(30, "Last 30 seconds"), new(60, "Last minute"), new(300, "Last 5 minutes"),
        new(900, "Last 15 minutes"), new(3600, "Last hour"), new(7200, "Last 2 hours"),
        new(-1, "Custom minutes"), new(-2, "Custom time range")];
    public static IReadOnlyList<Option<int>> Limits { get; } = [new(5, "Top 5"), new(10, "Top 10"), new(20, "Top 20"), new(50, "Top 50"), new(200, "Top 200")];
    public static IReadOnlyList<Option<int>> RefreshOptions { get; } = [new(500, "0.5 seconds"), new(1000, "1 second"), new(2000, "2 seconds")];

    [ObservableProperty] private Option<CombatMeterScope> selectedScope;
    [ObservableProperty] private Option<CombatMeterMetric> selectedMetric;
    [ObservableProperty] private Option<CombatMeterTargets> selectedTargets;
    [ObservableProperty] private Option<int> selectedPeriod;
    [ObservableProperty] private Option<int> selectedLimit;
    [ObservableProperty] private Option<int> selectedRefresh;
    [ObservableProperty] private int customMinutes;
    [ObservableProperty] private bool includeSelfHealing;
    [ObservableProperty] private bool activeRate;
    [ObservableProperty] private bool isOverlay;
    [ObservableProperty] private bool pinned;
    [ObservableProperty] private bool clickThrough;
    [ObservableProperty] private int overlayOpacity;
    [ObservableProperty] private bool showSettings;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string status = "Waiting for character";
    [ObservableProperty] private bool isLive;
    [ObservableProperty] private string factionStatus = "";
    [ObservableProperty] private string total = "0";
    [ObservableProperty] private CombatMeterContributionHeader contributionHeader = CombatMeterContributionHeader.Empty;
    [ObservableProperty] private string periodText = "";
    [ObservableProperty] private string participantsText = "";
    [ObservableProperty] private string error = "";
    [ObservableProperty] private string copyStatus = "";
    [ObservableProperty] private bool isHidden;
    [ObservableProperty] private bool shortcutsAvailable;
    [ObservableProperty] private string overlayError = "";
    [ObservableProperty] private DateTimeOffset? fromDate = DateTimeOffset.Now.Date;
    [ObservableProperty] private DateTimeOffset? toDate = DateTimeOffset.Now.Date;
    [ObservableProperty] private TimeSpan? fromTime = DateTime.Now.AddMinutes(-15).TimeOfDay;
    [ObservableProperty] private TimeSpan? toTime = DateTime.Now.TimeOfDay;

    public ReplaceableCollection<CombatMeterDisplayRow> Rows { get; } = new();
    public bool IsCustomMinutes => SelectedPeriod.Value == -1;
    public bool IsCustomRange => SelectedPeriod.Value == -2;
    public bool IsFactionScope => SelectedScope.Value == CombatMeterScope.Faction;
    public bool ShowGroupContribution => SelectedScope.Value != CombatMeterScope.Personal;
    public bool ShowScopeContribution => SelectedScope.Value is CombatMeterScope.Faction or CombatMeterScope.AllObserved;
    public int ContributionColumns => SelectedScope.Value == CombatMeterScope.Personal ? 1 : SelectedScope.Value == CombatMeterScope.Party ? 2 : 3;
    public string GroupContributionLabel => IsFactionScope ? "My group in faction (including me)" : "My group (including me)";
    public string ContributionHint => IsFactionScope
        ? "Me and My group show contributions within this city faction only. Group members without a matching observed city flag are excluded. These totals overlap and must not be added. Search and Top N filter only the player ranking."
        : "My group includes me. These totals overlap and must not be added. All contributions use the same period and combat filters; search and Top N filter only the player ranking.";
    public bool HasRows => Rows.Count > 0;
    public bool ShowTable => HasRows && !IsOverlay;
    public bool ShowOverlayRanking => HasRows && IsOverlay;
    public bool ShowControls => !IsOverlay || ShowSettings;
    public bool SupportsClickThrough => OperatingSystem.IsWindows();
    public string PauseText => tracker.CombatMeter.IsPaused ? "Resume" : "Pause";
    public string OverlayButtonText => IsOverlay ? "Window" : "Overlay";
    public string OverlayKeyHint => OperatingSystem.IsWindows()
        ? "Ctrl+Alt+D hide / show · Ctrl+Alt+H window"
        : "Ctrl+Alt+H window (while focused)";
    public string MetricTitle => SelectedMetric.Label;
    public string RateTitle => SelectedMetric.Value is CombatMeterMetric.Damage or CombatMeterMetric.DamageReceived ? "DPS" : "HPS";
    public string ShortcutsHelp => OperatingSystem.IsWindows()
        ? "Ctrl+Alt+D: hide/show. Ctrl+Alt+H: overlay/window. Click-through requires both shortcuts; leave overlay to unlock."
        : "Ctrl+Alt+D: hide/show; Ctrl+Alt+H: overlay/window while focused. Restore a hidden window from Activity. Click-through requires Windows.";

    public CombatMeterViewModel(ActivityTrackerService tracker, UserSettings settings, IClipboardService clipboard)
    {
        this.tracker = tracker; this.settings = settings; this.clipboard = clipboard;
        var p = settings.CombatMeterOptions;
        selectedScope = UiOptions.Find(Scopes, (CombatMeterScope)p.Scope);
        selectedMetric = UiOptions.Find(Metrics, (CombatMeterMetric)p.Metric);
        selectedTargets = UiOptions.Find(TargetOptions, (CombatMeterTargets)p.Targets);
        selectedPeriod = UiOptions.Find(Periods, p.Period);
        selectedLimit = UiOptions.Find(Limits, p.RowLimit);
        selectedRefresh = UiOptions.Find(RefreshOptions, p.RefreshMilliseconds);
        customMinutes = Math.Clamp(p.CustomMinutes, 1, 120);
        includeSelfHealing = p.IncludeSelfHealing; activeRate = p.ActiveRate;
        isOverlay = p.Overlay; pinned = p.Pinned; clickThrough = p.ClickThrough;
        overlayOpacity = Math.Clamp(p.Opacity, 20, 100);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SelectedRefresh.Value) };
        timer.Tick += Tick;
        searchDebouncer = new(TimeSpan.FromMilliseconds(200), Refresh);
        initialized = true;
    }

    public void Start() { Refresh(); timer.Start(); }
    private void Tick(object? sender, EventArgs e) => Refresh();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!initialized || disposed) return;
        if (e.PropertyName == nameof(SelectedPeriod))
        {
            OnPropertyChanged(nameof(IsCustomMinutes)); OnPropertyChanged(nameof(IsCustomRange));
        }
        if (e.PropertyName is nameof(IsOverlay) or nameof(ShowSettings)) OnPropertyChanged(nameof(ShowControls));
        if (e.PropertyName is nameof(IsOverlay) or nameof(HasRows))
        { OnPropertyChanged(nameof(ShowTable)); OnPropertyChanged(nameof(ShowOverlayRanking)); }
        if (e.PropertyName == nameof(IsOverlay)) OnPropertyChanged(nameof(OverlayButtonText));
        if (e.PropertyName == nameof(IsOverlay)) { IsHidden = false; ShowSettings = false; }
        if (e.PropertyName == nameof(SelectedMetric)) { OnPropertyChanged(nameof(MetricTitle)); OnPropertyChanged(nameof(RateTitle)); }
        if (e.PropertyName == nameof(SelectedScope))
        {
            OnPropertyChanged(nameof(IsFactionScope)); OnPropertyChanged(nameof(ShowGroupContribution));
            OnPropertyChanged(nameof(ShowScopeContribution)); OnPropertyChanged(nameof(GroupContributionLabel));
            OnPropertyChanged(nameof(ContributionHint)); OnPropertyChanged(nameof(ContributionColumns));
        }
        if (e.PropertyName == nameof(Search)) { searchDebouncer.Trigger(); return; }
        if (e.PropertyName is { } name && persisted.Contains(name))
        {
            settings.CombatMeterOptions = new()
            {
                Scope = (int)SelectedScope.Value,
                Metric = (int)SelectedMetric.Value,
                Targets = (int)SelectedTargets.Value,
                Period = SelectedPeriod.Value,
                RowLimit = SelectedLimit.Value,
                RefreshMilliseconds = SelectedRefresh.Value,
                CustomMinutes = Math.Clamp(CustomMinutes, 1, 120),
                IncludeSelfHealing = IncludeSelfHealing,
                ActiveRate = ActiveRate,
                Overlay = IsOverlay,
                Pinned = Pinned,
                ClickThrough = ClickThrough,
                Opacity = Math.Clamp(OverlayOpacity, 20, 100)
            };
            timer.Interval = TimeSpan.FromMilliseconds(SelectedRefresh.Value);
        }
        if (persisted.Contains(e.PropertyName ?? "") || e.PropertyName is nameof(FromDate) or nameof(ToDate) or nameof(FromTime) or nameof(ToTime)) Refresh();
    }

    private void Refresh()
    {
        if (disposed) return;
        DateTime? from = null, to = null;
        Error = "";
        if (IsCustomRange)
        {
            if (FromDate is null || ToDate is null || FromTime is null || ToTime is null)
            { Error = "Choose both dates and times."; ClearRows(); return; }
            var localFrom = DateTime.SpecifyKind(FromDate.Value.Date + FromTime.Value, DateTimeKind.Local);
            var localTo = DateTime.SpecifyKind(ToDate.Value.Date + ToTime.Value, DateTimeKind.Local);
            if (localTo <= localFrom || TimeZoneInfo.Local.IsInvalidTime(localFrom) || TimeZoneInfo.Local.IsInvalidTime(localTo)
                || TimeZoneInfo.Local.IsAmbiguousTime(localFrom) || TimeZoneInfo.Local.IsAmbiguousTime(localTo))
            { Error = "Choose an increasing time range without an ambiguous daylight-saving time."; ClearRows(); return; }
            from = localFrom.ToUniversalTime(); to = localTo.ToUniversalTime();
            if (from > DateTime.UtcNow) { Error = "The start time must not be in the future."; ClearRows(); return; }
        }
        var seconds = IsCustomMinutes ? Math.Clamp(CustomMinutes, 1, 120) * 60 : SelectedPeriod.Value;
        snapshot = tracker.CombatMeter.Snapshot(DateTime.UtcNow, seconds > 0 ? TimeSpan.FromSeconds(seconds) : null,
            from, to, SelectedScope.Value, SelectedMetric.Value, SelectedTargets.Value, IncludeSelfHealing, ActiveRate, Search);
        Rows.ReplaceAll(snapshot.Rows.Take(SelectedLimit.Value).Select((x, i) => new CombatMeterDisplayRow(i + 1, x, RateTitle)));
        OnPropertyChanged(nameof(HasRows));
        Total = UiFormat.Whole(snapshot.Total);
        var basis = SelectedScope.Value switch
        { CombatMeterScope.Party => "group", CombatMeterScope.Faction => "faction", _ => "all observed" };
        var personalDetail = $"{UiFormat.Whole(snapshot.Personal.Rate)} {RateTitle}";
        if (ShowGroupContribution) personalDetail += $" · {snapshot.Personal.Share:N1}% of {basis}";
        if (ShowScopeContribution)
            personalDetail += snapshot.Party.Amount > 0
                ? $"\n{snapshot.Personal.Amount / snapshot.Party.Amount * 100:N1}% of my group"
                : "\nNo group contribution in this scope";
        var groupDetail = $"{UiFormat.Whole(snapshot.Party.Rate)} {RateTitle}";
        if (ShowScopeContribution) groupDetail += $" · {snapshot.Party.Share:N1}% of {basis}";
        ContributionHeader = new(UiFormat.Whole(snapshot.Personal.Amount), personalDetail,
            UiFormat.Whole(snapshot.Party.Amount), groupDetail, Total, $"{UiFormat.Whole(snapshot.Overall.Rate)} {RateTitle}");
        PeriodText = $"{UiFormat.LocalTime(snapshot.Start)} – {UiFormat.LocalTime(snapshot.End)} · {UiFormat.Duration(snapshot.End - snapshot.Start)}";
        ParticipantsText = $"{UiFormat.Count(snapshot.Rows.Count, "matching player")} · {UiFormat.Count(snapshot.Observations, "observation")} in scope";
        IsLive = !tracker.CombatMeter.IsPaused && tracker.CombatCaptureReady;
        var captureStatus = tracker.CombatMeter.IsPaused ? "Paused — incoming events are skipped"
            : IsLive ? "Live" : "Waiting for confirmed game traffic";
        Status = $"{SelectedScope.Label} · {captureStatus}";
        var flag = tracker.CombatFactionFlag;
        var faction = flag switch { 1 => "Martlock", 2 => "Lymhurst", 3 => "Bridgewatch", 4 => "Fort Sterling", 5 => "Thetford", 6 => "Caerleon", _ => "" };
        FactionStatus = flag is >= 1 and <= 6 ? $"Your city faction: {faction} · membership at event time"
            : flag is null ? "Your faction is not observed yet. Reflag in game once while capture is running. Earlier unknown events stay unclassified."
            : "You are not city-faction flagged. Neutral and red PvP flags are excluded.";
        if (snapshot.Truncated) Error = "Oldest observations were removed by the 2-hour / 200,000-event memory limit. The period shows retained data.";
    }

    private void ClearRows() { snapshot = null; Rows.ReplaceAll([]); Total = "—"; ContributionHeader = CombatMeterContributionHeader.Empty; PeriodText = ""; ParticipantsText = ""; OnPropertyChanged(nameof(HasRows)); }
    [RelayCommand] private void Reset() { tracker.CombatMeter.Reset(DateTime.UtcNow); SelectedPeriod = Periods[0]; CopyStatus = ""; Refresh(); }
    [RelayCommand] private void TogglePause() { tracker.CombatMeter.SetPaused(!tracker.CombatMeter.IsPaused, DateTime.UtcNow); OnPropertyChanged(nameof(PauseText)); Refresh(); }
    [RelayCommand] private void ToggleOverlay() => IsOverlay = !IsOverlay;
    [RelayCommand] private void ToggleSettings() => ShowSettings = !ShowSettings;
    [RelayCommand] private void ToggleVisibility() => IsHidden = !IsHidden;
    [RelayCommand] private void Reveal() => IsHidden = false;
    [RelayCommand]
    private async Task Copy()
    {
        if (snapshot is null) return;
        var text = new StringBuilder($"{MetricTitle} | {SelectedScope.Label} | {PeriodText}\nOverlapping contributions (do not add)\nSummary\tTotal\t{RateTitle}\tShare of scope\tObservations\n");
        void Summary(string label, CombatMeterContribution value) => text.AppendLine($"{label}\t{value.Amount.ToString("0.##", CultureInfo.InvariantCulture)}\t{value.Rate.ToString("0.##", CultureInfo.InvariantCulture)}\t{value.Share.ToString("0.##", CultureInfo.InvariantCulture)}%\t{value.Hits}");
        Summary("Me", snapshot.Personal);
        if (ShowGroupContribution) Summary(GroupContributionLabel, snapshot.Party);
        if (ShowScopeContribution) Summary(SelectedScope.Label, snapshot.Overall);
        text.AppendLine("\nMatching players\nPlayer\tTotal\t" + RateTitle + "\tShare of scope\tObservations");
        foreach (var r in snapshot.Rows) text.AppendLine($"{r.Name}\t{r.Amount.ToString("0.##", CultureInfo.InvariantCulture)}\t{r.Rate.ToString("0.##", CultureInfo.InvariantCulture)}\t{r.Share.ToString("0.##", CultureInfo.InvariantCulture)}%\t{r.Hits}");
        CopyStatus = await clipboard.SetTextAsync(text.ToString()) ? "Copied contributions and matching players" : "Could not copy";
    }
    public void Dispose() { disposed = true; timer.Stop(); timer.Tick -= Tick; searchDebouncer.Dispose(); }
}
