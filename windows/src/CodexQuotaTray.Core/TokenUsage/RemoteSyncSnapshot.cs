namespace CodexQuotaTray.Core.TokenUsage;

public sealed record RemoteSyncSnapshot(int SchemaVersion, QuotaLanSnapshot? Quota, TokenUsageLanSnapshot? TokenUsage)
{
    public const int MaximumBytes = 256 * 1024;

    public static RemoteSyncSnapshot Capture(QuotaLanSnapshot? quota, TokenUsageSnapshot? tokenUsage) =>
        new(1, ProjectQuota(quota), tokenUsage is null ? null : TokenUsageLanSnapshot.From(tokenUsage));

    public static QuotaLanSnapshot? ProjectQuota(QuotaLanSnapshot? quota) =>
        quota?.ResetCredits is not { } resetCredits ? quota : quota with
        {
            ResetCredits = resetCredits with
            {
                Credits = resetCredits.Credits?
                    .Where(credit => string.Equals(credit?.Status?.Trim(), "available", StringComparison.OrdinalIgnoreCase))
                    .Select(credit => new QuotaLanResetCredit(
                        Id: null,
                        ResetType: null,
                        Status: "available",
                        GrantedAt: null,
                        ExpiresAt: NormalizeExpiry(credit.ExpiresAt),
                        Title: null,
                        Description: null))
                    .ToArray(),
            },
        };

    private static long? NormalizeExpiry(long? expiresAt) =>
        expiresAt > 0 && expiresAt <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? expiresAt : null;
}
