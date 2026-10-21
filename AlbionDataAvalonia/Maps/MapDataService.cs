using AlbionDataAvalonia.Locations;
using Avalonia.Media.Imaging;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Maps;

/// <summary>
/// Loads the AlbionWorld zone catalog, minimaps, minimap icons and zone features from the AFM CDN.
/// The CDN publishes a version that changes with these files, so a downloaded copy never changes and is kept
/// until the version moves on.
/// </summary>
public sealed class MapDataService : IDisposable
{
    private const string VersionUrl = "https://cdn.albionfreemarket.com/AlbionWorld/map-data-version.json";
    private const string CatalogUrl = "https://cdn.albionfreemarket.com/AlbionWorld/albionLocations.json";
    private const string MapsBase = "https://cdn.albionfreemarket.com/AlbionWorld/map";
    private const string FeaturesBase = "https://cdn.albionfreemarket.com/AlbionWorld/map-features";
    // Used while no version has ever been read from the CDN.
    private const string Unversioned = "unversioned";
    // A running app looks for a new version now and then, so an AlbionWorld update reaches it without a restart.
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan VersionRetryDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(10);
    private const int CachedImages = 3;
    // Royal outpost icons are a small banner in a large image; the website draws them bigger too.
    private const double OutpostIconScale = 1.65;
    private static readonly HashSet<string> OutpostIcons = ["Outpost_Forest", "Outpost_Highland", "Outpost_Mountain", "Outpost_Steppe", "Outpost_Swamp"];
    // A boss portrait reads at half again an icon's size, like a target frame.
    private const double WorldBossScale = 1.5;
    // Roads chests carry their spawn tile's name; these read better.
    private static readonly Dictionary<string, string> IconNames = new(StringComparer.Ordinal)
    {
        ["roads_of_avalon_solo_pve"] = "Avalonian chest (solo)",
        ["roads_of_avalon_group_pve"] = "Avalonian chest (group)",
        ["roads_of_avalon_raid_pve"] = "Avalonian chest (elite)",
    };

    // The CDN compresses the map catalog and feature files about ten times when asked.
    private readonly HttpClient http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly SemaphoreSlim downloads = new(3);
    private readonly object sync = new();
    private readonly ConcurrentDictionary<string, Task<MapZoneFeatures>> features = new();
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> iconImages = new();
    private readonly LinkedList<(string File, Task<Bitmap?> Image)> images = new();
    private Task<Bitmap?>? frameImage;
    private Task<IReadOnlyDictionary<string, MapZone>>? catalog;
    private string? version;
    private bool? versionFromCdn;
    private Task? versionCheck;
    private DateTime nextVersionCheck;
    private bool staleVersionsRemoved;

    private static string CacheRoot => Path.Combine(AppData.LocalPath, "cache", "maps");
    // The last version read from the CDN, so cached maps still show when it can't be reached.
    private static string LastVersionFile => Path.Combine(CacheRoot, "version.txt");

