[CmdletBinding()]
param(
    [string]$Cargo = "cargo",
    [switch]$SkipBuild,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Assert-ChildPath([string]$Parent, [string]$Child) {
    $parentPath = (Get-FullPath $Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $childPath = Get-FullPath $Child
    if (-not $childPath.StartsWith(
            $parentPath + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw "refusing to modify a path outside the expected project directory"
    }
}

$repoRoot = Get-FullPath (Join-Path $PSScriptRoot "..")
$targetRoot = Join-Path $repoRoot "target"
$distRoot = if ($OutputDirectory) {
    Get-FullPath $OutputDirectory
} else {
    Join-Path $repoRoot "dist"
}

Push-Location $repoRoot
try {
    $metadataText = & $Cargo metadata --format-version 1 --locked --offline `
        --filter-platform x86_64-pc-windows-msvc
    if ($LASTEXITCODE -ne 0) {
        throw "cargo metadata failed"
    }
    $metadata = $metadataText | ConvertFrom-Json
    $rootPackage = $metadata.packages | Where-Object { $_.id -eq $metadata.resolve.root }
    if (-not $rootPackage) {
        throw "could not find the root Cargo package"
    }
    $version = [string]$rootPackage.version
    $packageName = "CodexQuotaTray-$version-win-x64"

    if (-not $SkipBuild) {
        $remapFlag = "--remap-path-prefix=$repoRoot=."
        & $Cargo rustc --release --locked --bin codex-quota-tray-gui -- $remapFlag
        if ($LASTEXITCODE -ne 0) {
            throw "release build failed"
        }
    }

    $releaseExe = Join-Path $targetRoot "release\codex-quota-tray-gui.exe"
    if (-not (Test-Path -LiteralPath $releaseExe -PathType Leaf)) {
        throw "release executable was not found"
    }

    $stageParent = Join-Path $targetRoot "package"
    $stage = Join-Path $stageParent $packageName
    Assert-ChildPath $targetRoot $stage
    if (Test-Path -LiteralPath $stage) {
        $stageItem = Get-Item -LiteralPath $stage -Force
        if (($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "refusing to replace a reparse-point staging directory"
        }
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    $packageFiles = @(
        @{ Source = $releaseExe; Name = "codex-quota-tray-gui.exe" },
        @{ Source = (Join-Path $repoRoot "packaging\install.ps1"); Name = "install.ps1" },
        @{ Source = (Join-Path $repoRoot "packaging\uninstall.ps1"); Name = "uninstall.ps1" },
        @{ Source = (Join-Path $repoRoot "README.md"); Name = "README.md" },
        @{ Source = (Join-Path $repoRoot "LICENSE"); Name = "LICENSE" },
        @{ Source = (Join-Path $repoRoot "docs\PRIVACY.md"); Name = "PRIVACY.md" },
        @{ Source = (Join-Path $repoRoot "docs\DEPENDENCIES.md"); Name = "DEPENDENCIES.md" }
    )
    foreach ($file in $packageFiles) {
        if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
            throw "required package input is missing: $($file.Name)"
        }
        Copy-Item -LiteralPath $file.Source -Destination (Join-Path $stage $file.Name) -Force
    }

    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $stage "VERSION"), "$version`n", $utf8)

    $notice = [Text.StringBuilder]::new()
    [void]$notice.AppendLine("CodexQuotaTray third-party notices")
    [void]$notice.AppendLine("Generated from locked x86_64-pc-windows-msvc Cargo metadata.")
    foreach ($package in ($metadata.packages | Where-Object { $_.id -ne $rootPackage.id } |
            Sort-Object name, version)) {
        $crateRoot = Split-Path -Parent ([string]$package.manifest_path)
        $licenseFiles = @(Get-ChildItem -LiteralPath $crateRoot -File |
                Where-Object {
                    $_.Name -match '^(LICENSE|COPYING|UNLICENSE|COPYRIGHT)([._-].*)?$'
                } | Sort-Object Name)
        if ($licenseFiles.Count -eq 0) {
            throw "dependency $($package.name) $($package.version) has no bundled license file"
        }
        [void]$notice.AppendLine()
        [void]$notice.AppendLine(("=" * 78))
        [void]$notice.AppendLine("$($package.name) $($package.version)")
        [void]$notice.AppendLine("Declared license: $($package.license)")
        [void]$notice.AppendLine("Repository: $($package.repository)")
        foreach ($licenseFile in $licenseFiles) {
            [void]$notice.AppendLine()
            [void]$notice.AppendLine("--- $($licenseFile.Name) ---")
            [void]$notice.AppendLine((Get-Content -LiteralPath $licenseFile.FullName -Raw))
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $stage "THIRD_PARTY_NOTICES.txt"),
        $notice.ToString(),
        $utf8
    )

    $manifestLines = foreach ($file in (Get-ChildItem -LiteralPath $stage -File | Sort-Object Name)) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    [IO.File]::WriteAllText(
        (Join-Path $stage "MANIFEST.sha256"),
        (($manifestLines -join "`n") + "`n"),
        $utf8
    )

    New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
    $archive = Join-Path $distRoot "$packageName.zip"
    Assert-ChildPath $distRoot $archive
    if (Test-Path -LiteralPath $archive) {
        Remove-Item -LiteralPath $archive -Force
    }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive -CompressionLevel Optimal

    [pscustomobject]@{
        Archive = $archive
        ArchiveBytes = (Get-Item -LiteralPath $archive).Length
        ExecutableBytes = (Get-Item -LiteralPath $releaseExe).Length
        Version = $version
    }
} finally {
    Pop-Location
}
