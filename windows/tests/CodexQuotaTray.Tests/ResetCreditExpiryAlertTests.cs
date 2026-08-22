using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Persistence;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class ResetCreditExpiryAlertTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(100);

    [TestMethod]
    public void DisabledOrUnavailableCardsDoNotCreateExpiryAlerts()
    {
        var input = Input(Now.AddHours(1));
        var disabled = QuotaAlertReducer.Reduce(
            null,
            [],
            new NotificationSettings(NotifyResetCreditExpiry: false),
            [input],
            Now);
        Assert.IsNull(disabled.Alert);

        var enabled = new NotificationSettings(NotifyResetCreditExpiry: true, ResetCreditExpiryLeadHours: 1);
        var invalid = QuotaAlertReducer.Reduce(
            null,
            [],
            enabled,
            [
                input with { Status = "redeemed" },
                input with { Status = "redeeming" },
                input with { Status = "unknown" },
                input with { Status = null },
                input with { ExpiresAtUtc = Now.AddHours(-1) },
                input with { ExpiresAtUtc = null },
            ],
            Now);
        Assert.IsNull(invalid.Alert);
    }

    [TestMethod]
    public void AllLeadChoicesAlertAtOrInsideTheirWindow()
    {
        foreach (var leadHours in new[] { 24, 6, 1 })
        {
            var reduction = QuotaAlertReducer.Reduce(
                null,
                [],
                new NotificationSettings(
                    NotifyResetCreditExpiry: true,
                    ResetCreditExpiryLeadHours: leadHours),
                [Input(Now.AddHours(leadHours))],
                Now);
            Assert.IsNotNull(reduction.Alert);
            Assert.AreEqual(QuotaAlertKind.ResetCreditExpiry, reduction.Alert!.Kind);
            Assert.HasCount(1, reduction.Alert.ResetCreditExpiryWindows);
        }
    }

    [TestMethod]
    public void CardsAreAtMostOnceAndPartialDetailsCanParticipate()
    {
        var settings = new NotificationSettings(NotifyResetCreditExpiry: true, ResetCreditExpiryLeadHours: 24);
        var first = QuotaAlertReducer.Reduce(null, [], settings, [Input(Now.AddHours(2))], Now);
        Assert.IsNotNull(first.Alert);
        var repeated = QuotaAlertReducer.Reduce(first.State, [], settings, [Input(Now.AddHours(2))], Now);
        Assert.IsNull(repeated.Alert);

        var second = Input(Now.AddHours(3)) with { Title = "second" };
        var partial = QuotaAlertReducer.Reduce(first.State, [], settings, [second], Now);
        Assert.IsNotNull(partial.Alert);
        Assert.HasCount(1, partial.Alert!.ResetCreditExpiryWindows);
    }

    [TestMethod]
    public void StableFingerprintIgnoresStatusAndUsesNullableCanonicalFields()
    {
        var first = new CodexQuotaTray.Core.Models.ResetCreditView(
            null,
            " weekly ",
            "available",
            Now,
            Now.AddDays(1),
            " Card ",
            null);
        var second = first with { Status = "REDEEMED" };
        Assert.AreEqual(ResetCreditFingerprint.Create(first), ResetCreditFingerprint.Create(second));
        Assert.AreNotEqual(
            ResetCreditFingerprint.Create(first),
            ResetCreditFingerprint.Create(first with { Title = "other" }));
    }

    [TestMethod]
    public void SettingsDefaultsAndLeadNormalizationAreSafeForOldAndInvalidValues()
    {
        var defaults = new NotificationSettings();
        Assert.IsFalse(defaults.NotifyResetCreditExpiry);
        Assert.AreEqual(24, defaults.ResetCreditExpiryLeadHours);
        var invalid = new NotificationSettings(
            NotifyResetCreditExpiry: true,
            ResetCreditExpiryLeadHours: 99);
        Assert.AreEqual(
            24,
            SettingsService.Normalize(new AppSettings(Notifications: invalid))
                .EffectiveNotifications.ResetCreditExpiryLeadHours);
    }

    [TestMethod]
    public async Task ResetCreditAlertStateRoundTripsThroughTheExistingAlertStateFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexQuotaTray-reset-alerts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new PreviewDataPaths(root);
            var persistence = new PreviewPersistence(new JsonFileStore(), paths);
            var state = new AlertStateDocument(
                1,
                [],
                [],
                false,
                new Dictionary<string, ResetCreditAlertState>
                {
                    ["fingerprint"] = new(Now, Now.AddDays(1), true),
                });
            await persistence.SaveAlertStateAsync(state, CancellationToken.None);

            var loaded = await persistence.LoadAlertStateAsync(CancellationToken.None);
            Assert.IsNotNull(loaded);
            Assert.IsTrue(loaded!.ResetCredits!.TryGetValue("fingerprint", out var credit));
            Assert.AreEqual(Now.AddDays(1), credit!.ExpiresAtUtc);
            Assert.IsTrue(credit.Notified);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ResetCreditExpiryInput Input(TimeSpan expiryOffset) =>
        Input(Now.Add(expiryOffset));

    private static ResetCreditExpiryInput Input(DateTimeOffset expiresAtUtc) => new(
        Fingerprint: "fingerprint-" + expiresAtUtc.ToUnixTimeSeconds(),
        Status: " available ",
        ExpiresAtUtc: expiresAtUtc,
        Title: "重置卡",
        ResetType: "weekly");
}