    /// <summary>The catalog zone for a captured map id, trying the full id first and then its '@' parts.
    /// A Mists instance has no zone of its own, so its fixed layout is tried last; a Mists dungeon's layout has no catalog
    /// zone either, so the client's own map of it stands in.</summary>
    public async Task<MapZone?> FindZoneAsync(string rawMapId, string? layout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawMapId)) return null;
        var zones = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        foreach (var candidate in Candidates(rawMapId))
            if (zones.TryGetValue(candidate, out var zone)) return zone;
        if (string.IsNullOrWhiteSpace(layout)) return null;
        return zones.TryGetValue(layout, out var layoutZone) ? layoutZone : MistsMapData.DungeonZone(layout);
    }

    public async Task<string?> ZoneNameAsync(string id, CancellationToken cancellationToken = default) =>
        (await GetCatalogAsync(cancellationToken).ConfigureAwait(false)).TryGetValue(id, out var zone) ? zone.DisplayName : null;

    public async Task<MapZone?> ZoneAsync(string id, CancellationToken cancellationToken = default) =>
        (await GetCatalogAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(id);

    /// <summary>The zone's minimap, or null when the CDN has none. A failed download throws.</summary>
    public Task<Bitmap?> GetImageAsync(MapZone zone)
    {
        if (zone.ImageFile.Length == 0) return Task.FromResult<Bitmap?>(null);
        lock (sync)
        {
            var cached = images.FirstOrDefault(x => x.File == zone.ImageFile);
            // A failed download is tried again; a missing image is not.
            if (cached.Image is { IsFaulted: true } or { IsCanceled: true }) images.Remove(cached);
            else if (cached.Image is not null)
            {
                images.Remove(cached);
                images.AddFirst(cached);
                return cached.Image;
            }

            var name = Path.GetFileNameWithoutExtension(zone.ImageFile);
            var image = LoadBitmapAsync($"{MapsBase}/images/{Uri.EscapeDataString(name)}.webp", Path.Combine("images", SafeFileName(name) + ".webp"));
            images.AddFirst((zone.ImageFile, image));
            // Decoded open-world minimaps are large, so only the last few stay in memory.
            while (images.Count > CachedImages) images.RemoveLast();
            return image;
        }
    }

    /// <summary>The in-game zone map frame, or null when it can't be had.</summary>
    public Task<Bitmap?> GetFrameImageAsync()
    {
        lock (sync) return frameImage ??= LoadFrameImageAsync();
    }

    // A map still shows without its frame, so a failed download only leaves it out until it is asked for again.
    private async Task<Bitmap?> LoadFrameImageAsync()
    {
        try
        {
            return await LoadBitmapAsync($"{FeaturesBase}/frame/map-frame.webp", Path.Combine("frame", "map-frame.webp")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warning(error, "Could not load the map frame");
            lock (sync) frameImage = null;
            return null;
        }
    }

    /// <summary>The zone's minimap icons, resource spawn spots and points of interest; empty when its feature file can't be had,
    /// but for the Mists chest spots that come with the app.</summary>
    public Task<MapZoneFeatures> GetFeaturesAsync(MapZone zone) =>
        // A zone the client draws itself has no AlbionWorld feature file.
        zone.ImageFile.Length == 0 ? Task.FromResult(new MapZoneFeatures([], [], MistsMapData.Chests(zone.Id)))
            : features.GetOrAdd(zone.Id, _ => LoadFeaturesAsync(zone));

    public Task<Bitmap?> GetIconImageAsync(string markerType) =>
        iconImages.GetOrAdd(markerType, LoadIconImageAsync);

    // A map still shows without an icon, so a failed icon download only leaves it out until it is asked for again.
    private async Task<Bitmap?> LoadIconImageAsync(string markerType)
    {
        try
        {
            return await LoadBitmapAsync($"{FeaturesBase}/icons/minimap/{Uri.EscapeDataString(markerType)}.png",
                Path.Combine("icons", SafeFileName(markerType) + ".png")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warning(error, "Could not load map icon {MarkerType}", markerType);
            iconImages.TryRemove(markerType, out _);
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, MapZone>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        // Every map change comes through here, so this is also where a running app notices a new version.
        var current = await GetVersionAsync().ConfigureAwait(false);
        Task<IReadOnlyDictionary<string, MapZone>> loading;
        lock (sync)
        {
            if (catalog is null || catalog.IsFaulted || catalog.IsCanceled) catalog = LoadCatalogAsync(current);
            loading = catalog;
        }

        return await loading.ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, MapZone>> LoadCatalogAsync(string current)
    {
        var path = await DownloadAsync(CatalogUrl, "albionLocations.json").ConfigureAwait(false);
        await using var stream = File.OpenRead(path);
        var entries = await JsonSerializer.DeserializeAsync<List<CatalogEntry>>(stream, JsonOptions).ConfigureAwait(false) ?? [];
        var zones = new Dictionary<string, MapZone>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.ImageFile)) continue;
            var min = Point(entry.MinimapBoundsMin) ?? Point(entry.Origin);
            var max = Point(entry.MinimapBoundsMax) ?? (Point(entry.Origin) is { } origin && Point(entry.Size) is { } size
                ? new MapPoint(origin.X + size.X, origin.Y + size.Y) : null);
            if (min is null || max is null) continue;
            var exits = (entry.Exits ?? []).Concat(entry.PortalExits ?? []).Concat(entry.PortalEntrances ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x.TargetLocationId) && Point(x.Position) is not null)
                .Select(x => new MapZoneExit(x.TargetLocationId!, Point(x.Position)!.Value))
                .DistinctBy(x => (x.Position, x.TargetId)).ToArray();
            zones[entry.Id] = new MapZone(entry.Id, string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id : entry.DisplayName,
                entry.ImageFile, entry.MapCategory ?? "", entry.PvpCategory ?? "",
                int.TryParse(entry.Tier, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier) ? tier : null,
                min.Value, max.Value, exits);
        }

        Log.Information("Loaded {ZoneCount} map zones for map data version {Version}", zones.Count, current);
        return zones;
    }

    /// <summary>The map data version in use and whether the last check reached the CDN, for Copy diagnostics.
    /// The version stays null until a map or zone name is first needed.</summary>
    public (string? Version, bool? FromCdn) VersionStatus
    {
        get
        {
            lock (sync) return (version, versionFromCdn);
        }
    }

    /// <summary>The version the cached files belong to. Only the first check is waited for; later ones switch
    /// the version when they finish.</summary>
    private async Task<string> GetVersionAsync()
    {
        Task check;
        lock (sync)
        {
            if (versionCheck is null || versionCheck.IsCompleted && DateTime.UtcNow >= nextVersionCheck)
                versionCheck = CheckVersionAsync();

            if (version is not null) return version;
            check = versionCheck;
        }

        await check.ConfigureAwait(false);
        lock (sync) return version!;
    }

    private async Task CheckVersionAsync()
    {
        var published = await LoadPublishedVersionAsync().ConfigureAwait(false);
        lock (sync)
        {
            versionFromCdn = published is not null;
            nextVersionCheck = DateTime.UtcNow + (published is null ? VersionRetryDelay : VersionCheckInterval);
            var next = published ?? version ?? ReadLastVersion() ?? Unversioned;
            if (next == version) return;
            if (version is not null)
            {
                // Everything loaded so far belongs to the old version.
                Log.Information("Map data version changed from {OldVersion} to {Version}", version, next);
                catalog = null;
                features.Clear();
                iconImages.Clear();
                images.Clear();
                frameImage = null;
            }

            version = next;
        }

        if (published is not null) SaveLastVersion(published);
    }

    private async Task<string?> LoadPublishedVersionAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(VersionCheckTimeout);
            // Cloudflare keeps CDN files for two hours whatever the server says, so each minute asks for a new copy.
            var url = $"{VersionUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60}";
            var file = await http.GetFromJsonAsync<VersionFile>(url, JsonOptions, timeout.Token).ConfigureAwait(false);
            if (IsValidVersion(file?.Version)) return file!.Version;
            Log.Warning("The map data version file has no usable version");
        }
        catch (Exception error)
        {
            // Any failure only means the cached version is kept until the next check.
            Log.Warning(error, "Could not check the map data version");
        }

        return null;
    }

    private static string? ReadLastVersion()
    {
        try
        {
            var saved = File.Exists(LastVersionFile) ? File.ReadAllText(LastVersionFile).Trim() : null;
            return IsValidVersion(saved) ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Debug(error, "Could not read the last map data version");
            return null;
        }
    }

    private static void SaveLastVersion(string value)
    {
        try
        {
            Directory.CreateDirectory(CacheRoot);
            File.WriteAllText(LastVersionFile, value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Debug(error, "Could not save the map data version");
        }
    }

    // The version names a cache directory, so it may only hold letters, digits, '-' and '_'.
    private static bool IsValidVersion(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private async Task<MapZoneFeatures> LoadFeaturesAsync(MapZone zone)
    {
        try
        {
            var path = await DownloadAsync($"{FeaturesBase}/maps/{Uri.EscapeDataString(zone.Id)}.json",
                Path.Combine("features", SafeFileName(zone.Id) + ".json")).ConfigureAwait(false);
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<FeatureFile>(stream, JsonOptions).ConfigureAwait(false);
            var placed = (file?.Features ?? [])
                .Where(x => x.X is { } featureX && x.Z is { } featureZ && double.IsFinite(featureX) && double.IsFinite(featureZ))
                .ToArray();
            return new(placed.Select(Icon).OfType<MapZoneIcon>().ToArray(), placed.Select(ResourceSpawn).OfType<MapResourceSpawn>().ToArray(),
                [.. placed.Select(Poi).OfType<MapPoi>(), .. MistsMapData.Chests(zone.Id)]);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            Log.Warning(error, "Could not load map features for {ZoneId}", zone.Id);
            features.TryRemove(zone.Id, out _);
            // The Mists chest spots come with the app, so they still show.
            return new([], [], MistsMapData.Chests(zone.Id));
        }
    }

    // AlbionWorld lists what a layout places by the game's spawn point names ("Spawn Point Lootchest Static Morgana
    // Veteran", "Spawn Point Random Demon Cd Endboss"): loot chests, mobs (of which bosses are shown), shrines, the spots
    // the game picks mob camps from and the Mists wisp spawns. A Hellgate's objective chest is a named marker point. A
    // chest with a minimap icon, such as a Roads chest, already shows as that icon.
    private static MapPoi? Poi(Feature feature)
    {
        var position = new MapPoint(feature.X!.Value, feature.Z!.Value);
        switch (feature.Type)
        {
            case "lootchestspawnpoint" when string.IsNullOrWhiteSpace(feature.MarkerType):
                var chest = SpawnName(feature.Label, "Spawn Point Lootchest");
                if (chest.Length == 0) chest = "Chest";
                else if (!chest.Contains("chest", StringComparison.OrdinalIgnoreCase)) chest += " chest";
                return new(MapPoiKind.Chest, chest, ChestGrade(chest), position);
            case "namedmarkerpoint" when feature.Label == "HellgateObjectiveChest":
                return new(MapPoiKind.Chest, "Hellgate chest", 3, position);
            case "mobspawnpoint":
                var mob = SpawnName(feature.Label, "Spawn Point Random");
                var words = Words(mob);
                if (!words.Any(x => x is "BOSS" or "ENDBOSS" or "MINIBOSS")) return null;
                return new(MapPoiKind.Boss, mob, words.Contains("MINIBOSS") ? 2 : 3, position);
            case "mobcamp_candidate":
                var grade = feature.Band?.ToLowerInvariant() switch { "high" => 3, "medium" => 2, _ => 1 };
                var band = string.IsNullOrWhiteSpace(feature.Band) ? "" : $"{char.ToUpperInvariant(feature.Band[0])}{feature.Band[1..].ToLowerInvariant()} ";
                return new(MapPoiKind.Camp, $"{band}mob camp spot{(feature.Tier is >= 1 and <= 8 ? $", T{feature.Tier}" : "")}".Trim(), grade, position);
            case "mistswispspawn":
                return new(MapPoiKind.Wisp, "Mists wisp spawn", 1, position);
            case { } type when type.Contains("shrine", StringComparison.OrdinalIgnoreCase):
                var shrine = Humanize(string.IsNullOrWhiteSpace(feature.Label) ? type : feature.Label);
                return new(MapPoiKind.Shrine, shrine.Contains("shrine", StringComparison.OrdinalIgnoreCase) ? shrine : $"{shrine} shrine", 2, position);
            default:
                return null;
        }
    }

    // "Spawn Point Lootchest Static Morgana Veteran" reads as "Static Morgana Veteran"; a raw name is all capitals.
    private static string SpawnName(string? label, string prefix)
    {
        var name = Humanize(label ?? "");
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name[prefix.Length..].Trim();
        if (!name.Any(char.IsLower)) name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        // A mob spawn point that takes any kind of mob ends in "Any".
        return name.EndsWith(" Any", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static string[] Words(string name) => name.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int ChestGrade(string name)
    {
        var words = Words(name);
        if (words.Any(x => x is "BOSS" or "ENDBOSS" or "MINIBOSS" or "VETERAN" or "ELITE" or "LARGE" or "RAID" or "LEGENDARY" or "EPIC")) return 3;
        return words.Any(x => x is "MEDIUM" or "GROUP" or "DUO" or "UNCOMMON" or "RARE" or "STANDARD") ? 2 : 1;
    }

    // AlbionWorld lists, by the game's resource type and tier, the spots an open-world zone picks its ordinary nodes
    // from ("resource_candidate", thousands a zone), the nodes a layout section places itself ("harvestable") and the
    // mobs that yield resources ("resource_mob_spawnpoint").
    private static MapResourceSpawn? ResourceSpawn(Feature feature)
    {
        var mob = feature.Type == "resource_mob_spawnpoint";
        var candidate = feature.Type == "resource_candidate";
        if (feature.Type != "harvestable" && !mob && !candidate || feature.Tier is not (>= 1 and <= 8)
            || Family(feature.ResourceType) is not { } family) return null;
        return new(family, feature.Tier.Value, new MapPoint(feature.X!.Value, feature.Z!.Value), mob, candidate);
    }

    private static string? Family(string? resourceType) => resourceType?.ToUpperInvariant() switch
    {
        "WOOD" => "Wood",
        "ROCK" or "STONE" => "Stone",
        "FIBER" => "Fiber",
        "HIDE" => "Hide",
        "ORE" => "Ore",
        _ => null
    };

    // Any feature with a minimap icon (markers, resource landmarks, Roads chests) and world bosses, drawn with their
    // portrait. An icon on an exit's spot is kept; the map shows it in place of that exit's dot.
    private static MapZoneIcon? Icon(Feature feature)
    {
        var position = new MapPoint(feature.X!.Value, feature.Z!.Value);
        if (feature.Type == "worldboss" && !string.IsNullOrWhiteSpace(feature.Mob))
            return new(null, feature.Label?.Contains("_DAILY_", StringComparison.Ordinal) == true ? "Daily world boss" : "World boss", position,
                WorldBossScale, feature.Mob);
        if (string.IsNullOrWhiteSpace(feature.MarkerType) || feature.MarkerType == "ClusterExit") return null;
        var label = IconNames.GetValueOrDefault(feature.MarkerType)
            ?? Humanize(feature.Type is "minimap_marker" or "resource_landmark" && !string.IsNullOrWhiteSpace(feature.Label) ? feature.Label : feature.MarkerType);
        return new(feature.MarkerType, label, position, OutpostIcons.Contains(feature.MarkerType) ? OutpostIconScale : 1);
    }

    // Null when the CDN has no such file or it can't be decoded; a failed download throws.
    private async Task<Bitmap?> LoadBitmapAsync(string url, string relativePath)
    {
        try
        {
            var path = await DownloadAsync(url, relativePath).ConfigureAwait(false);
            return new Bitmap(path);
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            Log.Information("No map image at {Url}", url);
            return null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            Log.Warning(error, "Could not read map image {Url}", url);
            return null;
        }
    }

    // Writes to a temporary file first so a cut download never leaves a broken cache entry.
    private async Task<string> DownloadAsync(string url, string relativePath)
    {
        var current = await GetVersionAsync().ConfigureAwait(false);
        var path = Path.Combine(CacheRoot, current, relativePath);
        if (File.Exists(path)) return path;
        RemoveStaleVersions(current);
        await downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var versioned = $"{url}{(url.Contains('?') ? '&' : '?')}v={Uri.EscapeDataString(current)}";
            using var response = await http.GetAsync(versioned, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var temporary = path + ".download";
            await using (var file = File.Create(temporary))
                await response.Content.CopyToAsync(file).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            downloads.Release();
        }
    }

    // Runs once per start, so a version that changes while the app runs is cleaned up on the next start.
    private void RemoveStaleVersions(string current)
    {
        lock (sync)
        {
            if (staleVersionsRemoved) return;
            staleVersionsRemoved = true;
        }

        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(CacheRoot)
                         .Where(x => !string.Equals(Path.GetFileName(x), current, StringComparison.OrdinalIgnoreCase)))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Debug(error, "Could not remove old map data");
        }
    }

    // A generated instance (a random dungeon, the Mists, an island, a hideout) has no catalog zone of its own,
    // and its id can name the zone it sits in ("@HIDEOUT@2306@<guid>"), so only its full id may match.
    private static IEnumerable<string> Candidates(string rawMapId)
    {
        var trimmed = rawMapId.Trim().Trim('"', '\'');
        if (trimmed.Length == 0) yield break;
        yield return trimmed;
        if (IsGeneratedInstance(trimmed)) yield break;
        foreach (var part in trimmed.Split('@', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
            if (!string.Equals(part, trimmed, StringComparison.Ordinal)) yield return part;
    }

    private static bool IsGeneratedInstance(string id) => id.Contains('@') && AlbionLocations.ResolveActivityLocation(id).Kind is
        ActivityMapKind.RandomDungeon or ActivityMapKind.CorruptedDungeon or ActivityMapKind.Hellgate or ActivityMapKind.Expedition
        or ActivityMapKind.Mists or ActivityMapKind.MistsDungeon or ActivityMapKind.AbyssalDepths or ActivityMapKind.DragonArea
        or ActivityMapKind.Arena or ActivityMapKind.Island or ActivityMapKind.Hideout;

    private static MapPoint? Point(double[]? values) =>
        values is { Length: >= 2 } && double.IsFinite(values[0]) && double.IsFinite(values[1]) ? new MapPoint(values[0], values[1]) : null;

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    // "Outpost_Mountain" and "bankVault" read as "Outpost Mountain" and "Bank Vault".
    private static string Humanize(string value)
    {
        var text = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (c is '_' or '-') { text.Append(' '); continue; }
            if (index > 0 && char.IsUpper(c) && char.IsLetterOrDigit(value[index - 1]) && !char.IsUpper(value[index - 1])) text.Append(' ');
            text.Append(index == 0 ? char.ToUpperInvariant(c) : c);
        }

        return text.ToString().Trim();
    }

    public void Dispose()
    {
        http.Dispose();
        downloads.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class VersionFile
    {
        public string? Version { get; set; }
    }

    private sealed class CatalogEntry
    {
        public string? Id { get; set; }
        public string? ImageFile { get; set; }
        public string? DisplayName { get; set; }
        public string? Tier { get; set; }
        public string? MapCategory { get; set; }
        public string? PvpCategory { get; set; }
        public double[]? Origin { get; set; }
        public double[]? Size { get; set; }
        public double[]? MinimapBoundsMin { get; set; }
        public double[]? MinimapBoundsMax { get; set; }
        public List<CatalogExit>? Exits { get; set; }
        public List<CatalogExit>? PortalExits { get; set; }
        public List<CatalogExit>? PortalEntrances { get; set; }
    }

    private sealed class CatalogExit
    {
        public string? TargetLocationId { get; set; }
        public double[]? Position { get; set; }
    }

    private sealed class FeatureFile
    {
        public List<Feature>? Features { get; set; }
    }

    private sealed class Feature
    {
        public string? Type { get; set; }
        public string? MarkerType { get; set; }
        public string? Label { get; set; }
        public string? Mob { get; set; }
        public string? ResourceType { get; set; }
        public string? Band { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? Tier { get; set; }
        public double? X { get; set; }
        public double? Z { get; set; }
    }
}
