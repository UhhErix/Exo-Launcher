# Spec 08 — Exo GPU Control (`exo-gpu`): Unified Native Rust NVAPI/ADLX/IGCL, Auto-Undervolt & Clean Driver Installer

- **Inventory Rank:** #08 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** **NVIDIA Profile Inspector (NVPI)**, **MSI Afterburner** OC/V-F curve editor, **NVCleanstall**, and ~35 C# GPU files (`Helpers/Nvidia*.cs`, `AmdAdlx*.cs`, `IntelIgcl*.cs`, `Nvml.cs`, `Services/NvidiaDriverService.cs`, `GpuManualTuningService.cs`, `GpuUndervoltSearch.cs`, `AutoTune.cs`)
- **Anti-Cheat Class:** `AC-SAFE` (100% official vendor signed user-mode DLLs `nvapi64.dll`, `nvml.dll`, `amdadlx64.dll`, `ControlLib.dll` + official Authenticode-signed vendor driver installers — zero game injection!)
- **License:** MIT (clean-room Rust dynamic FFI wrappers; calls OS-installed vendor DLLs)

---

## 1. Scope

### What It Solves
1. Exo already has strong C# prototypes for NVAPI DRS, ADLX, IGCL, NVCleanstall-style stripped installs (`NvidiaDriverService.cs` using `tar.exe`), and stepped undervolt search (`GpuUndervoltSearch.cs`), **but** they are fragmented across ~35 C# files, shell out to `tar.exe` and `cmd.exe`, require manual user triggers, and lack unified closed-loop V/F curve optimization across NVIDIA, AMD, and Intel.
2. Gamers still juggle **3 separate external apps** (NVPI + MSI Afterburner + NVCleanstall) that fight each other, lose profiles after every GPU driver update, and risk crashes when manually tweaking voltage curves.

### In Scope
- **Unified Multi-Vendor Rust Control Crate (`exo-gpu`):**
  - **NVIDIA (`nvapi.rs` + `nvml.rs`):** Full DRS global & per-game profile read/write (ReBAR, Low Latency/Reflex mode, Power Management, Texture Filtering, Shader Cache Size 10 GB/Unlimited, VRR LFC, G-SYNC, Frame Cap, NGX DLSS override) + NVML clocks/power limit/temp limit + V/F curve offsets.
  - **AMD (`adlx.rs`):** Full ADLX tuning (Anti-Lag, Radeon Chill, Enhanced Sync, AFMF, voltage offset mV, min/max core clock, VRAM clock, power limit %, Smart Access Memory status).
  - **Intel (`igcl.rs`):** Full Intel Arc IGCL tuning (Low Latency, XeSS override, power limit, voltage offset, frequency offset, Adaptive Sync).
- **Native Pure-Rust Clean Driver Builder & Installer (`driver_clean.rs`):**
  - Drops `tar.exe` subprocess! Uses native Rust 7z/SFX streaming extractor (`sevenz-rust2`) to extract **only** minimal required components (`Display.Driver`, `NVI2`, optional `NVPCF` for laptops, optional `HDAudio` only when HDMI/DP audio is active — zero NVIDIA App/Telemetry/GeForce Experience bloat!).
  - Checks Exo's signed `DriverAdvisories.json` first (automatically warns/blocks drivers known to have black-screen or game-crash regressions!).
  - Snapshots monitor layout, refresh rates, HDR state, and DRS profiles before install → runs clean install → re-applies MSI interrupt mode & restores display/DRS settings automatically!
- **Automated Crash-Proof GPU Undervolt Optimizer (`undervolt.rs`):**
  - Runs Exo's built-in DX12 compute + shader CRC stress verifier (`exo-stress`) while stepping V/F curve offsets safely within strict hardware limits (`TuneLimits`), watching for TDR resets (`Display` event 4101), WHEA errors, thermal limit (`82 °C`), or CRC mismatch.
  - Automatically backs off **2 safety steps** below first instability, persists a boot-crash watchdog (`TunePersistence` 10-min crash window auto-pauses re-apply if BSOD/Kernel-Power 41 occurred!), and records measured before/after Watts, °C, and clock MHz.

