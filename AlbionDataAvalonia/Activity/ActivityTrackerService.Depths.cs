using Albion.Network;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Network.Events;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    private readonly List<DepthsLandmarkObservation> depthsLandmarks = new();
    private readonly Dictionary<LiveMemoryKey, DepthsLayoutPlacement> depthsPlacements = new();

    public void ObserveDepthsLandmark(MistsLandmarkEvent packet, DepthsLandmarkKind kind)
    {
        if (packet.ObjectId is not { } id || packet.Position is not { } position
            || !double.IsFinite(position.X) || !double.IsFinite(position.Y)) return;
        lock (sync)
        {
            if (disposed) return;
            var at = Utc(packet.CapturedAt);
            if (HasConfirmedContext && at < mapEnteredAt && SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            // During a same-connection transition a pre-Join object cannot safely be assigned to the new map.
            var mapKey = HasConfirmedContext && SameConnection(packet.ConnectionId, packet.MessageIdentity) ? CurrentLiveMemoryKey() : null;
            depthsLandmarks.RemoveAll(x => x.Id == id && x.Kind == kind && x.MapKey == mapKey
                && x.ConnectionId == packet.ConnectionId && x.Identity?.ReceiverGeneration == packet.MessageIdentity?.ReceiverGeneration
                && x.Identity?.Challenge == packet.MessageIdentity?.Challenge);
            if (depthsLandmarks.Count >= 256) depthsLandmarks.RemoveAt(0);
            depthsLandmarks.Add(new(id, kind, position, at, packet.ConnectionId, packet.MessageIdentity, mapKey));
            ApplyMapMetadata(WithDepthsPlacement(mapMetadata));
        }
    }

    private ActivityMapMetadata WithDepthsPlacement(ActivityMapMetadata metadata)
    {
        if (map is not { Kind: ActivityMapKind.AbyssalDepths }) return metadata;
        var key = CurrentLiveMemoryKey();
        var landmarks = depthsLandmarks.Where(x => x.At >= mapEnteredAt - LandmarkLead
                && (x.MapKey is null || x.MapKey == key) && SameConnection(x.ConnectionId, x.Identity))
            .Select(x => new DepthsLandmark(x.Kind, x.Position)).ToArray();
        var placement = DepthsMapData.Recognize(landmarks);
        if (placement is { } recognized && key is { } owner) depthsPlacements[owner] = recognized;
        else if (key is { } rememberedOwner && depthsPlacements.TryGetValue(rememberedOwner, out var remembered)) placement = remembered;
        if (placement is not { } found) return metadata;
        if (DepthsMapData.FloorLevel(found.Layout) is { } level) depthsFloors[map.RawLocationId] = level;
        if (metadata.Layout != found.Layout || metadata.LayoutTurns != found.QuarterTurns)
            Log.Debug("Placed {RawMapId} as Depths layout {Layout} turned {QuarterTurns} quarter turns from {Count} landmarks",
                map.RawLocationId, found.Layout, found.QuarterTurns, landmarks.Length);
        return metadata with { Layout = found.Layout, LayoutTurns = found.QuarterTurns };
    }

    private sealed record DepthsLandmarkObservation(long Id, DepthsLandmarkKind Kind, ActivityWorldPosition Position,
        DateTime At, string ConnectionId, PhotonMessageIdentity? Identity, LiveMemoryKey? MapKey);
}
