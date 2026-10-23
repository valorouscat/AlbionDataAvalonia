using AlbionDataAvalonia.ViewModels;
using Avalonia.Controls;

namespace AlbionDataAvalonia.Views;

public partial class MarketView : UserControl
{
    public MarketView()
    {
        InitializeComponent();
        AttachedToLogicalTree += (_, _) => (DataContext as MarketViewModel)?.Activate();
        DetachedFromLogicalTree += (_, _) => (DataContext as MarketViewModel)?.Deactivate();
    }
}
