# Exo Light (`exo-light` / Spec 13) — Zero-Bloat Native Rust Unified RGB Controller

- **Date:** 2026-11-24
- **Branch:** `feat/native-exo-light` (from `origin/feat/native-polish-2`)
- **License:** MIT (clean-room Rust — zero GPL/AGPL, zero `WinRing0`)
- **Hardware Safety:** 100% tested on `MockLampTransport` only — zero writes to real RGB/HID/SMBus devices.

---

## 1. What Was Built

Added clean-room MIT Rust crate `apps/launcher/native-ui/crates/exo-light`:

- **`transport.rs`**: `Rgb8`, `DeviceTier` (`Win11LampArray`, `UsbHid`, `SmbusPawnIo`), `LightDevice` trait, and `MockLampTransport` (records write counts, brightness, frames, and baseline restores without touching hardware).
- **`win11_lamparray.rs`**: Tier 1 Windows 11 Dynamic Lighting / USB-IF HID LampArray (`0x0059` Usage Page) report encoders (`LampRangeUpdate`, `LampMultiUpdate`, `LampArrayControl`) — zero vendor drivers required.
- **`usb_hid_rgb.rs`**: Tier 2 Direct USB HID RGB frame encoders (Finalmouse receiver, Wooting direct, Logitech HID++, Razer Chroma HID, Corsair direct HID).
- **`smbus_pawnio.rs`**: Tier 3 Optional signed PawnIO (`\\.\PawnIO`) SMBus command builder with unconditional **SPD EEPROM `0x50..=0x57` hard block** + explicit RGB controller address allowlist (**zero `WinRing0`**).
- **`effects.rs`**: Pure-Rust deterministic frame generator (`Static`, `Breathing`, `ExoVoidMinimal`, `ThermalReactive`, `BatteryAlert`, `AmbientSync`).
- **`auto_context.rs`**: OLED/HDR Cinema Auto-Dim + fullscreen game dim + night mode + wireless battery saver (`<25%` dim, `<15%` amber pulse) + clean restore on exit.
- **`lib.rs`**: `LightManager` with **Smart Static-Commit Dormancy** (commits static color once, then 0 writes / 0 wakeups), clean `shutdown_restore()`, and `REPLACED_VENDOR_SERVICES` list.
- **`bench_main.rs`**: Mock-only benchmark (`exo-light-bench.exe`) measuring RSS delta and 60 s static vs 30 FPS animated CPU cost via `GetProcessMemoryInfo` & `GetProcessTimes`.

---

## 2. Measured Before / After (Mock 3-Device Rig: 68-zone KB + Wireless Mouse + 16-zone DDR5 RAM)

| Metric | Baseline (SignalRGB / iCUE / Armoury Crate / OpenRGB) | Native MIT Rust (`exo-light`) | Target |
| --- | --- | --- | --- |
| **Memory Footprint (RSS Delta)** | SignalRGB 300–600 MB; iCUE 500+ MB | **80 KB** (`81,920` bytes; total process WS 4.74 MB) | `<3 MB` (38× better) |
| **Static Mode (60 s / 1,800 ticks)** | 1–5% continuous CPU | **0.0% CPU** (`3` commit writes, `0` subsequent writes, `13 µs` total wall) | `0.0% CPU` |
| **30 FPS Animated Mode (60 s / 1,800 frames)** | 1–5% continuous CPU | **`964 µs` total wall** (`0.536 µs`/frame, `<0.002%` CPU over 60 s, 4,818 mock writes) | `<0.2% CPU` |
| **Anti-Cheat / WinRing0 Safety** | OpenRGB SMBus uses `WinRing0` (blocked by Vanguard/Defender) | **0 `WinRing0`/`inpoutx64` references**; SPD `0x50..=0x57` hard-blocked | 0 conflicts |
| **Cost / License** | SignalRGB Pro $60/yr / OpenRGB GPL-2.0 | **$0 / MIT** | Free MIT |

---

## 3. Verification Gates

- `cargo fmt -p exo-light -- --check`: PASS
- `cargo clippy -p exo-light --all-targets -- -D warnings`: PASS (0 warnings)
- `cargo test -p exo-light`: PASS (11/11 — static dormancy, HDR auto-dim + shutdown restore, wireless battery saver, SPD `0x50..=0x57` hard block, SMBus allowlist, zero `WinRing0` static scan)
- `cargo build -p exo-light --release`: PASS
- `node tools/check-comments.mjs`: PASS (0 prose comments in all new `.rs` files)
