namespace CodexQuotaTray.Core.Models;

public static class QuotaBucketPolicy
{
    public const string CanonicalBucketId = "codex";

    public static bool IsCanonical(string? bucketId) =>
        string.Equals(bucketId?.Trim(), CanonicalBucketId, StringComparison.OrdinalIgnoreCase);

    public static string? CreateSemanticIdentity(string? bucketId, long? durationMinutes)
    {
        if (!IsCanonical(bucketId))
        {
            return null;
        }

        return $"bucket:{CanonicalBucketId}|window:{LogicalWindowKind(durationMinutes)}";
    }

    public static string LogicalWindowKind(long? durationMinutes) => durationMinutes switch
    {
        300 => "five-hour",
        10_080 => "seven-day",
        > 0 and var value => $"minutes:{value}",
        _ => "unknown",
    };
}
