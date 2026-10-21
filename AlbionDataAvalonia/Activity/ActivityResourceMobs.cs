using System;
using System.Collections.Generic;
using System.Globalization;

namespace AlbionDataAvalonia.Activity;

/// <summary>A resource mob a NewMob packet described, before the tracker names it and places it on the current map.</summary>
public readonly record struct ResourceMobSighting(long Id, int MobIndex, double X, double Y, int Enchantment);

/// <summary>
/// Mobs that yield gathering resources: hide animals, and the critters, guardians and treasure mobs of wood, stone,
/// fiber and ore. The generated catalog names them by the Harvestable loot mobs.json gives each. The packets are read
/// from raw parameters, as the shared NewMob model has no position, following the community's open-source radars:
/// <list type="bullet">
/// <item>NewMob: 0 id, 1 mob index, 7 position [x, y] (8 on older layouts), 33 enchantment.</item>
/// <item>MobChangeState: 0 id, 1 enchantment.</item>
/// <item>Move: 0 id, 1 bytes with the position as little-endian floats at 9 and 13.</item>
/// </list>
/// </summary>
internal static partial class ActivityResourceMobs
{
    /// <summary>The resource family (as ActivityResourceIdentity names it) and tier a mob yields, or null for any other mob.</summary>
    public static (string Family, int Tier)? Resource(string? uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName) || !Catalog.TryGetValue(uniqueName, out var family)) return null;
        return uniqueName.Length > 2 && uniqueName[0] is 'T' or 't' && uniqueName[1] is >= '1' and <= '8'
            ? (family, uniqueName[1] - '0') : null;
    }

    public static ResourceMobSighting? ReadNew(IReadOnlyDictionary<byte, object> parameters)
    {
        if (Number(parameters, 0) is not { } id || id <= 0 || Number(parameters, 1) is not { } index || index is < 0 or > int.MaxValue
            || (Point(parameters, 7) ?? Point(parameters, 8)) is not { } at) return null;
        var enchantment = Number(parameters, 33);
        return new(id, (int)index, at.X, at.Y, enchantment is >= 0 and <= 4 ? (int)enchantment.Value : 0);
    }

    public static (long Id, int Enchantment)? ReadChange(IReadOnlyDictionary<byte, object> parameters) =>
        Number(parameters, 0) is { } id && id > 0 && Number(parameters, 1) is { } enchantment and >= 0 and <= 4
            ? (id, (int)enchantment) : null;

    public static (long Id, double X, double Y)? ReadMove(IReadOnlyDictionary<byte, object> parameters)
    {
        if (!BitConverter.IsLittleEndian || Number(parameters, 0) is not { } id || id <= 0 || !parameters.TryGetValue(1, out var raw)
            || raw is not byte[] { Length: >= 17 } bytes) return null;
        var x = BitConverter.ToSingle(bytes, 9);
        var y = BitConverter.ToSingle(bytes, 13);
        return float.IsFinite(x) && float.IsFinite(y) && Math.Abs(x) < 10000 && Math.Abs(y) < 10000 ? (id, x, y) : null;
    }

    /// <summary>The parameter keys and value types of a NewMob packet, for checking the layout in the log.</summary>
    public static string Describe(IReadOnlyDictionary<byte, object> parameters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in parameters)
        {
            if (key >= 250) continue;
            var text = value switch
            {
                Array list => $"{value.GetType().Name}[{list.Length}]",
                string word => $"\"{word}\"",
                null => "null",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
            parts.Add($"{key}={text}");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(", ", parts);
    }

    private static (double X, double Y)? Point(IReadOnlyDictionary<byte, object> parameters, byte key) =>
        parameters.TryGetValue(key, out var raw) && raw is Array { Rank: 1, Length: >= 2 } position
            && Real(position.GetValue(0)) is { } x && Real(position.GetValue(1)) is { } y ? (x, y) : null;

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
