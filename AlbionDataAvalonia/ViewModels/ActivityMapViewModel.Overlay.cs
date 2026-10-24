using AlbionDataAvalonia.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace AlbionDataAvalonia.ViewModels;

public sealed partial class ActivityMapViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapControls), nameof(ShowOverlayBar), nameof(OverlayShortcutHelp))]
    private bool isOverlay;
    [ObservableProperty] private bool overlayPinned;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayOpacityText))]
    private int overlayOpacity = 75;
    [ObservableProperty] private bool overlayClickThrough;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMapControls), nameof(ShowOverlayBar))]
    private bool overlayControlsVisible;
    [ObservableProperty] private bool isOverlayHidden;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayShortcutHelp))]
    private bool overlayShortcutsAvailable;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOverlayBar))]
    private bool overlayClicksPassThrough;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlayError))]
    private string overlayError = "";

    public bool ShowMapControls => !IsOverlay || OverlayControlsVisible;
    public bool ShowOverlayBar => IsOverlay && !OverlayClicksPassThrough;
    public bool SupportsOverlayClickThrough => OperatingSystem.IsWindows();
    public string OverlayOpacityText => $"{OverlayOpacity}%";
    public bool HasOverlayError => OverlayError.Length > 0;
    public string OverlayShortcutHelp => !OperatingSystem.IsWindows()
        ? "Ctrl+Alt+M hides the overlay; Ctrl+Alt+O toggles overlay mode while the map has focus. Global shortcuts and click-through require Windows. A hidden map can be restored from Activity › Show map."
        : OverlayShortcutsAvailable
            ? "Ctrl+Alt+M: hide / show overlay. Ctrl+Alt+O: toggle overlay mode, including when locked. These shortcuts work while the game has focus and the map window is open."
            : "With the map window open: Ctrl+Alt+O toggles overlay mode; Ctrl+Alt+M hides / shows the overlay. Click-through requires both global shortcuts to be available.";

    private void InitializeOverlay()
    {
        IsOverlay = settings.MapOverlayEnabled;
        OverlayPinned = settings.MapOverlayPinned;
        OverlayOpacity = settings.MapOverlayOpacity;
        OverlayClickThrough = settings.MapOverlayClickThrough;
    }

    partial void OnIsOverlayChanged(bool value)
    {
        settings.MapOverlayEnabled = value;
        OverlayControlsVisible = false;
        IsOverlayHidden = false;
    }
    partial void OnOverlayPinnedChanged(bool value) => settings.MapOverlayPinned = value;
    partial void OnOverlayOpacityChanged(int value)
    {
        settings.MapOverlayOpacity = value;
        if (value != settings.MapOverlayOpacity) OverlayOpacity = settings.MapOverlayOpacity;
    }
    partial void OnOverlayClickThroughChanged(bool value) => settings.MapOverlayClickThrough = value;

    [RelayCommand] private void ToggleOverlay() => IsOverlay = !IsOverlay;
    [RelayCommand] private void ExitOverlay() { IsOverlay = false; IsOverlayHidden = false; }
    [RelayCommand] private void ToggleOverlayControls() => OverlayControlsVisible = !OverlayControlsVisible;
    [RelayCommand] private void ToggleOverlayVisibility() { if (IsOverlay) IsOverlayHidden = !IsOverlayHidden; }
    [RelayCommand] private void RevealMap() => IsOverlayHidden = false;

    internal WindowPlacement? OverlayPlacement(bool overlay) => overlay ? settings.MapOverlayWindowPlacement : settings.MapWindowPlacement;
    internal void RememberOverlayPlacement(bool overlay, WindowPlacement placement)
    {
        if (overlay) settings.MapOverlayWindowPlacement = placement;
        else settings.MapWindowPlacement = placement;
    }
}
