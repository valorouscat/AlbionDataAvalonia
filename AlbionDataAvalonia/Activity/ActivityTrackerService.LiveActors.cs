using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Locations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// Dynamic POIs share the old roaming-boss settings and scene slot for backward compatibility.
// Observations survive visibility changes and map revisits in process-local Live memory.
public sealed partial class ActivityTrackerService
{
    private readonly Dictionary<long, ActivityLiveActor> roamingBosses = new();
    private long liveActorsEpoch = -1;

    private bool PlaceRoamingBoss(ResourceMobSighting sighting, DateTime at)
    {
        SyncLiveActors();
        var descriptor = actors.DescribeMob(sighting.MobIndex);
        if (ActivityLiveActors.MobKind(descriptor?.UniqueName) is not { } kind) return roamingBosses.Remove(sighting.Id);
        Keep(new ActivityLiveActor(sighting.Id, kind,
            sighting.X, sighting.Y, at, descriptor!.DisplayName, MobUniqueName: descriptor.UniqueName ?? "",
            Tier: ActivityLiveActors.Tier(descriptor.UniqueName)));
        return true;
    }

    private void PlaceDungeonEntrance(NewRandomDungeonExitEvent entrance, DateTime at)
    {
        if (at < liveMemoryClearedAt) return;
        SyncLiveActors();
        if (entrance.ObjectId is not { } id || entrance.SourceExitPosition is not { } position
            || string.IsNullOrWhiteSpace(entrance.UniqueName)
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        // Portal wisps have a separate layer and rarity/size filters.
        if (entrance.UniqueName.StartsWith("MISTS_", StringComparison.OrdinalIgnoreCase)) return;
        observedThisVisit.Add(id);
        var metadata = ActivityInstanceMetadata.FromEntrance(entrance.UniqueName, entrance.DungeonType,
            entrance.Level, entrance.IsAlreadyEntered);
        var seasonal = ActivityLiveActors.IsSeasonal(entrance.UniqueName) || ActivityLiveActors.IsSeasonal(entrance.DungeonType);
        var name = seasonal ? "Halloween / event dungeon" : metadata.Mode is { } mode ? $"{mode} dungeon" : "Dungeon entrance";
        Keep(new ActivityLiveActor(id, seasonal ? ActivityLiveActorKind.EventDungeon : ActivityLiveActorKind.Dungeon,
            position.X, position.Y, at, name, Tier: metadata.Tier ?? 0, Enchantment: metadata.Enchantment));
        liveMapVersion++;
    }

    private void Keep(ActivityLiveActor actor)
    {
        roamingBosses[actor.Id] = actor;
    }

    private bool MoveLiveActor(long id, double x, double y, DateTime at)
    {
        SyncLiveActors();
        if (!roamingBosses.TryGetValue(id, out var actor)) return false;
        var moved = actor.X != x || actor.Y != y;
        if (!actor.IsMob) return false;
        roamingBosses[id] = actor with { X = x, Y = y, SeenAtUtc = at, IsInView = true };
        return moved || !actor.IsInView;
    }

    private void LeaveLiveActor(long id)
    {
        SyncLiveActors();
        if (!roamingBosses.TryGetValue(id, out var actor)) return;
        if (!actor.IsInView) return;
        roamingBosses[id] = actor with { IsInView = false };
        liveMapVersion++;
    }

    // Confirmed death removes even a remembered mob.
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
