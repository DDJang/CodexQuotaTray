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
    public void PastDeadlineEmitsWithoutARecoveryJump()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        var caughtUp = Reduce(initial.State, [Window("5h", 300, 20, resetAt)], Now);

        AssertReset(caughtUp, "5h", 20, resetAt);
        Assert.IsTrue(caughtUp.ResetDiagnostics.Single().DeadlineCrossed);
        Assert.IsFalse(caughtUp.ResetDiagnostics.Single().CumulativeRecovery);
    }

    [TestMethod]
    public void OfflineCrossingIsCaughtUpByTheFirstSnapshotAfterReset()
    {
        var resetAt = Now.AddHours(-1);
        var beforeOffline = Reduce(null, [Window("5h", 300, 35, resetAt)], Now.AddHours(-2));
        var afterOffline = Reduce(beforeOffline.State, [Window("5h", 300, 25, resetAt)], Now);

        AssertReset(afterOffline, "5h", 25, resetAt);
    }

    [TestMethod]
    public async Task RestartLoadsPendingDeadlineAndCatchesUp()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        using var directory = new TestDirectory();
        var persistence = new PreviewPersistence(new JsonFileStore(), new PreviewDataPaths(directory.Path));
        await persistence.SaveAlertStateAsync(initial.State, CancellationToken.None);

        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var caughtUp = Reduce(restored, [Window("5h", 300, 20, resetAt)], Now);

        Assert.IsNotNull(restored);
        AssertReset(caughtUp, "5h", 20, resetAt);
        Assert.AreEqual(resetAt, restored!.Windows["5h"].PendingResetDeadlineUtc);
    }

    [TestMethod]
    public void FirstSnapshotAlreadyInTheNextCycleCatchesUpTheOldDeadline()
    {
        var oldDeadline = Now.AddHours(-6);
        var nextDeadline = oldDeadline.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 25, oldDeadline)], Now.AddHours(-7));
        var nextCycle = Reduce(initial.State, [Window("5h", 300, 25, nextDeadline)], Now);

        AssertReset(nextCycle, "5h", 25, nextDeadline);
        Assert.AreEqual(oldDeadline, nextCycle.ResetDiagnostics.Single().PendingResetDeadlineUtc);
        Assert.AreNotEqual(
            nextCycle.State.Windows["5h"].PendingResetDeadlineUtc,
            nextCycle.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void MissingOrUnreliablePercentageStillCatchesUpPastDeadline()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        var missing = Reduce(
            initial.State,
            [Window("5h", 300, null, resetAt, reliable: false)],
            Now);

        AssertReset(missing, "5h", null, resetAt);
        Assert.IsTrue(missing.ResetDiagnostics.Single().DeadlineCrossed);
        Assert.IsNull(missing.ResetDiagnostics.Single().CurrentRemainingPercent);
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
    public void SinkFailureModelDoesNotAcknowledgeAndNextEvaluationRetries()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        var attempted = Reduce(initial.State, [Window("5h", 300, 20, resetAt)], Now);
        var retried = Reduce(initial.State, [Window("5h", 300, 20, resetAt)], Now.AddMinutes(1));

        AssertReset(attempted, "5h", 20, resetAt);
        AssertReset(retried, "5h", 20, resetAt);
        Assert.IsNull(initial.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        Assert.AreEqual(resetAt, attempted.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void SuccessfulAcknowledgementDeduplicatesTheSameSnapshot()
    {
        var resetAt = Now.AddHours(-1);
        var initial = Reduce(null, [Window("5h", 300, 20, resetAt)], Now.AddHours(-2));
        var delivered = Reduce(initial.State, [Window("5h", 300, 20, resetAt)], Now);
        var repeated = Reduce(delivered.State, [Window("5h", 300, 20, resetAt)], Now.AddMinutes(1));

        AssertReset(delivered, "5h", 20, resetAt);
        Assert.IsNull(repeated.Alert);
        Assert.AreEqual(resetAt, delivered.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void FiveHourAndSevenDayWindowsResetIndependently()
    {
        var fiveHourReset = Now.AddHours(-1);
        var sevenDayReset = Now.AddDays(-1);
        var initial = Reduce(
            null,
            [Window("5h", 300, 20, fiveHourReset), Window("7d", 10_080, 30, sevenDayReset)],
            Now.AddHours(-2));
        var caughtUp = Reduce(
            initial.State,
            [Window("5h", 300, 20, fiveHourReset), Window("7d", 10_080, 30, sevenDayReset)],
            Now);

        Assert.AreEqual(QuotaAlertKind.Reset, caughtUp.Alert!.Kind);
        Assert.HasCount(2, caughtUp.Alert.ResetWindows);
        Assert.AreEqual(fiveHourReset, caughtUp.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
        Assert.AreEqual(sevenDayReset, caughtUp.State.Windows["7d"].LastNotifiedResetDeadlineUtc);
    }

    [TestMethod]
    public void ReorderedWindowsKeepIndependentState()
    {
        var fiveHourReset = Now.AddHours(-1);
        var sevenDayReset = Now.AddDays(-1);
        var initial = Reduce(
            null,
            [Window("5h", 300, 20, fiveHourReset), Window("7d", 10_080, 20, sevenDayReset)],
            Now.AddHours(-2));
        var reordered = Reduce(
            initial.State,
            [Window("7d", 10_080, 20, sevenDayReset), Window("5h", 300, 20, fiveHourReset)],
            Now);

        Assert.HasCount(2, reordered.Alert!.ResetWindows);
        Assert.AreEqual(300, reordered.State.Windows["5h"].WindowDurationMinutes);
        Assert.AreEqual(10_080, reordered.State.Windows["7d"].WindowDurationMinutes);
        CollectionAssert.AreEquivalent(new[] { "5h", "7d" }, reordered.Alert.ResetWindows.Select(window => window.WindowName).ToArray());
    }

    [TestMethod]
    public void MultipleMissedCyclesProduceAtLeastOneEventPerSuccessfulEvaluation()
    {
        var oldDeadline = Now.AddHours(-12);
        var nextDeadline = oldDeadline.AddHours(5);
        var initial = Reduce(null, [Window("5h", 300, 20, oldDeadline)], Now.AddHours(-13));
        var firstCatchUp = Reduce(initial.State, [Window("5h", 300, 20, nextDeadline)], Now);
        var secondCatchUp = Reduce(firstCatchUp.State, [Window("5h", 300, 20, nextDeadline)], Now.AddMinutes(1));

        AssertReset(firstCatchUp, "5h", 20, nextDeadline);
        AssertReset(secondCatchUp, "5h", 20, nextDeadline);
        Assert.AreNotEqual(
            firstCatchUp.State.Windows["5h"].LastNotifiedResetDeadlineUtc,
            secondCatchUp.State.Windows["5h"].LastNotifiedResetDeadlineUtc);
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
    public async Task OldAlertStateJsonMigratesWithoutStartupAlertAndRemainsFailOpen()
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
        var realReset = Reduce(startup.State, [Window("5h", 300, 100, resetAt)], Now.AddMinutes(1));

        Assert.IsNotNull(restored);
        Assert.IsNull(startup.Alert);
        AssertReset(realReset, "5h", 100, resetAt);
        Assert.IsTrue(startup.State.Windows["5h"].ResetAlertMigrationPending);
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
        StringAssert.Contains(formatted, "resetDetected=True");
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
