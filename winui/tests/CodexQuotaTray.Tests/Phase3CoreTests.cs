using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Protocol;
using CodexQuotaTray.Core.Runtime;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class Phase3CoreTests
{
    [TestMethod]
    public void RefreshCoordinator_ManualOnlySuppressesEveryNonManualReason()
    {
        var coordinator = new RefreshCoordinator();
        coordinator.SetMode(RefreshMode.ManualOnly);
        foreach (var reason in Enum.GetValues<RefreshReason>().Where(value => value != RefreshReason.Manual))
        {
            Assert.AreEqual(RefreshDecision.Suppress, coordinator.Request(reason), reason.ToString());
        }

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
    public void RefreshIntervalsAndStaleThresholdsMatchPolicy()
    {
        var coordinator = new RefreshCoordinator();
        Assert.AreEqual(TimeSpan.FromMinutes(30), coordinator.EffectiveInterval(51));
        Assert.AreEqual(TimeSpan.FromMinutes(15), coordinator.EffectiveInterval(21));
        Assert.AreEqual(TimeSpan.FromMinutes(5), coordinator.EffectiveInterval(20));
        Assert.AreEqual(TimeSpan.FromMinutes(60), coordinator.StaleAfter(51));
        coordinator.SetMode(RefreshMode.ManualOnly);
        Assert.AreEqual(TimeSpan.FromMinutes(60), coordinator.StaleAfter(1));
    }

    [TestMethod]
    public void RefreshBackoffResetsAfterSuccess()
    {
        var coordinator = new RefreshCoordinator();
        _ = coordinator.Request(RefreshReason.Manual);
        _ = coordinator.Complete(false, DateTimeOffset.UnixEpoch);
        Assert.AreEqual(TimeSpan.FromMinutes(1), coordinator.EffectiveInterval(90));
        _ = coordinator.Request(RefreshReason.Manual);
        _ = coordinator.Complete(true, DateTimeOffset.UnixEpoch.AddMinutes(1));
        Assert.AreEqual(TimeSpan.FromMinutes(30), coordinator.EffectiveInterval(90));
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
        var value = AlertWindowIdentity.Create(raw, "primary", 300, 0);
        Assert.IsTrue(value.StartsWith("sha256:", StringComparison.Ordinal));
        Assert.IsFalse(value.Contains(raw, StringComparison.Ordinal));
        Assert.AreEqual(71, value.Length);
    }

    [TestMethod]
    public void FirstAlertSnapshotEstablishesBaselineWithoutNotification()
    {
        var input = Input(19);
        var reduction = QuotaAlertReducer.Reduce(null, [input], new NotificationSettings());
        Assert.IsNull(reduction.Alert);
        Assert.AreEqual(19, reduction.State.Windows[input.PseudonymousKey].LastReliableRemaining);
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
    public void SmallResetCorrectionDoesNotStartNewCycle()
    {
        var previous = new AlertWindowState("window", 10_080, DateTimeOffset.UnixEpoch.AddDays(7), 10, [20, 10]);
        var current = Input(11) with { WindowDurationMinutes = 10_080, ResetAtUtc = previous.ResetAtUtc!.Value.AddMinutes(6) };
        Assert.IsFalse(QuotaAlertReducer.IsNewCycle(previous, current));
        current = current with { ResetAtUtc = previous.ResetAtUtc.Value.AddDays(4) };
        Assert.IsTrue(QuotaAlertReducer.IsNewCycle(previous, current));
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
    public async Task SettingsMigratesLegacyRefreshAndNotifications()
    {
        using var directory = new TemporaryDirectory();
        var paths = new PreviewDataPaths(directory.Path);
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(paths.Settings, "{\"refreshMinutes\":15,\"notifyRemaining20\":false,\"notifyRemaining5\":false,\"notifyExhausted\":false}");
        var service = new SettingsService(new JsonFileStore(), paths);
        var settings = await service.LoadAsync(CancellationToken.None);
        Assert.AreEqual(RefreshMode.Every15Minutes, settings.RefreshMode);
        Assert.IsFalse(settings.EffectiveNotifications.Remaining20);
        Assert.IsFalse(settings.EffectiveNotifications.Remaining10);
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

    private static AlertInput Input(int remaining) => new(
        "window",
        "7 天额度",
        remaining,
        true,
        10_080,
        DateTimeOffset.UnixEpoch.AddDays(7));

    private sealed class NotificationReader : TextReader
    {
        private readonly Channel<string> values = Channel.CreateUnbounded<string>();

        internal void Write(string value) => values.Writer.TryWrite(value);

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await values.Reader.ReadAsync(cancellationToken);
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
