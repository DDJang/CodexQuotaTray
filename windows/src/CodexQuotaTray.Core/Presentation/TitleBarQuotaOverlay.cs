using System.Drawing;
using System.Globalization;
using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public sealed record TitleBarQuotaPresentation(string Text, string PercentageText, string CompactText, string TooltipText, QuotaTone Tone);

public sealed record TitleBarOverlayFrame(IntPtr Host, string Text, Rectangle Bounds, uint Dpi)
{
    public bool RequiresRepaint(TitleBarOverlayFrame? previous) => previous is null
        || Text != previous.Text || Dpi != previous.Dpi || Bounds.Size != previous.Bounds.Size;
}

public static class TitleBarQuotaOverlay
{
    public static bool CanShowHost(bool visible, bool minimized, bool cloaked) => visible && !minimized && !cloaked;

    public const string HostPackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
    public const int LeftReserveDips = 440;
    public const int RightReserveDips = 160;
    public const int HeightDips = 24;
    public const int TopInsetDips = 6;

    public static TitleBarQuotaPresentation Project(AppUiState state, bool showRemainingPercent = true, DateTimeOffset? now = null)
    {
        var status = state.IsRefreshing ? "刷新中…"
            : state.Windows.Count == 0 ? state.StatusText
            : state.StatusTone == StatusTone.Error ? FailureLabel(state.StatusText)
            : state.Windows.Any(window => window.IsStale) || state.StatusText.Contains("已过期", StringComparison.Ordinal)
                ? "已过期"
            : state.StatusTone == StatusTone.Warning ? "部分不可用"
            : string.Empty;
        var prefix = string.IsNullOrWhiteSpace(status) ? string.Empty : $"{status} · ";

        var currentTime = now ?? DateTimeOffset.UtcNow;
        var full = FormatWindows(state.Windows, 2, showRemainingPercent, currentTime);
        var percentages = FormatWindows(state.Windows, 2, showRemainingPercent);
        var compact = FormatWindows(state.Windows, 1, showRemainingPercent);
        var label = showRemainingPercent ? "剩余" : "已用";
        var reliable = state.Windows.Where(IsReliable).ToArray();
        var tone = state.StatusTone is StatusTone.Error or StatusTone.Warning
            || state.Windows.Any(window => window.IsStale)
            || state.StatusText.Contains("已过期", StringComparison.Ordinal)
            || reliable.Length != state.Windows.Count
            || reliable.Length == 0
                ? QuotaTone.Unavailable
                : QuotaTonePolicy.For(reliable.Min(window => 100 - window.UsedPercent), false, true);
        return new(
            full.Length == 0 ? status : $"{prefix}{label} {full}",
            percentages.Length == 0 ? status : $"{prefix}{label} {percentages}",
            compact.Length == 0 ? status : $"{prefix}{label} {compact}",
            string.Join("\n", state.Windows.Select(window =>
                $"{WindowName(window)}：{label} {Percentage(window, showRemainingPercent)} · 重置：{ResetCountdown(window.ResetAtUtc, currentTime)}\n"
                + (window.ResetAtUtc is { } reset
                    ? $"重置时间：{reset.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}"
                    : "重置时间未知"))) + "\n点击刷新额度",
            tone);
    }

    private static string FailureLabel(string status) => status.Contains("未登录", StringComparison.Ordinal)
        || status.Contains("登录已失效", StringComparison.Ordinal) ? "未登录 · 上次数据"
        : status.Contains("已断开", StringComparison.Ordinal) || status.Contains("离线", StringComparison.Ordinal)
            ? "离线 · 上次数据"
        : status.Contains("网络", StringComparison.Ordinal) ? "网络失败 · 上次数据"
        : "刷新失败 · 上次数据";

    private static bool IsReliable(QuotaWindowView window) =>
        window.IsAvailable && window.IsPercentageReliable && window.UsedPercent is >= 0 and <= 100;

    public static bool NeedsCountdownUpdate(IReadOnlyList<QuotaWindowView> windows, DateTimeOffset projectedAt, DateTimeOffset now) =>
        projectedAt.UtcTicks / TimeSpan.TicksPerMinute != now.UtcTicks / TimeSpan.TicksPerMinute
        || windows.Any(window => window.ResetAtUtc > projectedAt && window.ResetAtUtc <= now);

