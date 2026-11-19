#Requires -Version 7.0
<#
.SYNOPSIS
    Releases one Exo app: version bump, release build, update mirror, exo-site deploy, live check.
.DESCRIPTION
    Preflight first, read only: -Repo is a git checkout with no uncommitted changes to tracked files, on
    release/<Version>; its VERSION is older than -Version and no v<Version> or built/v<Version> tag exists; CHANGELOG.md
    has the "## <Version> - <date>" entry; the app's What's New file has an entry for <Version>; exo-site sits beside -Repo
    and its tests pass.
    Then, in order: VERSION is set to <Version> (launcher: tools\Publish-Docs.ps1 writes the release page); the repo's
    tools\Build-Release.ps1 writes artifacts\ (installer, .sha256, whats-new.json); CHANGELOG.md is copied into artifacts\
    and must hold the entry; publish-release.ps1 -WhatIf must pass; publish-release.ps1 uploads to the update mirror,
    verifies /v1/<app>/latest and asks the Worker for the release card; exo-site builds (its build reads the version the mirror serves
    now) and deploys with wrangler@4.158.0; then a live check of updates.getexo.app and getexo.app (the change list is
    checked at the update address; getexo.app's Changelog page refreshes every 15 minutes, so it only warns). The code
    lives on GitHub; there is no separate code backup.
    When the build writes the app's file list (artifacts\payload-manifest.json, Exo Cinema from 1.5), publish-release.ps1
    gets -Payload and the release can be installed file by file (misc\updates\file-level-updates.md in the Exo folder).
    The version bump is left uncommitted. Nothing is pushed, tagged or made into a GitHub release.
    -WhatIf runs the preflight and exo-site's tests, prints every step it would take, and changes, builds, uploads,
    deploys and posts nothing. Preflight failures are listed and the plan still prints. A real run stops at the first
    failure, and says whether the release was already live by then. Exit 0 on pass, 1 on fail.
.PARAMETER App
    launcher or cinema.
.PARAMETER Repo
    The app's repository: ExoLauncher or ExoCinema.
.PARAMETER Version
    The new version, X.Y.Z. It must be newer than the repo's VERSION.
.PARAMETER SetupOnly
    Publish no file list for this release, so installed copies update through the full Setup. For a release that changes
    what only Setup writes (the uninstaller, shortcuts, registry entries).
.EXAMPLE
    pwsh -File cloud\updates\release.ps1 -App cinema -Repo C:\Users\Erix\Code\Exo\ExoCinema -Version 1.5.1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('launcher', 'cinema')][string]$App,
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][ValidatePattern('^\d{1,4}\.\d{1,4}\.\d{1,4}$')][string]$Version,
    # This release must be installed by Setup (it changes what only Setup writes: the uninstaller, shortcuts, registry
    # entries): no file list is published, so every installed copy downloads the full installer for it.
    [switch]$SetupOnly
)

$ErrorActionPreference = 'Stop'
# The Wrangler pin every cloud README and script uses (publish-release.ps1 has the same one).
$WranglerVersion = '4.158.0'
$Base = 'https://updates.getexo.app'
$Front = 'https://getexo.app'
$Dash = [char]0x2014
$Info = @{
    launcher = @{ Name = 'ExoLauncher'; WhatsNew = 'ui\src\lib\whatsNew.json'; Docs = $true }
    cinema   = @{ Name = 'ExoCinema'; WhatsNew = 'src\ExoCinema\wwwroot\whats-new.json'; Docs = $false }
}[$App]
$Name = $Info.Name
$Tag = "v$Version"
$RepoPath = [IO.Path]::GetFullPath($Repo)
$Artifacts = Join-Path $RepoPath 'artifacts'
$Changelog = Join-Path $RepoPath 'CHANGELOG.md'
$SitePath = Join-Path (Split-Path -Parent $RepoPath) 'exo-site'
# The entry's heading carries its date ("## 1.5.0 — November 12, 2026"). A heading without one ("not released yet") is
# refused before anything is built: it would go out in the change list, and exo-site's feed cannot be built from it
# (Cinema 1.5.0's run, 2026-11-12).
$ChangelogHeading = "^## $([regex]::Escape($Version)) $Dash (January|February|March|April|May|June|July|August|September|October|November|December) \d{1,2}, \d{4}\s*$"
$Blockers = [System.Collections.Generic.List[string]]::new()
$StepName = 'start'
$Live = $false
$exitCode = 0

function Step([string]$Text) {
    $script:StepName = $Text
    Write-Host "`n== $Text" -ForegroundColor Cyan
}

# A check. A real run stops at the first failure; -WhatIf lists it and keeps printing the plan.
function Require([bool]$Ok, [string]$What) {
    if ($Ok) { Write-Host "  [PASS] $What"; return }
    Write-Host "  [FAIL] $What" -ForegroundColor Red
    if (-not $WhatIfPreference) { throw $What }
    $script:Blockers.Add($What)
}

