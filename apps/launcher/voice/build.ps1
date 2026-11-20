# Builds the native parts of Exo's Discord voice (voice\out): dave.dll, Discord's libdave (MIT) for DAVE
# end-to-end encryption, with mlspp and OpenSSL 3 linked in; opus.dll (BSD); and exo_apm.dll, WebRTC's echo
# cancellation (BSD). Pinned sources (tag and commit) and pinned vcpkg baselines, built with the Visual Studio C++
# tools and Visual Studio's own vcpkg.
# Every source is read from Exo's cache (updates.getexo.app/v1/cache), pinned by size and SHA-256 (fetch.ps1). No GitHub,
# GitLab, crates.io or PyPI download is made here, except the vcpkg registry and its port recipes (see the notes at vcpkg).
# Each source archive is the upstream commit's tree, written the way a Windows clone writes it, uploaded as
# cache/exo/voice/<repo>/<commit>/source.tar.gz with the upstream licence beside it. DeepFilterNet's archive also carries
# its crates (vendor/); deepfilter\offline-vendor.patch trims its manifests to what libDF builds, and cargo runs --offline.
#   pwsh -File voice\build.ps1 [-Targets dave,opus,apm,dfn]
[CmdletBinding()]
param(
    [string[]]$Targets = @('dave', 'opus', 'apm', 'dfn')
)
$ErrorActionPreference = 'Stop'
$Targets = @($Targets | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$here = $PSScriptRoot
. (Join-Path $here 'fetch.ps1')
$src = Join-Path $here 'third_party'
$downloads = Join-Path $here 'build\downloads'
$out = Join-Path $here 'out'
New-Item -ItemType Directory -Force -Path $src, $out | Out-Null

$needMsvc = @('dave', 'opus', 'apm') | Where-Object { $Targets -contains $_ }
if ($needMsvc) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'Visual Studio C++ tools (x64) are not installed.' }
    $devCmd = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
    # Visual Studio's vcpkg in manifest mode fetches the ports at each manifest's pinned baseline
    # (libdave's own vcpkg submodule predates Visual Studio 18 and cannot find it).
    $vcpkg = Join-Path $vs 'VC\vcpkg'
    $env:VCPKG_ROOT = $vcpkg
    function Invoke-Dev([string]$command) {
        cmd /c "`"$devCmd`" -arch=x64 -host_arch=x64 -no_logo && $command"
        if ($LASTEXITCODE) { throw "Failed ($LASTEXITCODE): $command" }
    }
}

if ($Targets -contains 'dave') {
    # libdave v1.2.2/cpp (commit 6396c90a). Exo's one change to libdave (dave-exporter.patch): daveSessionExportSecret, MLS's
    # exporter (RFC 9420 8.5), which keys the call relay's messages to the call's own group, and its test in libdave's suite.
    $dave = Join-Path $src 'libdave'
    $daveArchive = Get-SourcePin 'exo/voice/libdave/6396c90aedbb9a6ee117ea9ce1ce9832af6a3375/source.tar.gz' 133044 `
        '77ffc3c41e78697b275adabb83c9433ddbe7d09b46ffc6c27de847d4449f312d' (Join-Path $downloads 'libdave-source.tar.gz')
    Initialize-Source $daveArchive '77ffc3c41e78697b275adabb83c9433ddbe7d09b46ffc6c27de847d4449f312d' $dave @((Join-Path $here 'dave-exporter.patch'))
    $cpp = Join-Path $dave 'cpp'

    # libdave's dependencies (OpenSSL 3, mlspp from its overlay port), from its own manifest, except OpenSSL: libdave pins
    # 3.0.7; Exo's overlay port (voice\ports\openssl) builds the newest 3.5 LTS, pinned there by commit and SHA-512 and read
    # from Exo's cache.
    # Not yet off GitHub: vcpkg itself. Its baseline registry is a git clone of github.com/microsoft/vcpkg, and the port
    # recipes it builds (vcpkg-cmake, vcpkg-cmake-config, vcpkg-cmake-get-vars, nlohmann-json, gtest) plus mlspp (libdave's
    # overlay port, vcpkg_from_github in cpp\vcpkg-alts) download their sources from GitHub. Plan: mirror the registry at the
    # baseline as a tar.gz the build unpacks, and point vcpkg's asset cache (X_VCPKG_ASSET_SOURCES) at Exo's cache for each
    # port's SHA-512 pinned archive; mlspp needs a patch to its overlay portfile in libdave's tree.
    $openssl = '3.5.9'
    $deps = Join-Path $here 'build\dave-deps'
    Invoke-Dev "`"$vcpkg\vcpkg.exe`" install --x-manifest-root=`"$cpp\vcpkg-alts\openssl_3`" --overlay-ports=`"$here\ports`" --x-install-root=`"$deps`" --triplet x64-windows-static --disable-metrics"
    if (-not (Select-String -Quiet -SimpleMatch "# define OPENSSL_VERSION_STR `"$openssl`"" (Join-Path $deps 'x64-windows-static\include\openssl\opensslv.h'))) {
        throw "OpenSSL in $deps is not $openssl."
    }

    $daveBuild = Join-Path $here 'build\dave'
    Invoke-Dev ("cmake -S `"$cpp`" -B `"$daveBuild`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DTESTING=OFF " +
        "-DCMAKE_TOOLCHAIN_FILE=`"$vcpkg\scripts\buildsystems\vcpkg.cmake`" -DVCPKG_MANIFEST_MODE=OFF -DVCPKG_INSTALLED_DIR=`"$deps`" " +
        "-DVCPKG_TARGET_TRIPLET=x64-windows-static -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded")
    Invoke-Dev "cmake --build `"$daveBuild`" --target libdave"
    Get-ChildItem $daveBuild -Recurse -Filter '*.dll' | Where-Object Name -match 'dave' | Select-Object -First 1 | Copy-Item -Destination (Join-Path $out 'dave.dll') -Force
    Copy-Item (Join-Path $dave 'LICENSE') (Join-Path $out 'LICENSE-libdave.txt') -Force
}

if ($Targets -contains 'opus') {
    # Opus v1.6.1 (commit 22244de5), as the classic codec (no DRED, OSCE or BWE: those need model files from outside the tag), with
    # the static C runtime linked in like dave.dll and exo_apm.dll, so it needs no Visual C++ runtime on the PC.
    # Exo's one fix (opus-downmix.patch): 1.6 saturates the sum of the two channels before halving it, so a voice on both
    # channels coded as one (Exo's call encoder) was clipped at -6 dBFS in SILK's band; 1.5 summed the channels unclipped.
    $opusSrc = Join-Path $src 'opus'
    $opusArchive = Get-SourcePin 'exo/voice/opus/22244de5a79bd1d6d623c32e72bf1954b56235be/source.tar.gz' 4764964 `
        '57ccf72fd7e9d55bedf2c7c6e858fc9c341a4321cf3f463e9e2d3d90743ac3f1' (Join-Path $downloads 'opus-source.tar.gz')
    Initialize-Source $opusArchive '57ccf72fd7e9d55bedf2c7c6e858fc9c341a4321cf3f463e9e2d3d90743ac3f1' $opusSrc @((Join-Path $here 'opus-downmix.patch'))
    $opusBuild = Join-Path $here 'build\opus'
    Invoke-Dev "cmake -S `"$opusSrc`" -B `"$opusBuild`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DOPUS_STATIC_RUNTIME=ON"
    Invoke-Dev "cmake --build `"$opusBuild`" --target opus"
    Copy-Item (Join-Path $opusBuild 'opus.dll') $out -Force
    Copy-Item (Join-Path $opusSrc 'COPYING') (Join-Path $out 'LICENSE-opus.txt') -Force
}

if ($Targets -contains 'apm') {
    # Echo cancellation: exo_apm.dll, WebRTC's AEC3 from webrtc-audio-processing 2.1 (846fe90a, BSD-3-Clause), fetched by meson
    # from Exo's cache through apm\subprojects\webrtc-audio-processing.wrap (wrap-file, SHA-256 pinned), behind Exo's small C
    # interface (voice\apm\exo_apm.cc), with Abseil and the static C runtime linked in. Meson comes from a pinned wheel of its
    # own venv; WebRTC's code needs C++20 under MSVC.
    $venv = Join-Path $here 'build\meson-venv'
    $meson = Join-Path $venv 'Scripts\meson.exe'
    if (-not (Test-Path $meson)) {
        python -m venv $venv
        $wheel = Get-SourcePin 'files.pythonhosted.org/packages/9c/07/b48592d325cb86682829f05216e4efb2dc881762b8f1bafb48b57442307a/meson-1.9.1-py3-none-any.whl' 1030356 `
            'f824ab770c041a202f532f69e114c971918ed2daff7ea56583d80642564598d0' (Join-Path $downloads 'meson-1.9.1-py3-none-any.whl')
        & (Join-Path $venv 'Scripts\python.exe') -m pip install -q --no-index --no-deps $wheel
        if ($LASTEXITCODE) { throw 'Meson did not install.' }
    }
    $apmBuild = Join-Path $here 'build\apm'
    if (-not (Test-Path (Join-Path $apmBuild 'build.ninja'))) {
        Invoke-Dev "`"$meson`" setup `"$apmBuild`" `"$here\apm`" --backend=ninja -Dcpp_std=c++20 -Dwebrtc-audio-processing:cpp_std=c++20 -Dabseil-cpp:cpp_std=c++20"
    }
    Invoke-Dev "ninja -C `"$apmBuild`" exo_apm.dll"
    Copy-Item (Join-Path $apmBuild 'exo_apm.dll') $out -Force
    $wap = Join-Path $here 'apm\subprojects\webrtc-audio-processing'
    $apmLicenses = 'COPYING', 'webrtc\common_audio\third_party\ooura\LICENSE', 'webrtc\common_audio\third_party\spl_sqrt_floor\LICENSE',
        'webrtc\modules\third_party\fft\LICENSE', 'webrtc\third_party\pffft\LICENSE', 'webrtc\third_party\rnnoise\COPYING'
    Set-Content (Join-Path $out 'LICENSE-webrtc-audio-processing.txt') (($apmLicenses | ForEach-Object { "== $_`n" + (Get-Content -Raw (Join-Path $wap $_)) }) -join "`n")
    Copy-Item (Join-Path $here 'apm\subprojects\abseil-cpp-20240722.0\LICENSE') (Join-Path $out 'LICENSE-abseil.txt') -Force
}

if ($Targets -contains 'dfn') {
    # DeepFilterNet v0.5.6 (commit 978576aa) noise suppression (MIT/Apache-2.0): Exo's own build of the project's C interface
    # (libDF, low-latency model built in) with Exo's three entry points appended (deepfilter\exo-capi.rs), so a frame is cleaned on the
    # caller's thread with nothing to poll. It ships inside the app (voice\out\exo-deepfilter.dll, copied beside
    # ExoLauncher.exe); voice\build\dfn keeps the same file for the tests. Needs Rust (rustup, per user, MSVC toolchain); its
    # crates come from the archive's vendor\ folder, so the build runs cargo --offline.
    $dfn = Join-Path $here 'build\dfn\exo-deepfilter.dll'
    New-Item -ItemType Directory -Force -Path (Split-Path $dfn) | Out-Null
    $dfnSrc = Join-Path $here 'build\dfn-src'
    if (-not (Test-Path $dfn)) {
        $dfnArchive = Get-SourcePin 'exo/voice/DeepFilterNet/978576aa8400552a4ce9730838c635aa30db5e61/source.tar.gz' 131932063 `
            'c8331e97468fa2a4b10e476c453b556c12923353cbaeca6786502f1d9d582e3c' (Join-Path $downloads 'DeepFilterNet-source.tar.gz')
        Expand-SourceArchive $dfnArchive $dfnSrc
        Apply-SourcePatch $dfnSrc (Join-Path $here 'deepfilter\offline-vendor.patch')
        Add-Content (Join-Path $dfnSrc 'libDF\src\capi.rs') (Get-Content -Raw (Join-Path $here 'deepfilter\exo-capi.rs'))
        $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
        if (-not (Test-Path $cargo)) { $cargo = 'cargo' }
        Push-Location $dfnSrc
        try {
            & $cargo rustc --offline --manifest-path libDF\Cargo.toml --release --lib --no-default-features --features capi,default-model-ll --crate-type cdylib
            $code = $LASTEXITCODE
        } finally {
            Pop-Location
        }
        if ($code) { throw "DeepFilterNet build failed ($code)." }
        Copy-Item (Join-Path $dfnSrc 'target\release\df.dll') $dfn -Force
    }
    Copy-Item $dfn (Join-Path $out 'exo-deepfilter.dll') -Force
    if (Test-Path (Join-Path $dfnSrc 'LICENSE-MIT')) {
        Copy-Item (Join-Path $dfnSrc 'LICENSE-MIT') (Join-Path $out 'LICENSE-deepfilternet-mit.txt') -Force
        Copy-Item (Join-Path $dfnSrc 'LICENSE-APACHE') (Join-Path $out 'LICENSE-deepfilternet-apache.txt') -Force
    }
    Write-Output "exo-deepfilter.dll: $((Get-FileHash (Join-Path $out 'exo-deepfilter.dll') -Algorithm SHA256).Hash) ($((Get-Item (Join-Path $out 'exo-deepfilter.dll')).Length) bytes)"
}
