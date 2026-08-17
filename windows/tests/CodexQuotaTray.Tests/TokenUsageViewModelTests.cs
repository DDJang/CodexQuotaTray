using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenUsageViewModelTests
{
    [TestMethod]
    public void FormatterAndBucketsMatchTokenUsageProductSemantics()
    {
        Assert.AreEqual("999", TokenUsageFormatter.Format(999));
        Assert.AreEqual("1.7M", TokenUsageFormatter.Format(1_700_000));
        Assert.AreEqual("128K", TokenUsageFormatter.Format(128_392));
        Assert.AreEqual(0, TokenHeatmap.Bucket(0, [10, 20, 30, 40]));
        Assert.AreEqual(1, TokenHeatmap.Bucket(10, [10, 20, 30, 40]));
        Assert.AreEqual(4, TokenHeatmap.Bucket(40, [10, 20, 30, 40]));
    }

    [TestMethod]
    public void HeatmapBuildsSeventeenSundayFirstWeeksWithTooltips()
    {
        var today = new DateOnly(2026, 8, 12);
        var cells = TokenHeatmap.Build(
            [new TokenUsageDay(today, 128_392, null, null, null, null)],
            today,
            17);

        Assert.HasCount(119, cells);
        Assert.AreEqual(new DateOnly(2026, 4, 19), cells[0].Date);
        Assert.AreEqual(new DateOnly(2026, 8, 15), cells[^1].Date);
        Assert.AreEqual(DayOfWeek.Sunday, cells[0].Date.DayOfWeek);
        var todayCell = cells.Single(cell => cell.Date == today);
        Assert.AreEqual("128,392 Token", todayCell.TokenText);
        Assert.AreEqual("2026-08-12", todayCell.DateText);
        Assert.AreEqual("8 月 12 日 · 128,392 Token", todayCell.AutomationText);
    }

    [TestMethod]
    public async Task RefreshProjectsSummaryAndKeepsLastResultAfterFailure()
    {
        var calls = 0;
        var snapshot = CreateSnapshot(128_392);
        var viewModel = new TokenUsageViewModel(_ => ++calls == 1
            ? Task.FromResult(snapshot)
            : Task.FromException<TokenUsageSnapshot>(new IOException("scan failed")));

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.IsTrue(viewModel.HasData);
        Assert.IsTrue(viewModel.ShowContent);
        Assert.IsFalse(viewModel.ShowLoading);
        Assert.AreEqual("128K", viewModel.TodayTokens);
        Assert.AreEqual($"更新于 {snapshot.GeneratedAtUtc.ToLocalTime():HH:mm}", viewModel.StatusText);
        Assert.HasCount(119, viewModel.HeatmapCells);
        Assert.AreEqual(StatusTone.Success, viewModel.StatusTone);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.IsTrue(viewModel.HasData);
        Assert.IsTrue(viewModel.ShowContent);
        Assert.IsFalse(viewModel.ShowLoading);
        Assert.IsFalse(viewModel.HasErrorWithoutData);
        Assert.AreEqual("刷新失败 · 显示上次数据", viewModel.StatusText);
        Assert.AreEqual(StatusTone.Warning, viewModel.StatusTone);
    }

    [TestMethod]
    public void RestoredCacheProjectsWithoutStartingAScan()
    {
        var scans = 0;
        var snapshot = CreateSnapshot(128_392);
        var viewModel = new TokenUsageViewModel(_ =>
        {
            scans++;
            return Task.FromResult(snapshot);
        });

        viewModel.RestoreSnapshot(snapshot);

        Assert.AreEqual(0, scans);
        Assert.IsTrue(viewModel.HasData);
        Assert.AreEqual("128K", viewModel.TodayTokens);
        Assert.AreEqual(snapshot.GeneratedAtUtc, viewModel.LastAttemptUtc);
        Assert.AreEqual($"更新于 {snapshot.GeneratedAtUtc.ToLocalTime():HH:mm}", viewModel.StatusText);
    }

    [TestMethod]
    public async Task EmptyAndInitialFailureHaveDistinctStates()
    {
        var empty = new TokenUsageViewModel(_ => Task.FromResult(CreateSnapshot(0)));
        await empty.RefreshCommand.ExecuteAsync(null);
        Assert.IsTrue(empty.HasNoData);
        Assert.IsTrue(empty.ShowEmpty);
        Assert.IsFalse(empty.ShowLoading);
        Assert.IsFalse(empty.ShowContent);
        Assert.IsFalse(empty.HasErrorWithoutData);

        var failed = new TokenUsageViewModel(_ => Task.FromException<TokenUsageSnapshot>(new IOException("scan failed")));
        await failed.RefreshCommand.ExecuteAsync(null);
        Assert.IsFalse(failed.HasNoData);
        Assert.IsTrue(failed.ShowError);
        Assert.IsFalse(failed.ShowLoading);
        Assert.IsFalse(failed.ShowContent);
        Assert.IsTrue(failed.HasErrorWithoutData);
        Assert.AreEqual(StatusTone.Error, failed.StatusTone);
    }

    [TestMethod]
    public async Task InitialRefreshDoesNotExposeContentUntilLoadingCompletes()
    {
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = CreateSnapshot(128_392);
        var viewModel = new TokenUsageViewModel(async _ =>
        {
            scanStarted.SetResult();
            await releaseScan.Task;
            return snapshot;
        });

        var refresh = viewModel.RefreshNowAsync(CancellationToken.None);
        await scanStarted.Task;

        Assert.IsTrue(viewModel.ShowLoading);
        Assert.IsFalse(viewModel.ShowContent);
        Assert.IsFalse(viewModel.ShowEmpty);
        Assert.IsFalse(viewModel.ShowError);

        releaseScan.SetResult();
        await refresh;

        Assert.IsFalse(viewModel.ShowLoading);
        Assert.IsTrue(viewModel.ShowContent);
        Assert.IsFalse(viewModel.ShowEmpty);
        Assert.IsFalse(viewModel.ShowError);
        Assert.HasCount(119, viewModel.HeatmapCells);
    }

    [TestMethod]
    public async Task RestoredContentRemainsVisibleDuringBackgroundRefresh()
    {
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = CreateSnapshot(128_392);
        var viewModel = new TokenUsageViewModel(async _ =>
        {
            scanStarted.SetResult();
            await releaseScan.Task;
            return snapshot;
        });
        viewModel.RestoreSnapshot(snapshot);

        var refresh = viewModel.RefreshNowAsync(CancellationToken.None);
        await scanStarted.Task;

        Assert.IsFalse(viewModel.ShowLoading);
        Assert.IsTrue(viewModel.ShowContent);
        Assert.IsFalse(viewModel.ShowEmpty);
        Assert.IsFalse(viewModel.ShowError);

        releaseScan.SetResult();
        await refresh;
    }

    [TestMethod]
    public void BackgroundRefreshPolicyUsesConfiguredFixedIntervals()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        Assert.IsTrue(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every5Minutes, null, now));
        Assert.IsFalse(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every5Minutes, now.AddMinutes(-4), now));
        Assert.IsTrue(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every5Minutes, now.AddMinutes(-5), now));
        Assert.IsFalse(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every15Minutes, now.AddMinutes(-14), now));
        Assert.IsTrue(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every15Minutes, now.AddMinutes(-15), now));
        Assert.IsFalse(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every30Minutes, now.AddMinutes(-29), now));
        Assert.IsTrue(TokenUsageRefreshPolicy.IsDue(RefreshMode.Every30Minutes, now.AddMinutes(-30), now));
        Assert.IsFalse(TokenUsageRefreshPolicy.IsDue(RefreshMode.ManualOnly, null, now));
    }

    [TestMethod]
    public void PanelOpenRefreshPolicyIsIndependentAndDeduplicatesRapidReopen()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

        Assert.IsFalse(TokenUsageRefreshPolicy.ShouldRefreshOnPanelOpen(false, null, now));
        Assert.IsTrue(TokenUsageRefreshPolicy.ShouldRefreshOnPanelOpen(true, null, now));
        Assert.IsFalse(TokenUsageRefreshPolicy.ShouldRefreshOnPanelOpen(true, now.AddSeconds(-9), now));
        Assert.IsTrue(TokenUsageRefreshPolicy.ShouldRefreshOnPanelOpen(true, now.AddSeconds(-10), now));
    }

    private static TokenUsageSnapshot CreateSnapshot(long todayTokens)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return new TokenUsageSnapshot(
            1,
            new DateTimeOffset(2026, 8, 12, 11, 24, 0, TimeSpan.Zero),
            "Asia/Shanghai",
            new TokenUsageSummary(todayTokens, todayTokens, todayTokens, todayTokens, todayTokens, today, todayTokens > 0 ? 1 : 0, todayTokens > 0 ? 1 : 0, todayTokens > 0 ? 1 : 0),
            todayTokens > 0 ? [new TokenUsageDay(today, todayTokens, null, null, null, null)] : [],
            1,
            10,
            todayTokens > 0 ? today : null,
            todayTokens > 0 ? today : null);
    }
}
