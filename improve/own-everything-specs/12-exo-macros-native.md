# Spec 12 — Exo Macros Native (`exo-macros`): Anti-Cheat-Safe Per-Device Raw Input & Onboard HID Macro Engine

- **Inventory Rank:** #12 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** **AutoHotkey v2**, **Razer Synapse Macros**, **Logitech G HUB Macros**, **SteelSeries Engine Macros**, **Corsair iCUE Macros** (grounded in `lab/lab-macros:improve/lab-macros-report.md`)
- **Anti-Cheat Class:** Split strictly into two tiers:
  - **Onboard HID Hardware Macros** (written to supported mouse/keyboard firmware via Spec #05) & **Desktop/Productivity Software Macros**: **`AC-SAFE`**.
  - **Software Input Synthesis (`exo-macros-worker.exe`):** Strictly **auto-suspended** whenever ANY `AntiCheatCheck.AssumeProtected` game (`Found`, `PossiblyVac`, `Unknown`, `Riot`) is in foreground! Allowed in-game **only** for `AntiCheatCheck.Kind.None` single-player/offline games (and strictly banned from rapid-fire/recoil-compensation presets).
- **License:** MIT (clean-room Rust)

---

## 1. Scope

### What It Solves
1. Users keep AutoHotkey scripts or 500 MB vendor suites (Synapse, G HUB, iCUE) running 24/7 just for productivity chords, Discord push-to-mute/clip hotkeys, media layers, or single-player RPG accessibility macros.
2. Standard macro tools cannot distinguish *which* physical device pressed a key (e.g., macro pad vs main keyboard) and have **zero anti-cheat awareness**, exposing users to accidental kicks/bans when a desktop macro fires mid-match!
3. Exo's strict security invariant (`AGENTS.md`, `BannedSymbols.txt`) forbids `SendInput`/`mouse_event`/`keybd_event` inside `ExoLauncher.exe` and `exo-ui.exe`.

### In Scope
- **Split Architecture (Preserving the `BannedSymbols` Firewall):**
  1. **Control Plane (`crates/exo-macros`, linked in host/UI — ZERO `SendInput` imports!):**
     - Macro storage (`macros/macros.json`, synced in `PlayerData`), deterministic high-resolution QPC scheduler, chord matcher, layer stack (`once`, `repeat`, `hold`, `toggle`), per-game profile switcher with hysteresis, and **anti-cheat foreground watchdog**.
     - Onboard HID macro compiler pushing simple sequences directly to supported hardware memory via `exo-hid-devices` (Spec #05) when available.
  2. **Isolated Execution Sidecar (`exo-macros-worker.exe` — spawned ONLY when software macros are enabled & active!):**
     - Per-device identification via Win32 **Raw Input (`WM_INPUT`, `RIDEV_INPUTSINK`)** mapping `header.hDevice` → stable USB `VID/PID` instance path (`"vid_046d_pid_c094"`).
     - No-hook fast path (`swallowInput = false`): pure read-only Raw Input (zero global LL hooks!).
     - Optional minimal `WH_KEYBOARD_LL` / `WH_MOUSE_LL` hook correlated against a lock-free 64-entry Raw Input ring buffer (2 ms QPC window) ONLY when a binding requires key swallowing.
     - `GameInput` controller chord triggers (e.g., DualSense Edge `FnL + D-pad` triggers instant replay save or music skip!).
     - Isolated high-priority message loop so UI frames never jitter macro timing.

### Out of Scope
- Putting `SendInput` / `SetWindowsHookEx` inside `ExoLauncher.exe` or `exo-ui.exe`.
- Recoil scripts, pixel-bots, or bypassing anti-cheat input filtering (`AC-HARD-STOP`).

---

## 2. Native Rust Crate Layout

```text
crates/exo-macros/              # Control plane (ZERO SendInput / hook imports!)
├── Cargo.toml                  # serde, parking_lot
├── src/
│   ├── lib.rs
│   ├── model.rs                # MacroDef, Trigger (deviceId, chord, pad), Action, Layer, Mode
│   ├── scheduler.rs            # Pure QPC deterministic step scheduler + DryRunSink (for unit tests!)
│   ├── chord.rs                # Multi-key & controller chord window matcher + layer stack
│   ├── policy.rs               # Foreground AntiCheatCheck gate (auto-pauses worker on AssumeProtected!)
│   └── switcher.rs             # Debounced, hysteresis-guarded per-game profile switcher

crates/exo-macros-worker/       # Separate binary exo-macros-worker.exe (spawned ONLY on demand!)
├── Cargo.toml                  # windows (RawInput, Input, WindowsAndMessaging), exo-macros
├── src/
│   ├── main.rs                 # Named-pipe / exo-wire listener from Exo control plane
│   ├── raw_input.rs            # HWND_MESSAGE WM_INPUT + GetRawInputDeviceInfoW -> device VID/PID map
│   ├── hook_correlator.rs      # Lock-free 64-slot ring buffer correlating LL hook <-> Raw Input hDevice (2 ms window)
│   └── output_sink.rs          # SendInput scancode output (hard-gated by policy.rs AC pause flag!)
```

---

## 3. Automation & Intelligence Design

1. **Zero Idle Overhead:** When the user has no active software macros (or uses only onboard hardware profiles), **`exo-macros-worker.exe` does not even run** (0 MB RAM, 0 global hooks!).
2. **Automatic Anti-Cheat Safety Shield:** Whenever ANY `AntiCheatCheck.AssumeProtected` game takes foreground focus, the control plane instantly signals `exo-macros-worker.exe` to drop all hooks and suspend output (except safe Exo internal actions like Save Replay / Mute Discord via IPC, which need zero `SendInput`!).
3. **Built-In Smart Templates:** One-click zero-script templates for what gamers actually want: *"Mute Discord on side button"*, *"Save Instant Replay on Edge FnL+Share"*, *"Skip Spotify track on F13"*, *"Single-player accessibility auto-sprint toggle"* — zero AHK scripting required!
4. **Per-Device Awareness:** Bindings on a secondary macro pad or mouse never hijack keys on the primary keyboard.

---

## 4. Measurement Plan

Measured via deterministic `DryRunSink` timing benchmarks + isolated sidecar loopback test (zero real `SendInput` to desktop apps!):

| Metric | Baseline (AutoHotkey v2 / Razer Synapse) | Target (`exo-macros`) | How Measured |
| --- | --- | --- | --- |
| **Idle Overhead (No Active Software Macros)** | Synapse: 400+ MB; AHK: ~10 MB + global hook | **0 MB RAM / 0 hooks** (worker not spawned) | Process table check |
| **Active Worker Footprint** | Synapse: 400+ MB | **<3 MB WS / <0.1% CPU** | `GetProcessMemoryInfo` + `GetProcessTimes` |
| **QPC Step Timing Jitter (10 ms step)** | JS/C# timer ~1–15 ms | **<0.5 ms** p99 jitter | QPC timestamp log in `DryRunSink` |
| **Anti-Cheat Pause Latency on Focus Change** | None in AHK/Synapse! | **<10 ms** hook teardown upon `AssumeProtected` focus event | Automated mock focus-switch harness |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-macros -p exo-macros-worker --locked -- -D warnings`, `cargo test --locked`.
2. **Banned-Symbols Firewall Gate:** Verify `ExoLauncher.exe`, `exo-ui.exe`, and `exo-macros` lib import **ZERO** `SendInput`, `mouse_event`, `keybd_event`, or `SetWindowsHookEx` symbols (`dumpbin /imports` + `ExoLauncher.Tests/BannedNativeImportsTests`).
3. **Anti-Cheat Enforcement Gate:** Unit + integration tests prove `output_sink.rs` drops 100% of synthetic inputs while any `Found`/`PossiblyVac`/`Unknown`/`Riot` game is foreground.
4. **Infinite-Loop / Stuck-Key Safety Gate:** Panic/drop handler + `Esc+Pause` emergency panic chord releases every held virtual key automatically.

---

## 6. Safety Rules & Hard Stops

- **Preserve `BannedSymbols` Firewall:** `SendInput` lives strictly in `exo-macros-worker.exe`, never in the host or UI.
- **Zero Anti-Cheat Tampering:** Automatically unhook `WH_*_LL` and halt `SendInput` whenever an online/unknown/Riot game has focus.
- **No Competitive Cheat Primitives:** Explicitly disallow recoil-compensation loops, rapid-fire macros faster than human thresholds in games, or screen-reading auto-aim triggers.
- **Never Write to Real Hardware or Inject Real Keystrokes** in agent tests (`DryRunSink` only!).
