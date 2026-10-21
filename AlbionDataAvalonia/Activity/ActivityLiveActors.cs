namespace AlbionDataAvalonia.Activity;

/// <summary>The roaming bosses near the character, for the live map. The generated catalog names them by mobs.json.</summary>
internal static partial class ActivityLiveActors
{
    /// <summary>Whether a mob is a roaming boss, and if so whether it is a miniboss; null for any other mob.</summary>
    public static bool? RoamingBoss(string? uniqueName) =>
        !string.IsNullOrWhiteSpace(uniqueName) && RoamingBosses.TryGetValue(uniqueName, out var miniboss) ? miniboss : null;

    /// <summary>The tier a mob's unique name starts with ("T8_MOB_…"), or 0.</summary>
    public static int Tier(string? uniqueName) =>
        uniqueName is { Length: > 2 } && uniqueName[0] is 'T' or 't' && uniqueName[1] is >= '1' and <= '8' ? uniqueName[1] - '0' : 0;
}
