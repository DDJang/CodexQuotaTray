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
    int? RemainingPercent,
    DateTimeOffset? ResetAtUtc);

public sealed record QuotaResetCreditExpiry(
    string Fingerprint,
    DateTimeOffset ExpiresAtUtc,
    string? Title,
    string? ResetType);

public sealed record QuotaAlert(string WindowName, int? RemainingPercent, int Threshold)
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

public sealed record QuotaResetEvaluationDiagnostic(
    string Window,
    DateTimeOffset Now,
    int? CurrentRemainingPercent,
    int? PreviousRemainingPercent,
    int? MinRemainingPercent,
    DateTimeOffset? BaselineResetAtUtc,
    DateTimeOffset? PreviousResetAtUtc,
    DateTimeOffset? CurrentResetAtUtc,
    DateTimeOffset? PendingResetDeadlineUtc,
    DateTimeOffset? LastNotifiedResetDeadlineUtc,
    bool DeadlineCrossed,
    bool CumulativeRecovery,
    bool CumulativeResetAtAdvance,
    bool ResetDetected,
    string? ResetCycleKey,
    bool NotificationAttempted = false,
    bool NotificationSucceeded = false);

public sealed record AlertReduction(AlertStateDocument State, QuotaAlert? Alert)
{
    public IReadOnlyList<QuotaResetEvaluationDiagnostic> ResetDiagnostics { get; init; } = [];
}

public static class QuotaAlertReducer
{
    private static readonly int[] AllThresholds = [50, 20, 10];
    private static readonly TimeSpan ResetDeadlineGrace = TimeSpan.FromMinutes(3);
    private const int RecoveryDelta = 50;
    private const int RecoveryMinimum = 80;

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
        var resetAlerts = new List<QuotaResetWindow>();
        var thresholdAlerts = new List<QuotaThresholdWindow>();
        var resetDiagnostics = new List<QuotaResetEvaluationDiagnostic>();
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

            var evidenceCandidates = identityUncertain
                ? FindResetEvidenceCandidates(
                    oldWindows.Values,
                    input,
                    semanticIdentity,
                    matchedOldKeys)
                    : old is null
                        ? []
                        : [old];
            var currentRemaining = input.IsPercentageReliable
                ? NormalizePercent(input.RemainingPercent)
                : null;
            var percentageReliable = currentRemaining is not null;
            var hasReliableResetMetadata = input.ResetAtUtc is not null;
            var hasComparableHistory = evidenceCandidates.Any(candidate =>
                IsDurationCompatible(candidate, input.WindowDurationMinutes));
            if (!percentageReliable && !hasReliableResetMetadata && !hasComparableHistory)
            {
                if (old is not null && !identityUncertain && legacyKeyToRemove is null)
                {
                    output[input.PseudonymousKey] = old;
                }

                continue;
            }

            hasValidWindow |= percentageReliable || hasReliableResetMetadata;

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
            var evaluation = EvaluateReset(
                input,
                evidenceCandidates,
                !resetBaseline,
                identityChanged,
                identityUncertain,
                now);
            var diagnostic = evaluation.ToDiagnostic(now);
            resetDiagnostics.Add(diagnostic);

            if (evaluation.ResetDetected)
            {
                handled.Clear();
                if (settings.ResetAfterCycle)
                {
                    resetAlerts.Add(new QuotaResetWindow(
                        input.WindowName,
                        currentRemaining,
                        input.ResetAtUtc));
                }
            }

            var newlyEnabled = enabled.Except(previous?.BaselineThresholds ?? []).ToArray();
            foreach (var threshold in newlyEnabled)
            {
                if (currentRemaining is { } observed && observed <= threshold)
                {
                    handled.Add(threshold);
                }
            }

            int[] crossed;
            if (baseline
                || old?.LastReliableRemaining is null
                || evaluation.ResetDetected
                || currentRemaining is not { } observedForThreshold)
            {
                crossed = [];
            }
            else
            {
                crossed = enabled.Where(threshold =>
                    !handled.Contains(threshold)
                    && old.LastReliableRemaining > threshold
                    && observedForThreshold <= threshold).ToArray();
            }
            foreach (var threshold in crossed)
            {
                handled.Add(threshold);
            }

            if (crossed.Length > 0)
            {
                var threshold = crossed.Min();
                thresholdAlerts.Add(new QuotaThresholdWindow(
                    input.WindowName,
                    currentRemaining.GetValueOrDefault(),
                    threshold));
            }

