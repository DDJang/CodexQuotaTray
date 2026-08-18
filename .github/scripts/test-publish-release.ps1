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

Write-Host 'Platform-independent release script contract tests passed.'
