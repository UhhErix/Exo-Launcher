EXO LAB: Unified Signed Rust Installer & FastCDC Sub-File Binary Delta Updates (task-name: unified-rust-installer-delta)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "Improve any feature to impossible lengths. Try anything. Make new technology and software."
> Vision: instant, trustworthy, signed installs (<4 s) and tiny sub-megabyte point-release updates (<600 KB) over Exo's existing R2 mirror.

## Today (Measured at `e0289d9`)

- Installing Exo currently chains **4 different runtimes**:
  1. 640-line C++ WebView2 wrapper (`setup/src/main.cpp`) packing a Vite React UI (`ui.pack`);
  2. Copies a **137.02 MB** appended NSIS binary (`tools/ExoLauncher.nsi`, 1,012 lines, single-threaded `/SOLID lzma` 64 MB dict, 500+ lines of dead GDI UI) out of itself to `%TEMP%\...\engine.exe` — **double-writing ~520 MB** on every install (`setup/src/main.cpp:130-145`);
  3. Spawns NSIS silently while polling `FolderBytes()` recursively via `FindFirstFileExW`;
  4. Spawns 5 PowerShell scripts (`Apply-ExoDelta.ps1`, `Complete-ExoUpdate.ps1`, `Verify-Payload.ps1`, `Close-ExoLauncher.ps1`, `Clean-ExoInstall.ps1`) for updates/swaps/cleanup.
- **Authenticode signing blocker:** `setup/build.ps1:55-66` appends raw `[engine][u64][u64]["EXOSETUP"]` bytes after the PE image, which breaks standard PE Authenticode signing / triggers SmartScreen warnings.
- **File-level delta waste:** `files.pack` (`ExoLauncher/Services/AppFiles.cs`) is file-level only — changing 1 line in C# or Rust forces every player to re-download the entire compressed `ExoLauncher.dll` (30 MB raw) or `exo-ui.exe` (20.7 MB raw) (~12–20 MB per hotfix!).
- **Pack builder RAM spike:** `AppFiles.cs:91-97` constructs `new Compressor(19)` inside `Parallel.For` for EVERY SINGLE FILE (1,039 level-19 zstd contexts!) and holds every compressed `byte[]` in RAM simultaneously.

## 1. Benchmark & Proof Experiment First (<2 Days)

- Fix `AppFiles.cs:91-97` thread-local `Compressor` + streaming pack write as an immediate baseline measurement (time & peak RAM on the 380 MB app folder).
- Prototype **FastCDC (content-defined chunking, 16–64 KB avg chunk) + `zstd --patch-from` binary delta** between two real builds of `ExoLauncher.dll` (30 MB) and `exo-ui.exe` (20.7 MB) with a 10-line code edit:
  - Measure exact delta download bytes (target: **<600 KB** vs ~15–20 MB file-level delta / 137 MB full installer), reconstruction speed (MB/s), and SHA-256 bit-identity against the target build!
- Benchmark single-threaded NSIS LZMA vs multi-threaded zstd streaming extraction of the 380 MB app folder on this PC.

## 2. Unified Rust Installer & Updater Engine (`setup-v2` / `exo-setup.exe`)

- Design one self-contained Rust binary (`exo-setup.exe`, `<3 MB` overhead, `panic=abort`, thin LTO) that handles **install, repair, delta update, atomic swap, receipt verification, rollback, and uninstall** with **zero NSIS and zero PowerShell**:
  1. **Streaming Payload (Zero Temp Copy):** Embeds a clean PE resource (or verified companion `files.pack` / streaming manifest) with multi-threaded zstd decompression directly into `app.incoming-<uuid>`, preserving clean PE geometry so `signtool` / Azure Trusted Signing signs cleanly with zero overlay corruption.
  2. **Sub-File Delta Updates over Existing R2 Mirror:** Extends `files.json` to schema v2 (keeping v1 fallback for older installs!) with chunk index / `zstd --patch-from` delta ranges served via the exact same `/v1/launcher/download/<tag>/` HTTP range requests in `cloud/updates`.
  3. **Built-in Atomic Swap & Rollback:** Ports all invariants from `Verify-Payload.ps1`, `Close-ExoLauncher.ps1`, `Apply-ExoDelta.ps1`, and `Complete-ExoUpdate.ps1` directly into Rust (hardlinks for unchanged files, reparse-point rejection, owned WebView2/Exo close without force-kill, nonce startup receipt check, automatic rollback if new build fails to start).
  4. **UI Shell:** Supports both silent `/S` / `/UPDATE` mode and a lightweight native Rune (or minimal Win32/WebView2) progress window using Goeist + Void black tokens.

## 3. Safety & Verification

- Never install over `%LOCALAPPDATA%\ExoLauncher\app`! Test strictly inside isolated `%TEMP%` roots (`EXO_TEST_ROOT`)!
- Port and pass every single test case from `tools/Test-InstallerPayload.ps1` (corrupted byte, missing DLL, unexpected DLL, apostrophe paths, reparse-point escape, owned WebView profile matching) and `tools/Test-UpdateRollback.ps1` (receipt nonce/pid/version mismatch, locked previous folder rollback, user data preservation).
- Verify clean Authenticode PE structure (`signtool` / self-signed test cert verification passes with valid PE checksum and zero trailing unsigned overlay).

## 4. Deliver

- Branch `lab/unified-rust-installer-delta` from `origin/main`.
- `improve/unified-rust-installer-delta-report.md`: FastCDC + `zstd --patch-from` delta sizes vs `files.pack` vs NSIS, install wall-time before/after, `AppFiles.Build` peak RAM before/after, PE signing check, and full rollback/payload fixture results.
- Working `exo-setup` crate + schema v2 pack/patch tool + passing fixture test suite.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "unified Rust installer and delta results" --body "<point-release delta KB vs old MB, install seconds before/after, PE sign check, fixture test status>"`. Effort: high.
