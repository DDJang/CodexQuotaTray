[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [ValidateRange(1, 1000)]
    [int]$Cycles = 100
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
    [StructLayout(LayoutKind.Sequential)]
    public struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid GuidItem;
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

    public static int GetIconRect(IntPtr callbackWindow, out NativeRect iconLocation)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = callbackWindow,
            Id = 0x51435452,
            GuidItem = new Guid("8F4F2C19-0C4C-4E1B-8F5C-50D0F1A4A77D"),
        };
        return ShellNotifyIconGetRect(ref identifier, out iconLocation);
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
    $process = Start-Process `
        -FilePath $resolvedExecutable `
        -ArgumentList "--demo", "--isolated-preview-data" `
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
        $result = [CodexQuotaTraySmokeNative]::GetIconRect($callbackWindow, [ref]$rect)
        $lastResult = $result
        if ($result -ge 0 -and $rect.Right -gt $rect.Left -and $rect.Bottom -gt $rect.Top) {
            $iconRect = $rect
            break
        }
    } while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)

    if ($null -eq $iconRect) {
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
        throw "Explorer did not confirm a non-empty CodexQuotaTray notification icon rectangle " +
            "(callback=0x$($callbackWindow.ToInt64().ToString('X')), result=$resultText, process=$processText)."
    }

    $mainWindow = $process.MainWindowHandle
    if ($mainWindow -eq [IntPtr]::Zero) {
        throw "The WinUI panel HWND was not available."
    }

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
        CallbackWindow = "0x{0:X}" -f $callbackWindow.ToInt64()
        IconRectangle = "$($iconRect.Left),$($iconRect.Top),$($iconRect.Right),$($iconRect.Bottom)"
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
