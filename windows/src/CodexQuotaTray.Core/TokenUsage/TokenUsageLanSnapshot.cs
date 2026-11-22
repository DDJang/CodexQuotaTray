using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Core.TokenUsage;

public sealed record TokenUsageLanSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string SourceTimeZone,
    string Source,
    string Scope,
    TokenUsageLanSummary Summary,
    IReadOnlyList<TokenUsageDay> Days)
{
    public static TokenUsageLanSnapshot From(TokenUsageSnapshot snapshot)
    {
        var summary = snapshot.Summary;
        var available = snapshot.AvailableMetrics;
        var source = snapshot.Source switch
        {
            TokenUsageDataSource.CodexCli => "CodexCli",
            TokenUsageDataSource.OAuth => "OAuth",
            _ => "Local",
        };
        return new TokenUsageLanSnapshot(
            snapshot.SchemaVersion,
            snapshot.GeneratedAtUtc,
            snapshot.SourceTimeZone,
            source,
            snapshot.Source == TokenUsageDataSource.Local ? "Local" : "Account",
            new TokenUsageLanSummary(
                available.HasFlag(TokenUsageMetricAvailability.Today) ? summary.TodayTokens : null,
                available.HasFlag(TokenUsageMetricAvailability.Last7Days) ? summary.Last7DaysTokens : null,
                available.HasFlag(TokenUsageMetricAvailability.Last30Days) ? summary.Last30DaysTokens : null,
                available.HasFlag(TokenUsageMetricAvailability.Lifetime) ? summary.LifetimeTokens : null,
                available.HasFlag(TokenUsageMetricAvailability.Peak) ? summary.PeakDailyTokens : null,
                available.HasFlag(TokenUsageMetricAvailability.Peak) ? summary.PeakDate : null,
                summary.ActiveDays,
                available.HasFlag(TokenUsageMetricAvailability.CurrentStreak) ? summary.CurrentStreak : null,
                available.HasFlag(TokenUsageMetricAvailability.LongestStreak) ? summary.LongestStreak : null),
            snapshot.Days);
    }
}

public sealed record TokenUsageLanSummary(
    long? TodayTokens,
    long? Last7DaysTokens,
    long? Last30DaysTokens,
    long? LifetimeTokens,
    long? PeakDailyTokens,
    DateOnly? PeakDate,
    int ActiveDays,
    int? CurrentStreak,
    int? LongestStreak);
