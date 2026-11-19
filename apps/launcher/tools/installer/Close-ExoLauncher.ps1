param(
    [string]$AppDirectory,
    [string]$DataDirectory,
    [int]$SetupProcessId = 0,
    [switch]$DefinitionsOnly
)
$ErrorActionPreference = 'Stop'

function Test-ExoWebViewCommandLine([string]$CommandLine, [string[]]$ProfileRoots) {
    if ([string]::IsNullOrWhiteSpace($CommandLine)) { return $false }
    $match = [regex]::Match($CommandLine, '(?:^|\s)--user-data-dir=(?:"([^"\r\n]+)"|([^\s"\r\n]+))', 'IgnoreCase')
    if (-not $match.Success) { return $false }
    $value = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    try { $full = [IO.Path]::GetFullPath($value).TrimEnd('\') } catch { return $false }
    foreach ($root in $ProfileRoots) {
        if ([StringComparer]::OrdinalIgnoreCase.Equals($full, [IO.Path]::GetFullPath($root).TrimEnd('\'))) { return $true }
    }
    return $false
}

# The Lucid that Exo hosts runs from addons\lucid in the app folder (and would hold it), or from addons\lucid-current in the
# data folder when Exo downloaded a newer one. Only those exact files count: a Lucid the user runs from anywhere else (even
# from a folder named like the app's) is theirs and never touched.
function Test-ExoHostedLucid([string]$ExecutablePath, [string]$AppDirectory, [string]$DataDirectory = '') {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath) -or -not [IO.Path]::IsPathRooted($ExecutablePath)) { return $false }
    try {
        $full = [IO.Path]::GetFullPath($ExecutablePath)
        $hosted = @([IO.Path]::GetFullPath((Join-Path $AppDirectory 'addons\lucid\Lucid.exe')))
        if ($DataDirectory) { $hosted += [IO.Path]::GetFullPath((Join-Path $DataDirectory 'addons\lucid-current\Lucid.exe')) }
    } catch { return $false }
    foreach ($one in $hosted) { if ([StringComparer]::OrdinalIgnoreCase.Equals($full, $one)) { return $true } }
    return $false
}

function Get-ExoHostedLucid([string]$AppDirectory, [string]$DataDirectory = '') {
    @(Get-CimInstance Win32_Process -Filter "Name = 'Lucid.exe'" | Where-Object { Test-ExoHostedLucid $_.ExecutablePath $AppDirectory $DataDirectory })
}

# Asks the hosted Lucid to close and waits, then ends the ones still running (each listed again just before, so an id that
# went to another program is not touched). True when none is left. Exo brings Lucid back by itself: it starts it when it
# starts, so an update that relaunches Exo restores it.
function Stop-ExoHostedLucid([string]$AppDirectory, [string]$DataDirectory = '', [int]$AskSeconds = 3) {
    $hosted = @(Get-ExoHostedLucid $AppDirectory $DataDirectory)
    if ($hosted.Count -eq 0) { return $true }
    foreach ($process in $hosted) {
        try { [void](Get-Process -Id $process.ProcessId -ErrorAction Stop).CloseMainWindow() } catch { }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($AskSeconds)
    while (@(Get-ExoHostedLucid $AppDirectory $DataDirectory).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $left = @(Get-ExoHostedLucid $AppDirectory $DataDirectory)
        foreach ($process in $left) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
        if ($left.Count -eq 0) { return $true }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return (@(Get-ExoHostedLucid $AppDirectory $DataDirectory).Count -eq 0)
}

function Get-ExoProfileRoots([string]$AppDirectory, [string]$DataDirectory) {
    @('webview', 'webview-debug', 'discord-web', 'apple-music-web', 'youtube-music-web', 'spotify-web') |
        ForEach-Object { Join-Path $DataDirectory $_ }
    Join-Path $AppDirectory 'ExoLauncher.exe.WebView2'
}

function Stop-ExoInstalledApp([string]$AppDirectory, [string]$DataDirectory, [int]$SetupProcessId) {
$app = [IO.Path]::GetFullPath($AppDirectory).TrimEnd('\')
$data = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
if (-not [StringComparer]::OrdinalIgnoreCase.Equals($app, (Join-Path $data 'app'))) { throw 'Unexpected managed app location.' }
$target = Join-Path $app 'ExoLauncher.exe'
$profiles = @(Get-ExoProfileRoots $app $data)

function Get-InstalledExoProcess {
    @(Get-CimInstance Win32_Process -Filter "Name = 'ExoLauncher.exe'" | Where-Object {
        $_.ProcessId -ne $SetupProcessId -and $_.ExecutablePath -and
        [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($_.ExecutablePath), $target)
    })
}

# Ask Exo to close the way a click on its close button does, so it saves its state and lets its store
# clients and add-ons go; the updater already did when it started this. Then allow 8 s for that.
if ($env:EXO_SILENT_INSTALL -ne '1') {
    foreach ($process in @(Get-InstalledExoProcess)) {
        try { [void](Get-Process -Id $process.ProcessId -ErrorAction Stop).CloseMainWindow() } catch { }
    }
}
$deadline = [DateTime]::UtcNow.AddSeconds(8)
while (@(Get-InstalledExoProcess).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $matches = @(Get-InstalledExoProcess)
    foreach ($process in $matches) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($matches.Count -eq 0) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
if (@(Get-InstalledExoProcess).Count -gt 0) { return 1 }

# Lucid runs from the app folder and would hold it. Exo closes it as it closes (and ends it if it won't answer); this is
# for one Exo left behind (a crash) or an older Exo that could not.
if (-not (Stop-ExoHostedLucid $app $data)) { return 1 }

$deadline = [DateTime]::UtcNow.AddSeconds(5)
do {
    $web = @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object {
        Test-ExoWebViewCommandLine $_.CommandLine $profiles
    })
    foreach ($process in $web) { Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($web.Count -eq 0) { return 0 }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
return 1
}

if ($DefinitionsOnly) { return }
exit (Stop-ExoInstalledApp $AppDirectory $DataDirectory $SetupProcessId)
