EXO LAB: Spatial Continuity — Universal Shared-Element Flights, Living Launch Ritual & Post-Session Recap (task-name: ux-spatial-launch-ritual)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."
> Owner (2026-11-10, `tokens.css:152`): "hate the outline thing when selecting."

## Today

- In `apps/launcher/native-ui/crates/exo-ui/`:
  - `.matched()` shared-element flights exist ONLY on `nav-active-pill`, `deck/grid -> game_page`, and `store_tile -> store_game`. Music cover -> Immersive, Capture thumbnail -> Editor, Device card -> Device detail, and Search -> Palette all hard-cut!
  - `library.rs:163-165` draws a bright white `.outline(2.0, 3.0, FG)` around the selected HomeDeck tile — directly contradicting the owner's rule in `tokens.css:152` (visible in `library.png` parity snap).
  - Pressing `Play` shows a dry static `"Preparing"` button with zero visibility into what Exo is doing (shader check, per-game device/EQ profile application, silent store handoff) and no inline Cancel.
  - Exiting a game returns to a static Library with zero acknowledgment of the session you just played (duration, achievements unlocked, clips captured, average/1%-low FPS).

## 1. Benchmark & Motion Gate First

- **Targets:**
  - `6 / 6` media open/close surfaces using `.matched().matched_crop()` on `SPRING_FLIGHT` (300 ms critically damped) with `tx.unclipped_flights()`.
  - `0` Rune `motion_gate` findings (`flicker`, `overshoot`, `jerk`, `pop-in`, `cut-exit`, `never-settles`, `wakes-after-end`) in BOTH normal and `reduce_motion` modes!
  - `< 100 ms` post-session recap appearance after game exit using already-collected session telemetry.

## 2. Build Universal Flights + Living Launch Ritual + Post-Session Recap

- **Retire the White Selection Outline (`library.rs:163-165`):**
  - Remove `.outline(2.0, 3.0, FG)` on pointer/resting active tiles; replace with subtle `-6px` elevation, crisp `1.0` vs `0.86` brightness contrast, and a soft OKLab cover-color ambient backlight behind the active poster on Void black (keeping the 2px focus ring strictly for keyboard/gamepad `:focus-visible`).
- **Universal Shared-Element Flight Matrix (`exo-ui`):**
  - Wire `.matched()` + `.matched_crop()` across:
    1. `music-art:<uri>` (Music track/mini-player -> Immersive full-window cover).
    2. `capture-thumb:<id>` (Captures grid thumbnail -> Clip/Snip Editor canvas).
    3. `device-photo:<id>` (Devices showcase card -> Device detail header).
    4. `search-palette` (Top-bar Search capsule -> Command Palette header).
    5. Preserve and refine existing `deck/grid -> game_page` and `store -> store_game` flights.
  - Ensure `reduce_motion` cleanly crossfades in place with `0` spatial travel (`main.rs:2221` invariant preserved!).
- **The Living Launch Ritual:**
  - Pressing `Play` morphs the primary Play pill smoothly (`hold_landing_width()`) into an inline **Pre-Flight Telemetry Capsule** cycling through real host phases (`Checking shaders` -> `Applying Valorant profile (800 DPI · Footsteps EQ)` -> `Starting Steam quietly` -> `Launching`) with a crisp `Cancel` pill on the right.
  - Once the game window activates, Exo gracefully yields to sleep mode (0 GPU/CPU frames).
- **Instant Post-Session Recap Strip:**
  - When a session lasts `>= 5 minutes` and exits, reveal a single quiet, dismissible Liquid Glass hairline strip inside the Library hero (auto-hides after 60 s or on 1 click, never a modal!):
    `"Just played 1 hr 42 min · Avg 168 FPS (1% low 124) · 2 Achievements · 3 Clips [Open captures] [×]"`
  - Wire from existing host session-end data (`FpsCounter`, `AchievementWatch`, `Captures`, `Playtime`).

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (add `motion_gate` recordings for all 4 new flights, Living Launch morph, and Post-Session Recap enter/dismiss in both normal and `reduce_motion`), `cargo build --release --locked --bin exo-ui`.
- Capture before/after `--snap` PNGs of `library`, `game`, and the new Living Launch & Post-Session Recap states.

## 4. Deliver

- Branch `lab/ux-spatial-launch-ritual` from `origin/main`.
- `improve/ux-spatial-launch-ritual-report.md`: `motion_gate` cell report (all green), before/after PNGs, and flight matrix verification.
- Working implementation in `exo-ui` (and minimal host session-summary event if not already emitted, synced via `npm run contracts:write`).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-spatial-launch-ritual results" --body "<flights wired 6/6, motion_gate findings 0, recap states verified, commit sha>"`. Effort: high.
