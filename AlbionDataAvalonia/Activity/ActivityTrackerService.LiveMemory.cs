using AlbionDataAvalonia.Activity.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    // Process-local memory, isolated by server, character and exact map/instance id.
    private readonly Dictionary<LiveMemoryKey, LiveMemory> liveMemory = new();
    private readonly HashSet<long> observedThisVisit = new();
    private bool liveTrailNeedsBreak;
    private DateTime liveMemoryClearedAt;

    private LiveMemoryKey? CurrentLiveMemoryKey() => map is not null && actors.HasIdentity
        ? new(actors.ServerId, actors.CharacterId.Length > 0 ? actors.CharacterId : actors.CharacterName, map.RawLocationId) : null;

    private void SaveLiveMemory()
    {
        if (CurrentLiveMemoryKey() is not { } key) return;
        var trail = liveTrail.ToList();
        if (liveHead is { } head && (trail.Count == 0 || trail[^1] != head)) trail.Add(head);
        liveMemory[key] = new(trail.ToArray(), liveMarkers.ToArray(), CurrentResourceNodes(), CurrentResourceMobs(),
            CurrentRoamingBosses(), CurrentWisps());
    }

    private void RestoreLiveMemory()
    {
        resourceNodesEpoch = resourceMobsEpoch = liveActorsEpoch = wispsEpoch = mapEpoch;
        resourceNodes.Clear();
        resourceMobs.Clear();
        roamingBosses.Clear();
        wisps.Clear();
        observedThisVisit.Clear();
        liveTrailNeedsBreak = true;
        if (CurrentLiveMemoryKey() is not { } key || !liveMemory.Remove(key, out var remembered)) return;
        liveTrail.AddRange(remembered.Trail);
        liveMarkers.AddRange(remembered.Markers);
        foreach (var node in remembered.Nodes) resourceNodes[node.Id] = node with { IsInView = false };
        foreach (var mob in remembered.Mobs) resourceMobs[mob.Id] = mob with { IsInView = false };
        foreach (var poi in remembered.Pois) roamingBosses[poi.Id] = poi with { IsInView = false };
        foreach (var wisp in remembered.Wisps) wisps[wisp.Id] = wisp with { IsInView = false };
        liveMapVersion++;
    }

    /// <summary>Forget all process-local Live map observations without deleting recorded History or changing filters.</summary>
    public void ClearLiveMap()
    {
        lock (sync)
        {
            if (disposed) return;
            liveMemoryClearedAt = DateTime.UtcNow;
            liveMemory.Clear();
            pendingAbbeyObjects.Clear();
            observedThisVisit.Clear();
            resourceNodes.Clear();
            resourceMobs.Clear();
            roamingBosses.Clear();
            wisps.Clear();
            hostileSightings.Clear();
            liveMarkers.Clear();
            liveTrail.Clear();
            // Preserve the current player position; start a fresh trail there on the next movement.
            liveHead = liveHead is { } head ? head with { BreakBefore = true } : null;
            liveTrailNeedsBreak = true;
            liveMapVersion++;
        }
    }

    private readonly record struct LiveMemoryKey(int? ServerId, string CharacterId, string MapId);
    private sealed record LiveMemory(ActivityPathPoint[] Trail, ActivityMapMarker[] Markers, ActivityResourceNode[] Nodes,
        ActivityResourceMob[] Mobs, ActivityLiveActor[] Pois, ActivityWisp[] Wisps);
}
