using Albion.Network;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Network.Responses;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    private readonly ActivityLocationCollector locations = new();
    private ActivityMapMetadata mapMetadata = new();
    private ActivityEvent? locationObservation;
    // Fixed outdoor Mists and Abbey exits may arrive before Join confirms the instance.
    // Keep their connection and Join attempt so a reused socket cannot mix adjacent maps.
    private static readonly TimeSpan LandmarkLead = TimeSpan.FromSeconds(10);
    private readonly List<MistsLandmarkObservation> mistsLandmarks = new();
    private readonly Dictionary<LiveMemoryKey, MistsLayoutPlacement> mistsPlacements = new();
    // The Depths floor of each Depths map entered lately, so rejoining one after a disconnect keeps its floor.
    private readonly Dictionary<string, int> depthsFloors = new(StringComparer.OrdinalIgnoreCase);

    internal void CancelEntranceSelection(RequestPacket packet)
    {
        lock (sync)
        {
            if (disposed || !HasConfirmedContext || Utc(packet.CapturedAt) < mapEnteredAt
                || !SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            locations.ClearPending();
        }
    }

    private void ObserveLocationTransition(ChangeClusterResponse packet, DateTime at)
    {
        locations.Observe(packet, at);
        if (packet.ReturnCode != 0 || map is null || packet.Destination != map.RawLocationId) return;
        ApplyMapMetadata(WithDepthsPlacement(mapMetadata.Merge(ActivityLocationCollector.FromCore(
            ActivityInstanceMetadata.FromTransition(map.Kind, packet.LayoutId, packet.DungeonInformation)))));
    }

    public void ObserveMistsLandmark(MistsLandmarkEvent packet, bool abbey = false)
    {
        if (packet.Position is not { } position
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        lock (sync)
        {
            if (disposed) return;
            var id = packet.ObjectId;
            var at = Utc(packet.CapturedAt);
            if (HasConfirmedContext && at < mapEnteredAt && SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            var pendingJoin = actorJoinStartedAt is { } started && at >= started ? actorJoinStartedAt : null;
            var mapKey = pendingJoin is null && HasConfirmedContext && SameConnection(packet.ConnectionId, packet.MessageIdentity) ? CurrentLiveMemoryKey() : null;
            mistsLandmarks.RemoveAll(x => (id is not null && x.Id == id || x.Position == position) && x.Abbey == abbey && x.MapKey == mapKey && x.PendingJoinAt == pendingJoin
                && x.ConnectionId == packet.ConnectionId && x.Identity?.ReceiverGeneration == packet.MessageIdentity?.ReceiverGeneration
                && x.Identity?.Challenge == packet.MessageIdentity?.Challenge);
            if (mistsLandmarks.Count >= 256) mistsLandmarks.RemoveAt(0);
            mistsLandmarks.Add(new(id, abbey, position, at, packet.ConnectionId, packet.MessageIdentity, mapKey, pendingJoin));
            ApplyMapMetadata(WithMistsLayout(mapMetadata));
        }
    }

    private void BindMistsJoinLandmarks(DateTime? startedAt, DateTime joinedAt)
    {
        if (startedAt is null || CurrentLiveMemoryKey() is not { } key) return;
        for (var i = 0; i < mistsLandmarks.Count; i++)
        {
            var landmark = mistsLandmarks[i];
            if (landmark.PendingJoinAt != startedAt || landmark.At > joinedAt || joinedAt - landmark.At > LandmarkLead
                || !SameConnection(landmark.ConnectionId, landmark.Identity)) continue;
            mistsLandmarks[i] = landmark with { MapKey = key, PendingJoinAt = null };
        }
    }

    // Outdoor Mists use wisps/border exits; Abbeys use their own fixed exits back to the Mists.
    private ActivityMapMetadata WithMistsLayout(ActivityMapMetadata metadata)
    {
        if (map is not { Kind: ActivityMapKind.Mists or ActivityMapKind.MistsDungeon } || metadata.LayoutTurns is not null) return metadata;
        var key = CurrentLiveMemoryKey();
        var abbey = map.Kind == ActivityMapKind.MistsDungeon;
        var landmarks = mistsLandmarks.Where(x => x.Abbey == abbey && x.PendingJoinAt is null && x.At >= mapEnteredAt - LandmarkLead
                && (x.MapKey is null || x.MapKey == key) && SameConnection(x.ConnectionId, x.Identity))
            .Select(x => x.Position).Distinct().ToArray();
        var placement = abbey ? MistsMapData.RecognizeAbbey(landmarks) : MistsLayoutRecognizer.Recognize(landmarks);
        if (placement is { } recognized && key is { } owner) mistsPlacements[owner] = recognized;
        else if (key is { } rememberedOwner && mistsPlacements.TryGetValue(rememberedOwner, out var remembered)) placement = remembered;
        if (placement is not { } found) return metadata;
        Log.Debug("Placed {RawMapId} as {Kind} layout {Layout} turned {QuarterTurns} quarter turns from {Count} landmarks",
            map.RawLocationId, map.Kind, found.Layout, found.QuarterTurns, landmarks.Length);
        return metadata with { Layout = found.Layout, LayoutTurns = found.QuarterTurns };
    }

    private sealed record MistsLandmarkObservation(long? Id, bool Abbey, ActivityWorldPosition Position, DateTime At,
        string ConnectionId, PhotonMessageIdentity? Identity, LiveMemoryKey? MapKey, DateTime? PendingJoinAt);

    // Joining the Depths names no layout, so the floor is counted: a party enters at the first floor and each exit
    // down leads one floor deeper.
    private ActivityMapMetadata WithDepthsFloor(ActivityMapMetadata metadata, ActivityLocationDescriptor? previous)
    {
        if (map is not { Kind: ActivityMapKind.AbyssalDepths } || metadata.Layout is not null) return metadata;
        if (!depthsFloors.TryGetValue(map.RawLocationId, out var floor))
        {
            floor = previous is { Kind: ActivityMapKind.AbyssalDepths } && depthsFloors.TryGetValue(previous.RawLocationId, out var above)
                ? Math.Min(above + 1, DepthsMapData.FloorCount) : 1;
            if (depthsFloors.Count >= 32) depthsFloors.Clear();
            depthsFloors[map.RawLocationId] = floor;
        }

        Log.Debug("Placed {RawMapId} as Depths floor {Floor}", map.RawLocationId, floor);
        return DepthsMapData.FloorLayout(floor) is { } layout ? metadata with { Layout = layout } : metadata;
    }

    private void ApplyMapMetadata(ActivityMapMetadata enriched)
    {
        if (map is null || enriched == mapMetadata) return;
        if (enriched.Layout != mapMetadata.Layout || enriched.LayoutTurns != mapMetadata.LayoutTurns) liveMapVersion++;
        mapMetadata = enriched;
        if (!CanRecord || session is null) return;
        for (var i = 0; i < visits.Count; i++)
            if (visits[i].RawMapId == map.RawLocationId)
                visits[i] = visits[i] with { Metadata = (visits[i].Metadata ?? new()).Merge(enriched) };
        session = session with { Revision = session.Revision + 1, VisitsJson = JsonSerializer.Serialize(visits) };
        headersDirty = true;
        if (locationObservation is { } original && original.SessionId == session.Id && original.RawMapId == map.RawLocationId)
        {
            var amended = original with { Revision = original.Revision + 1, DetailsJson = LocationDetails() };
            if (persistence.Amend(amended)) locationObservation = amended;
            else storageSuspended = true;
        }
    }

    private void RestoreLocationMetadata() => mapMetadata =
        (visits.LastOrDefault(x => x.RawMapId == map?.RawLocationId && x.Metadata is not null)?.Metadata ?? new()).Merge(mapMetadata);

    private string LocationDetails() => JsonSerializer.Serialize(new
    {
        OriginalName = map?.DisplayName,
        map?.ParentClusterId,
        map?.SourceClusterId,
        GroupingInferred = session?.HasInferredGrouping ?? false,
        MapMetadata = mapMetadata
    });

    private ActivityEvent LocationEvent(DateTime at, string? origin) => locationObservation =
        WithSource(Stamp(ActivityEventKind.Location, at, origin), actors.Local) with { DetailsJson = LocationDetails() };
}
