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
    private readonly MainViewModel viewModel;
    private readonly WindowPlacementService placement = new();
    private readonly BackdropService backdrop = new();
    private readonly WindowVisibilityController visibility = new();
    private readonly AppWindow appWindow;
    private bool exiting;
    private bool trayAvailable = true;
    private bool positionQueued;
    private bool clampQueued;
    private bool hasSessionPosition;

    public MainWindow(MainViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        ContentRoot.DataContext = viewModel;

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Title = "CodexQuotaTray";
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderDragRegion);

        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        appWindow.Closing += OnClosing;
        appWindow.Changed += OnAppWindowChanged;
        Activated += OnActivated;
        PanelContent.SizeChanged += (_, _) => QueuePositionIfVisible();
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
        appWindow.IsShownInSwitchers = !available;
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
        _ = backdrop.Apply(this);
    }

    private void QueuePositionIfVisible()
    {
        if (!visibility.DesiredVisible || positionQueued)
        {
            return;
        }

        positionQueued = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            positionQueued = false;
            if (visibility.DesiredVisible)
            {
                Position();
            }
        });
    }

    private void Position()
    {
        var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1.0;
        var measureWidth = PanelContent.ActualWidth > 1 ? PanelContent.ActualWidth : 418;
        PanelContent.Measure(new Windows.Foundation.Size(measureWidth, double.PositiveInfinity));
        var measuredHeight = Math.Max(1, PanelContent.DesiredSize.Height + 2);
        if (hasSessionPosition)
        {
            placement.ResizeAndKeepPosition(appWindow, scale, measuredHeight);
            return;
        }

        placement.ResizeAndPlaceInitial(appWindow, scale, measuredHeight, TrayRectangleProvider());
        hasSessionPosition = true;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _ = backdrop.Apply(this);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange || !hasSessionPosition || clampQueued)
        {
            return;
        }

        clampQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                clampQueued = false;
                if (hasSessionPosition)
                {
                    var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1.0;
                    placement.ClampCurrentPosition(appWindow, scale);
                }
            }))
        {
            clampQueued = false;
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
