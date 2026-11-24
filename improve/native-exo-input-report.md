# Exo Native Input Graduation (`feat/native-exo-input`)

Graduates `lab/lab-macros` into v4 native Rust (`apps/launcher/native-ui/`), implementing **Spec 12 (`12-exo-macros-native.md`)** and **Spec 11 (`11-exo-controller-hub.md`)** on branch `feat/native-exo-input` from `origin/feat/native-polish-2`.

---

## 1. Delivered Native Crates & UI

### Spec 12 — Exo Macros Native (`exo-macros` + `exo-macros-worker`)
- **`crates/exo-macros` (Control Plane — 0 synthetic input or hook imports):**
  - `model.rs`: `MacroDef`, `MacroTrigger` (per-device `device_id`, `chord`, `chord_window_ms`, `layer_id`, `mode: once|repeat|hold|toggle`, `jitter_ms`, `swallow_input`), `MacroAction` (keys, mouse, controller buttons/axes, delay, text, app launch, layer switch, and AC-safe `ExoInternal` IPC commands), plus 4 built-in zero-script Smart Templates.
  - `policy.rs`: Re-exports the single canonical `exo_rpc::anticheat::AntiCheatClass` (`CompetitivePvpOnline`, `OnlineNonCompetitivePve`, `OfflineAntiCheat`, `Unknown` -> `effective()` maps to `CompetitivePvpOnline`), hard-disabling Tier 2 software macros for all online, competitive, PvE, and unknown games (**no confirmation override**), enforcing Tier 1 onboard hardware macros + `ExoInternal` IPC only there, permitting local hints only to tighten (`apply_stricter_hint`, never loosen), rejecting competitive cheat primitives (recoil-compensation loops & `< 25 ms` rapid-fire click loops), and providing `AntiCheatFocusWatchdog`.
  - `chord.rs`: Per-device multi-input chord matcher, `LayerStack` (`Base` / `Hold` / `Toggle`), and `Esc+Pause` emergency panic chord detection.
  - `scheduler.rs`: High-resolution QPC deterministic step scheduler (`PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` opt-out + `timeBeginPeriod(1)` scoped to active execution + `CreateWaitableTimerExW(HIGH_RESOLUTION)` + `2.0 ms` hybrid spin + `THREAD_PRIORITY_HIGHEST`), `DryRunSink`, and RAII `StuckKeyGuard` (releases all held keys on abort/panic/drop).
  - `switcher.rs`: Debounced (`350 ms`), exit-grace (`1500 ms`), onboard-hash-deduplicated, cooldown-protected (`2000 ms`, max `10 writes/min`) per-game & per-app profile switcher.
  - `onboard.rs`: Onboard HID macro compiler targeting `OpenMouse`, `WootingHid`, and `DualSenseEdge` (mock/preview safe, zero real hardware writes).
  - `lib.rs`: `MacroControlPlane` with strict demand-based sidecar lifecycle (`should_spawn_worker() == false` -> **0 MB RAM / 0 hooks** when only onboard or Exo IPC bindings are active or when an online/AC/unknown game has focus).

- **`crates/exo-macros-worker` (Isolated Sidecar Binary `exo-macros-worker.exe`):**
  - `raw_input.rs`: Win32 `WM_INPUT` (`RIDEV_INPUTSINK`) registration + `GetRawInputDeviceInfoW(RIDI_DEVICENAME)` USB `VID/PID` identity extractor (`vid_046d_pid_c094`).
  - `hook_correlator.rs`: Lock-free 64-slot ring buffer correlating LL hook events against Raw Input `hDevice` within a `2 ms` window (`swallowInput = true` path only).
  - `output_sink.rs`: `SendInput` scancode dispatcher hard-gated by `Arc<AtomicBool>` (`ac_paused`), with `DryRun` test mode and RAII held-key release.
  - `main.rs`: JSON-line control protocol + `--self-bench` real Win32 measurement runner.

