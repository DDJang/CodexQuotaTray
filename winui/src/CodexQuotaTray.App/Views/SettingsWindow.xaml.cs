using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.Graphics;

namespace CodexQuotaTray.App.Views;

public sealed partial class SettingsWindow : Window
{
    private const double DefaultWidthDips = 740;
    private const double DefaultHeightDips = 780;
    private const double MinimumWidthDips = 620;
    private const double MinimumHeightDips = 540;
    private const double DataOperationsWideBreakpointDips = 680;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        AboutButton.CommandParameter = AboutButton;
        SettingsRoot.DataContext = viewModel;
        SettingsRoot.SizeChanged += OnSettingsRootSizeChanged;
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(id);
        var scale = GetRasterizationScale(hwnd);
        _ = WindowIconService.TrySetIcon(appWindow);
        Activated += (_, _) => _ = WindowIconService.TrySetIcon(appWindow);

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
    }

    private void OnSettingsRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        var isWide = args.NewSize.Width >= DataOperationsWideBreakpointDips;
        DataOperationsPanel.Orientation = isWide
            ? Orientation.Horizontal
            : Orientation.Vertical;
        var state = isWide ? "Wide" : "Narrow";
        _ = VisualStateManager.GoToState(SettingsScroller, state, false);
    }

    internal void FocusAboutButton()
    {
        _ = AboutButton.Focus(FocusState.Programmatic);
    }

    private static double GetRasterizationScale(IntPtr hwnd) =>
        Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0);

    private static int DipsToPixels(double dips, double scale) =>
        Math.Max(1, (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero));
}
