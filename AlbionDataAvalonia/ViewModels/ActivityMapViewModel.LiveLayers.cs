using AlbionDataAvalonia.Activity.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class ActivityMapViewModel
{
    private bool liveShowKills;
    private bool historyShowKills = true;
    public bool ShowKills
    {
        get => IsLive ? liveShowKills : historyShowKills;
        set
        {
            if (ShowKills == value) return;
            if (IsLive) settings.MapLiveShowKills = liveShowKills = value;
            else historyShowKills = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    private bool liveShowLoot;
    private bool historyShowLoot = true;
    public bool ShowLoot
    {
        get => IsLive ? liveShowLoot : historyShowLoot;
        set
        {
            if (ShowLoot == value) return;
            if (IsLive) settings.MapLiveShowLoot = liveShowLoot = value;
            else historyShowLoot = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    private bool liveShowGathering;
    private bool historyShowGathering = true;
    public bool ShowGathering
    {
        get => IsLive ? liveShowGathering : historyShowGathering;
        set
        {
            if (ShowGathering == value) return;
            if (IsLive) settings.MapLiveShowGathering = liveShowGathering = value;
            else historyShowGathering = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWispFilters))]
    private bool showWisps;
    public bool ShowWispFilters => IsLive && ShowWisps;

    partial void OnShowWispsChanged(bool value)
    {
        settings.MapShowWisps = value;
        Rebuild();
    }

    [ObservableProperty] private bool showWispCommon;
    partial void OnShowWispCommonChanged(bool value) => SaveWispFilters();
    [ObservableProperty] private bool showWispUncommon;
    partial void OnShowWispUncommonChanged(bool value) => SaveWispFilters();
    [ObservableProperty] private bool showWispRare;
    partial void OnShowWispRareChanged(bool value) => SaveWispFilters();
    [ObservableProperty] private bool showWispEpic;
    partial void OnShowWispEpicChanged(bool value) => SaveWispFilters();
    [ObservableProperty] private bool showWispLegendary;
    partial void OnShowWispLegendaryChanged(bool value) => SaveWispFilters();
    [ObservableProperty] private bool showWispSolo;
    partial void OnShowWispSoloChanged(bool value) => SaveWispFilters();
    [ObservableProperty] private bool showWispDuo;
    partial void OnShowWispDuoChanged(bool value) => SaveWispFilters();

    private void SaveWispFilters()
    {
        settings.MapWispRarities = (ShowWispCommon ? 1 : 0) | (ShowWispUncommon ? 2 : 0) | (ShowWispRare ? 4 : 0)
            | (ShowWispEpic ? 8 : 0) | (ShowWispLegendary ? 16 : 0);
        settings.MapWispSizes = (ShowWispSolo ? 1 : 0) | (ShowWispDuo ? 2 : 0);
        Rebuild();
    }

    private IReadOnlyList<ActivityWisp> ShownWisps() => !IsLive || !ShowWisps ? [] : (live.Wisps ?? [])
        .Where(x => (settings.MapWispRarities & (1 << (int)x.Rarity)) != 0
            && (settings.MapWispSizes & (1 << (int)x.Size)) != 0)
        .OrderBy(x => x.Rarity).ToArray();
}
