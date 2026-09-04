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
    public void Placement_FirstOpenUsesBottomRightWithWorkAreaMargin()
    {
        var workArea = Rectangle.FromLTRB(0, 0, 1920, 1040);
        var popup = new Size(840, 520);
        var margin = 16;

        var result = PopupPlacement.PlaceAtBottomRight(workArea, popup, margin);

        Assert.AreEqual(new Point(1064, 504), result);
        Assert.AreEqual(workArea.Right - margin, result.X + popup.Width);
        Assert.AreEqual(workArea.Bottom - margin, result.Y + popup.Height);
    }

    [TestMethod]
    public void Placement_NearTrayClampPreservesMarginWhenCenteredPositionOverflows()
    {
        var workArea = Rectangle.FromLTRB(0, 0, 1920, 1040);
        var tray = Rectangle.FromLTRB(1896, 1016, 1920, 1040);
        var popup = new Size(840, 520);
        var margin = 16;

        var result = PopupPlacement.PlaceNearTray(tray, workArea, popup, margin);

        Assert.AreEqual(workArea.Right - popup.Width - margin, result.X);
        Assert.AreEqual(workArea.Bottom - popup.Height - margin, result.Y);
    }

    [DataRow(1.0)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    [TestMethod]
    public void ContextMenuPlacement_StaysInsideWorkAreaAtTargetDpi(double scale)
    {
        var workArea = Rectangle.FromLTRB(-1920, -120, 0, 980);
        var popup = new Size(
            PopupPlacement.DipsToPixels(160, scale),
            PopupPlacement.DipsToPixels(131, scale));
        var margin = PopupPlacement.DipsToPixels(8, scale);
        var anchors = new[]
        {
            new Rectangle(-1918, 300, 24, 24),
            new Rectangle(-980, -118, 24, 24),
            new Rectangle(-24, 300, 24, 24),
            new Rectangle(-980, 956, 24, 24),
        };

        foreach (var anchor in anchors)
        {
            var result = PopupPlacement.PlaceNearTray(anchor, workArea, popup, margin);

            Assert.IsGreaterThanOrEqualTo(workArea.Left + margin, result.X);
            Assert.IsGreaterThanOrEqualTo(workArea.Top + margin, result.Y);
            Assert.IsLessThanOrEqualTo(workArea.Right - margin, result.X + popup.Width);
            Assert.IsLessThanOrEqualTo(workArea.Bottom - margin, result.Y + popup.Height);
        }
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
    public void ContentHeight_PreservesMeasuredClientHeightAcrossDpi()
    {
        Assert.AreEqual(420, PopupPlacement.ContentHeightPixels(420, 1, 1080));
        Assert.AreEqual(525, PopupPlacement.ContentHeightPixels(420, 1.25, 1350));
        Assert.AreEqual(840, PopupPlacement.ContentHeightPixels(420, 2, 2160));
    }

    [TestMethod]
    public void Placement_DefaultMarginIsDpiAware()
    {
        Assert.AreEqual(24, PopupPlacement.DipsToPixels(PopupPlacement.DefaultMarginDips, 2));
    }

    [TestMethod]
    public void NaturalContentHeight_UsesLastVisibleBottomInsteadOfInflatedViewport()
    {
        Assert.AreEqual(210, PopupPlacement.NaturalContentHeight(176, 24, 10, 260));
        Assert.AreEqual(260, PopupPlacement.NaturalContentHeight(double.NaN, 24, 10, 260));
    }

    [TestMethod]
    public void QuotaPageHeight_UsesFooterBoundaryInsteadOfInflatedDesiredSize()
    {
        var mainWindow = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml.cs"));
        var boundaryEventStart = mainWindow.IndexOf(
            "quotaView.ContentBottomBoundary.SizeChanged +=",
            StringComparison.Ordinal);
        var boundaryEventEnd = mainWindow.IndexOf(
            "var windowId =",
            boundaryEventStart,
            StringComparison.Ordinal);

        StringAssert.Contains(mainWindow, "quotaView.ContentBottomBoundary");
        StringAssert.Contains(mainWindow, ".TransformToVisual(PanelContent)");
        StringAssert.Contains(mainWindow, "PanelContent.Padding.Bottom");
        Assert.IsGreaterThanOrEqualTo(0, boundaryEventStart);
        Assert.IsGreaterThan(boundaryEventStart, boundaryEventEnd);
        var boundaryEvent = mainWindow[boundaryEventStart..boundaryEventEnd];
        StringAssert.Contains(boundaryEvent, "if (!pageTransitionRunning)");
        StringAssert.Contains(boundaryEvent, "QueuePositionIfVisible(forceResize: true);");
    }

    [TestMethod]
    public void ContentHeightInterpolation_SmoothlySupportsGrowingAndShrinkingPages()
    {
        Assert.AreEqual(200, PopupPlacement.InterpolateContentHeight(200, 400, 0));
        Assert.AreEqual(400, PopupPlacement.InterpolateContentHeight(200, 400, 1));
        Assert.IsGreaterThan(200, PopupPlacement.InterpolateContentHeight(200, 400, 0.5));
        Assert.IsLessThan(400, PopupPlacement.InterpolateContentHeight(400, 200, 0.5));
    }

    [TestMethod]
    public void ClientResize_DeduplicatesOnlyTheSamePendingPhysicalSize()
    {
        Assert.IsTrue(PopupPlacement.ShouldResizeClient(840, 760, 840, 520, null, null));
        Assert.IsFalse(PopupPlacement.ShouldResizeClient(840, 760, 840, 520, 840, 520));
        Assert.IsTrue(PopupPlacement.ShouldResizeClient(840, 760, 840, 480, 840, 520));
        Assert.IsFalse(PopupPlacement.ShouldResizeClient(840, 520, 840, 520, null, null));
    }

    [TestMethod]
    public void ClientResize_ForceReappliesTheMeasuredSizeAfterWindowIsShown()
    {
        Assert.IsTrue(PopupPlacement.ShouldResizeClient(840, 520, 840, 520, 840, 520, force: true));
    }

    [TestMethod]
    public void PageHeightAnimation_PerformsLayoutOnlyThroughPosition()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Views", "MainWindow.xaml.cs"));
        var animationStart = source.IndexOf(
            "private async Task<bool> AnimatePageHeightAsync(",
            StringComparison.Ordinal);
        var animationEnd = source.IndexOf(
            "private void CompletePageSwitch(",
            animationStart,
            StringComparison.Ordinal);
        var positionStart = source.IndexOf("private void Position(", StringComparison.Ordinal);
        var positionEnd = source.IndexOf(
            "private double MeasureVisibleContentHeight(",
            positionStart,
            StringComparison.Ordinal);

        Assert.IsTrue(animationStart >= 0);
        Assert.IsTrue(animationEnd > animationStart);
        Assert.IsTrue(positionStart >= 0);
        Assert.IsTrue(positionEnd > positionStart);

        var animation = source[animationStart..animationEnd];
        var position = source[positionStart..positionEnd];
        Assert.AreEqual(
            0,
            animation.Split("ContentRoot.UpdateLayout();", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(
            2,
            animation.Split("Position(forceResize: true);", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(
            1,
            position.Split("ContentRoot.UpdateLayout();", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(animation, "PageResizeDuration");
        StringAssert.Contains(animation, "PopupPlacement.InterpolateContentHeight");
        StringAssert.Contains(animation, "Task.Delay(TimeSpan.FromMilliseconds(16))");
    }

    [TestMethod]
    public void TokenHeatmap_ReusesAccessibilitySettingsAndReadsCurrentHighContrastValue()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Views", "TokenUsageView.xaml.cs"));

        Assert.AreEqual(
            1,
            source.Split(
                "private readonly AccessibilitySettings accessibilitySettings = new();",
                StringSplitOptions.None).Length - 1);
        StringAssert.Contains(source, "if (accessibilitySettings.HighContrast)");
        Assert.IsFalse(source.Contains("new AccessibilitySettings().HighContrast", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("bool highContrast", StringComparison.Ordinal));
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

    [DataRow(true, true, false, BackdropKind.Mica)]
    [DataRow(false, true, false, BackdropKind.Opaque)]
    [DataRow(true, false, false, BackdropKind.Opaque)]
    [DataRow(true, true, true, BackdropKind.Opaque)]
    [TestMethod]
    public void SettingsBackdropPolicy_UsesMicaBaseOrOpaqueFallback(
        bool mica,
        bool transparency,
        bool highContrast,
        BackdropKind expected)
    {
        Assert.AreEqual(expected, BackdropPolicy.SelectForSettings(mica, transparency, highContrast));
    }
}
