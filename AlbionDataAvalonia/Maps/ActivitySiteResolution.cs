using AlbionDataAvalonia.Activity.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

/// <summary>Refine template alternatives using named observations, never an ordinary nearby resource node.</summary>
public static class ActivitySiteResolution
{
    public sealed record View(IReadOnlyList<MapPoi> Pois, IReadOnlyList<ActivityLiveActor> Actors, IReadOnlyList<MapPoint> ResolvedCenters)
    {
        public IReadOnlyList<MapResourceSpawn> Spawns(IReadOnlyList<MapResourceSpawn> spawns) =>
            spawns.Where(x => x.AreaHalfSize == 0 || !ResolvedCenters.Any(p => Same(p, x.Position))).ToArray();
    }

    public static View Resolve(IReadOnlyList<MapPoi> pois, IReadOnlyList<ActivityLiveActor> actors)
    {
        var sites = actors.Where(x => x.IsSite).ToArray();
        var resolved = sites.Where(x => !x.IsDepleted).Select(x => new MapPoint(x.X, x.Y)).ToList();
        var chestCenters = new Dictionary<long, MapPoint>();
        var superseded = new HashSet<long>();
        foreach (var chest in actors.Where(x => x.IsCampChest))
        {
            var grade = chest.CampChestDefinition.Contains("MEDIUM", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            var candidates = pois.Where(p => p.Kind == MapPoiKind.Camp && p.Grade == grade && p.Radius > 0
                && Math.Pow(p.Position.X - chest.X, 2) + Math.Pow(p.Position.Y - chest.Y, 2) <= Math.Pow(p.Radius + 0.1, 2)).ToArray();
            if (candidates.Length != 1) continue;
            var center = candidates[0].Position;
            var site = sites.Where(x => Same(center, new(x.X, x.Y))).MaxBy(x => x.SeenAtUtc);
            if (site is not null && site.SeenAtUtc >= chest.SeenAtUtc
                && (site.IsDepleted || site.Kind != ActivityLiveActorKind.CampSite))
            {
                superseded.Add(chest.Id);
                continue;
            }
            resolved.Add(center);
            if (!chest.IsDepleted) chestCenters[chest.Id] = center;
        }
        var visible = actors.Where(x => !x.IsDepleted && !superseded.Contains(x.Id)
            && !(x.Kind == ActivityLiveActorKind.CampSite && chestCenters.Values.Any(p => Same(p, new(x.X, x.Y))))).ToArray();
        return new(pois.Where(p => p.Kind != MapPoiKind.Camp || !resolved.Any(x => Same(x, p.Position))).ToArray(), visible, resolved);
    }

    private static bool Same(MapPoint a, MapPoint b) => Math.Abs(a.X - b.X) <= 0.1 && Math.Abs(a.Y - b.Y) <= 0.1;
}
