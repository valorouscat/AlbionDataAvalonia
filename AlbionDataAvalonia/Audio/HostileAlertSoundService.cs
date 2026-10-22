using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Settings;
using Serilog;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Audio;

/// <summary>A confirmed hostile arrival, with the map context needed to discard stale or safe-zone warnings.</summary>
public sealed record HostileAlertArrival(long MapEpoch, string RawMapId, DateTime SeenAtUtc, bool IsSafeZone);

/// <summary>Owns background alerts independently of the map window; coalesces bursts and never queues sounds.</summary>
public sealed class HostileAlertSoundService : IAsyncDisposable
{
    private readonly UserSettings settings;
    private readonly IAlertSoundPlayer player;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private ActivityTrackerService? tracker;
    private Task playback = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private long lastEpoch = -1;
    private DateTime lastAlert;
    private bool disposed;
    private string error = "";

    public HostileAlertSoundService(UserSettings settings, IAlertSoundPlayer player, TimeProvider? time = null)
    {
        this.settings = settings;
        this.player = player;
        this.time = time ?? TimeProvider.System;
        settings.PropertyChanged += SettingsChanged;
    }

    public string Error { get { lock (sync) return error; } }
    public event Action? ErrorChanged;

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

    private Task BeginPlayback(HostileAlertArrival? arrival)
    {
        if (settings.MapHostileSoundVolume == 0)
        {
            if (arrival is null) SetError("Increase the volume above 0% to test the sound.");
            return Task.CompletedTask;
        }
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            if (arrival is not null && !playback.IsCompleted) return Task.CompletedTask;
            // A new preview replaces the old sound; actual arrivals never interrupt or queue behind it.
            cancellation?.Cancel();
            var previous = playback;
            var next = cancellation = new CancellationTokenSource();
            var file = settings.MapHostileSoundFile;
            playback = Task.Run(() => PlayAsync(previous, next, arrival, file));
            return playback;
        }
    }

    private async Task PlayAsync(Task previous, CancellationTokenSource next, HostileAlertArrival? arrival, string file)
    {
        try
        {
            await previous.ConfigureAwait(false);
            next.Token.ThrowIfCancellationRequested();
            if (arrival is not null && !CanAlert(arrival)) return;
            SetError("");
            await player.PlayAsync(file, settings.MapHostileSoundVolume, next.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!next.IsCancellationRequested)
            {
                Log.Warning(failure, "Could not play the hostile alert sound");
                SetError(failure is FileNotFoundException or DirectoryNotFoundException
                    ? "The selected MP3 is missing. Choose another file or use the default sound."
                    : "Couldn't play the sound. Check your audio output or choose another MP3.");
            }
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(cancellation, next)) cancellation = null; }
            next.Dispose();
        }
    }

    public void Stop()
    {
        lock (sync) cancellation?.Cancel();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UserSettings.MapHostileSoundVolume))
        {
            player.SetVolume(settings.MapHostileSoundVolume);
            if (settings.MapHostileSoundVolume == 0) Stop();
        }
        else if (args.PropertyName == nameof(UserSettings.MapHostileSoundFile)
            || args.PropertyName == nameof(UserSettings.MapShowHostileAlert) && !settings.MapShowHostileAlert
            || args.PropertyName == nameof(UserSettings.MapHostileSoundEnabled) && !settings.MapHostileSoundEnabled)
            Stop();
        if (args.PropertyName is nameof(UserSettings.MapHostileSoundFile) or nameof(UserSettings.MapHostileSoundEnabled)
            or nameof(UserSettings.MapHostileSoundVolume)) SetError("");
    }

    private void SetError(string value)
    {
        lock (sync)
        {
            if (disposed || error == value) return;
            error = value;
        }
        ErrorChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            cancellation?.Cancel();
            pending = playback;
        }
        settings.PropertyChanged -= SettingsChanged;
        if (tracker is not null) tracker.HostileSpotted -= HostileSpotted;
        await pending.ConfigureAwait(false);
        player.Dispose();
    }
}
