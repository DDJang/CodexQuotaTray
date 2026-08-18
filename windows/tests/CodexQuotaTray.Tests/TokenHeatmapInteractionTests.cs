using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TokenHeatmapInteractionTests
{
    [TestMethod]
    public void HitTestAssignsGapHalvesAndEdgeInsetsWithoutOverlap()
    {
        Assert.AreEqual(0, TokenHeatmapInteraction.HitTest(8.5f, 8.5f, 119));
        Assert.AreEqual(0, TokenHeatmapInteraction.HitTest(18f, 8.5f, 119));
        Assert.AreEqual(7, TokenHeatmapInteraction.HitTest(19f, 8.5f, 119));
        Assert.AreEqual(7, TokenHeatmapInteraction.HitTest(20f, 8.5f, 119));
        Assert.AreEqual(0, TokenHeatmapInteraction.HitTest(8.5f, 18f, 119));
        Assert.AreEqual(1, TokenHeatmapInteraction.HitTest(8.5f, 19f, 119));
        Assert.AreEqual(0, TokenHeatmapInteraction.HitTest(-2f, 8.5f, 119));
        Assert.AreEqual(112, TokenHeatmapInteraction.HitTest(354f, 8.5f, 119));
        Assert.AreEqual(118, TokenHeatmapInteraction.HitTest(354f, 144f, 119));
    }

    [TestMethod]
    public void HitTestRejectsCoordinatesBeyondTheExpandedHeatmapBounds()
    {
        Assert.IsNull(TokenHeatmapInteraction.HitTest(-2.01f, 8.5f, 119));
        Assert.IsNull(TokenHeatmapInteraction.HitTest(355f, 8.5f, 119));
        Assert.IsNull(TokenHeatmapInteraction.HitTest(8.5f, -2.01f, 119));
        Assert.IsNull(TokenHeatmapInteraction.HitTest(8.5f, 145f, 119));
        Assert.IsNull(TokenHeatmapInteraction.HitTest(400f, 8.5f, 119));
    }

    [TestMethod]
    public void SelectedScaleMatchesAndroidInteractionSemantics()
    {
        Assert.AreEqual(1.5f, TokenHeatmapInteraction.SelectedScale, 0.001f);
        Assert.AreEqual(2f, TokenHeatmapInteraction.HitSurfaceInset, 0.001f);
    }

    [TestMethod]
    public void TooltipSpringParametersFavorFastTrackingWithoutInstantMovement()
    {
        Assert.AreEqual(0.82f, TokenHeatmapInteraction.TooltipSpringDampingRatio, 0.001f);
        Assert.AreEqual(110, TokenHeatmapInteraction.TooltipSpringPeriodMilliseconds);
    }

    [TestMethod]
    public void TooltipPlacementStaysAboveScaledCellWithoutViewportClamp()
    {
        var firstCell = TokenHeatmapInteraction.PlaceTooltipAboveCell(0, 119, 120f, 48f);
        var adjacentCell = TokenHeatmapInteraction.PlaceTooltipAboveCell(1, 119, 120f, 48f);
        var lastCell = TokenHeatmapInteraction.PlaceTooltipAboveCell(118, 119, 120f, 48f);

        Assert.AreNotEqual(firstCell, adjacentCell);
        Assert.AreEqual(-51.5f, firstCell.X, 0.001f);
        Assert.AreEqual(-60.25f, firstCell.Y, 0.001f);
        Assert.AreEqual(284.5f, lastCell.X, 0.001f);
        Assert.AreEqual(65.75f, lastCell.Y, 0.001f);
    }

}
