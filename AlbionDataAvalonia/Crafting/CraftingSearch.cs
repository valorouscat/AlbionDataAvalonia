using System;
using System.Linq;
using System.Text;

namespace AlbionDataAvalonia.Crafting;

/// <summary>Uses the same Unicode, punctuation and е/ё rules for recipe names and user input.</summary>
public static class CraftingSearch
{
    public static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormC).ToUpperInvariant()
        .Select(c => c == 'Ё' ? 'Е' : char.IsLetterOrDigit(c) || c is '_' or '@' or '.' ? c : ' ').ToArray());

    public static string[] Terms(string query) => Normalize(query).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool Matches(CraftableItem item, string[] terms) => terms.All(term => item.SearchText.Contains(term, StringComparison.Ordinal));
}
