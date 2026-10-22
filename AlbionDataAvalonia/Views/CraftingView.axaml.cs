using AlbionDataAvalonia.ViewModels;
using Avalonia.Controls;

namespace AlbionDataAvalonia.Views;

public partial class CraftingView : UserControl
{
    public CraftingView()
    {
        InitializeComponent();
        AttachedToLogicalTree += (_, _) => (DataContext as CraftingViewModel)?.Activate();
        DetachedFromLogicalTree += (_, _) => (DataContext as CraftingViewModel)?.Deactivate();
    }
}
