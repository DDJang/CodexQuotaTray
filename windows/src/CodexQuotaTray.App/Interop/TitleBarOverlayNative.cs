using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaTray.App.Interop;

internal static class TitleBarOverlayNative
{
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
    internal const uint TransparentColor = 0x00010101;

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
    internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

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
    internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

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

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
}
