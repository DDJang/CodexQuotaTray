using System.ComponentModel;
using System.Net;
using CodexQuotaTray.Core.Auth;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
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
    public async Task ApplySnapshot_SynchronizesAutomaticRefreshStateAndCommand()
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
            "正在刷新…",
            StatusTone.Refreshing,
            [],
            new ResetCreditViewState(ResetCreditKind.Unavailable),
            IsRefreshing: true,
            IsPrototype: false);

        viewModel.ApplySnapshot(refreshing);

        Assert.IsTrue(viewModel.IsRefreshing);
        Assert.IsTrue(viewModel.ShowLoading);
        Assert.IsFalse(viewModel.ShowContent);
        Assert.IsFalse(viewModel.HasWindows);
        Assert.IsFalse(viewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual("正在刷新…", viewModel.StatusText);

        var presentationCompleted = WaitForPropertyConditionAsync(
            viewModel,
            nameof(MainViewModel.IsRefreshing),
            () => !viewModel.IsRefreshing);
        viewModel.ApplySnapshot(refreshing with
        {
            StatusText = "更新于 14:30",
            StatusTone = StatusTone.Success,
            Windows = [QuotaWindowView.Demo("5 小时额度", 80, "1小时后重置", "14:30")],
            IsRefreshing = false,
        });

        Assert.IsTrue(viewModel.IsRefreshing);
        Assert.IsTrue(viewModel.ShowLoading);
        Assert.IsFalse(viewModel.ShowContent);
        Assert.IsFalse(viewModel.HasWindows);
        Assert.IsFalse(viewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual("正在刷新…", viewModel.StatusText);

        await presentationCompleted;

        Assert.IsFalse(viewModel.IsRefreshing);
        Assert.IsFalse(viewModel.ShowLoading);
        Assert.IsTrue(viewModel.ShowContent);
        Assert.IsTrue(viewModel.HasWindows);
        Assert.IsTrue(viewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual("更新于 14:30", viewModel.StatusText);
    }

    [TestMethod]
    public async Task EmptySourceRefreshRestartsMinimumLoadingPresentation()
    {
        var existingWindow = QuotaWindowView.Demo("5 小时额度", 80, "1小时后重置", "14:30");
        var replacementWindow = QuotaWindowView.Demo("7 天额度", 60, "2天后重置", "周二 14:30");
        var secondaryWindow = QuotaWindowView.Demo("5 小时额度", 40, "3小时后重置", "17:30");
        var viewModel = new MainViewModel(
            new StubProvider(new AppUiState(
                "Codex",
                "Plus",
                "更新于 14:30",
                StatusTone.Success,
                [existingWindow],
                new ResetCreditViewState(ResetCreditKind.Unavailable))),
            new StubNavigation());
        viewModel.ApplySnapshot(new AppUiState(
            "Codex",
            "Plus",
            "正在刷新…",
            StatusTone.Refreshing,
            [existingWindow],
            new ResetCreditViewState(ResetCreditKind.Unavailable),
            IsRefreshing: true));

        var loadingStarted = WaitForPropertyConditionAsync(
            viewModel,
            nameof(MainViewModel.ShowLoading),
            () => viewModel.ShowLoading);
        viewModel.ApplySnapshot(new AppUiState(
            "Codex",
            null,
            "已切换数据来源，正在刷新…",
            StatusTone.Refreshing,
            [],
            new ResetCreditViewState(ResetCreditKind.Unavailable),
            IsRefreshing: true));
        await loadingStarted;

        var presentationCompleted = WaitForPropertyConditionAsync(
            viewModel,
            nameof(MainViewModel.IsRefreshing),
            () => !viewModel.IsRefreshing);
        viewModel.ApplySnapshot(new AppUiState(
            "Codex",
            "Plus",
            "更新于 14:31",
            StatusTone.Success,
            [replacementWindow, secondaryWindow],
            new ResetCreditViewState(ResetCreditKind.Unavailable)));

        Assert.IsTrue(viewModel.IsRefreshing);
        Assert.IsTrue(viewModel.ShowLoading);
        Assert.IsFalse(viewModel.ShowContent);
        Assert.IsFalse(viewModel.HasWindows);
        Assert.HasCount(2, viewModel.LoadingWindows);

        await presentationCompleted;

        Assert.IsFalse(viewModel.IsRefreshing);
        Assert.IsFalse(viewModel.ShowLoading);
        Assert.IsTrue(viewModel.ShowContent);
        Assert.IsTrue(viewModel.HasWindows);
        Assert.HasCount(2, viewModel.Windows);
        Assert.AreEqual("7 天额度", viewModel.Windows[0].Name);
    }

    [TestMethod]
    public void ApplySnapshot_SameLocalKeysPreserveItemInstancesAndUpdateValues()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow(
                "five-hour",
                "5 小时额度",
                90,
                "1 小时后重置",
                "14:30",
                new DateTimeOffset(2026, 8, 26, 6, 30, 0, TimeSpan.Zero)),
            CreateQuotaWindow(
                "seven-day",
                "7 天额度",
                99,
                "6 天后重置",
                "周二 14:30",
                new DateTimeOffset(2026, 9, 1, 6, 30, 0, TimeSpan.Zero))));

        var fiveHour = viewModel.Windows.Single(window => window.LocalKey == "five-hour");
        var sevenDay = viewModel.Windows.Single(window => window.LocalKey == "seven-day");

        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow(
                "five-hour",
                "5 小时额度（更新）",
                89,
                "59 分钟后重置",
                "14:31",
                new DateTimeOffset(2026, 8, 26, 6, 31, 0, TimeSpan.Zero)),
            CreateQuotaWindow(
                "seven-day",
                "7 天额度（更新）",
                98,
                "5 天 23 小时后重置",
                "周二 14:31",
                new DateTimeOffset(2026, 9, 1, 6, 31, 0, TimeSpan.Zero))));

        Assert.AreSame(fiveHour, viewModel.Windows.Single(window => window.LocalKey == "five-hour"));
        Assert.AreSame(sevenDay, viewModel.Windows.Single(window => window.LocalKey == "seven-day"));
        Assert.AreEqual(89, fiveHour.DisplayPercent);
        Assert.AreEqual("89%", fiveHour.PercentText);
        Assert.AreEqual("59 分钟后重置", fiveHour.ResetRelative);
        Assert.AreEqual("14:31", fiveHour.ResetAt);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 26, 6, 31, 0, TimeSpan.Zero), fiveHour.ResetAtUtc);
        Assert.AreEqual(QuotaTone.Accent, fiveHour.Tone);
        Assert.AreEqual(98, sevenDay.DisplayPercent);
        Assert.AreEqual("98%", sevenDay.PercentText);
        Assert.AreEqual("5 天 23 小时后重置", sevenDay.ResetRelative);
        Assert.AreEqual("周二 14:31", sevenDay.ResetAt);
        Assert.AreEqual(QuotaTone.Accent, sevenDay.Tone);
    }

    [TestMethod]
    public void ApplySnapshot_TwoQuotaItemsNeverCrossIdentity()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("five-hour", "5 小时额度", 20, "1 小时后重置", "14:30"),
            CreateQuotaWindow("seven-day", "7 天额度", 90, "6 天后重置", "周二 14:30")));

        var fiveHour = viewModel.Windows.Single(window => window.LocalKey == "five-hour");
        var sevenDay = viewModel.Windows.Single(window => window.LocalKey == "seven-day");

        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("five-hour", "5 小时额度", 21, "59 分钟后重置", "14:31"),
            CreateQuotaWindow("seven-day", "7 天额度", 89, "5 天 23 小时后重置", "周二 14:31")));

        Assert.AreSame(fiveHour, viewModel.Windows.Single(window => window.LocalKey == "five-hour"));
        Assert.AreSame(sevenDay, viewModel.Windows.Single(window => window.LocalKey == "seven-day"));
        Assert.AreEqual(21, fiveHour.DisplayPercent);
        Assert.AreEqual(89, sevenDay.DisplayPercent);
    }

    [TestMethod]
    public void ApplySnapshot_AddsOnlyNewLocalKey()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("five-hour", "5 小时额度", 90, "1 小时后重置", "14:30")));
        var fiveHour = viewModel.Windows.Single();

        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("five-hour", "5 小时额度", 89, "59 分钟后重置", "14:31"),
            CreateQuotaWindow("seven-day", "7 天额度", 98, "5 天 23 小时后重置", "周二 14:31")));

        Assert.AreEqual(2, viewModel.Windows.Count);
        Assert.AreSame(fiveHour, viewModel.Windows.Single(window => window.LocalKey == "five-hour"));
        Assert.IsNotNull(viewModel.Windows.SingleOrDefault(window => window.LocalKey == "seven-day"));
    }

    [TestMethod]
    public void ApplySnapshot_RemovesOnlyMissingLocalKey()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("five-hour", "5 小时额度", 90, "1 小时后重置", "14:30"),
            CreateQuotaWindow("seven-day", "7 天额度", 99, "6 天后重置", "周二 14:30")));
        var sevenDay = viewModel.Windows.Single(window => window.LocalKey == "seven-day");

        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("seven-day", "7 天额度", 98, "5 天 23 小时后重置", "周二 14:31")));

        Assert.HasCount(1, viewModel.Windows);
        Assert.AreSame(sevenDay, viewModel.Windows.Single());
        Assert.AreEqual("seven-day", viewModel.Windows.Single().LocalKey);
    }

    [TestMethod]
    public void ApplySnapshot_ReordersExistingItemsWithoutReplacingThem()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("five-hour", "5 小时额度", 90, "1 小时后重置", "14:30"),
            CreateQuotaWindow("seven-day", "7 天额度", 99, "6 天后重置", "周二 14:30")));
        var fiveHour = viewModel.Windows[0];
        var sevenDay = viewModel.Windows[1];

        viewModel.ApplySnapshot(CreateState(
            CreateQuotaWindow("seven-day", "7 天额度", 98, "5 天 23 小时后重置", "周二 14:31"),
            CreateQuotaWindow("five-hour", "5 小时额度", 89, "59 分钟后重置", "14:31")));

        Assert.AreEqual("seven-day", viewModel.Windows[0].LocalKey);
        Assert.AreEqual("five-hour", viewModel.Windows[1].LocalKey);
        Assert.AreSame(sevenDay, viewModel.Windows[0]);
        Assert.AreSame(fiveHour, viewModel.Windows[1]);
    }

    [TestMethod]
    public void QuotaWindowItemViewModel_UpdateFromNotifiesDependentPresentationProperties()
    {
        var item = new QuotaWindowItemViewModel(
            CreateQuotaWindow("five-hour", "5 小时额度", 90, "1 小时后重置", "14:30"));
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                propertyNames.Add(args.PropertyName);
            }
        };

        item.UpdateFrom(CreateQuotaWindow(
            "five-hour",
            "5 小时额度（更新）",
            19,
            "刚刚重置",
            "14:31",
            new DateTimeOffset(2026, 8, 26, 6, 31, 0, TimeSpan.Zero)));

        Assert.AreEqual("5 小时额度（更新）", item.Name);
        Assert.AreEqual(19, item.DisplayPercent);
        Assert.AreEqual("19%", item.PercentText);
        Assert.AreEqual("刚刚重置", item.ResetRelative);
        Assert.AreEqual("14:31", item.ResetAt);
        Assert.AreEqual(new DateTimeOffset(2026, 8, 26, 6, 31, 0, TimeSpan.Zero), item.ResetAtUtc);
        Assert.AreEqual(QuotaTone.Critical, item.Tone);
        Assert.IsTrue(propertyNames.Contains(nameof(QuotaWindowItemViewModel.DisplayPercent)));
        Assert.IsTrue(propertyNames.Contains(nameof(QuotaWindowItemViewModel.PercentText)));
        Assert.IsTrue(propertyNames.Contains(nameof(QuotaWindowItemViewModel.ResetRelative)));
        Assert.IsTrue(propertyNames.Contains(nameof(QuotaWindowItemViewModel.ResetAt)));
        Assert.IsTrue(propertyNames.Contains(nameof(QuotaWindowItemViewModel.Tone)));
    }

    private static MainViewModel CreateViewModel() => new(
        new StubProvider(CreateState()),
        new StubNavigation());

    private static async Task WaitForPropertyConditionAsync(
        INotifyPropertyChanged source,
        string propertyName,
        Func<bool> condition)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (args.PropertyName is null || args.PropertyName == propertyName)
            {
                TryComplete();
            }
        };

        source.PropertyChanged += handler;
        try
        {
            TryComplete();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            source.PropertyChanged -= handler;
        }

        void TryComplete()
        {
            if (condition())
            {
                completed.TrySetResult();
            }
        }
    }

    private static AppUiState CreateState(params QuotaWindowView[] windows) => new(
        "Codex",
        "Plus",
        "更新于 14:30",
        StatusTone.Success,
        windows,
        new ResetCreditViewState(ResetCreditKind.Unavailable));

    private static QuotaWindowView CreateQuotaWindow(
        string localKey,
        string name,
        int remainingPercent,
        string resetRelative,
        string resetAt,
        DateTimeOffset? resetAtUtc = null,
        bool isAvailable = true,
        bool isStale = false) => new(
        localKey,
        name,
        100 - remainingPercent,
        remainingPercent,
        remainingPercent,
        remainingPercent,
        300,
        resetAtUtc,
        resetAt,
        resetRelative,
        QuotaTonePolicy.For(remainingPercent, isStale, isAvailable),
        true,
        isAvailable,
        isStale);

    [DataRow("plus", "已登录 · Plus")]
    [DataRow("TEAM", "已登录 · Team")]
    [DataRow(null, "已登录")]
    [TestMethod]
    public void OAuthAccountStatusIncludesNormalizedPlan(string? planType, string expected)
    {
        Assert.AreEqual(expected, SettingsViewModel.FormatOAuthAccount(planType));
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
        Assert.AreEqual("日志信息已复制", viewModel.StatusText);
    }

    [TestMethod]
    public void DiagnosticsCopyFailureIsReportedWithoutEscapingTheCommand()
    {
        var runtime = new StubRuntimeControl();
        var platform = new StubSettingsPlatformActions();
        var actions = new StubSettingsPageActions { CopyDiagnosticsResult = false };
        var viewModel = new SettingsViewModel(runtime, platform, actions);

        viewModel.CopyDiagnosticsCommand.Execute(null);

        Assert.AreEqual("无法复制日志信息，请关闭占用剪贴板的程序后重试", viewModel.StatusText);
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

        Assert.AreEqual("正在下载更新…", viewModel.DownloadProgressText);
        Assert.AreEqual("17.0 MB / 25.0 MB", viewModel.DownloadProgressSizeText);
        Assert.AreEqual("68%", viewModel.DownloadProgressPercentageText);
        Assert.IsFalse(viewModel.IsDownloadProgressIndeterminate);
        Assert.IsTrue(viewModel.HasDownloadProgress);

        updates.Publish(new WindowsUpdateDownloadProgress(
            WindowsUpdateDownloadPhase.Downloading,
            17 * 1024 * 1024,
            null));
        Assert.IsTrue(viewModel.IsDownloadProgressIndeterminate);
        Assert.AreEqual("17.0 MB", viewModel.DownloadProgressSizeText);
        Assert.AreEqual(string.Empty, viewModel.DownloadProgressPercentageText);

        updates.Publish(new WindowsUpdateDownloadProgress(
            WindowsUpdateDownloadPhase.Downloading,
            17 * 1024 * 1024,
            25 * 1024 * 1024,
            2.1 * 1024 * 1024));
        Assert.AreEqual("17.0 MB / 25.0 MB · 2.1 MB/s", viewModel.DownloadProgressSizeText);

        updates.Publish(new WindowsUpdateDownloadProgress(
            WindowsUpdateDownloadPhase.Verifying,
            25 * 1024 * 1024,
            25 * 1024 * 1024));
        Assert.AreEqual("正在校验安装包…", viewModel.UpdateStatusText);
        Assert.AreEqual("25.0 MB / 25.0 MB", viewModel.DownloadProgressSizeText);
        Assert.AreEqual("100%", viewModel.DownloadProgressPercentageText);
        Assert.IsFalse(viewModel.IsDownloadProgressIndeterminate);

        updates.InstallResult = false;
        Assert.IsFalse(await viewModel.InstallPreparedWindowsUpdateAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task CancelledDownloadKeepsRetryAndBrowserActionsAvailable()
    {
        var updates = new StubWindowsUpdateController
        {
            CurrentResult = AvailableWindowsUpdateResult(),
            DownloadGate = new TaskCompletionSource<WindowsUpdateDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var actions = new StubSettingsPageActions();
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            actions,
            updates);

        var download = viewModel.DownloadWindowsUpdateAsync(CancellationToken.None);
        Assert.IsTrue(updates.DownloadStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(viewModel.CanDownloadWindowsUpdate);

        viewModel.CancelWindowsUpdateCommand.Execute(null);
        var result = await download;

        Assert.IsTrue(result.WasCancelled);
        Assert.IsTrue(viewModel.CanDownloadWindowsUpdate);
        Assert.AreEqual("下载已取消", viewModel.UpdateStatusText);

        await viewModel.OpenWindowsUpdateInBrowserAsync(CancellationToken.None);
        Assert.IsTrue(actions.OpenedWindowsUpdateBrowser);
    }

    private static WindowsUpdateCheckResult AvailableWindowsUpdateResult() => new(
        WindowsUpdateCheckStatus.Available,
        new WindowsUpdateRelease(
            "windows-v0.7.5",
            new SemanticVersion(0, 7, 5),
            "CodexQuotaTray 0.7.5",
            string.Empty,
            null,
            new WindowsUpdateAsset(
                "CodexQuotaTray-0.7.5-setup.exe",
                new Uri("https://github.com/DDJang/CodexQuotaTray/a.exe")),
            new string('a', 64)),
        null,
        DateTimeOffset.UtcNow);

    [TestMethod]
    public async Task BrowserFallback_CancelsActiveDownloadBeforeOpeningBrowser()
    {
        var updates = new StubWindowsUpdateController
        {
            CurrentResult = new WindowsUpdateCheckResult(
                WindowsUpdateCheckStatus.Available,
                new WindowsUpdateRelease(
                    "windows-v0.7.5",
                    new SemanticVersion(0, 7, 5),
                    "CodexQuotaTray 0.7.5",
                    string.Empty,
                    null,
                    new WindowsUpdateAsset(
                        "CodexQuotaTray-0.7.5-setup.exe",
                        new Uri("https://github.com/DDJang/CodexQuotaTray/a.exe")),
                    new string('a', 64)),
                null,
                DateTimeOffset.UtcNow),
            DownloadGate = new TaskCompletionSource<WindowsUpdateDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var actions = new StubSettingsPageActions();
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            actions,
            updates);

        var download = viewModel.DownloadWindowsUpdateAsync(CancellationToken.None);
        Assert.IsTrue(updates.DownloadStarted.Wait(TimeSpan.FromSeconds(2)));
        await viewModel.OpenWindowsUpdateInBrowserAsync(CancellationToken.None);
        await download;

        Assert.IsTrue(updates.DownloadCancellationRequested);
        Assert.IsTrue(actions.OpenedWindowsUpdateBrowser);
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
    public async Task StatisticsSourceSelectionAppliesImmediately()
    {
        var runtime = new StubRuntimeControl(AppSettings.Defaults with
        {
            TokenUsageDataSource = TokenUsageDataSource.CodexCli,
        });
        var viewModel = new SettingsViewModel(
            runtime,
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());

        await viewModel.SelectStatisticsDataSourceAsync(
            TokenUsageDataSource.Local,
            CancellationToken.None);

        Assert.AreEqual(TokenUsageDataSource.Local, runtime.Settings.TokenUsageDataSource);
        Assert.AreEqual((int)TokenUsageDataSource.Local, viewModel.SelectedTokenUsageDataSourceIndex);
        Assert.AreEqual("统计来源已切换", viewModel.StatusText);
    }

    [TestMethod]
    public void DataSourceEditingStatesAreIndependent()
    {
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());

        viewModel.QuotaDataSourceChangeInProgress = true;

        Assert.IsFalse(viewModel.CanEditQuotaDataSource);
        Assert.IsTrue(viewModel.CanEditStatisticsDataSource);
        Assert.IsTrue(viewModel.DataSourceChangeInProgress);

        viewModel.QuotaDataSourceChangeInProgress = false;
        viewModel.StatisticsDataSourceChangeInProgress = true;

        Assert.IsTrue(viewModel.CanEditQuotaDataSource);
        Assert.IsFalse(viewModel.CanEditStatisticsDataSource);
        Assert.IsTrue(viewModel.DataSourceChangeInProgress);
    }

    [TestMethod]
    public async Task UnavailableCliStatisticsSourceIsRejectedAndSelectionRollsBack()
    {
        var runtime = new StubRuntimeControl(AppSettings.Defaults with
        {
            TokenUsageDataSource = TokenUsageDataSource.Local,
        });
        var viewModel = new SettingsViewModel(
            runtime,
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions());
        var selectionResetNotified = false;
        viewModel.PropertyChanged += (_, args) =>
            selectionResetNotified |= args.PropertyName == nameof(SettingsViewModel.SelectedTokenUsageDataSourceIndex);

        await viewModel.SelectStatisticsDataSourceAsync(
            TokenUsageDataSource.CodexCli,
            CancellationToken.None);

        Assert.AreEqual(TokenUsageDataSource.Local, runtime.Settings.TokenUsageDataSource);
        Assert.AreEqual((int)TokenUsageDataSource.Local, viewModel.SelectedTokenUsageDataSourceIndex);
        Assert.IsTrue(selectionResetNotified);
        StringAssert.Contains(viewModel.StatusText, "请先登录可用的 Codex CLI 账户");
    }

    [TestMethod]
    public async Task OAuthDeviceLoginCanBeCancelledWithoutDisablingDataSources()
    {
        var handler = new OAuthLoginPendingHandler();
        using var httpClient = new HttpClient(handler);
        var credentials = new OAuthCredentialManager(
            new EmptyOAuthCredentialStore(),
            new OAuthClient(httpClient, "https://auth.test"));
        await using var account = new WindowsAccountService(new UnusedCliFactory(), credentials);
        var viewModel = new SettingsViewModel(
            new StubRuntimeControl(),
            new StubSettingsPlatformActions(),
            new StubSettingsPageActions(),
            account: account);

        var login = viewModel.LoginOAuthCommand.ExecuteAsync(null);
        Assert.IsTrue(viewModel.ShowOAuthLoginPreparing);
        Assert.IsTrue(viewModel.ShowOAuthCancelButton);
        Assert.IsTrue(viewModel.CanEditDataSources);

        var deviceDetailsReady = WaitForPropertyConditionAsync(
            viewModel,
            nameof(SettingsViewModel.ShowOAuthDeviceLoginDetails),
            () => viewModel.ShowOAuthDeviceLoginDetails);
        handler.ReleaseDeviceCode();
        await deviceDetailsReady;

        Assert.IsTrue(viewModel.ShowOAuthDeviceLoginDetails);
        Assert.IsFalse(viewModel.ShowOAuthLoginPreparing);
        Assert.IsTrue(viewModel.ShowOAuthCancelButton);
        Assert.IsTrue(viewModel.CanEditDataSources);
        Assert.AreEqual("代码：ABCD-EFGH", viewModel.OAuthUserCodeDisplayText);
        Assert.AreEqual("ABCDEFGH", viewModel.OAuthUserCodeClipboardText);
        Assert.AreEqual("验证网址：https://auth.test/codex/device", viewModel.OAuthVerificationDisplayText);

        viewModel.CancelOAuthLoginCommand.Execute(null);
        await login;

        Assert.IsFalse(viewModel.OAuthLoginInProgress);
        Assert.IsFalse(viewModel.ShowOAuthDeviceLoginDetails);
        Assert.IsFalse(viewModel.ShowOAuthCancelButton);
        Assert.IsTrue(viewModel.ShowOAuthLoginButton);
        Assert.AreEqual("未登录", viewModel.OAuthAccountStatusText);
        Assert.AreEqual("OAuth 登录已取消", viewModel.StatusText);
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

        public event EventHandler? TokenRefreshScheduleChanged;

        public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            var previousMode = Settings.TokenRefreshMode;
            Settings = settings;
            if (previousMode != Settings.TokenRefreshMode)
            {
                TokenRefreshScheduleChanged?.Invoke(this, EventArgs.Empty);
            }
            return Task.CompletedTask;
        }

        public ValueTask RequestAsync(RefreshReason reason, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class EmptyOAuthCredentialStore : IOAuthCredentialStore
    {
        public Task<OAuthCredentials?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<OAuthCredentials?>(null);

        public Task SaveAsync(OAuthCredentials credentials, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UnusedCliFactory : ICodexAppServerClientFactory
    {
        public ICodexAppServerClient Create() => throw new InvalidOperationException("CLI is not used by this test.");
    }

    private sealed class OAuthLoginPendingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource deviceCodeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestCount;

        internal void ReleaseDeviceCode() => deviceCodeRelease.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                await deviceCodeRelease.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"device_auth_id\":\"device-1\",\"user_code\":\"ABCD-EFGH\",\"interval\":1}"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{}"),
            };
        }
    }

    private sealed class StubSettingsPlatformActions(bool canConfigureStartup = true) : ISettingsPlatformActions
    {
        public bool CanConfigureStartup { get; } = canConfigureStartup;

        public int SetStartupCount { get; private set; }

        public string TokenSyncStatusText => "已关闭";

        public string TokenSyncAddressText => string.Empty;

        public string TokenSyncDeviceNameText => string.Empty;

        public string TokenSyncMobileStatusText => string.Empty;

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

        public Task<string> RepairPhoneConnectionAsync(CancellationToken cancellationToken) =>
            Task.FromResult("尚未尝试");
    }

    private sealed class StubSettingsPageActions : ISettingsPageActions
    {
        public int RefreshCount { get; private set; }

        public bool OpenedUsage { get; private set; }

        public bool CopiedDiagnostics { get; private set; }

        public bool CopyDiagnosticsResult { get; init; } = true;

        public bool OpenedWindowsUpdateBrowser { get; private set; }

        public Task RefreshQuotaAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public void OpenOfficialUsage() => OpenedUsage = true;

        public Task OpenWindowsUpdateBrowserAsync(CancellationToken cancellationToken)
        {
            OpenedWindowsUpdateBrowser = true;
            return Task.CompletedTask;
        }

        public bool CopyDiagnostics()
        {
            CopiedDiagnostics = true;
            return CopyDiagnosticsResult;
        }
    }

    private sealed class StubWindowsUpdateController : IWindowsUpdateController
    {
        public bool IsProduction => true;

        public bool AutomaticChecksEnabled { get; private set; } = true;

        public bool UpdateRemindersEnabled { get; private set; } = true;

        public bool AutoLaunchInstallerAfterDownload { get; private set; }

        public DateTimeOffset? LastAttemptUtc { get; init; }

        public DateTimeOffset? LastSuccessfulCheckUtc { get; init; }

        public WindowsUpdateCheckResult CurrentResult { get; set; } = WindowsUpdateCheckResult.NotChecked;

        public WindowsUpdateDownloadProgress DownloadProgress { get; private set; } = WindowsUpdateDownloadProgress.Idle;

        public bool InstallResult { get; set; } = true;

        public TaskCompletionSource<WindowsUpdateCheckResult>? CheckGate { get; init; }

        public TaskCompletionSource<WindowsUpdateDownloadResult>? DownloadGate { get; init; }

        public ManualResetEventSlim DownloadStarted { get; } = new(false);

        public bool DownloadCancellationRequested { get; private set; }

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

        public async Task<WindowsUpdateDownloadResult> DownloadAsync(CancellationToken cancellationToken)
        {
            DownloadStarted.Set();
            if (DownloadGate is null)
            {
                return WindowsUpdateDownloadResult.Failed("not used");
            }

            try
            {
                return await DownloadGate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                DownloadCancellationRequested = true;
                Publish(new WindowsUpdateDownloadProgress(WindowsUpdateDownloadPhase.Cancelled));
                return WindowsUpdateDownloadResult.Cancelled();
            }
        }

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
