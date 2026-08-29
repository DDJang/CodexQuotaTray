[CmdletBinding()]
param(
    [string]$DotNet = "dotnet",
    [string]$Iscc = "",
    [Alias("RepoRoot")]
    [string]$RepoRootPath = "",
    [Alias("WindowsRoot")]
    [string]$WindowsRootPath = "",
    [Alias("OutputDir", "OutputDirectory")]
    [string]$OutputPath = "",
    [Alias("PublishDir", "PublishDirectory")]
    [string]$PublishPath = "",
    [Alias("RuntimeInstaller", "WindowsAppRuntimeInstallerPath")]
    [string]$WindowsAppRuntimeInstaller = "",
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

$windowsRootPath = if ($WindowsRootPath) {
    Get-FullPath $WindowsRootPath
} else {
    Get-FullPath (Join-Path $PSScriptRoot "..")
}
$repoRootPath = if ($RepoRootPath) {
    Get-FullPath $RepoRootPath
} else {
    Get-FullPath (Join-Path $windowsRootPath "..")
}
$outputRoot = if ($OutputPath) {
    Get-FullPath $OutputPath
} else {
    Join-Path $repoRootPath "dist-inno"
}
$publishRoot = if ($PublishPath) {
    Get-FullPath $PublishPath
} else {
    Join-Path $repoRootPath "target\winui-publish"
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

Push-Location $repoRootPath
try {
    $project = Join-Path $windowsRootPath "src\CodexQuotaTray.App\CodexQuotaTray.App.csproj"
    [xml]$projectXml = Get-Content -LiteralPath $project -Raw
    $versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup[Version]/Version')
    if ($null -eq $versionNode) { throw "WinUI application version is missing" }
    $version = ([string]$versionNode.InnerText).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) { throw "WinUI application version is empty" }

    $runtimeScript = Join-Path $windowsRootPath "scripts\acquire-windows-app-runtime.ps1"
    $runtimeArguments = @{}
    if ($WindowsAppRuntimeInstaller) {
        $runtimeArguments = @{ InstallerPath = $WindowsAppRuntimeInstaller }
    }
    $runtimeInfo = @(& $runtimeScript @runtimeArguments)
    if ($runtimeInfo.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$runtimeInfo[0].Path)) {
        throw "Windows App Runtime acquisition returned no validated installer path"
    }
    $runtimeInstallerPath = [IO.Path]::GetFullPath([string]$runtimeInfo[0].Path)

    [xml]$packagesProps = Get-Content -LiteralPath (Join-Path $windowsRootPath "Directory.Packages.props") -Raw
    $windowsAppSdkVersionNode = $packagesProps.SelectSingleNode(
        '/Project/ItemGroup/PackageVersion[@Include="Microsoft.WindowsAppSDK"]/@Version')
    if ($null -eq $windowsAppSdkVersionNode) {
        throw "Microsoft.WindowsAppSDK central package version is missing"
    }
    $windowsAppSdkVersion = ([string]$windowsAppSdkVersionNode.Value).Trim()
    if ($windowsAppSdkVersion -cne ([string]$runtimeInfo[0].Version).Trim()) {
        throw "Microsoft.WindowsAppSDK version $windowsAppSdkVersion does not match Windows App Runtime version $($runtimeInfo[0].Version)"
    }

    if (-not $SkipPublish) {
        & (Join-Path $windowsRootPath "scripts\publish-winui.ps1") -DotNet $DotNet -OutputDirectory $publishRoot
    }

    $binaryPath = Join-Path $publishRoot "codex-quota-tray-gui.exe"
    if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) { throw "WinUI publish output is missing" }
    $resourceVerifier = Join-Path $windowsRootPath "scripts\verify-pe-icon.ps1"
    & $resourceVerifier -Executable $binaryPath -GroupIconId 32512 | Format-Table -AutoSize

    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    $iss = Join-Path $windowsRootPath "installer\CodexQuotaTray.iss"
    & $Iscc $iss "/DMyAppVersion=$version" "/DRepoRoot=$repoRootPath" "/DWindowsRoot=$windowsRootPath" "/DOutputDir=$outputRoot" "/DPublishDir=$publishRoot" "/DWindowsAppRuntimeInstaller=$runtimeInstallerPath"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }

    $artifact = Join-Path $outputRoot "CodexQuotaTray-$version-setup.exe"
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Inno Setup did not produce the expected installer"
    }
    Get-FileHash -LiteralPath $artifact -Algorithm SHA256
} finally {
    Pop-Location
}
