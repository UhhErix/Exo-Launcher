# Exo Launcher — Dependency Inventory

**Audit date:** 2026-11-24 (updated for `feat/native-polish-2`)  
**Target base branch:** `feat/native-polish-2` (PRs rebased across `ffa6acab` → `10fc19ab`)  
**3-day supply-chain freshness cutoff:** `2026-11-21T14:15Z` (any release published after this timestamp is flagged `[<3d]`)

## Direction Legend (aligned with `research/deps-best-or-own:improve/deps-best-or-own.md`)

Every dependency row carries a `Direction` tag:
- **`KEEP`**: Best-in-class, OS/platform standard, or already tuned for Exo v4 — continue tracking stable releases.
- **`REPLACE-WITH-BETTER`**: Keep the category, swap to a faster, smaller, or cleaner commercial-friendly library/runtime (e.g., STJ source-gen, `sonic-rs`, `BLAKE3`/`XXH3-128`, BoringSSL/AWS-LC evaluation for `dave.dll`, Node 26 LTS / .NET 11 when hardware-validated).
- **`BUILD-OWN`**: Replace with an Exo-owned native Rust/C# component (several already landed on `feat/native-polish-2`: `exo-voice-ml`, `exo-wire`, `exo-delta` + `exo-installer`, `exo-audio-apo`, `exo-stores`, headless `ExoLauncher.Host`; remaining roadmap: `exo-dx12`, `exo-artcache`, `exo-tune`, `exo-enc`, `exo-shaders-native`).

## Supply-Chain & License Summary

