using AlbionDataAvalonia.Market;
using AlbionDataAvalonia.Ui;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace AlbionDataAvalonia.ViewModels;

public sealed class MarketPowerCardViewModel(MarketPowerBand band, MarketAction action, DateTime now)
{
    public int Minimum => band.Minimum;
    public decimal? SortPrice => band.Best?.Total;
    public decimal? SortPerPower => band.Best is { UnitPrice: { } price, Candidate.ItemPower: > 0 } best ? price / best.Candidate.ItemPower : null;
    public bool HasPrice => band.Best is not null;
    public string Range => $"{UiFormat.Whole(band.Minimum)} – {UiFormat.Whole(band.Maximum)}";
    public MarketRowViewModel Row { get; } = new(band.Best ?? new("", null, null, null, null), false, now);
    public string PriceLabel => MarketCalculator.IsBuying(action) ? "Best cost / item" : "Best net / item";
    public string PerPower => SortPerPower is { } value ? $"{UiFormat.Compact((double)value)} / IP" : "";
    public string Source => action is MarketAction.BuyInstantly or MarketAction.SellOrder ? "Sell listing" : "Buy order";
}

public sealed partial class MarketCityViewModel(string name, bool selected, Action changed) : ObservableObject
{
    public string Name => name;
    [ObservableProperty] private bool isSelected = selected;
    partial void OnIsSelectedChanged(bool value) => changed();
}
