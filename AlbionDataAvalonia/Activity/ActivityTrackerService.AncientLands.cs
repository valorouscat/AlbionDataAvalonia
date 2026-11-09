using Albion.Network;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using System;
using System.Collections.Generic;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    private ActivityMapMetadata WithAncientLandsLayout(ActivityMapMetadata metadata)
    {
        if (map is not { Kind: ActivityMapKind.DragonArea }) return metadata;
        var zone = AncientLandsMapData.Zone(map.RawLocationId, metadata.Layout);
        return zone is null ? metadata : metadata with
        {
            Layout = zone.Id,
            LayoutTurns = 0,
            Tier = zone.Tier ?? metadata.Tier,
            ZoneType = zone.PvpCategory.Length > 0 ? zone.PvpCategory : metadata.ZoneType
        };
    }

    // Caged objects have a GUID/type at 1 and the position at 2, unlike Mists landmarks.
    // The visual tile at 4 identifies the runestone; ordinary wisp cages are excluded.
    public void ObserveAncientRunestone(DebugEventProbeEvent packet)
    {
        if (!packet.Parameters.TryGetValue(4, out var rawName) || rawName is not string name
            || !name.Equals("DRAGON_KPR_FANATICS_RUNESTONE", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("DRAGON_AREA_RUNESTONE", StringComparison.OrdinalIgnoreCase)) return;
        var fields = new Dictionary<byte, object>();
        if (packet.Parameters.TryGetValue(0, out var rawId)) fields[0] = rawId;
        if (packet.Parameters.TryGetValue(2, out var rawPosition)) fields[1] = rawPosition;
        var decoded = new MistsLandmarkEvent(fields);
        if (decoded.ObjectId is not { } id || decoded.Position is not { } point
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > 560 || Math.Abs(point.Y) > 560) return;
        var state = packet.Parameters.TryGetValue(5, out var rawState) ? rawState switch
        {
            byte value => (int?)value,
            short value => value,
            int value => value,
            long value when value is >= 0 and <= int.MaxValue => (int)value,
            _ => null
        } : null;
        var at = Utc(packet.CapturedAt);
        ObserveMapObject(packet, new(id, ActivityLiveActorKind.EnergyShrine, point.X, point.Y, at, "Spirit Essence statue")
        {
            SourceTemplate = name,
            StateObservedAtUtc = at,
            IsDepleted = state == 2,
            ObservationNote = state switch
            {
                1 => "Closed when observed; grants Spirit Essence according to game data",
                2 => "Opened when observed",
                _ => "Observed position; current availability is unknown"
            }
        }, abbeyOnly: false, ancientOnly: true);
    }

    public void ObserveAncientRunestoneState(DebugEventProbeEvent packet)
    {
        var id = new MistsLandmarkEvent(packet.Parameters).ObjectId;
        if (id is null) return;
        lock (sync)
        {
            if (disposed) return;
            var at = Utc(packet.CapturedAt);
            if (at < liveMemoryClearedAt) return;
            // The update event's state-field schema has not been verified. Its arrival
            // invalidates the old availability claim, but does not prove depletion.
            static ActivityLiveActor Updated(ActivityLiveActor actor, DateTime at) => actor with
            {
                StateObservedAtUtc = at,
                ObservationNote = "State update received; current availability is unknown"
            };
            if (actorJoinStartedAt is { } started)
            {
                for (var i = 0; i < pendingAbbeyObjects.Count; i++)
                {
                    var entry = pendingAbbeyObjects[i];
                    if (entry.Actor.Id == id && entry.Actor.Kind == ActivityLiveActorKind.EnergyShrine
                        && entry.JoinStartedAt == started && entry.Actor.SeenAtUtc <= at && entry.Actor.StateObservedAtUtc <= at
                        && entry.ConnectionId == packet.ConnectionId
                        && entry.Identity?.ReceiverGeneration == packet.MessageIdentity?.ReceiverGeneration
                        && entry.Identity?.PeerId == packet.MessageIdentity?.PeerId
                        && entry.Identity?.Challenge == packet.MessageIdentity?.Challenge)
                        pendingAbbeyObjects[i] = entry with { Actor = Updated(entry.Actor, at) };
                }
                return;
            }
            if (!HasConfirmedContext || map?.Kind != ActivityMapKind.DragonArea || at < mapEnteredAt
                || !SameConnection(packet.ConnectionId, packet.MessageIdentity)
                || !roamingBosses.TryGetValue(id.Value, out var actor) || actor.Kind != ActivityLiveActorKind.EnergyShrine
                || actor.SeenAtUtc > at || actor.StateObservedAtUtc > at) return;
            roamingBosses[id.Value] = Updated(actor, at);
            liveMapVersion++;
        }
    }

    // Reuse the shared world-object decoder. Building positions are at field 4;
    // classify altars only at a known altar position, without guessing a numeric building type.
    public void ObserveAncientLandsObject(DebugEventProbeEvent packet, EventCodes code)
    {
        var fields = packet.Parameters;
        if (code == EventCodes.NewBuilding)
        {
            fields = new();
            if (packet.Parameters.TryGetValue(0, out var id)) fields[0] = id;
            if (packet.Parameters.TryGetValue(4, out var position)) fields[1] = position;
        }
        var decoded = new MistsLandmarkEvent(fields);
        if (decoded.ObjectId is not { } objectId || decoded.Position is not { } point
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || Math.Abs(point.X) > 560 || Math.Abs(point.Y) > 560) return;
        var (kind, name) = code switch
        {
            EventCodes.NewTeleporterNode => (ActivityLiveActorKind.Teleporter, "Leygate"),
            EventCodes.NewDragonAreaExit => (ActivityLiveActorKind.Exit, "Ancient Lands exit"),
            EventCodes.NewLoreSnippetObject => (ActivityLiveActorKind.Lore, "Lore spot"),
            EventCodes.NewBuilding => (ActivityLiveActorKind.Altar, "Dragon reward altar"),
            _ => ((ActivityLiveActorKind)(-1), "")
        };
        if (name.Length == 0) return;
        ObserveMapObject(packet, new(objectId, kind, point.X, point.Y, Utc(packet.CapturedAt), name)
        {
            ObservationNote = "Observed position; availability and activation state are unknown"
        }, abbeyOnly: false, ancientOnly: true);
    }
}
