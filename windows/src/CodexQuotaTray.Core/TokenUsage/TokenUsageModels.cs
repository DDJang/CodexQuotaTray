namespace CodexQuotaTray.Core.TokenUsage;

public sealed record TokenUsageDay(
    DateOnly Date,
    long TotalTokens,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens);

public sealed record TokenUsageSummary(
    long TodayTokens,
    long Last7DaysTokens,
    long Last30DaysTokens,
    long LifetimeTokens,
    long PeakDailyTokens,
    DateOnly? PeakDate,
    int ActiveDays,
    int CurrentStreak,
    int LongestStreak);

public sealed record TokenUsageSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string SourceTimeZone,
    TokenUsageSummary Summary,
    IReadOnlyList<TokenUsageDay> Days,
    int FilesScanned,
    long ScanElapsedMilliseconds,
    DateOnly? FirstActivityDate,
    DateOnly? LastActivityDate);
