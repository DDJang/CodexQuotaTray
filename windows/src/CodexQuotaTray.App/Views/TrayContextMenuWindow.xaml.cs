using System.Drawing;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;
using Windows.Graphics;
using BackdropKind = CodexQuotaTray.Core.Models.BackdropKind;

namespace CodexQuotaTray.App.Views;

internal sealed partial class TrayContextMenuWindow : Window, IDisposable
{
    private const double MenuWidthDips = 160;
    private const double MenuMarginDips = 8;

    private readonly Action openPanel;
    private readonly Action openSettings;
    private readonly Action exitApplication;
    private readonly BackdropService backdrop = new();
    private readonly IntPtr hwnd;
    private readonly AppWindow appWindow;
    private readonly OverlappedPresenter? presenter;
    private bool visible;
    private bool activationPending;
    private bool allowingClose;
    private bool disposed;
    private bool menuRootLoaded;
    private bool layoutResizeQueued;
    private bool firstPresentationCompleted;
    private bool firstPresentationCloaked;
    private Rectangle? pendingAnchor;
    private int focusedItemIndex;

    internal TrayContextMenuWindow(
        Action openPanel,
        Action openSettings,
        Action exitApplication)
    {
        this.openPanel = openPanel;
        this.openSettings = openSettings;
        this.exitApplication = exitApplication;

        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);
        NativeMethods.ConfigureToolWindow(hwnd);

