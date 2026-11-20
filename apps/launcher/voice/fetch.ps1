# Downloads for voice\build.ps1. Every outside file is read from Exo's cache, never from its upstream (GitHub, GitLab,
# crates.io, PyPI, wrapdb): updates.getexo.app/v1/cache/<key> is the R2 object cache/<key> (cloud/updates/README.md,
# "Exo's cache"). Each download is checked against its pinned size and SHA-256 before anything reads it.
$ProgressPreference = 'SilentlyContinue'
$VoiceCache = 'https://updates.getexo.app/v1/cache'

# Throws unless $file is exactly $size bytes with SHA-256 $sha256. The caller then uses nothing from it.
function Confirm-SourcePin([string]$file, [long]$size, [string]$sha256, [string]$label = '') {
    $item = Get-Item -LiteralPath $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($item.Length -ne $size -or $hash -ne $sha256) {
        $name = if ($label) { $label } else { $item.Name }
        throw "$name does not match its pin: $($item.Length) bytes, SHA-256 $hash; pinned $size bytes, SHA-256 $sha256. The build stops and the file is not used."
    }
}

# The cache object $key, saved at $dest. A copy already at $dest that matches its pin is kept. A download that does not
# match is deleted, never kept.
function Get-SourcePin([string]$key, [long]$size, [string]$sha256, [string]$dest) {
    if (Test-Path -LiteralPath $dest -PathType Leaf) {
        $item = Get-Item -LiteralPath $dest
        if ($item.Length -eq $size -and (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -eq $sha256) { return $dest }
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    $part = "$dest.part"
    try {
        Invoke-WebRequest -Uri "$VoiceCache/$key" -OutFile $part
        Confirm-SourcePin $part $size $sha256 $key
        Move-Item -LiteralPath $part -Destination $dest -Force
    } finally {
        Remove-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
    }
    return $dest
}

# Replaces $dir with the top folder of the tar.gz $archive (one level stripped). Windows' own tar: a Git for Windows tar on
# PATH reads a drive letter (C:\...) as a remote host and cannot open the archive.
function Expand-SourceArchive([string]$archive, [string]$dir) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    & (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $archive --strip-components=1 -C $dir
    if ($LASTEXITCODE) { throw "Could not extract $archive." }
}

# Applies a unified diff patch inside an extracted source tree. Stopping git directory discovery at the tree's parent
# prevents parent repos (with .gitignore rules for voice/third_party or voice/build) from silently skipping every file.
function Apply-SourcePatch([string]$dir, [string]$patch) {
    $prev = $env:GIT_CEILING_DIRECTORIES
    $env:GIT_CEILING_DIRECTORIES = Split-Path -Parent ([IO.Path]::GetFullPath($dir))
    try {
        git -C $dir apply --ignore-whitespace $patch
        if ($LASTEXITCODE) { throw "$(Split-Path -Leaf $patch) does not apply to $dir." }
    } finally {
        if ($null -eq $prev) { Remove-Item Env:GIT_CEILING_DIRECTORIES -ErrorAction SilentlyContinue }
        else { $env:GIT_CEILING_DIRECTORIES = $prev }
    }
}

# Extracts a verified archive into $dir, then applies Exo's patches in order. It runs again only when the archive or a
# patch changed (marker .exo-pin in $dir), so an unchanged source keeps its timestamps and the build reuses its output.
function Initialize-Source([string]$archive, [string]$sha256, [string]$dir, [string[]]$patches) {
    $stamp = (@($sha256) + @($patches | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })) -join ' '
    $marker = Join-Path $dir '.exo-pin'
    if ((Test-Path -LiteralPath $marker) -and ((Get-Content -LiteralPath $marker -Raw).Trim() -eq $stamp)) { return }
    Expand-SourceArchive $archive $dir
    foreach ($patch in $patches) {
        Apply-SourcePatch $dir $patch
    }
    Set-Content -LiteralPath $marker -Value $stamp -NoNewline
}
