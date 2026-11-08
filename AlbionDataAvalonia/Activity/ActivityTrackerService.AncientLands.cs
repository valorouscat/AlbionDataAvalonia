using Albion.Network;
using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Locations;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Network.Events;
using AlbionDataAvalonia.Shared;
using System;

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
            EventCodes.NewBuilding => (ActivityLiveActorKind.Altar, "Dragon altar"),
            _ => ((ActivityLiveActorKind)(-1), "")
        };
        if (name.Length == 0) return;
        ObserveMapObject(packet, new(objectId, kind, point.X, point.Y, Utc(packet.CapturedAt), name)
        {
            ObservationNote = "Observed position; availability and activation state are unknown"
        }, abbeyOnly: false, ancientOnly: true);
    }
}
