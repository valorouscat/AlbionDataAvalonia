using AlbionDataAvalonia.Ui;
using AlbionDataAvalonia.Settings;
using AlbionDataAvalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System;
using System.ComponentModel;
using System.Linq;

namespace AlbionDataAvalonia.Views;

public partial class CombatMeterWindow : Window
{
    private CombatMeterViewModel? model;
    private MapOverlayPlatform? platform;
    private bool opened;
    private bool? appliedOverlay;

    public CombatMeterWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (model is not null) { model.PropertyChanged -= ModelChanged; model.ExpandWindowRequested -= ExpandWindow; }
        base.OnDataContextChanged(e);
        model = DataContext as CombatMeterViewModel;
        if (model is not null) { model.PropertyChanged += ModelChanged; model.ExpandWindowRequested += ExpandWindow; }
        ApplyOverlay();
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        opened = true;
        platform ??= new(this, () => model?.ToggleVisibilityCommand.Execute(null),
            () => model?.ToggleOverlayCommand.Execute(null), 'D', 'H', "combat meter");
        ApplyOverlay();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (UiDispatch.Repost(() => ModelChanged(sender, e))) return;
        if (e.PropertyName is nameof(CombatMeterViewModel.IsOverlay) or nameof(CombatMeterViewModel.Pinned)
            or nameof(CombatMeterViewModel.ClickThrough) or nameof(CombatMeterViewModel.OverlayOpacity)
            or nameof(CombatMeterViewModel.ShowSettings)) ApplyOverlay();
        if (e.PropertyName == nameof(CombatMeterViewModel.IsHidden))
        {
            if (model?.IsHidden == true) Hide();
            else if (opened && !IsVisible) Show();
        }
    }
    private void ApplyOverlay()
    {
        if (model is not { } vm) return;
        var changedMode = appliedOverlay != vm.IsOverlay;
        if (changedMode && appliedOverlay is { } previous && opened) RememberPlacement(previous);
        appliedOverlay = vm.IsOverlay;
        SystemDecorations = vm.IsOverlay ? SystemDecorations.None : SystemDecorations.Full;
        ShowInTaskbar = !vm.IsOverlay;
        ShowActivated = !vm.IsOverlay;
        Topmost = vm.IsOverlay && vm.Pinned;
        MinWidth = vm.IsOverlay ? 260 : 360;
        MinHeight = vm.IsOverlay ? 180 : 260;
        if (changedMode)
        {
            if (opened) RestorePlacement(vm.Placement(vm.IsOverlay), vm.IsOverlay);
            else { Width = vm.IsOverlay ? 320 : 760; Height = vm.IsOverlay ? 260 : 560; }
        }
        TransparencyLevelHint = vm.IsOverlay ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
        Background = vm.IsOverlay ? null : UiPalette.Brush("UiWindowBrush");
        var native = false;
        if (opened && platform is not null)
        {
            native = platform.Configure(vm.IsOverlay, vm.OverlayOpacity, vm.ClickThrough && !vm.ShowSettings, out _, out var error);
            vm.ShortcutsAvailable = platform.ShortcutsAvailable;
            vm.OverlayError = error;
        }
        MeterContents.Opacity = vm.IsOverlay && !native ? vm.OverlayOpacity / 100d : 1;
        UpdateCompactLayout();
        if (changedMode && !vm.IsOverlay && opened && IsVisible) Activate();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) UpdateCompactLayout();
    }

    private void UpdateCompactLayout()
    {
        if (model is { } vm && ClientSize.Width > 0 && ClientSize.Height > 0)
            vm.IsCompactLayout = ClientSize.Width < 600 || ClientSize.Height < 480;
    }

    private void RememberPlacement(bool overlay)
    {
        if (model is null) return;
        var placement = WindowState == WindowState.Normal
            ? new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height)
            : (model.Placement(overlay) ?? new(Position.X, Position.Y, overlay ? 320 : 760, overlay ? 260 : 560))
                with
            { Maximized = WindowState == WindowState.Maximized };
        model.RememberPlacement(overlay, placement);
    }

    private void RestorePlacement(WindowPlacement? saved, bool overlay)
    {
        WindowState = WindowState.Normal;
        saved ??= new(Position.X, Position.Y, overlay ? 320 : 760, overlay ? 260 : 560);
        var screens = Screens?.All.Select(x => new PlacementScreen(x.WorkingArea, x.Scaling, x.IsPrimary)).ToArray() ?? [];
        var plan = WindowPlacementTracker.Plan(saved, screens, new(MinWidth, MinHeight), new(Width, Height), true);
        Width = plan.Size.Width;
        Height = plan.Size.Height;
        if (plan.Position is { } position) Position = position;
        if (plan.Maximized && !overlay) WindowState = WindowState.Maximized;
    }

    private void ExpandWindow(object? sender, EventArgs e)
    {
        if (Width < 600) Width = 760;
        if (Height < 480) Height = 560;
        UpdateCompactLayout();
        Activate();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || model is null || model.ShortcutsAvailable || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Alt)) return;
        if (e.Key == Key.D) model.ToggleVisibilityCommand.Execute(null);
        else if (e.Key == Key.H) model.ToggleOverlayCommand.Execute(null);
        else return;
        e.Handled = true;
    }
    protected override void OnClosed(EventArgs e)
    {
        platform?.Dispose();
        if (model is not null) { model.PropertyChanged -= ModelChanged; model.ExpandWindowRequested -= ExpandWindow; }
        base.OnClosed(e);
    }
}