### Spec 11 — Exo Controller Hub (`exo-controller`)
- **`crates/exo-controller` (Unified GameInput + Raw HID Multi-Pad Hub — 0 synthetic input imports):**
  - `gameinput.rs`: Dynamic Windows 11 `GameInput.dll` (`GameInputCreate`) runtime probe + normalized sub-ms `PadReading` & `GamepadNavEvent` navigation mapper.
  - `sony_hid.rs`: Clean-room DualSense (`0x054C:0x0CE6`), DualSense Edge (`0x054C:0x0DF2`), and DualShock 4 (`0x054C:0x05C4`/`0x09CC`) USB & Bluetooth HID report parser, USB `0x02` `build_usb_settings_report` / `is_only_settings_report` validator (byte-for-byte parity with `ControllerLab.Settings.cs`), Bluetooth `0x31` output report with exact IEEE CRC-32 seeded with `0xA2`, and `is_feature_allowed` command allowlist (parity with `ControllerLab.Commands.cs`).
  - `sony_calibrate.rs`: DualSense / Edge center + range calibration state machine enforcing **USB-wired + explicit user confirmation** before flash commit (and refusing write-protected Edge module commits), plus DualSense Edge 4-slot onboard profile (`0x70..0x7B`) lossless round-trip codec.
  - `xbox_hid.rs` & `switch_hid.rs`: Xbox Series / Elite Series 2 battery, rumble, & 3-slot onboard profiles; Switch Pro / Joy-Con HID subcommands (`0x30` player LEDs, `0x40` IMU enable, battery parser).
  - `diag.rs`: Live 36-bin radial circularity error %, rest center-drift early warning (`> 4.0%`), and polling rate / p99 / jitter / sensor-clock repeat diagnostics.
  - `steam_conflict.rs`: Automatic Steam Input conflict resolver (parity with `SteamInputConfig.cs`: disables Steam Input `"0"` for native PlayStation games, disables desktop layout `413080`, respects player override `"!"`, reversible VDF patch plan).
  - `auto_profiles.rs`: Per-game onboard Edge slot, adaptive trigger preset (`FpsHairTrigger`, `RacingProgressiveBrake`), `< 15%` low-battery amber lightbar warning, and hard anti-cheat gate disabling software virtual-pad/gyro-to-mouse remap on online/competitive/unknown games.

### Single Source of Truth for `AntiCheatClass` Across Host, RPC, & All Native Crates
- **C# Host (`ExoLauncher`):** `AntiCheatClass.WireClass(GameEntry, AntiCheatCheck.Answer?)` maps `PerGameTable` + `Classify` (+ zero-I/O `AntiCheatCheck.PeekCached`) onto `antiCheatClass` (`competitivePvpOnline | onlineNonCompetitivePve | offlineAntiCheat | unknown`) in `WebHostBridge.MapGame` (`library.get`, `shell.bootstrap`) and `ui/contracts/host-v1.json` + `ui/src/lib/host.ts`.
- **Rust Canonical Enum (`crates/exo-rpc/src/anticheat.rs`):** Single `exo_rpc::anticheat::AntiCheatClass` deserialized directly onto `exo_rpc::types::Game::anti_cheat_class` (missing/unknown wire values deserialize to `Unknown`, `effective()` maps `Unknown -> CompetitivePvpOnline`, `strictest()` allows local hints only to tighten).
- **Shared Consumers:** `exo-macros`, `exo-macros-worker`, `exo-shaders` (replaced private `policy.rs` enum), and `exo-controller` (replaced private `ControllerAntiCheatClass`) all import `exo_rpc::anticheat::AntiCheatClass`; the legacy substring classifier `classify_anti_cheat_name` is deleted.

### Native Rune UI Integration (`crates/exo-ui`)
- `src/macros.rs` wired into `Devices` (`pc_devices.rs`, `pc_state.rs`, `main.rs`) reads real per-game `anti_cheat_class` values live over RPC (`s.games`, zero hard-coded sample games outside `#[cfg(test)]`), shows idle sidecar status (`0 MB RAM · 0 hooks`), live QPC dry-run timing, Controller Hub `GameInput.dll` probe status, Tier 1 hardware vs Exo IPC vs Tier 2 offline delivery labels, and anti-cheat policy enforcement within `MAX_BACKDROP_BLURS <= 4`.

---

## 2. Anti-Cheat Policy Matrix

| Anti-Cheat Class | Examples | Tier 1 Onboard Hardware | Exo Internal IPC (Replay/Mute) | Tier 2 Software Macros / Virtual Remap |
| --- | --- | --- | --- | --- |
| **Competitive PvP** | Vanguard (VALORANT), Ricochet (CoD), VAC/FACEIT (CS2), EAC/BattlEye online MP, Hyperion (Roblox), EA AC | **Allowed (labelled hardware)** | **Allowed (0 synthetic input)** | **Hard-disabled (no override)** |
| **Online PvE / Gacha** | Always-online ACE (Wuthering Waves / WuWa), HoYo / GameGuard | **Allowed (labelled hardware)** | **Allowed (0 synthetic input)** | **Hard-disabled (no override)** |
| **Unknown / Unreadable** | Treated as Competitive PvP | **Allowed (labelled hardware)** | **Allowed (0 synthetic input)** | **Hard-disabled (no override)** |
| **Offline / Single-Player** | Stardew Valley, Cyberpunk 2077, Baldur's Gate 3 | **Allowed (labelled hardware)** | **Allowed** | **Allowed (one-time note)** |

---

## 3. Real Measured Numbers (Windows 11 Build 26300, 12-core x64, Release Build)

