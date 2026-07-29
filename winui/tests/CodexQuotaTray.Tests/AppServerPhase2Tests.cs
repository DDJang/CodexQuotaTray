using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using CodexQuotaTray.Core.Protocol;

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

    private sealed class FrozenTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
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

    private sealed class ControlledClient : ICodexAppServerClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }
        public bool Fail { get; set; }
        public CodexDiagnosticSnapshot Diagnostics { get; private set; } = new(CliFound: true, CliVersion: "9.99.0");

        public Task<CodexSessionInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CodexSessionInfo("9.99.0", "9.99.0"));

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
