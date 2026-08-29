[CmdletBinding()]
param(
    [string]$ConfigPath = "",
    [string]$CacheDirectory = "",
    [Alias("WindowsAppRuntimeInstaller", "WindowsAppRuntimeInstallerPath")]
    [string]$InstallerPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Require-ConfigValue(
    [pscustomobject]$Config,
    [string]$Name)
{
    $property = $Config.PSObject.Properties[$Name]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "Windows App Runtime config is missing '$Name'."
    }

    return [string]$property.Value
}

function Assert-OfficialRuntimeConfig([pscustomobject]$Config) {
    $version = Require-ConfigValue $Config "version"
    $architecture = Require-ConfigValue $Config "architecture"
    $filename = Require-ConfigValue $Config "filename"
    $sourceUrl = Require-ConfigValue $Config "sourceUrl"
    $downloadUrl = Require-ConfigValue $Config "downloadUrl"
    $sha256 = Require-ConfigValue $Config "sha256"

    if ($architecture -cne "x64") {
        throw "Only the fixed x64 Windows App Runtime installer is supported; config selected '$architecture'."
    }
    if ($filename -cne "WindowsAppRuntimeInstall-x64.exe") {
        throw "Windows App Runtime config selected an unexpected installer filename '$filename'."
    }
    if ($sourceUrl -notmatch '^https://aka\.ms/windowsappsdk/[^/]+/[^/]+/windowsappruntimeinstall-x64\.exe$') {
        throw "Windows App Runtime sourceUrl is not the fixed Microsoft aka.ms x64 release URL."
    }
    if ($downloadUrl -notmatch '^https://download\.microsoft\.com/download/[^/]+/WindowsAppRuntimeInstall-x64\.exe$') {
        throw "Windows App Runtime downloadUrl is not a fixed Microsoft download URL."
    }
    if ($sourceUrl -match '(?i)latest' -or $downloadUrl -match '(?i)latest') {
        throw "Windows App Runtime source URLs must not use latest aliases."
    }
    if ($sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "Windows App Runtime config sha256 must contain exactly 64 hexadecimal characters."
    }

    $authenticode = $Config.PSObject.Properties["authenticode"]
    if ($null -eq $authenticode -or $null -eq $authenticode.Value) {
        throw "Windows App Runtime config is missing Authenticode publisher details."
    }
    foreach ($name in @("subject", "issuer", "thumbprint")) {
        $value = $authenticode.Value.PSObject.Properties[$name]
        if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value.Value)) {
            throw "Windows App Runtime config Authenticode details are missing '$name'."
        }
    }

    return [pscustomobject]@{
        Version = $version
        Architecture = $architecture
        Filename = $filename
        SourceUrl = $sourceUrl
        DownloadUrl = $downloadUrl
        Sha256 = $sha256.ToUpperInvariant()
        SignerSubject = [string]$authenticode.Value.subject
        SignerIssuer = [string]$authenticode.Value.issuer
        SignerThumbprint = ([string]$authenticode.Value.thumbprint).ToUpperInvariant()
    }
}

function Test-ValidatedRuntimeInstaller(
    [string]$Path,
    [pscustomobject]$RuntimeConfig)
{
    $resolvedPath = Get-FullPath $Path
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Windows App Runtime installer was not found: $resolvedPath"
    }

    $actualHash = (Get-FileHash -LiteralPath $resolvedPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $RuntimeConfig.Sha256) {
        throw "Windows App Runtime installer SHA-256 mismatch; refusing to package it."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $resolvedPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Windows App Runtime installer Authenticode validation failed: $($signature.Status)."
    }
    if ($null -eq $signature.SignerCertificate) {
        throw "Windows App Runtime installer has no Authenticode signer certificate."
    }
    if ($signature.SignerCertificate.Subject -cne $RuntimeConfig.SignerSubject `
        -or $signature.SignerCertificate.Issuer -cne $RuntimeConfig.SignerIssuer `
        -or $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne $RuntimeConfig.SignerThumbprint) {
        throw "Windows App Runtime installer Authenticode publisher does not match the pinned Microsoft certificate."
    }

    return [pscustomobject]@{
        Path = $resolvedPath
        Version = $RuntimeConfig.Version
        Architecture = $RuntimeConfig.Architecture
        Filename = $RuntimeConfig.Filename
        Sha256 = $actualHash
        SizeBytes = (Get-Item -LiteralPath $resolvedPath).Length
        SignatureStatus = [string]$signature.Status
        SignerSubject = $signature.SignerCertificate.Subject
        SignerIssuer = $signature.SignerCertificate.Issuer
        SignerThumbprint = $signature.SignerCertificate.Thumbprint
    }
}

$windowsRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$repoRoot = Get-FullPath (Join-Path $windowsRoot "..")
$resolvedConfigPath = if ($ConfigPath) {
    Get-FullPath $ConfigPath
} else {
    Join-Path $windowsRoot "installer\windows-app-runtime.json"
}
if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
    throw "Windows App Runtime config was not found: $resolvedConfigPath"
}

$config = Get-Content -LiteralPath $resolvedConfigPath -Raw | ConvertFrom-Json
$runtimeConfig = Assert-OfficialRuntimeConfig $config

$resolvedInstallerPath = $null
if ($InstallerPath) {
    # A caller-supplied file is an offline/cache override only. It is still
    # required to match the exact pinned hash and Microsoft signature below.
    $resolvedInstallerPath = Get-FullPath $InstallerPath
} else {
    $resolvedCacheDirectory = if ($CacheDirectory) {
        Get-FullPath $CacheDirectory
    } else {
        Join-Path $repoRoot "target\windows-app-runtime\$($runtimeConfig.Version)"
    }
    New-Item -ItemType Directory -Path $resolvedCacheDirectory -Force | Out-Null
    $resolvedInstallerPath = Join-Path $resolvedCacheDirectory $runtimeConfig.Filename

    if (-not (Test-Path -LiteralPath $resolvedInstallerPath -PathType Leaf)) {
        $downloadPath = Join-Path $resolvedCacheDirectory "$($runtimeConfig.Filename).download-$PID"
        try {
            Invoke-WebRequest -Uri $runtimeConfig.DownloadUrl -OutFile $downloadPath
            if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) {
                throw "Windows App Runtime download did not create a file."
            }
            Move-Item -LiteralPath $downloadPath -Destination $resolvedInstallerPath -Force
        } finally {
            if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
                Remove-Item -LiteralPath $downloadPath -Force
            }
        }
    }
}

Test-ValidatedRuntimeInstaller $resolvedInstallerPath $runtimeConfig
