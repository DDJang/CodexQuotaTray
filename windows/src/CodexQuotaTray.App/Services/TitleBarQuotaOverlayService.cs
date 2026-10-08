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
    private readonly Func<Task> refresh;
    private readonly TitleBarOverlayRefreshGate refreshGate = new();
    private Point? pressedAt;
    private readonly NativeMethods.WindowProcedure windowProcedure;
    private readonly TitleBarOverlayNative.WinEventCallback eventCallback;
    private readonly string className = $"CodexQuotaTray.TitleBarOverlay.{Guid.NewGuid():N}";
    private readonly List<IntPtr> hooks = [];
    private readonly IntPtr instance = NativeMethods.GetModuleHandle(null);
    private IntPtr window;
    private IntPtr tooltip;
    private IntPtr tooltipTextBuffer;
    private IntPtr tooltipFont;
    private uint tooltipDpi;
    private string tooltipText = string.Empty;
    private bool countdownTimerRunning;
    private static readonly UIntPtr CountdownTimerId = new(1);
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
    private DateTimeOffset presentationTime;
    private AppUiState? lastSnapshot;
    private bool showRemainingPercent = true;
    private string renderedText = string.Empty;
    private Rectangle placement;
    private TitleBarOverlayFrame? renderedFrame;
    private (string Text, uint Dpi, int Width)? fullMeasurement;
    private (string Text, uint Dpi, int Width)? percentageMeasurement;
    private (string Text, uint Dpi, int Width)? compactMeasurement;
    private IntPtr fontHandle;
    private uint fontDpi;
    private string status = "disabled";

    internal TitleBarQuotaOverlayService(Func<Action, bool> enqueue, Func<Task> refresh)
    {
        this.enqueue = enqueue;
        this.refresh = refresh;
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
        showRemainingPercent = settings.ShowRemainingPercent;
        UpdatePresentation();
        SetEnabled(settings.EffectiveTitleBarQuotaOverlayEnabled);
        UpdateSafely();
    }

    internal void SetPercentageDisplayMode(bool showRemainingPercent)
    {
        if (disposed || lastSnapshot is null)
        {
            return;
        }
        this.showRemainingPercent = showRemainingPercent;
        UpdatePresentation();
        UpdateSafely();
    }

    private void UpdatePresentation()
    {
        if (lastSnapshot is not null)
        {
            var now = DateTimeOffset.UtcNow;
            presentation = TitleBarQuotaOverlay.Project(
                lastSnapshot with { IsRefreshing = lastSnapshot.IsRefreshing || refreshGate.IsInFlight }, showRemainingPercent, now);
            presentationTime = now;
        }
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
            Cursor = TitleBarOverlayNative.LoadCursor(IntPtr.Zero, new IntPtr(32649)),
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
                | NativeMethods.WsExNoActivate,
            className, "CodexQuotaTray quota", NativeMethods.WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw LastError();
        }
        CreateTooltip();
    }

    private void CreateTooltip()
    {
        DestroyTooltip();
        var controls = new TitleBarOverlayNative.CommonControls
        {
            Size = (uint)Marshal.SizeOf<TitleBarOverlayNative.CommonControls>(),
            Classes = 0x000000FF,
        };
        if (!TitleBarOverlayNative.InitCommonControlsEx(ref controls)) { throw LastError(); }
        tooltip = NativeMethods.CreateWindowEx(NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow,
            "tooltips_class32", string.Empty, NativeMethods.WsPopup | 3, 0, 0, 0, 0, window, IntPtr.Zero, instance, IntPtr.Zero);
        if (tooltip == IntPtr.Zero) { throw LastError(); }
        tooltipTextBuffer = Marshal.StringToHGlobalUni(string.Empty);
        var info = TooltipInfo();
        if (TitleBarOverlayNative.SendTooltipMessage(tooltip, TitleBarOverlayNative.TooltipAddTool, UIntPtr.Zero, ref info) == IntPtr.Zero)
        {
            throw new Win32Exception("Cannot register title-bar tooltip.");
        }
    }

    private TitleBarOverlayNative.TooltipInfo TooltipInfo() => new()
    {
        Size = (uint)Marshal.SizeOf<TitleBarOverlayNative.TooltipInfo>(),
        Flags = 0x11, // IDISHWND | SUBCLASS: follows the entire clickable HWND.
        Window = window,
        Id = new UIntPtr(unchecked((ulong)window.ToInt64())),
        Text = tooltipTextBuffer,
    };

    private void UpdateTooltip(string text)
    {
        if (tooltip == IntPtr.Zero) { return; }
        if (tooltipDpi != dpi)
        {
            // A native tooltip's default font can stay at system DPI. Use a dedicated
            // font so moving the host between monitors does not leave it tiny.
            var nextFont = TitleBarOverlayNative.CreateFont(-(int)Math.Ceiling(14 * dpi / 96d),
                0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
            if (nextFont == IntPtr.Zero) { throw LastError(); }
            _ = TitleBarOverlayNative.SendMessage(tooltip, TitleBarOverlayNative.TooltipPop, UIntPtr.Zero, IntPtr.Zero);
            _ = TitleBarOverlayNative.SendMessage(tooltip, TitleBarOverlayNative.SetFont,
                new UIntPtr(unchecked((ulong)nextFont.ToInt64())), IntPtr.Zero);
            if (tooltipFont != IntPtr.Zero) { _ = TitleBarOverlayNative.DeleteObject(tooltipFont); }
            tooltipFont = nextFont;
            tooltipDpi = dpi;
            var horizontal = (int)Math.Ceiling(8 * dpi / 96d);
            var vertical = (int)Math.Ceiling(6 * dpi / 96d);
            var margins = new NativeMethods.NativeRect { Left = horizontal, Right = horizontal, Top = vertical, Bottom = vertical };
            _ = TitleBarOverlayNative.SendTooltipMargins(tooltip, TitleBarOverlayNative.TooltipSetMargin, UIntPtr.Zero, ref margins);
            _ = TitleBarOverlayNative.SendMessage(tooltip, TitleBarOverlayNative.TooltipMaxWidth, UIntPtr.Zero,
                new IntPtr((int)Math.Ceiling(600 * dpi / 96d)));
        }
        if (text == tooltipText) { return; }
        var previous = tooltipTextBuffer;
        tooltipTextBuffer = Marshal.StringToHGlobalUni(text);
        var info = TooltipInfo();
        _ = TitleBarOverlayNative.SendTooltipMessage(tooltip, TitleBarOverlayNative.TooltipUpdateText, UIntPtr.Zero, ref info);
        if (previous != IntPtr.Zero) { Marshal.FreeHGlobal(previous); }
        tooltipText = text;
    }

    private void DestroyTooltip()
    {
        if (tooltip != IntPtr.Zero && TitleBarOverlayNative.IsWindow(tooltip)) { _ = NativeMethods.DestroyWindow(tooltip); }
        tooltip = IntPtr.Zero;
        if (tooltipFont != IntPtr.Zero) { _ = TitleBarOverlayNative.DeleteObject(tooltipFont); tooltipFont = IntPtr.Zero; }
        tooltipDpi = 0;
        if (tooltipTextBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(tooltipTextBuffer); tooltipTextBuffer = IntPtr.Zero; }
        tooltipText = string.Empty;
    }

    private void StopCountdownTimer()
    {
        if (countdownTimerRunning && window != IntPtr.Zero) { _ = TitleBarOverlayNative.KillTimer(window, CountdownTimerId); }
        countdownTimerRunning = false;
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
            if (!TitleBarOverlayNative.IsWindow(window))
            {
                StopCountdownTimer();
                DestroyTooltip();
                window = IntPtr.Zero;
                renderedFrame = null;
            }
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
        // Recompute from absolute timestamps, including immediately after restoring a hidden host.
        if (lastSnapshot is not null && TitleBarQuotaOverlay.NeedsCountdownUpdate(lastSnapshot.Windows, presentationTime, DateTimeOffset.UtcNow))
        {
            UpdatePresentation();
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
        var maxWidth = (int)Math.Ceiling(640 * dpi / 96d);
        var next = fullWidth <= maxWidth ? TitleBarQuotaOverlay.Place(host, workArea, captionButtons, dpi, fullWidth) : null;
        if (next is null)
        {
            text = presentation.PercentageText;
            var percentageWidth = MeasureCached(text, ref percentageMeasurement);
            next = percentageWidth <= maxWidth ? TitleBarQuotaOverlay.Place(host, workArea, captionButtons, dpi, percentageWidth) : null;
        }
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
        UpdateTooltip(presentation.TooltipText);
        if (lastSnapshot?.Windows.Any(item => item.ResetAtUtc > DateTimeOffset.UtcNow) == true)
        {
            if (!countdownTimerRunning)
            {
                if (TitleBarOverlayNative.SetTimer(window, CountdownTimerId, 60_000, IntPtr.Zero) == UIntPtr.Zero) { throw LastError(); }
                countdownTimerRunning = true;
            }
        }
        else { StopCountdownTimer(); }
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
        if (message == TitleBarOverlayNative.TimerMessage && wParam == CountdownTimerId)
        {
            if (countdownTimerRunning) { UpdateSafely(); }
            return IntPtr.Zero;
        }
        if (message == 0x0201)
        {
            if (tooltip != IntPtr.Zero) { _ = TitleBarOverlayNative.SendMessage(tooltip, TitleBarOverlayNative.TooltipPop, UIntPtr.Zero, IntPtr.Zero); }
            pressedAt = new Point((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff));
            _ = TitleBarOverlayNative.SetCapture(hwnd);
            return IntPtr.Zero;
        }
        if (message == 0x0202)
        {
            var down = pressedAt;
            pressedAt = null;
            if (TitleBarOverlayNative.GetCapture() == hwnd) { _ = TitleBarOverlayNative.ReleaseCapture(); }
            var up = new Point((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff));
            if (down is { } origin && TitleBarOverlayInteraction.IsClick(origin, up, placement.Size, dpi))
            {
                QueueManualRefresh();
            }
            return IntPtr.Zero;
        }
        if (message == 0x0200 && pressedAt is { } start)
        {
            var current = new Point((short)(lParam.ToInt64() & 0xffff), (short)((lParam.ToInt64() >> 16) & 0xffff));
            if (!TitleBarOverlayInteraction.IsClick(start, current, placement.Size, dpi))
            {
                pressedAt = null;
                if (TitleBarOverlayNative.GetCapture() == hwnd) { _ = TitleBarOverlayNative.ReleaseCapture(); }
            }
        }
        if (message == 0x0215) { pressedAt = null; }
        if (message == 0x0082 && hwnd == window)
        {
            // Destroying an owner can destroy its owned popup. Keep the event hooks
            // and class alive so a later eligible host gets a fresh companion HWND.
            StopCountdownTimer();
            DestroyTooltip();
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

    private void QueueManualRefresh()
    {
        if (disposed || !enabled || failed || lastSnapshot?.IsRefreshing == true || !refreshGate.TryBegin()) { return; }
        if (!enqueue(() =>
        {
            if (disposed || !enabled || failed) { refreshGate.Complete(); return; }
            UpdatePresentation();
            UpdateSafely();
            _ = RefreshFromClickAsync();
        })) { refreshGate.Complete(); }
    }

    private async Task RefreshFromClickAsync()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            System.Diagnostics.Debug.WriteLine($"Title-bar manual refresh failed: {error.GetType().Name}");
        }
        finally
        {
            await RefreshPresentationPolicy.WaitForMinimumAsync(started).ConfigureAwait(false);
            if (!enqueue(() =>
            {
                refreshGate.Complete();
                if (disposed) { return; }
                UpdatePresentation();
                UpdateSafely();
            })) { refreshGate.Complete(); }
        }
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
            var info = new TitleBarOverlayNative.BitmapInfo
            {
                Size = 40,
                Width = placement.Width,
                Height = -placement.Height,
                Planes = 1,
                BitCount = 32,
            };
            var bitmap = TitleBarOverlayNative.CreateDIBSection(destination, ref info, 0, out var pixels, IntPtr.Zero, 0);
            var font = GetFont();
            var clear = TitleBarOverlayNative.CreateSolidBrush(0);
            var oldBitmap = IntPtr.Zero;
            var oldFont = IntPtr.Zero;
            try
            {
                if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || pixels == IntPtr.Zero || font == IntPtr.Zero || clear == IntPtr.Zero)
                {
                    return;
                }

                oldBitmap = TitleBarOverlayNative.SelectObject(dc, bitmap);
                oldFont = TitleBarOverlayNative.SelectObject(dc, font);
                var rect = new NativeMethods.NativeRect { Right = placement.Width, Bottom = placement.Height };
                _ = TitleBarOverlayNative.FillRect(dc, ref rect, clear);
                // Render an antialiased white mask, then supply premultiplied alpha
                // so spaces in the text target receive clicks without a visible box.
                _ = TitleBarOverlayNative.SetBkMode(dc, 1);
                _ = TitleBarOverlayNative.SetTextColor(dc, 0x00FFFFFF);
                var padding = (int)Math.Ceiling(12 * dpi / 96d);
                rect.Left = padding;
                rect.Right -= padding;
                // SINGLELINE | VCENTER | END_ELLIPSIS | NOPREFIX.
                _ = TitleBarOverlayNative.DrawText(dc, renderedText, renderedText.Length, ref rect, 0x00008824);
                _ = TitleBarOverlayNative.GdiFlush();
                var mask = new byte[checked(placement.Width * placement.Height * 4)];
                Marshal.Copy(pixels, mask, 0, mask.Length);
                TitleBarOverlayInteraction.ComposeTextPixels(mask);
                Marshal.Copy(mask, 0, pixels, mask.Length);
                var position = new NativeMethods.NativePoint { X = placement.X, Y = placement.Y };
                var size = new TitleBarOverlayNative.NativeSize { Width = placement.Width, Height = placement.Height };
                var source = new NativeMethods.NativePoint();
                var blend = new TitleBarOverlayNative.BlendFunction { ConstantAlpha = 255, AlphaFormat = 1 };
                if (!TitleBarOverlayNative.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref position, ref size, dc, ref source, 0, ref blend, 2))
                {
                    status = $"redraw unavailable (Win32 {Marshal.GetLastWin32Error()})";
                }
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
        StopCountdownTimer();
        if (tooltip != IntPtr.Zero) { _ = TitleBarOverlayNative.SendMessage(tooltip, TitleBarOverlayNative.TooltipPop, UIntPtr.Zero, IntPtr.Zero); }
        pressedAt = null;
        if (window != IntPtr.Zero && TitleBarOverlayNative.GetCapture() == window) { _ = TitleBarOverlayNative.ReleaseCapture(); }
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
        StopCountdownTimer();
        DestroyTooltip();
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
        percentageMeasurement = null;
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
