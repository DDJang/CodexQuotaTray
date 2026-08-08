using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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

    public SettingsWindow(SettingsViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        AboutVersionText.Text = $"版本 {ProductVersion.Current}";
        AboutButton.CommandParameter = AboutButton;
        SettingsRoot.DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TokenSyncChanged += OnTokenSyncChanged;
        UpdateTokenSyncQrCode();
        SettingsRoot.SizeChanged += OnSettingsRootSizeChanged;
        SettingsRoot.Loaded += (_, _) => ApplyResponsiveLayout(SettingsRoot.ActualWidth);
        SettingsRoot.ActualThemeChanged += (_, _) => ApplyTitleBarTheme(viewModel.SelectedThemeMode);

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

        ApplyTitleBarTheme(mode);
        _ = DispatcherQueue.TryEnqueue(() => ApplyTitleBarTheme(mode));
    }

    internal void FocusAboutButton()
    {
        _ = AboutButton.Focus(FocusState.Programmatic);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _ = WindowIconService.TrySetIcon(appWindow);
        ApplyTitleBarTheme(viewModel.SelectedThemeMode);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.TokenSyncPairingInfo) or nameof(SettingsViewModel.PhoneTokenSyncEnabled))
        {
            _ = DispatcherQueue.TryEnqueue(UpdateTokenSyncQrCode);
        }
    }

    private void OnTokenSyncChanged(object? sender, EventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            viewModel.RefreshTokenSyncStatus();
            UpdateTokenSyncQrCode();
        });
    }

    private void UpdateTokenSyncQrCode()
    {
        var value = viewModel.PhoneTokenSyncEnabled ? viewModel.TokenSyncPairingInfo : null;
        if (string.IsNullOrWhiteSpace(value))
        {
            TokenSyncQrCodeImage.Source = null;
            TokenSyncQrPanel.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            TokenSyncQrCodeImage.Source = TokenUsageQrCodeGenerator.Create(value);
            TokenSyncQrPanel.Visibility = Visibility.Visible;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            TokenSyncQrCodeImage.Source = null;
            TokenSyncQrPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSettingsToggleButtonClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Content: ToggleSwitch toggleSwitch })
        {
            toggleSwitch.IsOn = !toggleSwitch.IsOn;
        }
    }

    private void OnSettingsRootSizeChanged(object sender, SizeChangedEventArgs args) =>
        ApplyResponsiveLayout(args.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        var state = width >= ResponsiveWideBreakpointDips ? "Wide" : "Narrow";
        _ = VisualStateManager.GoToState(SettingsScroller, state, false);
    }

    private void ApplyTitleBarTheme(ThemeMode mode)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var titleBar = appWindow.TitleBar;
        titleBar.ResetToDefault();
        titleBar.PreferredTheme = mode switch
        {
            ThemeMode.Light => TitleBarTheme.Light,
            ThemeMode.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private static int DipsToPixels(double dips, double scale) =>
        Math.Max(1, (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero));
}
