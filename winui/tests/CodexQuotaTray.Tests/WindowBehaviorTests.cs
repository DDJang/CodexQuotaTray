using System.Drawing;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class WindowBehaviorTests
{
    [TestMethod]
    public void VisibilityToggle_RemainsStableAcrossOneHundredShowHideCycles()
    {
        var controller = new WindowVisibilityController();
        for (var index = 0; index < 100; index++)
        {
            Assert.IsTrue(controller.Toggle());
            Assert.IsFalse(controller.Toggle());
        }

        Assert.IsFalse(controller.DesiredVisible);
    }

    [TestMethod]
    public void VisibilityToggle_IsDeterministic()
    {
        var controller = new WindowVisibilityController();

        Assert.IsTrue(controller.Toggle());
        Assert.IsFalse(controller.Toggle());
        Assert.IsTrue(controller.Toggle());
    }

    [DataRow(1.0)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(1.75)]
    [DataRow(2.0)]
    [TestMethod]
    public void Placement_RemainsInsideNegativeCoordinateWorkArea(double scale)
    {
        var work = Rectangle.FromLTRB(-1920, -200, 0, 880);
        var tray = Rectangle.FromLTRB(-40, 840, -16, 864);
        var size = new Size(
            PopupPlacement.DipsToPixels(420, scale),
            PopupPlacement.DipsToPixels(470, scale));

        var result = PopupPlacement.PlaceNearTray(
            tray,
            work,
            size,
            PopupPlacement.DipsToPixels(8, scale));

        Assert.IsGreaterThanOrEqualTo(work.Left, result.X);
        Assert.IsGreaterThanOrEqualTo(work.Top, result.Y);
        Assert.IsLessThanOrEqualTo(work.Right, result.X + size.Width);
        Assert.IsLessThanOrEqualTo(work.Bottom, result.Y + size.Height);
    }

    [TestMethod]
    public void Placement_RecognizesAllTrayEdges()
    {
        var work = Rectangle.FromLTRB(0, 0, 1920, 1040);
        Assert.AreEqual(TrayEdge.Left, PopupPlacement.NearestEdge(new Rectangle(0, 400, 24, 24), work));
        Assert.AreEqual(TrayEdge.Top, PopupPlacement.NearestEdge(new Rectangle(800, 0, 24, 24), work));
        Assert.AreEqual(TrayEdge.Right, PopupPlacement.NearestEdge(new Rectangle(1896, 400, 24, 24), work));
        Assert.AreEqual(TrayEdge.Bottom, PopupPlacement.NearestEdge(new Rectangle(800, 1016, 24, 24), work));
    }

    [DataRow(100, 100, 0, 0, 1920, 1040, 420, 470, 8, 100, 100)]
    [DataRow(1700, 900, 0, 0, 1920, 1040, 420, 470, 8, 1492, 562)]
    [DataRow(-2200, -400, -1920, -200, 0, 880, 420, 470, 8, -1912, -192)]
    [TestMethod]
    public void Placement_ClampsRememberedPositionInsideWorkArea(
        int x,
        int y,
        int left,
        int top,
        int right,
        int bottom,
        int width,
        int height,
        int margin,
        int expectedX,
        int expectedY)
    {
        var result = PopupPlacement.ClampToWorkArea(
            new Point(x, y),
            Rectangle.FromLTRB(left, top, right, bottom),
            new Size(width, height),
            margin);

        Assert.AreEqual(new Point(expectedX, expectedY), result);
    }

    [TestMethod]
    public void Placement_AllowsRememberedPositionToTouchTaskbarWorkAreaEdge()
    {
        var workArea = Rectangle.FromLTRB(0, 0, 1920, 1040);
        var popup = new Size(420, 470);
        var location = new Point(1500, 570);

        var result = PopupPlacement.ClampToWorkArea(location, workArea, popup, margin: 0);

        Assert.AreEqual(location, result);
        Assert.AreEqual(workArea.Bottom, result.Y + popup.Height);
    }

    [TestMethod]
    public void ContentHeight_UsesMeasuredContentAndClampsOnlyToWorkArea()
    {
        Assert.AreEqual(286, PopupPlacement.ContentHeightPixels(286, 1, 1080));
        Assert.AreEqual(358, PopupPlacement.ContentHeightPixels(286, 1.25, 1350));
        Assert.AreEqual(1050, PopupPlacement.ContentHeightPixels(900, 1.5, 1080, 10));
    }

    [TestMethod]
    public void TrayRegistration_UsesFiniteRetries()
    {
        CollectionAssert.AreEqual(new[] { 0, 250, 500, 1000 }, TrayRegistrationPolicy.RetryDelaysMilliseconds.ToArray());
        CollectionAssert.AreEqual(new[] { 250, 500, 1000, 2000 }, TrayRegistrationPolicy.VerificationDelaysMilliseconds.ToArray());
        Assert.AreEqual(TrayRegistrationState.Registered, TrayRegistrationPolicy.StateAfterAttempt(true, 1));
        Assert.AreEqual(TrayRegistrationState.RetryPending, TrayRegistrationPolicy.StateAfterAttempt(false, 3));
        Assert.AreEqual(TrayRegistrationState.Failed, TrayRegistrationPolicy.StateAfterAttempt(false, 4));
    }

    [DataRow(0, 10, 20, 26, 36, true)]
    [DataRow(1, -40, 1000, -16, 1024, true)]
    [DataRow(unchecked((int)0x80004005), 10, 20, 26, 36, false)]
    [DataRow(0, 10, 20, 10, 36, false)]
    [DataRow(0, 10, 20, 26, 20, false)]
    [TestMethod]
    public void TrayRegistration_RequiresExplorerConfirmedNonEmptyRectangle(
        int result,
        int left,
        int top,
        int right,
        int bottom,
        bool expected)
    {
        Assert.AreEqual(
            expected,
            TrayRegistrationPolicy.IsExplorerConfirmationSuccessful(result, left, top, right, bottom));
    }

    [DataRow(true, true, false, false, BackdropKind.Opaque)]
    [DataRow(true, true, true, true, BackdropKind.Opaque)]
    [DataRow(true, true, true, false, BackdropKind.DesktopAcrylic)]
    [DataRow(false, true, true, false, BackdropKind.Mica)]
    [DataRow(false, false, true, false, BackdropKind.Opaque)]
    [TestMethod]
    public void BackdropPolicy_HasRequiredFallbackOrder(
        bool acrylic,
        bool mica,
        bool transparency,
        bool highContrast,
        BackdropKind expected)
    {
        Assert.AreEqual(expected, BackdropPolicy.Select(acrylic, mica, transparency, highContrast));
    }
}
