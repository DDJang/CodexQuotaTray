using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodexQuotaTray.Core.TokenUsage;
using CodexQuotaTray.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenUsageTests
{
    [TestMethod]
    public void ManualRepairAddressPolicyAllowsOnlyPrivateOrLinkLocalIpv4()
    {
        Assert.IsTrue(TokenUsageSyncServer.IsAllowedRepairAddress(IPAddress.Parse("192.168.1.92")));
        Assert.IsTrue(TokenUsageSyncServer.IsAllowedRepairAddress(IPAddress.Parse("169.254.1.92")));
        Assert.IsFalse(TokenUsageSyncServer.IsAllowedRepairAddress(IPAddress.Parse("8.8.8.8")));
        Assert.IsFalse(TokenUsageSyncServer.IsAllowedRepairAddress(IPAddress.Parse("127.0.0.1")));
    }

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
    public async Task RepeatedCumulativeSnapshotDoesNotDoubleCount()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("repeated.jsonl",
            Event("2026-08-08T01:00:00Z", 1_000, 1_000),
            Event("2026-08-08T02:00:00Z", 1_800, 800),
            Event("2026-08-08T03:00:00Z", 1_800, 800),
            Event("2026-08-08T04:00:00Z", 2_500, 700));

        var result = await Scan(corpus);

        Assert.AreEqual(2_500L, result.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task RestartUsesPersistedOffsetsAndDoesNotReingestHistory()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("restart.jsonl",
            SessionMeta("session-restart"),
            Event("2026-08-08T01:00:00Z", 1_000, 1_000),
            Event("2026-08-08T02:00:00Z", 1_800, 800));
        var database = Path.Combine(corpus.Root, "ledger.sqlite3");
        var firstScanner = new TokenUsageScanner(database);
        var first = await firstScanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());
        var restartedScanner = new TokenUsageScanner(database);

        var restarted = await restartedScanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));

        Assert.AreEqual(1_800L, first.Summary.LifetimeTokens);
        Assert.AreEqual(1_800L, restarted.Summary.LifetimeTokens);
        Assert.AreEqual(0L, restartedScanner.LastBytesRead);
    }

    [TestMethod]
    public async Task VersionOneLedgerBackfillsDailyAggregatesIdempotently()
    {
        using var corpus = new TokenCorpus();
        var database = Path.Combine(corpus.Root, "version-one.sqlite3");
        await CreateVersionOneLedgerAsync(database, """
            INSERT INTO token_events VALUES
                ('a', 'session', '2026-08-07T01:00:00Z', '2026-08-07', 10, 8, 4, 2, 1),
                ('b', 'session', '2026-08-07T02:00:00Z', '2026-08-07', 15, 12, 6, 3, 2),
                ('c', 'session', '2026-08-08T01:00:00Z', '2026-08-08', 20, NULL, NULL, 4, NULL);
            """);
        var ledger = new TokenUsageLedger(database);

        await using (var first = await ledger.OpenAsync(CancellationToken.None))
        {
            var days = await TokenUsageLedger.QueryDaysAsync(first, CancellationToken.None);
            Assert.AreEqual(25L, days[0].TotalTokens);
            Assert.AreEqual(20L, days[0].InputTokens);
            Assert.AreEqual(20L, days[1].TotalTokens);
            Assert.IsNull(days[1].InputTokens);
        }

        await using (var restarted = await ledger.OpenAsync(CancellationToken.None))
        {
            var days = await TokenUsageLedger.QueryDaysAsync(restarted, CancellationToken.None);
            Assert.AreEqual(2, days.Count);
            Assert.AreEqual(45L, days.Sum(day => day.TotalTokens));
            Assert.AreEqual("2", await ScalarStringAsync(
                restarted,
                "SELECT value FROM ledger_meta WHERE key = 'schema_version';"));
        }
    }

    [TestMethod]
    public async Task DuplicateAndCorrectionUpdateDailyAggregateExactlyOnce()
    {
        using var corpus = new TokenCorpus();
        var ledger = new TokenUsageLedger(Path.Combine(corpus.Root, "aggregate.sqlite3"));
        await using var connection = await ledger.OpenAsync(CancellationToken.None);
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            var value = new LedgerTokenEvent(
                "event", "session", DateTimeOffset.Parse("2026-08-08T01:00:00Z"),
                new DateOnly(2026, 8, 8), new TokenCounters(100, 80, 40, 20, 5));
            Assert.IsTrue(await TokenUsageLedger.InsertEventAsync(connection, transaction, value, CancellationToken.None));
            Assert.IsFalse(await TokenUsageLedger.InsertEventAsync(connection, transaction, value, CancellationToken.None));
            await TokenUsageLedger.CorrectEventAsync(
                connection, transaction, "event", new TokenCounters(0, 0, 10, 0, 2), CancellationToken.None);
            await transaction.CommitAsync();
        }

        var day = (await TokenUsageLedger.QueryDaysAsync(connection, CancellationToken.None)).Single();
        Assert.AreEqual(100L, day.TotalTokens);
        Assert.AreEqual(80L, day.InputTokens);
        Assert.AreEqual(50L, day.CachedInputTokens);
        Assert.AreEqual(20L, day.OutputTokens);
        Assert.AreEqual(7L, day.ReasoningTokens);
    }

    [TestMethod]
    public async Task LargeVersionOneHistoryBackfillsWithoutLeavingQueriesOnEventTable()
    {
        using var corpus = new TokenCorpus();
        var database = Path.Combine(corpus.Root, "large-version-one.sqlite3");
        await CreateVersionOneLedgerAsync(database, """
            WITH RECURSIVE values_to_insert(value) AS (
                SELECT 0 UNION ALL SELECT value + 1 FROM values_to_insert WHERE value < 19999
            )
            INSERT INTO token_events
            SELECT printf('event-%d', value), 'session', '2026-01-01T00:00:00Z',
                   date('2026-01-01', printf('+%d days', value % 200)), 1, 1, 0, 0, 0
            FROM values_to_insert;
            """);
        var ledger = new TokenUsageLedger(database);
        await using var connection = await ledger.OpenAsync(CancellationToken.None);

        var days = await TokenUsageLedger.QueryDaysAsync(connection, CancellationToken.None);

        Assert.AreEqual(200, days.Count);
        Assert.AreEqual(20_000L, days.Sum(day => day.TotalTokens));
        Assert.AreEqual(200L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM token_daily_aggregate;"));
        var plan = await QueryPlanAsync(
            connection,
            "SELECT local_date FROM token_daily_aggregate ORDER BY local_date;");
        Assert.IsFalse(plan.Contains("token_events", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task SessionResumeInNewFileUsesSessionHighWater()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("resume-a.jsonl",
            SessionMeta("session-resume"),
            Event("2026-08-08T01:00:00Z", 1_000),
            Event("2026-08-08T02:00:00Z", 2_000));
        var scanner = new TokenUsageScanner();
        _ = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());
        corpus.Live("resume-b.jsonl",
            SessionMeta("session-resume"),
            Event("2026-08-08T03:00:00Z", 2_000),
            Event("2026-08-08T04:00:00Z", 2_600));

        var resumed = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));

        Assert.AreEqual(2_600L, resumed.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task ArchiveMoveAndDeletionKeepPersistedHistory()
    {
        using var corpus = new TokenCorpus();
        var live = corpus.Live("move.jsonl",
            SessionMeta("session-move"),
            Event("2026-08-08T01:00:00Z", 1_200));
        var scanner = new TokenUsageScanner();
        _ = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());
        var archived = Path.Combine(corpus.Root, "archived_sessions", "move.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.Move(live, archived);

        var moved = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));
        File.Delete(archived);
        var deleted = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(2));

        Assert.AreEqual(1_200L, moved.Summary.LifetimeTokens);
        Assert.AreEqual(1_200L, deleted.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task ForkReplayOnlyEstablishesChildBaseline()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("parent.jsonl",
            SessionMeta("parent"),
            Event("2026-08-08T01:00:00Z", 1_000),
            Event("2026-08-08T02:00:00Z", 5_000));
        corpus.Live("child.jsonl",
            SessionMeta("child", "parent"),
            Event("2026-08-08T01:00:00Z", 1_000),
            Event("2026-08-08T02:00:00Z", 3_000),
            Event("2026-08-08T03:00:00Z", 5_000),
            SessionMeta("parent"),
            Event("2026-08-08T04:00:00Z", 5_600));

        var result = await Scan(corpus);

        Assert.AreEqual(5_600L, result.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task SingleMetadataForkUsesInheritedImplicitBaseline()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("single-meta-inherited.jsonl",
            SessionMeta("fork-child-inherited", "parent"),
            DetailedEvent("2026-08-08T01:00:00Z", 400, 360, 320, 40, 8, 38, 34, 30, 4, 1),
            DetailedEvent("2026-08-08T02:00:00Z", 658, 590, 520, 68, 14, 90, 80, 70, 10, 2));

        var result = await Scan(corpus);
        var day = result.Days.Single();

        Assert.AreEqual(296L, day.TotalTokens);
        Assert.AreEqual(264L, day.InputTokens);
        Assert.AreEqual(230L, day.CachedInputTokens);
        Assert.AreEqual(32L, day.OutputTokens);
        Assert.AreEqual(7L, day.ReasoningTokens);
    }

    [TestMethod]
    public async Task SingleMetadataForkWithZeroBaselineCountsCompleteChildUsage()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("single-meta-zero.jsonl",
            SessionMeta("fork-child-zero", "parent"),
            DetailedEvent("2026-08-08T01:00:00Z", 24, 20, 10, 4, 1, 24, 20, 10, 4, 1),
            DetailedEvent("2026-08-08T02:00:00Z", 425, 370, 300, 55, 10, 137, 120, 100, 17, 3));

        var result = await Scan(corpus);
        var day = result.Days.Single();

        Assert.AreEqual(425L, day.TotalTokens);
        Assert.AreEqual(370L, day.InputTokens);
        Assert.AreEqual(300L, day.CachedInputTokens);
        Assert.AreEqual(55L, day.OutputTokens);
        Assert.AreEqual(10L, day.ReasoningTokens);
    }

    [TestMethod]
    public async Task ModernForkWaitsForChildUuidV7TaskBoundary()
    {
        using var corpus = new TokenCorpus();
        const string parentId = "019ff753-9210-7a41-9670-8f8d3a738a9d";
        const string childId = "019ffaca-c65e-78a3-8383-70d9d427eaf3";
        corpus.Live("modern-parent.jsonl",
            SessionMeta(parentId),
            DetailedEvent("2026-08-08T01:00:00Z", 300, 240, 180, 60, 10, 300, 240, 180, 60, 10));
        corpus.Live("modern-child.jsonl",
            SessionMeta(childId, parentId),
            SessionMeta(parentId),
            TaskStarted("019ff753-96f5-7ba1-9874-706a1c2e5a07"),
            DetailedEvent("2026-08-08T01:00:00Z", 150, 120, 90, 30, 5, 150, 120, 90, 30, 5),
            TaskStarted("019ff974-bc57-7d93-8712-3627856b327a"),
            DetailedEvent("2026-08-08T02:00:00Z", 300, 240, 180, 60, 10, 150, 120, 90, 30, 5),
            TaskStarted("019ffaca-cac2-7122-bd5c-e30f9b7c3715"),
            DetailedEvent("2026-08-08T03:00:00Z", 330, 264, 198, 66, 11, 30, 24, 18, 6, 1),
            DetailedEvent("2026-08-08T04:00:00Z", 350, 280, 210, 70, 12, 20, 16, 12, 4, 1));

        var result = await Scan(corpus);

        Assert.AreEqual(350L, result.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task StableEventIdsDoNotCollideAcrossSessions()
    {
        using var corpus = new TokenCorpus();
        var sameEvent = Event("2026-08-08T01:00:00Z", 100);
        corpus.Live("session-a.jsonl", SessionMeta("session-a"), sameEvent);
        corpus.Live("session-b.jsonl", SessionMeta("session-b"), sameEvent);

        var result = await Scan(corpus);

        Assert.AreEqual(200L, result.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task CategoryDeltasAndSameTotalCorrectionPreserveTotal()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("categories.jsonl",
            SessionMeta("categories"),
            CategoryEvent("2026-08-08T01:00:00Z", 1_000, 700, 300, 300, 100),
            CategoryEvent("2026-08-08T02:00:00Z", 1_500, 1_050, 450, 450, 150),
            CategoryEvent("2026-08-08T03:00:00Z", 1_500, 1_050, 500, 450, 175));

        var result = await Scan(corpus);
        var day = result.Days.Single();

        Assert.AreEqual(1_500L, day.TotalTokens);
        Assert.AreEqual(1_050L, day.InputTokens);
        Assert.AreEqual(500L, day.CachedInputTokens);
        Assert.AreEqual(450L, day.OutputTokens);
        Assert.AreEqual(175L, day.ReasoningTokens);
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
        corpus.Live("parent.jsonl", SessionMeta("parent"), copied);
        corpus.Live("fork.jsonl", SessionMeta("child", "parent"), copied, SessionMeta("parent"), suffix);
        corpus.Archived("parent.jsonl", SessionMeta("parent"), copied);

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
    public async Task ScannerBoundsLargeNonTokenLinesAndContinuesWithLaterEvents()
    {
        using var corpus = new TokenCorpus();
        corpus.Live(
            "large-record.jsonl",
            new string('x', 128 * 1024),
            Event("2026-08-08T01:00:00Z", 42, 42));
        var scanner = new TokenUsageScanner(512);

        var result = await scanner.ScanAsync(
            corpus.Root,
            TestTimeZone(),
            TestNow());

        Assert.AreEqual(42L, result.Summary.LifetimeTokens);
        Assert.IsGreaterThan(128 * 1024, scanner.LastBytesRead);
    }

    [TestMethod]
    public async Task ScannerSkipsOversizedTokenCandidateAndContinuesAtNextRecord()
    {
        using var corpus = new TokenCorpus();
        corpus.Live(
            "oversized-token.jsonl",
            "{\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"padding\":\"" + new string('x', 1024) + "\"}}",
            Event("2026-08-08T01:00:00Z", 7, 7));
        var scanner = new TokenUsageScanner(512);

        var result = await scanner.ScanAsync(
            corpus.Root,
            TestTimeZone(),
            TestNow());

        Assert.AreEqual(7L, result.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task ScannerReadsOnlyAppendedBytesAndReusesUnchangedFiles()
    {
        using var corpus = new TokenCorpus();
        corpus.Live("incremental.jsonl", Event("2026-08-08T01:00:00Z", 100, 100));
        var scanner = new TokenUsageScanner();

        var first = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());
        var firstBytes = scanner.TotalBytesRead;
        corpus.AppendLive("incremental.jsonl", Event("2026-08-08T02:00:00Z", 150, 50));
        var second = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));
        var appendBytes = scanner.LastBytesRead;
        var third = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(2));

        Assert.AreEqual(100L, first.Summary.LifetimeTokens);
        Assert.AreEqual(150L, second.Summary.LifetimeTokens);
        Assert.AreEqual(150L, third.Summary.LifetimeTokens);
        Assert.IsGreaterThan(appendBytes, firstBytes);
        Assert.IsGreaterThan(0L, appendBytes);
        Assert.AreEqual(0L, scanner.LastBytesRead);
    }

    [TestMethod]
    public async Task ConcurrentScannerCallersShareOnePhysicalRead()
    {
        using var corpus = new TokenCorpus();
        var path = corpus.Live("shared.jsonl", Event("2026-08-08T01:00:00Z", 123, 123));
        var scanner = new TokenUsageScanner();

        var results = await Task.WhenAll(
            scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow()),
            scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow()));

        Assert.IsTrue(results.All(result => result.Summary.LifetimeTokens == 123L));
        Assert.AreEqual(new FileInfo(path).Length, scanner.TotalBytesRead);
    }

    [TestMethod]
    public async Task ScannerRetriesIncompleteFinalRecordAfterItIsAppended()
    {
        using var corpus = new TokenCorpus();
        var complete = Event("2026-08-08T01:00:00Z", 123, 123);
        var split = complete.Length / 2;
        var path = corpus.LiveRaw("partial.jsonl", complete[..split]);
        var scanner = new TokenUsageScanner();

        var incomplete = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());
        File.AppendAllText(path, complete[split..] + Environment.NewLine, Encoding.UTF8);
        var completed = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));

        Assert.AreEqual(0L, incomplete.Summary.LifetimeTokens);
        Assert.AreEqual(123L, completed.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task ScannerRetriesIncompleteFinalRecordWhenTemporaryEofPrecedesTokenMarker()
    {
        using var corpus = new TokenCorpus();
        var complete = Event("2026-08-08T01:00:00Z", 123, 123);
        var markerIndex = complete.IndexOf("\"token_count\"", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, markerIndex);
        var path = corpus.LiveRaw("partial-before-marker.jsonl", complete[..markerIndex]);
        var scanner = new TokenUsageScanner();

        var incomplete = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());
        File.AppendAllText(path, complete[markerIndex..] + Environment.NewLine, Encoding.UTF8);
        var completed = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));

        Assert.AreEqual(0L, incomplete.Summary.LifetimeTokens);
        Assert.AreEqual(123L, completed.Summary.LifetimeTokens);
    }

    [TestMethod]
    public async Task ScannerKeepsLedgerHistoryWhenAFileIsTruncatedAndRewritten()
    {
        using var corpus = new TokenCorpus();
        var path = corpus.Live(
            "rewritten.jsonl",
            Event("2026-08-08T01:00:00Z", 100, 100),
            Event("2026-08-08T02:00:00Z", 200, 100));
        var scanner = new TokenUsageScanner();
        var first = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow());

        File.WriteAllLines(path, [Event("2026-08-08T03:00:00Z", 7, 7)], Encoding.UTF8);
        var rewritten = await scanner.ScanAsync(corpus.Root, TestTimeZone(), TestNow().AddMinutes(1));

        Assert.AreEqual(200L, first.Summary.LifetimeTokens);
        Assert.AreEqual(200L, rewritten.Summary.LifetimeTokens);
        Assert.AreEqual(new FileInfo(path).Length, scanner.LastBytesRead);
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
        Assert.AreEqual("Local", document.RootElement.GetProperty("source").GetString());
        Assert.AreEqual("Local", document.RootElement.GetProperty("scope").GetString());
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
    public async Task BurstClientsRespectFixedConcurrencyLimit()
    {
        using var corpus = new TokenCorpus();
        var server = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            "test-secret",
            corpus.Root,
            requestHeaderTimeout: TimeSpan.FromSeconds(5));
        server.Start(IPAddress.Loopback, 0);
        var clients = Enumerable.Range(0, TokenUsageSyncServer.MaximumConcurrentClients * 3)
            .Select(_ => new TcpClient())
            .ToArray();
        try
        {
            foreach (var client in clients)
            {
                await client.ConnectAsync(IPAddress.Loopback, server.Port);
                await client.GetStream().WriteAsync("GET /v1/token-usage HTTP/1.1\r\n"u8.ToArray());
            }

            await WaitUntilAsync(
                () => server.ActiveClientCount == TokenUsageSyncServer.MaximumConcurrentClients,
                TimeSpan.FromSeconds(2));

            Assert.AreEqual(TokenUsageSyncServer.MaximumConcurrentClients, server.ActiveClientCount);
            Assert.AreEqual(TokenUsageSyncServer.MaximumConcurrentClients, server.PeakActiveClientCount);
        }
        finally
        {
            await server.DisposeAsync();
            foreach (var client in clients) client.Dispose();
        }
    }

    [TestMethod]
    public async Task ShutdownDrainsActiveClientsAndClosesConnections()
    {
        using var corpus = new TokenCorpus();
        var server = new TokenUsageSyncServer(
            new TokenUsageScanner(),
            "test-secret",
            corpus.Root,
            requestHeaderTimeout: TimeSpan.FromSeconds(30));
        server.Start(IPAddress.Loopback, 0);
        var clients = Enumerable.Range(0, 3).Select(_ => new TcpClient()).ToArray();
        try
        {
            foreach (var client in clients)
            {
                await client.ConnectAsync(IPAddress.Loopback, server.Port);
                await client.GetStream().WriteAsync("GET /v1/token-usage HTTP/1.1\r\n"u8.ToArray());
            }
            await WaitUntilAsync(() => server.ActiveClientCount == clients.Length, TimeSpan.FromSeconds(2));

            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            var closed = await Task.WhenAll(clients.Select(client =>
                WaitForConnectionClosedAsync(client.GetStream(), TimeSpan.FromSeconds(2))));
            Assert.IsTrue(closed.All(value => value));
            Assert.AreEqual(0, server.ActiveClientCount);
        }
        finally
        {
            await server.DisposeAsync();
            foreach (var client in clients) client.Dispose();
        }
    }

    [TestMethod]
    public async Task HandlerExceptionIsObservedAndClientIsReleased()
    {
        var diagnostics = new List<string>();
        var server = new TokenUsageSyncServer(
            _ => Task.FromResult(Snapshot(1, DateTimeOffset.UtcNow)),
            "test-secret",
            () => throw new InvalidOperationException("quota failed"),
            diagnostic: message => diagnostics.Add(message));
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/v1/quota"));
            await WaitUntilAsync(() => server.ActiveClientCount == 0, TimeSpan.FromSeconds(2));

            Assert.IsTrue(diagnostics.Any(message => message.Contains("handler fault=InvalidOperationException", StringComparison.Ordinal)));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task DisposeDrainsBackgroundRefreshAndIsIdempotent()
    {
        var calls = 0;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new TokenUsageSyncServer(
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref calls) == 1) return Snapshot(1, DateTimeOffset.UtcNow);
                refreshStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Snapshot(2, DateTimeOffset.UtcNow);
                }
                finally
                {
                    refreshStopped.TrySetResult();
                }
            },
            "test-secret",
            TimeSpan.Zero);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        try
        {
            Assert.AreEqual(1L, await LifetimeTokensAsync(client));
            Assert.AreEqual(1L, await LifetimeTokensAsync(client));
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var first = server.DisposeAsync().AsTask();
            var second = server.DisposeAsync().AsTask();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.IsTrue(refreshStopped.Task.IsCompleted);
        }
        finally
        {
            await server.DisposeAsync();
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
        var refreshTimeout = Stopwatch.StartNew();
        long afterRefresh = 0;
        while (afterRefresh != 200 && refreshTimeout.Elapsed < TimeSpan.FromSeconds(2))
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
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanCalls = 0;
        await using var server = new TokenUsageSyncServer(
            _ =>
            {
                if (Interlocked.Increment(ref scanCalls) == 1)
                {
                    return Task.FromResult(Snapshot(100, DateTimeOffset.UtcNow));
                }

                refreshStarted.TrySetResult();
                return refresh.Task;
            },
            "test-secret",
            TimeSpan.FromHours(1));
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");
        Assert.AreEqual(100L, await LifetimeTokensAsync(client));

        var forced = LifetimeTokensAsync(client, "/v1/token-usage?refresh=force");
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
                     ResetsAt: null,
                     BucketId: "codex"),
            ],
            ResetCredits: new QuotaLanResetCredits(
                AvailableCount: 2,
                Credits:
                [
                    new QuotaLanResetCredit(
                        Id: "credit-1",
                        ResetType: "five_hour",
                        Status: "available",
                        GrantedAt: 1_899_000_000,
                        ExpiresAt: 1_900_000_000,
                        Title: "Five hour",
                        Description: null),
                ]));
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
        Assert.AreEqual("codex", window.GetProperty("bucketId").GetString());
        Assert.AreEqual(80, window.GetProperty("remainingPercent").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, window.GetProperty("resetsAt").ValueKind);
        var resetCredits = document.RootElement.GetProperty("resetCredits");
        Assert.AreEqual(2, resetCredits.GetProperty("availableCount").GetInt64());
        Assert.AreEqual("available", resetCredits.GetProperty("credits")[0].GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, resetCredits.GetProperty("credits")[0].GetProperty("description").ValueKind);
    }

    [TestMethod]
    public void LanResetCreditNullAndEmptyDetailsRemainDistinctDuringJsonRoundTrip()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var unavailable = new QuotaLanSnapshot(
            1,
            DateTimeOffset.UnixEpoch,
            "Plus",
            "available",
            [],
            new QuotaLanResetCredits(2, null));
        var empty = unavailable with { ResetCredits = new QuotaLanResetCredits(2, []) };

        var unavailableRoundTrip = JsonSerializer.Deserialize<QuotaLanSnapshot>(
            JsonSerializer.Serialize(unavailable, options),
            options);
        var emptyRoundTrip = JsonSerializer.Deserialize<QuotaLanSnapshot>(
            JsonSerializer.Serialize(empty, options),
            options);

        Assert.IsNotNull(unavailableRoundTrip?.ResetCredits);
        Assert.IsNull(unavailableRoundTrip!.ResetCredits!.Credits);
        Assert.IsNotNull(emptyRoundTrip?.ResetCredits);
        Assert.IsNotNull(emptyRoundTrip!.ResetCredits!.Credits);
        Assert.HasCount(0, emptyRoundTrip.ResetCredits.Credits!);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition(), $"Condition did not become true within {timeout}.");
    }

    private static Task<TokenUsageSnapshot> Scan(TokenCorpus corpus) => new TokenUsageScanner().ScanAsync(
        corpus.Root,
        TestTimeZone(),
        TestNow());

    private static TimeZoneInfo TestTimeZone() =>
        TimeZoneInfo.CreateCustomTimeZone("Test/+08", TimeSpan.FromHours(8), "Test/+08", "Test/+08");

    private static DateTimeOffset TestNow() =>
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static string Event(string timestamp, long total, long? last = null)
    {
        var totalUsage = Usage(total);
        var info = last is null
            ? $"\"total_token_usage\":{totalUsage}"
            : $"\"total_token_usage\":{totalUsage},\"last_token_usage\":{Usage(last.Value)}";
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{{info}}}}}}}";
    }

    [TestMethod]
    public async Task LanTokenEndpointFollowsCurrentWindowsTokenSourceAndEmitsScope()
    {
        var selected = TokenUsageDataSource.Local;
        await using var server = new TokenUsageSyncServer(
            _ => Task.FromResult(Snapshot(42, DateTimeOffset.UtcNow) with { Source = selected }),
            "test-secret",
            () => null,
            minimumScanInterval: TimeSpan.Zero);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        using var local = JsonDocument.Parse(await (await client.GetAsync("/v1/token-usage?refresh=force")).Content.ReadAsStringAsync());
        selected = TokenUsageDataSource.OAuth;
        using var account = JsonDocument.Parse(await (await client.GetAsync("/v1/token-usage?refresh=force")).Content.ReadAsStringAsync());
        selected = TokenUsageDataSource.CodexCli;
        using var cliAccount = JsonDocument.Parse(await (await client.GetAsync("/v1/token-usage?refresh=force")).Content.ReadAsStringAsync());

        Assert.AreEqual("Local", local.RootElement.GetProperty("source").GetString());
        Assert.AreEqual("Local", local.RootElement.GetProperty("scope").GetString());
        Assert.AreEqual("OAuth", account.RootElement.GetProperty("source").GetString());
        Assert.AreEqual("Account", account.RootElement.GetProperty("scope").GetString());
        Assert.AreEqual("CodexCli", cliAccount.RootElement.GetProperty("source").GetString());
        Assert.AreEqual("Account", cliAccount.RootElement.GetProperty("scope").GetString());
        Assert.AreEqual(42L, account.RootElement.GetProperty("summary").GetProperty("lifetimeTokens").GetInt64());
    }

    [TestMethod]
    public async Task LanTokenProjectionPreservesUnavailableAndAvailableZeroMetrics()
    {
        var availableToday = false;
        await using var server = new TokenUsageSyncServer(
            _ => Task.FromResult(
                Snapshot(0, DateTimeOffset.UtcNow) with
                {
                    Source = TokenUsageDataSource.OAuth,
                    AvailableMetrics = availableToday
                        ? TokenUsageMetricAvailability.Today
                        : TokenUsageMetricAvailability.None,
                }),
            "test-secret",
            () => null,
            minimumScanInterval: TimeSpan.Zero);
        server.Start(IPAddress.Loopback, 0);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret");

        using var unavailable = JsonDocument.Parse(
            await (await client.GetAsync("/v1/token-usage?refresh=force")).Content.ReadAsStringAsync());
        availableToday = true;
        using var available = JsonDocument.Parse(
            await (await client.GetAsync("/v1/token-usage?refresh=force")).Content.ReadAsStringAsync());

        Assert.AreEqual(JsonValueKind.Null, unavailable.RootElement.GetProperty("summary").GetProperty("todayTokens").ValueKind);
        Assert.AreEqual(JsonValueKind.Number, available.RootElement.GetProperty("summary").GetProperty("todayTokens").ValueKind);
        Assert.AreEqual(0L, available.RootElement.GetProperty("summary").GetProperty("todayTokens").GetInt64());
    }

    private static string SessionMeta(string sessionId, string? forkedFromId = null)
    {
        var fork = forkedFromId is null ? string.Empty : $",\"forked_from_id\":\"{forkedFromId}\"";
        return $"{{\"timestamp\":\"2026-08-08T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{sessionId}\"{fork}}}}}";
    }

    private static string CategoryEvent(
        string timestamp,
        long total,
        long input,
        long cached,
        long output,
        long reasoning) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new
                    {
                        total_tokens = total,
                        input_tokens = input,
                        cached_input_tokens = cached,
                        output_tokens = output,
                        reasoning_output_tokens = reasoning,
                    },
                },
            },
        });

    private static string DetailedEvent(
        string timestamp,
        long total,
        long input,
        long cached,
        long output,
        long reasoning,
        long lastTotal,
        long lastInput,
        long lastCached,
        long lastOutput,
        long lastReasoning) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new
                    {
                        total_tokens = total,
                        input_tokens = input,
                        cached_input_tokens = cached,
                        output_tokens = output,
                        reasoning_output_tokens = reasoning,
                    },
                    last_token_usage = new
                    {
                        total_tokens = lastTotal,
                        input_tokens = lastInput,
                        cached_input_tokens = lastCached,
                        output_tokens = lastOutput,
                        reasoning_output_tokens = lastReasoning,
                    },
                },
            },
        });

    private static string TaskStarted(string turnId) =>
        JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-08T00:00:00Z",
            type = "event_msg",
            payload = new { type = "task_started", turn_id = turnId },
        });

    private static string Usage(long total) =>
        $"{{\"total_tokens\":{total},\"input_tokens\":{total},\"cached_input_tokens\":0,\"output_tokens\":0,\"reasoning_output_tokens\":0}}";

    private static async Task CreateVersionOneLedgerAsync(string database, string seedSql)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = database, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ledger_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO ledger_meta VALUES('schema_version', '1');
            CREATE TABLE token_events(
                event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                local_date TEXT NOT NULL,
                total_tokens INTEGER NOT NULL,
                input_tokens INTEGER,
                cached_input_tokens INTEGER,
                output_tokens INTEGER,
                reasoning_tokens INTEGER
            );
            """ + seedSql;
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> QueryPlanAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return string.Join("\n", details);
    }

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

        internal string Live(string name, params string[] lines) => Write(Path.Combine(Root, "sessions", "2026", "08", "08", name), lines);

        internal void AppendLive(string name, params string[] lines) =>
            File.AppendAllLines(Path.Combine(Root, "sessions", "2026", "08", "08", name), lines, Encoding.UTF8);

        internal string LiveRaw(string name, string content)
        {
            var path = Path.Combine(Root, "sessions", "2026", "08", "08", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }

        internal string Archived(string name, params string[] lines) => Write(Path.Combine(Root, "archived_sessions", name), lines);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }

        private static string Write(string path, string[] lines)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return path;
        }
    }
}
