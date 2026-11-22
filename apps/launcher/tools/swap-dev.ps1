$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installer\Close-ExoLauncher.ps1') -DefinitionsOnly
$repo = Split-Path $PSScriptRoot -Parent
$pub = Join-Path $repo 'ExoLauncher\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish'
$app = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app'
if (-not (Test-Path -LiteralPath (Join-Path $pub 'ExoLauncher.exe'))) { throw 'publish output is missing ExoLauncher.exe' }
if (-not (Test-Path -LiteralPath (Join-Path $app 'ExoLauncher.exe'))) { throw 'installed app directory is missing' }
# Built once in voice\out, which a fresh worktree lacks: the publish then quietly leaves Discord voice and Exo Voice out.
$noVoice = @('dave.dll', 'opus.dll', 'exo_apm.dll', 'exo-deepfilter.dll') | Where-Object { -not (Test-Path -LiteralPath (Join-Path $pub $_)) }
if ($noVoice) { throw "publish output is missing $($noVoice -join ', '): copy voice\out from the main checkout or run voice/build.ps1, then publish again" }
$incoming = Join-Path $env:LOCALAPPDATA ('ExoLauncher\app.incoming-dev-' + [guid]::NewGuid().ToString('N'))
$previous = Join-Path $env:LOCALAPPDATA ('ExoLauncher\app.previous-dev-' + [guid]::NewGuid().ToString('N'))
robocopy $pub $incoming /E /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath (Join-Path $incoming 'ExoLauncher.exe'))) { throw 'staged copy is missing ExoLauncher.exe' }
$nativeUi = Join-Path $repo 'native-ui\target\release\exo-ui.exe'
if ((Test-Path -LiteralPath $nativeUi) -and -not (Test-Path -LiteralPath (Join-Path $incoming 'exo-ui.exe'))) {
    Copy-Item -LiteralPath $nativeUi -Destination (Join-Path $incoming 'exo-ui.exe') -Force
}
# dotnet publish carries no add-ons (Build-Release.ps1 adds them): keep each installed one the publish lacks, or the swap
# drops Exo Capture and Settings says it is not installed. Lucid (addons\lucid, hosted until 3.25) is not carried over.
$installedAddons = Join-Path $app 'addons'
if (Test-Path -LiteralPath $installedAddons) {
    foreach ($addon in Get-ChildItem -LiteralPath $installedAddons -Directory) {
        $target = Join-Path $incoming ('addons\' + $addon.Name)
        if ($addon.Name -eq 'lucid' -or (Test-Path -LiteralPath $target)) { continue }
        robocopy $addon.FullName $target /E /NFL /NDL /NJH /NJS | Out-Null
        if ($LASTEXITCODE -gt 7) { throw "robocopy of add-on $($addon.Name) failed with exit code $LASTEXITCODE" }
    }
}
$uiTargets = @(Get-Process 'exo-ui' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $app 'exo-ui.exe') })
foreach ($p in $uiTargets) { try { [void]$p.CloseMainWindow() } catch { } }
$targets = @(Get-Process ExoLauncher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $app 'ExoLauncher.exe') })
if ($targets.Count) {
    & (Join-Path $PSScriptRoot 'e2e\lib\win.ps1') -Command close -W 20 | Out-Null
}
# The Lucid an installed Exo before 3.26 hosts (addons\lucid in the app folder, or addons\lucid-current in its data folder)
# stays running when that Exo exits, and would stop the swap below. Asked to close, then ended if it won't, only when it is
# the exact exe in this folder; a Lucid run from anywhere else is left alone. The new Exo takes over from it (Exo EQ).
if (-not (Stop-ExoHostedLucid $app (Join-Path $env:LOCALAPPDATA 'ExoLauncher'))) { throw "Lucid from Exo's folder would not close. Swap canceled." }
# Exo's helpers (the Steam watcher checks its parent's pid every few seconds) can outlive it briefly and hold the folder.
$appPrefix = $app.TrimEnd('\') + '\'
for ($wait = 0; $wait -lt 30 -and (Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase) }); $wait++) { Start-Sleep -Milliseconds 500 }
if (Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase) }) { throw "A process from Exo's folder is still running. Swap canceled; no processes were killed." }
Rename-Item -LiteralPath $app -NewName (Split-Path $previous -Leaf)
try {
    Rename-Item -LiteralPath $incoming -NewName (Split-Path $app -Leaf)
}
catch {
    if (-not (Test-Path -LiteralPath $app)) { Rename-Item -LiteralPath $previous -NewName (Split-Path $app -Leaf) }
    throw
}
# Keep only the build we just replaced for rollback; older backups only
# accumulate (~1 GB each per swap).
Get-ChildItem (Split-Path $app -Parent) -Directory -Filter 'app.previous-dev-*' |
Where-Object { $_.FullName -ne $previous } |
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem (Split-Path $app -Parent) -Directory -Filter 'app.incoming-dev-*' |
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Test-Path -LiteralPath (Join-Path $app 'ExoLauncher.exe')
