using SoundFlow.Backends.MiniAudio;
using SoundFlow.Components;
using SoundFlow.Providers;
using SoundFlow.Structs;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.Audio;

/// <summary>Loads audio only when requested; no device or decoder is kept open while idle.</summary>
public sealed class AlertSoundPlayer(string defaultSoundResource = "Audio.hostile-alert.wav") : IAlertSoundPlayer
{
    private readonly object sync = new();
    private SoundPlayer? current;
    private bool disposed;
    private float desiredVolume;

    public async Task PlayAsync(string filePath, int volume, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            desiredVolume = Math.Clamp(volume, 0, 100) / 100f;
        }
        using var stream = string.IsNullOrWhiteSpace(filePath)
            ? typeof(AlertSoundPlayer).Assembly.GetManifestResourceStream(defaultSoundResource)
                ?? throw new FileNotFoundException("The default alert sound is missing.")
            : File.OpenRead(filePath);
        using var engine = new MiniAudioEngine();
        var format = AudioFormat.DvdHq;
        using var provider = new StreamDataProvider(engine, format, stream);
        using var device = engine.InitializePlaybackDevice(null, format);
        using var player = new SoundPlayer(engine, format, provider);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackEnded += (_, _) => finished.TrySetResult();
        device.MasterMixer.AddComponent(player);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            current = player;
            player.Volume = desiredVolume;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            player.Play();
            device.Start();
            await finished.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Stop the callback before disposing the player, decoder or input stream.
            try { device.Stop(); }
            finally
            {
                lock (sync) current = null;
                device.MasterMixer.RemoveComponent(player);
            }
        }
    }

    public void SetVolume(int volume)
    {
        lock (sync)
        {
            desiredVolume = Math.Clamp(volume, 0, 100) / 100f;
            if (current is not null) current.Volume = desiredVolume;
        }
    }

    public void Dispose()
    {
        lock (sync) disposed = true;
    }
}