    public static DateTimeOffset? NextCountdownUpdateAt(IReadOnlyList<QuotaWindowView> windows, DateTimeOffset now)
    {
        DateTimeOffset? nearestReset = null;
        foreach (var window in windows)
        {
            if (window.ResetAtUtc is { } reset && reset > now
                && (nearestReset is null || reset < nearestReset)) { nearestReset = reset; }
        }
        if (nearestReset is null) { return null; }
        var nextMinuteTicks = (now.UtcTicks / TimeSpan.TicksPerMinute + 1) * TimeSpan.TicksPerMinute;
        return new DateTimeOffset(Math.Min(nextMinuteTicks, nearestReset.Value.UtcTicks), TimeSpan.Zero);
    }

    public static uint CountdownTimerDelayMilliseconds(DateTimeOffset dueAt, DateTimeOffset now) =>
        (uint)Math.Clamp(Math.Ceiling((dueAt - now).TotalMilliseconds), 10d, 60_000d);

    public static string ResetCountdown(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } reset) { return "重置未知"; }
        if (reset <= now) { return "待更新"; }
        var minutes = (long)Math.Ceiling((reset - now).TotalMinutes);
        if (minutes < 60) { return $"{minutes}分后"; }
        if (minutes < 1440)
        {
            return minutes % 60 == 0 ? $"{minutes / 60}时后" : $"{minutes / 60}时{minutes % 60}分后";
        }
        return minutes / 60 % 24 == 0 ? $"{minutes / 1440}天后" : $"{minutes / 1440}天{minutes / 60 % 24}时后";
    }

    private static string WindowName(QuotaWindowView window) => window.Name.Replace("额度", string.Empty, StringComparison.Ordinal).Trim();

    // UsedPercent retains canonical usage even when the main panel's display mode changes.
    private static string Percentage(QuotaWindowView window, bool showRemainingPercent) => IsReliable(window)
        ? $"{(showRemainingPercent ? 100 - window.UsedPercent : window.UsedPercent)}%" : "—";

    private static string FormatWindows(IReadOnlyList<QuotaWindowView> windows, int count, bool showRemainingPercent, DateTimeOffset? now = null)
    {
        var text = string.Join(" · ", windows.Take(count).Select(window =>
        {
            var countdown = now is { } currentTime ? $"（{ResetCountdown(window.ResetAtUtc, currentTime)}）" : string.Empty;
            return $"{WindowName(window)} {Percentage(window, showRemainingPercent)}{countdown}";
        }));
        return windows.Count > count ? $"{text} · +{windows.Count - count}" : text;
    }

    public static bool IsHost(string processName, string packageFamily, string title, string windowClass, bool isToolWindow, bool hasOwner) =>
        !isToolWindow && !hasOwner
        && (processName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("Codex", StringComparison.OrdinalIgnoreCase))
        && packageFamily.Equals(HostPackageFamily, StringComparison.OrdinalIgnoreCase)
        && (title.Equals("ChatGPT", StringComparison.Ordinal) || title.Equals("Codex", StringComparison.Ordinal))
        && windowClass.Equals("Chrome_WidgetWin_1", StringComparison.Ordinal);

    public static Rectangle? Place(Rectangle host, Rectangle workArea, Rectangle? captionButtons, uint dpi, int measuredWidth)
    {
        if (dpi is < 48 or > 768 || measuredWidth <= 0 || host.Width <= 0 || host.Height <= 0)
        {
            return null;
        }

        var scale = dpi / 96d;
        int Pixels(int dips) => (int)Math.Ceiling(dips * scale);
        var left = Math.Max(host.Left + Pixels(LeftReserveDips), workArea.Left);
        var right = Math.Min(host.Right - Pixels(RightReserveDips), workArea.Right);
        if (captionButtons is { Width: > 0, Height: > 0 } buttons)
        {
            right = Math.Min(right, buttons.Left - Pixels(12));
        }

        var top = host.Top + Pixels(TopInsetDips);
        var height = Pixels(HeightDips);
        if (right - left < measuredWidth || top < workArea.Top || top + height > Math.Min(host.Bottom, workArea.Bottom))
        {
            return null;
        }

        var x = Math.Clamp(host.Left + (host.Width - measuredWidth) / 2, left, right - measuredWidth);
        return new Rectangle(x, top, measuredWidth, height);
    }
}
