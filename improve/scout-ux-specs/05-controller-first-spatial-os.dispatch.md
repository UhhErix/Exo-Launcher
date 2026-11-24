EXO LAB: 10-Foot Controller-First Spatial OS & Thumbstick Radial Quick-Wheel (task-name: ux-controller-spatial-os)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.
HARDWARE SAFETY: Never write to the owner's real mouse, keyboard, controller, or any HID device (read-only inspection and headless synthetic gamepad events only). Never launch `%LOCALAPPDATA%\ExoLauncher\app`.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."

## Today

- **Critical bugs in `apps/launcher/native-ui/crates/exo-ui/src/gamepad.rs` and `state.rs:1534-1656`:**
  1. `gamepad.rs:75-155` polls `XInputGetState` unconditionally even when Exo is unfocused or a game is running — playing a game with a controller drives Exo in the background!
  2. `state.rs:1610-1654` only handles D-pad/stick navigation in `Room::Library` and `palette_open` — Store, Discord, Music, Devices, Exo AI, Apps, Settings, Captures, and GamePage have **zero directional controller navigation**!
  3. In Library, `Up => -6, Down => 6` is hardcoded even though HomeDeck is a 1-row strip and All Games is 11 columns (`library.rs:20`).
  4. `gamepad.rs:120-149` uses a square axis deadzone (`14000`), flat `180 ms` repeat with `0 ms` initial delay, and `dx` shadows `dy`.
  5. `shell.rs:105-122` shows one static Xbox-only prompt string (`"A Open · B Back · X Play · Y Search · LB/RB Rooms"`) regardless of room, modal, or PlayStation/Nintendo controller.
- Meanwhile, 3.x `ui/src/lib/gamepadNav.ts` + `gamepadMath.ts` already has a clean directional cone spatial picker, scope focus memory, right-stick smooth scroll, and slider stepping — ready to port and evolve!

## 1. Benchmark & Coverage Verification First

- **Automated controller-only traversal script (Rune headless test):**
  - Every room (8 rooms) + GamePage + StoreGamePage + Palette + Capture Popover + Settings + Key Modal must be 100% navigable, actionable, and dismissible using ONLY gamepad events.
  - **Metrics:**
    - Background input leaks while unfocused: `0` (verified via `App::on_focus(false)` test).
    - Rooms with full 2D spatial focus coverage: `8 / 8` (up from `1 / 8`).
    - Radial Quick-Wheel command latency (chord press -> stick flick -> release -> action fired): `<= 150 ms`.

## 2. Build the Controller-First Spatial OS

- **Focus-Gated Input Pipeline (`gamepad.rs` + `main.rs`):**
  - Wire Rune's `App::on_focus` (`crates/rune/src/app.rs:267`) into an `AtomicBool` shared with the gamepad polling thread; pause input dispatch immediately when `!focused` or a game owns foreground.
  - Upgrade stick math: radial deadzone (`r = hypot(lx, ly) > 0.28`), dominant-axis resolution, accelerating key repeat (`340 ms` initial -> `105 ms` steady -> `65 ms` fast), plus **right-stick analog inertial scrolling** feeding Rune's `tx.scroll_to_with` velocity-inheriting glide!
  - Support `LT`/`RT` for sub-tab cycling, `Start/Menu` for Game/Room context menu, `View/Share` for Captures/Replay, and slider fine-stepping when a slider has focus.
- **Universal 2D Spatial Focus Graph & Scope Memory (`exo-ui`):**
  - Port `ui/src/lib/gamepadMath.ts`'s geometric cone + perpendicular penalty spatial picker into `native-ui`, resolving candidate rects from Rune's layout tree across ALL 8 rooms, popovers, and modals.
  - Remember last-focused element key per room/modal (`HashMap<ScopeId, String>`) and restore cleanly on Back (`B`/`Circle`).
- **Dynamic Controller Glyph Bar (`shell.rs` + `ui/src/components/shell/GamepadBar.tsx`):**
  - Detect Xbox, DualSense/DualShock (Cross/Circle/Square/Triangle), and Switch Pro (B/A/Y/X) from `Peripherals` / VID+PID.
  - Render context-aware prompt pills (e.g., Library: `A Open · X Play · Y Search · Menu Options`; GamePage: `A Play · X Properties · B Back`; Music: `A Play/Pause · LT/RT Skip`).
- **Signature Feature — Thumbstick Radial Liquid Glass Quick-Wheel (`LT + RT` or hold `View`):**
  - Unfurls an 8-wedge circular Liquid Glass wheel around center screen; pointing the right stick highlights a wedge with a haptic tick (Spec 03) and releasing executes in `< 150 ms` (Play/Resume top game, Mute Discord Mic, Save Replay, Skip Music, Switch Device Profile, Toggle FPS HUD, Open Ctrl+K, Minimize).
- **Automatic 10-Foot Density Handshake:**
  - When `InputMode` switches to `Gamepad`, smoothly bump focus rings, card spacing, and legible type scale via `DensityMode::TenFoot` (coordinating with Spec 08), reverting cleanly on pointer movement.

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (unit tests for radial deadzone, focus-loss suppression, spatial picker across all 8 rooms, radial wheel wedge selection, `motion_gate`), `cargo build --release --locked --bin exo-ui`.
- `ui/`: `npm run check`, `npm run dead`, `npm run build` (for glyph bar + radial wheel parity in 3.x).
- Capture headless `--snap` screenshots of the Radial Quick-Wheel and PlayStation/Xbox contextual prompt bars.

## 4. Deliver

- Branch `lab/ux-controller-spatial-os` from `origin/main`.
- `improve/ux-controller-spatial-os-report.md`: room-by-room traversal pass matrix, focus-gate test proof, radial wheel snaps, and latency numbers.
- Working implementation in `native-ui` + `ui/` (no prose comments).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-controller-spatial-os results" --body "<rooms covered 8/8, focus-gate status, radial wheel latency ms, commit sha>"`. Effort: high.
