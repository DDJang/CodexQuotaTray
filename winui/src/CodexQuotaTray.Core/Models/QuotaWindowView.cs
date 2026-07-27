namespace CodexQuotaTray.Core.Models;

public sealed record QuotaWindowView(
    string LocalKey,
    string Name,
    int UsedPercent,
    int RemainingPercent,
    int DisplayPercent,
    int ProgressValue,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc,
    string ResetAt,
    string ResetRelative,
    QuotaTone Tone,
    bool IsPercentageReliable,
    bool IsAvailable = true,
    bool IsStale = false)
{
    public string PercentText => $"{DisplayPercent}%";

    public static QuotaWindowView Demo(
        string name,
        int remainingPercent,
        string resetRelative,
        string resetAt) =>
        new(
            name,
            name,
            100 - remainingPercent,
            remainingPercent,
            remainingPercent,
            remainingPercent,
            null,
            null,
            resetAt,
            resetRelative,
            QuotaTonePolicy.For(remainingPercent, false, true),
            true);
}

public static class QuotaTonePolicy
{
    public static QuotaTone For(int remainingPercent, bool isStale, bool isAvailable)
    {
        if (!isAvailable || isStale || remainingPercent is < 0 or > 100)
        {
            return QuotaTone.Unavailable;
        }

        return remainingPercent switch
        {
            <= 10 => QuotaTone.Critical,
            <= 20 => QuotaTone.Warning,
            _ => QuotaTone.Accent,
        };
    }
}
