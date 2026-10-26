using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Market;

public enum MarketAction { BuyInstantly, BuyOrder, SellInstantly, SellOrder }
public enum MarketResultOrder { ItemPower, TotalPrice, PricePerPower }
public sealed record MarketQuote(string City, string ItemId, int Quality, long SellPrice, DateTime? SellDate, long BuyPrice, DateTime? BuyDate);
public sealed record MarketCandidate(MarketItem Item, int Quality, decimal? ItemPower = null);
public sealed record MarketResult(string City, MarketCandidate? Candidate, decimal? UnitPrice, decimal? Total, DateTime? ObservedAt);
public sealed record MarketPowerBand(int Minimum, int Maximum, MarketResult? Best);

public static class MarketCalculator
{
    public static bool IsBuying(MarketAction action) => action is MarketAction.BuyInstantly or MarketAction.BuyOrder;
    private static IOrderedEnumerable<MarketResult> Rank(IEnumerable<MarketResult> values, MarketAction action) => values
        .OrderBy(x => IsBuying(action) ? x.Total : -x.Total).ThenByDescending(x => x.ObservedAt)
        .ThenBy(x => x.Candidate!.Item.ItemId, StringComparer.Ordinal).ThenBy(x => x.Candidate!.Quality).ThenBy(x => x.City, StringComparer.Ordinal);

    public static IReadOnlyList<MarketResult> Calculate(IReadOnlyList<string> cities, IReadOnlyList<MarketCandidate> candidates,
        IReadOnlyList<MarketQuote> quotes, MarketAction action, decimal quantity, decimal salesTax, decimal setupFee,
        TimeSpan? maximumAge, DateTime now)
    {
        var offers = Offers(cities, candidates, quotes, action, quantity, salesTax, setupFee, maximumAge, now).ToLookup(x => x.City, StringComparer.OrdinalIgnoreCase);
        return cities.Select(city => Rank(offers[city], action).FirstOrDefault() ?? new MarketResult(city, null, null, null, null))
            .OrderBy(x => x.Total is null).ThenBy(x => IsBuying(action) ? x.Total : -x.Total).ThenBy(x => x.City, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<MarketPowerBand> ByPower(IReadOnlyList<string> cities, IReadOnlyList<MarketCandidate> candidates,
        IReadOnlyList<MarketQuote> quotes, MarketAction action, decimal quantity, decimal salesTax, decimal setupFee,
        TimeSpan? maximumAge, DateTime now, int interval)
    {
        if (interval is not (25 or 50 or 100)) throw new ArgumentOutOfRangeException(nameof(interval));
        var powered = candidates.Where(x => x.ItemPower is >= 0 and <= 10000).ToArray();
        if (powered.Length == 0) return [];
        int Bucket(decimal power) => (int)decimal.Floor(power / interval) * interval;
        var offers = Offers(cities, powered, quotes, action, quantity, salesTax, setupFee, maximumAge, now)
            .ToLookup(x => Bucket(x.Candidate!.ItemPower!.Value));
        var first = Bucket(powered.Min(x => x.ItemPower!.Value));
        var last = Bucket(powered.Max(x => x.ItemPower!.Value));
        return Enumerable.Range(0, (last - first) / interval + 1).Select(i =>
        {
            var minimum = first + i * interval;
            return new MarketPowerBand(minimum, minimum + interval - 1, Rank(offers[minimum], action).FirstOrDefault());
        }).ToArray();
    }

    private static IEnumerable<MarketResult> Offers(IReadOnlyList<string> cities, IReadOnlyList<MarketCandidate> candidates,
        IReadOnlyList<MarketQuote> quotes, MarketAction action, decimal quantity, decimal salesTax, decimal setupFee,
        TimeSpan? maximumAge, DateTime now)
    {
        if (!Enum.IsDefined(action) || quantity is < 1 or > 1_000_000 || quantity != decimal.Truncate(quantity)
            || salesTax is < 0 or >= 100 || setupFee is < 0 or >= 100 || salesTax + setupFee >= 100
            || maximumAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quantity));
        var items = candidates.DistinctBy(x => (x.Item.ItemId, x.Quality)).ToDictionary(x => (x.Item.ItemId, x.Quality));
        var markets = cities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var useSell = action is MarketAction.BuyInstantly or MarketAction.SellOrder;
        var factor = action switch
        {
            MarketAction.BuyOrder => 1 + setupFee / 100,
            MarketAction.SellInstantly => 1 - salesTax / 100,
            MarketAction.SellOrder => 1 - (salesTax + setupFee) / 100,
            _ => 1
        };
        return quotes.Where(x => markets.Contains(x.City) && items.ContainsKey((x.ItemId, x.Quality)))
            .Select(x => (Quote: x, Price: useSell ? x.SellPrice : x.BuyPrice, Date: useSell ? x.SellDate : x.BuyDate))
            .Where(x => x.Price > 0 && (x.Date is null ? maximumAge is null
                : x.Date <= now.AddMinutes(5) && (maximumAge is null || now - x.Date <= maximumAge)))
            .Select(x => new MarketResult(x.Quote.City, items[(x.Quote.ItemId, x.Quote.Quality)], x.Price * factor,
                x.Price * factor * quantity, x.Date));
    }
}
