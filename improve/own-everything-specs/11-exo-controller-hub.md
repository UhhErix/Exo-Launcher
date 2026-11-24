# Spec 11 — Exo Controller Hub (`exo-controller`): Native Rust GameInput + Raw HID Multi-Pad Calibration & Profiles

- **Inventory Rank:** #11 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** **DS4Windows** (GPL-3.0, archived upstream), **reWASD** (paid proprietary, triggers anti-cheat warnings!), **DualSenseX / DSX** (paid Steam app), **Steam Input** double-input conflicts, and C# `Services/ControllerLab*.cs` + `Adapters/SteamInputConfig.cs`
- **Anti-Cheat Class:**
  - Hardware calibration, stick circularity testing, battery/LED/haptics, DualSense Edge & Xbox Elite **onboard hardware profiles**, and Steam Input conflict auto-fix: **`AC-SAFE`** (100% standard USB/BT HID & Steam config).
  - Optional software virtual-controller remapping / gyro-to-mouse synthesis: **`AC-OFFLINE-ONLY`** (runs in isolated worker, strictly disabled for any `AntiCheatCheck.AssumeProtected` game!).
- **License:** MIT (clean-room Rust; follows public HID specs and Exo's own `ControllerLab` verified DualSense Edge format)

---

## 1. Scope

### What It Solves
1. Exo already has a great C# Sony `ControllerLab` (DualSense / DualSense Edge / DS4 calibration & Edge onboard profiles) and basic gamepad navigation in `native-ui/crates/exo-ui/src/gamepad.rs`, **but** lacks unified Rust `GameInput` support across Xbox Series/Elite, Switch Pro, and Hall-effect 3rd-party pads, plus adaptive trigger per-game presets, live circularity/polling diagnostics, and safe offline gyro/remap.
2. Users install **DS4Windows** (discontinued, needs kernel ViGEmBus driver) or **reWASD** (paid, causes instant kicks in EA/Apex/CoD anti-cheat!) just to use a DualSense or calibrate stick drift on PC.

### In Scope
- **Unified Native Rust Input Engine (`exo-controller`):**
  - Primary Windows 11 **Microsoft GameInput API** (`GameInput.dll` — sub-ms hardware-timestamped readings across Xbox, PlayStation, Switch, generic HID pads, zero polling timer!).
  - Direct Raw HID drivers (`exo-hid-core` shared with Spec #05) for **DualSense / DualSense Edge** (native `FnL/FnR/BackL/BackR` paddles, onboard 4-slot profile read/write, permanent flash stick center/range calibration, adaptive trigger effects, mic LED, lightbar, battery %, USB 1000 Hz / BT polling diagnostics), **DualShock 4**, **Xbox Elite Series 2** (onboard paddles/curves), and **Nintendo Switch Pro**.
- **Automatic Steam Input Conflict Resolver:** Detects games with native DualSense/haptics support and automatically toggles Steam Input (`localconfig.vdf` / `SteamInputConfig`) so players get native PlayStation prompts, adaptive triggers, and zero double-input!
- **Interactive Rune Calibration & Stick-Drift Lab:** Live radial circularity error %, deadzone/response curve visualizer, trigger travel linearity, and guided permanent flash recalibration (with `MockControllerTransport` in all tests!).
- **Anti-Cheat-Safe Remap Split:**
  - *Hardware Onboard Mode (`AC-SAFE`):* Writes paddle/deadzone/curve profiles directly into DualSense Edge / Xbox Elite onboard memory → works natively in every anti-cheat game with 0 background hooks!
  - *Offline Virtual/Gyro Mode (`AC-OFFLINE-ONLY`):* Optional software gyro-to-mouse / button remap active strictly for `AntiCheatCheck.Kind.None` games.

### Out of Scope
- Shipping or wrapping kernel virtual-bus drivers (`ViGEmBus` / `HIDHide`) inside Exo core, or injecting controller remaps into online anti-cheat games (reWASD-style competitive aim-assist abuse is explicitly forbidden!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-controller/
├── Cargo.toml                  # windows (Hid, SetupApi, Gaming), serde, parking_lot
├── src/
│   ├── lib.rs                  # ControllerHub + hotplug stream + MockControllerTransport
│   ├── gameinput.rs            # Windows 11 GameInput COM/FFI bindings (sub-ms timestamped callback)
│   ├── sony_hid.rs             # Clean-room DualSense / DualSense Edge / DS4 USB & BT HID protocol + CRC32
│   ├── sony_calibrate.rs       # Stick center/range calibration state machine + Edge onboard profile serializer
│   ├── xbox_hid.rs             # Xbox Series / Elite Series 2 battery, rumble, and onboard profile reader
│   ├── switch_hid.rs           # Switch Pro / Joy-Con HID subcommand driver (IMU + rumble + LED)
│   ├── diag.rs                 # Live stick circularity error %, drift detector, polling rate & latency jitter
│   ├── steam_conflict.rs       # Auto-detects native DualSense games & fixes Steam Input double-input
│   └── auto_profiles.rs        # Per-game onboard profile / trigger / LED switcher (with AC policy check)
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config PlayStation-on-PC:** Plug in a DualSense → Exo detects it instantly, fixes Steam Input conflicts for games with native DualSense support, shows real battery % in Rune's Bar, and flashes a subtle low-battery amber lightbar warning at `<15%`.
2. **Automatic Stick-Drift Early Warning:** Passively observes idle stick center offset while navigating Exo UI; if center drift exceeds `4%`, gently suggests 1-click guided recalibration or minimal deadzone bump!
3. **Per-Game Edge/Elite Profile Auto-Switch:** Switches DualSense Edge onboard slots & adaptive trigger presets automatically per game (`FPS Hair-Trigger` vs `Racing Progressive Brake`), restoring desktop defaults on exit.
4. **Native UI Gamepad Navigation:** Directly powers `exo-ui`'s couch/controller navigation (`gamepad.rs`) with zero duplicate polling threads.

---

## 4. Measurement Plan

All HID write/calibration sequences tested strictly via `MockControllerTransport` golden byte fixtures (never writing real controllers!); read-only GameInput/HID polling latency benchmarked safely:

| Metric | Baseline (DS4Windows / C# `ControllerLab`) | Target (`exo-controller`) | How Measured |
| --- | --- | --- | --- |
| **Input Poll Overhead (1000 Hz USB pad)** | DS4Windows ~45–80 MB RAM, 1–2% CPU | **<3 MB RAM / <0.15% CPU** | `GetProcessMemoryInfo` + `GetProcessTimes` |
| **DualSense Edge Profile Codec Parity** | C# `ControllerLab` verified byte layout | **100% byte-for-byte parity** on real Edge profile fixtures | Golden binary round-trip unit test suite |
| **Anti-Cheat Virtual Remap Guard** | reWASD causes AC kicks | **0** software synthesis events on `Found`/`PossiblyVac`/`Unknown`/`Riot` | Policy test matrix |
| **Paid / External Tools Replaced** | DS4Windows + DSX ($4) + reWASD ($7–35) | **0** ($0 native in Exo) | Feature matrix |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-controller --locked -- -D warnings`, `cargo test -p exo-controller --locked`.
2. **Sony CRC-32 & Edge Profile Golden Tests:** Verify exact CRC-32 seed (`0xA2` BT output reports) and lossless round-trip serialization of DualSense Edge onboard profiles against real anonymized fixtures.
3. **Anti-Cheat Remap Isolation Gate:** Verify software remap mode hard-refuses activation whenever foreground game is `AssumeProtected`.
4. **Banned-Symbols Gate:** Verify zero `SendInput`/`mouse_event`/`keybd_event` imports inside `exo-controller` library or `exo-ui.exe`.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER WRITE TO OWNER'S REAL CONTROLLERS IN AGENT TESTS:** Calibration flash commits and Edge profile writes MUST be tested exclusively against `MockControllerTransport`!
- **Calibration Safety Lock:** Require explicit user confirmation and USB-wired connection (never over flaky Bluetooth!) before committing permanent flash calibration to a DualSense.
- **Zero Anti-Cheat Abuse:** Never synthesize aim-assist/recoil macros or virtual mouse-to-stick emulation inside online anti-cheat games.
- **No Unaudited Kernel Drivers:** Do NOT bundle `ViGEmBus` or unsigned virtual HID drivers.
