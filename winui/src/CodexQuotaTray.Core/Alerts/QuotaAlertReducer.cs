using System.Security.Cryptography;
using System.Text;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Core.Alerts;

public enum QuotaAlertKind
{
    Threshold,
    Reset,
}

public sealed record QuotaResetWindow(
    string WindowName,
    int RemainingPercent,
    DateTimeOffset ResetAtUtc);

public sealed record QuotaAlert(string WindowName, int RemainingPercent, int Threshold)
{
    public QuotaAlertKind Kind { get; init; } = QuotaAlertKind.Threshold;

    public IReadOnlyList<QuotaResetWindow> ResetWindows { get; init; } = [];

    public static QuotaAlert ForReset(IReadOnlyList<QuotaResetWindow> windows)
    {
        if (windows.Count == 0)
        {
            throw new ArgumentException("At least one reset window is required.", nameof(windows));
        }

        var first = windows[0];
        return new QuotaAlert(
            string.Join("、", windows.Select(window => window.WindowName)),
            first.RemainingPercent,
            0)
        {
            Kind = QuotaAlertKind.Reset,
            ResetWindows = windows,
        };
    }
}

public sealed record AlertReduction(AlertStateDocument State, QuotaAlert? Alert);

public static class AlertWindowIdentity
{
    public static string Create(string? limitId, string sourceSlot, long? durationMinutes, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(limitId))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(limitId));
            return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
        }

        return $"fallback:{sourceSlot}:{durationMinutes?.ToString() ?? "unknown"}:{ordinal}";
    }
}

public static class QuotaAlertReducer
{
    private static readonly int[] AllThresholds = [50, 20, 10];

    public static AlertReduction Reduce(
        AlertStateDocument? previous,
        IReadOnlyList<AlertInput> windows,
        NotificationSettings settings)
    {
        var enabled = Enabled(settings).ToArray();
        var baseline = previous is null || previous.SchemaVersion != 1;
        var resetBaseline = baseline || previous?.ResetAlertBaselineEstablished != true;
        var oldWindows = baseline ? new Dictionary<string, AlertWindowState>() : previous!.Windows;
        var output = new Dictionary<string, AlertWindowState>(StringComparer.Ordinal);
        var resetAlerts = new List<QuotaResetWindow>();
        QuotaAlert? selected = null;
        var hasValidWindow = false;

        foreach (var input in windows)
        {
            oldWindows.TryGetValue(input.PseudonymousKey, out var old);
            if (!input.IsPercentageReliable || input.RemainingPercent is < 0 or > 100)
            {
                if (old is not null)
                {
                    output[input.PseudonymousKey] = old;
                }

                continue;
            }

            hasValidWindow = true;
            var handled = old?.HandledThresholds.ToHashSet() ?? [];
            var newCycle = old is not null && IsNewCycle(old, input);
            var resetCycle = !resetBaseline && old is not null && IsResetCycle(old, input);
            if (newCycle)
            {
                handled.Clear();
            }

            var resetAlertCycle = old?.LastResetAlertCycleUtc;
            if (resetCycle && input.ResetAtUtc is { } resetAt && resetAlertCycle != resetAt)
            {
                if (settings.ResetAfterCycle)
                {
                    resetAlerts.Add(new QuotaResetWindow(input.WindowName, input.RemainingPercent, resetAt));
                }

                resetAlertCycle = resetAt;
            }

            var newlyEnabled = enabled.Except(previous?.BaselineThresholds ?? []).ToArray();
            foreach (var threshold in newlyEnabled)
            {
                if (input.RemainingPercent <= threshold)
                {
                    handled.Add(threshold);
                }
            }

            var crossed = baseline || old?.LastReliableRemaining is null || newCycle
                ? []
                : enabled.Where(threshold =>
                    !handled.Contains(threshold)
                    && old.LastReliableRemaining > threshold
                    && input.RemainingPercent <= threshold).ToArray();
            foreach (var threshold in crossed)
            {
                handled.Add(threshold);
            }

            if (crossed.Length > 0)
            {
                var urgent = crossed.Min();
                if (selected is null || urgent < selected.Threshold)
                {
                    selected = new QuotaAlert(input.WindowName, input.RemainingPercent, urgent);
                }
            }

            output[input.PseudonymousKey] = new AlertWindowState(
                input.PseudonymousKey,
                input.WindowDurationMinutes,
                input.ResetAtUtc,
                input.RemainingPercent,
                handled.OrderDescending().ToArray(),
                resetAlertCycle);
        }

        var alert = resetAlerts.Count > 0 ? QuotaAlert.ForReset(resetAlerts) : selected;
        return new AlertReduction(
            new AlertStateDocument(1, enabled, output, resetBaseline || hasValidWindow),
            alert);
    }

    public static bool IsNewCycle(AlertWindowState previous, AlertInput current)
    {
        if (previous.ResetAtUtc is { } before && current.ResetAtUtc is { } after)
        {
            var tolerance = TimeSpan.FromMinutes(Math.Max(5, (current.WindowDurationMinutes ?? 0) / 2d));
            if (after - before >= tolerance)
            {
                return true;
            }
        }

        return previous.LastReliableRemaining is { } last
            && current.RemainingPercent - last >= 50
            && current.RemainingPercent >= 80;
    }

    public static bool IsResetCycle(AlertWindowState previous, AlertInput current)
    {
        if (previous.ResetAtUtc is not { } before || current.ResetAtUtc is not { } after)
        {
            return false;
        }

        var tolerance = TimeSpan.FromMinutes(Math.Max(5, (current.WindowDurationMinutes ?? 0) / 2d));
        return after - before >= tolerance
            && previous.LastReliableRemaining is { } last
            && current.RemainingPercent - last >= 50
            && current.RemainingPercent >= 80;
    }

    private static IEnumerable<int> Enabled(NotificationSettings settings)
    {
        if (settings.Remaining50)
        {
            yield return 50;
        }

        if (settings.Remaining20)
        {
            yield return 20;
        }

        if (settings.Remaining10)
        {
            yield return 10;
        }
    }
}

public sealed record AlertInput(
    string PseudonymousKey,
    string WindowName,
    int RemainingPercent,
    bool IsPercentageReliable,
    long? WindowDurationMinutes,
    DateTimeOffset? ResetAtUtc);
