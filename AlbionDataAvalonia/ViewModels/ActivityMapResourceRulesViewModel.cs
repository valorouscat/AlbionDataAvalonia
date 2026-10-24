using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class ActivityMapResourceRuleViewModel : ObservableObject
{
    private readonly Action changed;
    private readonly Action<ActivityMapResourceRuleViewModel>? expanded;
    public static IReadOnlyList<Option<int>> Tiers { get; } = Enumerable.Range(1, 8).Select(t => new Option<int>(t, $"T{t}")).ToArray();
    public static IReadOnlyList<Option<int>> Enchantments { get; } = [new(-1, "Any"), new(0, ".0"), new(1, ".1"), new(2, ".2"), new(3, ".3"), new(4, ".4")];
    [ObservableProperty] private Option<int> selectedTier;
    [ObservableProperty] private bool higherTiers;
    [ObservableProperty] private Option<int> selectedEnchantment;
    [ObservableProperty] private bool higherEnchantments;
    [ObservableProperty] private bool isExpanded;
    public string Summary => Rule.Summary;
    public bool HasEnchantment => SelectedEnchantment.Value >= 0;
    public MapResourceRule Rule => new(SelectedTier.Value, HigherTiers, SelectedEnchantment.Value, HigherEnchantments);
    public IRelayCommand RemoveCommand { get; }

    public ActivityMapResourceRuleViewModel(MapResourceRule rule, Action changed, Action<ActivityMapResourceRuleViewModel> remove,
        Action<ActivityMapResourceRuleViewModel>? expanded = null)
    {
        selectedTier = UiOptions.Find(Tiers, rule.Tier);
        selectedEnchantment = UiOptions.Find(Enchantments, rule.Enchantment);
        higherTiers = rule.HigherTiers;
        higherEnchantments = rule.HigherEnchantments;
        this.changed = changed;
        this.expanded = expanded;
        RemoveCommand = new RelayCommand(() => remove(this));
    }
    private void Update() { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(HasEnchantment)); changed(); }
    partial void OnSelectedTierChanged(Option<int> value) => Update();
    partial void OnHigherTiersChanged(bool value) => Update();
    partial void OnSelectedEnchantmentChanged(Option<int> value) => Update();
    partial void OnHigherEnchantmentsChanged(bool value) => Update();
    partial void OnIsExpandedChanged(bool value) { if (value) expanded?.Invoke(this); }
}

public sealed partial class ActivityMapResourceFamilyViewModel : ObservableObject
{
    private readonly UserSettings settings;
    private readonly bool spawn;
    private readonly Action changed;
    public string Family { get; }
    public string Label => Family;
    public string LayerLabel => spawn ? "Possible spawn spots" : "Seen nodes and resource mobs";
    public string InheritanceText => "Using the defaults below. Enable custom rules to configure this resource and this layer independently.";
    [ObservableProperty] private bool useCustomRules;
    [ObservableProperty] private bool enabled = true;
    public ObservableCollection<ActivityMapResourceRuleViewModel> Rules { get; } = new();
    public bool CanAddRule => Rules.Count < 16;
    public bool HasNoRules => Rules.Count == 0;

    public ActivityMapResourceFamilyViewModel(UserSettings settings, bool spawn, string family, Action changed)
    {
        this.settings = settings; this.spawn = spawn; Family = family; this.changed = changed;
        var stored = settings.GetMapResourceFamilyFilter(spawn, family);
        useCustomRules = stored is not null;
        enabled = stored?.Enabled ?? ActivityResourceFilter.ShowsFamily(settings, family);
        foreach (var rule in stored?.Rules ?? []) Add(rule);
    }

    private void Add(MapResourceRule rule) => Rules.Add(new(rule, Save, Remove, Expand));
    private void Expand(ActivityMapResourceRuleViewModel rule)
    {
        foreach (var other in Rules.Where(x => x != rule)) other.IsExpanded = false;
    }
    private void Remove(ActivityMapResourceRuleViewModel rule) { Rules.Remove(rule); Save(); }
    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private void AddRule() { Add(new()); Rules[^1].IsExpanded = true; Save(); }
    partial void OnEnabledChanged(bool value) => Save();
    partial void OnUseCustomRulesChanged(bool value)
    {
        if (value && Rules.Count == 0)
        {
            // Start from the current defaults, including the lower-tier exceptions, rather than silently losing them.
            Add(new(spawn ? settings.MapResourceSpawnMinTier : settings.MapResourceMinTier, true));
            if (!spawn)
                for (var offset = 1; offset <= settings.MapResourceLowerTiers && settings.MapResourceMinTier - offset >= 1; offset++)
                {
                    var filter = settings.GetMapResourceLowerTierEnchantment(offset);
                    Add(new(settings.MapResourceMinTier - offset, false, filter.Minimum, !filter.Exact));
                }
        }
        Save();
    }
    private void Save()
    {
        settings.SetMapResourceFamilyFilter(spawn, Family, UseCustomRules ? new(Enabled, Rules.Select(x => x.Rule).ToArray()) : null);
        OnPropertyChanged(nameof(CanAddRule)); OnPropertyChanged(nameof(HasNoRules)); AddRuleCommand.NotifyCanExecuteChanged();
        changed();
    }
}
