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
    IReadOnlyList<QuotaLanWindow> Windows);

public sealed record QuotaLanWindow(
    string? LimitId,
    string? LimitName,
    string? PlanType,
    string SourceSlot,
    long? UsedPercent,
    long? RemainingPercent,
    bool? PercentageReliable,
    long? WindowDurationMins,
    long? ResetsAt);
