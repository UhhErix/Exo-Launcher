EXO LAB: 380 MB -> <110 MB Install & Binary Diet (task-name: binary-footprint-diet)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "Improve any feature to impossible lengths. Try anything. Make new technology and software."
> Vision: the best performance from every part of every PC — lean, fast installs, tiny updates, minimal disk & AV scan overhead.

## Today (Measured in `%LOCALAPPDATA%\ExoLauncher\app` @ 3.30.2)

- Total installed size: **380.02 MB across 1,039 files**; installer `ExoLauncher-3.30.1-Setup.exe`: **137.02 MB**.
- Top measured bloat culprits:
  1. **`exo-deepfilter.dll`: 50.64 MB**! PE section inspection shows `.rdata = 36.99 MB`, `.text = 13.16 MB`. `voice/build.ps1:135` runs `cargo rustc --release` with NO `strip`, NO `lto`, NO `opt-level = "s"`, panic unwinding enabled, AND ships an entire Rust `tract` inference engine right beside `onnxruntime.dll` (16.45 MB) already shipped for TitaNet!
  2. **`addons\shaders\engine`: 129.03 MB across 256 files** (34% of the entire app!). `shaders/engine/build/publish-service.ps1:75-86` publishes `ExoShaders.Service`, `SCSKiller.Cli`, and `SCSKiller.CliW` **3 separate times** with `--self-contained true` and **NO `<PublishTrimmed>true</PublishTrimmed>`**, shipping a second full untrimmed .NET 10 runtime (`System.Private.CoreLib.dll` 15.29 MB vs Exo's trimmed 8.70 MB, `System.Private.Xml.dll` 7.43 MB, `BouncyCastle.Cryptography.dll` 11.06 MB, `CUE4Parse.dll` 6.79 MB, `coreclr.dll` 4.40 MB).
  3. **92 unused WinUI satellite locale directories (184 files, ~3.6 MB):** `af-ZA`..`zh-TW` shipped because `ExoLauncher/ExoLauncher.csproj` lacks `<SatelliteResourceLanguages>en-US</SatelliteResourceLanguages>` (unlike `ExoShaders.Service.csproj:23` which already sets it).
  4. **`wwwroot/assets/CHANGELOG_*.js`: 613.5 KB** bundled directly into JS via `import('../../../CHANGELOG.md?raw')` in `ui/src/lib/whatsNew.ts:88`.

## 1. Benchmark Baseline & Targets

- Record exact file count, uncompressed MB, and zstd-compressed `files.pack` MB before any change.
- **Targets:**
  - Total installed app: **380.02 MB → <110 MB** (`-71%`), file count **1,039 → <350** (`-66%`).
  - `exo-deepfilter.dll`: **50.64 MB → <6 MB** (or **0 MB** if DeepFilterNet3-LL runs on the already-shipped `onnxruntime.dll` with equal/better RTF!).
  - `addons\shaders\engine`: **129.03 MB → <35 MB** (`-73%`).
  - `wwwroot/assets`: eliminate 613.5 KB `CHANGELOG` JS chunk.
  - Zero feature regressions, zero license boundary violations (keep `shaders/` GPL-3.0 and `mouse/` AGPL-3.0 strictly isolated processes!).

## 2. Workstreams

1. **`ExoLauncher.csproj` Satellite Languages & Publish Hygiene:**
   - Add `<SatelliteResourceLanguages>en-US</SatelliteResourceLanguages>` to `ExoLauncher/ExoLauncher.csproj`.
   - Audit remaining untrimmed third-party DLLs in `publish` and verify `PublishReadyToRunComposite` size/startup trade-off.
2. **`exo-deepfilter.dll` 50.64 MB Slim-Down (or ONNX Runtime Unification):**
   - Inspect what sits inside the 36.99 MB `.rdata` section (embedded tarballs, `tract` LUTs, panic strings, debug symbols).
   - Experiment A: Build `voice/build.ps1` `dfn` with `lto = "thin"`, `codegen-units = 1`, `opt-level = "s"`, `strip = "symbols"` (keep `catch_unwind` or convert C API error path cleanly + add null checks to `exo-capi.rs:39-40`), and verify size + frame latency on `DiscordVoiceTests`.
   - Experiment B (moonshot): Export DeepFilterNet3-LL ONNX weights (~2 MB) and run via the ALREADY-SHIPPED `onnxruntime.dll` (16.45 MB) — measure RTF, CPU, quality (coordinate with `lab-voice-ns` if active) vs `tract` DLL. Pick the smaller/faster winner!
   - Also fix `voice/build.ps1:125` cache invalidation so editing `exo-capi.rs` or `offline-vendor.patch` triggers rebuild.
3. **`addons\shaders\engine` 129.03 MB Diet (GPL-3.0 Isolated):**
   - Enable `<PublishTrimmed>true</PublishTrimmed>` + `<TrimMode>partial</TrimMode>` (or full trim with explicit roots for JSON/CUE4Parse) in `ExoShaders.Service.csproj`.
   - Stop triple-publishing `ExoShaders.Service`, `SCSKiller.Cli`, and `SCSKiller.CliW` into the same directory (`publish-service.ps1:75-86`): merge CLI/CliW entry points or publish with shared app-host + single R2R pass.
   - Audit `BouncyCastle.Cryptography.dll` (11.06 MB) — check what single algorithm `SCSKiller.Core`/`CUE4Parse` actually uses and whether .NET 10 `System.Security.Cryptography` replaces it!
   - Verify `ExoBuild.Enabled == true`, zero upstream network calls, and `shaders/engine/tests/ExoShaders.Service.Tests` + `shaders/tests/SCSKiller.Tests` green (run from `apps/launcher/shaders/`!).
4. **`ui` `CHANGELOG.md` Externalization (`ui/src/lib/whatsNew.ts:88`):**
   - Replace `import('../../../CHANGELOG.md?raw')` with a host RPC (`app.changelog`) or static asset fetch so 613.5 KB of markdown is never parsed as JS.

## 3. Deliver

- Branch `lab/binary-footprint-diet` from `origin/main`.
- `improve/binary-footprint-diet-report.md`: before/after byte & file-count table per component, `files.pack` size delta, test results (`ExoLauncher.Tests`, `DiscordVoiceTests`, `ExoShaders.Service.Tests`, `ui` `npm run check` + `build`).
- Clean, minimal PR-ready commits (never modify `%LOCALAPPDATA%\ExoLauncher\app`).
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "binary footprint diet results" --body "<before/after total MB, file count, deepfilter MB, shaders MB, gate results>"`. Effort: high.
