# Exo Devices Overhaul — Audit, Root Causes, Exo EQ v2 & Automatic Setup Design

**Scope:** Read-only architecture audit, live hardware root-cause analysis, Exo EQ v2 design, automatic-setup + simple-overview UX/motion specification, and phased build plan for Exo Launcher's Devices room (`apps/launcher/`). No source files were modified.

**Real hardware verified on this PC (read-only host pipe & HID probes):**

| Device Class | Real Hardware on PC | USB / Link | VID / PID | Live Host Pipe Status (`devices.list` + per-device RPCs) |
|---|---|---|---|---|
| Controller | Sony DualSense Edge Wireless Controller (`Midnight Black`) | USB (CPU port) | `054C` / `0DF2` | `lab: true`, `battery: null` in list; `controller.read({id})` succeeds (`fw: 2.17`, `calibrated: true`, `holds: true`, 4 slots) |
| Keyboard | Wooting 60HE v2 (`ANSI · Split spacebar`) | USB (Chipset port, `better: "free"`) | `31E3` / `1342` | `lab: true`, `layout: "ansi-split"`; `keyboard.read` succeeds (`fw: 9.0.2`, `8000 Hz`, `canWrite: true`, `RT: 0.15 mm`, `Tachyon: on`) |
| Mouse | Finalmouse Starlight X (`StarlightX Dongle`) | Wireless (CPU port) | `361D` / `0300` | `lab: false`, `battery: null`; `hid.list` sees mouse interfaces; `system.mouseCore` serves AGPL Exo Mouse |
| Audio Output | Headphones (`High Definition Audio Device`) | 3.5 mm jack | HDA Endpoint | `role: "output"`, `48 kHz / 32-bit / 2ch`, `vol: 70%`; `eq state` active (`APO 1.4.2.0`, `ZiiGaat Lush`, `flat`, `-3.9 dB`) |
| Audio Input | `fifine Microphone` | USB | USB Audio | `role: "input"`, `48 kHz / 16-bit / 2ch`, `vol: 100%`; Exo Voice (`noise: "exo"`, `focus: true`) |

---

## 1. Inventory & Root Causes

### 1.1 Device Capability Inventory (Host vs. Contract vs. WebView2 vs. Native Rune UI)

| Device Class | Host Services (`apps/launcher/ExoLauncher/Services/`) | `ui/contracts/host-v1.json` RPCs & Events | WebView2 Devices Room (`ui/src/components/devices/`) | Native Rune UI (`native-ui/crates/exo-ui/src/pc*.rs`, `exo-rpc/`) |
|---|---|---|---|---|
| **Discovery & Shell** | `Peripherals.cs`, `Peripherals.Native.cs`, `WebHostBridge.Devices.cs` (USB topology, CPU vs. chipset port checks, official art lookup) | `devices.list`, `devices.image`; event `devices.changed` | `DevicesRoom.tsx` (grid of cards, USB port warnings, shared-element view transitions) | `pc.rs:508-616` (`devices_room`, `device_showcase_card`), `pc_actions.rs:565-610` (`load_pc_devices`). Shows 2x2 showcase cards and port advisory badges (`pc.rs:108-112, 152-156`), filters out `role == "input"` (`pc.rs:559`) |
| **Keyboard (Wooting)** | `WootingKeyboard.cs` (648 L), `WootingKeyboard.Backups.cs` (291 L), `WebHostBridge.Keyboard.cs` (105 L): HID read/write, profile switch, backups, Wootility detection | `keyboard.read`, `keyboard.profile`, `keyboard.settings`, `keyboard.backups`, `keyboard.backup`, `keyboard.restore`, `keyboard.openApp` | `KeyboardPage.tsx` (519 L), `KeyboardMap.tsx` (200 L): visual layout including `ansi-split`, profile tabs, actuation, RT toggle + mm, Tachyon, backups + restore, Wootility button | `pc.rs:161-392` (`wooting_key_matrix`, `wooting_detail`), `pc_actions.rs:730-865`. Expects `wooting: bool` instead of host `lab: bool` (`pc_types.rs:666`), renders blank void if `s.pc.keyboard` is `None` (`pc.rs:512-514`), hardcodes 6.25u Space (ignores `ansi-split`), non-interactive keys, actuation `±0.1`, RT toggle (no mm), Tachyon toggle, Backup create only (no restore list, `METHOD_KEYBOARD_OPEN_APP` unused, swallows errors) |
| **Controller (DualSense, Edge, Xbox, HID)** | `ControllerLab.cs`, `.Pad.cs`, `.Fit.cs`, `.Commands.cs`, `.Families.cs` (DualSense, Edge, DS4, Xbox via XInput, generic HID), `.Settings.cs`, `.GameProfiles.cs`, `WebHostBridge.Controller.cs`, `WebHostBridge.ControllerSetup.cs` | `controller.read`, `controller.watch`, `controller.measure`, `controller.test`, `controller.quality`, `controller.calibrate`, `controller.settings`, `controller.sound`, `controller.gameProfiles`, `controller.gameProfilesSave`; event `controller.state` | `ControllerPage.tsx` (514 L): live dual-stick + trigger meters, rest/edge stick tests, trigger sweep, button chatter, polling Hz, firmware check + Sony updater, Edge/DualSense stick calibration + auto-reapply on connect, Edge 4-slot read, light/sound/vibration, per-game profiles | **Completely unimplemented.** Zero `controller.*` `METHOD_*` constants in `exo-rpc/src/lib.rs`, zero `controller.state` event wiring. Clicking Edge falls into generic `else` stub (`pc.rs:517-540`) showing only `"Wired"` |
| **Mouse (Exo Mouse / AGPL)** | `Hid/HidService.cs` (500 L), `WebHostBridge.Hid.cs`, `WebHostBridge.MouseCore.cs` (`system.mouseCore`) | `hid.list`, `hid.open`, `hid.close`, `hid.sendReport`, `hid.sendFeatureReport`, `hid.receiveFeatureReport`, `hid.receiveInputReport`, `mouse.profiles.get`, `mouse.profiles.set`, `mouse.presets.get`, `mouse.presets.set`, `system.mouseCore`; events `hid.input`, `hid.changed` | `DevicesRoom.tsx:95` (`MouseFrame` iframe loading isolated AGPL `/mouse` app relayed via `ui/src/lib/mouseRelay.ts`, keeping live battery in background) | **Completely unimplemented.** Zero `hid.*`, `mouse.*`, or `system.mouseCore` calls in `exo-rpc`/`exo-ui`. Clicking Starlight X Dongle falls into generic `else` stub (`pc.rs:517-540`) showing only `"Wireless"` |
| **Audio, Exo EQ & Exo Voice** | `WebHostBridge.Devices.cs` (`audio.*`), `ExoEq.cs`, `ExoEqConfig.cs`, `ExoEqProfiles.cs`, `ExoEqSettings.cs`, `AutoEqCatalog.cs`, `EqualizerApo.cs`, `WebHostBridge.Eq.cs`; mic DSP in `DiscordClient/Voice/VoiceChain.cs` | `audio.read`, `audio.setVolume`, `audio.setMute`, `audio.formats`, `audio.setFormat`; `eq` (`state`, `install`, `selector`, `soundSettings`, `headphone`, `sound`, `compare`, `custom`, `night`, `game`), `eq.headphones` | `AudioPage.tsx` (output/mic pickers, volume, mute, format, Exo Voice toggles) + `EqPanels.tsx` (296 L: APO install/selector/restart banners, AutoEq headphone picker, Compare A/B, per-game auto status, headroom readout, Night Shift) | **Broken & stubbed.** `pc_types.rs:732` fails to deserialize `audio.read` (`format` is an object, not `String`), leaving `audio_detail()` (`pc.rs:395`) blank. `pc_actions.rs:576, 581` looks for `role == "mic"` instead of `"input"`. `eq` is called only for headphone photo/name; zero EQ controls rendered |

---

### 1.2 Verified Root Causes ("Even my PS5 Edge and Wooting 60HE v2 split bar don't do anything")

