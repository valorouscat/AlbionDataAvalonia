using AlbionDataAvalonia.Ui;
using AlbionDataAvalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using System;
using System.ComponentModel;

namespace AlbionDataAvalonia.Views;

public partial class CombatMeterWindow : Window
{
    private CombatMeterViewModel? model;
    private MapOverlayPlatform? platform;
    private bool opened;

    public CombatMeterWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (model is not null) model.PropertyChanged -= ModelChanged;
        base.OnDataContextChanged(e);
        model = DataContext as CombatMeterViewModel;
        if (model is not null) model.PropertyChanged += ModelChanged;
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
        SystemDecorations = vm.IsOverlay ? SystemDecorations.None : SystemDecorations.Full;
        ShowInTaskbar = !vm.IsOverlay;
        ShowActivated = !vm.IsOverlay;
        Topmost = vm.IsOverlay && vm.Pinned;
        MinWidth = vm.IsOverlay ? 540 : 640;
        MinHeight = vm.IsOverlay ? 400 : 560;
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
    }
    private void Drag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (model?.IsOverlay == true && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
    private void Resize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(WindowEdge.SouthEast, e);
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
        if (model is not null) model.PropertyChanged -= ModelChanged;
        base.OnClosed(e);
    }
}
