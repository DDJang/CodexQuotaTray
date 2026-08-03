using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Models;
using CodexQuotaTray.Core.Presentation;
using Microsoft.UI.Dispatching;

namespace CodexQuotaTray.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint OpenCommand = 1;
    private const uint SettingsCommand = 2;
    private const uint ExitCommand = 3;
    private const uint TrayId = 0x51435452;
    private static readonly Guid TrayGuid = new("8F4F2C19-0C4C-4E1B-8F5C-50D0F1A4A77D");
    private static readonly Dictionary<IntPtr, TrayIconService> Instances = [];
    private static readonly NativeMethods.WindowProcedure SharedWindowProcedure = WindowProcedure;
    private readonly DispatcherQueue dispatcher;
    private readonly Action toggleWindow;
    private readonly Action showWindow;
    private readonly Action openSettings;
    private readonly Action resume;
    private readonly Action exitApplication;
    private const string TrayCallbackWindowClassName = "CodexQuotaTray.Tray.CallbackWindow";
    private const string TrayBroadcastWindowClassName = "CodexQuotaTray.Tray.BroadcastWindow";
    private IntPtr instance;
    private IntPtr callbackWindow;
    private IntPtr broadcastWindow;
    private IntPtr icon;
    private uint taskbarCreatedMessage;
    private volatile bool added;
    private bool disposed;
    private readonly CancellationTokenSource retryLifetime = new();
    private readonly object iconRectGate = new();
    private Rectangle? cachedIconRect;
    private int registrationGeneration;
    private int registrationAttempt;
    private int lastExplorerResult;
    private bool explorerConfirmed;

    internal TrayRegistrationState RegistrationState { get; private set; } = TrayRegistrationState.NotStarted;

    internal int? LastRegistrationError { get; private set; }

    internal bool TaskbarCreatedObserved { get; private set; }

    internal event EventHandler<TrayRegistrationState>? RegistrationStateChanged;

    internal TrayIconService(
        DispatcherQueue dispatcher,
        Action toggleWindow,
        Action showWindow,
        Action openSettings,
        Action resume,
        Action exitApplication)
    {
        this.dispatcher = dispatcher;
        this.toggleWindow = toggleWindow;
        this.showWindow = showWindow;
        this.openSettings = openSettings;
        this.resume = resume;
        this.exitApplication = exitApplication;
    }

    internal void Start()
    {
        instance = NativeMethods.GetModuleHandle(null);
        RegisterWindowClass(TrayCallbackWindowClassName);
        RegisterWindowClass(TrayBroadcastWindowClassName);

        // The notification icon callback HWND is message-only. It cannot take focus,
        // appear in Alt+Tab, or accidentally become a second application window.
        callbackWindow = NativeMethods.CreateWindowEx(
            0,
            TrayCallbackWindowClassName,
            TrayCallbackWindowClassName,
            NativeMethods.WsPopup,
            0,
            0,
            0,
            0,
            NativeMethods.HwndMessage,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (callbackWindow == IntPtr.Zero)
        {
            throw LastWin32("create tray callback window");
        }

        // Message-only windows do not receive broadcast messages. Keep a separate,
        // never-shown tool window for TaskbarCreated and power notifications.
        broadcastWindow = NativeMethods.CreateWindowEx(
            NativeMethods.WsExToolWindow,
            TrayBroadcastWindowClassName,
            TrayBroadcastWindowClassName,
            NativeMethods.WsPopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (broadcastWindow == IntPtr.Zero)
        {
            throw LastWin32("create tray broadcast window");
        }

        Instances.Add(callbackWindow, this);
        Instances.Add(broadcastWindow, this);
        taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        var executable = Environment.ProcessPath;
        var smallIcons = new IntPtr[1];
        if (string.IsNullOrWhiteSpace(executable)
            || NativeMethods.ExtractIconEx(executable, 0, null, smallIcons, 1) != 1
            || smallIcons[0] == IntPtr.Zero)
        {
            throw LastWin32("extract the embedded tray icon");
        }

        icon = smallIcons[0];
        BeginRegistration();
    }

    private void RegisterWindowClass(string className)
    {
        var windowClass = new NativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProcedure = SharedWindowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw LastWin32($"register {className}");
        }
    }

    internal Rectangle? TryGetIconRect()
    {
        if (!added)
        {
            return null;
        }

        lock (iconRectGate)
        {
            return cachedIconRect;
        }
    }

    private void BeginRegistration()
    {
        if (disposed)
        {
            return;
        }

        registrationGeneration++;
        registrationAttempt = 0;
        explorerConfirmed = false;
        added = false;
        SetRegistrationState(TrayRegistrationState.RetryPending, null);
        ScheduleRegistrationAttempt(registrationGeneration);
    }

    private void ScheduleRegistrationAttempt(int generation)
    {
        if (disposed || generation != registrationGeneration)
        {
            return;
        }

        var delays = TrayRegistrationPolicy.RetryDelaysMilliseconds;
        if (registrationAttempt >= delays.Count)
        {
            SetRegistrationState(TrayRegistrationState.Failed, LastRegistrationError);
            return;
        }

        var attempt = registrationAttempt++;
        if (delays[attempt] == 0)
        {
            RunRegistrationAttempt(generation, attempt + 1);
            return;
        }

        _ = DelayThenEnqueueAsync(
            delays[attempt],
            generation,
            () => RunRegistrationAttempt(generation, attempt + 1));
    }

    private void RunRegistrationAttempt(int generation, int attemptNumber)
    {
        if (disposed || generation != registrationGeneration)
        {
            return;
        }

        _ = AddAndVerifyAsync(generation, attemptNumber, retryLifetime.Token);
    }

    private async Task AddAndVerifyAsync(
        int generation,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        (bool succeeded, int error) addResult;
        try
        {
            addResult = await Task.Run(
                () =>
                {
                    var succeeded = TryAddIcon(out var error);
                    return (succeeded, error);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            _ = dispatcher.TryEnqueue(() =>
            {
                if (disposed || generation != registrationGeneration)
                {
                    return;
                }

                LastRegistrationError = error.HResult;
                SetRegistrationState(
                    TrayRegistrationPolicy.StateAfterAttempt(false, attemptNumber),
                    LastRegistrationError);
                ScheduleRegistrationAttempt(generation);
            });
            return;
        }

        if (!addResult.succeeded)
        {
            _ = dispatcher.TryEnqueue(() =>
            {
                if (disposed || generation != registrationGeneration)
                {
                    return;
                }

                LastRegistrationError = addResult.error;
                ScheduleRegistrationAttempt(generation);
            });
            return;
        }

        await VerifyExplorerRegistrationAsync(generation, attemptNumber, cancellationToken).ConfigureAwait(false);
    }

    private bool TryAddIcon(out int error)
    {
        error = 0;
        var data = CreateData();
        // Clear a stale entry left by a previous host before reusing the stable
        // product GUID. This preserves the user's notification-area placement
        // while ensuring callbacks point at the current process.
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        data.Version = NativeMethods.NotifyIconVersion4;
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref data))
        {
            error = Marshal.GetLastWin32Error();
            _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
            return false;
        }

        added = true;
        return true;
    }

    private async Task VerifyExplorerRegistrationAsync(
        int generation,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in TrayRegistrationPolicy.VerificationDelaysMilliseconds)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (disposed || generation != registrationGeneration)
                {
                    return;
                }

                var identifier = new NativeMethods.NotifyIconIdentifier
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
                    Window = callbackWindow,
                    Id = TrayId,
                    GuidItem = TrayGuid,
                };
                var result = NativeMethods.ShellNotifyIconGetRect(ref identifier, out var rect);
                lastExplorerResult = result;
                if (!TrayRegistrationPolicy.IsExplorerConfirmationSuccessful(
                        result,
                        rect.Left,
                        rect.Top,
                        rect.Right,
                        rect.Bottom))
                {
                    continue;
                }

                _ = dispatcher.TryEnqueue(() =>
                {
                    if (disposed || generation != registrationGeneration)
                    {
                        return;
                    }

                    lock (iconRectGate)
                    {
                        cachedIconRect = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    }

                    explorerConfirmed = true;
                    SetRegistrationState(
                        TrayRegistrationPolicy.StateAfterAttempt(true, attemptNumber),
                        null);
                });
                return;
            }

            _ = dispatcher.TryEnqueue(() =>
            {
                if (disposed || generation != registrationGeneration)
                {
                    return;
                }

                DeleteIcon();
                LastRegistrationError = lastExplorerResult;
                SetRegistrationState(
                    TrayRegistrationPolicy.StateAfterAttempt(false, attemptNumber),
                    LastRegistrationError);
                ScheduleRegistrationAttempt(generation);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task DelayThenEnqueueAsync(int delay, int generation, Action action)
    {
        try
        {
            if (delay > 0)
            {
                await Task.Delay(delay, retryLifetime.Token).ConfigureAwait(false);
            }

            if (!disposed && generation == registrationGeneration)
            {
                _ = dispatcher.TryEnqueue(() => action());
            }
        }
        catch (OperationCanceledException) when (retryLifetime.IsCancellationRequested)
        {
        }
    }

    private void SetRegistrationState(TrayRegistrationState state, int? error)
    {
        RegistrationState = state;
        LastRegistrationError = error;
        RegistrationStateChanged?.Invoke(this, state);
    }

    private NativeMethods.NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = callbackWindow,
        Id = TrayId,
        Flags = NativeMethods.NifMessage
            | NativeMethods.NifIcon
            | NativeMethods.NifTip
            | NativeMethods.NifGuid
            | NativeMethods.NifShowTip,
        CallbackMessage = NativeMethods.TrayCallbackMessage,
        Icon = icon,
        Tip = "CodexQuotaTray",
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = TrayGuid,
    };

    private void HandleMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (hwnd == broadcastWindow
            && message == NativeMethods.WmPowerBroadcast
            && unchecked((uint)wParam.ToUInt64()) == NativeMethods.PbtApmResumeAutomatic)
        {
            _ = dispatcher.TryEnqueue(() => resume());
            return;
        }

        if (hwnd == broadcastWindow && message == taskbarCreatedMessage)
        {
            TaskbarCreatedObserved = true;
            added = false;
            explorerConfirmed = false;
            lock (iconRectGate)
            {
                cachedIconRect = null;
            }
            BeginRegistration();
            return;
        }

        if (hwnd != callbackWindow || message != NativeMethods.TrayCallbackMessage)
        {
            return;
        }

        var trayEvent = unchecked((uint)lParam.ToInt64()) & 0xffff;
        if (trayEvent == NativeMethods.WmLButtonUp)
        {
            _ = dispatcher.TryEnqueue(() => toggleWindow());
        }
        else if (trayEvent == NativeMethods.WmRButtonUp)
        {
            _ = dispatcher.TryEnqueue(ShowMenu);
        }
        else if (trayEvent == NativeMethods.NinBalloonUserClick)
        {
            _ = dispatcher.TryEnqueue(() => showWindow());
        }
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, OpenCommand, "打开面板");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, SettingsCommand, "设置");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfSeparator, UIntPtr.Zero, string.Empty);
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, ExitCommand, "退出");
            _ = NativeMethods.GetCursorPos(out var point);
            _ = NativeMethods.SetForegroundWindow(broadcastWindow);
            var command = unchecked((uint)NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCommand | NativeMethods.TpmNonotify,
                point.X,
                point.Y,
                0,
                broadcastWindow,
                IntPtr.Zero));
            _ = NativeMethods.PostMessage(broadcastWindow, NativeMethods.WmNull, UIntPtr.Zero, IntPtr.Zero);
            if (command == OpenCommand)
            {
                showWindow();
            }
            else if (command == SettingsCommand)
            {
                openSettings();
            }
            else if (command == ExitCommand)
            {
                exitApplication();
            }
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    internal void ShowQuotaAlert(QuotaAlert alert)
    {
        if (!added)
        {
            throw new InvalidOperationException("The tray icon is unavailable.");
        }

        var data = CreateData();
        data.Flags |= NativeMethods.NifInfo;
        data.InfoTitle = "Codex 额度提醒";
        data.Info = $"{alert.WindowName}剩余 {alert.RemainingPercent}%（已达到 {alert.Threshold}% 阈值）";
        data.InfoFlags = NativeMethods.NiifInfo;
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data))
        {
            throw LastWin32("show quota notification");
        }
    }

    internal void UpdateTooltip(string value)
    {
        if (!added)
        {
            return;
        }

        var data = CreateData();
        data.Tip = value.Length <= 127 ? value : value[..127];
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
    }

    internal string CreateDiagnosticText() => string.Join(
        Environment.NewLine,
        $"托盘注册状态: {RegistrationState}",
        $"托盘注册错误: {(LastRegistrationError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none")}",
        "托盘回调宿主: HWND_MESSAGE",
        "广播宿主: hidden tool window",
        $"Explorer 实际确认: {explorerConfirmed}",
        $"Explorer GetRect HRESULT: 0x{unchecked((uint)lastExplorerResult):X8}",
        $"Explorer 重建已观察: {TaskbarCreatedObserved}");

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var service))
        {
            service.HandleMessage(hwnd, message, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        retryLifetime.Cancel();
        registrationGeneration++;
        DeleteIcon();

        if (callbackWindow != IntPtr.Zero)
        {
            Instances.Remove(callbackWindow);
            _ = NativeMethods.DestroyWindow(callbackWindow);
            callbackWindow = IntPtr.Zero;
        }

        if (broadcastWindow != IntPtr.Zero)
        {
            Instances.Remove(broadcastWindow);
            _ = NativeMethods.DestroyWindow(broadcastWindow);
            broadcastWindow = IntPtr.Zero;
        }

        if (icon != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyIcon(icon);
            icon = IntPtr.Zero;
        }

        if (instance != IntPtr.Zero)
        {
            _ = NativeMethods.UnregisterClass(TrayCallbackWindowClassName, instance);
            _ = NativeMethods.UnregisterClass(TrayBroadcastWindowClassName, instance);
            instance = IntPtr.Zero;
        }


        retryLifetime.Dispose();
    }

    private static Win32Exception LastWin32(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Could not {operation}.");

    private void DeleteIcon()
    {
        if (!added || callbackWindow == IntPtr.Zero)
        {
            return;
        }

        var data = CreateData();
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
        added = false;
        explorerConfirmed = false;
        lock (iconRectGate)
        {
            cachedIconRect = null;
        }
    }
}
