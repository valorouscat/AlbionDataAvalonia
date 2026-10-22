using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Settings;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Audio;

/// <summary>A confirmed hostile arrival, with the map context needed to discard stale or safe-zone warnings.</summary>
public sealed record HostileAlertArrival(long MapEpoch, string RawMapId, DateTime SeenAtUtc, bool IsSafeZone);

/// <summary>Owns background alerts independently of the map window; coalesces bursts and never queues sounds.</summary>
public sealed class HostileAlertSoundService : IAsyncDisposable
{
    private readonly UserSettings settings;
    private readonly AlertSoundPlayback audio;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private ActivityTrackerService? tracker;
    private long lastEpoch = -1;
    private DateTime lastAlert;
    private bool disposed;

    public HostileAlertSoundService(UserSettings settings, IAlertSoundPlayer player, TimeProvider? time = null)
    {
        this.settings = settings;
        audio = new(player, "hostile alert");
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
            source.HostileSpotted += HostileSpotted;
        }
    }

    private bool CanAlert(HostileAlertArrival arrival)
    {
        var age = time.GetUtcNow().UtcDateTime - arrival.SeenAtUtc;
        return settings.MapShowHostileAlert && settings.MapHostileSoundEnabled && settings.MapHostileSoundVolume > 0
            && !arrival.IsSafeZone && age >= TimeSpan.FromSeconds(-1) && age < ActivityTrackerService.HostileAlertLength
            && tracker is { IsCaptureSuspended: false, IsAwaitingContext: false } && tracker.MapEpoch == arrival.MapEpoch;
    }

    private void HostileSpotted(HostileAlertArrival arrival)
    {
        if (!CanAlert(arrival)) return;
        lock (sync)
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (disposed || lastEpoch == arrival.MapEpoch && now - lastAlert < ActivityTrackerService.HostileAlertLength) return;
            lastEpoch = arrival.MapEpoch;
            lastAlert = now;
        }
        _ = BeginPlayback(arrival);
    }

    /// <summary>An explicit preview also works before automatic sound alerts have been enabled.</summary>
    public Task PreviewAsync() => BeginPlayback(null);

    private Task BeginPlayback(HostileAlertArrival? arrival) => audio.BeginAsync(
        settings.MapHostileSoundFile, () => settings.MapHostileSoundVolume, arrival is null ? null : () => CanAlert(arrival));

    public void Stop() => audio.Stop();

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UserSettings.MapHostileSoundVolume))
        {
            audio.SetVolume(settings.MapHostileSoundVolume);
            if (settings.MapHostileSoundVolume == 0) Stop();
        }
        else if (args.PropertyName == nameof(UserSettings.MapHostileSoundFile)
            || args.PropertyName == nameof(UserSettings.MapShowHostileAlert) && !settings.MapShowHostileAlert
            || args.PropertyName == nameof(UserSettings.MapHostileSoundEnabled) && !settings.MapHostileSoundEnabled)
            Stop();
        if (args.PropertyName is nameof(UserSettings.MapHostileSoundFile) or nameof(UserSettings.MapHostileSoundEnabled)
            or nameof(UserSettings.MapHostileSoundVolume)) audio.SetError("");
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
        }
        settings.PropertyChanged -= SettingsChanged;
        if (tracker is not null) tracker.HostileSpotted -= HostileSpotted;
        await audio.DisposeAsync().ConfigureAwait(false);
    }
}
