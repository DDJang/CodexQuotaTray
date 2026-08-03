[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [ValidateRange(0, 1000)]
    [int]$Cycles = 100,
    [switch]$Production
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedExecutable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "WinUI executable not found: $resolvedExecutable"
}

$existingProcesses = @(Get-Process -Name "codex-quota-tray-gui" -ErrorAction SilentlyContinue)
if ($existingProcesses.Count -ne 0) {
    $pids = $existingProcesses | Select-Object -ExpandProperty Id
    throw "Close every running CodexQuotaTray instance before running the tray smoke. PIDs: $($pids -join ', ')"
}

$nativeSource = @'
using System;
using System.Runtime.InteropServices;

public static class CodexQuotaTraySmokeNative
{
    private const uint NimModify = 0x00000001;
    private const uint NifTip = 0x00000004;
    private const uint NifGuid = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string className,
        string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    public static extern int ShellNotifyIconGetRect(
        ref NotifyIconIdentifier identifier,
        out NativeRect iconLocation);

    public static IntPtr FindCallbackWindow() =>
        FindWindowEx(
            new IntPtr(-3),
            IntPtr.Zero,
            "CodexQuotaTray.Tray.CallbackWindow",
            null);

    public static int GetIconRect(IntPtr callbackWindow, Guid guid, out NativeRect iconLocation)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = callbackWindow,
            Id = 0x51435452,
            GuidItem = guid,
        };
        return ShellNotifyIconGetRect(ref identifier, out iconLocation);
    }

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    public static bool VerifyIcon(
        IntPtr callbackWindow,
        Guid guid,
        string tooltip,
        out int lastError)
    {
        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = callbackWindow,
            Id = 0x51435452,
            Flags = NifTip | NifGuid,
            Tip = tooltip,
            Info = string.Empty,
            InfoTitle = string.Empty,
            GuidItem = guid,
        };
        var succeeded = ShellNotifyIcon(NimModify, ref data);
        lastError = Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static bool PostLeftClick(IntPtr callbackWindow) =>
        PostMessage(callbackWindow, 0x8029, UIntPtr.Zero, new IntPtr(0x0202));

    public static bool WaitForVisibility(IntPtr window, bool expected, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        do
        {
            if (IsWindowVisible(window) == expected)
            {
                return true;
            }
            System.Threading.Thread.Sleep(10);
        }
        while (Environment.TickCount64 < deadline);
        return IsWindowVisible(window) == expected;
    }
}
'@

Add-Type -TypeDefinition $nativeSource

$process = $null
try {
    $launchArguments = if ($Production) { @() } else { @('--demo', '--isolated-preview-data') }
    $instanceKey = if ($Production) { 'CodexQuotaTray' } else { 'CodexQuotaTray.Preview' }
    $trayGuid = if ($Production) {
        '8F4F2C19-0C4C-4E1B-8F5C-50D0F1A4A77D'
    } else {
        '4B3F9C1D-6C21-4B9B-AFC7-31D8BAFE19E2'
    }
    $trayTooltip = if ($Production) { 'CodexQuotaTray' } else { 'CodexQuotaTray Preview' }
    $process = Start-Process `
        -FilePath $resolvedExecutable `
        -ArgumentList $launchArguments `
        -WindowStyle Hidden `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $callbackWindow = [IntPtr]::Zero
    $iconRect = $null
    $lastResult = $null
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        $callbackWindow = [CodexQuotaTraySmokeNative]::FindCallbackWindow()
        if ($callbackWindow -eq [IntPtr]::Zero) {
            continue
        }

        $rect = [CodexQuotaTraySmokeNative+NativeRect]::new()
        $result = [CodexQuotaTraySmokeNative]::GetIconRect($callbackWindow, [Guid]$trayGuid, [ref]$rect)
        $lastResult = $result
        if ($result -ge 0 -and $rect.Right -gt $rect.Left -and $rect.Bottom -gt $rect.Top) {
            $iconRect = $rect
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)

    if ($callbackWindow -eq [IntPtr]::Zero) {
        $resultText = if ($null -eq $lastResult) {
            "not-called"
        } else {
            "0x{0:X8}" -f ($lastResult -band 0xFFFFFFFFL)
        }
        $processText = if ($process.HasExited) {
            "exited:$($process.ExitCode)"
        } else {
            "running:$($process.Id)"
        }
        throw "CodexQuotaTray notification callback window was not created " +
            "(callback=0x$($callbackWindow.ToInt64().ToString('X')), GetRectHResult=$resultText, ProcessState=$processText)."
    }

    $mainWindow = $process.MainWindowHandle
    if ($mainWindow -eq [IntPtr]::Zero) {
        throw "The WinUI panel HWND was not available."
    }

    $modifyLastError = 0
    $modifySucceeded = [CodexQuotaTraySmokeNative]::VerifyIcon(
        $callbackWindow,
        [Guid]$trayGuid,
        $trayTooltip,
        [ref]$modifyLastError)

    for ($index = 0; $index -lt $Cycles; $index++) {
        if (-not [CodexQuotaTraySmokeNative]::PostLeftClick($callbackWindow)) {
            throw "Could not post tray click $($index * 2 + 1)."
        }
        if (-not [CodexQuotaTraySmokeNative]::WaitForVisibility($mainWindow, $false, 1000)) {
            throw "Tray click $($index * 2 + 1) did not hide the panel."
        }

        if (-not [CodexQuotaTraySmokeNative]::PostLeftClick($callbackWindow)) {
            throw "Could not post tray click $($index * 2 + 2)."
        }
        if (-not [CodexQuotaTraySmokeNative]::WaitForVisibility($mainWindow, $true, 1000)) {
            throw "Tray click $($index * 2 + 2) did not show the panel."
        }
    }

    [pscustomobject]@{
        Executable = $resolvedExecutable
        ProcessId = $process.Id
        InstanceKey = $instanceKey
        TrayGuid = $trayGuid
        TrayTooltip = $trayTooltip
        RegistrationState = if ($modifySucceeded) { 'Registered (NIM_MODIFY succeeded)' } else { 'Not confirmed (NIM_MODIFY failed)' }
        NimModifySucceeded = $modifySucceeded
        NimModifyLastError = "0x{0:X8}" -f ($modifyLastError -band 0xFFFFFFFFL)
        CallbackWindow = "0x{0:X}" -f $callbackWindow.ToInt64()
        IconRectangle = if ($null -eq $iconRect) { "unavailable (0x{0:X8})" -f ($lastResult -band 0xFFFFFFFFL) } else { "$($iconRect.Left),$($iconRect.Top),$($iconRect.Right),$($iconRect.Bottom)" }
        ToggleCycles = $Cycles
        Result = "ok"
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(3000) | Out-Null
    }
}
