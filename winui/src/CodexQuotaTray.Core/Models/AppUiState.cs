namespace CodexQuotaTray.Core.Models;

public sealed record AppUiState(
    string Title,
    string? PlanBadge,
    string StatusText,
    StatusTone StatusTone,
    IReadOnlyList<QuotaWindowView> Windows,
    ResetCreditViewState ResetCredits,
    bool IsRefreshing = false,
    bool IsPrototype = true);
