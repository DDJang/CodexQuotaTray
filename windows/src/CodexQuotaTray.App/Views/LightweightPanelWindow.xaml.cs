using System.Drawing;
using System.ComponentModel;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;
using BackdropKind = CodexQuotaTray.Core.Models.BackdropKind;

namespace CodexQuotaTray.App.Views;

/// <summary>A persistent, small tray window that never constructs the full views.</summary>
internal sealed partial class LightweightPanelWindow : Window, IDisposable
{
    private readonly SettingsViewModel model;
    private readonly BackdropService backdrop = new();
    private readonly IntPtr hwnd;
    private bool updatingSwitch;
    private bool saving;
    private bool disposed;
    private Rectangle? panelAnchor;
    private bool positionQueued;
    internal bool IsVisible { get; private set; }

    internal LightweightPanelWindow(SettingsViewModel model, string displayName)
    {
        this.model = model;
        InitializeComponent();
        Title = $"{displayName} 轻量模式";
        hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.ConfigureToolWindow(hwnd);
        ExtendsContentIntoTitleBar = true;
        var presenter = OverlappedPresenter.CreateForToolWindow();
        AppWindow.SetPresenter(presenter);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.Closing += OnClosing;
        Activated += OnActivated;
        PanelRoot.ActualThemeChanged += OnThemeChanged;
        PanelRoot.SizeChanged += OnSizeChanged;
        model.PropertyChanged += OnModelPropertyChanged;
    }

    internal void ShowPanel(Rectangle? anchor, ThemeMode mode)
    {
        if (disposed) { return; }
        SyncSwitch();
        ErrorText.Visibility = Visibility.Collapsed;
        panelAnchor = anchor;
        IsVisible = true;
        ApplyTheme(mode);
        Position(anchor);
        AppWindow.Show();
        Activate();
        _ = NativeMethods.SetForegroundWindow(hwnd);
        _ = DispatcherQueue.TryEnqueue(() => { if (IsVisible) { Position(anchor); _ = ModeSwitch.Focus(FocusState.Programmatic); } });
    }

    internal void HidePanel()
    {
        IsVisible = false;
        AppWindow.Hide();
        backdrop.Dispose();
        SystemBackdrop = null;
    }

    internal void ApplyTheme(ThemeMode mode)
    {
        PanelRoot.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        RefreshTheme();
    }

    internal void RefreshTheme()
    {
        if (disposed || !IsVisible) { return; }
        var selected = backdrop.Apply(this);
        FallbackSurface.Visibility = selected == BackdropKind.Opaque ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => RefreshTheme();
    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!IsVisible || positionQueued) { return; }
        positionQueued = true;
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            positionQueued = false;
            if (IsVisible) { Position(panelAnchor); }
        });
    }

    private void Position(Rectangle? anchor)
    {
        var scale = WindowPlacementService.GetRasterizationScale(hwnd);
        PanelRoot.Measure(new Windows.Foundation.Size(300, double.PositiveInfinity));
        var size = new SizeInt32(PopupPlacement.DipsToPixels(300, scale),
            PopupPlacement.DipsToPixels(Math.Max(1, Math.Ceiling(PanelRoot.DesiredSize.Height)), scale));
        if (AppWindow.ClientSize.Width != size.Width || AppWindow.ClientSize.Height != size.Height)
        {
            var frame = AppWindow.Size;
            var client = AppWindow.ClientSize;
            AppWindow.Resize(new SizeInt32(size.Width + Math.Max(0, frame.Width - client.Width),
                size.Height + Math.Max(0, frame.Height - client.Height)));
        }
        if (anchor is null)
        {
            _ = NativeMethods.GetCursorPos(out var point);
            anchor = new Rectangle(point.X, point.Y, 1, 1);
        }
        var area = WindowPlacementService.GetWorkArea(anchor.Value);
        var location = PopupPlacement.PlaceNearTray(anchor.Value, area,
            new Size(AppWindow.Size.Width, AppWindow.Size.Height), PopupPlacement.DipsToPixels(8, scale));
        AppWindow.Move(new PointInt32(location.X, location.Y));
    }

    private void SyncSwitch()
    {
        updatingSwitch = true;
        ModeSwitch.IsOn = model.LightweightModeEnabled;
        ModeSwitch.IsEnabled = !saving && !model.IsBusy;
        updatingSwitch = false;
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (null or "" or nameof(SettingsViewModel.IsBusy)
            or nameof(SettingsViewModel.LightweightModeEnabled))) { return; }
        if (disposed) { return; }
        if (DispatcherQueue.HasThreadAccess) { RefreshModelState(args.PropertyName); }
        else { _ = DispatcherQueue.TryEnqueue(() => RefreshModelState(args.PropertyName)); }
    }

    private void RefreshModelState(string? propertyName)
    {
        if (disposed) { return; }
        // A busy notification must not undo the switch's pending off position.
        if (propertyName == nameof(SettingsViewModel.IsBusy)) { ModeSwitch.IsEnabled = !saving && !model.IsBusy; }
        else { SyncSwitch(); }
    }

    private async void OnModeToggled(object sender, RoutedEventArgs args)
    {
        if (updatingSwitch || model is null || ModeSwitch.IsOn || saving || disposed) { return; }
        saving = true;
        ModeSwitch.IsEnabled = false;
        try
        {
            await model.DisableLightweightModeAsync();
            if (model.LightweightModeEnabled)
            {
                ErrorText.Text = model.StatusText;
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        finally { saving = false; if (!disposed) { SyncSwitch(); } }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsVisible) { HidePanel(); }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (disposed) { return; }
        args.Cancel = true;
        HidePanel();
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        model.PropertyChanged -= OnModelPropertyChanged;
        AppWindow.Closing -= OnClosing;
        Activated -= OnActivated;
        PanelRoot.ActualThemeChanged -= OnThemeChanged;
        PanelRoot.SizeChanged -= OnSizeChanged;
        backdrop.Dispose();
        Close();
    }
}
