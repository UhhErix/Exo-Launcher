EXO LAB: Full-Coverage Sharded CI, Hermetic `voice/` Builds & Zero Silent Skips (task-name: ci-sharding-and-hermetic-builds)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... the pipelines, the flow, the technology ... every little nook and cranny, to see if we can make something better."
> Vision: fast (<6 min) hermetic CI that actually runs EVERY test suite in the repo with zero silent skips and zero unpinned network downloads.

## Today (Audited Directly in `.github/workflows/` & `voice/`)

- **6 test suites are COMPLETELY ABSENT from GitHub Actions CI:**
  1. `mouse/` (`npm run check` — 5 test files + tsc + eslint + CSS design);
  2. `capture/ExoCapture.Tests` (**86 tests**, passed in 36.52 s locally);
  3. `shaders/engine/tests/ExoShaders.Service.Tests` + `shaders/tests/SCSKiller.Tests` (155 test files);
  4. `relay/` (**14 tests**, passed in 5.14 s locally);
  5. `shield/` (`node shield/build.mjs --check`, deterministic MV3 pin check);
  6. `tools/` (`tools/exo.test.mjs`, `tools/e2e/lib/*.test.mjs`, `tools/Test-UpdateRollback.ps1`, `tools/Test-InstallerPayload.ps1`, `tools/Test-DependencySecurity.ps1`, `tools/Test-SecretHistory.ps1`, `voice/fetch.tests.ps1`, `ui` `npm run dead`).
- **22+ silent `Assert.Skip`s in `ExoLauncher.Tests`:** `.github/workflows/launcher-ci.yml:98` runs `ExoLauncher.Tests` in `Debug` (not `Release`) without `voice/out/*.dll` or `ExoShaders.Service`, silently skipping ALL DAVE, Opus, AEC3, DeepFilterNet, Shaders, and symlink-traversal tests!
- **`.github/workflows/native-ui-ci.yml:47`:** Runs ONLY `cargo test --locked` — skips `cargo fmt --check`, `cargo clippy --workspace --locked`, and `cargo build --release --locked --bin exo-ui`, AND uses raw `actions/cache` on `apps/launcher/native-ui/target/` (unbounded cache bloat across commits instead of `swatinem/rust-cache` / `sccache`).
- **`launcher-ci.yml:53-62, 93-95`:** Runs 5 Cloud Workers sequentially (`cloud/reports` alone takes 69 s today!), and `test` job runs `npm ci --prefix ui` + `npm ci --prefix mouse` solely because `ExoLauncher.csproj` builds UI during `dotnet test`.
- **Non-hermetic `voice/build.ps1:52-56`:** Comment admits `vcpkg` clones `github.com/microsoft/vcpkg` and fetches `nlohmann-json`, `gtest`, `mlspp` from GitHub during build, builds 4 targets serially, and invokes `VsDevCmd.bat` 6 separate times!
- **Broken `shaders` test command (`AGENTS.md:72` & `exo_run_gates`):** Running `dotnet test shaders/engine/tests/ExoShaders.Service.Tests` from `apps/launcher/` fails (exit 1) because `apps/launcher/global.json` forces `Microsoft.Testing.Platform` while `shaders` uses VSTest!

## 1. Hermetic `voice/` + Cached Prebuilt Artifact for CI

- Implement the mirrored vcpkg baseline + `X_VCPKG_ASSET_SOURCES` asset-cache plan documented in `voice/build.ps1:54-56` (plus mlspp overlay portfile pin), invoke `VsDevCmd.bat` once per script, and parallelize `dave`/`opus`/`apm`/`dfn` where safe.
- Add a content-hashed `voice-libs` CI job/cache keyed on `hashFiles('apps/launcher/voice/**')` so `dave.dll`, `opus.dll`, `exo_apm.dll`, and `exo-deepfilter.dll` are built ONLY when voice pins change and restored into `ExoLauncher.Tests` on every PR — eliminating silent voice test skips!

## 2. Sharded Full-Coverage CI Workflows

- Refactor `.github/workflows/launcher-ci.yml` into fast, parallel jobs (keeping the required `test` status check name intact for branch protection!):
  - `ui-mouse-shield`: parallel `ui` (`npm run check` + `npm run dead`), `mouse` (`npm run check`), `shield` (`node shield/build.mjs --check`), `tools` (`exo.test.mjs`, `check-comments.test.mjs`, `check-comments.mjs`, `e2e/lib/*.test.mjs`).
  - `cloud-and-relay`: matrix/parallel across `cloud/account`, `cloud/covers`, `cloud/reports`, `cloud/riot`, `cloud/updates`, `relay` + `wrangler deploy --dry-run` bundle check!
  - `host-tests` (sharded across 2–4 shards if >3 min, Release x64, `--filter` or MTP shard): loads cached `voice/out/*.dll` + runs `ExoLauncher.Tests` (4,353 tests) + fails on unexpected skips via a strict allowlist (only true hardware-only tests like `NvidiaProfiles` / `ControllerLabTests` / `MyVoiceTests` model-file allowed to skip on GH runners).
  - `addons-and-installer-tests`: runs `capture/ExoCapture.Tests` (86 tests), `shaders/engine/tests/ExoShaders.Service.Tests` (with `working-directory: apps/launcher/shaders`!), `voice/fetch.tests.ps1`, `tools/Test-InstallerPayload.ps1`, `tools/Test-UpdateRollback.ps1` (after fixing their temp-dir cleanup!), and `tools/Test-DependencySecurity.ps1`.
- Upgrade `.github/workflows/native-ui-ci.yml`:
  - Switch raw `target/` cache to `swatinem/rust-cache@v2` (or `sccache` + `mozilla-actions/sccache-action`), add `cargo fmt --check`, `cargo clippy --workspace --locked -- -D warnings`, `cargo test --workspace --locked`, and `cargo build --release --locked --bin exo-ui` (on `main` or PRs touching `native-ui`).
- Fix `AGENTS.md:72`, `exo.json`, and `exo_run_gates` (`shaders-test` cwd + add `capture`, `cloud`, `relay`, `tools` gate mapping + serialize `host-build` -> `host-test --no-build`).

## 3. Targets & Deliver

- **Targets:** 100% of repo test suites wired in CI (10/10 vs 4/10 today); **0** unexpected test skips in `ExoLauncher.Tests`; P95 PR wall-time `<6 min`; 100% offline/hermetic `voice/build.ps1` once cache is populated.
- Branch `lab/ci-sharding-and-hermetic-builds` from `origin/main`.
- `improve/ci-sharding-and-hermetic-builds-report.md`: before/after CI job graph, wall-time comparison, skip-count audit (all 22 `Assert.Skip`s classified), and `voice/` hermetic verification.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "CI sharding and hermetic builds results" --body "<suites added to CI, unexpected skips remaining, PR wall-time, voice hermetic status>"`. Effort: high.
