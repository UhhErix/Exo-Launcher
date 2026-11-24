EXO LAB: Continuous Automatic Perf-Regression Gate & Native Telemetry Lab (task-name: auto-perf-regression-gate)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... every little nook and cranny, to see if we can make something better, more experimental, not seen before."
> Vision: the best performance from every part of every PC — and a continuous automated immune system that makes perf, memory, frame-pacing, and binary-size regressions impossible to merge unnoticed.

## Today (Audited in `tools/e2e/suites/perf.mjs`, `tools/measure-memory.ps1`, `native-ui`)

- `tools/e2e/suites/perf.mjs` tests **ONLY** WebView2/CDP (`msedgewebview2`, `HeapProfiler.collectGarbage`, `frameStart` JS rAF) with huge static failure ceilings (`room-switch p95 > 1500 ms`, `heap growth > 60 MB`, `idle-cpu > 25% of a core`!) — a 3x regression from 40 ms to 120 ms or +40 MB RAM passes silently!
- `tools/measure-memory.ps1:39, 100, 127` hardcodes `WinUIDesktopWin32WindowClass`, dead `'Discord.exe'`, and `msedgewebview2.exe` — **completely blind to v4 `exo-ui.exe`, `ExoCapture.exe`, and `ExoShaders.Service.exe`**!
- `native-ui/crates/exo-ui/src/library.rs:963-980` has a great headless `motion_gate::Run` test harness, **YET explicitly filters out `pop-out` and `jerk` findings** for `nav-active-pill`, `gp-poster-`, and `tile-`!
- There is **ZERO PR-level tracking** of binary sizes (`ExoLauncher.dll`, `exo-ui.exe`, `exo-deepfilter.dll`, `wwwroot/assets/*.js`), zero Rust `criterion` layout/frame microbenchmarks, zero `.NET` RPC allocation microbenchmarks, and zero historical baseline / statistical change-point detection.

## 1. Deterministic PR-Level Perf & Size Gates (Zero-Flake in GitHub Actions)

1. **Binary & Asset Size Budget Gate (`tools/perf/check-size-budget.mjs`):**
   - Tracks exact byte sizes of `ExoLauncher.dll`, `exo-ui.exe`, `ExoCapture.exe`, `ExoLauncher.SteamIpc.exe`, `wwwroot/assets/*`, and total publish size against `tools/perf/size-budgets.json`.
   - Emits a crisp markdown delta table on PRs and fails if any tracked artifact grows by `>2%` (or `>250 KB`) without an explicit budget update in the PR.
2. **Rust `exo-ui` Headless Frame-Budget Microbenches (`criterion`):**
   - Add `benches/` in `native-ui/crates/exo-ui` benchmarking deterministic CPU-side view construction + Rune layout + text shaping (`Runtime::new` + `step()`) for worst-case scenes:
     - `library_500_games_grid_scroll`
     - `discord_1000_messages_channel`
     - `store_home_shelves`
     - `pc_exo_ai_bento`
     - `palette_search_500_games`
   - Track P50/P95/P99 microseconds per frame build and heap allocation count (custom counting global allocator in bench harness!) — **allocations per frame are 100% deterministic even on noisy cloud CI runners!**
3. **.NET Host Allocation & Serialization Microbenches (`ExoLauncher.Benchmarks`):**
   - Measure deterministic allocations (`GC.GetAllocatedBytesForCurrentThread()`) and throughput for `ExoBridgeProtocol` request/response serialization, `LibraryService` 500-game projection, `CoverArtService.WithCovers`, and `AppFiles.Diff`. Allocation-count regressions fail cleanly in CI with zero wall-clock noise!
4. **Tighten `exo-ui` Motion Gate (`library.rs:963-980`):**
   - Audit why `pop-out` and `jerk` were filtered out in `library.rs:963-980`; document or fix them so the motion gate catches real animation hitches rather than hiding them.

## 2. Real-Hardware Macro Perf Lab & Statistical Auto-Bisector

- **Upgrade `tools/measure-memory.ps1` for v4:**
  - Support both 3.x (`WinUIDesktopWin32WindowClass` + `msedgewebview2`) and v4 (`exo-ui.exe` + `ExoLauncher.exe` + `ExoCapture.exe` + `ExoShaders.Service.exe` + hidden music WebView2); remove dead `'Discord.exe'` cohort; output structured JSON suitable for time-series history.
- **Native v4 Perf Suite (`tools/perf/run-native-perf.ps1`):**
  - Runs against an isolated disposable data dir (never touching `%LOCALAPPDATA%\ExoLauncher`) and records: cold startup timeline (`Process.Start` → host pipe ready → `exo-ui` first frame → `library.get` complete), idle CPU % & private MB at 30 s, room-switch frame pacing, and `PERF rpc` P50/P95/P99 across 200 calls.
- **Beyond State-of-the-Art — Statistical Change-Point Detector & Auto-Bisect Driver (`tools/perf/regress.mjs`):**
  - Stores rolling JSONL benchmark history (`tools/perf/history/` or CI artifact), runs a two-sample **Mann-Whitney U test** (p < 0.01 + effect size > 5%) comparing current commit vs 14-day rolling baseline, and provides a one-command `node tools/perf/bisect.mjs --metric <name> --good <sha> --bad <sha>` wrapper around `git bisect run` that automatically pinpoints the exact regressing commit + emits a before/after flamegraph/allocation diff!

## 3. Targets & Deliver

- **Targets:** Deterministic allocation-count & binary-size gates running in `<60 s` in CI with **0% flake rate**; `measure-memory.ps1` tracking 100% of v4 processes; `criterion` + allocation bench baseline established for all 5 heavy `exo-ui` scenes; working Mann-Whitney U regression detector + unit tests.
- Branch `lab/auto-perf-regression-gate` from `origin/main`.
- `improve/auto-perf-regression-gate-report.md`: full initial baseline numbers (binary sizes, `exo-ui` µs/frame & allocs/frame per scene, `.NET` RPC alloc bytes, updated `measure-memory.ps1` output, Mann-Whitney U test verification).
- Never overwrite `%LOCALAPPDATA%\ExoLauncher\app` or run interactive `tools/e2e` while a game is open.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "auto perf regression gate results" --body "<benchmarks established, exo-ui frame us & allocs baseline, size budget baseline, gate test status>"`. Effort: high.
