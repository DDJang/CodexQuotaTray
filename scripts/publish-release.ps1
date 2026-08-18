[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Windows', 'Android', 'All')]
    [string]$Platform,
    [switch]$DryRun,
    [ValidateRange(5, 240)]
    [int]$TimeoutMinutes = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Platform = switch ($Platform.ToLowerInvariant()) {
    'windows' { 'Windows' }
    'android' { 'Android' }
    'all' { 'All' }
}

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:Blockers = [System.Collections.Generic.List[string]]::new()
$script:Step = 0
$script:Git = (Get-Command git -ErrorAction Stop).Source
$script:Gh = $null
$script:RepoName = $null
$script:Branch = $null
$script:HeadSha = $null
$script:MainSha = $null
$script:SelectedPlatforms = @(
    if ($Platform -eq 'All') {
        'Android'
        'Windows'
    } else {
        $Platform
    }
)
$script:ReleaseScope = if ($Platform -eq 'All') {
    'Android and Windows'
} else {
    $Platform
}

Set-Location -LiteralPath $script:RepoRoot

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    $script:Step++
    Write-Host ("[{0:00}] {1}" -f $script:Step, $Message)
}

function Add-Blocker {
    param([Parameter(Mandatory = $true)][string]$Message)
    if ($DryRun) {
        $script:Blockers.Add($Message)
        Write-Host "DRY RUN BLOCKER: $Message" -ForegroundColor Yellow
    } else {
        throw $Message
    }
}

function Invoke-Captured {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $output = & $FilePath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    return [pscustomobject]@{ ExitCode = $exitCode; Text = $text }
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [switch]$AllowFailure
    )
    $result = Invoke-Captured -FilePath $FilePath -Arguments $Arguments
    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {
        Write-Host $result.Text
    }
    if (-not $AllowFailure -and $result.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($result.ExitCode)."
    }
    return $result
}

function Read-ExternalText {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $result = Invoke-Captured -FilePath $FilePath -Arguments $Arguments
    if ($result.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($result.ExitCode): $($result.Text)"
    }
    return $result.Text
}

function Read-GhJson {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $text = Read-ExternalText -FilePath $script:Gh -Arguments $Arguments
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw 'GitHub CLI returned empty JSON.'
    }
    return $text | ConvertFrom-Json
}

function Test-Ancestor {
    param(
        [Parameter(Mandatory = $true)][string]$Ancestor,
        [Parameter(Mandatory = $true)][string]$Descendant
    )
    & $script:Git merge-base --is-ancestor $Ancestor $Descendant *> $null
    return $LASTEXITCODE -eq 0
}

function Normalize-Text {
    param([AllowNull()][string]$Value)
    if ($null -eq $Value) {
        return ''
    }
    $normalized = $Value -replace '\r\n', ([char]10)
    $normalized = $normalized -replace '\r', ([char]10)
    return $normalized.Trim()
}

function Require-Text {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) {
        Add-Blocker "$Label is missing required text '$Needle'."
    }
}

function Test-PlatformSelected {
    param([Parameter(Mandatory = $true)][ValidateSet('Windows', 'Android')][string]$Name)
    return $script:SelectedPlatforms -contains $Name
}

function Get-PlatformTagRecords {
    param([Parameter(Mandatory = $true)][string]$Prefix)
    $records = [System.Collections.Generic.List[object]]::new()
    $tags = @(& $script:Git tag --list "$Prefix*")
    foreach ($tag in $tags) {
        $tagText = ([string]$tag).Trim()
        $pattern = "^{0}((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))$" -f [regex]::Escape($Prefix)
        if ($tagText -notmatch $pattern) {
            continue
        }
        $versionText = $Matches[1]
        try {
            $commit = (Read-ExternalText -FilePath $script:Git -Arguments @(
                'rev-parse',
                ($tagText + '^{commit}')
            )).Trim()
            $records.Add([pscustomobject]@{
                Tag = $tagText
                VersionText = $versionText
                Version = [Version]$versionText
                Commit = $commit
            })
        } catch {
            Add-Blocker "Could not resolve local tag '$tagText'."
        }
    }
    return @($records)
}

function Get-PreviousTag {
    param(
        [Parameter(Mandatory = $true)][string]$Prefix,
        [Parameter(Mandatory = $true)][string]$Head
    )
    $candidates = @(Get-PlatformTagRecords -Prefix $Prefix | Where-Object {
        Test-Ancestor -Ancestor $_.Commit -Descendant $Head
    } | Sort-Object Version -Descending)
    if ($candidates.Count -eq 0) {
        Add-Blocker "No ancestor tag was found for platform prefix '$Prefix'."
        return $null
    }
    return $candidates[0]
}

function Get-RemoteTagState {
    param([Parameter(Mandatory = $true)][string]$Tag)
    $result = Invoke-Captured -FilePath $script:Git -Arguments @(
        'ls-remote',
        '--tags',
        'origin',
        "refs/tags/$Tag*"
    )
    if ($result.ExitCode -ne 0) {
        Add-Blocker "Could not inspect remote tag '$Tag'."
        return $null
    }
    $direct = $null
    $peeled = $null
    foreach ($line in ($result.Text -split [Environment]::NewLine)) {
        if ($line -match '^([0-9a-f]{40})\s+(.+)$') {
            if ($Matches[2] -ceq "refs/tags/$Tag") {
                $direct = $Matches[1]
            } elseif ($Matches[2] -ceq "refs/tags/$Tag^{}") {
                $peeled = $Matches[1]
            }
        }
    }
    if ($null -eq $direct -and $null -eq $peeled) {
        return [pscustomobject]@{ Exists = $false; Commit = $null }
    }
    return [pscustomobject]@{
        Exists = $true
        Commit = if ($null -ne $peeled) { $peeled } else { $direct }
    }
}

