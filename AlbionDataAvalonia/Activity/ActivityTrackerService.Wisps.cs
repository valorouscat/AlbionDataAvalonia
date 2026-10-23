using AlbionDataAvalonia.Activity.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    private const double WispMoveStep = 0.5;
    private readonly Dictionary<long, ActivityWisp> wisps = new();
    private long wispsEpoch = -1;

    private bool PlaceWisp(ResourceMobSighting sighting, DateTime at)
    {
        SyncWisps();
        if (ActivityWisps.Describe(actors.DescribeMob(sighting.MobIndex)?.UniqueName) is not { } kind)
            return wisps.Remove(sighting.Id);
        wisps[sighting.Id] = new(sighting.Id, kind.Rarity, kind.Size, sighting.X, sighting.Y, at);
        return true;
    }

    private bool MoveWisp(long id, double x, double y, DateTime at)
    {
        SyncWisps();
        if (!wisps.TryGetValue(id, out var wisp)) return false;
        var far = Math.Abs(wisp.X - x) >= WispMoveStep || Math.Abs(wisp.Y - y) >= WispMoveStep;
        wisps[id] = wisp with { X = x, Y = y, SeenAtUtc = at, IsInView = true };
        return far || !wisp.IsInView;
    }

    private void LeaveWisp(long id)
    {
        SyncWisps();
        if (!wisps.TryGetValue(id, out var wisp) || !wisp.IsInView) return;
        wisps[id] = wisp with { IsInView = false };
        liveMapVersion++;
    }

    private void ForgetWisp(long? id)
    {
        SyncWisps();
        if (id is { } value && wisps.Remove(value)) liveMapVersion++;
    }

    private void SyncWisps()
    {
        if (wispsEpoch == mapEpoch) return;
        wispsEpoch = mapEpoch;
        wisps.Clear();
    }

    private ActivityWisp[] CurrentWisps()
    {
        SyncWisps();
        return wisps.Values.ToArray();
    }
}
