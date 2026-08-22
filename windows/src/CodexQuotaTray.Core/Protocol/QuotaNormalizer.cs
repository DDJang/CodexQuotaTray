using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Protocol;

public sealed record NormalizedQuotaWindow(
    string LocalKey,
    string AlertKey,
    string? LegacyAlertKey,
    string? LimitName,
    string SourceSlot,
    long UsedPercent,
    long RemainingPercent,
    bool PercentageReliable,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc,
    string? BucketId = null);

public sealed record NormalizedQuotaSnapshot(
    IReadOnlyList<NormalizedQuotaWindow> Windows,
    ResetCreditViewState ResetCredits,
    string? PlanType,
    int IssueCount,
    bool ResetCreditsFieldPresent,
    long? AvailableCount,
    int? CreditDetailCount);

public static class QuotaNormalizer
{
    public static NormalizedQuotaSnapshot Normalize(RateLimitsReadResult result)
    {
        var response = result.Response;
        var issues = 0;
        var windows = new List<NormalizedQuotaWindow>();
        var snapshots = response.RateLimitsByLimitId is { Count: > 0 } buckets
            ? buckets.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (Bucket: (string?)entry.Key, Snapshot: entry.Value))
            : response.RateLimits is { } legacy
                ? new[] { (Bucket: (string?)null, Snapshot: legacy) }
                : [];

        var fallbackOrdinal = 0;
        foreach (var entry in snapshots)
        {
            Append(entry.Snapshot.Primary, "primary", entry.Bucket, entry.Snapshot, windows, ref issues, ref fallbackOrdinal);
            Append(entry.Snapshot.Secondary, "secondary", entry.Bucket, entry.Snapshot, windows, ref issues, ref fallbackOrdinal);
        }

        if (windows.Count == 0)
        {
            issues++;
        }

        var planType = response.RateLimitsByLimitId is { Count: > 0 }
            ? snapshots.Select(value => value.Snapshot.PlanType).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            : response.RateLimits?.PlanType;
        var reset = NormalizeResetCredits(response.RateLimitResetCredits, result.ResetCreditsFieldPresent, ref issues);
        return new NormalizedQuotaSnapshot(
            windows,
            reset.State,
            NormalizePlan(planType),
            issues,
            result.ResetCreditsFieldPresent,
            reset.AvailableCount,
            reset.DetailCount);
    }

    public static DateTimeOffset? ParseUnixSeconds(long? value)
    {
        if (value is null or < 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void Append(
        RateLimitWindow? window,
        string slot,
        string? bucket,
        RateLimitSnapshot snapshot,
        List<NormalizedQuotaWindow> output,
        ref int issues,
        ref int fallbackOrdinal)
    {
        if (window is null)
        {
            return;
        }

        if (window.UsedPercent is null)
        {
            issues++;
            return;
        }

        var rawUsed = window.UsedPercent.Value;
        var reliable = rawUsed is >= 0 and <= 100;
        if (!reliable)
        {
            issues++;
        }

        var used = Math.Clamp(rawUsed, 0, 100);
        var ordinal = fallbackOrdinal;
        var localKey = QuotaWindowIdentity.CreateLocalKey(
            snapshot.LimitId,
            bucket,
            slot,
            window.WindowDurationMinutes,
            ordinal);
        var alertKey = QuotaWindowIdentity.CreateAlertKey(
            snapshot.LimitId,
            bucket,
            slot,
            window.WindowDurationMinutes,
            ordinal);
        var legacyAlertKey = QuotaWindowIdentity.CreateLegacyAlertKey(
            snapshot.LimitId,
            bucket,
            slot,
            window.WindowDurationMinutes,
            ordinal);
        fallbackOrdinal++;
        output.Add(new NormalizedQuotaWindow(
            localKey,
            alertKey,
            legacyAlertKey,
            snapshot.LimitName,
            slot,
            used,
            100 - used,
            reliable,
            window.WindowDurationMinutes,
            ParseUnixSeconds(window.ResetsAt),
            bucket ?? QuotaBucketPolicy.CanonicalBucketId));
    }

    private static (ResetCreditViewState State, long? AvailableCount, int? DetailCount) NormalizeResetCredits(
        RateLimitResetCreditsSummary? summary,
        bool fieldPresent,
        ref int issues)
    {
        if (!fieldPresent || summary is null || summary.AvailableCount is null)
        {
            return (new ResetCreditViewState(ResetCreditKind.Unavailable), null, summary?.Credits?.Count);
        }

        var count = summary.AvailableCount.Value;
        if (count < 0)
        {
            issues++;
            count = 0;
        }

        if (count == 0)
        {
            return (new ResetCreditViewState(ResetCreditKind.Empty, 0), 0, summary.Credits?.Count);
        }

        var detailCount = summary.Credits?.Count;
        var expirations = summary.Credits?
            .Select(credit => ParseTimestampElement(credit.ExpiresAt))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Order()
            .ToArray() ?? [];
        if (expirations.Length == 0)
        {
            return (new ResetCreditViewState(ResetCreditKind.CountOnly, ClampCount(count)), count, detailCount);
        }

        var kind = detailCount == count ? ResetCreditKind.CompleteDetails : ResetCreditKind.PartialDetails;
        return (new ResetCreditViewState(kind, ClampCount(count), expirations[0]), count, detailCount);
    }

    private static DateTimeOffset? ParseTimestampElement(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Number } element || !element.TryGetInt64(out var timestamp))
        {
            return null;
        }

        return ParseUnixSeconds(timestamp);
    }

    private static int ClampCount(long count) => (int)Math.Clamp(count, 0, int.MaxValue);

    private static string? NormalizePlan(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : char.ToUpperInvariant(value.Trim()[0]) + value.Trim()[1..].ToLowerInvariant();
}

