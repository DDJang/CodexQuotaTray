using System.Drawing;

namespace CodexQuotaTray.Core.Presentation;

public static class PopupPlacement
{
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
            Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - popup.Width)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - popup.Height)));
    }

    public static Point PlaceAtBottomRight(Rectangle workArea, Size popup, int margin) =>
        new(
            Math.Max(workArea.Left, workArea.Right - popup.Width - margin),
            Math.Max(workArea.Top, workArea.Bottom - popup.Height - margin));

    public static Point ClampToWorkArea(Point location, Rectangle workArea, Size popup, int margin)
    {
        var left = workArea.Left + margin;
        var top = workArea.Top + margin;
        var right = Math.Max(left, workArea.Right - popup.Width - margin);
        var bottom = Math.Max(top, workArea.Bottom - popup.Height - margin);
        return new Point(
            Math.Clamp(location.X, left, right),
            Math.Clamp(location.Y, top, bottom));
    }

    public static int DipsToPixels(double dips, double scale) =>
        checked((int)Math.Round(dips * Math.Max(1.0, scale), MidpointRounding.AwayFromZero));

    public static int ContentHeightPixels(
        double measuredHeightDips,
        double scale,
        int workAreaHeightPixels,
        double marginDips = 8)
    {
        var desired = DipsToPixels(Math.Max(1, measuredHeightDips), scale);
        var margin = DipsToPixels(marginDips, scale);
        return Math.Clamp(desired, 1, Math.Max(1, workAreaHeightPixels - (margin * 2)));
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
