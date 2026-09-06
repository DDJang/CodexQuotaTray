using System.Drawing;

namespace CodexQuotaTray.Core.Presentation;

public static class PopupPlacement
{
    public const double DefaultMarginDips = 12;
    public const double DefaultScrollToleranceDips = 1;

    public readonly record struct ContentSizing(
        double NaturalContentHeightDips,
        double MaxAvailableClientHeightDips,
        double TargetClientHeightDips,
        int MaxAvailableClientHeightPixels,
        int TargetClientHeightPixels,
        bool NeedsVerticalScroll);

    public static Point PlaceNearTray(Rectangle tray, Rectangle workArea, Size popup, int margin)
    {
        var edge = NearestEdge(tray, workArea);
        var centeredX = tray.Left + ((tray.Width - popup.Width) / 2);
        var centeredY = tray.Top + ((tray.Height - popup.Height) / 2);
        var x = edge switch
        {
            Models.TrayEdge.Left => workArea.Left + margin,
            Models.TrayEdge.Right => workArea.Right - popup.Width - margin,
            _ => centeredX,
        };
        var y = edge switch
        {
            Models.TrayEdge.Top => workArea.Top + margin,
            Models.TrayEdge.Bottom => workArea.Bottom - popup.Height - margin,
            _ => centeredY,
        };

        return new Point(
            ClampOrigin(x, workArea.Left, workArea.Right, popup.Width, margin),
            ClampOrigin(y, workArea.Top, workArea.Bottom, popup.Height, margin));
    }

    public static Point PlaceAtBottomRight(Rectangle workArea, Size popup, int margin) =>
        new(
            ClampOrigin(workArea.Right - popup.Width - margin, workArea.Left, workArea.Right, popup.Width, margin),
            ClampOrigin(workArea.Bottom - popup.Height - margin, workArea.Top, workArea.Bottom, popup.Height, margin));

    public static Point ClampToWorkArea(Point location, Rectangle workArea, Size popup, int margin)
    {
        return new Point(
            ClampOrigin(location.X, workArea.Left, workArea.Right, popup.Width, margin),
            ClampOrigin(location.Y, workArea.Top, workArea.Bottom, popup.Height, margin));
    }

    public static int DipsToPixels(double dips, double scale) =>
        checked((int)Math.Round(dips * Math.Max(1.0, scale), MidpointRounding.AwayFromZero));

    public static int ContentHeightPixels(
        double measuredHeightDips,
        double scale,
        int workAreaHeightPixels,
        double marginDips = DefaultMarginDips)
        => ResolveContentSizing(
            measuredHeightDips,
            scale,
            workAreaHeightPixels,
            marginDips).TargetClientHeightPixels;

    public static ContentSizing ResolveContentSizing(
        double naturalContentHeightDips,
        double scale,
        int workAreaHeightPixels,
        double marginDips = DefaultMarginDips,
        double scrollToleranceDips = DefaultScrollToleranceDips)
    {
        var safeScale = double.IsFinite(scale) && scale >= 1 ? scale : 1;
        var safeNaturalHeight = double.IsFinite(naturalContentHeightDips)
            ? Math.Max(1, naturalContentHeightDips)
            : 1;
        var safeMarginDips = double.IsFinite(marginDips) ? Math.Max(0, marginDips) : 0;
        var safeToleranceDips = double.IsFinite(scrollToleranceDips)
            ? Math.Max(0, scrollToleranceDips)
            : DefaultScrollToleranceDips;
        var marginPixels = DipsToPixels(safeMarginDips, safeScale);
        var maxAvailableClientHeightPixels = Math.Max(
            1,
            workAreaHeightPixels - (marginPixels * 2));
        var maxAvailableClientHeightDips = maxAvailableClientHeightPixels / safeScale;
        var needsVerticalScroll = safeNaturalHeight
            > maxAvailableClientHeightDips + safeToleranceDips;
        var targetClientHeightDips = Math.Min(
            safeNaturalHeight,
            maxAvailableClientHeightDips);
        var targetClientHeightPixels = Math.Clamp(
            DipsToPixels(targetClientHeightDips, safeScale),
            1,
            maxAvailableClientHeightPixels);

        return new ContentSizing(
            safeNaturalHeight,
            maxAvailableClientHeightDips,
            targetClientHeightDips,
            maxAvailableClientHeightPixels,
            targetClientHeightPixels,
            needsVerticalScroll);
    }

