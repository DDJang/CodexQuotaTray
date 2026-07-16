[CmdletBinding()]
param(
    [switch]$KeepUserData,
    [string]$RunRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-SafeRegistryPath([string]$Path) {
    if (-not $Path.StartsWith("HKCU:\Software\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "the run registry path must remain under HKCU:\Software"
    }
}

function Assert-ExactChild([string]$Parent, [string]$Child, [string]$ExpectedLeaf) {
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $childPath = [IO.Path]::GetFullPath($Child)
    if ([IO.Path]::GetFileName($childPath) -ne $ExpectedLeaf -or
        [IO.Path]::GetDirectoryName($childPath) -ne $parentPath) {
        throw "refusing to remove an unexpected directory"
    }
    if (Test-Path -LiteralPath $childPath) {
        $item = Get-Item -LiteralPath $childPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "refusing to remove a reparse-point directory"
        }
    }
}

function Remove-WithRetry([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        } catch {
            if ($attempt -eq 9) {
                throw
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

Assert-SafeRegistryPath $RunRegistryPath
$localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$programsRoot = Join-Path $localRoot "Programs"
$installDir = Join-Path $programsRoot "CodexQuotaTray"
$dataDir = Join-Path $localRoot "CodexQuotaTray"
Assert-ExactChild $programsRoot $installDir "CodexQuotaTray"
Assert-ExactChild $localRoot $dataDir "CodexQuotaTray"

$targetExe = Join-Path $installDir "codex-quota-tray-gui.exe"
if (Test-Path -LiteralPath $targetExe -PathType Leaf) {
    $shutdown = Start-Process -FilePath $targetExe -ArgumentList "--shutdown-existing" `
        -Wait -WindowStyle Hidden -PassThru
    if ($shutdown.ExitCode -ne 0) {
        throw "The existing CodexQuotaTray process did not shut down cleanly"
    }
}

Remove-ItemProperty -Path $RunRegistryPath -Name "CodexQuotaTray" -ErrorAction SilentlyContinue
$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\CodexQuotaTray.lnk"
Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction SilentlyContinue

if (-not $KeepUserData) {
    Remove-WithRetry $dataDir
}
Remove-WithRetry $installDir

Write-Output "CodexQuotaTray was removed for the current user"
