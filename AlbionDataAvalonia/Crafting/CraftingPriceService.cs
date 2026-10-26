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
public sealed class CraftingPriceService(HttpClient http, TimeProvider? time = null)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private const int MaximumUriLength = 3500;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<PriceKey, CacheEntry> cache = new();
    private readonly ConcurrentDictionary<string, DateTime> cooldowns = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private long cacheVersion;
    private long? lastRequest;
    private readonly record struct PriceKey(string Host, string City, string ItemId, int Quality);
    private sealed record CacheEntry(long At, long Version, MarketQuote? Quote);
    private sealed record RequestPlan(string[] Cities, string[] Items, int[] Qualities);

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
        cancellationToken.ThrowIfCancellationRequested();
        var locations = cities.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var levels = qualities.Distinct().OrderBy(x => x).ToArray();
        if (locations.Length == 0 || levels.Length == 0 || levels.Any(x => x is < 1 or > 5)) throw new ArgumentException("Choose cities and valid qualities.");
        var host = region switch { "Americas" => "west", "Asia" => "east", "Europe" => "europe", _ => throw new ArgumentException("Unknown region.") };
        var ids = itemIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var version = Volatile.Read(ref cacheVersion);
        PriceKey Key(string city, string id, int quality) => new(host, city.ToUpperInvariant(), id, quality);
        var keys = locations.SelectMany(city => ids.SelectMany(id => levels.Select(q => Key(city, id, q)))).ToArray();
        bool Fresh(PriceKey key) => cache.TryGetValue(key, out var value)
            && (refresh ? value.Version > version : clock.GetElapsedTime(value.At) < CacheLifetime);
        IReadOnlyList<MarketQuote> Read() => keys.Select(key => cache.TryGetValue(key, out var value) ? value.Quote : null)
            .OfType<MarketQuote>().ToArray();
        // Hold a snapshot while checking the fast path: another caller may evict cache entries concurrently.
        var available = keys.Select(key => cache.GetValueOrDefault(key)).ToArray();
        if (available.All(value => value is not null
            && (refresh ? value.Version > version : clock.GetElapsedTime(value.At) < CacheLifetime)))
            return available.Select(value => value!.Quote).OfType<MarketQuote>().ToArray();
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Recheck after the gate: overlapping callers share the completed response, including explicit refreshes.
            // Group identical missing item sets, then merge cities and qualities into rectangular API requests.
            var parts = levels.SelectMany(quality => locations.Select(city => new
            {
                City = city,
                Quality = quality,
                Items = ids.Where(id => !Fresh(Key(city, id, quality))).ToArray()
            }).Where(x => x.Items.Length > 0).GroupBy(x => string.Join('\0', x.Items), StringComparer.Ordinal)
                .Select(group => new RequestPlan(group.Select(x => x.City).ToArray(), group.First().Items, [quality])));
            var plans = parts.GroupBy(x => (Items: string.Join('\0', x.Items), Cities: string.Join('\0', x.Cities)))
                .Select(group => new RequestPlan(group.First().Cities, group.First().Items, group.SelectMany(x => x.Qualities).ToArray())).ToArray();
            foreach (var plan in plans)
                foreach (var batch in Batches(host, plan))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cooldowns.TryGetValue(host, out var until) && until > clock.GetUtcNow().UtcDateTime) throw new MarketPriceRateLimitException(until);
                    // At most one HTTP start per second across Crafting and Market prices.
                    if (lastRequest is { } last)
                    {
                        var wait = TimeSpan.FromSeconds(1) - clock.GetElapsedTime(last);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, clock, cancellationToken).ConfigureAwait(false);
                    }
                    lastRequest = clock.GetTimestamp();
                    using var response = await http.GetAsync(UriFor(host, plan.Cities, batch, plan.Qualities), cancellationToken).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var now = clock.GetUtcNow().UtcDateTime;
                        var retryAt = now.AddMinutes(1);
                        if (response.Headers.RetryAfter?.Date is { } retryDate) retryAt = retryDate.UtcDateTime;
                        else if (response.Headers.RetryAfter?.Delta is { } delta) retryAt = now + delta;
                        if (response.Headers.TryGetValues("RateLimit-Reset", out var reset)
                            && long.TryParse(reset.FirstOrDefault(), out var epoch) && epoch is > 0 and < 253402300800)
                            retryAt = new[] { retryAt, DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime }.Max();
                        retryAt = new[] { retryAt, now.AddSeconds(1) }.Max();
                        cooldowns[host] = retryAt;
                        throw new MarketPriceRateLimitException(retryAt);
                    }
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var rows = await JsonSerializer.DeserializeAsync<Row[]>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
                    var quotes = rows.Where(x => batch.Contains(x.ItemId, StringComparer.Ordinal)
                        && plan.Cities.Contains(x.City, StringComparer.OrdinalIgnoreCase) && plan.Qualities.Contains(x.Quality))
                        .GroupBy(x => Key(x.City, x.ItemId, x.Quality)).ToDictionary(x => x.Key, x =>
                        {
                            var row = x.First();
                            return new MarketQuote(row.City, row.ItemId, row.Quality, Math.Max(0, row.Sell), Date(row.SellDate), Math.Max(0, row.Buy), Date(row.BuyDate));
                        });
                    var stamp = clock.GetTimestamp();
                    var written = Interlocked.Increment(ref cacheVersion);
                    // Cache missing rows too: an unavailable quote must not cause repeated calls on every filter change.
                    foreach (var city in plan.Cities)
                        foreach (var id in batch)
                            foreach (var quality in plan.Qualities)
                            {
                                var key = Key(city, id, quality);
                                cache[key] = new(stamp, written, quotes.GetValueOrDefault(key));
                            }
                }
            var result = Read();
            if (cache.Count > 20000)
                foreach (var oldest in cache.OrderBy(x => x.Value.At).Take(cache.Count - 16000)) cache.TryRemove(oldest.Key, out _);
            return result;
        }
        finally { requests.Release(); }
    }

    private static string UriFor(string host, string[] cities, IEnumerable<string> items, int[] qualities) =>
        $"https://{host}.albion-online-data.com/api/v2/stats/prices/{string.Join(",", items.Select(Uri.EscapeDataString))}.json?locations={string.Join(",", cities.Select(Uri.EscapeDataString))}&qualities={string.Join(",", qualities)}";

    private static IEnumerable<string[]> Batches(string host, RequestPlan plan)
    {
        var batch = new List<string>();
        foreach (var id in plan.Items)
        {
            batch.Add(id);
            if (UriFor(host, plan.Cities, batch, plan.Qualities).Length <= MaximumUriLength) continue;
            batch.RemoveAt(batch.Count - 1);
            if (batch.Count == 0) throw new ArgumentException("A market item ID exceeds the request URL limit.");
            yield return batch.ToArray();
            batch.Clear(); batch.Add(id);
            if (UriFor(host, plan.Cities, batch, plan.Qualities).Length > MaximumUriLength) throw new ArgumentException("A market item ID exceeds the request URL limit.");
        }
        if (batch.Count > 0) yield return batch.ToArray();
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
