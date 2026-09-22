[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('android', 'windows')]
    [string]$Platform,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$SourceSha
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$worktree = Join-Path $env:RUNNER_TEMP 'update-manifest-worktree'
$repository = 'DDJang/CodexQuotaTray'
$expectedTag = if ($Platform -ceq 'android') { "android-v$Version" } else { "windows-v$Version" }
if ($Tag -cne $expectedTag) {
    throw "Tag '$Tag' does not match $Platform version $Version (expected '$expectedTag')."
}
$assetName = if ($Platform -ceq 'android') {
    "CodexQuotaTray-Android-v$Version.apk"
} else {
    "CodexQuotaTray-$Version-setup.exe"
}

function Invoke-CheckedGit {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$branchExists = git ls-remote --exit-code --heads origin update-manifest
if ($LASTEXITCODE -eq 0) {
    Invoke-CheckedGit fetch --no-tags origin 'refs/heads/update-manifest:refs/remotes/origin/update-manifest'
    Invoke-CheckedGit worktree add --detach $worktree refs/remotes/origin/update-manifest
} elseif ($LASTEXITCODE -eq 2) {
    Invoke-CheckedGit worktree add --detach $worktree $SourceSha
    Push-Location $worktree
    try {
        Invoke-CheckedGit switch --orphan update-manifest
    } finally {
        Pop-Location
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot '.github\update-manifest.seed.json') `
        -Destination (Join-Path $worktree 'update-manifest.json')
} else {
    throw 'Could not determine whether update-manifest branch exists.'
}

$metadataPath = Join-Path $env:RUNNER_TEMP 'release-metadata.json'
& gh release view $Tag --json body,publishedAt,assets | Out-File -LiteralPath $metadataPath -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the completed GitHub Release.'
}
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$notesPath = Join-Path $env:RUNNER_TEMP 'release-notes.md'
[IO.File]::WriteAllText($notesPath, [string]$metadata.body, [Text.UTF8Encoding]::new($false))
$asset = @($metadata.assets | Where-Object { $_.name -ceq $assetName }) | Select-Object -First 1
if ($null -eq $asset) {
    throw "Completed GitHub Release is missing $assetName."
}

$releaseDir = Join-Path $env:RUNNER_TEMP "$Platform-release-manifest"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
& gh release download $Tag --pattern $assetName --pattern 'SHA256SUMS.txt' --dir $releaseDir
if ($LASTEXITCODE -ne 0) {
    throw "Could not download completed $Platform Release assets."
}
$assetPath = Join-Path $releaseDir $assetName
$checksumPath = Join-Path $releaseDir 'SHA256SUMS.txt'
$checksumLines = @(Get-Content -LiteralPath $checksumPath | Where-Object {
    $_ -match "\s+$([Regex]::Escape($assetName))$"
})
if ($checksumLines.Count -ne 1 -or $checksumLines[0] -notmatch '^([0-9a-fA-F]{64})\s+') {
    throw "Could not find $assetName in SHA256SUMS.txt."
}
$assetSha256 = $Matches[1].ToLowerInvariant()
$downloadedSha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($downloadedSha256 -cne $assetSha256) {
    throw "Downloaded $assetName does not match SHA256SUMS.txt."
}
if ([long]$asset.size -ne (Get-Item -LiteralPath $assetPath).Length) {
    throw "Downloaded $assetName size does not match GitHub Release metadata."
}

& (Join-Path $repoRoot '.github\scripts\update-release-manifest.ps1') `
    -Platform $Platform `
    -ManifestPath (Join-Path $worktree 'update-manifest.json') `
    -Version $Version `
    -Tag $Tag `
    -AssetName $assetName `
    -AssetUrl "https://github.com/$repository/releases/download/$Tag/$assetName" `
    -Sha256 $assetSha256 `
    -AssetSize ([long]$asset.size) `
    -ReleaseNotesPath $notesPath `
    -PublishedAt ([string]$metadata.publishedAt)

Push-Location $worktree
try {
    Invoke-CheckedGit config user.name 'github-actions[bot]'
    Invoke-CheckedGit config user.email '41898282+github-actions[bot]@users.noreply.github.com'
    Invoke-CheckedGit add update-manifest.json
    Invoke-CheckedGit commit -m "release: update $Platform manifest to $Version"
    Invoke-CheckedGit push origin HEAD:refs/heads/update-manifest
} finally {
    Pop-Location
}
