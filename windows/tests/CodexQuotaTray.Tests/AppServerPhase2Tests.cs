using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class AppServerPhase2Tests
{
    [TestMethod]
    public void NpmShimUsesOneCmdPayloadAndPrecedesPackagedAliases()
    {
        var path = Path.Combine("C:\\Users\\Example User", "AppData", "Roaming", "npm", "codex.cmd");
        var info = CodexAppServerProcess.CreateStartInfo(path, ["--version"]);
        Assert.AreEqual(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", info.FileName);
        StringAssert.StartsWith(info.Arguments, "/d /s /c \"");
        StringAssert.Contains(info.Arguments, $"\"{path}\"");
        Assert.IsTrue(info.Arguments.EndsWith("\"", StringComparison.Ordinal));
        Assert.AreEqual(0, info.ArgumentList.Count);

        var candidates = CodexCliLocator.DefaultCandidates();
        StringAssert.EndsWith(candidates[0], "npm\\codex.cmd");
        Assert.IsTrue(Array.IndexOf(candidates, "codex.exe") > 0);
    }

    [TestMethod]
    public async Task JsonLineRpc_UsesUniqueIdsAndMatchesOutOfOrderResponses()
    {
        var input = new ChannelTextReader();
        var output = new RecordingTextWriter();
        await using var rpc = new JsonLineRpcConnection(input, output);
        var first = rpc.RequestAsync("first", new { value = 1 }, TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = rpc.RequestAsync("second", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await output.WaitForLinesAsync(2);
        var requests = output.Lines.Select(line => JsonDocument.Parse(line)).ToArray();
        var firstId = requests.Single(value => value.RootElement.GetProperty("method").GetString() == "first").RootElement.GetProperty("id").GetInt64();
        var secondId = requests.Single(value => value.RootElement.GetProperty("method").GetString() == "second").RootElement.GetProperty("id").GetInt64();
        Assert.AreNotEqual(firstId, secondId);
        Assert.IsFalse(requests[0].RootElement.TryGetProperty("jsonrpc", out _));

        input.Write($"{{\"id\":{secondId},\"result\":{{\"name\":\"second\"}}}}");
        input.Write("{ not-json");
        input.Write("[]");
        input.Write("{\"method\":\"unknown/notice\"}");
        input.Write($"{{\"id\":{firstId},\"result\":{{\"name\":\"first\"}}}}");

        Assert.AreEqual("first", (await first).GetProperty("name").GetString());
        Assert.AreEqual("second", (await second).GetProperty("name").GetString());
        Assert.AreEqual(2, rpc.MalformedJsonCount);
        foreach (var request in requests)
        {
            request.Dispose();
        }
    }

    [TestMethod]
    public async Task JsonLineRpc_InitializedNotificationHasNoIdOrParams()
    {
        var input = new ChannelTextReader();
        var output = new RecordingTextWriter();
        await using var rpc = new JsonLineRpcConnection(input, output);
        await rpc.NotifyAsync("initialized", CancellationToken.None);
        await output.WaitForLinesAsync(1);
        using var message = JsonDocument.Parse(output.Lines[0]);

        Assert.AreEqual("initialized", message.RootElement.GetProperty("method").GetString());
        Assert.IsFalse(message.RootElement.TryGetProperty("id", out _));
        Assert.IsFalse(message.RootElement.TryGetProperty("params", out _));
    }

    [TestMethod]
    public async Task FakeServer_ConnectsInitializesAndReadsRealShape()
    {
        await using var client = CreateFake("happy");
        var session = await client.ConnectAsync(CancellationToken.None);
        var result = await client.ReadRateLimitsAsync(CancellationToken.None);
        var normalized = QuotaNormalizer.Normalize(result);

        Assert.AreEqual("9.99.0", session.CliVersion);
        Assert.AreEqual("9.99.0", session.RuntimeVersion);
        Assert.HasCount(2, normalized.Windows);
        Assert.AreEqual("Plus", normalized.PlanType);
        Assert.AreEqual(ResetCreditKind.CompleteDetails, normalized.ResetCredits.Kind);
        Assert.AreEqual(2, normalized.AvailableCount);
        Assert.IsTrue(client.Diagnostics.InitializeSucceeded);
        Assert.IsTrue(client.Diagnostics.RateLimitsReadSucceeded);
    }

    [TestMethod]
    public async Task MalformedJson_IsCountedAndReaderContinues()
    {
        await using var client = CreateFake("malformed");
        await client.ConnectAsync(CancellationToken.None);
        var result = await client.ReadRateLimitsAsync(CancellationToken.None);

        Assert.IsNotNull(result.Response.RateLimits);
        Assert.AreEqual(1, client.Diagnostics.MalformedJsonCount);
    }

    [TestMethod]
    public async Task MethodNotFound_IsDistinct()
    {
        await using var client = CreateFake("method-not-found");
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<CodexClientException>(
            () => client.ReadRateLimitsAsync(CancellationToken.None));
        Assert.AreEqual(CodexClientErrorKind.MethodNotFound, error.Kind);
    }

    [TestMethod]
    public async Task ReadTimeout_IsDistinctAndLateResponseCannotCompleteRequest()
    {
        await using var client = CreateFake("read-timeout", requestTimeout: TimeSpan.FromMilliseconds(150));
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<CodexClientException>(
            () => client.ReadRateLimitsAsync(CancellationToken.None));
        Assert.AreEqual(CodexClientErrorKind.RequestTimeout, error.Kind);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task InitializeTimeout_IsDistinctAndCleanupDoesNotHang()
    {
        var before = ProcessCount("CodexQuotaTray.FakeAppServer");
        var started = Stopwatch.StartNew();
        await using (var client = CreateFake("initialize-timeout", initializeTimeout: TimeSpan.FromMilliseconds(150)))
        {
            var error = await Assert.ThrowsAsync<CodexClientException>(
                () => client.ConnectAsync(CancellationToken.None));
            Assert.AreEqual(CodexClientErrorKind.InitializeTimeout, error.Kind);
        }

        Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.IsTrue(ProcessCount("CodexQuotaTray.FakeAppServer") <= before);
    }

    [TestMethod]
    public async Task Cancellation_IsDistinct()
    {
        await using var client = CreateFake("read-timeout", requestTimeout: TimeSpan.FromSeconds(5));
        await client.ConnectAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<CodexClientException>(
            () => client.ReadRateLimitsAsync(cancellation.Token));
        Assert.AreEqual(CodexClientErrorKind.Cancelled, error.Kind);
    }

    [TestMethod]
    public async Task StdoutEof_FailsPendingRequest()
    {
        await using var client = CreateFake("exit-after-init");
        await client.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<CodexClientException>(
            () => client.ReadRateLimitsAsync(CancellationToken.None));
        Assert.AreEqual(CodexClientErrorKind.TransportClosed, error.Kind);
    }

    [TestMethod]
    public void MultiBucket_WinsOverLegacyAndSortsByBucketThenSlot()
    {
        var result = LoadFixture("rate_limits_multi_bucket.json", resetFieldPresent: false);
        var normalized = QuotaNormalizer.Normalize(result);

        Assert.HasCount(3, normalized.Windows);
        Assert.AreEqual(10, normalized.Windows[0].UsedPercent);
        Assert.AreEqual(100, normalized.Windows[1].UsedPercent);
        Assert.IsFalse(normalized.Windows[1].PercentageReliable);
        Assert.AreEqual(80, normalized.Windows[2].UsedPercent);
        Assert.AreEqual("Team", normalized.PlanType);
        Assert.IsFalse(normalized.Windows.Any(window => window.UsedPercent == 99));
    }

    [TestMethod]
    public void LocalKeys_DoNotExposeRawLimitIds()
    {
        var normalized = QuotaNormalizer.Normalize(LoadFixture("rate_limits_reset_credits.json", true));

        Assert.IsTrue(normalized.Windows.All(window => window.LocalKey.Length == 64));
        Assert.IsFalse(normalized.Windows.Any(window => window.LocalKey.Contains("REDACTED", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void QuotaNormalizer_AlertKeysRemainDistinctThroughReducer()
    {
        const string limitId = "same-limit-id";
        var reset = new RateLimitsReadResult(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    LimitId = limitId,
                    LimitName = "Codex",
                    Primary = new RateLimitWindow
                    {
                        UsedPercent = 25,
                        WindowDurationMinutes = 300,
                        ResetsAt = 1_900_000_000,
                    },
                    Secondary = new RateLimitWindow
                    {
                        UsedPercent = 50,
                        WindowDurationMinutes = 10_080,
                        ResetsAt = 1_900_500_000,
                    },
                },
            },
            false);

        var normalized = QuotaNormalizer.Normalize(reset);

        Assert.HasCount(2, normalized.Windows);
        var primary = normalized.Windows.Single(window => window.SourceSlot == "primary");
        var secondary = normalized.Windows.Single(window => window.SourceSlot == "secondary");
        Assert.AreNotEqual(primary.AlertKey, secondary.AlertKey);
        Assert.IsFalse(primary.AlertKey.Contains(limitId, StringComparison.Ordinal));
        Assert.IsFalse(secondary.AlertKey.Contains(limitId, StringComparison.Ordinal));

        var inputs = normalized.Windows
            .Select(window => new AlertInput(
                window.AlertKey,
                window.SourceSlot,
                (int)window.RemainingPercent,
                window.PercentageReliable,
                window.WindowDurationMinutes,
                window.ResetAtUtc))
            .ToArray();
        var reduction = QuotaAlertReducer.Reduce(null, inputs, new NotificationSettings());

        Assert.HasCount(2, reduction.State.Windows);
        Assert.AreEqual((int)primary.RemainingPercent, reduction.State.Windows[primary.AlertKey].LastReliableRemaining!.Value);
        Assert.AreEqual((int)secondary.RemainingPercent, reduction.State.Windows[secondary.AlertKey].LastReliableRemaining!.Value);
    }

    [TestMethod]
    [DataRow(false, null, ResetCreditKind.Unavailable)]
    [DataRow(true, null, ResetCreditKind.Unavailable)]
    [DataRow(true, 0L, ResetCreditKind.Empty)]
    public void ResetCreditBaseStates_AreDistinct(bool fieldPresent, long? count, ResetCreditKind expected)
    {
        var response = new RateLimitsResponse
        {
            RateLimitResetCredits = fieldPresent && count is not null
                ? new RateLimitResetCreditsSummary { AvailableCount = count }
                : null,
        };

        var normalized = QuotaNormalizer.Normalize(new RateLimitsReadResult(response, fieldPresent));
        Assert.AreEqual(expected, normalized.ResetCredits.Kind);
    }

    [TestMethod]
    public void ResetCreditDetails_UseAuthoritativeCountAndEarliestValidExpiry()
    {
        var normalized = QuotaNormalizer.Normalize(LoadFixture("rate_limits_reset_credits.json", true));

        Assert.AreEqual(2, normalized.ResetCredits.AvailableCount);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_901_000_000), normalized.ResetCredits.EarliestKnownExpiry);
        Assert.AreEqual(ResetCreditKind.CompleteDetails, normalized.ResetCredits.Kind);
    }

    [TestMethod]
    public void ResetCreditInvalidTimestamps_DoNotThrowAndYieldCountOnly()
    {
        using var negative = JsonDocument.Parse("-1");
        using var overflow = JsonDocument.Parse("999999999999999999999999");
        var response = new RateLimitsResponse
        {
            RateLimitResetCredits = new RateLimitResetCreditsSummary
            {
                AvailableCount = 4,
                Credits =
                [
                    new RateLimitResetCredit { ExpiresAt = null },
                    new RateLimitResetCredit { ExpiresAt = negative.RootElement.Clone() },
                    new RateLimitResetCredit { ExpiresAt = overflow.RootElement.Clone() },
                ],
            },
        };

        var normalized = QuotaNormalizer.Normalize(new RateLimitsReadResult(response, true));
        Assert.AreEqual(ResetCreditKind.CountOnly, normalized.ResetCredits.Kind);
        Assert.AreEqual(4, normalized.ResetCredits.AvailableCount);
    }

    [TestMethod]
    public void ResetCreditPartialDetails_DoesNotUseDetailsAsTotal()
    {
        using var expiry = JsonDocument.Parse("1901000000");
        var response = new RateLimitsResponse
        {
            RateLimitResetCredits = new RateLimitResetCreditsSummary
            {
                AvailableCount = 5,
                Credits = [new RateLimitResetCredit { ExpiresAt = expiry.RootElement.Clone() }],
            },
        };

        var normalized = QuotaNormalizer.Normalize(new RateLimitsReadResult(response, true));
        Assert.AreEqual(ResetCreditKind.PartialDetails, normalized.ResetCredits.Kind);
        Assert.AreEqual(5, normalized.ResetCredits.AvailableCount);
        Assert.AreEqual(1, normalized.CreditDetailCount);
    }

    [TestMethod]
    public void Projector_UsesInjectedTimeZoneForAbsoluteTimes()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(8), "test", "test");
        var provider = new FrozenTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_899_990_000));
        var projector = new QuotaViewProjector(provider, zone);
        var normalized = QuotaNormalizer.Normalize(LoadFixture("rate_limits_reset_credits.json", true));

        var view = projector.Project(normalized, provider.GetUtcNow());
        var expectedReset = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), zone).ToString("M月d日 HH:mm");
        var expectedCredit = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(1_901_000_000), zone).ToString("M月d日");
        Assert.AreEqual(expectedReset, view.Windows[0].ResetAt);
        StringAssert.Contains(view.ResetCredits.Summary, expectedCredit);
    }

    [TestMethod]
    public async Task LiveProvider_SuppressesConcurrentRefreshAndPreservesLastData()
    {
        var client = new ControlledClient();
        await using var provider = new LiveQuotaStateProvider(new SingleClientFactory(client));
        var first = provider.GetSnapshotAsync(CancellationToken.None).AsTask();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var concurrent = await provider.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(concurrent.IsRefreshing);
        Assert.AreEqual(1, client.ReadCount);
        client.Release.TrySetResult();
        var success = await first;
        Assert.IsFalse(success.IsRefreshing);
        Assert.HasCount(1, success.Windows);

        client.Fail = true;
        var failed = await provider.RefreshAsync(CancellationToken.None);
        Assert.HasCount(1, failed.Windows);
        StringAssert.Contains(failed.StatusText, "显示上次数据");
    }

    [TestMethod]
    public async Task QuotaRuntime_ScheduledFailuresUseAttemptTimeBackoffAndResetAfterSuccess()
    {
        var client = new ControlledClient();
        client.Release.TrySetResult();
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var clock = new ManualTimeProvider();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths),
            timeProvider: clock);
        var fourthRefreshSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RefreshSettled += (succeeded, handoffReason) =>
        {
            if (succeeded && handoffReason is null && client.ReadCount >= 4)
            {
                fourthRefreshSettled.TrySetResult();
            }
        };

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(1, client.ReadCount);
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        client.Fail = true;
        clock.Advance(TimeSpan.FromMinutes(15));
        await WaitForReadCountAsync(client, 2);
        Assert.AreEqual(2, client.ReadCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.AreEqual(2, client.ReadCount);

        clock.Advance(TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(30)));
        await WaitForReadCountAsync(client, 3);
        Assert.AreEqual(3, client.ReadCount);

        clock.Advance(TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(59)));
        await Task.Delay(100);
        Assert.AreEqual(3, client.ReadCount);

        client.Fail = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForReadCountAsync(client, 4);
        Assert.AreEqual(4, client.ReadCount);
        await fourthRefreshSettled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(59)));
        await Task.Delay(100);
        Assert.AreEqual(4, client.ReadCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitForReadCountAsync(client, 5);
        Assert.AreEqual(5, client.ReadCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_StaleStatePublishesOnceAndSuccessRestoresNormalState()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        await new SettingsService(store, paths).SaveAsync(
            AppSettings.Defaults with { RefreshMode = RefreshMode.ManualOnly },
            CancellationToken.None);
        var clock = new ManualTimeProvider();
        var client = new ControlledClient();
        client.Release.TrySetResult();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths),
            timeProvider: clock);
        var states = new List<AppUiState>();
        service.StateChanged += (_, state) => states.Add(state);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        _ = await service.RefreshAsync(CancellationToken.None);
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(1));
        var beforeStale = states.Count;

        clock.Advance(TimeSpan.FromMinutes(60));
        await Task.Delay(100);
        var afterFirstStale = states.Count;
        Assert.AreEqual(beforeStale + 1, afterFirstStale);
        Assert.AreEqual(StatusTone.Warning, states[^1].StatusTone);
        Assert.IsTrue(states[^1].Windows.All(window => window.IsStale));

        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.AreEqual(afterFirstStale, states.Count);

        _ = await service.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(StatusTone.Success, states[^1].StatusTone);
        Assert.IsTrue(states[^1].Windows.All(window => !window.IsStale));
    }

    [TestMethod]
    public async Task QuotaRuntime_DropsRedundantCardOpenedRequestAfterSuccessfulStartup()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var client = new ControlledClient();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths));

        var startup = service.GetSnapshotAsync(CancellationToken.None).AsTask();
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var cardOpened = service.RequestAsync(RefreshReason.CardOpened).AsTask();
        client.Release.TrySetResult();

        await startup;
        await cardOpened;
        Assert.AreEqual(1, client.ReadCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_TransportClosedNotificationSetsErrorAndNextRefreshRecreatesClient()
    {
        using var directory = new TemporaryDirectory();
        var factory = new ReconnectingClientFactory();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), new PreviewDataPaths(directory.Path)),
            new PreviewPersistence(new JsonFileStore(), new PreviewDataPaths(directory.Path)));
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += (_, state) =>
        {
            if (state.StatusTone == StatusTone.Error && state.StatusText.Contains("连接已断开", StringComparison.Ordinal))
            {
                disconnected.TrySetResult();
            }
        };

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(1));
        _ = await service.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(2, factory.CreateCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_NotificationFailureDoesNotConsumeFutureResetAlert()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        await new SettingsService(store, paths).SaveAsync(
            AppSettings.Defaults with { RefreshMode = RefreshMode.ManualOnly },
            CancellationToken.None);
        var sink = new FailingNotificationSink();
        var client = new ResetRecoveryClient();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths),
            sink);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        _ = await service.RefreshAsync(CancellationToken.None);
        sink.Throw = true;
        _ = await service.RefreshAsync(CancellationToken.None);
        sink.Throw = false;
        _ = await service.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(2, sink.Attempts);
    }

    [TestMethod]
    public async Task QuotaRuntime_DeliversGeneratedResetAlertToNotificationSink()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        await new SettingsService(store, paths).SaveAsync(
            AppSettings.Defaults with { RefreshMode = RefreshMode.ManualOnly },
            CancellationToken.None);
        var sink = new RecordingNotificationSink();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(new ResetRecoveryClient()),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths),
            sink);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        _ = await service.RefreshAsync(CancellationToken.None);
        _ = await service.RefreshAsync(CancellationToken.None);

        Assert.HasCount(1, sink.Alerts);
        Assert.AreEqual(QuotaAlertKind.Reset, sink.Alerts[0].Kind);
    }

    [TestMethod]
    public async Task QuotaRuntime_OverflowRecoveryTransportClosedDoesNotSelfAwaitAndNextRefreshRecreatesClient()
    {
        using var directory = new TemporaryDirectory();
        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var factory = new OverflowRecoveryFactory();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), new PreviewDataPaths(directory.Path)),
            new PreviewPersistence(new JsonFileStore(), new PreviewDataPaths(directory.Path)));

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await factory.First.NotificationStarted.Task.WaitAsync(recoveryTimeout.Token);
        factory.First.Publish(new RateLimitsUpdatedNotification(
            new RateLimitsResponse(),
            false,
            IsOverflow: true));

        await factory.First.Disposed.Task.WaitAsync(recoveryTimeout.Token);
        var nextRefresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await factory.Second.ReadStarted.Task.WaitAsync(recoveryTimeout.Token);
        await nextRefresh.WaitAsync(recoveryTimeout.Token);

        Assert.AreEqual(2, factory.CreateCount);
        Assert.AreEqual(1, factory.Second.ReadCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_OverflowRecoveryDropsOlderBufferedNotificationByIngressSequence()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var factory = new SequenceRecoveryFactory();
        var sink = new RecordingNotificationSink();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths),
            sink);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await factory.Client.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factory.Client.Publish(new RateLimitsUpdatedNotification(
            new RateLimitsResponse(),
            false,
            IngressSequence: 10,
            IsOverflow: true));
        await factory.Client.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        factory.Client.Publish(new RateLimitsUpdatedNotification(
            SequenceRecoveryClient.CreateResponse(99),
            false,
            IngressSequence: 11,
            IngressAcknowledgement: () => factory.Client.BufferedNotificationAcknowledged.TrySetResult()));
        factory.Client.ReleaseRecovery.TrySetResult();
        await factory.Client.BufferedNotificationAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(12, factory.Client.RecoveryIngressSequence);
        var cache = await File.ReadAllTextAsync(paths.QuotaCache);
        StringAssert.Contains(cache, "\"usedPercent\": 20");
        Assert.IsFalse(cache.Contains("\"usedPercent\": 99", StringComparison.Ordinal));
        Assert.IsEmpty(sink.Alerts);
        var alertState = await File.ReadAllTextAsync(paths.AlertState);
        Assert.IsFalse(alertState.Contains("\"lastReliableRemaining\": 1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task QuotaRuntime_PendingManualAfterRecoveryFailureUsesOrdinaryRefreshPath()
    {
        using var directory = new TemporaryDirectory();
        var factory = new PendingRecoveryFactory();
        var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), new PreviewDataPaths(directory.Path)),
            new PreviewPersistence(new JsonFileStore(), new PreviewDataPaths(directory.Path)));
        try
        {
            _ = await service.GetSnapshotAsync(CancellationToken.None);
            await factory.First.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            factory.First.Publish(new RateLimitsUpdatedNotification(
                new RateLimitsResponse(),
                false,
                IngressSequence: 10,
                IsOverflow: true));
            await factory.First.RecoveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await service.RefreshAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            factory.First.ReleaseRecovery.TrySetResult();
            await factory.First.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await factory.Second.SecondNormalReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.AreEqual(2, factory.CreateCount);
            Assert.AreEqual(0, factory.Second.RecoveryReadCount);
            Assert.AreEqual(20, factory.Second.LastNormalIngressSequence);
        }
        finally
        {
            await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    public async Task QuotaRuntime_NewClientGenerationDoesNotMergeSparseNotificationWithOldBaseline()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var factory = new GenerationClientFactory();
        var store = new JsonFileStore();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths));
        AppUiState? observed = null;
        service.StateChanged += (_, state) => observed = state;

        var initial = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(90, initial.Windows[0].RemainingPercent);
        factory.First.TriggerDisconnect();
        await factory.First.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var refresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await factory.Second.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await factory.Second.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factory.Second.Publish(new RateLimitsUpdatedNotification(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    Primary = new RateLimitWindow { UsedPercent = 99 },
                },
            },
            false));
        await Task.Delay(50);

        var beforeFullRead = await File.ReadAllTextAsync(paths.QuotaCache);
        StringAssert.Contains(beforeFullRead, "\"usedPercent\": 10");
        Assert.IsFalse(beforeFullRead.Contains("\"usedPercent\": 99", StringComparison.Ordinal));

        factory.Second.ReleaseFirstRead.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsNotNull(observed);
        Assert.AreEqual(80, observed!.Windows[0].RemainingPercent);
        var afterFullRead = await File.ReadAllTextAsync(paths.QuotaCache);
        StringAssert.Contains(afterFullRead, "\"usedPercent\": 20");
        Assert.IsFalse(afterFullRead.Contains("\"usedPercent\": 99", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task QuotaRuntime_DetachedGenerationCannotCommitLateReadWork()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var factory = new LateGenerationClientFactory();
        var sink = new RecordingNotificationSink();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths),
            sink);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await factory.First.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var oldRefresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await factory.First.LateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factory.First.TriggerDisconnect();
        await factory.First.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await service.RefreshAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        factory.First.ReleaseLateRead.TrySetResult();
        // The stale read is released only after generation two is active. The
        // single snapshot worker may still be draining generation-two persistence
        // before it can complete this intentionally discarded generation-one work.
        await oldRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        await factory.Second.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(2, factory.CreateCount);
        Assert.IsEmpty(sink.Alerts);
        var cache = await File.ReadAllTextAsync(paths.QuotaCache);
        StringAssert.Contains(cache, "\"usedPercent\": 20");
        Assert.IsFalse(cache.Contains("\"usedPercent\": 90", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task QuotaRuntime_GenerationCommitRejectsStaleCacheBeforeReplacement()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var factory = new LateGenerationClientFactory();
        var persistence = new BlockingCommitPersistence(paths);
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), paths),
            persistence);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        persistence.BlockNextCache();
        var oldRefresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await factory.First.LateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factory.First.ReleaseLateRead.TrySetResult();
        await persistence.CacheStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        factory.First.TriggerDisconnect();
        await factory.First.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        persistence.ReleaseCache.TrySetResult();
        await oldRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RefreshAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var cache = await File.ReadAllTextAsync(paths.QuotaCache);
        StringAssert.Contains(cache, "\"usedPercent\": 20");
        Assert.IsFalse(cache.Contains("\"usedPercent\": 90", StringComparison.Ordinal));
        Assert.IsTrue(persistence.CacheCommitRejected);
        Assert.IsEmpty(Directory.GetFiles(paths.Root, "quota-cache.json.*.tmp"));
    }

    [TestMethod]
    public async Task QuotaRuntime_GenerationCommitRejectsStaleAlertStateBeforeReplacement()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var factory = new LateGenerationClientFactory();
        var persistence = new BlockingCommitPersistence(paths);
        var sink = new RecordingNotificationSink();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), paths),
            persistence,
            sink);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        persistence.BlockNextAlertState();
        var oldRefresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await factory.First.LateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factory.First.ReleaseLateRead.TrySetResult();
        await persistence.AlertStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        factory.First.TriggerDisconnect();
        await factory.First.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        persistence.ReleaseAlert.TrySetResult();
        await oldRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RefreshAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        var state = await persistence.LoadAlertStateAsync(CancellationToken.None);
        Assert.IsNotNull(state);
        Assert.IsFalse(state!.Windows.Values.Any(window => window.LastReliableRemaining == 10));
        Assert.IsTrue(persistence.AlertCommitRejected);
        Assert.IsEmpty(sink.Alerts);
        Assert.IsEmpty(Directory.GetFiles(paths.Root, "alert-state.json.*.tmp"));
    }

    [TestMethod]
    public async Task QuotaRuntime_GenerationCommitSerializesBlockingNotification()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var factory = new LateGenerationClientFactory();
        var persistence = new PreviewPersistence(new JsonFileStore(), paths);
        var sink = new BlockingNotificationSink();
        await using var service = new QuotaRuntimeService(
            factory,
            new SettingsService(new JsonFileStore(), paths),
            persistence,
            sink);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        var oldRefresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await factory.First.LateReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        factory.First.ReleaseLateRead.TrySetResult();
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        factory.First.TriggerDisconnect();
        await Task.Delay(50);
        Assert.IsFalse(factory.First.Disposed.Task.IsCompleted);

        sink.Release.TrySetResult();
        await factory.First.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await oldRefresh.WaitAsync(TimeSpan.FromSeconds(2));
        await service.RefreshAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task QuotaRuntime_DuplicateSnapshotSkipsUnchangedCacheAndAlertWrites()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var client = new ControlledClient();
        client.Release.TrySetResult();
        var store = new JsonFileStore();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths));

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        var cache = await File.ReadAllTextAsync(paths.QuotaCache);
        var alertState = await File.ReadAllTextAsync(paths.AlertState);
        _ = await service.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(cache, await File.ReadAllTextAsync(paths.QuotaCache));
        Assert.AreEqual(alertState, await File.ReadAllTextAsync(paths.AlertState));
    }

    [TestMethod]
    public async Task QuotaRuntime_CacheHeartbeatUpdatesLastSuccessWithoutWritingEveryRefresh()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var client = new ControlledClient();
        client.Release.TrySetResult();
        var clock = new ManualTimeProvider();
        var store = new JsonFileStore();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths),
            timeProvider: clock);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        var initial = await File.ReadAllTextAsync(paths.QuotaCache);

        clock.Advance(TimeSpan.FromMinutes(4));
        _ = await service.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(initial, await File.ReadAllTextAsync(paths.QuotaCache));

        clock.Advance(TimeSpan.FromMinutes(1));
        _ = await service.RefreshAsync(CancellationToken.None);
        var heartbeat = await File.ReadAllTextAsync(paths.QuotaCache);
        Assert.AreNotEqual(initial, heartbeat);
        StringAssert.Contains(heartbeat, "\"lastSuccessUtc\": \"1970-01-01T00:05:00+00:00");
    }

    [TestMethod]
    public async Task QuotaRuntime_RequestWaitsForInitializationBeforeApplyingRefreshMode()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        await new SettingsService(store, paths).SaveAsync(
            AppSettings.Defaults with
            {
                RefreshMode = RefreshMode.ManualOnly,
                RefreshOnPanelOpen = false,
            },
            CancellationToken.None);

        var client = new ControlledClient { BlockConnect = true };
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths));

        var initialization = service.GetSnapshotAsync(CancellationToken.None).AsTask();
        await client.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var cardOpened = service.RequestAsync(RefreshReason.CardOpened).AsTask();
        await Task.Delay(50);
        Assert.AreEqual(0, client.ReadCount);

        client.ConnectRelease.TrySetResult();
        client.Release.TrySetResult();
        await initialization;
        await cardOpened;

        Assert.AreEqual(RefreshMode.ManualOnly, service.Settings.RefreshMode);
        Assert.AreEqual(0, client.ReadCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_CardOpenedRefreshIgnoresSnapshotAgeButKeepsTenSecondSuppression()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var clock = new ManualTimeProvider();
        var client = new ControlledClient();
        client.Release.TrySetResult();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths),
            timeProvider: clock);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await client.WaitForReadCompletionAsync(1);
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(1));

        clock.Advance(TimeSpan.FromSeconds(9));
        await service.RequestAsync(RefreshReason.CardOpened, CancellationToken.None);
        Assert.AreEqual(1, client.ReadCount);

        clock.Advance(TimeSpan.FromSeconds(2));
        await service.RequestAsync(RefreshReason.CardOpened, CancellationToken.None);

        Assert.AreEqual(2, client.ReadCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_ManualOnlyKeepsPanelAndNetworkRefreshIndependent()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        await new SettingsService(store, paths).SaveAsync(
            AppSettings.Defaults with
            {
                RefreshMode = RefreshMode.ManualOnly,
                RefreshOnPanelOpen = true,
                RefreshOnNetworkRestore = true,
            },
            CancellationToken.None);
        var clock = new ManualTimeProvider();
        var client = new ControlledClient();
        client.Release.TrySetResult();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths),
            timeProvider: clock);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(0, client.ReadCount);

        await service.RequestAsync(RefreshReason.CardOpened, CancellationToken.None);
        Assert.AreEqual(1, client.ReadCount);

        clock.Advance(TimeSpan.FromSeconds(11));
        await service.RequestAsync(RefreshReason.NetworkRestored, CancellationToken.None);
        Assert.AreEqual(2, client.ReadCount);

        await service.ApplySettingsAsync(
            service.Settings with
            {
                RefreshOnPanelOpen = false,
                RefreshOnNetworkRestore = false,
            },
            CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(11));
        await service.RequestAsync(RefreshReason.CardOpened, CancellationToken.None);
        await service.RequestAsync(RefreshReason.NetworkRestored, CancellationToken.None);
        Assert.AreEqual(2, client.ReadCount);

        await service.RequestAsync(RefreshReason.Manual, CancellationToken.None);
        Assert.AreEqual(3, client.ReadCount);
    }

    [TestMethod]
    public async Task QuotaRuntime_SerializesFullReadAndNotificationSnapshotApply()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var client = new SnapshotRaceClient();
        var sink = new BlockingNotificationSink();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths),
            sink);
        AppUiState? observed = null;
        service.StateChanged += (_, state) => observed = state;

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await client.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        client.Publish(new RateLimitsUpdatedNotification(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    Primary = new RateLimitWindow { UsedPercent = 90, WindowDurationMinutes = 300 },
                },
            },
            false));
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var refresh = service.RefreshAsync(CancellationToken.None).AsTask();
        await client.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        Assert.IsNotNull(observed);
        Assert.AreEqual(10, observed!.Windows[0].RemainingPercent);

        sink.Release.TrySetResult();
        await refresh;
        Assert.AreEqual(70, observed!.Windows[0].RemainingPercent);
    }

    [TestMethod]
    public async Task QuotaRuntime_SuccessApplyRefreshesStaleClockBeforeSlowAlertPersistence()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var client = new SnapshotRaceClient();
        var sink = new BlockingNotificationSink();
        var clock = new ManualTimeProvider();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths),
            sink,
            clock);
        var states = new List<AppUiState>();
        service.StateChanged += (_, state) => states.Add(state);

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        await client.NotificationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(60));
        client.Publish(new RateLimitsUpdatedNotification(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    Primary = new RateLimitWindow { UsedPercent = 90, WindowDurationMinutes = 300 },
                },
            },
            false));
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsFalse(states.Any(state => state.StatusTone == StatusTone.Warning && state.Windows.Any(window => window.IsStale)));
        sink.Release.TrySetResult();
    }

    [TestMethod]
    public async Task QuotaRuntime_CacheRestoreDoesNotNotifyForReset()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var store = new JsonFileStore();
        await new SettingsService(store, paths).SaveAsync(
            AppSettings.Defaults with { RefreshMode = RefreshMode.ManualOnly },
            CancellationToken.None);
        await new PreviewPersistence(store, paths).SaveQuotaCacheAsync(
            new QuotaCacheDocument(
                1,
                DateTimeOffset.UnixEpoch,
                "plus",
                [new QuotaCacheWindow(
                    "primary",
                    20,
                    80,
                    true,
                    300,
                    DateTimeOffset.UnixEpoch.AddMinutes(300))]),
            CancellationToken.None);

        var client = new ControlledClient();
        client.Release.TrySetResult();
        var sink = new RecordingNotificationSink();
        await using var service = new QuotaRuntimeService(
            new SingleClientFactory(client),
            new SettingsService(store, paths),
            new PreviewPersistence(store, paths),
            sink);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.HasCount(1, snapshot.Windows);
        Assert.IsEmpty(sink.Alerts);
    }

    [TestMethod]
    public async Task Diagnostics_AreSanitized()
    {
        var client = new ControlledClient();
        await using var provider = new LiveQuotaStateProvider(new SingleClientFactory(client));
        client.Release.TrySetResult();
        _ = await provider.GetSnapshotAsync(CancellationToken.None);

        var text = provider.CreateDiagnosticText();
        Assert.IsFalse(text.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("[REDACTED]", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("limitId", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task QuotaRuntimeDiagnosticsUseEntryAssemblyVersion()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        var service = new QuotaRuntimeService(
            new SingleClientFactory(new ControlledClient()),
            new SettingsService(new JsonFileStore(), paths),
            new PreviewPersistence(new JsonFileStore(), paths));
        try
        {
            var expected = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
            StringAssert.Contains(service.CreateDiagnosticText(), $"CodexQuotaTray WinUI: {expected}");
            Assert.IsFalse(service.CreateDiagnosticText().Contains("CodexQuotaTray WinUI: 0.4.4", StringComparison.Ordinal));
        }
        finally
        {
            await service.DisposeAsync();
        }
    }

    private static CodexAppServerClient CreateFake(
        string mode,
        TimeSpan? requestTimeout = null,
        TimeSpan? initializeTimeout = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "FakeAppServer", "CodexQuotaTray.FakeAppServer.exe");
        Assert.IsTrue(File.Exists(executable), $"Fake App Server was not copied to {executable}");
        return new CodexAppServerClient(new CodexClientOptions(
            executable,
            ["--mode", mode],
            VersionTimeout: TimeSpan.FromSeconds(5),
            InitializeTimeout: initializeTimeout ?? TimeSpan.FromSeconds(1),
            RequestTimeout: requestTimeout ?? TimeSpan.FromSeconds(1),
            ShutdownTimeout: TimeSpan.FromMilliseconds(300)));
    }

    private static int ProcessCount(string name) => System.Diagnostics.Process.GetProcessesByName(name).Length;

    private static RateLimitsReadResult LoadFixture(string name, bool resetFieldPresent)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
        var response = JsonSerializer.Deserialize<RateLimitsResponse>(json);
        Assert.IsNotNull(response);
        return new RateLimitsReadResult(response, resetFieldPresent);
    }

    private static async Task WaitForReadCountAsync(ControlledClient client, int expected)
    {
        await client.WaitForReadCompletionAsync(expected);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long utcTicks = DateTimeOffset.UnixEpoch.Ticks;
        private readonly TaskCompletionSource timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ManualTimer? timer;

        public Task TimerCreated => timerCreated.Task;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var created = new ManualTimer(callback, state);
            timer = created;
            timerCreated.TrySetResult();
            return created;
        }

        public void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref utcTicks, amount.Ticks);
            timer?.Tick();
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private int disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                Volatile.Read(ref disposed) == 0;

            public void Dispose() => Interlocked.Exchange(ref disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Tick()
            {
                if (Volatile.Read(ref disposed) == 0)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexQuotaTray.Tests",
                Guid.NewGuid().ToString("N"));
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

    private sealed class ChannelTextReader : TextReader
    {
        private readonly Channel<string?> channel = Channel.CreateUnbounded<string?>();

        public void Write(string line) => channel.Writer.TryWrite(line);

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            channel.Reader.ReadAsync(cancellationToken);
    }

    private sealed class RecordingTextWriter : StringWriter
    {
        private readonly List<string> lines = [];
        private readonly SemaphoreSlim changed = new(0);

        public IReadOnlyList<string> Lines
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

        public async Task WaitForLinesAsync(int count, TimeSpan? timeout = null)
        {
            using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            while (Lines.Count < count)
            {
                await changed.WaitAsync(timeoutSource.Token);
            }
        }
    }

    private sealed class SingleClientFactory(ICodexAppServerClient client) : ICodexAppServerClientFactory
    {
        public ICodexAppServerClient Create() => client;
    }

    private sealed class ReconnectingClientFactory : ICodexAppServerClientFactory
    {
        public int CreateCount { get; private set; }

        public ICodexAppServerClient Create()
        {
            CreateCount++;
            return new ReconnectingClient(CreateCount == 1);
        }
    }

    private sealed class ReconnectingClient(bool closesNotifications) : ICodexAppServerClient
    {
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RateLimitsReadResult(
                new RateLimitsResponse
                {
                    RateLimits = new RateLimitSnapshot
                    {
                        Primary = new RateLimitWindow { UsedPercent = 25, WindowDurationMinutes = 300 },
                    },
                },
                false));

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (closesNotifications)
            {
                await Task.Yield();
                throw new ChannelClosedException(new CodexClientException(
                    CodexClientErrorKind.TransportClosed,
                    "synthetic stdout EOF"));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OverflowRecoveryFactory : ICodexAppServerClientFactory
    {
        public OverflowRecoveryClient First { get; } = new(failAfterFirstRead: true);
        public OverflowRecoveryClient Second { get; } = new(failAfterFirstRead: false);
        public int CreateCount { get; private set; }

        public ICodexAppServerClient Create()
        {
            CreateCount++;
            return CreateCount == 1 ? First : Second;
        }
    }

    private sealed class OverflowRecoveryClient(bool failAfterFirstRead) : ICodexAppServerClient
    {
        private readonly Channel<RateLimitsUpdatedNotification> notifications = Channel.CreateUnbounded<RateLimitsUpdatedNotification>();
        private int readCount;
        private int disposed;

        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => Volatile.Read(ref readCount);
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref readCount);
            ReadStarted.TrySetResult();
            if (failAfterFirstRead && count > 1)
            {
                return Task.FromException<RateLimitsReadResult>(new CodexClientException(
                    CodexClientErrorKind.TransportClosed,
                    "synthetic transport close"));
            }

            return Task.FromResult(new RateLimitsReadResult(
                new RateLimitsResponse
                {
                    RateLimits = new RateLimitSnapshot
                    {
                        LimitId = failAfterFirstRead ? "first" : "second",
                        Primary = new RateLimitWindow
                        {
                            UsedPercent = 25,
                            WindowDurationMinutes = 300,
                            ResetsAt = 2_000,
                        },
                    },
                },
                false));
        }

        public void Publish(RateLimitsUpdatedNotification notification) => notifications.Writer.TryWrite(notification);

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            NotificationStarted.TrySetResult();
            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                notifications.Writer.TryComplete();
                Disposed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceRecoveryFactory : ICodexAppServerClientFactory
    {
        public SequenceRecoveryClient Client { get; } = new();

        public ICodexAppServerClient Create() => Client;
    }

    private sealed class SequenceRecoveryClient : ICodexAppServerClient
    {
        private readonly Channel<RateLimitsUpdatedNotification> notifications = Channel.CreateUnbounded<RateLimitsUpdatedNotification>();
        private int disposed;

        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BufferedNotificationAcknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long RecoveryIngressSequence { get; private set; }
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RateLimitsReadResult(CreateResponse(10), true, 1));

        public async Task<RateLimitsReadResult> ReadRateLimitsForRecoveryAsync(CancellationToken cancellationToken)
        {
            RecoveryStarted.TrySetResult();
            await ReleaseRecovery.Task.WaitAsync(cancellationToken);
            RecoveryIngressSequence = 12;
            return new RateLimitsReadResult(CreateResponse(20), true, RecoveryIngressSequence);
        }

        public void Publish(RateLimitsUpdatedNotification notification) => notifications.Writer.TryWrite(notification);

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            NotificationStarted.TrySetResult();
            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                notifications.Writer.TryComplete();
            }

            return ValueTask.CompletedTask;
        }

        public static RateLimitsResponse CreateResponse(long usedPercent) => new()
        {
            RateLimits = new RateLimitSnapshot
            {
                LimitId = "sequence-window",
                Primary = new RateLimitWindow
                {
                    UsedPercent = usedPercent,
                    WindowDurationMinutes = 300,
                    ResetsAt = 2_000,
                },
            },
        };
    }

    private sealed class PendingRecoveryFactory : ICodexAppServerClientFactory
    {
        public PendingRecoveryClient First { get; } = new(failRecovery: true);
        public PendingRecoveryClient Second { get; } = new(failRecovery: false);
        public int CreateCount { get; private set; }

        public ICodexAppServerClient Create()
        {
            CreateCount++;
            return CreateCount == 1 ? First : Second;
        }
    }

    private sealed class PendingRecoveryClient(bool failRecovery) : ICodexAppServerClient
    {
        private readonly Channel<RateLimitsUpdatedNotification> notifications = Channel.CreateUnbounded<RateLimitsUpdatedNotification>();
        private int normalReadCount;
        private int recoveryReadCount;
        private int disposed;

        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecoveryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRecovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondNormalReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RecoveryReadCount => Volatile.Read(ref recoveryReadCount);
        public long LastNormalIngressSequence { get; private set; }
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref normalReadCount);
            LastNormalIngressSequence = failRecovery ? 1 : 20;
            if (!failRecovery)
            {
                SecondNormalReadStarted.TrySetResult();
            }

            return Task.FromResult(new RateLimitsReadResult(
                SequenceRecoveryClient.CreateResponse(failRecovery ? 10 : 30),
                true,
                LastNormalIngressSequence));
        }

        public async Task<RateLimitsReadResult> ReadRateLimitsForRecoveryAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref recoveryReadCount);
            RecoveryStarted.TrySetResult();
            if (failRecovery)
            {
                await ReleaseRecovery.Task.WaitAsync(cancellationToken);
                throw new CodexClientException(CodexClientErrorKind.TransportClosed, "synthetic transport close");
            }

            return new RateLimitsReadResult(SequenceRecoveryClient.CreateResponse(30), true, 30);
        }

        public void Publish(RateLimitsUpdatedNotification notification) => notifications.Writer.TryWrite(notification);

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            NotificationStarted.TrySetResult();
            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                notifications.Writer.TryComplete();
                Disposed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class GenerationClientFactory : ICodexAppServerClientFactory
    {
        public GenerationClient First { get; } = new("first", 10, closesOnSignal: true, blockFirstRead: false);
        public GenerationClient Second { get; } = new("second", 20, closesOnSignal: false, blockFirstRead: true);
        private int createCount;

        public ICodexAppServerClient Create() =>
            Interlocked.Increment(ref createCount) == 1 ? First : Second;
    }

    private sealed class LateGenerationClientFactory : ICodexAppServerClientFactory
    {
        public LateGenerationClient First { get; } = new(first: true);
        public LateGenerationClient Second { get; } = new(first: false);
        public int CreateCount { get; private set; }

        public ICodexAppServerClient Create()
        {
            CreateCount++;
            return CreateCount == 1 ? First : Second;
        }
    }

    private sealed class BlockingCommitPersistence(PreviewDataPaths paths) : PreviewPersistence(new JsonFileStore(), paths)
    {
        private int blockCache;
        private int blockAlert;

        public TaskCompletionSource CacheStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCache { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AlertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAlert { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CacheCommitRejected { get; private set; }
        public bool AlertCommitRejected { get; private set; }

        public void BlockNextCache() => Interlocked.Exchange(ref blockCache, 1);

        public void BlockNextAlertState() => Interlocked.Exchange(ref blockAlert, 1);

        public override async Task<bool> SaveQuotaCacheWithCommitAsync(
            QuotaCacheDocument value,
            CancellationToken cancellationToken,
            SemaphoreSlim commitGate,
            Func<bool> canCommit,
            Action onCommitted)
        {
            if (Interlocked.Exchange(ref blockCache, 0) == 1)
            {
                CacheStarted.TrySetResult();
                await ReleaseCache.Task.WaitAsync(cancellationToken);
            }

            var committed = await base.SaveQuotaCacheWithCommitAsync(
                value,
                cancellationToken,
                commitGate,
                canCommit,
                onCommitted);
            CacheCommitRejected |= !committed;
            return committed;
        }

        public override async Task<bool> SaveAlertStateWithCommitAsync(
            AlertStateDocument value,
            CancellationToken cancellationToken,
            SemaphoreSlim commitGate,
            Func<bool> canCommit,
            Action onCommitted)
        {
            if (Interlocked.Exchange(ref blockAlert, 0) == 1)
            {
                AlertStarted.TrySetResult();
                await ReleaseAlert.Task.WaitAsync(cancellationToken);
            }

            var committed = await base.SaveAlertStateWithCommitAsync(
                value,
                cancellationToken,
                commitGate,
                canCommit,
                onCommitted);
            AlertCommitRejected |= !committed;
            return committed;
        }
    }

    private sealed class LateGenerationClient(bool first) : ICodexAppServerClient
    {
        private readonly Channel<RateLimitsUpdatedNotification> notifications = Channel.CreateUnbounded<RateLimitsUpdatedNotification>();
        private readonly TaskCompletionSource disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int readCount;
        private int disposed;

        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LateReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLateRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public async Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref readCount);
            if (first && count == 2)
            {
                LateReadStarted.TrySetResult();
                await ReleaseLateRead.Task.WaitAsync(cancellationToken);
                return new RateLimitsReadResult(CreateResponse(90), true, 2);
            }

            if (!first)
            {
                ReadStarted.TrySetResult();
            }

            return new RateLimitsReadResult(CreateResponse(first ? 10 : 20), true, first ? 1 : 20);
        }

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            NotificationStarted.TrySetResult();
            if (first)
            {
                await disconnect.Task.WaitAsync(cancellationToken);
                throw new ChannelClosedException(new CodexClientException(
                    CodexClientErrorKind.TransportClosed,
                    "synthetic generation close"));
            }

            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }

        public void TriggerDisconnect() => disconnect.TrySetResult();

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                disconnect.TrySetResult();
                notifications.Writer.TryComplete();
                Disposed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        private static RateLimitsResponse CreateResponse(long usedPercent) => new()
        {
            RateLimits = new RateLimitSnapshot
            {
                LimitId = "late-generation-window",
                Primary = new RateLimitWindow
                {
                    UsedPercent = usedPercent,
                    WindowDurationMinutes = 300,
                    ResetsAt = 2_000,
                },
            },
        };
    }

    private sealed class GenerationClient(
        string limitId,
        long usedPercent,
        bool closesOnSignal,
        bool blockFirstRead) : ICodexAppServerClient
    {
        private readonly Channel<RateLimitsUpdatedNotification> notifications = Channel.CreateUnbounded<RateLimitsUpdatedNotification>();
        private readonly TaskCompletionSource disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int readCount;
        private int disposed;

        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => Volatile.Read(ref readCount);
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public async Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref readCount);
            if (blockFirstRead && count == 1)
            {
                FirstReadStarted.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return new RateLimitsReadResult(
                new RateLimitsResponse
                {
                    RateLimits = new RateLimitSnapshot
                    {
                        LimitId = limitId,
                        Primary = new RateLimitWindow
                        {
                            UsedPercent = usedPercent,
                            WindowDurationMinutes = 300,
                            ResetsAt = 2_000,
                        },
                    },
                },
                false);
        }

        public void Publish(RateLimitsUpdatedNotification notification) => notifications.Writer.TryWrite(notification);

        public void TriggerDisconnect() => disconnect.TrySetResult();

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            NotificationStarted.TrySetResult();
            if (closesOnSignal)
            {
                await disconnect.Task.WaitAsync(cancellationToken);
                throw new ChannelClosedException(new CodexClientException(
                    CodexClientErrorKind.TransportClosed,
                    "synthetic generation close"));
            }

            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                disconnect.TrySetResult();
                notifications.Writer.TryComplete();
                Disposed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingNotificationSink : IQuotaNotificationSink
    {
        public List<QuotaAlert> Alerts { get; } = [];

        public Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingNotificationSink : IQuotaNotificationSink
    {
        public int Attempts { get; private set; }
        public bool Throw { get; set; }

        public Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Throw)
            {
                throw new InvalidOperationException("synthetic notification failure");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ResetRecoveryClient : ICodexAppServerClient
    {
        private int readCount;

        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var usedPercent = Interlocked.Increment(ref readCount) == 1 ? 92 : 0;
            return Task.FromResult(new RateLimitsReadResult(
                new RateLimitsResponse
                {
                    RateLimits = new RateLimitSnapshot
                    {
                        Primary = new RateLimitWindow
                        {
                            UsedPercent = usedPercent,
                            WindowDurationMinutes = 300,
                            ResetsAt = 2_000,
                        },
                    },
                },
                false));
        }

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingNotificationSink : IQuotaNotificationSink
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class SnapshotRaceClient : ICodexAppServerClient
    {
        private readonly Channel<RateLimitsUpdatedNotification> notifications = Channel.CreateUnbounded<RateLimitsUpdatedNotification>();
        private int readCount;

        public TaskCompletionSource NotificationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CodexDiagnosticSnapshot Diagnostics { get; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

        public Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                return Task.FromResult(CreateSnapshot(20));
            }

            SecondReadStarted.TrySetResult();
            return Task.FromResult(CreateSnapshot(30));
        }

        public void Publish(RateLimitsUpdatedNotification notification) => notifications.Writer.TryWrite(notification);

        public async IAsyncEnumerable<RateLimitsUpdatedNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            NotificationStarted.TrySetResult();
            await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return notification;
            }
        }

        public ValueTask DisposeAsync()
        {
            notifications.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private static RateLimitsReadResult CreateSnapshot(long usedPercent) => new(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    Primary = new RateLimitWindow
                    {
                        UsedPercent = usedPercent,
                        WindowDurationMinutes = 300,
                    },
                },
            },
            false);
    }

    private sealed class ControlledClient : ICodexAppServerClient
    {
        private readonly object readGate = new();
        private readonly Dictionary<int, TaskCompletionSource> readCompletions = [];
        private int readCount;
        private int completedReadCount;

        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => Volatile.Read(ref readCount);
        public bool BlockConnect { get; set; }
        public bool Fail { get; set; }
        public CodexDiagnosticSnapshot Diagnostics { get; private set; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task WaitForReadCompletionAsync(int expected)
        {
            lock (readGate)
            {
                if (completedReadCount >= expected)
                {
                    return Task.CompletedTask;
                }

                if (!readCompletions.TryGetValue(expected, out var completion))
                {
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    readCompletions.Add(expected, completion);
                }

                return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public async Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectStarted.TrySetResult();
            if (BlockConnect)
            {
                await ConnectRelease.Task.WaitAsync(cancellationToken);
            }

            return new CodexSessionInfo("9.99.0", "9.99.0");
        }

        public async Task<RateLimitsReadResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            var currentRead = Interlocked.Increment(ref readCount);
            Started.TrySetResult();
            try
            {
                if (currentRead == 1)
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }

                if (Fail)
                {
                    throw new CodexClientException(CodexClientErrorKind.RequestTimeout, "synthetic");
                }

                Diagnostics = Diagnostics with { InitializeSucceeded = true, RateLimitsReadSucceeded = true };
                return new RateLimitsReadResult(
                    new RateLimitsResponse
                    {
                        RateLimits = new RateLimitSnapshot
                        {
                            PlanType = "plus",
                            Primary = new RateLimitWindow { UsedPercent = 25, WindowDurationMinutes = 300 },
                        },
                    },
                    false);
            }
            finally
            {
                lock (readGate)
                {
                    completedReadCount = Math.Max(completedReadCount, currentRead);
                    if (readCompletions.TryGetValue(currentRead, out var completion))
                    {
                        completion.TrySetResult();
                    }
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
