EXO LAB: 20x Faster Dev Loop & Multi-Worktree Shared Build Fabric (task-name: fast-dev-loop-and-shared-build-cache)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... the code itself, the pipelines, the flow, the technology ... every little nook and cranny, to see if we can make something better."
> Vision: instant inner loop for both humans and multi-agent Orca swarms across 60+ git worktrees on a 6C/12T 16 GB PC.

## Today (Measured Directly on This PC — Ryzen 5 5600X, 16 GB RAM)

- **66 active git worktrees** on this machine: **26 separate `native-ui/target`** directories (~3.12+ GB each = **~80–150 GB** wasted SSD!) and **39 separate `ui/node_modules`** directories!
- **`native-ui` cold `cargo test --workspace --locked`:** **588.6 s (9m 49s)** — **490 s compile** + **95.35 s running 51 `exo-ui` tests** because `[profile.dev.package."*"]` runs `rune`/`parley`/`taffy` at `opt-level = 0`! Followed by `cargo clippy --workspace --locked` taking **148.48 s (2m 28s)** = **737 s (>12 min) per new worktree!**
- **`ui` `npm run check` (serial): ~191.8 s (>3.1 min):**
  - JS `tsc` (`typescript@6.0.2`): **18.33 s** — YET `@typescript/native@7.0.2` (`tsgo`) is **ALREADY INSTALLED** in `ui/package.json` & `mouse/package.json` and measured **1.95 s (9.4x faster, exit 0)**!
  - `contracts`: **19.16 s**; `eslint`: **34.22 s**; `test` (70 TS test files): **104.56 s**; `check-comments`: **8.98 s**; `lint:css` 1.73 s; `icons:check` 2.05 s; `css:prune:check` 2.77 s.
- **`ExoLauncher.Tests` builds:** Triggers `BuildWebUi` (`build-cache.mjs` synchronously SHA-256 hashing all of `ui/` + `mouse/` + `wwwroot/`), triggers NativeAOT `PrepareSteamIpc` (missing 2 inputs `SteamIpcStubResolver.cs` & `SteamStateFlags.cs` at `ExoLauncher.csproj:138`!), recopies `Assets\**\*` (`Always` at `ExoLauncher.csproj:90-100`) and `onnxruntime.dll` (no `SkipUnchangedFiles` at L162), spawns 2 `git` processes per Debug build (`Directory.Build.targets:8-17`), and `exo.json:4` forces `-m:1` single-core MSBuild!
- **`cloud/reports` `npm test` (113 in-memory tests): 69.01 s** solely due to an un-cleared 8 s `setTimeout` at `cloud/reports/src/issues.js:136`!
- **`tools/check-comments.mjs`:** Fails `check-comments.test.mjs:96` and produces wrong `.tsx` ratchet counts whenever `ui/node_modules` isn't installed; omits `.rs`/`.c`/`.cpp`.
- **`tools/swap-dev.ps1:17`:** Silently skips updating `exo-ui.exe` if `publish` already has an older copy!

## 1. Fixes & Speedups (Every One Measured Before/After)

1. **Rust `native-ui` (588.6 s → <25 s warm cache / <15 s test run):**
   - Add `[profile.dev.package."*"] opt-level = 2` (and `[profile.test.package."*"] opt-level = 2`) in `native-ui/Cargo.toml` — re-measure the 51 `exo-ui` `motion_gate` tests (target: **95.35 s → <15 s**).
   - Add `native-ui/.cargo/config.toml` enabling `lld-link` (if installed) and optional `RUSTC_WRAPPER=sccache` guidance + documented shared `sccache` setup script for Orca worktrees (so 26 worktrees share one compiled crate cache instead of recompiling 350 crates 26 times!).
2. **TypeScript `ui/` & `mouse/` (191.8 s → <25 s):**
   - Switch `ui/package.json` (`typecheck`, `build`) and `mouse/package.json` (`check`, `build`) from JS `tsc` to already-installed `@typescript/native/bin/tsc` (`tsgo`) — verified **9.4x speedup (18.33 s → 1.95 s)**.
   - Run independent `ui` checks (`typecheck`, `contracts`, `lint`, `lint:css`, `css:prune:check`, `test`, `check-comments`) **in parallel** via a 40-line `ui/scripts/check-all.mjs` runner.
   - Profile why `ui` `node --test test/*.test.ts` takes **104.56 s** (find top 10 slowest test files and fix/parallelize with `--test-concurrency`!) and benchmark `oxlint` alongside ESLint (for the 34.22 s lint step).
3. **.NET Host & `ExoLauncher.Tests` Build Graph:**
   - Add `<SkipWebUi Condition="'$(BuildingForTest)' == 'true'">true</SkipWebUi>` (or `ExoSkipWebUi`/`ExoSkipSteamIpc`) so `dotnet test ExoLauncher.Tests` never blocks on `node` or NativeAOT linking of `SteamIpc` unless explicitly building the full app.
   - Fix `ExoLauncher.csproj:90-100` (`CopyToOutputDirectory=PreserveNewest` for `Assets` & font, keeping `CopyToPublishDirectory=Always` for clean releases), `ExoLauncher.csproj:162` (`SkipUnchangedFiles="true"` on `BuildOnnxRuntime`), and `ExoLauncher.csproj:138` (add missing `SteamIpcStubResolver.cs` & `SteamStateFlags.cs` to `_SteamIpcInputs`!).
   - Fix `exo.json:4` (remove `-m:1`) and `tools/swap-dev.ps1:17` (always copy fresher `exo-ui.exe`).
4. **Cloud Reports & Tooling Bugs:**
   - Fix `cloud/reports/src/issues.js:136` leaked 8 s `setTimeout` (`finally { clearTimeout(timer) }`) — measure drop from **69.01 s → <2 s**!
   - Fix `tools/check-comments.mjs` JSX apostrophe fallback bug (`check-comments.test.mjs:96` green without `ui/node_modules`) and add `.rs`/`.c`/`.cpp` support.

## 2. Deliver

- Branch `lab/fast-dev-loop-and-shared-build-cache` from `origin/main`.
- `improve/fast-dev-loop-report.md`: before/after timing table for every single suite (`cargo test`, `cargo clippy`, `ui check`, `ExoLauncher.Tests` incremental build, `cloud/reports test`, `tools` tests) and worktree disk savings guide.
- All modified gates verified green (`ui check`, `mouse check`, `ExoLauncher.Tests`, `cargo test --workspace --locked`, `cloud/reports`, `tools` tests).
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "fast dev loop and build cache results" --body "<cargo test before/after, ui check before/after, cloud/reports before/after, dotnet test incremental delta>"`. Effort: medium.
