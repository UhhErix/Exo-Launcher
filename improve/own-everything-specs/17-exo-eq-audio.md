# Spec 17 — Exo Audio & Spatial EQ (`exo-audio-apo`): Clean-Room MIT Rust Windows Audio APO + Embedded AutoEq

- **Inventory Rank:** #17 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** External GPL-2.0 **Equalizer APO** (downloaded from SourceForge via `Services/EqualizerApo.cs`), **Peace GUI**, **SteelSeries Sonar**, and paid **Dolby Access** (`Services/ExoEq*.cs`, `Services/AutoEqCatalog.cs`, `Services/AudioDetails.cs`)
- **Anti-Cheat Class:** `AC-SAFE` (100% standard Microsoft Windows Audio Processing Object COM interface `IAudioProcessingObject` running inside Windows `audiodg.exe` — zero game injection!)
- **License:** MIT (clean-room Rust COM DLL `exo_apo.dll` + AutoEq MIT measurement index with full upstream measurement attribution!)

---

## 1. Scope

### What It Solves
1. Today `ExoEq` cannot work out of the box: it forces the user to download GPL-2.0 **Equalizer APO**'s installer from SourceForge, run an external wizard, pick audio devices manually, reboot/restart `Audiosrv`, and **breaks silently whenever a Windows feature update resets endpoint APO registry keys**!
2. Gamers who skip Equalizer APO install **SteelSeries Sonar** (heavy GG daemon, forced account login, high latency virtual audio devices) or pay **$15** for Dolby Access.

### In Scope
- **Clean-Room MIT Rust Windows Audio APO (`exo_apo.dll`):**
  - Implements Microsoft's documented `IAudioProcessingObject`, `IAudioProcessingObjectRT`, `IAudioProcessingObjectConfiguration`, and `IAudioSystemEffects3` (SFX/MFX/EFX) COM interfaces in pure Rust (`windows` crate).
  - **Real-Time Lock-Free SIMD DSP Chain (`dsp.rs` — zero heap allocs on audio thread!):**
    - Up to 16-band parametric biquad IIR cascade (peaking, low/high shelf, low/high pass) in AVX2/SSE2 f64 internal precision (zero quantization noise!).
    - Pre-amp headroom auto-gain + true-peak soft limiter (zero digital clipping!).
    - Optional lightweight **SOFA HRTF / Crossfeed spatializer** for stereo headphone soundstage & competitive directional clarity.
    - Lock-free atomic double-buffered filter swap over shared memory (`Global\ExoApoCfg_<endpoint_id>`) → changing EQ sliders in Rune updates audio **live in <5 ms with zero audio pops or `Audiosrv` restarts!**
- **Compact Embedded + Auto-Synced AutoEq Catalog (`autoeq.rs`):**
  - Compiles Jaakko Pasanen's MIT **AutoEq** headphone/IEM parametric EQ database (retaining exact measurer attribution: oratory1990, crinacle, Rtings, etc.) into a compact `<400 KB` binary zstd index shipped/cached directly in Exo — instant offline headphone search!
- **Self-Healing APO Registration (`installer.rs`):**
  - Registers `exo_apo.dll` cleanly per chosen audio endpoint (preserving OEM APOs via Windows 11 `CompositeFX` chaining where supported!), backs up original `FxProperties` registry keys in Exo's `Keep` ledger for 1-click full uninstall, and **automatically detects & re-heals APO registration after Windows feature updates!**

