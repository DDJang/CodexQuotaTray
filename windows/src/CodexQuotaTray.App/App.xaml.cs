using CodexQuotaTray.App.Services;
using CodexQuotaTray.App.Views;
using CodexQuotaTray.Core;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;

namespace CodexQuotaTray.App;

public partial class App : Application
{
    private readonly CancellationTokenSource lifetime = new();
    private MainWindow? mainWindow;
    private TrayIconService? trayIcon;
    private AppInstance? currentInstance;
    private DispatcherQueue? uiDispatcher;
    private IAsyncDisposable? providerLifetime;
    private Task? initializationTask;
    private IQuotaRuntimeControl? runtime;
    private SettingsWindow? settingsWindow;
    private ISettingsPageActions? settingsPageActions;
    private HostEventService? hostEvents;
    private Microsoft.UI.Xaml.Controls.ContentDialog? aboutDialog;
    private TokenUsageSyncController? tokenUsageSync;
    private WindowsUpdateService? windowsUpdateService;
    private AppIdentity? applicationIdentity;
    private bool exiting;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var launchProfile = AppLaunchProfile.FromArguments(
            arguments,
            args.Arguments,
            AppBuildConfiguration.IsDevelopmentBuild);
        var showDemo = launchProfile.ShowDemo;
        var identity = AppIdentity.From(launchProfile.TrayIdentity);

