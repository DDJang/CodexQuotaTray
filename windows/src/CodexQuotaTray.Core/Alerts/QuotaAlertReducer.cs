using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Core.Alerts;

public enum QuotaAlertKind
{
    Threshold,
    Reset,
    Composite,
}

public sealed record QuotaThresholdWindow(
    string WindowName,
    int RemainingPercent,
    int Threshold);

public sealed record QuotaResetWindow(
    string WindowName,
    int RemainingPercent,
    DateTimeOffset? ResetAtUtc);

public sealed record QuotaAlert(string WindowName, int RemainingPercent, int Threshold)
{
    public QuotaAlertKind Kind { get; init; } = QuotaAlertKind.Threshold;

    public IReadOnlyList<QuotaThresholdWindow> ThresholdWindows { get; init; } = [];

    public IReadOnlyList<QuotaResetWindow> ResetWindows { get; init; } = [];

    public static QuotaAlert ForThresholds(IReadOnlyList<QuotaThresholdWindow> windows)
    {
        if (windows.Count == 0)
        {
            throw new ArgumentException("At least one threshold window is required.", nameof(windows));
        }

        var first = windows[0];
        return new QuotaAlert(first.WindowName, first.RemainingPercent, first.Threshold)
        {
            ThresholdWindows = windows,
        };
    }

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

    public static QuotaAlert ForComposite(
        IReadOnlyList<QuotaThresholdWindow> thresholds,
        IReadOnlyList<QuotaResetWindow> resets)
    {
        if (thresholds.Count == 0 || resets.Count == 0)
        {
            throw new ArgumentException("Both threshold and reset windows are required.");
        }

        var first = thresholds[0];
        return new QuotaAlert(first.WindowName, first.RemainingPercent, first.Threshold)
        {
            Kind = QuotaAlertKind.Composite,
            ThresholdWindows = thresholds,
            ResetWindows = resets,
        };
    }
}

