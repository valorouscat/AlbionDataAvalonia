using AlbionDataAvalonia.Activity.Models;
using System;

namespace AlbionDataAvalonia.Activity;

/// <summary>Dynamic mob POIs for the live map. The generated catalog names them by mobs.json.</summary>
internal static partial class ActivityLiveActors
{
    public static ActivityLiveActorKind? MobKind(string? name) =>
        name is not null && DynamicMobs.TryGetValue(name, out var kind) ? kind : null;

    public static bool IsSeasonal(string? name) => name is not null
        && (name.Contains("HALLOWEEN", StringComparison.OrdinalIgnoreCase)
            || name.Contains("EVENT", StringComparison.OrdinalIgnoreCase));

    public static bool IsAbbeyEntrance(string name)
    {
        var mode = Tier(name) > 0 && name[2] == '_' ? name[3..] : name;
        return mode.Equals("MISTS_DUNGEON_SOLO_YELLOW", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("MISTS_DUNGEON_SOLO_BLACK", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("MISTS_DUNGEON_DUO_BLACK", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tier prefix of a game unique name, or zero if absent.</summary>
    public static int Tier(string? uniqueName) =>
        uniqueName is { Length: > 2 } && uniqueName[0] is 'T' or 't' && uniqueName[1] is >= '1' and <= '8' ? uniqueName[1] - '0' : 0;
}