        ExtendsContentIntoTitleBar = true;
        var toolPresenter = OverlappedPresenter.CreateForToolWindow();
        appWindow.SetPresenter(toolPresenter);
        presenter = toolPresenter;
        presenter.IsAlwaysOnTop = false;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);

        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));

        appWindow.Closing += OnClosing;
        Activated += OnActivated;
        OpenButton.GotFocus += OnMenuItemGotFocus;
        SettingsButton.GotFocus += OnMenuItemGotFocus;
        ExitButton.GotFocus += OnMenuItemGotFocus;
        MenuRoot.Loaded += OnMenuRootLoaded;
        MenuRoot.KeyDown += OnMenuKeyDown;
    }

    internal void ToggleAt(Rectangle? anchor, ThemeMode themeMode)
    {
        if (disposed)
        {
            return;
        }

        if (visible)
        {
            HideMenu();
            return;
        }

        ApplyTheme(themeMode);
        pendingAnchor = anchor ?? CursorAnchor();
        PositionNearAnchor(pendingAnchor.Value);
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = true;
        }

        firstPresentationCloaked = !firstPresentationCompleted && SetCloaked(true);
        activationPending = true;
        visible = true;
        appWindow.Show();
        if (!firstPresentationCloaked)
        {
            firstPresentationCompleted = true;
            Activate();
            _ = NativeMethods.SetForegroundWindow(hwnd);
        }
        QueueLayoutResize();
    }

    internal void HideMenu()
    {
        if (!visible)
        {
            return;
        }

        visible = false;
        activationPending = false;
        pendingAnchor = null;
        ReleaseFirstPresentationCloak(activate: false);
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = false;
        }

        appWindow.Hide();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        visible = false;
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = false;
        }

        backdrop.Dispose();
        allowingClose = true;
        Close();
    }

    private void ApplyTheme(ThemeMode mode)
    {
        MenuRoot.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        var selected = backdrop.Apply(this);
        FallbackSurface.Visibility = selected == BackdropKind.Opaque
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnMenuRootLoaded(object sender, RoutedEventArgs args)
    {
        menuRootLoaded = true;
        QueueLayoutResize();
    }

    private void QueueLayoutResize()
    {
        if (!visible || !menuRootLoaded || layoutResizeQueued)
        {
            return;
        }

        layoutResizeQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            layoutResizeQueued = false;
            if (!visible)
            {
                ReleaseFirstPresentationCloak(activate: false);
                return;
            }

            MenuRoot.UpdateLayout();
            PositionNearAnchor(pendingAnchor ?? CursorAnchor());
            focusedItemIndex = 0;
            _ = OpenButton.Focus(FocusState.Programmatic);
            activationPending = false;
            ReleaseFirstPresentationCloak(activate: true);
        }))
        {
            layoutResizeQueued = false;
            activationPending = false;
            ReleaseFirstPresentationCloak(activate: true);
        }
    }

    private void PositionNearAnchor(Rectangle anchor)
    {
        var workArea = WindowPlacementService.GetWorkArea(anchor);

        var scale = WindowPlacementService.GetRasterizationScale(hwnd);
        var menuHeightDips = MeasureMenuHeightDips();
        var size = new Size(
            PopupPlacement.DipsToPixels(MenuWidthDips, scale),
            PopupPlacement.DipsToPixels(menuHeightDips, scale));
        var margin = PopupPlacement.DipsToPixels(MenuMarginDips, scale);

        var currentWindowSize = appWindow.Size;
        var currentClientSize = appWindow.ClientSize;
        var nonClientWidth = Math.Max(0, currentWindowSize.Width - currentClientSize.Width);
        var nonClientHeight = Math.Max(0, currentWindowSize.Height - currentClientSize.Height);
        appWindow.Resize(new SizeInt32(
            size.Width + nonClientWidth,
            size.Height + nonClientHeight));
        var finalWindowSize = appWindow.Size;
        var location = PopupPlacement.PlaceNearTray(
            anchor,
            workArea,
            new Size(finalWindowSize.Width, finalWindowSize.Height),
            margin);
        appWindow.Move(new PointInt32(location.X, location.Y));
    }

    private double MeasureMenuHeightDips()
    {
        var availableSize = new Windows.Foundation.Size(MenuWidthDips, double.PositiveInfinity);
        MenuRoot.Measure(availableSize);
        var desiredHeight = MenuRoot.DesiredSize.Height;
        if (!double.IsFinite(desiredHeight) || desiredHeight <= 0)
        {
            desiredHeight = MenuItems.DesiredSize.Height;
        }

        desiredHeight = Math.Max(1, Math.Ceiling(desiredHeight));
        MenuRoot.Arrange(new Windows.Foundation.Rect(0, 0, MenuWidthDips, desiredHeight));
        MenuRoot.UpdateLayout();
        return desiredHeight;
    }

    private bool SetCloaked(bool value)
    {
        var enabled = value ? 1 : 0;
        return NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaCloak,
            ref enabled,
            sizeof(int)) == 0;
    }

    private void ReleaseFirstPresentationCloak(bool activate)
    {
        if (!firstPresentationCloaked)
        {
            return;
        }

        _ = SetCloaked(false);
        firstPresentationCloaked = false;
        if (!activate || !visible)
        {
            return;
        }

        firstPresentationCompleted = true;
        Activate();
        _ = NativeMethods.SetForegroundWindow(hwnd);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (allowingClose)
        {
            return;
        }

        args.Cancel = true;
        HideMenu();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated
            && visible
            && !activationPending)
        {
            HideMenu();
        }
    }

    private void OnMenuItemGotFocus(object sender, RoutedEventArgs args)
    {
        focusedItemIndex = ReferenceEquals(sender, SettingsButton) ? 1 : 0;
        if (ReferenceEquals(sender, ExitButton))
        {
            focusedItemIndex = 2;
        }
    }

    private void OnMenuKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            HideMenu();
            args.Handled = true;
            return;
        }

        if (args.Key is not (VirtualKey.Up or VirtualKey.Down))
        {
            return;
        }

        var delta = args.Key == VirtualKey.Down ? 1 : -1;
        var next = (focusedItemIndex + delta + 3) % 3;
        switch (next)
        {
            case 0:
                _ = OpenButton.Focus(FocusState.Keyboard);
                break;
            case 1:
                _ = SettingsButton.Focus(FocusState.Keyboard);
                break;
            default:
                _ = ExitButton.Focus(FocusState.Keyboard);
                break;
        }

        args.Handled = true;
    }

    private void OnOpenClicked(object sender, RoutedEventArgs args) => Invoke(openPanel);

    private void OnSettingsClicked(object sender, RoutedEventArgs args) => Invoke(openSettings);

    private void OnExitClicked(object sender, RoutedEventArgs args) => Invoke(exitApplication);

    private void Invoke(Action action)
    {
        HideMenu();
        action();
    }

    private static Rectangle CursorAnchor()
    {
        return NativeMethods.GetCursorPos(out var point)
            ? new Rectangle(point.X, point.Y, 1, 1)
            : new Rectangle(0, 0, 1, 1);
    }
}
