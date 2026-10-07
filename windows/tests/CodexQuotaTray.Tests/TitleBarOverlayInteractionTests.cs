using System.Drawing;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TitleBarOverlayInteractionTests
{
    [TestMethod]
    public void RefreshingLabelWinsOverOldFailureEvenWhenNoQuotaHasLoaded()
    {
        var state = new AppUiState("Codex", null, "刷新失败", StatusTone.Error, [],
            new ResetCreditViewState(ResetCreditKind.Unavailable), IsRefreshing: true, IsPrototype: false);
        Assert.AreEqual("刷新中…", TitleBarQuotaOverlay.Project(state).Text);
    }
    [TestMethod]
    public void TextMaskProducesClickableSpacesAndPremultipliedNeutralGlyphs()
    {
        byte[] pixels = [0, 0, 0, 0, 255, 255, 255, 0, 128, 128, 128, 0];
        TitleBarOverlayInteraction.ComposeTextPixels(pixels);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 1, 0x90, 0x8F, 0x8E, 255, 72, 71, 71, 128 }, pixels);
    }

    [TestMethod]
    public void ClickAcceptsWhitespaceButRejectsDraggingAndReleaseOutside()
    {
        var size = new Size(400, 48);
        Assert.IsTrue(TitleBarOverlayInteraction.IsClick(new Point(190, 24), new Point(190, 24), size, 192));
        Assert.IsTrue(TitleBarOverlayInteraction.IsClick(new Point(190, 24), new Point(196, 28), size, 192));
        Assert.IsFalse(TitleBarOverlayInteraction.IsClick(new Point(190, 24), new Point(210, 24), size, 192));
        Assert.IsFalse(TitleBarOverlayInteraction.IsClick(new Point(399, 24), new Point(400, 24), size, 192));
        Assert.IsFalse(TitleBarOverlayInteraction.IsClick(new Point(10, 10), new Point(-1, 10), size, 192));
        Assert.IsFalse(TitleBarOverlayInteraction.IsClick(new Point(10, 10), new Point(10, 10), size, 0));
    }

    [TestMethod]
    public void RapidClicksAllowOneRefreshUntilCompletion()
    {
        var gate = new TitleBarOverlayRefreshGate();
        Assert.IsTrue(gate.TryBegin());
        Parallel.For(0, 100, _ => Assert.IsFalse(gate.TryBegin()));
        Assert.IsTrue(gate.IsInFlight);
        gate.Complete();
        Assert.IsFalse(gate.IsInFlight);
        Assert.IsTrue(gate.TryBegin());
        gate.Complete();
    }
}
