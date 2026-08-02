using System.Drawing;

namespace CodexQuotaTray.Core.Presentation;

public static class PopupPlacement
{
    public const double DefaultMarginDips = 12;
    // Compensates for the non-client/client viewport delta so the footer's
    // bottom gap matches the measured top inset at the target DPI.
    public const int DefaultBottomTrimPixels = 50;

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
        double marginDips = DefaultMarginDips,
        int bottomTrimPixels = 0)
    {
        var desired = DipsToPixels(Math.Max(1, measuredHeightDips), scale);
        var trimmed = Math.Max(1, desired - Math.Max(0, bottomTrimPixels));
        var margin = DipsToPixels(marginDips, scale);
        return Math.Clamp(trimmed, 1, Math.Max(1, workAreaHeightPixels - (margin * 2)));
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