    public static double NaturalContentHeight(
        double lastVisibleTop,
        double lastVisibleHeight,
        double bottomPadding,
        double fallbackHeight)
    {
        var visibleBottom = lastVisibleTop + lastVisibleHeight + Math.Max(0, bottomPadding);
        return double.IsFinite(visibleBottom) && visibleBottom > 0
            ? Math.Ceiling(visibleBottom)
            : Math.Max(1, Math.Ceiling(fallbackHeight));
    }

    public static double InterpolateContentHeight(double startHeight, double targetHeight, double progress)
    {
        var clampedProgress = Math.Clamp(progress, 0, 1);
        var easedProgress = 1 - Math.Pow(1 - clampedProgress, 3);
        return startHeight + ((targetHeight - startHeight) * easedProgress);
    }

    public static bool ShouldResizeClient(
        int currentWidth,
        int currentHeight,
        int requestedWidth,
        int requestedHeight,
        int? lastRequestedWidth,
        int? lastRequestedHeight,
        bool force = false)
    {
        if (force)
        {
            return true;
        }

        if (currentWidth == requestedWidth && currentHeight == requestedHeight)
        {
            return false;
        }

        return lastRequestedWidth != requestedWidth || lastRequestedHeight != requestedHeight;
    }

    public static Models.TrayEdge NearestEdge(Rectangle tray, Rectangle workArea)
    {
        var centerX = tray.Left + (tray.Width / 2);
        var centerY = tray.Top + (tray.Height / 2);
        var candidates = new (Models.TrayEdge Edge, int Distance)[]
        {
            (Models.TrayEdge.Left, Math.Abs(centerX - workArea.Left)),
            (Models.TrayEdge.Top, Math.Abs(centerY - workArea.Top)),
            (Models.TrayEdge.Right, Math.Abs(workArea.Right - centerX)),
            (Models.TrayEdge.Bottom, Math.Abs(workArea.Bottom - centerY)),
        };
        return candidates.MinBy(candidate => candidate.Distance).Edge;
    }

    private static int ClampOrigin(
        int origin,
        int workStart,
        int workEnd,
        int popupLength,
        int margin)
    {
        var safeMargin = Math.Max(0, margin);
        var min = workStart + safeMargin;
        var max = workEnd - popupLength - safeMargin;
        if (max < min)
        {
            min = workStart;
            max = Math.Max(workStart, workEnd - popupLength);
        }

        return Math.Clamp(origin, min, max);
    }
}

public static class BackdropPolicy
{
    public static Models.BackdropKind Select(
        bool acrylicSupported,
        bool micaSupported,
        bool transparencyEnabled,
        bool highContrast)
    {
        if (highContrast || !transparencyEnabled)
        {
            return Models.BackdropKind.Opaque;
        }

        if (acrylicSupported)
        {
            return Models.BackdropKind.DesktopAcrylic;
        }

        return micaSupported ? Models.BackdropKind.Mica : Models.BackdropKind.Opaque;
    }

    public static Models.BackdropKind SelectForSettings(
        bool micaSupported,
        bool transparencyEnabled,
        bool highContrast)
    {
        if (highContrast || !transparencyEnabled)
        {
            return Models.BackdropKind.Opaque;
        }

        return micaSupported ? Models.BackdropKind.Mica : Models.BackdropKind.Opaque;
    }
}

public sealed class WindowVisibilityController
{
    public bool DesiredVisible { get; private set; }

    public bool Toggle()
    {
        DesiredVisible = !DesiredVisible;
        return DesiredVisible;
    }

    public void Show() => DesiredVisible = true;

    public void Hide() => DesiredVisible = false;
}
