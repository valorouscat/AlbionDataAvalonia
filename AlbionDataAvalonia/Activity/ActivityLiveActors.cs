using System;
using System.Collections.Generic;
using System.Globalization;

namespace AlbionDataAvalonia.Activity;

/// <summary>A player a NewCharacter packet described, before the tracker places it on the current map.</summary>
public readonly record struct PlayerSighting(long Id, double X, double Y, string Name, string Guild, string Alliance);

/// <summary>
/// The roaming bosses and other players near the character, for the live map. The generated catalog names the roaming
/// bosses by mobs.json. The shared NewCharacter model has no position, so the packet is read from raw parameters,
/// following the community's open-source radars: 0 id, 1 name, 8 guild, 51 alliance, and the position as a pair of
/// floats, at 13 on the layouts they describe. The pair is looked for at a few keys, so a moved key still places the player;
/// the first player of each map logs its packet, so the layout can be checked in game.
/// </summary>
internal static partial class ActivityLiveActors
{
    private static readonly byte[] PositionKeys = [13, 12, 14, 19];

    /// <summary>Whether a mob is a roaming boss, and if so whether it is a miniboss; null for any other mob.</summary>
    public static bool? RoamingBoss(string? uniqueName) =>
        !string.IsNullOrWhiteSpace(uniqueName) && RoamingBosses.TryGetValue(uniqueName, out var miniboss) ? miniboss : null;

    /// <summary>The tier a mob's unique name starts with ("T8_MOB_…"), or 0.</summary>
    public static int Tier(string? uniqueName) =>
        uniqueName is { Length: > 2 } && uniqueName[0] is 'T' or 't' && uniqueName[1] is >= '1' and <= '8' ? uniqueName[1] - '0' : 0;

    public static PlayerSighting? ReadCharacter(IReadOnlyDictionary<byte, object> parameters)
    {
        if (Number(parameters, 0) is not { } id || id <= 0 || Position(parameters) is not { } at) return null;
        return new(id, at.X, at.Y, Text(parameters, 1), Text(parameters, 8), Text(parameters, 51));
    }

    private static (double X, double Y)? Position(IReadOnlyDictionary<byte, object> parameters)
    {
        foreach (var key in PositionKeys)
            if (Point(parameters, key) is { } at) return at;
        // Otherwise the first pair of floats that can be a position.
        (double X, double Y)? found = null;
        var foundKey = byte.MaxValue;
        foreach (var (key, _) in parameters)
            if (key < foundKey && key < 250 && Point(parameters, key) is { } at)
            {
                found = at;
                foundKey = key;
            }

        return found;
    }

    private static (double X, double Y)? Point(IReadOnlyDictionary<byte, object> parameters, byte key) =>
        parameters.TryGetValue(key, out var raw) && raw is float[] { Length: 2 } position
            && float.IsFinite(position[0]) && float.IsFinite(position[1]) && Math.Abs(position[0]) < 10000 && Math.Abs(position[1]) < 10000
            ? (position[0], position[1]) : null;

    private static string Text(IReadOnlyDictionary<byte, object> parameters, byte key) =>
        parameters.TryGetValue(key, out var raw) && raw is string text ? text : "";

    private static long? Number(IReadOnlyDictionary<byte, object> parameters, byte key)
    {
        if (!parameters.TryGetValue(key, out var raw)) return null;
        try
        {
            return raw switch
            {
                byte or sbyte or short or ushort or int or uint or long => Convert.ToInt64(raw, CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (OverflowException) { return null; }
    }
}
