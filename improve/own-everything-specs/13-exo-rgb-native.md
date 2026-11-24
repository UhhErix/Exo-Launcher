# Spec 13 — Exo Light (`exo-light`): Zero-Bloat Native Rust Unified RGB Controller (Win11 LampArray + HID + PawnIO)

- **Inventory Rank:** #13 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** **OpenRGB** (GPL-2.0, often pulls in anti-cheat-banned `WinRing0` for SMBus!), **SignalRGB** (300–600 MB RAM, $5/mo Pro paywall), **ASUS Armoury Crate**, **Corsair iCUE**, **Razer Chroma**, **Logitech G HUB LIGHTSYNC**, **MSI Mystic Light**
- **Anti-Cheat Class:** `AC-SAFE` (Windows 11 native **Dynamic Lighting / HID LampArray** + standard USB HID + optional WHQL/HVCI-signed **PawnIO** SMBus module — **ZERO `WinRing0`!**)
- **License:** MIT (clean-room Rust)

---

## 1. Scope

### What It Solves
1. Nearly every gaming PC ships with 2–4 conflicting RGB ecosystems (motherboard + RAM + mouse + keyboard + GPU), forcing users to run **500–1,500 MB** of overlapping vendor daemons or **SignalRGB** ($60/yr subscription, 400+ MB RAM) — or **OpenRGB**, whose SMBus RAM/motherboard access historically relies on **`WinRing0`** which triggers Vanguard/FACEIT/Windows Defender blocks!
2. Even worse, blazing RGB LEDs ruin OLED dark-room immersion and waste wireless mouse/keyboard battery.

### In Scope
- **3-Tier Zero-Conflict Lighting Backend (`exo-light`):**
  1. **Tier 1 (Zero-Driver Native Win11):** **Windows 11 Dynamic Lighting (`HID LampArray` / `Windows.Devices.Lights`)** — works natively with zero extra drivers across modern Razer, Logitech, ASUS, SteelSeries, Corsair, Acer, HP, and Wooting devices that expose HID LampArray!
  2. **Tier 2 (Direct USB HID):** Shares `exo-hid-core` (Spec #05) for devices lacking LampArray (Finalmouse receiver LED, Wooting per-key RGB, Logitech/Razer/Corsair/HyperX direct USB HID lighting).
  3. **Tier 3 (Optional Signed PawnIO SMBus):** Clean-room Rust caller over WHQL-signed **PawnIO** SMBus modules for DDR4/DDR5 RAM sticks (Corsair Vengeance, G.Skill Trident, Kingston Fury) and motherboard ARGB headers — **never `WinRing0`!**
- **Lightweight Effect Compositor (`effect.rs`):**
  - Renders static, smooth breathing, Exo Void minimal accent, temperature-reactive (linked in-process to `exo-sensors` Spec #10!), low-battery peripheral alert, and screen-edge ambient sync (opt-in, via `exo-capture` tiny 8×4 downsampled average color — 0 CPU cost!).
  - **Smart Static-Commit Mode:** For static colors (what 80%+ of minimal setups want), writes the color ONCE and sleeps at **0.0% CPU / 0 USB traffic**!

### Out of Scope
- Using `WinRing0` or unsigned kernel SMBus drivers (`AC-HARD-STOP`).
- Shipping bloated web-canvas/WebGL RGB shader engines like SignalRGB.
- Writing RGB packets to the owner's real hardware during automated tests (`MockLampTransport` only!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-light/
├── Cargo.toml                  # windows (Devices::Lights, Hid), serde, parking_lot
├── src/
│   ├── lib.rs                  # LightManager + device discovery + zero-CPU static sleep scheduler
│   ├── transport.rs            # Trait LightDevice + MockLampTransport (for tests!)
│   ├── win11_lamparray.rs      # Native Windows 11 HID LampArray backend (zero vendor drivers!)
│   ├── usb_hid_rgb.rs          # Direct HID lighting protocols (Finalmouse, Wooting, Razer, Logitech, Corsair)
│   ├── smbus_pawnio.rs         # Optional read/write via signed PawnIO SMBus module (RAM & mobo ARGB, 0 WinRing0)
│   ├── effects.rs              # Pure-Rust SIMD color-frame generator (static, breathe, thermal, battery-alert)
│   └── auto_context.rs         # Auto-dim on HDR/OLED/fullscreen game, night schedule, battery-saver LED off
```

---

## 3. Automation & Intelligence Design

1. **OLED & HDR Cinema Auto-Dim:** Automatically dims or turns off blinding keyboard/case/RAM LEDs the moment an HDR game or fullscreen media starts (and restores smoothly on exit) — zero manual clicking!
2. **Wireless Battery Saver:** Automatically disables or lowers LED brightness on wireless mice/keyboards/controllers when battery drops below `25%`, and pulses a subtle amber cue at `<15%`.
3. **Zero-CPU Static Mode:** Unlike SignalRGB/iCUE which burn 2–5% CPU rendering 30 FPS animations even for a static solid color, `exo-light` commits static lighting once and goes completely dormant (**0.0% CPU**!).
4. **Silent Bloat Killer:** Flags conflicting vendor RGB services (Armoury Crate LightingService, iCUE, SignalRGB, Mystic Light) in `ReplacedApps` once Exo controls all detected devices.

---

## 4. Measurement Plan

All lighting writes and effect loops tested strictly against `MockLampTransport`; read-only LampArray enumeration benchmarked safely on Windows 11:

| Metric | Baseline (SignalRGB / iCUE / Armoury Crate) | Target (`exo-light`) | How Measured |
| --- | --- | --- | --- |
| **Memory Footprint** | SignalRGB 300–600 MB; iCUE 500+ MB | **<3 MB** delta WS | `GetProcessMemoryInfo` |
| **CPU Usage (Static / 30 FPS Animated)** | SignalRGB/iCUE: 1–5% CPU continuous | **0.0% CPU** (static) / **<0.2% CPU** (30 FPS animated) | `GetProcessTimes` over 60 s mock render |
| **Anti-Cheat / WinRing0 Conflicts** | OpenRGB SMBus uses `WinRing0` (blocked by Vanguard/Defender) | **0 `WinRing0` calls** (100% LampArray + HID + PawnIO) | Binary import & static check |
| **Cost to User** | SignalRGB Pro $60/yr | **$0** (100% free, MIT) | Pricing/license audit |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-light --locked -- -D warnings`, `cargo test -p exo-light --locked`.
2. **Static Dormancy Gate:** Verify that in static mode, the compositor thread issues 1 commit write per state change and **0** ticks/wakeups for the next 60 seconds.
3. **Zero-WinRing0 Gate:** Static scan ensures zero `WinRing0`, `inpoutx64`, or raw I/O port drivers.
4. **HDR Auto-Dim State Machine Tests:** Verify HDR/OLED fullscreen enter → smooth dim to target % → clean restore on exit/crash.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER WRITE TO OWNER'S REAL RGB HARDWARE / SMBUS IN AGENT TESTS:** SMBus writes to wrong addresses can corrupt SPD EEPROMs! Every SMBus device ID MUST be strictly allowlisted (never write to SPD address `0x50–0x57`!) and tested exclusively on `MockLampTransport`!
- **Never Touch SPD EEPROMs:** Hard-block SMBus write addresses `0x50..=0x57` at compile time inside `smbus_pawnio.rs` so DDR4/DDR5 SPD corruption is mathematically impossible!
- **Zero WinRing0:** Never bundle or invoke `WinRing0`.
- **Clean Shutdown:** Restore user's baseline color/brightness cleanly on exit or sleep.