        currentInstance = AppInstance.FindOrRegisterForKey(launchProfile.InstanceKey);
        if (!currentInstance.IsCurrent)
        {
            await currentInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        if (HasArgument(arguments, "--shutdown-existing"))
        {
            Exit();
            return;
        }

        currentInstance.Activated += OnInstanceActivated;
        applicationIdentity = identity;
        uiDispatcher = DispatcherQueue.GetForCurrentThread();
        var startupLaunch = arguments.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
        var explicitCodex = ReadOption(arguments, "--codex-bin");

        IUiStateProvider stateProvider;
        IDiagnosticTextProvider diagnostics;
        if (showDemo)
        {
            var demo = new DemoStateProvider();
            stateProvider = demo;
            diagnostics = demo;
            runtime = new DemoRuntimeControl();
            settingsActions = new DemoSettingsPlatformActions();
        }
        else
        {
            var paths = CreateDataPaths(identity);
            var jsonStore = new JsonFileStore();
            var persistence = new PreviewPersistence(jsonStore, paths);
            var notificationSink = new TrayNotificationSink(uiDispatcher);
            var liveRuntime = new QuotaRuntimeService(
                new CodexAppServerClientFactory(new CodexClientOptions(ExplicitCodexBinary: explicitCodex)),
                new SettingsService(jsonStore, paths),
                persistence,
                notificationSink);
            runtime = liveRuntime;
            stateProvider = liveRuntime;
            diagnostics = liveRuntime;
            providerLifetime = liveRuntime;

            liveRuntime.StateChanged += (_, state) =>
            {
                _ = uiDispatcher.TryEnqueue(() =>
                {
                    viewModelReference?.ApplySnapshot(state);
                    mainWindow?.ApplyTheme(liveRuntime.Settings.ThemeMode);
                    trayIcon?.UpdateTooltip(TrayTooltipFormatter.Create(identity.TrayIcon.Tooltip, state));
                });
            };
            tokenUsageSync = new TokenUsageSyncController(
                new TokenUsageSettingsService(jsonStore, paths),
                liveRuntime.GetLastSuccessfulLanQuotaSnapshot,
                identity.TokenSyncPort,
                identity.TokenSyncDisplayNameSuffix,
                identity.TokenSyncDnsSdInstancePrefix);
            if (launchProfile.TrayIdentity == TrayIdentityMode.Production
                && SemanticVersion.TryParse(ProductVersion.Current, out var currentVersion))
            {
                var updateCoordinator = new WindowsUpdateCoordinator(
                    new GitHubWindowsReleaseProvider(),
                    new FileWindowsUpdateStateStore(
                        jsonStore,
                        Path.Combine(paths.Root, "windows-update-state.json")),
                    currentVersion,
                    log: message => System.Diagnostics.Debug.WriteLine(message));
                windowsUpdateService = new WindowsUpdateService(
                    updateCoordinator,
                    new WindowsUpdateDownloader(Path.Combine(paths.Root, "updates")),
                    new WindowsUpdateInstaller(),
                    ExitApplication,
                    message => System.Diagnostics.Debug.WriteLine(message),
                    action =>
                    {
                        _ = uiDispatcher?.TryEnqueue(() => action());
                    });
            }
            settingsActions = new SettingsPlatformActions(
                paths,
                persistence,
                launchProfile.CanConfigureStartup,
                tokenUsageSync,
                () => liveRuntime.Settings.PhoneTokenSyncEnabled,
                identity.StartupValueName);
            pendingNotificationSink = notificationSink;
        }

        var runtimeStateEventsAuthoritative = !showDemo;
        var viewModel = new MainViewModel(
            stateProvider,
            new ExternalNavigation(),
            runtimeStateEventsAuthoritative);
        viewModelReference = viewModel;
        mainWindow = new MainWindow(viewModel, identity.DisplayName);
        mainWindow.Activated += (_, activation) =>
        {
            if (activation.WindowActivationState != WindowActivationState.Deactivated
                && windowsUpdateService is not null
                && initializationTask is not null)
            {
                _ = StartWindowsUpdateCheckAfterInitializationAsync(windowsUpdateService, lifetime.Token);
            }
        };
        mainWindow.ApplyTheme(runtime?.Settings.ThemeMode ?? ThemeMode.System);
        mainWindow.Activate();
        if (!showDemo)
        {
            mainWindow.HidePanel();
        }

        // Start the data task before optional shell integration so a tray initialization
        // failure can never strand the model in its initial connecting state.
        initializationTask = InitializeStateAsync(
            stateProvider,
            viewModel,
            uiDispatcher,
            runtimeStateEventsAuthoritative,
            lifetime.Token);
        if (tokenUsageSync is not null)
        {
            _ = StartTokenUsageSyncAfterInitializationAsync(tokenUsageSync, lifetime.Token);
        }

        var clipboard = new DiagnosticsClipboardService(new DelegateDiagnosticTextProvider(() => string.Join(
            Environment.NewLine,
            diagnostics.CreateDiagnosticText(),
            trayIcon?.CreateDiagnosticText() ?? "托盘注册状态: NotStarted")));
        settingsPageActions = new DelegateSettingsPageActions(
            cancellationToken => viewModel.RefreshCommand.ExecuteAsync(cancellationToken),
            () => viewModel.OpenUsageCommand.Execute(null),
            clipboard.Copy,
            ShowAbout);
        trayIcon = new TrayIconService(
            uiDispatcher,
            mainWindow.TogglePanel,
            mainWindow.ShowPanel,
            ShowSettings,
            () => RequestRuntimeRefresh(RefreshReason.Resume),
            ExitApplication,
            () => runtime?.Settings.ThemeMode ?? CodexQuotaTray.Core.Persistence.ThemeMode.System,
            identity.TrayIcon);
        trayIcon.RegistrationStateChanged += (_, state) =>
        {
            _ = uiDispatcher.TryEnqueue(() =>
            {
                if (state == CodexQuotaTray.Core.Models.TrayRegistrationState.Registered)
                {
                    mainWindow?.SetTrayAvailable(true);
                }
                else if (state == CodexQuotaTray.Core.Models.TrayRegistrationState.Failed)
                {
                    mainWindow?.SetTrayAvailable(false);
                }
            });
        };
        try
        {
            trayIcon.Start();
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Tray initialization failed: {error.GetType().Name}");
            mainWindow.SetTrayAvailable(false);
        }
        if (pendingNotificationSink is not null)
        {
            pendingNotificationSink.Tray = trayIcon;
        }
        mainWindow.TrayRectangleProvider = trayIcon.TryGetIconRect;
        mainWindow.PanelShown += (_, _) =>
        {
            if (runtime is not null)
            {
                _ = runtime.RequestAsync(RefreshReason.CardOpened, lifetime.Token);
            }
        };
        mainWindow.ExitRequested += (_, _) => ExitApplication();

        hostEvents = new HostEventService(() => RequestRuntimeRefresh(RefreshReason.NetworkRestored));
        hostEvents.Start();

        if (windowsUpdateService is not null)
        {
            windowsUpdateService.UpdateAvailable += OnWindowsUpdateAvailable;
            _ = StartWindowsUpdateCheckAfterInitializationAsync(windowsUpdateService, lifetime.Token);
        }

        if (showDemo)
        {
            _ = uiDispatcher.TryEnqueue(() =>
            {
                mainWindow?.ShowPanel();
            });
        }
        else if (startupLaunch)
        {
            _ = ShowAfterInitializationWhenRequestedAsync();
        }
        else
        {
            _ = uiDispatcher.TryEnqueue(() =>
            {
                mainWindow?.ShowPanel();
            });
        }

    }

