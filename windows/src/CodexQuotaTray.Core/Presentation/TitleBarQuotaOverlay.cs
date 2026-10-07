using System.Drawing;
using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public sealed record TitleBarQuotaPresentation(string Text, string CompactText, QuotaTone Tone);

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

    public static TitleBarQuotaPresentation Project(AppUiState state, bool showRemainingPercent = true)
    {
        var status = state.IsRefreshing ? "刷新中…"
            : state.Windows.Count == 0 ? state.StatusText
            : state.StatusTone == StatusTone.Error ? FailureLabel(state.StatusText)
            : state.Windows.Any(window => window.IsStale) || state.StatusText.Contains("已过期", StringComparison.Ordinal)
                ? "已过期"
            : state.StatusTone == StatusTone.Warning ? "部分不可用"
            : string.Empty;
        var prefix = string.IsNullOrWhiteSpace(status) ? string.Empty : $"{status} · ";

        var full = FormatWindows(state.Windows, 2, showRemainingPercent);
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
            compact.Length == 0 ? status : $"{prefix}{label} {compact}",
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

    private static string FormatWindows(IReadOnlyList<QuotaWindowView> windows, int count, bool showRemainingPercent)
    {
        var text = string.Join(" · ", windows.Take(count).Select(window =>
        {
            // The main panel's percentage mode also affects RemainingPercent in its
            // current projection. UsedPercent retains the actual normalized usage.
            var percentage = IsReliable(window)
                ? $"{(showRemainingPercent ? 100 - window.UsedPercent : window.UsedPercent)}%" : "—";
            var name = window.Name.Replace("额度", string.Empty, StringComparison.Ordinal).Trim();
            return $"{name} {percentage}";
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
