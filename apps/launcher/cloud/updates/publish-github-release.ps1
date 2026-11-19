#Requires -Version 7.0
<#
.SYNOPSIS
    Puts a published GitHub release of Exo Launcher into the update mirror: it stages the release's files the way
    tools\Build-Release.ps1 writes them, then runs publish-release.ps1 on them (the dry run unless -Publish).
.DESCRIPTION
    The apps never ask GitHub for their own updates; they ask updates.getexo.app, which serves R2 bucket exo-releases. This
    script is the bridge from a GitHub release to that bucket, used by .github/workflows/launcher-release-mirror.yml when a
    release is published and by hand on any PC with gh and wrangler.

    1. Reads the release from GitHub (gh release view): it must be published (not a draft, not a pre-release) and hold
       ExoLauncher-<version>-Setup.exe and its .sha256 (ExoLauncher-Setup.exe and its .sha256 are accepted too).
    2. Downloads both and checks each against GitHub's own record of the asset: the size, and the SHA-256 GitHub computed
       at upload ("digest"). The .sha256 must name the installer's SHA-256 (the first word) and its file. Releases are not
       signed (no keys, owner's rule), so there is no signature to check: the SHA-256, the size and the host are the checks.
    3. Reads the tag's committed tree (never the working tree): apps/launcher/VERSION must be the tag's version; the What's
       New entry for the version (ui/src/lib/whatsNew.json) and CHANGELOG.md (it must hold the "## <version>" entry).
    4. Stages, byte for byte as Build-Release.ps1 and release.ps1 write them: ExoLauncher-Setup.exe (the installer, renamed),
       ExoLauncher-Setup.exe.sha256 ("<hash>  ExoLauncher-Setup.exe", ASCII, no newline), whats-new.json (the entry as
       ConvertTo-Json -Depth 4, UTF-8 without BOM, no newline) and CHANGELOG.md. When the release carries a whats-new.json
       asset (the one Build-Release wrote beside the installer), that file is used instead, and a difference from the
       tag's entry is a warning.
    5. Runs publish-release.ps1 -App launcher -Tag <tag> -Dir <staging> (with -Publish and -NoAnnounce when given), which
       checks what the mirror already holds, uploads, verifies /v1/launcher/latest and puts the old latest.json back on a
       mismatch (cloud/updates/README.md).
    6. With -Publish, checks /v1/launcher/latest once more on its own: it must name the tag with the installer's size and
       SHA-256 as GitHub recorded them. Without -Publish it prints the R2 keys the run would write.

    gh needs GH_TOKEN (or gh auth login); wrangler needs CLOUDFLARE_API_TOKEN and CLOUDFLARE_ACCOUNT_ID (or wrangler login)
    to read the bucket, even in the dry run. Nothing is printed of either.
.PARAMETER Tag
    The release tag, vX.Y.Z. Without it, the newest published GitHub release.
.PARAMETER Repo
    The GitHub repository. UhhErix/Exo-Launcher.
.PARAMETER Publish
    Uploads for real. Without it the run is publish-release.ps1's dry run: checks and plan, nothing uploaded.
.PARAMETER NoAnnounce
    Passed to publish-release.ps1: the Worker is not asked for the Discord release card.
.PARAMETER Out
    The staging folder (must be empty or absent). A temporary folder by default, removed afterwards.
.EXAMPLE
    pwsh -File cloud\updates\publish-github-release.ps1                  # dry run for the newest GitHub release
.EXAMPLE
    pwsh -File cloud\updates\publish-github-release.ps1 -Tag v3.26.0 -Publish
#>
[CmdletBinding()]
param(
    [ValidatePattern('^v\d{1,4}\.\d{1,4}\.\d{1,4}$')][string]$Tag,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repo = 'UhhErix/Exo-Launcher',
    [switch]$Publish,
    [switch]$NoAnnounce,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
$Base = 'https://updates.getexo.app'
$Setup = 'ExoLauncher-Setup.exe'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$Launcher = 'apps/launcher'
$Dash = [char]0x2014

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Pass([string]$text) { Write-Host "  [PASS] $text" }
function Fail([string]$text) { throw $text }
function Get-FileSha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Invoke-Gh([string[]]$arguments) {
    # stdout only (the JSON); stderr is kept apart and shown only on a failure.
    $errors = [IO.Path]::GetTempFileName()
    try {
        $output = & gh @arguments 2>$errors
        $code = $LASTEXITCODE
        if ($code -ne 0) { Fail "gh $($arguments[0]) $($arguments[1]) failed (exit $code): $((Get-Content -LiteralPath $errors -Raw) -replace '\s+', ' ')" }
        return (@($output | ForEach-Object { "$_" }) -join "`n")
    } finally { Remove-Item -LiteralPath $errors -Force -ErrorAction SilentlyContinue }
}

# A committed file's exact bytes at $rev, written to $file ($false when the tree has no such path). Read through a process so
# PowerShell never re-encodes the bytes.
function Save-GitBlob([string]$rev, [string]$path, [string]$file) {
    $info = [System.Diagnostics.ProcessStartInfo]::new('git')
    foreach ($a in @('-C', $RepoRoot, 'show', "${rev}:$path")) { $info.ArgumentList.Add($a) }
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($info)
    $errors = $process.StandardError.ReadToEndAsync()
    $to = [IO.File]::Create($file)
    try { $process.StandardOutput.BaseStream.CopyTo($to) } finally { $to.Dispose() }
    $process.WaitForExit()
    if ($process.ExitCode -eq 0) { return $true }
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    if ($errors.Result -match 'does not exist|exists on disk, but not in') { return $false }
    Fail "git show ${rev}:$path failed: $($errors.Result.Trim())"
}

$ownOut = -not $Out
$exitCode = 0
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Fail 'gh (GitHub CLI) is not installed.' }

    Step "GitHub release $(if ($Tag) { $Tag } else { '(newest)' }) in $Repo"
    $fields = 'tagName,name,isDraft,isPrerelease,publishedAt,assets'
    $releaseArgs = @('release', 'view') + @(if ($Tag) { $Tag }) + @('-R', $Repo, '--json', $fields)
    $release = Invoke-Gh $releaseArgs | ConvertFrom-Json
    if (-not $Tag) { $Tag = [string]$release.tagName }
    if ($Tag -notmatch '^v\d{1,4}\.\d{1,4}\.\d{1,4}$') { Fail "the newest release's tag '$Tag' is not vX.Y.Z." }
    if ([string]$release.tagName -ne $Tag) { Fail "GitHub answered for '$($release.tagName)', not $Tag." }
    if ($release.isDraft -or $release.isPrerelease) { Fail "$Tag is a draft or a pre-release; the mirror only serves stable releases." }
    $Version = $Tag.Substring(1)
    Pass "$Tag ('$($release.name)'), published $($release.publishedAt)"

    $assets = @($release.assets)
    $installerAsset = $assets | Where-Object { $_.name -in @("ExoLauncher-$Version-Setup.exe", $Setup) } | Select-Object -First 1
    if (-not $installerAsset) { Fail "$Tag has no ExoLauncher-$Version-Setup.exe asset (assets: $(($assets.name) -join ', '))." }
    $shaAsset = $assets | Where-Object { $_.name -eq "$($installerAsset.name).sha256" } | Select-Object -First 1
    if (-not $shaAsset) { Fail "$Tag has no $($installerAsset.name).sha256 asset." }
    $notesAsset = $assets | Where-Object { $_.name -eq 'whats-new.json' } | Select-Object -First 1
    foreach ($asset in @($installerAsset, $shaAsset) + @($notesAsset | Where-Object { $_ })) {
        if ($asset.state -ne 'uploaded') { Fail "$($asset.name) is '$($asset.state)', not uploaded." }
        if ([string]$asset.digest -notmatch '^sha256:[0-9a-f]{64}$') { Fail "GitHub records no SHA-256 for $($asset.name) ('$($asset.digest)')." }
    }
    $githubHash = ([string]$installerAsset.digest).Substring(7)
    Pass "$($installerAsset.name): $($installerAsset.size) bytes, GitHub's SHA-256 $githubHash"

    if ($ownOut) { $Out = Join-Path ([IO.Path]::GetTempPath()) "exo-gh-release-$Tag-$([guid]::NewGuid().ToString('N'))" }
    if ((Test-Path -LiteralPath $Out) -and @(Get-ChildItem -LiteralPath $Out -Force).Count) { Fail "-Out $Out is not empty." }
    $download = Join-Path $Out 'download'
    $stage = Join-Path $Out 'stage'
    New-Item -ItemType Directory -Path $download, $stage -Force | Out-Null

    Step "Download and check against GitHub's record"
    $patterns = @($installerAsset, $shaAsset) + @($notesAsset | Where-Object { $_ }) | ForEach-Object { '--pattern'; $_.name }
    $null = Invoke-Gh (@('release', 'download', $Tag, '-R', $Repo, '--dir', $download, '--clobber') + $patterns)
    foreach ($asset in @($installerAsset, $shaAsset) + @($notesAsset | Where-Object { $_ })) {
        $file = Join-Path $download $asset.name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Fail "$($asset.name) was not downloaded." }
        $size = (Get-Item -LiteralPath $file).Length
        $hash = Get-FileSha256 $file
        if ($size -ne [long]$asset.size) { Fail "$($asset.name) is $size bytes; GitHub records $($asset.size)." }
        if ("sha256:$hash" -ne [string]$asset.digest) { Fail "$($asset.name) has SHA-256 $hash; GitHub records $($asset.digest)." }
        Pass "$($asset.name): $size bytes, SHA-256 matches GitHub's"
    }
    $installer = Join-Path $download $installerAsset.name
    $shaWords = ((Get-Content -LiteralPath (Join-Path $download $shaAsset.name) -Raw).Trim() -split '\s+')
    if ($shaWords[0].ToLowerInvariant() -ne $githubHash) { Fail "$($shaAsset.name) names $($shaWords[0]), not the installer's $githubHash." }
    if ($shaWords.Count -gt 1 -and $shaWords[1].TrimStart('*') -ne $installerAsset.name) { Fail "$($shaAsset.name) names the file '$($shaWords[1])', not $($installerAsset.name)." }
    Pass "$($shaAsset.name) names the installer's SHA-256"

    Step "The tag's committed tree ($Tag)"
    $rev = "refs/tags/$Tag"
    & git -C $RepoRoot rev-parse --verify --quiet "$rev^{commit}" *> $null
    if ($LASTEXITCODE -ne 0) {
        # A shallow checkout (the workflow's) fetches only the tag's commit; a full clone is never made shallow.
        $depth = @(if ((& git -C $RepoRoot rev-parse --is-shallow-repository) -eq 'true') { '--depth=1' })
        & git -C $RepoRoot fetch --quiet --no-tags @depth origin "+refs/tags/${Tag}:refs/tags/$Tag" 2>&1 | Out-Null
        & git -C $RepoRoot rev-parse --verify --quiet "$rev^{commit}" *> $null
        if ($LASTEXITCODE -ne 0) { Fail "the tag $Tag is not in $RepoRoot and could not be fetched from origin." }
    }
    $commit = (& git -C $RepoRoot rev-parse "$rev^{commit}").Trim()
    $versionFile = Join-Path $Out 'VERSION'
    if (-not (Save-GitBlob $commit "$Launcher/VERSION" $versionFile)) { Fail "$Tag has no $Launcher/VERSION." }
    $treeVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
    if ($treeVersion -ne $Version) { Fail "$Launcher/VERSION at $Tag says '$treeVersion', not $Version." }
    Pass "$Launcher/VERSION at $($commit.Substring(0, 12)) is $Version"

    $whatsNewSource = Join-Path $Out 'whatsNew.json'
    if (-not (Save-GitBlob $commit "$Launcher/ui/src/lib/whatsNew.json" $whatsNewSource)) { Fail "$Tag has no ui/src/lib/whatsNew.json." }
    # As tools\Build-Release.ps1 writes it.
    $whatsNew = @(Get-Content -LiteralPath $whatsNewSource -Raw | ConvertFrom-Json) | Where-Object version -EQ $Version | Select-Object -First 1
    if (-not $whatsNew) { Fail "ui/src/lib/whatsNew.json at $Tag has no entry for $Version." }
    $notesFile = Join-Path $stage 'whats-new.json'
    if ($notesAsset) {
        # The file Build-Release wrote for this installer wins: it is what the build that made it said.
        Copy-Item -LiteralPath (Join-Path $download 'whats-new.json') -Destination $notesFile
        $assetNotes = Get-Content -LiteralPath $notesFile -Raw | ConvertFrom-Json
        if ([string]$assetNotes.version -ne $Version) { Fail "the release's whats-new.json is for '$($assetNotes.version)', not $Version." }
        if (($assetNotes | ConvertTo-Json -Depth 6 -Compress) -ne ($whatsNew | ConvertTo-Json -Depth 6 -Compress)) {
            Write-Host "  [WARN] the release's whats-new.json asset is not the $Version entry in ui/src/lib/whatsNew.json at $Tag; the asset is used." -ForegroundColor Yellow
        }
        Pass "whats-new.json: the release's asset, $(@($assetNotes.items).Count) item(s) for $Version, SHA-256 $(Get-FileSha256 $notesFile)"
    } else {
        # As tools\Build-Release.ps1 writes it.
        Set-Content -LiteralPath $notesFile -Value ($whatsNew | ConvertTo-Json -Depth 4) -NoNewline -Encoding utf8NoBOM
        Pass "whats-new.json: built from the tag, $(@($whatsNew.items).Count) item(s) for $Version, SHA-256 $(Get-FileSha256 $notesFile)"
    }

    $changelog = Join-Path $stage 'CHANGELOG.md'
    if (-not (Save-GitBlob $commit "$Launcher/CHANGELOG.md" $changelog)) { Fail "$Tag has no $Launcher/CHANGELOG.md." }
    $heading = "^## $([regex]::Escape($Version)) $Dash (January|February|March|April|May|June|July|August|September|October|November|December) \d{1,2}, \d{4}\s*$"
    if (-not (Select-String -LiteralPath $changelog -Pattern $heading -Quiet)) { Fail "CHANGELOG.md at $Tag has no '## $Version $Dash <date>' entry." }
    Pass "CHANGELOG.md has the $Version entry, SHA-256 $(Get-FileSha256 $changelog)"

    Step 'Staged as Build-Release.ps1 writes them'
    Copy-Item -LiteralPath $installer -Destination (Join-Path $stage $Setup)
    Set-Content -LiteralPath (Join-Path $stage "$Setup.sha256") -Value "$githubHash  $Setup" -NoNewline -Encoding ascii
    foreach ($file in Get-ChildItem -LiteralPath $stage -File) { Write-Host "  $($file.Name): $($file.Length) bytes, SHA-256 $(Get-FileSha256 $file.FullName)" }

    if (-not $Publish) {
        Step "R2 keys a -Publish run writes in bucket exo-releases (each only when not already there with the same bytes)"
        foreach ($key in @("launcher/$Tag/$Setup", "launcher/$Tag/$Setup.sha256", "launcher/$Tag/whats-new.json", 'launcher/latest.json', 'launcher/CHANGELOG.md')) { Write-Host "  $key" }
    }

    Step "publish-release.ps1 $(if ($Publish) { '-Publish' } else { '(dry run)' })"
    $publishArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'publish-release.ps1'), '-App', 'launcher', '-Tag', $Tag, '-Dir', $stage)
    if ($Publish) { $publishArgs += '-Publish' }
    if ($NoAnnounce) { $publishArgs += '-NoAnnounce' }
    & pwsh @publishArgs
    if ($LASTEXITCODE -ne 0) { Fail "publish-release.ps1 failed (exit $LASTEXITCODE)." }

    if ($Publish) {
        Step "Independent check: $Base/v1/launcher/latest"
        $latest = $null
        foreach ($try in 1..6) {
            try { $latest = Invoke-RestMethod -Uri "$Base/v1/launcher/latest" -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 30 } catch { $latest = $null }
            if ($latest -and $latest.tag -eq $Tag) { break }
            if ($try -lt 6) { Start-Sleep -Seconds 10 }
        }
        if (-not $latest -or $latest.tag -ne $Tag) { Fail "/v1/launcher/latest names '$($latest.tag)', not $Tag." }
        if ($latest.checks.setup.sha256 -ne $githubHash -or [long]$latest.checks.setup.size -ne [long]$installerAsset.size) {
            Fail "/v1/launcher/latest offers SHA-256 $($latest.checks.setup.sha256), $($latest.checks.setup.size) bytes; GitHub's $Tag installer is $githubHash, $($installerAsset.size) bytes."
        }
        Pass "/v1/launcher/latest names $Tag with GitHub's installer SHA-256 and size"
        Write-Host "`nPASS: GitHub release $Tag is the update the apps are offered." -ForegroundColor Green
    } else {
        Write-Host "`nDRY RUN PASS: GitHub release $Tag checks out and stages; nothing uploaded." -ForegroundColor Green
    }
} catch {
    Write-Host "`nFAIL: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($ownOut -and $Out) { Remove-Item -LiteralPath $Out -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $exitCode
