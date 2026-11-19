# The file-level update's swap (ExoLauncher › AppUpdateService.Delta): Exo built the new app folder beside the installed one
# (app.incoming-<id>, every file checked against the release's files.json) and Setup's own files beside that
# (app.incoming-<id>setup), started this, and closed. This does what Exo's installer does after it copies its files, with
# the installer's own scripts: closes what is left of Exo and Lucid, checks the new folder against its payload list, swaps
# the folders (the old one kept as app.previous-<pid>), lets Complete-ExoUpdate.ps1 start the new build and roll back if it
# doesn't come up, then puts Setup's files in, updates Apps & features and cleans up. Anything that fails before the swap
# leaves the old app as it was, starts it again and marks the version so the next try uses the full installer.
param(
    [string]$InstallRoot,
    [string]$Incoming,
    [string]$SetupFiles,
    [string]$Version,
    [string]$UpdatesDirectory,
    [int]$WaitProcessId = 0,
    [switch]$DefinitionsOnly
)
$ErrorActionPreference = 'Stop'
$deltaDefinitionsOnly = $DefinitionsOnly
. (Join-Path $PSScriptRoot 'Verify-Payload.ps1') -DefinitionsOnly
. (Join-Path $PSScriptRoot 'Close-ExoLauncher.ps1') -DefinitionsOnly

function Get-ExoDeltaPaths([string]$Root, [string]$Incoming, [string]$SetupFiles) {
    $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $staged = [IO.Path]::GetFullPath($Incoming).TrimEnd('\')
    $setup = [IO.Path]::GetFullPath($SetupFiles).TrimEnd('\')
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetDirectoryName($staged), $full) -or
        [IO.Path]::GetFileName($staged) -cnotmatch '^app\.incoming-[0-9a-f]{32}$') { throw 'Unexpected staging location.' }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($setup, $staged + 'setup')) { throw 'Unexpected Setup files location.' }
    $app = Join-Path $full 'app'
    foreach ($path in @($full, $app, $staged, $setup)) {
        if ([IO.Directory]::Exists($path) -and ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Update paths cannot be links.' }
    }
    @{ root = $full; app = $app; incoming = $staged; setup = $setup; previous = (Join-Path $full ('app.previous-' + $PID)) }
}

# The new folder checked against its own payload list (the installer's check), then the two renames. A failure of the
# second puts the old folder back; either way nothing is left half done.
function Invoke-ExoDeltaSwap([hashtable]$Paths, [string]$Version) {
    $manifest = Test-ExoPayload $Paths.incoming
    if ($manifest.version -cne $Version) { throw 'The staged files are for another version.' }
    if (-not [IO.File]::Exists((Join-Path $Paths.app 'ExoLauncher.exe'))) { throw 'The installed app folder is not where it should be.' }
    if ([IO.Directory]::Exists($Paths.previous)) { throw 'An unexpected rollback folder is in the way.' }
    for ($attempt = 1; ; $attempt++) {
        try { [IO.Directory]::Move($Paths.app, $Paths.previous); break }
        catch { if ($attempt -ge 10) { throw "Exo Launcher's folder is still in use: $($_.Exception.Message)" }; Start-Sleep -Milliseconds 500 }
    }
    try { [IO.Directory]::Move($Paths.incoming, $Paths.app) }
    catch {
        if (-not [IO.Directory]::Exists($Paths.app)) { [IO.Directory]::Move($Paths.previous, $Paths.app) }
        throw
    }
}

# Setup's files (its window as the uninstaller, NSIS's uninstaller) go in once the new build is up, as the installer writes them.
function Move-ExoSetupFiles([hashtable]$Paths) {
    if (-not [IO.Directory]::Exists($Paths.setup)) { return }
    foreach ($file in [IO.DirectoryInfo]::new($Paths.setup).GetFiles()) {
        $target = Join-Path $Paths.app $file.Name
        if ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
        [IO.File]::Move($file.FullName, $target)
    }
    [IO.Directory]::Delete($Paths.setup, $true)
}

# Apps & features: the version and size the installer would have written. Its other values name the same folder and files.
function Set-ExoUninstallEntry([string]$App, [string]$Version, [string]$Key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ExoLauncher') {
    if (-not (Test-Path -LiteralPath $Key)) { return }
    $kb = [int][Math]::Ceiling(((Get-ChildItem -LiteralPath $App -Recurse -File -Force | Measure-Object Length -Sum).Sum) / 1KB)
    Set-ItemProperty -LiteralPath $Key -Name DisplayVersion -Value $Version
    Set-ItemProperty -LiteralPath $Key -Name EstimatedSize -Value $kb -Type DWord
}

