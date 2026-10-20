using AlbionDataAvalonia.Activity.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace AlbionDataAvalonia.Activity;

/// <summary>A node a harvestable packet described, before the tracker places it on the current map.</summary>
public readonly record struct ResourceNodeSighting(long Id, string Family, int Tier, int Enchantment, double X, double Y, int Charges);

/// <summary>A node's new charge count and enchantment; zero charges means it was gathered out.</summary>
public readonly record struct ResourceNodeChange(long Id, int Charges, int? Enchantment);

/// <summary>
/// Reads the game's harvestable node events. The shared packet models don't cover these yet, so they arrive as raw
/// parameters. The layouts follow the community's open-source Albion radars and are checked in game against the
/// resource each harvest reports (see ActivityTrackerService.NoteHarvestedNode):
/// <list type="bullet">
/// <item>NewHarvestableObject: 0 id, 5 type, 7 tier, 8 position [x, y], 10 charges, 11 enchantment.</item>
/// <item>NewSimpleHarvestableObjectList: parallel lists of 0 ids, 1 types, 2 tiers, 3 positions (x, y pairs), 4 charges;
/// listed nodes are never enchanted.</item>
/// <item>HarvestableChangeState: 0 id, 1 charges (missing once gathered out), 2 enchantment.</item>
/// </list>
/// </summary>
public static class ActivityResourceNodePackets
{
    private const int MaxListedNodes = 2048;

    /// <summary>The kind a node type number stands for: the game numbers trees, rocks, fiber plants, hides and ore veins in blocks.</summary>
    public static string? Family(long type) => type switch
    {
        >= 0 and <= 5 => "Wood",
        >= 6 and <= 10 => "Stone",
        >= 11 and <= 14 => "Fiber",
        >= 15 and <= 22 => "Hide",
        >= 23 and <= 27 => "Ore",
        _ => null
    };

    /// <summary>The raw resource a node yields, such as T5_WOOD_LEVEL1@1, for its icon.</summary>
    public static string ItemUniqueName(string family, int tier, int enchantment)
    {
        var token = family switch { "Wood" => "WOOD", "Stone" => "ROCK", "Fiber" => "FIBER", "Hide" => "HIDE", "Ore" => "ORE", _ => family.ToUpperInvariant() };
        return enchantment > 0 ? $"T{tier}_{token}_LEVEL{enchantment}@{enchantment}" : $"T{tier}_{token}";
    }

    public static ResourceNodeSighting? ReadNode(IReadOnlyDictionary<byte, object> parameters)
    {
        if (Number(parameters, 0) is not { } id || Number(parameters, 5) is not { } type || Number(parameters, 7) is not { } tier
            || !parameters.TryGetValue(8, out var raw) || raw is not Array { Length: >= 2 } position
            || Real(position.GetValue(0)) is not { } x || Real(position.GetValue(1)) is not { } y) return null;
        return Sighting(id, type, tier, x, y, Number(parameters, 10) ?? 0, Number(parameters, 11) ?? 0);
    }

    public static IReadOnlyList<ResourceNodeSighting> ReadNodeList(IReadOnlyDictionary<byte, object> parameters)
    {
        if (List(parameters, 0) is not { } ids || List(parameters, 1) is not { } types || List(parameters, 2) is not { } tiers
            || List(parameters, 3) is not { } positions) return [];
        var charges = List(parameters, 4);
        var count = Math.Min(MaxListedNodes, Math.Min(ids.Length, Math.Min(types.Length, Math.Min(tiers.Length, positions.Length / 2))));
        var nodes = new List<ResourceNodeSighting>(count);
        for (var index = 0; index < count; index++)
        {
            if (Number(ids.GetValue(index)) is not { } id || Number(types.GetValue(index)) is not { } type || Number(tiers.GetValue(index)) is not { } tier
                || Real(positions.GetValue(index * 2)) is not { } x || Real(positions.GetValue(index * 2 + 1)) is not { } y) continue;
            var left = charges is not null && index < charges.Length ? Number(charges.GetValue(index)) ?? 0 : 0;
            if (Sighting(id, type, tier, x, y, left, 0) is { } node) nodes.Add(node);
        }

        return nodes;
    }

    public static ResourceNodeChange? ReadChange(IReadOnlyDictionary<byte, object> parameters)
    {
        if (Number(parameters, 0) is not { } id || id <= 0) return null;
        var charges = Number(parameters, 1);
        var enchantment = Number(parameters, 2);
        return new(id, charges is > 0 and <= int.MaxValue ? (int)charges.Value : 0,
            enchantment is >= 0 and <= 4 ? (int)enchantment.Value : null);
    }

    private static ResourceNodeSighting? Sighting(long id, long type, long tier, double x, double y, long charges, long enchantment) =>
        id > 0 && Family(type) is { } family && tier is >= 1 and <= 8 && enchantment is >= 0 and <= 4
            ? new(id, family, (int)tier, (int)enchantment, x, y, (int)Math.Clamp(charges, 0, int.MaxValue)) : null;

    private static Array? List(IReadOnlyDictionary<byte, object> parameters, byte key) =>
        parameters.TryGetValue(key, out var raw) && raw is Array { Rank: 1 } list ? list : null;

    private static long? Number(IReadOnlyDictionary<byte, object> parameters, byte key) =>
        parameters.TryGetValue(key, out var raw) ? Number(raw) : null;

    private static long? Number(object? value)
    {
        try
        {
            return value switch
            {
                byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (OverflowException) { return null; }
    }

    private static double? Real(object? value) => value is float or double or int or short or long
        && Convert.ToDouble(value, CultureInfo.InvariantCulture) is var number && double.IsFinite(number) ? number : null;
}
