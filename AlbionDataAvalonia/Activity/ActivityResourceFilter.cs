using AlbionDataAvalonia.Settings;
using System;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

/// <summary>The tier rules shared by seen-resource markers and their sound notification.</summary>
public static class ActivityResourceFilter
{
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
