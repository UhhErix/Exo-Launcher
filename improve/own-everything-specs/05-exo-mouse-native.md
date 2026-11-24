# Spec 05 — Exo Mouse & Keyboard Native (`exo-hid-devices`): Clean-Room MIT Rust Multi-Vendor HID Engine

- **Inventory Rank:** #05 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `XL`
- **Replaces:** `mouse/` AGPL-3.0 React/iframe app (`@openmouse/protocol 0.58.0` + `patch-protocol.mjs` for Finalmouse xpanel/Starlight X/TMR-DS), **Wootility** (see `improve/wooting-report.md`), and core device settings of **Razer Synapse**, **Logitech G HUB**, **SteelSeries GG**, **Corsair iCUE**, and Pulsar/Lamzu/Ninjutso web configurators (`Services/MousePanel*.cs`, `MouseCore*.cs`, `MouseDiagnostics*.cs`, `UsbPorts*.cs`, `Services/Hid/`)
- **Anti-Cheat Class:** `AC-SAFE` (100% standard OS USB HID `HidD_SetFeature`/`ReadFile`/`WriteFile` to device firmware + read-only USB topology/Raw Input polling diagnostics — zero game injection!)
- **License:** MIT (100% clean-room Rust — permanently eliminates the AGPL-3.0 `mouse/` iframe, Node/Vite build, and `exo-mouse-<v>.tar.gz` source-tarball obligation!)

---

## 1. Scope

### What It Solves
1. Exo Mouse currently ships as a separate **AGPL-3.0** React app in `mouse/` running inside an iframe on `mouse.exo-launcher.local`, relying on `@openmouse/protocol` with fragile build-time patches (`patch-protocol.mjs`), forcing Exo v4's native Rune UI to either embed a WebView just for mouse settings or maintain two UI stacks.
2. Gamers still have to install **300–1,000+ MB** of bloated vendor daemons (Synapse, G HUB, iCUE, Wootility, Finalmouse web xpanel) just to change DPI, polling rate, Rapid Trigger, actuation point, debounce, or check battery!

### In Scope
- **Native Rust HID Transport (`exo-hid-core`):** Pure Win32 `SetupAPI` + `hid.dll` (`HidD_*`, overlapped async `ReadFile`/`WriteFile`) with hot-plug `WM_DEVICECHANGE` detection and strict `MockHidTransport` trait so **100% of protocol write tests run against emulated devices without ever touching real hardware**.
- **Clean-Room Vendor Protocol Drivers (Prioritized Wave 1):**
  1. **Finalmouse** (ULX, Starlight-12, Starlight X / TMR-DS — DPI, polling up to 8000 Hz, LOD, Motion Sync, Ultra Click / debounce, receiver LED color & battery, factory-reset guard).
  2. **Wooting** (60HE/60HE+, 80HE, Two HE — per-key actuation 0.1–4.0 mm, Rapid Trigger sensitivity, Continuous RT, Tachyon 8 kHz mode, SOCD/Rappy Snappy profile switching, per-profile RGB; grounded in `improve/wooting-report.md`).
  3. **Logitech HID++ 2.0** (G Pro X Superlight 1/2, G305, G502X — DPI stages, onboard memory profiles, report rate up to 8 kHz, battery %).
  4. **Razer USB HID** (Viper V3 Pro, DeathAdder V3, Huntsman V3 Pro — DPI, polling, Smart Tracking/LOD, battery, HyperPolling dongle).
  5. **Universal Mice (CompX / PixArt / Nordic OEMs):** Pulsar, Lamzu, Ninjutso, Endgame Gear, WLMouse, VXE/ATK.
