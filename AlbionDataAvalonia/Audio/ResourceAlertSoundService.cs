using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Settings;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Audio;

public sealed record ResourceAlertTarget(string Family, int Tier, int Enchantment, bool IsMob);
public sealed record ResourceAlertArrival(long MapEpoch, DateTime SeenAtUtc, ResourceAlertTarget Target, ResourceAlertTarget? Previous);

/// <summary>Notifies only when a live resource first meets the seen-resource filters, independently of the map window.</summary>
public sealed class ResourceAlertSoundService : IAsyncDisposable
{
    private static readonly TimeSpan AlertInterval = TimeSpan.FromSeconds(3);
    private readonly UserSettings settings;
    private readonly AlertSoundPlayback audio;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private ActivityTrackerService? tracker;
    private long lastEpoch = -1;
    private DateTime lastAlert;
    private bool disposed;

    public ResourceAlertSoundService(UserSettings settings, IAlertSoundPlayer player, TimeProvider? time = null)
    {
        this.settings = settings;
        audio = new(player, "resource alert");
        this.time = time ?? TimeProvider.System;
        settings.PropertyChanged += SettingsChanged;
    }

    public string Error => audio.Error;
    public event Action? ErrorChanged { add => audio.ErrorChanged += value; remove => audio.ErrorChanged -= value; }

    public void Start(ActivityTrackerService source)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (tracker is not null) return;
            tracker = source;
            source.ResourceSpotted += ResourceSpotted;
        }
    }

    private bool Matches(ResourceAlertTarget target) => settings.MapShowResources
        && (!target.IsMob || settings.MapShowResourceMobs)
        && ActivityResourceFilter.ShowsSeen(settings, target.Family, target.Tier, target.Enchantment);

    private bool CanAlert(ResourceAlertArrival arrival)
    {
        var age = time.GetUtcNow().UtcDateTime - arrival.SeenAtUtc;
        return settings.MapResourceSoundEnabled && settings.MapResourceSoundVolume > 0
            && age >= TimeSpan.FromSeconds(-1) && age < AlertInterval
            && tracker is { IsCaptureSuspended: false, IsAwaitingContext: false } && tracker.MapEpoch == arrival.MapEpoch
            && Matches(arrival.Target) && (arrival.Previous is null || !Matches(arrival.Previous));
    }

    private void ResourceSpotted(ResourceAlertArrival arrival)
    {
        if (!CanAlert(arrival)) return;
        lock (sync)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (disposed || lastEpoch == arrival.MapEpoch && now - lastAlert < AlertInterval) return;
            lastEpoch = arrival.MapEpoch;
            lastAlert = now;
        }
        _ = audio.BeginAsync(settings.MapResourceSoundFile, () => settings.MapResourceSoundVolume, () => CanAlert(arrival));
    }

    public Task PreviewAsync() => audio.BeginAsync(settings.MapResourceSoundFile, () => settings.MapResourceSoundVolume);
    public void Stop() => audio.Stop();

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UserSettings.MapResourceSoundVolume))
        {
            audio.SetVolume(settings.MapResourceSoundVolume);
            if (settings.MapResourceSoundVolume == 0) Stop();
        }
        else if (args.PropertyName == nameof(UserSettings.MapResourceSoundFile)
            || args.PropertyName == nameof(UserSettings.MapShowResources) && !settings.MapShowResources
            || args.PropertyName == nameof(UserSettings.MapResourceSoundEnabled) && !settings.MapResourceSoundEnabled)
            Stop();
        if (args.PropertyName is nameof(UserSettings.MapResourceSoundFile) or nameof(UserSettings.MapResourceSoundEnabled)
            or nameof(UserSettings.MapResourceSoundVolume)) audio.SetError("");
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        settings.PropertyChanged -= SettingsChanged;
        if (tracker is not null) tracker.ResourceSpotted -= ResourceSpotted;
        await audio.DisposeAsync().ConfigureAwait(false);
    }
}
