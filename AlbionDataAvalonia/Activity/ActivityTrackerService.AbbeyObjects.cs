using Albion.Network;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    private readonly List<AbbeyObjectObservation> pendingAbbeyObjects = new();

    // These objects are not mobs. Reuse the shared world-object value decoder,
    // keeping the original packet's transport identity and capture time for binding.
    public void ObserveAbbeyObject(DebugEventProbeEvent packet, EventCodes code)
    {
        var fields = packet.Parameters;
        if (code == EventCodes.NewMultiRewardObject)
        {
            fields = new();
            if (packet.Parameters.TryGetValue(0, out var rawId)) fields[0] = rawId;
            if (packet.Parameters.TryGetValue(2, out var rawPosition)) fields[1] = rawPosition;
        }
        var decoded = new MistsLandmarkEvent(fields);
        if (decoded.ObjectId is not { } id || decoded.Position is not { } position) return;
        var (kind, name) = code switch
        {
            EventCodes.NewLootChest => (ActivityLiveActorKind.Chest, "Loot chest"),
            EventCodes.NewTreasureChest => (ActivityLiveActorKind.Chest, "Treasure chest / coffer"),
            EventCodes.NewShrine => (ActivityLiveActorKind.Shrine, "Shrine / reward statue"),
            EventCodes.NewMultiRewardObject => (ActivityLiveActorKind.Reward, "Room reward: chest or statue"),
            _ => ((ActivityLiveActorKind)(-1), "")
        };
        if (name.Length == 0) return;
        lock (sync)
        {
            if (disposed) return;
            var at = Utc(packet.CapturedAt);
            if (at < liveMemoryClearedAt) return;
            var actor = new ActivityLiveActor(id, kind, position.X, position.Y, at, name);
            if (actorJoinStartedAt is { } started && at >= started)
            {
                if (pendingAbbeyObjects.Count >= 256) pendingAbbeyObjects.RemoveAt(0);
                pendingAbbeyObjects.Add(new(actor, packet.ConnectionId, packet.MessageIdentity, started));
            }
            else if (HasConfirmedContext && map!.Kind == ActivityMapKind.MistsDungeon && at >= mapEnteredAt
                && SameConnection(packet.ConnectionId, packet.MessageIdentity)) PlaceAbbeyObject(actor);
        }
    }

    private void PlaceAbbeyObject(ActivityLiveActor actor)
    {
        if (actor.SeenAtUtc < liveMemoryClearedAt) return;
        SyncLiveActors();
        // A generic room-reward announcement must not replace a subsequently decoded
        // chest/statue for the same object. Do not guess the reward from its numeric field.
        if (actor.Kind == ActivityLiveActorKind.Reward && roamingBosses.TryGetValue(actor.Id, out var specific)
            && specific.Kind is ActivityLiveActorKind.Chest or ActivityLiveActorKind.Shrine)
            actor = actor with { Kind = specific.Kind, Name = specific.Name };
        observedThisVisit.Add(actor.Id);
        Keep(actor);
        liveMapVersion++;
    }

    private void ReplayAbbeyObjects(DateTime? startedAt, DateTime joinedAt)
    {
        if (startedAt is not null && map?.Kind == ActivityMapKind.MistsDungeon)
            foreach (var observation in pendingAbbeyObjects.Where(x => x.JoinStartedAt == startedAt
                && x.Actor.SeenAtUtc <= joinedAt && joinedAt - x.Actor.SeenAtUtc <= LandmarkLead
                && SameConnection(x.ConnectionId, x.Identity)).OrderBy(x => x.Actor.SeenAtUtc))
                PlaceAbbeyObject(observation.Actor);
        pendingAbbeyObjects.Clear();
    }

    private void ObservePendingAbbeyObjectState(BaseEvent packet, DateTime at)
    {
        long? id = packet switch
        {
            LeaveEvent leave => leave.userObjectId,
            UpdateLootChestEvent { State: 8 } empty => empty.ObjectId,
            _ => null
        };
        if (id is null || actorJoinStartedAt is not { } started) return;
        for (var i = pendingAbbeyObjects.Count - 1; i >= 0; i--)
        {
            var observation = pendingAbbeyObjects[i];
            if (observation.Actor.Id != id || observation.Actor.SeenAtUtc > at || observation.JoinStartedAt != started
                || observation.ConnectionId != packet.ConnectionId
                || observation.Identity?.ReceiverGeneration != packet.MessageIdentity?.ReceiverGeneration
                || observation.Identity?.Challenge != packet.MessageIdentity?.Challenge) continue;
            if (packet is LeaveEvent) pendingAbbeyObjects[i] = observation with { Actor = observation.Actor with { IsInView = false } };
            else pendingAbbeyObjects.RemoveAt(i);
        }
    }

    private sealed record AbbeyObjectObservation(ActivityLiveActor Actor, string ConnectionId,
        PhotonMessageIdentity? Identity, DateTime JoinStartedAt);
}
