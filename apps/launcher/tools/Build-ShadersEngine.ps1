# Builds Exo Shaders for a release: the engine service from shaders\ (GPL-3.0-or-later, Exo's fork of SCSKiller; its
# source is published at getexo.app/source by cloud\updates\addon-source.mjs, from the same commit), through
# shaders\engine\build\publish-service.ps1 (the Exo build, with the native files kept in shaders\engine\prebuilt).
# The source published must be what shipped, so a release refuses a shaders\ with changes that aren't committed
# (-AllowDirty is for a local try, never a release). Prints the staging folder, which holds engine\, the licence files
# and commit.txt (the commit it was built from).
#   pwsh -File tools\Build-ShadersEngine.ps1 [-AllowDirty]
[CmdletBinding()]
param([switch]$AllowDirty)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$shaders = Join-Path $repo 'shaders'

$dirty = git -C $shaders status --porcelain -- . 2>$null
if ($LASTEXITCODE) { throw "git status in $shaders failed." }
if ($dirty -and -not $AllowDirty) { throw "shaders\ has changes that aren't committed; commit them first (the published source must be what ships):`n$($dirty -join "`n")" }
$commit = (git -C $shaders rev-parse HEAD).Trim()
$short = $commit.Substring(0, 12) + $(if ($dirty) { '-dirty' } else { '' })

$stage = Join-Path ([IO.Path]::GetTempPath()) "exo-shaders-$($commit.Substring(0, 12))-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item -ItemType Directory -Force $stage | Out-Null
pwsh -NoProfile -File (Join-Path $shaders 'engine\build\publish-service.ps1') -Out (Join-Path $stage 'engine') -SymbolsOut (Join-Path $stage 'symbols') | Out-Host
if ($LASTEXITCODE) { throw "publish-service.ps1 failed ($LASTEXITCODE)." }
foreach ($notice in 'LICENSE', 'LICENSE-EXCEPTION.txt', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $shaders "engine\$notice") (Join-Path $stage $notice) -Force
}
Set-Content -Path (Join-Path $stage 'commit.txt') -Value $short -NoNewline
if (-not (Test-Path (Join-Path $stage 'engine\ExoShaders.Service.exe'))) { throw "No ExoShaders.Service.exe in $stage." }
Write-Host "Exo Shaders built from shaders\ at $short in $stage"
$stage
