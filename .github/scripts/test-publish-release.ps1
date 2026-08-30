$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repoRoot 'scripts\publish-release.ps1'
$source = [IO.File]::ReadAllText($scriptPath) -replace '\r\n', "`n"

$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$errors)
if ($errors.Count -gt 0) {
    throw "publish-release.ps1 has PowerShell parse errors: $($errors -join '; ')"
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Text.Contains($Needle, [StringComparison]::Ordinal)) {
        throw $Message
    }
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

$platformParameter = @($ast.ParamBlock.Parameters | Where-Object {
    $_.Name.VariablePath.UserPath -ceq 'Platform'
})
if ($platformParameter.Count -ne 1) {
    throw 'publish-release.ps1 must define exactly one Platform parameter.'
}
$platformAttributes = @($platformParameter[0].Attributes)
if (-not @($platformAttributes | Where-Object {
    $_.TypeName.Name -ceq 'Parameter' -and
    @($_.NamedArguments | Where-Object {
        $_.ArgumentName -ceq 'Mandatory' -and $_.Argument.Extent.Text -ceq '$true'
    }).Count -gt 0
}).Count) {
    throw 'Platform must be mandatory so All cannot be selected accidentally.'
}
if (-not @($platformAttributes | Where-Object {
    $_.TypeName.Name -ceq 'ValidateSet' -and
    (@($_.PositionalArguments | ForEach-Object { $_.Extent.Text }) -join ',') -ceq "'Windows','Android','All'"
}).Count) {
    throw 'Platform must use ValidateSet Windows|Android|All.'
}

Assert-Contains -Text $source -Needle '$script:SelectedPlatforms' `
    -Message 'Release script must derive all side effects from SelectedPlatforms.'
Assert-Contains -Text $source -Needle "if (Test-PlatformSelected -Name 'Android')" `
    -Message 'Release script is missing the Android selection guard.'
Assert-Contains -Text $source -Needle "if (Test-PlatformSelected -Name 'Windows')" `
    -Message 'Release script is missing the Windows selection guard.'
Assert-Contains -Text $source -Needle '"android-v$Version"' `
    -Message 'Release script must retain the Android tag format.'
Assert-Contains -Text $source -Needle '"windows-v$Version"' `
    -Message 'Release script must retain the Windows tag format.'

$validationStart = $source.IndexOf('function Run-ReleasePreparationChecks', [StringComparison]::Ordinal)
$validationEnd = $source.IndexOf('function Get-OpenReleasePr', [StringComparison]::Ordinal)
if ($validationStart -lt 0 -or $validationEnd -le $validationStart) {
    throw 'Could not isolate Run-ReleasePreparationChecks for contract checks.'
}
$validation = $source.Substring($validationStart, $validationEnd - $validationStart)
Assert-Contains -Text $validation -Needle '.\.github\scripts\test-update-release-manifest.ps1' `
    -Message 'Release preparation checks must run the manifest writer tests.'
Assert-Contains -Text $validation -Needle '.\.github\scripts\test-publish-release.ps1' `
    -Message 'Release preparation checks must run the release planner tests.'
Assert-Contains -Text $validation -Needle "'diff', '--check'" `
    -Message 'Release preparation checks must run git diff --check.'
if ($validation.Contains('gradlew', [StringComparison]::Ordinal) -or
    $validation.Contains('verify-winui.ps1', [StringComparison]::Ordinal)) {
    throw 'Release preparation checks must not duplicate platform builds or tests owned by PR CI.'
}
$dryRunStart = $source.IndexOf('if ($DryRun) {', [StringComparison]::Ordinal)
$preparationCall = $source.LastIndexOf('Run-ReleasePreparationChecks', [StringComparison]::Ordinal)
if ($dryRunStart -lt 0 -or $preparationCall -lt $dryRunStart) {
    throw 'DryRun must exit before release preparation checks can run.'
}
Assert-Contains -Text $source -Needle 'DRY RUN: skips platform builds/tests;' `
    -Message 'DryRun must explicitly skip platform builds and tests.'
Assert-Contains -Text $source -Needle "'--json', 'number,url,headRefName,headRefOid,baseRefName,baseRefOid'" `
    -Message 'Release PR lookup must record both the PR head and base commit SHAs.'
Assert-Contains -Text $source -Needle 'Assert-ReleasePrHeadMatchesHead' `
    -Message 'Release planner must fail closed when the PR head SHA differs from the refreshed branch HEAD.'
Assert-Contains -Text $source -Needle 'function Get-ReleasePrBaseSha' `
    -Message 'Release planner must validate and record the release PR base SHA.'
