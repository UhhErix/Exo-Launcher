# Puts the Lucid.exe that ships in Exo into addons\lucid of an app folder, unmodified and only if it is exactly the pinned
# file. Lucid is by tekkusai, shipped with the author's permission (THIRD_PARTY_NOTICES.md), and is never committed here.
# Its release replaces one file at one address with each version, so the first good download is kept under artifacts\.
# When tekkusai ships a new version the download stops matching: try it, then pin its version and SHA-256 below (the
# author publishes that at raw.githubusercontent.com/tekkusai/Lucid/main/Lucid.exe.sha256).
#   pwsh -File tools\Get-Lucid.ps1 -AppFolder <publish or app folder>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppFolder)
$ErrorActionPreference = 'Stop'
$version = '6.0.4.0'
$sha256 = 'a011166e12243e20b8759811a6d5d486c4a2eb064c41d3d783f46605a3a7ae1b'
$url = 'https://github.com/tekkusai/Lucid/releases/download/Lucid/Lucid.exe'

$repo = Split-Path -Parent $PSScriptRoot
$kept = Join-Path $repo "artifacts\lucid\$sha256\Lucid.exe"
function Test-Pinned([string]$Path) { (Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $sha256 }

if (-not (Test-Pinned $kept)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $kept) | Out-Null
    $download = "$kept.download"
    Invoke-WebRequest -Uri $url -OutFile $download
    if (-not (Test-Pinned $download)) {
        Remove-Item -LiteralPath $download -Force
        throw "Lucid's release is no longer $version ($sha256). Try the new one, then pin it in tools\Get-Lucid.ps1."
    }
    Move-Item -LiteralPath $download -Destination $kept -Force
}
$folder = Join-Path $AppFolder 'addons\lucid'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Copy-Item -LiteralPath $kept -Destination (Join-Path $folder 'Lucid.exe') -Force
if (-not (Test-Pinned (Join-Path $folder 'Lucid.exe'))) { throw "Lucid.exe didn't copy whole to $folder." }
Write-Output "Lucid $version in $folder"
