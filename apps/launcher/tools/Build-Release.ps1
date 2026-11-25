# Builds a release installer: artifacts\ExoLauncher-Setup.exe and its .sha256, artifacts\whats-new.json
# (this version's What's New), and artifacts\files.json and files.pack (the app folder file by file, for the in-app
# updater's file-level update; tools\AppFiles). cloud\updates\publish-release.ps1 puts the installer files in the update
# mirror; -Release vX.Y.Z uploads files.json, files.pack and whats-new.json to that GitHub release (it must exist; gh signed in).
#   pwsh -File tools\Build-Release.ps1 [-Release vX.Y.Z]
# Steps: a clean Release publish (self-contained, ReadyToRun), the UI and Exo Mouse
# builds, Exo Shaders' engine (from shaders\) under addons\shaders, Exo Capture under
# addons\capture in the app folder,
# then the NSIS installer and the SHA-256 file the in-app updater checks.
[CmdletBinding()]
param(
    [string]$MakeNsis = 'C:\Program Files (x86)\NSIS\makensis.exe',
    [string]$Release
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$version = (Get-Content VERSION -Raw).Trim()
# This release's What's New, uploaded beside the installer as whats-new.json: the build before it shows these items
# while it offers the update (AppUpdateService.ParseWhatsNew checks the file).
$whatsNew = @(Get-Content ui\src\lib\whatsNew.json -Raw | ConvertFrom-Json | ForEach-Object { $_ }) | Where-Object { $_.version -eq $version } | Select-Object -First 1
if (-not $whatsNew) { throw "ui\src\lib\whatsNew.json has no entry for $version." }
$whatsNewItems = @($whatsNew.items)
if ($whatsNewItems.Count -lt 1 -or $whatsNewItems.Count -gt 30) {
    throw "ui\src\lib\whatsNew.json entry for $version must have 1 to 30 items (got $($whatsNewItems.Count))."
}
for ($i = 0; $i -lt $whatsNewItems.Count; $i++) {
    $entry = $whatsNewItems[$i]
    if ($entry.icon -isnot [string] -or $entry.icon -notmatch '^[A-Za-z]{1,32}$') {
        throw "ui\src\lib\whatsNew.json ($version) item $i has invalid icon '$($entry.icon)'."
    }
    foreach ($field in @(@{ Name = 'title'; Max = 80 }, @{ Name = 'detail'; Max = 240 })) {
        $value = $entry.($field.Name)
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value.Length -gt $field.Max -or
            ($value.ToCharArray() | Where-Object { [char]::IsControl($_) })) {
            throw "ui\src\lib\whatsNew.json ($version) item $i $($field.Name) failed length/control validation (max $($field.Max))."
        }
    }
}
$whatsNewJson = $whatsNew | ConvertTo-Json -Depth 4
if ($whatsNewJson.Length -gt 65536 -or [Text.Encoding]::UTF8.GetByteCount($whatsNewJson) -gt 65536) {
    throw "ui\src\lib\whatsNew.json ($version) exceeds the 65536-byte limit."
}

$oauthProviders = @(
    @{ Name = 'spotify';   Var = 'EXO_OAUTH_SPOTIFY_CLIENT_ID' }
    @{ Name = 'discord';   Var = 'EXO_OAUTH_DISCORD_CLIENT_ID' }
    @{ Name = 'google';    Var = 'EXO_OAUTH_GOOGLE_CLIENT_ID' }
    @{ Name = 'twitch';    Var = 'EXO_OAUTH_TWITCH_CLIENT_ID' }
    @{ Name = 'battlenet'; Var = 'EXO_OAUTH_BATTLENET_CLIENT_ID' }
    @{ Name = 'microsoft'; Var = 'EXO_OAUTH_MICROSOFT_CLIENT_ID' }
    @{ Name = 'riot';      Var = 'EXO_OAUTH_RIOT_CLIENT_ID' }
    @{ Name = 'epic';      Var = 'EXO_OAUTH_EPIC_CLIENT_ID' }
)
$hasGh = $null -ne (Get-Command gh -ErrorAction SilentlyContinue)
$bakedOAuth = [System.Collections.Generic.List[string]]::new()
$missingOAuth = [System.Collections.Generic.List[string]]::new()
foreach ($p in $oauthProviders) {
    $val = [Environment]::GetEnvironmentVariable($p.Var)
    if ([string]::IsNullOrWhiteSpace($val) -and $hasGh) {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $fetched = (& gh variable get $p.Var 2>$null | Out-String).Trim()
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($fetched)) {
                [Environment]::SetEnvironmentVariable($p.Var, $fetched, 'Process')
                $val = $fetched
            }
        } catch {
        } finally {
            $ErrorActionPreference = $prevEap
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($val)) {
        $bakedOAuth.Add($p.Name)
    } else {
        $missingOAuth.Add($p.Name)
    }
}
$bakedText = if ($bakedOAuth.Count -gt 0) { $bakedOAuth -join ', ' } else { 'none' }
$missingText = if ($missingOAuth.Count -gt 0) { $missingOAuth -join ', ' } else { 'none' }
if ($missingOAuth.Count -gt 0) {
    Write-Warning "OAuth client IDs baked: [$bakedText]; missing: [$missingText]."
} else {
    Write-Host "OAuth client IDs baked: [$bakedText]; missing: [$missingText]."
}

