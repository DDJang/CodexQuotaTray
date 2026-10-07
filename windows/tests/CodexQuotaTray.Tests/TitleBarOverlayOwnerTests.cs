using System.Runtime.InteropServices;
using CodexQuotaTray.App.Interop;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TitleBarOverlayOwnerTests
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [TestMethod]
    public void HiddenOwnedOverlayIsAboveHostAndCanReattachAfterOwnerDestruction()
    {
        // These anonymous HWNDs are never shown, activated, or connected to a real
        // app. Exercise the Win32 ownership contract without a desktop GUI smoke.
        var host = CreateHiddenWindow();
        var coveringWindow = CreateHiddenWindow();
        var overlay = CreateHiddenWindow();
        var replacement = IntPtr.Zero;
        try
        {
            TitleBarOverlayNative.AttachOwner(overlay, host);
            var zOrder = TitleBarOverlayNative.OwnerPlacementZOrder(overlay, host);
            Assert.IsTrue(NativeMethods.SetWindowPos(overlay, zOrder.After, 0, 0, 10, 10,
                NativeMethods.SwpNoActivate | 0x0200 | zOrder.Flags));
            Assert.AreEqual(host, TitleBarOverlayNative.GetWindow(overlay, 4));
            var order = new List<IntPtr>();
            EnumWindowCallback callback = (window, _) =>
            {
                if (window == host || window == overlay || window == coveringWindow) { order.Add(window); }
                return true;
            };
            Assert.IsTrue(EnumWindows(callback, IntPtr.Zero));
            CollectionAssert.AreEqual(new[] { coveringWindow, overlay, host }, order.ToArray());
            Assert.IsFalse(TitleBarOverlayNative.IsWindowVisible(overlay));
            Assert.AreEqual(0L, NativeMethods.GetWindowLongPtr(overlay, NativeMethods.GwlExStyle).ToInt64() & 8);

            Assert.IsTrue(NativeMethods.DestroyWindow(host));
            Assert.IsFalse(TitleBarOverlayNative.IsWindow(overlay));
            Assert.IsTrue(TitleBarOverlayNative.IsWindow(coveringWindow));
            host = CreateHiddenWindow();
            replacement = CreateHiddenWindow();
            TitleBarOverlayNative.AttachOwner(replacement, host);
            Assert.AreEqual(host, TitleBarOverlayNative.GetWindow(replacement, 4));
        }
        finally
        {
            // Never destroy a potentially reused HWND after owner destruction.
            if (replacement != IntPtr.Zero) { _ = NativeMethods.DestroyWindow(replacement); }
            if (TitleBarOverlayNative.IsWindow(overlay) && TitleBarOverlayNative.GetWindow(overlay, 4) == host)
            {
                _ = NativeMethods.DestroyWindow(overlay);
            }
            _ = NativeMethods.DestroyWindow(host);
            _ = NativeMethods.DestroyWindow(coveringWindow);
        }
    }

    private static IntPtr CreateHiddenWindow()
    {
        var hwnd = NativeMethods.CreateWindowEx(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate,
            "STATIC", "anonymous quota overlay test", NativeMethods.WsPopup,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        Assert.AreNotEqual(IntPtr.Zero, hwnd);
        return hwnd;
    }
}
