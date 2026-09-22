$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$git = (Get-Command git -ErrorAction Stop).Source
$originalRunnerTemp = $env:RUNNER_TEMP
$originalLocation = Get-Location

function Invoke-TestGit {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $output = @(& $git -C $WorkingDirectory @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $WorkingDirectory $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
}

function Write-TestFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Test-ManifestPublisher {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('android', 'windows')][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Version
    )

    $root = Join-Path ([IO.Path]::GetTempPath()) "codex-manifest-publisher-$Platform-$([Guid]::NewGuid().ToString('N'))"
    $remote = Join-Path $root 'remote.git'
    $repo = Join-Path $root 'repo'
    $runnerTemp = Join-Path $root 'runner'
    $releaseAssets = Join-Path $root 'release-assets'
    try {
        New-Item -ItemType Directory -Force -Path $root, $runnerTemp, $releaseAssets | Out-Null
        & $git init --bare $remote *> $null
        & $git init --initial-branch=main $repo *> $null
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not initialize manifest publisher test repositories.'
        }
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('config', 'user.name', 'Manifest Publisher Test') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('config', 'user.email', 'manifest-publisher@example.invalid') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('remote', 'add', 'origin', $remote) | Out-Null

        $fixtureScripts = Join-Path $repo '.github\scripts'
        New-Item -ItemType Directory -Force -Path $fixtureScripts | Out-Null
        Copy-Item -LiteralPath (Join-Path $repoRoot '.github\scripts\publish-release-manifest.ps1') -Destination $fixtureScripts
        Copy-Item -LiteralPath (Join-Path $repoRoot '.github\scripts\update-release-manifest.ps1') -Destination $fixtureScripts
        Copy-Item -LiteralPath (Join-Path $repoRoot '.github\update-manifest.seed.json') -Destination (Join-Path $repo '.github\update-manifest.seed.json')
        Write-TestFile -Path (Join-Path $repo 'README.md') -Content 'manifest publisher fixture'
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('add', '.') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('commit', '-m', 'test: seed manifest publisher fixture') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('push', '--set-upstream', 'origin', 'main') | Out-Null
        $sourceSha = Invoke-TestGit -WorkingDirectory $repo -Arguments @('rev-parse', 'HEAD')

        Invoke-TestGit -WorkingDirectory $repo -Arguments @('switch', '-c', 'update-manifest') | Out-Null
        Copy-Item -LiteralPath (Join-Path $repo '.github\update-manifest.seed.json') -Destination (Join-Path $repo 'update-manifest.json')
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('add', 'update-manifest.json') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('commit', '-m', 'test: seed update manifest') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('push', '--set-upstream', 'origin', 'update-manifest') | Out-Null
        Invoke-TestGit -WorkingDirectory $repo -Arguments @('switch', 'main') | Out-Null

        $tag = if ($Platform -ceq 'android') { "android-v$Version" } else { "windows-v$Version" }
        $assetName = if ($Platform -ceq 'android') {
            "CodexQuotaTray-Android-v$Version.apk"
        } else {
            "CodexQuotaTray-$Version-setup.exe"
        }
        $assetPath = Join-Path $releaseAssets $assetName
        Write-TestFile -Path $assetPath -Content "$Platform release asset $Version"
        $assetSize = (Get-Item -LiteralPath $assetPath).Length
        $assetSha256 = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-TestFile -Path (Join-Path $releaseAssets 'SHA256SUMS.txt') -Content "$assetSha256  $assetName`n"
        $global:CodexManifestFixtureMetadata = @{
            body = "release notes for $Platform"
            publishedAt = '2026-09-22T00:00:00Z'
            assets = @(@{ name = $assetName; size = $assetSize })
        } | ConvertTo-Json -Depth 5 -Compress
        $global:CodexManifestFixtureAssetPath = $assetPath
        $global:CodexManifestFixtureChecksumPath = Join-Path $releaseAssets 'SHA256SUMS.txt'

        function global:gh {
            if ($args.Count -ge 2 -and $args[0] -ceq 'release' -and $args[1] -ceq 'view') {
                $global:LASTEXITCODE = 0
                return $global:CodexManifestFixtureMetadata
            }
            if ($args.Count -ge 2 -and $args[0] -ceq 'release' -and $args[1] -ceq 'download') {
                $directoryIndex = [Array]::IndexOf($args, '--dir')
                if ($directoryIndex -lt 0 -or $directoryIndex + 1 -ge $args.Count) {
                    throw 'Fake gh did not receive a download directory.'
                }
                $destination = [string]$args[$directoryIndex + 1]
                Copy-Item -LiteralPath $global:CodexManifestFixtureAssetPath -Destination $destination
                Copy-Item -LiteralPath $global:CodexManifestFixtureChecksumPath -Destination $destination
                $global:LASTEXITCODE = 0
                return
            }
            throw "Unexpected gh invocation: $($args -join ' ')"
        }

        $env:RUNNER_TEMP = $runnerTemp
        Set-Location -LiteralPath $repo
        & (Join-Path $fixtureScripts 'publish-release-manifest.ps1') `
            -Platform $Platform `
            -Version $Version `
            -Tag $tag `
            -SourceSha $sourceSha

        $manifestJson = & $git --git-dir=$remote show 'refs/heads/update-manifest:update-manifest.json'
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not read the published manifest from the test remote.'
        }
        $manifest = ($manifestJson -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
        $assetProperty = if ($Platform -ceq 'android') { 'apk' } else { 'installer' }
        $node = $manifest[$Platform]
        if ($node.version -cne $Version -or
            $node.tag -cne $tag -or
            $node[$assetProperty].name -cne $assetName -or
            $node[$assetProperty].sha256 -cne $assetSha256 -or
            [long]$node[$assetProperty].size -ne $assetSize) {
            throw "$Platform manifest publisher output is incorrect."
        }
    } finally {
        Set-Location -LiteralPath $originalLocation
        Remove-Item Function:\gh -ErrorAction SilentlyContinue
        Remove-Variable -Name CodexManifestFixtureMetadata -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name CodexManifestFixtureAssetPath -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable -Name CodexManifestFixtureChecksumPath -Scope Global -ErrorAction SilentlyContinue
        $env:RUNNER_TEMP = $originalRunnerTemp
        if (Test-Path -LiteralPath $root) {
            Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Test-ManifestPublisher -Platform windows -Version '9.8.7'
Test-ManifestPublisher -Platform android -Version '9.8.7'
Write-Host 'Shared release manifest publisher tests passed.'
