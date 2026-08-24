using System.Diagnostics;

namespace CodexQuotaTray.Core.Presentation;

public static class RefreshPresentationPolicy
{
    public static readonly TimeSpan MinimumIndicatorDuration = TimeSpan.FromMilliseconds(700);

    public static TimeSpan Remaining(long startedTimestamp)
    {
        var remaining = MinimumIndicatorDuration - Stopwatch.GetElapsedTime(startedTimestamp);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static async Task WaitForMinimumAsync(
        long startedTimestamp,
        CancellationToken cancellationToken = default)
    {
        var remaining = Remaining(startedTimestamp);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
        }
    }
}
