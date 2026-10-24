using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Maps;
using System;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

/// <summary>The tier rules shared by seen-resource markers and their sound notification.</summary>
public static class ActivityResourceFilter
{
    public static bool ShowsSeen(UserSettings settings, string family, int tier, int enchantment) =>
        settings.GetMapResourceFamilyFilter(false, family) is { } filter ? filter.Matches(tier, enchantment)
        : ShowsFamily(settings, family) && ShowsTier(settings, tier, enchantment);

    public static bool ShowsSpawn(UserSettings settings, MapResourceSpawn spawn)
    {
        if (settings.GetMapResourceFamilyFilter(true, spawn.Family) is not { } filter)
            return ShowsFamily(settings, spawn.Family) && spawn.MatchesMinimumTier(settings.MapResourceSpawnMinTier);
        // A candidate has no observed enchantment. Keep it only if at least one possible tier/enchantment fits;
        // never label a candidate as an actually observed enchanted node.
        for (var tier = 1; tier <= 8; tier++)
        {
            if ((spawn.TierMask & (1 << tier)) == 0) continue;
            var maximum = tier < 4 ? 0 : spawn.Family == "Stone" ? 3 : 4;
            for (var enchantment = 0; enchantment <= maximum; enchantment++)
                if (filter.Matches(tier, enchantment)) return true;
        }
        return false;
    }

    public static bool ShowsTier(UserSettings settings, int tier, int enchantment)
    {
        if (tier >= settings.MapResourceMinTier) return true;
        var offset = settings.MapResourceMinTier - tier;
        return offset <= settings.MapResourceLowerTiers && settings.GetMapResourceLowerTierEnchantment(offset).Matches(enchantment);
    }

    public static bool ShowsFamily(UserSettings settings, string family) =>
        // Unknown future families are shown by the map, too.
        family is not ("Wood" or "Stone" or "Fiber" or "Hide" or "Ore")
        || !settings.MapResourceHiddenFamilies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(family, StringComparer.OrdinalIgnoreCase);
}
