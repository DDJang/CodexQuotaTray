[CmdletBinding()]
param(
    [ValidateSet("Quick", "Full", "Release")]
    [string]$Mode = "Quick",
    [switch]$TraySmoke,
    [switch]$ProductionTraySmoke,
    [switch]$InteractiveDesktopConfirmed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Test-SdkVersion([string]$Actual, [string]$Required) {
    if ($Actual.Contains('-', [StringComparison]::Ordinal)) {
        return $false
    }

    try {
        $actualVersion = [Version]$Actual
        $requiredVersion = [Version]$Required
    } catch {
        return $false
    }

    return $actualVersion.Major -eq $requiredVersion.Major `
        -and $actualVersion.Minor -eq $requiredVersion.Minor `
        -and [Math]::Floor($actualVersion.Build / 100) -eq [Math]::Floor($requiredVersion.Build / 100) `
        -and $actualVersion.Build -ge $requiredVersion.Build
}

function Get-DotNetVersion([string]$DotNetPath, [string]$WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        $output = @(& $DotNetPath --version 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet --version failed for '$DotNetPath': $($output -join [Environment]::NewLine)"
        }

        return ([string]$output[-1]).Trim()
    } finally {
        Pop-Location
    }
}

function Resolve-RepositoryDotNet(
    [string]$RepositoryRoot,
    [string]$RequiredVersion)
{
    $candidates = @(
        (Join-Path $RepositoryRoot "target\dotnet-sdk-$RequiredVersion-full\dotnet.exe"),
        (Join-Path $RepositoryRoot "target\dotnet-sdk-$RequiredVersion\dotnet.exe")
    )

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $actualVersion = Get-DotNetVersion $candidate $RepositoryRoot
        if (-not (Test-SdkVersion $actualVersion $RequiredVersion)) {
            throw "Repository SDK '$candidate' reports $actualVersion; global.json requires $RequiredVersion with latestPatch."
        }

        return [pscustomobject]@{ Path = $candidate; Version = $actualVersion }
    }

    $pathCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $pathCommand) {
        throw "Required .NET SDK $RequiredVersion was not found in target/ or PATH. The verifier does not download SDKs."
    }

    $pathDotNet = $pathCommand.Source
    $pathVersion = Get-DotNetVersion $pathDotNet $RepositoryRoot
    if (-not (Test-SdkVersion $pathVersion $RequiredVersion)) {
        throw "PATH dotnet '$pathDotNet' selected SDK $pathVersion; global.json requires $RequiredVersion with latestPatch."
    }

    return [pscustomobject]@{ Path = $pathDotNet; Version = $pathVersion }
}

function Invoke-Checked(
    [string]$FilePath,
    [string[]]$ArgumentList,
    [string]$Description)
{
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DiffCheck([string]$RepositoryRoot) {
    & git -C $RepositoryRoot diff --check
    if ($LASTEXITCODE -ne 0) {
        throw "git diff --check failed with exit code $LASTEXITCODE."
    }
}

function Normalize-TestOutput([object[]]$Output) {
    $text = ($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    $ansiPattern = [string][char]27 + '\[[0-?]*[ -/]*[@-~]'
    return [Regex]::Replace($text, $ansiPattern, '')
}

function Get-TestCount([string]$Output, [string[]]$Labels) {
    foreach ($line in ($Output -split "\r\n|\n|\r")) {
        $candidate = $line.Trim()
        foreach ($label in $Labels) {
            $match = [Regex]::Match($candidate, "(?i)^$([Regex]::Escape($label))\s*:\s*(\d+)$")
            if ($match.Success) {
                return [int]$match.Groups[1].Value
            }
        }
    }

    throw "Could not parse the '$($Labels -join '/')' count from the test summary."
}

function Invoke-OfflineTests(
    [string]$DotNetPath,
    [string]$TestProject,
    [string]$NuGetConfig)
{
    $previousLiveBinary = $env:CODEXQUOTATRAY_LIVE_CODEX_BIN
    $env:CODEXQUOTATRAY_LIVE_CODEX_BIN = ""
    try {
        $arguments = @(
            "test", $TestProject,
            "-c", "Release",
            # The solution maps the test project to Any CPU. Let dotnet test
            # use that same default so it resolves the build output in
            # bin\Release instead of looking under bin\x64\Release.
            "-p:RestoreConfigFile=$NuGetConfig",
            "--no-build",
            "--no-restore"
        )
        $testOutput = @(& $DotNetPath @arguments 2>&1)
        $testExitCode = $LASTEXITCODE
        $testOutput | ForEach-Object { Write-Host $_ }
        if ($testExitCode -ne 0) {
            throw "Complete offline tests failed with exit code $testExitCode."
        }

        $text = Normalize-TestOutput $testOutput
        $total = Get-TestCount $text @("Total", "总计")
        $passed = Get-TestCount $text @("Passed", "成功")
        $failed = Get-TestCount $text @("Failed", "失败")
        $skipped = Get-TestCount $text @("Skipped", "已跳过")
        $expectedSkipped = "CodexQuotaTray.Tests.LiveResourceSmokeTests.RealAppServer_SerialReadAndLifecycleMeasurement"
        $expectedReason = "CODEXQUOTATRAY_LIVE_CODEX_BIN is not set; this is an explicit opt-in real-account smoke."

        if ($skipped -ne 1 `
            -or -not $text.Contains("RealAppServer_SerialReadAndLifecycleMeasurement", [StringComparison]::Ordinal) `
            -or -not $text.Contains("CODEXQUOTATRAY_LIVE_CODEX_BIN", [StringComparison]::Ordinal)) {
            throw "Offline test skip set did not match the expected opt-in Live smoke."
        }

        Write-Host "Offline test summary: passed=$passed; failed=$failed; skipped=$skipped; total=$total"
        Write-Host "Skipped test: $expectedSkipped"
        Write-Host "Skipped reason: $expectedReason"
    } finally {
        $env:CODEXQUOTATRAY_LIVE_CODEX_BIN = $previousLiveBinary
    }
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$globalJsonPath = Join-Path $repoRoot "global.json"
$winuiRoot = Join-Path $repoRoot "winui"
$solution = Join-Path $winuiRoot "CodexQuotaTray.WinUI.sln"
$testProject = Join-Path $winuiRoot "tests\CodexQuotaTray.Tests\CodexQuotaTray.Tests.csproj"
$nugetConfig = Join-Path $winuiRoot "NuGet.Config"

if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw "Repository global.json was not found: $globalJsonPath"
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$requiredSdkVersion = [string]$globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($requiredSdkVersion)) {
    throw "global.json does not define sdk.version."
}

$dotnet = Resolve-RepositoryDotNet $repoRoot $requiredSdkVersion
$winuiSdkVersion = Get-DotNetVersion $dotnet.Path $winuiRoot
if ($winuiSdkVersion -ne $dotnet.Version) {
    throw "SDK selection differs by working directory: root=$($dotnet.Version), winui=$winuiSdkVersion."
}

Write-Host "Verification mode: $Mode"
Write-Host "Repository root: $repoRoot"
Write-Host "dotnet path: $($dotnet.Path)"
Write-Host "dotnet version from repository root: $($dotnet.Version)"
Write-Host "dotnet version from winui: $winuiSdkVersion"
Write-Host "global.json: $globalJsonPath"
Write-Host "NuGet.Config: $nugetConfig"

Push-Location $repoRoot
try {
    Invoke-Checked $dotnet.Path @(
        "restore", $solution,
        "--configfile", $nugetConfig,
        "-p:Platform=x64"
    ) "WinUI restore"

    if ($Mode -in @("Full", "Release")) {
        Invoke-Checked $dotnet.Path @(
            "format", $solution,
            "--verify-no-changes",
            "--no-restore",
            "--verbosity", "minimal"
        ) "WinUI format verification"
    }

    Invoke-Checked $dotnet.Path @(
        "build", $solution,
        "-c", "Release",
        "-p:Platform=x64",
        "-p:RestoreConfigFile=$nugetConfig",
        "--no-restore"
    ) "WinUI Release x64 build"

    if ($Mode -in @("Full", "Release")) {
        Invoke-OfflineTests $dotnet.Path $testProject $nugetConfig
    }

    Invoke-DiffCheck $repoRoot

    if ($Mode -eq "Release") {
        $publishScript = Join-Path $PSScriptRoot "publish-winui.ps1"
        & $publishScript -DotNet $dotnet.Path
        if ($LASTEXITCODE -ne 0) {
            throw "WinUI publish script failed with exit code $LASTEXITCODE."
        }

        $publishDirectory = Join-Path $repoRoot "target\winui-publish"
        $publishedExecutable = Join-Path $publishDirectory "codex-quota-tray-gui.exe"
        $publishedIcon = Join-Path $publishDirectory "Assets\AppIcon.ico"
        if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
            throw "Release verification did not find $publishedExecutable."
        }
        if (-not (Test-Path -LiteralPath $publishedIcon -PathType Leaf)) {
            throw "Release verification did not find $publishedIcon."
        }
        if (@(Get-ChildItem -LiteralPath $publishDirectory -Filter "*.dll" -File -Recurse).Count -eq 0) {
            throw "Release verification found no runtime DLLs in $publishDirectory."
        }

        Write-Host "Publish verification passed: $publishDirectory"

        if ($TraySmoke) {
            if (-not $InteractiveDesktopConfirmed) {
                throw "Tray smoke requires -InteractiveDesktopConfirmed on an interactive Windows Explorer desktop with all CodexQuotaTray instances closed."
            }
            if ($null -eq (Get-Process explorer -ErrorAction SilentlyContinue)) {
                throw "Tray smoke requires an interactive Windows Explorer desktop."
            }

            $traySmokeScript = Join-Path $PSScriptRoot "test-winui-tray.ps1"
            if ($ProductionTraySmoke) {
                & $traySmokeScript -Executable $publishedExecutable -Production
            } else {
                & $traySmokeScript -Executable $publishedExecutable
            }
        } elseif ($ProductionTraySmoke) {
            throw "-ProductionTraySmoke requires -TraySmoke. Preview is the default smoke identity."
        }
    } elseif ($TraySmoke -or $ProductionTraySmoke -or $InteractiveDesktopConfirmed) {
        throw "Tray smoke is available only with -Mode Release and explicit confirmation."
    }

    Write-Host "$Mode verification passed."
} finally {
    Pop-Location
}
