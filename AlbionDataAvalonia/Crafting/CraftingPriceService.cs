using AlbionDataAvalonia.Market;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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
    private readonly ConcurrentDictionary<string, DateTime> cooldowns = new();
    private readonly SemaphoreSlim requests = new(1, 1);

    public static HttpClient CreateHttpClient()
    {
        // AODP restricts non-compressed requests to one per five minutes.
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli })
        { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AFMDataClient/1.0");
        return client;
    }

    public static string ErrorText(Exception failure) => failure switch
    {
        MarketPriceRateLimitException limited => $"AODP rate limit (HTTP 429). Requests paused until {limited.RetryAt.ToLocalTime():HH:mm:ss}. Refresh after this time.",
        HttpRequestException { StatusCode: { } status } => $"AODP returned HTTP {(int)status}. Prices are unavailable; try refreshing later.",
        OperationCanceledException => "AODP request timed out. Try refreshing again.",
        JsonException => "AODP returned an unreadable price response. Try refreshing later.",
        _ => "Couldn't connect to AODP. Check your connection and refresh."
    };

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
            Row[] rows;
            await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cooldowns.TryGetValue(host, out var until) && until > DateTime.UtcNow) throw new MarketPriceRateLimitException(until);
                using var response = await http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAt = DateTime.UtcNow.AddMinutes(1);
                    if (response.Headers.RetryAfter?.Date is { } retryDate) retryAt = retryDate.UtcDateTime;
                    else if (response.Headers.RetryAfter?.Delta is { } delta) retryAt = DateTime.UtcNow + delta;
                    if (response.Headers.TryGetValues("RateLimit-Reset", out var reset)
                        && long.TryParse(reset.FirstOrDefault(), out var epoch) && epoch is > 0 and < 253402300800)
                        retryAt = new[] { retryAt, DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime }.Max();
                    retryAt = new[] { retryAt, DateTime.UtcNow.AddSeconds(1) }.Max();
                    cooldowns[host] = retryAt;
                    throw new MarketPriceRateLimitException(retryAt);
                }
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                rows = await JsonSerializer.DeserializeAsync<Row[]>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
            }
            finally { requests.Release(); }
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
        [JsonPropertyName("quality"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int Quality { get; set; }
        [JsonPropertyName("sell_price_min"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long Sell { get; set; }
        [JsonPropertyName("sell_price_min_date")] public string? SellDate { get; set; }
        [JsonPropertyName("buy_price_max"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long Buy { get; set; }
        [JsonPropertyName("buy_price_max_date")] public string? BuyDate { get; set; }
    }
}

public sealed class MarketPriceRateLimitException(DateTime retryAt) : HttpRequestException("AODP rate limit reached.", null, HttpStatusCode.TooManyRequests)
{
    public DateTime RetryAt { get; } = retryAt;
}
