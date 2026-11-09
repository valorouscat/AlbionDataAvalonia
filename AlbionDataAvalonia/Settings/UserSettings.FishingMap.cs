namespace AlbionDataAvalonia.Settings;

public partial class UserSettings
{
    private bool mapFishingMode;
    public bool MapFishingMode
    {
        get => mapFishingMode;
        set { if (mapFishingMode == value) return; mapFishingMode = value; OnPropertyChanged(nameof(MapFishingMode)); }
    }

    private bool mapFishingShowLocations = true;
    public bool MapFishingShowLocations
    {
        get => mapFishingShowLocations;
        set { if (mapFishingShowLocations == value) return; mapFishingShowLocations = value; OnPropertyChanged(nameof(MapFishingShowLocations)); }
    }

    private int mapFishingKinds = 7;
    public int MapFishingKinds
    {
        get => mapFishingKinds;
        set { var next = value & 31; if (mapFishingKinds == next) return; mapFishingKinds = next; OnPropertyChanged(nameof(MapFishingKinds)); }
    }

    private int mapFishingWater;
    public int MapFishingWater
    {
        get => mapFishingWater;
        set { var next = System.Math.Clamp(value, 0, 2); if (mapFishingWater == next) return; mapFishingWater = next; OnPropertyChanged(nameof(MapFishingWater)); }
    }

    private bool mapFishingDynamic = true;
    public bool MapFishingDynamic
    {
        get => mapFishingDynamic;
        set { if (mapFishingDynamic == value) return; mapFishingDynamic = value; OnPropertyChanged(nameof(MapFishingDynamic)); }
    }

    private bool mapFishingShowCatches = true;
    public bool MapFishingShowCatches
    {
        get => mapFishingShowCatches;
        set { if (mapFishingShowCatches == value) return; mapFishingShowCatches = value; OnPropertyChanged(nameof(MapFishingShowCatches)); }
    }
}