#### 1. DualSense Edge (`VID_054C&PID_0DF2`) — **Native UI only (100% unimplemented stub), plus two host caveats**
* **Primary root cause (Native UI):**
  * `apps/launcher/native-ui/crates/exo-rpc/src/lib.rs` defines zero `controller.*` RPC methods and zero `controller.state` event subscriptions.
  * `apps/launcher/native-ui/crates/exo-ui/src/pc.rs:511-540` (`devices_room`) only dispatches detail views for `dev.wooting || dev.kind == "keyboard"` and `dev.kind == "audio"`. Selecting the DualSense Edge enters the fallback `else` branch (`pc.rs:517-540`), which renders a static `amoled_card` displaying only the connection word (`"Wired"`) and zero interactive controls.
  * `devices.list` returns `battery: null` for a USB-connected DualSense Edge because Windows does not expose DualSense USB battery in PnP; battery level only arrives via HID input report `0x01` inside `ControllerLab` (`WebHostBridge.ControllerSetup.cs:253-266`). Because native never calls `controller.read` or `controller.watch`, not even the Edge's charge appears.
* **Host verification (works today):**
  * Live headless pipe probe calling `controller.read({ "id": "usb-054c-0df2-41a6f6ff466d" })` returned full Edge state: `ok: true, edge: true, firmware: "2.17" (current: true), calibration: { calibrated: true, holds: true, canUndo: true }, slots: [{ slot: 1, name: "Default Profile" }, 2..4 empty]`.
  * `%LOCALAPPDATA%\ExoLauncher\logs\app.log:5293` confirms automatic host-side calibration persistence works: `"Controller: the Edge's calibration put back, and it reads back the same."`
* **Host caveats to fix in Phase 1:**
  1. **Blocking firmware HTTP check (`app.log:1663-1664`):** `WebHostBridge.Controller.cs:30` awaits Sony's remote firmware lookup (`ControllerLab.LatestAsync`) inline inside `controller.read`, causing 14.5–20.1 second hangs when Sony's endpoint stalls or times out. Firmware check should return cached state immediately and refresh asynchronously.
  2. **Edge onboard profile slots:** `ControllerLab` reads all 4 onboard Edge profile slots (`ControllerLab.Commands.cs:9, 52-75`, feature reports `0x70–0x7B`), and writes light/sound/vibration/game-profiles plus stick finetune calibration (`ControllerLab.Settings.cs`, `ControllerLab.GameProfiles.cs`), but does not yet write Edge onboard button remaps/stick-curve slots (documented as read-only in `ControllerPage.tsx`).

#### 2. Wooting 60HE v2 Split Spacebar (`VID_31E3&PID_1342`) — **BOTH a Host HID Write Bug AND Native UI Gaps**
* **Does the host recognize the 60HE v2 split-spacebar model?**
  * **Yes for detection & read:** `Peripherals.Native.cs:197` + `Peripherals.cs:616-630` queries `CmdGetDeviceConfig` and decodes byte 12 = `0x03` as `layout: "ansi-split"`. `WootingKeyboard.cs:89, 439` identifies `PID 0x1342` (`0x1340` family) as `"Wooting 60HE v2"`, `8000 Hz`, `"ANSI · Split spacebar"`. Live `keyboard.read` returned `ok: true, firmware: "9.0.2", actuationMm: 3.0, rapidTrigger: true, rapidTriggerMm: 0.15, tachyon: true, activeProfile: 0, profiles: [{ index: 0, name: "General" }]`.
  * **No for writes (Host HID bug):** Live read-only `HidP_GetCaps` inspection on the owner's real Wooting 60HE v2 (`VID_31E3&PID_1342`) proved:

| Usage Page / Usage | Function | `InputReportByteLength` | `OutputReportByteLength` | `FeatureReportByteLength` |
|---|---|---|---|---|
| `0xFF55` / `0x0001` | Config / Wootility (`MI_02`) | **2047** | **2047** | **8** |
| `0xFF53` / `0x0001` | Analog / Raw | 65 | 0 | 0 |
| `0xFF00` / `0x0001` | Vendor | 9 | 0 | 0 |
| `0x0001` / `0x0006` | Keyboard (Boot / NKRO) | 29 | 2 | 0 |
| `0x0001` / `0x0002` | Mouse | 8 | 0 | 2 |
| `0x000C` / `0x0001`, `0x0001` / `0x0080` | Consumer / System | 7 / 3 | 0 | 0 |

  * **Root cause #1 (Host — `WootingKeyboard.cs:237-249, 381-385, 461-468`):** `EncodeProfileOutputReport` selects `ArmOutputSizes` entry `(1, 32)` and allocates a **33-byte** buffer (`new byte[1 + 32]`), and `WriteOutputReport` (`WootingKeyboard.cs:461-468`) writes that 33-byte array straight to `FileStream.WriteAsync` (`WriteFile`) without calling `GetCaps(handle)` (unlike `SendFeatureOnly` right above it at `WootingKeyboard.cs:453-456` or `HidService.cs:396-405` which pads via `Frame(reportId, data, i.Caps.OutputLength)`). On Windows `hidclass.sys`, `WriteFile` requires `buffer.Length == HIDP_CAPS.OutputReportByteLength` (**2047 bytes** on 60HE v2, zero-padded); passing 33 bytes fails with `ERROR_INVALID_USER_BUFFER` / `IOException`. `WootingKeyboard.Backups.cs:172-173` (`RestoreBackup`) calls the exact same `EncodeProfileOutputReport` + `WriteOutputReport` path, breaking restore too.
  * **Root cause #2 (Host — `WootingKeyboard.cs:357-393`):** `UpdateProfile` never sends `CmdWootDevInit` (`0x21`) before writing or `CmdReloadProfile` (`0x26`) after writing, unlike `SwitchProfile` (`WootingKeyboard.cs:342-346`) and `RestoreBackup` (`WootingKeyboard.Backups.cs:175, 181-183`). Even after buffer padding is fixed, firmware can hold the old RAM profile until `CmdReloadProfile` fires.
  * **Root cause #3 (Host scope — `WootingKeyboard.cs:181-220`):** `PatchKeyboardProfile` only patches 4 global protobuf fields (actuation `f1`, RT enable `f3`, RT mm `f4`, Tachyon `f5`). It does not expose per-key actuation or split-spacebar key bindings (left space / center Fn / right space), DKS, or Rappy Snappy/SOCD.
  * **Root cause #4 (Native UI — `exo-rpc/src/pc_types.rs:666`, `pc.rs:161-274, 287-392, 512-514` & `pc_actions.rs:754-810`):**
    1. `exo-rpc/src/pc_types.rs:666` deserializes `pub wooting: bool`, which the host never sends (`Peripherals.cs:52` and `WebHostBridge.Devices.cs:160` send `lab: bool`), and `pc.rs:512-514` renders an empty `el()` void if `s.pc.keyboard` is `None`.
    2. `wooting_key_matrix` (`pc.rs:228`) hardcodes a single 6.25u `("Space", 6.25)` key, completely ignoring `kb.layout` (`"ANSI · Split spacebar"`), whereas WebView2 `KeyboardMap.tsx:54-60` renders `[2.75u Space | 1.25u Fn | 2.25u Space]`.
    3. Every key cap in `wooting_key_matrix` (`pc.rs:258-274`) has **no `.hit()` and no `.on_press()`** — clicking any key on the keyboard diagram does nothing.
    4. `wooting_detail` (`pc.rs:287-392`) omits Rapid Trigger mm adjustment, firmware/scan rate/layout display, backup restore list, and Open Wootility (`METHOD_KEYBOARD_OPEN_APP` is defined in `exo-rpc` but never wired). With only 1 profile (`General`) returned on this 60HE v2, clicking the single profile pill is a no-op.
    5. `pc_actions.rs:754-758, 771-810` optimistically mutates `s.pc.keyboard` before the RPC finishes and **silently drops** `!kb.ok` failures (`if let Ok(kb) = res && kb.ok`), never surfacing `kb.message` or rolling back the UI when the host write fails.

