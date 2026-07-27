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

    [TestMethod]
    public void ContentHeight_IsCompactAndContentDrivenForZeroToThreeWindows()
    {
        var heights = Enumerable.Range(0, 4).Select(PopupPlacement.ContentHeightDips).ToArray();

        Assert.AreEqual(270, heights[0]);
        Assert.IsTrue(heights.SequenceEqual(heights.OrderBy(value => value)));
        Assert.IsTrue(heights.Zip(heights.Skip(1), (left, right) => right > left).All(value => value));
        Assert.IsLessThanOrEqualTo(520, heights[^1]);
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