# The release gate: the telemetry and crash-report tests (what may leave this PC, and in what shape) and the UI's tests,
# run before anything is built or published.
dotnet test --project ExoLauncher.Tests\ExoLauncher.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~CrashSendTests|FullyQualifiedName~AppsConfirmReasonTests|FullyQualifiedName~NetworkDestinationsTests"
if ($LASTEXITCODE) { throw "Telemetry and crash-report tests failed ($LASTEXITCODE); nothing was published." }
Push-Location ui
npm run -s test
if ($LASTEXITCODE) { throw "UI tests failed ($LASTEXITCODE); nothing was published." }
Pop-Location
Push-Location native-ui
cargo test --workspace --locked
if ($LASTEXITCODE) { throw "Native UI tests failed ($LASTEXITCODE); nothing was published." }
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
Push-Location native-ui
cargo build --release --locked --bin exo-ui
if ($LASTEXITCODE) { throw "Native UI release build failed ($LASTEXITCODE)." }
cargo build --release --locked -p exo-voice-ml
if ($LASTEXITCODE) { throw "Exo Voice ML release build failed ($LASTEXITCODE)." }
cargo build --release --locked -p exo-audio-apo --lib --bin exo-apo-helper
if ($LASTEXITCODE) { throw "Exo Audio APO release build failed ($LASTEXITCODE)." }
Pop-Location
dotnet publish ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -v q -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)." }
$publish = Get-ChildItem -Recurse -Directory ExoLauncher\bin | Where-Object { $_.FullName -match 'Release\\.*win-x64\\publish$' } | Select-Object -First 1
if (-not $publish) { throw 'No publish folder.' }
# The app copies each voice library only once it is built (CI goes without): a release without one would have no voice.
$noVoice = @('dave.dll', 'opus.dll', 'exo_apm.dll', 'exo-deepfilter.dll') | Where-Object { -not (Test-Path (Join-Path $publish.FullName $_)) }
if ($noVoice) { throw "The app folder lacks Discord voice's $($noVoice -join ', ')." }

$shaders = & (Join-Path $repo 'tools\Build-ShadersEngine.ps1')
$engine = Join-Path $shaders 'engine'
$addon = Join-Path $publish.FullName 'addons\shaders'
New-Item -ItemType Directory -Force -Path $addon | Out-Null
robocopy $engine (Join-Path $addon 'engine') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the Shaders engine failed ($LASTEXITCODE)." }
$shadersCommit = (Get-Content (Join-Path $shaders 'commit.txt') -Raw).Trim()
foreach ($notice in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    $source = Join-Path $shaders $notice
    if (Test-Path $source) { Copy-Item $source (Join-Path $addon $notice) -Force }
}
Remove-Item $shaders -Recurse -Force

# Exo Capture (MIT, capture\LICENSE, source in capture\; OBS hook files stay GPL-2.0-or-later): built here, under addons\capture.
pwsh -NoProfile -File (Join-Path $repo 'capture\build.ps1')
if ($LASTEXITCODE) { throw "Exo Capture build failed ($LASTEXITCODE)." }
$captureAddon = Join-Path $publish.FullName 'addons\capture'
New-Item -ItemType Directory -Force -Path $captureAddon | Out-Null
Copy-Item (Join-Path $repo 'capture\out\ExoCapture.exe'), (Join-Path $repo 'capture\LICENSE') $captureAddon -Force
# OBS Studio's signed game capture files (GPL-2.0-or-later, see capture\third_party\obs\README.md).
Copy-Item (Join-Path $repo 'capture\out\obs') $captureAddon -Recurse -Force