    private MainViewModel? viewModelReference;
    private ISettingsPlatformActions? settingsActions;
    private TrayNotificationSink? pendingNotificationSink;

    private void OnWindowsUpdateAvailable(object? sender, WindowsUpdateRelease release)
    {
        _ = uiDispatcher?.TryEnqueue(() =>
        {
            try
            {
                trayIcon?.ShowWindowsUpdateAvailable(release);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Debug.WriteLine($"Windows update notification failed: {error.GetType().Name}");
            }
        });
    }

    private async Task StartWindowsUpdateCheckAfterInitializationAsync(
        WindowsUpdateService service,
        CancellationToken cancellationToken)
    {
        try
        {
            if (initializationTask is not null)
            {
                await initializationTask.ConfigureAwait(false);
            }

            _ = await service.CheckAsync(manual: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Windows automatic update check failed: {error.GetType().Name}");
        }
    }

    private static PreviewDataPaths CreateDataPaths(AppIdentity identity) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        identity.DataDirectoryName));

    private async Task ShowAfterInitializationWhenRequestedAsync()
    {
        if (initializationTask is not null)
        {
            await initializationTask;
        }

        if (runtime is { Settings.SilentStartup: false })
        {
            _ = uiDispatcher?.TryEnqueue(() => mainWindow?.ShowPanel());
        }
    }

    private async Task StartTokenUsageSyncAfterInitializationAsync(
        TokenUsageSyncController controller,
        CancellationToken cancellationToken)
    {
        try
        {
            if (initializationTask is not null)
            {
                await initializationTask.ConfigureAwait(false);
            }

            if (runtime?.Settings.PhoneTokenSyncEnabled == true)
            {
                await controller.SetEnabledAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RequestRuntimeRefresh(RefreshReason reason)
    {
        if (runtime is not null && (reason != RefreshReason.NetworkRestored || runtime.Settings.RefreshOnNetworkRestore))
        {
            _ = runtime.RequestAsync(reason, lifetime.Token);
        }
    }

    private void ShowSettings()
    {
        if (runtime is null || settingsActions is null || settingsPageActions is null)
        {
            return;
        }

        if (settingsWindow is null)
        {
            var settingsViewModel = new SettingsViewModel(runtime, settingsActions, settingsPageActions, windowsUpdateService);
            settingsViewModel.ThemeSaved += OnSettingsThemeSaved;
            settingsWindow = new SettingsWindow(
                settingsViewModel,
                applicationIdentity?.DisplayName ?? AppIdentity.Production.DisplayName);
        }

        settingsWindow.ApplyTheme(runtime.Settings.ThemeMode);
        settingsWindow.Activate();
        if (windowsUpdateService is not null)
        {
            _ = StartWindowsUpdateCheckAfterInitializationAsync(windowsUpdateService, lifetime.Token);
        }
    }

    private void OnSettingsThemeSaved(object? sender, ThemeMode mode)
    {
        _ = uiDispatcher?.TryEnqueue(() =>
        {
            mainWindow?.ApplyTheme(mode);
            settingsWindow?.ApplyTheme(mode);
        });
    }

    private void ShowAbout(object? host)
    {
        if (aboutDialog is not null || host is not Microsoft.UI.Xaml.FrameworkElement hostElement || hostElement.XamlRoot is null)
        {
            return;
        }

        _ = hostElement.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        var dialog = new ContentDialog
        {
            Title = $"关于 {applicationIdentity?.DisplayName ?? AppIdentity.Production.DisplayName}",
            Content = CreateAboutContent(applicationIdentity?.DisplayName ?? AppIdentity.Production.DisplayName),
            CloseButtonText = "关闭",
            XamlRoot = hostElement.XamlRoot,
        };
        aboutDialog = dialog;
        _ = ShowAboutAsync(dialog, hostElement);
    }

    private static StackPanel CreateAboutContent(string displayName)
    {
        var content = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
        var iconUri = File.Exists(iconPath)
            ? new Uri(iconPath, UriKind.Absolute)
            : new Uri("ms-appx:///Assets/AppIcon.png");
        content.Children.Add(new Image
        {
            Source = new BitmapImage(iconUri),
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform,
        });
        content.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = $"版本 {ProductVersion.Current}",
            Opacity = 0.78,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "只读额度桌面应用，用于查看额度窗口、重置时间和可用重置卡。不会消耗重置卡或执行账户写操作。",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        links.Children.Add(new HyperlinkButton
        {
            Content = "GitHub 项目主页",
            NavigateUri = new Uri("https://github.com/DDJang/CodexQuotaTray"),
        });
        links.Children.Add(new HyperlinkButton
        {
            Content = "开源许可证（MIT）",
            NavigateUri = new Uri("https://github.com/DDJang/CodexQuotaTray/blob/main/LICENSE"),
        });
        content.Children.Add(links);
        return content;
    }

    private async Task ShowAboutAsync(
        ContentDialog dialog,
        Microsoft.UI.Xaml.FrameworkElement hostElement)
    {
        try
        {
            await dialog.ShowAsync();
        }
        catch (InvalidOperationException error)
        {
            System.Diagnostics.Debug.WriteLine($"Could not show about dialog: {error.GetType().Name}");
        }
        finally
        {
            if (ReferenceEquals(aboutDialog, dialog))
            {
                aboutDialog = null;
            }

            _ = RestoreAboutFocusAsync(hostElement);
        }
    }

    private async Task RestoreAboutFocusAsync(Microsoft.UI.Xaml.FrameworkElement hostElement)
    {
        await Task.Delay(75);
        _ = uiDispatcher?.TryEnqueue(() =>
        {
            settingsWindow?.Activate();
            settingsWindow?.FocusAboutButton();
            if (hostElement.XamlRoot is not null)
            {
                _ = hostElement.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
            }
        });
    }

    private sealed class DelegateSettingsPageActions(
        Func<CancellationToken, Task> refreshQuota,
        Action openOfficialUsage,
        Action copyDiagnostics,
        Action<object?> showAbout) : ISettingsPageActions
    {
        public Task RefreshQuotaAsync(CancellationToken cancellationToken) => refreshQuota(cancellationToken);

        public void OpenOfficialUsage() => openOfficialUsage();

        public void CopyDiagnostics() => copyDiagnostics();

        public void ShowAbout(object? host) => showAbout(host);
    }

    private static string? ReadOption(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static async Task InitializeStateAsync(
        IUiStateProvider provider,
        MainViewModel viewModel,
        DispatcherQueue dispatcher,
        bool stateEventsAuthoritative,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            var snapshot = await provider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!stateEventsAuthoritative)
            {
                await EnqueueAsync(dispatcher, () => viewModel.ApplySnapshot(snapshot), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"WinUI live initialization failed: {error.GetType().Name}");
            _ = dispatcher.TryEnqueue(viewModel.ReportStartupFailure);
        }
    }

    private static Task EnqueueAsync(DispatcherQueue dispatcher, Action action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The WinUI dispatcher is unavailable."));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        if (ActivationContains(args, "--shutdown-existing"))
        {
            _ = uiDispatcher?.TryEnqueue(ExitApplication);
            return;
        }

        _ = uiDispatcher?.TryEnqueue(() => mainWindow?.ShowPanel());
    }

    private async void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        lifetime.Cancel();
        if (initializationTask is not null)
        {
            try
            {
                await initializationTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (providerLifetime is not null)
        {
            await providerLifetime.DisposeAsync();
            providerLifetime = null;
        }

        if (tokenUsageSync is not null)
        {
            await tokenUsageSync.DisposeAsync();
            tokenUsageSync = null;
        }

        if (windowsUpdateService is not null)
        {
            await windowsUpdateService.DisposeAsync();
            windowsUpdateService = null;
        }

        trayIcon?.Dispose();
        trayIcon = null;
        hostEvents?.Dispose();
        hostEvents = null;
        settingsWindow?.PrepareForExit();
        mainWindow?.PrepareForExit();
        mainWindow?.Close();
        settingsWindow?.Close();
        settingsWindow = null;
        currentInstance = null;
        lifetime.Dispose();
        Exit();
    }

    internal static bool HasArgument(IEnumerable<string> arguments, string expected) =>
        arguments.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));

    private static bool ActivationContains(AppActivationArguments activation, string expected)
    {
        if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            return launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }
}

internal sealed class DelegateDiagnosticTextProvider(Func<string> create) : IDiagnosticTextProvider
{
    public string CreateDiagnosticText() => create();
}
