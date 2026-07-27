using System.Drawing;
using CodexQuotaTray.App.Services;
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
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        appWindow.Closing += OnClosing;
        Activated += OnActivated;
        ContentRoot.SizeChanged += (_, _) => PositionIfVisible();
    }

    internal Func<Rectangle?> TrayRectangleProvider { get; set; } = static () => null;

    internal bool IsDesiredVisible => visibility.DesiredVisible;

    internal event EventHandler? PanelShown;

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
    }

    private void ShowPanelCore()
    {
        Position();
        Activate();
        appWindow.Show();
        PanelShown?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyTheme(ThemeMode mode) => ContentRoot.RequestedTheme = mode switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void PositionIfVisible()
    {
        if (visibility.DesiredVisible)
        {
            Position();
        }
    }

    private void Position()
    {
        var scale = ContentRoot.XamlRoot?.RasterizationScale ?? 1.0;
        placement.ResizeAndPlace(appWindow, scale, viewModel.Windows.Count, TrayRectangleProvider());
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _ = backdrop.Apply(this);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exiting)
        {
            return;
        }

        args.Cancel = true;
        HidePanel();
    }
}
