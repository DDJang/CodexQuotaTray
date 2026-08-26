$ErrorActionPreference = 'Stop'

$root = Join-Path ([IO.Path]::GetTempPath()) "codexquotatray-manifest-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $manifestPath = Join-Path $root 'update-manifest.json'
    $notesPath = Join-Path $root 'notes.md'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\update-manifest.seed.json') -Destination $manifestPath
    [IO.File]::WriteAllText($notesPath, "release notes`n", [Text.UTF8Encoding]::new($false))

    $before = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $androidBefore = $before.android | ConvertTo-Json -Depth 8 -Compress
    & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
        -Platform windows `
        -ManifestPath $manifestPath `
        -Version '0.7.0' `
        -Tag 'windows-v0.7.0' `
        -AssetName 'CodexQuotaTray-0.7.0-setup.exe' `
        -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.7.0/CodexQuotaTray-0.7.0-setup.exe' `
        -Sha256 ('a' * 64) `
        -AssetSize 123 `
        -ReleaseNotesPath $notesPath `
        -PublishedAt '2026-08-12T10:00:00Z'

    $afterWindows = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if (($afterWindows.android | ConvertTo-Json -Depth 8 -Compress) -cne $androidBefore) {
        throw 'Updating Windows changed the Android manifest node.'
    }
    if ($afterWindows.windows.version -cne '0.7.0' -or $afterWindows.windows.installer.sha256 -cne ('a' * 64)) {
        throw 'Windows manifest node was not updated correctly.'
    }

    & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
        -Platform windows `
        -ManifestPath $manifestPath `
        -Version '0.8.0' `
        -Tag 'windows-v0.8.0' `
        -AssetName 'CodexQuotaTray-0.8.0-setup.exe' `
        -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.8.0/CodexQuotaTray-0.8.0-setup.exe' `
        -Sha256 ('c' * 64) `
        -AssetSize 234 `
        -ReleaseNotesPath $notesPath `
        -PublishedAt '2026-08-12T10:30:00Z'

    $afterWindowsUpgrade = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($afterWindowsUpgrade.windows.version -cne '0.8.0') {
        throw 'Windows manifest did not accept a higher version.'
    }

    $downgradeRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
            -Platform windows `
            -ManifestPath $manifestPath `
            -Version '0.7.0' `
            -Tag 'windows-v0.7.0' `
            -AssetName 'CodexQuotaTray-0.7.0-setup.exe' `
            -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.7.0/CodexQuotaTray-0.7.0-setup.exe' `
            -Sha256 ('d' * 64) `
            -AssetSize 345 `
            -ReleaseNotesPath $notesPath `
            -PublishedAt '2026-08-12T10:45:00Z'
    } catch {
        $downgradeRejected = $true
    }
    if (-not $downgradeRejected) {
        throw 'Manifest writer accepted a lower Windows version.'
    }

    & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
        -Platform windows `
        -ManifestPath $manifestPath `
        -Version '0.8.0' `
        -Tag 'windows-v0.8.0' `
        -AssetName 'CodexQuotaTray-0.8.0-setup.exe' `
        -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.8.0/CodexQuotaTray-0.8.0-setup.exe' `
        -Sha256 ('e' * 64) `
        -AssetSize 456 `
        -ReleaseNotesPath $notesPath `
        -PublishedAt '2026-08-12T11:00:00Z'

    $afterSameWindows = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($afterSameWindows.windows.version -cne '0.8.0' -or $afterSameWindows.windows.installer.sha256 -cne ('e' * 64)) {
        throw 'Manifest writer did not allow an equal Windows version.'
    }

    $windowsBefore = $afterSameWindows.windows | ConvertTo-Json -Depth 8 -Compress
    & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
        -Platform android `
        -ManifestPath $manifestPath `
        -Version '0.8.0' `
        -Tag 'android-v0.8.0' `
        -AssetName 'CodexQuotaTray-Android-v0.8.0.apk' `
        -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/android-v0.8.0/CodexQuotaTray-Android-v0.8.0.apk' `
        -Sha256 ('b' * 64) `
        -AssetSize 456 `
        -ReleaseNotesPath $notesPath `
        -PublishedAt '2026-08-12T11:00:00Z'

    $afterAndroid = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if (($afterAndroid.windows | ConvertTo-Json -Depth 8 -Compress) -cne $windowsBefore) {
        throw 'Updating Android changed the Windows manifest node.'
    }
    if ($afterAndroid.android.version -cne '0.8.0' -or $afterAndroid.android.apk.sha256 -cne ('b' * 64)) {
        throw 'Android manifest node was not updated correctly.'
    }

    $emptyManifestPath = Join-Path $root 'empty-manifest.json'
    [IO.File]::WriteAllText(
        $emptyManifestPath,
        '{"schemaVersion":1,"windows":{},"android":{}}',
        [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
        -Platform windows `
        -ManifestPath $emptyManifestPath `
        -Version '0.1.0' `
        -Tag 'windows-v0.1.0' `
        -AssetName 'CodexQuotaTray-0.1.0-setup.exe' `
        -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.1.0/CodexQuotaTray-0.1.0-setup.exe' `
        -Sha256 ('f' * 64) `
        -AssetSize 123 `
        -ReleaseNotesPath $notesPath `
        -PublishedAt '2026-08-12T12:00:00Z'
    $emptyAfter = Get-Content -LiteralPath $emptyManifestPath -Raw | ConvertFrom-Json -AsHashtable
    if ($emptyAfter.windows.version -cne '0.1.0') {
        throw 'Manifest writer did not initialize an empty platform node.'
    }

    $malformedManifestPath = Join-Path $root 'malformed-manifest.json'
    Copy-Item -LiteralPath $manifestPath -Destination $malformedManifestPath
    $malformed = Get-Content -LiteralPath $malformedManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $malformed.windows.version = '0.8'
    [IO.File]::WriteAllText(
        $malformedManifestPath,
        ($malformed | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    $malformedRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'update-release-manifest.ps1') `
            -Platform windows `
            -ManifestPath $malformedManifestPath `
            -Version '0.9.0' `
            -Tag 'windows-v0.9.0' `
            -AssetName 'CodexQuotaTray-0.9.0-setup.exe' `
            -AssetUrl 'https://github.com/DDJang/CodexQuotaTray/releases/download/windows-v0.9.0/CodexQuotaTray-0.9.0-setup.exe' `
            -Sha256 ('1' * 64) `
            -AssetSize 234 `
            -ReleaseNotesPath $notesPath `
            -PublishedAt '2026-08-12T12:30:00Z'
    } catch {
        $malformedRejected = $true
    }
    if (-not $malformedRejected) {
        throw 'Manifest writer accepted a malformed existing version.'
    }

    $releaseWorkflows = @(
        Join-Path $PSScriptRoot '..\workflows\android-release.yml'
        Join-Path $PSScriptRoot '..\workflows\windows-release.yml'
    )
    foreach ($workflowPath in $releaseWorkflows) {
        $workflow = ((Get-Content -LiteralPath $workflowPath -Raw).TrimStart([char]0xFEFF)) -replace '\r\n', "`n"
        if ($workflow -cmatch '(?m)^concurrency:\s*$') {
            throw "Release workflow must not lock the entire workflow: $workflowPath"
        }
        if ($workflow -cnotmatch '(?ms)^  publish-manifest:\s*$.*?^    needs: release\s*$') {
            throw "Release workflow must have a manifest job depending on release: $workflowPath"
        }
        if ($workflow -cnotmatch '(?ms)^  publish-manifest:\s*$.*?^    concurrency:\s*$\n      group: update-manifest-publish\s*$\n      cancel-in-progress: false\s*$') {
            throw "Release workflow must use the shared non-cancelling manifest concurrency group on publish-manifest: $workflowPath"
        }
        $releaseIndex = $workflow.LastIndexOf('- name: Create public GitHub Release', [StringComparison]::Ordinal)
        $manifestIndex = $workflow.LastIndexOf('- name: Publish unified update manifest', [StringComparison]::Ordinal)
        if ($releaseIndex -lt 0 -or $manifestIndex -le $releaseIndex) {
            throw "Manifest publishing must run after GitHub Release creation: $workflowPath"
        }
        $remainingSteps = $workflow.Substring($manifestIndex + 1)
        if ($remainingSteps.Contains("`n      - name:", [StringComparison]::Ordinal)) {
            throw "Manifest publishing must be the final release workflow step: $workflowPath"
        }
    }

    Write-Host 'Unified update manifest read-modify-write and release ordering tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
