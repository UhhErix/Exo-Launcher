#Requires -Version 7.0
<#
.SYNOPSIS
    Puts one Exo app's release into the update mirror (R2 bucket exo-releases), then checks what updates.getexo.app serves.
.DESCRIPTION
    Local checks: Exo<App>-Setup.exe, its .sha256 and whats-new.json are in -Dir; the .sha256 is the installer's SHA-256;
    whats-new.json is for -Tag. A tag's files are never replaced: when <app>/<tag>/ already holds any of the three with other
    bytes the run stops before it uploads anything (the same bytes are not uploaded again, so a re-run after a failed check is safe).
    Order, so no app is ever offered a release that is not fully there:
      1. the three files go to <app>/<tag>/;
      2. they are checked through https://updates.getexo.app by tag (200 with no redirect, the served installer's SHA-256 and
         size are the local ones, the .sha256 names the same hash, whats-new.json is for -Tag); a failure stops here, and
         <app>/latest.json was never touched;
      3. only then <app>/latest.json is uploaded, and /v1/<app>/latest must name -Tag with the three files' checks; if it does
         not, the latest.json that was there before this run is put back (or, when there was none, the run says so);
      4. CHANGELOG.md (optional: the app's change list, when -Dir holds one) goes to <app>/CHANGELOG.md and is checked.
    With -Payload (file-level updates, misc\updates\file-level-updates.md in the Exo folder), between 2 and 3: the app
    folder's files go to <app>/files/<sha256>.gz (only those the address does not serve yet; each read back and checked),
    then its file list to <app>/<tag>/payload-manifest.json. A failure there is a warning: the list is not left
    published and the release goes on, updating through Setup only.
    Older tags are deleted only with -PruneOlder and -OlderTag, and only after the new release verifies.
    The dry run is the default (as -WhatIf): the local checks, the read-only latest check and the read-only tag check, the
    plan, and nothing uploaded, deleted or verified. -Publish uploads for real.
    Last, after a live pass, it asks the exo-updates Worker to post the release card to Discord (POST /v1/<app>/announce; the Worker
    holds the webhook and posts each tag once). -NoAnnounce tells it to skip the card for this tag; a re-upload of the tag that was
    already live before this run asks for nothing. -WhatIf posts nothing and asks nothing.
    Exit 0 on pass, 1 on fail; a failed announcement is a warning, since the release is live. Wrangler is pinned by $WranglerVersion
    below; the Cloudflare login is wrangler's own. Releases are not signed (no keys, owner's rule): the apps check the SHA-256,
    the size and the host.
.PARAMETER NoAnnounce
    Skip the release card for this tag: the Worker is not asked, and nothing is recorded, so a later run can still post it.
.PARAMETER App
    launcher or cinema.
.PARAMETER Tag
    The release tag, vX.Y.Z; must equal the version in whats-new.json.
.PARAMETER Dir
    The folder holding the three release files.
.PARAMETER Payload
    The app folder the installer was built from (the app repo's out\release\payload), holding payload-manifest.json;
    -Dir must hold the same payload-manifest.json. Optional.
.PARAMETER PruneOlder
    Also delete the files of the tags named in -OlderTag. Without this switch no tag is ever deleted.
.PARAMETER OlderTag
    Older tags to delete with -PruneOlder (each must be lower than -Tag).
.EXAMPLE
    pwsh -File cloud\updates\publish-release.ps1 -App launcher -Tag v3.24.1 -Dir artifacts
.EXAMPLE
    pwsh -File cloud\updates\publish-release.ps1 -App cinema -Tag v1.5.0 -Dir artifacts -Publish
.EXAMPLE
    pwsh -File cloud\updates\publish-release.ps1 -App cinema -Tag v1.4.0 -Dir artifacts -Publish -PruneOlder -OlderTag v1.3.2
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('launcher', 'cinema')][string]$App,
    [Parameter(Mandatory)][ValidatePattern('^v\d{1,4}\.\d{1,4}\.\d{1,4}$')][string]$Tag,
    [Parameter(Mandatory)][string]$Dir,
    [switch]$PruneOlder,
    # Backfill before the schema-2 Worker is deployed: the old Worker does not pass "checks" through, so skip that one check.
    [switch]$SkipManifestChecks,
    [switch]$NoAnnounce,
    # File-level updates (misc\updates\file-level-updates.md): the app folder the installer was built from, with its
    # payload-manifest.json. Its files go up by SHA-256 (only those not there yet) and its file list under the tag, before
    # latest.json. Without it the release updates through Setup only, as before.
    [string]$Payload,
    [ValidateRange(1, 32)][int]$UploadsAtOnce = 8,
    [ValidatePattern('^v\d{1,4}\.\d{1,4}\.\d{1,4}$')][string[]]$OlderTag = @(),
    # How many times, and how far apart, a served file is asked for while the mirror and the edge catch up.
    [ValidateRange(1, 60)][int]$Tries = 9,
    [ValidateRange(0, 120)][int]$RetrySeconds = 10,
    # Uploads for real. Without it the run is the dry run (-WhatIf): checks and plan, nothing uploaded or deleted.
    [switch]$Publish,
    # test/publish.test.mjs only: the stand-ins named by EXO_TEST_WRANGLER and EXO_TEST_UPDATES_BASE are used.
    [switch]$TestRun
)