public sealed record AlertReduction(AlertStateDocument State, QuotaAlert? Alert);

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
        var baselineEstablished = !baseline && previous!.ResetAlertBaselineEstablished;
        var resetBaseline = !baselineEstablished;
        var oldWindows = baseline ? new Dictionary<string, AlertWindowState>() : previous!.Windows;
        var output = new Dictionary<string, AlertWindowState>(oldWindows, StringComparer.Ordinal);
        var resetAlerts = new List<QuotaResetWindow>();
        var thresholdAlerts = new List<QuotaThresholdWindow>();
        var hasValidWindow = false;

        foreach (var input in windows)
        {
            oldWindows.TryGetValue(input.PseudonymousKey, out var old);
            string? legacyKeyToRemove = null;
            if (old is null
                && input.LegacyPseudonymousKey is { } legacyKey
                && oldWindows.TryGetValue(legacyKey, out old))
            {
                legacyKeyToRemove = legacyKey;
            }

            if (!input.IsPercentageReliable || input.RemainingPercent is < 0 or > 100)
            {
                if (old is not null && legacyKeyToRemove is null)
                {
                    output[input.PseudonymousKey] = old;
                }

                continue;
            }

            hasValidWindow = true;
            if (legacyKeyToRemove is not null)
            {
                output.Remove(legacyKeyToRemove);
            }

            var handled = old?.HandledThresholds.ToHashSet() ?? [];
            var resetAtAdvance = old is not null && IsReliableResetAtAdvance(old, input);
            var strongRecovery = old is not null && IsStrongRecovery(old, input);
            var metadataCatchUp = old is not null
                && !strongRecovery
                && old.ResetAlertAwaitingCycleMetadata
                && IsCycleMetadataCatchUp(old, input, resetAtAdvance);
            var cycleTransition = resetAtAdvance || strongRecovery;
            // A migrated/unestablished baseline is deliberately not allowed
            // to infer a reset from percentage recovery alone. A reliable
            // resetAt advance can still prove a transition from persisted
            // window history. Fresh installs have no old window to compare.
            var newCycle = !metadataCatchUp
                && cycleTransition
                && (!resetBaseline || resetAtAdvance);
            if (newCycle)
            {
                handled.Clear();
            }

            var resetAlertCycle = old?.LastResetAlertCycleUtc;
            // Older state files only had LastResetAlertCycleUtc; treat an
            // existing marker as already consumed when migrating them.
            var resetAlertConsumed = old?.ResetAlertCycleConsumed
                ?? old?.LastResetAlertCycleUtc is not null;
            var awaitingCycleMetadata = old?.ResetAlertAwaitingCycleMetadata ?? false;
            if (metadataCatchUp)
            {
                resetAlertCycle = input.ResetAtUtc;
                awaitingCycleMetadata = false;
            }
            else if (newCycle)
            {
                if (settings.ResetAfterCycle)
                {
                    resetAlerts.Add(new QuotaResetWindow(
                        input.WindowName,
                        input.RemainingPercent,
                        input.ResetAtUtc));
                }

                resetAlertCycle = input.ResetAtUtc ?? resetAlertCycle;
                resetAlertConsumed = true;
                // Strong recovery is a complete logical-cycle signal. If
                // resetAt did not also advance reliably, remember that its
                // later advance only labels this already-consumed cycle.
                awaitingCycleMetadata = strongRecovery && !resetAtAdvance;
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
                var threshold = crossed.Min();
                thresholdAlerts.Add(new QuotaThresholdWindow(
                    input.WindowName,
                    input.RemainingPercent,
                    threshold));
            }

            output[input.PseudonymousKey] = new AlertWindowState(
                input.PseudonymousKey,
                input.WindowDurationMinutes,
                input.ResetAtUtc,
                input.RemainingPercent,
                handled.OrderDescending().ToArray(),
                resetAlertCycle,
                resetAlertConsumed,
                awaitingCycleMetadata);
        }

        thresholdAlerts.Sort((left, right) => left.Threshold.CompareTo(right.Threshold));
        var alert = thresholdAlerts.Count > 0 && resetAlerts.Count > 0
            ? QuotaAlert.ForComposite(thresholdAlerts, resetAlerts)
            : thresholdAlerts.Count > 0
                ? QuotaAlert.ForThresholds(thresholdAlerts)
                : resetAlerts.Count > 0
                    ? QuotaAlert.ForReset(resetAlerts)
                    : null;
        return new AlertReduction(
            new AlertStateDocument(
                1,
                enabled,
                output,
                baselineEstablished || hasValidWindow),
            alert);
    }

    public static bool IsCycleTransition(AlertWindowState previous, AlertInput current)
    {
        if (IsStrongRecovery(previous, current))
        {
            return true;
        }

        var resetAtAdvance = IsReliableResetAtAdvance(previous, current);
        return resetAtAdvance
            && !(previous.ResetAlertAwaitingCycleMetadata
                && IsCycleMetadataCatchUp(previous, current, resetAtAdvance));
    }

    private static bool IsStrongRecovery(AlertWindowState previous, AlertInput current) =>
        previous.LastReliableRemaining is { } last
            && current.RemainingPercent - last >= 50
            && current.RemainingPercent >= 80;

    public static bool IsNewCycle(AlertWindowState previous, AlertInput current) =>
        IsCycleTransition(previous, current);

    public static bool IsResetCycle(AlertWindowState previous, AlertInput current)
    {
        return IsCycleTransition(previous, current);
    }

    private static bool IsReliableResetAtAdvance(AlertWindowState previous, AlertInput current)
    {
        if (previous.ResetAtUtc is not { } before || current.ResetAtUtc is not { } after)
        {
            return false;
        }

        if (previous.WindowDurationMinutes is not > 0
            || current.WindowDurationMinutes is not > 0
            || previous.WindowDurationMinutes != current.WindowDurationMinutes)
        {
            return false;
        }

        var tolerance = TimeSpan.FromMinutes(Math.Max(5, current.WindowDurationMinutes.Value / 2d));
        return after - before >= tolerance;
    }

    private static bool IsCycleMetadataCatchUp(
        AlertWindowState previous,
        AlertInput current,
        bool resetAtAdvance)
    {
        if (current.ResetAtUtc is null)
        {
            return false;
        }

        if (previous.ResetAtUtc is null)
        {
            return previous.WindowDurationMinutes is > 0
                && current.WindowDurationMinutes == previous.WindowDurationMinutes
                && current.ResetAtUtc != previous.LastResetAlertCycleUtc;
        }

        return resetAtAdvance;
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
    DateTimeOffset? ResetAtUtc,
    string? LegacyPseudonymousKey = null);
