$ErrorActionPreference = 'Stop'
$resolver = Join-Path $PSScriptRoot 'resolve-android-release-source.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('codex-release-source-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$origin = Join-Path $root 'origin'
$clone = Join-Path $root 'clone'
$cases = 0

function Invoke-FixtureGit([string[]]$Arguments) {
    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($Arguments -join ' ')" }
    return $output
}
function Reject([hashtable]$Arguments, [string]$Reason) {
    try { $null = & $resolver @Arguments } catch {
        if ($_.Exception.Message -notlike "*$Reason*") { throw }
        $script:cases++
        return
    }
    throw "Expected rejection: $Reason"
}

Push-Location $root
try {
    $null = Invoke-FixtureGit @('init', '--initial-branch=main', $origin)
    Set-Location $origin
    $null = Invoke-FixtureGit @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '--allow-empty', '-m', 'source')
    $sha = [string](Invoke-FixtureGit @('rev-parse', 'HEAD'))
    $null = Invoke-FixtureGit @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'tag', '-a', 'android-v0.11.6', '-m', 'fixture')
    $null = Invoke-FixtureGit @('checkout', '--orphan', 'unmerged')
    $null = Invoke-FixtureGit @('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '--allow-empty', '-m', 'unmerged')
    $unmerged = [string](Invoke-FixtureGit @('rev-parse', 'HEAD'))
    $null = Invoke-FixtureGit @('tag', 'android-v9.0.0')
    $null = Invoke-FixtureGit @('checkout', 'main')
    $null = Invoke-FixtureGit @('clone', $origin, $clone)
    Set-Location $clone
    $releaseArguments = @{ EventName='workflow_dispatch'; WorkflowRef='refs/heads/main'; RequestedTag='android-v0.11.6'; ExpectedSha=$sha }
    $result = & $resolver @releaseArguments
    if ($result.Tag -cne 'android-v0.11.6' -or $result.Sha -cne $sha) { throw 'Recovery resolved wrong source.' }
    $cases++
    $result = & $resolver -EventName push -WorkflowRef refs/tags/android-v0.11.6 -EventSha $sha
    if ($result.Sha -cne $sha) { throw 'Push resolved wrong source.' }
    $cases++
    $bad = $releaseArguments.Clone(); $bad.WorkflowRef='refs/heads/topic'; Reject $bad 'must run from main'
    $bad = $releaseArguments.Clone(); $bad.ExpectedSha=('0' * 40); Reject $bad 'does not match'
    $bad = $releaseArguments.Clone(); $bad.ExpectedSha=$sha.Substring(0,7); Reject $bad 'full lowercase'
    $bad = $releaseArguments.Clone(); $bad.RequestedTag='windows-v0.11.6'; Reject $bad 'strict android'
    $bad = $releaseArguments.Clone(); $bad.RequestedTag='android-v0.11.6;echo unsafe'; Reject $bad 'strict android'
    $bad = $releaseArguments.Clone(); $bad.EventName='pull_request'; Reject $bad 'Only a tag push'
    $bad = $releaseArguments.Clone(); $bad.RequestedTag='android-v9.0.0'; $bad.ExpectedSha=$unmerged; Reject $bad 'Git command failed: merge-base'
    $bad = $releaseArguments.Clone(); $bad.RequestedTag='android-v0.11.7'; Reject $bad 'Git command failed: fetch'
    if ([string](Invoke-FixtureGit @('rev-parse', 'android-v0.11.6^{commit}')) -cne $sha) { throw 'Tag was changed.' }
    Write-Host "Android release source checks passed: $cases"
} finally {
    Pop-Location
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('codex-release-source-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