### Out of Scope
- Modifying GPU VBIOS firmware or bypassing vendor hardware voltage/power ceilings.
- Writing to the owner's real GPU clocks/voltages during automated agent tests (all tests use `MockGpuBackend`!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-gpu/
├── Cargo.toml                  # windows (WinVerifyTrust, Dxgi, Registry, EventLog), libloading, sevenz-rust2, serde
├── src/
│   ├── lib.rs                  # Trait GpuBackend + vendor auto-discovery + MockGpuBackend
│   ├── nvidia_drs.rs           # Dynamic nvapi64.dll DRS session/profile/setting R/W (replaces NVPI!)
│   ├── nvidia_nvml.rs          # Dynamic nvml.dll power/clock/V-F curve/thermal control
│   ├── amd_adlx.rs             # Dynamic amdadlx64.dll 3D settings + manual tuning + AFMF
│   ├── intel_igcl.rs           # Dynamic ControlLib.dll 3D settings + Arc tuning
│   ├── driver_feed.rs          # Official NVIDIA/AMD/Intel version check + signed DriverAdvisories guard
│   ├── driver_install.rs       # Pure-Rust SFX minimal component unpack + Authenticode verify + pre/post restore
│   ├── undervolt.rs            # Closed-loop stepped V/F curve search + 2-step backoff + crash-window guard
│   └── profiles.rs             # Per-game driver profile engine (ReBAR, Shader Cache, Low Latency, VRR)
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config Best Driver Profile:** On first run (and automatically after every GPU driver update!), applies verified optimal global settings (Shader Cache ≥10 GB, VRR LFC fix, ReBAR allowlist, no forced V-Sync) and restores every per-game DRS/ADLX/IGCL profile automatically.
2. **One-Click Self-Verifying Undervolt:** User clicks **"Auto-Undervolt"** once (no Afterburner curve graph wrestling!) → Exo finds the exact silent sweet spot for *this silicon*, proves stability via CRC-checked DX12 load, backs off 2 steps for 100% margin, and shows verified **Before vs After Watts / °C / Boost MHz**.
3. **Per-Game Power & Efficiency Switching:** Automatically applies quiet/undervolted profile for indie/competitive games (where GPU sits at 40% load) and full-boost undervolt profile for AAA titles, restoring stock clocks immediately on exit or crash detection.
4. **Driver Regression Shield:** Automatically checks `DriverAdvisories.json` before notifying of a new driver — skips broken releases instead of breaking the user's games.

---

## 4. Measurement Plan

Protocol/logic/rollback tested via `MockGpuBackend`; read-only capability query & pure-Rust SFX unpack benchmarked safely on this RTX 3070 PC:

| Metric | Baseline (Current C# + `tar.exe` + Afterburner + NVPI) | Target (`exo-gpu`) | How Measured |
| --- | --- | --- | --- |
| **External Tools Replaced** | 3 (NVPI + Afterburner + NVCleanstall) | **0** (all native in `exo-gpu`) | Feature parity matrix |
| **DRS Profile Read/Write Latency** | C# P/Invoke marshaling (~15–40 ms) | **<5 ms** direct Rust FFI | Read-only benchmark on NVAPI DRS read |
| **Driver SFX Minimal Unpack** | `tar.exe` external subprocess | Pure-Rust streaming unpack (0 subprocesses, 0 `cmd.exe`) | Timed unpack of sample 7z/SFX fixture |
| **Crash-Window Boot Guard Reliability** | C# `TunePersistence` | **100%** auto-pause on simulated Kernel-Power 41 / WHEA / TDR within 10 min | Automated unit & fault-injection suite |
| **Undervolt Thermal/Power Win (User Opt-In Real Run)** | Stock V/F curve | Typical **-15% to -25% W / -5 to -10 °C** at ≥stock boost *(hardware-dependent, measured live per PC)* | Built-in `before` vs `after` telemetry report |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-gpu --locked -- -D warnings`, `cargo test -p exo-gpu --locked`.
2. **Limits & Watchdog Exhaustive Tests:** Verify `TuneLimits` rejects any out-of-range MHz/mV value, always backs off 2 steps below failure, and pauses re-apply after any simulated Kernel-Power 41 / BugCheck 1001 / WHEA / TDR event.
3. **Authenticode & Path-Traversal Gate:** Driver unpacker rejects any unsigned installer or archive entry attempting `../` path traversal.
4. **Read-Only Hardware Probe Test:** On RTX 3070, read-only NVAPI/NVML capability query succeeds cleanly with 0 writes.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER MODIFY OWNER'S LIVE GPU CLOCKS/VOLTAGES/DRIVERS IN AGENT SESSIONS:** All automated lab & CI tests run strictly against `MockGpuBackend` or read-only sensor/profile queries!
- **Strict Hardware Clamping:** Hard-enforce vendor ceilings (`TuneLimits`); abort immediately on GPU temp `>= 82 °C`, any WHEA event, or any DX12 device-lost/CRC error, and immediately call `restore_stock()`.
- **No Unofficial Drivers:** Download and unpack ONLY Authenticode-signed packages from official NVIDIA/AMD/Intel domains (`NetworkDestinations`).
- **Power-Loss / Bootloop Proof:** Never persist an untested tuning step across reboot; mark every test step as *unverified* on disk BEFORE applying so a power loss automatically rolls back to stock on next boot.
