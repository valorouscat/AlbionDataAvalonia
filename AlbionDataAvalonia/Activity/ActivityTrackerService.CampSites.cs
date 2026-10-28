using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Locations;
using System;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    public void ObserveCampChestUpdate(DebugEventProbeEvent packet)
    {
        var id = new MistsLandmarkEvent(packet.Parameters).ObjectId;
        var reward = ActivityCampSites.Reward(packet.Parameters, 13);
        if (id is null || reward == CampChestReward.Unknown) return;
        lock (sync)
        {
            if (disposed) return;
            var at = Utc(packet.CapturedAt);
            if (at < liveMemoryClearedAt) return;
            if (actorJoinStartedAt is { } started)
            {
                for (var i = 0; i < pendingAbbeyObjects.Count; i++)
                {
                    var entry = pendingAbbeyObjects[i];
                    if (entry.Actor.Id == id && entry.Actor.IsCampChest && entry.JoinStartedAt == started
                        && entry.Actor.SeenAtUtc <= at && entry.Actor.ChestRewardObservedAtUtc <= at
                        && entry.ConnectionId == packet.ConnectionId
                        && entry.Identity?.ReceiverGeneration == packet.MessageIdentity?.ReceiverGeneration
                        && entry.Identity?.PeerId == packet.MessageIdentity?.PeerId
                        && entry.Identity?.Challenge == packet.MessageIdentity?.Challenge)
                        pendingAbbeyObjects[i] = entry with { Actor = ActivityCampSites.WithReward(entry.Actor, reward, at) };
                }
                return;
            }
            if (!HasConfirmedContext || at < mapEnteredAt || !SameConnection(packet.ConnectionId, packet.MessageIdentity)
                || !roamingBosses.TryGetValue(id.Value, out var chest) || !chest.IsCampChest
                || at < chest.SeenAtUtc || at < chest.ChestRewardObservedAtUtc) return;
            roamingBosses[id.Value] = ActivityCampSites.WithReward(chest, reward, at);
            liveMapVersion++;
        }
    }

    private void EmptyObservedChest(long id, DateTime at)
    {
        SyncLiveActors();
        if (!roamingBosses.TryGetValue(id, out var chest)) return;
        if (chest.IsCampChest)
        {
            if (chest.SeenAtUtc > at) return;
            // Keep hidden evidence of the selected camp after its reward is collected.
            roamingBosses[id] = chest with { IsDepleted = true, IsInView = false, SeenAtUtc = at };
            liveMapVersion++;
        }
        else if (map?.Kind == ActivityMapKind.MistsDungeon) ForgetLiveActor(id);
    }

    public void ObserveSiteDestroyed(DebugEventProbeEvent packet)
    {
        if (new MistsLandmarkEvent(packet.Parameters).ObjectId is not { } id) return;
        lock (sync)
        {
            if (disposed) return;
            var at = Utc(packet.CapturedAt);
            if (at < liveMemoryClearedAt) return;
            if (actorJoinStartedAt is { } started)
            {
                for (var i = 0; i < pendingAbbeyObjects.Count; i++)
                {
                    var entry = pendingAbbeyObjects[i];
                    if (entry.Actor.Id == id && entry.Actor.IsSite && entry.JoinStartedAt == started && entry.Actor.SeenAtUtc <= at
                        && entry.ConnectionId == packet.ConnectionId && entry.Identity?.ReceiverGeneration == packet.MessageIdentity?.ReceiverGeneration
                        && entry.Identity?.PeerId == packet.MessageIdentity?.PeerId
                        && entry.Identity?.Challenge == packet.MessageIdentity?.Challenge)
                        pendingAbbeyObjects[i] = entry with { Actor = entry.Actor with { IsDepleted = true, IsInView = false, SeenAtUtc = at } };
                }
                return;
            }
            if (!HasConfirmedContext || at < mapEnteredAt || !SameConnection(packet.ConnectionId, packet.MessageIdentity)
                || !roamingBosses.TryGetValue(id, out var site) || !site.IsSite || site.SeenAtUtc > at) return;
            roamingBosses[id] = site with { IsDepleted = true, IsInView = false, SeenAtUtc = at };
            liveMapVersion++;
        }
    }
}
