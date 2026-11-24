EXO LAB: Self-Verifying Release Bot, Authenticode Signing & Canary Rollout Gate (task-name: self-verifying-release-bot)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... the pipelines, the flow, the technology ... every little nook and cranny, to see if we can make something better, more experimental, not seen before."
> Vision: zero-touch, cryptographically signed, self-verifying releases with automatic canary crash-rate protection — no manual dev-laptop builds, no branch drift, no bad releases ever reaching 100% of players.

## Today (Audited in `tools/Build-Release.ps1`, `RELEASES.md`, `cloud/updates/`, `origin/release/3.30`)

- **Manual single-machine release script:** `tools/Build-Release.ps1` (189 lines) runs locally and serially on one dev PC:
  - Runs ONLY **4 test filters** (`TelemetryTests|CrashSendTests|AppsConfirmReasonTests|NetworkDestinationsTests`), `ui` tests, and `native-ui` tests — skips the rest of `ExoLauncher.Tests` (4,353 tests), `mouse`, `capture`, `shaders`, `cloud`, `relay`, and `tools/e2e`!
  - **Zero Authenticode code signing** (`RELEASES.md:72`: *"Releases are not code-signed today"* — every download hits Windows SmartScreen!).
  - Manual steps outside the script: `addon-source.mjs build` + `upload` to R2 `exo-source`, `Publish-Docs.ps1`, editing `RELEASES.md` checksum table, creating GitHub release, uploading assets.
  - Branch drift: `origin/release/3.30` has diverged **30 vs 36 commits** from `origin/main` with manual cherry-picks.
- **No staged rollout / automatic halt:** `cloud/updates` (`exo-updates` Worker) promotes every new GitHub release directly to **100%** of players within 5 minutes; there is no canary ring and no link to `cloud/reports` crash-rate spikes.

## 1. Reproducible Parallel CI Release Pipeline (`.github/workflows/release.yml`)

- Triggered on `workflow_dispatch` (dry-run or release candidate) or signed tag `vX.Y.Z`:
  1. **Parallel Build Fan-Out:** Builds `ui + mouse`, `exo-ui.exe` (with PGO), `ExoLauncher` + `SteamIpc` (clean Release publish), `ExoShaders.Service` (from committed `shaders/` rev, verified `ExoBuild.Enabled == true`), `ExoCapture` (NativeAOT), and `voice/` (from pinned cache) concurrently in `<12 min` instead of serial local build.
  2. **Deterministic Packaging & Compliance:** Produces installer (`ExoLauncher-Setup.exe`), `files.json` + `files.pack` (plus schema v2 binary delta once ready), `whats-new.json`, GPL/AGPL add-on source archives (`exo-mouse`, `exo-shaders`, `exo-shield`, OBS), **CycloneDX SBOM**, and **SLSA Level 3 build provenance attestation** (`actions/attest-build-provenance`).
  3. **Authenticode Signing (Zero Static Secrets):** Integrate OIDC-authenticated **Azure Trusted Signing** (or **SignPath.io** free open-source tier for MIT repos) to sign `ExoLauncher.exe`, `exo-ui.exe`, `ExoCapture.exe`, `ExoLauncher.SteamIpc.exe`, `ExoShaders.Service.exe`, `ExoSetup.exe`, and `ExoLauncher-Setup.exe` with RFC 3161 timestamps.
  4. **Reproducibility Check:** Run a proof experiment comparing SHA-256 of `files.pack` + `ExoLauncher.dll` + `exo-ui.exe` across two clean independent builds from the same commit (eliminating timestamp/path nondeterminism via `Deterministic=true`, `ContinuousIntegrationBuild=true`, `--remap-path-prefix`).

## 2. Self-Verification Sandbox Stage (Before Publishing Live)

- Before promoting a release from Draft → Published (or into `exo-updates` live ring), an automated verifier job:
  1. Verifies all Authenticode signatures (`Get-AuthenticodeSignature` == `Valid`), verifies all voice/ONNX DLLs load cleanly (`NativeLibrary.TryLoad`), verifies `ExoBuild.Enabled == true`, verifies `payload-manifest.json` & `files.json` integrity, and verifies GPL/AGPL source archives build cleanly from their tarball!
  2. Runs an isolated **fresh install** (`EXO_TEST_ROOT`) AND an **N-1 → N upgrade simulation** (fetching the current live release's `files.json` from `updates.getexo.app`, staging an in-place delta update into an isolated root, validating startup receipt + rollback recovery + `app.log` clean).
  3. Runs a headless `exo-ui` + `ExoLauncher --host-pipe` live probe smoke check (connects pipe, runs `shell.bootstrap`, `library.get`, `settings.get`, verifies zero crashes/WARNs).
  4. Only if **every** check passes does it publish the release + auto-open the docs/checksum PR (`Publish-Docs.ps1` + `RELEASES.md`).

## 3. Experimental Canary Crash-Gate in `exo-updates` + `cloud/reports`

- Design & prototype a **Canary Rollout Controller** in `cloud/updates`:
  - `launcher/latest.json` carries an optional `rollout: { pct: 5 | 25 | 100, startedAt, pausedReason? }`.
  - Exo sends a deterministic bucket hash (e.g., first byte of `SHA256(installId + tag)` mod 100 — zero PII, still anonymous!) or evaluates bucket client-side from `latest.json`'s `pct`! (Evaluating client-side keeps `/v1/launcher/latest` 100% edge-cacheable!)
  - A scheduled cron in `cloud/updates` queries a read-only crash-rate summary from `cloud/reports` D1 (crashes per 1,000 `app.start` events for the new version vs previous version): if crash rate exceeds `2.0x` baseline (with minimum sample threshold `N >= 50` starts), it **automatically freezes `pct` at 5%** and posts a Discord alert! If healthy after 6 h → advances 5% → 25% → 100%.

## 4. Safety & Deliver

- **NEVER deploy Workers, NEVER publish a real GitHub release, NEVER touch production R2 or `%LOCALAPPDATA%\ExoLauncher`!** Test strictly via dry-run workflows, unit tests, and local fixtures!
- Branch `lab/self-verifying-release-bot` from `origin/main`.
- `improve/self-verifying-release-bot-report.md`: reproducible-build diff proof, parallel CI release architecture, signing provider comparison (Azure Trusted Signing vs SignPath OSS), self-verification check results, and canary crash-gate simulation tests.
- Working `.github/workflows/release-dry-run.yml`, `tools/Verify-ReleaseCandidate.ps1`, and unit-tested canary rollout logic in `cloud/updates` (not deployed).
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "self-verifying release bot results" --body "<reproducibility proof, verification checks built, canary crash-gate tests, signing recommendation>"`. Effort: high.
