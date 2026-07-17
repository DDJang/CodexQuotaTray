[CmdletBinding()]
param(
    [string]$Cargo = "cargo",
    [string]$Iscc = "",
    [string]$OutputDirectory = "",
    [string]$TargetDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$outputRoot = if ($OutputDirectory) {
    Get-FullPath $OutputDirectory
} else {
    Join-Path $repoRoot "dist-inno"
}
$targetRoot = if ($TargetDirectory) {
    Get-FullPath $TargetDirectory
} else {
    Join-Path $repoRoot "target"
}

if (-not $Iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $Iscc = $command.Source
    } else {
        $knownPaths = @(
            "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe"
        )
        $Iscc = $knownPaths | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    }
}
if (-not $Iscc -or -not (Test-Path -LiteralPath $Iscc -PathType Leaf)) {
    throw "ISCC.exe not found. Install Inno Setup 7 from https://jrsoftware.org/isinfo.php, then rerun this script with -Iscc PATH."
}

Push-Location $repoRoot
try {
    $metadataText = & $Cargo metadata --format-version 1 --locked --offline --filter-platform x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) { throw "cargo metadata failed" }
    $metadata = $metadataText | ConvertFrom-Json
    $rootPackage = $metadata.packages | Where-Object { $_.id -eq $metadata.resolve.root }
    if (-not $rootPackage) { throw "could not find the root Cargo package" }
    $version = [string]$rootPackage.version

    $remapFlag = "--remap-path-prefix=$repoRoot=."
    & $Cargo rustc --release --target-dir $targetRoot --locked --offline --bin codex-quota-tray-gui -- $remapFlag
    if ($LASTEXITCODE -ne 0) { throw "release build failed" }

    $binaryPath = Join-Path $targetRoot "release\codex-quota-tray-gui.exe"
    $resourceVerifier = Join-Path $repoRoot "scripts\verify-pe-icon.ps1"
    & $resourceVerifier -Executable $binaryPath | Format-Table -AutoSize
    if ($LASTEXITCODE -ne 0) { throw "release PE icon resource verification failed" }

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $iss = Join-Path $repoRoot "installer\CodexQuotaTray.iss"
    & $Iscc $iss "/DMyAppVersion=$version" "/DSourceDir=$repoRoot" "/DOutputDir=$outputRoot" "/DBinaryPath=$binaryPath"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }

    $artifact = Join-Path $outputRoot "CodexQuotaTray-$version-setup.exe"
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Inno Setup did not produce the expected installer"
    }
    Get-FileHash -LiteralPath $artifact -Algorithm SHA256
} finally {
    Pop-Location
}
