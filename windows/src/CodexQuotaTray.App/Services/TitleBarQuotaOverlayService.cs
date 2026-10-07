using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;

namespace CodexQuotaTray.App.Services;

/// <summary>A window-following companion HWND. All native resources belong to the UI thread.</summary>
internal sealed class TitleBarQuotaOverlayService : IDisposable
{
    private readonly Func<Action, bool> enqueue;
    private readonly NativeMethods.WindowProcedure windowProcedure;
    private readonly TitleBarOverlayNative.WinEventCallback eventCallback;
    private readonly string className = $"CodexQuotaTray.TitleBarOverlay.{Guid.NewGuid():N}";
    private readonly List<IntPtr> hooks = [];
    private readonly IntPtr instance = NativeMethods.GetModuleHandle(null);
    private IntPtr window;
    private IntPtr target;
    private uint targetProcessId;
    private bool registered;
    private bool enabled;
    private bool failed;
    private bool disposed;
    private bool updateQueued;
    private bool discoveryPending;
    private uint dpi = 96;
    private TitleBarQuotaPresentation? presentation;
    private AppUiState? lastSnapshot;
    private string renderedText = string.Empty;
    private Rectangle placement;
    private TitleBarOverlayFrame? renderedFrame;
    private (string Text, uint Dpi, int Width)? fullMeasurement;
    private (string Text, uint Dpi, int Width)? compactMeasurement;
    private IntPtr fontHandle;
    private uint fontDpi;
    private string status = "disabled";

    internal TitleBarQuotaOverlayService(Func<Action, bool> enqueue)
    {
        this.enqueue = enqueue;
        windowProcedure = WindowProc;
        eventCallback = OnWindowEvent;
    }

    internal string CreateDiagnosticText() => $"Title-bar quota overlay: {status}";

    internal void ApplySnapshot(AppUiState state, AppSettings settings)
    {
        if (disposed)
        {
            return;
        }

        lastSnapshot = state;
        presentation = TitleBarQuotaOverlay.Project(state, settings.ShowRemainingPercent);
        SetEnabled(settings.TitleBarQuotaOverlayEnabled);
        UpdateSafely();
    }

    internal void SetPercentageDisplayMode(bool showRemainingPercent)
    {
        if (disposed || lastSnapshot is null)
        {
            return;
        }
        presentation = TitleBarQuotaOverlay.Project(lastSnapshot, showRemainingPercent);
        UpdateSafely();
    }

    internal void SetEnabled(bool value)
    {
        if (disposed || enabled == value)
        {
            return;
        }

        enabled = value;
        failed = false;
        if (!value)
        {
            StopNative();
            status = "disabled";
            return;
        }

        try
        {
            StartNative();
            UpdatePlacement(discover: true);
        }
        catch (Win32Exception error)
        {
            Fail(error);
        }
    }

    private void StartNative()
    {
        var definition = new NativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProcedure = windowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (NativeMethods.RegisterClassEx(ref definition) == 0)
        {
            throw LastError();
        }

        registered = true;
        CreateOverlayWindow();

        foreach (var eventType in new[]
        {
            TitleBarOverlayNative.Foreground, TitleBarOverlayNative.LocationChange,
            TitleBarOverlayNative.MinimizeStart, TitleBarOverlayNative.MinimizeEnd,
            TitleBarOverlayNative.Destroy, TitleBarOverlayNative.Show, TitleBarOverlayNative.Hide,
            TitleBarOverlayNative.Cloaked, TitleBarOverlayNative.Uncloaked,
        })
        {
            // OUTOFCONTEXT | SKIPOWNPROCESS: notification only, never injects a DLL.
            var hook = TitleBarOverlayNative.SetWinEventHook(eventType, eventType, IntPtr.Zero, eventCallback, 0, 0, 2);
            if (hook == IntPtr.Zero)
            {
                throw LastError();
            }

            hooks.Add(hook);
        }
    }

    private void CreateOverlayWindow()
    {
        renderedFrame = null;
        window = NativeMethods.CreateWindowEx(
            TitleBarOverlayNative.WsExLayered | NativeMethods.WsExToolWindow
                | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent,
            className, "CodexQuotaTray quota", NativeMethods.WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero
            || !TitleBarOverlayNative.SetLayeredWindowAttributes(window, TitleBarOverlayNative.TransparentColor, 255, 1))
        {
            throw LastError();
        }
    }

