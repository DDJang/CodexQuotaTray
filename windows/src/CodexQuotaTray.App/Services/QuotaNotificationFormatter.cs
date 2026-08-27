using System.Globalization;
using CodexQuotaTray.Core.Alerts;

namespace CodexQuotaTray.App.Services;

internal sealed record QuotaNotificationContent(string Title, string Body);

internal static class QuotaNotificationFormatter
{
    internal static QuotaNotificationContent Format(QuotaAlert alert)
    {
        var title = alert.Kind == QuotaAlertKind.ResetCreditExpiry
            ? ResetCreditExpiryTitle()
            : "Codex 额度提醒";
        var quotaMessage = alert.Kind switch
        {
            QuotaAlertKind.Reset => FormatResetAlert(alert.ResetWindows),
            QuotaAlertKind.ResetCreditExpiry => FormatResetCreditExpiryAlert(alert.ResetCreditExpiryWindows),
            QuotaAlertKind.Composite => FormatCompositeAlert(alert),
            _ => alert.ThresholdWindows.Count > 0
                ? FormatThresholdAlert(alert.ThresholdWindows)
                : $"{alert.WindowName}剩余 {FormatRemaining(alert.RemainingPercent)}",
        };
        var body = quotaMessage
            + (alert.ResetCreditExpiryWindows.Count > 0
                && alert.Kind is not (QuotaAlertKind.Composite or QuotaAlertKind.ResetCreditExpiry)
                ? Environment.NewLine + FormatResetCreditExpiryAlert(alert.ResetCreditExpiryWindows)
                : string.Empty);
        return new QuotaNotificationContent(title, body);
    }

    private static string FormatResetAlert(IReadOnlyList<QuotaResetWindow> windows) =>
        $"{string.Join("、", windows.Select(window => $"{window.WindowName}已重置"))}。当前剩余 "
        + $"{string.Join("、", windows.Select(window => FormatRemaining(window.RemainingPercent)))}，下次重置时间为 "
        + $"{string.Join("、", windows.Select(window => window.ResetAtUtc?.ToLocalTime().ToString("M月d日 HH:mm") ?? "未知"))}。";

    private static string FormatThresholdAlert(IReadOnlyList<QuotaThresholdWindow> windows)
    {
        if (windows.Count == 0)
        {
            return string.Empty;
        }

        if (windows.Count == 1)
        {
            var window = windows[0];
            return $"{window.WindowName}剩余 {window.RemainingPercent}%";
        }

        return string.Join(
            Environment.NewLine,
            windows.Select(window => $"{window.WindowName}剩余 {window.RemainingPercent}%"));
    }

    private static string FormatCompositeAlert(QuotaAlert alert) =>
        FormatThresholdAlert(alert.ThresholdWindows)
        + Environment.NewLine
        + FormatResetAlert(alert.ResetWindows)
        + (alert.ResetCreditExpiryWindows.Count == 0
            ? string.Empty
            : Environment.NewLine + FormatResetCreditExpiryAlert(alert.ResetCreditExpiryWindows));

    private static string FormatResetCreditExpiryAlert(IReadOnlyList<QuotaResetCreditExpiry> credits)
    {
        if (credits.Count == 0)
        {
            return string.Empty;
        }

        var earliest = credits.OrderBy(credit => credit.ExpiresAtUtc).First().ExpiresAtUtc
            .ToLocalTime()
            .ToString("MM-dd HH:mm");
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return credits.Count == 1
                ? $"1 available reset credit expires at {earliest}"
                : $"{credits.Count} available reset credits expiring · earliest {earliest}";
        }

        return credits.Count == 1
            ? $"1 张可用重置卡将在 {earliest} 到期"
            : $"{credits.Count} 张可用重置卡即将到期 · 最早 {earliest}";
    }

    private static string ResetCreditExpiryTitle() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? "Reset credit expiring"
            : "重置卡即将到期";

    private static string FormatRemaining(int? remainingPercent) =>
        remainingPercent is { } remaining ? $"{remaining}%" : "未知";
}