function Get-AndroidVersionCode {
    param([Parameter(Mandatory = $true)][string]$Commit)
    $content = Read-ExternalText -FilePath $script:Git -Arguments @(
        'show',
        ('{0}:android/app/build.gradle.kts' -f $Commit)
    )
    $matches = [regex]::Matches($content, '(?m)^\s*versionCode\s*=\s*([0-9]+)\s*$')
    if ($matches.Count -ne 1) {
        return $null
    }
    return [int]$matches[0].Groups[1].Value
}

function Read-AndroidVersionInfo {
    $path = Join-Path $script:RepoRoot 'android\app\build.gradle.kts'
    $content = [IO.File]::ReadAllText($path)
    $versionMatches = [regex]::Matches($content, '(?m)^\s*versionName\s*=\s*"([^"]+)"\s*$')
    $codeMatches = [regex]::Matches($content, '(?m)^\s*versionCode\s*=\s*([0-9]+)\s*$')
    if ($versionMatches.Count -ne 1 -or $codeMatches.Count -ne 1) {
        Add-Blocker 'android/app/build.gradle.kts must define exactly one versionName and one versionCode.'
        return $null
    }
    return [pscustomobject]@{
        Path = $path
        Content = $content
        Version = $versionMatches[0].Groups[1].Value
        VersionCode = [int]$codeMatches[0].Groups[1].Value
    }
}

function Read-WindowsVersionInfo {
    $path = Join-Path $script:RepoRoot 'windows\src\CodexQuotaTray.App\CodexQuotaTray.App.csproj'
    $content = [IO.File]::ReadAllText($path)
    $matches = [regex]::Matches($content, '(?m)^\s*<Version>([^<]+)</Version>\s*$')
    if ($matches.Count -ne 1) {
        Add-Blocker 'CodexQuotaTray.App.csproj must define exactly one Version.'
        return $null
    }
    return [pscustomobject]@{
        Path = $path
        Content = $content
        Version = $matches[0].Groups[1].Value.Trim()
    }
}

function Read-Notes {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Blocker "Missing release notes file '$Path'."
        return ''
    }
    $content = [IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($content)) {
        Add-Blocker "Release notes file '$Path' is empty."
    }
    return $content
}

function Get-RepoRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return $Path.Substring($script:RepoRoot.Length + 1).Replace('\', '/')
}

function Test-FileInHead {
    param([Parameter(Mandatory = $true)][string]$Path)
    $relativePath = Get-RepoRelativePath -Path $Path
    $tracked = Invoke-Captured -FilePath $script:Git -Arguments @(
        'ls-files', '--error-unmatch', '--', $relativePath
    )
    if ($tracked.ExitCode -ne 0) {
        return $false
    }
    $headFile = Invoke-Captured -FilePath $script:Git -Arguments @(
        'cat-file', '-e', "HEAD:$relativePath"
    )
    return $headFile.ExitCode -eq 0
}

function Get-CommitChangedPaths {
    param([Parameter(Mandatory = $true)][string]$Commit)
    $text = Read-ExternalText -FilePath $script:Git -Arguments @(
        'diff-tree', '--no-commit-id', '--name-only', '-r', $Commit
    )
    if ([string]::IsNullOrWhiteSpace($text)) {
        return @()
    }
    return @($text -split [Environment]::NewLine | ForEach-Object {
        ([string]$_).Trim()
    } | Where-Object { $_ })
}

function Test-ExistingReleasePreparationState {
    param(
        [AllowNull()]$AndroidInfo,
        [AllowNull()]$WindowsInfo,
        [Parameter(Mandatory = $true)][string[]]$NotesPaths,
        [Parameter(Mandatory = $true)][string]$CommitMessage
    )
    $worktreeStatus = (Read-ExternalText -FilePath $script:Git -Arguments @(
        'status', '--short'
    )).Trim()
    if (-not [string]::IsNullOrWhiteSpace($worktreeStatus)) {
        return $false
    }

    $expectedVersionPaths = @()
    if (Test-PlatformSelected -Name 'Android') {
        if ($null -eq $AndroidInfo -or [string]$AndroidInfo.Version -cne $Version) {
            return $false
        }
        $expectedVersionPaths += Get-RepoRelativePath -Path $AndroidInfo.Path
    }
    if (Test-PlatformSelected -Name 'Windows') {
        if ($null -eq $WindowsInfo -or [string]$WindowsInfo.Version -cne $Version) {
            return $false
        }
        $expectedVersionPaths += Get-RepoRelativePath -Path $WindowsInfo.Path
    }
    foreach ($notesPath in $NotesPaths) {
        if (-not (Test-FileInHead -Path $notesPath)) {
            return $false
        }
    }

    $subject = (Read-ExternalText -FilePath $script:Git -Arguments @(
        'log', '-1', '--format=%s', 'HEAD'
    )).Trim()
    if ($subject -cne $CommitMessage) {
        return $false
    }

    $changedPaths = @(Get-CommitChangedPaths -Commit 'HEAD')
    if ($changedPaths.Count -ne $expectedVersionPaths.Count) {
        return $false
    }
    foreach ($path in $expectedVersionPaths) {
        if ($path -notin $changedPaths) {
            return $false
        }
    }
    return $true
}

