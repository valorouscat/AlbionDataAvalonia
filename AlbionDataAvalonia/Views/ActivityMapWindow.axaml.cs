using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.Ui;
using AlbionDataAvalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.ComponentModel;
using System.Linq;

namespace AlbionDataAvalonia.Views;

public partial class ActivityMapWindow : Window
{
    private ActivityMapViewModel? model;
    private MapOverlayPlatform? overlayPlatform;
    private bool? appliedOverlay;
    private bool opened;

    public ActivityMapWindow()
    {
        InitializeComponent();
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => MapCanvas.ZoomBy(1.4);

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => MapCanvas.ZoomBy(1 / 1.4);

    private void Fit_Click(object? sender, RoutedEventArgs e) => MapCanvas.Fit();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (model is not null) model.PropertyChanged -= ModelChanged;
        base.OnDataContextChanged(e);
        model = DataContext as ActivityMapViewModel;
        if (model is null) return;
        model.PropertyChanged += ModelChanged;
        ApplyOverlay();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        opened = true;
        overlayPlatform ??= new(this, () => model?.ToggleOverlayVisibilityCommand.Execute(null), () => model?.ExitOverlayCommand.Execute(null));
        ApplyOverlay();
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (UiDispatch.Repost(() => ModelChanged(sender, e))) return;
        if (e.PropertyName is nameof(ActivityMapViewModel.IsOverlay) or nameof(ActivityMapViewModel.OverlayPinned)
            or nameof(ActivityMapViewModel.OverlayOpacity) or nameof(ActivityMapViewModel.OverlayClickThrough)
            or nameof(ActivityMapViewModel.OverlayControlsVisible)) ApplyOverlay();
        else if (e.PropertyName == nameof(ActivityMapViewModel.IsOverlayHidden))
        {
            if (model?.IsOverlayHidden == true) Hide();
            else if (opened && !IsVisible) Show();
        }
    }

    private void ApplyOverlay()
    {
        if (model is not { } viewModel) return;
        var overlay = viewModel.IsOverlay;
        var changedMode = appliedOverlay != overlay;
        if (changedMode && appliedOverlay is { } previous && opened) RememberPlacement(previous);
        appliedOverlay = overlay;
        MinWidth = overlay ? 240 : 560;
        MinHeight = overlay ? 200 : 420;
        SystemDecorations = overlay ? SystemDecorations.None : SystemDecorations.Full;
        ShowInTaskbar = !overlay;
        ShowActivated = !overlay;
        Topmost = overlay && viewModel.OverlayPinned;
        TransparencyLevelHint = overlay ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
        Background = overlay ? null : UiPalette.Brush("UiWindowBrush");
        if (changedMode)
        {
            if (opened) RestorePlacement(viewModel.OverlayPlacement(overlay), overlay);
            else { Width = overlay ? 480 : 980; Height = overlay ? 480 : 700; }
        }
        var nativeOpacity = false;
        if (opened && overlayPlatform is { } platform)
        {
            nativeOpacity = platform.Configure(overlay, viewModel.OverlayOpacity,
                viewModel.OverlayClickThrough && !viewModel.OverlayControlsVisible, out var passing, out var error);
            viewModel.OverlayShortcutsAvailable = platform.ShortcutsAvailable;
            viewModel.OverlayClicksPassThrough = passing;
            viewModel.OverlayError = error;
        }
        MapContents.Opacity = overlay && !nativeOpacity ? viewModel.OverlayOpacity / 100d : 1;
        if (changedMode && !overlay && opened && IsVisible) Activate();
    }

    private void RememberPlacement(bool overlay)
    {
        if (model is null) return;
        var placement = WindowState == WindowState.Normal
            ? new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height)
            : (model.OverlayPlacement(overlay) ?? new(Position.X, Position.Y, overlay ? 480 : 980, overlay ? 480 : 700))
                with
            { Maximized = WindowState == WindowState.Maximized };
        model.RememberOverlayPlacement(overlay, placement);
    }

    private void RestorePlacement(WindowPlacement? saved, bool overlay)
    {
        WindowState = WindowState.Normal;
        saved ??= new(Position.X, Position.Y, overlay ? 480 : 980, overlay ? 480 : 700);
        var screens = Screens?.All.Select(x => new PlacementScreen(x.WorkingArea, x.Scaling, x.IsPrimary)).ToArray() ?? [];
        var plan = WindowPlacementTracker.Plan(saved, screens, new(MinWidth, MinHeight), new(Width, Height), true);
        Width = plan.Size.Width;
        Height = plan.Size.Height;
        if (plan.Position is { } position) Position = position;
        if (plan.Maximized && !overlay) WindowState = WindowState.Maximized;
    }

    private void OverlayDrag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OverlayResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || model is null || model.OverlayShortcutsAvailable
            || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Alt)) return;
        if (e.Key == Key.M && model.IsOverlay) model.ToggleOverlayVisibilityCommand.Execute(null);
        else if (e.Key == Key.O && model.IsOverlay) model.ExitOverlayCommand.Execute(null);
        else return;
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        overlayPlatform?.Dispose();
        if (model is not null) model.PropertyChanged -= ModelChanged;
        base.OnClosed(e);
    }
}