#### 3. Audio & Exo EQ — **Native UI Broken (DTO Crash + Wrong Role Filter + No EQ UI) + Host APO ACL Gap**
* **Native UI root cause #1 (`exo-rpc/src/pc_types.rs:732`):** `audio.read` returns `"format": { "rate": 48000, "bits": 32, "channels": 2 }` (a JSON object), but `AudioEndpoint.format` is typed as `Option<String>`. Every `audio.read` response fails `serde_json` deserialization; `pc_actions.rs:631` silently discards the error, leaving `s.pc.audio == None` and making `audio_detail()` (`pc.rs:395`) render an empty `el()`.
* **Native UI root cause #2 (`pc_actions.rs:581` & `pc.rs:559`):** `load_pc_devices` searches for `d.role.as_deref() == Some("mic")`, whereas `devices.list` emits `"role": "input"` (and `pc.rs:559` hides `role == "input"` from the grid). The USB `fifine Microphone` is never attached.
* **Native UI root cause #3 (`pc_actions.rs:656`, `pc.rs:395-507`):** Native only calls `eq { action: "state" }` to grab the headphone photo/name for the overview card; `audio_detail` renders zero Exo EQ controls (no APO status/install/selector, no headphone picker, no sound/game profile, no Compare A/B, no Night Shift, no audio format picker).
* **Host caveats:**
  1. `app.log:2881, 3278, 3375` logged `WARN Exo EQ: Equalizer APO's config couldn't be written: UnauthorizedAccessException` (`ExoEq.cs:190-203`) when writing `exo.txt.exo-tmp` inside `C:\Program Files\EqualizerAPO\config`. On fresh Equalizer APO installs or restrictive ACLs (before `Users:(OI)(CI)(F)` is granted), writes fail silently without offering a one-click elevated permission repair or fallback include path in `%PROGRAMDATA%\ExoLauncher\eq\`.
  2. `WebHostBridge.Devices.cs:79-81, 133`: `audio.formats` validates endpoint IDs against `_audioIds` populated only when `devices.list` was previously called; calling `audio.formats` before `devices.list` fails with `ok: false`.

#### 4. Mouse (Finalmouse Starlight X Dongle, `VID_361D&PID_0300`) — **Native UI Unimplemented (AGPL Boundary)**
* Native UI (`pc.rs:517-540`) falls into the generic static `"Wireless"` card with zero battery, zero DPI/polling/debounce controls, and zero `hid.*` / `mouse.*` / `system.mouseCore` RPCs.
* **Architectural constraint (`apps/launcher/AGENTS.md`):** `apps/launcher/mouse/` (`@openmouse/protocol`) is **AGPL-3.0**, whereas `apps/launcher/ui/` and `apps/launcher/native-ui/` are **MIT**. Native UI must never compile AGPL protocol logic into `exo-ui` or `exo-kit`. It must talk to Exo Mouse strictly across the process/IPC boundary (e.g., an out-of-process AGPL Exo Mouse daemon exposing bounded JSON-RPC over a separate pipe/localhost channel, or embedding Exo Mouse's isolated WebView surface on demand while keeping status/battery/profile summary via `mouse.state` IPC).

---

### 1.3 What Is Heavy & Bloated Today (Both UIs)

| Area | Current State (WebView2 & Native) | Why It Feels Heavy / Bloated | What Should Be Automatic |
|---|---|---|---|
| **Overview Grid** | Large 2x2 showcase cards (`DevicesRoom.tsx`, `pc.rs:552-607`) with big product renders, minimal actionable state (no active profile, no EQ mode, no Edge calibration status, no keyboard RT/actuation summary) | Takes an entire 1080p viewport just to show 4 photos and `"Wired"` / `"Wireless"`. You have to click into 4 separate deep pages just to verify everything is configured | One calm, scannable row/card per device showing status, battery, active profile/settings summary, port health if suboptimal, and a single **Change** action |
| **DualSense Edge Page (`ControllerPage.tsx`, 514 L)** | 10+ stacked sections: Sticks live view + Rest/Edge Test + Calibration + Range Fit + Undo + Triggers live view + Trigger Sweep + Stop Sliders + Button Chatter Test + Polling Rate Test + Firmware + Edge Slots + Light/Sound/Vibration + Per-Game Profiles | Overwhelming diagnostic workbench mixed with daily settings; users must manually run 4 separate timed tests (5s rest, 6s edge, 6s triggers, 8s buttons) and manually trigger firmware & calibration checks | Automatic setup checks firmware, reapplies/verifies stick calibration, reads trigger stop positions, applies sensible defaults (light/mic/vibration), and runs a single guided 10-second stick+trigger check only when uncalibrated. Diagnostics move behind **Change › Calibration & tests** |
| **Wooting 60HE v2 Page (`KeyboardPage.tsx`, 519 L)** | Dense map + 6 control rows + backup management + Wootility handoff + port warning; Native misses half of these | Users must manually check chipset vs. CPU USB port, manually create their first backup before touching settings, manually configure Rapid Trigger + Tachyon + actuation from scratch | Automatic setup creates the first safety backup automatically, detects `ANSI · Split spacebar` & `8000 Hz`, proposes tuned gaming defaults (e.g., `0.4 mm` actuation / `0.15 mm` Rapid Trigger / `Tachyon on`, or preserves current Wootility profile with 1 click), and flags the CPU USB port cleanly |
| **Audio & Exo EQ (`AudioPage.tsx` + `EqPanels.tsx`, 460+ L)** | 6 separate panels: Output, Mic, Exo Voice, Equalizer APO setup + Device Selector + Sound Settings, Headphone search across thousands of entries, Sound/Headroom, Night Shift | Requires multi-step manual choreography: click Install APO -> wait -> click Open Device Selector -> find device in 3rd-party Win32 tree -> restart audio -> search AutoEq catalog by hand -> configure Night Shift | Automatic setup detects installed/active APO (asking explicit consent once if install/selector is needed), auto-matches USB/BT headsets (or confirms analog IEM/headphone once with top popular picks + search), enables AutoEq + JM-1/Harman target + game auto-switching + smart loudness guard automatically, and surfaces Exo Voice mic status on the same card |
| **Mouse (`MouseFrame` iframe)** | Full separate web app embedded inside a frame (`DevicesRoom.tsx:95`) | Heavy first-load delay (up to 3s `LOAD_MS`), mismatched chrome/layout density compared to native rooms, requires opening the full mouse workbench just to set DPI/polling | Automatic setup reads battery, DPI stage, polling rate & wireless link via AGPL boundary IPC, offers a clean default gaming profile (e.g. `800 / 1600 DPI`, `1000–4000 Hz` balanced battery), and opens the full mouse detail view only when **Change** is pressed |

---

## 2. Exo EQ v2: Game-Specific, Smart & Objective

### 2.1 How Exo EQ Works Today (`file:line`)

* **Engine path (`ExoEq.cs:1-310`, `EqualizerApo.cs:1-304`):** Pure system-level Equalizer APO text configuration — zero resident Exo audio process, zero game injection. Exo writes `C:\Program Files\EqualizerAPO\config\exo.txt` (`ExoEq.cs:175-204`) and ensures `config.txt` includes `Include: exo.txt` (`ExoEq.cs:206-245`), disabling any legacy Lucid include lines cleanly (`# Exo EQ took over: ...`). Equalizer APO hot-reloads `exo.txt` within milliseconds with zero audio dropout.
* **AutoEq correction (`AutoEqCatalog.cs:1-180`, `ExoEqConfig.cs:1-200`):** Downloads and caches AutoEq (MIT) parametric EQ index & per-model filters (preferring highest-accuracy rig measurements, e.g., `Super Review`, `oratory1990`, `Crinacle`), then adds a 3-band target refinement (`JM-1 / IEF 2025` for in-ear, `Harman OE` for over-ear — visible right now in `exo.txt`: 10 AutoEq filters + 3 JM-1 refinement bands for `ZiiGaat Lush`, `-3.9 dB` preamp).
* **Sound profiles (`ExoEqProfiles.cs:1-192`):** Currently only 4 static presets (`flat` Reference, `footsteps`, `vocal`, `immersion`) plus 10-band manual `custom`, plus a 2-band warm shelf for `Night Shift` (`ExoEqSettings.cs:1-187`).
* **Per-game switching (`WebHostBridge.Eq.cs:1-214`):** Hooks `Library` game launch/exit events (`ExoEqProfiles.cs` maps coarse game genres: shooters -> `footsteps`, RPG/story -> `vocal`, action/racing -> `immersion`, rhythm/desktop -> `flat`). Does **not** react to Alt+Tab window focus, has only 1 generic `footsteps` curve for all shooters, and has no loudness/dynamics protection.
* **Mic chain (`DiscordClient/Voice/VoiceChain.cs:1-140`):** Exo Voice already owns the microphone DSP pipeline (`LowCut` -> `KnockDetector` -> `DeepFilterNet` / `WebRtcNoise` -> `VoiceFocus` / `VoiceGate` -> `VoiceShape` EQ + soft compressor -> `SpeechLeveler` targeting `-22 dBFS`), automatically stepping aside when hardware mic suppression is active (`StepsAside`).

