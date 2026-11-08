using System.Collections.Generic;

namespace AlbionDataAvalonia.Settings;

public sealed record CombatMeterPreferences
{
    public int Scope { get; init; } = 1;
    public int Metric { get; init; }
    public int Targets { get; init; }
    public int Period { get; init; } = 300;
    public int CustomMinutes { get; init; } = 10;
    public int RowLimit { get; init; } = 20;
    public int RefreshMilliseconds { get; init; } = 1000;
    public bool IncludeSelfHealing { get; init; } = true;
    public bool ActiveRate { get; init; }
    public bool Overlay { get; init; }
    public bool Pinned { get; init; } = true;
    public bool ClickThrough { get; init; }
    public int Opacity { get; init; } = 85;
}

public partial class UserSettings
{
    private CombatMeterPreferences combatMeterOptions = new();
    public CombatMeterPreferences CombatMeterOptions
    {
        get => combatMeterOptions;
        set => SetCombatSetting(ref combatMeterOptions, value ?? new(), nameof(CombatMeterOptions));
    }
    private bool combatMeterWindowOpen;
    public bool CombatMeterWindowOpen
    {
        get => combatMeterWindowOpen;
        set => SetCombatSetting(ref combatMeterWindowOpen, value, nameof(CombatMeterWindowOpen));
    }
    private WindowPlacement? combatMeterWindowPlacement;
    public WindowPlacement? CombatMeterWindowPlacement
    {
        get => combatMeterWindowPlacement;
        set => SetCombatSetting(ref combatMeterWindowPlacement, value, nameof(CombatMeterWindowPlacement));
    }
    private WindowPlacement? combatMeterOverlayWindowPlacement;
    public WindowPlacement? CombatMeterOverlayWindowPlacement
    {
        get => combatMeterOverlayWindowPlacement;
        set => SetCombatSetting(ref combatMeterOverlayWindowPlacement, value, nameof(CombatMeterOverlayWindowPlacement));
    }

    private void SetCombatSetting<T>(ref T field, T value, string property)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(property);
    }
}
