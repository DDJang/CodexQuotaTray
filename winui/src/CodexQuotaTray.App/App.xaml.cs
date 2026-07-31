using CodexQuotaTray.App.Services;
using CodexQuotaTray.App.Views;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace CodexQuotaTray.App;

public partial class App : Application
{
    private const string InstanceKey = "CodexQuotaTray";
    private readonly CancellationTokenSource lifetime = new();
    private MainWindow? mainWindow;
    private TrayIconService? trayIcon;
    private AppInstance? currentInstance;
    private DispatcherQueue? uiDispatcher;
    private IAsyncDisposable? providerLifetime;
    private Task? initializationTask;
    private QuotaRuntimeService? runtime;
    private SettingsWindow? settingsWindow;
    private HostEventService? hostEvents;
    private bool exiting;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        currentInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!currentInstance.IsCurrent)
        {
            await currentInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        var arguments = Environment.GetCommandLineArgs();
        if (HasArgument(arguments, "--shutdown-existing"))
        {
            Exit();
            return;
        }

        currentInstance.Activated += OnInstanceActivated;
        uiDispatcher = DispatcherQueue.GetForCurrentThread();
        var launchArguments = args.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var showDemo = launchArguments.Any(IsDemo) || arguments.Any(IsDemo);
        var startupLaunch = arguments.Any(value => string.Equals(value, "--startup", StringComparison.OrdinalIgnoreCase));
        var explicitCodex = ReadOption(arguments, "--codex-bin");

        IUiStateProvider stateProvider;
        IDiagnosticTextProvider diagnostics;
        if (showDemo)
        {
            var demo = new DemoStateProvider();
            stateProvider = demo;
            diagnostics = demo;
        }
        else
        {
            var paths = CreateDataPaths(arguments);
            var jsonStore = new JsonFileStore();
            var persistence = new PreviewPersistence(jsonStore, paths);
            var notificationSink = new TrayNotificationSink(uiDispatcher);
            runtime = new QuotaRuntimeService(
                new CodexAppServerClientFactory(new CodexClientOptions(ExplicitCodexBinary: explicitCodex)),
                new SettingsService(jsonStore, paths),
                persistence,
                notificationSink);
            stateProvider = runtime;
            diagnostics = runtime;
            providerLifetime = runtime;

            runtime.StateChanged += (_, state) =>
            {
                _ = uiDispatcher.TryEnqueue(() =>
                {
                    viewModelReference?.ApplySnapshot(state);
                    mainWindow?.ApplyTheme(runtime.Settings.ThemeMode);
                    trayIcon?.UpdateTooltip(CreateTooltip(state));
                });
            };
            settingsActions = new SettingsPlatformActions(paths, persistence, new ProductionDataImporter(jsonStore));
            pendingNotificationSink = notificationSink;
        }

        var viewModel = new MainViewModel(stateProvider, new ExternalNavigation());
        viewModelReference = viewModel;
        mainWindow = new MainWindow(viewModel);
        mainWindow.Activate();
        if (!showDemo)
        {
            mainWindow.HidePanel();
        }

        // Start the data task before optional shell integration so a tray initialization
        // failure can never strand the model in its initial connecting state.
        initializationTask = InitializeStateAsync(stateProvider, viewModel, uiDispatcher, lifetime.Token);

        var clipboard = new DiagnosticsClipboardService(new DelegateDiagnosticTextProvider(() => string.Join(
            Environment.NewLine,
            diagnostics.CreateDiagnosticText(),
            trayIcon?.CreateDiagnosticText() ?? "托盘注册状态: NotStarted")));
        var summaryClipboard = new DiagnosticsClipboardService(new DelegateDiagnosticTextProvider(viewModel.CreateQuotaSummary));
        trayIcon = new TrayIconService(
            uiDispatcher,
            mainWindow.TogglePanel,
            mainWindow.ShowPanel,
            () => _ = viewModel.RefreshCommand.ExecuteAsync(null),
            () => viewModel.OpenUsageCommand.Execute(null),
            summaryClipboard.Copy,
            clipboard.Copy,
            ShowSettings,
            ShowAbout,
            () => RequestRuntimeRefresh(RefreshReason.Resume),
            () => runtime?.Settings,
            mode => UpdateRuntimeSettings(settings => settings with { RefreshMode = mode }),
            ToggleAlert,
            ToggleStartup,
            ExitApplication);
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

        if (showDemo)
        {
            mainWindow.ShowPanel();
        }
        else if (startupLaunch)
        {
            _ = ShowAfterInitializationWhenRequestedAsync();
        }
        else
        {
            mainWindow.ShowPanel();
        }

    }

    private MainViewModel? viewModelReference;
    private SettingsPlatformActions? settingsActions;
    private TrayNotificationSink? pendingNotificationSink;

    private static PreviewDataPaths CreateDataPaths(string[] arguments)
    {
        if (!HasArgument(arguments, "--isolated-preview-data"))
        {
            return new PreviewDataPaths(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodexQuotaTray"));
        }

        return new PreviewDataPaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexQuotaTray-WinUI-Preview"));
    }

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

    private void RequestRuntimeRefresh(RefreshReason reason)
    {
        if (runtime is not null && (reason != RefreshReason.NetworkRestored || runtime.Settings.RefreshOnNetworkRestore))
        {
            _ = runtime.RequestAsync(reason, lifetime.Token);
        }
    }

    private void ShowSettings()
    {
        if (runtime is null || settingsActions is null)
        {
            return;
        }

        settingsWindow ??= new SettingsWindow(new SettingsViewModel(runtime, settingsActions));
        settingsWindow.Activate();
    }

    private void ShowAbout()
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "CodexQuotaTray WinUI",
            Content = "0.4.3\n只读额度桌面应用。不会消耗重置卡或执行账户写操作。",
            CloseButtonText = "关闭",
            XamlRoot = mainWindow?.Content.XamlRoot,
        };
        _ = dialog.ShowAsync();
    }

    private async void UpdateRuntimeSettings(Func<AppSettings, AppSettings> update)
    {
        if (runtime is null)
        {
            return;
        }

        try
        {
            await runtime.ApplySettingsAsync(update(runtime.Settings), lifetime.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update settings: {error.GetType().Name}");
        }
    }

    private void ToggleAlert(int threshold) => UpdateRuntimeSettings(settings =>
    {
        var current = settings.EffectiveNotifications;
        var changed = threshold switch
        {
            50 => current with { Remaining50 = !current.Remaining50 },
            20 => current with { Remaining20 = !current.Remaining20 },
            _ => current with { Remaining10 = !current.Remaining10 },
        };
        return settings with { Notifications = changed };
    });

    private async void ToggleStartup()
    {
        if (runtime is null || settingsActions is null)
        {
            return;
        }

        var enabled = !runtime.Settings.StartWithWindows;
        try
        {
            await settingsActions.SetStartupAsync(enabled, lifetime.Token);
            await runtime.ApplySettingsAsync(runtime.Settings with { StartWithWindows = enabled }, lifetime.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update startup: {error.GetType().Name}");
        }
    }

    private static string CreateTooltip(CodexQuotaTray.Core.Models.AppUiState state)
    {
        var quotas = string.Join(" · ", state.Windows.Take(2).Select(window => $"{window.Name} {window.RemainingPercent}%"));
        return string.IsNullOrWhiteSpace(quotas)
            ? $"CodexQuotaTray · {state.StatusText}"
            : $"CodexQuotaTray · {quotas} · {state.StatusText}";
    }

    private static bool IsDemo(string argument) =>
        string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase);

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
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            var snapshot = await provider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await EnqueueAsync(dispatcher, () => viewModel.ApplySnapshot(snapshot), cancellationToken).ConfigureAwait(false);
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

        trayIcon?.Dispose();
        trayIcon = null;
        hostEvents?.Dispose();
        hostEvents = null;
        mainWindow?.PrepareForExit();
        mainWindow?.Close();
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
