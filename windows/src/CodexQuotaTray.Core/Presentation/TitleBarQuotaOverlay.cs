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

    public static TitleBarQuotaPresentation Project(AppUiState state, bool showRemainingPercent = true, DateTimeOffset? now = null, TimeZoneInfo? timeZone = null)
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
            FormatTooltip(state, showRemainingPercent, currentTime, timeZone ?? TimeZoneInfo.Local, status),
            tone);
    }

    private static string FormatTooltip(AppUiState state, bool remaining, DateTimeOffset now, TimeZoneInfo timeZone, string status)
    {
        var lines = new List<string>();
        if (status.Length > 0) { lines.Add(status); }
        foreach (var window in state.Windows)
        {
            var reset = DisplayCountdown(window.ResetAtUtc, now).Replace("时", "小时", StringComparison.Ordinal);
            var absolute = window.ResetAtUtc is { } at ? $"（{LocalTime(at, timeZone)}）" : string.Empty;
            lines.Add($"{WindowName(window)}：{(remaining ? "剩余" : "已用")} {Percentage(window, remaining)} · 重置：{reset}{absolute}");
        }
        if (lines.Count > 0) { lines.Add(string.Empty); }
        var credits = state.ResetCredits;
        if (credits.Kind == ResetCreditKind.Unavailable || credits.AvailableCount is null or < 0)
        {
            lines.Add("重置卡信息未提供");
        }
        else
        {
            var count = credits.AvailableCount.Value;
            lines.Add($"重置卡：{count} 张");
            var available = AvailableCredits(credits).ToArray();
            var shown = Math.Min(5, available.Length);
            for (var index = 0; index < shown; index++)
            {
                var expiry = available[index].ExpiresAtUtc;
                var detail = expiry is { } at
                    ? $"{LocalTime(at, timeZone)} 到期 · {CreditRemaining(at, now)}"
                    : "到期时间未知";
                lines.Add($"第{index + 1}张：{detail}");
            }
            if (available.Length > shown) { lines.Add($"…另有{available.Length - shown}张未显示"); }
            if (count > available.Length)
            {
                lines.Add(available.Length == 0 ? "到期信息未提供" : $"其余{count - available.Length}张的到期信息未提供");
            }
        }
        lines.Add(string.Empty);
        lines.Add("点击ChatGPT标题栏刷新");
        return string.Join("\n", lines);
    }

    private static string LocalTime(DateTimeOffset at, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(at, timeZone).ToString("M月d日 HH:mm", CultureInfo.InvariantCulture);

    private static IEnumerable<ResetCreditView> AvailableCredits(ResetCreditViewState? state) =>
        state is { Kind: not ResetCreditKind.Unavailable, AvailableCount: > 0, Credits: { } credits }
            ? credits.Where(credit => string.Equals(credit.Status?.Trim(), "available", StringComparison.OrdinalIgnoreCase))
                .OrderBy(credit => credit.ExpiresAtUtc ?? DateTimeOffset.MaxValue).Take(state.AvailableCount.Value)
            : [];

    private static string CreditRemaining(DateTimeOffset expiry, DateTimeOffset now)
    {
        if (expiry <= now) { return "已到期"; }
        var minutes = DisplayMinutes(expiry, now);
        return minutes >= 1440 ? $"剩余{minutes / 1440}天{minutes / 60 % 24}小时"
            : minutes >= 60 ? $"剩余{minutes / 60}小时{minutes % 60}分" : $"剩余{minutes}分";
    }

    // Quantize both timestamps, so scheduled ticks and snapshots agree within a minute.
    private static long DisplayMinutes(DateTimeOffset at, DateTimeOffset now) =>
        Math.Max(1, at.UtcTicks / TimeSpan.TicksPerMinute - now.UtcTicks / TimeSpan.TicksPerMinute);

    private static string DisplayCountdown(DateTimeOffset? resetAt, DateTimeOffset now) =>
        resetAt is { } reset && reset > now ? FormatCountdown(DisplayMinutes(reset, now)) : ResetCountdown(resetAt, now);

    private static string FailureLabel(string status) => status.Contains("未登录", StringComparison.Ordinal)
        || status.Contains("登录已失效", StringComparison.Ordinal) ? "未登录 · 上次数据"
        : status.Contains("已断开", StringComparison.Ordinal) || status.Contains("离线", StringComparison.Ordinal)
            ? "离线 · 上次数据"
        : status.Contains("网络", StringComparison.Ordinal) ? "网络失败 · 上次数据"
        : "刷新失败 · 上次数据";

    private static bool IsReliable(QuotaWindowView window) =>
        window.IsAvailable && window.IsPercentageReliable && window.UsedPercent is >= 0 and <= 100;

    public static bool NeedsCountdownUpdate(IReadOnlyList<QuotaWindowView> windows, DateTimeOffset projectedAt, DateTimeOffset now, ResetCreditViewState? resetCredits = null) =>
        projectedAt.UtcTicks / TimeSpan.TicksPerMinute != now.UtcTicks / TimeSpan.TicksPerMinute
        || windows.Any(window => window.ResetAtUtc > projectedAt && window.ResetAtUtc <= now)
        || AvailableCredits(resetCredits).Take(5).Any(credit => credit.ExpiresAtUtc > projectedAt && credit.ExpiresAtUtc <= now);

    public static DateTimeOffset? NextCountdownUpdateAt(IReadOnlyList<QuotaWindowView> windows, DateTimeOffset now, ResetCreditViewState? resetCredits = null)
    {
        DateTimeOffset? nearestReset = null;
        foreach (var window in windows)
        {
            if (window.ResetAtUtc is { } reset && reset > now
                && (nearestReset is null || reset < nearestReset)) { nearestReset = reset; }
        }
        foreach (var credit in AvailableCredits(resetCredits).Take(5))
        {
            if (credit.ExpiresAtUtc is { } expiry && expiry > now
                && (nearestReset is null || expiry < nearestReset)) { nearestReset = expiry; }
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
        return FormatCountdown((long)Math.Ceiling((reset - now).TotalMinutes));
    }

    private static string FormatCountdown(long minutes)
    {
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
            var countdown = now is { } currentTime ? $"（{DisplayCountdown(window.ResetAtUtc, currentTime)}）" : string.Empty;
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
