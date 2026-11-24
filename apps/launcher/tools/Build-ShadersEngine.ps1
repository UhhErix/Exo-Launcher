[CmdletBinding()]
param([switch]$AllowDirty)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$nativeUi = Join-Path $repo 'native-ui'
$crateDir = Join-Path $nativeUi 'crates\exo-shaders'

$dirty = git -C $repo status --porcelain -- 'native-ui/crates/exo-shaders' 2>$null
if ($LASTEXITCODE) { throw "git status failed." }
if ($dirty -and -not $AllowDirty) { throw "exo-shaders has uncommitted changes; commit them first:`n$($dirty -join "`n")" }
$commit = (git -C $repo rev-parse HEAD).Trim()
$short = $commit.Substring(0, 12) + $(if ($dirty) { '-dirty' } else { '' })

Push-Location $nativeUi
try {
    cargo build -p exo-shaders --bin exo-shaders-service --release | Out-Host
    if ($LASTEXITCODE) { throw "cargo build -p exo-shaders failed ($LASTEXITCODE)." }
} finally {
    Pop-Location
}

$built = Join-Path $nativeUi 'target\release\exo-shaders-service.exe'
if (-not (Test-Path $built)) { throw "Missing $built." }

$stage = Join-Path ([IO.Path]::GetTempPath()) "exo-shaders-$($commit.Substring(0, 12))-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$engineDir = Join-Path $stage 'engine'
New-Item -ItemType Directory -Force $engineDir | Out-Null
Copy-Item $built (Join-Path $engineDir 'ExoShaders.Service.exe') -Force
Set-Content -Path (Join-Path $stage 'commit.txt') -Value $short -NoNewline
$rootLicense = Join-Path (Split-Path -Parent (Split-Path -Parent $repo)) 'LICENSE'
if (Test-Path $rootLicense) { Copy-Item $rootLicense (Join-Path $stage 'LICENSE') -Force }
Write-Host "Exo Shaders (MIT Rust) built from $crateDir at $short in $stage"
$stage