function Assert-WorkflowContracts {
    Write-Step 'Checking release workflow contracts.'
    $workflowContracts = @()
    if (Test-PlatformSelected -Name 'Android') {
        $workflowContracts += [pscustomobject]@{
            Path = '.github\workflows\android-release.yml'
            Platform = 'Android'
            TagPrefix = 'android-v*'
        }
    }
    if (Test-PlatformSelected -Name 'Windows') {
        $workflowContracts += [pscustomobject]@{
            Path = '.github\workflows\windows-release.yml'
            Platform = 'Windows'
            TagPrefix = 'windows-v*'
        }
    }
    foreach ($contract in $workflowContracts) {
        $workflow = [IO.File]::ReadAllText((Join-Path $script:RepoRoot $contract.Path))
        Require-Text -Text $workflow -Needle 'group: update-manifest-publish' -Label "$($contract.Platform) Release concurrency"
        Require-Text -Text $workflow -Needle 'cancel-in-progress: false' -Label "$($contract.Platform) Release concurrency"
        Require-Text -Text $workflow -Needle $contract.TagPrefix -Label "$($contract.Platform) Release tag trigger"
        Require-Text -Text $workflow -Needle '--notes-file' -Label "$($contract.Platform) Release notes"
        if ($workflow.Contains('--generate-notes')) {
            Add-Blocker "$($contract.Platform) Release workflow must not use --generate-notes."
        }
    }
    $requiredPaths = @()
    if (Test-PlatformSelected -Name 'Android') {
        $requiredPaths += '.github\workflows\android-ci.yml'
    }
    if (Test-PlatformSelected -Name 'Windows') {
        $requiredPaths += '.github\workflows\windows-ci.yml'
    }
    $requiredPaths += @(
        '.github\scripts\update-release-manifest.ps1',
        '.github\scripts\test-update-release-manifest.ps1',
        '.github\scripts\test-publish-release.ps1'
    )
    foreach ($path in $requiredPaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $script:RepoRoot $path) -PathType Leaf)) {
            Add-Blocker "Required release contract file is missing: $path"
        }
    }
}

function Update-VersionFiles {
    param(
        [AllowNull()]$AndroidInfo,
        [AllowNull()]$WindowsInfo,
        [AllowNull()][int]$VersionCode
    )
    if (Test-PlatformSelected -Name 'Android') {
        if ($null -eq $AndroidInfo) {
            throw 'Android version information was not loaded.'
        }
        $androidText = [regex]::Replace(
            $AndroidInfo.Content,
            '(?m)^(\s*versionName\s*=\s*")[^"]+(")\s*$',
            ('${1}' + $Version + '${2}'),
            1)
        $androidText = [regex]::Replace(
            $androidText,
            '(?m)^(\s*versionCode\s*=\s*)[0-9]+(\s*)$',
            ('${1}' + $VersionCode + '${2}'),
            1)
        [IO.File]::WriteAllText($AndroidInfo.Path, $androidText, [Text.UTF8Encoding]::new($false))
    }
    if (Test-PlatformSelected -Name 'Windows') {
        if ($null -eq $WindowsInfo) {
            throw 'Windows version information was not loaded.'
        }
        $windowsText = [regex]::Replace(
            $WindowsInfo.Content,
            '(?m)^(\s*<Version>)[^<]+(</Version>\s*)$',
            ('${1}' + $Version + '${2}'),
            1)
        [IO.File]::WriteAllText($WindowsInfo.Path, $windowsText, [Text.UTF8Encoding]::new($false))
    }
}

function Run-LocalValidation {
    Write-Step 'Running release validation.'
    if (Test-PlatformSelected -Name 'Android') {
        Invoke-External -FilePath (Join-Path $script:RepoRoot 'android\gradlew.bat') -Arguments @(
            '-p', 'android', ':app:testDebugUnitTest', ':app:lintDebug', ':app:assembleDebug'
        )
    }
    if (Test-PlatformSelected -Name 'Windows') {
        Invoke-External -FilePath 'pwsh' -Arguments @(
            '-NoProfile', '-File', '.\windows\scripts\verify-winui.ps1', '-Mode', 'Release'
        )
    }
    Invoke-External -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.\.github\scripts\test-update-release-manifest.ps1'
    )
    Invoke-External -FilePath 'pwsh' -Arguments @(
        '-NoProfile', '-File', '.\.github\scripts\test-publish-release.ps1'
    )
    Invoke-External -FilePath $script:Git -Arguments @('diff', '--check')
}

function Get-OpenReleasePr {
    $prs = @(Read-GhJson @(
        'pr', 'list', '--head', $script:Branch, '--base', 'main', '--state', 'open',
        '--json', 'number,url,headRefName,baseRefName'
    ))
    if ($prs.Count -eq 0) { return $null }
    if ($prs.Count -gt 1) {
        Add-Blocker "More than one open PR exists from '$($script:Branch)' to main."
        return $null
    }
    return $prs[0]
}

