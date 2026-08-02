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
        var first = rpc.RequestAsync("first", new { value = 1 }, TimeSpan.FromSeconds(1), CancellationToken.None);
        var second = rpc.RequestAsync("second", null, TimeSpan.FromSeconds(1), CancellationToken.None);
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

        _ = await service.GetSnapshotAsync(CancellationToken.None);
        Assert.AreEqual(1, client.ReadCount);
        await clock.TimerCreated.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        client.Fail = true;
        clock.Advance(TimeSpan.FromMinutes(30));
        await WaitForReadCountAsync(client, 2);
        Assert.AreEqual(2, client.ReadCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);
        Assert.AreEqual(2, client.ReadCount);

        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitForReadCountAsync(client, 3);
        Assert.AreEqual(3, client.ReadCount);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(100);
        Assert.AreEqual(3, client.ReadCount);

        client.Fail = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForReadCountAsync(client, 4);
        Assert.AreEqual(4, client.ReadCount);
        await Task.Delay(500);

        clock.Advance(TimeSpan.FromMinutes(29));
        await Task.Delay(100);
        Assert.AreEqual(4, client.ReadCount);

        clock.Advance(TimeSpan.FromMinutes(1));
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
            AppSettings.Defaults with { RefreshMode = RefreshMode.ManualOnly },
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
        client.Publish(new RateLimitsUpdatedNotification(
            new RateLimitsResponse
            {
                RateLimits = new RateLimitSnapshot
                {
                    Primary = new RateLimitWindow { UsedPercent = 90, WindowDurationMinutes = 300 },
                },
            },
            false));
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

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
        await sink.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(100);

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (client.ReadCount < expected)
        {
            await Task.Delay(10, timeout.Token);
        }
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

        public async Task WaitForLinesAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while (Lines.Count < count)
            {
                await changed.WaitAsync(timeout.Token);
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

    private sealed class RecordingNotificationSink : IQuotaNotificationSink
    {
        public List<QuotaAlert> Alerts { get; } = [];

        public Task ShowAsync(QuotaAlert alert, CancellationToken cancellationToken)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
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
        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }
        public bool BlockConnect { get; set; }
        public bool Fail { get; set; }
        public CodexDiagnosticSnapshot Diagnostics { get; private set; } = new(CliFound: true, CliVersion: "9.99.0");

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
            ReadCount++;
            Started.TrySetResult();
            if (ReadCount == 1)
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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
