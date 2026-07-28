using System.Drawing;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowPlacementService
{
    internal void ResizeAndPlaceInitial(
        AppWindow appWindow,
        double rasterizationScale,
        double measuredContentHeightDips,
        Rectangle? trayRectangle)
    {
        var anchor = trayRectangle ?? CursorAnchor();
        var workArea = GetWorkArea(anchor);
        var (size, margin) = Resize(appWindow, rasterizationScale, measuredContentHeightDips, workArea);

        var location = trayRectangle is { } tray
            ? PopupPlacement.PlaceNearTray(tray, workArea, size, margin)
            : PopupPlacement.PlaceAtBottomRight(workArea, size, margin);
        appWindow.Move(new PointInt32(location.X, location.Y));
    }

    internal void ResizeAndKeepPosition(
        AppWindow appWindow,
        double rasterizationScale,
        double measuredContentHeightDips)
    {
        var current = appWindow.Position;
        var anchor = new Rectangle(current.X, current.Y, Math.Max(1, appWindow.Size.Width), Math.Max(1, appWindow.Size.Height));
        var workArea = GetWorkArea(anchor);
        var (size, margin) = Resize(appWindow, rasterizationScale, measuredContentHeightDips, workArea);
        var location = PopupPlacement.ClampToWorkArea(
            new Point(current.X, current.Y),
            workArea,
            size,
            margin);
        appWindow.Move(new PointInt32(location.X, location.Y));
    }

    internal void ClampCurrentPosition(AppWindow appWindow, double rasterizationScale)
    {
        var current = appWindow.Position;
        var size = new Size(Math.Max(1, appWindow.Size.Width), Math.Max(1, appWindow.Size.Height));
        var workArea = GetWorkArea(new Rectangle(current.X, current.Y, size.Width, size.Height));
        var margin = PopupPlacement.DipsToPixels(8, rasterizationScale);
        var location = PopupPlacement.ClampToWorkArea(
            new Point(current.X, current.Y),
            workArea,
            size,
            margin);
        if (location.X != current.X || location.Y != current.Y)
        {
            appWindow.Move(new PointInt32(location.X, location.Y));
        }
    }

    private static (Size Size, int Margin) Resize(
        AppWindow appWindow,
        double rasterizationScale,
        double measuredContentHeightDips,
        Rectangle workArea)
    {
        var scale = Math.Max(1.0, rasterizationScale);
        var width = PopupPlacement.DipsToPixels(420, scale);
        var margin = PopupPlacement.DipsToPixels(8, scale);
        var height = PopupPlacement.ContentHeightPixels(
            measuredContentHeightDips,
            scale,
            workArea.Height,
            8);
        var requestedSize = new SizeInt32(width, height);
        if (appWindow.ClientSize.Width != requestedSize.Width || appWindow.ClientSize.Height != requestedSize.Height)
        {
            appWindow.ResizeClient(requestedSize);
        }

        return (
            new Size(Math.Max(1, appWindow.Size.Width), Math.Max(1, appWindow.Size.Height)),
            margin);
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