$ErrorActionPreference = 'Stop'
if (-not $Publish) { $WhatIfPreference = $true }
# A stand-in left in a shell's environment must never give a real run a false pass: they count only with -TestRun.
if (-not $TestRun -and ($env:EXO_TEST_WRANGLER -or $env:EXO_TEST_UPDATES_BASE)) {
    Write-Host "`nFAIL: EXO_TEST_WRANGLER or EXO_TEST_UPDATES_BASE is set; clear them (they are for test/publish.test.mjs)." -ForegroundColor Red
    exit 1
}
$Wrangler = if ($TestRun) { $env:EXO_TEST_WRANGLER } else { $null }
# The address the apps ask. A test runs against a local stand-in (test/publish.test.mjs).
$Base = if ($TestRun -and $env:EXO_TEST_UPDATES_BASE) { $env:EXO_TEST_UPDATES_BASE } else { 'https://updates.getexo.app' }
$Bucket = 'exo-releases'
$WranglerVersion = '4.158.0'
$Name = @{ launcher = 'ExoLauncher'; cinema = 'ExoCinema' }[$App]
$Setup = "$Name-Setup.exe"
$Version = $Tag.Substring(1)
$Failures = [System.Collections.Generic.List[string]]::new()
$Handler = [System.Net.Http.HttpClientHandler]::new()
$Handler.AllowAutoRedirect = $false
$Http = [System.Net.Http.HttpClient]::new($Handler)
$Http.Timeout = [TimeSpan]::FromMinutes(15)
$Http.DefaultRequestHeaders.UserAgent.ParseAdd('exo-publish-release')

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if (-not $ok) { $script:Failures.Add($what) }
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host "  [$mark] $what$(if ($detail) { " - $detail" })"
}

function Invoke-Wrangler([string[]]$arguments) {
    # A test's stand-in for wrangler's R2 commands (a folder, not Cloudflare); a release never sets this.
    if ($Wrangler) { $output = & node $Wrangler @arguments 2>&1 }
    else { $output = & npx -y "wrangler@$WranglerVersion" @arguments 2>&1 }
    # Each line as wrangler wrote it, without its colour codes (a stderr line would otherwise come back wrapped as a PowerShell error record).
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = ((@($output | ForEach-Object { "$_" }) -join "`n") -replace "\x1B\[[0-9;]*[A-Za-z]", '') }
}

function Invoke-WranglerOrThrow([string[]]$arguments) {
    $result = Invoke-Wrangler $arguments
    Write-Host $result.Text.TrimEnd()
    if ($result.Code -ne 0) { throw "wrangler failed (exit $($result.Code)) on $($arguments[2])." }
}

# The object's bytes saved to $file: $true when it exists, $false only when wrangler says R2 has no such key (its own
# words for that, and nothing else); any other answer stops the run, so an outage or a login prompt is never taken for
# "not there" (which would let a tag's files be replaced).
# Checked against wrangler 4.158.0 reading a key that does not exist (2026-11-10): exit 1, an empty --file left behind, and
# the line "X [ERROR] The specified key does not exist." in colour codes (the X is its error mark).
$MissingKey = '(?m)^.{0,3}\[ERROR\] The specified key does not exist\.\s*$'
function Get-Object([string]$key, [string]$file) {
    $result = Invoke-Wrangler @('r2', 'object', 'get', "$Bucket/$key", '--file', $file, '--remote')
    if ($result.Code -eq 0 -and (Test-Path -LiteralPath $file -PathType Leaf)) { return $true }
    if ($result.Code -ne 0 -and $result.Text -match $MissingKey) { return $false }
    throw "could not read $Bucket/$key (exit $($result.Code)): $($result.Text.Trim())"
}

