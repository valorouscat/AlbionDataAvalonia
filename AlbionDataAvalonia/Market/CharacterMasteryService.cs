using AlbionDataAvalonia.Items.Services;
using AlbionDataAvalonia.Network.Events;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace AlbionDataAvalonia.Market;

public sealed record MasteryProfile(string Region, string Character, DateTime CapturedAtUtc, Dictionary<string, int> Levels)
{
    public string Key => Region + ":" + Character;
}

/// <summary>Uses the same decoded Destiny Board levels that the client uploads to AFM, without another profile API.</summary>
public sealed class CharacterMasteryService
{
    private readonly object sync = new();
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private readonly Dictionary<string, MasteryProfile> profiles = new(StringComparer.Ordinal);
    private readonly string file;
    private readonly Lazy<Task<PowerReward[]>> rewards = new(() => Task.Run(LoadRewards));
    public event Action? Changed;
    public CharacterMasteryService() : this(Path.Combine(AppData.DataDirectoryPath, "character-mastery.json")) { }
    public CharacterMasteryService(string file)
    {
        this.file = file;
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 2_000_000) return;
            foreach (var profile in JsonSerializer.Deserialize<MasteryProfile[]>(File.ReadAllText(file)) ?? [])
            {
                if (profiles.Count >= 20) break;
                if (profile.Region is not ("Europe" or "Americas" or "Asia") || string.IsNullOrWhiteSpace(profile.Character)
                    || profile.Character.Length > 128 || profile.Levels is null || profile.Levels.Count > 10000
                    || profile.Levels.Any(x => x.Key.Length > 128 || x.Value is < 0 or > 120)) continue;
                profiles[profile.Key] = profile;
            }
        }
        catch (Exception failure) { Log.Warning(failure, "Could not read local character mastery"); }
    }
    public Task InitializeAsync() => rewards.Value;
    public IReadOnlyList<MasteryProfile> Profiles(string region)
    {
        lock (sync) return profiles.Values.Where(x => x.Region == region).OrderByDescending(x => x.CapturedAtUtc).ToArray();
    }
    public MasteryProfile? Find(string key) { lock (sync) return profiles.GetValueOrDefault(key); }
    public async Task ObserveAsync(FullAchievementInfoEvent packet, string region, string character, AchievementsService definitions)
    {
        if (string.IsNullOrWhiteSpace(character) || region is not ("Europe" or "Americas" or "Asia")) return;
        var indexes = packet.AchievementsIndex ?? [];
        var levels = packet.AchievementLevels ?? [];
        if (indexes.Length != levels.Length || indexes.Length > 10000 || definitions.Achievements.Count == 0
            || packet.AchievementsIndex is null && packet.AchievementsIndexLevel100 is null) return;
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < indexes.Length; i++)
        {
            if (indexes[i] < 0 || indexes[i] >= definitions.Achievements.Count || levels[i] > 120) return;
            values[definitions.GetAchievementInfoByIndex(indexes[i]).Id] = levels[i];
        }
        foreach (var index in packet.AchievementsIndexLevel100 ?? [])
        {
            if (index < 0 || index >= definitions.Achievements.Count) return;
            var definition = definitions.GetAchievementInfoByIndex(index);
            // An explicit elite level wins over the compact level-100 list.
            values.TryAdd(definition.Id, definition.IsTemplate ? 100 : 1);
        }
        var profile = new MasteryProfile(region, character, packet.CapturedAt.ToUniversalTime(), values);
        lock (sync)
        {
            if (profiles.TryGetValue(profile.Key, out var previous) && previous.CapturedAtUtc > profile.CapturedAtUtc) return;
            profiles[profile.Key] = profile;
            if (profiles.Count > 20) profiles.Remove(profiles.Values.MinBy(x => x.CapturedAtUtc)!.Key);
        }
        Changed?.Invoke();
        await SaveAsync().ConfigureAwait(false);
    }
    public decimal Bonus(MasteryProfile profile, MarketItem item)
    {
        if (!rewards.Value.IsCompletedSuccessfully) throw new InvalidOperationException("Mastery rewards are not loaded.");
        var id = item.ItemId.Split('@')[0];
        return rewards.Value.Result.Where(x => item.Tier >= x.MinTier && item.Tier <= x.MaxTier
                && profile.Levels.ContainsKey(x.Achievement) && x.Patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, id)))
            .Sum(x => x.Bonus * (x.Elite ? Math.Clamp(profile.Levels[x.Achievement] - 100, 0, x.MaxLevel)
                : Math.Clamp(profile.Levels[x.Achievement], 0, x.MaxLevel)));
    }
    private async Task SaveAsync()
    {
        await saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            MasteryProfile[] snapshot;
            lock (sync) snapshot = profiles.Values.ToArray();
            snapshot = snapshot.OrderByDescending(x => x.CapturedAtUtc).ToArray();
            var json = JsonSerializer.Serialize(snapshot);
            while (System.Text.Encoding.UTF8.GetByteCount(json) > 2_000_000 && snapshot.Length > 1)
            {
                snapshot = snapshot[..^1];
                json = JsonSerializer.Serialize(snapshot);
            }
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", json);
                File.Move(file + ".tmp", file, true);
            }).ConfigureAwait(false);
        }
        catch (Exception failure) { Log.Warning(failure, "Could not save local character mastery"); }
        finally { saveGate.Release(); }
    }
    private static PowerReward[] LoadRewards()
    {
        using var packed = typeof(CharacterMasteryService).Assembly.GetManifestResourceStream("ReferenceData.achievements.xml.gz")
            ?? throw new FileNotFoundException("The bundled Destiny Board rewards are missing.");
        using var gzip = new GZipStream(packed, CompressionMode.Decompress);
        var root = XDocument.Load(gzip).Root!;
        var templates = root.Elements("template").ToDictionary(x => (string)x.Attribute("name")!, StringComparer.Ordinal);
        var result = new List<PowerReward>();
        foreach (var achievement in root.Elements().Where(x => x.Name.LocalName is "achievement" or "templateachievement"))
        {
            var id = (string?)achievement.Attribute("id") ?? "";
            templates.TryGetValue((string?)achievement.Attribute("usetemplate") ?? "", out var template);
            foreach (var elite in new[] { false, true })
            {
                var section = elite ? "eliterewards" : "baserewards";
                var levelSection = elite ? "elitelevels" : "baselevels";
                var levelText = (string?)(achievement.Element(levelSection) ?? template?.Element(levelSection));
                var maxLevel = levelText?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
                    ?? (elite ? 0 : 1);
                foreach (var bonus in achievement.Element(section)?.Elements("bonus") ?? [])
                {
                    if ((string?)bonus.Attribute("type") != "itemwearbonus" || (string?)bonus.Attribute("attribute") != "itempower"
                        || !decimal.TryParse((string?)bonus.Attribute("bonus"), NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)) continue;
                    result.Add(new(id, elite, maxLevel, amount, (int?)bonus.Attribute("mintier") ?? 1, (int?)bonus.Attribute("maxtier") ?? 8,
                        bonus.Elements("itempattern").Select(x => (string)x.Attribute("pattern")!).ToArray()));
                }
            }
        }
        return result.ToArray();
    }
    private sealed record PowerReward(string Achievement, bool Elite, int MaxLevel, decimal Bonus, int MinTier, int MaxTier, string[] Patterns);
}
