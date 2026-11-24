EXO LAB: Native Peripherals Live Telemetry Studio & Physical Calibration Lab (240 Hz HID Oscilloscope) (task-name: ux-devices-telemetry-lab)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## Hard License Boundary (CRITICAL — Read `apps/launcher/AGENTS.md:56`)

- `apps/launcher/mouse/` is **AGPL-3.0-or-later** (`@openmouse/protocol`). NEVER copy any code from `mouse/` or `@openmouse/protocol` into MIT-licensed `ExoLauncher/` or `native-ui/`!
- Native Exo Mouse integration MUST either:
  1. Communicate over a clean local IPC boundary (stdin/stdout NDJSON or named pipe) with the standalone AGPL `mouse/` engine (exactly how `shaders/` runs `ExoShaders.Service.exe` over NDJSON stdio!), OR
  2. Build clean-room native HID telemetry/calibration panels in `ExoLauncher` + `native-ui` using only Windows Raw Input / existing MIT `ExoLauncher/Services/Peripherals*.cs` & `ControllerLab*.cs`!

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... feature, menu, framework, UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."
> Vision: "the best performance from every part of every PC."

## Today

- In `apps/launcher/native-ui/crates/exo-ui/src/pc.rs:511-540`:
  - Clicking a **Mouse** or **Controller** card in native Devices drops you onto a bare 3-line read-only card (`Wired / Battery / Polling rate`) with zero settings!
  - `pc.rs:161-393` (`wooting_key_matrix`) draws a static keyboard grid with no live key-actuation depth animation.
  - `pc.rs:36` renders a headphones icon for USB microphones (`Shure MV7 USB Microphone`).
- In 3.x `apps/launcher/mouse/src/SensorAngle.tsx`: Exo already has a standout raw-input pointer-lock wrist-angle auto-calibrator (`requestPointerLock({ unadjustedMovement: true })`), trapped inside a WebView2 iframe (`mouseRelay.ts`).
- No vendor app (G Hub, Synapse, Wootility, iCUE, XPANEL) combines 240 Hz GPU-rendered live mouse polling/jitter oscilloscope + live Hall-Effect actuation depth waterfall + live controller stick-drift polar plot + blind Flick-Feel A/B calibration + per-game profile badges in one <15 MB native studio!

## 1. Benchmark first, with real numbers

- **Targets:**
  - Live HID-to-photon visualization rate: `240 Hz` (`<= 4.2 ms` GPU frame time, `< 1.5%` CPU during an `8000 Hz` raw mouse sweep or rapid Wooting typing).
  - Mouse raw-input oscilloscope accuracy: `<= 0.1 ms` timestamp jitter using `QueryPerformanceCounter` / Win32 `WM_INPUT` `RIDEV_INPUTSINK` (no synthetic input, read-only!).
  - `100%` license separation verified (zero `@openmouse` or `mouse/` code imported into `ExoLauncher`/`native-ui`).

## 2. Build the 240 Hz Native Peripherals Studio & Calibration Lab

- **Mouse Live Telemetry & Calibration Lab (`native-ui` + `mouse/` upgrade):**
  - **Live 1000–8000 Hz Polling & Motion-Sync Oscilloscope:** Real-time GPU-drawn inter-packet interval histogram & smoothness waveform reacting as you move your mouse across the pad, immediately surfacing bad USB hub jitter or CPU core contention (directly linked to Exo's existing Mouse CPU Core pinning!).
  - **Sensor Angle Auto-Calibrator (Native Raw Input):** Clean-room Win32 `WM_INPUT` raw-delta swipe calibrator in `native-ui` (matching `mouse/src/SensorAngle.tsx`'s wrist-tilt UX without needing browser pointer-lock!).
  - **Flick-Feel Blind A/B Micro-Lab:** 15-second raw-input target drill comparing two DPI / LOD / Angle / Debounce presets blind and plotting overshoot/undershoot error in mm so players pick settings by real motor data instead of placebo!
  - **Full Mouse Controls in v4:** Bridge `mouse/` settings cleanly over an isolated AGPL sidecar IPC (mirroring `shaders/engine/CONTRACT.md`), so native `Devices` renders DPI stages, polling rate, LOD, Finalmouse/Logitech/Pulsar/Endgame controls, and per-game auto-profiles in native Rune Liquid Glass with zero iframe!
- **Wooting Hall-Effect Live Actuation Waterfall (`pc.rs`):**
  - Upgrade `wooting_key_matrix` so every key dynamically fills bottom-to-top in real time at 240 Hz as you press keys, with a crisp horizontal hairline showing exact Actuation Point (`0.1–4.0 mm`) and Rapid Trigger reset bar!
- **Controller Stick-Drift Polar Plot & Adaptive Trigger Preview (`pc.rs`):**
  - Live dual-stick polar drift plot with circularity error %, live deadzone ring preview (using Spec 09 precision-scrub sliders!), and real-time DualSense adaptive trigger resistance feel test as you drag trigger curves!
- **Audio & Mic Live Meter + EQ Curve (`pc.rs`):**
  - Fix `pc.rs:36` mic icon, add interactive parametric EQ frequency-response curve canvas + live pre/post Noise Suppression voice level meter for microphones.
- **Per-Game Auto-Profile Strip on Every Device Card:**
  - Show active game profile mapping right on each device card (`"Valorant -> 800 DPI · 0.1 mm RT · Footsteps EQ"`).

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked`, `cargo build --release --locked --bin exo-ui`.
- `mouse/`: `npm run check`, `npm run build` (if AGPL sidecar/mouse UI touched).
- `ExoLauncher.Tests`: `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (including `BannedNativeImportsTests` green).
- Capture headless `--snap` PNGs of all 4 native device detail views (Mouse Oscilloscope/Lab, Wooting Live Matrix, Controller Polar Plot, Audio EQ/Mic) and benchmark 8000 Hz raw-input CPU/GPU frame pacing.

## 4. Deliver

- Branch `lab/ux-devices-telemetry-lab` from `origin/main`.
- `improve/ux-devices-telemetry-lab-report.md`: 240 Hz frame-pacing benchmarks under 8000 Hz input, license-boundary verification, before/after snaps of all 4 device pages.
- Working implementation respecting the strict MIT/AGPL boundary (no prose comments).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-devices-telemetry-lab results" --body "<240Hz frame ms at 8kHz input, 4/4 device detail parity, license check clean, commit sha>"`. Effort: high.
