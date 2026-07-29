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
$output = if ($OutputDirectory) {
    Get-FullPath $OutputDirectory
} else {
    Join-Path $repoRoot "target\winui-publish"
}

Push-Location $winuiRoot
try {
    & $DotNet restore $project --configfile (Join-Path $winuiRoot "NuGet.config") -p:Platform=x64 -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "WinUI restore failed" }

    & $DotNet publish $project -c Release -p:Platform=x64 -r win-x64 --self-contained true `
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