- **Native Hardware Diagnostics & Tuning (Ported from C#):**
  - `UsbPorts`: PCI ID + xHCI topology detector identifying **CPU-Direct USB ports** vs chipset/hub ports.
  - `MouseDiagnostics`: Real-time Raw Input polling rate & micro-jitter analyzer (detects USB hub contention / wireless interference).
  - `MouseCore`: USB xHCI controller IRQ CPU-core affinity optimizer (moves 4–8 kHz polling interrupts off Core 0).
- **Rendered natively in Rune (`exo-ui/src/devices.rs`)** using `exo-kit` Void/Liquid Glass tokens — zero WebView, zero iframe, zero AGPL!

### Out of Scope
- Copying any code from AGPL-3.0 `@openmouse/protocol` or `apps/launcher/mouse/` (all protocol packets MUST be implemented clean-room from public hardware protocol docs, USB HID descriptors, and our own `wooting-report.md`!).
- Firmware flashing in Wave 1 (high brick risk; Wave 1 is strictly volatile/onboard config reads & writes with explicit schema validation).

---

## 2. Native Rust Crate Layout

```text
crates/exo-hid-devices/
├── Cargo.toml                  # windows (Hid, SetupApi, RawInput, Registry), serde, parking_lot
├── src/
│   ├── lib.rs                  # DeviceManager + hotplug event stream -> exo-ui
│   ├── transport.rs            # Trait HidTransport + Win32HidTransport + MockHidTransport (for tests!)
│   ├── registry.rs             # VID/PID -> driver matcher + capability flags
│   ├── drivers/
│   │   ├── finalmouse.rs       # Clean-room Finalmouse USB/wireless dongle protocol (incl. TMR-DS)
│   │   ├── wooting.rs          # Clean-room Wooting HID feature/output report driver (actuation, RT, Tachyon)
│   │   ├── logitech_hidpp.rs   # Logitech HID++ 2.0 feature discovery (0x1000/0x1004 battery, 0x2201 DPI, 0x8060 rate)
│   │   ├── razer.rs            # Razer 90-byte HID control report driver (DPI, poll, battery, LOD)
│   │   └── oem_nordic.rs       # Common Nordic/CompX wireless mouse family
│   ├── usb_topology.rs         # CPU-direct USB root-hub / xHCI PCI bus tree analyzer
│   ├── poll_diag.rs            # Lock-free Raw Input 1kHz–8kHz polling jitter histogram
│   ├── irq_core.rs             # xHCI interrupt CPU affinity helper (port of MouseCore)
│   └── auto_profiles.rs        # Per-game DPI/polling/RT/actuation auto-switcher with write-loop hysteresis!
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config Per-Game Hardware Switching:** Launches Valorant/CS2 → automatically switches keyboard to **0.1 mm Rapid Trigger + Tachyon mode** and mouse to **competitive DPI + 2k/4k/8k Hz**; exits to desktop → automatically restores **1.8 mm typo-free typing actuation** and **1000 Hz battery-saving mouse mode**!
2. **Write-Loop-Proof Hysteresis:** Debounces foreground focus transitions (500 ms settle) and skips redundant HID writes if device state already matches target (preventing EEPROM wear and USB micro-stutters — learning from `UhhErix/mouse-write-loop`!).
3. **Proactive Hardware Health Warnings:** Automatically detects if a 4k/8k Hz mouse is plugged into a chipset/external hub instead of a CPU-direct USB port, if polling jitter exceeds 15%, or if battery drops `<15%` — surfacing a single quiet native toast in Rune.
4. **Vendor Bloat Retirement:** Adds Razer Synapse, Logitech G HUB, SteelSeries GG, Corsair iCUE, and Wootility to `ReplacedApps` suggestions once all of the user's plugged peripherals are natively supported by `exo-hid-devices`!

---

## 4. Measurement Plan

All write paths tested strictly against `MockHidTransport` packet-capture fixtures; read-only enumeration benchmarked safely on local host:

| Metric | Baseline (Current `mouse/` AGPL iframe + vendor suites) | Target (`exo-hid-devices`) | How Measured |
| --- | --- | --- | --- |
| **Devices Room Cold Open** | WebView2 iframe spawn (~400–800 ms, +250 MB WS) | **<15 ms** in Rune, **<5 MB** RAM | Rune frame timer + `GetProcessMemoryInfo` |
| **HID Packet Round-Trip (Mock & Read-Only)** | `postMessage` JS → C# → Win32 HID (~3–8 ms) | **<0.5 ms** direct Rust Win32 HID | QPC benchmark over overlapped HID reads |
| **RAM Saved vs Vendor Suites** | Synapse/G HUB/iCUE: **300–1,000+ MB** resident | **<5 MB** total inside `exo-ui` (**0** resident daemon) | Process memory comparison |
| **AGPL / Node Dependencies** | AGPL-3.0 `mouse/` + separate `npm ci` + R2 tarball | **0** (100% MIT Rust) | `cargo-deny` + build script audit |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-hid-devices --locked -- -D warnings`, `cargo test -p exo-hid-devices --locked`.
2. **Mock Protocol Golden Tests:** Every driver (`finalmouse`, `wooting`, `logitech_hidpp`, `razer`, `oem_nordic`) passes golden request/response byte-vector tests via `MockHidTransport` including CRC/checksum validation, timeout retry, and malformed-packet rejection.
3. **Write-Loop Guard Test:** Rapidly toggling game focus 20 times in 2 seconds issues at most 1 HID write after hysteresis settles and **0** writes when target equals cached hardware state.
4. **License Gate:** Zero AGPL/GPL dependencies in `Cargo.lock`; zero imports from `apps/launcher/mouse/`.

---

## 6. Safety Rules & Hard Stops

- **CRITICAL HARD STOP — NEVER WRITE TO THE OWNER'S REAL HARDWARE:** Every automated test, lab run, and agent verification MUST use `MockHidTransport` (or strictly read-only `HidD_GetAttributes` / `SetupAPI` enumeration). NEVER send HID write/feature reports or factory resets to real connected devices!
- **EEPROM Wear Guard:** Persist profile changes to volatile RAM where hardware supports it; only commit to flash/EEPROM on explicit user Save or rate-limited profile switch, with write-deduplication.
- **No Firmware Flashing in Wave 1:** Reject any bootloader-entry HID commands at compile time until dedicated brick-proof recovery testing exists.
- **Clean-Room Isolation:** Do NOT copy source code from AGPL `mouse/` or `@openmouse/protocol`.
