namespace AlbionDataAvalonia.Settings;

/// <summary>An exact or minimum enchantment for one tier below the map's target tier.</summary>
public sealed record MapResourceEnchantmentFilter(int Minimum = 1, bool Exact = false)
{
    public bool Matches(int enchantment) => Exact ? enchantment == Minimum : enchantment >= Minimum;
}
