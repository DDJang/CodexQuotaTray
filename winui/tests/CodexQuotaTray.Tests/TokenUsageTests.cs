using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.TokenUsage;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenUsageTests
{
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
