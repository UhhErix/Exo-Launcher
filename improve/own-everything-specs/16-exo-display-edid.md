# Spec 16 — Exo Display & True HDR Engine (`exo-display`): Safe CRU EDID Override + MHC2 Hardware-LUT HDR + DDC/CI

- **Inventory Rank:** #16 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** **CRU (Custom Resolution Utility)**, **novideo_srgb** (GPL-3.0, NVIDIA-only), **Windows HDR Calibration**, **ClickMonitorDDC / Twinkle Tray**, and C# `Helpers/Edid.cs`, `DisplayLink.cs`, `DisplayOutput.cs`, `MonitorDdc.cs`, `WindowsHdr.cs`, `HdrBrightness.cs`, `Services/HdrHostService.cs`, `GameHdr.cs`
- **Anti-Cheat Class:** `AC-SAFE` (100% OS display APIs — `DisplayConfig`, Windows Color System `MHC2` ICC profiles, `EDID_OVERRIDE` registry, `dxva2` DDC/CI — zero game injection!)
- **License:** MIT (clean-room Rust)

---

## 1. Scope

### What It Solves
1. Windows 11's desktop HDR compositor maps SDR content using a **piecewise sRGB curve** instead of pure **2.2 gamma**, leaving blacks washed out and gray on OLED and Mini-LED monitors! Fixing it today requires third-party tools (`MHC2Gen` / `novideo_srgb`) or manual ICC hacks.
2. Gamers use **CRU (Custom Resolution Utility)** to fix broken TV `4096×2160` resolutions (which ruin 16:9 DSR/scaling), adjust VRR/FreeSync ranges to cure OLED VRR flicker, or add custom refresh rates — **but CRU has zero automatic revert watchdog**, so a bad timing leaves the user with a black screen until they boot Safe Mode!
3. Exo already has C# readers for EDID, DDC/CI, DisplayLink, and per-game Auto-HDR (`GameHdr.cs`), ready to unify and upgrade in native Rust.

### In Scope
- **Safe EDID Override Editor (`edid_override.rs` — Beats CRU!):**
  - Parses full EDID 1.4 + CTA-861 + DisplayID 2.0 extension blocks (DTDs, VRR min/max Hz range, HDR10 static metadata `MaxCLL`/`MaxFALL`, audio blocks, broken 4096p cinema modes).
  - Writes strictly validated Windows `EDID_OVERRIDE` registry blocks + triggers clean GPU display restart (`SetupDiRestartDevices`) wrapped in a **15-second mandatory user-confirm revert watchdog**: if the user doesn't click "Keep" within 15 s (e.g., black screen / out-of-range monitor!), Exo **automatically deletes the override and restarts display output**!
- **MHC2 Hardware-LUT True-Black HDR & sRGB Clamp Generator (`mhc2_icc.rs`):**
  - Generates valid Windows 11 **WCS MHC2** (Display Hardware LUT) ICC color profiles on the fly from each monitor's real EDID primaries + measured/configured peak nits:
    - **True-Black 2.2 Gamma Fix** for Windows 11 HDR (eliminates washed-out SDR-in-HDR blacks at **0% GPU shader cost** — runs in display engine hardware LUT!).
    - **Wide-Gamut sRGB Clamp** for oversaturated QD-OLED/IPS monitors on SDR desktop (works on **NVIDIA + AMD + Intel**, beating NVIDIA-only `novideo_srgb`!).
- **DDC/CI & Per-Game Display Automation (`ddc_auto.rs`):**
  - Native Rust `dxva2` DDC/CI brightness, contrast, input source, and monitor preset automation + per-game HDR/refresh/resolution switching with automatic `DriverRestore` snapshot/restore across GPU driver installs!

