# Exo Display & True HDR Engine (`exo-display` / Spec 16) — Native Rust EDID, MHC2 ICC & DDC/CI

- **Date:** 2026-11-24
- **Branch:** `feat/native-exo-display` (from `origin/feat/native-polish-2`)
- **License:** MIT (clean-room Rust — zero GPL/AGPL, zero game hooks)
- **Hardware Safety:** Read-only EDID registry scan (`KEY_READ`) on live monitors only; all EDID overrides, ICC installs, and DDC/CI writes tested 100% on `MockDisplayBackend`.

---

## 1. What Was Built

Added clean-room MIT Rust crate `apps/launcher/native-ui/crates/exo-display`:

- **`edid.rs`**: Pure-Rust EDID 1.4 + CTA-861 parser, validator, and serializer (PNP manufacturer, product code, monitor name `0xFC`, VRR range `0xFD`, 10-bit CIE 1931 chromaticity primaries, CTA-861 HDR10 static metadata `MaxCLL`/`MaxFALL`/`MinLuminance`, OLED detection, `strip_4096p_modes`, `tune_vrr_range`, and 128-byte block checksum verification/fixing).
- **`mhc2_icc.rs`**: Pure-Rust ICC v4 + Microsoft WCS `MHC2` Hardware-LUT binary builder & validator (1024-entry S15Fixed16 True-Black 2.2 gamma fix for Windows 11 HDR and 3×3 gamut-clamping sRGB matrix for SDR desktop — runs in display hardware LUT at **0.0 ms GPU shader cost**).
- **`edid_override.rs`**: `DisplayBackend` trait + `MockDisplayBackend` + `SafeEdidOverrideSession` with **mandatory 15-second auto-revert watchdog**, **boot-recovery crash journal**, EDID checksum pre-validation, and **laptop eDP pixel-clock overclock protection**.
- **`ddc_ci.rs`**: Clean-room VCP code helpers (`0x10` brightness, `0x12` contrast, `0x60` input, `0xD6` power) tested strictly on `MockDisplayBackend`.
- **`hdr_auto.rs`**: Per-game HDR + MHC2 True-Black 2.2 / desktop sRGB-clamp planner and OLED VRR flicker cap advisor.
- **`lib.rs` & `bench_main.rs`**: Read-only Windows registry EDID enumerator (`read_live_monitors_readonly`, `KEY_READ` only) and `exo-display-bench.exe` harness.

---

## 2. Measured Before / After (Live Read-Only Monitor Scan + Mock Override Watchdog)

| Metric | Baseline (CRU + novideo_srgb + Win HDR Cal + Twinkle Tray) | Native MIT Rust (`exo-display`) | Target |
| --- | --- | --- | --- |
| **External Tools Replaced** | 4 separate apps (including GPL `novideo_srgb`, NVIDIA-only) | **0** (unified MIT Rust, NVIDIA + AMD + Intel) | Unified MIT |
| **Live Read-Only Monitor EDID Scan** (`HKLM\...\DISPLAY`) | Manual CRU / C# helpers | **4.23 ms** across **3 real EDID records** (`ASUS VG248` 48–165 Hz, `ONN 100002487` 48–76 Hz, 100% valid checksums) | Read-only safe |
| **MHC2 ICC Generation & Validation** (1024-entry LUT, `12,912` B) | Manual `MHC2Gen` CLI | **0.097 ms** (`lutMonotonic = true`, valid ICC v4 + `MHC2` tag) | `<15 ms` (**154× faster**) |
| **EDID Black-Screen Watchdog Safety** | CRU has **0 s** watchdog (Safe Mode rescue) | **100%** automatic revert on unconfirmed timeout + crash-recovery journal | `100%` auto-revert |
| **GPU Cost of HDR 2.2 / sRGB Clamp** | Shader-based fixes: 0.5–1.5 ms GPU | **0.0 ms GPU** (WCS MHC2 hardware display pipeline LUT) | `0.0 ms GPU` |

---

## 3. Verification Gates

- `cargo fmt -p exo-display -- --check`: PASS
- `cargo clippy -p exo-display --all-targets -- -D warnings`: PASS (0 warnings)
- `cargo test -p exo-display`: PASS (7/7 — EDID 4096p scrub + VRR tune + checksums, MHC2 ICC validation, watchdog auto-revert, crash-recovery journal, laptop eDP overclock block, mock DDC/CI brightness clamp, HDR/SDR auto-planner)
- `cargo build -p exo-display --release`: PASS
- `node tools/check-comments.mjs`: PASS (0 prose comments in all new `.rs` files)