            output[input.PseudonymousKey] = BuildState(
                input,
                evaluation,
                handled.OrderDescending().ToArray(),
                semanticIdentity,
                identityUncertain && evaluation.CurrentCycleAlreadyAcknowledged && evaluation.Reference is null,
                now);
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
            alert)
        {
            ResetDiagnostics = resetDiagnostics,
        };
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

    private static int? NormalizePercent(int? value) =>
        value is { } percent && percent is >= 0 and <= 100 ? percent : null;

    private static string? CreateResetCycleKey(long? durationMinutes, DateTimeOffset? resetAtUtc)
    {
        if (resetAtUtc is not { } resetAt)
        {
            return null;
        }

        return $"window:{QuotaBucketPolicy.LogicalWindowKind(durationMinutes)}|resetAt:{resetAt.ToUnixTimeSeconds()}";
    }

    private static HashSet<string> CollectAcknowledgedCycleKeys(IEnumerable<AlertWindowState> states)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            if (state.LastNotifiedResetDeadlineUtc is { } lastNotified
                && CreateResetCycleKey(state.WindowDurationMinutes, lastNotified) is { } notifiedKey)
            {
                result.Add(notifiedKey);
            }

            if (!HasExtendedResetState(state) && IsLegacyCycleConsumed(state))
            {
                var key = state.LastResetAlertCycleFingerprint
                    ?? CreateResetCycleKey(state.WindowDurationMinutes, state.LastResetAlertCycleUtc);
                if (key is not null)
                {
                    result.Add(key);
                }
            }
        }

        return result;
    }

    private static ResetEvaluation EvaluateReset(
        AlertInput input,
        IReadOnlyList<AlertWindowState> candidates,
        bool allowRecovery,
        bool identityChanged,
        bool identityUncertain,
        DateTimeOffset now)
    {
        var currentRemaining = input.IsPercentageReliable
            ? NormalizePercent(input.RemainingPercent)
            : null;
        var currentDuration = input.WindowDurationMinutes
            ?? candidates.FirstOrDefault()?.WindowDurationMinutes;
        var currentCycleKey = CreateResetCycleKey(currentDuration, input.ResetAtUtc);
        var currentCycleAlreadyAcknowledged = currentCycleKey is not null
            && CollectAcknowledgedCycleKeys(candidates).Contains(currentCycleKey);
        var evidence = candidates
            .Where(candidate => IsDurationCompatible(candidate, input.WindowDurationMinutes))
            .Select(candidate => EvaluateEvidence(candidate, input, currentRemaining, now))
            .ToArray();
        if (evidence.Length == 0)
        {
            return new ResetEvaluation(
                null,
                currentRemaining,
                null,
                null,
                null,
                null,
                input.ResetAtUtc,
                null,
                null,
                false,
                false,
                false,
                false,
                false,
                null,
                currentCycleAlreadyAcknowledged,
                currentDuration);
        }

        var deadlineCrossed = evidence.Any(value => value.DeadlineCrossed);
        var cumulativeRecovery = evidence.Any(value => value.CumulativeRecovery);
        var cumulativeResetAtAdvance = evidence.Any(value => value.CumulativeResetAtAdvance);
        var advanceEvidence = evidence
            .Where(value => value.CumulativeResetAtAdvance)
            .ToArray();
        var metadataCatchUp = advanceEvidence.Length > 0
            && advanceEvidence.All(value => value.MetadataCatchUp);
        var rawResetDetected = deadlineCrossed
            || (allowRecovery && cumulativeRecovery)
            || (cumulativeResetAtAdvance && !metadataCatchUp);
        var reference = evidence.FirstOrDefault(value => value.DeadlineCrossed)
            ?? (allowRecovery
                ? evidence.FirstOrDefault(value => value.CumulativeRecovery)
                : null)
            ?? evidence.FirstOrDefault(value => value.CumulativeResetAtAdvance)
            ?? evidence[0];
        var sameAcknowledgedCycle = reference.PendingResetDeadlineUtc is { } pending
            && reference.LastNotifiedResetDeadlineUtc == pending
            && (input.ResetAtUtc is null || input.ResetAtUtc == pending);
        var currentCycleMatchesConsumedMarker = currentCycleKey is not null
            && IsLegacyCycleConsumed(reference.State)
            && string.Equals(
                reference.State.LastResetAlertCycleFingerprint
                    ?? CreateResetCycleKey(
                        reference.State.WindowDurationMinutes,
                        reference.State.LastResetAlertCycleUtc),
                currentCycleKey,
                StringComparison.Ordinal);
        var suppressAcknowledgedIdentity = currentCycleAlreadyAcknowledged
            && !deadlineCrossed
            && (identityChanged || identityUncertain);
        var suppressConsumedMarkerDuplicate = currentCycleMatchesConsumedMarker
            && (identityChanged || identityUncertain);
        var resetDetected = rawResetDetected
            && !suppressAcknowledgedIdentity
            && !suppressConsumedMarkerDuplicate
            && !(sameAcknowledgedCycle && !deadlineCrossed && !cumulativeRecovery);
        var effectiveDuration = input.WindowDurationMinutes ?? reference.EffectiveDurationMinutes;
        return new ResetEvaluation(
            reference.State,
            currentRemaining,
            reference.PreviousRemainingPercent,
            reference.MinRemainingPercent,
            reference.BaselineResetAtUtc,
            reference.PreviousResetAtUtc,
            input.ResetAtUtc,
            reference.PendingResetDeadlineUtc,
            reference.LastNotifiedResetDeadlineUtc,
            deadlineCrossed,
            cumulativeRecovery,
            cumulativeResetAtAdvance,
            metadataCatchUp,
            resetDetected,
            CreateResetCycleKey(effectiveDuration, reference.PendingResetDeadlineUtc),
            currentCycleAlreadyAcknowledged,
            effectiveDuration);
    }

    private static ResetEvidence EvaluateEvidence(
        AlertWindowState state,
        AlertInput input,
        int? currentRemaining,
        DateTimeOffset now)
    {
        var baselineResetAt = BaselineResetAt(state);
        var pendingResetDeadline = PendingResetDeadline(state);
        var lastNotifiedResetDeadline = LastNotifiedResetDeadline(state);
        var effectiveDuration = input.WindowDurationMinutes ?? state.WindowDurationMinutes;
        var currentReliable = input.IsPercentageReliable && currentRemaining is not null;
        var hasTrustworthyCurrentSnapshot = currentReliable || input.ResetAtUtc is not null;
        var canCatchUpLegacyDeadline = (!HasExtendedResetState(state) || state.ResetAlertMigrationPending)
            && state.ResetAtUtc is not null
            && hasTrustworthyCurrentSnapshot;
        var canUsePersistedDeadline = canCatchUpLegacyDeadline
            || (!state.ResetAlertMigrationPending
                && (state.PendingResetDeadlineUtc is not null
                    || (HasExtendedResetState(state) && state.BaselineResetAtUtc is not null)));
        var deadlineCrossed = canUsePersistedDeadline
            && pendingResetDeadline is { } pending
            && lastNotifiedResetDeadline != pending
            && now >= pending + ResetDeadlineGrace;
        var cumulativeRecovery = currentReliable
            && currentRemaining is { } observed
            && (state.MinRemainingPercentSinceBaseline ?? state.LastReliableRemaining) is { } min
            && observed - min >= RecoveryDelta
            && observed >= RecoveryMinimum;
        var cumulativeResetAtAdvance = input.ResetAtUtc is { } currentResetAt
            && baselineResetAt is { } baseline
            && HasMatchingPositiveDuration(state, input.WindowDurationMinutes)
            && effectiveDuration is > 0
            && currentResetAt - baseline >= TimeSpan.FromMinutes(effectiveDuration.Value / 2d);
        var metadataCatchUp = state.ResetAlertAwaitingCycleMetadata
            && input.ResetAtUtc is not null
            && IsCycleMetadataCatchUp(state, input.ResetAtUtc.Value, effectiveDuration);
        return new ResetEvidence(
            state,
            state.LastObservedRemainingPercent ?? state.LastReliableRemaining,
            state.MinRemainingPercentSinceBaseline ?? state.LastReliableRemaining,
            baselineResetAt,
            state.LastObservedResetAtUtc ?? state.ResetAtUtc,
            pendingResetDeadline,
            lastNotifiedResetDeadline,
            deadlineCrossed,
            cumulativeRecovery,
            cumulativeResetAtAdvance,
            metadataCatchUp,
            effectiveDuration);
    }

    private static AlertWindowState BuildState(
        AlertInput input,
        ResetEvaluation evaluation,
        IReadOnlyList<int> handledThresholds,
        string semanticIdentity,
        bool markCurrentCycleAcknowledged,
        DateTimeOffset now)
    {
        var old = evaluation.Reference;
        var currentRemaining = evaluation.CurrentRemainingPercent;
        var currentReliable = input.IsPercentageReliable && currentRemaining is not null;
        var duration = input.WindowDurationMinutes ?? old?.WindowDurationMinutes;
        var resetAt = input.ResetAtUtc ?? old?.ResetAtUtc;
        var baselineResetAt = evaluation.BaselineResetAtUtc;
        var pendingResetDeadline = evaluation.PendingResetDeadlineUtc;
        var lastNotifiedResetDeadline = evaluation.LastNotifiedResetDeadlineUtc;
        var minRemaining = evaluation.MinRemainingPercent;
        var lastReliableRemaining = old?.LastReliableRemaining;
        var lastObservedRemaining = old?.LastObservedRemainingPercent ?? old?.LastReliableRemaining;
        var lastObservedResetAt = old?.LastObservedResetAtUtc ?? old?.ResetAtUtc;
        var resetAlertCycle = old?.LastResetAlertCycleUtc;
        var resetAlertConsumed = old is not null && IsLegacyCycleConsumed(old);
        var awaitingCycleMetadata = old?.ResetAlertAwaitingCycleMetadata ?? false;
        var resetCycleFingerprint = old?.LastResetAlertCycleFingerprint
            ?? CreateResetCycleKey(old?.WindowDurationMinutes, old?.LastResetAlertCycleUtc);
        var migratingLegacyState = old is not null && !HasExtendedResetState(old);
        var resetAlertMigrationPending = old?.ResetAlertMigrationPending ?? false;
        if (migratingLegacyState)
        {
            resetAlertMigrationPending = true;
        }

        if (currentReliable)
        {
            var observed = currentRemaining!.Value;
            lastReliableRemaining = observed;
            lastObservedRemaining = observed;
            minRemaining = minRemaining is { } previousMin
                ? Math.Min(previousMin, observed)
                : observed;
        }
        if (input.ResetAtUtc is { } observedResetAt)
        {
            resetAt = observedResetAt;
            lastObservedResetAt = observedResetAt;
        }

        baselineResetAt ??= input.ResetAtUtc;
        if (!resetAlertMigrationPending)
        {
            pendingResetDeadline ??= input.ResetAtUtc;
        }

        if (evaluation.ResetDetected)
        {
            resetAlertMigrationPending = false;
            resetAlertConsumed = true;
            if (pendingResetDeadline is { } oldPending)
            {
                lastNotifiedResetDeadline = oldPending;
            }
            else if (input.ResetAtUtc is { } currentResetForNotification)
            {
                lastNotifiedResetDeadline = currentResetForNotification;
            }

            if (input.ResetAtUtc is { } currentResetAt)
            {
                baselineResetAt = currentResetAt;
                pendingResetDeadline = currentResetAt;
                resetAlertCycle = currentResetAt;
                resetCycleFingerprint = CreateResetCycleKey(duration, currentResetAt);
                if (evaluation.DeadlineCrossed
                    && now >= currentResetAt + ResetDeadlineGrace)
                {
                    // One successful catch-up acknowledges every missed cycle
                    // represented by this snapshot instead of replaying them
                    // one at a time on subsequent evaluations.
                    lastNotifiedResetDeadline = currentResetAt;
                }
            }

            minRemaining = currentReliable ? currentRemaining : null;
            lastReliableRemaining = currentReliable ? currentRemaining : null;
            awaitingCycleMetadata = evaluation.CumulativeRecovery
                && !evaluation.CumulativeResetAtAdvance
                && !evaluation.DeadlineCrossed;
        }
        else if (evaluation.MetadataCatchUp)
        {
            resetAlertMigrationPending = false;
            baselineResetAt = input.ResetAtUtc ?? baselineResetAt;
            pendingResetDeadline = input.ResetAtUtc ?? pendingResetDeadline;
            // This timestamp only labels a recovery event that was already
            // acknowledged. Carry it as acknowledged so a later deadline
            // check cannot replay the same logical reset.
            lastNotifiedResetDeadline = input.ResetAtUtc ?? lastNotifiedResetDeadline;
            resetAlertCycle = input.ResetAtUtc ?? resetAlertCycle;
            resetCycleFingerprint = CreateResetCycleKey(duration, input.ResetAtUtc) ?? resetCycleFingerprint;
            awaitingCycleMetadata = false;
        }

        if (markCurrentCycleAcknowledged && input.ResetAtUtc is { } currentCycle)
        {
            resetAlertConsumed = true;
            baselineResetAt ??= currentCycle;
            pendingResetDeadline ??= currentCycle;
            lastNotifiedResetDeadline ??= currentCycle;
            resetAlertCycle ??= currentCycle;
            resetCycleFingerprint ??= CreateResetCycleKey(duration, currentCycle);
        }

        return new AlertWindowState(
            input.PseudonymousKey,
            duration,
            resetAt,
            lastReliableRemaining,
            handledThresholds,
            resetAlertCycle,
            resetAlertConsumed,
            awaitingCycleMetadata,
            semanticIdentity,
            resetCycleFingerprint,
            baselineResetAt,
            pendingResetDeadline,
            lastNotifiedResetDeadline,
            minRemaining,
            lastObservedRemaining,
            lastObservedResetAt,
            resetAlertMigrationPending);
    }

    private static DateTimeOffset? BaselineResetAt(AlertWindowState state) =>
        state.BaselineResetAtUtc
        ?? state.ResetAtUtc
        ?? state.LastResetAlertCycleUtc;

    private static DateTimeOffset? PendingResetDeadline(AlertWindowState state) =>
        state.PendingResetDeadlineUtc
        ?? state.BaselineResetAtUtc
        ?? state.ResetAtUtc
        ?? state.LastResetAlertCycleUtc;

    private static DateTimeOffset? LastNotifiedResetDeadline(AlertWindowState state) =>
        state.LastNotifiedResetDeadlineUtc
        ?? (!HasExtendedResetState(state)
            && state.LastResetAlertCycleUtc is not null && state.ResetAlertCycleConsumed is not false
            ? state.LastResetAlertCycleUtc
            : null);

    private static bool HasExtendedResetState(AlertWindowState state) =>
        state.BaselineResetAtUtc is not null
        || state.PendingResetDeadlineUtc is not null
        || state.LastNotifiedResetDeadlineUtc is not null
        || state.MinRemainingPercentSinceBaseline is not null
        || state.LastObservedRemainingPercent is not null
        || state.LastObservedResetAtUtc is not null
        || state.ResetAlertMigrationPending;

    private static bool IsLegacyCycleConsumed(AlertWindowState state) =>
        state.ResetAlertCycleConsumed
        ?? (state.LastResetAlertCycleUtc is not null
            || state.LastResetAlertCycleFingerprint is not null);

    private static bool IsDurationCompatible(AlertWindowState state, long? currentDuration) =>
        state.WindowDurationMinutes is not > 0
        || currentDuration is not > 0
        || state.WindowDurationMinutes == currentDuration;

    private static bool HasMatchingPositiveDuration(AlertWindowState state, long? currentDuration) =>
        state.WindowDurationMinutes is > 0
        && currentDuration is > 0
        && state.WindowDurationMinutes == currentDuration;

    private static bool IsCycleMetadataCatchUp(
        AlertWindowState previous,
        DateTimeOffset currentResetAt,
        long? effectiveDuration)
    {
        var before = BaselineResetAt(previous);
        if (before is null)
        {
            return true;
        }

        return currentResetAt >= before.Value
            && effectiveDuration is > 0
            && currentResetAt - before.Value <= TimeSpan.FromMinutes(effectiveDuration.Value);
    }

    public static bool IsCycleTransition(AlertWindowState previous, AlertInput current)
    {
        if (previous.WindowDurationMinutes is not > 0
            || current.WindowDurationMinutes is not > 0
            || previous.WindowDurationMinutes != current.WindowDurationMinutes
            || !current.IsPercentageReliable
            || NormalizePercent(current.RemainingPercent) is not { } currentRemaining)
        {
            return false;
        }

        var min = previous.MinRemainingPercentSinceBaseline ?? previous.LastReliableRemaining;
        var recovery = min is { } low
            && currentRemaining - low >= RecoveryDelta
            && currentRemaining >= RecoveryMinimum;
        var advance = false;
        DateTimeOffset currentResetAt = default;
        if (current.ResetAtUtc is { } observedResetAt
            && BaselineResetAt(previous) is { } baseline)
        {
            currentResetAt = observedResetAt;
            advance = currentResetAt > baseline
                && currentResetAt - baseline >= TimeSpan.FromMinutes(current.WindowDurationMinutes.Value / 2d);
        }

        return recovery
            || (advance
                && !(previous.ResetAlertAwaitingCycleMetadata
                    && IsCycleMetadataCatchUp(previous, currentResetAt, current.WindowDurationMinutes)));
    }

    public static bool IsNewCycle(AlertWindowState previous, AlertInput current) =>
        IsCycleTransition(previous, current);

    public static bool IsResetCycle(AlertWindowState previous, AlertInput current) =>
        IsCycleTransition(previous, current);

    internal static string FormatResetEvaluation(QuotaResetEvaluationDiagnostic diagnostic) =>
        string.Join(
            " ",
            "quota_reset_evaluation",
            $"window={diagnostic.Window}",
            $"now={diagnostic.Now:O}",
            $"currentRemaining={FormatValue(diagnostic.CurrentRemainingPercent)}",
            $"previousRemaining={FormatValue(diagnostic.PreviousRemainingPercent)}",
            $"minRemaining={FormatValue(diagnostic.MinRemainingPercent)}",
            $"baselineResetAt={FormatValue(diagnostic.BaselineResetAtUtc)}",
            $"previousResetAt={FormatValue(diagnostic.PreviousResetAtUtc)}",
            $"currentResetAt={FormatValue(diagnostic.CurrentResetAtUtc)}",
            $"pendingResetDeadline={FormatValue(diagnostic.PendingResetDeadlineUtc)}",
            $"lastNotifiedResetDeadline={FormatValue(diagnostic.LastNotifiedResetDeadlineUtc)}",
            $"deadlineCrossed={diagnostic.DeadlineCrossed}",
            $"cumulativeRecovery={diagnostic.CumulativeRecovery}",
            $"cumulativeResetAtAdvance={diagnostic.CumulativeResetAtAdvance}",
            $"resetDetected={diagnostic.ResetDetected}",
            $"resetCycleKey={diagnostic.ResetCycleKey ?? "unknown"}",
            $"notificationAttempted={diagnostic.NotificationAttempted}",
            $"notificationSucceeded={diagnostic.NotificationSucceeded}");

    private static string FormatValue(int? value) => value?.ToString() ?? "unknown";

    private static string FormatValue(DateTimeOffset? value) => value?.ToString("O") ?? "unknown";

    private sealed record ResetEvidence(
        AlertWindowState State,
        int? PreviousRemainingPercent,
        int? MinRemainingPercent,
        DateTimeOffset? BaselineResetAtUtc,
        DateTimeOffset? PreviousResetAtUtc,
        DateTimeOffset? PendingResetDeadlineUtc,
        DateTimeOffset? LastNotifiedResetDeadlineUtc,
        bool DeadlineCrossed,
        bool CumulativeRecovery,
        bool CumulativeResetAtAdvance,
        bool MetadataCatchUp,
        long? EffectiveDurationMinutes);

    private sealed record ResetEvaluation(
        AlertWindowState? Reference,
        int? CurrentRemainingPercent,
        int? PreviousRemainingPercent,
        int? MinRemainingPercent,
        DateTimeOffset? BaselineResetAtUtc,
        DateTimeOffset? PreviousResetAtUtc,
        DateTimeOffset? CurrentResetAtUtc,
        DateTimeOffset? PendingResetDeadlineUtc,
        DateTimeOffset? LastNotifiedResetDeadlineUtc,
        bool DeadlineCrossed,
        bool CumulativeRecovery,
        bool CumulativeResetAtAdvance,
        bool MetadataCatchUp,
        bool ResetDetected,
        string? ResetCycleKey,
        bool CurrentCycleAlreadyAcknowledged,
        long? EffectiveDurationMinutes)
    {
        public QuotaResetEvaluationDiagnostic ToDiagnostic(DateTimeOffset now) =>
            new(
                QuotaBucketPolicy.LogicalWindowKind(EffectiveDurationMinutes),
                now,
                CurrentRemainingPercent,
                PreviousRemainingPercent,
                MinRemainingPercent,
                BaselineResetAtUtc,
                PreviousResetAtUtc,
                CurrentResetAtUtc,
                PendingResetDeadlineUtc,
                LastNotifiedResetDeadlineUtc,
                DeadlineCrossed,
                CumulativeRecovery,
                CumulativeResetAtAdvance,
                ResetDetected,
                ResetCycleKey);
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
    int? RemainingPercent,
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
