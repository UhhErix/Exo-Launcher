# Builds a release installer: artifacts\ExoLauncher-Setup.exe and its .sha256, and artifacts\whats-new.json
# (this version's What's New); cloud\updates\publish-release.ps1 puts all three in the update mirror.
#   pwsh -File tools\Build-Release.ps1
# Steps: a clean Release publish (self-contained, ReadyToRun), the UI and Exo Mouse
# builds, Exo Shaders' engine (from shaders\) under addons\shaders, Exo Capture under
# addons\capture in the app folder,
# then the NSIS installer and the SHA-256 file the in-app updater checks.
[CmdletBinding()]
param(
    [string]$MakeNsis = 'C:\Program Files (x86)\NSIS\makensis.exe'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$version = (Get-Content VERSION -Raw).Trim()
# This release's What's New, uploaded beside the installer as whats-new.json: the build before it shows these items
# while it offers the update (AppUpdateService.ParseWhatsNew checks the file).
$whatsNew = @(Get-Content ui\src\lib\whatsNew.json -Raw | ConvertFrom-Json) | Where-Object version -EQ $version | Select-Object -First 1
if (-not $whatsNew) { throw "ui\src\lib\whatsNew.json has no entry for $version." }

# The release gate: the telemetry and crash-report tests (what may leave this PC, and in what shape) and the UI's tests,
# run before anything is built or published.
dotnet test --project ExoLauncher.Tests\ExoLauncher.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~CrashSendTests|FullyQualifiedName~AppsConfirmReasonTests|FullyQualifiedName~NetworkDestinationsTests"
if ($LASTEXITCODE) { throw "Telemetry and crash-report tests failed ($LASTEXITCODE); nothing was published." }
Push-Location ui
npm run -s test
if ($LASTEXITCODE) { throw "UI tests failed ($LASTEXITCODE); nothing was published." }
Pop-Location

# A clean publish: an incremental one once shipped plain IL instead of ReadyToRun.
Get-ChildItem -Recurse -Directory ExoLauncher\bin, ExoLauncher\obj -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Release\\.*win-x64\\(publish|R2R)$' } |
    ForEach-Object { Remove-Item -Recurse -Force $_.FullName }

Push-Location ui
npm run -s build
if ($LASTEXITCODE) { throw "UI build failed ($LASTEXITCODE)." }
Pop-Location

# Discord voice (dave.dll, opus.dll, exo_apm.dll and exo-deepfilter.dll from voice\build.ps1) is built once into voice\out; the app project copies them.
if (@('dave.dll', 'opus.dll', 'exo_apm.dll', 'exo-deepfilter.dll') | Where-Object { -not (Test-Path (Join-Path $repo "voice\out\$_")) }) {
    pwsh -NoProfile -File (Join-Path $repo 'voice\build.ps1')
    if ($LASTEXITCODE) { throw "Discord voice build failed ($LASTEXITCODE)." }
}
dotnet publish ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -v q -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)." }
$publish = Get-ChildItem -Recurse -Directory ExoLauncher\bin | Where-Object { $_.FullName -match 'Release\\.*win-x64\\publish$' } | Select-Object -First 1
if (-not $publish) { throw 'No publish folder.' }
# The app copies each voice library only once it is built (CI goes without): a release without one would have no voice.
$noVoice = @('dave.dll', 'opus.dll', 'exo_apm.dll', 'exo-deepfilter.dll') | Where-Object { -not (Test-Path (Join-Path $publish.FullName $_)) }
if ($noVoice) { throw "The app folder lacks Discord voice's $($noVoice -join ', ')." }

# Exo Shaders (GPL-3.0-or-later, shaders\; its source published at getexo.app/source by cloud\updates\addon-source.mjs from
# the same commit): the engine built from the committed tree (tools\Build-ShadersEngine.ps1 refuses uncommitted changes).
$shaders = & (Join-Path $repo 'tools\Build-ShadersEngine.ps1')
$engine = Join-Path $shaders 'engine'
$addon = Join-Path $publish.FullName 'addons\shaders'
New-Item -ItemType Directory -Force -Path $addon | Out-Null
robocopy $engine (Join-Path $addon 'engine') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the Shaders engine failed ($LASTEXITCODE)." }
# The shipped engine must be the Exo build (EXO_BUILD: no request to SCSKiller's or Patreon's servers, no anti-cheat game).
# Read its Core's constant without loading the file, so the copy stays unlocked.
$exoBuild = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $addon 'engine\SCSKiller.Core.dll'))).GetType('SCSKiller.Core.App.ExoBuild').GetField('Enabled').GetRawConstantValue()
if ($exoBuild -ne $true) { throw "The Exo Shaders engine in $engine is not the Exo build (ExoBuild.Enabled is off)." }
# The commit of shaders\ this release bundles, named in the output below (addon-source.mjs build --rev publishes it).
$shadersCommit = (Get-Content (Join-Path $shaders 'commit.txt') -Raw).Trim()
foreach ($notice in 'LICENSE', 'LICENSE-EXCEPTION.txt', 'THIRD-PARTY-NOTICES.md') {
    $source = Join-Path $shaders $notice
    if (Test-Path $source) { Copy-Item $source (Join-Path $addon $notice) -Force }
}
Remove-Item $shaders -Recurse -Force

