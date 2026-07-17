[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot "..\target\debug\codex-quota-tray-gui.exe")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class CodexDebugCapture
{
    const uint INFINITE = 0xffffffff;
    const uint WAIT_OBJECT_0 = 0;
    const uint FILE_MAP_READ = 0x0004;
    const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMapping(IntPtr file, IntPtr attributes, uint protect, uint high, uint low, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMapping(uint access, bool inheritHandle, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr handle);

    static readonly ConcurrentQueue<string> Lines = new ConcurrentQueue<string>();
    static IntPtr bufferReady;
    static IntPtr dataReady;
    static IntPtr mapping;
    static IntPtr view;
    static Thread worker;
    static volatile bool stopping;

    public static void Start()
    {
        bufferReady = CreateEvent(IntPtr.Zero, false, false, "DBWIN_BUFFER_READY");
        dataReady = CreateEvent(IntPtr.Zero, false, false, "DBWIN_DATA_READY");
        mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE, 0, 4096, "DBWIN_BUFFER");
        if (bufferReady == IntPtr.Zero || dataReady == IntPtr.Zero || mapping == IntPtr.Zero)
            throw new InvalidOperationException("DBWIN setup failed: " + Marshal.GetLastWin32Error());
        view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, new UIntPtr(4096));
        if (view == IntPtr.Zero)
            throw new InvalidOperationException("DBWIN map failed: " + Marshal.GetLastWin32Error());
        stopping = false;
        worker = new Thread(ReadLoop) { IsBackground = true };
        worker.Start();
    }

    static void ReadLoop()
    {
        SetEvent(bufferReady);
        while (!stopping)
        {
            if (WaitForSingleObject(dataReady, 250) != WAIT_OBJECT_0) continue;
            if (stopping) break;
            int pid = Marshal.ReadInt32(view);
            IntPtr textAddress = IntPtr.Add(view, 4);
            string text = Marshal.PtrToStringAnsi(textAddress) ?? "";
            Lines.Enqueue(pid + ": " + text.TrimEnd('\0', '\r', '\n'));
            SetEvent(bufferReady);
        }
    }

    public static string[] Stop()
    {
        stopping = true;
        SetEvent(bufferReady);
        worker?.Join(1000);
        if (view != IntPtr.Zero) UnmapViewOfFile(view);
        if (mapping != IntPtr.Zero) CloseHandle(mapping);
        if (bufferReady != IntPtr.Zero) CloseHandle(bufferReady);
        if (dataReady != IntPtr.Zero) CloseHandle(dataReady);
        return Lines.ToArray();
    }

    public static string[] Snapshot() { return Lines.ToArray(); }
}

public static class CodexWindowControl
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string title);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);
}
"@

$exe = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "debug executable not found: $exe" }

[CodexDebugCapture]::Start()
$process = Start-Process -FilePath $exe -ArgumentList "--demo" -PassThru
try {
    $main = [IntPtr]::Zero
    $tray = [IntPtr]::Zero
    for ($i = 0; $i -lt 40 -and ($main -eq [IntPtr]::Zero -or $tray -eq [IntPtr]::Zero); $i++) {
        foreach ($line in [CodexDebugCapture]::Snapshot()) {
            if ($main -eq [IntPtr]::Zero -and $line -match 'WM_SETICON hwnd=(\d+)') {
                $main = [IntPtr]::new([int64]$Matches[1])
            }
            if ($tray -eq [IntPtr]::Zero -and $line -match 'tray_message_hwnd created hwnd=(\d+)') {
                $tray = [IntPtr]::new([int64]$Matches[1])
            }
        }
        if ($main -eq [IntPtr]::Zero) {
            $foundMain = [CodexWindowControl]::FindWindow("CodexQuotaTrayWindow", $null)
            if ($foundMain -eq [IntPtr]::Zero) {
                $foundMain = [CodexWindowControl]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, "CodexQuotaTrayWindow", $null)
            }
            if ($foundMain -ne [IntPtr]::Zero) { $main = $foundMain }
        }
        if ($tray -eq [IntPtr]::Zero) {
            $foundTray = [CodexWindowControl]::FindWindowEx([IntPtr]::new(-3), [IntPtr]::Zero, "CodexQuotaTrayMessageWindow", $null) # HWND_MESSAGE
            if ($foundTray -eq [IntPtr]::Zero) {
                $foundTray = [CodexWindowControl]::FindWindowEx([IntPtr]::Zero, [IntPtr]::Zero, "CodexQuotaTrayMessageWindow", $null)
            }
            if ($foundTray -ne [IntPtr]::Zero) { $tray = $foundTray }
        }
        Start-Sleep -Milliseconds 50
    }
    if ($main -eq [IntPtr]::Zero -or $tray -eq [IntPtr]::Zero) {
        throw "could not find both Codex windows (main=$($main.ToInt64()) tray=$($tray.ToInt64()) pid=$($process.Id))"
    }

    # Demo mode starts visible. Hide through the normal explicit close path, then exercise one
    # real tray callback for opening and one for closing.
    [CodexWindowControl]::PostMessage($main, 0x0010, [UIntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 150
    [CodexWindowControl]::PostMessage($tray, 0x8000 + 17, [UIntPtr]::Zero, [IntPtr]0x0202) | Out-Null
    Start-Sleep -Milliseconds 250
    [CodexWindowControl]::PostMessage($tray, 0x8000 + 17, [UIntPtr]::Zero, [IntPtr]0x0202) | Out-Null
    Start-Sleep -Milliseconds 250

    # Explicit process exit keeps the capture finite and uses the existing shutdown path.
    [CodexWindowControl]::PostMessage($main, 0x8000 + 21, [UIntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $process.WaitForExit(3000) | Out-Null
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    [CodexDebugCapture]::Stop() | ForEach-Object { $_ }
}
