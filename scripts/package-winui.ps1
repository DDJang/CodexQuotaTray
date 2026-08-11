[CmdletBinding()]
param(
    [string]$DotNet = "dotnet",
    [string]$OutputDirectory = "",
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) { return [IO.Path]::GetFullPath($Path) }
function Assert-ChildPath([string]$Parent, [string]$Child) {
    $parentPath = (Get-FullPath $Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $childPath = Get-FullPath $Child
    if (-not $childPath.StartsWith($parentPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "refusing to modify a path outside the expected project directory"
    }
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$distRoot = if ($OutputDirectory) { Get-FullPath $OutputDirectory } else { Join-Path $repoRoot "dist" }
$publishRoot = Join-Path $repoRoot "target\winui-publish"
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot "winui\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj") -Raw
$versionNode = $project.SelectSingleNode('/Project/PropertyGroup[Version]/Version')
if ($null -eq $versionNode) {
    throw 'CodexQuotaTray.App.csproj does not define a Version.'
}
$version = ([string]$versionNode.InnerText).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'CodexQuotaTray.App.csproj defines an empty Version.'
}
$packageName = "CodexQuotaTray-$version-win-x64"
$stage = Join-Path $repoRoot "target\package\$packageName"
Assert-ChildPath $repoRoot $stage
Assert-ChildPath $repoRoot $publishRoot

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot "publish-winui.ps1") -DotNet $DotNet -OutputDirectory $publishRoot
}
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot "codex-quota-tray-gui.exe") -PathType Leaf)) {
    throw "WinUI publish output is missing"
}

if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $publishRoot "*") -Destination $stage -Recurse -Force -Exclude "*.pdb"

foreach ($entry in @(
    @{ Source = "README.md"; Destination = "README.md" },
    @{ Source = "LICENSE"; Destination = "LICENSE" },
    @{ Source = "docs\PRIVACY.md"; Destination = "PRIVACY.md" },
    @{ Source = "docs\DEPENDENCIES.md"; Destination = "DEPENDENCIES.md" }
)) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $entry.Source) -Destination (Join-Path $stage $entry.Destination) -Force
}

$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $stage "VERSION"), "$version`n", $utf8)
$manifest = foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $relative"
}
[IO.File]::WriteAllText((Join-Path $stage "MANIFEST.sha256"), (($manifest -join "`n") + "`n"), $utf8)

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
$archive = Join-Path $distRoot "$packageName.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal
Get-FileHash -LiteralPath $archive -Algorithm SHA256
