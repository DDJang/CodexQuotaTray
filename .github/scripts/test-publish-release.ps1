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

$validationStart = $source.IndexOf('function Run-LocalValidation', [StringComparison]::Ordinal)
$validationEnd = $source.IndexOf('function Get-OpenReleasePr', [StringComparison]::Ordinal)
if ($validationStart -lt 0 -or $validationEnd -le $validationStart) {
    throw 'Could not isolate Run-LocalValidation for contract checks.'
}
$validation = $source.Substring($validationStart, $validationEnd - $validationStart)
Assert-Matches -Text $validation `
    -Pattern "(?s)if \(Test-PlatformSelected -Name 'Android'\).*?android\\gradlew\.bat" `
    -Message 'Android local validation is not selection-gated.'
Assert-Matches -Text $validation `
    -Pattern "(?s)if \(Test-PlatformSelected -Name 'Windows'\).*?verify-winui\.ps1.*?-Mode', 'Release'" `
    -Message 'Windows local validation is not selection-gated.'

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
Assert-Contains -Text $main -Needle '$mainCiWorkflows = @()' `
    -Message 'Main CI waits must be built from the selected platform set.'
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
concurrency:
  group: update-manifest-publish
  cancel-in-progress: false
jobs:
  release:
    steps:
      - run: echo --notes-file
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
  echo [{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","baseRefName":"main"}]
  exit /b 0
)
if "%1"=="pr" if "%2"=="checks" (
  echo RESUME_REACHED_PR_CHECKS
  exit /b 42
)
exit /b 99
'@
    $unixGhShim = @'
#!/bin/sh
if [ "$1" = "auth" ]; then exit 0; fi
if [ "$1" = "repo" ]; then
  printf '%s\n' 'example/test'
  exit 0
fi
if [ "$1" = "pr" ] && [ "$2" = "list" ]; then
  printf '%s\n' '[{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","baseRefName":"main"}]'
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
  echo [{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","headRefOid":"$preparationHead","baseRefName":"main","mergeCommit":{"oid":"$squashMainHead"}}]
  exit /b 0
)
if "%1"=="run" if "%2"=="list" (
  echo POST_MERGE_MAIN_CI_SENTINEL
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
        throw 'Post-merge resume fixture unexpectedly completed without reaching the main CI sentinel.'
    }
    if ($postMergeText -notmatch 'POST_MERGE_MAIN_CI_SENTINEL') {
        throw "Post-merge resume did not reach main CI. Output: $postMergeText"
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
  echo [{"number":42,"url":"https://github.com/example/test/pull/42","headRefName":"codex/resume-test","headRefOid":"$seedHead","baseRefName":"main","mergeCommit":{"oid":"$squashMainHead"}}]
  exit /b 0
)
if "%1"=="run" if "%2"=="list" (
  echo POST_MERGE_NEGATIVE_MAIN_CI_SENTINEL
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
    if ($negativeText -match 'POST_MERGE_NEGATIVE_MAIN_CI_SENTINEL') {
        throw 'Post-merge identity counterexample incorrectly reached main CI.'
    }
} finally {
    $env:PATH = $originalPath
    if (Test-Path -LiteralPath $resumeRoot) {
        Remove-Item -LiteralPath $resumeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
}

Write-Host 'Platform-independent release script contract tests passed.'
