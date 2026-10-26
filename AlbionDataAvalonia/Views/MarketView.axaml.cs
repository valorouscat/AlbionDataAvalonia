using AlbionDataAvalonia.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AlbionDataAvalonia.Views;

public partial class MarketView : UserControl
{
    public MarketView()
    {
        InitializeComponent();
        // Closing a selection popup is view interaction; selection and loading remain in the view model.
        MarketItems.PointerReleased += (_, args) =>
        {
            if (args.Source is Control control && (control is ListBoxItem || control.FindAncestorOfType<ListBoxItem>() is not null))
                ItemPicker.Flyout?.Hide();
        };
        MarketItems.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && MarketItems.SelectedItem is not null) { ItemPicker.Flyout?.Hide(); args.Handled = true; }
        };
        AttachedToLogicalTree += (_, _) => (DataContext as MarketViewModel)?.Activate();
        DetachedFromLogicalTree += (_, _) => (DataContext as MarketViewModel)?.Deactivate();
    }
}
