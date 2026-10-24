using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace AlbionDataAvalonia.Settings;

/// <summary>One alternative in a resource family's filter. Enchantment -1 means any.</summary>
public sealed record MapResourceRule(int Tier = 4, bool HigherTiers = false, int Enchantment = -1, bool HigherEnchantments = false)
{
    public MapResourceRule Normalize() => this with { Tier = Math.Clamp(Tier, 1, 8), Enchantment = Math.Clamp(Enchantment, -1, 4) };
    public bool Matches(int tier, int enchantment) => tier is >= 1 and <= 8 && enchantment is >= 0 and <= 4
        && (HigherTiers ? tier >= Tier : tier == Tier)
        && (Enchantment < 0 || (HigherEnchantments ? enchantment >= Enchantment : enchantment == Enchantment));
    [JsonIgnore]
    public string Summary => $"T{Tier}{(HigherTiers && Tier < 8 ? "+" : "")} · {(Enchantment < 0 ? "Any enchantment" : $".{Enchantment}{(HigherEnchantments && Enchantment < 4 ? "+" : "")}")}";
}

public sealed record MapResourceFamilyFilter(bool Enabled = true, MapResourceRule[]? Rules = null)
{
    public MapResourceFamilyFilter Normalize() => this with
    {
        Rules = (Rules ?? []).Where(x => x is not null).Select(x => x.Normalize()).Distinct().Take(16).ToArray()
    };
    public bool Matches(int tier, int enchantment) => Enabled && (Rules ?? []).Any(x => x.Matches(tier, enchantment));
}
