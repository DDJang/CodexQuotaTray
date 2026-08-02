using System.Drawing;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using CodexQuotaTray.Core.Persistence;
using WinRT.Interop;

namespace CodexQuotaTray.App.Views;

public sealed partial class MainWindow : Window
{
    private const double PanelWidthDips = 420;
    private readonly MainViewModel viewModel;
    private readonly WindowPlacementService placement = new();
    private readonly BackdropService backdrop = new();
    private readonly WindowVisibilityController visibility = new();
    private readonly AppWindow appWindow;
    private readonly IntPtr hwnd;
    private bool exiting;
    private bool trayAvailable = true;
    private bool positionQueued;
    private bool forcePositionQueued;
    private bool hasSessionPosition;
    private bool windowConfigured;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        ContentRoot.DataContext = viewModel;

        hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);

        appWindow.Closing += OnClosing;
        Activated += OnActivated;
        PanelContent.SizeChanged += (_, _) => QueuePositionIfVisible();
    }

    internal void ConfigureWindow()
    {
        appWindow.Title = "CodexQuotaTray";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderDragRegion);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    internal Func<Rectangle?> TrayRectangleProvider { get; set; } = static () => null;

    internal bool IsDesiredVisible => visibility.DesiredVisible;

    internal event EventHandler? PanelShown;

    internal event EventHandler? ExitRequested;

    internal void TogglePanel()
    {
        if (visibility.Toggle())
        {
            ShowPanelCore();
        }
        else
        {
            appWindow.Hide();
        }
    }

    internal void ShowPanel()
    {
        visibility.Show();
        ShowPanelCore();
    }

    internal void HidePanel()
    {
        visibility.Hide();
        appWindow.Hide();
    }

    internal void PrepareForExit()
    {
        exiting = true;
        visibility.Hide();
        backdrop.Dispose();
    }

    internal void SetTrayAvailable(bool available)
    {
        trayAvailable = available;
        TrayFailureBanner.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        if (!available)
        {
            ShowPanel();
        }
        else
        {
            QueuePositionIfVisible();
        }
    }

    private void ShowPanelCore()
    {
        Position();
        Activate();
        appWindow.Show();
        if (!windowConfigured)
        {
            ConfigureWindow();
            windowConfigured = true;
        }

        // The first measure can still see the hidden window's old ScrollViewer
        // viewport. Re-measure after the window is shown so the client height is
        // based on the actual footer boundary instead of that stale viewport.
        QueuePositionIfVisible(forceResize: true);
        PanelShown?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyTheme(ThemeMode mode)
    {
        ContentRoot.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (visibility.DesiredVisible)
        {
            _ = backdrop.Apply(this);
        }
    }

    private void QueuePositionIfVisible(bool forceResize = false)
    {
        if (!visibility.DesiredVisible)
        {
            return;
        }

        forcePositionQueued |= forceResize;
        if (positionQueued)
        {
            return;
        }

        positionQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            positionQueued = false;
            var shouldForceResize = forcePositionQueued;
            forcePositionQueued = false;
            if (visibility.DesiredVisible)
            {
                Position(shouldForceResize);
            }
        }))
        {
            positionQueued = false;
            forcePositionQueued = false;
        }
    }

    private void Position(bool forceResize = false)
    {
        var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1.0;
        PanelContent.InvalidateMeasure();
        PanelContent.Measure(new Windows.Foundation.Size(PanelWidthDips, double.PositiveInfinity));
        ContentRoot.UpdateLayout();
        var fallbackHeight = Math.Max(1, Math.Ceiling(PanelContent.DesiredSize.Height));
        var measuredHeight = MeasureVisibleContentHeight(fallbackHeight);
        if (hasSessionPosition)
        {
            placement.ResizeAndKeepPosition(appWindow, scale, measuredHeight, forceResize);
            return;
        }

        placement.ResizeAndPlaceInitial(appWindow, scale, measuredHeight, TrayRectangleProvider());
        hasSessionPosition = true;
    }

    private double MeasureVisibleContentHeight(double fallbackHeight)
    {
        if (FooterRow.ActualHeight <= 0)
        {
            return fallbackHeight;
        }

        try
        {
            var footerTop = FooterRow
                .TransformToVisual(PanelContent)
                .TransformPoint(new Windows.Foundation.Point(0, 0))
                .Y;
            var bottomGap = PanelContent.Padding.Top;
            try
            {
                bottomGap = Math.Max(
                    0,
                    RefreshButton
                        .TransformToVisual(PanelContent)
                        .TransformPoint(new Windows.Foundation.Point(0, 0))
                        .Y);
            }
            catch (InvalidOperationException)
            {
            }

            return PopupPlacement.NaturalContentHeight(
                footerTop,
                FooterRow.ActualHeight,
                bottomGap,
                fallbackHeight);
        }
        catch (InvalidOperationException)
        {
            return fallbackHeight;
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (visibility.DesiredVisible)
        {
            _ = backdrop.Apply(this);
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exiting)
        {
            return;
        }

        if (!trayAvailable)
        {
            args.Cancel = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        args.Cancel = true;
        HidePanel();
    }
}
