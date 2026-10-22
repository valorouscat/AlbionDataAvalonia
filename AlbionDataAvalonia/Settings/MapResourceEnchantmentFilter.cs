namespace AlbionDataAvalonia.Settings;

/// <summary>The minimum enchantment for one tier below the map's target tier.</summary>
public sealed record MapResourceEnchantmentFilter(int Minimum = 1)
{
    public bool Matches(int enchantment) => enchantment >= Minimum;
}
