using System.Diagnostics;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.App.Views;
using CodexQuotaTray.Core;
using CodexQuotaTray.Core.Auth;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.Updates;
using CodexQuotaTray.Core.TokenUsage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CodexQuotaTray.App;

public partial class App : Application
{
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource lifetime = new();
    private MainWindow? mainWindow;
    private TrayIconService? trayIcon;
    private AppInstance? currentInstance;
    private DispatcherQueue? uiDispatcher;
    private IAsyncDisposable? providerLifetime;
    private Task? initializationTask;
    private Task<AppSettings>? tokenUsageSettingsTask;
    private Task<TokenUsageCacheSettingsState>? tokenUsageCacheSettingsStateTask;
    private Task? tokenUsageInitializationTask;
    private Task? tokenUsageRefreshTask;
    private IQuotaRuntimeControl? runtime;
    private SettingsWindow? settingsWindow;
    private ISettingsPageActions? settingsPageActions;
    private HostEventService? hostEvents;
    private TokenUsageSyncController? tokenUsageSync;
    private LanDiagnosticBuffer? lanDiagnostics;
    private WindowsAccountService? accountService;
    private TokenUsageSourceResolver? tokenUsageSourceResolver;
    private TokenUsageViewModel? tokenUsageViewModel;
    private WindowsUpdateService? windowsUpdateService;
    private WindowsAppNotificationService? appNotifications;
    private AppIdentity? applicationIdentity;
    private CrashSessionLog? crashSessionLog;
    private PreviousCrashInfo? previousCrashInfo;
    private int exitStarted;

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

        if (!showDemo)
        {
            appNotifications = new WindowsAppNotificationService(OnAppNotificationInvoked);
            var iconUri = new Uri(WindowIconService.AppNotificationIconPath);
            _ = appNotifications.TryRegister(identity.DisplayName, iconUri);
        }

        currentInstance.Activated += OnInstanceActivated;
        applicationIdentity = identity;
        uiDispatcher = DispatcherQueue.GetForCurrentThread();
        var paths = CreateDataPaths(identity);
        crashSessionLog = new CrashSessionLog(paths.Root);
        previousCrashInfo = crashSessionLog.StartSession();
        UnhandledException += OnApplicationUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        var startupLaunch = arguments.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
        var explicitCodex = ReadOption(arguments, "--codex-bin");
        var lanDiagnosticBuffer = new LanDiagnosticBuffer(paths.Root);
        lanDiagnostics = lanDiagnosticBuffer;
        var tokenUsageScanner = new TokenUsageScanner(paths.TokenUsageDatabase);

        IUiStateProvider stateProvider;
        IDiagnosticTextProvider diagnostics;
        PreviewPersistence? persistence = null;
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
            var jsonStore = new JsonFileStore();
            persistence = new PreviewPersistence(jsonStore, paths);
            var settingsService = new SettingsService(jsonStore, paths);
            tokenUsageSettingsTask = settingsService.LoadAsync(lifetime.Token);
            tokenUsageCacheSettingsStateTask = TokenUsageCacheSettingsState.CreateAsync(tokenUsageSettingsTask);
            var notificationSink = new TrayNotificationSink(
                uiDispatcher,
                appNotifications ?? throw new InvalidOperationException("App notifications are unavailable."));
            var cliFactory = new CodexAppServerClientFactory(new CodexClientOptions(ExplicitCodexBinary: explicitCodex));
            var oauthCredentials = new OAuthCredentialManager(
                new DpapiOAuthCredentialStore(paths.OAuthCredentials),
                new OAuthClient());
            var liveAccountService = new WindowsAccountService(cliFactory, oauthCredentials);
            accountService = liveAccountService;
            var liveRuntime = new QuotaRuntimeService(
                cliFactory,
                settingsService,
                persistence,
                notificationSink,
                clientFactoryResolver: source => source == QuotaDataSource.OAuth
                    ? liveAccountService.OAuthFactory
                    : cliFactory);
            runtime = new TokenUsageCacheRuntimeControl(liveRuntime, tokenUsageCacheSettingsStateTask);
            stateProvider = liveRuntime;
            diagnostics = liveRuntime;
            providerLifetime = liveRuntime;
            tokenUsageSourceResolver = new TokenUsageSourceResolver(
                () => runtime?.Settings.TokenUsageDataSource ?? TokenUsageDataSource.Local,
                tokenUsageScanner,
                liveAccountService.ReadCodexCliUsageAsync,
                liveAccountService.ReadOAuthUsageAsync);