# What every release must hold. The voice libraries are only copied once they're built, and releases have gone out
# without them, so a missing part stops the build, and the voice libraries must also load here.
$voice = 'dave.dll', 'opus.dll', 'exo_apm.dll', 'exo-deepfilter.dll', 'exo_voice_ml.dll', 'onnxruntime.dll'
$nativeAudio = $voice + 'exo_apo.dll'
if (Test-Path (Join-Path $publish.FullName 'sherpa-onnx-c-api.dll')) { throw 'sherpa-onnx-c-api.dll must not ship; exo_voice_ml.dll replaces it.' }
$parts = $nativeAudio + 'ExoLauncher.exe', 'exo-ui.exe', 'exo-apo-helper.exe', 'wwwroot\mouse\index.html', 'wwwroot\changelog.gz', 'steam-ipc\ExoLauncher.SteamIpc.exe',
    'addons\shaders\engine\ExoShaders.Service.exe', 'addons\capture\ExoCapture.exe'
$missing = $parts | Where-Object { -not (Test-Path (Join-Path $publish.FullName $_)) }
if ($missing) { throw "The app folder lacks $($missing -join ', ')." }
$leakedPdbs = @(Get-ChildItem -Recurse -File -Filter '*.pdb' $publish.FullName)
if ($leakedPdbs.Count -gt 0) { throw "Debug symbols leaked into publish: $(($leakedPdbs | ForEach-Object Name) -join ', ')" }
$dead = $nativeAudio | Where-Object {
    $handle = [IntPtr]::Zero
    -not [Runtime.InteropServices.NativeLibrary]::TryLoad((Join-Path $publish.FullName $_), [ref]$handle)
}
if ($dead) { throw "These won't load: $($dead -join ', ')." }

$artifacts = Join-Path $repo 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$symbols = Join-Path $artifacts 'symbols'
if (Test-Path $symbols) { Remove-Item $symbols -Recurse -Force }
New-Item -ItemType Directory -Force -Path $symbols | Out-Null
$symbolCandidates = @(
    (Join-Path $repo 'ExoLauncher\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\ExoLauncher.pdb'),
    (Join-Path $repo 'ExoLauncher.SteamIpc\bin\x64\Release\native-publish\ExoLauncher.SteamIpc.pdb'),
    (Join-Path $repo 'capture\out\ExoCapture.pdb'),
    (Join-Path $repo 'native-ui\target\release\exo_ui.pdb'),
    (Join-Path $repo 'native-ui\target\release\exo_shaders_service.pdb'),
    (Join-Path $repo 'native-ui\target\release\exo_voice_ml.pdb'),
    (Join-Path $repo 'native-ui\target\release\exo_apo.pdb'),
    (Join-Path $repo 'native-ui\target\release\exo_apo_helper.pdb')
)
foreach ($pdb in $symbolCandidates) {
    if (Test-Path $pdb) { Copy-Item $pdb $symbols -Force }
}
$voiceSymbols = Join-Path $repo 'voice\out\symbols'
if (Test-Path $voiceSymbols) {
    Get-ChildItem $voiceSymbols -Filter '*.pdb' -ErrorAction SilentlyContinue | Copy-Item -Destination $symbols -Force
}
$symbolsZip = Join-Path $artifacts 'ExoLauncher-symbols.zip'
if (Test-Path $symbolsZip) { Remove-Item $symbolsZip -Force }
$symbolFiles = @(Get-ChildItem $symbols -Filter '*.pdb' -ErrorAction SilentlyContinue)
if ($symbolFiles.Count -gt 0) {
    Compress-Archive -Path ($symbolFiles | ForEach-Object FullName) -DestinationPath $symbolsZip -CompressionLevel Optimal
}
$setup = Join-Path $artifacts 'ExoLauncher-Setup.exe'
# Exo Setup's window (setup\): Exo's own screens in WebView2. It goes into the app as its uninstaller too.
pwsh -NoProfile -File (Join-Path $repo 'setup\build.ps1')
if ($LASTEXITCODE) { throw "Exo Setup build failed ($LASTEXITCODE)." }
$shell = Join-Path $repo 'setup\out\ExoSetup.exe'
# The very ExoSetup.exe the installer puts in the app folder, for files.pack (joining the installer below builds it again).
$packShell = Join-Path $artifacts 'pack-setup'
New-Item -ItemType Directory -Force -Path $packShell | Out-Null
Copy-Item $shell (Join-Path $packShell 'ExoSetup.exe') -Force
# What the installer does outside the app folder (its script and the helpers it runs), as one SHA-256 over their text with
# LF line ends. The installer records it in Apps & features; the in-app updater replaces only the changed files while a
# release's revision is the same (AppUpdateService.Delta) and runs the installer when it differs. Apply-ExoDelta.ps1 is
# the updater's own and is left out.
$installerInputs = @(Get-Item (Join-Path $repo 'tools\ExoLauncher.nsi')) +
    @(Get-ChildItem (Join-Path $repo 'tools\installer') -Filter *.ps1 | Where-Object Name -NE 'Apply-ExoDelta.ps1') | Sort-Object Name
