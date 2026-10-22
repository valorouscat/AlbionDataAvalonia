using Serilog;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Audio;

/// <summary>Serializes previews and automatic alerts, and owns decoder cancellation and cleanup.</summary>
internal sealed class AlertSoundPlayback(IAlertSoundPlayer player, string alertKind) : IAsyncDisposable
{
    private readonly object sync = new();
    private Task playback = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private bool disposed;
    private string error = "";

    public string Error { get { lock (sync) return error; } }
    public event Action? ErrorChanged;

    public Task BeginAsync(string file, Func<int> volume, Func<bool>? canPlay = null)
    {
        if (volume() == 0)
        {
            if (canPlay is null) SetError("Increase the volume above 0% to test the sound.");
            return Task.CompletedTask;
        }
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            if (canPlay is not null && !playback.IsCompleted) return Task.CompletedTask;
            // A new preview replaces the old sound; actual arrivals never interrupt or queue behind it.
            cancellation?.Cancel();
            var previous = playback;
            var next = cancellation = new CancellationTokenSource();
            playback = Task.Run(() => PlayAsync(previous, next, canPlay, file, volume));
            return playback;
        }
    }

    private async Task PlayAsync(Task previous, CancellationTokenSource next, Func<bool>? canPlay, string file, Func<int> volume)
    {
        try
        {
            await previous.ConfigureAwait(false);
            next.Token.ThrowIfCancellationRequested();
            if (canPlay is not null && !canPlay()) return;
            SetError("");
            await player.PlayAsync(file, volume(), next.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (next.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!next.IsCancellationRequested)
            {
                Log.Warning(failure, "Could not play the {AlertKind} sound", alertKind);
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

    public void SetVolume(int volume) => player.SetVolume(volume);

    public void SetError(string value)
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
        await pending.ConfigureAwait(false);
        player.Dispose();
    }
}
