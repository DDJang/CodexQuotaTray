using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using CodexQuotaTray.App.Services;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CodexQuotaTray.Core.Persistence;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace CodexQuotaTray.App.Views;

public sealed partial class MainWindow : Window
{
    private const double PanelWidthDips = 420;
    private static readonly TimeSpan PageResizeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan FirstPresentationTimeout = TimeSpan.FromSeconds(1);
    private readonly MainViewModel viewModel;
    private readonly TokenUsageViewModel tokenUsageViewModel;
    private TokenUsageView? tokenUsageView;
    private readonly WindowPlacementService placement = new();
    private readonly BackdropService backdrop = new();
    private readonly WindowVisibilityController visibility = new();
    private readonly FirstPresentationGate firstPresentation = new();
    private readonly CancellationTokenSource presentationLifetime = new();
    private readonly UISettings uiSettings = new();
    private readonly AppWindow appWindow;
    private readonly IntPtr hwnd;
    private bool exiting;
    private bool trayAvailable = true;
    private bool positionQueued;
    private bool forcePositionQueued;
    private bool hasSessionPosition;
    private bool windowConfigured;
    private bool showingTokenPage;
    private bool pageTransitionRunning;
    private bool firstShowRequestedLogged;
    private Stopwatch? firstPresentationStopwatch;
#if DEBUG
    private readonly List<string> firstPresentationTiming = [];
#endif
    private long pageTransitionRevision;

    public MainWindow(MainViewModel viewModel, TokenUsageViewModel tokenUsageViewModel, string displayName)
    {
        this.viewModel = viewModel;
        this.tokenUsageViewModel = tokenUsageViewModel;
        InitializeComponent();
        Title = displayName;
        hwnd = WindowNative.GetWindowHandle(this);
        ContentRoot.DataContext = viewModel;
        QuotaPageHost.Children.Add(new QuotaView());

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appWindow = AppWindow.GetFromWindowId(windowId);

        // The app is activated once during startup before the panel is shown.
        // Set the icon before that first activation so the taskbar button does
        // not capture WinUI's default application icon.
        _ = WindowIconService.TrySetIcon(appWindow, ContentRoot.ActualTheme == ElementTheme.Dark);

        appWindow.Closing += OnClosing;
        Activated += OnActivated;
        PanelContent.SizeChanged += (_, _) =>
        {
            if (!pageTransitionRunning)
            {
                QueuePositionIfVisible();
            }
        };
        ContentRoot.ActualThemeChanged += (_, _) =>
        {
            UpdateTabSelectionVisuals();
            if (visibility.DesiredVisible)
            {
                ApplyBackdrop();
            }
        };
        if (!uiSettings.AnimationsEnabled)
        {
            TabSelectionPill.TranslationTransition = null;
        }

        UpdateTabSelectionVisuals();
    }

