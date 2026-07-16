[CmdletBinding()]
param(
    [string]$Cargo = "cargo"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-ChildPath([string]$Parent, [string]$Child) {
    $parentPath = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $childPath = [IO.Path]::GetFullPath($Child)
    if (-not $childPath.StartsWith(
            $parentPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "packaging smoke path escaped the project target directory"
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$targetRoot = Join-Path $repoRoot "target"
$smokeRoot = Join-Path $targetRoot "packaging-smoke-$PID"
Assert-ChildPath $targetRoot $smokeRoot
$oldLocalAppData = $env:LOCALAPPDATA
$oldAppData = $env:APPDATA
$registryPath = "HKCU:\Software\CodexQuotaTray\PackagingSmoke-$PID"

try {
    if (Test-Path -LiteralPath $smokeRoot) {
        $item = Get-Item -LiteralPath $smokeRoot -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "refusing to replace a reparse-point smoke directory"
        }
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null

    $packageOutput = & (Join-Path $repoRoot "scripts\package.ps1") -Cargo $Cargo
    $packageResult = $packageOutput | Where-Object { $_.PSObject.Properties.Name -contains "Archive" } |
        Select-Object -Last 1
    Assert-True ($null -ne $packageResult) "package script did not return artifact metadata"
    $archive = [string]$packageResult.Archive
    Assert-True (Test-Path -LiteralPath $archive -PathType Leaf) "release archive is missing"
    Assert-True ((Get-Item -LiteralPath $archive).Length -lt 20MB) "release archive exceeds 20 MB"

    $expanded = Join-Path $smokeRoot "package"
    Expand-Archive -LiteralPath $archive -DestinationPath $expanded -Force
    Assert-True (-not (Get-ChildItem -LiteralPath $expanded -Directory)) `
        "release archive unexpectedly contains nested directories"
    $expectedFiles = @(
        "codex-quota-tray-gui.exe",
        "DEPENDENCIES.md",
        "install.ps1",
        "LICENSE",
        "MANIFEST.sha256",
        "PRIVACY.md",
        "README.md",
        "THIRD_PARTY_NOTICES.txt",
        "uninstall.ps1",
        "VERSION"
    )
    $actualFiles = @(Get-ChildItem -LiteralPath $expanded -File | ForEach-Object Name | Sort-Object)
    $difference = @(Compare-Object ($expectedFiles | Sort-Object) $actualFiles)
    Assert-True ($difference.Count -eq 0) "release archive file allowlist changed"
    Assert-True ((Get-Item -LiteralPath (Join-Path $expanded "codex-quota-tray-gui.exe")).Length -lt 20MB) `
        "release executable exceeds 20 MB"
    $executableText = [Text.Encoding]::ASCII.GetString(
        [IO.File]::ReadAllBytes((Join-Path $expanded "codex-quota-tray-gui.exe"))
    )
    Assert-True (-not $executableText.Contains($repoRoot)) `
        "release executable contains the local repository path"
    Assert-True (-not $executableText.Contains($repoRoot.Replace('\', '/'))) `
        "release executable contains the normalized local repository path"

    $metadataText = & $Cargo metadata --format-version 1 --locked --offline `
        --filter-platform x86_64-pc-windows-msvc
    Assert-True ($LASTEXITCODE -eq 0) "could not inspect the locked dependency graph"
    $metadata = $metadataText | ConvertFrom-Json
    $rootPackage = $metadata.packages | Where-Object { $_.id -eq $metadata.resolve.root }
    $inventory = Get-Content -LiteralPath (Join-Path $expanded "DEPENDENCIES.md") -Raw
    $notices = Get-Content -LiteralPath (Join-Path $expanded "THIRD_PARTY_NOTICES.txt") -Raw
    foreach ($package in ($metadata.packages | Where-Object { $_.id -ne $rootPackage.id })) {
        $inventoryPattern = '\|\s*' + [regex]::Escape([string]$package.name) +
            '\s*\|\s*' + [regex]::Escape([string]$package.version) + '\s*\|'
        Assert-True ($inventory -match $inventoryPattern) `
            "dependency inventory is missing $($package.name) $($package.version)"
        $noticePattern = '(?m)^' + [regex]::Escape([string]$package.name) + '\s+' +
            [regex]::Escape([string]$package.version) + '\r?$'
        Assert-True ($notices -match $noticePattern) `
            "third-party notices are missing $($package.name) $($package.version)"
    }

    $env:LOCALAPPDATA = Join-Path $smokeRoot "LocalAppData"
    $env:APPDATA = Join-Path $smokeRoot "RoamingAppData"
    New-Item -ItemType Directory -Path $env:LOCALAPPDATA, $env:APPDATA -Force | Out-Null

    $tampered = Join-Path $smokeRoot "tampered"
    Copy-Item -LiteralPath $expanded -Destination $tampered -Recurse
    Add-Content -LiteralPath (Join-Path $tampered "README.md") -Value "tampered"
    $tamperRejected = $false
    try {
        & (Join-Path $tampered "install.ps1") -NoLaunch -RunRegistryPath $registryPath
    } catch {
        $tamperRejected = $true
    }
    Assert-True $tamperRejected "installer accepted a tampered package"

    & (Join-Path $expanded "install.ps1") -NoLaunch -StartWithWindows `
        -RunRegistryPath $registryPath
    $installDir = Join-Path $env:LOCALAPPDATA "Programs\CodexQuotaTray"
    $installedExe = Join-Path $installDir "codex-quota-tray-gui.exe"
    Assert-True (Test-Path -LiteralPath $installedExe -PathType Leaf) "installer did not copy the executable"
    $packageHash = (Get-FileHash -LiteralPath (Join-Path $expanded "codex-quota-tray-gui.exe") `
            -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    Assert-True ($packageHash -eq $installedHash) "installed executable hash differs from the package"

    $runValue = (Get-ItemProperty -Path $registryPath -Name "CodexQuotaTray").CodexQuotaTray
    Assert-True ($runValue -eq ('"' + $installedExe + '"')) "start-with-Windows value is incorrect"
    $shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\CodexQuotaTray.lnk"
    Assert-True (Test-Path -LiteralPath $shortcut -PathType Leaf) "Start Menu shortcut is missing"

    Set-Content -LiteralPath (Join-Path $installDir "README.md") -Value "old-version-marker"
    & (Join-Path $expanded "install.ps1") -NoLaunch -RunRegistryPath $registryPath
    $packageReadmeHash = (Get-FileHash -LiteralPath (Join-Path $expanded "README.md") -Algorithm SHA256).Hash
    $installedReadmeHash = (Get-FileHash -LiteralPath (Join-Path $installDir "README.md") `
            -Algorithm SHA256).Hash
    Assert-True ($packageReadmeHash -eq $installedReadmeHash) "upgrade did not replace installed files"
    $preservedRunValue = (Get-ItemProperty -Path $registryPath -Name "CodexQuotaTray").CodexQuotaTray
    Assert-True ($preservedRunValue -eq $runValue) "upgrade unexpectedly changed start-with-Windows"

    $dataDir = Join-Path $env:LOCALAPPDATA "CodexQuotaTray"
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dataDir "quota-cache.json") -Value "synthetic-test-data"
    & (Join-Path $installDir "uninstall.ps1") -RunRegistryPath $registryPath
    Assert-True (-not (Test-Path -LiteralPath $installDir)) "uninstaller left the install directory"
    Assert-True (-not (Test-Path -LiteralPath $dataDir)) "uninstaller left user data by default"
    Assert-True (-not (Test-Path -LiteralPath $shortcut)) "uninstaller left the Start Menu shortcut"
    $remainingRun = Get-ItemProperty -Path $registryPath -Name "CodexQuotaTray" `
        -ErrorAction SilentlyContinue
    Assert-True ($null -eq $remainingRun) "uninstaller left the start-with-Windows value"

    & (Join-Path $expanded "install.ps1") -NoLaunch -RunRegistryPath $registryPath
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $dataDir "settings.json") -Value "synthetic-test-data"
    & (Join-Path $installDir "uninstall.ps1") -KeepUserData -RunRegistryPath $registryPath
    Assert-True (-not (Test-Path -LiteralPath $installDir)) `
        "keep-user-data uninstall left the install directory"
    Assert-True (Test-Path -LiteralPath $dataDir -PathType Container) `
        "keep-user-data uninstall removed user data"
    Remove-Item -LiteralPath $dataDir -Recurse -Force

    [pscustomobject]@{
        Archive = $archive
        ArchiveBytes = (Get-Item -LiteralPath $archive).Length
        ExecutableBytes = (Get-Item -LiteralPath (Join-Path $expanded "codex-quota-tray-gui.exe")).Length
        IntegrityTamperRejected = $tamperRejected
        BuildPathRemapped = $true
        InstallUpgradeUninstall = "passed"
        KeepUserData = "passed"
    }
} finally {
    $env:LOCALAPPDATA = $oldLocalAppData
    $env:APPDATA = $oldAppData
    Remove-Item -Path $registryPath -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $smokeRoot) {
        Assert-ChildPath $targetRoot $smokeRoot
        $item = Get-Item -LiteralPath $smokeRoot -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force
        }
    }
}
