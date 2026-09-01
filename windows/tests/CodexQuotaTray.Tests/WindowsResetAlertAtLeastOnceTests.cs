using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class WindowsResetAlertAtLeastOnceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);
    private static readonly NotificationSettings Settings = new(true, true, true);

    [TestMethod]
    public void FiveHourRecoveryFromTwentyToOneHundredEmitsReset()
    {
        var resetAt = Now.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now);
        var recovered = Reduce(initial.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(1));

        AssertReset(recovered, "5h", 100, resetAt);
    }

    [TestMethod]
    public void SevenDayRecoveryFromTwentyToOneHundredEmitsReset()
    {
        var resetAt = Now.AddDays(7);
        var initial = Reduce(null, [Window("7d", 10_080, 20, resetAt)], Now);
        var recovered = Reduce(initial.State, [Window("7d", 10_080, 100, resetAt)], Now.AddMinutes(1));

        AssertReset(recovered, "7d", 100, resetAt);
    }

    [TestMethod]
    public void SegmentedRecoveryKeepsTheLowWatermarkAndEmitsOnce()
    {
        var resetAt = Now.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now);
        Assert.IsNull(initial.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        Assert.IsFalse(initial.State.Windows["5h"].ResetAlertMigrationPending);
        Assert.IsFalse(initial.State.Windows["5h"].ResetAlertAwaitingCycleMetadata);
        var middle = Reduce(initial.State, [Window("5h", 300, 60, resetAt)], Now.AddMinutes(1));
        var recovered = Reduce(middle.State, [Window("5h", 300, 85, resetAt)], Now.AddMinutes(2));
        var final = Reduce(recovered.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(3));

        Assert.IsNull(middle.Alert);
        Assert.IsNull(middle.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        Assert.IsFalse(middle.State.Windows["5h"].ResetAlertMigrationPending);
        AssertReset(recovered, "5h", 85, resetAt);
        Assert.IsNull(final.Alert);
        Assert.AreEqual(20, middle.State.Windows["5h"].MinRemainingPercentSinceBaseline);
        Assert.AreEqual(20, recovered.ResetDiagnostics.Single().MinRemainingPercent);
    }

    [TestMethod]
    public void SegmentedResetAtAdvanceUsesTheFirstBaselineAndEmitsOnce()
    {
        var baselineResetAt = Now.AddHours(8);
        var initial = Reduce(null, [Window("5h", 300, 20, baselineResetAt)], Now);
        var plusTwoHours = Reduce(
            initial.State,
            [Window("5h", 300, 20, baselineResetAt.AddHours(2))],
            Now.AddMinutes(1));
        var plusFourHours = Reduce(
            plusTwoHours.State,
            [Window("5h", 300, 20, baselineResetAt.AddHours(4))],
            Now.AddMinutes(2));
        var plusFiveHours = Reduce(
            plusFourHours.State,
            [Window("5h", 300, 20, baselineResetAt.AddHours(5))],
            Now.AddMinutes(3));

        Assert.IsNull(plusTwoHours.Alert);
        AssertReset(plusFourHours, "5h", 20, baselineResetAt.AddHours(4));
        Assert.IsNull(plusFiveHours.Alert);
        Assert.AreEqual(baselineResetAt, plusTwoHours.State.Windows["5h"].BaselineResetAtUtc);
        Assert.IsTrue(plusFourHours.ResetDiagnostics.Single().CumulativeResetAtAdvance);
    }

    [TestMethod]
    public void PastDeadlineOnlyWaitsForPositiveEvidence()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        var caughtUp = Reduce(initial.State, [Window("5h", 300, 20, resetAt)], Now);

        Assert.IsNull(caughtUp.Alert);
        Assert.IsTrue(caughtUp.ResetDiagnostics.Single().DeadlineCrossed);
        Assert.IsFalse(caughtUp.ResetDiagnostics.Single().CumulativeRecovery);
        Assert.IsFalse(caughtUp.ResetDiagnostics.Single().CumulativeResetAtAdvance);
        Assert.IsFalse(caughtUp.ResetDiagnostics.Single().ResetDetected);
        Assert.AreEqual(resetAt, caughtUp.State.Windows["5h"].PendingResetDeadlineUtc);
        Assert.IsNull(caughtUp.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void OfflineCrossingPreservesEvidenceUntilConfirmedSnapshot()
    {
        var resetAt = Now.AddHours(-1);
        var beforeOffline = Reduce(null, [Window("5h", 300, 35, resetAt)], Now.AddHours(-2));
        var afterOffline = Reduce(beforeOffline.State, [Window("5h", 300, 25, resetAt)], Now);
        var confirmedResetAt = resetAt.AddHours(5);
        var confirmed = Reduce(
            afterOffline.State,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now.AddMinutes(1));

        Assert.IsNull(afterOffline.Alert);
        Assert.AreEqual(resetAt, afterOffline.State.Windows["5h"].PendingResetDeadlineUtc);
        AssertReset(confirmed, "5h", 100, confirmedResetAt);
        Assert.AreEqual(confirmedResetAt, confirmed.Alert!.ResetWindows.Single().NextResetAtUtc);
    }

    [TestMethod]
    public async Task RestartLoadsPendingDeadlineWithoutFalseAlertAndCatchesUpOnConfirmation()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        using var directory = new TestDirectory();
        var persistence = new PreviewPersistence(new JsonFileStore(), new PreviewDataPaths(directory.Path));
        await persistence.SaveAlertStateAsync(initial.State, CancellationToken.None);

        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var stale = Reduce(restored, [Window("5h", 300, 20, resetAt)], Now);
        var confirmedResetAt = resetAt.AddHours(5);
        var confirmed = Reduce(
            stale.State,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now.AddMinutes(1));

        Assert.IsNotNull(restored);
        Assert.IsNull(stale.Alert);
        Assert.AreEqual(resetAt, stale.State.Windows["5h"].PendingResetDeadlineUtc);
        Assert.IsNull(stale.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        AssertReset(confirmed, "5h", 100, confirmedResetAt);
        Assert.AreEqual(confirmedResetAt, confirmed.Alert!.ResetWindows.Single().NextResetAtUtc);
    }

    [TestMethod]
    public void FirstSnapshotAlreadyInTheNextCycleCatchesUpTheOldDeadline()
    {
        var oldDeadline = Now.AddHours(-6);
        var nextDeadline = oldDeadline.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 25, oldDeadline)], Now.AddHours(-7));
        var nextCycle = Reduce(initial.State, [Window("5h", 300, 25, nextDeadline)], Now);

        AssertReset(nextCycle, "5h", 25, nextDeadline);
        Assert.IsNull(nextCycle.Alert!.ResetWindows.Single().NextResetAtUtc);
        Assert.AreEqual(oldDeadline, nextCycle.ResetDiagnostics.Single().PendingResetDeadlineUtc);
        Assert.AreEqual(
            nextCycle.State.Windows["5h"].PendingResetDeadlineUtc,
            nextCycle.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void MissingOrUnreliablePercentageNeedsResetAtAdvanceToConfirm()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        var missing = Reduce(
            initial.State,
            [Window("5h", 300, null, resetAt, reliable: false)],
            Now);
        var confirmedResetAt = resetAt.AddHours(5);
        var confirmed = Reduce(
            missing.State,
            [Window("5h", 300, null, confirmedResetAt, reliable: false)],
            Now.AddMinutes(1));

        Assert.IsNull(missing.Alert);
        Assert.IsTrue(missing.ResetDiagnostics.Single().DeadlineCrossed);
        Assert.IsNull(missing.ResetDiagnostics.Single().CurrentRemainingPercent);
        Assert.IsFalse(missing.ResetDiagnostics.Single().ResetDetected);
        AssertReset(confirmed, "5h", null, confirmedResetAt);
        Assert.AreEqual(confirmedResetAt, confirmed.Alert!.ResetWindows.Single().NextResetAtUtc);
    }

    [TestMethod]
    public void NormalizerRetainsResetMetadataWhenPercentageIsMissing()
    {
        var resetAt = Now.AddHours(5);
        var snapshot = QuotaNormalizer.Normalize(
            new RateLimitsReadResult(
                new RateLimitsResponse
                {
                    RateLimits = new RateLimitSnapshot
                    {
                        LimitId = "codex",
                        Primary = new RateLimitWindow
                        {
                            UsedPercent = null,
                            WindowDurationMinutes = 300,
                            ResetsAt = resetAt.ToUnixTimeSeconds(),
                        },
                    },
                },
                false));

        Assert.IsEmpty(snapshot.Windows);
        var observation = snapshot.ResetObservations!.Single();
        Assert.AreEqual(300, observation.WindowDurationMinutes);
        Assert.AreEqual(resetAt, observation.ResetAtUtc);
    }

    [TestMethod]
    public void SinkFailureModelDoesNotAcknowledgeConfirmedRecoveryAndNextEvaluationRetries()
    {
        var resetAt = Now.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now);
        var attempted = Reduce(initial.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(1));
        var retried = Reduce(initial.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(2));

        AssertReset(attempted, "5h", 100, resetAt);
        AssertReset(retried, "5h", 100, resetAt);
        Assert.IsNull(initial.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        Assert.AreEqual(resetAt, attempted.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        Assert.AreEqual(resetAt, attempted.Alert!.ResetWindows.Single().NextResetAtUtc);
    }

    [TestMethod]
    public void SuccessfulAcknowledgementDeduplicatesTheSameSnapshot()
    {
        var resetAt = Now.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now);
        var delivered = Reduce(initial.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(1));
        var repeated = Reduce(delivered.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(2));

        AssertReset(delivered, "5h", 100, resetAt);
        Assert.IsNull(repeated.Alert);
        Assert.AreEqual(resetAt, delivered.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void FiveHourAndSevenDayWindowsResetIndependently()
    {
        var fiveHourReset = Now.AddHours(-1);
        var fiveHourNextReset = fiveHourReset.AddHours(5);
        var sevenDayReset = Now.AddDays(-1);
        var initial = Reduce(
            null,
            [Window("5h", 300, 20, fiveHourReset), Window("7d", 10_080, 30, sevenDayReset)],
            Now.AddHours(-2));
        var caughtUp = Reduce(
            initial.State,
            [Window("5h", 300, 100, fiveHourNextReset), Window("7d", 10_080, 30, sevenDayReset)],
            Now);

        Assert.AreEqual(QuotaAlertKind.Reset, caughtUp.Alert!.Kind);
        Assert.HasCount(1, caughtUp.Alert.ResetWindows);
        Assert.AreEqual("5h", caughtUp.Alert.ResetWindows.Single().WindowName);
        Assert.AreEqual(fiveHourNextReset, caughtUp.State.Windows["5h"].ResetAtUtc);
        Assert.AreEqual(fiveHourNextReset, caughtUp.Alert.ResetWindows.Single().NextResetAtUtc);
        Assert.IsNull(caughtUp.State.Windows["7d"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void ReorderedWindowsKeepIndependentState()
    {
        var fiveHourReset = Now.AddHours(-1);
        var fiveHourNextReset = fiveHourReset.AddHours(5);
        var sevenDayReset = Now.AddDays(-1);
        var initial = Reduce(
            null,
            [Window("5h", 300, 20, fiveHourReset), Window("7d", 10_080, 20, sevenDayReset)],
            Now.AddHours(-2));
        var reordered = Reduce(
            initial.State,
            [Window("7d", 10_080, 20, sevenDayReset), Window("5h", 300, 100, fiveHourNextReset)],
            Now);

        Assert.AreEqual(QuotaAlertKind.Reset, reordered.Alert!.Kind);
        Assert.HasCount(1, reordered.Alert.ResetWindows);
        Assert.AreEqual("5h", reordered.Alert.ResetWindows.Single().WindowName);
        Assert.AreEqual(300, reordered.State.Windows["5h"].WindowDurationMinutes);
        Assert.AreEqual(10_080, reordered.State.Windows["7d"].WindowDurationMinutes);
        Assert.AreEqual(fiveHourNextReset, reordered.State.Windows["5h"].ResetAtUtc);
        Assert.IsNull(reordered.State.Windows["7d"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void MultipleMissedCyclesProduceOneCatchUpForTheLatestObservedCycle()
    {
        var oldDeadline = Now.AddHours(-12);
        var nextDeadline = oldDeadline.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, oldDeadline)], Now.AddHours(-13));
        var firstCatchUp = Reduce(initial.State, [Window("5h", 300, 20, nextDeadline)], Now);
        var secondCatchUp = Reduce(firstCatchUp.State, [Window("5h", 300, 20, nextDeadline)], Now.AddMinutes(1));

        AssertReset(firstCatchUp, "5h", 20, nextDeadline);
        Assert.IsNull(firstCatchUp.Alert!.ResetWindows.Single().NextResetAtUtc);
        Assert.IsNull(secondCatchUp.Alert);
        Assert.AreEqual(nextDeadline, firstCatchUp.State.Windows["5h"].PendingResetDeadlineUtc);
        Assert.AreEqual(nextDeadline, firstCatchUp.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void SmallResetAtJitterBeforeDeadlineDoesNotFalsePositive()
    {
        var baselineResetAt = Now.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, baselineResetAt)], Now);
        var jittered = Reduce(
            initial.State,
            [Window("5h", 300, 20, baselineResetAt.AddMinutes(6))],
            Now.AddMinutes(1));

        Assert.IsNull(jittered.Alert);
        Assert.IsFalse(jittered.ResetDiagnostics.Single().DeadlineCrossed);
        Assert.IsFalse(jittered.ResetDiagnostics.Single().CumulativeRecovery);
        Assert.IsFalse(jittered.ResetDiagnostics.Single().CumulativeResetAtAdvance);
        Assert.AreEqual(baselineResetAt, jittered.State.Windows["5h"].BaselineResetAtUtc);
    }

    [TestMethod]
    public async Task LegacyFutureDeadlineDoesNotAlertAtStartup()
    {
        var resetAt = Now.AddHours(5);
        using var directory = new TestDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(
            paths.AlertState,
            $$"""
            {
              "schemaVersion": 1,
              "baselineThresholds": [50, 20, 10],
              "resetAlertBaselineEstablished": true,
              "windows": {
                "5h": {
                  "pseudonymousKey": "5h",
                  "windowDurationMinutes": 300,
                  "resetAtUtc": "{{resetAt:O}}",
                  "lastReliableRemaining": 20,
                  "handledThresholds": [20, 10]
                }
              }
            }
            """);

        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var startup = Reduce(restored, [Window("5h", 300, 20, resetAt)], Now);

        Assert.IsNotNull(restored);
        Assert.IsNull(startup.Alert);
        Assert.IsTrue(startup.State.Windows["5h"].ResetAlertMigrationPending);
    }

    [TestMethod]
    public void MigratedLegacyDeadlineWaitsForConfirmationAfterItPasses()
    {
        var resetAt = Now.AddHours(5);
        var startup = Reduce(
            LegacyState(resetAt, remaining: 20),
            [Window("5h", 300, 20, resetAt)],
            Now);
        var caughtUp = Reduce(
            startup.State,
            [Window("5h", 300, 20, resetAt)],
            resetAt.AddMinutes(4));
        var confirmedResetAt = resetAt.AddHours(5);
        var confirmed = Reduce(
            caughtUp.State,
            [Window("5h", 300, 100, confirmedResetAt)],
            resetAt.AddHours(4));

        Assert.IsNull(startup.Alert);
        Assert.IsTrue(startup.State.Windows["5h"].ResetAlertMigrationPending);
        Assert.IsNull(caughtUp.Alert);
        Assert.IsTrue(caughtUp.State.Windows["5h"].ResetAlertMigrationPending);
        AssertReset(confirmed, "5h", 100, confirmedResetAt);
        Assert.AreEqual(confirmedResetAt, confirmed.Alert!.ResetWindows.Single().NextResetAtUtc);
    }

    [TestMethod]
    public void LegacyPastDeadlineWithUnchangedSnapshotWaitsForConfirmation()
    {
        var resetAt = Now.AddHours(-1);
        var legacy = LegacyState(resetAt, remaining: 20);
        var caughtUp = Reduce(legacy, [Window("5h", 300, 20, resetAt)], Now);
        var confirmedResetAt = resetAt.AddHours(5);
        var confirmed = Reduce(
            caughtUp.State,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now.AddMinutes(1));

        Assert.IsNull(caughtUp.Alert);
        Assert.IsTrue(caughtUp.State.Windows["5h"].ResetAlertMigrationPending);
        AssertReset(confirmed, "5h", 100, confirmedResetAt);
        Assert.IsFalse(confirmed.State.Windows["5h"].ResetAlertMigrationPending);
    }

    [TestMethod]
    public void SuccessfulLegacyConfirmationDoesNotRepeat()
    {
        var resetAt = Now.AddHours(-1);
        var legacy = LegacyState(resetAt, remaining: 20);
        var stale = Reduce(
            legacy,
            [Window("5h", 300, 20, resetAt)],
            Now);
        var confirmedResetAt = resetAt.AddHours(5);
        var caughtUp = Reduce(
            stale.State,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now.AddMinutes(1));
        var repeated = Reduce(
            caughtUp.State,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now.AddMinutes(2));

        Assert.IsNull(stale.Alert);
        AssertReset(caughtUp, "5h", 100, confirmedResetAt);
        Assert.IsNull(repeated.Alert);
    }

    [TestMethod]
    public void LegacyConfirmedResetRetriesWhenNotificationStateIsNotCommitted()
    {
        var resetAt = Now.AddHours(-1);
        var legacy = LegacyState(resetAt, remaining: 20);
        var confirmedResetAt = resetAt.AddHours(5);
        var attempted = Reduce(
            legacy,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now);
        var retried = Reduce(
            legacy,
            [Window("5h", 300, 100, confirmedResetAt)],
            Now.AddMinutes(1));

        AssertReset(attempted, "5h", 100, confirmedResetAt);
        AssertReset(retried, "5h", 100, confirmedResetAt);
    }

    [TestMethod]
    public void StaleDeadlineDoesNotNotifyUntilTheNextCycleIsConfirmed()
    {
        var oldResetAt = new DateTimeOffset(2026, 9, 1, 18, 11, 0, TimeSpan.Zero);
        var newResetAt = new DateTimeOffset(2026, 9, 1, 23, 15, 0, TimeSpan.Zero);
        var baseline = Reduce(
            null,
            [Window("5h", 300, 96, oldResetAt)],
            oldResetAt.AddMinutes(-5));
        var staleAt1813 = Reduce(
            baseline.State,
            [Window("5h", 300, 96, oldResetAt)],
            oldResetAt.AddMinutes(2));
        var staleAt1814 = Reduce(
            staleAt1813.State,
            [Window("5h", 300, 96, oldResetAt)],
            oldResetAt.AddMinutes(3));
        var confirmed = Reduce(
            staleAt1814.State,
            [Window("5h", 300, 100, newResetAt)],
            oldResetAt.AddMinutes(4));
        var repeated = Reduce(
            confirmed.State,
            [Window("5h", 300, 100, newResetAt)],
            oldResetAt.AddMinutes(5));

        Assert.IsNull(staleAt1813.Alert);
        Assert.IsNull(staleAt1814.Alert);
        Assert.IsTrue(staleAt1814.ResetDiagnostics.Single().DeadlineCrossed);
        Assert.IsFalse(staleAt1814.ResetDiagnostics.Single().ResetDetected);
        Assert.AreEqual(oldResetAt, staleAt1814.State.Windows["5h"].PendingResetDeadlineUtc);
        Assert.IsNull(staleAt1814.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        AssertReset(confirmed, "5h", 100, newResetAt);
        Assert.AreEqual(newResetAt, confirmed.Alert!.ResetWindows.Single().NextResetAtUtc);
        Assert.IsNull(repeated.Alert);
    }

    [TestMethod]
    public void RecoveryAfterDeadlineAwaitsLaterCycleMetadataWithoutRepeatingReset()
    {
        var oldResetAt = Now.AddHours(-1);
        var newResetAt = oldResetAt.AddHours(5);
        var baseline = Reduce(null, [Window("5h", 300, 20, oldResetAt)], Now.AddHours(-2));
        var recoveredWithStaleMetadata = Reduce(
            baseline.State,
            [Window("5h", 300, 100, oldResetAt)],
            Now);
        var metadataCatchUp = Reduce(
            recoveredWithStaleMetadata.State,
            [Window("5h", 300, 100, newResetAt)],
            Now.AddMinutes(1));

        AssertReset(recoveredWithStaleMetadata, "5h", 100, oldResetAt);
        Assert.IsNull(recoveredWithStaleMetadata.Alert!.ResetWindows.Single().NextResetAtUtc);
        Assert.IsTrue(recoveredWithStaleMetadata.State.Windows["5h"].ResetAlertAwaitingCycleMetadata);
        Assert.IsNull(metadataCatchUp.Alert);
        Assert.IsFalse(metadataCatchUp.State.Windows["5h"].ResetAlertAwaitingCycleMetadata);
        Assert.AreEqual(newResetAt, metadataCatchUp.State.Windows["5h"].PendingResetDeadlineUtc);
        Assert.AreEqual(newResetAt, metadataCatchUp.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void StaleDeadlineDoesNotEraseLowWatermarkOrFutureConfirmationEvidence()
    {
        var oldResetAt = Now.AddHours(-1);
        var baseline = Reduce(null, [Window("5h", 300, 96, oldResetAt)], Now.AddHours(-2));
        var stale = Reduce(baseline.State, [Window("5h", 300, 96, oldResetAt)], Now);

        Assert.AreEqual(96, stale.State.Windows["5h"].MinRemainingPercentSinceBaseline);
        Assert.AreEqual(96, stale.State.Windows["5h"].LastObservedRemainingPercent);
        Assert.AreEqual(oldResetAt, stale.State.Windows["5h"].LastObservedResetAtUtc);
        Assert.IsNull(stale.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void FormatterTimestampEvidenceIsExplicitlySeparatedFromObservedResetAt()
    {
        var oldResetAt = Now.AddHours(-1);
        var reset = Reduce(
            null,
            [Window("5h", 300, 20, oldResetAt)],
            Now.AddHours(-2));
        var confirmed = Reduce(
            reset.State,
            [Window("5h", 300, 100, oldResetAt)],
            Now);

        AssertReset(confirmed, "5h", 100, oldResetAt);
        Assert.IsNull(confirmed.Alert!.ResetWindows.Single().NextResetAtUtc);
    }

    [TestMethod]
    public void DiagnosticsExposeEveryResetDecisionFieldWithoutRawIdentity()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("sensitive-key", 300, 20, resetAt)], Now.AddHours(-2));
        var reduction = Reduce(initial.State, [Window("sensitive-key", 300, null, resetAt, reliable: false)], Now);
        var diagnostic = reduction.ResetDiagnostics.Single();
        var formatted = QuotaAlertReducer.FormatResetEvaluation(diagnostic);

        StringAssert.Contains(formatted, "window=five-hour");
        StringAssert.Contains(formatted, "currentRemaining=unknown");
        StringAssert.Contains(formatted, "previousRemaining=20");
        StringAssert.Contains(formatted, "minRemaining=20");
        StringAssert.Contains(formatted, "baselineResetAt=");
        StringAssert.Contains(formatted, "previousResetAt=");
        StringAssert.Contains(formatted, "currentResetAt=");
        StringAssert.Contains(formatted, "pendingResetDeadline=");
        StringAssert.Contains(formatted, "lastNotifiedResetDeadline=unknown");
        StringAssert.Contains(formatted, "deadlineCrossed=True");
        StringAssert.Contains(formatted, "cumulativeRecovery=False");
        StringAssert.Contains(formatted, "cumulativeResetAtAdvance=False");
        StringAssert.Contains(formatted, "resetDetected=False");
        StringAssert.Contains(formatted, "resetCycleKey=");
        StringAssert.Contains(formatted, "notificationAttempted=False");
        StringAssert.Contains(formatted, "notificationSucceeded=False");
        Assert.IsFalse(formatted.Contains("sensitive-key", StringComparison.Ordinal));
    }

    private static AlertReduction Reduce(
        AlertStateDocument? previous,
        IReadOnlyList<AlertInput> windows,
        DateTimeOffset now) =>
        QuotaAlertReducer.Reduce(previous, windows, Settings, nowUtc: now);

    private static AlertInput Window(
        string key,
        long durationMinutes,
        int? remaining,
        DateTimeOffset? resetAt,
        bool reliable = true) =>
        new(key, key, remaining, reliable, durationMinutes, resetAt);

    private static AlertStateDocument LegacyState(DateTimeOffset resetAt, int remaining) =>
        new(
            1,
            [50, 20, 10],
            new Dictionary<string, AlertWindowState>(StringComparer.Ordinal)
            {
                ["5h"] = new(
                    "5h",
                    300,
                    resetAt,
                    remaining,
                    [20, 10]),
            },
            ResetAlertBaselineEstablished: true);

    private static void AssertReset(
        AlertReduction reduction,
        string key,
        int? remaining,
        DateTimeOffset? resetAt)
    {
        Assert.IsNotNull(reduction.Alert);
        Assert.AreEqual(QuotaAlertKind.Reset, reduction.Alert!.Kind);
        var window = reduction.Alert.ResetWindows.Single(window => window.WindowName == key);
        Assert.AreEqual(remaining, window.RemainingPercent);
        Assert.AreEqual(resetAt, window.ResetAtUtc);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexQuotaTray-ResetTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
