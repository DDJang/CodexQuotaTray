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

    $windowsBefore = $afterWindows.windows | ConvertTo-Json -Depth 8 -Compress
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

    $releaseWorkflows = @(
        Join-Path $PSScriptRoot '..\workflows\android-release.yml'
        Join-Path $PSScriptRoot '..\workflows\windows-release.yml'
    )
    foreach ($workflowPath in $releaseWorkflows) {
        $workflow = (Get-Content -LiteralPath $workflowPath -Raw).TrimStart([char]0xFEFF)
        if ($workflow -cnotmatch '(?m)^concurrency:\r?\n  group: update-manifest-publish\r?\n  cancel-in-progress: false\r?$') {
            throw "Release workflow does not use the shared non-cancelling manifest concurrency group: $workflowPath"
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
