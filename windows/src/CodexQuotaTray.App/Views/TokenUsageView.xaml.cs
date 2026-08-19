using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexQuotaTray.App.Views;

public sealed partial class TokenUsageView : UserControl
{
    private const float TooltipWidth = 176f;
    private const float TooltipHeight = 64f;
    private const int PointerExitDebounceMilliseconds = 40;
    private readonly DispatcherQueue uiDispatcher;
    private readonly TokenUsageViewModel tokenUsageViewModel;
    private readonly IntPtr hostWindowHandle;
    private readonly HeatmapTooltipWindow sharedTooltipWindow;
    private Border? activeHeatmapCell;
    private int? activeHeatmapIndex;
    private bool sharedTooltipHasPosition;
    private bool heatmapInteractionEnabled;
    private bool firstTooltipDiagnosticsReported;
    private int heatmapPointerRevision;
    private bool applyLayoutMeasurementPending;
    private long applyCompletedTimestamp;

    public TokenUsageView(TokenUsageViewModel viewModel, IntPtr hostWindowHandle)
    {
        tokenUsageViewModel = viewModel;
        this.hostWindowHandle = hostWindowHandle;
        uiDispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        sharedTooltipWindow = new HeatmapTooltipWindow(hostWindowHandle);
        tokenUsageViewModel.ApplyCompleted += OnTokenUsageApplyCompleted;
        LayoutUpdated += OnTokenUsageLayoutUpdated;
        DataContext = viewModel;
    }

    private void OnTokenUsageApplyCompleted(object? sender, EventArgs args)
    {
        var completedTimestamp = Stopwatch.GetTimestamp();
        _ = uiDispatcher.TryEnqueue(() =>
        {
            applyCompletedTimestamp = completedTimestamp;
            applyLayoutMeasurementPending = true;
        });
    }

    private void OnTokenUsageLayoutUpdated(object? sender, object args)
    {
        if (!applyLayoutMeasurementPending
            || !IsLoaded
            || Visibility != Visibility.Visible
            || ActualWidth <= 0
            || ActualHeight <= 0
            || !tokenUsageViewModel.ShowContent)
        {
            return;
        }

        applyLayoutMeasurementPending = false;
        Debug.WriteLine(
            $"TokenUsage diagnostics: stage=ui-layout-visible "
            + $"elapsedMs={Stopwatch.GetElapsedTime(applyCompletedTimestamp).TotalMilliseconds:F1}");
    }

    internal void PrepareHeatmapInteraction()
    {
        heatmapInteractionEnabled = true;
        sharedTooltipWindow.ApplyTheme(TokenUsageRoot.ActualTheme);
    }

