using System.Drawing;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace CodexQuotaTray.App.Services;

internal sealed class WindowPlacementService
{
    private SizeInt32? lastRequestedClientSize;

    internal WindowPlacementResult ResizeAndPlaceInitial(
        AppWindow appWindow,
        double rasterizationScale,
        double measuredContentHeightDips,
        Rectangle? trayRectangle)
    {
        var anchor = trayRectangle ?? CursorAnchor();
        var workArea = GetWorkArea(anchor);
        var (size, result, margin) = Resize(
            appWindow,
            rasterizationScale,
            measuredContentHeightDips,
            workArea);

        var location = PopupPlacement.PlaceAtBottomRight(workArea, size, margin);
        appWindow.Move(new PointInt32(location.X, location.Y));
        return result;
    }

    internal WindowPlacementResult ResizeAndKeepPosition(
        AppWindow appWindow,
        double rasterizationScale,
        double measuredContentHeightDips,
        bool forceResize = false)
    {
        var current = appWindow.Position;
        var anchor = new Rectangle(current.X, current.Y, Math.Max(1, appWindow.Size.Width), Math.Max(1, appWindow.Size.Height));
        var workArea = GetWorkArea(anchor);
        var (size, result, margin) = Resize(
            appWindow,
            rasterizationScale,
            measuredContentHeightDips,
            workArea,
            forceResize);
        var location = PopupPlacement.ClampToWorkArea(
            new Point(current.X, current.Y),
            workArea,
            size,
            margin);
        appWindow.Move(new PointInt32(location.X, location.Y));
        return result;
    }

    private (Size WindowSize, WindowPlacementResult Result, int Margin) Resize(
        AppWindow appWindow,
        double rasterizationScale,
        double measuredContentHeightDips,
        Rectangle workArea,
        bool forceResize = false)
    {
        var scale = Math.Max(1.0, rasterizationScale);
        var width = PopupPlacement.DipsToPixels(420, scale);
        var margin = PopupPlacement.DipsToPixels(PopupPlacement.DefaultMarginDips, scale);
        var contentSizing = PopupPlacement.ResolveContentSizing(
            measuredContentHeightDips,
            scale,
            workArea.Height,
            PopupPlacement.DefaultMarginDips);
        var requestedSize = new SizeInt32(width, contentSizing.TargetClientHeightPixels);
        var currentWindowSize = appWindow.Size;
        var currentClientSize = appWindow.ClientSize;
        var nonClientWidth = Math.Max(0, currentWindowSize.Width - currentClientSize.Width);
        var nonClientHeight = Math.Max(0, currentWindowSize.Height - currentClientSize.Height);
        var windowSize = new Size(
            requestedSize.Width + nonClientWidth,
            requestedSize.Height + nonClientHeight);
        var currentMatchesRequest = currentClientSize.Width == requestedSize.Width
            && currentClientSize.Height == requestedSize.Height;
        var lastMatchesRequest = lastRequestedClientSize is { } last
            && last.Width == requestedSize.Width
            && last.Height == requestedSize.Height;
        if (PopupPlacement.ShouldResizeClient(
            currentClientSize.Width,
            currentClientSize.Height,
            requestedSize.Width,
            requestedSize.Height,
            lastRequestedClientSize?.Width,
            lastRequestedClientSize?.Height,
            force: forceResize && (!currentMatchesRequest || !lastMatchesRequest)))
        {
            lastRequestedClientSize = requestedSize;
            // With the WinUI custom title bar, ResizeClient does not preserve
            // the requested effective client height on this runtime. Resize
            // the full frame using only the current non-client delta; the
            // content height has already been resolved by the view boundary.
            appWindow.Resize(new SizeInt32(
                windowSize.Width,
                windowSize.Height));
            if (appWindow.ClientSize.Width != requestedSize.Width || appWindow.ClientSize.Height != requestedSize.Height)
            {
                lastRequestedClientSize = null;
            }
        }

        return (
            windowSize,
            new WindowPlacementResult(requestedSize, contentSizing),
            margin);
    }

    private static Rectangle CursorAnchor()
    {
        _ = NativeMethods.GetCursorPos(out var point);
        return new Rectangle(point.X, point.Y, 1, 1);
    }

    internal static double GetRasterizationScale(IntPtr hwnd) =>
        Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0);

    internal static Rectangle GetWorkArea(Rectangle anchor)
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

internal readonly record struct WindowPlacementResult(
    SizeInt32 RequestedClientSize,
    PopupPlacement.ContentSizing ContentSizing);
