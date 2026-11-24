EXO LAB: Closed-Loop Profile-Guided Optimization (Crossgen2 Static PGO + LLVM PGO/BOLT + AVX2) (task-name: pgo-bolt-pipeline)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "Improve any feature to impossible lengths. Try anything. Make new technology and software."
> Vision: the best performance from every part of every PC — extract every free 15–30% CPU, startup, and frame-pacing win from compiler & binary layout optimization with zero UX compromise.

## Today (Measured at `e0289d9`)

- `ExoLauncher/ExoLauncher.csproj:39` explicitly sets `<TieredPGO>false</TieredPGO>` (to avoid runtime JIT tiering overhead), leaving `ExoLauncher.dll` (30.05 MB) with **zero dynamic PGO AND zero static Crossgen2 PGO** and **non-composite ReadyToRun**.
- `native-ui/Cargo.toml:28-32` compiles `exo-ui.exe` (20.73 MB) with `lto = "thin"`, `codegen-units = 1`, `strip = true`, `panic = "abort"` — **no LLVM PGO (`-Cprofile-use`), no LLVM BOLT post-link basic-block reordering, and baseline `x86-64` ISA** even though Windows 11 x64 requires 8th-gen Intel / Ryzen 2000+ (100% `x86-64-v3`: AVX2 + FMA + BMI2!).
- `voice/build.ps1` compiles `opus.dll`, `exo_apm.dll`, and `dave.dll` with default MSVC Release flags — no `/GL` + `/LTCG` PGO and no AVX2 codegen check.

## 1. Benchmark Suite First (Repeatable & Real)

- Establish deterministic, zero-flake benchmarks on this PC (`Ryzen 5 5600X`, `RTX 3070`):
  1. **`exo-ui` (Rust/Rune):** Cold start ms to first frame + headless Rune layout/shaping/render-list build time (500-game Library scroll, 1,000-message Discord channel, Store shelves, PC room) + `exo-ui.exe` size & instruction-cache miss count (via ETW/Windows perf counters if available, or wall-time P50/P95 across 100 iterations).
  2. **`ExoLauncher` (.NET 10):** Cold start `managedEntryMs` + `first library.get` projection ms + 1,000 RPC serialize/deserialize throughput (`WebHostBridge` / `ExoBridgeProtocol`) + working set MB.
  3. **`voice/` DLLs (`opus.dll`, `exo_apm.dll`):** 10,000-frame 48 kHz encode/decode & AEC3 processing CPU time (ms/frame and RTF).

## 2. Closed-Loop PGO + BOLT Pipeline

1. **Rust `exo-ui.exe` LLVM PGO + `x86-64-v3` (+ BOLT Experiment):**
   - Add a reproducible PGO training harness script (`tools/pgo/train-exo-ui.ps1`): compiles `exo-ui` with `-Cprofile-generate=<dir>`, runs a deterministic headless workload covering all rooms (Library, Store, Game Page, Discord, Music, PC, Devices, Settings, Palette, Tour) using Rune's headless `Runtime` / live disposable-dir probe, merges `.profraw` via `llvm-profdata`, and rebuilds with `-Cprofile-use=<merged.profdata> -Cllvm-args=-pgo-warn-missing-function`.
   - Test `-C target-cpu=x86-64-v3` (verify CPU feature guard at startup via `is_x86_feature_detected!("avx2")` or clear message for unsupported CPUs, though Win11 officially mandates AVX2-capable CPUs).
   - Test `lto = "fat"` vs `lto = "thin"` + PGO (measure binary size vs speed vs build time).
   - Experiment with `llvm-bolt` (Windows PE/COFF support status in LLVM 19/20 — document honestly if Windows PE BOLT works or if LLVM PGO + `lld-link` `/OPT:REF,ICF` + function-order profile is the shipping winner!).
2. **.NET 10 Host Static Crossgen2 PGO (`.mibc`) + Composite R2R:**
   - Capture an EventPipe/JIT profile (`.mibc`) from a real startup + Library/Store/Discord/PC RPC script against a disposable data dir, feed it to Crossgen2 (`<PublishReadyToRunComposite>true</PublishReadyToRunComposite>` + `PublishReadyToRunPgo`), and measure cold startup ms, RPC throughput, and binary size vs stock `3.30.2` (zero runtime TieredPGO jitter!).
3. **Voice DLLs (`/GL` + `/LTCG` / AVX2):**
   - Verify whether `opus.dll` and `exo_apm.dll` already enable AVX2 runtime dispatch (`-DOPUS_X86_MAY_HAVE_AVX2=ON` etc.) and benchmark `/GL /LTCG` + PGO on 10,000 voice frames.

## 3. Targets & Deliver

- **Targets:** **≥15%** speedup in `exo-ui` headless layout/frame-build CPU time, **≥10%** faster cold boot on `ExoLauncher` + `exo-ui`, **zero** regressions in `cargo test --workspace --locked`, `ExoLauncher.Tests`, or `DiscordVoiceTests`, and a single reproducible script `tools/pgo/Build-Pgo.ps1` ready for CI release builds.
- Branch `lab/pgo-bolt-pipeline` from `origin/main`.
- `improve/pgo-bolt-pipeline-report.md`: full A/B benchmark table (stock vs `x86-64-v3` vs LLVM PGO vs fat LTO vs BOLT vs Crossgen2 Static PGO), binary sizes, build-time delta, and exact toolchain versions.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "PGO and BOLT pipeline results" --body "<exo-ui frame speedup %, host startup speedup %, voice speedup %, binary size delta, BOLT verdict>"`. Effort: high.