function Get-MergeConvention {
    Write-Step 'Inferring the recent main merge convention.'
    $merged = @(Read-GhJson @(
        'pr', 'list', '--base', 'main', '--state', 'merged', '--limit', '3',
        '--json', 'number,mergeCommit'
    ))
    $methods = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $merged) {
        if ($null -eq $item.mergeCommit -or [string]::IsNullOrWhiteSpace([string]$item.mergeCommit.oid)) {
            continue
        }
        $detail = Read-GhJson @('pr', 'view', ([string]$item.number), '--json', 'commits')
        $commitCount = @($detail.commits).Count
        $parentsText = Read-ExternalText -FilePath $script:Git -Arguments @(
            'show', '-s', '--format=%P', ([string]$item.mergeCommit.oid)
        )
        $parentCount = @($parentsText -split '\s+' | Where-Object { $_ }).Count
        if ($parentCount -gt 1) {
            $methods.Add('merge')
        } elseif ($parentCount -eq 1 -and $commitCount -gt 1) {
            $methods.Add('squash')
        }
    }
    $distinct = @($methods | Select-Object -Unique)
    if ($distinct.Count -ne 1) {
        Add-Blocker 'Recent merged PRs do not identify one unambiguous merge convention.'
        return $null
    }
    Write-Host "Recent merge convention: $($distinct[0])"
    return $distinct[0]
}

function Wait-PrChecks {
    param([Parameter(Mandatory = $true)][int]$Number)
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        $result = Invoke-Captured -FilePath $script:Gh -Arguments @(
            'pr', 'checks', ([string]$Number), '--json', 'name,state,bucket,workflow,link'
        )
        if ($result.ExitCode -ne 0) {
            if ($result.Text -match 'no checks') {
                Write-Host 'PR checks are not visible yet.'
            } else {
                throw "Could not read PR checks: $($result.Text)"
            }
        } else {
            $checks = @($result.Text | ConvertFrom-Json)
            if ($checks.Count -gt 0) {
                $failed = @($checks | Where-Object { $_.bucket -in @('fail', 'cancel', 'error') })
                if ($failed.Count -gt 0) {
                    throw 'A required PR check failed or was cancelled.'
                }
                $pending = @($checks | Where-Object { $_.bucket -notin @('pass', 'skipping') })
                if ($pending.Count -eq 0) {
                    Write-Host "All visible PR checks passed for #$Number."
                    return
                }
                Write-Host "$($pending.Count) PR check(s) are still pending."
            }
        }
        Start-Sleep -Seconds 15
    }
    throw "Timed out waiting for PR checks for #$Number."
}

function Get-WorkflowRuns {
    param([Parameter(Mandatory = $true)][string]$Workflow)
    return @(Read-GhJson @(
        'run', 'list', '--workflow', $Workflow, '--limit', '100',
        '--json', 'databaseId,status,conclusion,headSha,headBranch,event,displayTitle,url,createdAt'
    ))
}

function Wait-WorkflowSuccess {
    param(
        [Parameter(Mandatory = $true)][string]$Workflow,
        [Parameter(Mandatory = $true)][string]$Commit,
        [string]$Tag
    )
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        $runs = @(Get-WorkflowRuns -Workflow $Workflow | Where-Object {
            $_.headSha -ceq $Commit -and $_.event -eq 'push' -and
            ([string]::IsNullOrWhiteSpace($Tag) -or $_.headBranch -ceq $Tag)
        } | Sort-Object createdAt -Descending)
        if ($runs.Count -gt 0) {
            $run = $runs[0]
            if ($run.status -eq 'completed') {
                if ($run.conclusion -ne 'success') {
                    throw "$Workflow failed for $Commit (conclusion: $($run.conclusion))."
                }
                Write-Host "$Workflow succeeded for $Commit."
                return $run
            }
            Write-Host "Workflow run $($run.databaseId) is $($run.status)."
        } else {
            Write-Host "Waiting for $Workflow run for $Commit."
        }
        Start-Sleep -Seconds 15
    }
    throw "Timed out waiting for $Workflow for $Commit."
}

function Get-Release {
    param([Parameter(Mandatory = $true)][string]$Tag)
    return Read-GhJson -Arguments @(
        'release', 'view', $Tag, '--json', 'tagName,isDraft,isPrerelease,body,publishedAt,assets'
    )
}

function Assert-ReleaseAssets {
    param(
        [Parameter(Mandatory = $true)]$Release,
        [Parameter(Mandatory = $true)][string[]]$ExpectedNames
    )
    $names = @($Release.assets | ForEach-Object { [string]$_.name })
    foreach ($name in $ExpectedNames) {
        if ($name -notin $names) {
            throw "Release $($Release.tagName) is missing asset '$name'."
        }
    }
}

