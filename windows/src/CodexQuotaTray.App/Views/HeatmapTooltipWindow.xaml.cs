using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    private IntPtr originalWindowProcedure;
    private bool allowingClose;
    private bool disposed;
    private bool visible;
    private ElementTheme? appliedTheme;

    internal HeatmapTooltipWindow(IntPtr owner)
    {
        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);

        ExtendsContentIntoTitleBar = true;
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
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaBorderColor,
            ref borderColor,
            sizeof(int));

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
        visible = true;
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

    private IntPtr HandleWindowMessage(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam)
    {
        if (message == NativeMethods.WmNcHitTest)
        {
            return new IntPtr(NativeMethods.HtTransparent);
        }

        return originalWindowProcedure == IntPtr.Zero
            ? NativeMethods.DefWindowProc(window, message, wParam, lParam)
            : NativeMethods.CallWindowProc(
                originalWindowProcedure,
                window,
                message,
                wParam,
                lParam);
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
