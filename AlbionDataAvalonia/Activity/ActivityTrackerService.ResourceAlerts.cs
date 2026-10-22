using AlbionDataAvalonia.Audio;
using System;

namespace AlbionDataAvalonia.Activity;

public sealed partial class ActivityTrackerService
{
    public event Action<ResourceAlertArrival>? ResourceSpotted;

    private void NotifyResourceAppearance(DateTime at, ResourceAlertTarget target, ResourceAlertTarget? previous)
    {
        if (HasConfirmedContext && target != previous)
            ResourceSpotted?.Invoke(new(mapEpoch, at, target, previous));
    }
}
