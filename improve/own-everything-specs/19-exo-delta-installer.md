# Spec 19 — Exo Native Installer & Delta Updater (`exo-installer` + `exo-delta`): Single Rust Binary Replacing NSIS, ExoSetup & PowerShell Scripts

- **Inventory Rank:** #19 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** **NSIS (`makensis.exe` + `tools/ExoLauncher.nsi`)**, C++/CMake/WebView2 **`setup/ExoSetup.exe`**, C# **`tools/AppFiles`** (`files.pack` file-level zstd), **5 embedded PowerShell update scripts** (`tools/installer/*.ps1`), `ZstdSharp.Port` NuGet, AND unused **`Velopack`** inside Shaders (`Services/AppUpdateService*.cs`, `Services/AppFiles.cs`)
- **Anti-Cheat Class:** `AC-SAFE` (user-space `%LOCALAPPDATA%\ExoLauncher` installer/updater — zero elevation needed!)
- **License:** MIT (clean-room Rust; directly graduates proven **`lab/own-delta-pack`** `4cc76e5`!)

---

## 1. Scope

### What It Solves
1. Today installing or updating Exo requires a Rube Goldberg chain of 5 different technologies: C++/CMake/WebView2 `ExoSetup.exe` wrapping an **NSIS** LZMA self-extractor (`ExoLauncher-Engine.exe`), which extracts an app payload, PLUS `AppFiles.csproj` generating file-level `files.pack` (**19.68 MB** for a real Exo release diff measured on this PC!), PLUS **5 embedded PowerShell scripts** (`Apply-ExoDelta.ps1`, `Complete-ExoUpdate.ps1`, `Verify-Payload.ps1`, `Close-ExoLauncher.ps1`, `Clean-ExoInstall.ps1`) spawned via `powershell.exe` to swap files!
2. Real benchmarks on this PC (`lab/own-delta-pack` `4cc76e5` against real `app.previous-dev` → `app` across all `1,039` files / `380.02 MB`) ALREADY proved that our pure-Rust **`exo-delta`** zstd prefix-dictionary + BLAKE3 binary delta shrinks real updates by **83.3%** (`19.68 MB → 3.28 MB`) and applies + cryptographically verifies all 380 MB in **127 ms**!

