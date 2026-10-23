using AlbionDataAvalonia.Market;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Crafting;

public sealed record CraftingQuote(string ItemId, int Quality, long SellPrice, DateTime? SellDate, long BuyPrice, DateTime? BuyDate);

/// <summary>Public AODP snapshots; a five-minute cache limits repeated requests without inventing missing prices.</summary>
public sealed class CraftingPriceService(HttpClient http)
{
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<MarketQuote> Quotes)> cache = new();
    public async Task<IReadOnlyList<CraftingQuote>> GetAsync(string region, string city, IEnumerable<string> itemIds, int quality,
        bool refresh, CancellationToken cancellationToken)
    {
        var quotes = await GetMarketsAsync(region, [city], itemIds, new[] { 1, quality }, refresh, cancellationToken).ConfigureAwait(false);
        return quotes.Select(x => new CraftingQuote(x.ItemId, x.Quality, x.SellPrice, x.SellDate, x.BuyPrice, x.BuyDate)).ToArray();
    }

    public async Task<IReadOnlyList<MarketQuote>> GetMarketsAsync(string region, IEnumerable<string> cities, IEnumerable<string> itemIds,
        IEnumerable<int> qualities, bool refresh, CancellationToken cancellationToken)
    {
        var locations = cities.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var levels = qualities.Distinct().OrderBy(x => x).ToArray();
        if (locations.Length == 0 || levels.Length == 0 || levels.Any(x => x is < 1 or > 5)) throw new ArgumentException("Choose cities and valid qualities.");
        var host = region switch { "Americas" => "west", "Asia" => "east", "Europe" => "europe", _ => throw new ArgumentException("Unknown region.") };
        var ids = itemIds.Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();
        var results = new List<MarketQuote>();
        foreach (var batch in ids.Chunk(20))
        {
            var uri = $"https://{host}.albion-online-data.com/api/v2/stats/prices/{string.Join(",", batch.Select(Uri.EscapeDataString))}.json?locations={string.Join(",", locations.Select(Uri.EscapeDataString))}&qualities={string.Join(",", levels)}";
            if (!refresh && cache.TryGetValue(uri, out var entry) && DateTime.UtcNow - entry.At < TimeSpan.FromMinutes(5))
            { results.AddRange(entry.Quotes); continue; }
            using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var rows = await JsonSerializer.DeserializeAsync<Row[]>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
            var quotes = rows.Where(x => batch.Contains(x.ItemId, StringComparer.Ordinal) && locations.Contains(x.City, StringComparer.OrdinalIgnoreCase) && levels.Contains(x.Quality))
                .Select(x => new MarketQuote(x.City, x.ItemId, x.Quality, Math.Max(0, x.Sell), Date(x.SellDate), Math.Max(0, x.Buy), Date(x.BuyDate))).ToArray();
            if (cache.Count >= 256)
            {
                var oldest = cache.MinBy(x => x.Value.At);
                cache.TryRemove(oldest.Key, out _);
            }
            cache[uri] = (DateTime.UtcNow, quotes);
            results.AddRange(quotes);
        }
        return results;
    }

    private static DateTime? Date(string? value) => DateTime.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) && date.Year > 2000 ? date : null;
    private sealed class Row
    {
        [JsonPropertyName("item_id")] public string ItemId { get; set; } = "";
        [JsonPropertyName("city")] public string City { get; set; } = "";
        [JsonPropertyName("quality")] public int Quality { get; set; }
        [JsonPropertyName("sell_price_min")] public long Sell { get; set; }
        [JsonPropertyName("sell_price_min_date")] public string? SellDate { get; set; }
        [JsonPropertyName("buy_price_max")] public long Buy { get; set; }
        [JsonPropertyName("buy_price_max_date")] public string? BuyDate { get; set; }
    }
}
