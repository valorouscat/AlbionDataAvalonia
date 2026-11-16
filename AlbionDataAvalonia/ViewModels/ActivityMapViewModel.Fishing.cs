using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class ActivityMapViewModel
{
    public FishingMapViewModel Fishing { get; }
    public IReadOnlyList<Option<int>> ActivityOptions { get; } = [new(0, "Map"), new(1, "Fishing")];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFishingMode))]
    private int selectedActivityIndex;
    public bool IsFishingMode => SelectedActivityIndex == 1;

    partial void OnSelectedActivityIndexChanged(int value)
    {
        settings.MapFishingMode = IsFishingMode;
        if (SelectedRunMap is { } option) UpdateHistoryDetails(option);
        Rebuild();
    }

    partial void OnSceneChanged(ActivityMapScene? value)
    {
        var zone = IsLive ? liveZone : historyZone;
        // A loading or failed zone must never report data from the previously displayed map.
        var matching = value is not null && zone?.RawMapId == (IsLive ? live.RawMapId : SelectedRunMap?.RawMapId);
        Fishing.Update(value, matching ? zone?.FishingSpots ?? [] : [], matching && zone?.FishingDataAvailable == true);
    }

    private IReadOnlyList<MapFishingSpot> FishingSpots(ZoneView? zone, int turns) => !Fishing.ShowLocations ? []
        : Fishing.Filter((zone?.FishingSpots ?? []).Select(spot => spot with { Position = Turn(spot.Position, turns) }).ToArray());
}