Assert-Contains -Text $source -Needle 'function Assert-ReleasePrBaseUnchanged' `
    -Message 'Release planner must guard against main drift after PR checks.'
$waitChecksIndex = $source.IndexOf('Wait-PrChecks -Number $prNumber', [StringComparison]::Ordinal)
$headGuardIndex = $source.IndexOf('Assert-ReleasePrHeadMatchesHead -Pr $pr', [StringComparison]::Ordinal)
$baseGuardIndex = $source.IndexOf('Assert-ReleasePrBaseUnchanged -Number $prNumber', [StringComparison]::Ordinal)
$mergeIndex = $source.IndexOf("Write-Step 'Merging the release PR with squash merge.'", [StringComparison]::Ordinal)
if ($headGuardIndex -lt 0 -or $waitChecksIndex -lt 0 -or $headGuardIndex -ge $waitChecksIndex) {
    throw 'Release planner must guard the PR head SHA before waiting for checks or merging.'
}
if ($baseGuardIndex -le $waitChecksIndex -or $mergeIndex -le $baseGuardIndex) {
    throw 'Release planner must guard the recorded PR base SHA after checks pass and before merge.'
}
Assert-Contains -Text $source -Needle 'synchronize the release branch with main and rerun PR CI' `
    -Message 'Main drift must fail closed with an actionable synchronization message.'

$mainStart = $source.IndexOf("Write-Step 'Checking previous platform tags and release notes.'", [StringComparison]::Ordinal)
if ($mainStart -lt 0) {
    throw 'Could not isolate the release preflight for contract checks.'
}
$main = $source.Substring($mainStart)
Assert-Matches -Text $main `
    -Pattern '(?s)if \(Test-PlatformSelected -Name ''Android''\).*?Read-Notes -Path \$androidNotesPath' `
    -Message 'Android release notes are not selection-gated.'
Assert-Matches -Text $main `
    -Pattern '(?s)if \(Test-PlatformSelected -Name ''Windows''\).*?Read-Notes -Path \$windowsNotesPath' `
    -Message 'Windows release notes are not selection-gated.'
Assert-Contains -Text $main -Needle '$targetTags = @()' `
    -Message 'Target tags must be built from the selected platform set.'
Assert-Contains -Text $main -Needle 'Get-PostMergeReleaseResumeState -MainCommit' `
    -Message 'Release script must verify the merged main commit before tagging.'
