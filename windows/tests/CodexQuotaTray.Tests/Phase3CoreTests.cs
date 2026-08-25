using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Runtime;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class Phase3CoreTests
{
    [TestMethod]
    public void RefreshCoordinator_LeavesReasonGatingToRuntime()
    {
        var coordinator = new RefreshCoordinator();
        coordinator.SetMode(RefreshMode.ManualOnly);
        Assert.AreEqual(RefreshDecision.Start, coordinator.Request(RefreshReason.CardOpened));
        Assert.IsNull(coordinator.Complete(true, DateTimeOffset.UnixEpoch));
        Assert.AreEqual(RefreshDecision.Start, coordinator.Request(RefreshReason.Manual));
    }

    [TestMethod]
    public void RefreshCoordinator_QueuesOnlyHighestPriorityAndMaintainsSingleFlight()
    {
        var coordinator = new RefreshCoordinator();
        Assert.AreEqual(RefreshDecision.Start, coordinator.Request(RefreshReason.Scheduled));
        Assert.AreEqual(RefreshDecision.Queue, coordinator.Request(RefreshReason.CardOpened));
        Assert.AreEqual(RefreshDecision.Queue, coordinator.Request(RefreshReason.Manual));
        Assert.AreEqual(RefreshReason.Manual, coordinator.PendingReason);
        Assert.AreEqual(RefreshReason.Manual, coordinator.Complete(true, DateTimeOffset.UnixEpoch));
        Assert.IsNull(coordinator.Complete(true, DateTimeOffset.UnixEpoch.AddMinutes(1)));
    }

    [TestMethod]
    public void RefreshCoordinator_HandoffKeepsLaterRequestsAndExecutionOwnership()
    {
        var coordinator = new RefreshCoordinator();
        Assert.AreEqual(RefreshDecision.Start, coordinator.Request(RefreshReason.RateLimitNotification));
        Assert.AreEqual(RefreshDecision.Queue, coordinator.Request(RefreshReason.Manual));

        var handedOff = coordinator.CompleteAndHandoff(false, DateTimeOffset.UnixEpoch);

        Assert.AreEqual(RefreshReason.Manual, handedOff);
        Assert.IsTrue(coordinator.IsInFlight);
        Assert.AreEqual(RefreshDecision.Queue, coordinator.Request(RefreshReason.NetworkRestored));
        Assert.AreEqual(RefreshReason.NetworkRestored, coordinator.PendingReason);

        var next = coordinator.CompleteAndHandoff(true, DateTimeOffset.UnixEpoch.AddMinutes(1));

        Assert.AreEqual(RefreshReason.NetworkRestored, next);
        Assert.IsTrue(coordinator.IsInFlight);
        Assert.IsNull(coordinator.CompleteAndHandoff(true, DateTimeOffset.UnixEpoch.AddMinutes(2)));
        Assert.IsFalse(coordinator.IsInFlight);
    }

    [TestMethod]
    public void RefreshIntervalsAndStaleThresholdsMatchPolicy()
    {
        var coordinator = new RefreshCoordinator();
        Assert.AreEqual(RefreshMode.Every15Minutes, coordinator.Mode);
        foreach (var mode in new[] { RefreshMode.Every5Minutes, RefreshMode.Every15Minutes, RefreshMode.Every30Minutes })
        {
            coordinator.SetMode(mode);
            var expected = mode switch
            {
                RefreshMode.Every5Minutes => TimeSpan.FromMinutes(5),
                RefreshMode.Every15Minutes => TimeSpan.FromMinutes(15),
                _ => TimeSpan.FromMinutes(30),
            };
            Assert.AreEqual(expected, coordinator.EffectiveInterval(100));
            Assert.AreEqual(expected, coordinator.EffectiveInterval(20));
            Assert.AreEqual(expected, coordinator.EffectiveInterval(null));
        }

        coordinator.SetMode(RefreshMode.Auto);
        Assert.AreEqual(RefreshMode.Every15Minutes, coordinator.Mode);
        Assert.AreEqual(TimeSpan.FromMinutes(30), coordinator.StaleAfter(51));
        coordinator.SetMode(RefreshMode.ManualOnly);
        Assert.AreEqual(TimeSpan.FromMinutes(60), coordinator.StaleAfter(1));
    }

    [TestMethod]
    public async Task JsonLineRpc_PreservesIngressOrderAcrossResponseAndNotification()
    {
        var input = new ChannelTextReader();
        var output = new RecordingTextWriter();
        await using var rpc = new JsonLineRpcConnection(input, output);
        var request = rpc.RequestWithSequenceAsync(
            "account/rateLimits/read",
            null,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        await output.WaitForLinesAsync(1);
        var id = JsonDocument.Parse(output.Lines[0]).RootElement.GetProperty("id").GetInt64();

        input.Write("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
        input.Write($"{{\"id\":{id},\"result\":{{}}}}");

        var notifications = rpc.ReadNotificationsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await notifications.MoveNextAsync());
        var notification = notifications.Current;
        Assert.IsFalse(request.IsCompleted);
        notification.Acknowledge();

        var response = await request;
        Assert.IsTrue(notification.IngressSequence < response.IngressSequence);
    }

    [TestMethod]
    public async Task JsonLineRpc_RecoveryReadKeepsIngressSequenceWithoutWaitingForBarrier()
    {
        var input = new ChannelTextReader();
        var output = new RecordingTextWriter();
        await using var rpc = new JsonLineRpcConnection(input, output);
        var request = rpc.RequestWithSequenceAsync(
            "account/rateLimits/read",
            null,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            waitForIngressBarrier: false);
        await output.WaitForLinesAsync(1);
        var id = JsonDocument.Parse(output.Lines[0]).RootElement.GetProperty("id").GetInt64();

        input.Write("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
        input.Write($"{{\"id\":{id},\"result\":{{}}}}");

        await using var notifications = rpc.ReadNotificationsAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await notifications.MoveNextAsync());
        var notification = notifications.Current;
        var response = await request.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.IsTrue(notification.IngressSequence > 0);
        Assert.IsTrue(response.IngressSequence > notification.IngressSequence);
        notification.Acknowledge();
    }

    [TestMethod]
    public async Task JsonLineRpc_ReportsBoundedNotificationOverflow()
    {
        var input = new ChannelTextReader();
        var output = new RecordingTextWriter();
        await using var rpc = new JsonLineRpcConnection(input, output);
        for (var index = 0; index < 64; index++)
        {
            input.Write(JsonSerializer.Serialize(new
            {
                method = "account/rateLimits/updated",
                @params = new
                {
                    rateLimits = new { primary = new { usedPercent = index } },
                },
            }));
        }
        await Task.Delay(100);

        var notifications = rpc.ReadNotificationsAsync(CancellationToken.None).GetAsyncEnumerator();
        var sawOverflow = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (await notifications.MoveNextAsync().AsTask().WaitAsync(timeout.Token))
        {
            var notification = notifications.Current;
            if (notification.IsOverflow)
            {
                sawOverflow = true;
                notification.Acknowledge();
                break;
            }

            notification.Acknowledge();
        }

        Assert.IsTrue(sawOverflow);
        await rpc.DisposeAsync();
    }

    [TestMethod]
    public async Task JsonLineRpc_OverflowWakesAnAlreadyRunningReaderAndReleasesResponseBarrier()
    {
        var input = new ChannelTextReader();
        var output = new RecordingTextWriter();
        await using var rpc = new JsonLineRpcConnection(input, output);
        var request = rpc.RequestWithSequenceAsync(
            "account/rateLimits/read",
            null,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);
        await output.WaitForLinesAsync(1);
        var id = JsonDocument.Parse(output.Lines[0]).RootElement.GetProperty("id").GetInt64();

        var notifications = rpc.ReadNotificationsAsync(CancellationToken.None).GetAsyncEnumerator();
        var first = notifications.MoveNextAsync().AsTask();
        input.Write("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(1)));
        notifications.Current.Acknowledge();

        var second = notifications.MoveNextAsync().AsTask();
        for (var index = 0; index < 96; index++)
        {
            input.Write(JsonSerializer.Serialize(new
            {
                method = "account/rateLimits/updated",
                @params = new { rateLimits = new { primary = new { usedPercent = index } } },
            }));
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await input.WaitForReadCountAsync(97, timeout.Token);
        Assert.IsTrue(await second.WaitAsync(TimeSpan.FromSeconds(1)));
        input.Write($"{{\"id\":{id},\"result\":{{}}}}");
        await input.WaitForReadCountAsync(98, timeout.Token);

        var notification = notifications.Current;
        var sawOverflow = false;
        while (true)
        {
            if (notification.IsOverflow)
            {
                sawOverflow = true;
                Assert.IsFalse(request.IsCompleted);
            }

            notification.Acknowledge();
            var next = notifications.MoveNextAsync().AsTask();
            var completed = await Task.WhenAny(next, request).WaitAsync(timeout.Token);
            if (completed == request)
            {
                break;
            }

            Assert.IsTrue(await next);
            notification = notifications.Current;
        }

        Assert.IsTrue(sawOverflow);
        await request;
        await rpc.DisposeAsync();
    }

    [TestMethod]
    public void RefreshBackoffResetsAfterSuccess()
    {
        var coordinator = new RefreshCoordinator();
        _ = coordinator.Request(RefreshReason.Manual);
        _ = coordinator.Complete(false, DateTimeOffset.UnixEpoch);
        Assert.AreEqual(TimeSpan.FromMinutes(15), coordinator.EffectiveInterval(90));
        _ = coordinator.Request(RefreshReason.Manual);
        _ = coordinator.Complete(true, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.AreEqual(TimeSpan.FromMinutes(15), coordinator.EffectiveInterval(90));
    }

    [TestMethod]
    public void SparseNotificationPreservesResetCreditsAndMissingWindowMetadata()
    {
        var baseline = new RateLimitsReadResult(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    LimitId = "private-id",
                    PlanType = "plus",
                    Primary = new RateLimitWindow { UsedPercent = 25, WindowDurationMinutes = 300, ResetsAt = 2000 },
                },
                RateLimitResetCredits = new RateLimitResetCreditsSummary { AvailableCount = 2 },
            },
            true);
        var patch = new RateLimitsUpdatedNotification(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    Primary = new RateLimitWindow { UsedPercent = 26 },
                },
            },
            false);

        var merged = RateLimitsSnapshotMerger.Merge(baseline, patch).Snapshot!;
        Assert.AreEqual(26, merged.Response.RateLimits!.Primary!.UsedPercent);
        Assert.AreEqual(300, merged.Response.RateLimits.Primary.WindowDurationMinutes);
        Assert.AreEqual(2, merged.Response.RateLimitResetCredits!.AvailableCount);
        Assert.IsTrue(merged.ResetCreditsFieldPresent);
    }

    [TestMethod]
    public void SparseNotificationWithoutBaselineRequestsFullRead()
    {
        var patch = new RateLimitsUpdatedNotification(new RateLimitsResponse(), false);
        var merged = RateLimitsSnapshotMerger.Merge(null, patch);
        Assert.IsNull(merged.Snapshot);
        Assert.IsTrue(merged.RequiresFullRead);
    }

    [TestMethod]
    public void IndependentSnapshotRequiresIdentityAndCompleteWindows()
    {
        var complete = new RateLimitsResponse
        {
            RateLimits = new RateLimitSnapshot
            {
                LimitId = "private-id",
                Primary = CompleteWindow(20, 300, 2_000),
                Secondary = CompleteWindow(30, 10_080, 3_000),
            },
        };
        var missingIdentity = new RateLimitsResponse
        {
            RateLimits = new RateLimitSnapshot
            {
                Primary = CompleteWindow(20, 300, 2_000),
                Secondary = CompleteWindow(30, 10_080, 3_000),
            },
        };
        var missingSecondary = new RateLimitsResponse
        {
            RateLimits = new RateLimitSnapshot
            {
                LimitId = "private-id",
                Primary = CompleteWindow(20, 300, 2_000),
            },
        };
        var missingDuration = new RateLimitsResponse
        {
            RateLimits = new RateLimitSnapshot
            {
                LimitId = "private-id",
                Primary = new RateLimitWindow { UsedPercent = 20, ResetsAt = 2_000 },
                Secondary = CompleteWindow(30, 10_080, 3_000),
            },
        };
        var missingReset = new RateLimitsResponse
        {
            RateLimits = new RateLimitSnapshot
            {
                LimitId = "private-id",
                Primary = new RateLimitWindow { UsedPercent = 20, WindowDurationMinutes = 300 },
                Secondary = CompleteWindow(30, 10_080, 3_000),
            },
        };
        foreach (var (response, isComplete) in new[]
        {
            (complete, true),
            (missingIdentity, false),
            (missingSecondary, false),
            (missingDuration, false),
            (missingReset, false),
        })
        {
            var result = RateLimitsSnapshotMerger.Merge(
                null,
                new RateLimitsUpdatedNotification(response, false));
            Assert.AreEqual(isComplete, !result.RequiresFullRead);
        }
    }

    [TestMethod]
    public async Task JsonRpcEofCompletesNotificationStreamAsTransportClosed()
    {
        await using var connection = new JsonLineRpcConnection(new StringReader(string.Empty), new StringWriter());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var notifications = connection.ReadNotificationsAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var error = await Assert.ThrowsAsync<CodexClientException>(async () => await notifications.MoveNextAsync());
        Assert.AreEqual(CodexClientErrorKind.TransportClosed, error.Kind);
        await notifications.DisposeAsync();
    }

    [TestMethod]
    public async Task JsonRpcPublishesKnownAndUnknownNotificationsWithoutBreakingResponses()
    {
        var input = new NotificationReader();
        var output = new StringWriter();
        await using var connection = new JsonLineRpcConnection(input, output);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var notifications = connection.ReadNotificationsAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        input.Write("{\"method\":\"unknown/event\",\"params\":{}}");
        Assert.IsTrue(await notifications.MoveNextAsync());
        Assert.AreEqual("unknown/event", notifications.Current.Method);
        await notifications.DisposeAsync();
    }

    [TestMethod]
    public void AlertIdentityNeverPersistsRawStableIdentifier()
    {
        const string raw = "sensitive-limit-id";
        var value = QuotaWindowIdentity.CreateAlertKey(raw, null, "primary", 300, 0);
        Assert.IsTrue(value.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.IsFalse(value.Contains(raw, StringComparison.Ordinal));
        Assert.AreEqual(71, value.Length);
    }

    [TestMethod]
    public void BucketBackedAlertIdentityIsStableAcrossOrdinalChanges()
    {
        var first = QuotaWindowIdentity.CreateAlertKey(null, "stable-bucket", "primary", 300, 0);
        var reordered = QuotaWindowIdentity.CreateAlertKey(null, "stable-bucket", "primary", 300, 4);

        Assert.AreEqual(first, reordered);
        Assert.IsTrue(first.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.AreEqual(
            "fallback:primary:300:4",
            QuotaWindowIdentity.CreateLegacyAlertKey(null, "stable-bucket", "primary", 300, 4));
    }

    [TestMethod]
    public void BucketBackedAlertIdentityMigratesLegacyFallbackHistory()
    {
        const string legacyKey = "fallback:primary:300:0";
        var stableKey = QuotaWindowIdentity.CreateAlertKey(null, "stable-bucket", "primary", 300, 0);
        var previousWindow = new AlertWindowState(
            legacyKey,
            300,
            DateTimeOffset.UnixEpoch.AddMinutes(300),
            40,
            [50],
            null,
            false);
        var previous = new AlertStateDocument(
            1,
            [50, 20, 10],
            new Dictionary<string, AlertWindowState> { [legacyKey] = previousWindow },
            true);
        var input = new AlertInput(
            stableKey,
            "5 小时额度",
            39,
            true,
            300,
            DateTimeOffset.UnixEpoch.AddMinutes(300),
            legacyKey);

        var reduction = QuotaAlertReducer.Reduce(previous, [input], new NotificationSettings(true, true, true));

        Assert.IsFalse(reduction.State.Windows.ContainsKey(legacyKey));
        Assert.IsTrue(reduction.State.Windows.ContainsKey(stableKey));
        CollectionAssert.Contains(reduction.State.Windows[stableKey].HandledThresholds.ToArray(), 50);
        Assert.IsNull(reduction.Alert);
    }

    [TestMethod]
    public void AlertIdentity_SameLimitIdSurvivesReconnectAndEmitsReset()
    {
        var before = LegacySnapshot("codex", Window(92, 300, 1_900_000_000));
        var after = LegacySnapshot("codex", Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        AssertResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_LimitIdToSameBucketSurvivesReconnectAndEmitsReset()
    {
        var before = BucketSnapshot("codex", "codex", Window(92, 300, 1_900_000_000));
        var after = BucketSnapshot("codex", null, Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        AssertResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_MissingLimitIdThenAppearingDoesNotMigrateFallbackAndMissesReset()
    {
        var before = LegacySnapshot(null, Window(92, 300, 1_900_000_000));
        var after = LegacySnapshot("codex", Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreNotEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        Assert.IsNull(afterWindow.LegacyAlertKey);
        AssertNoResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_MissingLimitIdThenAppearingWithSameBucketSurvivesReconnect()
    {
        var before = BucketSnapshot("codex", null, Window(92, 300, 1_900_000_000));
        var after = BucketSnapshot("codex", "codex", Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        AssertResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_ChangedLimitIdDoesNotMigrateWhenBucketAndWindowStaySame()
    {
        var before = BucketSnapshot("codex", "codex", Window(92, 300, 1_900_000_000));
        var after = BucketSnapshot("codex", "codex-renamed", Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreEqual("codex", beforeWindow.BucketId);
        Assert.AreEqual("codex", afterWindow.BucketId);
        Assert.AreNotEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        Assert.IsNull(afterWindow.LegacyAlertKey);
        AssertNoResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_LegacyAndBucketCollectionFormsSurviveWhenStableIdentityIsPreserved()
    {
        var before = LegacySnapshot("codex", Window(92, 300, 1_900_000_000));
        var after = BucketSnapshot("codex", null, Window(0, 300, 1_900_018_000));

        AssertResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_BucketFallbackToLegacyWithoutLimitIdMissesReset()
    {
        var before = BucketSnapshot("codex", null, Window(92, 300, 1_900_000_000));
        var after = LegacySnapshot(null, Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreNotEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        Assert.IsNull(afterWindow.LegacyAlertKey);
        AssertNoResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_BucketSortIgnoresDictionaryOrderButFallbackOrdinalCanMissReset()
    {
        var before = BucketSnapshots(
            ("b", "b", Window(92, 300, 1_900_000_000), null),
            ("a", "a", Window(92, 10_080, 1_900_500_000), null));
        var after = BucketSnapshots(
            ("a", "a", Window(0, 10_080, 1_901_104_800), null),
            ("b", "b", Window(0, 300, 1_900_018_000), null));
        var beforeWindows = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows;
        var afterWindows = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows;

        CollectionAssert.AreEqual(
            beforeWindows.Select(window => window.AlertKey).ToArray(),
            afterWindows.Select(window => window.AlertKey).ToArray());
        var initial = QuotaAlertReducer.Reduce(null, ToAlertInputs(beforeWindows), new NotificationSettings());
        var reordered = QuotaAlertReducer.Reduce(initial.State, ToAlertInputs(afterWindows), new NotificationSettings());
        Assert.AreEqual(QuotaAlertKind.Reset, reordered.Alert!.Kind);
        Assert.HasCount(2, reordered.Alert.ResetWindows);

        var fallbackBefore = LegacySnapshot(
            null,
            primary: null,
            secondary: Window(92, 10_080, 1_900_000_000));
        var fallbackAfter = LegacySnapshot(
            null,
            primary: Window(20, 300, 1_900_018_000),
            secondary: Window(0, 10_080, 1_900_604_800));
        var fallbackBeforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(fallbackBefore, false)).Windows.Single(window => window.SourceSlot == "secondary");
        var fallbackAfterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(fallbackAfter, false)).Windows.Single(window => window.SourceSlot == "secondary");

        Assert.AreNotEqual(fallbackBeforeWindow.AlertKey, fallbackAfterWindow.AlertKey);
        AssertNoResetTransition(fallbackBefore, fallbackAfter);
    }

    [TestMethod]
    public void AlertIdentity_SourceSlotChangeMissesResetEvenWithSameLimitId()
    {
        var before = LegacySnapshot("codex", primary: Window(92, 300, 1_900_000_000));
        var after = LegacySnapshot("codex", secondary: Window(0, 300, 1_900_018_000));
        var beforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false)).Windows.Single();
        var afterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false)).Windows.Single();

        Assert.AreNotEqual(beforeWindow.AlertKey, afterWindow.AlertKey);
        AssertNoResetTransition(before, after);
    }

    [TestMethod]
    public void AlertIdentity_DurationIsIgnoredForStableKeyButPartOfFallbackKey()
    {
        var stableBefore = LegacySnapshot("codex", Window(92, 300, 1_900_000_000));
        var stableAfter = LegacySnapshot("codex", Window(0, 600, 1_900_018_000));
        var stableBeforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(stableBefore, false)).Windows.Single();
        var stableAfterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(stableAfter, false)).Windows.Single();

        Assert.AreEqual(stableBeforeWindow.AlertKey, stableAfterWindow.AlertKey);
        AssertResetTransition(stableBefore, stableAfter);

        var fallbackBefore = LegacySnapshot(null, Window(92, 300, 1_900_000_000));
        var fallbackAfter = LegacySnapshot(null, Window(0, 600, 1_900_018_000));
        var fallbackBeforeWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(fallbackBefore, false)).Windows.Single();
        var fallbackAfterWindow = QuotaNormalizer.Normalize(new RateLimitsReadResult(fallbackAfter, false)).Windows.Single();

        Assert.AreNotEqual(fallbackBeforeWindow.AlertKey, fallbackAfterWindow.AlertKey);
        AssertNoResetTransition(fallbackBefore, fallbackAfter);
    }

    [TestMethod]
    public void FirstAlertSnapshotEstablishesBaselineWithoutNotification()
    {
        var input = Input(19);
        var reduction = QuotaAlertReducer.Reduce(null, [input], new NotificationSettings());
        Assert.IsNull(reduction.Alert);
        Assert.AreEqual(19, reduction.State.Windows[input.PseudonymousKey].LastReliableRemaining);
        Assert.IsTrue(reduction.State.ResetAlertBaselineEstablished);
    }

    [DataRow(98)]
    [DataRow(97)]
    [TestMethod]
    public void ResetCycleWithSignificantRecoveryEmitsOneResetAlert(int remaining)
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(20)], new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var reduction = QuotaAlertReducer.Reduce(
            first.State,
            [Input(remaining, resetAt)],
            new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Reset, reduction.Alert!.Kind);
        Assert.HasCount(1, reduction.Alert.ResetWindows);
        Assert.AreEqual(remaining, reduction.Alert.ResetWindows[0].RemainingPercent);
        Assert.AreEqual(resetAt, reduction.State.Windows["window"].LastResetAlertCycleUtc);
    }

    [TestMethod]
    public void StrongRecoveryUsesTheSameTransitionForNewCycleAndResetAlert()
    {
        var first = QuotaAlertReducer.Reduce(
            null,
            [Input(8, DateTimeOffset.UnixEpoch.AddDays(7))],
            new NotificationSettings());
        var current = Input(100, DateTimeOffset.UnixEpoch.AddDays(7));

        Assert.IsTrue(QuotaAlertReducer.IsNewCycle(first.State.Windows["window"], current));
        Assert.IsTrue(QuotaAlertReducer.IsResetCycle(first.State.Windows["window"], current));
        var reduction = QuotaAlertReducer.Reduce(first.State, [current], new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Reset, reduction.Alert!.Kind);
        Assert.IsTrue(reduction.State.Windows["window"].ResetAlertCycleConsumed);
    }

    [TestMethod]
    public void StrongRecoveryWorksWhenResetTimeIsDelayedOrMissing()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(20)], new NotificationSettings());
        var delayed = QuotaAlertReducer.Reduce(
            first.State,
            [Input(90, DateTimeOffset.UnixEpoch.AddDays(7).AddMinutes(1))],
            new NotificationSettings());
        Assert.AreEqual(QuotaAlertKind.Reset, delayed.Alert!.Kind);

        var missingFirst = QuotaAlertReducer.Reduce(
            null,
            [InputWithoutResetAt(20)],
            new NotificationSettings());
        var missing = QuotaAlertReducer.Reduce(
            missingFirst.State,
            [InputWithoutResetAt(90)],
            new NotificationSettings());
        Assert.AreEqual(QuotaAlertKind.Reset, missing.Alert!.Kind);
    }

    [TestMethod]
    public void StrongRecoveryStartsNextCycleWhenPreviousCycleWasConsumedAndResetTimeIsDelayed()
    {
        var t1 = DateTimeOffset.UnixEpoch.AddDays(7);
        var t2 = t1.AddDays(7);
        var t3 = t2.AddDays(7);
        var settings = new NotificationSettings();

        var initialLow = QuotaAlertReducer.Reduce(null, [Input(20, t1)], settings);
        var firstReset = QuotaAlertReducer.Reduce(initialLow.State, [Input(95, t2)], settings);
        var usedAgain = QuotaAlertReducer.Reduce(firstReset.State, [Input(20, t2)], settings);
        var delayedSecondReset = QuotaAlertReducer.Reduce(usedAgain.State, [Input(95, t2)], settings);
        var repeatedHigh = QuotaAlertReducer.Reduce(delayedSecondReset.State, [Input(94, t2)], settings);
        var metadataCatchUp = QuotaAlertReducer.Reduce(repeatedHigh.State, [Input(94, t3)], settings);
        var usedThirdCycle = QuotaAlertReducer.Reduce(metadataCatchUp.State, [Input(20, t3)], settings);
        var delayedThirdReset = QuotaAlertReducer.Reduce(usedThirdCycle.State, [Input(95, t3)], settings);

        Assert.AreEqual(QuotaAlertKind.Reset, firstReset.Alert!.Kind);
        Assert.AreEqual(QuotaAlertKind.Reset, delayedSecondReset.Alert!.Kind);
        Assert.IsNull(repeatedHigh.Alert);
        Assert.IsFalse(QuotaAlertReducer.IsCycleTransition(
            repeatedHigh.State.Windows["window"],
            Input(94, t3)));
        Assert.IsNull(metadataCatchUp.Alert);
        Assert.AreEqual(t3, metadataCatchUp.State.Windows["window"].LastResetAlertCycleUtc);
        Assert.AreEqual(QuotaAlertKind.Reset, delayedThirdReset.Alert!.Kind);
    }

    [TestMethod]
    public async Task PendingResetMetadataSurvivesRestartAndIgnoresStaleCycleTimestamp()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var t1 = DateTimeOffset.UnixEpoch.AddDays(7);
        var t2 = t1.AddDays(7);
        var t3 = t2.AddDays(7);
        var settings = new NotificationSettings();
        var initial = QuotaAlertReducer.Reduce(null, [Input(20, t1)], settings);
        var firstReset = QuotaAlertReducer.Reduce(initial.State, [Input(95, t2)], settings);
        var low = QuotaAlertReducer.Reduce(firstReset.State, [Input(20, t2)], settings);
        var resetWithoutMetadata = QuotaAlertReducer.Reduce(
            low.State,
            [InputWithoutResetAt(95)],
            settings);
        await persistence.SaveAlertStateAsync(resetWithoutMetadata.State, CancellationToken.None);

        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var staleTimestamp = QuotaAlertReducer.Reduce(restored, [Input(94, t2)], settings);
        var metadataCatchUp = QuotaAlertReducer.Reduce(staleTimestamp.State, [Input(94, t3)], settings);

        Assert.AreEqual(QuotaAlertKind.Reset, resetWithoutMetadata.Alert!.Kind);
        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.Windows["window"].ResetAlertAwaitingCycleMetadata);
        Assert.IsNull(staleTimestamp.Alert);
        Assert.IsTrue(staleTimestamp.State.Windows["window"].ResetAlertAwaitingCycleMetadata);
        Assert.IsNull(metadataCatchUp.Alert);
        Assert.IsFalse(metadataCatchUp.State.Windows["window"].ResetAlertAwaitingCycleMetadata);
        Assert.AreEqual(t3, metadataCatchUp.State.Windows["window"].LastResetAlertCycleUtc);
    }

    [TestMethod]
    public async Task PendingMetadataDoesNotConsumeMultiWindowResetJumpAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var t1 = DateTimeOffset.UnixEpoch.AddDays(7);
        var t2 = t1.AddDays(7);
        var t4 = t2.AddDays(14);
        var settings = new NotificationSettings(true, true, true);
        var initial = QuotaAlertReducer.Reduce(null, [Input(20, t1)], settings);
        var firstReset = QuotaAlertReducer.Reduce(initial.State, [Input(95, t2)], settings);
        var low = QuotaAlertReducer.Reduce(firstReset.State, [Input(20, t2)], settings);
        var delayedSecondReset = QuotaAlertReducer.Reduce(low.State, [Input(95, t2)], settings);
        var usedSecondCycle = QuotaAlertReducer.Reduce(delayedSecondReset.State, [Input(50, t2)], settings);
        await persistence.SaveAlertStateAsync(usedSecondCycle.State, CancellationToken.None);

        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var skippedCycleReset = QuotaAlertReducer.Reduce(restored, [Input(95, t4)], settings);
        var thresholdRearmed = QuotaAlertReducer.Reduce(skippedCycleReset.State, [Input(50, t4)], settings);

        Assert.AreEqual(QuotaAlertKind.Reset, firstReset.Alert!.Kind);
        Assert.AreEqual(QuotaAlertKind.Reset, delayedSecondReset.Alert!.Kind);
        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.Windows["window"].ResetAlertAwaitingCycleMetadata);
        CollectionAssert.Contains(restored.Windows["window"].HandledThresholds.ToArray(), 50);
        Assert.AreEqual(QuotaAlertKind.Reset, skippedCycleReset.Alert!.Kind);
        Assert.IsFalse(skippedCycleReset.State.Windows["window"].ResetAlertAwaitingCycleMetadata);
        Assert.AreEqual(t4, skippedCycleReset.State.Windows["window"].LastResetAlertCycleUtc);
        Assert.AreEqual(50, thresholdRearmed.Alert!.Threshold);
    }

    [DataRow(40, 60)]
    [DataRow(70, 100)]
    [DataRow(85, 100)]
    [TestMethod]
    public void OrdinaryRecoveryDoesNotStartResetCycle(int previous, int current)
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(previous)], new NotificationSettings());
        var next = QuotaAlertReducer.Reduce(first.State, [Input(current)], new NotificationSettings());
        Assert.IsFalse(QuotaAlertReducer.IsResetCycle(first.State.Windows["window"], Input(current)));
        Assert.IsNull(next.Alert);
    }

    [TestMethod]
    public void ResetAlertCombinesMultipleWindowsAndDoesNotRepeatSameCycle()
    {
        var first = QuotaAlertReducer.Reduce(
            null,
            [Input(20), Input(30, key: "other")],
            new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var next = QuotaAlertReducer.Reduce(
            first.State,
            [Input(98, resetAt), Input(97, resetAt, key: "other")],
            new NotificationSettings());
        var repeat = QuotaAlertReducer.Reduce(
            next.State,
            [Input(96, resetAt), Input(95, resetAt, key: "other")],
            new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Reset, next.Alert!.Kind);
        Assert.HasCount(2, next.Alert.ResetWindows);
        Assert.IsNull(repeat.Alert);
    }

    [TestMethod]
    public void ThresholdAndResetAlertsCombineIntoOneStructuredAlert()
    {
        var first = QuotaAlertReducer.Reduce(
            null,
            [
                Input(80, key: "reset") with { WindowName = "reset window" },
                Input(21, key: "threshold") with { WindowName = "threshold window" },
            ],
            new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var next = QuotaAlertReducer.Reduce(
            first.State,
            [
                Input(98, resetAt, key: "reset") with { WindowName = "reset window" },
                Input(10, key: "threshold") with { WindowName = "threshold window" },
            ],
            new NotificationSettings());
        var repeat = QuotaAlertReducer.Reduce(
            next.State,
            [
                Input(98, resetAt, key: "reset") with { WindowName = "reset window" },
                Input(10, key: "threshold") with { WindowName = "threshold window" },
            ],
            new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Composite, next.Alert!.Kind);
        Assert.AreEqual("threshold window", next.Alert.WindowName);
        Assert.AreEqual(10, next.Alert.Threshold);
        Assert.HasCount(1, next.Alert.ResetWindows);
        Assert.HasCount(1, next.Alert.ThresholdWindows);
        CollectionAssert.AreEqual(new[] { 10 }, next.Alert.ThresholdWindows.Select(window => window.Threshold).ToArray());
        CollectionAssert.Contains(next.State.Windows["threshold"].HandledThresholds.ToArray(), 10);
        Assert.AreEqual(resetAt, next.State.Windows["reset"].LastResetAlertCycleUtc);
        Assert.IsNull(repeat.Alert);
    }

    [TestMethod]
    public void ResetRequiresCycleChangeAndReliablePercentage()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(20)], new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var timeOnly = QuotaAlertReducer.Reduce(first.State, [Input(25, resetAt)], new NotificationSettings());
        var recoveryOnly = QuotaAlertReducer.Reduce(first.State, [Input(98)], new NotificationSettings());
        var untrusted = QuotaAlertReducer.Reduce(
            first.State,
            [Input(98, resetAt) with { IsPercentageReliable = false }],
            new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Reset, timeOnly.Alert!.Kind);
        Assert.AreEqual(QuotaAlertKind.Reset, recoveryOnly.Alert!.Kind);
        Assert.IsNull(untrusted.Alert);
    }

    [TestMethod]
    public void ResetWithNoUsageStillEmitsResetAlert()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(100)], new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var reduction = QuotaAlertReducer.Reduce(
            first.State,
            [Input(100, resetAt)],
            new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Reset, reduction.Alert!.Kind);
        Assert.AreEqual(100, reduction.Alert.ResetWindows[0].RemainingPercent);
    }

    [TestMethod]
    public async Task ResetAlertPersistsAcrossRestartAndOldAlertStateLoads()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var first = QuotaAlertReducer.Reduce(null, [Input(20)], new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var alerted = QuotaAlertReducer.Reduce(first.State, [Input(98, resetAt)], new NotificationSettings());
        await persistence.SaveAlertStateAsync(alerted.State, CancellationToken.None);

        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var repeat = QuotaAlertReducer.Reduce(restored, [Input(97, resetAt)], new NotificationSettings());
        await File.WriteAllTextAsync(
            paths.AlertState,
            "{\"schemaVersion\":1,\"baselineThresholds\":[20,10],\"windows\":{}}");
        var old = await persistence.LoadAlertStateAsync(CancellationToken.None);

        Assert.IsNotNull(restored);
        Assert.IsNull(repeat.Alert);
        Assert.IsNotNull(old);
        Assert.IsFalse(old.ResetAlertBaselineEstablished);
    }

    [TestMethod]
    public async Task OldAlertStateWithoutConsumedFieldMigratesExistingCycleAsConsumed()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(
            paths.AlertState,
            $$"""
            {
              "schemaVersion": 1,
              "baselineThresholds": [20, 10],
              "resetAlertBaselineEstablished": true,
              "windows": {
                "window": {
                  "pseudonymousKey": "window",
                  "windowDurationMinutes": 10080,
                  "resetAtUtc": "{{resetAt:O}}",
                  "lastReliableRemaining": 94,
                  "handledThresholds": [],
                  "lastResetAlertCycleUtc": "{{resetAt:O}}"
                }
              }
            }
            """);

        var restored = await persistence.LoadAlertStateAsync(CancellationToken.None);
        var normalized = QuotaAlertReducer.Reduce(restored, [Input(94, resetAt)], new NotificationSettings());

        Assert.IsNotNull(restored);
        Assert.IsNull(restored.Windows["window"].ResetAlertCycleConsumed);
        Assert.IsTrue(normalized.State.Windows["window"].ResetAlertCycleConsumed);
        Assert.IsNull(normalized.Alert);
    }

    [TestMethod]
    public void MigratedBaselineAllowsReliablyProvenResetAtTransition()
    {
        var t1 = DateTimeOffset.UnixEpoch.AddDays(7);
        var t2 = t1.AddDays(7);
        var previous = new AlertStateDocument(
            1,
            [20, 10],
            new Dictionary<string, AlertWindowState>
            {
                ["window"] = new("window", 10_080, t1, 25, [20], t1, true),
            });

        var reduction = QuotaAlertReducer.Reduce(previous, [Input(25, t2)], new NotificationSettings());

        Assert.AreEqual(QuotaAlertKind.Reset, reduction.Alert!.Kind);
        Assert.IsTrue(reduction.State.ResetAlertBaselineEstablished);
    }

    [TestMethod]
    public void DisabledResetAlertSuppressesCurrentCycleAndDoesNotBackfillWhenEnabled()
    {
        var disabled = new NotificationSettings(ResetAfterCycle: false);
        var first = QuotaAlertReducer.Reduce(null, [Input(20)], disabled);
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var suppressed = QuotaAlertReducer.Reduce(first.State, [Input(98, resetAt)], disabled);
        var enabledLater = QuotaAlertReducer.Reduce(suppressed.State, [Input(97, resetAt)], new NotificationSettings());

        Assert.IsNull(suppressed.Alert);
        Assert.AreEqual(resetAt, suppressed.State.Windows["window"].LastResetAlertCycleUtc);
        Assert.IsNull(enabledLater.Alert);
    }

    [TestMethod]
    public void AlertCrossingUsesPreviousGreaterAndCurrentLessOrEqual()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(21)], new NotificationSettings());
        var crossed = QuotaAlertReducer.Reduce(first.State, [Input(20)], new NotificationSettings());
        Assert.AreEqual(20, crossed.Alert!.Threshold);
        var equalAgain = QuotaAlertReducer.Reduce(crossed.State, [Input(20)], new NotificationSettings());
        Assert.IsNull(equalAgain.Alert);
    }

    [TestMethod]
    public void MultipleCrossingsEmitMostUrgentOneAndHandleAll()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(60)], new NotificationSettings(true, true, true));
        var crossed = QuotaAlertReducer.Reduce(first.State, [Input(9)], new NotificationSettings(true, true, true));
        Assert.AreEqual(10, crossed.Alert!.Threshold);
        CollectionAssert.AreEqual(new[] { 10 }, crossed.Alert.ThresholdWindows.Select(window => window.Threshold).ToArray());
        CollectionAssert.AreEquivalent(new[] { 50, 20, 10 }, crossed.State.Windows["window"].HandledThresholds.ToArray());
    }

    [TestMethod]
    public void InvalidPercentageDoesNotAdvanceAlertBaseline()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(30)], new NotificationSettings());
        var invalid = Input(101) with { IsPercentageReliable = false };
        var result = QuotaAlertReducer.Reduce(first.State, [invalid], new NotificationSettings());
        Assert.AreEqual(30, result.State.Windows["window"].LastReliableRemaining);
        Assert.IsNull(result.Alert);
    }

    [TestMethod]
    public void MissingWindowPreservesHistoryAndLaterResetStillAlerts()
    {
        var first = QuotaAlertReducer.Reduce(null, [Input(20)], new NotificationSettings());
        var partial = QuotaAlertReducer.Reduce(first.State, [], new NotificationSettings());
        var resetAt = DateTimeOffset.UnixEpoch.AddDays(14);
        var reset = QuotaAlertReducer.Reduce(
            partial.State,
            [Input(98, resetAt)],
            new NotificationSettings());

        Assert.IsTrue(partial.State.ResetAlertBaselineEstablished);
        Assert.AreEqual(20, partial.State.Windows["window"].LastReliableRemaining);
        Assert.AreEqual(QuotaAlertKind.Reset, reset.Alert!.Kind);
    }

    [TestMethod]
    public void PartialSnapshotPreservesOtherWindowAndReliableBaseline()
    {
        var first = QuotaAlertReducer.Reduce(
            null,
            [Input(20), Input(30, key: "other")],
            new NotificationSettings());
        var partial = QuotaAlertReducer.Reduce(
            first.State,
            [Input(25)],
            new NotificationSettings());

        Assert.IsTrue(partial.State.ResetAlertBaselineEstablished);
        Assert.AreEqual(30, partial.State.Windows["other"].LastReliableRemaining);
    }

    [TestMethod]
    public void SmallResetCorrectionDoesNotStartNewCycle()
    {
        var previous = new AlertWindowState("window", 10_080, DateTimeOffset.UnixEpoch.AddDays(7), 10, [20, 10]);
        var current = Input(11) with { WindowDurationMinutes = 10_080, ResetAtUtc = previous.ResetAtUtc!.Value.AddMinutes(6) };
        Assert.IsFalse(QuotaAlertReducer.IsNewCycle(previous, current));
        current = current with { ResetAtUtc = previous.ResetAtUtc.Value.AddDays(4) };
        Assert.IsTrue(QuotaAlertReducer.IsNewCycle(previous, current));
    }

    [TestMethod]
    public void ResetRequiresMatchingKnownPositiveDurations()
    {
        var previous = new AlertWindowState("window", 10_080, DateTimeOffset.UnixEpoch.AddDays(7), 20, []);
        var resetAt = previous.ResetAtUtc!.Value.AddDays(4);
        var valid = Input(25, resetAt) with { WindowDurationMinutes = 10_080 };

        Assert.IsTrue(QuotaAlertReducer.IsResetCycle(previous, valid));
        Assert.IsFalse(QuotaAlertReducer.IsResetCycle(previous, valid with { WindowDurationMinutes = null }));
        Assert.IsFalse(QuotaAlertReducer.IsResetCycle(previous, valid with { WindowDurationMinutes = 0 }));
        Assert.IsFalse(QuotaAlertReducer.IsResetCycle(previous, valid with { WindowDurationMinutes = -1 }));
        Assert.IsFalse(QuotaAlertReducer.IsResetCycle(previous, valid with { WindowDurationMinutes = 300 }));
        Assert.IsFalse(QuotaAlertReducer.IsResetCycle(previous with { WindowDurationMinutes = null }, valid));

        var state = new AlertStateDocument(
            1,
            [20, 10],
            new Dictionary<string, AlertWindowState> { ["window"] = previous },
            true);
        var baselineOnly = QuotaAlertReducer.Reduce(
            state,
            [valid with { WindowDurationMinutes = null }],
            new NotificationSettings());

        Assert.IsNull(baselineOnly.Alert);
        Assert.IsNull(baselineOnly.State.Windows["window"].LastResetAlertCycleUtc);
    }

    [TestMethod]
    public async Task PersistenceUsesCamelCaseAndKeepsAlertStateSeparateFromCacheDeletion()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        var persistence = new PreviewPersistence(store, paths);
        await persistence.SaveQuotaCacheAsync(new QuotaCacheDocument(1, DateTimeOffset.UnixEpoch, null, []), CancellationToken.None);
        await persistence.SaveAlertStateAsync(new AlertStateDocument(1, [20, 10], []), CancellationToken.None);
        await persistence.ClearQuotaCacheAsync();
        Assert.IsFalse(File.Exists(paths.QuotaCache));
        Assert.IsTrue(File.Exists(paths.AlertState));
        var json = await File.ReadAllTextAsync(paths.AlertState);
        StringAssert.Contains(json, "\"schemaVersion\"");
        Assert.IsFalse(json.Contains("SchemaVersion", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TokenUsageCacheRoundTripsAndCanBeCleared()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var date = new DateOnly(2026, 8, 12);
        var snapshot = new TokenUsageSnapshot(
            1,
            new DateTimeOffset(2026, 8, 12, 11, 24, 0, TimeSpan.Zero),
            "Asia/Shanghai",
            new TokenUsageSummary(128_392, 128_392, 128_392, 128_392, 128_392, date, 1, 1, 1),
            [new TokenUsageDay(date, 128_392, null, null, null, null)],
            1,
            10,
            date,
            date);

        await persistence.SaveTokenUsageCacheAsync(snapshot, CancellationToken.None);
        var loaded = await persistence.LoadTokenUsageCacheAsync(CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(snapshot.GeneratedAtUtc, loaded.GeneratedAtUtc);
        Assert.AreEqual(128_392, loaded.Summary.TodayTokens);
        Assert.HasCount(1, loaded.Days);

        await persistence.ClearTokenUsageCacheAsync();

        Assert.IsFalse(File.Exists(paths.TokenUsageCache));
        Assert.IsNull(await persistence.LoadTokenUsageCacheAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task SettingsMigratesLegacyRefreshAndNotifications()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(paths.Settings, "{\"use24HourTime\":false,\"refreshMinutes\":15,\"notifyRemaining20\":false,\"notifyRemaining5\":false,\"notifyExhausted\":false}");
        var service = new SettingsService(new JsonFileStore(), paths);
        var settings = await service.LoadAsync(CancellationToken.None);
        Assert.AreEqual(RefreshMode.Every15Minutes, settings.RefreshMode);
        Assert.AreEqual(RefreshMode.Every15Minutes, settings.TokenRefreshMode);
        Assert.IsTrue(settings.Use24HourTime);
        Assert.IsTrue(settings.RefreshOnPanelOpen);
        Assert.IsTrue(settings.TokenRefreshOnPanelOpen);
        Assert.IsTrue(settings.PersistTokenUsageCache);
        Assert.IsFalse(settings.EffectiveNotifications.Remaining20);
        Assert.IsFalse(settings.EffectiveNotifications.Remaining10);
        Assert.IsTrue(settings.EffectiveNotifications.ResetAfterCycle);

        await service.SaveAsync(settings with { Use24HourTime = false }, CancellationToken.None);
        var savedJson = await File.ReadAllTextAsync(paths.Settings);
        StringAssert.Contains(savedJson, "\"use24HourTime\": true");
    }

    [TestMethod]
    public async Task SettingsUsesDarkForFirstInstallAndPreservesLegacyThemeFallback()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var service = new SettingsService(new JsonFileStore(), paths);

        var firstInstall = await service.LoadAsync(CancellationToken.None);
        Assert.AreEqual(ThemeMode.Dark, firstInstall.ThemeMode);

        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(paths.Settings, "{\"startWithWindows\":true}");
        var legacy = await service.LoadAsync(CancellationToken.None);
        Assert.AreEqual(ThemeMode.System, legacy.ThemeMode);

        await File.WriteAllTextAsync(paths.Settings, "{\"themeMode\":\"light\"}");
        var explicitTheme = await service.LoadAsync(CancellationToken.None);
        Assert.AreEqual(ThemeMode.Light, explicitTheme.ThemeMode);
    }

    [TestMethod]
    public async Task SettingsMigratesLegacyAutoAndNormalizesSavedRefreshMode()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(paths.Settings, "{\"refreshMode\":\"auto\",\"tokenRefreshMode\":\"auto\"}");
        var service = new SettingsService(new JsonFileStore(), paths);

        var loaded = await service.LoadAsync(CancellationToken.None);

        Assert.AreEqual(RefreshMode.Every15Minutes, loaded.RefreshMode);
        Assert.AreEqual(RefreshMode.Every15Minutes, loaded.TokenRefreshMode);
        Assert.IsTrue(loaded.RefreshOnPanelOpen);
        Assert.IsTrue(loaded.TokenRefreshOnPanelOpen);

        await service.SaveAsync(
            loaded with
            {
                RefreshMode = RefreshMode.Auto,
                TokenRefreshMode = RefreshMode.ManualOnly,
                RefreshOnPanelOpen = false,
                TokenRefreshOnPanelOpen = false,
                PersistTokenUsageCache = false,
            },
            CancellationToken.None);
        var savedJson = await File.ReadAllTextAsync(paths.Settings);
        var roundTrip = await service.LoadAsync(CancellationToken.None);

        Assert.AreEqual(RefreshMode.Every15Minutes, roundTrip.RefreshMode);
        Assert.AreEqual(RefreshMode.ManualOnly, roundTrip.TokenRefreshMode);
        Assert.IsFalse(roundTrip.RefreshOnPanelOpen);
        Assert.IsFalse(roundTrip.TokenRefreshOnPanelOpen);
        Assert.IsFalse(roundTrip.PersistTokenUsageCache);
        StringAssert.Contains(savedJson, "\"refreshMode\": \"every15Minutes\"");
        StringAssert.Contains(savedJson, "\"tokenRefreshMode\": \"manualOnly\"");
        StringAssert.Contains(savedJson, "\"tokenRefreshOnPanelOpen\": false");
        StringAssert.Contains(savedJson, "\"persistTokenUsageCache\": false");
        Assert.IsFalse(savedJson.Contains("\"refreshMode\": \"auto\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OversizedSettingsSafelyFallsBackToDefaults()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(paths.Settings, "{\"padding\":\"" + new string('x', JsonFileStore.MaximumBytes) + "\"}");
        var settings = await new SettingsService(new JsonFileStore(), paths).LoadAsync(CancellationToken.None);
        Assert.AreEqual(AppSettings.Defaults, settings);
    }

    private static AlertInput Input(
        int remaining,
        DateTimeOffset? resetAt = null,
        string key = "window") => new(
        key,
        "7 天额度",
        remaining,
        true,
        10_080,
        resetAt ?? DateTimeOffset.UnixEpoch.AddDays(7));

    private static AlertInput InputWithoutResetAt(int remaining, string key = "window") => new(
        key,
        "7 天额度",
        remaining,
        true,
        10_080,
        null);

    private static void AssertResetTransition(RateLimitsResponse before, RateLimitsResponse after)
    {
        var reduction = ReduceIdentityTransition(before, after);
        Assert.AreEqual(QuotaAlertKind.Reset, reduction.Alert?.Kind);
    }

    private static void AssertNoResetTransition(RateLimitsResponse before, RateLimitsResponse after)
    {
        var reduction = ReduceIdentityTransition(before, after);
        Assert.IsNull(reduction.Alert);
    }

    private static AlertReduction ReduceIdentityTransition(
        RateLimitsResponse before,
        RateLimitsResponse after)
    {
        var initialSnapshot = QuotaNormalizer.Normalize(new RateLimitsReadResult(before, false));
        var nextSnapshot = QuotaNormalizer.Normalize(new RateLimitsReadResult(after, false));
        var initial = QuotaAlertReducer.Reduce(
            null,
            ToAlertInputs(initialSnapshot.Windows),
            new NotificationSettings());
        return QuotaAlertReducer.Reduce(
            initial.State,
            ToAlertInputs(nextSnapshot.Windows),
            new NotificationSettings());
    }

    private static AlertInput[] ToAlertInputs(IReadOnlyList<NormalizedQuotaWindow> windows) => windows
        .Select(window => new AlertInput(
            window.AlertKey,
            window.SourceSlot,
            (int)window.RemainingPercent,
            window.PercentageReliable,
            window.WindowDurationMinutes,
            window.ResetAtUtc,
            window.LegacyAlertKey))
        .ToArray();

    private static RateLimitsResponse LegacySnapshot(
        string? limitId,
        RateLimitWindow? primary = null,
        RateLimitWindow? secondary = null) => new()
    {
        RateLimits = new RateLimitSnapshot
        {
            LimitId = limitId,
            Primary = primary,
            Secondary = secondary,
        },
    };

    private static RateLimitsResponse BucketSnapshot(
        string bucket,
        string? limitId,
        RateLimitWindow primary,
        RateLimitWindow? secondary = null) =>
        BucketSnapshots((bucket, limitId, primary, secondary));

    private static RateLimitsResponse BucketSnapshots(
        params (string Bucket, string? LimitId, RateLimitWindow? Primary, RateLimitWindow? Secondary)[] values) => new()
    {
        RateLimitsByLimitId = values.ToDictionary(
            value => value.Bucket,
            value => new RateLimitSnapshot
            {
                LimitId = value.LimitId,
                Primary = value.Primary,
                Secondary = value.Secondary,
            },
            StringComparer.Ordinal),
    };

    private static RateLimitWindow Window(long usedPercent, long durationMinutes, long resetsAt) => new()
    {
        UsedPercent = usedPercent,
        WindowDurationMinutes = durationMinutes,
        ResetsAt = resetsAt,
    };

    private static RateLimitWindow CompleteWindow(long usedPercent, long durationMinutes, long resetsAt) => new()
    {
        UsedPercent = usedPercent,
        WindowDurationMinutes = durationMinutes,
        ResetsAt = resetsAt,
    };

    private sealed class NotificationReader : TextReader
    {
        private readonly Channel<string> values = Channel.CreateUnbounded<string>();

        internal void Write(string value) => values.Writer.TryWrite(value);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await values.Reader.ReadAsync(cancellationToken);
    }

    private sealed class ChannelTextReader : TextReader
    {
        private readonly Channel<string?> values = Channel.CreateUnbounded<string?>();
        private readonly SemaphoreSlim changed = new(0);
        private int readCount;

        internal void Write(string value) => values.Writer.TryWrite(value);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var value = await values.Reader.ReadAsync(cancellationToken);
            Interlocked.Increment(ref readCount);
            changed.Release();
            return value;
        }

        internal async Task WaitForReadCountAsync(int count, CancellationToken cancellationToken)
        {
            while (Volatile.Read(ref readCount) < count)
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class RecordingTextWriter : StringWriter
    {
        private readonly List<string> lines = [];
        private readonly SemaphoreSlim changed = new(0);

        internal IReadOnlyList<string> Lines
        {
            get
            {
                lock (lines)
                {
                    return lines.ToArray();
                }
            }
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (lines)
            {
                lines.Add(buffer.ToString());
            }

            changed.Release();
            return Task.CompletedTask;
        }

        internal async Task WaitForLinesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (Lines.Count < count)
            {
                await changed.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexQuotaTray.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
