namespace CodexQuotaTray.Core.TokenUsage;

/// <summary>
/// The deliberately small, read-only quota projection exposed to a paired phone.
/// It is a normalized snapshot, never an App Server response or account record.
/// </summary>
public sealed record QuotaLanSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string? PlanType,
    string QuotaState,
    IReadOnlyList<QuotaLanWindow> Windows,
    QuotaLanResetCredits? ResetCredits = null);

public sealed record QuotaLanWindow(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    string SourceSlot,
    long? UsedPercent,
    long? RemainingPercent,
    bool? PercentageReliable,
    long? WindowDurationMins,
    long? ResetsAt,
    string? BucketId = null);

/// <summary>
/// Read-only reset-credit projection for the paired Android client. A null
/// Credits list means details were unavailable; an empty list means the
/// detail read succeeded and returned no items.
/// </summary>
public sealed record QuotaLanResetCredits(
    long? AvailableCount,
    IReadOnlyList<QuotaLanResetCredit>? Credits = null);

public sealed record QuotaLanResetCredit(
    string? Id,
    string? ResetType,
    string? Status,
    long? GrantedAt,
    long? ExpiresAt,
    string? Title,
    string? Description);