Assert-Contains -Text $main -Needle 'Verify-Releases -AndroidNotes $androidNotes -WindowsNotes $windowsNotes' `
    -Message 'Release verification call is missing.'

$manifestStart = $source.IndexOf('function Verify-Manifest', [StringComparison]::Ordinal)
$manifestEnd = $source.IndexOf('function Verify-Releases', [StringComparison]::Ordinal)
if ($manifestStart -lt 0 -or $manifestEnd -le $manifestStart) {
    throw 'Could not isolate Verify-Manifest for contract checks.'
}
$manifest = $source.Substring($manifestStart, $manifestEnd - $manifestStart)
Assert-Matches -Text $manifest `
    -Pattern "(?s)if \(Test-PlatformSelected -Name 'Android'\).*?Assert-ManifestNode.*?-Platform 'android'" `
    -Message 'Android manifest verification is not selection-gated.'
Assert-Matches -Text $manifest `
    -Pattern "(?s)if \(Test-PlatformSelected -Name 'Windows'\).*?Assert-ManifestNode.*?-Platform 'windows'" `
    -Message 'Windows manifest verification is not selection-gated.'

$stageStart = $source.IndexOf('$pathsToStage = @()', [StringComparison]::Ordinal)
$stageEnd = $source.IndexOf('$staged = @(', $stageStart, [StringComparison]::Ordinal)
if ($stageStart -lt 0 -or $stageEnd -le $stageStart) {
    throw 'Could not isolate release preparation staging for contract checks.'
}
$staging = $source.Substring($stageStart, $stageEnd - $stageStart)
if ($staging.Contains('$androidNotesPath', [StringComparison]::Ordinal) -or
    $staging.Contains('$windowsNotesPath', [StringComparison]::Ordinal)) {
    throw 'Release preparation staging must not stage release notes.'
}
$preparationPushIndex = $source.IndexOf("Invoke-External -FilePath `$script:Git -Arguments @('push', 'origin', `$script:Branch)", [StringComparison]::Ordinal)
$headRefreshIndex = $source.LastIndexOf("`$script:HeadSha = (Read-ExternalText -FilePath `$script:Git", [StringComparison]::Ordinal)
if ($preparationPushIndex -lt 0 -or $headRefreshIndex -lt 0 -or $headRefreshIndex -ge $preparationPushIndex) {
    throw 'Release preparation must refresh HEAD after creating or reusing the preparation commit and before pushing it.'
}
Assert-Contains -Text $source -Needle 'Test-ExistingReleasePreparationState' `
    -Message 'Release preparation reruns must validate an existing preparation commit before resuming.'
Assert-Contains -Text $source -Needle 'without creating an empty commit' `
    -Message 'Release preparation reruns must not bypass the resume check with an empty commit.'

function Invoke-ResumeTestGit {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $output = @(& $testGitCommand -C $WorkingDirectory @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $text = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
        throw "Test git command failed with exit code ${exitCode}: git -C $WorkingDirectory $($Arguments -join ' '): $text"
    }
    return (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
}

function Write-ResumeTestFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $path = Join-Path $Root $RelativePath
    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($path, $Content, [Text.UTF8Encoding]::new($false))
    return $path
}

$isWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (-not $isWindowsHost) {
    Write-Host 'Skipping release-preparation resume integration test on a non-Windows host; the publish script uses Windows paths.'
} else {
Write-Host 'Running release-preparation resume regression test.'
$testGitCommand = (Get-Command git -ErrorAction Stop).Source
$testPwshCommand = (Get-Command pwsh -ErrorAction Stop).Source
$resumeRoot = Join-Path ([IO.Path]::GetTempPath()) ('codex-release-resume-' + [Guid]::NewGuid().ToString('N'))
$resumeRepo = Join-Path $resumeRoot 'repo'
$resumeRemote = Join-Path $resumeRoot 'remote.git'
$resumeShim = Join-Path $resumeRoot 'bin'
$targetVersion = '0.8.8'
$originalPath = $env:PATH
try {
    New-Item -ItemType Directory -Force -Path $resumeRoot, $resumeShim | Out-Null
    & $testGitCommand init --bare $resumeRemote *> $null
    & $testGitCommand init --initial-branch=main $resumeRepo *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not initialize the resume regression repository.'
    }
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('config', 'user.name', 'Release Resume Test')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('config', 'user.email', 'release-resume@example.invalid')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('remote', 'add', 'origin', $resumeRemote)

    Write-ResumeTestFile -Root $resumeRepo -RelativePath '.github/workflows/windows-release.yml' -Content @'
name: Windows Release
on:
  push:
    tags:
      - windows-v*
jobs:
  release:
    steps:
      - run: echo --notes-file
  publish-manifest:
    needs: release
    concurrency:
      group: update-manifest-publish
      cancel-in-progress: false
'@
    Write-ResumeTestFile -Root $resumeRepo -RelativePath '.github/workflows/windows-ci.yml' -Content 'name: Windows CI'
    Write-ResumeTestFile -Root $resumeRepo -RelativePath '.github/scripts/update-release-manifest.ps1' -Content '# fixture'
    Write-ResumeTestFile -Root $resumeRepo -RelativePath '.github/scripts/test-update-release-manifest.ps1' -Content '# fixture'
    Write-ResumeTestFile -Root $resumeRepo -RelativePath '.github/scripts/test-publish-release.ps1' -Content '# fixture'
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/scripts/verify-winui.ps1' -Content '# fixture'
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'scripts/publish-release.ps1' -Content ([IO.File]::ReadAllText((Join-Path $repoRoot 'scripts/publish-release.ps1')))
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/src/CodexQuotaTray.App/Business.cs' -Content 'class BusinessBaseline {}'
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj' -Content @'
<Project>
  <PropertyGroup>
    <Version>0.8.7</Version>
  </PropertyGroup>
</Project>
'@
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', '.')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'test: seed release fixtures')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('tag', '-a', 'windows-v0.8.7', '-m', 'windows-v0.8.7')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('push', '--set-upstream', 'origin', 'main', '--tags')
    $seedHead = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'main')

    $businessPath = Join-Path $resumeRepo 'windows/src/CodexQuotaTray.App/Business.cs'
    [IO.File]::WriteAllText($businessPath, 'class BusinessBaseline { public const string ReleaseBehavior = "updated"; }', [Text.UTF8Encoding]::new($false))
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', '-c', 'codex/resume-test')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/src/CodexQuotaTray.App/Business.cs')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'feat: update Windows business behavior')
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/release-notes/0.8.8.md' -Content '# Windows 0.8.8' | Out-Null
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/release-notes/0.8.8.md')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'docs: add Windows 0.8.8 notes')

    $versionPath = Join-Path $resumeRepo 'windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj'
    $versionText = [IO.File]::ReadAllText($versionPath).Replace('<Version>0.8.7</Version>', '<Version>0.8.8</Version>')
    [IO.File]::WriteAllText($versionPath, $versionText, [Text.UTF8Encoding]::new($false))
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'release: prepare Windows 0.8.8')
    $preparationHead = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('push', '--set-upstream', 'origin', 'codex/resume-test')

    $windowsGhShim = @'
@echo off
if "%1"=="auth" exit /b 0
if "%1"=="repo" (
  echo example/test
  exit /b 0
)
if "%1"=="pr" if "%2"=="list" (
  echo [{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","headRefOid":"$preparationHead","baseRefName":"main","baseRefOid":"$seedHead"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="checks" (
  echo RESUME_REACHED_PR_CHECKS
  exit /b 42
)
exit /b 99
'@
    $windowsGhShim = $windowsGhShim.Replace('$seedHead', $seedHead).Replace('$preparationHead', $preparationHead)
    $unixGhShim = @'
#!/bin/sh
if [ "$1" = "auth" ]; then exit 0; fi
if [ "$1" = "repo" ]; then
  printf '%s\n' 'example/test'
  exit 0
fi
if [ "$1" = "pr" ] && [ "$2" = "list" ]; then
  printf '%s\n' '[{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","baseRefName":"main","baseRefOid":"0000000000000000000000000000000000000000"}]'
  exit 0
fi
if [ "$1" = "pr" ] && [ "$2" = "checks" ]; then
  printf '%s\n' 'RESUME_REACHED_PR_CHECKS'
  exit 42
fi
exit 99
'@
    $ghShimPath = if ($isWindowsHost) {
        Write-ResumeTestFile -Root $resumeShim -RelativePath 'gh.cmd' -Content $windowsGhShim
    } else {
        $path = Write-ResumeTestFile -Root $resumeShim -RelativePath 'gh' -Content $unixGhShim
        & chmod +x $path
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not make the Unix gh regression stub executable.'
        }
        $path
    }
    $gitDirectory = Split-Path -Parent $testGitCommand
    $pwshDirectory = Split-Path -Parent $testPwshCommand
    $systemDirectories = if ($isWindowsHost) {
        @(
            (Join-Path $env:SystemRoot 'System32'),
            $env:SystemRoot,
            (Join-Path $env:SystemRoot 'System32\Wbem')
        )
    } else {
        @('/usr/local/bin', '/usr/bin', '/bin')
    }
    $env:PATH = (($resumeShim, $gitDirectory, $pwshDirectory) + $systemDirectories | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    }) -join ';'

    $resumeOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $resumeRepo 'scripts/publish-release.ps1') `
        -Platform Windows -Version $targetVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
            [string]$_
        })
    $resumeExitCode = $LASTEXITCODE
    $resumeText = ($resumeOutput -join [Environment]::NewLine)
    if ($resumeExitCode -eq 0) {
        throw 'Resume regression fixture unexpectedly completed without reaching the PR checks sentinel.'
    }
    if ($resumeText -notmatch 'RESUME_REACHED_PR_CHECKS') {
        throw "Resume regression did not reach the reused PR checks. Output: $resumeText"
    }
    if ($resumeText -match 'No release preparation changes were staged') {
        throw 'Resume regression still failed with the old staged-change error.'
    }
    if ($resumeText -notmatch 'Existing release preparation commit is valid' -or
        $resumeText -notmatch 'Release PR: #42') {
        throw 'Resume regression did not validate the preparation commit and reuse the existing PR.'
    }
    $headAfterResume = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')
    if ($headAfterResume -cne $preparationHead) {
        throw 'Resume regression changed HEAD instead of avoiding an empty commit.'
    }

    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', 'main')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('merge', '--squash', 'codex/resume-test')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'release: prepare Windows 0.8.8')
    $squashMainHead = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('push', 'origin', 'main')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', 'codex/resume-test')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @(
        'fetch', 'origin', 'refs/heads/main:refs/remotes/origin/main'
    )

    $postMergeGhShim = @"
