using AlbionDataAvalonia.Activity.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The live map can show the roaming bosses around the character where they stand now, like the resource mobs: they come
// with NewMob, follow Move packets, and go when the game says they left or died. They aren't remembered between visits.
// Other players can't be shown this way: the game encrypts their positions, and a passive capture can't read them.
public sealed partial class ActivityTrackerService
{
    private const int MaxLiveActors = 500;
    // A move shorter than this doesn't redraw the map.
    private const double LiveActorStep = 0.5;
    private readonly Dictionary<long, ActivityLiveActor> roamingBosses = new();
    private long liveActorsEpoch = -1;

    private bool PlaceRoamingBoss(ResourceMobSighting sighting, DateTime at)
    {
        SyncLiveActors();
        var descriptor = actors.DescribeMob(sighting.MobIndex);
        if (ActivityLiveActors.RoamingBoss(descriptor?.UniqueName) is not { } miniboss) return roamingBosses.Remove(sighting.Id);
        Keep(new ActivityLiveActor(sighting.Id, miniboss ? ActivityLiveActorKind.Miniboss : ActivityLiveActorKind.Boss,
            sighting.X, sighting.Y, at, descriptor!.DisplayName, MobUniqueName: descriptor.UniqueName ?? "",
            Tier: ActivityLiveActors.Tier(descriptor.UniqueName)));
        return true;
    }

    private void Keep(ActivityLiveActor actor)
    {
        if (roamingBosses.Count >= MaxLiveActors && !roamingBosses.ContainsKey(actor.Id))
            roamingBosses.Remove(roamingBosses.Values.MinBy(x => x.SeenAtUtc)!.Id);
        roamingBosses[actor.Id] = actor;
    }

    private bool MoveLiveActor(long id, double x, double y, DateTime at)
    {
        SyncLiveActors();
        if (!roamingBosses.TryGetValue(id, out var actor)) return false;
        var far = Math.Abs(actor.X - x) >= LiveActorStep || Math.Abs(actor.Y - y) >= LiveActorStep;
        roamingBosses[id] = far ? actor with { X = x, Y = y, SeenAtUtc = at } : actor with { SeenAtUtc = at };
        return far;
    }

    // A boss that left the character's view or died.
    private void ForgetLiveActor(long? id)
    {
        SyncLiveActors();
        if (id is { } value && roamingBosses.Remove(value)) liveMapVersion++;
    }

    // Object ids belong to one map, so a new map starts with none.
    private void SyncLiveActors()
    {
        if (liveActorsEpoch == mapEpoch) return;
        liveActorsEpoch = mapEpoch;
        roamingBosses.Clear();
    }

    private ActivityLiveActor[] CurrentRoamingBosses()
    {
        SyncLiveActors();
        return roamingBosses.Values.ToArray();
    }
}
