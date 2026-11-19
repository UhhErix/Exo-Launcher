param([string]$Repository = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
$version = '8.30.1'
# CI runs this on Linux, a developer on Windows. $IsLinux is unset in Windows PowerShell, which takes the Windows branch.
if ($IsLinux) { $asset = 'linux_x64.tar.gz'; $expectedSha = '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb'; $tool = 'gitleaks' }
else { $asset = 'windows_x64.zip'; $expectedSha = 'd29144deff3a68aa93ced33dddf84b7fdc26070add4aa0f4513094c8332afc4e'; $tool = 'gitleaks.exe' }
$toolRoot = Join-Path ([IO.Path]::GetTempPath()) ('exo-gitleaks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $toolRoot | Out-Null
$archive = Join-Path $toolRoot "gitleaks-$asset"
Invoke-WebRequest -Uri "https://github.com/gitleaks/gitleaks/releases/download/v$version/gitleaks_${version}_$asset" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedSha) { throw 'Gitleaks archive checksum mismatch.' }
if ($IsLinux) { tar -xzf $archive -C $toolRoot } else { Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot }
& (Join-Path $toolRoot $tool) git $Repository --redact --log-opts=--all --config (Join-Path $Repository '.gitleaks.toml')
if ($LASTEXITCODE -ne 0) { throw 'The Git history secret scan failed.' }