### 2.2 Why Lucid Was Retired (`docs/architecture/exo-eq.md`, `CHANGELOG.md`) & Public Research on Tekkusai Lucid

* **Lucid history in Exo (`apps/launcher/docs/architecture/exo-eq.md:8`, `CHANGELOG.md:54-55, 560, 1061, 1131-1135`):**
  * Until v3.25, Exo shipped and managed Tekkusai's closed-source Lucid as an add-on (`addons\lucid`); Exo EQ replaced it starting in v3.26 (`docs/architecture/exo-eq.md:8`).
  * **Why it was replaced (`docs/architecture/exo-eq.md:12-26`, `CHANGELOG.md:54-55`):** Shipping a closed third-party companion required restarting Lucid to apply changes (causing ~3 s audio dropouts unless deferred until game exit, `CHANGELOG.md:560`), fighting **Lucid Voice** turning itself back on and double-processing mics ahead of Exo Voice (`CHANGELOG.md:1061`), maintaining separate updater/installer plumbing, and limiting headphone support to Lucid's closed list instead of AutoEq's ~6,000 measured models. Exo EQ replaced Lucid by writing Equalizer APO configs directly with live hot-reload, zero resident process, and automatic one-time migration of Lucid's saved headphone, game rules, and Night Shift.
* **Public Tekkusai Lucid capabilities (ideas & capabilities only — zero branding, UI, assets, or code copied):**
  * *Sources (public web):* Tekkusai product & supported devices pages (`tekkus.ai`), ProSettings review (`prosettings.net`), PlayFront review (`playfront.de`), user/audio technical impressions (`note.com`), Equalizer APO (`equalizerapo.com`).
  * **Key capabilities that make it feel smart and competitive:**
    1. **Subtractive competitive FPS tuning:** Rather than harshly boosting treble (which causes piercing gunfire and fatigue), competitive curves attenuate non-essential masking clutter (boomy low-mids, ambient wind/rumble) and tame harsh gunshot/armor-break peaks so distant footsteps emerge naturally without ear fatigue.
    2. **Title-specific shooter tuning:** Distinct profiles per sound engine (e.g., CS2, VALORANT, Apex Legends, Overwatch 2, Marvel Rivals, The Finals, PUBG, Fortnite, Rainbow Six Siege) instead of one generic "Footsteps" preset, plus single-player immersion, movie, music, and easy-listening profiles.
    3. **Smart auto-detection & automatic revert:** Detects when a game runs, applies its tailored profile automatically with an optional subtle switch cue, and cleanly reverts to desktop/music EQ when leaving the game (with a manual lock override).
    4. **Measured third-party compensation:** Applies each curve *on top of* device-specific frequency correction so the intended tonal balance lands accurately on IEMs, gaming headsets, and studio headphones alike.
    5. **Night Mode & dynamic softening:** Compresses/softens harsh spikes for comfortable lower-volume or late-night sessions.
    6. **Built-in Equalizer APO diagnostics:** Automatically detects broken Windows audio enhancements, missing APO hook on the active endpoint, or stale configs and walks the user through fixing them.
    7. **Quick hotkeys & zero-clutter UX:** Instant toggle/bypass without leaving a match, keeping advanced parametric bands tucked away unless requested.

### 2.3 Exo EQ v2 Architecture Proposal

**Hard constraint:** Strictly system-level Windows audio (Equalizer APO + WASAPI metadata). **Zero** game process injection, **zero** DLL hooks, **zero** anti-cheat contact.

1. **Smart Headphone Matching & Calibration Layer (Base Correction)**
   * **USB / Wireless / Bluetooth auto-detection:** Match endpoint PnP friendly name + USB VID/PID against an offline-bundled top-500 AutoEq index (shipping inside the app, zero network wait during setup) plus the full online AutoEq catalog (5,000+ models) on search. Offer a 1-click confirmation banner (`Looks like ZiiGaat Lush — Use it`).
   * **3.5 mm / Analog jack memory:** Remember the chosen model per physical audio endpoint GUID (`{e69ea4db-...}`), pre-populated with previously chosen headphones and quick popular picks during setup.
   * **Target selector (advanced):** Default to **JM-1 / IEF 2025** for IEMs and **Harman OE 2018** for over-ear headphones, with optional **Flat Studio** or **Warm** tilt (`-1.5` to `+1.5 dB/oct` single-knob tonal balance).
   * **Resilient APO config storage:** Write Exo's generated config into `%PROGRAMDATA%\ExoLauncher\eq\exo.txt` (owned with explicit `Users:Modify` ACL created once during APO setup) and add a single `Include: C:\ProgramData\ExoLauncher\eq\exo.txt` line in Equalizer APO's `config.txt` — eliminating `Program Files` `UnauthorizedAccessException` errors (`app.log:2881,3278,3375`). Include an automated **APO Health Check** (installed, hooked on current endpoint, Windows audio enhancements enabled, config writable, live reload verified).

2. **Deep, Subtractive Per-Game EQ Profiles (Layer 2 over Base Correction)**
   * **Engine/archetype library + title overrides (all original Exo-authored, zero copied presets):**
     * **Tactical FPS (CS2, VALORANT, R6 Siege):** Subtractive low-mid mud cut (`120–280 Hz`, `-2.5` to `-3.5 dB`) to remove map drone/explosions, surgical upper-mid clarity shelf (`2–4 kHz`, `+1.5 dB` wide Q) for footsteps/reloads/defuse cues, and de-harsh notch (`5.5–7.5 kHz`, `-2.5 dB`) to tame rifle cracks.
     * **Battle Royale / Extraction (Apex Legends, PUBG, Warzone, Escape from Tarkov, Fortnite):** Deep sub-bass rumble roll-off (`<55 Hz` HPF/LSC) to strip storm/vehicle/zone drone, clear mid-band spatial separation (`800 Hz–3 kHz`) for directional steps and ziplines, and high-frequency peak taming (`4–8 kHz`) for shield cracks and close gunfire.
     * **Arena / Hero Shooter (Overwatch 2, Marvel Rivals, The Finals):** Balanced speech + ability cue window (`1–4 kHz`) with controlled bass impact (`80–160 Hz` tight) so voice callouts and ultimate cues never drown in team-fight explosions.
     * **Story & Dialogue (RPG / Narrative):** Gentle vocal presence (`1.5–3.5 kHz`, `+1.8 dB`), warm chest (`120 Hz`, `+1.0 dB`), zero harshness.
     * **Cinematic Immersion (Action / Racing / Horror):** Controlled sub-bass weight (`45–90 Hz`, `+2.5 dB`) + open air (`12 kHz`, `+1.0 dB`) without muddying dialogue.
     * **Studio Reference / Rhythm / Music:** Pure base headphone correction (`0 dB` overlay) with optional subtle Bass/Warmth tilt.
   * **Per-game intensity & custom override:** Every game gets an automatic recommended profile from Exo's library metadata (title match first -> genre fallback), with a single clean **Strength** slider (`50%` Subtle / `100%` Standard / `130%` Competitive) and **Lock / Override** per game, plus a 10-band parametric/graphic editor tucked under **Custom**.

