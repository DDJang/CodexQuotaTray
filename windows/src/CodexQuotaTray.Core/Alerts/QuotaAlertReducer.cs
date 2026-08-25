using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Core.Alerts;

public enum QuotaAlertKind
{
    Threshold,
    Reset,
    Composite,
    ResetCreditExpiry,
}

public sealed record QuotaThresholdWindow(
    string WindowName,
    int RemainingPercent,
    int Threshold);

public sealed record QuotaResetWindow(
    string WindowName,
    int RemainingPercent,
    DateTimeOffset? ResetAtUtc);

public sealed record QuotaResetCreditExpiry(
    string Fingerprint,
    DateTimeOffset ExpiresAtUtc,
    string? Title,
    string? ResetType);

public sealed record QuotaAlert(string WindowName, int RemainingPercent, int Threshold)
{
    public QuotaAlertKind Kind { get; init; } = QuotaAlertKind.Threshold;

    public IReadOnlyList<QuotaThresholdWindow> ThresholdWindows { get; init; } = [];

    public IReadOnlyList<QuotaResetWindow> ResetWindows { get; init; } = [];

    public IReadOnlyList<QuotaResetCreditExpiry> ResetCreditExpiryWindows { get; init; } = [];

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
        IReadOnlyList<QuotaResetWindow> resets,
        IReadOnlyList<QuotaResetCreditExpiry>? resetCredits = null)
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
            ResetCreditExpiryWindows = resetCredits ?? [],
        };
    }

    public static QuotaAlert ForResetCreditExpiry(IReadOnlyList<QuotaResetCreditExpiry> credits)
    {
        if (credits.Count == 0)
        {
            throw new ArgumentException("At least one reset credit is required.", nameof(credits));
        }

        var first = credits[0];
        return new QuotaAlert(
            first.Title ?? first.ResetType ?? "重置卡",
            0,
            0)
        {
            Kind = QuotaAlertKind.ResetCreditExpiry,
            ResetCreditExpiryWindows = credits,
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
        NotificationSettings settings,
        IReadOnlyList<ResetCreditExpiryInput>? resetCreditInputs = null,
        DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var enabled = Enabled(settings).ToArray();
        var baseline = previous is null || previous.SchemaVersion != 1;
        var baselineEstablished = !baseline && previous!.ResetAlertBaselineEstablished;
        var resetBaseline = !baselineEstablished;
        var oldWindows = baseline ? new Dictionary<string, AlertWindowState>() : previous!.Windows;
        var output = new Dictionary<string, AlertWindowState>(oldWindows, StringComparer.Ordinal);
        var matchedOldKeys = new HashSet<string>(StringComparer.Ordinal);
        var consumedCycleFingerprints = CollectConsumedCycleFingerprints(oldWindows.Values);
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
            var matchedOldKey = old is not null
                ? legacyKeyToRemove ?? input.PseudonymousKey
                : null;
            var semanticIdentity = input.SemanticIdentity
                ?? CreateSemanticIdentity(input.WindowDurationMinutes);
            var identityUncertain = false;
            if (old is null)
            {
                var semanticMatches = oldWindows
                    .Where(entry => !matchedOldKeys.Contains(entry.Key)
                        && SemanticIdentityMatches(entry.Value, semanticIdentity))
                    .Select(entry => (Key: entry.Key, State: entry.Value))
                    .ToArray();
                if (semanticMatches.Length == 1)
                {
                    matchedOldKey = semanticMatches[0].Key;
                    old = semanticMatches[0].State;
                }
                else
                {
                    identityUncertain = true;
                }
            }

            if (old is not null && matchedOldKey is not null)
            {
                matchedOldKeys.Add(matchedOldKey);
            }

            if (matchedOldKey is not null
                && !string.Equals(matchedOldKey, input.PseudonymousKey, StringComparison.Ordinal))
            {
                output.Remove(matchedOldKey);
            }

            var identityChanged = old is not null
                && !string.Equals(old.PseudonymousKey, input.PseudonymousKey, StringComparison.Ordinal);
            HashSet<int> handled = identityUncertain
                ? []
                : old?.HandledThresholds.ToHashSet() ?? [];
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
            var resetCycleFingerprint = old?.LastResetAlertCycleFingerprint
                ?? CreateResetCycleFingerprint(old?.WindowDurationMinutes, old?.LastResetAlertCycleUtc);
            var currentCycleFingerprint = CreateResetCycleFingerprint(
                input.WindowDurationMinutes,
                input.ResetAtUtc);
            var currentCycleAlreadyConsumed = currentCycleFingerprint is not null
                && consumedCycleFingerprints.Contains(currentCycleFingerprint);

            if (identityUncertain)
            {
                var evidenceCandidates = FindResetEvidenceCandidates(
                    oldWindows.Values,
                    input,
                    semanticIdentity,
                    matchedOldKeys);
                var resetAtEvidence = evidenceCandidates.Any(candidate => IsReliableResetAtAdvance(candidate, input));
                var strongRecoveryEvidence = evidenceCandidates.Any(candidate => IsStrongRecovery(candidate, input));
                var hasResetEvidence = resetAtEvidence
                    || (!resetBaseline && strongRecoveryEvidence);
                if (hasResetEvidence)
                {
                    resetAlertCycle = input.ResetAtUtc ?? resetAlertCycle;
                    resetAlertConsumed = true;
                    resetCycleFingerprint = currentCycleFingerprint ?? resetCycleFingerprint;
                    awaitingCycleMetadata = strongRecoveryEvidence && !resetAtEvidence;
                    if (currentCycleFingerprint is not null)
                    {
                        consumedCycleFingerprints.Add(currentCycleFingerprint);
                    }

                    if (settings.ResetAfterCycle && !currentCycleAlreadyConsumed)
                    {
                        resetAlerts.Add(new QuotaResetWindow(
                            input.WindowName,
                            input.RemainingPercent,
                            input.ResetAtUtc));
                    }
                }
                else if (currentCycleAlreadyConsumed)
                {
                    // The current key may be a third representation of a cycle
                    // already acknowledged under another identity. Carry only
                    // the cycle marker; threshold history remains unmerged.
                    resetAlertCycle = input.ResetAtUtc ?? resetAlertCycle;
                    resetAlertConsumed = true;
                    resetCycleFingerprint = currentCycleFingerprint;
                }

                output[input.PseudonymousKey] = new AlertWindowState(
                    input.PseudonymousKey,
                    input.WindowDurationMinutes,
                    input.ResetAtUtc,
                    input.RemainingPercent,
                    [],
                    resetAlertCycle,
                    resetAlertConsumed,
                    awaitingCycleMetadata,
                    semanticIdentity,
                    resetCycleFingerprint);
                continue;
            }

            if (metadataCatchUp)
            {
                resetAlertCycle = input.ResetAtUtc;
                awaitingCycleMetadata = false;
                resetCycleFingerprint = currentCycleFingerprint ?? resetCycleFingerprint;
            }
            else if (newCycle)
            {
                var suppressChangedIdentityDuplicate = identityChanged && currentCycleAlreadyConsumed;
                if (settings.ResetAfterCycle && !suppressChangedIdentityDuplicate)
                {
                    resetAlerts.Add(new QuotaResetWindow(
                        input.WindowName,
                        input.RemainingPercent,
                        input.ResetAtUtc));
                }

                resetAlertCycle = input.ResetAtUtc ?? resetAlertCycle;
                resetAlertConsumed = true;
                resetCycleFingerprint = currentCycleFingerprint ?? resetCycleFingerprint;
                // Strong recovery is a complete logical-cycle signal. If
                // resetAt did not also advance reliably, remember that its
                // later advance only labels this already-consumed cycle.
                awaitingCycleMetadata = strongRecovery && !resetAtAdvance;
                if (currentCycleFingerprint is not null)
                {
                    consumedCycleFingerprints.Add(currentCycleFingerprint);
                }
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
                awaitingCycleMetadata,
                semanticIdentity,
                resetCycleFingerprint);
        }

        var resetCreditStates = baseline
            ? new Dictionary<string, ResetCreditAlertState>(StringComparer.Ordinal)
            : new Dictionary<string, ResetCreditAlertState>(
                previous!.ResetCredits ?? new Dictionary<string, ResetCreditAlertState>(),
                StringComparer.Ordinal);
        var resetCreditAlerts = new List<QuotaResetCreditExpiry>();
        var seenResetCredits = new HashSet<string>(StringComparer.Ordinal);
        if (settings.NotifyResetCreditExpiry)
        {
            foreach (var input in resetCreditInputs ?? [])
            {
                if (!IsValidResetCredit(input, now)
                    || string.IsNullOrWhiteSpace(input.Fingerprint)
                    || !seenResetCredits.Add(input.Fingerprint))
                {
                    continue;
                }

                resetCreditStates.TryGetValue(input.Fingerprint, out var old);
                var dueAt = input.ExpiresAtUtc!.Value
                    - TimeSpan.FromHours(NormalizeLeadHours(settings.ResetCreditExpiryLeadHours));
                var notified = old?.Notified == true;
                if (!notified && now >= dueAt)
                {
                    resetCreditAlerts.Add(new QuotaResetCreditExpiry(
                        input.Fingerprint,
                        input.ExpiresAtUtc.Value,
                        input.Title,
                        input.ResetType));
                    notified = true;
                }

                resetCreditStates[input.Fingerprint] = new ResetCreditAlertState(
                    now,
                    input.ExpiresAtUtc,
                    notified);
            }

            var staleBefore = now - TimeSpan.FromDays(30);
            foreach (var entry in resetCreditStates.ToArray())
            {
                if (seenResetCredits.Contains(entry.Key))
                {
                    continue;
                }

                if (entry.Value.ExpiresAtUtc <= now
                    || entry.Value.LastSeenUtc is null
                    || entry.Value.LastSeenUtc < staleBefore)
                {
                    resetCreditStates.Remove(entry.Key);
                }
            }

            while (resetCreditStates.Count > 128)
            {
                var oldest = resetCreditStates
                    .OrderBy(entry => entry.Value.LastSeenUtc ?? DateTimeOffset.MinValue)
                    .First().Key;
                resetCreditStates.Remove(oldest);
            }
        }

        thresholdAlerts.Sort((left, right) => left.Threshold.CompareTo(right.Threshold));
        var alert = thresholdAlerts.Count > 0 && resetAlerts.Count > 0
            ? QuotaAlert.ForComposite(thresholdAlerts, resetAlerts, resetCreditAlerts)
            : thresholdAlerts.Count > 0
                ? QuotaAlert.ForThresholds(thresholdAlerts)
                : resetAlerts.Count > 0
                    ? QuotaAlert.ForReset(resetAlerts)
                    : resetCreditAlerts.Count > 0
                        ? QuotaAlert.ForResetCreditExpiry(resetCreditAlerts)
                        : null;
        if (alert is not null && resetCreditAlerts.Count > 0 && alert.Kind is not QuotaAlertKind.Composite)
        {
            alert = alert with { ResetCreditExpiryWindows = resetCreditAlerts };
        }
        return new AlertReduction(
            new AlertStateDocument(
                1,
                enabled,
                output,
                baselineEstablished || hasValidWindow,
                resetCreditStates),
            alert);
    }

    private static bool IsValidResetCredit(ResetCreditExpiryInput input, DateTimeOffset now) =>
        string.Equals(input.Status?.Trim(), "available", StringComparison.OrdinalIgnoreCase)
        && input.ExpiresAtUtc is { } expiresAt
        && expiresAt > now;

    private static int NormalizeLeadHours(int value) => value switch
    {
        6 => 6,
        1 => 1,
        _ => 24,
    };

    private static string CreateSemanticIdentity(long? durationMinutes) =>
        QuotaBucketPolicy.CreateSemanticIdentity(
            QuotaBucketPolicy.CanonicalBucketId,
            durationMinutes)
        ?? $"bucket:{QuotaBucketPolicy.CanonicalBucketId}|window:unknown";

    private static bool SemanticIdentityMatches(AlertWindowState state, string semanticIdentity) =>
        string.Equals(
            state.SemanticIdentity ?? CreateSemanticIdentity(state.WindowDurationMinutes),
            semanticIdentity,
            StringComparison.Ordinal);

    private static AlertWindowState[] FindResetEvidenceCandidates(
        IEnumerable<AlertWindowState> states,
        AlertInput current,
        string semanticIdentity,
        ISet<string> matchedOldKeys)
    {
        var available = states
            .Where(state => !matchedOldKeys.Contains(state.PseudonymousKey))
            .ToArray();
        var semanticMatches = available
            .Where(state => SemanticIdentityMatches(state, semanticIdentity))
            .ToArray();
        if (semanticMatches.Length > 0)
        {
            return semanticMatches;
        }

        var logicalMatches = available
            .Where(state => SameLogicalWindow(state.WindowDurationMinutes, current.WindowDurationMinutes))
            .ToArray();
        if (logicalMatches.Length > 0)
        {
            return logicalMatches;
        }

        // A single old window is still useful evidence when an app-server
        // shape change removed every stable identity field. Multiple windows
        // with no semantic agreement stay non-mergeable.
        return available.Length == 1 ? available : [];
    }

    private static bool SameLogicalWindow(long? left, long? right) =>
        left is > 0
        && right is > 0
        && string.Equals(
            QuotaBucketPolicy.LogicalWindowKind(left),
            QuotaBucketPolicy.LogicalWindowKind(right),
            StringComparison.Ordinal);

    private static string? CreateResetCycleFingerprint(long? durationMinutes, DateTimeOffset? resetAtUtc)
    {
        if (resetAtUtc is not { } resetAt)
        {
            return null;
        }

        return $"window:{QuotaBucketPolicy.LogicalWindowKind(durationMinutes)}|resetAt:{resetAt.ToUnixTimeSeconds()}";
    }

    private static HashSet<string> CollectConsumedCycleFingerprints(IEnumerable<AlertWindowState> states)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var consumed = state.ResetAlertCycleConsumed
                ?? state.LastResetAlertCycleUtc is not null
                || state.LastResetAlertCycleFingerprint is not null;
            if (!consumed)
            {
                continue;
            }

            var fingerprint = state.LastResetAlertCycleFingerprint
                ?? CreateResetCycleFingerprint(state.WindowDurationMinutes, state.LastResetAlertCycleUtc);
            if (fingerprint is not null)
            {
                result.Add(fingerprint);
            }
        }

        return result;
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
        if (current.ResetAtUtc is not { } after
            || previous.WindowDurationMinutes is not > 0
            || current.WindowDurationMinutes != previous.WindowDurationMinutes)
        {
            return false;
        }

        if (previous.ResetAtUtc is not null && !resetAtAdvance)
        {
            return false;
        }

        var before = previous.ResetAtUtc ?? previous.LastResetAlertCycleUtc;
        if (before is null)
        {
            return true;
        }

        var advance = after - before.Value;
        var duration = TimeSpan.FromMinutes(current.WindowDurationMinutes.Value);
        // One window of resetAt movement can label the strong-recovery cycle
        // already consumed. A larger jump proves at least one later cycle.
        return advance > TimeSpan.Zero && advance <= duration;
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
    string? LegacyPseudonymousKey = null,
    string? SemanticIdentity = null);

public sealed record ResetCreditExpiryInput(
    string Fingerprint,
    string? Status,
    DateTimeOffset? ExpiresAtUtc,
    string? Title,
    string? ResetType);
