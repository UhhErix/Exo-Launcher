#Requires -Version 7
# Starts Exo's web host as a disposable test instance: its own data folder, its own window on the second screen and its own
# loopback debug port. It runs beside an installed Exo, never instead of it: the installed app, its data, its startup
# entries, its store files and the registry are never touched.
#   -Folder <path>    parent folder for the data dir (required), e.g. $env:TEMP\exo-test-instance
#   -Exe <path>       ExoLauncher.exe to start (default: the Debug build under this repo)
#   -DataDir <path>   reuse exactly this data dir; it must be under -Folder (default: <Folder>\instance)
#   -Fresh            delete and recreate the data dir first (without it an existing data dir is reused)
#   -Env <hashtable>  extra environment variables, e.g. @{ EXO_REPORT_SMOKE = '1' }
#   -Stop <pid>       close that test instance: CloseMainWindow on that process only (never a kill), wait up to 40 s
# Refuses while a game runs and without a second screen. Prints the PID, the data dir and the debug port; the last line
# is the same as JSON (tools/e2e/lib/exo.mjs reads it).
param(
    [string]$Folder,
    [string]$Exe,
    [string]$DataDir,
    [switch]$Fresh,
    [hashtable]$Env = @{},
    [int]$Stop = 0
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installedData = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ExoLauncher')).TrimEnd('\')
$installedExe = Join-Path $installedData 'app\ExoLauncher.exe'
$defaultExe = Join-Path $repo 'ExoLauncher\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\ExoLauncher.exe'

if ($Stop -gt 0) {
    $exePath = if ($Exe) { (Resolve-Path -LiteralPath $Exe).Path } else { $defaultExe }
    $p = Get-Process -Id $Stop -ErrorAction SilentlyContinue
    if (-not $p) { Write-Host "PID $Stop is not running."; return }
    $path = try { $p.Path } catch { $null }
    if ($p.ProcessName -ne 'ExoLauncher' -or $path -ne $exePath -or $path -eq $installedExe) {
        throw "PID $Stop is not a test instance from $exePath. Not touched."
    }
    $closed = $p.CloseMainWindow()
    $exited = $closed -and $p.WaitForExit(40000)
    Write-Host "CloseMainWindow on PID ${Stop}: $closed; exited: $exited"
    if (-not $exited) { throw "PID $Stop did not close." }
    return
}

if (-not $Folder) { throw '-Folder is required (the parent folder of the test data dir).' }
$games = 'VALORANT-Win64-Shipping', 'VALORANT', 'League of Legends', 'LeagueOfLegends', 'TFTClient-Win64-Shipping', 'cs2', 'dota2', 'FortniteClient-Win64-Shipping', 'RobloxPlayerBeta'
if (Get-Process -Name $games -ErrorAction SilentlyContinue) { throw 'A game is running. Not starting.' }

Add-Type -AssemblyName System.Windows.Forms
$second = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
if (-not $second) { throw 'No second screen: test windows open only there.' }

$folderFull = [IO.Path]::GetFullPath($Folder).TrimEnd('\')
$data = if ($DataDir) { [IO.Path]::GetFullPath($DataDir).TrimEnd('\') } else { Join-Path $folderFull 'instance' }
if (-not $data.StartsWith("$folderFull\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "The data dir $data must be under -Folder $folderFull."
}
if ($data -eq $installedData -or $data.StartsWith("$installedData\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "The data dir $data is the installed Exo's data folder or inside it. Refused."
}
$exePath = if ($Exe) { (Resolve-Path -LiteralPath $Exe).Path } else { $defaultExe }
if ($exePath -eq $installedExe) { throw 'That is the installed Exo. Refused.' }
if (-not (Test-Path -LiteralPath $exePath)) { throw "No host at $exePath. Build the Debug host first." }

if ($Fresh -and (Test-Path -LiteralPath $data)) { Remove-Item -LiteralPath $data -Recurse -Force }
New-Item -ItemType Directory -Force -Path $data | Out-Null

# A free loopback port for the debug server: bind port 0, read it, release it.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

# The window: 1600x960 at most, centred on the second screen's working area (the exo-port script's arithmetic).
$a = $second.WorkingArea
$w = [Math]::Min(1600, $a.Width - 40)
$h = [Math]::Min(960, $a.Height - 40)
$x = $a.X + [int](($a.Width - $w) / 2)
$y = $a.Y + [int](($a.Height - $h) / 2)

foreach ($key in $Env.Keys) { [Environment]::SetEnvironmentVariable([string]$key, [string]$Env[$key]) }
$env:EXO_LAUNCHER_DATA_DIR = $data
$env:EXO_NO_ACTIVATE = '1'
$env:EXO_WINDOW_RECT = "$x,$y,$w,$h"
$env:EXO_UI = 'web'
$env:EXO_CDP = '1'
$env:EXO_CDP_PORT = "$port"

$p = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
$info = [pscustomobject]@{ pid = $p.Id; dataDir = $data; port = $port; exe = $exePath; started = (Get-Date).ToString('o') }
$info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $data 'test-instance.json') -Encoding utf8
Write-Host "Test instance PID $($p.Id), data $data, debug port $port, window $x,$y ${w}x$h on the second screen."
Write-Output ($info | ConvertTo-Json -Compress)
