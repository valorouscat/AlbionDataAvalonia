using AlbionDataAvalonia.Locations;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AlbionDataAvalonia.Maps;

public enum DepthsLandmarkKind { Up, Down, SoulExit, ResurrectionShrine, Altar }

internal readonly record struct DepthsLandmark(DepthsLandmarkKind Kind, ActivityWorldPosition Position);
internal readonly record struct DepthsLayoutPlacement(string Layout, int QuarterTurns);

internal static partial class DepthsMapData
{
    // Fixed world objects are at template points; tolerate float rounding, not proximity to a room.
    private const double LandmarkTolerance = 1.5;

    public static int? FloorLevel(string master) => Floors.TryGetValue(master, out var floor) ? floor.Level : null;

    public static DepthsLayoutPlacement? Recognize(IReadOnlyCollection<DepthsLandmark> landmarks)
    {
        if (landmarks.Count < 2) return null;
        DepthsLayoutPlacement? best = null;
        int bestScore = 0, runnerUp = 0;
        foreach (var (layout, floor) in Floors)
        {
            var altars = floor.Altars.Select(x => (x.X, x.Z)).ToArray();
            for (var turns = 0; turns < 4; turns++)
            {
                // Count distinct template points, so duplicate packets or two objects at one spot cannot confirm a turn.
                var matched = new HashSet<(DepthsLandmarkKind, double, double)>();
                foreach (var landmark in landmarks)
                {
                    if (!double.IsFinite(landmark.Position.X) || !double.IsFinite(landmark.Position.Y)) continue;
                    var points = landmark.Kind switch
                    {
                        DepthsLandmarkKind.Up => floor.Up,
                        DepthsLandmarkKind.Down => floor.Down,
                        DepthsLandmarkKind.SoulExit => floor.SoulExits,
                        DepthsLandmarkKind.ResurrectionShrine => floor.Shrines,
                        DepthsLandmarkKind.Altar => altars,
                        _ => []
                    };
                    foreach (var point in points)
                    {
                        var (x, y) = point;
                        for (var turn = 0; turn < turns; turn++) (x, y) = (y, -x);
                        if (Math.Abs(x - landmark.Position.X) > LandmarkTolerance || Math.Abs(y - landmark.Position.Y) > LandmarkTolerance) continue;
                        matched.Add((landmark.Kind, point.Item1, point.Item2));
                        break;
                    }
                }
                var score = matched.Count;
                if (score > bestScore)
                {
                    runnerUp = bestScore;
                    bestScore = score;
                    best = new(layout, turns);
                }
                else if (score > runnerUp) runnerUp = score;
            }
        }

        // Slave sections can add objects that are absent from the master. They are harmless outliers;
        // a close runner-up still means we must wait for another fixed landmark.
        return bestScore >= 2 && runnerUp < bestScore && runnerUp * 2 <= bestScore ? best : null;
    }
}
