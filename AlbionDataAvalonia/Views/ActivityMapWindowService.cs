using AlbionDataAvalonia.Activity;
using AlbionDataAvalonia.Audio;
using AlbionDataAvalonia.Ui.Services;
using AlbionDataAvalonia.Maps;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.ViewModels;
using AlbionDataAvalonia.Ui;
using System;
using System.ComponentModel;

namespace AlbionDataAvalonia.Views;

/// <summary>Owns the single map window. Its view model reads the tracker only while the window is open.</summary>
public sealed class ActivityMapWindowService
{
    private readonly SingleWindowHost<ActivityMapWindow, ActivityMapViewModel> host;

    public ActivityMapWindowService(ActivityTrackerService tracker, ActivityPersistence persistence, MapDataService maps, SettingsManager settings,
        IFileDialogService files, HostileAlertSoundService sound, ResourceAlertSoundService resourceSound)
    {
        var user = settings.UserSettings;
        host = new(() =>
        {
            var model = new ActivityMapViewModel(tracker, persistence, maps, user, files, sound, resourceSound);
            model.PropertyChanged += ModelChanged;
            return model;
        }, model => model.Start(), new WindowMemory(
            () => user.MapWindowOpen, open => user.MapWindowOpen = open,
            () => user.MapOverlayEnabled ? user.MapOverlayWindowPlacement : user.MapWindowPlacement,
            placement => { if (user.MapOverlayEnabled) user.MapOverlayWindowPlacement = placement; else user.MapWindowPlacement = placement; }), restoreSize: true);
        host.IsOpenChanged += (_, _) => IsOpenChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsOpen => host.IsOpen;
    public string ButtonText => host.ViewModel?.IsOverlayHidden == true ? "Show map" : IsOpen ? "Close map" : "Open map";

    public event EventHandler? IsOpenChanged;

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (UiDispatch.Repost(() => ModelChanged(sender, e))) return;
        if (e.PropertyName == nameof(ActivityMapViewModel.IsOverlayHidden)) IsOpenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreIfOpen() => host.RestoreIfOpen();

    public void Toggle()
    {
        if (host.ViewModel?.IsOverlayHidden == true) host.ViewModel.RevealMapCommand.Execute(null);
        else host.Toggle();
    }

    public void Show()
    {
        host.ViewModel?.RevealMapCommand.Execute(null);
        host.Show();
    }

    public void ShowRun(Guid runId)
    {
        Show();
        host.ViewModel?.ExitOverlayCommand.Execute(null);
        host.ViewModel?.ShowRun(runId);
    }
}
