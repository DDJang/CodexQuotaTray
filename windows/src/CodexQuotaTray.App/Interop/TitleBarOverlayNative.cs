using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.App.Interop;

internal static class TitleBarOverlayNative
{
    internal delegate IntPtr SubclassProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProcedure procedure, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProcedure procedure, UIntPtr id);

    [DllImport("comctl32.dll")]
    internal static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("user32.dll")]
    internal static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    internal static extern uint GetSysColor(int index);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RoundRect(IntPtr dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    /// <summary>Keep native hover/layout behavior, but paint a rounded, buffered surface.</summary>
    internal sealed class TooltipSurface : IDisposable
    {
        private readonly IntPtr window;
        private readonly SubclassProcedure procedure;
        private static readonly UIntPtr SubclassId = new(1);
        private (int Width, int Height, uint Dpi)? regionSize;
        private string text = string.Empty;
        private uint dpi = 96;
        private IntPtr font;
        private bool disposed;

        internal TooltipSurface(IntPtr window)
        {
            this.window = window;
            procedure = WindowProc;
            if (!SetWindowSubclass(window, procedure, SubclassId, UIntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception("Cannot attach tooltip surface.");
            }
            // The custom buffered border replaces the native rectangular frame.
            var style = NativeMethods.GetWindowLongPtr(window, NativeMethods.GwlStyle).ToInt64();
            _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GwlStyle, new IntPtr(style & ~0x00C00000L));
            _ = NativeMethods.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | 0x0023);
        }

        internal void Update(string value, uint newDpi, IntPtr newFont)
        {
            if (disposed || (text == value && dpi == newDpi && font == newFont)) { return; }
            text = value;
            dpi = newDpi;
            font = newFont;
            UpdateRegion();
            _ = InvalidateRect(window, IntPtr.Zero, false);
        }

        private int Pixels(int dips) => (int)Math.Ceiling(dips * dpi / 96d);

        private void UpdateRegion()
        {
            if (!NativeMethods.GetWindowRect(window, out var rect)) { return; }
            var size = (Width: rect.Right - rect.Left, Height: rect.Bottom - rect.Top, Dpi: dpi);
            if (size.Width <= 0 || size.Height <= 0 || regionSize == size) { return; }
            var region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, Pixels(16), Pixels(16));
            if (region == IntPtr.Zero) { return; }
            // SetWindowRgn sends position messages synchronously; cache before calling.
            regionSize = size;
            if (SetWindowRgn(window, region, true) == 0)
            {
                regionSize = null;
                _ = DeleteObject(region);
            }
            // On success Windows owns and releases the region.
        }

        private IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (message == 0x0082)
            {
                Dispose();
                return DefSubclassProc(hwnd, message, wParam, lParam);
            }
            if (font != IntPtr.Zero && text.Length > 0)
            {
                if (message is 0x0014 or 0x0085) { return new IntPtr(1); }
                if (message == 0x000F) { Paint(); return IntPtr.Zero; }
            }
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            if (message == 0x0047) { UpdateRegion(); }
            return result;
        }

        private void Paint()
        {
            var destination = BeginPaint(window, out var paint);
            var dc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            var brush = IntPtr.Zero;
            var pen = IntPtr.Zero;
            var previousBitmap = IntPtr.Zero;
            var previousBrush = IntPtr.Zero;
            var previousPen = IntPtr.Zero;
            var previousFont = IntPtr.Zero;
            try
            {
                if (destination == IntPtr.Zero || !NativeMethods.GetClientRect(window, out var rect) || rect.Right <= 0 || rect.Bottom <= 0) { return; }
                dc = CreateCompatibleDC(destination);
                bitmap = CreateCompatibleBitmap(destination, rect.Right, rect.Bottom);
                brush = CreateSolidBrush(GetSysColor(24)); // COLOR_INFOBK
                pen = CreatePen(0, Math.Max(1, Pixels(1)), GetSysColor(16)); // COLOR_BTNSHADOW
                if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || brush == IntPtr.Zero || pen == IntPtr.Zero) { return; }
                previousBitmap = SelectObject(dc, bitmap);
                previousBrush = SelectObject(dc, brush);
                previousPen = SelectObject(dc, pen);
                previousFont = SelectObject(dc, font);
                _ = FillRect(dc, ref rect, brush);
                _ = RoundRect(dc, 0, 0, rect.Right, rect.Bottom, Pixels(16), Pixels(16));
                rect.Left = Pixels(8);
                rect.Top = Pixels(6);
                rect.Right -= Pixels(8);
                rect.Bottom -= Pixels(6);
                _ = SetBkMode(dc, 1);
                _ = SetTextColor(dc, GetSysColor(23)); // COLOR_INFOTEXT
                _ = DrawText(dc, text, text.Length, ref rect, 0x0810); // WORDBREAK | NOPREFIX
                _ = NativeMethods.GetClientRect(window, out rect);
                _ = BitBlt(destination, 0, 0, rect.Right, rect.Bottom, dc, 0, 0, 0x00CC0020);
            }
            finally
            {
                if (previousFont != IntPtr.Zero) { _ = SelectObject(dc, previousFont); }
                if (previousPen != IntPtr.Zero) { _ = SelectObject(dc, previousPen); }
                if (previousBrush != IntPtr.Zero) { _ = SelectObject(dc, previousBrush); }
                if (previousBitmap != IntPtr.Zero) { _ = SelectObject(dc, previousBitmap); }
                foreach (var resource in new[] { pen, brush, bitmap }) { if (resource != IntPtr.Zero) { _ = DeleteObject(resource); } }
                if (dc != IntPtr.Zero) { _ = DeleteDC(dc); }
                _ = EndPaint(window, ref paint);
            }
        }

        public void Dispose()
        {
            if (disposed) { return; }
            disposed = true;
            if (IsWindow(window)) { _ = RemoveWindowSubclass(window, procedure, SubclassId); }
        }
    }

    internal const uint TimerMessage = 0x0113;
    internal const uint TooltipAddTool = 0x0432;
    internal const uint TooltipActivate = 0x0401;
    internal const uint NotifyMessage = 0x004E;
    internal const uint TooltipShowNotification = unchecked((uint)-521);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotificationHeader
    {
        internal IntPtr Window;
        internal UIntPtr Id;
        internal uint Code;
    }
    internal const uint TooltipUpdateText = 0x0439;
    internal const uint TooltipPop = 0x041C;
    internal const uint TooltipMaxWidth = 0x0418;
    internal const uint TooltipSetMargin = 0x041A;
    internal const uint SetFont = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CommonControls
    {
        internal uint Size;
        internal uint Classes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TooltipInfo
    {
        // TOOLINFO V2 omits lpReserved, so both legacy and v6 common controls accept cbSize.
        internal uint Size;
        internal uint Flags;
        internal IntPtr Window;
        internal UIntPtr Id;
        internal NativeMethods.NativeRect Rect;
        internal IntPtr Instance;
        internal IntPtr Text;
        internal IntPtr Parameter;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitCommonControlsEx(ref CommonControls controls);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern IntPtr SendTooltipMessage(IntPtr hwnd, uint message, UIntPtr wParam, ref TooltipInfo info);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern IntPtr SendTooltipMargins(IntPtr hwnd, uint message, UIntPtr wParam, ref NativeMethods.NativeRect margins);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint milliseconds, IntPtr callback);

    internal static UIntPtr SetCountdownTimer(IntPtr hwnd, UIntPtr id, DateTimeOffset dueAt, DateTimeOffset now) =>
        SetTimer(hwnd, id, TitleBarQuotaOverlay.CountdownTimerDelayMilliseconds(dueAt, now), IntPtr.Zero);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ClrUsed;
        internal uint ClrImportant;
        internal uint Colors;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BlendFunction
    {
        internal byte Operation;
        internal byte Flags;
        internal byte ConstantAlpha;
        internal byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, ref NativeMethods.NativePoint position,
        ref NativeSize size, IntPtr sourceDc, ref NativeMethods.NativePoint sourcePoint, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr pixels, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GdiFlush();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    internal static void AttachOwner(IntPtr overlay, IntPtr owner)
    {
        if (GetWindow(overlay, 4) == owner)
        {
            return;
        }
        _ = NativeMethods.SetWindowLongPtr(overlay, NativeMethods.GwlHwndParent, owner);
        if (GetWindow(overlay, 4) != owner)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static (IntPtr After, uint Flags) OwnerPlacementZOrder(IntPtr overlay, IntPtr owner)
    {
        var aboveOwner = GetWindow(owner, 3);
        if (aboveOwner == overlay)
        {
            return (IntPtr.Zero, NativeMethods.SwpNoZOrder);
        }
        // Inserting after a topmost HWND can promote the popup. HWND_TOP instead
        // keeps it at the top of the normal band when its owner is already there.
        var isTopmost = aboveOwner != IntPtr.Zero
            && (NativeMethods.GetWindowLongPtr(aboveOwner, NativeMethods.GwlExStyle).ToInt64() & 8) != 0;
        return (isTopmost ? IntPtr.Zero : aboveOwner, 0);
    }

    internal const uint Foreground = 0x0003;
    internal const uint MinimizeStart = 0x0016;
    internal const uint MinimizeEnd = 0x0017;
    internal const uint Destroy = 0x8001;
    internal const uint Show = 0x8002;
    internal const uint Hide = 0x8003;
    internal const uint LocationChange = 0x800B;
    internal const uint Cloaked = 0x8017;
    internal const uint Uncloaked = 0x8018;
    internal const uint WsExLayered = 0x00080000;

    internal delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);

    internal delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PaintStruct
    {
        internal IntPtr Dc;
        internal int Erase;
        internal NativeMethods.NativeRect Paint;
        internal int Restore;
        internal int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        internal byte[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        internal int Width;
        internal int Height;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder family);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint length);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int GetDwmRect(IntPtr hwnd, int attribute, out NativeMethods.NativeRect rect, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    internal static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    internal static extern int FillRect(IntPtr dc, ref NativeMethods.NativeRect rect, IntPtr brush);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawText(IntPtr dc, string text, int length, ref NativeMethods.NativeRect rect, uint format);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTextExtentPoint32(IntPtr dc, string text, int count, out NativeSize size);

    [DllImport("gdi32.dll")]
    internal static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    internal static extern uint SetTextColor(IntPtr dc, uint color);

}