# A command. -WhatIf prints it instead; -ReadOnly commands (the site's tests) run in -WhatIf too. A non-zero exit fails.
function Invoke-Tool([string]$Label, [string]$Exe, [string[]]$Arguments, [string]$Cwd, [switch]$ReadOnly) {
    $shown = "$Exe $($Arguments -join ' ')   (in $Cwd)"
    if ($WhatIfPreference -and -not $ReadOnly) {
        Write-Host "  WHATIF would run: $shown" -ForegroundColor Yellow
        return
    }
    Write-Host "  > $shown"
    $code = -1
    $problem = ''
    Push-Location -LiteralPath $Cwd
    try {
        & $Exe @Arguments
        $code = $LASTEXITCODE
    } catch {
        $problem = $_.Exception.Message
    } finally {
        Pop-Location
    }
    Require ($code -eq 0) "$Label exits 0 (exit $code$(if ($problem) { ": $problem" }))"
}

# Polls a live URL until $Holds accepts its text (6 tries, 10 s apart: the mirror and the edge take a moment).
# -Soft: a page that is refreshed on its own schedule; not there yet is a warning, never a failed release.
function Test-Live([string]$Url, [scriptblock]$Holds, [string]$Expect, [switch]$Soft) {
    if ($WhatIfPreference) { Write-Host "  WHATIF would check $Url $Expect" -ForegroundColor Yellow; return }
    $served = ''
    for ($try = 1; $try -le 6; $try++) {
        try {
            $served = [string](Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 30).Content
            if (& $Holds $served) { Require $true "$Url $Expect"; return }
        } catch {
            $served = $_.Exception.Message
        }
        Start-Sleep -Seconds 10
    }
    if ($Soft) { Write-Host "  [WARN] $Url does not show it yet ($Expect); the page refreshes within 15 minutes." -ForegroundColor Yellow; return }
    Require $false "$Url $Expect after 6 tries (served: $($served.Substring(0, [Math]::Min(120, $served.Length))))"
}

function Test-WhatsNew([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        return [bool](@(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) | Where-Object { $_.version -eq $Version })
    } catch {
        return $false
    }
}

