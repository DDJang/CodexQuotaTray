using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.TokenUsage;

public static class AccountTokenUsageNormalizer
{
    public static TokenUsageSnapshot Normalize(
        AccountUsageReadResult input,
        TokenUsageDataSource source,
        DateOnly? today = null,
        DateTimeOffset? generatedAtUtc = null,
        TimeZoneInfo? timeZone = null)
    {
        var localToday = today ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(generatedAtUtc ?? DateTimeOffset.UtcNow, timeZone ?? TimeZoneInfo.Local).DateTime);
        var days = input.DailyUsageBuckets is null
            ? Array.Empty<TokenUsageDay>()
            : input.DailyUsageBuckets
                .Where(bucket => bucket.StartDate is not null && bucket.Tokens is >= 0)
                .GroupBy(bucket => bucket.StartDate!.Value)
                .Select(group => new TokenUsageDay(
                    group.Key,
                    SaturatingSum(group.Select(bucket => bucket.Tokens!.Value)),
                    null,
                    null,
                    null,
                    null))
                .OrderBy(day => day.Date)
                .ToArray();

        var byDate = days.ToDictionary(day => day.Date);
        var summary = input.Summary;
        var availability = TokenUsageMetricAvailability.None;
        var todayTokens = 0L;
        var last7Tokens = 0L;
        var last30Tokens = 0L;
        if (input.DailyUsageBuckets is not null)
        {
            availability |= TokenUsageMetricAvailability.Today
                | TokenUsageMetricAvailability.Last7Days
                | TokenUsageMetricAvailability.Last30Days;
            todayTokens = byDate.TryGetValue(localToday, out var todayDay) ? todayDay.TotalTokens : 0;
            last7Tokens = SaturatingSum(days.Where(day => day.Date >= localToday.AddDays(-6) && day.Date <= localToday).Select(day => day.TotalTokens));
            last30Tokens = SaturatingSum(days.Where(day => day.Date >= localToday.AddDays(-29) && day.Date <= localToday).Select(day => day.TotalTokens));
        }

        var lifetime = NonNegative(summary?.LifetimeTokens, ref availability, TokenUsageMetricAvailability.Lifetime);
        var peak = NonNegative(summary?.PeakDailyTokens, ref availability, TokenUsageMetricAvailability.Peak);
        var peakDate = days.OrderByDescending(day => day.TotalTokens).ThenByDescending(day => day.Date).FirstOrDefault()?.Date;
        if (!HasMetric(availability, TokenUsageMetricAvailability.Peak) && input.DailyUsageBuckets is not null)
        {
            peak = days.Length == 0 ? 0 : days.Max(day => day.TotalTokens);
            availability |= TokenUsageMetricAvailability.Peak;
        }

        var currentStreak = (int)Math.Clamp(
            NonNegative(summary?.CurrentStreakDays, ref availability, TokenUsageMetricAvailability.CurrentStreak),
            0,
            int.MaxValue);
        var longestStreak = (int)Math.Clamp(
            NonNegative(summary?.LongestStreakDays, ref availability, TokenUsageMetricAvailability.LongestStreak),
            0,
            int.MaxValue);
        var activeDays = days.Count(day => day.TotalTokens > 0);
        var now = generatedAtUtc ?? DateTimeOffset.UtcNow;
        return new TokenUsageSnapshot(
            SchemaVersion: 1,
            GeneratedAtUtc: now,
            SourceTimeZone: (timeZone ?? TimeZoneInfo.Local).Id,
            Summary: new TokenUsageSummary(
                todayTokens,
                last7Tokens,
                last30Tokens,
                lifetime,
                peak,
                peakDate,
                activeDays,
                currentStreak,
                longestStreak),
            Days: days,
            FilesScanned: 0,
            ScanElapsedMilliseconds: 0,
            FirstActivityDate: days.FirstOrDefault()?.Date,
            LastActivityDate: days.LastOrDefault()?.Date,
            Source: source,
            AvailableMetrics: availability,
            LongestRunningTurnSeconds: NonNegativeValue(summary?.LongestRunningTurnSec));
    }

    private static long NonNegative(
        long? value,
        ref TokenUsageMetricAvailability availability,
        TokenUsageMetricAvailability metric)
    {
        if (value is >= 0)
        {
            availability |= metric;
            return value.Value;
        }

        return 0;
    }

    private static long? NonNegativeValue(long? value) => value is >= 0 ? value : null;

    private static bool HasMetric(TokenUsageMetricAvailability value, TokenUsageMetricAvailability metric) =>
        (value & metric) == metric;

    private static long SaturatingSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            if (long.MaxValue - total < value)
            {
                return long.MaxValue;
            }

            total += value;
        }

        return total;
    }
}

public sealed class TokenUsageSourceResolver(
    Func<TokenUsageDataSource> source,
    TokenUsageScanner localScanner,
    Func<CancellationToken, Task<AccountUsageReadResult>> readCodexCliAsync,
    Func<CancellationToken, Task<AccountUsageReadResult>> readOAuthAsync)
{
    public TokenUsageDataSource Source => source();

    public async Task<TokenUsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var selected = Source;
        return selected switch
        {
            TokenUsageDataSource.Local => (await localScanner.ScanAsync(cancellationToken: cancellationToken).ConfigureAwait(false)) with
            {
                Source = TokenUsageDataSource.Local,
                AvailableMetrics = TokenUsageMetricAvailability.All,
            },
            TokenUsageDataSource.CodexCli => AccountTokenUsageNormalizer.Normalize(
                await readCodexCliAsync(cancellationToken).ConfigureAwait(false),
                TokenUsageDataSource.CodexCli),
            TokenUsageDataSource.OAuth => AccountTokenUsageNormalizer.Normalize(
                await readOAuthAsync(cancellationToken).ConfigureAwait(false),
                TokenUsageDataSource.OAuth),
            _ => throw new InvalidOperationException("Unknown token usage data source."),
        };
    }
}
