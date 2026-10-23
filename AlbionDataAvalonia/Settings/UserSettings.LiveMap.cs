namespace AlbionDataAvalonia.Settings;

// Live sightings and recorded event markers have independent preferences.
public partial class UserSettings
{
    private bool mapLiveShowKills = false;
    public bool MapLiveShowKills
    {
        get => mapLiveShowKills;
        set
        {
            var next = value;
            if (mapLiveShowKills == next) return;
            mapLiveShowKills = next;
            OnPropertyChanged(nameof(MapLiveShowKills));
        }
    }

    private bool mapLiveShowLoot = false;
    public bool MapLiveShowLoot
    {
        get => mapLiveShowLoot;
        set
        {
            var next = value;
            if (mapLiveShowLoot == next) return;
            mapLiveShowLoot = next;
            OnPropertyChanged(nameof(MapLiveShowLoot));
        }
    }

    private bool mapLiveShowGathering = false;
    public bool MapLiveShowGathering
    {
        get => mapLiveShowGathering;
        set
        {
            var next = value;
            if (mapLiveShowGathering == next) return;
            mapLiveShowGathering = next;
            OnPropertyChanged(nameof(MapLiveShowGathering));
        }
    }

    private bool mapShowWisps = false;
    public bool MapShowWisps
    {
        get => mapShowWisps;
        set
        {
            var next = value;
            if (mapShowWisps == next) return;
            mapShowWisps = next;
            OnPropertyChanged(nameof(MapShowWisps));
        }
    }

    private int mapWispRarities = 31;
    public int MapWispRarities
    {
        get => mapWispRarities;
        set
        {
            var next = value & 31;
            if (mapWispRarities == next) return;
            mapWispRarities = next;
            OnPropertyChanged(nameof(MapWispRarities));
        }
    }

    private int mapWispSizes = 3;
    public int MapWispSizes
    {
        get => mapWispSizes;
        set
        {
            var next = value & 3;
            if (mapWispSizes == next) return;
            mapWispSizes = next;
            OnPropertyChanged(nameof(MapWispSizes));
        }
    }

}
