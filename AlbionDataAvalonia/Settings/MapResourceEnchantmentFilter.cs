namespace AlbionDataAvalonia.Settings;

/// <summary>The inclusive enchantment range for one tier below the map's target tier.</summary>
public sealed record MapResourceEnchantmentFilter(int Minimum = 1, int Maximum = 4)
{
    public bool Matches(int enchantment) => enchantment >= Minimum && enchantment <= Maximum;
}
