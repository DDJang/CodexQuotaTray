namespace CodexQuotaTray.Core.Protocol;

public sealed record SnapshotMergeResult(RateLimitsReadResult? Snapshot, bool RequiresFullRead);

public static class RateLimitsSnapshotMerger
{
    public static SnapshotMergeResult Merge(RateLimitsReadResult? baseline, RateLimitsUpdatedNotification update)
    {
        if (baseline is null)
        {
            var complete = HasIndependentSnapshot(update.Response);
            return complete
                ? new SnapshotMergeResult(new RateLimitsReadResult(update.Response, update.ResetCreditsFieldPresent), false)
                : new SnapshotMergeResult(null, true);
        }

        var merged = new RateLimitsResponse
        {
            RateLimits = MergeSnapshot(baseline.Response.RateLimits, update.Response.RateLimits),
            RateLimitsByLimitId = MergeBuckets(baseline.Response.RateLimitsByLimitId, update.Response.RateLimitsByLimitId),
            RateLimitResetCredits = update.ResetCreditsFieldPresent
                ? update.Response.RateLimitResetCredits
                : baseline.Response.RateLimitResetCredits,
        };
        return new SnapshotMergeResult(
            new RateLimitsReadResult(merged, baseline.ResetCreditsFieldPresent || update.ResetCreditsFieldPresent),
            false);
    }

    private static bool HasIndependentSnapshot(RateLimitsResponse value) =>
        value.RateLimits is not null || value.RateLimitsByLimitId is { Count: > 0 };

    private static Dictionary<string, RateLimitSnapshot>? MergeBuckets(
        Dictionary<string, RateLimitSnapshot>? baseline,
        Dictionary<string, RateLimitSnapshot>? patch)
    {
        if (patch is null)
        {
            return baseline;
        }

        var merged = baseline is null
            ? new Dictionary<string, RateLimitSnapshot>(StringComparer.Ordinal)
            : new Dictionary<string, RateLimitSnapshot>(baseline, StringComparer.Ordinal);
        foreach (var (key, value) in patch)
        {
            merged.TryGetValue(key, out var old);
            merged[key] = MergeSnapshot(old, value)!;
        }

        return merged;
    }

    private static RateLimitSnapshot? MergeSnapshot(RateLimitSnapshot? baseline, RateLimitSnapshot? patch)
    {
        if (patch is null)
        {
            return baseline;
        }

        return new RateLimitSnapshot
        {
            LimitId = patch.LimitId ?? baseline?.LimitId,
            LimitName = patch.LimitName ?? baseline?.LimitName,
            PlanType = patch.PlanType ?? baseline?.PlanType,
            Primary = MergeWindow(baseline?.Primary, patch.Primary),
            Secondary = MergeWindow(baseline?.Secondary, patch.Secondary),
        };
    }

    private static RateLimitWindow? MergeWindow(RateLimitWindow? baseline, RateLimitWindow? patch)
    {
        if (patch is null)
        {
            return baseline;
        }

        return new RateLimitWindow
        {
            UsedPercent = patch.UsedPercent ?? baseline?.UsedPercent,
            WindowDurationMinutes = patch.WindowDurationMinutes ?? baseline?.WindowDurationMinutes,
            ResetsAt = patch.ResetsAt ?? baseline?.ResetsAt,
        };
    }
}
