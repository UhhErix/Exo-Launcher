#Requires -Version 7
param(
    [Parameter(Mandatory)] [string[]] $Scenes,
    [Parameter(Mandatory)] [string] $Out,
    [string] $Tag = 'snap',
    [switch] $NoBuild,
    [string] $Refs,
    [string] $HostExe,
    [string] $DataDir
)

$ErrorActionPreference = 'Stop'
$launcherRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$nativeUi = Join-Path $launcherRoot 'native-ui'
$compare = Join-Path $PSScriptRoot 'parity-compare.py'

$installedData = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ExoLauncher')).TrimEnd('\')
$installedExe = Join-Path $installedData 'app\ExoLauncher.exe'

if (-not $HostExe) {
    $candidates = @(
        (Join-Path $launcherRoot 'ExoLauncher\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\ExoLauncher.exe'),
        (Join-Path $launcherRoot 'ExoLauncher\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\ExoLauncher.exe')
    )
    $HostExe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $HostExe -or -not (Test-Path -LiteralPath $HostExe)) {
    throw 'No repo ExoLauncher.exe found under ExoLauncher\bin\x64\{Debug,Release}. Build the host first.'
}
$HostExe = (Resolve-Path -LiteralPath $HostExe).Path
if ($HostExe -eq $installedExe) {
    throw 'Refused: will not run the installed ExoLauncher.exe.'
}

$dataPath = if ($DataDir) {
    [IO.Path]::GetFullPath($DataDir).TrimEnd('\')
} else {
    Join-Path $env:TEMP ('exo-parity-snap-' + ([guid]::NewGuid().ToString('N').Substring(0, 8)))
}
if ($dataPath -eq $installedData -or $dataPath.StartsWith("$installedData\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refused: DataDir cannot be inside %LOCALAPPDATA%\ExoLauncher.'
}
New-Item -ItemType Directory -Force -Path $dataPath | Out-Null

$refByScene = @{
    'library'     = '01_library_homedeck.png'
    'library-all' = '02_library_all_games.png'
    'game'        = '03_game_page.png'
    'store'       = '04_store.png'
    'discord'     = '05_discord.png'
    'music'       = '06_music.png'
    'pc-devices'  = '07_devices.png'
    'pc'          = '08_exo_ai.png'
    'pc-apps'     = '09_apps.png'
    'settings'    = '10_settings.png'
    'captures'    = '11_captures.png'
    'palette'     = '12_palette.png'
}

$Out = (New-Item -ItemType Directory -Force -Path $Out).FullName
if ($Refs -and (Test-Path -LiteralPath $Refs)) { $Refs = (Resolve-Path -LiteralPath $Refs).Path }
Push-Location $nativeUi
try {
    if (-not $NoBuild) {
        cargo build --bin exo-ui --locked 2>&1 | Select-String -Pattern '^(error|warning: unused)|could not compile|Finished' | Select-Object -First 30 | ForEach-Object Line
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed ($LASTEXITCODE)" }
    }
    $pipe = 'exo-parity-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $prevData = $env:EXO_LAUNCHER_DATA_DIR
    $env:EXO_LAUNCHER_DATA_DIR = $dataPath
    $hostProc = Start-Process -FilePath $HostExe -ArgumentList @("--host-pipe=$pipe") -WorkingDirectory (Split-Path -Parent $HostExe) -WindowStyle Hidden -PassThru
    $env:EXO_LAUNCHER_DATA_DIR = $prevData
    Start-Sleep -Seconds 2
    try {
        foreach ($scene in $Scenes) {
            $png = Join-Path $Out "$Tag-$scene.png"
            $sceneArgs = @()
            if ($scene -ne 'library') { $sceneArgs = @('--scene', $scene) }
            & .\target\debug\exo-ui.exe --pipe $pipe @sceneArgs --snap $png --size 1920x1080 --scale 1
            if (-not (Test-Path -LiteralPath $png)) { "SNAP FAILED: $scene"; continue }
            $ref = $refByScene[$scene]
            if ($Refs -and $ref -and (Test-Path -LiteralPath (Join-Path $Refs $ref))) {
                python -I $compare (Join-Path $Refs $ref) $png (Join-Path $Out "$Tag-$scene-compare.png")
            } else {
                "saved $png"
            }
        }
    } finally {
        if ($hostProc -and -not $hostProc.HasExited) { Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue }
        if (-not $DataDir -and (Test-Path -LiteralPath $dataPath)) { Remove-Item -LiteralPath $dataPath -Recurse -Force -ErrorAction SilentlyContinue }
    }
} finally {
    Pop-Location
}
