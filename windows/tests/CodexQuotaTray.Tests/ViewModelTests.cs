using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.Updates;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class ViewModelTests
{
    [TestMethod]
    public void UnexpectedStartupFailure_AlwaysLeavesRefreshingState()
    {
        var viewModel = new MainViewModel(
            new StubProvider(new AppUiState(
                "Codex",
                null,
                "正在连接",
                StatusTone.Refreshing,
                [],
                new ResetCreditViewState(ResetCreditKind.Unavailable),
                IsRefreshing: true,
                IsPrototype: false)),
            new StubNavigation());
        viewModel.ReportStartupFailure();

        Assert.IsFalse(viewModel.IsRefreshing);
        Assert.AreEqual(StatusTone.Error, viewModel.StatusTone);
        StringAssert.Contains(viewModel.StatusText, "点击刷新重试");
    }

    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    [TestMethod]
    public async Task ViewModel_ProjectsVariableWindowCounts(int count)
    {
        var windows = Enumerable.Range(0, count)
            .Select(index => QuotaWindowView.Demo($"窗口 {index}", 80, "1小时后重置", "12:00"))
            .ToArray();
        var provider = new StubProvider(new AppUiState(
            "Codex",
            count == 0 ? null : "Plus",
            "● 静态测试",
            StatusTone.Success,
            windows,
            new ResetCreditViewState(ResetCreditKind.Unavailable)));
        var navigation = new StubNavigation();
        var viewModel = new MainViewModel(provider, navigation);

        await viewModel.InitializeAsync();

        Assert.AreEqual(count, viewModel.Windows.Count);
        Assert.AreEqual(count != 0, viewModel.HasPlanBadge);
        Assert.AreEqual(count != 0, viewModel.HasWindows);
    }

    [TestMethod]
    public async Task OpenUsageCommand_UsesInjectedBoundary()
    {
        var navigation = new StubNavigation();
        var viewModel = new MainViewModel(
            new StubProvider(new AppUiState(
                "Codex",
                null,
                "无数据",
                StatusTone.Neutral,
                [],
                new ResetCreditViewState(ResetCreditKind.Unavailable))),
            navigation);

        viewModel.OpenUsageCommand.Execute(null);

        Assert.IsTrue(navigation.WasOpened);
        await Task.CompletedTask;
    }

    [TestMethod]
    public void ApplySnapshot_SynchronizesAutomaticRefreshStateAndCommand()
    {
        var viewModel = new MainViewModel(
            new StubProvider(new AppUiState(
                "Codex",
                null,
                "● 已更新",
                StatusTone.Success,
                [],
                new ResetCreditViewState(ResetCreditKind.Unavailable))),
            new StubNavigation());
        var refreshing = new AppUiState(
            "Codex",
            "Plus",
            "正在获取额度…",
            StatusTone.Refreshing,
            [],
            new ResetCreditViewState(ResetCreditKind.Unavailable),
            IsRefreshing: true,
            IsPrototype: false);

        viewModel.ApplySnapshot(refreshing);

        Assert.IsTrue(viewModel.IsRefreshing);
        Assert.IsFalse(viewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual("正在获取额度…", viewModel.StatusText);

        viewModel.ApplySnapshot(refreshing with
        {
            StatusText = "更新于 14:30",
            StatusTone = StatusTone.Success,
            IsRefreshing = false,
        });

        Assert.IsFalse(viewModel.IsRefreshing);
        Assert.IsTrue(viewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual("更新于 14:30", viewModel.StatusText);
    }

    [TestMethod]
    public async Task SettingsPageCommands_UseInjectedExistingActions()
    {
        var runtime = new StubRuntimeControl();
        var platform = new StubSettingsPlatformActions();
        var actions = new StubSettingsPageActions();
        var viewModel = new SettingsViewModel(runtime, platform, actions);

        await viewModel.RefreshQuotaCommand.ExecuteAsync(null);
        viewModel.OpenOfficialUsageCommand.Execute(null);
        viewModel.CopyDiagnosticsCommand.Execute(null);

        Assert.AreEqual(1, actions.RefreshCount);
        Assert.IsTrue(actions.OpenedUsage);
        Assert.IsTrue(actions.CopiedDiagnostics);
    }

    [TestMethod]
    public void UpdateStatus_SuppressedAutomaticCheckIsNotUserVisible()
    {
        Assert.AreEqual(
            string.Empty,
            SettingsViewModel.FormatUpdateStatus(new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.Skipped,
                null,
                "自动检查已在 24 小时内执行过",
                DateTimeOffset.UtcNow)));
        Assert.AreEqual(
            "正在检查…",
            SettingsViewModel.FormatUpdateStatus(new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.Checking,
                null,
                null,
                null)));
        Assert.AreEqual(
            "检查更新失败",
            SettingsViewModel.FormatUpdateStatus(new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.Failed,
                null,
                "network",
                DateTimeOffset.UtcNow)));
    }

    [TestMethod]
    public void UpdateStatus_ReportsSuccessfulManualCheck()
    {
        Assert.AreEqual(
            string.Empty,
            SettingsViewModel.FormatUpdateStatus(new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.NotChecked,
                null,
                null,
                null)));
        Assert.AreEqual(
            "当前已是最新版本",
            SettingsViewModel.FormatUpdateStatus(new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.UpToDate,
                null,
                null,
                DateTimeOffset.UtcNow)));
        Assert.AreEqual(
            "正在检查…",
            SettingsViewModel.FormatUpdateStatus(new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.Checking,
                null,
                null,
                null)));
    }

    [TestMethod]
    public async Task DevelopmentUpdateCheckIsEnabledAndReportsWhyItCannotRun()
    {
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());
        WindowsUpdateCheckResult? completed = null;
        viewModel.UpdateCheckCompleted += (_, result) => completed = result;

        Assert.IsTrue(viewModel.CanCheckForWindowsUpdates);
        Assert.AreEqual(string.Empty, viewModel.UpdateStatusText);
        Assert.IsFalse(viewModel.HasUpdateStatusText);
        Assert.AreEqual("尚未检查", viewModel.UpdateLastCheckText);

        await viewModel.CheckForWindowsUpdatesCommand.ExecuteAsync(null);

        Assert.IsNotNull(completed);
        Assert.AreEqual(WindowsUpdateCheckStatus.Disabled, completed.Status);
        Assert.AreEqual("开发版本不检查正式更新", completed.ErrorMessage);
    }

    [TestMethod]
    public async Task ManualUpdateCheck_ShowsCheckingThenUpToDate()
    {
        var updates = new StubWindowsUpdateController
        {
            CheckGate = new TaskCompletionSource<WindowsUpdateCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions(),
            updates);

        var check = viewModel.CheckForWindowsUpdatesCommand.ExecuteAsync(null);
        Assert.AreEqual("正在检查…", viewModel.UpdateStatusText);
        updates.CheckGate.SetResult(new WindowsUpdateCheckResult(
            WindowsUpdateCheckStatus.UpToDate,
            null,
            null,
            DateTimeOffset.UtcNow));

        await check;
        Assert.AreEqual("当前已是最新版本", viewModel.UpdateStatusText);
    }

    [TestMethod]
    public void UpdateLastCheck_UsesMostRecentAttemptAfterFailure()
    {
        var updates = new StubWindowsUpdateController
        {
            LastSuccessfulCheckUtc = new DateTimeOffset(2026, 8, 12, 1, 0, 0, TimeSpan.Zero),
            LastAttemptUtc = new DateTimeOffset(2026, 8, 12, 2, 0, 0, TimeSpan.Zero),
        };
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions(),
            updates);

        Assert.AreEqual(
            updates.LastAttemptUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            viewModel.UpdateLastCheckText);
    }

    [TestMethod]
    public async Task WindowsUpdateProgress_IsProjectedAndInstallerFailureStaysInApp()
    {
        var updates = new StubWindowsUpdateController();
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions(),
            updates);

        Assert.IsFalse(viewModel.AutoLaunchInstallerAfterDownload);
        updates.Publish(new WindowsUpdateDownloadProgress(
            WindowsUpdateDownloadPhase.Downloading,
            17 * 1024 * 1024,
            25 * 1024 * 1024));

        Assert.AreEqual("正在下载更新 · 68%", viewModel.DownloadProgressText);
        Assert.AreEqual("17.0 MB / 25.0 MB", viewModel.DownloadProgressSizeText);
        Assert.IsFalse(viewModel.IsDownloadProgressIndeterminate);
        Assert.IsTrue(viewModel.HasDownloadProgress);

        updates.Publish(new WindowsUpdateDownloadProgress(
            WindowsUpdateDownloadPhase.Downloading,
            17 * 1024 * 1024,
            null));
        Assert.IsTrue(viewModel.IsDownloadProgressIndeterminate);
        Assert.AreEqual("已下载 17.0 MB", viewModel.DownloadProgressSizeText);

        updates.Publish(new WindowsUpdateDownloadProgress(
            WindowsUpdateDownloadPhase.Verifying,
            25 * 1024 * 1024,
            25 * 1024 * 1024));
        Assert.AreEqual("正在验证安装包…", viewModel.UpdateStatusText);
        Assert.IsTrue(viewModel.IsDownloadProgressIndeterminate);

        updates.InstallResult = false;
        Assert.IsFalse(await viewModel.InstallPreparedWindowsUpdateAsync(CancellationToken.None));
    }

    [TestMethod]
    public void SettingsChangesApplyImmediatelyAndReportTheme()
    {
        var runtime = new StubRuntimeControl();
        var viewModel = new SettingsViewModel(
            runtime,
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());
        CodexQuotaTray.Core.Persistence.ThemeMode? savedTheme = null;
        viewModel.ThemeSaved += (_, mode) => savedTheme = mode;

        viewModel.SelectedThemeMode = CodexQuotaTray.Core.Persistence.ThemeMode.Light;

        Assert.AreEqual(CodexQuotaTray.Core.Persistence.ThemeMode.Light, savedTheme);
        Assert.AreEqual(CodexQuotaTray.Core.Persistence.ThemeMode.Light, runtime.Settings.ThemeMode);
    }

    [TestMethod]
    public void PercentageDisplaySelectionMapsToExistingBooleanSetting()
    {
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());

        Assert.AreEqual("剩余百分比", viewModel.SelectedPercentageDisplayMode.DisplayName);

        viewModel.SelectedPercentageDisplayMode = viewModel.PercentageDisplayModes[1];

        Assert.IsFalse(viewModel.ShowRemainingPercent);
        Assert.AreEqual("使用百分比", viewModel.SelectedPercentageDisplayMode.DisplayName);
    }

    [TestMethod]
    public void SettingsViewModel_ExposesFixedRefreshModesAndPanelRefreshDefault()
    {
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());

        CollectionAssert.AreEqual(
            new[]
            {
                RefreshMode.Every5Minutes,
                RefreshMode.Every15Minutes,
                RefreshMode.Every30Minutes,
                RefreshMode.ManualOnly,
            },
            viewModel.RefreshModes.ToArray());
        Assert.IsFalse(viewModel.RefreshModes.Contains(RefreshMode.Auto));
        Assert.IsTrue(viewModel.RefreshOnPanelOpen);
        Assert.IsTrue(viewModel.TokenRefreshOnPanelOpen);
        Assert.IsTrue(viewModel.PersistTokenUsageCache);
        Assert.AreEqual(RefreshMode.Every15Minutes, viewModel.SelectedTokenRefreshMode);
    }

    [TestMethod]
    public void SettingsViewModel_LoadsDisabledTokenPanelRefresh()
    {
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(AppSettings.Defaults with
            {
                TokenRefreshOnPanelOpen = false,
                PersistTokenUsageCache = false,
            }),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());

        Assert.IsFalse(viewModel.TokenRefreshOnPanelOpen);
        Assert.IsFalse(viewModel.PersistTokenUsageCache);
    }

    [TestMethod]
    public void PreviewSettingsCannotConfigureProductionStartup()
    {
        var runtime = new StubRuntimeControl();
        var platform = new StubSettingsPlatformActions(canConfigureStartup: false);
        var viewModel = new SettingsViewModel(runtime, platform, new StubSettingsPageActions())
        {
            StartWithWindows = true,
        };

        Assert.IsFalse(viewModel.CanConfigureStartup);
        Assert.AreEqual("预览模式不可配置开机启动。", viewModel.StartupDescription);
        Assert.AreEqual(0, platform.SetStartupCount);
        Assert.IsFalse(runtime.Settings.StartWithWindows);
    }

    [TestMethod]
    public async Task RuntimeAuthoritativeProviderDoesNotReapplyReturnedSnapshot()
    {
        var returned = new AppUiState(
            "Codex",
            "Plus",
            "更新于 14:30",
            StatusTone.Success,
            [QuotaWindowView.Demo("5 小时额度", 80, "1小时后重置", "14:30")],
            new ResetCreditViewState(ResetCreditKind.Unavailable));
        var viewModel = new MainViewModel(
            new StubProvider(returned),
            new StubNavigation(),
            stateEventsAuthoritative: true);

        await viewModel.InitializeAsync();

        Assert.AreEqual("正在连接 Codex…", viewModel.StatusText);
        Assert.IsEmpty(viewModel.Windows);
    }

    private sealed class StubProvider(AppUiState state) : IUiStateProvider
    {
        public ValueTask<AppUiState> GetSnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(state);

        public ValueTask<AppUiState> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(state);
    }

    private sealed class StubNavigation : IExternalNavigation
    {
        public bool WasOpened { get; private set; }

        public void OpenOfficialUsage() => WasOpened = true;
    }

    private sealed class StubRuntimeControl(AppSettings? initialSettings = null) : IQuotaRuntimeControl
    {
        public AppSettings Settings { get; private set; } = initialSettings ?? AppSettings.Defaults;

        public event EventHandler<AppUiState>? StateChanged
        {
            add { }
            remove { }
        }

        public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class StubSettingsPlatformActions(bool canConfigureStartup = true) : ISettingsPlatformActions
    {
        public bool CanConfigureStartup { get; } = canConfigureStartup;

        public int SetStartupCount { get; private set; }

        public string TokenSyncStatusText => "已关闭";

        public string TokenSyncAddressText => string.Empty;

        public string TokenSyncDeviceNameText => string.Empty;

        public string? TokenSyncPairingInfo => null;

        public event EventHandler? TokenSyncChanged
        {
            add { }
            remove { }
        }

        public Task SetStartupAsync(bool enabled, CancellationToken cancellationToken)
        {
            SetStartupCount++;
            return Task.CompletedTask;
        }

        public void OpenDataDirectory()
        {
        }

        public Task ClearQuotaCacheAsync() => Task.CompletedTask;

        public Task ApplyTokenSyncEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public void CopyTokenSyncPairingInfo()
        {
        }

        public Task RegenerateTokenSyncSecretAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubSettingsPageActions : ISettingsPageActions
    {
        public int RefreshCount { get; private set; }

        public bool OpenedUsage { get; private set; }

        public bool CopiedDiagnostics { get; private set; }

        public Task RefreshQuotaAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public void OpenOfficialUsage() => OpenedUsage = true;

        public void CopyDiagnostics() => CopiedDiagnostics = true;
    }

    private sealed class StubWindowsUpdateController : IWindowsUpdateController
    {
        public bool IsProduction => true;

        public bool AutomaticChecksEnabled { get; private set; } = true;

        public bool UpdateRemindersEnabled { get; private set; } = true;

        public bool AutoLaunchInstallerAfterDownload { get; private set; }

        public DateTimeOffset? LastAttemptUtc { get; init; }

        public DateTimeOffset? LastSuccessfulCheckUtc { get; init; }

        public WindowsUpdateCheckResult CurrentResult { get; private set; } = WindowsUpdateCheckResult.NotChecked;

        public WindowsUpdateDownloadProgress DownloadProgress { get; private set; } = WindowsUpdateDownloadProgress.Idle;

        public bool InstallResult { get; set; } = true;

        public TaskCompletionSource<WindowsUpdateCheckResult>? CheckGate { get; init; }

        public event EventHandler? Changed;

        public event EventHandler<WindowsUpdateDownloadProgress>? DownloadProgressChanged;

        public Task SetAutomaticChecksEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            AutomaticChecksEnabled = enabled;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SetUpdateRemindersEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            UpdateRemindersEnabled = enabled;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task SetAutoLaunchInstallerAfterDownloadAsync(bool enabled, CancellationToken cancellationToken)
        {
            AutoLaunchInstallerAfterDownload = enabled;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public async Task<WindowsUpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken)
        {
            var result = CheckGate is null
                ? CurrentResult
                : await CheckGate.Task.WaitAsync(cancellationToken);
            CurrentResult = result;
            return result;
        }

        public Task<WindowsUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(WindowsUpdateDownloadResult.Failed("not used"));

        public Task<bool> InstallPreparedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(InstallResult);

        public void Publish(WindowsUpdateDownloadProgress progress)
        {
            DownloadProgress = progress;
            DownloadProgressChanged?.Invoke(this, progress);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
