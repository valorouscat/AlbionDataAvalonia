using AlbionDataAvalonia.Audio;
using AlbionDataAvalonia.Ui;
using AlbionDataAvalonia.Ui.Services;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class ActivityMapViewModel
{
    private readonly IFileDialogService? soundFiles;
    private readonly HostileAlertSoundService? hostileSoundService;

    [ObservableProperty] private bool hostileSoundEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostileSoundVolumeText))]
    private double hostileSoundVolume;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostileSoundFileName))]
    [NotifyCanExecuteChangedFor(nameof(UseDefaultHostileSoundCommand))]
    private string hostileSoundFile = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHostileSoundError))]
    private string hostileSoundError = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestHostileSoundCommand))]
    private bool isTestingHostileSound;

    public bool ShowHostileSoundSettings => IsLive && ShowHostileAlert;
    public bool HasHostileSoundError => HostileSoundError.Length > 0;
    public string HostileSoundFileName => string.IsNullOrWhiteSpace(HostileSoundFile) ? "Default alert" : Path.GetFileName(HostileSoundFile);
    public string HostileSoundVolumeText => UiFormat.Whole(HostileSoundVolume) + "%";

    private void SoundErrorChanged()
    {
        if (UiDispatch.Repost(SoundErrorChanged)) return;
        if (!disposed) HostileSoundError = hostileSoundService?.Error ?? "";
    }

    partial void OnHostileSoundEnabledChanged(bool value) => settings.MapHostileSoundEnabled = value;
    partial void OnHostileSoundVolumeChanged(double value) =>
        settings.MapHostileSoundVolume = double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), 0, 100) : 50;
    partial void OnHostileSoundFileChanged(string value) => settings.MapHostileSoundFile = value;

    private bool CanChooseHostileSound => soundFiles is not null;
    [RelayCommand(CanExecute = nameof(CanChooseHostileSound))]
    private async Task ChooseHostileSound()
    {
        try
        {
            if (soundFiles is null || await soundFiles.OpenAsync("Choose hostile alert sound", "MP3 audio", "*.mp3") is not { } file) return;
            using var selection = file;
            var path = file.TryGetLocalPath();
            if (path is null || !Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                HostileSoundError = "Choose an MP3 file stored on this device.";
                return;
            }
            HostileSoundFile = path;
            HostileSoundError = "";
        }
        catch (Exception failure)
        {
            Log.Warning(failure, "Could not select the hostile alert MP3");
            HostileSoundError = "Couldn't open the selected MP3. Choose another file.";
        }
    }

    private bool HasCustomHostileSound => !string.IsNullOrWhiteSpace(HostileSoundFile);
    [RelayCommand(CanExecute = nameof(HasCustomHostileSound))]
    private void UseDefaultHostileSound()
    {
        HostileSoundFile = "";
        HostileSoundError = "";
    }

    private bool CanTestHostileSound => hostileSoundService is not null && !IsTestingHostileSound;
    [RelayCommand(CanExecute = nameof(CanTestHostileSound))]
    private async Task TestHostileSound()
    {
        if (hostileSoundService is null) return;
        IsTestingHostileSound = true;
        try
        {
            await hostileSoundService.PreviewAsync();
            HostileSoundError = hostileSoundService.Error;
        }
        finally { IsTestingHostileSound = false; }
    }

    [RelayCommand]
    private void StopHostileSound() => hostileSoundService?.Stop();
}
