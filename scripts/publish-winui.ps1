[CmdletBinding()]
param(
    [string]$DotNet = "dotnet",
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$winuiRoot = Join-Path $repoRoot "winui"
$project = Join-Path $winuiRoot "src\CodexQuotaTray.App\CodexQuotaTray.App.csproj"
$globalJsonPath = Join-Path $repoRoot "global.json"
if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw "Repository global.json was not found: $globalJsonPath"
}
[string]$requiredSdkVersion = (Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json).sdk.version
if ([string]::IsNullOrWhiteSpace($requiredSdkVersion)) {
    throw "global.json does not define sdk.version."
}
$dotnetCandidates = if ($DotNet -eq "dotnet") {
    @(
        (Join-Path $repoRoot "target\dotnet-sdk-$requiredSdkVersion-full\dotnet.exe"),
        (Join-Path $repoRoot "target\dotnet-sdk-$requiredSdkVersion\dotnet.exe"),
        "dotnet"
    )
} else {
    @($DotNet)
}
$dotnetCommand = $null
foreach ($candidate in $dotnetCandidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $dotnetCommand = Get-FullPath $candidate
        break
    }

    $command = Get-Command $candidate -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $dotnetCommand = $command.Source
        break
    }
}
if (-not $dotnetCommand) {
    throw "Required .NET SDK $requiredSdkVersion was not found in target/ or PATH. The publish script does not download SDKs."
}

Push-Location $repoRoot
try {
    $actualSdkVersion = ([string](& $dotnetCommand --version)).Trim()
    if ($LASTEXITCODE -ne 0) { throw "dotnet --version failed for '$dotnetCommand'." }
} finally {
    Pop-Location
}
$actualVersion = [Version]$actualSdkVersion
$requiredVersion = [Version]$requiredSdkVersion
$sdkMatches = -not $actualSdkVersion.Contains('-', [StringComparison]::Ordinal) `
    -and $actualVersion.Major -eq $requiredVersion.Major `
    -and $actualVersion.Minor -eq $requiredVersion.Minor `
    -and [Math]::Floor($actualVersion.Build / 100) -eq [Math]::Floor($requiredVersion.Build / 100) `
    -and $actualVersion.Build -ge $requiredVersion.Build
if (-not $sdkMatches) {
    throw "dotnet '$dotnetCommand' selected SDK $actualSdkVersion; global.json requires $requiredSdkVersion with latestPatch."
}
$output = if ($OutputDirectory) {
    Get-FullPath $OutputDirectory
} else {
    Join-Path $repoRoot "target\winui-publish"
}

Push-Location $winuiRoot
try {
    & $dotnetCommand restore $project --configfile (Join-Path $winuiRoot "NuGet.config") -p:Platform=x64 -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "WinUI restore failed" }

    & $dotnetCommand publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true `
        --no-restore -o $output -p:WindowsAppSDKSelfContained=true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "WinUI publish failed" }

    $executable = Join-Path $output "codex-quota-tray-gui.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "WinUI publish did not produce codex-quota-tray-gui.exe"
    }

    [pscustomobject]@{
        OutputDirectory = $output
        Executable = $executable
        FileCount = @(Get-ChildItem -LiteralPath $output -File -Recurse).Count
        TotalBytes = (Get-ChildItem -LiteralPath $output -File -Recurse | Measure-Object Length -Sum).Sum
    }
} finally {
    Pop-Location
}