function Get-ChecksumFromRelease {
    param(
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$AssetName
    )
    $temp = Join-Path ([IO.Path]::GetTempPath()) (
        "codex-release-check-" + [Guid]::NewGuid().ToString('N')
    )
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        $null = Invoke-External -FilePath $script:Gh -Arguments @(
            'release', 'download', $Tag, '--pattern', 'SHA256SUMS.txt',
            '--dir', $temp, '--clobber'
        )
        $checksumPath = Join-Path $temp 'SHA256SUMS.txt'
        $line = @(Get-Content -LiteralPath $checksumPath | Where-Object {
            $_ -match ("\s" + [regex]::Escape($AssetName) + "\s*$")
        })
        if ($line.Count -ne 1 -or $line[0] -notmatch '^([0-9a-fA-F]{64})\s+') {
            throw "Could not find a unique SHA256 entry for '$AssetName' in $Tag."
        }
        return $Matches[1].ToLowerInvariant()
    } finally {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Assert-ManifestNode {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$AssetProperty,
        [Parameter(Mandatory = $true)][string]$AssetName,
        [Parameter(Mandatory = $true)][string]$Tag,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][long]$ExpectedSize
    )
    if ($Manifest.schemaVersion -ne 1) {
        throw 'Published update manifest schemaVersion is not 1.'
    }
    $node = $Manifest.PSObject.Properties[$Platform].Value
    if ($null -eq $node) {
        throw "Published update manifest is missing '$Platform'."
    }
    if ([string]$node.version -cne $Version -or [string]$node.tag -cne $Tag) {
        throw "Published update manifest '$Platform' version or tag is incorrect."
    }
    if ((Normalize-Text ([string]$node.releaseNotes)) -cne (Normalize-Text $Notes)) {
        throw "Published update manifest '$Platform' release notes do not match the notes file."
    }
    $asset = $node.PSObject.Properties[$AssetProperty].Value
    if ($null -eq $asset -or [string]$asset.name -cne $AssetName) {
        throw "Published update manifest '$Platform' asset is incorrect."
    }
    $expectedUrl = "https://github.com/DDJang/CodexQuotaTray/releases/download/$Tag/$AssetName"
    if ([string]$asset.url -cne $expectedUrl) {
        throw "Published update manifest '$Platform' URL is not canonical."
    }
    if ([string]$asset.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$asset.size -lt 1) {
        throw "Published update manifest '$Platform' asset integrity fields are invalid."
    }
    if (([string]$asset.sha256).ToLowerInvariant() -cne $ExpectedSha256.ToLowerInvariant()) {
        throw "Published update manifest '$Platform' SHA256 does not match the Release checksum."
    }
    if ([long]$asset.size -ne $ExpectedSize) {
        throw "Published update manifest '$Platform' size does not match the Release asset."
    }
}

function Verify-Manifest {
    param(
        [AllowNull()][string]$AndroidNotes,
        [AllowNull()][string]$WindowsNotes,
        [AllowNull()][string]$AndroidSha256,
        [AllowNull()][long]$AndroidSize,
        [AllowNull()][string]$WindowsSha256,
        [AllowNull()][long]$WindowsSize
    )
    $response = Read-GhJson -Arguments @(
        'api', "repos/$($script:RepoName)/contents/update-manifest.json?ref=update-manifest"
    )
    $base64 = ([string]$response.content) -replace '\r?\n', ''
    $jsonText = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($base64))
    $manifest = $jsonText | ConvertFrom-Json
    $verifiedPlatforms = [System.Collections.Generic.List[string]]::new()
    if (Test-PlatformSelected -Name 'Android') {
        Assert-ManifestNode -Manifest $manifest -Platform 'android' -AssetProperty 'apk' -AssetName "CodexQuotaTray-Android-v$Version.apk" -Tag "android-v$Version" -Notes $AndroidNotes -ExpectedSha256 $AndroidSha256 -ExpectedSize $AndroidSize
        $verifiedPlatforms.Add('Android')
    }
    if (Test-PlatformSelected -Name 'Windows') {
        Assert-ManifestNode -Manifest $manifest -Platform 'windows' -AssetProperty 'installer' -AssetName "CodexQuotaTray-$Version-setup.exe" -Tag "windows-v$Version" -Notes $WindowsNotes -ExpectedSha256 $WindowsSha256 -ExpectedSize $WindowsSize
        $verifiedPlatforms.Add('Windows')
    }
    Write-Host "Published update-manifest contains the verified platform node(s): $($verifiedPlatforms -join ', ')."
}

function Verify-Releases {
    param(
        [AllowNull()][string]$AndroidNotes,
        [AllowNull()][string]$WindowsNotes
    )
    $androidTag = "android-v$Version"
    $windowsTag = "windows-v$Version"
    $android = $null
    $windows = $null
    $androidAsset = $null
    $windowsAsset = $null
    $androidSha = ''
    $windowsSha = ''
    $androidSize = 0L
    $windowsSize = 0L

    if (Test-PlatformSelected -Name 'Android') {
        $android = Get-Release -Tag $androidTag
        if ($android.isDraft -or $android.isPrerelease -or
            [string]::IsNullOrWhiteSpace([string]$android.publishedAt)) {
            throw "Release $($android.tagName) is not a published stable release."
        }
        Assert-ReleaseAssets -Release $android -ExpectedNames @(
            "CodexQuotaTray-Android-v$Version.apk", 'SHA256SUMS.txt'
        )
        if ((Normalize-Text ([string]$android.body)) -cne (Normalize-Text $AndroidNotes)) {
            throw 'Android Release body does not match the Android notes file.'
        }
        $androidAsset = @($android.assets | Where-Object {
            $_.name -ceq "CodexQuotaTray-Android-v$Version.apk"
        })[0]
        $androidSha = Get-ChecksumFromRelease -Tag $androidTag -AssetName $androidAsset.name
        $androidSize = [long]$androidAsset.size
        Write-Host "Android APK: $($androidAsset.size) bytes, SHA256 $androidSha"
    }
    if (Test-PlatformSelected -Name 'Windows') {
        $windows = Get-Release -Tag $windowsTag
        if ($windows.isDraft -or $windows.isPrerelease -or
            [string]::IsNullOrWhiteSpace([string]$windows.publishedAt)) {
            throw "Release $($windows.tagName) is not a published stable release."
        }
        Assert-ReleaseAssets -Release $windows -ExpectedNames @(
            "CodexQuotaTray-$Version-win-x64.zip", "CodexQuotaTray-$Version-setup.exe", 'SHA256SUMS.txt'
        )
        if ((Normalize-Text ([string]$windows.body)) -cne (Normalize-Text $WindowsNotes)) {
            throw 'Windows Release body does not match the Windows notes file.'
        }
        $windowsAsset = @($windows.assets | Where-Object {
            $_.name -ceq "CodexQuotaTray-$Version-setup.exe"
        })[0]
        $windowsSha = Get-ChecksumFromRelease -Tag $windowsTag -AssetName $windowsAsset.name
        $windowsSize = [long]$windowsAsset.size
        Write-Host "Windows installer: $($windowsAsset.size) bytes, SHA256 $windowsSha"
    }
    Verify-Manifest -AndroidNotes $AndroidNotes -WindowsNotes $WindowsNotes -AndroidSha256 $androidSha -AndroidSize $androidSize -WindowsSha256 $windowsSha -WindowsSize $windowsSize
}

