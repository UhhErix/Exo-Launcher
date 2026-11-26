#Requires -Version 7
param(
    [switch]$Reseed,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$daily = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ExoLauncher')).TrimEnd('\')
$root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ExoLauncherRune')).TrimEnd('\')
$app = Join-Path $root 'app'
$data = Join-Path $root 'RuneData'
$name = 'Exo Launcher Rune'

if ($root -eq $daily -or $root.StartsWith("$daily\", [StringComparison]::OrdinalIgnoreCase)) { throw "$root is inside the installed Exo's folder. Refused." }
$games = 'VALORANT-Win64-Shipping', 'VALORANT', 'League of Legends', 'LeagueOfLegends', 'TFTClient-Win64-Shipping', 'cs2', 'dota2', 'FortniteClient-Win64-Shipping', 'RobloxPlayerBeta'
if (Get-Process -Name $games -ErrorAction SilentlyContinue) { throw 'A game is running. Not building.' }

$real = $repo
if ($repo -match '^([A-Za-z]:)') {
    $drive = $Matches[1].ToUpperInvariant()
    foreach ($line in (subst)) {
        if ($line -match '^([A-Z]:)\\: => (.+)$' -and $Matches[1] -eq $drive) { $real = $Matches[2].TrimEnd('\') + $repo.Substring(2) }
    }
}

if (-not $NoBuild) {
    Push-Location (Join-Path $repo 'native-ui')
    try { cargo build --release --locked --bin exo-ui; if ($LASTEXITCODE) { throw "exo-ui build failed ($LASTEXITCODE)." } } finally { Pop-Location }
    Push-Location (Join-Path $real 'ui')
    try { npm run -s build; if ($LASTEXITCODE) { throw "UI build failed ($LASTEXITCODE)." } } finally { Pop-Location }
    Push-Location $real
    try {
        dotnet publish ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained -v q -nologo
        if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)." }
    } finally { Pop-Location }
}
$publish = Get-ChildItem -Recurse -Directory (Join-Path $real 'ExoLauncher\bin') | Where-Object { $_.FullName -match 'Release\\.*win-x64\\publish$' } | Select-Object -First 1
if (-not $publish) { throw 'No publish folder. Run without -NoBuild.' }
$ui = Join-Path $repo 'native-ui\target\release\exo-ui.exe'
if (-not (Test-Path -LiteralPath $ui)) { throw "No $ui. Run without -NoBuild." }

foreach ($p in Get-Process -Name 'ExoLauncher', 'exo-ui' -ErrorAction SilentlyContinue) {
    $path = try { $p.Path } catch { $null }
    if (-not $path -or -not $path.StartsWith("$app\", [StringComparison]::OrdinalIgnoreCase)) { continue }
    if ($p.ProcessName -eq 'ExoLauncher') { [void]$p.CloseMainWindow() }
    if (-not $p.WaitForExit(40000)) { throw "$name (PID $($p.Id)) did not close. Close it and run again." }
}

New-Item -ItemType Directory -Force -Path $app | Out-Null
robocopy $publish.FullName $app /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the app failed ($LASTEXITCODE)." }
Copy-Item -LiteralPath $ui -Destination (Join-Path $app 'exo-ui.exe') -Force

if ($Reseed -or -not (Test-Path -LiteralPath (Join-Path $data 'settings.json'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $daily 'settings.json'))) { throw "No installed Exo data at $daily to seed from." }
    $skip = 'app', 'updates', 'logs', 'cache', 'drivers', 'frame-gen', 'dlss', 'gifts-webview', 'webview-debug', 'native-web-host', 'telemetry', 'pending-reports', 'EBWebView'
    robocopy $daily $data /MIR /XJ /XD @($skip | ForEach-Object { Join-Path $daily $_ }) /XF *.tmp *~RF*.TMP /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the installed data failed ($LASTEXITCODE)." }
    Set-Content -LiteralPath (Join-Path $data '.exo-rune-test') -Value "Seeded from $daily at $((Get-Date).ToString('o'))" -Encoding utf8
}

$launch = Join-Path $root 'Launch-ExoLauncherRune.ps1'
@"
`$env:EXO_LAUNCHER_DATA_DIR = '$data'
Start-Process -FilePath '$(Join-Path $app 'ExoLauncher.exe')' -WorkingDirectory '$app'
"@ | Set-Content -LiteralPath $launch -Encoding utf8

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$shortcut.TargetPath = (Get-Command pwsh).Source
$shortcut.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$launch`""
$shortcut.WorkingDirectory = $app
$shortcut.IconLocation = "$(Join-Path $app 'ExoLauncher.exe'),0"
$shortcut.Description = 'Exo Launcher v4 (Rune) test copy: own data, never updates, changes nothing on this PC'
$shortcut.WindowStyle = 7
$shortcut.Save()

Write-Host "$name ($((git -C $repo rev-parse --short HEAD).Trim())) installed: app $app, data $data, shortcut $lnk."
