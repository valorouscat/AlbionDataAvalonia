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
    private readonly Dictionary<DepthsLandmarkKind, long> depthsPacketWarningEpochs = new();
    private ActivityDepthsRecognition? depthsRecognition;

    public void ObserveDepthsLandmark(MistsLandmarkEvent packet, DepthsLandmarkKind kind)
    {
        lock (sync)
        {
            if (disposed) return;
            if (packet.ObjectId is not { } id || packet.Position is not { } position
                || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            {
                if (!depthsPacketWarningEpochs.TryGetValue(kind, out var epoch) || epoch != mapEpoch)
                {
                    depthsPacketWarningEpochs[kind] = mapEpoch;
                    DiagnosticNote?.Invoke($"Depths {kind} spawn ignored: no usable object id / position in the shared world-object envelope. Check the raw spawn parameters.");
                }
                return;
            }
            var at = Utc(packet.CapturedAt);
            if (HasConfirmedContext && at < mapEnteredAt && SameConnection(packet.ConnectionId, packet.MessageIdentity)) return;
            // Spawns can precede Join's response on the same socket. Hold them for that join attempt,
            // rather than binding them to the map being left or applying them to it prematurely.
            var pendingJoin = actorJoinStartedAt is { } started && at >= started ? actorJoinStartedAt : null;
            var mapKey = pendingJoin is null && HasConfirmedContext && SameConnection(packet.ConnectionId, packet.MessageIdentity) ? CurrentLiveMemoryKey() : null;
            depthsLandmarks.RemoveAll(x => x.Id == id && x.Kind == kind && x.MapKey == mapKey
                && x.PendingJoinAt == pendingJoin
                && x.ConnectionId == packet.ConnectionId && x.Identity?.ReceiverGeneration == packet.MessageIdentity?.ReceiverGeneration
                && x.Identity?.Challenge == packet.MessageIdentity?.Challenge);
            if (depthsLandmarks.Count >= 256) depthsLandmarks.RemoveAt(0);
            depthsLandmarks.Add(new(id, kind, position, at, packet.ConnectionId, packet.MessageIdentity, mapKey, pendingJoin));
            ApplyMapMetadata(WithDepthsPlacement(mapMetadata));
        }
    }

    private void BindDepthsJoinLandmarks(DateTime? startedAt, DateTime joinedAt)
    {
        if (startedAt is null || CurrentLiveMemoryKey() is not { } key) return;
        for (var i = 0; i < depthsLandmarks.Count; i++)
        {
            var landmark = depthsLandmarks[i];
            if (landmark.PendingJoinAt != startedAt || landmark.At > joinedAt || joinedAt - landmark.At > LandmarkLead
                || !SameConnection(landmark.ConnectionId, landmark.Identity)) continue;
            depthsLandmarks[i] = landmark with { MapKey = key, PendingJoinAt = null };
        }
    }

    private ActivityMapMetadata WithDepthsPlacement(ActivityMapMetadata metadata)
    {
        if (map is not { Kind: ActivityMapKind.AbyssalDepths })
        {
            depthsRecognition = null;
            return metadata;
        }
        var key = CurrentLiveMemoryKey();
        var landmarks = depthsLandmarks.Where(x => x.PendingJoinAt is null && x.At >= mapEnteredAt - LandmarkLead
                && (x.MapKey is null || x.MapKey == key) && SameConnection(x.ConnectionId, x.Identity))
            .Select(x => new DepthsLandmark(x.Kind, x.Position)).ToArray();
        var analysis = DepthsMapData.Analyze(landmarks);
        var progress = new ActivityDepthsRecognition(analysis.Observed, analysis.Matched,
            analysis.Matched > 0 && analysis.RunnerUp > 0 && analysis.Placement is null);
        if (depthsRecognition != progress)
        {
            depthsRecognition = progress;
            liveMapVersion++;
            DiagnosticNote?.Invoke($"Depths orientation: {analysis.Observed} current-map landmarks, {analysis.Matched} best matches, {analysis.RunnerUp} runner-up matches.");
        }
        var placement = analysis.Placement;
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
        DateTime At, string ConnectionId, PhotonMessageIdentity? Identity, LiveMemoryKey? MapKey, DateTime? PendingJoinAt);
}
