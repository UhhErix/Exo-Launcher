# Builds Exo Shaders (GPL-3.0-or-later; its source is published at getexo.app/source) for a release from one pinned commit: the engine
# (engine\build\publish-service.ps1, the Exo build) and its page (ui, npm run build), in a temporary git worktree of the
# source repository, so a release never builds from a dirty working tree. The commit is the head of -ShadersBranch, unless
# -ShadersCommit names another one. Prints the staging folder, which holds engine\, ui\ (the page's dist), the licence
# files and commit.txt (the short commit), and removes the worktree.
#   pwsh -File tools\Build-ShadersEngine.ps1 [-ShadersWork C:\path\to\exo-shaders] [-ShadersCommit <sha>]
[CmdletBinding()]
param(
    [string]$ShadersWork = (Join-Path $env:USERPROFILE 'Code\Exo\exo-shaders'),
    [string]$ShadersBranch = 'fix/warm-restart-watchdog',
    [string]$ShadersCommit
)
$ErrorActionPreference = 'Stop'

# The pin: the branch head, or the commit named explicitly. A named commit is the release's choice; the default is the head.
$head = (git -C $ShadersWork rev-parse --verify --quiet "refs/heads/$ShadersBranch" 2>$null)
if ($LASTEXITCODE -or -not $head) { throw "No branch $ShadersBranch in $ShadersWork (pass -ShadersWork)." }
if ($ShadersCommit) {
    $pin = (git -C $ShadersWork rev-parse --verify --quiet "$ShadersCommit^{commit}" 2>$null)
    if ($LASTEXITCODE -or -not $pin) { throw "-ShadersCommit $ShadersCommit is not a commit in $ShadersWork." }
} else {
    $pin = $head
}
$pin = $pin.Trim()
if (-not $ShadersCommit -and $pin -ne $head.Trim()) { throw "The pinned commit $pin is not $ShadersBranch's head $($head.Trim()); pass -ShadersCommit to build it." }
$short = $pin.Substring(0, 12)

$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$tree = Join-Path ([IO.Path]::GetTempPath()) "exo-shaders-tree-$id"
$stage = Join-Path ([IO.Path]::GetTempPath()) "exo-shaders-$short-$id"

git -C $ShadersWork worktree add --detach $tree $pin | Out-Host
if ($LASTEXITCODE) { throw "git worktree add $tree $pin failed." }
try {
    $built = (git -C $tree rev-parse HEAD).Trim()
    if ($built -ne $pin) { throw "The worktree is at $built, not the pinned $pin." }

    pwsh -NoProfile -File (Join-Path $tree 'engine\build\publish-service.ps1') | Out-Host
    if ($LASTEXITCODE) { throw "publish-service.ps1 failed ($LASTEXITCODE)." }

    Push-Location (Join-Path $tree 'ui')
    try {
        npm ci --no-audit --no-fund | Out-Host
        if ($LASTEXITCODE) { throw "npm ci in the Exo Shaders page failed ($LASTEXITCODE)." }
        npm run build | Out-Host
        if ($LASTEXITCODE) { throw "npm run build in the Exo Shaders page failed ($LASTEXITCODE)." }
    } finally { Pop-Location }

    New-Item -ItemType Directory -Force $stage | Out-Null
    robocopy (Join-Path $tree 'engine\out\service') (Join-Path $stage 'engine') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the Exo Shaders engine failed ($LASTEXITCODE)." }
    robocopy (Join-Path $tree 'ui\dist') (Join-Path $stage 'ui') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying the Exo Shaders page failed ($LASTEXITCODE)." }
    foreach ($notice in 'LICENSE', 'LICENSE-EXCEPTION.txt', 'THIRD-PARTY-NOTICES.md') {
        $source = Join-Path $tree "engine\$notice"
        if (Test-Path $source) { Copy-Item $source (Join-Path $stage $notice) -Force }
    }
    Set-Content -Path (Join-Path $stage 'commit.txt') -Value $short -NoNewline
    if (-not (Test-Path (Join-Path $stage 'engine\ExoShaders.Service.exe'))) { throw "No ExoShaders.Service.exe in $stage." }
    if (-not (Test-Path (Join-Path $stage 'ui\index.html'))) { throw "No index.html in the Exo Shaders page of $stage." }
} finally {
    git -C $ShadersWork worktree remove --force $tree | Out-Host
    if ($LASTEXITCODE) { throw "Removing the worktree $tree failed; remove it with git worktree remove --force." }
}
Write-Host "Exo Shaders $short built from $pin in $stage"
$stage
