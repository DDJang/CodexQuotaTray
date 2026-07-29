using System.Runtime.InteropServices;

namespace CodexQuotaTray.App.Interop;

internal static class NativeMethods
{
    internal const uint WmApp = 0x8000;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmNull = 0x0000;
    internal const uint TrayCallbackMessage = WmApp + 41;
    internal const uint NimAdd = 0x00000000;
    internal const uint NimDelete = 0x00000002;
    internal const uint NimModify = 0x00000001;
    internal const uint NimSetVersion = 0x00000004;
    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const uint NifGuid = 0x00000020;
    internal const uint NifShowTip = 0x00000080;
    internal const uint NifInfo = 0x00000010;
    internal const uint NiifInfo = 0x00000001;
    internal const uint NinBalloonUserClick = 0x0405;
    internal const uint WmPowerBroadcast = 0x0218;
    internal const uint PbtApmResumeAutomatic = 0x0012;
    internal const uint NotifyIconVersion4 = 4;
    internal const uint MfString = 0x00000000;
    internal const uint MfChecked = 0x00000008;
    internal const uint MfPopup = 0x00000010;
    internal const uint TpmRightButton = 0x0002;
    internal const uint TpmReturnCommand = 0x0100;
    internal const uint TpmNonotify = 0x0080;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsPopup = 0x80000000;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmWindowCornerPreferenceRound = 2;
    internal static readonly IntPtr HwndMessage = new(-3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal WindowProcedure WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal IntPtr Instance;
        internal IntPtr Icon;
        internal IntPtr Cursor;
        internal IntPtr Background;
        internal string? MenuName;
        internal string ClassName;
        internal IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal uint Flags;
        internal uint CallbackMessage;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tip;
        internal uint State;
        internal uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid GuidItem;
        internal IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NotifyIconIdentifier
    {
        internal uint Size;
        internal IntPtr Window;
        internal uint Id;
        internal Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WindowClassEx value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string name);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    internal static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect iconLocation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        IntPtr[]? largeIcons,
        IntPtr[]? smallIcons,
        uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern int TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr hwnd,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll", SetLastError = false)]
    internal static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);
}