### Out of Scope
- Injecting per-game swapchain HDR retrofits (Special K / RenoDX belongs to Wave 2 Row #25, `AC-OFFLINE-ONLY`).
- Writing live `EDID_OVERRIDE` or DDC commands to the owner's real monitors during automated agent tests!

---

## 2. Native Rust Crate Layout

```text
crates/exo-display/
├── Cargo.toml                  # windows (Gdi, Monitor, Devices::Display, Registry), serde, parking_lot
├── src/
│   ├── lib.rs                  # DisplayManager + DisplayConfig topology snapshot/restore
│   ├── edid.rs                 # Pure-Rust EDID 1.4 + CTA-861 + DisplayID 2.0 parser/serializer + CRC-8 verifier
│   ├── edid_override.rs        # Safe EDID_OVERRIDE writer + mandatory 15-second auto-revert watchdog!
│   ├── mhc2_icc.rs             # Pure-Rust ICC v4 + Microsoft MHC2 3x3 matrix + 1D LUT binary builder & WCS installer
│   ├── hdr_auto.rs             # Per-game Windows HDR toggle + SDR-in-HDR brightness slider + OLED detection
│   ├── ddc_ci.rs               # Win32 dxva2 DDC/CI VCP read/write (brightness, input, power) with retry & rate-limit
│   └── mock.rs                 # MockDisplayBackend for 100% hardware-safe unit & watchdog testing
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config OLED / HDR Auto-Fix:** Detects an HDR/OLED display from EDID metadata (`MaxLuminance`, `MinLuminance == 0`, panel primaries) → offers 1-click **"Fix washed-out Windows HDR blacks"** (generates & installs exact MHC2 2.2-gamma ICC profile in `<50 ms`!).
2. **Broken 4096p TV Mode One-Click Scrub:** Automatically detects when an HDMI OLED TV advertises cinema `4096×2160` alongside `3840×2160` (a classic Windows scaling bug!) and strips it cleanly via watchdog-protected EDID override.
3. **Automatic Per-Game HDR & Refresh:** Enables Windows HDR automatically when launching a verified HDR game (using Exo's `GameWiki` + PCGamingWiki cache) and switches back to crisp sRGB-clamped SDR on desktop!
4. **OLED Anti-Flicker VRR Helper:** Detects VRR flicker risk on OLED panels + suggests exact `FrameCapPlanner` ceiling + clean VRR min-Hz tuning.

---

## 4. Measurement Plan

EDID parser/serializer, MHC2 ICC binary layout, and 15 s revert watchdog tested deterministically on real anonymized EDID dumps + `MockDisplayBackend`; read-only EDID/DisplayConfig read benchmarked safely on local host:

| Metric | Baseline (CRU + novideo_srgb + Win HDR Cal + Twinkle Tray) | Target (`exo-display`) | How Measured |
| --- | --- | --- | --- |
| **External Tools Replaced** | 4 separate apps | **0** (unified in `exo-display`) | Feature matrix |
| **MHC2 ICC Generation & Validation** | Manual `MHC2Gen` CLI | **<15 ms** generation; **100%** valid `WcsAssociateColorProfileWithDevice` / ICC v4 header & MHC2 tag | Binary ICC validator test suite |
| **EDID Black-Screen Watchdog Safety** | CRU has **0 s** watchdog (manual Safe Mode rescue!) | **100%** automatic revert within **15.0 ± 0.2 s** if unconfirmed or on process crash | Mock watchdog & crash-journal test |
| **GPU Cost of sRGB Clamp / HDR 2.2 Fix** | Shader-based fixes: 0.5–1.5 ms GPU | **0.0 ms GPU** (hardware display pipeline LUT!) | Architectural verification (WCS MHC2) |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-display --locked -- -D warnings`, `cargo test -p exo-display --locked`.
2. **EDID Checksum & Round-Trip Tests:** Parse, modify (strip 4096p / tweak VRR range), re-serialize, and verify valid 128-byte block checksums across 10 real monitor/TV EDID fixtures (DP 1.4, HDMI 2.1 OLED, ultrawide, laptop eDP).
3. **MHC2 Binary Format Gate:** Verify generated ICC profiles pass strict ICC v4 profile ID MD5 + `MHC2` tag offset/signature/fixed-point S15Fixed16 checks.
4. **Watchdog Crash-Recovery Gate:** Simulate unconfirmed timeout AND simulated process kill before confirmation; verify override is cleanly removed on timeout/restart.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER MODIFY OWNER'S LIVE DISPLAY / EDID / DDC IN AGENT TESTS:** All EDID override, ICC install, and DDC write tests MUST run strictly against `MockDisplayBackend` (read-only `EnumDisplayDevices` / EDID reads are safe).
- **Mandatory 15 s Revert Watchdog:** Never commit a permanent `EDID_OVERRIDE` without an armed revert timer AND a boot-recovery journal entry (in case of power loss during display restart).
- **Laptop eDP Protection:** Refuse custom EDID pixel-clock overclocking on internal laptop eDP panels unless explicitly allowed by panel timing bounds.
- **No Anti-Cheat Interference:** 100% OS display/color APIs, zero game hooks.
