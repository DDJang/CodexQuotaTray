using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexQuotaTray.App.Views;

internal sealed partial class HeatmapTooltipWindow : Window, IDisposable
{
    private const double TooltipWidthDips = 176;
    private const double TooltipHeightDips = 64;
    private readonly IntPtr hwnd;
    private readonly AppWindow appWindow;
    private readonly BackdropService backdrop = new();
    private readonly OverlappedPresenter presenter;
    private readonly NativeMethods.WindowProcedure windowProcedure;
    private readonly int borderColorSetHResult;
    private IntPtr originalWindowProcedure;
    private bool allowingClose;
    private bool disposed;
    private bool firstShowDwmDiagnosticsReported;
    private bool firstShowGeometryDiagnosticsReported;
    private bool enforceBorderlessNormalStyle;
    private bool normalStyleConfigured;
    private bool visible;
    private ElementTheme? appliedTheme;

    internal HeatmapTooltipWindow(IntPtr owner)
    {
        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);

        presenter = OverlappedPresenter.CreateForToolWindow();
        appWindow.SetPresenter(presenter);
        presenter.IsAlwaysOnTop = false;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);

        // A real top-level Window is required here: the system backdrop
        // controller targets an HWND, while the rejected XAML Popup path did
        // not produce a blurred surface on this runtime.
        NativeMethods.ConfigureTooltipWindow(hwnd, owner);
        // WS_EX_TRANSPARENT only affects paint ordering. The tooltip must also
        // return HTTRANSPARENT so pointer input reaches the owned main window
        // while the shared HWND is covering a heatmap cell.
        windowProcedure = HandleWindowMessage;
        originalWindowProcedure = NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(windowProcedure));
        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
        var borderColor = NativeMethods.DwmColorNone;
        borderColorSetHResult = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaBorderColor,
            ref borderColor,
            sizeof(int));
        LogDwmBorderDiagnostics("before-first-show");

        appWindow.Closing += OnClosing;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
    }

    internal void SetContent(string tokenText, string dateText)
    {
        HeatmapTooltipTokenText.Text = tokenText;
        HeatmapTooltipDateText.Text = dateText;
    }

    internal bool ShowAt(PointInt32 screenPosition, double rasterizationScale, ElementTheme theme)
    {
        if (disposed)
        {
            return false;
        }

        ApplyTheme(theme);
        // Move the HWND itself so DWM samples the pixels currently underneath
        // the tooltip. Translating content inside a fixed host would leave
        // the blur sampling the host's old screen location.
        var width = Math.Max(1, (int)Math.Round(TooltipWidthDips * rasterizationScale));
        var height = Math.Max(1, (int)Math.Round(TooltipHeightDips * rasterizationScale));
        var positioned = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTop,
            screenPosition.X,
            screenPosition.Y,
            width,
            height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        if (!positioned)
        {
            _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            visible = false;
            return false;
        }

        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwShownoactivate);
        if (!normalStyleConfigured)
        {
            // The presenter applies its final non-client style as the HWND is
            // shown. Apply this one-bit A/B after that update, then force the
            // non-client frame to be recalculated.
            RemoveResidualDialogFrame();
            normalStyleConfigured = true;
        }

        if (!firstShowGeometryDiagnosticsReported)
        {
            firstShowGeometryDiagnosticsReported = true;
            LogGeometryDiagnostics("after-first-show-frame");
        }

        visible = true;
        if (!firstShowDwmDiagnosticsReported)
        {
            firstShowDwmDiagnosticsReported = true;
            LogDwmBorderDiagnostics("after-first-show");
        }

        return true;
    }

    internal void Hide()
    {
        if (!visible)
        {
            return;
        }

        visible = false;
        _ = NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
    }

    internal void ApplyTheme(ElementTheme theme)
    {
        if (appliedTheme == theme)
        {
            return;
        }

        TooltipRoot.RequestedTheme = theme;
        var selected = backdrop.Apply(this);
        FallbackSurface.Visibility = selected == BackdropKind.DesktopAcrylic
            ? Visibility.Collapsed
            : Visibility.Visible;
        appliedTheme = theme;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Hide();
        backdrop.Dispose();
        enforceBorderlessNormalStyle = false;
        if (originalWindowProcedure != IntPtr.Zero)
        {
            _ = NativeMethods.SetWindowLongPtr(
                hwnd,
                NativeMethods.GwlWndProc,
                originalWindowProcedure);
            originalWindowProcedure = IntPtr.Zero;
        }

        allowingClose = true;
        Close();
    }

    private void RemoveResidualDialogFrame()
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        Debug.WriteLine(
            $"TokenUsage tooltip normal style before WS_DLGFRAME A/B: {DescribeNormalStyle(style)}");
        var updated = style & ~((long)NativeMethods.WsDlgFrame);
        if (updated == style)
        {
            Debug.WriteLine("TokenUsage tooltip WS_DLGFRAME was not present; no style change applied.");
            return;
        }

        enforceBorderlessNormalStyle = true;
        _ = NativeMethods.SetWindowLongPtr(
            hwnd,
            NativeMethods.GwlStyle,
            new IntPtr(updated));
        var frameChanged = NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
                | NativeMethods.SwpNoSize
                | NativeMethods.SwpNoZOrder
                | NativeMethods.SwpNoActivate
                | NativeMethods.SwpFrameChanged);
        var finalStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlStyle).ToInt64();
        Debug.WriteLine(
            $"TokenUsage tooltip normal style after WS_DLGFRAME A/B: {DescribeNormalStyle(finalStyle)} "
            + $"frameChanged={frameChanged}");
    }

    private void LogDwmBorderDiagnostics(string stage)
    {
        var borderColor = 0;
        var readHResult = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaBorderColor,
            out borderColor,
            sizeof(int));
        Debug.WriteLine(
            $"TokenUsage tooltip DWM border diagnostics: stage={stage} "
            + $"setHResult={FormatHResult(borderColorSetHResult)} "
            + $"readHResult={FormatHResult(readHResult)} "
            + $"readValue=0x{unchecked((uint)borderColor):X8} "
            + $"expected=0x{unchecked((uint)NativeMethods.DwmColorNone):X8}");
    }

    private void LogGeometryDiagnostics(string stage)
    {
        var windowOk = NativeMethods.GetWindowRect(hwnd, out var windowRect);
        var clientOk = NativeMethods.GetClientRect(hwnd, out var clientRect);
        var clientOrigin = new NativeMethods.NativePoint();
        var clientOriginOk = NativeMethods.ClientToScreen(hwnd, ref clientOrigin);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var windowWidth = windowRect.Right - windowRect.Left;
        var windowHeight = windowRect.Bottom - windowRect.Top;
        var clientWidth = clientRect.Right - clientRect.Left;
        var clientHeight = clientRect.Bottom - clientRect.Top;
        var deltaX = clientOrigin.X - windowRect.Left;
        var deltaY = clientOrigin.Y - windowRect.Top;
        Debug.WriteLine(
            $"TokenUsage tooltip geometry: stage={stage} dpi={dpi} "
            + $"windowOk={windowOk} window=({windowRect.Left},{windowRect.Top}) "
            + $"{windowWidth}x{windowHeight} "
            + $"clientOk={clientOk} client=({clientRect.Left},{clientRect.Top}) "
            + $"{clientWidth}x{clientHeight} "
            + $"clientOriginOk={clientOriginOk} clientOrigin=({clientOrigin.X},{clientOrigin.Y}) "
            + $"delta=({deltaX},{deltaY})");
    }

    private static string DescribeNormalStyle(long style)
    {
        var hasDialogFrame = (style & (long)NativeMethods.WsDlgFrame) != 0;
        var hasBorder = (style & (long)NativeMethods.WsBorder) != 0;
        var hasThickFrame = (style & (long)NativeMethods.WsThickFrame) != 0;
        var hasCaption = (style & (long)NativeMethods.WsCaption)
            == (long)NativeMethods.WsCaption;
        return $"style=0x{unchecked((ulong)style):X16} "
            + $"WS_DLGFRAME={hasDialogFrame} WS_BORDER={hasBorder} "
            + $"WS_THICKFRAME={hasThickFrame} WS_CAPTION={hasCaption}";
    }

    private static string FormatHResult(int hResult) =>
        $"0x{unchecked((uint)hResult):X8}";

    private IntPtr HandleWindowMessage(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam)
    {
        var isNormalStyleChanging = message == NativeMethods.WmStyleChanging
            && enforceBorderlessNormalStyle
            && (wParam.ToUInt64() & 0xFFFFFFFFUL) == unchecked((uint)NativeMethods.GwlStyle)
            && lParam != IntPtr.Zero;
        if (isNormalStyleChanging)
        {
            ClearDialogFrameFromStyleChange(lParam);
        }

        if (message == NativeMethods.WmNcHitTest)
        {
            return new IntPtr(NativeMethods.HtTransparent);
        }

        var result = originalWindowProcedure == IntPtr.Zero
            ? NativeMethods.DefWindowProc(window, message, wParam, lParam)
            : NativeMethods.CallWindowProc(
                originalWindowProcedure,
                window,
                message,
                wParam,
                lParam);
        if (isNormalStyleChanging)
        {
            // The presenter can rewrite STYLESTRUCT.styleNew while handling
            // WM_STYLECHANGING. Apply the same single-bit decision after it
            // returns so the subsequent frame recalculation sees the value
            // requested by this A/B.
            ClearDialogFrameFromStyleChange(lParam);
        }

        return result;
    }

    private static void ClearDialogFrameFromStyleChange(IntPtr styleStruct)
    {
        var newStyle = Marshal.ReadInt32(
            styleStruct,
            NativeMethods.StyleStructNewOffset);
        var updated = newStyle & ~unchecked((int)NativeMethods.WsDlgFrame);
        if (updated != newStyle)
        {
            Marshal.WriteInt32(
                styleStruct,
                NativeMethods.StyleStructNewOffset,
                updated);
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowingClose)
        {
            return;
        }

        args.Cancel = true;
        Hide();
    }
}
