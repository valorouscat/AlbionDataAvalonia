using AlbionDataAvalonia.Activity.Models;
using System.Collections.Generic;

namespace AlbionDataAvalonia.Activity;

internal static partial class ActivityCampSites
{
    // Public chest decoders use 21 at spawn and 13 at update for the revealed rare-state index.
    // Missing fields are unknown, never the default numeric zero (books).
    internal static CampChestReward Reward(IReadOnlyDictionary<byte, object> fields, byte key)
    {
        if (!fields.TryGetValue(key, out var raw)) return CampChestReward.Unknown;
        var value = raw switch
        {
            byte n => n,
            short n => n,
            int n => n,
            long n when n is >= 0 and <= 3 => (int)n,
            _ => -1
        };
        return value switch { 0 => CampChestReward.Books, >= 1 and <= 3 => CampChestReward.Loot, _ => CampChestReward.Unknown };
    }

    internal static ActivityLiveActor WithReward(ActivityLiveActor actor, CampChestReward reward, System.DateTime? at = null) => actor with
    {
        ChestReward = reward,
        ChestRewardObservedAtUtc = at ?? actor.SeenAtUtc,
        Name = reward switch
        {
            CampChestReward.Books => "Camp chest: books",
            CampChestReward.Loot => "Camp chest: loot",
            _ => "Camp chest: reward unknown"
        }
    };
}