### Out of Scope
- Copying any GPL-2.0 code from Equalizer APO (100% clean-room implementation from Microsoft Windows Audio APO SDK documentation!).
- Virtual audio device kernel drivers (Windows native APO chaining avoids virtual-cable latency and Discord device-routing bugs!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-audio-apo/
├── Cargo.toml                  # windows (Media::Audio, Com, Registry, Memory), serde, zstd
├── src/
│   ├── lib.rs                  # COM DllGetClassObject / DllCanUnloadNow / DllRegisterServer exports
│   ├── apo_com.rs              # IAudioProcessingObject + IAudioProcessingObjectRT + IAudioSystemEffects3
│   ├── dsp.rs                  # Zero-alloc AVX2/f64 16-band biquad cascade + true-peak limiter + crossfeed/HRTF
│   ├── shm_ipc.rs              # Lock-free atomic double-buffered shared memory config (Rune UI -> audiodg.exe)
│   ├── autoeq_db.rs            # Compact binary AutoEq catalog + measurer attribution + USB VID/PID auto-matcher
│   └── endpoint_reg.rs         # CompositeFX endpoint registration, Keep backup/rollback, Win-update auto-heal
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config USB Headphone Auto-Recognition:** Plug in a known USB DAC/headset (e.g., HyperX, Sennheiser, Logitech, SteelSeries, Apple USB-C, Moondrop) → Exo identifies it by USB VID/PID or endpoint name and offers 1-click AutoEq Harman/JM-1 correction with live A/B toggle!
2. **Per-Game EQ Auto-Switch:** Automatically switches from **Audiophile Harman/Flat** (for Spotify/Music & single-player games) to **Competitive Clarity** (gentle sub-bass trim + 2–4 kHz footstep detail without harshness) when launching FPS titles, switching smoothly with zero audio dropouts!
3. **Auto-Heal After Windows Updates:** Startup health check verifies `FxProperties` on active Exo EQ endpoints; if Windows Update wiped them, silently restores them cleanly (solving Equalizer APO's #1 user complaint!).
4. **Live Spectrum & Zero-Restart Tweaking:** Drag any EQ band in Rune → shared-memory atomic swap updates `audiodg.exe` seamlessly at the next 10 ms buffer boundary with zero clicks/pops.

---

## 4. Measurement Plan

All DSP math, impulse/frequency response, lock-free shared-memory swaps, and registry registration tested in isolated unit/mock harnesses (never modifying owner's live `Audiosrv` or audio endpoints in agent tests!):

| Metric | Baseline (Equalizer APO / SteelSeries Sonar) | Target (`exo-audio-apo`) | How Measured |
| --- | --- | --- | --- |
| **Added Audio Latency** | Sonar virtual driver: ~15–35 ms; EqAPO: <1 ms | **<0.2 ms** (zero extra buffer, in-place APO!) | APO buffer timestamp measurement |
| **DSP CPU Usage (48 kHz Stereo, 10-Band EQ + Limiter)** | EqAPO ~0.3–0.6% CPU; Sonar ~2–4% CPU | **<0.15% CPU** (<30 µs per 10 ms 480-sample block) | Criterion benchmark over 10,000 audio blocks |
| **Heap Allocations on RT Audio Thread** | Must be 0 | **0 allocations** (verified by custom counting allocator in test!) | Counting-allocator unit test |
| **Frequency Response Accuracy vs AutoEq Reference** | Reference RBJ Audio EQ Cookbook | **≤0.02 dB** max error (20 Hz – 20 kHz) | Swept-sine FFT verification test |
| **External GPL Installer Required** | Equalizer APO GPL-2.0 SourceForge EXE | **0** (100% native MIT) | License & installer audit |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-audio-apo --locked -- -D warnings`, `cargo test -p exo-audio-apo --locked`.
2. **Zero-Allocation RT Gate:** Unit test runs `APOProcess` for 60 simulated seconds under a panic-on-alloc global allocator hook — **zero heap allocations allowed on the RT thread!**
3. **Zero-Click Parameter Swap Gate:** Sweep EQ gain from `-12 dB` to `+12 dB` across 100 rapid shared-memory updates and assert max sample-to-sample delta stays below click threshold (smooth coefficient interpolation!).
4. **Clean Uninstall / Registry Rollback Gate:** Mock `FxProperties` test verifies 100% byte-for-byte restoration of original endpoint keys on uninstall.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER MODIFY OWNER'S LIVE AUDIO ENDPOINTS / `Audiosrv` IN AGENT TESTS:** All `endpoint_reg.rs` tests MUST run against mock registry hives!
- **Hearing Protection Hard Limiter:** Always enforce a hard true-peak limiter (`-0.5 dBFS`) and clamp max single-band boost (`+12 dB`) + automatic negative pre-amp so a buggy config can NEVER blast ears or blow headphone drivers!
- **Fail-Pass-Through in `audiodg.exe`:** Any internal error or unmapped shared memory MUST pass input samples straight through unmodified (never silence or crash `audiodg.exe`!).
- **AutoEq Attribution:** Always display original measurement source (oratory1990, crinacle, Rtings, etc.) as required by `THIRD_PARTY_NOTICES.md`.
