using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Market;

public enum MarketAction { BuyInstantly, BuyOrder, SellInstantly, SellOrder }
public sealed record MarketQuote(string City, string ItemId, int Quality, long SellPrice, DateTime? SellDate, long BuyPrice, DateTime? BuyDate);
public sealed record MarketCandidate(MarketItem Item, int Quality, decimal? ItemPower = null);
public sealed record MarketResult(string City, MarketCandidate? Candidate, decimal? UnitPrice, decimal? Total, DateTime? ObservedAt);

public static class MarketCalculator
{
    public static bool IsBuying(MarketAction action) => action is MarketAction.BuyInstantly or MarketAction.BuyOrder;
    public static IReadOnlyList<MarketResult> Calculate(IReadOnlyList<string> cities, IReadOnlyList<MarketCandidate> candidates,
        IReadOnlyList<MarketQuote> quotes, MarketAction action, decimal quantity, decimal salesTax, decimal setupFee,
        TimeSpan? maximumAge, DateTime now)
    {
        if (!Enum.IsDefined(action) || quantity is < 1 or > 1_000_000 || quantity != decimal.Truncate(quantity)
            || salesTax is < 0 or >= 100 || setupFee is < 0 or >= 100 || salesTax + setupFee >= 100
            || maximumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quantity));
        var items = candidates.DistinctBy(x => (x.Item.ItemId, x.Quality)).ToDictionary(x => (x.Item.ItemId, x.Quality));
        var buying = IsBuying(action);
        var useSell = action is MarketAction.BuyInstantly or MarketAction.SellOrder;
        var factor = action switch
        {
            MarketAction.BuyOrder => 1 + setupFee / 100,
            MarketAction.SellInstantly => 1 - salesTax / 100,
            MarketAction.SellOrder => 1 - (salesTax + setupFee) / 100,
            _ => 1
        };
        return cities.Select(city =>
        {
            var choices = quotes.Where(x => string.Equals(x.City, city, StringComparison.OrdinalIgnoreCase) && items.ContainsKey((x.ItemId, x.Quality)))
                .Select(x => (Quote: x, Price: useSell ? x.SellPrice : x.BuyPrice, Date: useSell ? x.SellDate : x.BuyDate))
                .Where(x => x.Price > 0 && (x.Date is null ? maximumAge is null
                    : x.Date <= now.AddMinutes(5) && (maximumAge is null || now - x.Date <= maximumAge)))
                .Select(x => new MarketResult(city, items[(x.Quote.ItemId, x.Quote.Quality)], x.Price * factor, x.Price * factor * quantity, x.Date));
            return (buying ? choices.OrderBy(x => x.Total) : choices.OrderByDescending(x => x.Total))
                .ThenByDescending(x => x.ObservedAt).ThenBy(x => x.Candidate!.Item.ItemId, StringComparer.Ordinal).ThenBy(x => x.Candidate!.Quality)
                .FirstOrDefault() ?? new MarketResult(city, null, null, null, null);
        }).OrderBy(x => x.Total is null).ThenBy(x => buying ? x.Total : -x.Total).ThenBy(x => x.City, StringComparer.Ordinal).ToArray();
    }
}
