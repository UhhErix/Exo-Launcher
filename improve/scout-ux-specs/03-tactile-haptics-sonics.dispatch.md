EXO LAB: Micro-Acoustic UI Foley & DualSense / XInput Tactile Haptics Engine (task-name: ux-tactile-haptics)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.
HARDWARE SAFETY: Never write to the owner's real mouse, keyboard, controller, or any HID device (`XInputSetState`/HID writes must be verified via unit-tested mock/virtual sinks in agent sessions — never fire rumble/HID writes at physical hardware). Never launch `%LOCALAPPDATA%\ExoLauncher\app`.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."

## Today

- `apps/launcher/ui/src/`: `0` matches for `AudioContext`, `vibrationActuator`, `haptic`, or `rumble`.
- `apps/launcher/native-ui/crates/exo-ui/src/gamepad.rs:59-68`: dynamically loads `xinput1_4.dll` and imports ONLY `XInputGetState` — never `XInputSetState`!
- Only `ExoLauncher/AchievementBanner.cs:236-269` (achievement unlock WAV via `winmm.dll`) and `ExoLauncher/Services/DiscordClient/DiscordSounds.cs` (Discord ring/join/leave) produce audio; `ExoLauncher/Services/ControllerLab.Settings.cs` already speaks HID to DualSense controllers.
- Navigating Exo with a controller, dragging sliders, arming Confirm-in-Place, or launching a game has zero tactile audio or haptic feedback.

## 1. Benchmark & Latency Budget First

- **Metrics to measure:**
  - Input-to-sound onset latency: `<= 12 ms` (WASAPI shared-mode / WebAudio `AudioContext` `latencyHint: 'interactive'`).
  - Input-to-rumble onset latency: `<= 10 ms` (`XInputSetState` / DualSense HID report).
  - Idle CPU & RAM overhead: `0.0% CPU` when silent (stream suspends after 500 ms idle), `< 64 KB` DSP memory, `0` bundled WAV bloat (`< 30 KB` code total).
  - Game-foreground safety: `100%` immediate auto-mute & zero controller vibration commands sent whenever Exo is unfocused or any game process is foregrounded.

## 2. Build the Procedural Micro-Foley + Haptics Engine

- **Procedural Acoustic Synth (zero static WAV assets):**
  - Synthesize ultra-clean, dry, tactile micro-impulses in real time (short filtered noise bursts + critically damped sine taps, `-24 dBFS` peak — Apple/PS5 restraint, never arcade beeps!):
    - `FocusStep { velocity, column_ratio }`: 9 ms damped wooden/glass tick; pitch nudges subtly (`±8%`) with horizontal grid index and scroll velocity.
    - `SliderDetent { is_default }`: crisp 5 ms mechanical click at round/default detents during drag.
    - `ConfirmArm` & `ConfirmCommit`: two-stage tactile latch (soft tension click on arm -> warm low-end thock on commit; soft downward release on disarm/Esc).
    - `RoomSwitch`: feather-light 18 ms filtered air brush synced to `SPRING_MOVE`.
    - `LaunchGame`: restrained harmonic low-frequency swell as the Living Launch starts.
    - `ActionSuccess` / `ActionError` / `ToastArrive`: clean micro-chimes distinct from Discord/Achievement sounds.
- **Dual-Tier Controller Haptics (`gamepad.rs` + host `ControllerLab`):**
  - **XInput (`XInputSetState`):** Ultra-short high-frequency right-motor micro-pulses (`8–16 ms` at `12–28%` duty) for focus steps & detents; brief low-frequency left-motor thump (`22 ms`) for ConfirmCommit / Play / Download Complete. Auto-zero timer guarantees motors NEVER stick on crash/focus-loss!
  - **DualSense / DualSense Edge:** Route impulse envelopes through DualSense HD voice-coil / haptic actuator path when available (fallback to XInput).
- **Single Clean Setting:**
  - Add `Tactile feedback` (`Controller & UI` / `Controller only` (default) / `Off`) in Settings › General › Feel (persisted in `LauncherSettings` + `host-v1.json`). Respect Windows mute, Streamer Mode, and `reduce_motion` (softens/disables motion-linked foley).

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (unit tests verifying zero-duration auto-stop timer, unfocused suppression, DSP peak `<= -18 dBFS`, no NaNs).
- `ui/`: `npm run check`, `npm run dead`, `npm run build` (and `npm run contracts:write` for the new setting).
- `ExoLauncher.Tests`: `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64`.
- Record short synthetic stereo WAV samples of the 6 procedural impulses into `improve/ux-tactile-haptics-samples/` (or verify spectrum/duration programmatically) so the coordinator can audition them directly.

## 4. Deliver

- Branch `lab/ux-tactile-haptics` from `origin/main`.
- `improve/ux-tactile-haptics-report.md`: onset latency measurements, CPU/RAM idle readings, impulse specs (duration/Hz/dBFS), and game-focus safety proof.
- Working engine in `exo-ui` + `ui/` + `host-v1.json` setting (no prose comments).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-tactile-haptics results" --body "<onset latency ms, idle CPU/RAM, controller types tested, commit sha>"`. Effort: medium.
