[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Architecture,
    [Parameter(Mandatory = $true)]
    [string]$PublisherId,
    [Parameter(Mandatory = $true)]
    [string]$FrameworkName,
    [Parameter(Mandatory = $true)]
    [string]$FrameworkMinimumVersion,
    [Parameter(Mandatory = $true)]
    [string]$MainName,
    [Parameter(Mandatory = $true)]
    [string]$MainMinimumVersion,
    [Parameter(Mandatory = $true)]
    [string]$SingletonName,
    [Parameter(Mandatory = $true)]
    [string]$SingletonMinimumVersion,
    [Parameter(Mandatory = $true)]
    [string]$DdlmNamePattern,
    [Parameter(Mandatory = $true)]
    [string]$DdlmMinimumVersion,
    [string]$PackageJson = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-PackageProperty(
    [psobject]$Package,
    [string]$Name)
{
    $property = $Package.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Runtime package probe output is missing '$Name'."
    }

    return $property.Value
}

function Get-PackageString(
    [psobject]$Package,
    [string]$Name)
{
    $value = [string](Get-PackageProperty $Package $Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Runtime package probe output has an empty '$Name'."
    }

    return $value
}

function Get-PackageBoolean(
    [psobject]$Package,
    [string]$Name)
{
    $value = Get-PackageProperty $Package $Name
    if ($value -isnot [bool]) {
        throw "Runtime package probe output has a non-boolean '$Name'."
    }

    return [bool]$value
}

function Get-RequiredVersion([string]$Value, [string]$Role) {
    $parsed = $null
    if (-not [Version]::TryParse($Value, [ref]$parsed)) {
        throw "Runtime package metadata has an invalid $Role minimum version."
    }

    return $parsed
}

function Get-InstalledPackages([string]$Json) {
    if (-not [string]::IsNullOrWhiteSpace($Json)) {
        $parsed = ConvertFrom-Json -InputObject $Json
        if ($null -eq $parsed) {
            throw "Runtime package fixture is empty."
        }

        return @($parsed)
    }

    return @(Get-AppxPackage -ErrorAction Stop)
}

function Test-PackageSet(
    [object[]]$Packages,
    [pscustomobject]$Check,
    [string]$ExpectedArchitecture,
    [string]$ExpectedPublisherId)
{
    $minimumVersion = Get-RequiredVersion $Check.MinimumVersion $Check.Role
    $nameMatches = @(
        $Packages | Where-Object {
            (Get-PackageString $_ "Name") -like $Check.NamePattern
        }
    )
    if ($nameMatches.Count -eq 0) {
        return $false
    }

    foreach ($package in $nameMatches) {
        $name = Get-PackageString $package "Name"
        $publisherId = Get-PackageString $package "PublisherId"
        if ($publisherId -ine $ExpectedPublisherId) {
            continue
        }

        $familyName = Get-PackageString $package "PackageFamilyName"
        if ($familyName -ine "$name`_$ExpectedPublisherId") {
            continue
        }
        if ((Get-PackageString $package "Architecture") -ine $ExpectedArchitecture) {
            continue
        }
        if ((Get-PackageString $package "Status") -ine "Ok") {
            continue
        }
        if (Get-PackageBoolean $package "IsPartiallyStaged") {
            continue
        }
        if (Get-PackageBoolean $package "IsDevelopmentMode") {
            continue
        }
        if (Get-PackageBoolean $package "IsResourcePackage") {
            continue
        }
        if (Get-PackageBoolean $package "IsBundle") {
            continue
        }
        if ((Get-PackageBoolean $package "IsFramework") -ne $Check.IsFramework) {
            continue
        }

        $actualVersion = $null
        if (-not [Version]::TryParse((Get-PackageString $package "Version"), [ref]$actualVersion)) {
            throw "Runtime package probe output has an invalid $($Check.Role) version."
        }
        if ($actualVersion -ge $minimumVersion) {
            return $true
        }
    }

    return $false
}

try {
    $checks = @(
        [pscustomobject]@{
            Role = "Framework"
            NamePattern = $FrameworkName
            MinimumVersion = $FrameworkMinimumVersion
            IsFramework = $true
        }
        [pscustomobject]@{
            Role = "Main"
            NamePattern = $MainName
            MinimumVersion = $MainMinimumVersion
            IsFramework = $false
        }
        [pscustomobject]@{
            Role = "Singleton"
            NamePattern = $SingletonName
            MinimumVersion = $SingletonMinimumVersion
            IsFramework = $false
        }
        [pscustomobject]@{
            Role = "DDLM"
            NamePattern = $DdlmNamePattern
            MinimumVersion = $DdlmMinimumVersion
            IsFramework = $false
        }
    )
    $packages = Get-InstalledPackages $PackageJson

    foreach ($check in $checks) {
        if (-not (Test-PackageSet $packages $check $Architecture $PublisherId)) {
            Write-Output "NOT_READY"
            exit 1
        }
    }

    Write-Output "READY"
    exit 0
} catch {
    [Console]::Error.WriteLine("Windows App Runtime probe failed.")
    exit 2
}