Write-Step 'Checking tools and repository identity.'
$script:Gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if ([string]::IsNullOrWhiteSpace($script:Gh)) {
    Add-Blocker 'GitHub CLI (gh) is not installed or not on PATH.'
} else {
    $auth = Invoke-External -FilePath $script:Gh -Arguments @('auth', 'status') -AllowFailure
    if ($auth.ExitCode -ne 0) {
        Add-Blocker 'GitHub CLI is not authenticated.'
    }
}

$script:Branch = (Read-ExternalText -FilePath $script:Git -Arguments @(
    'branch', '--show-current'
)).Trim()
$script:HeadSha = (Read-ExternalText -FilePath $script:Git -Arguments @(
    'rev-parse', 'HEAD'
)).Trim()
if ([string]::IsNullOrWhiteSpace($script:Branch)) {
    Add-Blocker 'Release must start from a named, non-detached branch.'
} elseif ($script:Branch -ceq 'main') {
    Add-Blocker 'Release preparation must not run directly on main.'
}
Write-Host "Branch: $($script:Branch)"
Write-Host "HEAD: $($script:HeadSha)"

$repoStatus = (Read-ExternalText -FilePath $script:Git -Arguments @(
    'status', '--short'
)).Trim()
if (-not [string]::IsNullOrWhiteSpace($repoStatus)) {
    Add-Blocker 'Worktree is not clean. Commit or remove unrelated changes before a formal release.'
}
$changedPaths = @($repoStatus -split [Environment]::NewLine | ForEach-Object {
    if ($_ -match '^\S+\s+(.+)$') { $Matches[1].Trim('"') }
} | Where-Object { $_ })
if (@($changedPaths | Where-Object {
    $_ -match '(?i)(^|[\\/])(\.env|.*\.jks$|.*keystore.*|.*secret.*)$'
}).Count -gt 0) {
    Add-Blocker 'The worktree contains a sensitive-looking file path.'
}

Write-Step 'Checking remote refs without mutating refs in DryRun.'
if ($DryRun) {
    Invoke-External -FilePath $script:Git -Arguments @(
        'fetch', '--dry-run', '--tags', 'origin'
    )
} else {
    Invoke-External -FilePath $script:Git -Arguments @('fetch', '--tags', 'origin')
}
$mainCapture = Invoke-Captured -FilePath $script:Git -Arguments @(
    'rev-parse', 'refs/remotes/origin/main'
)
if ($mainCapture.ExitCode -ne 0) {
    Add-Blocker 'origin/main is not available after ref checks.'
} else {
    $script:MainSha = $mainCapture.Text.Trim()
    Write-Host "origin/main: $($script:MainSha)"
    if (-not (Test-Ancestor -Ancestor $script:MainSha -Descendant $script:HeadSha)) {
        Add-Blocker "Current HEAD does not contain origin/main ($($script:MainSha))."
    }
}

if ($null -ne $script:Gh) {
    $repoCapture = Invoke-Captured -FilePath $script:Gh -Arguments @(
        'repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner'
    )
    if ($repoCapture.ExitCode -ne 0) {
        Add-Blocker 'Could not determine the GitHub repository from gh.'
    } else {
        $script:RepoName = $repoCapture.Text.Trim()
        Write-Host "Repository: $($script:RepoName)"
    }
}

Write-Step 'Checking previous platform tags and release notes.'
$androidPrevious = $null
$windowsPrevious = $null
if (Test-PlatformSelected -Name 'Android') {
    $androidPrevious = Get-PreviousTag -Prefix 'android-v' -Head $script:HeadSha
    if ($null -ne $androidPrevious) {
        Write-Host "Previous Android tag: $($androidPrevious.Tag) at $($androidPrevious.Commit)"
        if ([Version]$Version -le $androidPrevious.Version) {
            Add-Blocker "Target Android version $Version is not newer than $($androidPrevious.VersionText)."
        }
    }
}
if (Test-PlatformSelected -Name 'Windows') {
    $windowsPrevious = Get-PreviousTag -Prefix 'windows-v' -Head $script:HeadSha
    if ($null -ne $windowsPrevious) {
        Write-Host "Previous Windows tag: $($windowsPrevious.Tag) at $($windowsPrevious.Commit)"
        if ([Version]$Version -le $windowsPrevious.Version) {
            Add-Blocker "Target Windows version $Version is not newer than $($windowsPrevious.VersionText)."
        }
    }
}

