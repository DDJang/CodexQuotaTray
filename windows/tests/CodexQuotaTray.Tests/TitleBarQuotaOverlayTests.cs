using System.Drawing;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TitleBarQuotaOverlayTests
{
    [TestMethod]
    [DataRow(true, false, false, true)]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    [DataRow(true, true, true, false)]
    [DataRow(false, false, false, false)]
    [DataRow(false, true, false, false)]
    [DataRow(false, false, true, false)]
    [DataRow(false, true, true, false)]
    public void VisibilityFollowsHostStateWithoutForegroundRequirement(bool visible, bool minimized, bool cloaked, bool expected)
    {
        Assert.AreEqual(expected, TitleBarQuotaOverlay.CanShowHost(visible, minimized, cloaked));
    }

    [TestMethod]
    public void MovingOrSwitchingSameDpiHostReusesRasterButContentSizeAndDpiChangesRepaint()
    {
        var frame = new TitleBarOverlayFrame(new IntPtr(1), "剩余 50%", new Rectangle(-1500, -300, 300, 48), 192);
        Assert.IsTrue(frame.RequiresRepaint(null));
        Assert.IsFalse(frame.RequiresRepaint(frame));
        Assert.IsFalse((frame with { Bounds = new Rectangle(-1200, -200, 300, 48) }).RequiresRepaint(frame));
        Assert.IsFalse((frame with { Host = new IntPtr(2) }).RequiresRepaint(frame));
        Assert.IsTrue((frame with { Text = "已用 50%" }).RequiresRepaint(frame));
        Assert.IsTrue((frame with { Dpi = 144 }).RequiresRepaint(frame));
        Assert.IsTrue((frame with { Bounds = new Rectangle(-1500, -300, 350, 48) }).RequiresRepaint(frame));
    }

    [TestMethod]
    [DataRow(true, "剩余", 54)]
    [DataRow(false, "已用", 46)]
    public void ProjectionFollowsDisplayModeUsingCanonicalUsage(bool showRemainingPercent, string label, int expected)
    {
        var window = QuotaWindowView.Demo("5 小时额度", 54, "稍后重置", "12:00") with
        {
            RemainingPercent = 46,
            DisplayPercent = 46,
        };
        var presentation = TitleBarQuotaOverlay.Project(State(window), showRemainingPercent);

        Assert.AreEqual($"{label} 5 小时 {expected}%（重置未知）", presentation.Text);
    }

    [TestMethod]
    public void FiftyPercentStillChangesTheLabelWithoutNewQuotaData()
    {
        var state = State(QuotaWindowView.Demo("窗口", 50, "稍后", "12:00"));
        Assert.AreEqual("剩余 窗口 50%（重置未知）", TitleBarQuotaOverlay.Project(state, true).Text);
        Assert.AreEqual("已用 窗口 50%（重置未知）", TitleBarQuotaOverlay.Project(state, false).Text);
    }

    [TestMethod]
    [DataRow(true, "剩余", 64)]
    [DataRow(false, "已用", 36)]
    public void ProjectionPreservesUnknownWindowsAndReportsOmittedWindowCount(bool showRemainingPercent, string label, int expected)
    {
        var windows = new[]
        {
            QuotaWindowView.Demo("自定义窗口", 0, "未知", "未知") with { IsPercentageReliable = false },
            QuotaWindowView.Demo("2 小时额度", 64, "稍后", "12:00"),
            QuotaWindowView.Demo("30 天额度", 82, "稍后", "12:00"),
        };
        var presentation = TitleBarQuotaOverlay.Project(State(windows), showRemainingPercent);

        StringAssert.Contains(presentation.Text, $"{label} 自定义窗口 —");
        StringAssert.Contains(presentation.Text, $"2 小时 {expected}%（重置未知） · +1");
        StringAssert.Contains(presentation.CompactText, "自定义窗口 — · +2");
        Assert.IsFalse(presentation.Text.Contains("0%", StringComparison.Ordinal));
        Assert.AreEqual(QuotaTone.Unavailable, presentation.Tone);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(101)]
    public void InvalidPercentageNeverBecomesAUsableValue(int used)
    {
        var window = QuotaWindowView.Demo("窗口", 80, "稍后", "12:00") with { UsedPercent = used };
        var presentation = TitleBarQuotaOverlay.Project(State(window));
        StringAssert.Contains(presentation.Text, "窗口 —");
        Assert.AreEqual(QuotaTone.Unavailable, presentation.Tone);
    }

    [TestMethod]
    public void RefreshFailureKeepsLastValueAndLabelsItBeforeQuotaText()
    {
        var state = State(QuotaWindowView.Demo("窗口", 19, "稍后", "12:00")) with
        {
            StatusText = "刷新失败：Codex 连接已断开 · 显示上次数据",
            StatusTone = StatusTone.Error,
        };
        var presentation = TitleBarQuotaOverlay.Project(state);
        StringAssert.StartsWith(presentation.Text, "离线 · 上次数据 · 剩余");
        StringAssert.Contains(presentation.Text, "19%");
        Assert.AreEqual(QuotaTone.Unavailable, presentation.Tone);
    }

    [TestMethod]
    public void RefreshingStaleAndNoDataHaveDistinctPresentation()
    {
        var state = State(QuotaWindowView.Demo("窗口", 54, "稍后", "12:00"));
        var refreshing = TitleBarQuotaOverlay.Project(state with { IsRefreshing = true });
        var expired = TitleBarQuotaOverlay.Project(state with { StatusText = "更新于 9月1日 · 已过期", StatusTone = StatusTone.Warning });
        var unavailable = TitleBarQuotaOverlay.Project(State() with { StatusText = "OAuth 账户未登录", StatusTone = StatusTone.Error });

        StringAssert.Contains(refreshing.Text, "刷新中");
        StringAssert.Contains(expired.Text, "已过期");
        StringAssert.Contains(unavailable.Text, "OAuth 账户未登录");
        Assert.IsFalse(unavailable.Text.Contains('%'));
        Assert.AreEqual(QuotaTone.Unavailable, expired.Tone);
    }

    [TestMethod]
    public void NetworkFailureDoesNotClaimTheComputerIsOffline()
    {
        var state = State(QuotaWindowView.Demo("窗口", 54, "稍后", "12:00")) with
        {
            StatusText = "刷新失败：OAuth 网络请求失败 · 显示上次数据",
            StatusTone = StatusTone.Error,
        };
        var presentation = TitleBarQuotaOverlay.Project(state);
        StringAssert.Contains(presentation.Text, "网络失败 · 上次数据");
        Assert.IsFalse(presentation.Text.Contains("离线", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SourceSwitchCanClearThePreviousPercentage()
    {
        var before = TitleBarQuotaOverlay.Project(State(QuotaWindowView.Demo("窗口", 19, "稍后", "12:00")));
        var after = TitleBarQuotaOverlay.Project(State() with { IsRefreshing = true, StatusText = "正在连接" });
        StringAssert.Contains(before.Text, "19%");
        Assert.AreEqual("刷新中…", after.Text);
        Assert.IsFalse(after.Text.Contains("19%", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(1, "1分后")]
    [DataRow(59, "1分后")]
    [DataRow(1080, "18分后")]
    [DataRow(3540, "59分后")]
    [DataRow(3541, "1时后")]
    [DataRow(3600, "1时后")]
    [DataRow(8280, "2时18分后")]
    [DataRow(86340, "23时59分后")]
    [DataRow(86400, "1天后")]
    [DataRow(532800, "6天4时后")]
    public void CountdownUsesMinutesHoursAndDaysWithoutPrematureZero(int seconds, string expected)
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        Assert.AreEqual(expected, TitleBarQuotaOverlay.ResetCountdown(now.AddSeconds(seconds), now));
    }

    [TestMethod]
    [DataRow(true, "剩余", 13, 84)]
    [DataRow(false, "已用", 87, 16)]
    public void CountdownStaysWithEachWindowAndNarrowLayoutsKeepPercentages(bool remaining, string label, int first, int second)
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var windows = new[]
        {
            QuotaWindowView.Demo("5小时额度", 13, "过时的相对时间", "未知") with { ResetAtUtc = now.AddMinutes(138) },
            QuotaWindowView.Demo("7天额度", 84, "过时的相对时间", "未知") with { ResetAtUtc = now.AddDays(6).AddHours(4) },
            QuotaWindowView.Demo("自定义额度", 72, "未知", "未知") with { ResetAtUtc = now.AddMinutes(18) },
        };
        var result = TitleBarQuotaOverlay.Project(State(windows), remaining, now);
        Assert.AreEqual($"{label} 5小时 {first}%（2时18分后） · 7天 {second}%（6天4时后） · +1", result.Text);
        Assert.AreEqual($"{label} 5小时 {first}% · 7天 {second}% · +1", result.PercentageText);
        Assert.AreEqual($"{label} 5小时 {first}% · +2", result.CompactText);
        StringAssert.Contains(result.TooltipText, "自定义：");
        StringAssert.Contains(result.TooltipText, "18分后");
        StringAssert.Contains(result.TooltipText, windows[0].ResetAtUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(result.TooltipText.Contains("过时的相对时间", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ClockAdvancesCountdownAndExpiryKeepsTheLastPercentageEvenOnFailure()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var state = State(QuotaWindowView.Demo("窗口", 13, "旧文本", "未知") with { ResetAtUtc = now.AddMinutes(2) });
        StringAssert.Contains(TitleBarQuotaOverlay.Project(state, now: now).Text, "13%（2分后）");
        StringAssert.Contains(TitleBarQuotaOverlay.Project(state, now: now.AddMinutes(1)).Text, "13%（1分后）");
        var failed = state with { StatusText = "网络失败", StatusTone = StatusTone.Error };
        StringAssert.Contains(TitleBarQuotaOverlay.Project(failed, now: now.AddMinutes(2)).Text, "13%（待更新）");
        StringAssert.Contains(TitleBarQuotaOverlay.Project(failed, now: now.AddDays(1)).Text, "13%（待更新）");
        StringAssert.Contains(TitleBarQuotaOverlay.Project(state with { IsRefreshing = true }, now: now.AddMinutes(2)).Text, "刷新中…");
        Assert.AreEqual("重置未知", TitleBarQuotaOverlay.ResetCountdown(null, now));
        // Different timestamp offsets still identify the same instant.
        Assert.AreEqual("待更新", TitleBarQuotaOverlay.ResetCountdown(now.ToOffset(TimeSpan.FromHours(8)), now));
    }

    [TestMethod]
    public void DeadlineInsideTheSameMinuteUpdatesBeforeTheLastTimerStops()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:10Z");
        var windows = new[] { QuotaWindowView.Demo("窗口", 13, "未知", "未知") with { ResetAtUtc = now.AddSeconds(20) } };
        Assert.IsFalse(TitleBarQuotaOverlay.NeedsCountdownUpdate(windows, now, now.AddSeconds(19)));
        Assert.IsTrue(TitleBarQuotaOverlay.NeedsCountdownUpdate(windows, now, now.AddSeconds(20)));
        Assert.IsTrue(TitleBarQuotaOverlay.NeedsCountdownUpdate(windows, now, now.AddMinutes(1)));
        Assert.IsFalse(TitleBarQuotaOverlay.NeedsCountdownUpdate(windows, now.AddSeconds(20), now.AddSeconds(25)));
        Assert.IsTrue(TitleBarQuotaOverlay.NeedsCountdownUpdate(windows, now, now.AddMinutes(-1)));
    }

    [TestMethod]
    [DataRow("2026-10-08T10:00:00Z", 20_000, 20_000u)]
    [DataRow("2026-10-08T10:00:37.125Z", 120_000, 22_875u)]
    [DataRow("2026-10-08T10:01:00Z", 120_000, 60_000u)]
    [DataRow("2026-10-08T23:59:59.999Z", 120_000, 10u)]
    [DataRow("2026-10-08T18:00:37.125+08:00", 120_000, 22_875u)]
    public void CountdownWakeupUsesTheEarlierMinuteBoundaryOrReset(string timestamp, int resetAfterMilliseconds, uint expectedDelay)
    {
        var now = DateTimeOffset.Parse(timestamp);
        var windows = new[] { QuotaWindowView.Demo("窗口", 13, "未知", "未知") with { ResetAtUtc = now.AddMilliseconds(resetAfterMilliseconds) } };
        var due = TitleBarQuotaOverlay.NextCountdownUpdateAt(windows, now);
        Assert.IsNotNull(due);
        Assert.AreEqual(expectedDelay, TitleBarQuotaOverlay.CountdownTimerDelayMilliseconds(due.Value, now));
    }

    [TestMethod]
    public void NewEarlierResetChangesTheWakeupButGeometryUpdatesDoNotPostponeIt()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var first = QuotaWindowView.Demo("窗口一", 13, "未知", "未知") with { ResetAtUtc = now.AddMinutes(2) };
        var second = QuotaWindowView.Demo("窗口二", 84, "未知", "未知") with { ResetAtUtc = now.AddSeconds(20) };
        Assert.AreEqual(now.AddMinutes(1), TitleBarQuotaOverlay.NextCountdownUpdateAt([first], now));
        Assert.AreEqual(now.AddSeconds(20), TitleBarQuotaOverlay.NextCountdownUpdateAt([first, second], now));
        Assert.AreEqual(now.AddSeconds(20), TitleBarQuotaOverlay.NextCountdownUpdateAt([first, second], now.AddSeconds(10)));
        Assert.AreEqual(now.AddMinutes(1), TitleBarQuotaOverlay.NextCountdownUpdateAt([first, second], now.AddSeconds(20)));
        Assert.IsNull(TitleBarQuotaOverlay.NextCountdownUpdateAt([first, second], now.AddMinutes(2)));
    }

    [TestMethod]
    public void MissingOrPassedResetsDoNotWakeTheTimerAndDateRangeEndDoesNotOverflow()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var unknown = QuotaWindowView.Demo("未知窗口", 13, "未知", "未知");
        var past = unknown with { ResetAtUtc = now.AddSeconds(-1) };
        Assert.IsNull(TitleBarQuotaOverlay.NextCountdownUpdateAt([], now));
        Assert.IsNull(TitleBarQuotaOverlay.NextCountdownUpdateAt([unknown, past], now));
        var limit = DateTimeOffset.MaxValue;
        var future = unknown with { ResetAtUtc = limit };
        Assert.AreEqual(limit, TitleBarQuotaOverlay.NextCountdownUpdateAt([future], limit.AddTicks(-1)));
    }

    [TestMethod]
    public void TimerDelayRoundsUpAndHandlesPaintingLatencyOrClockChanges()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        Assert.AreEqual(11u, TitleBarQuotaOverlay.CountdownTimerDelayMilliseconds(now.AddTicks(100_001), now));
        Assert.AreEqual(10u, TitleBarQuotaOverlay.CountdownTimerDelayMilliseconds(now.AddMilliseconds(20), now.AddMilliseconds(25)));
        Assert.AreEqual(60_000u, TitleBarQuotaOverlay.CountdownTimerDelayMilliseconds(now.AddDays(1), now));
    }

    [TestMethod]
    public void HostIdentityRejectsLookalikesAndNonMainSurfaces()
    {
        bool Match(string name = "ChatGPT", string family = TitleBarQuotaOverlay.HostPackageFamily,
            string title = "ChatGPT", string windowClass = "Chrome_WidgetWin_1", bool tool = false, bool owner = false) =>
            TitleBarQuotaOverlay.IsHost(name, family, title, windowClass, tool, owner);

        Assert.IsTrue(Match());
        Assert.IsTrue(Match(name: "Codex", title: "Codex"));
        Assert.IsFalse(Match(family: "Example.ChatGPT_unknown"));
        Assert.IsFalse(Match(family: "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0"));
        Assert.IsFalse(Match(name: "OtherApp"));
        Assert.IsFalse(Match(title: "Desktop pet"));
        Assert.IsFalse(Match(windowClass: "OtherClass"));
        Assert.IsFalse(Match(tool: true));
        Assert.IsFalse(Match(owner: true));
    }

    [TestMethod]
    [DataRow(96u)]
    [DataRow(120u)]
    [DataRow(144u)]
    [DataRow(192u)]
    public void LayoutAvoidsMenusAndCaptionButtonsOnNegativeCoordinateMonitor(uint dpi)
    {
        var host = new Rectangle(-3200, -400, 3000, 1600);
        var work = new Rectangle(-3200, -400, 3200, 1700);
        var buttons = new Rectangle(-520, -400, 320, 60);
        var width = (int)Math.Ceiling(350 * dpi / 96d);
        var placement = TitleBarQuotaOverlay.Place(host, work, buttons, dpi, width);

        Assert.IsNotNull(placement);
        var value = placement.Value;
        Assert.IsTrue(value.Left >= host.Left + TitleBarQuotaOverlay.LeftReserveDips * dpi / 96d);
        Assert.IsTrue(value.Right <= buttons.Left - 12 * dpi / 96d);
        Assert.IsTrue(value.Right <= host.Right - TitleBarQuotaOverlay.RightReserveDips * dpi / 96d);
        Assert.IsTrue(work.Contains(value));
    }

    [TestMethod]
    public void NarrowWindowCanFallBackAndHidesWhenNeitherLayoutFits()
    {
        var work = new Rectangle(0, 0, 1920, 1040);
        var host = new Rectangle(0, 0, 860, 800);
        Assert.IsNull(TitleBarQuotaOverlay.Place(host, work, null, 96, 350));
        Assert.IsNotNull(TitleBarQuotaOverlay.Place(host, work, null, 96, 230));
        Assert.IsNull(TitleBarQuotaOverlay.Place(host with { Width = 600 }, work, null, 96, 230));
        Assert.IsNotNull(TitleBarQuotaOverlay.Place(host with { Width = 1300 }, work, null, 96, 350));
    }

    [TestMethod]
    public void MissingDpiAndTitleBarOutsideWorkAreaAreHidden()
    {
        var work = new Rectangle(0, 0, 1920, 1040);
        Assert.IsNull(TitleBarQuotaOverlay.Place(work, work, null, 0, 300));
        Assert.IsNull(TitleBarQuotaOverlay.Place(new Rectangle(0, -100, 1920, 1000), work, null, 96, 300));
    }

    private static AppUiState State(params QuotaWindowView[] windows) =>
        new("Codex", null, "已更新", StatusTone.Success, windows, new ResetCreditViewState(ResetCreditKind.Unavailable), IsPrototype: false);
}