            liveRuntime.StateChanged += (_, state) =>
            {
                _ = uiDispatcher.TryEnqueue(() =>
                {
                    viewModelReference?.ApplySnapshot(state);
                    mainWindow?.ApplyTheme(liveRuntime.Settings.ThemeMode);
                    trayIcon?.UpdateTooltip(TrayTooltipFormatter.Create(state));
                });
            };
            tokenUsageSync = new TokenUsageSyncController(
                new TokenUsageSettingsService(jsonStore, paths),
                cancellationToken => tokenUsageSourceResolver.ReadAsync(cancellationToken),
                liveRuntime.GetLastSuccessfulLanQuotaSnapshot,
                identity.TokenSyncPort,
                identity.TokenSyncDisplayNameSuffix,
                identity.TokenSyncDnsSdInstancePrefix,
                lanDiagnosticBuffer.Record,
                () => lanDiagnosticBuffer.Snapshot);
            if (launchProfile.TrayIdentity == TrayIdentityMode.Production
                && SemanticVersion.TryParse(ProductVersion.Current, out var currentVersion))
            {
                var updateCoordinator = new WindowsUpdateCoordinator(
                    new StaticUpdateManifestProvider(),
                    new FileWindowsUpdateStateStore(
                        jsonStore,
                        Path.Combine(paths.Root, "windows-update-state.json")),
                    currentVersion,
                    log: message => System.Diagnostics.Debug.WriteLine(message));
                windowsUpdateService = new WindowsUpdateService(
                    updateCoordinator,
                    new WindowsUpdateDownloader(Path.Combine(paths.Root, "updates")),
                    new WindowsUpdateInstaller(),
                    ExitForWindowsUpdate,
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
        var presentationDispatcher = uiDispatcher
            ?? throw new InvalidOperationException("The WinUI dispatcher is unavailable.");
        tokenUsageViewModel = new TokenUsageViewModel(
            cancellationToken => ScanTokenUsageAsync(tokenUsageScanner, persistence, cancellationToken),
            (action, cancellationToken) => EnqueueAsync(presentationDispatcher, action, cancellationToken));
        var tokenUsageViewModelLocal = tokenUsageViewModel;
        viewModelReference = viewModel;
        mainWindow = new MainWindow(viewModel, tokenUsageViewModelLocal, identity.DisplayName);
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
        // Start the data task before optional shell integration so a tray initialization
        // failure can never strand the model in its initial connecting state.
        initializationTask = InitializeStateAsync(
            stateProvider,
            viewModel,
            uiDispatcher,
            runtimeStateEventsAuthoritative,
            lifetime.Token);
        if (!showDemo)
        {
            tokenUsageInitializationTask = InitializeTokenUsageAsync(
                tokenUsageViewModelLocal,
                persistence!,
                tokenUsageSettingsTask!,
                lifetime.Token);
            tokenUsageRefreshTask = RunTokenUsageRefreshLoopAsync(tokenUsageViewModelLocal, lifetime.Token);
        }
        if (tokenUsageSync is not null)
        {
            _ = StartTokenUsageSyncAfterInitializationAsync(tokenUsageSync, lifetime.Token);
        }

        var clipboard = new DiagnosticsClipboardService(new DelegateDiagnosticTextProvider(() => string.Join(
            Environment.NewLine,
            diagnostics.CreateDiagnosticText(),
            lanDiagnosticBuffer.CreateDiagnosticText(),
            appNotifications?.CreateDiagnosticText() ?? "Windows notifications: unavailable (demo)",
            trayIcon?.CreateDiagnosticText() ?? "托盘注册状态: NotStarted")));
        settingsPageActions = new DelegateSettingsPageActions(
            cancellationToken => viewModel.RefreshCommand.ExecuteAsync(cancellationToken),
            () => viewModel.OpenUsageCommand.Execute(null),
            OpenWindowsUpdateBrowserAsync,
            clipboard.TryCopy);
        trayIcon = new TrayIconService(
            uiDispatcher,
            mainWindow.TogglePanel,
            mainWindow.ShowPanel,
            ShowSettings,
            () => RequestRuntimeRefresh(RefreshReason.Resume),
            () => crashSessionLog?.MarkExpectedTermination(),
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

            _ = RefreshTokenUsageOnPanelShownAsync(tokenUsageViewModelLocal);
        };
        if (previousCrashInfo is { } crashInfo)
        {
            EventHandler? showCrashInfo = null;
            var showingCrashInfo = false;
            showCrashInfo = async (_, _) =>
            {
                if (mainWindow is null || showingCrashInfo)
                {
                    return;
                }

                showingCrashInfo = true;
                try
                {
                    if (await mainWindow.ShowPreviousCrashNoticeAsync(crashInfo))
                    {
                        _ = crashSessionLog?.AcknowledgePreviousCrash(crashInfo);
                        mainWindow.PanelShown -= showCrashInfo;
                        previousCrashInfo = null;
                    }
                }
                finally
                {
                    showingCrashInfo = false;
                }
            };
            mainWindow.PanelShown += showCrashInfo;
        }
        mainWindow.ExitRequested += (_, _) => ExitApplication();

        hostEvents = new HostEventService(
            () => RequestRuntimeRefresh(RefreshReason.NetworkRestored),
            reason => tokenUsageSync?.OnNetworkChanged(reason));
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
                if (Volatile.Read(ref exitStarted) == 0)
                {
                    mainWindow?.ShowPanel();
                }
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
                if (Volatile.Read(ref exitStarted) == 0)
                {
                    mainWindow?.ShowPanel();
                }
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
            _ = DeliverWindowsUpdateNotificationAsync(release).ContinueWith(
                ObserveWindowsUpdateDelivery,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        });
    }

