# Builds Exo Capture (capture\out\ExoCapture.exe) via Native AOT and copies OBS Studio's signed
# game capture files into capture\out\obs for Build-Release.ps1.
#   pwsh -NoProfile -File capture\build.ps1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'out'
$proj = Join-Path $here 'ExoCapture\ExoCapture.csproj'
$obsSrc = Join-Path $here 'third_party\obs'
$obsOut = Join-Path $out 'obs'

# NativeAOT's linker step invokes Visual Studio scripts that locate tools via vswhere.exe.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and (($env:PATH -split ';') -notcontains $vsInstaller)) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish $proj -c Release -r win-x64 -p:Platform=x64 --output $out -nologo
if ($LASTEXITCODE) { throw "Exo Capture publish failed ($LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $out 'ExoCapture.exe'))) { throw 'capture\out\ExoCapture.exe was not produced.' }

Copy-Item $obsSrc $obsOut -Recurse -Force
Write-Output "Exo Capture: $out\ExoCapture.exe"
