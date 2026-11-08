using Avalonia.Controls;
using Avalonia.Input;

namespace AlbionDataAvalonia.Views;

public partial class CombatMeterWidget : UserControl
{
    public CombatMeterWidget() => InitializeComponent();

    private void Drag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            window.BeginMoveDrag(e);
    }
    private void Resize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window window && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            window.BeginResizeDrag(WindowEdge.SouthEast, e);
    }
}
