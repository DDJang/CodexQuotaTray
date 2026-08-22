namespace CodexQuotaTray.Core.Models;

public sealed record ResetCreditViewState(
    ResetCreditKind Kind,
    int? AvailableCount = null,
    DateTimeOffset? EarliestKnownExpiry = null,
    string? ExpiryLabel = null,
    IReadOnlyList<ResetCreditView>? Credits = null)
{
    public string Summary => Kind switch
    {
        ResetCreditKind.Unavailable => "当前账户未提供重置卡信息",
        ResetCreditKind.Empty => "可用重置卡 0 张",
        ResetCreditKind.CountOnly => $"重置卡 {AvailableCount ?? 0} 张 · 到期时间未提供",
        ResetCreditKind.PartialDetails =>
            $"重置卡 {AvailableCount ?? 0} 张 · 最近已知 {FormatExpiry(EarliestKnownExpiry)}到期",
        ResetCreditKind.CompleteDetails =>
            $"重置卡 {AvailableCount ?? 0} 张 · 最早 {FormatExpiry(EarliestKnownExpiry)}到期",
        _ => "当前账户未提供重置卡信息",
    };

    private string FormatExpiry(DateTimeOffset? value) =>
        ExpiryLabel is { Length: > 0 } label ? $"{label} " : value is null ? "未知日期" : $"{value:M月d日} ";
}

public sealed record ResetCreditView(
    string? Id,
    string? ResetType,
    string? Status,
    DateTimeOffset? GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Title,
    string? Description);
