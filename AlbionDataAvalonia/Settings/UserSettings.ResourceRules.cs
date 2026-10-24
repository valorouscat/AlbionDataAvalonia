using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Settings;

public partial class UserSettings
{
    private IReadOnlyDictionary<string, MapResourceFamilyFilter> mapSeenResourceFilters = new Dictionary<string, MapResourceFamilyFilter>();
    public IReadOnlyDictionary<string, MapResourceFamilyFilter> MapSeenResourceFilters
    {
        get => mapSeenResourceFilters;
        set { mapSeenResourceFilters = NormalizeResourceFilters(value); OnPropertyChanged(nameof(MapSeenResourceFilters)); }
    }

    private IReadOnlyDictionary<string, MapResourceFamilyFilter> mapSpawnResourceFilters = new Dictionary<string, MapResourceFamilyFilter>();
    public IReadOnlyDictionary<string, MapResourceFamilyFilter> MapSpawnResourceFilters
    {
        get => mapSpawnResourceFilters;
        set { mapSpawnResourceFilters = NormalizeResourceFilters(value); OnPropertyChanged(nameof(MapSpawnResourceFilters)); }
    }

    private static Dictionary<string, MapResourceFamilyFilter> NormalizeResourceFilters(IReadOnlyDictionary<string, MapResourceFamilyFilter>? value) =>
        (value ?? new Dictionary<string, MapResourceFamilyFilter>()).Where(x => x.Value is not null
            && new[] { "Wood", "Stone", "Fiber", "Hide", "Ore" }.Contains(x.Key, StringComparer.OrdinalIgnoreCase))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last().Value.Normalize(), StringComparer.OrdinalIgnoreCase);

    public MapResourceFamilyFilter? GetMapResourceFamilyFilter(bool spawn, string family) =>
        (spawn ? MapSpawnResourceFilters : MapSeenResourceFilters).GetValueOrDefault(family);

    public void SetMapResourceFamilyFilter(bool spawn, string family, MapResourceFamilyFilter? filter)
    {
        var next = new Dictionary<string, MapResourceFamilyFilter>(spawn ? MapSpawnResourceFilters : MapSeenResourceFilters, StringComparer.OrdinalIgnoreCase);
        if (filter is null) next.Remove(family); else next[family] = filter;
        if (spawn) MapSpawnResourceFilters = next; else MapSeenResourceFilters = next;
    }
}
