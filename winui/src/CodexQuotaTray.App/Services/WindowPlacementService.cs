using System.Drawing;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowPlacementService
{
    internal void ResizeAndPlace(
        AppWindow appWindow,
        double rasterizationScale,
        int windowCount,
        Rectangle? trayRectangle)
    {
        var scale = Math.Max(1.0, rasterizationScale);
        var width = PopupPlacement.DipsToPixels(420, scale);
        var heightDips = PopupPlacement.ContentHeightDips(windowCount);
        var height = PopupPlacement.DipsToPixels(heightDips, scale);
        appWindow.ResizeClient(new SizeInt32(width, height));

        var anchor = trayRectangle ?? CursorAnchor();
        var workArea = GetWorkArea(anchor);
        var margin = PopupPlacement.DipsToPixels(8, scale);
        var location = trayRectangle is { } tray
            ? PopupPlacement.PlaceNearTray(tray, workArea, new Size(width, height), margin)
            : PopupPlacement.PlaceAtBottomRight(workArea, new Size(width, height), margin);
        appWindow.Move(new PointInt32(location.X, location.Y));
    }

    private static Rectangle CursorAnchor()
    {
        _ = NativeMethods.GetCursorPos(out var point);
        return new Rectangle(point.X, point.Y, 1, 1);
    }

    private static Rectangle GetWorkArea(Rectangle anchor)
    {
        var point = new NativeMethods.NativePoint
        {
            X = anchor.Left + (anchor.Width / 2),
            Y = anchor.Top + (anchor.Height / 2),
        };
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        return monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info)
            ? Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom)
            : Rectangle.FromLTRB(0, 0, 1920, 1080);
    }
}
