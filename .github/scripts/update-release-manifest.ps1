param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('windows', 'android')]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [string]$AssetName,

    [Parameter(Mandatory = $true)]
    [string]$AssetUrl,

    [Parameter(Mandatory = $true)]
    [string]$Sha256,

    [Parameter(Mandatory = $true)]
    [long]$AssetSize,

    [Parameter(Mandatory = $true)]
    [string]$ReleaseNotesPath,

    [Parameter(Mandatory = $true)]
    [string]$PublishedAt
)

$ErrorActionPreference = 'Stop'

function ConvertTo-StrictVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ($Value -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "$Label '$Value' is not a strict three-part semantic version."
    }

    return [Version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
}

$newVersion = ConvertTo-StrictVersion -Value $Version -Label 'Version'
$expectedTag = "$Platform-v$Version"
if ($Tag -cne $expectedTag) {
    throw "Tag '$Tag' does not match '$expectedTag'."
}

$expectedAssetName = if ($Platform -ceq 'windows') {
    "CodexQuotaTray-$Version-setup.exe"
} else {
    "CodexQuotaTray-Android-v$Version.apk"
}
if ($AssetName -cne $expectedAssetName) {
    throw "Asset '$AssetName' does not match '$expectedAssetName'."
}

$expectedAssetUrl = "https://github.com/DDJang/CodexQuotaTray/releases/download/$Tag/$AssetName"
if ($AssetUrl -cne $expectedAssetUrl) {
    throw "Asset URL '$AssetUrl' does not match the canonical release asset URL."
}

$normalizedSha256 = $Sha256.Trim().ToLowerInvariant()
if ($normalizedSha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw 'Asset SHA-256 must contain exactly 64 hexadecimal characters.'
}
if ($AssetSize -lt 1) {
    throw 'Asset size must be positive.'
}

$published = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($PublishedAt, [ref]$published)) {
    throw "PublishedAt '$PublishedAt' is invalid."
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.schemaVersion -ne 1 -or -not $manifest.ContainsKey('windows') -or -not $manifest.ContainsKey('android')) {
    throw 'Update manifest must contain schemaVersion 1 and both platform nodes.'
}

$currentNode = $manifest[$Platform]
if ($null -ne $currentNode -and -not ($currentNode -is [hashtable])) {
    throw "Existing $Platform manifest node is malformed."
}
$currentVersionText = if ($null -eq $currentNode) { '' } else { [string]$currentNode['version'] }
if (-not [string]::IsNullOrWhiteSpace($currentVersionText)) {
    $currentVersion = ConvertTo-StrictVersion -Value $currentVersionText -Label "Existing $Platform version"
    if ($newVersion -lt $currentVersion) {
        throw "New $Platform version '$Version' is lower than the existing version '$currentVersionText'."
    }
}

$asset = [ordered]@{
    name = $AssetName
    url = $AssetUrl
    sha256 = $normalizedSha256
    size = $AssetSize
}
$platformNode = [ordered]@{
    version = $Version
    tag = $Tag
    releaseNotes = [IO.File]::ReadAllText($ReleaseNotesPath)
    publishedAt = $published.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$assetProperty = if ($Platform -ceq 'windows') { 'installer' } else { 'apk' }
$platformNode.Insert(2, $assetProperty, $asset)
$manifest[$Platform] = $platformNode

$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($ManifestPath, "$json`n", [Text.UTF8Encoding]::new($false))