@echo off
if "%1"=="auth" exit /b 0
if "%1"=="repo" (
  echo example/test
  exit /b 0
)
if "%1"=="pr" if "%2"=="list" (
  echo [{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","headRefOid":"$preparationHead","baseRefName":"main","baseRefOid":"$seedHead","mergeCommit":{"oid":"$squashMainHead"}}]
  exit /b 0
)
if "%1"=="run" if "%2"=="list" (
  echo POST_MERGE_RELEASE_WORKFLOW_SENTINEL
  exit /b 42
)
if "%1"=="pr" (
  echo POST_MERGE_PR_LOOKUP
  exit /b 42
)
exit /b 99
"@
    Write-ResumeTestFile -Root $resumeShim -RelativePath 'gh.cmd' -Content $postMergeGhShim | Out-Null
    $postMergeOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $resumeRepo 'scripts/publish-release.ps1') `
        -Platform Windows -Version $targetVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
            [string]$_
        })
    $postMergeExitCode = $LASTEXITCODE
    $postMergeText = ($postMergeOutput -join [Environment]::NewLine)
    if ($postMergeExitCode -eq 0) {
        throw 'Post-merge resume fixture unexpectedly completed without reaching the Release workflow sentinel.'
    }
    if ($postMergeText -notmatch 'POST_MERGE_RELEASE_WORKFLOW_SENTINEL') {
        throw "Post-merge resume did not reach the Release workflow. Output: $postMergeText"
    }
    if ($postMergeText -match 'Current HEAD does not contain origin/main') {
        throw 'Post-merge resume still failed the origin/main ancestry preflight.'
    }
    if ($postMergeText -match 'POST_MERGE_PR_LOOKUP') {
        throw 'Post-merge resume incorrectly re-entered the PR stage.'
    }
    if ($postMergeText -notmatch 'Verified post-merge release resume') {
        throw 'Post-merge resume did not verify the merged release preparation state.'
    }
    if ($postMergeText -notmatch [regex]::Escape($squashMainHead)) {
        throw 'Post-merge resume did not retain the verified squash merge commit as the release main SHA.'
    }
    $headAfterPostMergeResume = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')
    if ($headAfterPostMergeResume -cne $preparationHead) {
        throw 'Post-merge resume changed the original release branch HEAD.'
    }

    $wrongHeadGhShim = @"
@echo off
if "%1"=="auth" exit /b 0
if "%1"=="repo" (
  echo example/test
  exit /b 0
)
if "%1"=="pr" if "%2"=="list" (
  echo [{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","headRefOid":"$seedHead","baseRefName":"main","baseRefOid":"$seedHead","mergeCommit":{"oid":"$squashMainHead"}}]
  exit /b 0
)
if "%1"=="run" if "%2"=="list" (
  echo POST_MERGE_NEGATIVE_RELEASE_SENTINEL
  exit /b 42
)
exit /b 99
"@
    Write-ResumeTestFile -Root $resumeShim -RelativePath 'gh.cmd' -Content $wrongHeadGhShim | Out-Null
    $negativeOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $resumeRepo 'scripts/publish-release.ps1') `
        -Platform Windows -Version $targetVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
            [string]$_
        })
    $negativeExitCode = $LASTEXITCODE
    $negativeText = ($negativeOutput -join [Environment]::NewLine)
    if ($negativeExitCode -eq 0) {
        throw 'Post-merge identity counterexample unexpectedly passed.'
    }
    if ($negativeText -notmatch 'Post-merge release resume could not be verified') {
        throw "Post-merge identity counterexample did not fail closed. Output: $negativeText"
    }
    if ($negativeText -match 'POST_MERGE_NEGATIVE_RELEASE_SENTINEL') {
        throw 'Post-merge identity counterexample incorrectly reached the Release workflow.'
    }

    Write-Host 'Running single-run release preparation and post-merge regression test.'
    $firstRunRoot = Join-Path ([IO.Path]::GetTempPath()) ('codex-release-first-run-' + [Guid]::NewGuid().ToString('N'))
    $firstRunRemote = Join-Path $firstRunRoot 'remote.git'
    $firstRunRepo = Join-Path $firstRunRoot 'repo'
    $firstRunShim = Join-Path $firstRunRoot 'bin'
    $firstRunBranch = 'codex/first-run-preparation-test'
    $firstRunVersion = '0.8.11'
    try {
        New-Item -ItemType Directory -Force -Path $firstRunRoot, $firstRunShim | Out-Null
        & $testGitCommand clone --bare --no-local $resumeRemote $firstRunRemote *> $null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not clone the single-run release preparation fixture remote.'
        }
        & $testGitCommand clone --no-local $firstRunRemote $firstRunRepo *> $null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not clone the single-run release preparation fixture.'
        }
        Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('config', 'user.name', 'Release First-Run Test')
        Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('config', 'user.email', 'release-first-run@example.invalid')
        Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('switch', '--track', '-c', 'main', 'origin/main')
        Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('switch', '-c', $firstRunBranch)
        Write-ResumeTestFile -Root $firstRunRepo -RelativePath "windows/release-notes/$firstRunVersion.md" -Content "# Windows $firstRunVersion" | Out-Null
        Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('add', "windows/release-notes/$firstRunVersion.md")
        Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('commit', '-m', "docs: add Windows $firstRunVersion notes")
        $firstRunBaseSha = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('rev-parse', 'main')
        $firstRunStartSha = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('rev-parse', 'HEAD')
        $firstRunStartSubject = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('log', '-1', '--format=%s', 'HEAD')
        if ($firstRunStartSubject -like 'release: prepare*') {
            throw 'Single-run fixture unexpectedly started with a release preparation commit.'
        }

        $firstRunGhShim = @"
