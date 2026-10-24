using AlbionDataAvalonia.Audio;
using Serilog;
using System;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    public event Action<ResourceAlertArrival>? ResourceSpotted;
    private long alertClockWarningEpoch = -1;

    private DateTime LiveAlertObservedAt(DateTime capturedAt)
    {
        var now = DateTime.UtcNow;
        var delay = now - capturedAt;
        if (alertClockWarningEpoch != mapEpoch && (delay >= HostileAlertLength || delay < TimeSpan.FromSeconds(-1)))
        {
            alertClockWarningEpoch = mapEpoch;
            Log.Warning("Live alert capture time differs from observation time by {DelaySeconds:0.000}s; using observation time for alerts", delay.TotalSeconds);
        }
        return now;
    }

    private void NotifyResourceAppearance(DateTime at, ResourceAlertTarget target, ResourceAlertTarget? previous)
    {
        if (HasConfirmedContext && target != previous)
            // Keep the alert's short lifetime on the same clock as playback, rather than the capture driver clock.
            ResourceSpotted?.Invoke(new(mapEpoch, LiveAlertObservedAt(at), target, previous));
    }
}
