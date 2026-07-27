using CodexQuotaTray.Core.Models;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class QuotaPresentationTests
{
    [DataRow(100, false, true, QuotaTone.Accent)]
    [DataRow(21, false, true, QuotaTone.Accent)]
    [DataRow(20, false, true, QuotaTone.Warning)]
    [DataRow(11, false, true, QuotaTone.Warning)]
    [DataRow(10, false, true, QuotaTone.Critical)]
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
}
