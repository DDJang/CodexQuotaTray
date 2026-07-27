using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using CodexQuotaTray.App.Interop;
using CodexQuotaTray.Core.Alerts;
using CodexQuotaTray.Core.Persistence;
using CodexQuotaTray.Core.Runtime;
using Microsoft.UI.Dispatching;

namespace CodexQuotaTray.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private const uint OpenCommand = 1;
    private const uint RefreshCommand = 2;
    private const uint UsageCommand = 3;
    private const uint CopySummaryCommand = 4;
    private const uint CopyDiagnosticsCommand = 5;
    private const uint SettingsCommand = 6;
    private const uint AboutCommand = 7;
    private const uint ExitCommand = 8;
    private const uint AutoRefreshCommand = 20;
    private const uint Every5MinutesCommand = 21;
    private const uint Every15MinutesCommand = 22;
    private const uint Every30MinutesCommand = 23;
    private const uint ManualOnlyCommand = 24;
    private const uint Alert50Command = 30;
    private const uint Alert20Command = 31;
    private const uint Alert10Command = 32;
    private const uint StartupCommand = 40;
    private static readonly Guid TrayGuid = new("8F4F2C19-0C4C-4E1B-8F5C-50D0F1A4A77D");
    private static readonly Dictionary<IntPtr, TrayIconService> Instances = [];
    private static readonly NativeMethods.WindowProcedure SharedWindowProcedure = WindowProcedure;
    private readonly DispatcherQueue dispatcher;
    private readonly Action toggleWindow;
    private readonly Action showWindow;
    private readonly Action copyDiagnostics;
    private readonly Action refresh;
    private readonly Action openUsage;
    private readonly Action copySummary;
    private readonly Action openSettings;
    private readonly Action showAbout;
    private readonly Action resume;
    private readonly Func<AppSettings?> getSettings;
    private readonly Action<RefreshMode> setRefreshMode;
    private readonly Action<int> toggleAlert;
    private readonly Action toggleStartup;
    private readonly Action exitApplication;
    private readonly string className = $"CodexQuotaTray.Tray.{Environment.ProcessId}";
    private IntPtr instance;
    private IntPtr window;
    private IntPtr icon;
    private uint taskbarCreatedMessage;
    private bool added;

    internal TrayIconService(
        DispatcherQueue dispatcher,
        Action toggleWindow,
        Action showWindow,
        Action refresh,
        Action openUsage,
        Action copySummary,
        Action copyDiagnostics,
        Action openSettings,
        Action showAbout,
        Action resume,
        Func<AppSettings?> getSettings,
        Action<RefreshMode> setRefreshMode,
        Action<int> toggleAlert,
        Action toggleStartup,
        Action exitApplication)
    {
        this.dispatcher = dispatcher;
        this.toggleWindow = toggleWindow;
        this.showWindow = showWindow;
        this.refresh = refresh;
        this.openUsage = openUsage;
        this.copySummary = copySummary;
        this.copyDiagnostics = copyDiagnostics;
        this.openSettings = openSettings;
        this.showAbout = showAbout;
        this.resume = resume;
        this.getSettings = getSettings;
        this.setRefreshMode = setRefreshMode;
        this.toggleAlert = toggleAlert;
        this.toggleStartup = toggleStartup;
        this.exitApplication = exitApplication;
    }

    internal void Start()
    {
        instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new NativeMethods.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProcedure = SharedWindowProcedure,
            Instance = instance,
            ClassName = className,
        };
        if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
        {
            throw LastWin32("register tray message window");
        }

        window = NativeMethods.CreateWindowEx(
            0,
            className,
            className,
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw LastWin32("create tray message window");
        }

        Instances.Add(window, this);
        taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable.");
        var smallIcons = new IntPtr[1];
        if (NativeMethods.ExtractIconEx(executable, 0, null, smallIcons, 1) != 1 || smallIcons[0] == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not extract the embedded application icon.");
        }

        icon = smallIcons[0];

        AddIcon();
    }

    internal Rectangle? TryGetIconRect()
    {
        if (!added)
        {
            return null;
        }

        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
            Window = window,
            GuidItem = TrayGuid,
        };
        return NativeMethods.ShellNotifyIconGetRect(ref identifier, out var rect) >= 0
            ? Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : null;
    }

    private void AddIcon()
    {
        var data = CreateData();
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data))
        {
            throw LastWin32("add tray icon");
        }

        data.Version = NativeMethods.NotifyIconVersion4;
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimSetVersion, ref data))
        {
            _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
            throw LastWin32("set tray icon notification version");
        }

        added = true;
    }

    private NativeMethods.NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = window,
        Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifGuid,
        CallbackMessage = NativeMethods.TrayCallbackMessage,
        Icon = icon,
        Tip = "CodexQuotaTray",
        Info = string.Empty,
        InfoTitle = string.Empty,
        GuidItem = TrayGuid,
    };

    private void HandleMessage(uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (message == NativeMethods.WmPowerBroadcast
            && unchecked((uint)wParam.ToUInt64()) == NativeMethods.PbtApmResumeAutomatic)
        {
            _ = dispatcher.TryEnqueue(() => resume());
            return;
        }

        if (message == taskbarCreatedMessage)
        {
            added = false;
            try
            {
                AddIcon();
            }
            catch (Win32Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"Could not restore the tray icon: {error.NativeErrorCode}");
            }

            return;
        }

        if (message != NativeMethods.TrayCallbackMessage)
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
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, RefreshCommand, "刷新额度");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, UsageCommand, "官方用量页面");
            AddRefreshMenu(menu);
            AddAlertMenu(menu);
            var settings = getSettings();
            _ = NativeMethods.AppendMenu(
                menu,
                NativeMethods.MfString | (settings?.StartWithWindows == true ? NativeMethods.MfChecked : 0),
                StartupCommand,
                "开机启动");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, CopySummaryCommand, "复制额度摘要");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, CopyDiagnosticsCommand, "复制诊断信息");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, SettingsCommand, "设置");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, AboutCommand, "关于");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MfString, ExitCommand, "退出 CodexQuotaTray");
            _ = NativeMethods.GetCursorPos(out var point);
            _ = NativeMethods.SetForegroundWindow(window);
            var command = unchecked((uint)NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TpmRightButton | NativeMethods.TpmReturnCommand | NativeMethods.TpmNonotify,
                point.X,
                point.Y,
                0,
                window,
                IntPtr.Zero));
            _ = NativeMethods.PostMessage(window, NativeMethods.WmNull, UIntPtr.Zero, IntPtr.Zero);
            if (command == OpenCommand)
            {
                showWindow();
            }
            else if (command == RefreshCommand)
            {
                refresh();
            }
            else if (command == UsageCommand)
            {
                openUsage();
            }
            else if (command == CopySummaryCommand)
            {
                copySummary();
            }
            else if (command == CopyDiagnosticsCommand)
            {
                copyDiagnostics();
            }
            else if (command == SettingsCommand)
            {
                openSettings();
            }
            else if (command == AboutCommand)
            {
                showAbout();
            }
            else if (command is >= AutoRefreshCommand and <= ManualOnlyCommand)
            {
                setRefreshMode(command switch
                {
                    AutoRefreshCommand => RefreshMode.Auto,
                    Every5MinutesCommand => RefreshMode.Every5Minutes,
                    Every15MinutesCommand => RefreshMode.Every15Minutes,
                    Every30MinutesCommand => RefreshMode.Every30Minutes,
                    _ => RefreshMode.ManualOnly,
                });
            }
            else if (command is Alert50Command or Alert20Command or Alert10Command)
            {
                toggleAlert(command == Alert50Command ? 50 : command == Alert20Command ? 20 : 10);
            }
            else if (command == StartupCommand)
            {
                toggleStartup();
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

    private void AddRefreshMenu(IntPtr root)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        var current = getSettings()?.RefreshMode ?? RefreshMode.Auto;
        AppendChecked(menu, AutoRefreshCommand, "自动", current == RefreshMode.Auto);
        AppendChecked(menu, Every5MinutesCommand, "每 5 分钟", current == RefreshMode.Every5Minutes);
        AppendChecked(menu, Every15MinutesCommand, "每 15 分钟", current == RefreshMode.Every15Minutes);
        AppendChecked(menu, Every30MinutesCommand, "每 30 分钟", current == RefreshMode.Every30Minutes);
        AppendChecked(menu, ManualOnlyCommand, "仅手动", current == RefreshMode.ManualOnly);
        _ = NativeMethods.AppendMenu(root, NativeMethods.MfPopup, unchecked((UIntPtr)(nuint)menu), "刷新间隔");
    }

    private void AddAlertMenu(IntPtr root)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        var current = getSettings()?.EffectiveNotifications ?? new NotificationSettings();
        AppendChecked(menu, Alert50Command, "剩余 50%", current.Remaining50);
        AppendChecked(menu, Alert20Command, "剩余 20%", current.Remaining20);
        AppendChecked(menu, Alert10Command, "剩余 10%", current.Remaining10);
        _ = NativeMethods.AppendMenu(root, NativeMethods.MfPopup, unchecked((UIntPtr)(nuint)menu), "额度提醒");
    }

    private static void AppendChecked(IntPtr menu, uint command, string text, bool isChecked) =>
        _ = NativeMethods.AppendMenu(
            menu,
            NativeMethods.MfString | (isChecked ? NativeMethods.MfChecked : 0),
            command,
            text);

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

    private static IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out var service))
        {
            service.HandleMessage(message, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (added)
        {
            var data = CreateData();
            _ = NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
            added = false;
        }

        if (window != IntPtr.Zero)
        {
            Instances.Remove(window);
            _ = NativeMethods.DestroyWindow(window);
            window = IntPtr.Zero;
        }

        if (icon != IntPtr.Zero)
        {
            _ = NativeMethods.DestroyIcon(icon);
            icon = IntPtr.Zero;
        }

        if (instance != IntPtr.Zero)
        {
            _ = NativeMethods.UnregisterClass(className, instance);
            instance = IntPtr.Zero;
        }
    }

    private static Win32Exception LastWin32(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Could not {operation}.");
}