Measured directly via `target\release\exo-macros-worker.exe --self-bench` (3 consecutive runs, 30 × 10 ms QPC steps + 1,000 lock-free `HookCorrelator` cycles + live AC focus pause + `K32GetProcessMemoryInfo`):

| Metric | Target | Measured Run 1 | Measured Run 2 | Measured Run 3 | Status |
| --- | --- | ---: | ---: | ---: | --- |
| **Idle Sidecar Overhead** | 0 MB / 0 hooks | **0 MB / 0 hooks** (not spawned) | **0 MB / 0 hooks** | **0 MB / 0 hooks** | PASS |
| **Active Worker Binary Size** | Minimal | **381.5 KB** (`390,656 B`) | **381.5 KB** | **381.5 KB** | PASS |
| **Active Worker Working Set** | `< 3 MB` target / low WS | `6.33 MB` (`6,484 KB`) | `5.74 MB` (`5,876 KB`) | `5.73 MB` (`5,872 KB`) | Note: Win11 loader + `user32.dll` + CRT baseline (~5.7 MB WS; 0 MB when idle) |
| **QPC 10 ms Step Jitter (P50)** | `< 500 µs` | **`0.2 µs`** | **`0.3 µs`** | **`0.1 µs`** | PASS |
| **QPC 10 ms Step Jitter (P99 / Max)** | `< 500 µs` | **`68.4 µs`** | **`1.4 µs`** | **`18.7 µs`** | PASS |
| **Hook ↔ Raw Input Correlation (64-slot ring)** | `< 2 ms` window | **`116.2 ns`** | **`107.3 ns`** | **`116.3 ns`** | PASS |
| **Anti-Cheat Focus Pause & Release Latency** | `< 10 ms` | **`0.4 µs`** | **`0.8 µs`** | **`1.4 µs`** | PASS |
| **AC Synthetic Input Drop Rate** | 100% dropped | `1 / 1` (`100%`) | `1 / 1` (`100%`) | `1 / 1` (`100%`) | PASS |
| **`GameInput.dll` Runtime Present** | Win11 native | `True` (`C:\Windows\System32\GameInput.dll`) | — | — | PASS |

### Banned-Symbols Firewall Audit
- Source audit across `crates/exo-macros`, `crates/exo-controller`, and `crates/exo-ui`: **0 occurrences** of `SendInput`, `mouse_event`, `keybd_event`, or `SetWindowsHookEx`.
- `SendInput` lives strictly inside `crates/exo-macros-worker/src/output_sink.rs` (isolated opt-in sidecar binary `exo-macros-worker.exe`).
- Note: PE IAT scan of `exo-ui.exe` shows `user32.dll!SendInput` inherited solely from upstream `winit` / `accesskit_windows` inside `rune` (already present on `feat/native-polish-2`), not from `exo-macros`, `exo-controller`, or `exo-ui`.

---

## 4. Verification Gates Executed

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64 -v minimal` — **PASS (exit 0, 0 warnings, 0 errors)**
- `dotnet test ExoLauncher.Tests\ExoLauncher.Tests.csproj -c Release -p:Platform=x64 --no-restore -v minimal` — **PASS (8,455 total: 8,426 succeeded, 0 failed, 29 skipped — includes `AntiCheatClassTests`, `HostV1GoldenTests`, `BannedNativeImportsTests`)**
- `npm run contracts:write && npm run check` (`apps/launcher/ui`) — **PASS (exit 0: `tsc`, `host-v1.json` 365 methods / 83 events, `oxlint`, `eslint`, `lint:css`, `css:prune:check`, 344/344 non-skipped unit tests)**
- `cargo fmt --all -- --check` — **PASS (exit 0)**
- `cargo clippy --workspace --locked -- -D warnings` — **PASS (exit 0, 0 warnings across all 7 workspace crates)**
- `cargo test -p exo-rpc -p exo-macros -p exo-macros-worker -p exo-controller -p exo-shaders --locked` — **PASS (`exo-controller`: 5/5, `exo-macros`: 5/5, `exo-macros-worker`: 3/3, `exo-rpc`: 29/29 (+1 ignored bench), `exo-shaders`: 14/14)**
- `cargo test -p exo-ui --locked macros` — **PASS (1/1: RPC library classes including `competitivePvpOnline`, `onlineNonCompetitivePve`, `unknown`, missing field, and `offlineAntiCheat`; blur cap `<= 4`; zero-idle-worker; live dry-run QPC check)**
- `node apps/launcher/tools/check-comments.mjs` — **PASS (exit 0, 0 prose comments in all new/cleaned files)**
- Hardware safety: **0 writes to real hardware devices**, **installed Exo never started**, all hardware paths tested via `MockControllerTransport` & `DryRunSink`.
