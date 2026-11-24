# Exo Lab: Universal Macros (`lab-macros`)

## 1. Executive Summary

Exo Universal Macros delivers one unified macro, remapping, layer, and per-game/per-app profile system for **any mouse, keyboard, or controller from any brand** with zero vendor bloatware.

Why this beats the incumbent tools for this job:

| Tool | What it does well | Where Exo beats it |
| --- | --- | --- |
| **Razer Synapse / Logitech G Hub** | Per-device onboard memory, G-Shift / Hypershift layers | Vendor-locked, heavy background suites (200–600 MB RAM), no cross-brand or controller chords. Exo works across any hardware, compiles to onboard memory where Exo already speaks HID (`@openmouse/protocol`, Wooting HID, DualSense Edge), and stays near 0% idle CPU. |
| **Wootility** | Best-in-class onboard Wooting keyboard tuning & rapid trigger | Keyboard-only, no cross-device macros (mouse + controller + keyboard chords), no per-game profile automation. Exo reuses `WootingKeyboard.cs` and adds universal cross-device macros. |
| **Steam Input** | Rich controller layers, chords, action sets | Controller-centric, requires Steam overlay/client hook, weak mouse/keyboard macro timeline. Exo works store-agnostically (Steam, Epic, GOG, Riot, Xbox, local) with zero overlay injection. |
| **reWASD** | Cross-device remapping & virtual controllers | Paid closed-source app whose virtual bus / mapper driver is actively blocked or flagged by strict anti-cheats (EA AC, Ricochet, Vanguard, FACEIT). Exo ships **no kernel filter driver** and enforces a transparent no-block anti-cheat policy. |
| **AutoHotkey** | Scriptable, zero vendor lock-in | Text-script UX, no visual recorder/timeline, no per-device VID/PID binding out of the box, no onboard memory compilation, no anti-cheat awareness or game profile debounce. Exo gives a sub-millisecond visual recorder + timeline + per-device bindings + onboard path. |

---

## 2. Architecture & Process Boundary

Exo Launcher's host binary (`ExoLauncher.exe`) is strictly barred from declaring `SendInput`, `mouse_event`, or `keybd_event` P/Invokes (`ExoLauncher.Tests/BannedNativeImportsTests.cs` and `BannedSymbols.txt`) — the same architectural firewall that isolates screen capture inside `capture/` (`ExoCapture.exe`).

Universal Macros preserves that boundary cleanly:

1. **Control plane in `ExoLauncher` (`Services/Macros/`):**
   - `MacroService`: owns persistence (`macros/macros.json`, registered in `PlayerData.cs` for backup/restore), device capability discovery (onboard vs software), per-game anti-cheat policy evaluation (`AntiCheatCheck`), recording session control, and RPC dispatch (`WebHostBridge.Macros.cs`).
   - `MacroProfileSwitcher`: debounced, hysteresis-guarded, write-loop-proof profile switcher wired directly into `LaunchOrchestrator` game run-state events (`WebHostBridge.cs:OnGameRunStateChanged`) and foreground executable tracking.
   - `MacroScheduler` + `MacroChordMatcher`: high-resolution QPC (`Stopwatch.GetTimestamp()`) timing engine, layer stack, chord window matcher, playback modes (`once`, `repeat`, `hold`, `toggle`), and dry-run timing verifier (`IMacroOutputSink`).
   - **Zero banned native imports in `ExoLauncher.exe`:** verified by `BannedNativeImportsTests`.
2. **Execution plane (`ExoMacros.Service.exe` sidecar — designed for 3.x opt-in / v4):**
   - Separate lightweight native/C# worker process communicating with `ExoLauncher` over the same named-pipe/NDJSON pattern used by `ExoCapture` and `ExoShaders.Service`.
   - Loaded **only** when software macros are enabled and active. When idle or when using onboard device memory only, zero hooks and zero background sidecar processes run.
   - Isolated high-priority message loop (`WM_INPUT`, `WH_KEYBOARD_LL` / `WH_MOUSE_LL`, `GameInput`) so UI frames or GC pauses in the launcher never introduce input jitter.