$androidNotesPath = $null
$windowsNotesPath = $null
$androidNotes = ''
$windowsNotes = ''
if (Test-PlatformSelected -Name 'Android') {
    $androidNotesPath = Join-Path $script:RepoRoot "android\release-notes\$Version.md"
    $androidNotes = Read-Notes -Path $androidNotesPath
}
if (Test-PlatformSelected -Name 'Windows') {
    $windowsNotesPath = Join-Path $script:RepoRoot "windows\release-notes\$Version.md"
    $windowsNotes = Read-Notes -Path $windowsNotesPath
}
Assert-WorkflowContracts

Write-Step 'Checking selected platform version files and Android versionCode plan.'
$androidInfo = $null
$windowsInfo = $null
if (Test-PlatformSelected -Name 'Android') {
    $androidInfo = Read-AndroidVersionInfo
}
if (Test-PlatformSelected -Name 'Windows') {
    $windowsInfo = Read-WindowsVersionInfo
}
$androidHistoryMax = 0
$plannedCode = $null
if ((Test-PlatformSelected -Name 'Android') -and $null -ne $androidInfo) {
    foreach ($record in @(Get-PlatformTagRecords -Prefix 'android-v' | Where-Object {
        Test-Ancestor -Ancestor $_.Commit -Descendant $script:HeadSha
    })) {
        $code = Get-AndroidVersionCode -Commit $record.Commit
        if ($null -ne $code -and $code -gt $androidHistoryMax) {
            $androidHistoryMax = $code
        }
    }
    $plannedCode = [Math]::Max($androidInfo.VersionCode, $androidHistoryMax + 1)
    Write-Host "Android version: $($androidInfo.Version) -> $Version; versionCode: $($androidInfo.VersionCode) -> $plannedCode."
}
if ((Test-PlatformSelected -Name 'Windows') -and $null -ne $windowsInfo) {
    Write-Host "Windows version: $($windowsInfo.Version) -> $Version."
}

$androidTag = "android-v$Version"
$windowsTag = "windows-v$Version"
$targetTags = @()
if (Test-PlatformSelected -Name 'Android') {
    $targetTags += $androidTag
}
if (Test-PlatformSelected -Name 'Windows') {
    $targetTags += $windowsTag
}
foreach ($tag in $targetTags) {
    $localState = [pscustomobject]@{ Exists = $false; Commit = $null }
    $localCommitCapture = Invoke-Captured -FilePath $script:Git -Arguments @(
        'rev-parse',
        ($tag + '^{commit}')
    )
    if ($localCommitCapture.ExitCode -eq 0) {
        $localState = [pscustomobject]@{
            Exists = $true
            Commit = $localCommitCapture.Text.Trim()
        }
    }
    $remoteState = if ($null -ne $script:Gh) {
        Get-RemoteTagState -Tag $tag
    } else {
        [pscustomobject]@{ Exists = $false; Commit = $null }
    }
    if ($localState.Exists -or $remoteState.Exists) {
        $commits = @($localState.Commit, $remoteState.Commit | Where-Object { $_ })
        if (@($commits | Select-Object -Unique).Count -gt 1) {
            Add-Blocker "Target tag '$tag' differs between local and origin."
        }
        $existingCommit = @($commits | Select-Object -First 1)
        if ([string]$existingCommit -ne $script:MainSha -and
            [string]$existingCommit -ne $script:HeadSha) {
            Add-Blocker "Target tag '$tag' already points to $existingCommit and cannot be moved."
        }
        Write-Host "Existing target tag: $tag -> $existingCommit"
    }
}

Write-Step 'Preparing release plan.'
Write-Host "Platform: $Platform"
if (Test-PlatformSelected -Name 'Android') {
    Write-Host "Android notes: $androidNotesPath"
}
if (Test-PlatformSelected -Name 'Windows') {
    Write-Host "Windows notes: $windowsNotesPath"
}
Write-Host "Tags: $($targetTags -join ', ')"
Write-Host "Formal run will validate $script:ReleaseScope, commit only selected platform files, push, create or reuse a PR, wait for selected CI, push selected annotated tag(s), then verify selected Release(s) and manifest node(s)."

if ($DryRun) {
    Write-Host 'DRY RUN: no version files, refs, commits, PRs, Releases, or manifest files will be written.'
    if ($script:Blockers.Count -gt 0) {
        Write-Host ("DRY RUN BLOCKED with {0} issue(s)." -f $script:Blockers.Count) -ForegroundColor Yellow
        exit 2
    }
    Write-Host 'DRY RUN PASS: the formal release state machine is eligible to start.'
    exit 0
}

if ($script:Blockers.Count -gt 0) {
    throw ("Release preflight failed with {0} blocker(s)." -f $script:Blockers.Count)
}

Update-VersionFiles -AndroidInfo $androidInfo -WindowsInfo $windowsInfo -VersionCode $plannedCode
Run-LocalValidation

