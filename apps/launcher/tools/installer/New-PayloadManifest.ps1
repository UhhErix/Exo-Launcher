param([Parameter(Mandatory)][string]$PayloadRoot, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Verify-Payload.ps1') -DefinitionsOnly -PayloadRoot $PayloadRoot
$rootPath = (Resolve-Path -LiteralPath $PayloadRoot).Path.TrimEnd('\')
$required = @('ExoLauncher.exe', 'ExoLauncher.dll', 'ExoLauncher.deps.json', 'ExoLauncher.runtimeconfig.json', 'exo_apo.dll', 'exo-apo-helper.exe')
foreach ($relative in $required) {
    if (-not [IO.File]::Exists((Join-Path $rootPath $relative))) { throw "Required payload file is missing: $relative" }
}
if (-not [IO.File]::Exists((Join-Path $rootPath 'exo-ui.exe')) -and -not [IO.File]::Exists((Join-Path $rootPath 'wwwroot\index.html'))) {
    throw "Required UI entry point is missing: exo-ui.exe or wwwroot\index.html"
}
$files = @(Get-ExoPayloadFiles $rootPath | Sort-Object | ForEach-Object {
        @{ path = $_.Substring($rootPath.Length + 1).Replace('\', '/'); size = ([IO.FileInfo]$_).Length; sha256 = (Get-ExoPayloadHash $_) }
    })
$json = @{ schema = 1; version = $Version; files = $files } | ConvertTo-Json -Depth 5 -Compress
[IO.File]::WriteAllText((Join-Path $rootPath 'payload-manifest.json'), $json, [Text.UTF8Encoding]::new($false))
$null = Test-ExoPayload $rootPath
Write-Output "Recorded $($files.Count) payload hashes."