@echo off
if "%1"=="auth" exit /b 0
if "%1"=="repo" (
  echo example/test
  exit /b 0
)
if "%1"=="pr" if "%2"=="list" (
  if exist "%~dp0merged.marker" (
    for /f "delims=" %%H in ('git rev-parse HEAD') do for /f "delims=" %%M in ('git rev-parse refs/remotes/origin/main') do echo [{"number":44,"url":"https://github.com/example/test/pull/44","headRefName":"$firstRunBranch","headRefOid":"%%H","baseRefName":"main","baseRefOid":"$firstRunBaseSha","mergeCommit":{"oid":"%%M"}}]
  ) else (
    for /f "delims=" %%H in ('git rev-parse HEAD') do echo [{"number":44,"url":"https://github.com/example/test/pull/44","headRefName":"$firstRunBranch","headRefOid":"%%H","baseRefName":"main","baseRefOid":"$firstRunBaseSha"}]
  )
  exit /b 0
)
if "%1"=="pr" if "%2"=="checks" (
  echo [{"name":"Windows PR","state":"SUCCESS","bucket":"pass","workflow":"windows-ci","link":"https://example.invalid/check"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="merge" (
  git switch main >nul 2>nul
  if errorlevel 1 exit /b 90
  git merge --squash $firstRunBranch >nul 2>nul
  if errorlevel 1 exit /b 91
  git commit -m "release: prepare Windows $firstRunVersion" >nul 2>nul
  if errorlevel 1 exit /b 92
  git push origin main >nul 2>nul
  if errorlevel 1 exit /b 93
  git switch $firstRunBranch >nul 2>nul
  if errorlevel 1 exit /b 94
  >"%~dp0merged.marker" echo merged
  exit /b 0
)
if "%1"=="run" if "%2"=="list" (
  echo FIRST_RUN_RELEASE_WORKFLOW_SENTINEL
  exit /b 42
)
exit /b 99
"@
        Write-ResumeTestFile -Root $firstRunShim -RelativePath 'gh.cmd' -Content $firstRunGhShim | Out-Null
        $env:PATH = (($firstRunShim, $gitDirectory, $pwshDirectory) + $systemDirectories | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        }) -join ';'

        $firstRunOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $firstRunRepo 'scripts/publish-release.ps1') `
            -Platform Windows -Version $firstRunVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
                [string]$_
            })
        $firstRunExitCode = $LASTEXITCODE
        $firstRunText = ($firstRunOutput -join [Environment]::NewLine)
        if ($firstRunExitCode -eq 0) {
            throw 'Single-run release preparation fixture unexpectedly completed without reaching the Release workflow sentinel.'
        }
        if ($firstRunText -notmatch 'FIRST_RUN_RELEASE_WORKFLOW_SENTINEL') {
            throw "Single-run release preparation did not reach the Release workflow after post-merge verification. Output: $firstRunText"
        }
        if ($firstRunText -match 'headRefOid .* does not match release branch HEAD') {
            throw 'Single-run release preparation used a stale branch HEAD for PR identity.'
        }
        if ($firstRunText -notmatch 'Creating the release preparation commit\.' -or
            $firstRunText -notmatch 'Release PR: #44' -or
            $firstRunText -notmatch 'Creating and pushing annotated selected platform tag\(s\)\.') {
            throw "Single-run release preparation did not create the preparation commit, merge the PR, and pass post-merge verification in one run. Output: $firstRunText"
        }
        $firstRunHead = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('rev-parse', 'HEAD')
        if ($firstRunHead -ceq $firstRunStartSha) {
            throw 'Single-run release preparation did not create a new preparation commit.'
        }
        $firstRunHeadSubject = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('log', '-1', '--format=%s', 'HEAD')
        if ($firstRunHeadSubject -cne "release: prepare Windows $firstRunVersion") {
            throw 'Single-run release preparation did not leave the expected preparation commit on the release branch.'
        }
        $firstRunMainHead = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('rev-parse', 'refs/remotes/origin/main')
        $firstRunMainSubject = Invoke-ResumeTestGit -WorkingDirectory $firstRunRepo -Arguments @('log', '-1', '--format=%s', $firstRunMainHead)
        if ($firstRunMainSubject -cne "release: prepare Windows $firstRunVersion") {
            throw 'Single-run release preparation fixture did not create the expected squash merge on main.'
        }
    } finally {
        $env:PATH = (($resumeShim, $gitDirectory, $pwshDirectory) + $systemDirectories | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        }) -join ';'
        if (Test-Path -LiteralPath $firstRunRoot) {
            Remove-Item -LiteralPath $firstRunRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host 'Running release PR base/main SHA guard regression tests.'
    $baseGuardBranch = 'codex/base-guard-test'
    $baseGuardVersion = '0.8.9'
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', 'main')
    $baseGuardMainSha = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', '-c', $baseGuardBranch)
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/release-notes/0.8.9.md' -Content '# Windows 0.8.9' | Out-Null
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/release-notes/0.8.9.md')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'docs: add Windows 0.8.9 notes')
    $baseGuardVersionPath = Join-Path $resumeRepo 'windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj'
    $baseGuardVersionText = [IO.File]::ReadAllText($baseGuardVersionPath).Replace('<Version>0.8.8</Version>', '<Version>0.8.9</Version>')
    [IO.File]::WriteAllText($baseGuardVersionPath, $baseGuardVersionText, [Text.UTF8Encoding]::new($false))
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'release: prepare Windows 0.8.9')
    $baseGuardPreparationHead = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')

    $baseGuardGhShim = @"
@echo off
if "%1"=="auth" exit /b 0
if "%1"=="repo" (
  echo example/test
  exit /b 0
)
if "%1"=="pr" if "%2"=="list" (
  echo [{"number":43,"url":"https://github.com/example/test/pull/43","headRefName":"$baseGuardBranch","headRefOid":"$baseGuardPreparationHead","baseRefName":"main","baseRefOid":"$baseGuardMainSha"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="checks" (
  echo [{"name":"Windows PR","state":"SUCCESS","bucket":"pass","workflow":"windows-ci","link":"https://example.invalid/check"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="merge" (
  echo BASE_GUARD_MERGE_SENTINEL
  exit /b 42
)
exit /b 99
"@
    Write-ResumeTestFile -Root $resumeShim -RelativePath 'gh.cmd' -Content $baseGuardGhShim | Out-Null
    $baseGuardNormalOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $resumeRepo 'scripts/publish-release.ps1') `
        -Platform Windows -Version $baseGuardVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
            [string]$_
        })
    $baseGuardNormalExitCode = $LASTEXITCODE
    $baseGuardNormalText = ($baseGuardNormalOutput -join [Environment]::NewLine)
    if ($baseGuardNormalExitCode -eq 0 -or
        $baseGuardNormalText -notmatch 'BASE_GUARD_MERGE_SENTINEL') {
        throw "Unchanged-main guard regression did not reach the merge sentinel. Output: $baseGuardNormalText"
    }
    if ($baseGuardNormalText -match 'origin/main changed after PR checks') {
        throw 'Unchanged-main guard regression incorrectly rejected an unchanged base.'
    }

    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', 'main')
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'main-guard-drift.txt' -Content 'main moved after PR checks' | Out-Null
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'main-guard-drift.txt')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'test: advance main after PR checks')
    $baseGuardDriftSha = Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('rev-parse', 'HEAD')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', $baseGuardBranch)
    $baseGuardDriftGhShim = @"
