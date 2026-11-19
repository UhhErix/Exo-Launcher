# Exo Shaders - unofficial fork of SCSKiller, based on SCSKiller. GPL-3.0-or-later (see LICENSE, LICENSE-EXCEPTION.txt).
#
# Publishes the engine service self-contained (win-x64) to engine\out\service, in the layout ScsKiller expects next to
# its exe:
#   ExoShaders.Service.exe (+ .NET runtime + VC++ runtime)
#   vcruntime140.dll, vcruntime140_1.dll, msvcp140.dll, VC-REDIST.txt  app-local Visual C++ runtime for libsodium and codecs
#   zlib-ng2.dll                    Codecs seed it from AppContext.BaseDirectory into <data>\codecs
#   native\d3d12.dll                the recorder (NativeTools.Find: <exe dir>\native\<file>)
#   native\d3d9.dll, x86\d3d9.dll   the DirectX 9 recorder, for 64- and 32-bit games
#   native\amd_ags_x64.dll          AGS app-name registration for AMD compiles
#   native\scskiller_ags.dll        the recorder for games whose D3D12 device AGS creates (AMD)
#   native\scskiller_warm.exe       the compile process
#   native\segheap\scskiller_warm.exe
#   native\VkLayer_scskiller.dll    the Vulkan recorder layer (Fossilize), registered per game
#   native\fossilize-replay.exe     the Vulkan compile's replayer
#   native\preload\*.dll            proxies that load the recorder ahead of DLSS/Streamline (NVIDIA)
#   scskiller.exe, scskillerw.exe   beside the service, sharing its one .NET runtime: ScheduledTask.TaskExe (the
#                                   "re-warm after a driver update" task) and Elevated.CliExe (cache size / Auto
#                                   Shader Compilation, run as administrator)
# The native binaries are built from the source in proxy\ (prebuilt\SOURCE.md).
#
#   pwsh engine\build\publish-service.ps1 [-NativeFrom <a folder in prebuilts layout>] [-Out <dir>] [-SkipCli]

[CmdletBinding()]
param(
    # The native files built from proxy\ (prebuilt\SOURCE.md), kept in prebuilt\.
    [string]$NativeFrom = (Join-Path $PSScriptRoot '..\prebuilt'),
    [string]$Out,
    # Every assembly's version. Core's test hooks (SCSK_TEST_HOOKS) build in only under 0.0.0-internal, a plain build's.
    [string]$Version = '1.4.8-exo.1',
    [switch]$SkipCli
)

$ErrorActionPreference = 'Stop'
$engine = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $engine
if (-not $Out) { $Out = Join-Path $engine 'out\service' }
$Out = [IO.Path]::GetFullPath($Out)

$natives = @(
    'native\d3d12.dll',
    'native\d3d9.dll',
    'native\x86\d3d9.dll',
    'native\amd_ags_x64.dll',
    'native\scskiller_ags.dll',
    'native\scskiller_warm.exe',
    'native\segheap\scskiller_warm.exe',
    'native\VkLayer_scskiller.dll',
    'native\fossilize-replay.exe',
    'native\preload\dinput8.dll',
    'native\preload\version.dll',
    'native\preload\winmm.dll'
)
$zlibCandidates = @('zlib-ng2.dll', 'cli\zlib-ng2.dll')

foreach ($n in $natives) {
    if (-not (Test-Path (Join-Path $NativeFrom $n))) { throw "missing $n in $NativeFrom (see prebuilt\SOURCE.md)" }
}
$zlib = $zlibCandidates | ForEach-Object { Join-Path $NativeFrom $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $zlib) { throw "zlib-ng2.dll not found in $NativeFrom" }

function Invoke-Dotnet([string[]]$Arguments) {
    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed ($LASTEXITCODE)" }
}

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null

# Every program published here is the Exo build: they share SCSKiller.Core.dll in $Out, and the last publish wins.
$exoBuild = '-p:ExoBuild=true'

