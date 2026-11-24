# Spec 09 — Exo Thermal & Fan Control (`exo-thermal`): Unified GPU + CPU + Case Fan Engine with Acoustic Calibration

- **Inventory Rank:** #09 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** **FanControl (Rem0o)**, **MSI Afterburner** fan curves, **Armoury Crate / iCUE / NZXT CAM** fan bloat, and Exo's NVIDIA-only `Services/ExoAiFans.cs`
- **Anti-Cheat Class:** `AC-SAFE` (official vendor GPU APIs NVML/ADLX/IGCL + optional WHQL/HVCI-signed **PawnIO** SuperIO/EC modules — **zero WinRing0!**)
- **License:** MIT (clean-room Rust); optional PawnIO driver stays separate WHQL-signed user install (`KEEP`, Row #49)

---

## 1. Scope

### What It Solves
1. Exo's current `Services/ExoAiFans.cs` only supports **NVIDIA GPUs via NVML** (4 static curves, 0 AMD/Intel GPU support, 0 CPU/case/AIO fan support).
2. Users run **FanControl** (closed-source freeware depending on LibreHardwareMonitor plugins) or heavy motherboard suites (Armoury Crate / Gigabyte Control Center / iCUE: 300–600 MB RAM) and spend hours hand-tuning curves by trial and error.

### In Scope
- **Multi-Vendor GPU Fan Backends:**
  - NVIDIA (`nvml.dll` — multi-fan independent PWM + auto restore).
  - AMD Radeon (`amdadlx64.dll` — `IADLXManualFanTuning`, zero-RPM support).
  - Intel Arc (`ControlLib.dll` — `ctlFanSetStateSpeed` / auto restore).
- **System / CPU / Case / AIO Fan Backend (Optional Signed PawnIO):**
  - Clean-room Rust user-mode controller talking to signed **PawnIO** SuperIO/LPC modules (`NCT6775/6798`, `IT8688/87xx`, common desktop motherboards) + standard USB HID AIO/fan controllers (Corsair Commander, NZXT Kraken/Smart Device, Aquacomputer).
- **FanControl-Beating Curve Math Engine (`curve.rs`):**
  - **Mix Sensors:** `max(CPU_Tctl, GPU_Hotspot)`, weighted averages, or coolant delta so case intake fans automatically ramp when *either* GPU or CPU heats up!
  - **Asymmetric Hysteresis & Response-Rate Damping:** Independent ramp-up rate (fast on sudden load) vs ramp-down damping (slow smooth decay so fans never "rev/breathe" on 1-second CPU boost spikes!).
  - **Hard Thermal Safety Floor:** Non-overridable safety clamp (`>=80 °C → min 85%`, `>=85 °C → 100%`) plus guaranteed `restore_auto()` on exit, crash, or sleep/resume!
- **One-Click Acoustic Self-Calibration (`acoustic.rs`):**
  - Optional 30-second user-triggered sweep using the user's existing microphone (already known to Exo!) + fan RPM tachometers: identifies exact **zero-RPM start/stop thresholds** and each fan's **acoustic resonance knee** (the highest RPM before noise jumps sharply), automatically generating a personalized Quiet / Balanced / Max-Cooling curve!

### Out of Scope
- Using vulnerable/banned `WinRing0` drivers (strictly forbidden!).
- Writing fan speeds to the owner's real hardware during automated tests (`MockFanBackend` only!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-thermal/
├── Cargo.toml                  # libloading, windows (IoCtl/Hid/Audio), serde, parking_lot
├── src/
│   ├── lib.rs                  # ThermalDaemon + 1-2s control tick + Drop/CtrlC auto-restore guard
│   ├── backend.rs              # Trait FanBackend + SensorSource + MockFanBackend
│   ├── gpu_nvml.rs             # NVIDIA NVML multi-fan PWM + temp/hotspot
│   ├── gpu_adlx.rs             # AMD ADLX manual fan & zero-RPM control
│   ├── gpu_igcl.rs             # Intel Arc IGCL fan control
│   ├── superio_pawnio.rs       # Signed PawnIO SuperIO/LPC reader & PWM writer (zero WinRing0)
│   ├── usb_controllers.rs      # Native HID AIO/fan hubs (Corsair/NZXT/Aquacomputer)
│   ├── curve.rs                # Mixed-source curves, asymmetric hysteresis, slew-rate limiter, 85°C hard clamp
│   └── acoustic_cal.rs         # Optional mic+tach sweep to discover spin-up % and quiet acoustic knee
```

---

## 3. Automation & Intelligence Design

1. **Zero-Curve-Editing Experience:** Instead of forcing users to build 6 linked graph nodes like FanControl, Exo automatically pairs case intake/exhaust fans to `max(GPU, CPU)` and applies the user's acoustically calibrated quiet knee!
2. **Per-Game Acoustic Profiles:** Whisper-quiet on desktop & indie games → balanced acoustic knee in AAA games → max thermal headroom during `exo-gpu` undervolt search or heavy summer ambients.
3. **Resonance Notch Avoidance:** If acoustic calibration detects a humming resonance band (e.g., 1,350–1,450 RPM), the curve controller automatically skips past that band!
4. **Crash-Proof Hardware Hand-Back:** Registers Win32 shutdown/console/panic hooks + a lightweight watchdog so if Exo exits or crashes, every GPU and SuperIO fan channel returns to VBIOS/EC automatic control immediately.

---

## 4. Measurement Plan

All control loops, hysteresis, slew limits, thermal fail-safes, and acoustic knee detection verified against deterministic `MockFanBackend` thermal profiles; read-only NVML fan/temp poll benchmarked safely on this RTX 3070 PC:

| Metric | Baseline (FanControl / `ExoAiFans.cs` NVIDIA-only) | Target (`exo-thermal`) | How Measured |
| --- | --- | --- | --- |
| **Hardware Coverage** | `ExoAiFans.cs`: NVIDIA GPU only | **NVIDIA + AMD + Intel GPU + SuperIO CPU/Case + USB AIOs** | Backend capability matrix |
| **Control Loop Overhead** | FanControl ~35–70 MB RAM / 0.5–1% CPU | **<2 MB RAM / <0.1% CPU** | `GetProcessMemoryInfo` + `GetProcessTimes` over 5-min read-only poll |
| **Thermal Spike Breathing Suppression** | Static curve revs fans on 1 s CPU spikes | **0 false rev-ups** on 2 s synthetic +15 °C CPU spike; **<3 s** ramp on sustained load | Deterministic synthetic thermal trace test |
| **Fail-Safe Auto-Restore** | Risk of stuck low RPM on crash | **100%** `restore_auto()` invoked on normal exit, panic, and simulated driver reset | Automated panic/drop harness test |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-thermal --locked -- -D warnings`, `cargo test -p exo-thermal --locked`.
2. **Thermal Safety Proof Tests:** Fuzz/property tests prove that for ANY user curve input, `temp >= 85 °C` ALWAYS outputs `100%` and `temp >= 80 °C` ALWAYS outputs `>= 85%`.
3. **Fail-Safe Drop/Panic Test:** Verify `Drop` + panic hook restores VBIOS/EC auto on every touched fan channel.
4. **Zero-WinRing0 Gate:** Static scan verifies zero references to `WinRing0`, `inpoutx64`, or unsigned kernel drivers.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER WRITE FAN SPEEDS TO OWNER'S HARDWARE IN AGENT TESTS:** All automated tests use `MockFanBackend` or strictly read-only NVML temperature/RPM queries.
- **Unbreakable Thermal Clamps:** Never allow 0% fan speed above `60 °C` GPU / `55 °C` CPU; hard-clamp `85 °C → 100%`.
- **Laptop Safety:** Automatically disable manual fan overrides on laptops/locked VBIOS EC controllers (read-only monitoring only) to avoid fighting OEM thermal firmware.
- **Anti-Cheat Safety:** Never use `WinRing0`; motherboard PWM requires the user's explicit opt-in installation of WHQL-signed `PawnIO`.