try {
    Step "1. Preflight: $Name $Version in $RepoPath (read only)"
    $inRepo = Test-Path -LiteralPath (Join-Path $RepoPath '.git')
    Require $inRepo '-Repo is a git checkout'
    $versionFile = Join-Path $RepoPath 'VERSION'
    $current = if (Test-Path -LiteralPath $versionFile) { (Get-Content -LiteralPath $versionFile -Raw).Trim() } else { '' }
    Require ($current -match '^\d+\.\d+\.\d+$' -and ([version]$Version -gt [version]$current)) "-Version $Version is newer than VERSION ('$current')"
    $changed = if ($inRepo) { @(git -C $RepoPath status --porcelain --untracked-files=no 2>$null) } else { @() }
    Require ($inRepo -and $changed.Count -eq 0) "no uncommitted changes to tracked files ($($changed.Count) listed)"
    $branch = if ($inRepo) { [string](git -C $RepoPath branch --show-current 2>$null) } else { '' }
    Require ($branch -eq "release/$Version") "on release/$Version (now '$branch')"
    $tags = if ($inRepo) { @(git -C $RepoPath tag --list $Tag "built/$Tag" 2>$null) } else { @() }
    Require ($tags.Count -eq 0) "no $Tag or built/$Tag tag yet"
    Require ((Test-Path -LiteralPath $Changelog) -and (Select-String -LiteralPath $Changelog -Pattern $ChangelogHeading -Quiet)) "CHANGELOG.md has the '## $Version' entry"
    Require (Test-WhatsNew (Join-Path $RepoPath $Info.WhatsNew)) "$($Info.WhatsNew) has an entry for $Version"
    $siteOk = Test-Path -LiteralPath (Join-Path $SitePath 'package.json')
    Require $siteOk "exo-site beside the app repo ($SitePath)"
    if ($siteOk) { Invoke-Tool 'exo-site tests (npm test)' 'npm.cmd' @('test') $SitePath -ReadOnly }

    Step "2. Set VERSION to $Version$(if ($Info.Docs) { ', then write the release page' })"
    if ($WhatIfPreference) {
        Write-Host "  WHATIF would write VERSION: $current -> $Version" -ForegroundColor Yellow
    } else {
        [IO.File]::WriteAllText($versionFile, "$Version`n", [Text.UTF8Encoding]::new($false))
        Write-Host "  VERSION: $current -> $Version"
    }
    if ($Info.Docs) { Invoke-Tool 'release docs (Publish-Docs.ps1)' 'pwsh' @('-NoProfile', '-File', 'tools\Publish-Docs.ps1') $RepoPath }

    Step "3. Release build: tools\Build-Release.ps1 writes artifacts\"
    $buildStart = Get-Date
    Invoke-Tool 'release build (Build-Release.ps1)' 'pwsh' @('-NoProfile', '-File', 'tools\Build-Release.ps1') $RepoPath

    Step "4. Artifacts: $Name-Setup.exe, its .sha256, whats-new.json and CHANGELOG.md"
    $outputs = @("$Name-Setup.exe", "$Name-Setup.exe.sha256", 'whats-new.json')
    if ($WhatIfPreference) {
        Write-Host "  WHATIF would check $($outputs -join ', ') in $Artifacts, copy CHANGELOG.md there and check its '$Version' entry" -ForegroundColor Yellow
    } else {
        foreach ($file in $outputs) {
            $path = Join-Path $Artifacts $file
            Require ((Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).LastWriteTime -ge $buildStart) "$file written by this build"
        }
        Copy-Item -LiteralPath $Changelog -Destination (Join-Path $Artifacts 'CHANGELOG.md') -Force
        Require (Select-String -LiteralPath (Join-Path $Artifacts 'CHANGELOG.md') -Pattern $ChangelogHeading -Quiet) "artifacts\CHANGELOG.md has the '$Version' entry"
    }

    $publish = Join-Path $PSScriptRoot 'publish-release.ps1'
    $publishArgs = @('-NoProfile', '-File', $publish, '-App', $App, '-Tag', $Tag, '-Dir', $Artifacts)
    # File-level updates (misc\updates\file-level-updates.md): when this build wrote the app's file list beside the
    # installer, the app folder it was built from goes along, so installed copies download only the files that changed.
    $payloadDir = Join-Path $RepoPath 'out\release\payload'
    $fileList = Join-Path $Artifacts 'payload-manifest.json'
    if ($SetupOnly) {
        Write-Host '  -SetupOnly: no file list is published; this release updates through Setup only.'
    } elseif ($WhatIfPreference) {
        Write-Host "  WHATIF would add -Payload $payloadDir when the build writes artifacts\payload-manifest.json (file-level updates)" -ForegroundColor Yellow
    } elseif ((Test-Path -LiteralPath $fileList) -and (Get-Item -LiteralPath $fileList).LastWriteTime -ge $buildStart -and (Test-Path -LiteralPath (Join-Path $payloadDir 'payload-manifest.json'))) {
        $publishArgs += @('-Payload', $payloadDir)
        Write-Host "  File-level update: the app folder $payloadDir goes up with this release."
    } else {
        Write-Host '  No file list from this build: the release updates through Setup only.'
    }
    Step "5. Dry run: publish-release.ps1 -WhatIf (must pass before anything uploads)"
    Invoke-Tool 'publish-release.ps1 -WhatIf' 'pwsh' ($publishArgs + '-WhatIf') $RepoPath

    Step "6. Upload, verify and card: publish-release.ps1 (the Worker posts the release card last)"
    Invoke-Tool 'publish-release.ps1 (upload, verify, release card)' 'pwsh' ($publishArgs + '-Publish') $RepoPath
    if (-not $WhatIfPreference) { $Live = $true }

    Step "7. exo-site: build (it reads the version the mirror serves now), then deploy"
    Invoke-Tool 'exo-site build (npm run build)' 'npm.cmd' @('run', 'build') $SitePath
    Invoke-Tool "exo-site deploy (wrangler@$WranglerVersion)" 'npx.cmd' @('-y', "wrangler@$WranglerVersion", 'deploy') $SitePath

    Step "8. Live check"
    Test-Live "$Base/v1/$App/latest" { param($t) ([string](ConvertFrom-Json $t).tag) -eq $Tag } "names $Tag"
    Test-Live "$Front/download/" { param($t) [regex]::IsMatch($t, "data-release=`"$App`"[\s\S]*?<span data-version>Version $([regex]::Escape($Version))<") } "shows Version $Version for $App"
    # The change list, where it is published: the update address has the new CHANGELOG.md at once. getexo.app's
    # Changelog page is built from it and refreshed at the edge every 15 minutes, so the page not showing the version
    # yet is a warning (1.4.11's run stopped here for that reason).
    Test-Live "$Base/v1/$App/changelog" { param($t) [regex]::IsMatch($t, "(?m)$ChangelogHeading") } "has the '## $Version' entry"
    Test-Live "$Front/$App/changelog" { param($t) $t.Contains($Version) } "has the $Version entry" -Soft
    if (-not $WhatIfPreference) { Write-Host "`nPASS: $Name $Tag is live: $Base serves it, and $Front shows $Version." -ForegroundColor Green }
} catch {
    $exitCode = 1
    Write-Host "`nSTOP at '$StepName': $($_.Exception.Message)" -ForegroundColor Red
    if ($Live) {
        Write-Host "$Name $Tag is already live on $Base. Finish the site by hand: cd `"$SitePath`"; npm run build; npx wrangler@$WranglerVersion deploy" -ForegroundColor Yellow
    }
}

if ($WhatIfPreference -and $exitCode -eq 0) {
    if ($Blockers.Count) {
        $exitCode = 1
        Write-Host "`nWHATIF: a real run would stop. $($Blockers.Count) check(s) fail: $($Blockers -join '; ')" -ForegroundColor Red
    } else {
        Write-Host "`nWHATIF PASS: $Name ${Tag}: every check passes. Nothing was built, written, uploaded, deployed or posted." -ForegroundColor Green
    }
}
exit $exitCode