# The service
Invoke-Dotnet @('publish', (Join-Path $engine 'src\ExoShaders.Service\ExoShaders.Service.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Out, '-p:DebugType=none', '-p:PublishReadyToRun=true', "-p:Version=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false', $exoBuild)

# The CLI the engine starts for the scheduled task and elevated driver settings: published into the same folder, so
# all three programs run on one copy of the .NET runtime (same version, same SCSKiller.Core).
if (-not $SkipCli) {
    $cliProj = Join-Path $repo 'src\SCSKiller.Cli\SCSKiller.Cli.csproj'
    Invoke-Dotnet @('publish', $cliProj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Out, '-p:DebugType=none', '-p:PublishReadyToRun=true', "-p:Version=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false', $exoBuild)
    # scskillerw.exe: the same program without a console window (what the scheduled task runs), same dependencies
    Invoke-Dotnet @('publish', (Join-Path $engine 'src\SCSKiller.CliW\SCSKiller.CliW.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Out, '-p:DebugType=none', '-p:PublishReadyToRun=true', "-p:Version=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false', $exoBuild)
}

# Native files, in the app's layout
foreach ($n in $natives) {
    $to = Join-Path $Out $n
    New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
    Copy-Item (Join-Path $NativeFrom $n) $to -Force
}
Copy-Item $zlib (Join-Path $Out 'zlib-ng2.dll') -Force

# App-local Visual C++ runtime next to ExoShaders.Service.exe / scskiller.exe / scskillerw.exe (libsodium.dll and
# zlib-ng2.dll import vcruntime140.dll, which Windows does not ship). Taken from Visual Studio's redist, Microsoft-signed.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = if (Test-Path $vswhere) { & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath }
if (-not $vs) { throw "no Visual Studio with the x64 C++ tools (vswhere): the VC++ runtime DLLs come from its redist" }
$vsVersion = (& $vswhere -path $vs -property installationVersion | Select-Object -First 1).Trim()
$redistVersion = (Get-Content (Join-Path $vs 'VC\Auxiliary\Build\Microsoft.VCRedistVersion.default.txt') -TotalCount 1).Trim()
$crt = @(Get-ChildItem (Join-Path $vs "VC\Redist\MSVC\$redistVersion\x64") -Directory -Filter 'Microsoft.VC14*.CRT')
if ($crt.Count -ne 1) { throw "VC\Redist\MSVC\$redistVersion\x64 has $($crt.Count) Microsoft.VC14*.CRT folders, not one" }
$crtFiles = foreach ($f in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
    $p = Join-Path $crt[0].FullName $f
    $sig = Get-AuthenticodeSignature $p
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') { throw "$p isn't signed by Microsoft ($($sig.Status))" }
    $p
}
$crtVersions = @($crtFiles | ForEach-Object { $v = (Get-Item $_).VersionInfo; "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)" } | Sort-Object -Unique)
if ($crtVersions.Count -ne 1) { throw "the VC++ runtime DLLs in $($crt[0].FullName) differ in version: $($crtVersions -join ', ')" }
$crtVersion = $crtVersions[0]
Copy-Item $crtFiles $Out -Force
$crtSource = "VC\Redist\MSVC\$redistVersion\x64\$($crt[0].Name)"
$exesList = if ($SkipCli) { 'ExoShaders.Service.exe' } else { 'ExoShaders.Service.exe, scskiller.exe and scskillerw.exe' }
[IO.File]::WriteAllText((Join-Path $Out 'VC-REDIST.txt'),
    "Microsoft Visual C++ runtime $crtVersion (x64), app-local next to ${exesList}: $((($crtFiles | Split-Path -Leaf) -join ', '))`r`nFrom Visual Studio $vsVersion, $crtSource`r`n")

# Licence and notices (GPL-3.0 with SCSKiller's additional permission), stamped with the shipped runtime versions
foreach ($f in 'LICENSE', 'LICENSE-EXCEPTION.txt', 'CONTRACT.md') {
    Copy-Item (Join-Path $engine $f) (Join-Path $Out $f) -Force
}
$runtime = ((Get-Content (Join-Path $Out 'ExoShaders.Service.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks |
    Where-Object name -eq 'Microsoft.NETCore.App').version
if (-not $runtime) { throw 'ExoShaders.Service.runtimeconfig.json has no included Microsoft.NETCore.App version' }
$noticesText = [IO.File]::ReadAllText((Join-Path $engine 'THIRD-PARTY-NOTICES.md'))
$row = '| .NET runtime (self-contained) |'
$noticesText = $noticesText -replace "(?m)^$([regex]::Escape($row)) [^|]* \|", "$row $runtime |"
if (-not $noticesText.Contains("$row $runtime |")) { throw "engine\THIRD-PARTY-NOTICES.md has no $row row to put .NET $runtime in" }
$row = '| Microsoft Visual C++ runtime (vcruntime140.dll, vcruntime140_1.dll, msvcp140.dll) |'
$noticesText = $noticesText -replace "(?m)^$([regex]::Escape($row)) [^|]* \|", "$row $crtVersion |"
if (-not $noticesText.Contains("$row $crtVersion |")) { throw "engine\THIRD-PARTY-NOTICES.md has no $row row to put $crtVersion in" }
[IO.File]::WriteAllText((Join-Path $Out 'THIRD-PARTY-NOTICES.md'), $noticesText, [Text.UTF8Encoding]::new($false))

$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$depsLibs = (Get-Content (Join-Path $Out 'ExoShaders.Service.deps.json') -Raw | ConvertFrom-Json).libraries.PSObject.Properties.Name
$ctkPkg = ($depsLibs | Where-Object { $_ -like 'CommunityToolkit.HighPerformance/*' } | Select-Object -First 1)
if (-not $ctkPkg) { throw 'CommunityToolkit.HighPerformance not found in ExoShaders.Service.deps.json' }
$ctkVer = $ctkPkg.Split('/')[1]
New-Item -ItemType Directory -Force (Join-Path $Out 'notices') | Out-Null
foreach ($n in @(
    @("microsoft.netcore.app.runtime.win-x64\$runtime\THIRD-PARTY-NOTICES.TXT", 'dotnet-THIRD-PARTY-NOTICES.txt'),
    @("communitytoolkit.highperformance\$ctkVer\ThirdPartyNotices.txt", 'CommunityToolkit-ThirdPartyNotices.txt')
)) {
    $srcNotice = Join-Path $nuget $n[0].ToLowerInvariant()
    if (-not (Test-Path $srcNotice)) { throw "missing package notice file: $srcNotice" }
    Copy-Item $srcNotice (Join-Path $Out "notices\$($n[1])") -Force
}

# Post-publish check: every shipped SCSKiller.Core must be the Exo build (its ExoBuild.Enabled is a constant, read here
# without loading the file, as tools/Build-Release.ps1 does in the launcher). An upstream Core would send requests to
# SCSKiller's servers and Patreon.
$cores = @(Get-ChildItem -Recurse -File -Filter SCSKiller.Core.dll -Path $Out)
if ($cores.Count -eq 0) { throw "no SCSKiller.Core.dll in $Out" }
foreach ($core in $cores) {
    $enabled = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($core.FullName)).GetType('SCSKiller.Core.App.ExoBuild').GetField('Enabled').GetRawConstantValue()
    if ($enabled -ne $true) { throw "$($core.FullName) is not the Exo build (ExoBuild.Enabled is off): publish with -p:ExoBuild=true" }
}

& (Join-Path $repo 'build\check-vcruntime.ps1') -Dir $Out
if ($LASTEXITCODE -ne 0) { throw "check-vcruntime.ps1 failed ($LASTEXITCODE)" }

$size = (Get-ChildItem -Recurse -File $Out | Measure-Object -Sum Length).Sum
Write-Host ("published to {0} ({1:N0} MB)" -f $Out, ($size / 1MB))
