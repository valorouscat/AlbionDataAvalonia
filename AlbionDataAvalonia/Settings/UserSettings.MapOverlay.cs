using System;

namespace AlbionDataAvalonia.Settings;

public partial class UserSettings
{
    private bool mapOverlayEnabled;
    public bool MapOverlayEnabled
    {
        get => mapOverlayEnabled;
        set { if (mapOverlayEnabled == value) return; mapOverlayEnabled = value; OnPropertyChanged(nameof(MapOverlayEnabled)); }
    }

    private bool mapOverlayPinned = true;
    public bool MapOverlayPinned
    {
        get => mapOverlayPinned;
        set { if (mapOverlayPinned == value) return; mapOverlayPinned = value; OnPropertyChanged(nameof(MapOverlayPinned)); }
    }

    private bool mapOverlayClickThrough;
    public bool MapOverlayClickThrough
    {
        get => mapOverlayClickThrough;
        set { if (mapOverlayClickThrough == value) return; mapOverlayClickThrough = value; OnPropertyChanged(nameof(MapOverlayClickThrough)); }
    }

    private int mapOverlayOpacity = 75;
    public int MapOverlayOpacity
    {
        get => mapOverlayOpacity;
        set
        {
            var next = Math.Clamp(value, 20, 100);
            if (mapOverlayOpacity == next) return;
            mapOverlayOpacity = next;
            OnPropertyChanged(nameof(MapOverlayOpacity));
        }
    }

    private WindowPlacement? mapOverlayWindowPlacement;
    public WindowPlacement? MapOverlayWindowPlacement
    {
        get => mapOverlayWindowPlacement;
        set { if (mapOverlayWindowPlacement == value) return; mapOverlayWindowPlacement = value; OnPropertyChanged(nameof(MapOverlayWindowPlacement)); }
    }
}
