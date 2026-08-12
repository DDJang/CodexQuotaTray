using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core;
using BackdropKind = CodexQuotaTray.Core.Models.BackdropKind;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Updates;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    private readonly BackdropService backdrop = new();
    private readonly AppWindow appWindow;
    private bool exiting;

    public SettingsWindow(SettingsViewModel viewModel, string displayName)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        Title = $"{displayName} 设置";
        ApplyAboutIcon();
        AboutProductNameText.Text = displayName;
        AutomationProperties.SetName(AboutButton, $"关于 {displayName}");
        AboutVersionText.Text = $"版本 {ProductVersion.Current}";
        AboutButton.CommandParameter = AboutButton;
        SettingsRoot.DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TokenSyncChanged += OnTokenSyncChanged;
        viewModel.UpdateCheckCompleted += OnUpdateCheckCompleted;
        UpdateTokenSyncQrCode();
        SettingsRoot.SizeChanged += OnSettingsRootSizeChanged;
        SettingsRoot.Loaded += (_, _) =>
        {
            ApplyResponsiveLayout(SettingsRoot.ActualWidth);
            ApplyBackdrop();
        };
        var hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(id);
        appWindow.Title = Title;
        var scale = WindowPlacementService.GetRasterizationScale(hwnd);
        _ = WindowIconService.TrySetIcon(appWindow, SettingsRoot.ActualTheme == ElementTheme.Dark);
        SettingsRoot.ActualThemeChanged += (_, _) =>
        {
            ApplyTitleBarTheme(viewModel.SelectedThemeMode);
            ApplyAboutIcon();
            ApplyBackdrop();
            _ = WindowIconService.TrySetIcon(appWindow, SettingsRoot.ActualTheme == ElementTheme.Dark);
        };
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
        appWindow.Closing += OnClosing;

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

        ApplyAboutIcon();
        _ = WindowIconService.TrySetIcon(
            appWindow,
            mode == ThemeMode.Dark
                || mode == ThemeMode.System && SettingsRoot.ActualTheme == ElementTheme.Dark);
        ApplyTitleBarTheme(mode);
        ApplyBackdrop();
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ApplyAboutIcon();
            _ = WindowIconService.TrySetIcon(
                appWindow,
                mode == ThemeMode.Dark
                    || mode == ThemeMode.System && SettingsRoot.ActualTheme == ElementTheme.Dark);
            ApplyTitleBarTheme(mode);
            ApplyBackdrop();
        });
    }

    internal void PrepareForExit()
    {
        exiting = true;
        backdrop.Dispose();
    }

    internal void FocusAboutButton()
    {
        _ = AboutButton.Focus(FocusState.Programmatic);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _ = WindowIconService.TrySetIcon(appWindow, SettingsRoot.ActualTheme == ElementTheme.Dark);
        ApplyTitleBarTheme(viewModel.SelectedThemeMode);
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            ApplyBackdrop();
        }
    }

    private void ApplyBackdrop()
    {
        var kind = backdrop.Apply(this);
        SettingsFallbackSurface.Visibility = kind == BackdropKind.Opaque
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exiting)
        {
            return;
        }

        args.Cancel = true;
        appWindow.Hide();
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

    private async void OnUpdateCheckCompleted(object? sender, WindowsUpdateCheckResult result)
    {
        if (!viewModel.IsWindowsUpdateAvailable)
        {
            await ShowUpdateMessageAsync(
                "检查更新",
                result.ErrorMessage ?? "开发版本不检查正式更新",
                "关闭");
            return;
        }

        if (result.Status == WindowsUpdateCheckStatus.Available && result.Release is not null)
        {
            var release = result.Release;
            var notes = string.IsNullOrWhiteSpace(release.ReleaseNotes)
                ? "此 Release 没有提供说明。"
                : release.ReleaseNotes.Length > 12000
                    ? release.ReleaseNotes[..12000] + Environment.NewLine + "（说明已截断）"
                    : release.ReleaseNotes;
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock { Text = $"当前版本：{viewModel.CurrentVersionText}" });
            content.Children.Add(new TextBlock { Text = $"最新版本：{release.Version}" });
            content.Children.Add(new TextBlock { Text = "Release notes" });
            content.Children.Add(new ScrollViewer
            {
                Content = ReleaseNotesMarkdownRenderer.Create(notes),
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            var dialog = CreateUpdateDialog("发现 Windows 更新", content, "下载更新", "稍后");
            if (await TryShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            var download = await viewModel.DownloadWindowsUpdateAsync(CancellationToken.None);
            if (!download.Succeeded)
            {
                if (download.WasCancelled)
                {
                    return;
                }

                await ShowUpdateMessageAsync("更新下载失败", download.ErrorMessage ?? "无法下载更新安装包。", "关闭");
                return;
            }

            if (viewModel.AutoLaunchInstallerAfterDownload)
            {
                if (!await viewModel.InstallPreparedWindowsUpdateAsync(CancellationToken.None))
                {
                    await ShowUpdateMessageAsync("更新启动失败", "无法启动安装器，请稍后重试。", "关闭");
                }

                return;
            }

            var readyDialog = CreateUpdateDialog(
                "更新已准备好",
                new TextBlock { Text = "安装包已通过 SHA-256 校验，可以启动现有安装器完成升级。", TextWrapping = TextWrapping.Wrap },
                "立即安装",
                "稍后安装");
            if (await TryShowDialogAsync(readyDialog) == ContentDialogResult.Primary
                && !await viewModel.InstallPreparedWindowsUpdateAsync(CancellationToken.None))
            {
                await ShowUpdateMessageAsync("更新启动失败", "无法启动安装器，请稍后重试。", "关闭");
            }

            return;
        }

        if (result.Status is WindowsUpdateCheckStatus.Failed or WindowsUpdateCheckStatus.NoRelease)
        {
            await ShowUpdateMessageAsync(
                "检查更新失败",
                result.ErrorMessage ?? "无法读取有效的 Windows Release。",
                "关闭");
        }
    }

    private ContentDialog CreateUpdateDialog(
        string title,
        object content,
        string primaryButton,
        string closeButton) => new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButton,
            CloseButtonText = closeButton,
            RequestedTheme = SettingsRoot.ActualTheme,
            XamlRoot = SettingsRoot.XamlRoot,
        };

    private async Task<ContentDialogResult> TryShowDialogAsync(ContentDialog dialog)
    {
        if (dialog.XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        try
        {
            return await dialog.ShowAsync();
        }
        catch (InvalidOperationException)
        {
            return ContentDialogResult.None;
        }
    }

    private Task ShowUpdateMessageAsync(string title, string message, string closeButton) =>
        TryShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = closeButton,
            RequestedTheme = SettingsRoot.ActualTheme,
            XamlRoot = SettingsRoot.XamlRoot,
        });

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

    private void ApplyAboutIcon()
    {
        var icon = SettingsRoot.ActualTheme == ElementTheme.Dark
            ? "ms-appx:///Assets/AppIcon.png"
            : "ms-appx:///Assets/AppIconDark.png";
        AboutIconImage.Source = new BitmapImage(new Uri(icon));
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
