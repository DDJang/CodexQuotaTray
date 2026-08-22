namespace CodexQuotaTray.Core.Models;

public static class QuotaBucketPolicy
{
    public const string CanonicalBucketId = "codex";

    public static bool IsCanonical(string? bucketId) =>
        string.Equals(bucketId?.Trim(), CanonicalBucketId, StringComparison.OrdinalIgnoreCase);
}