    internal void ConfigureWindow()
    {
        appWindow.Title = Title;
        _ = WindowIconService.TrySetIcon(appWindow, ContentRoot.ActualTheme == ElementTheme.Dark);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderDragRegion);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }

        var cornerPreference = NativeMethods.DwmWindowCornerPreferenceRound;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    internal Func<Rectangle?> TrayRectangleProvider { get; set; } = static () => null;

    internal bool IsDesiredVisible => visibility.DesiredVisible;

    internal event EventHandler? PanelShown;

    internal event EventHandler? ExitRequested;

    internal void TogglePanel()
    {
        if (visibility.Toggle())
        {
            ShowPanelCore();
        }
        else
        {
            appWindow.Hide();
        }
    }

    internal void ShowPanel()
    {
        var wasHidden = !visibility.DesiredVisible;
        visibility.Show();
        ShowPanelCore(wasHidden);
    }

    internal void HidePanel()
    {
        tokenUsageView?.ResetHeatmapInteraction();
        visibility.Hide();
        appWindow.Hide();
    }

    internal void PrepareForExit()
    {
        exiting = true;
        presentationLifetime.Cancel();
        Interlocked.Increment(ref pageTransitionRevision);
        tokenUsageView?.Dispose();
        visibility.Hide();
        backdrop.Dispose();
    }

    internal async Task<bool> ShowPreviousCrashNoticeAsync(PreviousCrashInfo crashInfo)
    {
        if (!ContentRoot.IsLoaded)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                ContentRoot.Loaded -= onLoaded;
                loaded.TrySetResult();
            };
            ContentRoot.Loaded += onLoaded;
            if (ContentRoot.IsLoaded)
            {
                ContentRoot.Loaded -= onLoaded;
                loaded.TrySetResult();
            }

            await loaded.Task;
        }

        if (ContentRoot.XamlRoot is null)
        {
            return false;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "上次运行未正常结束，已保存本地崩溃日志。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = crashInfo.LogPath,
            TextWrapping = TextWrapping.Wrap,
        });
        var dialog = new ContentDialog
        {
            Title = "检测到异常退出",
            Content = content,
            CloseButtonText = "关闭",
            RequestedTheme = ContentRoot.ActualTheme,
            XamlRoot = ContentRoot.XamlRoot,
        };
        try
        {
            _ = await dialog.ShowAsync();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal void SetTrayAvailable(bool available)
    {
        trayAvailable = available;
        TrayFailureBanner.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        if (!available)
        {
            ShowPanel();
        }
        else
        {
            QueuePositionIfVisible();
        }
    }

    private void ShowPanelCore(bool raisePanelShown = true)
    {
        if (!firstShowRequestedLogged)
        {
            firstShowRequestedLogged = true;
            firstPresentationStopwatch = Stopwatch.StartNew();
            TraceFirstPresentation("ShowPanel requested");
        }

        _ = PresentPanelAsync(raisePanelShown);
    }

    private async Task PresentPanelAsync(bool raisePanelShown)
    {
        try
        {
            _ = await firstPresentation.PresentAsync(
                SetFirstPresentationCloaked,
                PresentPanelCore,
                WaitForFirstPresentationReadyAsync,
                () => !exiting && visibility.DesiredVisible,
                appWindow.Hide,
                () => OnPanelRevealed(raisePanelShown),
                FirstPresentationTimeout,
                presentationLifetime.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Debug.WriteLine($"First presentation readiness failed: {error.GetType().Name}");
        }
    }

    private void PresentPanelCore()
    {
        if (!windowConfigured)
        {
            ConfigureWindow();
            windowConfigured = true;
            TraceFirstPresentation("ConfigureWindow complete");
        }

        ApplyBackdrop();
        TraceFirstPresentation("ApplyBackdrop complete");
        Position();
        TraceFirstPresentation("Position complete");
        TraceFirstPresentation("Activate called");
        Activate();
        TraceFirstPresentation("Activate returned");
        appWindow.Show();
        if (showingTokenPage)
        {
            tokenUsageView?.PrepareHeatmapInteraction();
        }

        // The first measure can still see the hidden window's old ScrollViewer
        // viewport. Re-measure after the window is shown so the client height is
        // based on the actual footer boundary instead of that stale viewport.
        QueuePositionIfVisible(forceResize: true);
    }

    private void OnPanelRevealed(bool raisePanelShown)
    {
        if (raisePanelShown && !exiting && visibility.DesiredVisible)
        {
            PanelShown?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task WaitForFirstPresentationReadyAsync(CancellationToken cancellationToken)
    {
        await WaitForContentLoadedAsync(cancellationToken);
        TraceFirstPresentation("ContentRoot Loaded");

        var firstRendering = true;
        do
        {
            await WaitForRenderingAsync(cancellationToken);
            if (firstRendering)
            {
                firstRendering = false;
                TraceFirstPresentation("first post-Loaded Rendering");
            }
        }
        while (ContentRoot.ActualWidth <= 0
            || ContentRoot.ActualHeight <= 0
            || PanelContent.ActualWidth <= 0
            || PanelContent.ActualHeight <= 0);

        cancellationToken.ThrowIfCancellationRequested();
        var flushResult = NativeMethods.DwmFlush();
        TraceFirstPresentation($"DwmFlush complete hresult=0x{flushResult:X8}");
    }

    private Task WaitForContentLoadedAsync(CancellationToken cancellationToken)
    {
        if (ContentRoot.IsLoaded)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            completion.TrySetResult();
        };
        ContentRoot.Loaded += loaded;
        if (ContentRoot.IsLoaded)
        {
            completion.TrySetResult();
        }

        return AwaitLoadedAsync(completion, loaded, cancellationToken);
    }

    private async Task AwaitLoadedAsync(
        TaskCompletionSource completion,
        RoutedEventHandler loaded,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            await completion.Task;
        }
        finally
        {
            ContentRoot.Loaded -= loaded;
        }
    }

    private static async Task WaitForRenderingAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object>? rendering = null;
        rendering = (_, _) =>
        {
            completion.TrySetResult();
        };
        CompositionTarget.Rendering += rendering;
        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            await completion.Task;
        }
        finally
        {
            CompositionTarget.Rendering -= rendering;
        }
    }

    private bool SetFirstPresentationCloaked(bool value)
    {
        var enabled = value ? 1 : 0;
        var result = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DwmwaCloak,
            ref enabled,
            sizeof(int));
        TraceFirstPresentation(value
            ? $"cloak hresult=0x{result:X8}"
            : $"uncloak hresult=0x{result:X8}");
        if (!value)
        {
            firstPresentationStopwatch?.Stop();
            FlushFirstPresentationTiming();
        }

        return result == 0;
    }

    [Conditional("DEBUG")]
    private void TraceFirstPresentation(string message)
    {
#if DEBUG
        if (firstPresentationStopwatch is { IsRunning: true } stopwatch)
        {
            var line = $"pid={Environment.ProcessId} +{stopwatch.ElapsedMilliseconds}ms {message}";
            Debug.WriteLine($"First presentation {line}");
            firstPresentationTiming.Add(line);
        }
#endif
    }

    [Conditional("DEBUG")]
    private void FlushFirstPresentationTiming()
    {
#if DEBUG
        var lines = firstPresentationTiming.ToArray();
        _ = Task.Run(() =>
        {
            try
            {
                File.AppendAllLines(
                    Path.Combine(Path.GetTempPath(), "CodexQuotaTray-first-presentation.log"),
                    lines.Append(string.Empty));
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                Debug.WriteLine($"First presentation timing log failed: {error.GetType().Name}");
            }
        });
#endif
    }

    internal void ApplyTheme(ThemeMode mode)
    {
        ContentRoot.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        _ = WindowIconService.TrySetIcon(
            appWindow,
            mode == ThemeMode.Dark
                || mode == ThemeMode.System && ContentRoot.ActualTheme == ElementTheme.Dark);
        if (visibility.DesiredVisible)
        {
            ApplyBackdrop();
        }
    }

    private void QueuePositionIfVisible(bool forceResize = false)
    {
        if (!visibility.DesiredVisible)
        {
            return;
        }

        forcePositionQueued |= forceResize;
        if (positionQueued)
        {
            return;
        }

        positionQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            positionQueued = false;
            var shouldForceResize = forcePositionQueued;
            forcePositionQueued = false;
            if (visibility.DesiredVisible)
            {
                Position(shouldForceResize);
            }
        }))
        {
            positionQueued = false;
            forcePositionQueued = false;
        }
    }

    private void Position(bool forceResize = false)
    {
        var scale = ContentRoot.XamlRoot?.RasterizationScale
            ?? WindowPlacementService.GetRasterizationScale(hwnd);
        PanelContent.InvalidateMeasure();
        PanelContent.Measure(new Windows.Foundation.Size(PanelWidthDips, double.PositiveInfinity));
        ContentRoot.UpdateLayout();
        var fallbackHeight = Math.Max(1, Math.Ceiling(PanelContent.DesiredSize.Height));
        var measuredHeight = MeasureVisibleContentHeight(fallbackHeight);
        if (hasSessionPosition)
        {
            placement.ResizeAndKeepPosition(appWindow, scale, measuredHeight, forceResize);
            return;
        }

        placement.ResizeAndPlaceInitial(appWindow, scale, measuredHeight, TrayRectangleProvider());
        hasSessionPosition = true;
    }

    private double MeasureVisibleContentHeight(double fallbackHeight)
        => fallbackHeight;

    private void OnQuotaTabClick(object sender, RoutedEventArgs args) => _ = ShowPageAsync(showToken: false);

    private void OnTokenTabClick(object sender, RoutedEventArgs args)
    {
        _ = EnsureTokenUsageView();
        if (!tokenUsageViewModel.HasLoaded && !tokenUsageViewModel.IsRefreshing)
        {
            _ = tokenUsageViewModel.RefreshCommand.ExecuteAsync(null);
        }

        _ = ShowPageAsync(showToken: true);
    }

    private async Task ShowPageAsync(bool showToken)
    {
        var tokenView = showToken ? EnsureTokenUsageView() : tokenUsageView;
        if (showingTokenPage == showToken)
        {
            UpdateTabSelectionVisuals();
            return;
        }

        var revision = Interlocked.Increment(ref pageTransitionRevision);
        pageTransitionRunning = true;
        showingTokenPage = showToken;
        UpdateTabSelectionVisuals();

        var incoming = showToken ? TokenPageHost : QuotaPageHost;
        var outgoing = showToken ? QuotaPageHost : TokenPageHost;
        if (!showToken)
        {
            tokenView?.ResetHeatmapInteraction();
        }

        var startHeight = PageHost.ActualHeight > 0
            ? PageHost.ActualHeight
            : Math.Max(1, outgoing.ActualHeight);
        PageHost.Height = startHeight;

        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        outgoing.Visibility = Visibility.Collapsed;
        outgoing.IsHitTestVisible = false;
        if (showToken)
        {
            tokenView!.PrepareHeatmapInteraction();
        }

        var availableWidth = PageHost.ActualWidth > 0
            ? PageHost.ActualWidth
            : PanelWidthDips - 36;
        incoming.Measure(new Windows.Foundation.Size(
            availableWidth,
            double.PositiveInfinity));
        var targetHeight = Math.Max(1, Math.Ceiling(incoming.DesiredSize.Height));
        UpdateHeaderForSelectedPage();

        if (!uiSettings.AnimationsEnabled)
        {
            CompletePageSwitch(incoming, outgoing, revision);
            return;
        }

        ContentRoot.UpdateLayout();
        if (!await AnimatePageHeightAsync(startHeight, targetHeight, revision))
        {
            return;
        }

        CompletePageSwitch(incoming, outgoing, revision);
    }

    private async Task<bool> AnimatePageHeightAsync(double startHeight, double targetHeight, long revision)
    {
        var stopwatch = Stopwatch.StartNew();
        var duration = PageResizeDuration;
        while (stopwatch.Elapsed < duration)
        {
            if (revision != Volatile.Read(ref pageTransitionRevision))
            {
                return false;
            }

            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            PageHost.Height = PopupPlacement.InterpolateContentHeight(startHeight, targetHeight, progress);
            Position(forceResize: true);
            await Task.Delay(TimeSpan.FromMilliseconds(16));
        }

        if (revision != Volatile.Read(ref pageTransitionRevision))
        {
            return false;
        }

        PageHost.Height = targetHeight;
        Position(forceResize: true);
        return true;
    }

    private void CompletePageSwitch(Grid incoming, Grid outgoing, long revision)
    {
        if (revision != Volatile.Read(ref pageTransitionRevision))
        {
            return;
        }

        outgoing.Visibility = Visibility.Collapsed;
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        PageHost.Height = double.NaN;
        pageTransitionRunning = false;
        QueuePositionIfVisible(forceResize: true);
    }

    private void UpdateHeaderForSelectedPage()
    {
        HeaderStatusText.DataContext = showingTokenPage ? tokenUsageViewModel : viewModel;
        RefreshButton.DataContext = showingTokenPage ? tokenUsageViewModel : viewModel;
        var refreshName = showingTokenPage ? "刷新统计" : "刷新额度";
        ToolTipService.SetToolTip(RefreshButton, refreshName);
        AutomationProperties.SetName(RefreshButton, refreshName);
    }

    private TokenUsageView EnsureTokenUsageView()
    {
        if (tokenUsageView is not null)
        {
            return tokenUsageView;
        }

        tokenUsageView = new TokenUsageView(tokenUsageViewModel, hwnd);
        TokenPageHost.Children.Add(tokenUsageView);
        return tokenUsageView;
    }

    private void OnTabSelectorSizeChanged(object sender, SizeChangedEventArgs args) => UpdateTabSelectionVisuals();

    private void UpdateTabSelectionVisuals()
    {
        QuotaTabButton.IsChecked = !showingTokenPage;
        TokenTabButton.IsChecked = showingTokenPage;
        var tokenOffset = (float)(QuotaTabButton.ActualWidth + TabSelectorGrid.ColumnSpacing);
        TabSelectionPill.Translation = new Vector3(showingTokenPage ? tokenOffset : 0f, 0f, 0f);
    }

    private static Brush ResolveThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    private void ApplyBackdrop()
    {
        var selected = backdrop.Apply(this);
        PanelSurface.Background = ResolveThemeBrush(
            selected == CodexQuotaTray.Core.Models.BackdropKind.Opaque
                ? "MainWindowOpaqueSurfaceBrush"
                : "MainWindowSurfaceBrush");
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            tokenUsageView?.ResetHeatmapInteraction();
            return;
        }

        _ = WindowIconService.TrySetIcon(appWindow, ContentRoot.ActualTheme == ElementTheme.Dark);
        if (visibility.DesiredVisible)
        {
            ApplyBackdrop();
            if (showingTokenPage)
            {
                tokenUsageView?.PrepareHeatmapInteraction();
            }
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exiting)
        {
            return;
        }

        if (!trayAvailable)
        {
            args.Cancel = true;
            ExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        args.Cancel = true;
        HidePanel();
    }
}