    private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
    {
        if (disposed || !enabled || failed || hwnd == IntPtr.Zero
            || (eventType != TitleBarOverlayNative.Foreground && (objectId != 0 || childId != 0)))
        {
            return;
        }

        if (eventType is TitleBarOverlayNative.Foreground or TitleBarOverlayNative.Show or TitleBarOverlayNative.Uncloaked)
        {
            if (hwnd != target)
            {
                if (!IsHost(hwnd) || !CanShowHost(hwnd)) { return; }
                SetTarget(hwnd);
            }
        }
        else if (hwnd != target)
        {
            return;
        }
        if (eventType == TitleBarOverlayNative.Destroy)
        {
            SetTarget(IntPtr.Zero);
        }
        QueueUpdate(eventType == TitleBarOverlayNative.Destroy);
    }

    private void QueueUpdate(bool discover = false)
    {
        if (disposed || !enabled || failed) { return; }
        discoveryPending |= discover;
        if (updateQueued) { return; }
        updateQueued = true;
        if (!enqueue(() =>
        {
            updateQueued = false;
            var shouldDiscover = discoveryPending;
            discoveryPending = false;
            UpdateSafely(shouldDiscover);
        }))
        {
            updateQueued = false;
        }
    }

    private void UpdateSafely(bool discover = false)
    {
        if (disposed || !enabled || failed)
        {
            return;
        }

        try
        {
            UpdatePlacement(discover);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1400)
        {
            // Host destruction can race geometry/ownership reads. Keep discovery
            // hooks alive; the next eligible show/foreground event can reattach.
            if (!TitleBarOverlayNative.IsWindow(window)) { window = IntPtr.Zero; renderedFrame = null; }
            Hide("host lifecycle changed");
        }
        catch (Win32Exception error)
        {
            Fail(error);
        }
    }

    private void UpdatePlacement(bool discover = false)
    {
        if (discover)
        {
            SetTarget(FindHost());
        }
        _ = TitleBarOverlayNative.GetWindowThreadProcessId(target, out var processId);
        if (target == IntPtr.Zero || processId == 0 || processId != targetProcessId)
        {
            Hide("waiting for ChatGPT window");
            return;
        }

        if (presentation is null || !CanShowHost(target))
        {
            Hide("host hidden, minimized or cloaked", detach: false);
            return;
        }
        if (TitleBarOverlayNative.GetDwmRect(target, 9, out var bounds, Marshal.SizeOf<NativeMethods.NativeRect>()) != 0
            && !NativeMethods.GetWindowRect(target, out bounds))
        {
            Hide("host geometry unavailable", detach: false);
            return;
        }

        dpi = NativeMethods.GetDpiForWindow(target);
        if (dpi is < 48 or > 768)
        {
            Hide("host DPI unavailable", detach: false);
            return;
        }
        var host = ToRectangle(bounds);
        var monitor = TitleBarOverlayNative.MonitorFromWindow(target, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            Hide("monitor geometry unavailable", detach: false);
            return;
        }

        Rectangle? captionButtons = null;
        if (TitleBarOverlayNative.GetDwmRect(target, 5, out var buttons, Marshal.SizeOf<NativeMethods.NativeRect>()) == 0
            && buttons.Right > buttons.Left && buttons.Bottom > buttons.Top
            && NativeMethods.GetWindowRect(target, out var outerBounds))
        {
            // DWM caption-button bounds are relative to the window rectangle.
            captionButtons = new Rectangle(outerBounds.Left + buttons.Left, outerBounds.Top + buttons.Top,
                buttons.Right - buttons.Left, buttons.Bottom - buttons.Top);
        }

        var workArea = ToRectangle(monitorInfo.Work);
        if (window == IntPtr.Zero)
        {
            CreateOverlayWindow();
        }
        var text = presentation.Text;
        var fullWidth = MeasureCached(text, ref fullMeasurement);
        var maxWidth = (int)Math.Ceiling(480 * dpi / 96d);
        var next = fullWidth <= maxWidth ? TitleBarQuotaOverlay.Place(host, workArea, captionButtons, dpi, fullWidth) : null;
        if (next is null)
        {
            text = presentation.CompactText;
            var compactWidth = text == presentation.Text ? fullWidth : MeasureCached(text, ref compactMeasurement);
            next = TitleBarQuotaOverlay.Place(host, workArea, captionButtons, dpi, Math.Min(compactWidth, maxWidth));
        }

        if (next is not { } rectangle)
        {
            Hide("title bar has insufficient space", detach: false);
            return;
        }

        renderedText = text;
        placement = rectangle;
        var frame = new TitleBarOverlayFrame(target, text, rectangle, dpi);
        var repaint = frame.RequiresRepaint(renderedFrame);
        var ownerChanged = TitleBarOverlayNative.GetWindow(window, 4) != target;
        var showing = !TitleBarOverlayNative.IsWindowVisible(window);
        TitleBarOverlayNative.AttachOwner(window, target);
        if (ownerChanged || showing || frame != renderedFrame)
        {
            var zOrder = ownerChanged || showing
                ? TitleBarOverlayNative.OwnerPlacementZOrder(window, target)
                : (After: IntPtr.Zero, Flags: NativeMethods.SwpNoZOrder);
            // NOOWNERZORDER prevents showing a background companion from raising
            // its host. Geometry-only changes preserve the existing stacking order.
            if (!NativeMethods.SetWindowPos(window, zOrder.After, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow | 0x0200 | zOrder.Flags))
            {
                throw LastError();
            }
        }
        if (repaint) { _ = TitleBarOverlayNative.InvalidateRect(window, IntPtr.Zero, false); }
        renderedFrame = frame;
        status = "visible";
    }

