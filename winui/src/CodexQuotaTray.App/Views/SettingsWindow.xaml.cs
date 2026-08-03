using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using Windows.Graphics;

namespace CodexQuotaTray.App.Views;

public sealed partial class SettingsWindow : Window
{
    private const double DefaultWidthDips = 740;
    private const double DefaultHeightDips = 780;
    private const double MinimumWidthDips = 620;
    private const double MinimumHeightDips = 540;
    private const double ResponsiveWideBreakpointDips = 680;

    private readonly SettingsViewModel viewModel;
    private readonly AppWindow appWindow;
    private readonly UISettings uiSettings = new();

    public SettingsWindow(SettingsViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        AboutButton.CommandParameter = AboutButton;
        SettingsRoot.DataContext = viewModel;
        SettingsRoot.SizeChanged += OnSettingsRootSizeChanged;
        SettingsRoot.Loaded += (_, _) => ApplyResponsiveLayout(SettingsRoot.ActualWidth);
        SettingsRoot.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        uiSettings.ColorValuesChanged += (_, _) =>
        {
            if (new AccessibilitySettings().HighContrast || viewModel.SelectedThemeMode == ThemeMode.System)
            {
                _ = DispatcherQueue.TryEnqueue(ApplyTitleBarTheme);
            }
        };

        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(id);
        var scale = WindowPlacementService.GetRasterizationScale(hwnd);
        _ = WindowIconService.TrySetIcon(appWindow);
        Activated += OnActivated;

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = DipsToPixels(MinimumWidthDips, scale);
            presenter.PreferredMinimumHeight = DipsToPixels(MinimumHeightDips, scale);
        }

        appWindow.Resize(new SizeInt32(
            DipsToPixels(DefaultWidthDips, scale),
            DipsToPixels(DefaultHeightDips, scale)));
        appWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            appWindow.Hide();
        };

        ApplyTheme(viewModel.SelectedThemeMode);
    }

    internal void ApplyTheme(ThemeMode mode)
    {
        SettingsRoot.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        ApplyTitleBarTheme();
        _ = DispatcherQueue.TryEnqueue(ApplyTitleBarTheme);
    }

    internal void FocusAboutButton()
    {
        _ = AboutButton.Focus(FocusState.Programmatic);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _ = WindowIconService.TrySetIcon(appWindow);
        ApplyTitleBarTheme();
    }

    private void OnSettingsRootSizeChanged(object sender, SizeChangedEventArgs args) =>
        ApplyResponsiveLayout(args.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var state = width >= ResponsiveWideBreakpointDips ? "Wide" : "Narrow";
        _ = VisualStateManager.GoToState(SettingsScroller, state, false);
    }

    private void ApplyTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.BackgroundColor = BrushColor(TitleBarBackgroundResource.Background, Color.FromArgb(255, 234, 242, 252));
        titleBar.ForegroundColor = BrushColor(TitleBarForegroundResource.Background, Color.FromArgb(255, 23, 33, 43));
        titleBar.ButtonBackgroundColor = BrushColor(TitleBarButtonBackgroundResource.Background, Color.FromArgb(255, 234, 242, 252));
        titleBar.ButtonForegroundColor = BrushColor(TitleBarButtonForegroundResource.Background, Color.FromArgb(255, 23, 33, 43));
        titleBar.ButtonHoverBackgroundColor = BrushColor(TitleBarButtonHoverBackgroundResource.Background, Color.FromArgb(26, 8, 123, 232));
        titleBar.ButtonHoverForegroundColor = BrushColor(TitleBarButtonHoverForegroundResource.Background, Color.FromArgb(255, 23, 33, 43));
        titleBar.ButtonPressedBackgroundColor = BrushColor(TitleBarButtonPressedBackgroundResource.Background, Color.FromArgb(50, 8, 123, 232));
        titleBar.ButtonPressedForegroundColor = BrushColor(TitleBarButtonPressedForegroundResource.Background, Color.FromArgb(255, 23, 33, 43));
    }

    private static Color BrushColor(Brush? brush, Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private static int DipsToPixels(double dips, double scale) =>
        Math.Max(1, (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero));
}