@echo off
if "%1"=="auth" exit /b 0
if "%1"=="repo" (
  echo example/test
  exit /b 0
)
if "%1"=="pr" if "%2"=="list" (
  echo [{"number":43,"url":"https://github.com/example/test/pull/43","headRefName":"$baseGuardBranch","headRefOid":"$baseGuardPreparationHead","baseRefName":"main","baseRefOid":"$baseGuardMainSha"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="checks" (
  git -C "$resumeRepo" push origin refs/heads/main:refs/heads/main >nul 2>nul
  echo [{"name":"Windows PR","state":"SUCCESS","bucket":"pass","workflow":"windows-ci","link":"https://example.invalid/check"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="merge" (
  echo BASE_GUARD_DRIFT_MERGE_SENTINEL
  exit /b 42
)
exit /b 99
"@
    Write-ResumeTestFile -Root $resumeShim -RelativePath 'gh.cmd' -Content $baseGuardDriftGhShim | Out-Null
    $baseGuardDriftOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $resumeRepo 'scripts/publish-release.ps1') `
        -Platform Windows -Version $baseGuardVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
            [string]$_
        })
    $baseGuardDriftExitCode = $LASTEXITCODE
    $baseGuardDriftText = ($baseGuardDriftOutput -join [Environment]::NewLine)
    if ($baseGuardDriftExitCode -eq 0 -or
        $baseGuardDriftText -notmatch 'origin/main changed after PR checks') {
        throw "Main-drift guard regression did not fail closed. Output: $baseGuardDriftText"
    }
    if ($baseGuardDriftText -match 'BASE_GUARD_DRIFT_MERGE_SENTINEL') {
        throw 'Main-drift guard regression reached the merge command.'
    }
    if ($baseGuardDriftText -notmatch 'synchronize the release branch with' -or
        $baseGuardDriftText -notmatch 'and rerun PR CI') {
        throw "Main-drift guard regression did not provide the synchronization instruction. Output: $baseGuardDriftText"
    }

    Write-Host 'Running final-HEAD release preparation regression test.'
    $ancestorBranch = 'codex/ancestor-preparation-test'
    $ancestorVersion = '0.8.10'
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', 'main')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('switch', '-c', $ancestorBranch)
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/release-notes/0.8.10.md' -Content '# Windows 0.8.10' | Out-Null
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/release-notes/0.8.10.md')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'docs: add Windows 0.8.10 notes')
    $ancestorVersionText = [IO.File]::ReadAllText($baseGuardVersionPath).Replace('<Version>0.8.8</Version>', '<Version>0.8.10</Version>')
    [IO.File]::WriteAllText($baseGuardVersionPath, $ancestorVersionText, [Text.UTF8Encoding]::new($false))
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/src/CodexQuotaTray.App/CodexQuotaTray.App.csproj')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'release: prepare Windows 0.8.10')
    Write-ResumeTestFile -Root $resumeRepo -RelativePath 'windows/src/CodexQuotaTray.App/Business.cs' -Content 'class BusinessBaseline { public const string ReleaseBehavior = "changed after preparation"; }' | Out-Null
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('add', 'windows/src/CodexQuotaTray.App/Business.cs')
    Invoke-ResumeTestGit -WorkingDirectory $resumeRepo -Arguments @('commit', '-m', 'test: change branch after preparation')
    $ancestorOutput = @(& $testPwshCommand -NoProfile -File (Join-Path $resumeRepo 'scripts/publish-release.ps1') `
        -Platform Windows -Version $ancestorVersion -TimeoutMinutes 5 2>&1 | ForEach-Object {
            [string]$_
        })
    $ancestorExitCode = $LASTEXITCODE
    $ancestorText = ($ancestorOutput -join [Environment]::NewLine)
    if ($ancestorExitCode -eq 0 -or
        $ancestorText -notmatch 'No release preparation changes were staged') {
        throw "Release preparation ancestor regression did not reject a non-final preparation commit. Output: $ancestorText"
    }
    if ($ancestorText -match 'BASE_GUARD_DRIFT_MERGE_SENTINEL') {
        throw 'Release preparation ancestor regression reached the PR stage.'
    }
} finally {
    $env:PATH = $originalPath
    if (Test-Path -LiteralPath $resumeRoot) {
        Remove-Item -LiteralPath $resumeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
}

Write-Host 'Platform-independent release script contract tests passed.'
exit 0
