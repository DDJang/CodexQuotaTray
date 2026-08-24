using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Core.TokenUsage;

[Flags]
public enum TokenUsageMetricAvailability
{
    None = 0,
    Today = 1,
    Last7Days = 2,
    Last30Days = 4,
    Lifetime = 8,
    Peak = 16,
    CurrentStreak = 32,
    LongestStreak = 64,
    All = Today | Last7Days | Last30Days | Lifetime | Peak | CurrentStreak | LongestStreak,
}

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
    DateOnly? LastActivityDate,
    TokenUsageDataSource Source = TokenUsageDataSource.Local,
    TokenUsageMetricAvailability AvailableMetrics = TokenUsageMetricAvailability.All,
    long? LongestRunningTurnSeconds = null);