# Exo Capture (GPL-3.0-or-later, source in capture\): built here, under addons\capture.
pwsh -NoProfile -File (Join-Path $repo 'capture\build.ps1')
if ($LASTEXITCODE) { throw "Exo Capture build failed ($LASTEXITCODE)." }
$captureAddon = Join-Path $publish.FullName 'addons\capture'
New-Item -ItemType Directory -Force -Path $captureAddon | Out-Null
Copy-Item (Join-Path $repo 'capture\out\ExoCapture.exe'), (Join-Path $repo 'capture\LICENSE') $captureAddon -Force
# OBS Studio's signed game capture files (GPL-2.0-or-later, see capture\third_party\obs\README.md).
Copy-Item (Join-Path $repo 'capture\out\obs') $captureAddon -Recurse -Force

# What every release must hold. The voice libraries are only copied once they're built, and releases have gone out
# without them, so a missing part stops the build, and the voice libraries must also load here.
$voice = 'dave.dll', 'opus.dll', 'exo_apm.dll', 'exo-deepfilter.dll', 'sherpa-onnx-c-api.dll', 'onnxruntime.dll'
$parts = $voice + 'ExoLauncher.exe', 'wwwroot\index.html', 'wwwroot\mouse\index.html', 'steam-ipc\ExoLauncher.SteamIpc.exe',
    'addons\shaders\engine\ExoShaders.Service.exe', 'addons\capture\ExoCapture.exe'
$missing = $parts | Where-Object { -not (Test-Path (Join-Path $publish.FullName $_)) }
if ($missing) { throw "The app folder lacks $($missing -join ', ')." }
$dead = $voice | Where-Object {
    $handle = [IntPtr]::Zero
    -not [Runtime.InteropServices.NativeLibrary]::TryLoad((Join-Path $publish.FullName $_), [ref]$handle)
}
if ($dead) { throw "These won't load: $($dead -join ', ')." }

$artifacts = Join-Path $repo 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$setup = Join-Path $artifacts 'ExoLauncher-Setup.exe'
# Exo Setup's window (setup\): Exo's own screens in WebView2. It goes into the app as its uninstaller too.
pwsh -NoProfile -File (Join-Path $repo 'setup\build.ps1')
if ($LASTEXITCODE) { throw "Exo Setup build failed ($LASTEXITCODE)." }
$shell = Join-Path $repo 'setup\out\ExoSetup.exe'
# The install engine (NSIS): the payload, its checks, the atomic swap, shortcuts and registry. Its icon and helper paths are relative to tools\.
$engine = Join-Path $artifacts 'ExoLauncher-Engine.exe'
Push-Location (Join-Path $repo 'tools')
& $MakeNsis /V2 "/DPAYLOAD_DIR=$($publish.FullName)" "/DOUTFILE=$engine" "/DPRODUCT_VERSION=$version" "/DSETUP_SHELL=$shell" ExoLauncher.nsi
$nsis = $LASTEXITCODE
Pop-Location
if ($nsis) { throw "makensis failed ($nsis)." }
# The installer people download: the window with the engine inside it (and the app's size, for real progress).
$appBytes = (Get-ChildItem -Recurse -File $publish.FullName | Measure-Object Length -Sum).Sum
pwsh -NoProfile -File (Join-Path $repo 'setup\build.ps1') -Engine $engine -AppBytes $appBytes -Out $setup
if ($LASTEXITCODE) { throw "Joining Exo Setup failed ($LASTEXITCODE)." }
Remove-Item $engine -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  ExoLauncher-Setup.exe" -NoNewline -Encoding ascii
Set-Content -Path (Join-Path $artifacts 'whats-new.json') -Value ($whatsNew | ConvertTo-Json -Depth 4) -NoNewline -Encoding utf8NoBOM
Write-Output "Exo Launcher ${version}: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB), SHA-256 $hash, Exo Shaders engine from shaders\ at $shadersCommit"