function Publish-Object {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$key, [string]$file, [string]$type)
    if ($PSCmdlet.ShouldProcess("$Bucket/$key", "upload $(Split-Path $file -Leaf) as $type")) {
        Invoke-WranglerOrThrow @('r2', 'object', 'put', "$Bucket/$key", '--file', $file, '--content-type', $type, '--remote')
    }
}

function Remove-Object {
    [CmdletBinding(SupportsShouldProcess)]
    param([string]$key)
    if ($PSCmdlet.ShouldProcess("$Bucket/$key", 'delete')) {
        Invoke-WranglerOrThrow @('r2', 'object', 'delete', "$Bucket/$key", '--remote')
    }
}

function Invoke-Prune {
    if (-not $PruneOlder) { return }
    Step "Older tags to delete: $($OlderTag -join ', ')"
    foreach ($old in $OlderTag) {
        foreach ($file in @($Setup, "$Setup.sha256", 'whats-new.json')) { Remove-Object "$App/$old/$file" }
        # An older tag's file list goes with it (its hashed files stay: other releases share them).
        if ($Payload) { Remove-Object "$App/$old/payload-manifest.json" }
    }
}

# The status of a HEAD (0 when nothing answered).
function Get-HeadStatus([string]$url) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, $url)
    try {
        $response = $script:Http.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try { return [int]$response.StatusCode } finally { $response.Dispose() }
    } catch { return 0 } finally { $request.Dispose() }
}

