param(
    [string]$WorktreeRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [switch]$IncludeTarget,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

$resolved = (Resolve-Path -LiteralPath $WorktreeRoot).Path
$relPaths = @(
    'apps\launcher\ui\node_modules',
    'apps\launcher\mouse\node_modules',
    'apps\launcher\ui\dist',
    'apps\launcher\mouse\dist',
    'apps\launcher\ui\.vite',
    'apps\launcher\ExoLauncher\bin',
    'apps\launcher\ExoLauncher\obj',
    'apps\launcher\ExoLauncher.Tests\bin',
    'apps\launcher\ExoLauncher.Tests\obj',
    'apps\launcher\ExoLauncher.SteamIpc\bin',
    'apps\launcher\ExoLauncher.SteamIpc\obj',
    'apps\launcher\shaders\engine\src\ExoShaders.Service\bin',
    'apps\launcher\shaders\engine\src\ExoShaders.Service\obj',
    'apps\launcher\shaders\engine\tests\ExoShaders.Service.Tests\bin',
    'apps\launcher\shaders\engine\tests\ExoShaders.Service.Tests\obj'
)
if ($IncludeTarget) {
    $relPaths = @('apps\launcher\native-ui\target', 'target') + $relPaths
}

$removed = 0
foreach ($rel in $relPaths) {
    $full = Join-Path $resolved $rel
    if (-not (Test-Path -LiteralPath $full)) { continue }
    if ($WhatIf) {
        Write-Output "[WhatIf] Would remove $full"
        continue
    }
    cmd /c "rmdir /s /q `"$full`"" 2>$null
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not (Test-Path -LiteralPath $full)) {
        $removed++
        Write-Output "Removed $rel"
    } else {
        Write-Warning "Could not fully remove $rel (in use)"
    }
}

Write-Output "Clean-Worktree complete: removed $removed folder(s) under $resolved"
