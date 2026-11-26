[CmdletBinding()]
param(
    [string]$Destination,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = Split-Path -Parent $PSScriptRoot

$pinFile = Join-Path $repo 'ExoLauncher\Services\DiscordClient\Voice\VoiceModels.cs'
$pattern = 'GitHubReleaseAsset\s+\w+\s*=\s*new\(\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)",\s*"([^"]+)",\s*([\d_]+),\s*"([0-9a-f]{64})"'
$pins = @([regex]::Matches((Get-Content $pinFile -Raw), $pattern) | ForEach-Object {
    [pscustomobject]@{
        Owner = $_.Groups[1].Value
        Repo = $_.Groups[2].Value
        Tag = $_.Groups[3].Value
        Name = $_.Groups[4].Value
        Size = [long]($_.Groups[5].Value -replace '_', '')
        Sha256 = $_.Groups[6].Value
    }
})
if ($pins.Count -ne 3) { throw "Expected 3 voice model pins in $pinFile, found $($pins.Count)." }

function Test-Pin($pin, [string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $path).Length -ne $pin.Size) { return $false }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $pin.Sha256.ToUpperInvariant()
}

if (-not $Destination) {
    $Destination = Get-ChildItem -Directory (Join-Path $repo "ExoLauncher\bin\x64\$Configuration") -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'win-x64' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Destination) { throw "No ExoLauncher\bin\x64\$Configuration\<tfm>\win-x64 folder; build Exo first or pass -Destination." }
}

$cache = Join-Path $repo 'artifacts\voice-models'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
foreach ($pin in $pins) {
    $cached = Join-Path $cache $pin.Name
    if (Test-Pin $pin $cached) { continue }
    $partial = "$cached.download"
    $url = "https://github.com/$($pin.Owner)/$($pin.Repo)/releases/download/$($pin.Tag)/$($pin.Name)"
    Write-Host "Downloading $($pin.Name) ($([math]::Round($pin.Size / 1MB, 1)) MB)"
    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
    if (-not (Test-Pin $pin $partial)) {
        Remove-Item -LiteralPath $partial -Force
        throw "$($pin.Name) from $url doesn't match its pinned size and SHA-256."
    }
    Move-Item -LiteralPath $partial $cached -Force
}

$models = Join-Path $Destination 'voice\models'
New-Item -ItemType Directory -Force -Path $models | Out-Null
foreach ($pin in $pins) {
    $target = Join-Path $models $pin.Name
    if (-not (Test-Pin $pin $target)) { Copy-Item -LiteralPath (Join-Path $cache $pin.Name) $target -Force }
    if (-not (Test-Pin $pin $target)) { throw "$target doesn't match its pinned size and SHA-256." }
}
$licenses = Join-Path $Destination 'voice\licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item (Join-Path $repo 'voice\licenses\*') $licenses -Force

$pins | ForEach-Object { "voice\models\$($_.Name)" }
Get-ChildItem -File $licenses | ForEach-Object { "voice\licenses\$($_.Name)" }
