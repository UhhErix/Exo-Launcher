# Checks for voice\fetch.ps1: a download that is not exactly its pinned size and SHA-256 must stop the build.
# Run: pwsh -File voice\fetch.tests.ps1   (exit 1 on any failure)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fetch.ps1')

$dir = Join-Path ([IO.Path]::GetTempPath()) ('exo-voice-fetch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$failures = 0
function Assert-Case([string]$name, [scriptblock]$case, [bool]$shouldThrow, [string]$expect = 'does not match its pin') {
    $threw = $false
    $message = ''
    try { & $case } catch { $threw = $true; $message = $_.Exception.Message }
    if ($threw -ne $shouldThrow) {
        $script:failures++
        Write-Output "FAIL $name (threw: $threw)"
    } elseif ($threw -and $message -notmatch $expect) {
        $script:failures++
        Write-Output "FAIL $name (message: $message)"
    } else {
        Write-Output "ok   $name"
    }
}

try {
    $file = Join-Path $dir 'pinned.bin'
    [IO.File]::WriteAllBytes($file, [Text.Encoding]::ASCII.GetBytes('exo'))
    $sha = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $wrongSha = ('0' * 64)

    Assert-Case 'right size and SHA-256 pass' { Confirm-SourcePin $file 3 $sha } $false
    Assert-Case 'pin in lower case passes' { Confirm-SourcePin $file 3 $sha.ToLowerInvariant() } $false
    Assert-Case 'wrong SHA-256 of the right size stops' { Confirm-SourcePin $file 3 $wrongSha } $true
    Assert-Case 'wrong size with the right SHA-256 stops' { Confirm-SourcePin $file 4 $sha } $true
    Assert-Case 'the message names the cache key when given' {
        try { Confirm-SourcePin $file 3 $wrongSha 'exo/voice/x/source.tar.gz' } catch {
            if ($_.Exception.Message -notmatch '^exo/voice/x/source\.tar\.gz does not match') { throw 'no key in message' }
            throw
        }
    } $true
    Assert-Case 'a truncated download (2 of 3 bytes) stops' {
        $short = Join-Path $dir 'short.bin'
        [IO.File]::WriteAllBytes($short, [Text.Encoding]::ASCII.GetBytes('ex'))
        Confirm-SourcePin $short 3 $sha
    } $true
    # Extraction: one top folder is stripped; a damaged archive stops the build.
    $tree = Join-Path $dir 'tree\top'
    New-Item -ItemType Directory -Path $tree | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $tree 'kept.txt'), [Text.Encoding]::ASCII.GetBytes('kept'))
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    $good = Join-Path $dir 'good.tar.gz'
    & $tar -czf $good -C (Join-Path $dir 'tree') top
    $extracted = Join-Path $dir 'extracted'
    Assert-Case 'an archive extracts with its top folder stripped' {
        Expand-SourceArchive $good $extracted
        if (-not (Test-Path -LiteralPath (Join-Path $extracted 'kept.txt'))) { throw 'top folder not stripped' }
    } $false
    $bad = Join-Path $dir 'bad.tar.gz'
    [IO.File]::WriteAllBytes($bad, [Text.Encoding]::ASCII.GetBytes('not a gzip stream'))
    Assert-Case 'a damaged archive stops the build' {
        Expand-SourceArchive $bad (Join-Path $dir 'x')
    } $true 'Could not extract'
    $ignoredRoot = Join-Path $PSScriptRoot 'third_party'
    $ignoredHadRoot = Test-Path -LiteralPath $ignoredRoot
    $ignoredTree = Join-Path $ignoredRoot ('patch-check-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    Assert-Case 'a patch applies inside an ignored voice folder' {
        try {
            New-Item -ItemType Directory -Force -Path (Join-Path $ignoredTree 'sub') | Out-Null
            Set-Content -LiteralPath (Join-Path $ignoredTree 'sub\hello.txt') -Value 'before' -NoNewline
            $patch = Join-Path $dir 'change.patch'
            Set-Content -LiteralPath $patch -Value @(
                'diff --git a/sub/hello.txt b/sub/hello.txt'
                '--- a/sub/hello.txt'
                '+++ b/sub/hello.txt'
                '@@ -1 +1 @@'
                '-before'
                '+after'
            )
            Apply-SourcePatch $ignoredTree $patch
            if ((Get-Content -LiteralPath (Join-Path $ignoredTree 'sub\hello.txt') -Raw).Trim() -ne 'after') {
                throw 'patch did not apply'
            }
        } finally {
            Remove-Item -LiteralPath $ignoredTree -Recurse -Force -ErrorAction SilentlyContinue
            if (-not $ignoredHadRoot -and (Test-Path -LiteralPath $ignoredRoot) -and -not (Get-ChildItem -LiteralPath $ignoredRoot -Force)) {
                Remove-Item -LiteralPath $ignoredRoot -Force -ErrorAction SilentlyContinue
            }
        }
    } $false
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all fetch checks passed'
