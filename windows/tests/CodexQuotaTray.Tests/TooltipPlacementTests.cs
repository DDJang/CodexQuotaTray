using System.Drawing;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TooltipPlacementTests
{
    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.5d)]
    [DataRow(2d)]
    public void TooltipLeavesRoomBelowHandCursorAtEachScale(double scale)
    {
        var cursor = new Point(500, 40);
        var popup = new Size(600, 260);
        var workArea = new Rectangle(0, 0, 1920, 1080);
        var location = PopupPlacement.PlaceTooltip(cursor, workArea, popup, scale);

        Assert.AreEqual(cursor.X + PopupPlacement.DipsToPixels(12, scale), location.X);
        Assert.AreEqual(cursor.Y + PopupPlacement.DipsToPixels(32, scale), location.Y);
        Assert.IsTrue(workArea.Contains(new Rectangle(location, popup)));
    }

    [TestMethod]
    public void TooltipFlipsAboveCursorNearBottomOfNegativeCoordinateMonitor()
    {
        var cursor = new Point(-1200, -40);
        var popup = new Size(600, 260);
        var workArea = new Rectangle(-1920, -1080, 1920, 1080);
        var location = PopupPlacement.PlaceTooltip(cursor, workArea, popup, 1.5);

        Assert.AreEqual(cursor.Y - 12 - popup.Height, location.Y);
        Assert.IsTrue(workArea.Contains(new Rectangle(location, popup)));
    }

    [TestMethod]
    public void TooltipClampsAtRightEdgeAndOversizedTooltipKeepsItsOriginVisible()
    {
        var workArea = new Rectangle(-800, 100, 800, 600);
        var cursor = new Point(-10, 110);
        var location = PopupPlacement.PlaceTooltip(cursor, workArea, new Size(600, 260), 2);
        Assert.AreEqual(-616, location.X);
        Assert.AreEqual(174, location.Y);

        location = PopupPlacement.PlaceTooltip(cursor, workArea, new Size(1000, 900), 2);
        Assert.AreEqual(workArea.Location, location);
    }
}