    private void OnHeatmapPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        heatmapPointerRevision++;
        UpdateHeatmapSelection(args);
    }

    private void OnHeatmapPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        heatmapPointerRevision++;
        UpdateHeatmapSelection(args);
    }

    private void OnHeatmapPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        heatmapPointerRevision++;
        UpdateHeatmapSelection(args);
    }

    private void OnHeatmapPointerExited(object sender, PointerRoutedEventArgs args)
    {
        var revision = ++heatmapPointerRevision;
        _ = ClearHeatmapSelectionAfterExitAsync(revision);
    }

    private async Task ClearHeatmapSelectionAfterExitAsync(int revision)
    {
        await Task.Delay(PointerExitDebounceMilliseconds).ConfigureAwait(false);
        _ = uiDispatcher.TryEnqueue(() =>
        {
            if (revision == heatmapPointerRevision)
            {
                ClearHeatmapSelection();
            }
        });
    }

    private void UpdateHeatmapSelection(PointerRoutedEventArgs args)
    {
        if (!heatmapInteractionEnabled)
        {
            return;
        }

        var collectFirstTooltipDiagnostics = !firstTooltipDiagnosticsReported;
        var interactionStarted = collectFirstTooltipDiagnostics
            ? Stopwatch.GetTimestamp()
            : 0L;
        var point = args.GetCurrentPoint(HeatmapHitSurface).Position;
        var hitTestStarted = collectFirstTooltipDiagnostics
            ? Stopwatch.GetTimestamp()
            : 0L;
        var index = TokenHeatmapInteraction.HitTest(
            (float)point.X - TokenHeatmapInteraction.HitSurfaceInset,
            (float)point.Y - TokenHeatmapInteraction.HitSurfaceInset,
            (DataContext as TokenUsageViewModel)?.HeatmapCells.Count ?? 0);
        var hitTestMilliseconds = collectFirstTooltipDiagnostics
            ? Stopwatch.GetElapsedTime(hitTestStarted).TotalMilliseconds
            : 0d;

        if (index == activeHeatmapIndex)
        {
            return;
        }

        // Keep the current tooltip while the pointer is still inside the
        // expanded hit surface but falls outside a valid cell for one frame.
        // Clearing here makes a fast drag look like repeated hide/show flicker.
        if (index is not int validIndex)
        {
            return;
        }

        var tryGetElementStarted = collectFirstTooltipDiagnostics
            ? Stopwatch.GetTimestamp()
            : 0L;
        var cell = HeatmapItemsRepeater.TryGetElement(validIndex) as Border;
        var tryGetElementMilliseconds = collectFirstTooltipDiagnostics
            ? Stopwatch.GetElapsedTime(tryGetElementStarted).TotalMilliseconds
            : 0d;
        var heatmapCell = cell?.DataContext as TokenHeatmapCell;

        // A repeater can briefly have no realized element while it is laying
        // out. Keep the visible selection until the next pointer sample.
        if (cell is null || heatmapCell is null)
        {
            return;
        }

        ClearHeatmapCell();

        activeHeatmapIndex = validIndex;
        activeHeatmapCell = cell;
        cell.Scale = new Vector3(TokenHeatmapInteraction.SelectedScale, TokenHeatmapInteraction.SelectedScale, 1f);
        cell.Translation = new Vector3(0f, 0f, 16f);
        cell.Shadow = new ThemeShadow();
        var isEmptyCell = heatmapCell.Bucket == 0;
        cell.BorderBrush = CreateHeatmapHighlightBrush(cell.Background, isEmptyCell);
        cell.BorderThickness = new Thickness(isEmptyCell ? 1.5 : 1);
        Canvas.SetZIndex(cell, 1);
        UpdateSharedHeatmapTooltip(
            validIndex,
            heatmapCell,
            collectFirstTooltipDiagnostics,
            interactionStarted,
            hitTestMilliseconds,
            tryGetElementMilliseconds);
    }

    private void ClearHeatmapSelection()
    {
        ClearHeatmapCell();
        HideSharedHeatmapTooltip();
    }

    internal void ResetHeatmapInteraction()
    {
        heatmapPointerRevision++;
        heatmapInteractionEnabled = false;
        ClearHeatmapCell();
        sharedTooltipHasPosition = false;
        sharedTooltipWindow.Hide();
    }

    internal void Dispose()
    {
        tokenUsageViewModel.ApplyCompleted -= OnTokenUsageApplyCompleted;
        sharedTooltipWindow.Dispose();
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

    private void UpdateSharedHeatmapTooltip(
        int index,
        TokenHeatmapCell heatmapCell,
        bool collectFirstTooltipDiagnostics,
        long interactionStarted,
        double hitTestMilliseconds,
        double tryGetElementMilliseconds)
    {
        var transformStarted = collectFirstTooltipDiagnostics
            ? Stopwatch.GetTimestamp()
            : 0L;
        var heatmapOrigin = HeatmapItemsRepeater
            .TransformToVisual(TokenUsageRoot)
            .TransformPoint(new Point(0, 0));
        var transformMilliseconds = collectFirstTooltipDiagnostics
            ? Stopwatch.GetElapsedTime(transformStarted).TotalMilliseconds
            : 0d;
        var target = TokenHeatmapInteraction.PlaceTooltipAboveCell(
            index,
            (DataContext as TokenUsageViewModel)?.HeatmapCells.Count ?? 0,
            TooltipWidth,
            TooltipHeight,
            (float)heatmapOrigin.X,
            (float)heatmapOrigin.Y);
        target = ClampTooltipToWorkArea(target);
        var scale = Math.Max(1.0, HeatmapInteractionHost.XamlRoot?.RasterizationScale ?? 1.0);
        var screenPosition = GetTooltipScreenPosition(target, scale);
        sharedTooltipWindow.SetContent(heatmapCell.TokenText, heatmapCell.DateText);
        // The DWM blur must follow the same screen pixels as the content, so
        // the single shared HWND moves for each realized cell.
        var tooltipShown = sharedTooltipWindow.ShowAt(
            screenPosition,
            scale,
            TokenUsageRoot.ActualTheme);
        var wasPreviouslyVisible = sharedTooltipHasPosition;
        sharedTooltipHasPosition = tooltipShown;

        if (!wasPreviouslyVisible)
        {
            if (collectFirstTooltipDiagnostics)
            {
                firstTooltipDiagnosticsReported = true;
                Debug.WriteLine(
                    $"TokenUsage tooltip diagnostics: hitTestMs={hitTestMilliseconds:F2} "
                    + $"tryGetElementMs={tryGetElementMilliseconds:F2} "
                    + $"transformToVisualMs={transformMilliseconds:F2} measureMs=0 "
                    + $"firstShowTotalMs={Stopwatch.GetElapsedTime(interactionStarted).TotalMilliseconds:F2}");
            }
        }
    }

    private void HideSharedHeatmapTooltip()
    {
        if (!sharedTooltipHasPosition)
        {
            return;
        }

        sharedTooltipHasPosition = false;
        sharedTooltipWindow.Hide();
    }

    private TokenHeatmapTooltipPlacement ClampTooltipToWorkArea(TokenHeatmapTooltipPlacement target)
    {
        var scale = Math.Max(1.0, HeatmapInteractionHost.XamlRoot?.RasterizationScale ?? 1.0);
        var rootOrigin = GetRootScreenOrigin();
        var screenX = rootOrigin.X + (target.X * (float)scale);
        var screenY = rootOrigin.Y + (target.Y * (float)scale);
        var monitorAnchor = new NativeMethods.NativePoint
        {
            X = (int)Math.Round(screenX + (TooltipWidth * scale / 2.0), MidpointRounding.AwayFromZero),
            Y = (int)Math.Round(screenY, MidpointRounding.AwayFromZero),
        };
        var workArea = WindowPlacementService.GetWorkArea(
            new System.Drawing.Rectangle(monitorAnchor.X, monitorAnchor.Y, 1, 1));
        var tooltipWidthPixels = TooltipWidth * scale;
        var tooltipHeightPixels = TooltipHeight * scale;
        var maxScreenX = workArea.Right - tooltipWidthPixels;
        var clampedScreenX = maxScreenX < workArea.Left
            ? workArea.Left
            : Math.Clamp(screenX, workArea.Left, maxScreenX);
        var maxScreenY = workArea.Bottom - tooltipHeightPixels;
        var clampedScreenY = maxScreenY < workArea.Top
            ? workArea.Top
            : Math.Clamp(screenY, workArea.Top, maxScreenY);

        return target with
        {
            X = (float)((clampedScreenX - rootOrigin.X) / scale),
            Y = (float)((clampedScreenY - rootOrigin.Y) / scale),
        };
    }

    private PointInt32 GetTooltipScreenPosition(
        TokenHeatmapTooltipPlacement target,
        double scale)
    {
        var rootOrigin = GetRootScreenOrigin();
        return new PointInt32
        {
            X = (int)Math.Round(rootOrigin.X + (target.X * scale), MidpointRounding.AwayFromZero),
            Y = (int)Math.Round(rootOrigin.Y + (target.Y * scale), MidpointRounding.AwayFromZero),
        };
    }

    private (int X, int Y) GetRootScreenOrigin()
    {
        var point = new NativeMethods.NativePoint();
        return NativeMethods.ClientToScreen(hostWindowHandle, ref point)
            ? (point.X, point.Y)
            : (0, 0);
    }

    private static Brush CreateHeatmapHighlightBrush(Brush background, bool isEmptyCell)
    {
        if (isEmptyCell)
        {
            return (Brush)Application.Current.Resources["TokenHeatmapEmptyCellHighlightBrush"];
        }

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