$installerRevision = & {
    $sha = [Security.Cryptography.SHA256]::Create()
    $lines = $installerInputs | ForEach-Object {
        $text = [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n")
        $_.Name + ' ' + [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    }
    [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))).ToLowerInvariant()
    $sha.Dispose()
}
# The install engine (NSIS): the payload, its checks, the atomic swap, shortcuts and registry. Its icon and helper paths are relative to tools\.
$engine = Join-Path $artifacts 'ExoLauncher-Engine.exe'
Push-Location (Join-Path $repo 'tools')
& $MakeNsis /V2 "/DPAYLOAD_DIR=$($publish.FullName)" "/DOUTFILE=$engine" "/DPRODUCT_VERSION=$version" "/DSETUP_SHELL=$shell" "/DINSTALLER_REVISION=$installerRevision" ExoLauncher.nsi
$nsis = $LASTEXITCODE
Pop-Location
if ($nsis) { throw "makensis failed ($nsis)." }
# The installer people download: the window with the engine inside it (and the app's size, for real progress).
$appBytes = (Get-ChildItem -Recurse -File $publish.FullName | Measure-Object Length -Sum).Sum
pwsh -NoProfile -File (Join-Path $repo 'setup\build.ps1') -Engine $engine -AppBytes $appBytes -Out $setup
if ($LASTEXITCODE) { throw "Joining Exo Setup failed ($LASTEXITCODE)." }
Remove-Item $engine -Force

# files.json and files.pack: every file of the app folder the installer writes (the payload with the payload-manifest.json
# makensis wrote into the publish folder, and ExoSetup.exe), each compressed with zstd on its own.
dotnet run --project (Join-Path $repo 'tools\AppFiles\AppFiles.csproj') -c Release -- build --app $publish.FullName `
    --setup (Join-Path $packShell 'ExoSetup.exe') --version $version --installer $installerRevision --out $artifacts
if ($LASTEXITCODE) { throw "Building files.json and files.pack failed ($LASTEXITCODE)." }
$deltaManifest = Get-Content (Join-Path $artifacts 'files.json') -Raw | ConvertFrom-Json
$deltaPaths = @($deltaManifest.files | ForEach-Object { $_.path })
foreach ($apoAsset in 'exo_apo.dll', 'exo-apo-helper.exe') {
    if ($deltaPaths -notcontains $apoAsset) { throw "Delta pack manifest files.json lacks $apoAsset." }
}
Remove-Item $packShell -Recurse -Force

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  ExoLauncher-Setup.exe" -NoNewline -Encoding ascii
Set-Content -Path (Join-Path $artifacts 'whats-new.json') -Value ($whatsNew | ConvertTo-Json -Depth 4) -NoNewline -Encoding utf8NoBOM
Write-Output "Exo Launcher ${version}: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB), SHA-256 $hash, Exo Shaders engine from shaders\ at $shadersCommit"
Write-Output "File-level update: files.pack $([math]::Round((Get-Item (Join-Path $artifacts 'files.pack')).Length / 1MB, 1)) MB, installer revision $installerRevision"

# The release's file list and pack (and its What's New) on the GitHub release, beside the installer. Never replaced: an
# install may already have read them.
if ($Release) {
    $existing = @(gh release view $Release --repo UhhErix/Exo-Launcher --json assets --jq '.assets[].name')
    if ($LASTEXITCODE) { throw "GitHub release $Release was not found (create it first)." }
    foreach ($asset in 'files.json', 'files.pack', 'whats-new.json') {
        if ($existing -contains $asset) { throw "$asset is already on $Release; delete it there first if this build replaces it." }
        gh release upload $Release (Join-Path $artifacts $asset) --repo UhhErix/Exo-Launcher
        if ($LASTEXITCODE) { throw "Uploading $asset to $Release failed ($LASTEXITCODE)." }
    }
}