- **Stable releases only:** Every update targets a stable release with lockfiles preserved. (Exception: `polish-2`'s pre-existing `@typescript/native` dev pin `7.1.0-dev.20261121.1` was left untouched.)
- **Maintainer changes:** None detected across npm, NuGet, crates.io, or GitHub releases checked.
- **License changes to GPL/AGPL or non-commercial:** **None.** Existing copyleft components remain isolated in their own processes/origins per `apps/launcher/AGENTS.md` and `apps/launcher/THIRD_PARTY_NOTICES.md`:
  - `Exo Mouse` (`@openmouse/protocol`, AGPL-3.0-or-later) — isolated iframe origin (`mouse.exo-launcher.local`)
  - `Exo Shaders` (`SCSKiller`, GPL-3.0-or-later + section 7 exception) — separate process (`ExoShaders.Service.exe`)
  - `OBS Studio game capture files` (GPL-2.0-or-later) — separate binaries in `addons/capture/obs/`
  - `Exo Shield` (`uBlock Origin Lite`, GPL-3.0) & `SponsorBlock` (GPL-3.0) — browser extensions in music WebView
  - `RyzenSMU` (`PawnIO.Modules`, LGPL-2.1) — separate signed module loaded by user-installed PawnIO driver
  - `OptiScaler` (GPL-3.0), `Legendary` / `gogdl` / `Nile` (GPL-3.0), `Equalizer APO` (GPL-2.0) — downloaded on demand, never linked into Exo Launcher
- **Freshness flags (`[<3d]`, published after 2026-11-21T14:15Z):**
  - npm: `@iconify-json/lucide` `1.2.159` (2026-11-24), `@interfere/landmines` `0.3.1` (2026-11-24), `@types/node` `26.6.7` (2026-11-24), `oxlint` & `eslint-plugin-oxlint` `1.95.0` (2026-11-23), `knip` `6.48.0` (2026-11-22), `typescript-eslint` `8.74.0` (2026-11-23), `wrangler` `4.168.0` (2026-11-24), `@cloudflare/vitest-plugin` `1.9.2` (2026-11-24)
  - NuGet: `org.k2fsa.sherpa.onnx.runtime.win-x64` `2.0.3` (2026-11-22), `Microsoft.GameInput` `4.1.315` (2026-11-23)
  - GitHub / native: `obsproject/obs-studio` `33.0.3` (2026-11-22), `BlueHeisenberg/SCSKiller` `v1.4.9` (2026-11-21T18:36Z — held back), `dashdogy/MFG-Unlocker` `v1.7.2` (2026-11-22), `uBlockOrigin/uBOL-home` `2026.1122.1948` (2026-11-22 — held back), `simple-icons` `16.38.1` (2026-11-22 — held back)

## Security / CVE Audit for v3.30.3 (No `main` PR Opened — Coordinator Decision)

1. **Bumped npm, NuGet, and crates.io packages across PRs #155, #158, #162, #195, #198:** **0 CVEs** reported in OSV.dev against the versions shipped in v3.30.3 (`Microsoft.Web.WebView2 1.0.4314.35`, `Microsoft.Data.Sqlite 10.0.15`, `Microsoft.ML.OnnxRuntime 1.31.1`, `org.k2fsa.sherpa.onnx.runtime.win-x64 2.0.2`, `Microsoft.Windows.SDK.BuildTools.MSIX 1.7.261102100`, `Microsoft.Bcl.Memory 10.0.12`, `ValvePak 6.0.0.182`, `Tmds.DBus.Protocol 0.94.1`, `Velopack 1.2.158`, `Microsoft.GameInput 4.0.306`, `ahash 0.8.12`, `crc32fast 1.5.5`, `litrs 1.0.1`, `zerocopy 0.8.64`, `zlib-rs 0.6.8`, `wrangler 4.165.0`, `knip 6.46.1`, `typescript-eslint 8.73.0`, `@iconify-json/lucide 1.2.157`, `@cloudflare/vitest-plugin 1.8.5`, `oxlint 1.94.0`). Note: `WebView2 Runtime` itself is updated by Windows Edge Update independently of the SDK wrapper.
2. **OBS Studio `32.2.2` → `33.0.3` (PR #198):** OBS 33.0 updated CEF 127 → 150 fixing **`CVE-2024-7971`** (High, V8 type confusion in OBS browser source). **Does NOT affect Exo Launcher v3.30.3** — Exo Capture only bundles the 6 native `win-capture` hook binaries (`graphics-hook{32,64}.dll`, `inject-helper{32,64}.exe`, `get-graphics-offsets{32,64}.exe`), never OBS's CEF/browser source.
3. **OpenSSL `3.5.9` (statically linked into `dave.dll`, NOT bumped — upstream `3.5.10` not yet released):**
   - Verified directly against `https://openssl-library.org/source/` and `github.com/openssl/openssl`: **`openssl-3.5.9` (2026-09-29) is still the latest published 3.5 LTS tarball and tag**; `openssl-3.5.10` is **not released yet** (and `updates.getexo.app/v1/cache/openssl-3.5.10.tar.gz` returns `404`).
   - OpenSSL published **12 rolling security advisories** (Nov 10–24, 2026) as commits on the `openssl-3.5` branch slated for upcoming `3.5.10` (all rated **Low** by OpenSSL; NVD CVSS scores `CVE-2026-95691` ~8.1 High for QUIC clients):
     - QUIC (5): `CVE-2026-95691` (Low / NVD ~8.1 High, QUIC CRYPTO frame UAF), `CVE-2026-84785` (Low, QUIC stream reassembly DoS), `CVE-2026-107494` (Low, QUIC RETIRE_CONNECTION_ID queue), `CVE-2026-124709` (Low, QUIC server rejected streams), `CVE-2026-88822` (Low, QUIC uninitialized stack bytes)
     - AES-GCM-SIV (2): `CVE-2026-95692` (Low, OOB read & IV length handling), `CVE-2026-88823` (Low, AAD forgery after 0-len incremental AAD)
     - X.509 / PEM / TLS 1.3 / CMS (5): `CVE-2026-107493` (Low, UAF in `PEM_X509_INFO_read_bio_ex()`), `CVE-2026-121482` (Low, relative CRLDP rebuild), `CVE-2026-84786` (Low, NULL deref in non-SMTP DANE), `CVE-2026-35190` (Low, OOB read in TLS 1.3 Suite B key-share), `CVE-2026-107496` (Low, infinite loop in `CMS_encrypt()`)
   - **Impact on v3.30.3 (`dave.dll`):** **Not reachable.** `libdave` / `mlspp` uses OpenSSL strictly for MLS cipher suite `MLS_128_DHKEMP256_AES128GCM_SHA256_P256` (P-256 ECDH/ECDSA, SHA-256, HKDF, AES-128-GCM) — never QUIC, AES-GCM-SIV, X.509 CRL/PEM/DANE, TLS 1.3 Suite B, or CMS. Recommendation: bump `voice/ports/openssl` to `3.5.10` once upstream tags and Exo uploads the tarball to R2, no emergency 3.30.3 hotfix required.

## Before / After Measurements (This PC, `feat/native-polish-2`)

| Metric | Before (`ebf0e04f` polish-2 tip) | After (`ebf0e04f` + PR #158) | Delta | Notes |
| --- | --- | --- | --- | --- |
| App memory — `EXO_UI=web` total (3-run median, 3m idle, 7 procs) | `533.5 MB` WS / `294.2 MB` Private | `530.3 MB` WS / `295.4 MB` Private | `-3.2 MB` WS / `+1.2 MB` Priv | Mean: `537.9/297.5` → `533.0/295.0 MB` (`-4.9/-2.5 MB`). `exo` median `220.1/143.4` → `223.3/145.7 MB`; `webview` `313.4/150.8` → `311.4/149.9 MB`. |
| App memory — `EXO_UI=native` total (3-run median, 3m idle, 2 procs) | `387.5 MB` WS / `379.3 MB` Private | `388.7 MB` WS / `381.9 MB` Private | `+1.2 MB` WS / `+2.6 MB` Priv | `exo` `214.1/128.1` → `232.4/146.7 MB`, `exo-ui` `173.2/251.2` → `157.8/235.5 MB`; 3-round native bisect (`A` vs `WV2` vs `SQL` vs `B`) confirmed Gen2 GC variance (`exo` medians: `A 253.2/175.6`, `WV2 251.6/167.7`, `SQL 227.5/147.6`, `B 236.8/156.2 MB`). |
| Startup — `EXO_UI=web` `webview-core-ready` / `navigation-complete` (3-run median) | `399 ms` / `468 ms` | `366 ms` / `447 ms` | `-33 ms` / `-21 ms` | Isolated Release publish (`-Fresh`, `test-instance.ps1`, 3 interleaved pairs) |
| `ExoLauncher` Release publish (PR #158 with ORT held at `1.31.1`) | `259,892,673 B` (`247.85 MB`) | `259,905,932 B` (`247.87 MB`) | `+13,259 B` (`+12.9 KB`) | Holding ORT `1.32.0` avoids `+3,348,440 B` (`+3.19 MB` raw / `+0.85 MB` gzip, 0 CVEs). |
| `ExoSetup.exe` (`setup/build.ps1`) | `1,084,928 B` | `1,086,464 B` | `+1,536 B` | Links `WebView2LoaderStatic.lib 1.0.4370.26` |
| `OBS` win-capture 6 files (`capture/third_party/obs/`) | `1,079,056 B` (`32.2.2`) | `1,079,568 B` (`33.0.3`) | `+512 B` | All 6 Authenticode-verified (`OBS Project, LLC`, merged in #198) |
| `exo-ui.exe` release binary (`native-ui`, apples-to-apples on `071f1052`) | `35,031,552 B` (`33.41 MB`) | `35,036,160 B` (`33.41 MB`) | `+4,608 B` (`+0.013%`) | `cargo build --release --locked --bin exo-ui` (Rune pin untouched, merged in #162) |

### Pull Requests Retargeted to `feat/native-polish-2`

- **PR #155** (`chore/deps-npm`, merged `b3a1520c`): Full dependency inventory (`improve/deps-inventory.md`) + JS/TS updates
- **PR #158** (`chore/deps-dotnet`): `.NET` / NuGet host (`ExoLauncher`, `ExoLauncher.Host`), `setup/third_party/wv2` WebView2 `1.0.4370.26`, `Microsoft.Data.Sqlite 11.0.0`, `Microsoft.Windows.SDK.BuildTools.MSIX 1.7.261119100`, `sherpa-onnx 2.0.3`, `ExoCapture.Tests` (ONNX Runtime held at `1.31.1` due to `+3.19 MB` DLL size regression)
- **PR #162** (`chore/deps-native-ui`, merged `f0c42c2d`): `native-ui/Cargo.lock` updates; Rune pin untouched
- **PR #195** (`chore/deps-shaders`, merged `be13fb90`): Exo Shaders NuGet updates + `shaders/global.json`
- **PR #198** (`chore/deps-native-pins`, merged `ebf0e04f`): OBS capture hook `33.0.3`, `Microsoft.GameInput 4.1.315`, `OptiScaler v0.9.5`, `MFG-Unlocker v1.7.2`

---

## 1. Toolchains & GitHub Actions

| Component | Manifest | Current | Latest Stable | Behind | Published | License | Direction | Breaking / Notes | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| .NET SDK (launcher) | `apps/launcher/global.json` | `10.0.403` | `11.0.100` (10.x: `10.0.404`) | 1 major | 2026-11-10 | MIT | **REPLACE-WITH-BETTER** | `.NET 11` R2R targets x86-64-v3 (AVX2); requires Win11 build 22000 hardware validation (`docs/RUNTIME_RELEASE_READINESS.md`); move to NativeAOT once headless `ExoLauncher.Host` graduates. | **Held back** (per `RUNTIME_RELEASE_READINESS.md`) |
| .NET SDK (shaders) | `apps/launcher/shaders/global.json` | `10.0.402` | `11.0.100` (10.x: `10.0.404`) | 1 major | 2026-11-10 | MIT | **BUILD-OWN** | Align floor with launcher (`10.0.403`); long-term replaced by clean-room Rust `exo-shaders`. | **Updated** (`10.0.403`, PR #195) |
| Node.js (CI) | `.github/workflows/launcher-ci.yml` | `24` (`>=24.19.0`) | `26.13.1` LTS (24.x: `24.22.3`) | 1 LTS major | 2026-11-10 | MIT | **KEEP** | Node 24 LTS active through Apr 2028; all JS/TS tests verified on Node `v26.13.0` locally. | **Held / Optional** (matches `ui/package.json` `engines`) |
| Rust toolchain | Local / CI | `1.100.0` | `1.100.0` | 0 | 2026-11-12 | MIT OR Apache-2.0 | **KEEP** | Current. | **Current** |
| `actions/checkout` | `.github/workflows/*.yml` | `v7.0.1` (`3d3c42e5`) | `v7.0.1` | 0 | 2026-07-20 | MIT | **KEEP** | Current. | **Current** |
| `actions/setup-node` | `.github/workflows/launcher-ci.yml` | `v7.1.0` (`949feb24`) | `v7.1.0` | 0 | 2026-10-08 | MIT | **KEEP** | Current. | **Current** |
| `actions/setup-dotnet` | `.github/workflows/launcher-ci.yml` | `v6.1.0` (`0f4a354d`) | `v6.1.0` | 0 | 2026-11-03 | MIT | **KEEP** | Current. | **Current** |
| `actions/cache` | `.github/workflows/*.yml` | `v6.1.0` (`55cc8345`) | `v6.1.0` | 0 | 2026-06-26 | MIT | **KEEP** | Current. | **Current** |

---

## 2. JavaScript / TypeScript (`ui/`, `mouse/`, `tools/exo-agent-kit/`, `cloud/*`, `relay/`)

| Package | Manifest(s) | Current | Latest Stable | Behind | Published | Flags | License | Direction | Breaking / Changelog | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `clsx` | `ui` | `^2.1.1` | `2.1.1` | 0 | 2024-04-23 | — | MIT | **KEEP** | — | **Current** |
| `dashjs` | `ui` | `^5.3.0` | `5.3.0` | 0 | 2026-11-04 | — | BSD-3-Clause | **KEEP** | Web-UI video playback (retired in native Rune UI). | **Current** |
| `hls.js` | `ui` | `1.7.4` | `1.7.4` | 0 | 2026-10-28 | — | Apache-2.0 | **KEEP** | Web-UI HLS playback (retired in native Rune UI). | **Current** |
| `lottie-web` | `ui` | `5.13.0` | `5.13.0` | 0 | 2025-05-21 | — | MIT | **REPLACE-WITH-BETTER** | Uses `eval()` in SVG player; retire with web UI in v4. | **Current** |
| `qrcode-generator` | `ui` | `2.0.4` | `2.0.4` | 0 | 2025-08-07 | — | MIT | **KEEP** | — | **Current** |
| `react` / `react-dom` | `ui`, `mouse` | `19.3.1` | `19.3.1` | 0 | 2026-10-14 | — | MIT | **BUILD-OWN** | Legacy v3 web UI + Exo Mouse; main launcher UI migrated to native Rust/Rune (`exo-ui`). | **Current** |
| `skullet` | `ui` | `0.0.3` | `0.0.3` | 0 | 2026-10-31 | — | MIT | **KEEP** | — | **Current** |
| `@eslint/js` | `ui` | `10.0.1` | `10.0.1` | 0 | 2026-02-06 | — | MIT | **KEEP** | — | **Current** |
| `@iconify-json/lucide` | `ui` | `1.2.157` | `1.2.159` | 2 patches | 2026-11-24 | `[<3d]` | ISC | **KEEP** | Maintainer `cyberalien` unchanged; `npm run icons` verified unchanged for Exo's mapped icons. | **Updated** (PR #155) |
| `@interfere/landmines` | `ui` | `0.3.0` | `0.3.1` | 1 patch | 2026-11-24 | `[<3d]` | MIT | **KEEP** | Added on `polish-2`; bug-fix patch. | **Updated** (PR #155) |
| `@tailwindcss/vite` / `tailwindcss` | `ui` | `^4.3.5` | `4.3.5` | 0 | 2026-11-06 | — | MIT | **KEEP** | — | **Current** |
| `@types/node` | `ui`, `tools/exo-agent-kit` | `26.6.6` | `26.6.7` | 1 patch | 2026-11-24 | `[<3d]` | MIT | **KEEP** | Type definitions patch. | **Updated** (PR #155) |
| `@types/react` / `@types/react-dom` | `ui`, `mouse` | `19.3.0` | `19.3.0` | 0 | 2026-09-09 | — | MIT | **KEEP** | — | **Current** |
| `@typescript/native` | `ui`, `mouse` | `7.1.0-dev.20261121.1` | `7.0.2` (stable) | dev pin | 2026-11-21 | — | Apache-2.0 | **KEEP** | `polish-2` intentionally pins TS 7.1 native dev build alongside TS 6.0.2. | **Held** (`polish-2` pin) |
| `typescript` | `ui`, `mouse`, `tools/exo-agent-kit` | `6.0.2` / `7.0.2` | `7.0.2` | 0 | 2026-07-08 | — | Apache-2.0 | **KEEP** | — | **Current** |
| `@vitejs/plugin-react` | `ui`, `mouse` | `^6.3.0` / `6.3.0` | `6.3.0` | 0 | 2026-11-09 | — | MIT | **KEEP** | — | **Current** |
| `esbuild` | `tools/exo-agent-kit` | `0.28.2` | `0.28.2` | 0 | 2026-11 | — | MIT | **KEEP** | Added on `polish-2`. | **Current** |
| `eslint` | `ui`, `mouse` | `10.15.0` | `10.15.0` | 0 | 2026-11-13 | — | MIT | **KEEP** | Paired with `oxlint` on `polish-2`. | **Current** |
| `eslint-plugin-oxlint` / `oxlint` | `ui`, `mouse` | `1.94.0` | `1.95.0` | 1 minor | 2026-11-23 | `[<3d]` | MIT | **KEEP** | Added on `polish-2`; rule & perf updates. | **Updated** (PR #155) |
| `eslint-plugin-react-hooks` | `ui` | `7.1.1` | `7.1.1` | 0 | 2026-04-17 | — | MIT | **KEEP** | — | **Current** |
| `globals` | `ui` | `^17.14.0` | `17.14.0` | 0 | 2026-11-01 | — | MIT | **KEEP** | — | **Current** |
| `knip` | `ui` | `^6.46.1` | `6.48.0` | 2 minors | 2026-11-22 | `[<3d]` | ISC | **KEEP** | Maintainer `webpro` unchanged; `npm run dead` passes. | **Updated** (PR #155) |
| `oxc-transform-react` | `ui` | `^0.158.0` | `0.158.0` | 0 | 2026-11-09 | — | MIT | **KEEP** | — | **Current** |
| `postcss` | `ui`, `mouse` | `8.5.33` | `8.5.33` | 0 | 2026-11-13 | — | MIT | **KEEP** | — | **Current** |
| `tsx` | `mouse` | `4.23.15` | `4.23.15` | 0 | 2026-09-20 | — | MIT | **KEEP** | — | **Current** |
| `typescript-eslint` | `ui`, `mouse` | `8.73.0` | `8.74.0` | 1 minor | 2026-11-23 | `[<3d]` | MIT | **KEEP** | Maintainers unchanged; no breaking changes. | **Updated** (PR #155) |
| `vite` | `ui`, `mouse` | `^8.3.8` / `8.3.8` | `8.3.8` | 0 | 2026-11-11 | — | MIT | **KEEP** | — | **Current** |
| `@openmouse/protocol` | `mouse/vendor/openmouse-protocol` | `0.60.0-exo.1` | `0.60.0` | 0 (Exo fork) | 2026-11-17 | — | AGPL-3.0-or-later | **KEEP** | Isolated iframe origin; `THIRD_PARTY_NOTICES.md` synced `0.58.0` → `0.60.0`. | **Current** (notice synced in PR #155) |
| `@noble/hashes` | `cloud/account` | `2.4.0` | `2.4.0` | 0 | 2026-08-27 | — | MIT | **KEEP** | — | **Current** |
| `wrangler` | `cloud/{account,covers,riot}` | `4.165.0` | `4.168.0` | 3 minors | 2026-11-24 | `[<3d]` | MIT OR Apache-2.0 | **KEEP** | Official Cloudflare publisher unchanged; all worker tests pass. | **Updated** (PR #155) |
| `@cloudflare/vitest-plugin` | `cloud/{covers,riot}` | `1.8.5` | `1.9.2` | 1 minor | 2026-11-24 | `[<3d]` | MIT | **KEEP** | Matches `wrangler@4.168.0` / `workerd`. | **Updated** (PR #155) |
| `vitest` | `cloud/{covers,riot}` | `5.1.1` | `5.1.1` | 0 | 2026-11-18 | — | MIT | **KEEP** | — | **Current** |
| `miniflare` | `cloud/updates` | `^4.20260730.0` | `4.20260730.0` | 0 | 2026-07-30 | — | MIT | **KEEP** | — | **Current** |
| `sharp` (override) | `cloud/updates` | `0.35.5` | `0.35.5` | 0 | 2026-09-27 | — | Apache-2.0 | **KEEP** | — | **Current** |
| `undici` (override) | `cloud/updates` | `>=7.30.1` | `8.13.1` | 1 major | 2026-11-20 | — | MIT | **KEEP** | `miniflare@4.20260730.0` requires `undici@^7`. | **Held back** (miniflare peer range) |

---

## 3. .NET / NuGet — Host, Headless Host, Setup, Capture, Tests, Tools

| Package | Project(s) | Current | Latest Stable | Behind | Published | Flags | License | Direction | Breaking / Changelog | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | `Directory.Build.props` | `5.6.0` | `5.6.0` | 0 | 2026-07-02 | — | MIT | **KEEP** | — | **Current** |
| `Microsoft.WindowsAppSDK.{Base,DWrite,Foundation,InteractiveExperiences,Runtime,WinUI}` | `ExoLauncher` (dropped in `ExoLauncher.Host`) | `2.7.0` | `2.7.0` | 0 | 2026-11-17 | — | Microsoft Software License Terms | **BUILD-OWN** | Being retired in v4 (`ExoLauncher.Host` `#193` already drops WASDK/WinUI3, saving `-110.6 MB`). | **Current** |
| `Microsoft.Graphics.Win2D` | `ExoLauncher` (dropped in `ExoLauncher.Host`) | `1.4.0` | `1.4.0` | 0 | 2026-03-16 | — | MIT | **BUILD-OWN** | Replaced by Rune DComp HUD (`exo-present` `#189`) and retired in `ExoLauncher.Host`. | **Current** |
| `Microsoft.Windows.SDK.BuildTools` | `ExoLauncher` | `10.0.28000.2957` | `10.0.28000.2957` | 0 | 2026-10-28 | — | Microsoft Windows SDK License | **KEEP** | — | **Current** |
| `Microsoft.Windows.SDK.BuildTools.MSIX` | `ExoLauncher` | `1.7.261102100` | `1.7.261119100` | 1 build | 2026-11-19 | — | Microsoft Software License Terms | **KEEP** | Build tooling patch. | **Updated** (PR #158) |
| `Microsoft.Web.WebView2` (+ `setup/third_party/wv2`) | `ExoLauncher`, `ExoLauncher.Host`, `setup` | `1.0.4314.35` | `1.0.4370.26` | 1 release | 2026-11-21 | — | BSD-3-Clause | **KEEP** | Keep strictly for embedded browser pages; `ExoSetup` static loader replaced long-term by `exo-installer` (`#196`). | **Updated** (PR #158) |
| `Microsoft.Data.Sqlite` | `ExoLauncher`, `ExoLauncher.Host` | `10.0.15` | `11.0.0` | 1 major | 2026-11-10 | — | MIT | **KEEP** | Targets `net10.0`; apply `WAL + mmap` PRAGMAs on Exo-owned DBs. | **Updated** (PR #158) |
| `SQLitePCLRaw.bundle_e_sqlite3` | `ExoLauncher`, `ExoLauncher.Host` | `3.0.5` | `3.0.5` | 0 | 2026-07-27 | — | Apache-2.0 | **KEEP** | — | **Current** |
| `ZstdSharp.Port` | `ExoLauncher`, `ExoLauncher.Host`, `tools/AppFiles` | `0.8.8` | `0.8.8` | 0 | 2026-04-29 | — | MIT | **BUILD-OWN** | Being replaced by native Rust `exo-delta` (`#196`, `-83.3%` update wire size). | **Current** |
| `org.k2fsa.sherpa.onnx.runtime.win-x64` | `ExoLauncher.Host` (removed from `ExoLauncher` on `polish-2`) | `2.0.2` | `2.0.3` | 1 patch | 2026-11-22 | `[<3d]` | Apache-2.0 | **BUILD-OWN** | Replaced in `ExoLauncher` on `polish-2` by `exo_voice_ml.dll` + direct `onnxruntime.dll`; bumped to `2.0.3` in `ExoLauncher.Host` until removed there too. | **Updated** (PR #158) |
| `Microsoft.ML.OnnxRuntime` | `ExoLauncher`, `ExoLauncher.Host`, `ExoLauncher.Tests` | `1.31.1` | `1.32.0` | 1 minor | 2026-11-13 | — | MIT | **KEEP** | `1.32.0` grows `onnxruntime.dll` by `+3,348,440 B` (`+3.19 MB` / `+19.4%` raw: `16.45 → 19.65 MB`; `+890,140 B` / `+0.85 MB` / `+13.7%` gzip) with 0 CVEs fixed and no idle memory/startup benefit. | **Held back** (`1.31.1`; size regression) |
| `Microsoft.NET.Test.Sdk` | `ExoLauncher.Tests`, `ExoLauncher.Host.Tests`, `ExoCapture.Tests` | `18.12.0` / `18.11.0` | `18.12.0` | 0 / 1 minor | 2026-11-11 | — | MIT | **KEEP** | Aligns `ExoCapture.Tests` with `18.12.0`. | **Updated** (PR #158) |
| `xunit.v3` | `ExoLauncher.Tests`, `ExoLauncher.Host.Tests`, `ExoCapture.Tests` | `4.0.3` / `4.0.2` | `4.0.3` | 0 / 1 patch | 2026-11-09 | — | Apache-2.0 | **KEEP** | Aligns `ExoCapture.Tests` with `4.0.3`. | **Updated** (PR #158) |
| `xunit.runner.visualstudio` | `ExoLauncher.Tests`, `ExoLauncher.Host.Tests`, `ExoCapture.Tests` | `4.0.1` | `4.0.1` | 0 | 2026-10-09 | — | Apache-2.0 | **KEEP** | — | **Current** |
| `Vortice.{Direct3D11,MediaFoundation,Direct2D1}` | `ExoCapture` | `3.8.3` | `3.8.3` | 0 | 2026-03-04 | — | MIT | **KEEP** | Keep Vortice + MF (reject FFmpeg); optionally add thin `exo-enc` NVENC/AMF/QSV wrapper. | **Current** |

---

## 4. Native UI & Rust Workspace (`apps/launcher/native-ui/`, 24 Workspace Crates on `polish-2`)

| Crate / Fork | Manifest | Current | Latest Stable | Behind | Published | Flags | License | Direction | Breaking / Changelog | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `UhhErix/rune` (`exo` branch: `rune`, `rune-kit`, `rune-media`, `rune-ui`, 6 `[patch.crates-io]`) | `native-ui/Cargo.toml` | `caaa6a71` (pinned by `polish-2` `#190`) | `exo` HEAD | pinned | 2026-11-24 | — | MIT OR Apache-2.0 | **KEEP** | Kept untouched on PR #162 per coordinator direction (`#190` already stripped unused defaults). Long-term: custom `exo-dx12` backend in `rune-gpu` (`BUILD-OWN`). | **Kept pinned** (PR #162) |
| `serde` | `native-ui/Cargo.toml` | `1.0.229` | `1.0.229` | 0 | 2026-07-18 | — | MIT OR Apache-2.0 | **KEEP** | — | **Current** |
| `serde_json` | `native-ui/Cargo.toml` | `1.0.154` | `1.0.154` | 0 | 2026-10-11 | — | MIT OR Apache-2.0 | **BUILD-OWN** | `host-v1` IPC graduated to binary `exo-wire` (`#184`); evaluate `sonic-rs` (`REPLACE-WITH-BETTER`) for remaining external JSON. | **Current** |
| `windows` | 11 workspace crates | `0.62.2` | `0.62.2` | 0 | 2025-10-06 | — | MIT OR Apache-2.0 | **KEEP** | — | **Current** |
| `windows-core` / `windows-future` | `exo-auth`, `exo-autopilot`, `exo-fg-bridge`, `exo-flow`, `exo-upscale` | `0.62.2` / `0.3.2` | `0.100.0` | locked to `windows 0.62` | 2026 | — | MIT OR Apache-2.0 | **KEEP** | Locked to `windows 0.62.2` ABI. | **Held** (matches `windows 0.62`) |
| `windows-sys` | `exo-controller`, `exo-macros*`, `exo-present`, `exo-rpc`, `exo-ui` | `0.61.2` | `0.61.2` | 0 | 2025-10-06 | — | MIT OR Apache-2.0 | **KEEP** | — | **Current** |
| `blake3` | `exo-fg-bridge`, `exo-shaders`, `exo-upscale`, `exo-delta` | `1.x` | latest `1.x` | 0 | 2026 | — | CC0-1.0 OR Apache-2.0 | **KEEP** | — | **Current** |
| `bytemuck` | `exo-fg-bridge`, `exo-flow` | `1.25.2` | `1.25.2` | 0 | 2026 | — | Zlib OR Apache-2.0 OR MIT | **KEEP** | — | **Current** |
| `image` / `png` | `exo-ui`, `exo-flow` | `0.25.10` / `0.18.1` | `0.25.10` / `0.18.1` | 0 | 2026 | — | MIT OR Apache-2.0 | **KEEP** | Keep decoders; add `exo-artcache` mmap/LZ4 cover slab (`BUILD-OWN`). | **Current** |
| `parking_lot` | `exo-fg-bridge`, `exo-present` | `0.12.5` | `0.13.0` | 1 minor (0.x) | 2026 | — | MIT OR Apache-2.0 | **KEEP** | Held at `0.12.5` to share single instance with `wgpu`/`winit`/Rune. | **Held** (shared with Rune) |
| Transitive crates in `Cargo.lock` | `native-ui/Cargo.lock` | locked | latest semver-compatible | minor/patch | — | — | MIT / Apache-2.0 / Zlib | **KEEP** | Updated `ahash 0.8.12→0.8.13`, `crc32fast 1.5.5→1.5.6`, `litrs 1.0.1→1.1.0`, `zerocopy`/`zerocopy-derive 0.8.64→0.8.65`, `zlib-rs 0.6.8→0.6.9`; pruned 4 unused crates (`getrandom 0.3.4`, `r-efi`, `wasip2`, `wit-bindgen`). | **Updated** (PR #162) |

---

## 5. Exo Shaders (`apps/launcher/shaders/`)

| Component / Package | Manifest | Current | Latest Stable | Behind | Published | Flags | License | Direction | Breaking / Changelog | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `BlueHeisenberg/SCSKiller` (upstream fork base) | `shaders/UPSTREAM.md` | `v1.4.8` (`c5badf84`) | `v1.4.9` | 1 patch | 2026-11-21T18:36Z | `[<3d]` | GPL-3.0-or-later + sec 7 | **BUILD-OWN** | Modifies `proxy/` C++ prebuilts; long-term replaced by clean-room MIT Rust `exo-shaders`. | **Held back** (`<3d`, separate upstream-merge PR) |
| `Microsoft.Bcl.Memory` | `SCSKiller.Core.csproj` | `10.0.12` | `11.0.0` | 1 major | 2026-11-10 | — | MIT | **KEEP** | Targets `net10.0`; drop-in compatible. | **Updated** (PR #195) |
| `Tmds.DBus.Protocol` | `SCSKiller.Core.csproj` | `0.94.1` | `0.95.2` | 1 minor | 2026-10-27 | — | MIT | **KEEP** | Linux-only conditional ref. | **Updated** (PR #195) |
| `ValvePak` | `SCSKiller.Core.csproj` | `6.0.0.182` | `7.2.0.221` | 1 major | 2026-10-21 | — | MIT | **KEEP** | Breaking in 7.0: canonical alphabetical VPK write order; `Source2ReaderTests.cs` updated & verified. | **Updated** (PR #195) |
| `Velopack` | `SCSKiller.Core.csproj` | `1.2.158` | `1.2.161` | 1 patch | 2026-09-29 | — | MIT | **BUILD-OWN** | Unused standalone updater inside Exo add-on; strip or replace with `exo-shaders`. | **Updated** (PR #195) |
| `NSec.Cryptography` | `SCSKiller.Core.csproj` | `26.4.0` | `26.4.0` | 0 | 2026-04-30 | — | MIT | **KEEP** | — | **Current** |
| `SCSKiller.CUE4Parse` | `SCSKiller.Core.csproj` | `1.2.2.202609-scsk.1` | local package | 0 | — | — | Apache-2.0 | **BUILD-OWN** | 6.8 MB + 11 MB BouncyCastle; replaced long-term by clean-room Rust `exo-shaders`. | **Current** |
| `Microsoft.NET.Test.Sdk` | `SCSKiller.Tests.csproj` (`ExoShaders.Service.Tests` already `18.12.0` on `polish-2`) | `17.14.1` | `18.12.0` | 1 major | 2026-11-11 | — | MIT | **KEEP** | Compatible with `xunit` 2.9.3 + `xunit.runner.visualstudio` 3.1.5. | **Updated** (PR #195) |
| `xunit` | `SCSKiller.Tests.csproj` | `2.9.3` | `2.9.3` (v2) | 0 on v2 | 2025-01-08 | — | Apache-2.0 | **KEEP** | Upstream SCSKiller tests stay on xUnit v2 for mergeability. | **Current** (on v2) |
| `xunit.runner.visualstudio` | `SCSKiller.Tests.csproj` | `3.1.4` | `3.1.5` (v2-compat) | 1 patch | 2025-09 | — | Apache-2.0 | **KEEP** | `4.x` targets `xunit.v3`; `3.1.5` is latest for xUnit v2. | **Updated** (PR #195) |
| `ValveSoftware/Fossilize` | `shaders/proxy/CMakeLists.txt` | `556da9ff` | `556da9ff` | 0 | 2026-09-24 | — | MIT | **KEEP** | Current HEAD. | **Current** |
| `zlib-ng2.dll` / `amd_ags_x64.dll` | `shaders/engine/prebuilt/` | SCSKiller 1.4.2 / AMD | unchanged | 0 | — | — | Zlib / MIT | **KEEP** | Matches upstream SCSKiller prebuilt. | **Current** |

---

## 6. Voice Native C / C++ / Rust Libraries (`apps/launcher/voice/`)

> **Note:** `voice/fetch.ps1` downloads strictly from Exo's R2 mirror (`https://updates.getexo.app/v1/cache/...`) with pinned byte size + SHA-256/SHA-512. Newer upstream archives not yet mirrored to R2 return `404`.

| Component | Manifest | Current | Latest Stable | Behind | Published | License | Direction | Breaking / Notes | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `discord/libdave` | `voice/build.ps1` | `v1.2.2/cpp` (`6396c90a`) | `v1.3.0/cpp` (`95549c90`) | 1 minor | 2026-11-10 | MIT | **KEEP** | Not yet mirrored in R2 cache (`404`) and requires rebasing `voice/dave-exporter.patch`. | **Held back** (awaiting R2 mirror + patch rebase) |
| `openssl/openssl` | `voice/ports/openssl/vcpkg.json` | `3.5.9` | `3.5.9` (3.5 LTS; `3.5.10` unreleased) / `4.1.0` | 0 on 3.5 LTS | 2026-09-29 | Apache-2.0 | **REPLACE-WITH-BETTER** | `3.5.9` is still latest published 3.5 LTS release (12 Nov-2026 Low CVEs slated for upcoming `3.5.10` are unreachable in `libdave` — see CVE section). Evaluate BoringSSL/AWS-LC for v4. | **Current (3.5 LTS)** |
| `xiph/opus` | `voice/build.ps1` | `v1.6.1` (`22244de5`) | `v1.6.1` | 0 | 2026 | BSD-3-Clause | **KEEP** | Carries `voice/opus-downmix.patch`; enable AVX2 + LTCG build flags. | **Current** |
| `webrtc-audio-processing` | `voice/apm/subprojects/*.wrap` | `v2.1` (`846fe90a`) | `v2.1` | 0 | 2025-01-22 | BSD-3-Clause | **KEEP** | Latest tag on freedesktop GitLab. | **Current** |
| `abseil/abseil-cpp` | `voice/apm/subprojects/abseil-cpp.wrap` | `20240722.0` | `20260817.0` | ~2 years | 2026-08-18 | Apache-2.0 | **KEEP** | Requires Meson wrapdb patch + R2 cache upload (`404`). | **Held back** (awaiting R2 mirror) |
| `meson` | `voice/build.ps1` | `1.9.1` | `1.12.1` | 3 minors | 2026-09-22 | Apache-2.0 | **KEEP** | Build-time wheel fetched solely from Exo R2 cache. | **Held back** (awaiting R2 mirror) |
| `Rikorose/DeepFilterNet` | `voice/build.ps1` | `v0.5.6` (`978576aa`) | `v0.5.6` | 0 | 2023-08-31 | MIT OR Apache-2.0 | **BUILD-OWN** | 50.64 MB `exo-deepfilter.dll` (`tract` + FP32 weights); unified on `polish-2` via `exo-voice-ml` + `onnxruntime.dll` (DPDFNet + VoxLock). | **Current** |

---

## 7. Vendored Binaries, Extensions, Assets & Pinned Runtime / Tool Downloads

| Component | Location | Current | Latest Stable | Behind | Published | Flags | License | Direction | Verification / Notes | Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `OBS Studio` win-capture hook (6 files) | `capture/third_party/obs/` | `32.2.2` | `33.0.3` | 1 major | 2026-11-22 | `[<3d]` | GPL-2.0-or-later | **KEEP** | `HOOK_VER_MAJOR = 1` (`1.8.8`), `graphics-hook-info.h` byte-identical (`648 B`); all 6 signed by `OBS Project, LLC`. | **Updated** (PR #198) |
| `PawnIO.Modules` (`RyzenSMU.bin`) | `ExoLauncher/Assets/pawnio/` | `0.2.11` | `0.2.12` | 0 (byte-identical) | 2026-11-17 | — | LGPL-2.1 | **KEEP** | Verified `RyzenSMU.bin` inside `0.2.12` is byte-identical (`50,764 B`, SHA-256 `301D9CA3...`). | **Current** (byte-identical) |
| `Exo Shield` (`uBlock Origin Lite` + HaGeZi) | `Assets/Extensions/exo-shield/` | `2026.1119.1` | `2026.1122.1948` | 3 days | 2026-11-22 | `[<3d]` | GPL-3.0 | **KEEP** | Custom-assembled extension with HaGeZi Multi PRO ruleset; `<3d` upstream release. | **Held back** (`<3d` + custom assembly) |
| `SponsorBlock` | `Assets/Extensions/sponsorblock/` | `6.1.7` | `6.1.7` | 0 | 2026-07-13 | — | GPL-3.0 | **KEEP** | Current. | **Current** |
| `Geist` font (vercel/geist-font) | `native-ui/crates/exo-kit/assets/fonts/`, `ui/public/fonts/`, `mouse/src/fonts/` | `v1.8.1` | `v1.9.2` | 0 | 2026-11-26 | — | OFL-1.1 | **KEEP** | Replaces Goeist on every surface (owner, 2026-11-26); the variable TTFs are byte-identical from v1.8.1 to v1.9.2. | **Current** |
| `emojibase-data` | `ui/src/components/discord/emoji.json` | `18.0.0` | `18.0.0` | 0 | 2026-10-16 | — | MIT | **KEEP** | Current. | **Current** |
| `Simple Icons` (inline brand SVGs) | `connectionMarks.ts`, `ServiceMarks.cs` | `16.36.0` | `16.38.1` | 2 minors | 2026-11-22 | `[<3d]` | CC0-1.0 | **KEEP** | Hand-picked brand paths; no target brand icon changed. | **Held back** (`<3d`) |
| `Legendary` / `gogdl` / `Nile` (store CLIs) | `Adapters/{Epic,Gog,Amazon}Adapter.cs` | `0.21.1` / `v1.4.0` / `v1.2.0` | same | 0 | 2026 | — | GPL-3.0 | **BUILD-OWN** | Current; clean-room MIT Rust `exo-stores` engine landed on `polish-2` (`#199`). | **Current** |
| `TitaNet-small` / `DPDFNet` / `VoxLock` (voice ONNX models) | `Voice/SpeakerModel.cs`, `exo-voice-ml` | pinned | unchanged | 0 | 2026 | — | CC-BY-4.0 / Apache-2.0 | **KEEP** | Models checked against size + SHA-256; VoxLock added on `polish-2`. | **Current** |
| `NVIDIA DLSS` / `AMD FidelityFX` / `Intel XeSS` | `Services/DlssSwapService.Releases.cs` | `310.9.16` / `2.4.0` / `3.0.2` | same | 0 | Jul–Oct 2026 | — | Vendor SDK licenses | **KEEP** | All three match latest GitHub releases. | **Current** |
| `OptiScaler` (FrameGen pin) | `Services/DlssSwapService.FrameGen.cs` | `v0.9.4` | `v0.9.5` | 1 patch | 2026-11-16 | — | GPL-3.0 | **KEEP** | `Optiscaler_0.9.5-final.20261116.7z` (`70,418,897 B`, SHA-256 verified). | **Updated** (PR #198) |
| `MFG-Unlocker` (FrameGen pin) | `Services/DlssSwapService.FrameGen.cs` | `v1.7.0` | `v1.7.2` | 2 patches | 2026-11-22 | `[<3d]` | MIT | **KEEP** | `MFGUnlocker-v1.7.2.zip` (`5,334,929 B`, SHA-256 verified). | **Updated** (PR #198) |
| `dlssg_for_sm86` (FrameGen pin) | `Services/DlssSwapService.FrameGen.cs` | `0.4.2` (pinned on `polish-2`) | `0.4.2` | 0 | 2026-11-14 | — | MIT + NVIDIA binary | **KEEP** | Preserved `polish-2`'s `0.4.2` pin (`89,769,758 B`). | **Current** |
| `Microsoft.GameInput` (runtime pin) | `Services/DependencyService.Catalog.cs` | `4.0.306` | `4.1.315` | 1 minor | 2026-11-23 | `[<3d]` | Microsoft Software License Terms | **KEEP** | `6,247,535 B`, SHA-256 `7573FEFD...`, `GameInputRedist.msi` verified `Valid` Authenticode (`Microsoft Corporation`). | **Updated** (PR #198) |
| `Visual C++ 2015–2026` (`vc14`) | `Services/DependencyService.Catalog.cs` | `14.52.37029.0` | `14.52.37029.0` | 0 | 2026 | — | Microsoft | **KEEP** | Live `aka.ms/vs/18/release/vc_redist.{x64,x86,arm64}.exe` lengths match exactly. | **Current** |
| Legacy VC++ (`2005`–`2013`), `DirectX Jun 2010`, `.NET Fx 4.8.1`, `XNA 4.0`, `OpenAL 2.0.7.0`, `PhysX 9.26.0703` & `9.13.0604` | `DependencyService.Catalog.cs` | pinned | same | 0 | — | — | Vendor | **KEEP** | All verified at latest final/stable releases. | **Current** |
| `Equalizer APO` | `EqualizerApo.cs` | `1.4.2` | `1.4.2` | 0 | — | — | GPL-2.0 | **BUILD-OWN** | Clean-room MIT Rust `exo-audio-apo` (`exo_apo.dll`) landed on `polish-2` (`#200`, `#206`). | **Current** |
| `.NET Desktop Runtime` fallback channels | `Services/DependencyService.Catalog.cs` | `10.0.15`, `9.0.23`, `8.0.34`, `6.0.36` | same (+ new `11.0.0` channel) | 0 on tracked channels | 2026-11-10 | — | MIT | **KEEP** | All 4 tracked channels at latest patches. | **Current** |
