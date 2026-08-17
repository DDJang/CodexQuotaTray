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
    private const double DefaultHeightDips = 580;
    private const double MinimumWidthDips = 480;
    private const double MinimumHeightDips = 420;
    private readonly SettingsViewModel viewModel;
    private readonly BackdropService backdrop = new();
    private readonly AppWindow appWindow;
    private SettingsContentPage? currentSettingsPage;
    private bool showingSettingsHome;
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
        AboutHomeDescriptionText.Text = $"{displayName} · 版本 {ProductVersion.Current}";
        SettingsRoot.DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TokenSyncChanged += OnTokenSyncChanged;
        viewModel.UpdateCheckCompleted += OnUpdateCheckCompleted;
        InitializeSettingsNavigation();
        UpdateTokenSyncQrCode();
        SettingsRoot.Loaded += (_, _) =>
        {
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
            presenter.IsMaximizable = true;
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
        var kind = backdrop.ApplyForSettings(this);
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
        ShowSettingsHome();
        appWindow.Hide();
    }

    private void OnSettingsCategoryClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string category })
        {
            return;
        }

        var (title, panel) = category switch
        {
            "General" => ("常规", GeneralSettingsPanel),
            "Sync" => ("刷新与同步", SyncSettingsPanel),
            "Appearance" => ("个性化", AppearanceSettingsPanel),
            "Alerts" => ("额度提醒", AlertSettingsPanel),
            "Updates" => ("更新", UpdateSettingsPanel),
            "Advanced" => ("数据与高级选项", AdvancedSettingsPanel),
            "About" => ("关于", AboutSettingsPanel),
            _ => (string.Empty, null),
        };
        if (panel is null)
        {
            return;
        }

        ShowSettingsPage(new SettingsPageNavigation(panel, viewModel, title, ShowSettingsHome));
    }

    private void ShowSettingsHome()
    {
        if (!showingSettingsHome)
        {
            NavigateToSettingsHome(animate: true);
        }
    }

    private FrameworkElement[] SettingsSections() =>
    [
        GeneralSettingsPanel,
        SyncSettingsPanel,
        AppearanceSettingsPanel,
        AlertSettingsPanel,
        UpdateSettingsPanel,
        AdvancedSettingsPanel,
        AboutSettingsPanel,
    ];

    private void InitializeSettingsNavigation()
    {
        _ = SettingsContent.Children.Remove(SettingsHomePanel);
        foreach (var section in SettingsSections())
        {
            _ = SettingsContent.Children.Remove(section);
            section.Visibility = Visibility.Visible;
        }

        NavigateToSettingsHome(animate: false);
    }

    private void NavigateToSettingsHome(bool animate) =>
        ShowSettingsPage(
            new SettingsPageNavigation(SettingsHomePanel, viewModel),
            isHome: true,
            fromHorizontalOffset: animate ? -48 : null);

    private void ShowSettingsPage(
        SettingsPageNavigation navigation,
        bool isHome = false,
        double? fromHorizontalOffset = 48)
    {
        currentSettingsPage?.DetachPageContent();
        currentSettingsPage = new SettingsContentPage(navigation, fromHorizontalOffset);
        showingSettingsHome = isHome;
        SettingsPresenter.Content = currentSettingsPage;
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

            var dialog = CreateUpdateDialog("发现 Windows 更新", content, "下载并安装", "稍后", "浏览器下载");
            var dialogResult = await TryShowDialogAsync(dialog);
            if (dialogResult == ContentDialogResult.Secondary)
            {
                await viewModel.OpenWindowsUpdateInBrowserAsync(CancellationToken.None);
                return;
            }

            if (dialogResult != ContentDialogResult.Primary)
            {
                return;
            }

            await DownloadAndInstallWindowsUpdateAsync();
            return;
        }

        if (result.Status is WindowsUpdateCheckStatus.Failed or WindowsUpdateCheckStatus.NoRelease)
        {
            await ShowUpdateMessageAsync(
                "检查更新失败",
                result.ErrorMessage ?? "无法读取有效的 Windows 更新清单。",
                "关闭");
        }
    }

    private async void OnDownloadWindowsUpdateRequested(object sender, RoutedEventArgs args) =>
        await DownloadAndInstallWindowsUpdateAsync();

    private async Task DownloadAndInstallWindowsUpdateAsync()
    {
        var download = await viewModel.DownloadWindowsUpdateAsync(CancellationToken.None);
        while (!download.Succeeded)
        {
            if (download.WasCancelled)
            {
                return;
            }

            var failureContent = new TextBlock
            {
                Text = download.ErrorMessage ?? "无法下载更新安装包。",
                TextWrapping = TextWrapping.Wrap,
            };
            var failureDialog = CreateUpdateDialog(
                "更新下载失败",
                failureContent,
                "重试",
                "关闭",
                "浏览器下载");
            var failureResult = await TryShowDialogAsync(failureDialog);
            if (failureResult == ContentDialogResult.Secondary)
            {
                await viewModel.OpenWindowsUpdateInBrowserAsync(CancellationToken.None);
                return;
            }

            if (failureResult != ContentDialogResult.Primary)
            {
                return;
            }

            download = await viewModel.DownloadWindowsUpdateAsync(CancellationToken.None);
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
    }

    private async void OnRegenerateTokenSyncSecretRequested(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            Title = "重新生成配对密钥？",
            Content = "重新生成后，当前已配对的手机将无法继续连接，需要在手机端重新扫码配对。",
            PrimaryButtonText = "重新生成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = SettingsRoot.ActualTheme,
            XamlRoot = SettingsRoot.XamlRoot,
        };

        if (await TryShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            await viewModel.RegenerateTokenSyncSecretCommand.ExecuteAsync(null);
        }
    }

    private ContentDialog CreateUpdateDialog(
        string title,
        object content,
        string primaryButton,
        string closeButton,
        string? secondaryButton = null) => new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButton,
            CloseButtonText = closeButton,
            SecondaryButtonText = secondaryButton,
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

    private void ApplyAboutIcon()
    {
        var icon = SettingsRoot.ActualTheme == ElementTheme.Dark
            ? "ms-appx:///Assets/AppIcon.png"
            : "ms-appx:///Assets/AppIconDark.png";
        AboutIconImage.Source = new BitmapImage(new Uri(icon));
        AboutDetailIconImage.Source = new BitmapImage(new Uri(icon));
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