    private void SetTarget(IntPtr value)
    {
        target = value;
        _ = TitleBarOverlayNative.GetWindowThreadProcessId(value, out targetProcessId);
    }

    private static bool CanShowHost(IntPtr hwnd)
    {
        var cloaked = NativeMethods.DwmGetWindowAttribute(hwnd, 14, out var value, sizeof(int)) == 0 && value != 0;
        return TitleBarQuotaOverlay.CanShowHost(TitleBarOverlayNative.IsWindowVisible(hwnd), TitleBarOverlayNative.IsIconic(hwnd), cloaked);
    }

    private static IntPtr FindHost()
    {
        var foreground = TitleBarOverlayNative.GetForegroundWindow();
        if (IsHost(foreground) && CanShowHost(foreground)) { return foreground; }
        var found = IntPtr.Zero;
        var visited = 0;
        TitleBarOverlayNative.EnumWindowCallback callback = (hwnd, _) =>
        {
            if (++visited > 4096) { return false; }
            if (CanShowHost(hwnd) && IsHost(hwnd)) { found = hwnd; return false; }
            return true;
        };
        _ = TitleBarOverlayNative.EnumWindows(callback, IntPtr.Zero);
        return found;
    }

    private static bool IsHost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !TitleBarOverlayNative.IsWindow(hwnd))
        {
            return false;
        }

        _ = TitleBarOverlayNative.GetWindowThreadProcessId(hwnd, out var processId);
        var process = TitleBarOverlayNative.OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var family = new StringBuilder(256);
            var length = (uint)family.Capacity;
            if (TitleBarOverlayNative.GetPackageFamilyName(process, ref length, family) != 0
                || !family.ToString().Equals(TitleBarQuotaOverlay.HostPackageFamily, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var path = new StringBuilder(1024);
            length = (uint)path.Capacity;
            if (!TitleBarOverlayNative.QueryFullProcessImageName(process, 0, path, ref length))
            {
                return false;
            }

            var title = new StringBuilder(64);
            var windowClass = new StringBuilder(64);
            _ = TitleBarOverlayNative.GetWindowText(hwnd, title, title.Capacity);
            _ = TitleBarOverlayNative.GetClassName(hwnd, windowClass, windowClass.Capacity);
            return TitleBarQuotaOverlay.IsHost(Path.GetFileNameWithoutExtension(path.ToString()), family.ToString(),
                title.ToString(), windowClass.ToString(),
                (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExToolWindow) != 0,
                TitleBarOverlayNative.GetWindow(hwnd, 4) != IntPtr.Zero);
        }
        finally
        {
            _ = TitleBarOverlayNative.CloseHandle(process);
        }
    }

    private int MeasureCached(string text, ref (string Text, uint Dpi, int Width)? cache)
    {
        if (cache is { } value && value.Text == text && value.Dpi == dpi) { return value.Width; }
        var width = Measure(text);
        cache = (text, dpi, width);
        return width;
    }

    private int Measure(string text)
    {
        var dc = TitleBarOverlayNative.GetDC(window);
        var font = GetFont();
        if (dc == IntPtr.Zero || font == IntPtr.Zero)
        {
            if (dc != IntPtr.Zero) { _ = TitleBarOverlayNative.ReleaseDC(window, dc); }
            throw LastError();
        }

        var previous = TitleBarOverlayNative.SelectObject(dc, font);
        try
        {
            if (!TitleBarOverlayNative.GetTextExtentPoint32(dc, text, text.Length, out var size))
            {
                throw LastError();
            }

            return size.Width + (int)Math.Ceiling(24 * dpi / 96d);
        }
        finally
        {
            _ = TitleBarOverlayNative.SelectObject(dc, previous);
            _ = TitleBarOverlayNative.ReleaseDC(window, dc);
        }
    }

    private IntPtr GetFont()
    {
        if (fontHandle == IntPtr.Zero || fontDpi != dpi)
        {
            if (fontHandle != IntPtr.Zero) { _ = TitleBarOverlayNative.DeleteObject(fontHandle); }
            fontHandle = TitleBarOverlayNative.CreateFont(-(int)Math.Ceiling(14 * dpi / 96d), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            fontDpi = dpi;
        }
        return fontHandle;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == 0x0082 && hwnd == window)
        {
            // Destroying an owner can destroy its owned popup. Keep the event hooks
            // and class alive so a later eligible host gets a fresh companion HWND.
            window = IntPtr.Zero;
            renderedFrame = null;
            SetTarget(IntPtr.Zero);
            QueueUpdate(discover: true);
        }
        if (message == 0x000F)
        {
            Paint(hwnd);
            return IntPtr.Zero;
        }

        if (message == 0x0014) { return new IntPtr(1); }
        if (message == 0x0021) { return new IntPtr(3); }
        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Paint(IntPtr hwnd)
    {
        var destination = TitleBarOverlayNative.BeginPaint(hwnd, out var paint);
        try
        {
            if (destination == IntPtr.Zero || placement.Width <= 0 || placement.Height <= 0)
            {
                return;
            }

            var dc = TitleBarOverlayNative.CreateCompatibleDC(destination);
            var bitmap = TitleBarOverlayNative.CreateCompatibleBitmap(destination, placement.Width, placement.Height);
            var font = GetFont();
            var clear = TitleBarOverlayNative.CreateSolidBrush(TitleBarOverlayNative.TransparentColor);
            var oldBitmap = IntPtr.Zero;
            var oldFont = IntPtr.Zero;
            try
            {
                if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || font == IntPtr.Zero || clear == IntPtr.Zero)
                {
                    return;
                }

                oldBitmap = TitleBarOverlayNative.SelectObject(dc, bitmap);
                oldFont = TitleBarOverlayNative.SelectObject(dc, font);
                var rect = new NativeMethods.NativeRect { Right = placement.Width, Bottom = placement.Height };
                _ = TitleBarOverlayNative.FillRect(dc, ref rect, clear);
                // TRANSPARENT (1), not OPAQUE (2): only glyphs enter the color-key
                // surface. Match the native menu's neutral gray, without a capsule.
                _ = TitleBarOverlayNative.SetBkMode(dc, 1);
                _ = TitleBarOverlayNative.SetTextColor(dc, 0x00908F8E);
                var padding = (int)Math.Ceiling(12 * dpi / 96d);
                rect.Left = padding;
                rect.Right -= padding;
                // SINGLELINE | VCENTER | END_ELLIPSIS | NOPREFIX.
                _ = TitleBarOverlayNative.DrawText(dc, renderedText, renderedText.Length, ref rect, 0x00008824);
                _ = TitleBarOverlayNative.BitBlt(destination, 0, 0, placement.Width, placement.Height, dc, 0, 0, 0x00CC0020);
            }
            finally
            {
                if (oldFont != IntPtr.Zero) { _ = TitleBarOverlayNative.SelectObject(dc, oldFont); }
                if (oldBitmap != IntPtr.Zero) { _ = TitleBarOverlayNative.SelectObject(dc, oldBitmap); }
                foreach (var resource in new[] { clear, bitmap })
                {
                    if (resource != IntPtr.Zero) { _ = TitleBarOverlayNative.DeleteObject(resource); }
                }
                if (dc != IntPtr.Zero) { _ = TitleBarOverlayNative.DeleteDC(dc); }
            }
        }
        finally
        {
            _ = TitleBarOverlayNative.EndPaint(hwnd, ref paint);
        }
    }

    private void Hide(string reason, bool detach = true)
    {
        if (detach) { SetTarget(IntPtr.Zero); }
        if (TitleBarOverlayNative.IsWindowVisible(window)) { _ = NativeMethods.ShowWindow(window, NativeMethods.SwHide); }
        if (detach && window != IntPtr.Zero)
        {
            _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.GwlHwndParent, IntPtr.Zero);
        }
        status = reason;
    }

    private void Fail(Win32Exception error)
    {
        failed = true;
        StopNative();
        status = $"unavailable (Win32 {error.NativeErrorCode}); toggle off/on to retry";
    }

    private void StopNative()
    {
        SetTarget(IntPtr.Zero);
        foreach (var hook in hooks) { _ = TitleBarOverlayNative.UnhookWinEvent(hook); }
        hooks.Clear();
        if (window != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyWindow(window);
            window = IntPtr.Zero;
        }
        if (registered)
        {
            _ = NativeMethods.UnregisterClass(className, instance);
            registered = false;
        }
        if (fontHandle != IntPtr.Zero) { _ = TitleBarOverlayNative.DeleteObject(fontHandle); fontHandle = IntPtr.Zero; }
        fullMeasurement = null;
        compactMeasurement = null;
        renderedFrame = null;
    }

    private static Rectangle ToRectangle(NativeMethods.NativeRect rect) => Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        StopNative();
    }
}
