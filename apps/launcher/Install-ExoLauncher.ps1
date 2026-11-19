# Exo Launcher bootstrap installer.
# Asks Exo's update address for the newest launcher, downloads its installer, verifies the SHA-256 and size
# the manifest gives, and launches it. The installer itself is also at https://getexo.app/download/launcher.
param([switch]$Force)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows only.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Manifest = 'https://updates.getexo.app/v1/launcher/latest'
Write-Host ''
Write-Host '  Exo Launcher - checking for the newest release...' -ForegroundColor Cyan
Write-Host ''

$headers = @{ 'User-Agent' = 'ExoLauncher-Installer/1.0' }

try {
    $release = Invoke-RestMethod -Uri $Manifest -Headers $headers -TimeoutSec 30
}
catch {
    Write-Host '  No release published yet (or network error).' -ForegroundColor Yellow
    Write-Host '  Get the installer from https://getexo.app/download/launcher' -ForegroundColor Yellow
    Write-Host ''
    return
}

$setupUrl = [string]$release.files.setup
$check = $release.checks.setup

if (-not $setupUrl) {
    Write-Host "  Latest release $($release.tag) has no installer yet." -ForegroundColor Yellow
    Write-Host '  Build from source with Run-ExoLauncher.ps1.' -ForegroundColor Yellow
    Write-Host ''
    return
}

# The installer must come from Exo's own update address, and its manifest must give a size and SHA-256 to check against.
$expectedHash = ([string]$check.sha256).ToLowerInvariant()
if (-not $setupUrl.StartsWith('https://updates.getexo.app/', [StringComparison]::Ordinal)) {
    throw 'The update address named an installer outside updates.getexo.app. Nothing was installed.'
}
if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or -not $check.size -or [long]$check.size -le 0) {
    throw 'The update address gave no usable SHA-256 and size for the installer. Nothing was installed.'
}

$destDir = Join-Path $env:LOCALAPPDATA 'ExoLauncher\app'
$sfx = Join-Path $env:TEMP ('ExoLauncher-setup-' + [guid]::NewGuid().ToString('N') + '.exe')

Write-Host "[*] $($release.tag) -> $sfx" -ForegroundColor DarkGray
Invoke-WebRequest -Uri $setupUrl -OutFile $sfx -UseBasicParsing -Headers $headers -TimeoutSec 300

$downloaded = Get-Item -LiteralPath $sfx
if ($downloaded.Length -ne [long]$check.size) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw "Downloaded installer has the wrong size ($($downloaded.Length); expected $($check.size))."
}

$actualHash = (Get-FileHash -LiteralPath $sfx -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw 'Downloaded installer failed its SHA-256 integrity check.'
}
Write-Host '[+] SHA-256 and size verified' -ForegroundColor Green

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($sfx)
if ($versionInfo.ProductName -ne 'Exo Launcher' -or
    $versionInfo.FileDescription -ne 'Exo Launcher Setup') {
    Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue
    throw 'The verified release asset is not an Exo Launcher installer.'
}

# The release asset is an NSIS installer, not the installed application. Let it
# perform its atomic app.incoming -> app swap; never rename it to ExoLauncher.exe.
try {
    $installer = Start-Process -FilePath $sfx -ArgumentList '/S' -WorkingDirectory ([IO.Path]::GetDirectoryName($sfx)) -Wait -PassThru -WindowStyle Hidden
}
finally { Remove-Item -LiteralPath $sfx -Force -ErrorAction SilentlyContinue }
if ($installer.ExitCode -ne 0) { throw "Installer failed with exit code $($installer.ExitCode)." }
$target = Join-Path $destDir 'ExoLauncher.exe'
if (-not (Test-Path -LiteralPath $target)) { throw 'Installer completed but ExoLauncher.exe was not found.' }
Write-Host "[+] Installed to $target" -ForegroundColor Green
Start-Process -FilePath $target -WorkingDirectory $destDir

Write-Host ''
