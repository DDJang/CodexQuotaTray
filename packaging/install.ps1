[CmdletBinding()]
param(
    [switch]$StartWithWindows,
    [switch]$NoLaunch,
    [string]$RunRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-SafeRegistryPath([string]$Path) {
    if (-not $Path.StartsWith("HKCU:\Software\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "the run registry path must remain under HKCU:\Software"
    }
}

function Assert-NormalDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        $item = Get-Item -LiteralPath $Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "refusing to install through a reparse point"
        }
    }
}

function Test-PackageManifest([string]$Root) {
    $manifest = Join-Path $Root "MANIFEST.sha256"
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
        throw "MANIFEST.sha256 is missing"
    }
    $manifestNames = @()
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -notmatch '^([0-9a-fA-F]{64})  ([A-Za-z0-9_.-]+)$') {
            throw "the package manifest contains an invalid entry"
        }
        $expected = $Matches[1].ToLowerInvariant()
        $name = $Matches[2]
        $manifestNames += $name
        $candidate = Join-Path $Root $name
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "the package is incomplete"
        }
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "package integrity verification failed"
        }
    }
    $requiredNames = @(
        "codex-quota-tray-gui.exe",
        "DEPENDENCIES.md",
        "install.ps1",
        "LICENSE",
        "PRIVACY.md",
        "README.md",
        "THIRD_PARTY_NOTICES.txt",
        "uninstall.ps1",
        "VERSION"
    )
    if (@(Compare-Object ($requiredNames | Sort-Object) ($manifestNames | Sort-Object)).Count -ne 0) {
        throw "the package manifest allowlist is incomplete or unexpected"
    }
}

function Copy-WithRetry([string]$Source, [string]$Destination) {
    $temporary = "$Destination.new"
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $temporary -Force
            Move-Item -LiteralPath $temporary -Destination $Destination -Force
            return
        } catch {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
            if ($attempt -eq 9) {
                throw
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Major -lt 10) {
    throw "CodexQuotaTray requires 64-bit Windows 10 or Windows 11"
}
Assert-SafeRegistryPath $RunRegistryPath
$packageRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
Test-PackageManifest $packageRoot

$localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$programsRoot = Join-Path $localRoot "Programs"
$installDir = Join-Path $programsRoot "CodexQuotaTray"
Assert-NormalDirectory $programsRoot
Assert-NormalDirectory $installDir
$targetExe = Join-Path $installDir "codex-quota-tray-gui.exe"

if (Test-Path -LiteralPath $targetExe -PathType Leaf) {
    $shutdown = Start-Process -FilePath $targetExe -ArgumentList "--shutdown-existing" `
        -Wait -WindowStyle Hidden -PassThru
    if ($shutdown.ExitCode -ne 0) {
        throw "The existing CodexQuotaTray process did not shut down cleanly"
    }
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$installedFiles = @(
    "codex-quota-tray-gui.exe",
    "install.ps1",
    "uninstall.ps1",
    "README.md",
    "LICENSE",
    "PRIVACY.md",
    "DEPENDENCIES.md",
    "THIRD_PARTY_NOTICES.txt",
    "MANIFEST.sha256",
    "VERSION"
)
foreach ($name in $installedFiles) {
    Copy-WithRetry (Join-Path $packageRoot $name) (Join-Path $installDir $name)
}

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
New-Item -ItemType Directory -Path $startMenuDir -Force | Out-Null
$shortcutPath = Join-Path $startMenuDir "CodexQuotaTray.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $targetExe
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$targetExe,0"
$shortcut.Save()

if ($StartWithWindows) {
    New-Item -Path $RunRegistryPath -Force | Out-Null
    $runCommand = '"' + $targetExe + '"'
    New-ItemProperty -Path $RunRegistryPath -Name "CodexQuotaTray" -Value $runCommand `
        -PropertyType String -Force | Out-Null
}

if (-not $NoLaunch) {
    Start-Process -FilePath $targetExe -WindowStyle Hidden | Out-Null
}

Write-Output "CodexQuotaTray installed for the current user at $installDir"