### In Scope
- **`exo-delta` (Binary Delta Builder & Verifier — Graduated from `lab/own-delta-pack`):**
  - Uses previous-release files as zstd prefix dictionaries (`ZSTD_CCtx_refPrefix`) for changed binaries (`ExoLauncher.dll`, `exo-ui.exe`, etc.), raw zstd for brand-new files, and hardlink/copy-reuse for unchanged files.
  - Streaming **BLAKE3** full-tree manifest (`manifest.bin` signed with Exo's Ed25519 release key!) verifying every single byte of the resulting install tree before swap.
- **`exo-installer` (Single `<1.5 MB` Native Rust Setup & Uninstaller — Zero NSIS, Zero WebView2, Zero PowerShell!):**
  - Lightweight native Win32/Direct2D window rendering Exo's exact Void `#000` + Goeist design (one Install screen, live progress bar, Launch Exo) — **zero WebView2 spawn**, **<30 ms** cold open!
  - Handles fresh install (embedded zstd-compressed payload OR ultra-tiny `<1.5 MB` web-bootstrapper fetching signed payload from `updates.getexo.app`), silent in-app update (`/update`), atomic NTFS staging swap (`app.incoming-<id>` → `app`, keeping `app.previous-<id>` rollback), Start Menu / Desktop shortcuts (`IShellLinkW` with `AppUserModelId = ImAvgErix.ExoLauncher`), `HKCU\...\Uninstall\ExoLauncher`, and clean full uninstall!
- **Automatic Crash-Health Rollback (`UpdateStartupHealth`):**
  - After an update swap, watches the first 30 seconds of launch; if the new version crashes twice before reaching healthy UI ready, `exo-installer` **automatically swaps `app.previous` back in `<50 ms`** so an update can *never* brick a user's launcher!

### Out of Scope
- Machine-wide `Program Files` / admin UAC installs (Exo stays clean per-user `%LOCALAPPDATA%\ExoLauncher`, requiring zero UAC!).
- Modifying or running against the owner's real `%LOCALAPPDATA%\ExoLauncher` during development/lab tests (always use isolated temp test roots!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-delta/               # Already proven in lab/own-delta-pack (4cc76e5)!
├── Cargo.toml                  # zstd-safe / zstd, blake3, ed25519-dalek, rayon, serde
├── src/
│   ├── lib.rs                  # DeltaManifest, Ed25519 signature verify, streaming BLAKE3 tree hash
│   ├── pack.rs                 # Release builder: old_dir + new_dir -> delta.exopack + full.exopack + manifest.sig
│   └── apply.rs                # Atomic staging apply: hardlink unchanged + zstd prefix-dict patch changed + verify

crates/exo-installer/           # Compiles to ExoLauncher-Setup.exe (<1.5 MB + optional embedded payload)
├── Cargo.toml                  # exo-delta, windows (Direct2D, Shell, Registry, WinHttp), blake3
├── src/
│   ├── main.rs                 # CLI modes: GUI install, --update <pkg>, --uninstall, --rollback, --test-root <dir>
│   ├── ui_d2d.rs               # Pure Direct2D + Goeist Void #000 installer window (0 WebView2, <30 ms open!)
│   ├── swap.rs                 # Graceful close signal -> atomic NTFS dir swap -> health marker -> rollback
│   ├── shell_reg.rs            # Pure Win32 IShellLinkW (AUMID) + HKCU Uninstall/AppPaths registration
│   └── fetch.rs                # Resumable HTTPS download from updates.getexo.app with SHA-256 + Ed25519 check
```

---

## 3. Automation & Intelligence Design

1. **Invisible Background Updates:** While PC is idle and no game is running, Exo silently fetches the tiny **~3–4 MB** `exo-delta` patch, verifies Ed25519 + BLAKE3 in background, stages `app.incoming` via NTFS hardlinks (taking `<10 MB` extra disk!), and completes the atomic swap in **`~127 ms`** on next open/close!
2. **Zero-Downtime Crash Rollback:** Automatically detects bootloop/crash on a newly updated build and restores the exact previous working build without user intervention.
3. **One-Click Clean Uninstall:** Removes app, shortcuts, registry keys, and optional player data cleanly without leaving orphaned PowerShell temp files or broken shortcuts.

---

## 4. Measurement Plan

Already benchmarked on this exact PC (`lab/own-delta-pack` `4cc76e5`) against a real 1,039-file / 380.02 MB Exo Launcher release diff, plus new isolated-root installer/rollback tests:

| Metric | Baseline (Measured: NSIS + `ExoSetup` + `files.pack` + PS1) | Target / Measured (`exo-delta` + `exo-installer`) | How Measured |
| --- | --- | --- | --- |
| **Real Release Update Download Size** | **19.68 MB** (`files.pack` L19) / **21.95 MB** (L12) | **3.28 MB** (L19, **-83.3% / 6.0× smaller!**) · **4.12 MB** (L12, **4.95 s** build) *(already measured!)* | `lab/own-delta-pack` on real Exo diff |
| **Client Delta Apply + Full 380 MB Verify** | PowerShell + SHA-256 (~1.5–4 s) | **127 ms** (L19) / **280 ms** (L12) *(already measured!)* | QPC timer in `exo-delta apply` |
| **Setup Window Cold Open** | WebView2 `ExoSetup.exe` (~450–900 ms) | **<30 ms** Direct2D native window, **<1.5 MB** stub | QPC timer to first D2D paint |
| **External Packaging Dependencies** | NSIS + CMake/C++ + WebView2 + 5 PS1 scripts + `ZstdSharp` | **0** (100% Rust) | Release script audit |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-delta -p exo-installer --locked -- -D warnings`, `cargo test --locked`.
2. **Ed25519 & Tamper Gate:** Flip 1 bit in manifest signature, 1 bit in delta payload, or 1 bit in a base file → `apply.rs` MUST abort cleanly and leave the active `app/` folder 100% untouched!
3. **Isolated-Root Full Lifecycle E2E Test (`--test-root %TEMP%\exo-inst-test`):** Fresh install → launch mock exe → background delta update → atomic swap → simulated crash on v2 → automatic rollback to v1 → clean uninstall (0 leftover files/reg keys!).
4. **Path-Traversal & Symlink Escape Gate:** Reject any manifest path containing `..`, drive letters, or reparse points outside staging dir.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER TOUCH OWNER'S REAL `%LOCALAPPDATA%\ExoLauncher`:** Every lab/CI installer test MUST pass `--test-root <temp>` and use isolated HKCU test keys (`Software\ExoLauncherTest`) — NEVER overwrite or kill the owner's installed Exo!
- **Cryptographic Release Trust:** Never apply any downloaded delta or installer unless both Ed25519 signature AND full BLAKE3 post-apply tree hash succeed.
- **No Force-Killing Processes:** Close Exo gracefully via named-pipe/WM_CLOSE (matching `Close-ExoLauncher.ps1`); never kill unrelated or anti-cheat processes.
- **Atomic Rollback Guarantee:** Keep `app.previous` intact until the new version reports `healthy` after startup.
