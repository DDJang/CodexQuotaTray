using System.Numerics;
using System.Threading.Tasks;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexQuotaTray.App.Views;

public sealed partial class TokenUsageView : UserControl
{
    private readonly DispatcherQueue uiDispatcher;
    private Border? activeHeatmapCell;
    private int? activeHeatmapIndex;
    private bool sharedTooltipHasPosition;
    private int sharedTooltipRevision;

    public TokenUsageView(TokenUsageViewModel viewModel)
    {
        uiDispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnHeatmapPointerEntered(object sender, PointerRoutedEventArgs args) =>
        UpdateHeatmapSelection(args);

    private void OnHeatmapPointerMoved(object sender, PointerRoutedEventArgs args) =>
        UpdateHeatmapSelection(args);

    private void OnHeatmapPointerPressed(object sender, PointerRoutedEventArgs args) =>
        UpdateHeatmapSelection(args);

    private void OnHeatmapPointerExited(object sender, PointerRoutedEventArgs args) =>
        ClearHeatmapSelection();

    private void UpdateHeatmapSelection(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(HeatmapHitSurface).Position;
        var index = TokenHeatmapInteraction.HitTest(
            (float)point.X - TokenHeatmapInteraction.HitSurfaceInset,
            (float)point.Y - TokenHeatmapInteraction.HitSurfaceInset,
            (DataContext as TokenUsageViewModel)?.HeatmapCells.Count ?? 0);

        if (index == activeHeatmapIndex)
        {
            return;
        }

        ClearHeatmapCell();
        if (index is not int validIndex
            || HeatmapItemsRepeater.TryGetElement(validIndex) is not Border cell
            || cell.DataContext is not TokenHeatmapCell heatmapCell)
        {
            HideSharedHeatmapTooltip();
            return;
        }

        activeHeatmapIndex = validIndex;
        activeHeatmapCell = cell;
        cell.Scale = new Vector3(TokenHeatmapInteraction.SelectedScale, TokenHeatmapInteraction.SelectedScale, 1f);
        cell.Translation = new Vector3(0f, 0f, 16f);
        cell.Shadow = new ThemeShadow();
        cell.BorderBrush = CreateHeatmapHighlightBrush(cell.Background);
        cell.BorderThickness = new Thickness(1);
        Canvas.SetZIndex(cell, 1);
        UpdateSharedHeatmapTooltip(validIndex, heatmapCell);
    }

    private void ClearHeatmapSelection()
    {
        ClearHeatmapCell();
        HideSharedHeatmapTooltip();
    }

    private void ClearHeatmapCell()
    {
        if (activeHeatmapCell is Border cell)
        {
            cell.Scale = Vector3.One;
            cell.Translation = Vector3.Zero;
            cell.Shadow = null;
            cell.BorderBrush = null;
            cell.BorderThickness = new Thickness(0);
            Canvas.SetZIndex(cell, 0);
        }

        activeHeatmapCell = null;
        activeHeatmapIndex = null;
    }

    private void UpdateSharedHeatmapTooltip(int index, TokenHeatmapCell heatmapCell)
    {
        HeatmapTooltipTokenText.Text = heatmapCell.TokenText;
        HeatmapTooltipDateText.Text = heatmapCell.DateText;
        SharedHeatmapTooltip.Visibility = Visibility.Visible;
        SharedHeatmapTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var viewportWidth = (float)Math.Max(
            HeatmapInteractionHost.ActualWidth,
            HeatmapItemsRepeater.ActualWidth);
        var viewportHeight = (float)Math.Max(
            HeatmapInteractionHost.ActualHeight,
            HeatmapItemsRepeater.ActualHeight);
        var heatmapOriginX = MathF.Max(
            0f,
            ((float)HeatmapInteractionHost.ActualWidth - (float)HeatmapItemsRepeater.ActualWidth) / 2f);
        var heatmapOriginY = MathF.Max(
            0f,
            ((float)HeatmapInteractionHost.ActualHeight - (float)HeatmapItemsRepeater.ActualHeight) / 2f);
        var desiredSize = SharedHeatmapTooltip.DesiredSize;
        var target = TokenHeatmapInteraction.PlaceTooltip(
            viewportWidth,
            viewportHeight,
            index,
            (DataContext as TokenUsageViewModel)?.HeatmapCells.Count ?? 0,
            (float)desiredSize.Width,
            (float)desiredSize.Height,
            heatmapOriginX,
            heatmapOriginY);

        sharedTooltipRevision++;
        var tooltipVisual = ElementCompositionPreview.GetElementVisual(SharedHeatmapTooltip);
        tooltipVisual.CenterPoint = new Vector3(
            (float)desiredSize.Width / 2f,
            (float)desiredSize.Height / 2f,
            0f);

        if (!sharedTooltipHasPosition)
        {
            tooltipVisual.StopAnimation("Offset");
            tooltipVisual.Offset = new Vector3(target.X, target.Y, 0f);
            tooltipVisual.Opacity = 0f;
            tooltipVisual.Scale = new Vector3(0.96f, 0.96f, 1f);
            sharedTooltipHasPosition = true;
        }
        else
        {
            StartTooltipSpring(tooltipVisual, target);
        }

        AnimateTooltipVisual(tooltipVisual, 1f, Vector3.One);
    }

    private void HideSharedHeatmapTooltip()
    {
        if (SharedHeatmapTooltip.Visibility == Visibility.Collapsed)
        {
            return;
        }

        var revision = ++sharedTooltipRevision;
        var tooltipVisual = ElementCompositionPreview.GetElementVisual(SharedHeatmapTooltip);
        AnimateTooltipVisual(
            tooltipVisual,
            0f,
            new Vector3(0.96f, 0.96f, 1f));
        _ = CollapseSharedHeatmapTooltipAsync(revision);
    }

    private async Task CollapseSharedHeatmapTooltipAsync(int revision)
    {
        await Task.Delay(TokenHeatmapInteraction.TooltipFadeDurationMilliseconds).ConfigureAwait(false);
        _ = uiDispatcher.TryEnqueue(() =>
        {
            if (revision != sharedTooltipRevision || activeHeatmapIndex is not null)
            {
                return;
            }

            SharedHeatmapTooltip.Visibility = Visibility.Collapsed;
            sharedTooltipHasPosition = false;
        });
    }

    private static void StartTooltipSpring(Visual tooltipVisual, TokenHeatmapTooltipPlacement target)
    {
        tooltipVisual.StopAnimation("Offset");
        var animation = tooltipVisual.Compositor.CreateSpringVector3Animation();
        animation.DampingRatio = TokenHeatmapInteraction.TooltipSpringDampingRatio;
        animation.Period = TimeSpan.FromMilliseconds(TokenHeatmapInteraction.TooltipSpringPeriodMilliseconds);
        animation.FinalValue = new Vector3(target.X, target.Y, 0f);
        tooltipVisual.StartAnimation("Offset", animation);
    }

    private static void AnimateTooltipVisual(Visual tooltipVisual, float opacity, Vector3 scale)
    {
        var compositor = tooltipVisual.Compositor;
        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = TimeSpan.FromMilliseconds(TokenHeatmapInteraction.TooltipFadeDurationMilliseconds);
        opacityAnimation.InsertKeyFrame(1f, opacity);
        tooltipVisual.StartAnimation("Opacity", opacityAnimation);

        var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = TimeSpan.FromMilliseconds(TokenHeatmapInteraction.TooltipFadeDurationMilliseconds);
        scaleAnimation.InsertKeyFrame(1f, scale);
        tooltipVisual.StartAnimation("Scale", scaleAnimation);
    }

    private static Brush CreateHeatmapHighlightBrush(Brush background)
    {
        if (new AccessibilitySettings().HighContrast)
        {
            return (Brush)Application.Current.Resources["TokenHeatmapCellBorderBrush"];
        }

        if (background is not SolidColorBrush solidColorBrush)
        {
            return background;
        }

        var color = solidColorBrush.Color;
        return new SolidColorBrush(Color.FromArgb(
            color.A,
            LiftChannel(color.R),
            LiftChannel(color.G),
            LiftChannel(color.B)));
    }

    private static byte LiftChannel(byte channel)
    {
        const double whiteMix = 0.24;
        return (byte)Math.Round(channel + ((255 - channel) * whiteMix), MidpointRounding.AwayFromZero);
    }
}
