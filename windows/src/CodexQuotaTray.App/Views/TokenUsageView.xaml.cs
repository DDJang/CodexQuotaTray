using System.Numerics;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CodexQuotaTray.App.Views;

public sealed partial class TokenUsageView : UserControl
{
    public TokenUsageView(TokenUsageViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnHeatmapCellPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Border cell)
        {
            cell.Scale = new Vector3(1.28f, 1.28f, 1f);
            cell.Translation = new Vector3(0f, 0f, 16f);
            cell.Shadow = new ThemeShadow();
            cell.BorderBrush = (Brush)Application.Current.Resources["TokenHeatmapCellBorderBrush"];
            cell.BorderThickness = new Thickness(2);
            Canvas.SetZIndex(cell, 1);
            if (ToolTipService.GetToolTip(cell) is ToolTip toolTip)
            {
                toolTip.IsOpen = true;
            }
        }
    }

    private void OnHeatmapCellPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Border cell)
        {
            cell.Scale = Vector3.One;
            cell.Translation = Vector3.Zero;
            cell.Shadow = null;
            cell.BorderThickness = new Thickness(0);
            Canvas.SetZIndex(cell, 0);
            if (ToolTipService.GetToolTip(cell) is ToolTip toolTip)
            {
                toolTip.IsOpen = false;
            }
        }
    }
}
