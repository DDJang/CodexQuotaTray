using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public static class TrayTooltipFormatter
{
    public static string Create(string baseName, AppUiState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        var quotas = string.Join(
            " · ",
            state.Windows.Take(2).Select(window => $"{window.Name} {window.RemainingPercent}%"));
        return string.IsNullOrWhiteSpace(quotas)
            ? $"{baseName} · {state.StatusText}"
            : $"{baseName} · {quotas} · {state.StatusText}";
    }
}