# The local checks for -Payload: the folder is exactly what its file list says (every file, size and SHA-256), the list is
# for this version, and it is the list the build wrote beside the installer. Returns the distinct files, by hash.
function Read-Payload {
    $listFile = Join-Path $Payload 'payload-manifest.json'
    $beside = Join-Path $Dir 'payload-manifest.json'
    $ok = (Test-Path -LiteralPath $listFile -PathType Leaf) -and (Test-Path -LiteralPath $beside -PathType Leaf) -and ((Get-FileSha256 $listFile) -eq (Get-FileSha256 $beside))
    Check 'the payload folder''s file list is the one the build wrote beside the installer (payload-manifest.json)' $ok
    if (-not $ok) { return @() }
    $list = Get-Content -LiteralPath $listFile -Raw | ConvertFrom-Json
    Check "the file list is for $Version" ($list.schema -eq 1 -and [string]$list.version -eq $Version) "it says '$($list.version)'"
    $root = (Resolve-Path -LiteralPath $Payload).Path.TrimEnd('\')
    $wrong = @($list.files | ForEach-Object -Parallel {
            $path = Join-Path $using:root ([string]$_.path).Replace('/', '\')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne [long]$_.size -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$_.sha256) { $_.path }
        } -ThrottleLimit 8)
    $extra = @(Get-ChildItem -LiteralPath $root -Recurse -File).Count - 1 - @($list.files).Count
    Check "the payload folder is exactly its file list ($(@($list.files).Count) files)" ($wrong.Count -eq 0 -and $extra -eq 0) "$($wrong.Count) differ, $extra extra"
    return @($list.files | Group-Object sha256 | ForEach-Object { [pscustomobject]@{ Sha256 = ([string]$_.Name).ToLowerInvariant(); Size = [long]$_.Group[0].size; Path = Join-Path $root ([string]$_.Group[0].path).Replace('/', '\') } })
}

# Uploads what a file-level update needs, in the order that keeps every app safe: each file by its SHA-256 (gzipped; one
# already there is never sent again), each upload read back and checked, and the file list last, since an app only starts
# a file-level update once the list is there. $true when the release can be updated file by file. A failure here never
# stops the release: the list is not left published, and the apps use Setup for this version.
function Publish-Payload([object[]]$Files) {
    $listFile = Join-Path $Payload 'payload-manifest.json'
    $need = @($Files | ForEach-Object -Parallel {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, "$using:Base/v1/$using:App/file/$($_.Sha256)")
            try { $status = [int]($using:Http).SendAsync($request).GetAwaiter().GetResult().StatusCode } catch { $status = 0 }
            if ($status -ne 200) { $_ }
        } -ThrottleLimit 16)
    $bytes = ($need | Measure-Object Size -Sum).Sum
    Write-Host "  $($Files.Count) distinct files; $($Files.Count - $need.Count) already there; $($need.Count) to upload ($([math]::Round($bytes / 1MB, 1)) MB before gzip)"
    if ($WhatIfPreference) {
        Write-Host "  WHATIF would gzip and upload $($need.Count) file(s) to $Bucket/$App/files/<sha256>.gz, read each back, then upload $Bucket/$App/$Tag/payload-manifest.json" -ForegroundColor Yellow
        return $true
    }
    try {
        $packed = Join-Path $work 'files'
        New-Item -ItemType Directory -Path $packed -Force | Out-Null
        $failed = @($need | ForEach-Object -Parallel {
                $gz = Join-Path $using:packed "$($_.Sha256).gz"
                $from = [IO.File]::OpenRead($_.Path)
                try {
                    $to = [IO.File]::Create($gz)
                    try { $zip = [IO.Compression.GZipStream]::new($to, [IO.Compression.CompressionLevel]::SmallestSize); try { $from.CopyTo($zip) } finally { $zip.Dispose() } } finally { $to.Dispose() }
                } finally { $from.Dispose() }
                $arguments = @('r2', 'object', 'put', "$using:Bucket/$using:App/files/$($_.Sha256).gz", '--file', $gz, '--content-type', 'application/gzip', '--remote')
                # Cloudflare's API answers a 502 now and then (two of 370 on Cinema 1.5.0's run): an upload is tried three
                # times, a few seconds apart, before it counts as failed. The same bytes to the same key: safe to repeat.
                foreach ($try in 1..3) {
                    if ($using:Wrangler) { $output = & node $using:Wrangler @arguments 2>&1 } else { $output = & npx -y "wrangler@$using:WranglerVersion" @arguments 2>&1 }
                    if ($LASTEXITCODE -eq 0) { break }
                    if ($try -lt 3) { Start-Sleep -Seconds (4 * $try) }
                }
                if ($LASTEXITCODE -ne 0) {
                    $said = (@($output | ForEach-Object { "$_" }) -join ' ') -replace '\s+', ' '
                    "$($_.Sha256): $($said.Substring(0, [Math]::Min(300, $said.Length)))"
                }
            } -ThrottleLimit $UploadsAtOnce)
        if ($failed.Count) { throw "$($failed.Count) upload(s) failed: $($failed[0])" }
        # Each upload as the apps will get it: unpacked, it is exactly the file, by size and SHA-256.
        foreach ($file in $need) {
            $response = Send-Served "$Base/v1/$App/file/$($file.Sha256)"
            try {
                if ([int]$response.StatusCode -ne 200) { throw "$($file.Sha256) answers $([int]$response.StatusCode) after its upload" }
                $unzip = [IO.Compression.GZipStream]::new($response.Content.ReadAsStream(), [IO.Compression.CompressionMode]::Decompress)
                try {
                    $sha = [System.Security.Cryptography.SHA256]::Create()
                    try { $served = [Convert]::ToHexString($sha.ComputeHash($unzip)).ToLowerInvariant() } finally { $sha.Dispose() }
                } finally { $unzip.Dispose() }
                if ($served -ne $file.Sha256) { throw "$($file.Sha256) is served with other bytes" }
            } finally { $response.Dispose() }
        }
        Write-Host "  [PASS] $($need.Count) uploaded file(s) unpack to their SHA-256"
        # The list, last. Under a tag it is never replaced, as the tag's other files are not.
        $there = Join-Path $work 'there-payload-manifest.json'
        $listHash = Get-FileSha256 $listFile
        if (Get-Object "$App/$Tag/payload-manifest.json" $there) {
            if ((Get-FileSha256 $there) -ne $listHash) { throw "$Tag already has another file list; it is not replaced" }
        } else {
            Publish-Object "$App/$Tag/payload-manifest.json" $listFile 'application/json'
        }
        $listServed = Get-ServedSha256 "$Base/v1/$App/payload/$Tag"
        if ($listServed.Status -ne 200 -or $listServed.Hash -ne $listHash) {
            # A list that doesn't check out is taken away again, so no app starts a file-level update on it.
            try { Remove-Object "$App/$Tag/payload-manifest.json" -WhatIf:$false } catch { Write-Host "  [WARN] could not remove the file list: $($_.Exception.Message)" -ForegroundColor Yellow }
            throw "the served file list is not the local one (status $($listServed.Status))"
        }
        Write-Host "  [PASS] /v1/$App/payload/$Tag serves the release's file list"
        return $true
    } catch {
        Write-Host "  [WARN] file-level update not published: $($_.Exception.Message). $Tag updates through Setup only." -ForegroundColor Yellow
        return $false
    }
}

function Invoke-Announce {
    # The Worker posts the card once per tag and only for a tag it serves as newest; -NoAnnounce sends announce:false, which it ignores.
    if (-not $NoAnnounce -and $current -eq $Tag) { Step "Release card skipped: $Tag was already live before this run"; return }
    Step $(if ($NoAnnounce) { 'Release card: -NoAnnounce, so the Worker is not asked' } else { 'Release card: exo-updates posts it to Discord' })
    if ($WhatIfPreference) { Write-Host "  WHATIF would POST $Base/v1/$App/announce for $Tag (nothing posted)" -ForegroundColor Yellow; return }
    $body = @{ tag = $Tag; announce = [bool](-not $NoAnnounce) } | ConvertTo-Json -Compress
    try {
        $reply = Invoke-RestMethod -Method Post -Uri "$Base/v1/$App/announce" -ContentType 'application/json' -Body $body -TimeoutSec 60
        Write-Host "  release card: $($reply.status)"
    } catch {
        $detail = if ($_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        Write-Host "  [WARN] the release card was not posted ($detail); the release is live." -ForegroundColor Yellow
    }
}

function Send-Get([string]$url) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $url)
    try {
        return $script:Http.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    } finally {
        $request.Dispose()
    }
}

# A GET that retries (the mirror and the edge take a moment to show an upload) until it is 200 or the tries run out.
function Send-Served([string]$url) {
    for ($try = 1; ; $try++) {
        $response = Send-Get $url
        if ([int]$response.StatusCode -eq 200 -or $try -ge $script:Tries) { return $response }
        $response.Dispose()
        Start-Sleep -Seconds $script:RetrySeconds
    }
}

function Get-ServedText([string]$url) {
    $response = Send-Served $url
    try {
        $status = [int]$response.StatusCode
        $text = ''
        if ($status -eq 200) { $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
        return [pscustomobject]@{ Status = $status; Location = [string]$response.Headers.Location; Text = $text }
    } finally {
        $response.Dispose()
    }
}

# Streams the served file through SHA-256 (no copy kept in memory).
function Get-ServedSha256([string]$url) {
    $response = Send-Served $url
    try {
        $status = [int]$response.StatusCode
        $hash = ''
        $bytes = 0L
        if ($status -eq 200) {
            $sha = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
            $stream = $response.Content.ReadAsStream()
            $buffer = [byte[]]::new(1MB)
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $sha.AppendData($buffer, 0, $read)
                $bytes += $read
            }
            $hash = [Convert]::ToHexString($sha.GetHashAndReset()).ToLowerInvariant()
            $sha.Dispose()
        }
        return [pscustomobject]@{ Status = $status; Hash = $hash; Bytes = $bytes; Location = [string]$response.Headers.Location }
    } finally {
        $response.Dispose()
    }
}

# The tag /v1/<app>/latest names, or '' when it can't say.
function Get-LatestTag {
    try {
        $response = Send-Get "$Base/v1/$App/latest"
        try {
            if ([int]$response.StatusCode -ne 200) { return '' }
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $tag = [string](ConvertFrom-Json $text).tag
            if ($tag -match '^v\d{1,4}\.\d{1,4}\.\d{1,4}$') { return $tag }
            return ''
        } finally {
            $response.Dispose()
        }
    } catch {
        return ''
    }
}

function Get-FileSha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

$exitCode = 0
$work = Join-Path ([System.IO.Path]::GetTempPath()) "exo-publish-$App-$Tag-$([guid]::NewGuid().ToString('N'))"
$latestFile = Join-Path $work 'latest.json'
$previousLatest = Join-Path $work 'previous-latest.json'
$latestUploaded = $false
$hadPreviousLatest = $false
try {
    New-Item -ItemType Directory -Path $work -Force -WhatIf:$false | Out-Null
    if ($OlderTag.Count -and -not $PruneOlder) { throw '-OlderTag deletes nothing without -PruneOlder.' }
    if ($PruneOlder) {
        if (-not $OlderTag.Count) { throw '-PruneOlder needs -OlderTag: the older tags to delete.' }
        foreach ($old in $OlderTag) {
            if ([version]$old.Substring(1) -ge [version]$Version) { throw "$old is not older than $Tag; refusing to delete it." }
        }
    }

    Step "Local files in $Dir"
    if (-not (Test-Path -LiteralPath $Dir -PathType Container)) { throw "-Dir $Dir is not a folder." }
    $installer = Join-Path $Dir $Setup
    $shaFile = "$installer.sha256"
    $notesFile = Join-Path $Dir 'whats-new.json'
    # The app's change list for getexo.app's Changelog pages. Optional: without it the pages keep the last one uploaded.
    $changelogFile = Join-Path $Dir 'CHANGELOG.md'
    $hasChangelog = Test-Path -LiteralPath $changelogFile -PathType Leaf
    $missing =@($installer, $shaFile, $notesFile) | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }
    $missingDetail = if ($missing) { 'missing: ' + (($missing | ForEach-Object { Split-Path $_ -Leaf }) -join ', ') } else { '' }
    Check "three files present ($Setup, $Setup.sha256, whats-new.json)" (-not $missing) $missingDetail
    if ($missing) { throw 'release files are missing.' }

    $size = (Get-Item -LiteralPath $installer).Length
    $expected = ((Get-Content -LiteralPath $shaFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = Get-FileSha256 $installer
    Check 'the .sha256 is the installer''s SHA-256' ($expected -match '^[0-9a-f]{64}$' -and $expected -eq $actual) "installer is $size bytes, SHA-256 $actual"

    $notes = $null
    try { $notes = Get-Content -LiteralPath $notesFile -Raw | ConvertFrom-Json } catch { $notes = $null }
    $notesVersion = if ($notes) { [string]$notes.version } else { '' }
    Check "whats-new.json is for $Version" ($notesVersion -eq $Version) "file says '$notesVersion'"
    $payloadFiles = @()
    if ($Payload) {
        Check "-Payload $Payload is a folder" (Test-Path -LiteralPath $Payload -PathType Container)
        if (-not $Failures.Count) { $payloadFiles = @(Read-Payload) }
    }
    if ($Failures.Count) { throw 'local checks failed.' }

    Step "Newest version $Base serves now (read only)"
    $current = Get-LatestTag
    Check "$Tag is not older than the one served now" ($current -eq '' -or [version]$current.Substring(1) -le [version]$Version) "serves '$current'"
    if ($Failures.Count) { throw "$Tag would roll back $App's update address." }

    # A tag's files never change: the bytes the apps were offered under it are the bytes they get, and the edge's copy (an hour)
    # never disagrees with the bucket. The same bytes again are left as they are.
    Step "Files already under $Bucket/$App/$Tag/ (read only)"
    $tagFiles = [ordered]@{ $Setup = $installer; "$Setup.sha256" = $shaFile; 'whats-new.json' = $notesFile }
    $toUpload = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $tagFiles.GetEnumerator()) {
        $there = Join-Path $work ('there-' + $entry.Key)
        if (-not (Get-Object "$App/$Tag/$($entry.Key)" $there)) { $toUpload.Add($entry.Key); Write-Host "  $($entry.Key): not there yet"; continue }
        $same = (Get-FileSha256 $there) -eq (Get-FileSha256 $entry.Value)
        Check "$($entry.Key) under $Tag is the same file, or not there yet" $same 'a published tag''s files are never replaced: publish another tag'
        if ($same) { Write-Host "  $($entry.Key): already there with the same bytes; not uploaded again" }
    }
    if ($Failures.Count) { throw "$Tag already holds other files; nothing uploaded." }

    # The manifest (schema 2): the tag and each of the three files' name, size and SHA-256, so an app checks its download
    # against what the release says, and the Worker serves the same.
    $notesSize = (Get-Item -LiteralPath $notesFile).Length
    $notesHash = Get-FileSha256 $notesFile
    $shaSize = (Get-Item -LiteralPath $shaFile).Length
    $shaHash = Get-FileSha256 $shaFile
    $manifest = [ordered]@{
        schema = 2
        app = $App
        tag = $Tag
        version = $Version
        files = [ordered]@{
            setup = [ordered]@{ name = $Setup; size = $size; sha256 = $actual }
            sha256 = [ordered]@{ name = "$Setup.sha256"; size = $shaSize; sha256 = $shaHash }
            whatsNew = [ordered]@{ name = 'whats-new.json'; size = $notesSize; sha256 = $notesHash }
        }
    }
    [System.IO.File]::WriteAllText($latestFile, ($manifest | ConvertTo-Json -Depth 5))
    $types = @{ $Setup = 'application/octet-stream'; "$Setup.sha256" = 'text/plain'; 'whats-new.json' = 'application/json' }

    if ($WhatIfPreference) {
        Step 'Plan (WhatIf: nothing uploaded)'
        foreach ($file in $toUpload) { Publish-Object "$App/$Tag/$file" $tagFiles[$file] $types[$file] }
        Write-Host "  then check $Base/v1/$App/download/$Tag/$Setup, .sha256 and whats-new.json: 200 with no redirect, SHA-256 $expected, $size bytes"
        if ($Payload) { Step 'File-level update (WhatIf: read only)'; $null = Publish-Payload $payloadFiles }
        else { Write-Host '  no -Payload: this release updates through Setup only' }
        Publish-Object "$App/latest.json" $latestFile 'application/json'
        Write-Host "  then check $Base/v1/$App/latest names $Tag; on a failure the latest.json from before is put back"
        if ($hasChangelog) { Publish-Object "$App/CHANGELOG.md" $changelogFile 'text/markdown; charset=utf-8' }
        Invoke-Prune
        Invoke-Announce
        Write-Host "`nWHATIF PASS: $Name $Tag local checks pass; nothing uploaded or deleted." -ForegroundColor Green
    } else {
        Step "1. Upload $Tag's files to R2 bucket $Bucket"
        foreach ($file in $toUpload) { Publish-Object "$App/$Tag/$file" $tagFiles[$file] $types[$file] }

        Step "2. Check $Tag's files through $Base, before any app is offered them"
        $installerServed = Get-ServedSha256 "$Base/v1/$App/download/$Tag/$Setup"
        Check "$Setup answers 200 with no redirect" ($installerServed.Status -eq 200 -and -not $installerServed.Location) "status $($installerServed.Status)"
        Check 'served installer SHA-256 matches the local one' ($installerServed.Hash -eq $expected) "served $($installerServed.Bytes) bytes"
        Check 'served installer size matches the local one' ($installerServed.Bytes -eq $size)

        $shaServed = Get-ServedText "$Base/v1/$App/download/$Tag/$Setup.sha256"
        Check "$Setup.sha256 answers 200 with no redirect" ($shaServed.Status -eq 200 -and -not $shaServed.Location) "status $($shaServed.Status)"
        Check 'served .sha256 names the same hash' ((($shaServed.Text.Trim() -split '\s+')[0]) -eq $expected)

        $notesServed = Get-ServedText "$Base/v1/$App/download/$Tag/whats-new.json"
        Check 'whats-new.json answers 200 with no redirect' ($notesServed.Status -eq 200 -and -not $notesServed.Location) "status $($notesServed.Status)"
        $servedNotesVersion = ''
        try { $servedNotesVersion = [string](ConvertFrom-Json $notesServed.Text).version } catch { $servedNotesVersion = '' }
        Check "served whats-new.json is for $Version" ($servedNotesVersion -eq $Version) "serves '$servedNotesVersion'"
        if ($Failures.Count) { throw "not live: $App/latest.json was not touched, nothing pruned: $($Failures -join '; ')." }

        $fileLevel = $false
        if ($Payload) {
            Step "2b. File-level update: $Tag's changed files by SHA-256, then its file list (before any app is offered $Tag)"
            $fileLevel = Publish-Payload $payloadFiles
        }

        Step "3. Point $App/latest.json at $Tag, then check $Base/v1/$App/latest"
        $hadPreviousLatest = Get-Object "$App/latest.json" $previousLatest
        # Read again from the bucket itself (the address's answer is a minute old at the edge), right before the write: a
        # newer tag published meanwhile (another run, by hand or by the GitHub workflow) is never pointed back at this one.
        if ($hadPreviousLatest) {
            $bucketTag = ''
            try { $bucketTag = [string](Get-Content -LiteralPath $previousLatest -Raw | ConvertFrom-Json).tag } catch { $bucketTag = '' }
            if ($bucketTag -match '^v\d{1,4}\.\d{1,4}\.\d{1,4}$' -and [version]$bucketTag.Substring(1) -gt [version]$Version) {
                throw "$Bucket/$App/latest.json already names $bucketTag, newer than $Tag (published during this run); latest.json was not touched."
            }
        }
        $latestUploaded = $true
        Publish-Object "$App/latest.json" $latestFile 'application/json'
        $servedTag = ''
        for ($try = 1; $try -le $Tries; $try++) {
            $servedTag = Get-LatestTag
            if ($servedTag -eq $Tag) { break }
            if ($try -lt $Tries) { Start-Sleep -Seconds $RetrySeconds }
        }
        Check "/v1/$App/latest names $Tag" ($servedTag -eq $Tag) "serves '$servedTag'"
        $servedChecks = ''
        try { $servedChecks = (Send-Get "$Base/v1/$App/latest").Content.ReadAsStringAsync().GetAwaiter().GetResult() } catch { $servedChecks = '' }
        $servedManifest = $null
        try { $servedManifest = ConvertFrom-Json $servedChecks } catch { $servedManifest = $null }
        $checksOk = $servedManifest -and $servedManifest.checks -and
            $servedManifest.checks.setup.sha256 -eq $expected -and $servedManifest.checks.setup.size -eq $size -and
            $servedManifest.checks.whatsNew.sha256 -eq $notesHash -and $servedManifest.checks.sha256.sha256 -eq $shaHash
        if ($SkipManifestChecks) { Write-Host '  [SKIP] manifest checks (-SkipManifestChecks: verified after the Worker deploy)' }
        else { Check '/v1/<app>/latest carries the SHA-256 and size of all three files' ([bool]$checksOk) 'manifest checks match the local files' }
        if ($Failures.Count) { throw "the newest-version answer did not check out: $($Failures -join '; ')." }
        $latestUploaded = $false

        if ($hasChangelog) {
            Step "4. The change list"
            Publish-Object "$App/CHANGELOG.md" $changelogFile 'text/markdown; charset=utf-8'
            $changelogServed = Get-ServedSha256 "$Base/v1/$App/changelog"
            $changelogHash = Get-FileSha256 $changelogFile
            Check "/v1/$App/changelog answers 200 with the local CHANGELOG.md" ($changelogServed.Status -eq 200 -and $changelogServed.Hash -eq $changelogHash) "status $($changelogServed.Status), SHA-256 $($changelogServed.Hash)"
            if ($Failures.Count) { throw "the release is live, but the change list did not check out: $($Failures -join '; ')." }
        }

        Invoke-Prune
        Write-Host "`nPASS: $Name $Tag is live on $Base (latest names $Tag; three files 200 with no redirect; installer SHA-256 $expected)." -ForegroundColor Green
        if ($Payload) { Write-Host "File-level update: $(if ($fileLevel) { 'published; installed copies download only the files that changed' } else { 'NOT published; this version updates through Setup only (see the warning above)' })." -ForegroundColor $(if ($fileLevel) { 'Green' } else { 'Yellow' }) }
        Invoke-Announce
    }
} catch {
    Write-Host "`nFAIL: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
    # latest.json went up but did not check out: the answer from before this run is put back, so no app is offered a release
    # this run could not confirm.
    if ($latestUploaded) {
        if ($hadPreviousLatest) {
            $restored = $false
            for ($try = 1; $try -le 3 -and -not $restored; $try++) {
                try {
                    Publish-Object "$App/latest.json" $previousLatest 'application/json' -WhatIf:$false
                    $restored = $true
                    Write-Host "  $App/latest.json is back to what it was before this run." -ForegroundColor Yellow
                } catch {
                    Write-Host "  [WARN] putting $App/latest.json back failed (try $try of 3): $($_.Exception.Message)" -ForegroundColor Yellow
                    if ($try -lt 3) { Start-Sleep -Seconds $RetrySeconds }
                }
            }
            if (-not $restored) { Write-Host "  [FAIL] $App/latest.json could not be put back after 3 tries. Upload the previous one by hand now." -ForegroundColor Red }
        } else {
            Write-Host "  There was no $App/latest.json before this run; delete $Bucket/$App/latest.json by hand if it should not be live." -ForegroundColor Red
        }
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -WhatIf:$false -ErrorAction SilentlyContinue
    $Http.Dispose()
}
exit $exitCode
