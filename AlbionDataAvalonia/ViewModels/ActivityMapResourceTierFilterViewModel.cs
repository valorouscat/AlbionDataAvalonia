using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace AlbionDataAvalonia.ViewModels;

/// <summary>The enchantment choice for one relative tier in the map's resource filters.</summary>
public sealed class ActivityMapResourceTierFilterViewModel : ObservableObject
{
    private readonly Action<MapResourceEnchantmentFilter> changed;
    private Option<MapResourceEnchantmentFilter> selectedEnchantment;

    public ActivityMapResourceTierFilterViewModel(int offset, int tier,
        IReadOnlyList<Option<MapResourceEnchantmentFilter>> options, MapResourceEnchantmentFilter filter,
        Action<MapResourceEnchantmentFilter> changed)
    {
        Label = $"{offset} {(offset == 1 ? "tier" : "tiers")} below · T{tier}";
        EnchantmentOptions = options;
        selectedEnchantment = UiOptions.Find(options, filter);
        this.changed = changed;
    }

    public string Label { get; }
    public IReadOnlyList<Option<MapResourceEnchantmentFilter>> EnchantmentOptions { get; }
    public Option<MapResourceEnchantmentFilter> SelectedEnchantment
    {
        get => selectedEnchantment;
        set
        {
            if (value is not null && SetProperty(ref selectedEnchantment, value)) changed(value.Value);
        }
    }
}
