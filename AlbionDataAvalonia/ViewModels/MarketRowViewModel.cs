using AlbionDataAvalonia.Market;
using AlbionDataAvalonia.Ui;
using System;

namespace AlbionDataAvalonia.ViewModels;

public sealed class MarketRowViewModel(MarketResult result, bool best, DateTime now)
{
    public string City => result.City;
    public string Rank => best ? "Best" : "";
    public bool IsBest => best;
    public string ItemId => result.Candidate?.Item.ItemId ?? "";
    public int Quality => result.Candidate?.Quality ?? 1;
    public string ItemName => result.Candidate?.Item.Label ?? "No matching price";
    public string QualityText => result.Candidate is { } candidate ? QualityName(candidate.Quality) : "";
    public string PowerText => result.Candidate?.ItemPower is { } power ? power == decimal.Truncate(power) ? UiFormat.Whole((double)power) : UiFormat.Decimal(power) : UiText.None;
    public string UnitText => result.UnitPrice is { } value ? UiFormat.Decimal(value) : UiText.None;
    public string TotalText => result.Total is { } value ? UiFormat.Decimal(value) : UiText.None;
    public string SnapshotText => result.Candidate is null ? UiText.None : result.ObservedAt is { } date ? UiFormat.Ago(date, now) : "Time unknown";
    public static string QualityName(int quality) => quality switch { 2 => "Good", 3 => "Outstanding", 4 => "Excellent", 5 => "Masterpiece", _ => "Normal" };
}
