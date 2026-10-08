using System.Runtime.InteropServices;
using System.Diagnostics;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.Tests;

[TestClass]
public sealed class TitleBarOverlayOwnerTests
{
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimerMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativeMethods.NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out TimerMessage message, IntPtr hwnd, uint minimum, uint maximum, uint remove);

    [TestMethod]
    public void HiddenCountdownTimerReusesItsIdAndFiresAtAnEarlierReset()
    {
        var hwnd = CreateHiddenWindow();
        var timerId = new UIntPtr(1);
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var window = QuotaWindowView.Demo("匿名窗口", 13, "未知", "未知") with { ResetAtUtc = now.AddMinutes(2) };
        try
        {
            var due = TitleBarQuotaOverlay.NextCountdownUpdateAt([window], now)!.Value;
            Assert.AreNotEqual(UIntPtr.Zero, TitleBarOverlayNative.SetCountdownTimer(hwnd, timerId, due, now));
            window = window with { ResetAtUtc = now.AddMilliseconds(200) };
            due = TitleBarQuotaOverlay.NextCountdownUpdateAt([window], now)!.Value;
            Assert.AreEqual(200u, TitleBarQuotaOverlay.CountdownTimerDelayMilliseconds(due, now));
            Assert.AreNotEqual(UIntPtr.Zero, TitleBarOverlayNative.SetCountdownTimer(hwnd, timerId, due, now));
            var elapsed = Stopwatch.StartNew();
            var received = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (PeekMessage(out var message, hwnd, TitleBarOverlayNative.TimerMessage, TitleBarOverlayNative.TimerMessage, 1))
                {
                    Assert.AreEqual(hwnd, message.Window);
                    Assert.AreEqual(timerId, message.WParam);
                    received = true;
                    break;
                }
                Thread.Sleep(5);
            }
            Assert.IsTrue(received, "The rearmed 200 ms reset timer must fire without waiting for the original minute timer.");
            Assert.IsFalse(TitleBarOverlayNative.IsWindowVisible(hwnd));
        }
        finally
        {
            _ = TitleBarOverlayNative.KillTimer(hwnd, timerId);
            _ = NativeMethods.DestroyWindow(hwnd);
        }
    }

    [TestMethod]
    public void HiddenTooltipAcceptsUnicodeUpdatesAndIsDestroyedWithItsOwner()
    {
        var controls = new TitleBarOverlayNative.CommonControls
        {
            Size = (uint)Marshal.SizeOf<TitleBarOverlayNative.CommonControls>(),
            Classes = 0xFF,
        };
        Assert.IsTrue(TitleBarOverlayNative.InitCommonControlsEx(ref controls));
        var overlay = CreateHiddenWindow();
        var tooltip = NativeMethods.CreateWindowEx(NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow,
            "tooltips_class32", string.Empty, NativeMethods.WsPopup | 3, 0, 0, 0, 0,
            overlay, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
        var initial = Marshal.StringToHGlobalUni("初始重置时间");
        var updated = Marshal.StringToHGlobalUni("窗口：剩余 13% · 重置：2时18分后\n重置时间：2026-10-08 12:18:00 +08:00");
        var result = Marshal.AllocHGlobal(2048);
        var ownerDestroyed = false;
        try
        {
            Assert.AreNotEqual(IntPtr.Zero, tooltip);
            var info = new TitleBarOverlayNative.TooltipInfo
            {
                Size = (uint)Marshal.SizeOf<TitleBarOverlayNative.TooltipInfo>(),
                Flags = 0x11,
                Window = overlay,
                Id = new UIntPtr(unchecked((ulong)overlay.ToInt64())),
                Text = initial,
            };
            Assert.AreNotEqual(IntPtr.Zero, TitleBarOverlayNative.SendTooltipMessage(tooltip,
                TitleBarOverlayNative.TooltipAddTool, UIntPtr.Zero, ref info));
            info.Text = updated;
            _ = TitleBarOverlayNative.SendTooltipMessage(tooltip, TitleBarOverlayNative.TooltipUpdateText, UIntPtr.Zero, ref info);
            info.Text = result;
            // TTM_GETTEXTW validates the actual registered Unicode text and native struct layout.
            _ = TitleBarOverlayNative.SendTooltipMessage(tooltip, 0x0438, new UIntPtr(1024), ref info);
            Assert.AreEqual(Marshal.PtrToStringUni(updated), Marshal.PtrToStringUni(result));
            Assert.IsFalse(TitleBarOverlayNative.IsWindowVisible(overlay));
            Assert.IsFalse(TitleBarOverlayNative.IsWindowVisible(tooltip));
            Assert.IsTrue(NativeMethods.DestroyWindow(overlay));
            ownerDestroyed = true;
            Assert.IsFalse(TitleBarOverlayNative.IsWindow(tooltip));
        }
        finally
        {
            if (!ownerDestroyed) { _ = NativeMethods.DestroyWindow(overlay); }
            foreach (var buffer in new[] { initial, updated, result }) { Marshal.FreeHGlobal(buffer); }
        }
    }

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
