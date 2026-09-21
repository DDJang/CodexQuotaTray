param(
    [Parameter(Mandatory)][string]$EventName,
    [Parameter(Mandatory)][string]$WorkflowRef,
    [string]$RequestedTag,
    [string]$ExpectedSha,
    [string]$EventSha
)
$ErrorActionPreference = 'Stop'

if ($EventName -eq 'workflow_dispatch') {
    if ($WorkflowRef -cne 'refs/heads/main') { throw 'Recovery must run from main.' }
    $tag = $RequestedTag
    $expected = $ExpectedSha
} elseif ($EventName -eq 'push' -and $WorkflowRef.StartsWith('refs/tags/', [StringComparison]::Ordinal)) {
    $tag = $WorkflowRef.Substring('refs/tags/'.Length)
    $expected = $EventSha
} else {
    throw 'Only a tag push or explicit main recovery is supported.'
}
if ($tag -cnotmatch '^android-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Expected a strict android-vMAJOR.MINOR.PATCH tag.'
}
if ($expected -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected a full lowercase commit SHA.' }

function Invoke-GitChecked([string[]]$Arguments) {
    $result = & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git command failed: $($Arguments[0])" }
    return $result
}

$null = Invoke-GitChecked @('fetch', '--no-tags', 'origin', 'refs/heads/main:refs/remotes/origin/main')
$null = Invoke-GitChecked @('fetch', '--no-tags', 'origin', "refs/tags/${tag}:refs/tags/${tag}")
$sha = [string](Invoke-GitChecked @('rev-parse', '--verify', "refs/tags/${tag}^{commit}"))
if ($sha -cne $expected) { throw 'Release tag does not match the expected immutable source commit.' }
$null = Invoke-GitChecked @('merge-base', '--is-ancestor', $sha, 'refs/remotes/origin/main')
[pscustomobject]@{ Tag = $tag; Sha = $sha }
