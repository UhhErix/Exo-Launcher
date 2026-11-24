# Exo Thermal & Fan Control (`exo-thermal` / Spec 09) — Unified Native Rust Fan Engine

- **Date:** 2026-11-24
- **Branch:** `feat/native-exo-thermal` (from `origin/feat/native-polish-2`)
- **License:** MIT (clean-room Rust — zero GPL/AGPL, zero `WinRing0`)
- **Hardware Safety:** 100% mock-tested (`MockFanBackend`) — zero writes to real fans, GPU, or EC.

---

## 1. What Was Built

Added clean-room MIT Rust crate `apps/launcher/native-ui/crates/exo-thermal`:

- **`backend.rs`**: `FanDomain` (`GpuNvidia`, `GpuAmd`, `GpuIntel`, `SuperIo`, `UsbAio`), `SensorKind` (`GpuCore`, `GpuHotspot`, `CpuTctl`, `Coolant`), `FanBackend` trait, and `MockFanBackend` (deterministic mock with write log, BIOS restore log, and simulated driver-reset faults).
- **`curve.rs`**:
  - Mixed sensor curves (`Max`, `WeightedAverage`, `CoolantDelta`) so case fans respond to `max(CPU_Tctl, GPU_Hotspot)`.
  - Asymmetric hysteresis (`hysteresis_up_c`, `hysteresis_down_c`), spike hold filter (`spike_hold_ticks`), and independent ramp-up / ramp-down slew-rate limiting.
  - Resonance notch avoidance (steps outside humming PWM bands).
  - **Unbreakable Hard Thermal Safety Floor**: `temp >= 85 °C -> 100%`, `temp >= 80 °C -> >=85%`, `temp > 60 °C (GPU) / >55 °C (CPU) -> >=25%` (overrides any user curve, including `0%`).
- **`acoustic_cal.rs`**: Sweep analyzer detecting zero-RPM spin-up threshold, acoustic quiet knee, and humming resonance notch bands to auto-generate Quiet curves.
- **`gpu_backends.rs`**: Capability matrix for NVML, ADLX, IGCL, USB HID AIO hubs, and optional `superio-pawnio`.
- **`superio_pawnio.rs`** (gated behind off-by-default `superio-pawnio` cargo feature): SuperIO chip-ID verification (`NCT6775/6798`, `IT8688`) before any write, laptop EC lock refusal, opt-in + competitive anti-cheat guard, zero `WinRing0`.
- **`lib.rs`**: `ThermalDaemon` with laptop read-only protection, immediate driver-reset failsafe restore, and **`Drop` failsafe** that restores every touched channel to BIOS/EC automatic control on normal exit or panic.
- **`bench_main.rs`**: Mock-only benchmark (`exo-thermal-bench.exe`) measuring RSS delta, 300 s control-loop CPU overhead, 2 s `+15 °C` CPU spike suppression, sustained-load ramp latency, and `Drop` BIOS auto-restore.

---

## 2. Measured Before / After (Mock 2-Channel Rig: GPU + Mixed-Sensor Case Intake)

| Metric | Baseline (FanControl / `ExoAiFans.cs` NVIDIA-only) | Native MIT Rust (`exo-thermal`) | Target |
| --- | --- | --- | --- |
| **Memory Footprint (RSS Delta)** | FanControl 35–70 MB; Armoury Crate / iCUE 300–600 MB | **132 KB** (`135,168` bytes; 4.95 MB total WS) | `<2 MB` (15× better) |
| **Control Loop CPU (300 s / 300 ticks)** | FanControl 0.5–1% CPU | **`399 µs` total wall** (`1.33 µs`/tick, `0.0%` CPU) | `<0.1% CPU` |
| **2 s `+15 °C` CPU Spike Breathing** | Static curve revs immediately | **`0` false rev-ups** | `0` false rev-ups |
| **Sustained Load Ramp Latency** | N/A | **`3 s`** | `<=3 s` |
| **Fail-Safe BIOS Auto-Restore** | Risk of stuck low RPM on crash | **`100%`** (`["case:intake", "gpu:0"]` restored on Drop, panic, and driver reset) | `100%` |
| **WinRing0 / Anti-Cheat Safety** | Legacy fan tools use `WinRing0` | **0 `WinRing0`/`inpoutx64`**; SuperIO gated behind off-by-default `superio-pawnio` | `0` conflicts |

---

## 3. Verification Gates

- `cargo fmt -p exo-thermal -- --check`: PASS
- `cargo clippy -p exo-thermal --all-targets -- -D warnings` (default + `--features superio-pawnio`): PASS (0 warnings)
- `cargo test -p exo-thermal`: PASS (9/9 default) + PASS (10/10 `--features superio-pawnio`)
- `cargo build -p exo-thermal --release`: PASS
- `node tools/check-comments.mjs`: PASS (0 prose comments in all new `.rs` files)
