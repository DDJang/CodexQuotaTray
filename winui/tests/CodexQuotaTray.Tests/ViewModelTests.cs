using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Runtime;

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
            StatusText = "● 更新于 14:30",
            StatusTone = StatusTone.Success,
            IsRefreshing = false,
        });

        Assert.IsFalse(viewModel.IsRefreshing);
        Assert.IsTrue(viewModel.RefreshCommand.CanExecute(null));
        Assert.AreEqual("● 更新于 14:30", viewModel.StatusText);
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
        viewModel.CopyQuotaSummaryCommand.Execute(null);
        viewModel.CopyDiagnosticsCommand.Execute(null);
        var aboutHost = new object();
        viewModel.ShowAboutCommand.Execute(aboutHost);

        Assert.AreEqual(1, actions.RefreshCount);
        Assert.IsTrue(actions.OpenedUsage);
        Assert.IsTrue(actions.CopiedSummary);
        Assert.IsTrue(actions.CopiedDiagnostics);
        Assert.IsTrue(actions.ShowedAbout);
        Assert.AreSame(aboutHost, actions.AboutHost);
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

    private sealed class StubRuntimeControl : IQuotaRuntimeControl
    {
        public AppSettings Settings { get; private set; } = AppSettings.Defaults;

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

    private sealed class StubSettingsPlatformActions : ISettingsPlatformActions
    {
        public Task SetStartupAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public void OpenDataDirectory()
        {
        }

        public Task<int> ImportProductionDataAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task ClearQuotaCacheAsync() => Task.CompletedTask;
    }

    private sealed class StubSettingsPageActions : ISettingsPageActions
    {
        public int RefreshCount { get; private set; }

        public bool OpenedUsage { get; private set; }

        public bool CopiedSummary { get; private set; }

        public bool CopiedDiagnostics { get; private set; }

        public bool ShowedAbout { get; private set; }

        public object? AboutHost { get; private set; }

        public Task RefreshQuotaAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public void OpenOfficialUsage() => OpenedUsage = true;

        public void CopyQuotaSummary() => CopiedSummary = true;

        public void CopyDiagnostics() => CopiedDiagnostics = true;

        public void ShowAbout(object? host)
        {
            ShowedAbout = true;
            AboutHost = host;
        }
    }
}