3. **Dual UI over one `host-v1.json` contract:**
   - **React UI (`ui/src/components/devices/MacrosPage.tsx` + `ui/src/styles/macros.css`):** integrated into `DevicesRoom`, built strictly with Exo tokens, `Switch`, `.exo-set-btn`, and Exo Lucide icons.
   - **Native Rune UI sketch (`native-ui/crates/exo-ui/src/macros.rs` + `exo-rpc/src/macro_types.rs`):** integrated into `devices_room` in `pc.rs`, using `exo-kit` tokens and staying within `MAX_BACKDROP_BLURS <= 4`.

---

## 3. Input Capture Design

### 3.1 Keyboard & Mouse Capture + Per-Device Identification
- **Primary device identity (`WM_INPUT` / Raw Input):** Register `RIDEV_INPUTSINK` on a dedicated message-only window (`HWND_MESSAGE`). Every `RAWINPUT` packet carries `header.hDevice`, mapped via `GetRawInputDeviceInfoW(RIDI_DEVICENAME)` to a stable hardware instance path (`VID_xxxx&PID_xxxx&...`) and USB serial/container ID. This allows a binding to lock onto **one specific mouse or keyboard** (`deviceId: "vid_046d_pid_c094"`) or match any device (`deviceId: null`).
- **Trigger suppression (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`):** Raw Input is read-only and cannot swallow the original keystroke. When `swallowInput: true`, a minimal LL hook sits in the sidecar and correlates each event against a lock-free 64-entry ring buffer populated by `WM_INPUT` (matching scancode/button + QPC timestamp within a 2 ms correlation window) to identify `hDevice` before deciding whether to swallow.
- **No-hook fast path:** Bindings with `swallowInput: false` (e.g., overlay triggers, clip hotkeys, text expanders on F13–F24) run purely on Raw Input with zero global hooks installed.

### 3.2 Controller Capture Stack
| API | Scope | Polling / Latency | Used for |
| --- | --- | --- | --- |
| **GameInput** (Windows 11 preferred) | Unified Xbox, PlayStation, Switch, generic HID, keyboard & mouse | Sub-ms, timestamped reading stream (`GetCurrentReading` / `RegisterReadingCallback`) | Primary controller capture on Windows 11 x64 (lowest jitter, zero polling loop). |
| **Raw HID (`HidService` + `ControllerLab`)** | DualSense, DualSense Edge, DualShock 4, custom pads | Native USB 250–1000 Hz report rate | Already in Exo! Exposes DualSense Edge **FnL / FnR / BackL / BackR** paddles (`ControllerLab.FnLeft..BackRight`), touchpad, and gyro as macro triggers. |
| **XInput (`xinput1_4.dll`)** | Xbox 360 / Series / XInput clones (4 slots) | 125–1000 Hz (polled on timer only while pad connected) | Universal fallback when GameInput runtime is unavailable. |
| **Windows.Gaming.Input (WGI)** | `RawGameController` / DirectInput-class sticks, wheels, flight sticks | Event + polled reading | Fallback for non-XInput / non-Sony specialty controllers. |

---

## 4. Output & Input-Driver Route Comparison

| Route | Signing & Distribution | Stability & Maintenance | Latency | UIPI (Elevated Games) | Anti-Cheat Treatment | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| **Onboard Device Memory** (`@openmouse/protocol`, `WootingKeyboard.cs`, `ControllerLab`) | No driver needed (standard HID) | High; reuses Exo's tested HID protocols; survives with Exo closed | **0 ms host overhead** (firmware-native) | **Bypasses UIPI** (real USB HID) | **No software injection risk** (standard peripheral firmware; game ToS still applies to automation) | **Tier 1 (Default wherever supported)** |
| **User-mode `SendInput` (in sidecar)** | No kernel driver; EV code-sign + `uiAccess="true"` in `Program Files` for elevated windows | Very high; zero BSOD risk; zero reboot | **< 0.1 ms** syscall + **0.6 µs** median scheduler error | Blocked (`ERROR_ACCESS_DENIED = 5`) unless sidecar has `uiAccess="true"` or matching elevation | Sets `LLKHF_INJECTED` / `LLMHF_INJECTED`; transparent; **instant-ban risk** in Vanguard/Ricochet/VAC/FACEIT/EAC/BattlEye/ACE/Hyperion | **Tier 2 (3.x offline & single-player games only, with one-time note; hard-disabled in online/AC/unknown games)** |
| **Windows VHF (`vhf.sys`) + UMDF2/KMDF Virtual Gamepad** | Microsoft in-box `vhf.sys`; custom source driver requires EV cert + Microsoft Partner Center WHQL attestation | High (official MS Virtual HID Framework, e.g. Sunshine/`libvirtualhid` pattern); UMDF2 isolates crashes from kernel | **~0.2–0.5 ms** IOCTL -> `VhfReadReportSubmit` | Bypasses UIPI (kernel HID device) | Cleanly signed, no kernel filter hooks, **still visible** in device tree (`vhf.sys` parent) — flagged by strict ACs if used for KBM-to-pad aim assist | **v4 Optional Add-On** (consent-gated install like Exo Capture; MIT/open-source friendly) |
| **Nefarius VirtualPad** (ViGEmBus successor) | WHQL-signed commercial runtime | Actively maintained; Xbox 360 + DualSense emulation | **~0.2–0.5 ms** | Bypasses UIPI | Proprietary/commercial license (incompatible with Exo's MIT distribution); flagged by EA AC / Vanguard / FACEIT | **Optional detection only** if user already installed it; do not bundle |
| **ViGEmBus (`v1.22.0`)** | Legacy signed binary | **Officially retired & archived Nov 2023** (trademark conflict); unmaintained | ~0.3–0.5 ms | Bypasses UIPI | Frequently blocked/flagged by EA AC, FACEIT, Vanguard | **Do not adopt** |
| **WinRT `InputInjector.InjectGamepadInput`** | Built into Windows (no driver) | Preview/UWP-oriented; requires `inputInjectionBrokered` / packaged context; inconsistent across Win32 XInput titles | ~0.5–1.5 ms | Restricted by broker | Still synthetic injection | **Not viable as universal virtual pad** |
| **Interception (`interception.sys`)** | Old third-party signed kernel filter driver | Hard-coded 10-device limit, aging codebase, reboot required | < 0.1 ms | Bypasses UIPI | **Hard-blocked on boot/launch** by Vanguard, FACEIT, EA AC, ACE, iRacing — breaks games merely by being installed | **Hard reject — never install or support** |
| **Custom KMDF Keyboard/Mouse Filter** | Expensive EV + WHQL attestation; continuous Windows update maintenance | High BSOD/regression surface | < 0.1 ms | Bypasses UIPI | Immediate AC scrutiny & trust-model conflict with Exo's non-invasive stance | **Hard reject** |

**Virtual Controller Recommendation:** ViGEmBus is retired and VirtualPad is proprietary/commercial. Do **not** bundle any virtual controller kernel driver in Exo 3.x (avoiding anti-cheat collateral damage for all users). For **v4**, build a clean, open-source, WHQL-signed **VHF (`vhf.sys`) UMDF2 virtual gamepad add-on** installed only with explicit user consent — matching the exact optional add-on model of Exo Capture.

---

## 5. Features & Profile Switcher Anti-Flap Design

### 5.1 Implemented Schema & Engine (`ExoLauncher/Services/Macros/`)
- **Exact-timing Recorder & Timeline Editor:** QPC timestamps (`Stopwatch.GetTimestamp()` in host, `performance.now()` in UI preview), ordered editable steps covering `keyDown`, `keyUp`, `mouseDown`, `mouseUp`, `mouseMove`, `mouseWheel`, `padButtonDown`, `padButtonUp`, `padAxis`, `delay`, `text`, `launchApp`, and `layer`.
- **Playback Modes:** `once`, `repeat` (1–100), `hold`, and `toggle`.
- **Randomization (`jitterMs`):** **Off by default (`0 ms`)**. Bounded optional timing variance (0–50 ms), never marketed or designed as anti-detection.
- **Layers & Shift Modifiers:** Base layer plus Hold (G-Shift / Fn style) and Toggle layers per profile.
- **Cross-Device Remapping & Chords:** Any input source (`keyboard`, `mouse`, `controller`) can trigger any output target across device types, with optional per-device `deviceId` lock and multi-input chord matching within a configurable `chordWindowMs` (default `45 ms`).

### 5.2 Preventing the Profile-Switch Write Loop (`MacroProfileSwitcher.cs`)
Today's mouse write-loop (`mouse/src/Profiles.tsx`) stemmed from un-debounced `game.runState` transitions firing `land()` (20 retries × 3 s) immediately on every process transition or readback mismatch. `MacroProfileSwitcher` eliminates this class of bug structurally:

1. **Settle Debounce (`350 ms`):** Candidate profile changes from `ObserveGameState` and `ObserveForegroundApp` queue a pending transition; any oscillation before 350 ms resets or cancels the pending switch (`SuppressedFlaps++`) with zero applied writes.
2. **Exit Grace Hold / Hysteresis (`1500 ms`):** When an active game's process exits (common during launcher -> game binary handoffs or anti-cheat bootstrap restarts), the switcher holds the game profile for 1.5 s before falling back to Desktop/app profile. If the game reappears within the grace window, zero profile switches occur.
3. **Idempotent Active Deduplication & Onboard SHA-256 Hash:** If the resolved profile matches the active profile — or if the new profile's onboard bindings hash (`ComputeOnboardHash`) equals what is already on the device — **zero hardware writes** are emitted.
4. **Hardware Write Cooldown & Rate Circuit Breaker:** Minimum `2000 ms` cooldown between onboard writes and a hard ceiling of `10 writes/minute`, preventing flash wear loops even under pathological OS process flapping.

---

## 6. Latency & CPU Measurements (Real Hardware Evidence)

Measured directly on this Windows 11 x64 host (`Microsoft Windows NT 10.0.26300.0`, 12 logical cores, QPC frequency `10,000,000 Hz`, unloaded CPU, 2026-11-24):

| Benchmark | Samples | P50 | P95 | P99 | Max | Notes |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| **Naive `Thread.Sleep(5)` error** | 60 | `4,599.6 µs` (4.60 ms) | `12,001.5 µs` | `13,484.9 µs` | `13,484.9 µs` | Unusable for sub-ms macros on default Windows 15.6 ms tick. |
| **WaitableTimer alone (0.45 ms spin, heavy CPU load)** | 240 | `9,356.0 µs` | `42,962.6 µs` | `80,028.6 µs` | `87,027.3 µs` | Exposed Windows 11 background timer-resolution throttling (`IGNORE_TIMER_RESOLUTION`). |
| **Exo `MacroScheduler` (upgraded)** | 210 (`1–16 ms` steps) | **`0.6 µs`** | **`1.0 µs`** | **`1.1 µs`** | **`1.1 µs`** | `SetProcessInformation(ProcessPowerThrottling)` opt-out + `timeBeginPeriod(1)` + `CreateWaitableTimerExW(HIGH_RESOLUTION)` + `2.0 ms` hybrid spin + `ThreadPriority.Highest`. **>900× better than 1 ms target.** |

### CPU Overhead (Measured over 2.0 s windows on 12 logical cores)
- **Idle (no active macro playback):** **`0.0000%` total CPU** (`0.0000%` single core). Timer period `timeBeginPeriod(1)` is only acquired during active playback and released in `finally`, so Exo adds zero timer-tick or battery penalty at rest.
- **Active 100 Hz continuous macro playback (10 ms cadence, 2.0 ms spin cushion):** **`1.628%` total CPU** (`19.53%` of 1 core). At typical macro burst rates (5–20 steps/s) or with a `1.2 ms` spin cushion, active total CPU stays well under `0.5%`.
- **UIPI finding on `SendInput`:** Calling `SendInput` from a non-elevated worker session returned `0` with `GetLastWin32Error() = 5 (ERROR_ACCESS_DENIED)` due to Windows UIPI isolation. Production `ExoMacros.Service.exe` must be Authenticode-signed with `uiAccess="true"` (installed under a trusted path) for software injection into elevated windows — whereas **onboard device memory remaps bypass UIPI completely**.

---

## 7. Anti-Cheat & Fairness Policy (Owner Clarified Rule — 2026-11-24)

Because software-injected input or mods in competitive titles like Call of Duty (Ricochet) and VALORANT (Vanguard) carry an instant-ban risk, Exo enforces a **hard tier boundary** backed by `Services/AntiCheatCheck.cs` (`MacroPolicy.cs`):

- **Never touch or work around anti-cheat.** No stealth hooks, no `LLKHF_INJECTED` stripping, no anti-detection modes, no kernel bypasses.
- **Online, competitive anti-cheat, and unknown games -> Tier 1 onboard hardware ONLY:**
  - **Tier 2 software macros (`SendInput` sidecar) are completely disabled — not even behind a confirmation or override.**
  - **Only Tier 1 onboard device memory macros (`delivery: "onboard"`) are allowed**, explicitly labelled in both UIs as **hardware**.
  - **Unknown / unreadable installs (`Kind.Unknown`) and `PossiblyVac` titles are treated as online** -> Tier 1 onboard hardware only.
- **Offline & single-player games (`Kind.None` and not Riot) -> Tier 1 + Tier 2 allowed:**
  - Keep **Tier 2 software macros** with a **one-time note** (`OfflineOneTimeNote`), plus full Tier 1 onboard hardware support.

### Anti-Cheat Matrix

| Anti-Cheat / Category | Example Titles | Tier 2 Software (`SendInput`) | Virtual Controller Driver | Tier 1 Onboard Hardware | Exo Policy (`MacroPolicy.cs`) |
| --- | --- | --- | --- | --- | --- |
| **Riot Vanguard** | VALORANT, League of Legends | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **Ricochet** | Call of Duty, Warzone | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **VAC / CS2 & FACEIT** | Counter-Strike 2, TF2, Dota 2 | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **Easy Anti-Cheat (EAC)** | Apex Legends, Fortnite, Rust (online MP) | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **BattlEye** | Rainbow Six Siege, Tarkov, DayZ, PUBG | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **Always-Online ACE** | Wuthering Waves (WuWa), Delta Force, Arena Breakout | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **Hyperion** | Roblox | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **EA AntiCheat / Warden / Other Online AC** | EA FC, Battlefield, WoW | **Disabled (no override)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **Unknown / Unreadable / Possibly VAC** | Any install Exo cannot verify as offline/clean | **Disabled (treated as online)** | **Disabled** | **Allowed (labelled hardware)** | `onboard-only`, `softwareAllowed: false` |
| **Offline & Single-Player (Clean)** | Stardew Valley, Cyberpunk 2077, Baldur's Gate 3 | **Allowed (one-time note)** | **v4 optional add-on** | **Allowed (labelled hardware)** | `software-and-onboard`, `softwareAllowed: true` |

---

## 8. Shipping Roadmap: 3.x vs v4

### Ships in 3.x (Zero Kernel Driver Burden, Immediate Value)
1. **Host `MacroService` + `MacroProfileSwitcher` + `MacroScheduler` + `host-v1.json` RPC** (prototype complete on `lab/lab-macros`).
2. **React Macros UI in `Devices › Macros`** (profiles, layers, chords, timeline editor, exact recorder, Tier 1 hardware vs Tier 2 software badges, hard online/AC block with no override, offline one-time note).
3. **Tier 1 Onboard Memory Compilation (Zero-Software Path):** Compile 1:1 remaps and device macros directly to Wooting (`WootingKeyboard.cs`), Exo Mouse (`@openmouse/protocol`), and DualSense Edge (`ControllerLab`) profiles using the debounced, cooldown-protected `MacroProfileSwitcher`. *(Note: the existing mouse profile write-loop is already addressed separately by the `mouse-write-loop` hotfix `ctx_67711086e33b`; no backport to `mouse/src/Profiles.tsx` is needed from this lab.)*
4. **Opt-in Out-of-Process `ExoMacros.Service.exe` (Tier 2 `SendInput` + Raw Input + GameInput):** Separate signed sidecar binary (`uiAccess="true"` in `Program Files`) for **offline and single-player games** and desktop apps; automatically disabled whenever an online, competitive anti-cheat, or unknown game is active.

### Ships in v4 (Native UI + Virtual Controller Add-On)
1. **Native Rune UI Macros Room (`native-ui/crates/exo-ui/src/macros.rs`):** Full interactive timeline, drag-reorder, live device picker, and live latency histogram in Rune (`wgpu` DX12).
2. **Optional WHQL-Signed VHF (`vhf.sys`) UMDF2 Virtual Gamepad Add-On:** Separate consent-gated installer (matching `capture/`), enabling full cross-device remapping to virtual Xbox/DualSense outputs (keyboard/mouse -> controller stick/button) without proprietary VirtualPad licensing or deprecated ViGEmBus.
3. **Stick-to-Mouse & Gyro-to-Mouse Curves:** Sub-ms GameInput / DualSense raw HID response curves, deadzones, and dynamic sensitivity layers.
4. **Community Profile Share (Sanitized JSON Import/Export):** Portable macro/layer packs stripped of local paths and per-machine device instance IDs.

---

## 9. Prototype Summary & Verification

### Implemented on `lab/lab-macros`
- **Host service & RPC:** `ExoLauncher/Services/Macros/{MacroModels,MacroPolicy,MacroProfileSwitcher,MacroScheduler,MacroService}.cs`, `ExoLauncher/Services/WebHostBridge.Macros.cs`, wired into `AppServices.cs`, `WebHostBridge.cs`, and `PlayerData.cs` (`macros/macros.json`).
- **Contract:** `ui/contracts/host-v1.json` regenerated (`350` methods, `79` events).
- **React UI:** `ui/src/components/devices/MacrosPage.tsx`, `ui/src/styles/macros.css`, wired into `DevicesRoom.tsx` and `ui/src/lib/host.ts`.
- **Native Rune UI sketch:** `native-ui/crates/exo-rpc/src/macro_types.rs`, `native-ui/crates/exo-ui/src/macros.rs`, wired into `exo-rpc/src/lib.rs`, `exo-ui/src/{main,pc,pc_state}.rs`.
- **Tests:** `ExoLauncher.Tests/MacroEngineTests.cs` (10 tests: sub-ms timing accuracy, randomization off-by-default/bounded, repeat count, chord/device/layer matching, rapid flapping suppression, exit grace hold, hardware write-loop cooldown, foreground app debounce, anti-cheat no-block/explicit confirmation/invalidation, AC auto-enable pause) + `native-ui` Rune test `macros::tests::macros_room_opens_from_devices_respects_blur_budget_and_confirms_ac`.