Write-Step 'Creating the release preparation commit.'
$releaseSubject = "release: prepare $script:ReleaseScope $Version"
$pathsToStage = @()
if (Test-PlatformSelected -Name 'Android') {
    $pathsToStage += $androidInfo.Path
}
if (Test-PlatformSelected -Name 'Windows') {
    $pathsToStage += $windowsInfo.Path
}
Invoke-External -FilePath $script:Git -Arguments (@('add', '--') + $pathsToStage)
$staged = @(& $script:Git diff --cached --name-only | ForEach-Object {
    ([string]$_).Trim()
} | Where-Object { $_ })
$allowedStaged = @($pathsToStage | ForEach-Object {
    $_.Substring($script:RepoRoot.Length + 1).Replace('\', '/')
})
foreach ($path in $staged) {
    if ($path -notin $allowedStaged) {
        throw "Unexpected staged release file: $path"
    }
}
if ($staged.Count -eq 0) {
    $resumeNotesPaths = @()
    if (Test-PlatformSelected -Name 'Android') {
        $resumeNotesPaths += $androidNotesPath
    }
    if (Test-PlatformSelected -Name 'Windows') {
        $resumeNotesPaths += $windowsNotesPath
    }
    if (-not (Test-ExistingReleasePreparationState -AndroidInfo $androidInfo -WindowsInfo $windowsInfo -NotesPaths $resumeNotesPaths -CommitMessage $releaseSubject)) {
        throw 'No release preparation changes were staged and no matching release preparation commit is ready to resume.'
    }
    Write-Host 'Existing release preparation commit is valid; resuming without creating an empty commit.'
} else {
    Invoke-External -FilePath $script:Git -Arguments @('commit', '-m', $releaseSubject)
}
Invoke-External -FilePath $script:Git -Arguments @('push', 'origin', $script:Branch)

Write-Step 'Creating or reusing the release PR.'
$pr = Get-OpenReleasePr
if ($null -eq $pr) {
    $body = "Prepare $script:ReleaseScope $Version. Release notes, validation, tags, Releases, and update-manifest verification are limited to the selected platform(s); the release process will wait for merged main CI before tagging."
    $prUrl = Read-ExternalText -FilePath $script:Gh -Arguments @(
        'pr', 'create', '--base', 'main', '--head', $script:Branch,
        '--title', $releaseSubject, '--body', $body
    )
    $pr = Read-GhJson -Arguments @(
        'pr', 'view', $prUrl.Trim(), '--json', 'number,url,headRefName,baseRefName'
    )
}
$prNumber = [int]$pr.number
Write-Host "Release PR: #$prNumber $($pr.url)"
Wait-PrChecks -Number $prNumber

$mergeMethod = Get-MergeConvention
if ($null -eq $mergeMethod) {
    throw 'No safe merge method was identified.'
}
if ($mergeMethod -ceq 'squash') {
    Invoke-External -FilePath $script:Gh -Arguments @(
        'pr', 'merge', ([string]$prNumber), '--squash',
        '--subject', $releaseSubject,
        '--body', "Release preparation for $script:ReleaseScope $Version."
    )
} else {
    Invoke-External -FilePath $script:Gh -Arguments @(
        'pr', 'merge', ([string]$prNumber), '--merge',
        '--subject', $releaseSubject,
        '--body', "Release preparation for $script:ReleaseScope $Version."
    )
}

Write-Step 'Resolving the merged main commit and waiting for main CI.'
Invoke-External -FilePath $script:Git -Arguments @(
    'fetch', 'origin', 'refs/heads/main:refs/remotes/origin/main'
)
$script:MainSha = (Read-ExternalText -FilePath $script:Git -Arguments @(
    'rev-parse', 'refs/remotes/origin/main'
)).Trim()
$mainCiWorkflows = @()
if (Test-PlatformSelected -Name 'Android') {
    $mainCiWorkflows += 'android-ci.yml'
}
if (Test-PlatformSelected -Name 'Windows') {
    $mainCiWorkflows += 'windows-ci.yml'
}
foreach ($workflow in $mainCiWorkflows) {
    Wait-WorkflowSuccess -Workflow $workflow -Commit $script:MainSha | Out-Null
}

Write-Step 'Creating and pushing annotated selected platform tag(s).'
$tagsToPush = [System.Collections.Generic.List[string]]::new()
foreach ($tag in $targetTags) {
    $state = Get-RemoteTagState -Tag $tag
    if ($state.Exists) {
        if ($state.Commit -cne $script:MainSha) {
            throw "Existing remote tag '$tag' does not point to verified main SHA $($script:MainSha)."
        }
        continue
    }
    $local = Invoke-Captured -FilePath $script:Git -Arguments @(
        'rev-parse',
        ($tag + '^{commit}')
    )
    if ($local.ExitCode -eq 0 -and $local.Text.Trim() -cne $script:MainSha) {
        throw "Existing local tag '$tag' does not point to verified main SHA $($script:MainSha)."
    }
    if ($local.ExitCode -ne 0) {
        Invoke-External -FilePath $script:Git -Arguments @(
            'tag', '-a', $tag, $script:MainSha, '-m', "CodexQuotaTray $tag"
        )
    }
    $tagsToPush.Add("refs/tags/$tag")
}
if ($tagsToPush.Count -gt 0) {
    Invoke-External -FilePath $script:Git -Arguments (@('push', '--atomic', 'origin') + $tagsToPush)
}

Write-Step "Waiting for $script:ReleaseScope Release workflow(s)."
if (Test-PlatformSelected -Name 'Android') {
    Wait-WorkflowSuccess -Workflow 'android-release.yml' -Commit $script:MainSha -Tag $androidTag | Out-Null
}
if (Test-PlatformSelected -Name 'Windows') {
    Wait-WorkflowSuccess -Workflow 'windows-release.yml' -Commit $script:MainSha -Tag $windowsTag | Out-Null
}

Write-Step 'Verifying Releases and update-manifest.'
Verify-Releases -AndroidNotes $androidNotes -WindowsNotes $windowsNotes
Write-Host "Release $Version completed successfully at main SHA $($script:MainSha)."
