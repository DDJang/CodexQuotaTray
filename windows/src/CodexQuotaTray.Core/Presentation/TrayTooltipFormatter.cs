using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Presentation;

public static class TrayTooltipFormatter
{
    public static string Create(AppUiState state)
    {
        var quotas = string.Join(
            " · ",
            state.Windows.Take(2).Select(window => $"{window.Name} {window.RemainingPercent}%"));
        return string.IsNullOrWhiteSpace(quotas)
            ? state.StatusText
            : $"{quotas} · {state.StatusText}";
    }
}
