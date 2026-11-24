# Exo Upscale (`exo-upscale`) — Spec 02 PR A Implementation & Measurement Report

## 1. Summary

Implemented **PR A (`exo-upscale`)** of Spec 02: a clean-room MIT Rust upscaler vault, local manifest generator, `WinVerifyTrust` + `CryptQueryObject` Authenticode publisher & PE FileVersion verifier, parallel library scanner, zero-file-touch NVIDIA DRS NGX override planner (with read-only NVAPI driver probe), atomic content-addressed backup & rollback ledger, and transactional `SessionMods` with crash-orphan cleanup, all governed strictly by the shared 3-class anti-cheat rule (`exo_rpc::anticheat::AntiCheatClass` / `ExoLauncher.Services.AntiCheatClass`).

*(PR B = `exo-fg-bridge` follows in the next phase, per coordinator phasing instructions.)*

## 2. Deliverables

- **Rust crate (`apps/launcher/native-ui/crates/exo-upscale/`):**
  - `src/policy.rs`: Shared 3-class anti-cheat enforcement using `exo_rpc::anticheat::AntiCheatClass` only:
    - `CompetitivePvpOnline` & `Unknown`: NO DLL swap, NO proxy; driver-level NVAPI DRS override allowed (`touchesGameFiles: false`).
    - `OnlineNonCompetitivePve`: config/DRS on by default; vendor-signed DLL swap (and future FG bridge) opt-in only behind explicit `BanRiskConfirmation`.
    - `OfflineAntiCheat`: allowed in official offline mode with `OfflineOneTimeNote`.
    - Clean no-AC: allowed without warnings.
  - `src/verify.rs`: Real Windows `WinVerifyTrust` (`WINTRUST_ACTION_GENERIC_VERIFY_V2`) + `CryptQueryObject` signer publisher verification (`NVIDIA Corporation`, `Advanced Micro Devices, Inc.`, `Intel Corporation`) + `GetFileVersionInfoW` PE `VS_FIXEDFILEINFO` parser + streaming BLAKE3 and SHA-256 hashing.
  - `src/catalog.rs`: Local content-addressed vault (`blobs/<blake3>/`, `backups/<blake3>/`, `manifest.json`) and local manifest generator (`generate_local_manifest`) pointing exclusively to official vendor SDK repositories (`NVIDIA/DLSS`, `GPUOpen-LibrariesAndSDKs/FidelityFX-SDK`, `intel/xess`) — zero R2 deployment and zero `dlss-swapper-downloads.beeradmoore.com` URLs.
  - `src/scanner.rs`: Parallel multi-threaded (`std::thread::scope`) game-library upscaler scanner (`nvngx_dlss*`, `amd_fidelityfx*`, `ffx_fsr2*`, `libxess*`).
  - `src/drs_override.rs`: Zero-file-touch NVIDIA DRS NGX override plan (`0x10E41E01`/`02`/`03`/`06`, `0x10E41DF3` Preset K = `11`, `0x10E41DF7` Preset E = `5`, `0x10E41DF1` Latest) + read-only `nvapi64.dll` driver probe (never writes to the owner's live GPU profiles).
  - `src/swap.rs`: Content-addressed atomic backup (`MoveFileExW` with `MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH`), BLAKE3 pre/post verification, instant rollback ledger, and 15-second crash auto-rollback policy.
  - `src/session.rs`: Transactional `SessionMods` with durable atomic JSON journal written before placement, `CreateToolhelp32Snapshot` PID liveness check (zero `OpenProcess` imports), and startup crash-orphan cleanup restoring originals byte-for-byte.
  - `src/main.rs`: CLI (`--bench`, `--inspect-drs`, `--verify`, `--generate-manifest`, `--recover-orphans`).
- **Shared RPC & C# Host Integration:**
  - `apps/launcher/native-ui/crates/exo-rpc/src/anticheat.rs`: Added `allows_driver_drs_override()` and `allows_signed_upscaler_swap(ban_risk_confirmed)`.
  - `apps/launcher/ExoLauncher/Services/ExoUpscaleService.cs`: Managed 3-class policy bridge using `ExoLauncher.Services.AntiCheatClass` + startup orphan-session recovery helper (zero banned native imports).
  - `apps/launcher/ExoLauncher.Tests/ExoUpscalePolicyTests.cs`: Exhaustive C# tests across all 4 `GameAntiCheatClass` classes + `Unknown`.

## 3. Measured Results (RTX 3070 Workstation)

Source artifact: [`improve/lab-exo-upscale/bench.json`](lab-exo-upscale/bench.json)

| Metric | Measured Value | Target / Notes |
|---|---|---|
| GPU / NVAPI Read-Only Probe | `NVIDIA GeForce RTX 3070`, driver `621.35` (`r621_20`), `supportsNgxOverride: true` | Read-only probe only; zero writes to live GPU/DRS |
| Parallel Library Scan (50 mock games, 63 DLLs) | **`143.48 ms`** | Target `< 150 ms` (**PASS**) |
| Real Signed DLL Verification (`nvapi64.dll`, 7+ MB) | `WinVerifyTrust: true`, Publisher `"NVIDIA Corporation"`, Version `32.0.16.2135`, cold first-call `308.57 ms` | Cold Windows cert-chain init + full BLAKE3/SHA-256 read |
| Authenticode 1-Byte Tamper Test | **`tampered1byteRejected: true`** | Flipping 1 byte immediately rejected by `WinVerifyTrust` |
| Crash-Orphan Cleanup (`TerminateProcess` child harness) | **`100%` recovery** (`1` placed proxy removed, `1` replaced DLL restored byte-for-byte, `0` errors) | Target `100%` (**PASS**) |
| Local Manifest Generator | **`localManifestIntegrityOk: true`** | Tested locally only; zero R2 mirror/deploy |
| `dumpbin /imports exo-upscale.exe` | **0** banned imports (`WriteProcessMemory`, `CreateRemoteThread`, `SetWindowsHookEx`, `VirtualAllocEx`, `OpenProcess`) | Only `wintrust.dll`, `crypt32.dll`, `version.dll`, `dxgi.dll`, standard Windows/CRT |

## 4. Verification Gates

| Gate | Command | Result |
|---|---|---|
| Rust format | `cargo fmt -p exo-upscale -p exo-rpc -- --check` | **PASS** (exit 0) |
| Rust clippy | `cargo clippy -p exo-upscale -p exo-rpc --locked -- -D warnings` | **PASS** (exit 0, 0 warnings) |
| Rust unit tests | `cargo test -p exo-upscale -p exo-rpc --locked` | **PASS** (`exo-upscale`: 18/18, `exo-rpc`: 30/30, 0 failed) |
| Rust release build | `cargo build --release --locked -p exo-upscale` | **PASS** (exit 0) |
| C# build + tests | `dotnet build ExoLauncher.Tests -c Release -p:Platform=x64` + `ExoUpscalePolicyTests`, `AntiCheatClassTests`, `BannedNativeImportsTests` | **PASS** (0 warnings, 25/25 passed) |
| Zero-comment ratchet | `node tools/check-comments.mjs` | **PASS** (exit 0) |
| Zero-injection import audit | `dumpbin /imports exo-upscale.exe` | **PASS** (0 banned imports) |

## 5. Next Phase (PR B)

- **PR B (`exo-fg-bridge`)**: Clean-room MIT DX12 `exo_fg_bridge.dll` routing DLSS-G calls to official FSR 3.1 FG (`amd_fidelityfx_dx12.dll`) on RTX 20/30 (replacing `dlssg_for_sm86`), verified on the RTX 3070 in a no-AC game once an unlocked interactive session is available.
