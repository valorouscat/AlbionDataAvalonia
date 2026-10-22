using AlbionDataAvalonia.Activity.Models;
using AlbionDataAvalonia.Audio;
using AlbionDataAvalonia.Network.Events;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

// The game encrypts other players' positions, but still says when one comes into view: a NewCharacter event, with the
// player's name, guild and alliance in the clear. The live map flashes a warning over the character for a player outside
// its party, guild and alliance, so a hostile player is noticed even though the map can't place them.
public sealed partial class ActivityTrackerService
{
    /// <summary>How long a hostile player's arrival stays on the live map.</summary>
    public static readonly TimeSpan HostileAlertLength = TimeSpan.FromSeconds(3);
    private readonly List<ActivityHostileSighting> hostileSightings = new();
    private long hostileEpoch = -1;

    public event Action<HostileAlertArrival>? HostileSpotted;

    private void ObserveHostile(NewCharacterEvent packet, DateTime at)
    {
        SyncHostiles();
        if (!actors.Hostile(packet.ObjectId)) return;
        hostileSightings.RemoveAll(x => at - x.SeenAtUtc >= HostileAlertLength);
        hostileSightings.Add(new(at, packet.Name, packet.GuildName ?? "", packet.AllianceName ?? ""));
        liveMapVersion++;
        if (HasConfirmedContext && !captureSuspended && map is { } current)
            HostileSpotted?.Invoke(new(mapEpoch, current.RawLocationId, at,
                current.IsHub || string.Equals(current.ZoneType, "Blue", StringComparison.OrdinalIgnoreCase)));
    }

    // A new map starts without the last one's arrivals.
    private void SyncHostiles()
    {
        if (hostileEpoch == mapEpoch) return;
        hostileEpoch = mapEpoch;
        hostileSightings.Clear();
    }

    private ActivityHostileSighting[] CurrentHostiles()
    {
        SyncHostiles();
        return hostileSightings.ToArray();
    }
}