3. **Focus-Aware Automatic Switching (Zero Game Injection)**
   * **Trigger source:** Combine Exo's own game-session tracker (`Library` launch/running state) with a lightweight Windows `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` watching only the foreground HWND's PID against Exo's known running game PID(s).
   * **Behavior:**
     * Game enters foreground -> immediately switch `exo.txt` to that game's profile (hot-reloaded by Equalizer APO in <15 ms).
     * Alt+Tab to desktop/browser/music -> optionally cross-switch back to **Reference / Music** after a 1.5 s debounce (configurable toggle: **Keep game EQ on Alt+Tab**, default *off* so YouTube/Spotify on desktop sounds clean).
     * Game exits -> immediately revert to desktop profile.
     * Optional unobtrusive notification pill inside Exo (never stealing focus or injecting overlays into the game) + optional soft audible chime (off by default during calls).

4. **Smart Loudness & True-Peak Limiter (Gunshot Protection + Night Shift)**
   * **Zero-clipping headroom (always on):** Compute the exact worst-case combined frequency response `|H_total(f)|` of (Base AutoEq + Target + Game Profile + Night Shift + Tilt) across 20 Hz–20 kHz at 1/24-octave resolution and automatically set `Preamp: -(maxPeakDb + 0.3) dB` so digital clipping (`>0 dBFS`) is mathematically impossible.
   * **Smart Dynamics / Loudness Protection (opt-in per profile & Night Shift):**
     * Use Equalizer APO's native `VSTPlugin` stage hosting a tiny, MIT/open-source, zero-latency (or <2 ms lookahead) pure C++ **Exo Dynamics VST2/VST3** (true-peak brickwall limiter + gentle upward/downward loudness leveler tuned for FPS gunshot-vs-footstep crest factor) — OR Equalizer APO's built-in `LoudnessCorrection` + safe soft-clipper stage when the VST is not enabled.
     * **Competitive Gunshot Guard:** Soft knee compression above `-12 dBFS` (ratio `3:1`, fast `2 ms` attack, `80 ms` release) + true-peak ceiling at `-1.0 dBTP` — keeps footsteps untouched while cutting ear-fatiguing gunshot/flashbang spikes by 6–10 dB.
     * **Night Shift v2:** Combines the warm top-end shelf (`-2.5 dB` above 5 kHz) with gentle dynamic range compression so late-night listening stays clear at low Windows volume, synced to Windows Night light or custom hours.

5. **Mic Chain Coordination (Exo Voice)**
   * Keep **Exo Voice** (`VoiceChain.cs`) as the single owner of microphone DSP — Exo EQ v2 **never** registers Equalizer APO capture filters on microphones (avoiding the double-suppression / extra-latency defect that forced Exo to disable Lucid Voice in `CHANGELOG.md:1061`).
   * Surface live Exo Voice status (`DeepFilterNet` / `Voice Focus` / `Tuned for fifine Microphone`) directly in the Audio setup card and overview row, with a 1-click link to Voice Tune.

