using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Protocol;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class QuotaPresentationTests
{
    [DataRow(100, false, true, QuotaTone.Accent)]
    [DataRow(51, false, true, QuotaTone.Accent)]
    [DataRow(50, false, true, QuotaTone.Warning)]
    [DataRow(20, false, true, QuotaTone.Warning)]
    [DataRow(19, false, true, QuotaTone.Critical)]
    [DataRow(0, false, true, QuotaTone.Critical)]
    [DataRow(50, true, true, QuotaTone.Unavailable)]
    [DataRow(50, false, false, QuotaTone.Unavailable)]
    [DataRow(-1, false, true, QuotaTone.Unavailable)]
    [DataRow(101, false, true, QuotaTone.Unavailable)]
    [TestMethod]
    public void QuotaTonePolicy_UsesBoundariesAndStaleOverride(
        int remaining,
        bool stale,
        bool available,
        QuotaTone expected)
    {
        Assert.AreEqual(expected, QuotaTonePolicy.For(remaining, stale, available));
    }

    [TestMethod]
    public void ResetCreditStates_HaveDistinctSummaries()
    {
        var expiry = new DateTimeOffset(2026, 8, 16, 2, 0, 0, TimeSpan.Zero);
        var summaries = new[]
        {
            new ResetCreditViewState(ResetCreditKind.Unavailable).Summary,
            new ResetCreditViewState(ResetCreditKind.Empty, 0).Summary,
            new ResetCreditViewState(ResetCreditKind.CountOnly, 2).Summary,
            new ResetCreditViewState(ResetCreditKind.PartialDetails, 2, expiry).Summary,
            new ResetCreditViewState(ResetCreditKind.CompleteDetails, 2, expiry).Summary,
        };

        Assert.AreEqual(5, summaries.Distinct().Count());
        StringAssert.Contains(summaries[2], "2 张");
        StringAssert.Contains(summaries[3], "最近已知");
        StringAssert.Contains(summaries[4], "最早");
    }

    [TestMethod]
    public void RelativeResetTextUsesConsistentSpacingAndUnknownCopy()
    {
        var now = new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.AreEqual("2 天 3 小时后重置", QuotaViewProjector.FormatRelative(now.AddDays(2).AddHours(3), now));
        Assert.AreEqual("3 小时 20 分钟后重置", QuotaViewProjector.FormatRelative(now.AddHours(3).AddMinutes(20), now));
        Assert.AreEqual("5 分钟后重置", QuotaViewProjector.FormatRelative(now.AddMinutes(5), now));
        Assert.AreEqual("剩余时间未知", QuotaViewProjector.FormatRelative(null, now));
    }

    [TestMethod]
    public void Projector_OnlyExposesCanonicalBucketFromMixedResponse()
    {
        var normalized = QuotaNormalizer.Normalize(new RateLimitsReadResult(
            new RateLimitsResponse
            {
                RateLimitsByLimitId = new Dictionary<string, RateLimitSnapshot>
                {
                    ["codex"] = Snapshot(20, "Plus"),
                    ["gpt-reserve"] = Snapshot(80, "Reserve"),
                },
            },
            ResetCreditsFieldPresent: false));

        var view = Project(normalized);

        Assert.HasCount(2, normalized.Windows);
        Assert.AreEqual("codex", normalized.Windows[0].BucketId);
        Assert.AreEqual("gpt-reserve", normalized.Windows[1].BucketId);
        Assert.HasCount(1, view.Windows);
        Assert.AreEqual("Plus", view.PlanBadge);
    }

    [TestMethod]
    public void Projector_DropsNonCanonicalOnlyResponse()
    {
        foreach (var bucketId in new[] { "gpt-reserve", "future-unknown" })
        {
            var normalized = QuotaNormalizer.Normalize(new RateLimitsReadResult(
                new RateLimitsResponse
                {
                    RateLimitsByLimitId = new Dictionary<string, RateLimitSnapshot>
                    {
                        [bucketId] = Snapshot(20, "Hidden"),
                    },
                },
                ResetCreditsFieldPresent: false));

            var view = Project(normalized);

            Assert.HasCount(1, normalized.Windows);
            Assert.IsEmpty(view.Windows);
            Assert.IsNull(view.PlanBadge);
        }
    }

    private static AppUiState Project(NormalizedQuotaSnapshot snapshot) =>
        new QuotaViewProjector(TimeProvider.System, TimeZoneInfo.Utc)
            .Project(snapshot, DateTimeOffset.UtcNow);

    private static RateLimitSnapshot Snapshot(long used, string planType) => new()
    {
        LimitId = "opaque",
        LimitName = "Codex",
        PlanType = planType,
        Primary = new RateLimitWindow
        {
            UsedPercent = used,
            WindowDurationMinutes = 300,
        },
    };
}
