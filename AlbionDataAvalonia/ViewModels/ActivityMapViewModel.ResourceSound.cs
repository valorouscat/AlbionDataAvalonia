using AlbionDataAvalonia.Audio;
using AlbionDataAvalonia.Ui;
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
    private readonly ResourceAlertSoundService? resourceSoundService;

    [ObservableProperty] private bool resourceSoundEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourceSoundVolumeText))]
    private double resourceSoundVolume;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResourceSoundFileName))]
    [NotifyCanExecuteChangedFor(nameof(UseDefaultResourceSoundCommand))]
    private string resourceSoundFile = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResourceSoundError))]
    private string resourceSoundError = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestResourceSoundCommand))]
    private bool isTestingResourceSound;

    public bool ShowResourceSoundSettings => IsLive && ShowResources;
    public bool HasResourceSoundError => ResourceSoundError.Length > 0;
    public string ResourceSoundFileName => string.IsNullOrWhiteSpace(ResourceSoundFile) ? "Soft resource chime" : Path.GetFileName(ResourceSoundFile);
    public string ResourceSoundVolumeText => UiFormat.Whole(ResourceSoundVolume) + "%";

    private void ResourceSoundErrorChanged()
    {
        if (UiDispatch.Repost(ResourceSoundErrorChanged)) return;
        if (!disposed) ResourceSoundError = resourceSoundService?.Error ?? "";
    }

    partial void OnResourceSoundEnabledChanged(bool value) => settings.MapResourceSoundEnabled = value;
    partial void OnResourceSoundVolumeChanged(double value) =>
        settings.MapResourceSoundVolume = double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value), 0, 100) : 35;
    partial void OnResourceSoundFileChanged(string value) => settings.MapResourceSoundFile = value;

    private bool CanChooseResourceSound => soundFiles is not null;
    [RelayCommand(CanExecute = nameof(CanChooseResourceSound))]
    private async Task ChooseResourceSound()
    {
        try
        {
            if (soundFiles is null || await soundFiles.OpenAsync("Choose resource alert sound", "MP3 audio", "*.mp3") is not { } file) return;
            using var selection = file;
            var path = file.TryGetLocalPath();
            if (path is null || !Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                ResourceSoundError = "Choose an MP3 file stored on this device.";
                return;
            }
            ResourceSoundFile = path;
            ResourceSoundError = "";
        }
        catch (Exception failure)
        {
            Log.Warning(failure, "Could not select the resource alert MP3");
            ResourceSoundError = "Couldn't open the selected MP3. Choose another file.";
        }
    }

    private bool HasCustomResourceSound => !string.IsNullOrWhiteSpace(ResourceSoundFile);
    [RelayCommand(CanExecute = nameof(HasCustomResourceSound))]
    private void UseDefaultResourceSound()
    {
        ResourceSoundFile = "";
        ResourceSoundError = "";
    }

    private bool CanTestResourceSound => resourceSoundService is not null && !IsTestingResourceSound;
    [RelayCommand(CanExecute = nameof(CanTestResourceSound))]
    private async Task TestResourceSound()
    {
        if (resourceSoundService is null) return;
        IsTestingResourceSound = true;
        try
        {
            await resourceSoundService.PreviewAsync();
            ResourceSoundError = resourceSoundService.Error;
        }
        finally { IsTestingResourceSound = false; }
    }

    [RelayCommand]
    private void StopResourceSound() => resourceSoundService?.Stop();
}
