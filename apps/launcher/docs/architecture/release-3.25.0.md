# Exo Launcher 3.25.0: release record

Developer record of how 3.25.0 was prepared and what was and was not verified. The reader-facing release page is
`docs/RELEASE_3.25.0.md`, which `tools/Publish-Docs.ps1` writes (date and download only); this file is where the detail
lives, because the repo's rule is that the release page carries no notes.

## Published (2026-11-09)

- Live on `https://updates.getexo.app`: `/v1/launcher/latest` names `v3.25.0`; `publish-release.ps1` verified the served
  installer (size and SHA-256), `.sha256`, `whats-new.json` and the changelog, and the exo-updates Worker posted the Discord
  release card. getexo.app was rebuilt and deployed (exo-site `c393ce2`): the download and Launcher pages say 3.25.0 and
  `/download/launcher` redirects to the 3.25.0 installer.
- Before the manifest flipped: `cache-mirror.mjs` uploaded the TitaNet voice model and its licence to the cache (61 other
  objects were already there), `source-mirror.mjs` re-checked the four third-party GPL archives, and `addon-source.mjs`
  put Exo Mouse, Exo Shaders, Exo Shield (3.25.0) and OBS Studio 32.2.2 source in `exo-source`; `/source/index.json` lists
  all eight.
- Roll back: put the saved 3.24.4 manifest back as `exo-releases/launcher/latest.json`
  (`Exo\misc\release-3.25.0-rollback\prev-latest.json`).
- Not done before publishing: no installer run, no on-screen check of the installed build, no E2E run, and the full
  `dotnet test` suite was not re-run after the UI-pass merge (Build-Release's own gate and the UI gates were).

## Built from

- Installer commit: `4f98a8d7420db70290de67937f4cb9c937d93574` on `release/3.25.0` (the merge of the 3.25 UI pass; the
  exe's ProductVersion reads `3.25.0+4f98a8d7...`), tagged `built/v3.25.0`. Later commits touch only `docs/` and
  `cloud/updates/cache-licences.json`, which are not in the payload. An earlier build from `57188d75` (212,225,725 bytes,
  SHA-256 `f9f86261...ab5d9`) was never published.
- `artifacts\ExoLauncher-Setup.exe`: 212,232,545 bytes, SHA-256
  `a0bceedb9905a9952420adc9267634c3d7da9c3515f6193907ba8081f12c4d6f`; beside it `ExoLauncher-Setup.exe.sha256` (87 bytes) and
  `whats-new.json` (1,349 bytes, version 3.25.0, six items); `CHANGELOG.md` copied in for the update address.
- After the UI-pass merge: `npm run check` in `ui/` (120 tests), `npm run dead` and `npm run build` pass; Build-Release's
  own gate passed. The table below is from before that merge.
- ExoLauncher.exe: FileVersion 3.25.0.0. Exo Shaders engine from exo-shaders `af4f4f645ff9` (product version 1.4.6-exo.1).
  Lucid 6.0.4.0 from the pinned local copy. No update-signing keys exist, so the release is unsigned and the update address
  and the app check size and SHA-256 only (owner rule: no keys).
- Source archives for the copyleft add-ons: `artifacts\source\` (see `cloud/updates/README.md`, `addon-source.mjs`).

## What ran

| Gate | Result |
| --- | --- |
| `dotnet build ExoLauncher.csproj -c Release -p:Platform=x64` | 0 warnings, 0 errors |
| `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (twice, before and after the last source changes) | 6,140 total, 6,137 passed, 3 skipped (a connected pad, the learned-voice model, the FPS preview PNG), 0 failed |
| Build-Release's own gate (telemetry, consent, network-destination tests) | 72 passed |
| `npm run check` in `ui/` (contracts, ESLint, CSS design, icons, CSS prune, 119 tests) | pass |
| `npm run dead` in `ui/` | pass |
| `npm run build` in `ui/` (inside Build-Release, builds `mouse/` too) | pass |
| `npm run check` in `mouse/` (tsc, ESLint, CSS design, 18 tests) | pass |
| `cloud/updates` `npm test` (workerd against a fake upstream) | 129 passed after this release's changes |
| `tools\Test-InstallerPayload.ps1`, `tools\Test-UpdateRollback.ps1` | pass |
| `voice\fetch.tests.ps1` (from the merged branch) | all pass |
| `tools\Publish-Docs.ps1` | docs agree on 3.25.0 (November 9, 2026) |

## What did not run

- `node tools\e2e\run.mjs`: not run. By default it closes and restarts the installed Exo and saves/restores its files. The
  test-instance mode (`EXO_E2E_TEST_INSTANCE=1`) needs a Debug build, a fresh data folder (first run asks for an Exo account,
  which would create a real account on the account service) and drives real mouse input on the second screen.
- Nothing was seen on screen in this pass: no installer run, no swap into `%LOCALAPPDATA%\ExoLauncher`, no screenshots.
  27 of 28 CHANGELOG entries are `Checked in code only` and one is `Verified: on screen` (Alt+Tab); none is end-to-end.
- `tools\Test-SecretHistory.ps1` (downloads gitleaks from GitHub) and `tools\Test-DependencySecurity.ps1` (queries NuGet's
  vulnerability feed): not run, they need the network.
- `security\attacks\local\run-all.ps1` and `files\Run-Attacks.ps1`: not run (the unit suite covers the same predicates).
- `shell.precompiled` (ReadyToRun) and the memory measurement: need the installed exe.

## Merged and committed

`fix/3.25-voice-offgithub` (6 commits: the voice build reads every source from Exo's cache, pinned by size and SHA-256;
build scripts only, the voice DLLs were already built and are not rebuilt by a release). Then: line-ending normalisation of
two files, the CHANGELOG date and two missing entries (Windows 11 requirement, automatic upscaler updates), the release page
and docs check, the TitaNet cache pin and source archives for Exo's add-ons, GitHub pointers in the shipped licence and
reader docs, `Publish-Docs.ps1` without a public commit, and AGENTS.md.

## Known issues and risks

- Voice build inputs under `cache/exo/voice/...`, the meson wheel (`files.pythonhosted.org/...`) and the OpenSSL, abseil and
  wrapdb objects are read from Exo's cache by `voice\build.ps1`, but no script in the repo uploads them. Shipping 3.25.0 does
  not need them (the DLLs are built); rebuilding the voice libraries does. vcpkg itself still clones GitHub.
- Still contacting GitHub on purpose, listed in PRIVACY.md: Exo Shaders' compression libraries and Lucid's own update check.
  `tools\Get-Lucid.ps1` and `tools\Test-SecretHistory.ps1` download from GitHub at build time.
- The GitHub copy of Exo Capture's source up to 3.17.1 (`github.com/UhhErix/exo-capture`) is named in LICENSE and
  capture/LICENSE; it has no archive at getexo.app/source yet.
