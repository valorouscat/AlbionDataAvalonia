using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class ActivityMapViewModel
{
    public IReadOnlyList<Option<int>> FilterTabs { get; } = [new(0, "Layers"), new(1, "Resources"), new(2, "Alerts"), new(3, "View")];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayersTab), nameof(IsResourcesTab), nameof(IsAlertsTab), nameof(IsViewTab))]
    private int selectedFilterTab;
    public bool IsLayersTab => SelectedFilterTab == 0;
    public bool IsResourcesTab => SelectedFilterTab == 1;
    public bool IsAlertsTab => SelectedFilterTab == 2;
    public bool IsViewTab => SelectedFilterTab == 3;

    public IReadOnlyList<Option<int>> ResourceLayerOptions { get; } = [new(0, "Seen"), new(1, "Possible spots")];
    public IReadOnlyList<Option<string>> ResourceFamilyOptions { get; } =
        [new("Wood", "Wood"), new("Stone", "Stone"), new("Fiber", "Fiber"), new("Hide", "Hide"), new("Ore", "Ore")];
    private IReadOnlyList<ActivityMapResourceFamilyViewModel> seenFamilies = [], spawnFamilies = [];
    [ObservableProperty] private int selectedResourceLayerIndex;
    [ObservableProperty] private int selectedResourceFamilyIndex;
    [ObservableProperty] private ActivityMapResourceFamilyViewModel? selectedResourceFamily;
    public bool EditingSpawnRules => SelectedResourceLayerIndex == 1;
    private void InitializeResourceRules()
    {
        seenFamilies = ResourceFamilyOptions.Select(x => new ActivityMapResourceFamilyViewModel(settings, false, x.Value, Rebuild)).ToArray();
        spawnFamilies = ResourceFamilyOptions.Select(x => new ActivityMapResourceFamilyViewModel(settings, true, x.Value, Rebuild)).ToArray();
        UpdateSelectedResourceFamily();
    }
    partial void OnSelectedResourceLayerIndexChanged(int value) { UpdateSelectedResourceFamily(); OnPropertyChanged(nameof(EditingSpawnRules)); }
    partial void OnSelectedResourceFamilyIndexChanged(int value) => UpdateSelectedResourceFamily();
    private void UpdateSelectedResourceFamily() => SelectedResourceFamily = (EditingSpawnRules ? spawnFamilies : seenFamilies)
        .ElementAtOrDefault(SelectedResourceFamilyIndex);
}