function Write-ExoDeltaFailed([string]$Updates, [string]$Version, [string]$Message) {
    if (-not $Updates) { return }
    try {
        [IO.Directory]::CreateDirectory($Updates) | Out-Null
        $json = @{ schema = 1; version = $Version; message = $Message; atUtc = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText((Join-Path $Updates 'delta-failed.json'), $json, [Text.UTF8Encoding]::new($false))
    } catch { }
}

function Start-ExoApp([string]$App) {
    $exe = Join-Path $App 'ExoLauncher.exe'
    if (-not [IO.File]::Exists($exe)) { return }
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false; $start.WorkingDirectory = $App
    $process = [Diagnostics.Process]::Start($start)
    if ($null -ne $process) { $process.Dispose() }
}

if ($deltaDefinitionsOnly) { return }

# Held while this runs, as the install engine holds it: a launch made now waits for the update (UpdateHandOff.EngineMutex).
$engine = [Threading.Mutex]::new($false, 'Local\ExoLauncher.Installing')
# Every Exo started from here is the update's own (Program.cs, UpdateHandOff.FromSetupVariable).
$env:EXO_FROM_SETUP = '1'
try { $paths = Get-ExoDeltaPaths $InstallRoot $Incoming $SetupFiles }
catch {
    Write-ExoDeltaFailed $UpdatesDirectory $Version $_.Exception.Message
    try { [IO.File]::WriteAllText((Join-Path $InstallRoot 'update-error.log'), $_.Exception.Message) } catch { }
    exit 1
}
try {
    if ($WaitProcessId -gt 0) { try { Wait-Process -Id $WaitProcessId -Timeout 60 -ErrorAction Stop } catch { } }
    if ((Stop-ExoInstalledApp $paths.app $paths.root $PID) -ne 0) { throw 'Exo Launcher is still running and could not be stopped safely.' }
    # Exo's helpers (the Steam watcher, an add-on) can outlive it for a moment and hold the folder; never ended here.
    $inside = $paths.app.TrimEnd('\') + '\'
    for ($wait = 0; $wait -lt 30; $wait++) {
        $left = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($inside, [StringComparison]::OrdinalIgnoreCase) })
        if ($left.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Invoke-ExoDeltaSwap $paths $Version
} catch {
    $message = $_.Exception.Message
    if (-not [IO.Directory]::Exists($paths.app) -and [IO.Directory]::Exists($paths.previous)) { try { [IO.Directory]::Move($paths.previous, $paths.app) } catch { } }
    # The staged folder goes only once the old one is back in place.
    if ([IO.Directory]::Exists($paths.app)) {
        foreach ($folder in @($paths.incoming, $paths.setup)) { try { if ([IO.Directory]::Exists($folder)) { [IO.Directory]::Delete($folder, $true) } } catch { } }
    }
    Write-ExoDeltaFailed $UpdatesDirectory $Version $message
    try { [IO.File]::WriteAllText((Join-Path $paths.root 'update-error.log'), "The file-level update did not start: $message Your installation was not changed.") } catch { }
    Start-ExoApp $paths.app
    exit 1
}

# The installer's own health check: starts the new build, waits for its healthy start, and rolls back (and restarts the old
# build) when it doesn't come up.
& (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Complete-ExoUpdate.ps1') -InstallRoot $paths.root -PreviousDirectory $paths.previous -Version $Version
if ($LASTEXITCODE -ne 0) {
    Write-ExoDeltaFailed $UpdatesDirectory $Version 'The new build did not start; the previous one is back.'
    try { if ([IO.Directory]::Exists($paths.setup)) { [IO.Directory]::Delete($paths.setup, $true) } } catch { }
    exit 1
}
try { Move-ExoSetupFiles $paths } catch { Write-Warning "Setup's files were not put in: $($_.Exception.Message)" }
try { Set-ExoUninstallEntry $paths.app $Version } catch { Write-Warning "Apps & features was not updated: $($_.Exception.Message)" }
$failed = Join-Path $UpdatesDirectory 'delta-failed.json'
try { if ([IO.File]::Exists($failed)) { [IO.File]::Delete($failed) } } catch { }
& (Join-Path $PSHOME 'powershell.exe') -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Clean-ExoInstall.ps1') -InstallRoot $paths.root | Out-Null
# As Setup does at the end of a silent update: the new Exo comes forward (a second start hands over to the running one).
Start-ExoApp $paths.app
$engine.Dispose()
exit 0