6. **Instant Live A/B Compare**
   * Single **Compare** toggle (and optional global hotkey, e.g., `Ctrl+Alt+E`, strictly user-configured) that writes a clean bypass state (`Preamp: 0 dB`, no filters) with **loudness-matched A/B option** (matches average perceived loudness at 1 kHz so "louder" doesn't trick the ear into thinking bypass sounds better).

7. **Measure-Free Objective Verification Harness**
   * Because Exo cannot assume a measurement rig on CI or user PCs, Exo EQ v2 ships with a deterministic automated verification suite (`dotnet test` + offline DSP evaluator):
     1. **Complex Biquad Transfer Function Evaluator:** Parses the generated `exo.txt` (every `PK`, `LSC`, `HSC`, `LP`, `HP`, `Preamp` line) and computes exact complex `H(e^{j\omega})` over 512 log-spaced frequencies (20 Hz–20 kHz) at `44.1`, `48`, `96`, and `192 kHz`.
     2. **Headroom & Stability Assertions:** Proves `max_f (20*log10|H(f)| + preamp) <= -0.3 dBFS` (zero digital clipping across every headphone x game profile x Night Shift combination), verifies all biquad poles lie strictly inside the unit circle (`|p| < 1`), and asserts zero NaN/Inf coefficients at every sample rate.
     3. **Profile Target Envelope Tests:** Asserts each game profile achieves its intended acoustic delta (e.g., Tactical FPS cuts `120–280 Hz` by `-2` to `-4 dB` and tames `5.5–7.5 kHz` without boosting treble >`+2.5 dB`; Night Shift attenuates `>6 kHz`).
     4. **CPU & Latency Budget Benchmark:** Offline 48 kHz stereo 10-second float buffer benchmark proving Exo Dynamics + 20-biquad cascade uses **< 0.5% of one CPU core** and adds **<= 1.5 ms algorithmic latency** (0 samples for pure IIR EQ, <= 64 samples / 1.33 ms lookahead when true-peak limiter is active).
     5. **APO Config & Safety Contract Tests:** Verifies atomic write + ACL check, non-destructive `config.txt` patching, clean uninstall/restore, and zero process-open/injection calls against game executables.

---

## 3. Design: Automatic Setup, Then a Simple Overview

### 3.1 Core UX Architecture

Instead of dropping the user into a dense 4-card launcher of diagnostic workbenches, Devices has **two calm states** plus an on-demand **Detail** view:

1. **Automatic Setup Flow** (runs automatically on first visit, whenever a new unconfigured device is plugged in, or anytime via **Re-run setup**):
   * **Step 0 — Scan & Detect (`mockups-v2/01-setup-start.html`):** Full-window pure Void (`#000`) stage under the full-width setup top stage track (`Devices` current, live sub-lines, Done states) with the device-photo stepper strip, presenting four full-height panoramic hardware bays with real cutout photography for every connected device found via `devices.list` (DualSense Edge, Wooting 60HE v2, Finalmouse Starlight X, ZiiGaat Lush + fifine Microphone) above a full-width Liquid Glass bottom dock (`Start setup`, `Preview sequence`, `Skip to overview`).
   * **Step 1..N — One Full-Window Hero Stage Per Device:** Rather than a narrow centered card, each device owns the entire window underneath the persistent device-photo stepper strip (no left rail anywhere in setup). Each stage runs 3 crisp, visible phases automatically:
     1. **Identify & Health Check:** Verifies model, firmware, connection, and USB port (advises moving Wooting from Chipset USB to free CPU USB without blocking setup). Any download, installer, or elevated action (Equalizer APO, Sony firmware updater) pauses cleanly on an explicit **consent prompt** (`Install Equalizer APO` / `Skip for now`).
     2. **Auto-Tune / Quick Calibration:**
        * *DualSense Edge (`mockups-v2/02-setup-dualsense-edge.html`):* Left ~65% displays a huge Midnight Black DualSense Edge hero over a subtle hardware wash, ringed by 4 floating Liquid Glass telemetry callouts with leader lines to L2/R2 trigger stops (`Short · 1 of 3`) and Left/Right analog sticks (live center rings & circularity), plus a bottom telemetry bar (`1000 Hz USB`, `100% · Charging`, `Auto-reapplies`, `Slot 1`); right ~35% runs automatic verification (`fw 2.17`, saved stick calibration `Holds` or 6-second guided roll, Edge 4-slot readout, clean mute/light defaults).
        * *Wooting 60HE v2 (`mockups-v2/03-setup-wooting-60he-v2.html`):* Renders the true `ANSI · Split spacebar` `[2.75u Space | 1.25u Fn | 2.25u Space]` matrix at ~1840 px wide (1920x1080) under a small real keyboard photo header, above a bottom dock holding the automatic safety backup, actuation, Rapid Trigger and Tachyon controls, with the Chipset USB advisory in the dock; creates an automatic safety backup, verifies `8000 Hz` Tachyon, and applies (or keeps existing `3.0 mm` Wootility profile on choice) responsive actuation (`0.4 mm`) + Rapid Trigger (`0.15 mm`).
        * *Finalmouse Starlight X:* Reads wireless link & battery via AGPL Exo Mouse IPC, confirms `1000 Hz / 4000 Hz` & `800 / 1600 DPI` stages, links per-game profiles.
        * *Audio & Exo EQ v2 (`mockups-v2/04-setup-audio-eq.html`):* Upper full-width stage plots the live 20 Hz–20 kHz frequency response curve (`ZiiGaat Lush` AutoEq + JM-1 target + active per-game overlay, `-3.9 dB` headroom, instant **Compare A/B** toggle); lower full-width 3-column band covers Equalizer APO (`1.4.2.0 · Active on Headphones`), per-game auto-switching / Gunshot Guard / strength slider, and `fifine Microphone` on Exo Voice.
     3. **Done Checkpoint:** Crisp check mark (`#3dd68c`) settles onto the device photo tile in the stepper and advances smoothly to the next device (auto-advances after `900 ms` when no input is needed, or waits cleanly when user consent/calibration is active).
2. **Simple Overview (`mockups-v2/05-overview.html`):**
   * Once setup finishes (and on every subsequent visit), Devices opens directly to a serene, full-width overview stretching edge-to-edge inside the Exo shell:
     * Header: `Devices` + `4 configured · Ready` pill on the left; `Preview new device plug-in`, `Rescan`, and `Re-run setup` buttons on the right.
     * **Four full-width horizontal hardware strips** (one per device): large crisp product render on the left, device name + connection/status badge, **3 hairline-divided live stat cells** across the width (e.g. Calibration / Onboard profile / Firmware; Actuation-RT / 8000 Hz / USB port health; Battery / DPI / Wireless polling; Active EQ / Headroom & guard / Exo Voice Mic), and a single **Change** button on the right.
     * **New device hot-plug:** Plugging in an unconfigured device slides in a single quiet glass banner at the top of the overview (`New device found: <Name>` with `Set up` and `Dismiss`), never hijacking the screen mid-session.
3. **Device Detail Page (`mockups-v2/06-detail-wooting.html`):**
   * Pressing **Change** transitions smoothly from that strip into a full-window detail workbench: `< Devices` toolbar at top, a wide ~67% left stage combining the real hardware photo, 4 hairline telemetry cells, the interactive split-spacebar key matrix, and a live 4.0 mm travel bar, alongside a full-height right settings & backup-restore panel with `Open Wootility`.

### 3.2 Visual & Material Rules (`DESIGN.md`, `exo-design`, `apple-design`)
* **Canvas:** Pure Void `#000000` page background (AMOLED true black). Static content containers use `#000` with `--exo-line` (`rgba(255,255,255,0.07)`) or `--exo-surface-card` (`rgba(255,255,255,0.035)`).
* **Liquid Glass:** Reserved exclusively for floating stage docks, callouts, popovers, and controls (`--glassy-ring` top-lit inset hairline, `--exo-control-glass: blur(10px) brightness(0.62) saturate(1.8)`, `--glassy-overlay: rgb(0 0 0 / 0.36)`). Never stacked glass-on-glass.
* **Typography:** `Goeist` (`Goeist-Variable.woff2`) and `Goeist Mono` (`GoeistMono-Variable.woff2`), fluid `clamp()` scale from `1280x720` to `2560x1440`, sentence case everywhere, `font-variant-numeric: tabular-nums` for battery %, Hz, mm, dB, and ms, zero letter-spacing on lowercase.
* **Color Discipline:** Pure white `#ffffff` primary ink, `#b4b4b4` muted, `#9a9a9a` faint; `#3dd68c` good/ready, `#ffb340` advisory (e.g., Chipset USB port), `#ff5c5c` error/action needed. Selected pills use solid `#ffffff` fill with `#000000` ink (never outlined rings).
* **Icons:** Lucide icons at `1.5px` stroke weight, zero emoji anywhere.

### 3.3 Exact Rune Motion Choreography Plan (`exo-kit/src/tokens.rs` + `rune`)

All springs in `apps/launcher/native-ui/crates/exo-kit/src/tokens.rs:157-161` are critically damped (`bounce = 0.0`, zero overshoot):
* `SPRING_PRESS` (`Spring::new(0.09, 0.0)`), `SPRING_QUICK` (`Spring::new(0.12, 0.0)`), `SPRING_BASE` (`Spring::new(0.16, 0.0)`), `SPRING_MOVE` (`Spring::new(0.22, 0.0)`), `SPRING_FLIGHT` (`Spring::new(0.30, 0.0)`).
* **Mandatory Rune state-commit rule:** Any setup step advance, check-row completion, or hot-plug banner driven by an async RPC callback or timer **must** commit state via `Tx::animated()` (otherwise Rune treats programmatic updates as instant/unanimated). Reduce motion is driven by `cx.reduced_motion(true)` (and tested via `rt.set_system_reduced_motion(true)`).

| Choreography Beat | Element Cast (`choreograph`) | Exact Rune API & Springs | Frequency / Restraint (`animate`) | Reduce Motion (`cx.reduced_motion(true)`) |
|---|---|---|---|---|
| **Setup stage advance** (Device `i` -> Device `i+1`) | **Lead:** full-window hero device stage; **Support:** visual device-photo stepper highlight & check mark; **Static:** top stage track / window chrome | Outgoing stage: `Transition::offset(-28.0, 0.0).combined(Transition::opacity())` on `SPRING_QUICK`; incoming stage: `Transition::asymmetric(Transition::offset(28.0, 0.0).combined(Transition::opacity()), Transition::opacity()).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))`. Active device tile uses `.matched("setup-device-tile")` with `.motion(SPRING_BASE)`. | Rare / celebratory (runs once per device during setup, never loops continuously) | `Transition::opacity().springs(SpringPair::new(SPRING_QUICK, SPRING_QUICK))` — zero horizontal travel, instant tile swap |
| **Per-device check rows settling** (Identify -> Firmware -> Calibrate -> Ready) | **Lead:** active step status dot/check; **Support:** row value text; **Static:** row labels | Each row enters with `Transition::offset(0.0, 6.0).combined(Transition::opacity()).springs(SpringPair::new(SPRING_BASE, SPRING_QUICK))` staggered with `.enter_delay(0.04 * idx as f32)`. Active check uses `.transition(Transition::draw())` for the Lucide checkmark path, then stops. | Purposeful progress feedback only; stops immediately on completion | Static opacity crossfade (`Transition::opacity()`) with zero `offset`, `enter_delay`, or `draw` travel |
| **Setup complete -> Simple overview** | **Lead:** device product photo & title; **Support:** full-width overview strips arriving; **Static:** `Devices` heading & toolbar | Each device's photo container shares `.matched(format!("device-art-{}", dev.id))` and `.matched_crop()` with `.motion(SPRING_FLIGHT)` so the last setup device lands cleanly into its full-width overview strip while sibling strips fade in via `Transition::offset(0.0, 8.0).combined(Transition::opacity()).springs(SpringPair::new(SPRING_BASE, SPRING_QUICK))`. | Once at end of setup | Pure `Transition::opacity()` swap (`Transition::none()` on layout travel) |
| **Overview strip -> Device detail ("Change")** | **Lead:** device photo & title; **Support:** full-window detail panels & matrix; **Static:** `< Devices` back button | Shared element `.matched(format!("device-art-{}", dev.id))` + `.matched_crop()` with `.motion(SPRING_FLIGHT)` and `.hold_landing_width()`. Detail control panels enter with `Transition::offset(0.0, 10.0).combined(Transition::opacity()).springs(SpringPair::new(SPRING_MOVE, SPRING_QUICK))`. Back reverses smoothly. | Medium frequency — crisp and interruptible mid-flight | `.matched()` disabled when `cx.reduced_motion()` is active; falls back to `Transition::opacity()` on `SPRING_QUICK` |
| **Button press & hover** (`Change`, profile pills, toggles) | **Support:** individual controls | `.hover_motion(Spring::NONE, SPRING_QUICK)` (instant hover-in, smooth `0.12s` hover-out) and press scale (`PRESS_SCALE = 0.97`) / fill via `.motion(SPRING_PRESS)` | High frequency — zero overshoot, zero travel | Instant fill change (`Spring::NONE`), no scale travel |
| **Hot-plug new device banner** | **Lead:** single Liquid Glass top banner | `Transition::asymmetric(Transition::offset(0.0, -8.0).combined(Transition::opacity()), Transition::opacity()).springs(SpringPair::new(SPRING_BASE, SPRING_QUICK))` + `.animate_layout()` on the overview strips below | Rare (only on USB/BT plug event) | `Transition::opacity()` only, no layout slide |

**Automated motion verification:** Every transition above must include a headless `rune::motion_gate::Run` test in `exo-ui` running with both `reduce_motion = false` and `reduce_motion = true` (following `discord.rs:3716-3746`) asserting zero motion-gate findings and zero non-opacity travel under Reduce Motion.

---

## 4. Phased Build Plan (Self-Contained Task Specs)

### Phase 1 — Make Every Device Work End-to-End in Native

* **Goal:** Fix all verified host bugs and wire every real device class (Wooting 60HE v2 split bar, DualSense Edge / DualSense / Xbox, Audio + Exo EQ, and AGPL-isolated Mouse) end-to-end in `native-ui`.
* **Owners:** Host C# Engineer (`ExoLauncher/Services/*`) + Native Rust UI Engineer (`native-ui/crates/*`).
* **Files to edit:**
  * **Host (C#):**
    * `apps/launcher/ExoLauncher/Services/WootingKeyboard.cs`: query `HIDP_CAPS.OutputReportByteLength` for the opened config interface (`2047` on 60HE v2), zero-pad `WriteOutputReport` buffers to exact `OutputReportByteLength` (matching `HidService.cs:396-405`), and send `CmdWootDevInit` (`0x21`) + `CmdReloadProfile` (`0x26`) in `UpdateProfile` so actuation/RT/Tachyon writes persist cleanly on 60HE v2.
    * `apps/launcher/ExoLauncher/Services/WootingKeyboard.Backups.cs`: route `RestoreBackup` through the same `OutputReportByteLength`-padded writer.
    * `apps/launcher/ExoLauncher/Services/WebHostBridge.Controller.cs`: decouple Sony remote firmware check (`ControllerLab.LatestAsync` at line 30) from `controller.read` (return cached/instant firmware status, refresh in background, emit `devices.changed` if newer firmware found) so `controller.read` completes in `<100 ms` instead of blocking up to `20 s`.
    * `apps/launcher/ExoLauncher/Services/WebHostBridge.Devices.cs`: allow `audio.formats` to resolve active audio endpoints lazily even if `devices.list` hasn't been called in the current session.
    * `apps/launcher/ExoLauncher/Services/ExoEq.cs`: add `%PROGRAMDATA%\ExoLauncher\eq\exo.txt` writable config path (referenced from Equalizer APO `config.txt`) and expose actionable ACL/permission error state in `eq state` when `config.txt` is unwritable.
  * **Native RPC & UI (Rust):**
    * `apps/launcher/native-ui/crates/exo-rpc/src/lib.rs`: add all missing `METHOD_CONTROLLER_READ`, `METHOD_CONTROLLER_WATCH`, `METHOD_CONTROLLER_MEASURE`, `METHOD_CONTROLLER_TEST`, `METHOD_CONTROLLER_QUALITY`, `METHOD_CONTROLLER_CALIBRATE`, `METHOD_CONTROLLER_SETTINGS`, `METHOD_CONTROLLER_SOUND`, `METHOD_CONTROLLER_GAME_PROFILES`, `METHOD_CONTROLLER_GAME_PROFILES_SAVE`, `METHOD_AUDIO_FORMATS`, `METHOD_AUDIO_SET_FORMAT`, `METHOD_EQ_HEADPHONES`, `METHOD_KEYBOARD_RESTORE`, `METHOD_MOUSE_PROFILES_GET`, `METHOD_MOUSE_PROFILES_SET`, `METHOD_SYSTEM_MOUSE_CORE` constants and `controller.state` event deserializers (and wire existing unused `METHOD_KEYBOARD_OPEN_APP`).
    * `apps/launcher/native-ui/crates/exo-rpc/src/pc_types.rs`: fix `DeviceCard.wooting` (`pc_types.rs:666`) to match host's `lab: bool` field (`#[serde(default, alias = "wooting")] pub lab: bool`); fix `AudioEndpoint.format` (`pc_types.rs:732`) from `Option<String>` to `Option<AudioFormat>` (`{ rate: u32, bits: u32, channels: u32 }`); add `ControllerRead`, `ControllerState`, `EqState`, `EqHeadphone`, `MouseSummary` DTOs.
    * `apps/launcher/native-ui/crates/exo-ui/src/pc_actions.rs`: fix mic role filter (`role == "input"`, `pc_actions.rs:576, 581`); stop swallowing `!kb.ok` errors (revert optimistic state and show inline error banner); add async loaders & action dispatchers for Controller Lab (`controller.read`, `controller.watch`, `controller.calibrate`, `controller.test`, `controller.measure`, `controller.settings`, `controller.sound`, `controller.gameProfiles`, `controller.gameProfilesSave`), Audio/EQ (`eq`, `eq.headphones`, `audio.formats`, `audio.setFormat`), Wooting (`keyboard.restore`, `keyboard.openApp`, `rapidTriggerMm`), and Mouse IPC summary (`system.mouseCore` / `mouse.profiles.get` without importing AGPL code into MIT crates).
    * `apps/launcher/native-ui/crates/exo-ui/src/pc.rs` (split into `pc_devices.rs`, `pc_wooting.rs`, `pc_controller.rs`, `pc_audio.rs`, `pc_mouse.rs` to keep modules small):
      * Fix `wooting_key_matrix` to branch on `layout` and render true `ANSI · Split spacebar` (`2.75u Space | 1.25u Fn | 2.25u Space`), wire `.hit()`/`.on_press()` key inspection, add loading/error fallback instead of blank void when `s.pc.keyboard` is `None` (`pc.rs:512-514`), add RT mm control, backup restore list, firmware/Hz/port readout, and Open Wootility button.
      * Build native DualSense / Edge / Xbox detail view (live stick + trigger telemetry via `controller.state`, battery %, firmware, Edge 4-slot readout, calibration + auto-reapply status, light/sound/vibration, game profiles).
      * Build native Audio & Exo EQ detail view (output + mic controls, format selector, Exo Voice status, APO health + consent-gated install/selector, headphone picker, sound profile, Compare A/B, Night Shift).
      * Build native Mouse summary card + AGPL-isolated Exo Mouse handoff/IPC view (strictly honoring `AGENTS.md` AGPL boundary).
* **Acceptance checks:**
  * Host unit tests (`dotnet test`) cover Wooting 2047-byte report padding + `CmdReloadProfile`, non-blocking `controller.read`, and `AudioFormat` contract round-trip.
  * `cargo check` & `cargo test` in `apps/launcher/native-ui` pass with zero warnings.
  * Live hardware smoke test on owner's PC: clicking DualSense Edge shows live sticks/triggers, firmware `2.17`, calibration `Holds`, and writes light/vibration; changing Wooting 60HE v2 actuation/RT/Tachyon persists and reads back cleanly while showing the split spacebar; Audio shows `ZiiGaat Lush` + `fifine Microphone` + live EQ Compare toggle; Starlight X shows battery/status and opens Exo Mouse without license contamination.

### Phase 2 — Automatic Setup Flow, Simple Overview & Rune Motion Choreography

* **Goal:** Replace the heavy showcase grid + manual setup steps with the animated automatic-setup flow and calm simple overview designed in Section 3 and `docs-audit/devices/mockups/*.html`.
* **Owners:** Native Rust UI Engineer (`exo-ui`, `exo-kit`) + Host C# Engineer (setup persistence & consent actions).
* **Files to edit:**
  * **Contract & Host:**
    * `apps/launcher/ui/contracts/host-v1.json`: add `devices.setupState` / `devices.setupComplete` (persisted per device hardware ID / serial in `%LOCALAPPDATA%\ExoLauncher\devices-setup.json` so completed devices go straight to Overview while newly plugged hardware triggers the non-intrusive banner).
    * `apps/launcher/ExoLauncher/Services/WebHostBridge.Devices.cs`: implement `devices.setupState` & `devices.setupComplete`; expose concise per-device active-profile summary fields so the overview loads in one fast round-trip.
  * **Native UI (Rust):**
    * `apps/launcher/native-ui/crates/exo-ui/src/devices_setup.rs` (new): multi-step automatic setup coordinator (Detect -> per-device animated card -> consent gates for APO/firmware/drivers -> auto-backup for Wooting -> quick stick check for uncalibrated Edge -> Done).
    * `apps/launcher/native-ui/crates/exo-ui/src/devices_overview.rs` (new): simple one-row-per-device overview (status, battery, active profile, port warning only when suboptimal, `Change`, `Re-run setup`, hot-plug arrival banner).
    * `apps/launcher/native-ui/crates/exo-ui/src/pc.rs`: wire `Devices` room entry to check `setupState` (first run -> setup flow; configured -> simple overview; `Change` -> matched-geometry detail view).
    * `apps/launcher/native-ui/crates/exo-ui/tests/devices_motion_gate.rs` (new): `rune::motion_gate::Run` automated tests for setup step advance, setup-to-overview transition, and overview-to-detail `.matched()` flight under both `reduce_motion = false` and `reduce_motion = true`.
* **Acceptance checks:**
  * First run walks through DualSense Edge, Wooting 60HE v2, Finalmouse Starlight X, and Headphones/Mic with zero dead clicks, explicit consent before any install/elevation, and finishes on the simple overview.
  * `Re-run setup` replays cleanly; plugging in a new device displays the arrival banner without interrupting active work.
  * `cargo test` including `devices_motion_gate` passes for both normal and reduced motion.

### Phase 3 — Exo EQ v2 (Smart, Game-Specific & Objective)

* **Goal:** Upgrade Exo EQ from 4 basic curves into a smart, game-specific, loudness-protected, focus-aware, objectively verified system-level audio engine.
* **Owners:** Host Audio/DSP Engineer (C# + optional C++ VST/APO module) + Native Rust UI Engineer (`pc_audio.rs` / setup card).
* **Files to edit:**
  * **Host Audio & DSP:**
    * `apps/launcher/ExoLauncher/Services/AutoEqCatalog.cs`: bundle offline top-500 AutoEq JSON resource for instant setup matching + add USB/BT friendly-name fuzzy matcher (`MatchEndpoint`).
    * `apps/launcher/ExoLauncher/Services/ExoEqProfiles.cs`: expand to subtractive game-archetype + title-specific curve table (Tactical FPS, BR/Extraction, Arena/Hero FPS, Story/Dialogue, Cinematic Immersion, Studio Reference/Music) + strength scaling (`50%`/`100%`/`130%`) + tilt (`-1.5..+1.5 dB/oct`).
    * `apps/launcher/ExoLauncher/Services/ExoEqFocusWatcher.cs` (new): lightweight `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` matching only Exo-launched/tracked game PIDs, 1.5 s Alt+Tab debounce, optional "Keep game EQ on Alt+Tab", zero game injection.
    * `apps/launcher/ExoLauncher/Services/ExoEqMath.cs` (new): exact complex biquad `H(e^{j\omega})` evaluator across 20 Hz–20 kHz for deterministic true-headroom preamp calculation (`<= -0.3 dBFS` ceiling) and loudness-matched A/B compare offset.
    * `apps/launcher/ExoLauncher/Services/ExoEq.cs` + optional `apps/launcher/native-dsp/exo-dynamics/` (MIT C++ zero/low-latency true-peak limiter + Gunshot Guard / Night Shift dynamics stage loaded via Equalizer APO `VSTPlugin` with pure-APO fallback): wire Gunshot Guard, Night Shift v2 dynamics, and `%PROGRAMDATA%\ExoLauncher\eq\exo.txt` ACL-safe config path.
    * `apps/launcher/ExoLauncher/Services/WebHostBridge.Eq.cs` & `apps/launcher/ui/contracts/host-v1.json`: expose `eq` actions for `autoMatch`, `strength`, `tilt`, `gunshotGuard`, `altTabRevert`, `healthCheck`, and `responseCurve` (64-point log-spaced preview array for UI curve rendering).
    * `apps/launcher/ExoLauncher.Tests/ExoEqV2Tests.cs` (new): measure-free objective test suite validating complex transfer functions, pole stability, zero-clipping headroom across all profiles, CPU/latency budget benchmarks, and APO config round-trip safety.
  * **Native UI (Rust):**
    * `apps/launcher/native-ui/crates/exo-ui/src/pc_audio.rs` & `devices_setup.rs`: render live 64-point EQ frequency response curve, auto-matched headphone confirmation pill, per-game profile selector + strength control, Gunshot Guard toggle, Alt+Tab revert toggle, instant loudness-matched Compare A/B, and APO Health Check diagnostics.
* **Acceptance checks:**
  * `dotnet test` passes `ExoEqV2Tests`: 100% of headphone x game profile x Night Shift matrices maintain `<= -0.3 dBFS` peak headroom, zero unstable poles, `<0.5%` single-core CPU, `<= 1.5 ms` latency.
  * Launching a tracked FPS game switches `exo.txt` within `<50 ms` of foreground focus and reverts cleanly on Alt+Tab (when enabled) or game exit, with zero audio dropout and zero process injection.
  * Exo Voice remains sole owner of microphone DSP (zero APO capture entries written).

---

## 5. Static HTML Mockups (v2 Full-Window — 1920x1080, 2560x1440, 1280x720)

Full-window v2 mockups live in `docs-audit/devices/mockups-v2/` (with PNG screenshots at `1920x1080`, `2560x1440`, and `1280x720` in `docs-audit/devices/mockups-v2/screenshots/`, sharing real local hardware cutouts, store logos, and `Goeist` `.woff2` fonts from `docs-audit/assets/`). Original v1 centered-card mockups remain in `docs-audit/devices/mockups/` for comparison; see `docs-audit/setup-redesign.md` for the final layout decisions.

1. `01-setup-start.html` — Full-window automatic setup start under the top stage track (with the device-photo stepper strip) with 4 panoramic hardware bays, real device photography, and an interactive bottom dock (`Start setup`, `Preview sequence`, `Skip to overview`).
2. `02-setup-dualsense-edge.html` — Large Midnight Black DualSense Edge hero with 4 floating Liquid Glass telemetry callouts & leader lines (L2/R2 trigger stops + live stick center rings), bottom telemetry strip, and right automatic verification panel.
3. `03-setup-wooting-60he-v2.html` — Full-width ~1840 px interactive `ANSI · Split spacebar` (`[2.75u Space | 1.25u Fn | 2.25u Space]`) matrix under a small real keyboard photo header, above a bottom dock (automatic safety backup with `Saved` state, actuation, Rapid Trigger, Tachyon, and the Chipset USB port note). No right settings panel: the matrix stays full width, and the dock keeps all the settings.
4. `04-setup-audio-eq.html` — Full-width 20 Hz–20 kHz interactive Exo EQ v2 frequency response curve (`ZiiGaat Lush` AutoEq + JM-1 + live per-game overlay, `-3.9 dB` headroom, live `Compare A/B`) above a 3-column bottom band for Equalizer APO health, Smart Game EQ / Gunshot Guard, and `fifine Microphone` on Exo Voice.
5. `05-overview.html` — Full-width horizontal hardware overview strips (large product renders, 3 hairline live stat cells per device, `Change` button, `Re-run setup`, and toggleable hot-plug arrival banner preview). Keeps the app's normal top bar.
6. `06-detail-wooting.html` — Full-window Wooting 60HE v2 detail workbench (`< Devices` toolbar, real hardware photo + 4 telemetry cells, interactive split-spacebar key matrix, live 4.0 mm travel bar, right settings/backup-restore panel, and `Open Wootility`). Keeps the app's normal top bar.