internal static class QuotaWindowIdentity
{
    public static string CreateLocalKey(
        string? limitId,
        string? bucket,
        string sourceSlot,
        long? durationMinutes,
        int ordinal)
    {
        var identity = !string.IsNullOrWhiteSpace(limitId) ? limitId : bucket;
        return string.IsNullOrWhiteSpace(identity)
            ? CreateFallback(sourceSlot, durationMinutes, ordinal)
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}\n{sourceSlot}"))).ToLowerInvariant();
    }

    public static string CreateAlertKey(
        string? limitId,
        string? bucket,
        string sourceSlot,
        long? durationMinutes,
        int ordinal)
    {
        return !string.IsNullOrWhiteSpace(limitId) || !string.IsNullOrWhiteSpace(bucket)
            ? $"sha256:{CreateLocalKey(limitId, bucket, sourceSlot, durationMinutes, ordinal)}"
            : CreateFallback(sourceSlot, durationMinutes, ordinal);
    }

    public static string? CreateLegacyAlertKey(
        string? limitId,
        string? bucket,
        string sourceSlot,
        long? durationMinutes,
        int ordinal) =>
        string.IsNullOrWhiteSpace(limitId) && !string.IsNullOrWhiteSpace(bucket)
            ? CreateFallback(sourceSlot, durationMinutes, ordinal)
            : null;

    private static string CreateFallback(string sourceSlot, long? durationMinutes, int ordinal) =>
        $"fallback:{sourceSlot}:{durationMinutes?.ToString() ?? "unknown"}:{ordinal}";
}

public sealed class QuotaViewProjector(TimeProvider timeProvider, TimeZoneInfo timeZone)
{
    public AppUiState Project(
        NormalizedQuotaSnapshot snapshot,
        DateTimeOffset receivedAtUtc,
        bool showRemainingPercent = true,
        bool use24HourTime = true)
    {
        var now = timeProvider.GetUtcNow();
        var visibleWindows = snapshot.Windows
            .Where(window => QuotaBucketPolicy.IsCanonical(window.BucketId))
            .ToArray();
        var windows = visibleWindows
            .Select(window => ProjectWindow(window, now, showRemainingPercent, use24HourTime))
            .ToArray();
        var receivedLocal = TimeZoneInfo.ConvertTime(receivedAtUtc, timeZone);
        var nowLocal = TimeZoneInfo.ConvertTime(now, timeZone);
        var tone = snapshot.IssueCount == 0 ? StatusTone.Success : StatusTone.Warning;
        var status = snapshot.IssueCount == 0
            ? receivedLocal.Date == nowLocal.Date
                ? $"更新于 {receivedLocal:HH:mm}"
                : $"更新于 {receivedLocal:MM-dd HH:mm}"
            : "部分额度信息暂不可用";
        var resetCredits = snapshot.ResetCredits.EarliestKnownExpiry is { } expiry
            ? snapshot.ResetCredits with { ExpiryLabel = TimeZoneInfo.ConvertTime(expiry, timeZone).ToString("M月d日") }
            : snapshot.ResetCredits;
        return new AppUiState(
            "Codex",
            visibleWindows.Length == 0 ? null : snapshot.PlanType,
            status,
            tone,
            windows,
            resetCredits,
            IsPrototype: false);
    }

    private QuotaWindowView ProjectWindow(
        NormalizedQuotaWindow window,
        DateTimeOffset now,
        bool showRemainingPercent,
        bool use24HourTime)
    {
        var remaining = (int)window.RemainingPercent;
        return new QuotaWindowView(
            window.LocalKey,
            DisplayName(window.LimitName, window.WindowDurationMinutes),
            (int)window.UsedPercent,
            showRemainingPercent ? remaining : (int)window.UsedPercent,
            showRemainingPercent ? remaining : (int)window.UsedPercent,
            remaining,
            window.WindowDurationMinutes,
            window.ResetAtUtc,
            FormatResetAt(window.ResetAtUtc, use24HourTime),
            FormatRelative(window.ResetAtUtc, now),
            QuotaTonePolicy.For(remaining, false, true),
            window.PercentageReliable);
    }

    private string FormatResetAt(DateTimeOffset? utc, bool use24HourTime) => utc is null
        ? "重置时间未知"
        : TimeZoneInfo.ConvertTime(utc.Value, timeZone).ToString(use24HourTime ? "M月d日 HH:mm" : "M月d日 h:mm tt");

    public static string FormatRelative(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null)
        {
            return "剩余时间未知";
        }

        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "剩余时间未知";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays} 天 {remaining.Hours} 小时后重置";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟后重置";
        }

        return $"{Math.Max(1, remaining.Minutes)} 分钟后重置";
    }

    private static string DisplayName(string? limitName, long? minutes)
    {
        var duration = minutes switch
        {
            300 => "5 小时额度",
            10_080 => "7 天额度",
            > 0 and var value when value % 1_440 == 0 => $"{value / 1_440} 天额度",
            > 0 and var value when value % 60 == 0 => $"{value / 60} 小时额度",
            > 0 and var value => $"{value} 分钟额度",
            _ => "额度窗口",
        };
        return string.IsNullOrWhiteSpace(limitName) || string.Equals(limitName.Trim(), "Codex", StringComparison.OrdinalIgnoreCase)
            ? duration
            : $"{limitName.Trim()} · {duration}";
    }
}
