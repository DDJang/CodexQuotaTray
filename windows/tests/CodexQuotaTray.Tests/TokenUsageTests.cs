using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenUsageTests
{
    [TestMethod]
    public void LanAddressSelectionPrefersPhysicalWifiOverVpnTunnelAndVirtualAdapters()
    {
        var candidates = new[]
        {
            Candidate("10.8.0.2", "255.255.255.0", NetworkInterfaceType.Ppp, "10.8.0.1", "1", "VPN"),
            Candidate("172.20.0.1", "255.255.240.0", NetworkInterfaceType.Ethernet, "172.20.0.254", "2", "Hyper-V Virtual Ethernet"),
            Candidate("192.168.50.20", "255.255.255.0", NetworkInterfaceType.Wireless80211, "192.168.50.1", "3", "physical"),
        };

        var selected = TokenUsageSyncServer.SelectPrivateLanSelection(candidates);
        Assert.AreEqual(IPAddress.Parse("192.168.50.20"), selected?.Address);
        Assert.AreEqual(3u, selected?.InterfaceIndex);
    }

    [TestMethod]
    public void LanAddressSelectionFailsClosedWithoutPhysicalOnLinkGateway()
    {
        var candidates = new[]
        {
            Candidate("10.8.0.2", "255.255.255.0", NetworkInterfaceType.Tunnel, "10.8.0.1", "1", "tunnel"),
            Candidate("192.168.50.20", "255.255.255.0", NetworkInterfaceType.Wireless80211, "192.168.60.1", "2", "physical"),
        };

        Assert.IsNull(TokenUsageSyncServer.SelectPrivateLanAddress(candidates));
    }

    [TestMethod]
    public async Task ScannerUsesLastUsageAndDoesNotSumCumulativeCounters()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("one.jsonl",
            Event("2026-08-08T01:00:00Z", 1_000, 1_000),
            Event("2026-08-08T02:00:00Z", 1_500, 500),
            Event("2026-08-08T03:00:00Z", 2_100, 600));

        var result = await Scan(corpus);

        Assert.AreEqual(2_100L, result.Summary.TodayTokens);
        Assert.AreEqual(2_100L, result.Summary.LifetimeTokens);
        Assert.AreEqual(2_100L, result.Days.Single().TotalTokens);
    }

    [TestMethod]
    public async Task ScannerUsesAuthoritativeTotalWithoutDoubleCountingBreakdowns()
    {
        using var corpus = new TokenCorpus();
        const string usage = "{\"total_tokens\":100,\"input_tokens\":100,\"cached_input_tokens\":50,\"output_tokens\":25,\"reasoning_output_tokens\":10}";
        corpus.Live(
            "breakdown.jsonl",
            "{\"timestamp\":\"2026-08-08T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":" + usage + ",\"last_token_usage\":" + usage + "}}}");

        var result = await Scan(corpus);
        var day = result.Days.Single();

        Assert.AreEqual(100L, day.TotalTokens);
        Assert.AreEqual(100L, day.InputTokens);
        Assert.AreEqual(50L, day.CachedInputTokens);
        Assert.AreEqual(25L, day.OutputTokens);
        Assert.AreEqual(10L, day.ReasoningTokens);
    }

    [TestMethod]
    public async Task ScannerFallsBackToCumulativeDeltaAndHandlesCounterReset()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("reset.jsonl",
            Event("2026-08-07T01:00:00Z", 1_000),
            Event("2026-08-07T02:00:00Z", 1_500),
            Event("2026-08-07T03:00:00Z", 200),
            Event("2026-08-07T04:00:00Z", 350));

        var result = await Scan(corpus);

        Assert.AreEqual(1_850L, result.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task ScannerSkipsMalformedLineAndReadsArchivedSessions()
    {
        using var corpus = new TokenCorpus();
        corpus.Archived(
            "archive.jsonl",
            "{broken",
            "{\"timestamp\":\"2026-08-08T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}",
            Event("2026-08-08T01:00:00Z", 42, 42));

        var result = await Scan(corpus);

        Assert.AreEqual(42L, result.Summary.LifetimeTokens);
        Assert.AreEqual(1, result.FilesScanned);
    }

    [TestMethod]
    public async Task ScannerDeduplicatesLiveArchiveAndForkCopiedHistory()
    {
        using var corpus = new TokenCorpus();
        var copied = Event("2026-08-07T01:00:00Z", 1_000, 1_000);
        var suffix = Event("2026-08-08T01:00:00Z", 1_400, 400);
        corpus.Live("parent.jsonl", copied);
        corpus.Live("fork.jsonl", copied, suffix);
        corpus.Archived("parent.jsonl", copied);

        var result = await Scan(corpus);

        Assert.AreEqual(1_400L, result.Summary.LifetimeTokens);
        Assert.AreEqual(3, result.FilesScanned);
    }

    [TestMethod]
    public async Task ScannerAggregatesLocalDayAndSummaryWindowsAndStreaks()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("days.jsonl",
            Event("2026-08-05T16:30:00Z", 10, 10),
            Event("2026-08-06T16:30:00Z", 30, 20),
            Event("2026-08-07T16:30:00Z", 60, 30),
            Event("2026-07-01T01:00:00Z", 100, 40));

        var result = await Scan(corpus);

        Assert.AreEqual(30L, result.Summary.TodayTokens);
        Assert.AreEqual(60L, result.Summary.Last7DaysTokens);
        Assert.AreEqual(60L, result.Summary.Last30DaysTokens);
        Assert.AreEqual(100L, result.Summary.LifetimeTokens);
        Assert.AreEqual(new DateOnly(2026, 7, 1), result.Summary.PeakDate);
        Assert.AreEqual(4, result.Summary.ActiveDays);
        Assert.AreEqual(3, result.Summary.CurrentStreak);
        Assert.AreEqual(3, result.Summary.LongestStreak);
    }

    [TestMethod]
    public async Task CurrentStreakUsesYesterdayWhenTodayHasNoUsage()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("days.jsonl",
            Event("2026-08-06T01:00:00Z", 10, 10),
            Event("2026-08-07T01:00:00Z", 20, 10));

        var result = await Scan(corpus);

        Assert.AreEqual(2, result.Summary.CurrentStreak);
    }

    [TestMethod]
    public async Task ScannerPreservesInt64AndEmptyCorpus()
    {
        using var corpus = new TokenCorpus();
        var empty = await Scan(corpus);
        Assert.AreEqual(0L, empty.Summary.LifetimeTokens);
        corpus.Live("large.jsonl", Event("2026-08-08T01:00:00Z", 5_000_000_000L, 5_000_000_000L));
        var large = await Scan(corpus);
        Assert.AreEqual(5_000_000_000L, large.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task LanServerEnforcesContractAndBearerAuthentication()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("one.jsonl", Event("2026-08-08T01:00:00Z", 123, 123));
        await using var server = new TokenUsageSyncServer(new TokenUsageScanner(), "test-secret", corpus.Root, TimeSpan.FromMinutes(1));
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        var missing = await client.GetAsync("/v1/token-usage");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        var wrong = await client.GetAsync("/v1/token-usage");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var correct = await client.GetAsync("/v1/token-usage");
        var unknown = await client.GetAsync("/unknown");
        var post = await client.PostAsync("/v1/token-usage", new StringContent(string.Empty));
        var json = await correct.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, correct.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.AreEqual("no-store", correct.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var forbidden in new[] { "session", "path", "email", "account", "prompt", "response" })
        {
            Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestMethod]
    public async Task PartialClientIsClosedWhenRequestHeaderTimeoutExpires()
    {
        using var corpus = new TokenCorpus();
        await using var server = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            "test-secret",
            corpus.Root,
            requestHeaderTimeout: TimeSpan.FromMilliseconds(100));
        server.Start(IPAddress.Loopback, 0);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var stream = client.GetStream();
        await stream.WriteAsync("GET /v1/token-usage HTTP/1.1\r\n"u8.ToArray());

        Assert.IsTrue(await WaitForConnectionClosedAsync(stream, TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task TenPartialClientsAreAllClosedWhenRequestHeaderTimeoutExpires()
    {
        using var corpus = new TokenCorpus();
        await using var server = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            "test-secret",
            corpus.Root,
            requestHeaderTimeout: TimeSpan.FromMilliseconds(100));
        server.Start(IPAddress.Loopback, 0);
        var clients = Enumerable.Range(0, 10).Select(_ => new TcpClient()).ToArray();
        try
        {
            foreach (var client in clients)
            {
                await client.ConnectAsync(IPAddress.Loopback, server.Port);
                await client.GetStream().WriteAsync("GET /v1/token-usage HTTP/1.1\r\n"u8.ToArray());
            }

            var closed = await Task.WhenAll(clients.Select(client =>
                WaitForConnectionClosedAsync(client.GetStream(), TimeSpan.FromSeconds(2))));
            Assert.IsTrue(closed.All(value => value));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task LanServerReusesNormalCacheButForceRefreshScansAgain()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("one.jsonl", Event("2026-08-08T01:00:00Z", 123, 123));
        await using var server = new TokenUsageSyncServer(new TokenUsageScanner(), "test-secret", corpus.Root, TimeSpan.FromMinutes(1));
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var first = JsonDocument.Parse(await (await client.GetAsync("/v1/token-usage")).Content.ReadAsStringAsync());
        corpus.Live("two.jsonl", Event("2026-08-08T02:00:00Z", 456, 456));

        var cached = JsonDocument.Parse(await (await client.GetAsync("/v1/token-usage")).Content.ReadAsStringAsync());
        var forced = JsonDocument.Parse(await (await client.GetAsync("/v1/token-usage?refresh=force")).Content.ReadAsStringAsync());

        Assert.AreEqual(123L, first.RootElement.GetProperty("summary").GetProperty("lifetimeTokens").GetInt64());
        Assert.AreEqual(123L, cached.RootElement.GetProperty("summary").GetProperty("lifetimeTokens").GetInt64());
        Assert.AreEqual(579L, forced.RootElement.GetProperty("summary").GetProperty("lifetimeTokens").GetInt64());
        Assert.IsTrue(
            forced.RootElement.GetProperty("generatedAtUtc").GetDateTimeOffset()
                > first.RootElement.GetProperty("generatedAtUtc").GetDateTimeOffset());
    }

    [TestMethod]
    public async Task ForceRefreshRequiresBearerAndDoesNotChangeQuotaSemantics()
    {
        using var corpus = new TokenCorpus();
        var quota = new QuotaLanSnapshot(
            SchemaVersion: 1,
            GeneratedAtUtc: new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            PlanType: "Plus",
            QuotaState: "available",
            Windows: []);
        await using var server = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            "test-secret",
            corpus.Root,
            quotaSnapshotProvider: () => quota);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        var unauthorized = await client.GetAsync("/v1/token-usage?refresh=force");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var quotaResponse = await client.GetAsync("/v1/quota?refresh=force");

        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, quotaResponse.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentForceRequestsRemainSuccessfulThroughScanGate()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("one.jsonl", Event("2026-08-08T01:00:00Z", 123, 123));
        await using var server = new TokenUsageSyncServer(new TokenUsageScanner(), "test-secret", corpus.Root, TimeSpan.Zero);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        var responses = await Task.WhenAll(
            client.GetAsync("/v1/token-usage?refresh=force"),
            client.GetAsync("/v1/token-usage?refresh=force"));

        Assert.IsTrue(responses.All(response => response.StatusCode == HttpStatusCode.OK));
    }

    [TestMethod]
    public async Task StaleNormalRequestsReturnImmediatelyAndShareOneBackgroundRefresh()
    {
        var scanCalls = 0;
        var refresh = new TaskCompletionSource<TokenUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSnapshot = Snapshot(100, new DateTimeOffset(2026, 8, 16, 1, 0, 0, TimeSpan.Zero));
        var refreshedSnapshot = Snapshot(200, new DateTimeOffset(2026, 8, 16, 2, 0, 0, TimeSpan.Zero));
        await using var server = new TokenUsageSyncServer(
            _ => ++scanCalls == 1 ? Task.FromResult(firstSnapshot) : refresh.Task,
            "test-secret",
            TimeSpan.Zero);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        Assert.AreEqual(100L, await LifetimeTokensAsync(client));
        var stale = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => LifetimeTokensAsync(client)));

        Assert.IsTrue(stale.All(value => value == 100L));
        Assert.AreEqual(2, scanCalls);
        refresh.SetResult(refreshedSnapshot);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        long afterRefresh = 0;
        while (afterRefresh != 200 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            afterRefresh = await LifetimeTokensAsync(client);
        }
        Assert.AreEqual(200L, afterRefresh);
    }

    [TestMethod]
    public async Task ForceRequestWaitsForFreshScan()
    {
        var refresh = new TaskCompletionSource<TokenUsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanCalls = 0;
        await using var server = new TokenUsageSyncServer(
            _ => ++scanCalls == 1 ? Task.FromResult(Snapshot(100, DateTimeOffset.UtcNow)) : refresh.Task,
            "test-secret",
            TimeSpan.FromHours(1));
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        Assert.AreEqual(100L, await LifetimeTokensAsync(client));

        var forced = LifetimeTokensAsync(client, "/v1/token-usage?refresh=force");
        await Task.Delay(30);
        Assert.IsFalse(forced.IsCompleted);
        refresh.SetResult(Snapshot(300, DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.AreEqual(300L, await forced);
    }

    [TestMethod]
    public async Task LanServerServesOnlyInjectedQuotaSnapshotAndKeepsTokenUsageContract()
    {
        var quota = new QuotaLanSnapshot(
            SchemaVersion: 1,
            GeneratedAtUtc: new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
            PlanType: "Plus",
            QuotaState: "available",
            Windows:
            [
                new QuotaLanWindow(
                    LimitId: "local:primary",
                    LimitName: null,
                    PlanType: null,
                    SourceSlot: "primary",
                    UsedPercent: 20,
                    RemainingPercent: 80,
                    PercentageReliable: true,
                    WindowDurationMins: 300,
                    ResetsAt: null),
            ]);
        QuotaLanSnapshot? availableQuota = quota;
        await using var server = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            "test-secret",
            quotaSnapshotProvider: () => availableQuota);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        var unauthorized = await client.GetAsync("/v1/quota");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        var quotaResponse = await client.GetAsync("/v1/quota");
        availableQuota = null;
        var unavailable = await client.GetAsync("/v1/quota");

        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, quotaResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.AreEqual("no-store", quotaResponse.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await quotaResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("available", document.RootElement.GetProperty("quotaState").GetString());
        var window = document.RootElement.GetProperty("windows")[0];
        Assert.AreEqual(80, window.GetProperty("remainingPercent").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, window.GetProperty("resetsAt").ValueKind);
    }

    private static async Task<long> LifetimeTokensAsync(HttpClient client, string path = "/v1/token-usage")
    {
        using var document = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("summary").GetProperty("lifetimeTokens").GetInt64();
    }

    private static TokenUsageSnapshot Snapshot(long lifetimeTokens, DateTimeOffset generatedAtUtc) => new(
        1,
        generatedAtUtc,
        "UTC",
        new TokenUsageSummary(0, 0, 0, lifetimeTokens, 0, null, 0, 0, 0),
        [],
        0,
        0,
        null,
        null);

    private static async Task<bool> WaitForConnectionClosedAsync(NetworkStream stream, TimeSpan timeout)
    {
        var read = stream.ReadAsync(new byte[1]).AsTask();
        if (await Task.WhenAny(read, Task.Delay(timeout)) != read)
        {
            return false;
        }

        try
        {
            return await read == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private static Task<TokenUsageSnapshot> Scan(TokenCorpus corpus) => new TokenUsageScanner().ScanAsync(
        corpus.Root,
        TimeZoneInfo.CreateCustomTimeZone("Test/+08", TimeSpan.FromHours(8), "Test/+08", "Test/+08"),
        new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero));

    private static string Event(string timestamp, long total, long? last = null)
    {
        var totalUsage = Usage(total);
        var info = last is null
            ? $"\"total_token_usage\":{totalUsage}"
            : $"\"total_token_usage\":{totalUsage},\"last_token_usage\":{Usage(last.Value)}";
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{{info}}}}}}}";
    }

    private static string Usage(long total) =>
        $"{{\"total_tokens\":{total},\"input_tokens\":{total},\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0}}";

    private static LanAddressCandidate Candidate(
        string address,
        string mask,
        NetworkInterfaceType type,
        string gateway,
        string id,
        string description) => new(
            IPAddress.Parse(address),
            IPAddress.Parse(mask),
            type,
            OperationalStatus.Up,
            [IPAddress.Parse(gateway)],
            uint.Parse(id),
            id,
            description);

    private sealed class TokenCorpus : IDisposable
    {
        internal TokenCorpus()
        {
            Root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray.TokenUsage", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal void Live(string name, params string[] lines) => Write(Path.Combine(Root, "sessions", "2026", "08", "08", name), lines);

        internal void Archived(string name, params string[] lines) => Write(Path.Combine(Root, "archived_sessions", name), lines);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static void Write(string path, string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
    }
}