    private async Task DeliverWindowsUpdateNotificationAsync(WindowsUpdateRelease release)
    {
        var notifications = appNotifications;
        var deliveryAttempt = notifications?.BeginDelivery();

        await NotificationDeliveryRouter.DeliverAsync(
            notifications?.IsRegistered == true,
            () => (notifications
                ?? throw new InvalidOperationException("Windows app notifications are unavailable."))
                .ShowWindowsUpdateAvailable(release),
            _ =>
            {
                var tray = trayIcon
                    ?? throw new InvalidOperationException("The tray notification service is unavailable.");
                tray.ShowWindowsUpdateAvailable(release);
                return Task.CompletedTask;
            },
            () => notifications?.RecordAppNotificationDeliverySuccess(deliveryAttempt),
            error => notifications?.RecordAppNotificationDeliveryFailure(deliveryAttempt, error),
            () => notifications?.RecordSuppressedBySetting(deliveryAttempt),
            () => notifications?.RecordShellFallbackDeliverySuccess(deliveryAttempt),
            error => notifications?.RecordShellFallbackDeliveryFailure(deliveryAttempt, error),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static void ObserveWindowsUpdateDelivery(Task delivery)
    {
        if (!delivery.IsFaulted || delivery.Exception?.GetBaseException() is not { } error)
        {
            return;
        }

        System.Diagnostics.Debug.WriteLine(
            $"Windows update notification failed: {error.GetType().Name} "
            + $"hresult=0x{unchecked((uint)error.HResult):X8}");
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

        if (Volatile.Read(ref exitStarted) == 0
            && runtime is { Settings.SilentStartup: false })
        {
            _ = uiDispatcher?.TryEnqueue(() =>
            {
                if (Volatile.Read(ref exitStarted) == 0)
                {
                    mainWindow?.ShowPanel();
                }
            });
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
            var settingsViewModel = new SettingsViewModel(
                runtime,
                settingsActions,
                settingsPageActions,
                windowsUpdateService,
                accountService);
            settingsViewModel.ThemeSaved += OnSettingsThemeSaved;
            settingsViewModel.DataSourcesChanged += OnSettingsDataSourcesChanged;
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

    private void OnSettingsDataSourcesChanged(object? sender, DataSourcesChangedEventArgs args)
    {
        var viewModel = tokenUsageViewModel;
        var dispatcher = uiDispatcher;
        if (!args.TokenUsageDataSourceChanged || viewModel is null || dispatcher is null)
        {
            return;
        }

        _ = dispatcher.TryEnqueue(() =>
        {
            viewModel.ClearForSourceChange();
            _ = viewModel.RefreshAfterSourceChangeAsync(lifetime.Token);
        });
    }

    private void OnSettingsThemeSaved(object? sender, ThemeMode mode)
    {
        _ = uiDispatcher?.TryEnqueue(() =>
        {
            mainWindow?.ApplyTheme(mode);
            settingsWindow?.ApplyTheme(mode);
        });
    }

    private Task OpenWindowsUpdateBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = windowsUpdateService?.CurrentResult.Release?.Installer.Url;
        if (uri is null || !WindowsUpdateSecurity.IsAllowedAssetUri(uri))
        {
            throw new InvalidOperationException("更新文件来源不受信任。");
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }

    private sealed class DelegateSettingsPageActions(
        Func<CancellationToken, Task> refreshQuota,
        Action openOfficialUsage,
        Func<CancellationToken, Task> openWindowsUpdateBrowser,
        Func<bool> copyDiagnostics) : ISettingsPageActions
    {
        public Task RefreshQuotaAsync(CancellationToken cancellationToken) => refreshQuota(cancellationToken);

        public void OpenOfficialUsage() => openOfficialUsage();

        public Task OpenWindowsUpdateBrowserAsync(CancellationToken cancellationToken) =>
            openWindowsUpdateBrowser(cancellationToken);

        public bool CopyDiagnostics() => copyDiagnostics();
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
            ExitApplication();
            return;
        }

        if (Volatile.Read(ref exitStarted) != 0)
        {
            return;
        }

        _ = uiDispatcher?.TryEnqueue(() =>
        {
            if (Volatile.Read(ref exitStarted) == 0)
            {
                mainWindow?.ShowPanel();
            }
        });
    }

    private void OnAppNotificationInvoked()
    {
        if (Volatile.Read(ref exitStarted) != 0)
        {
            return;
        }

        _ = uiDispatcher?.TryEnqueue(() =>
        {
            if (Volatile.Read(ref exitStarted) == 0)
            {
                mainWindow?.ShowPanel();
            }
        });
    }

    private async Task<TokenUsageSnapshot> ScanTokenUsageAsync(
        TokenUsageScanner scanner,
        PreviewPersistence? persistence,
        CancellationToken cancellationToken)
    {
        var scanStopwatch = Stopwatch.StartNew();
        var snapshot = tokenUsageSourceResolver is null
            ? await Task.Run(
                () => scanner.ScanAsync(cancellationToken: cancellationToken),
                cancellationToken)
            : await tokenUsageSourceResolver.ReadAsync(cancellationToken);
        scanStopwatch.Stop();
        Debug.WriteLine(
            $"TokenUsage diagnostics: stage=scan files={snapshot.FilesScanned} "
            + $"scannerMs={snapshot.ScanElapsedMilliseconds} elapsedMs={scanStopwatch.ElapsedMilliseconds}");

        if (persistence is not null && tokenUsageCacheSettingsStateTask is not null)
        {
            var settingsState = await tokenUsageCacheSettingsStateTask.WaitAsync(cancellationToken);
            var cacheStopwatch = Stopwatch.StartNew();
            var saveAttempted = false;
            try
            {
                _ = await settingsState.PersistIfEnabledAsync(
                    async token =>
                    {
                        saveAttempted = true;
                        await persistence.SaveTokenUsageCacheAsync(snapshot, token);
                    },
                    cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Token usage cache save failed: {error.GetType().Name}");
            }
            finally
            {
                cacheStopwatch.Stop();
                if (saveAttempted)
                {
                    Debug.WriteLine(
                        $"TokenUsage diagnostics: stage=cache-save elapsedMs={cacheStopwatch.ElapsedMilliseconds}");
                }
            }
        }

        return snapshot;
    }

    private async Task InitializeTokenUsageAsync(
        TokenUsageViewModel tokenUsageViewModel,
        PreviewPersistence persistence,
        Task<AppSettings> settingsTask,
        CancellationToken cancellationToken)
    {
        var settings = await settingsTask;
        if (!settings.PersistTokenUsageCache)
        {
            return;
        }

        var snapshot = settings.TokenUsageDataSource == TokenUsageDataSource.Local
            ? await persistence.LoadTokenUsageCacheAsync(cancellationToken)
            : await persistence.LoadTokenUsageCacheAsync(cancellationToken, settings.TokenUsageDataSource);
        if (snapshot is not null && uiDispatcher is not null)
        {
            await EnqueueAsync(
                uiDispatcher,
                () => tokenUsageViewModel.RestoreSnapshot(snapshot),
                cancellationToken);
        }
    }

    private async Task RefreshTokenUsageOnPanelShownAsync(TokenUsageViewModel tokenUsageViewModel)
    {
        try
        {
            if (tokenUsageInitializationTask is not null)
            {
                await tokenUsageInitializationTask;
            }

            var now = DateTimeOffset.UtcNow;
            if (runtime is not null
                && TokenUsageRefreshPolicy.ShouldRefreshOnPanelOpen(
                    runtime.Settings.TokenRefreshOnPanelOpen,
                    tokenUsageViewModel.LastAttemptUtc,
                    now))
            {
                await tokenUsageViewModel.RefreshNowAsync(lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Panel-open Token refresh failed: {error.GetType().Name}");
        }
    }

    private async Task RunTokenUsageRefreshLoopAsync(
        TokenUsageViewModel tokenUsageViewModel,
        CancellationToken cancellationToken)
    {
        try
        {
            if (tokenUsageInitializationTask is not null)
            {
                await tokenUsageInitializationTask;
            }

            while (true)
            {
                var mode = runtime?.Settings.TokenRefreshMode ?? RefreshMode.ManualOnly;
                if (TokenUsageRefreshPolicy.IsDue(mode, tokenUsageViewModel.LastAttemptUtc, DateTimeOffset.UtcNow))
                {
                    await tokenUsageViewModel.RefreshNowAsync(cancellationToken);
                }

                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnApplicationUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) =>
        crashSessionLog?.Record(args.Exception, "WinUI.UnhandledException");

    private void OnDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception error)
        {
            crashSessionLog?.Record(error, "AppDomain.UnhandledException");
        }
    }

    private void ExitForWindowsUpdate() =>
        SessionEndingPolicy.ExitForWindowsUpdate(crashSessionLog, StartExit);

    private void ExitApplication() => StartExit();

    private void StartExit()
    {
        if (Interlocked.Exchange(ref exitStarted, 1) != 0)
        {
            return;
        }

        // Cleanup is best-effort, but an updater must never be left waiting on a
        // background component that does not observe cancellation promptly.
        crashSessionLog?.MarkExpectedTermination();
        _ = ForceExitAfterGracePeriodAsync();

        var dispatcher = uiDispatcher;
        if (dispatcher is null)
        {
            FallbackExitWithoutUiDispatcher();
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            _ = CompleteExitAsync();
            return;
        }

        if (!dispatcher.TryEnqueue(() => _ = CompleteExitAsync()))
        {
            FallbackExitWithoutUiDispatcher();
        }
    }

    private static async Task ForceExitAfterGracePeriodAsync()
    {
        await Task.Delay(ExitGracePeriod).ConfigureAwait(false);
        Environment.Exit(0);
    }

    private void FallbackExitWithoutUiDispatcher()
    {
        try
        {
            DisposeAppNotifications();
            crashSessionLog?.CompleteSession();
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine($"Crash session completion failed during fallback exit: {error.GetType().Name}");
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private async Task CompleteExitAsync()
    {
        lifetime.Cancel();
        if (initializationTask is not null)
        {
            try
            {
                await initializationTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                Debug.WriteLine("WinUI initialization did not finish during shutdown.");
            }
        }

        if (tokenUsageRefreshTask is not null)
        {
            await tokenUsageRefreshTask;
            tokenUsageRefreshTask = null;
        }

        if (providerLifetime is not null)
        {
            await providerLifetime.DisposeAsync();
            providerLifetime = null;
        }

        if (accountService is not null)
        {
            await accountService.DisposeAsync();
            accountService = null;
        }

        if (tokenUsageSync is not null)
        {
            await tokenUsageSync.DisposeAsync();
            tokenUsageSync = null;
        }

        if (lanDiagnostics is not null)
        {
            await lanDiagnostics.DisposeAsync();
            lanDiagnostics = null;
        }

        if (windowsUpdateService is not null)
        {
            await windowsUpdateService.DisposeAsync();
            windowsUpdateService = null;
        }

        trayIcon?.Dispose();
        trayIcon = null;
        DisposeAppNotifications();
        hostEvents?.Dispose();
        hostEvents = null;
        settingsWindow?.PrepareForExit();
        mainWindow?.PrepareForExit();
        mainWindow?.Close();
        settingsWindow?.Close();
        settingsWindow = null;
        currentInstance = null;
        lifetime.Dispose();
        crashSessionLog?.CompleteSession();
        Exit();
    }

    private void DisposeAppNotifications()
    {
        appNotifications?.Dispose();
        appNotifications = null;
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
