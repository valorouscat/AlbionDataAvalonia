using AlbionDataAvalonia.Activity.Models;
using System;

namespace AlbionDataAvalonia.Activity;

internal static class ActivityWisps
{
    public static (ActivityWispRarity Rarity, ActivityWispSize Size)? Describe(string? uniqueName)
    {
        const string prefix = "MOB_UNIQUE_MISTS_PORTAL_WISP";
        if (uniqueName is null || !uniqueName.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var suffix = uniqueName[prefix.Length..];
        var size = ActivityWispSize.Solo;
        if (suffix.StartsWith("_DUO", StringComparison.Ordinal))
        {
            size = ActivityWispSize.Duo;
            suffix = suffix[4..];
        }

        // mists.xml difficulty levels 0..4: the internal names are one rarity behind their display names.
        // In particular, STANDARD is uncommon and RARE is epic. RESOURCE wisps aren't solo/duo portals.
        ActivityWispRarity? rarity = suffix switch
        {
            "" => ActivityWispRarity.Common,
            "_STANDARD" => ActivityWispRarity.Uncommon,
            "_UNCOMMON" => ActivityWispRarity.Rare,
            "_RARE" => ActivityWispRarity.Epic,
            "_LEGENDARY" => ActivityWispRarity.Legendary,
            _ => null
        };
        return rarity is { } value ? (value, size) : null;
    }
}
