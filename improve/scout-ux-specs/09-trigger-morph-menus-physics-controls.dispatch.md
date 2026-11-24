EXO LAB: Physics-Native Controls — Trigger-Origin Morph Menus, Precision-Scrub Sliders & Real FLIP Confirm-in-Place (task-name: ux-physics-controls)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... menu, UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."
> `exo-design/SKILL.md`: "respond on pointer-down, not on release; a drag tracks the pointer 1:1 from where it was grabbed; every animation can be interrupted; push past an edge against growing resistance rather than a hard stop; things leave the way they came and grow from their trigger."

## Today (Verified Bugs & Gaps)

- `apps/launcher/native-ui/crates/exo-kit/src/components.rs:11-15`: `popover_transition()` hardcodes `Vec2::new(1.0, 0.0)` (top-right origin) for EVERY popover regardless of which side/edge its trigger sits on!
- `apps/launcher/ui/src/components/ui/ConfirmButton.tsx:31-37`: `morph(element, from)` measures `from` width, then ignores `from` and only runs `scale(0.96) -> scale(1)` — the button width jumps instantaneously instead of smoothly morphing!
- `apps/launcher/ui/src/components/ui/Menu.tsx:19-32`: `rowsOf` selects `[aria-disabled="true"]` items, so ArrowDown/ArrowUp lands focus on disabled menu rows; no typeahead jump on menus with `<= 10` items; no safe-triangle submenu corridor.
- `apps/launcher/ui/src/components/ui/Slider.tsx:1-14`: Plain native `<input type="range">` with no magnetic default detents, no floating value pill while dragging, no double-click-to-reset, and no perpendicular fine-precision scrubbing.
- `apps/launcher/native-ui/crates/exo-ui/src/notice.rs:94`: Left toast drag hits a hard stop at `SWIPE_MIN_DX = -24.0` instead of exponential rubber-band resistance; no inline action/Undo button; no stacked fan collapse.
- `apps/launcher/native-ui/crates/exo-ui/src/key_modal.rs:90-101`: Disabled `Activate` button stays `.focusable(true)` and `.hit()`.
- `native-ui/crates/exo-ui/src/**`: `0` uses of Rune's `.focus_group()` or `.focus_return(trigger)`.

## 1. Benchmark & Motion Gate First

- **Targets:**
  - `0` layout-thrash frames during Confirm-in-Place width morph, menu open/close, and toast fan expand/collapse.
  - `10×` finer slider precision (`0.1×` gear at `>= 72 px` perpendicular drag) on any 100 px track (DPI, Wooting 0.1 mm actuation, EQ dB, Music/Clip seek) while preserving 100% native keyboard/SR range accessibility.
  - `100%` keyboard menu compliance (skip `aria-disabled`, typeahead jump `< 50 ms`, `.focus_group()` + `.focus_return()` across every native popover/modal).
  - `0` Rune `motion_gate` findings (`overshoot`, `jerk`, `flicker`, `never-settles`) across all controls in normal + `reduce_motion`.

## 2. Upgrade Every Core Control Primitive (`exo-kit` + `ui/src/components/ui/`)

- **True Trigger-Origin Popovers & Menus (`components.rs` + `Menu.tsx`):**
  - Replace hardcoded `Vec2::new(1.0, 0.0)` with `popover_transition_from(side, align)` computing the exact corner `Vec2` facing the trigger; add `.focus_group()` and `.focus_return(anchor_key)` to every native popover/modal.
  - Fix `Menu.tsx:19` to skip `[aria-disabled="true"]`; add 500 ms buffer 1-key typeahead to ALL menus (even `<= 10` items); add safe-triangle pointer corridor for nested submenus.
- **Perpendicular Precision-Scrub Sliders (`Slider.tsx` + `exo-kit` slider):**
  - Dragging horizontally scrubs `1×`; pulling the pointer vertically away from the slider smoothly shifts gear to `0.5×` (`>= 32 px`) and `0.1×` (`>= 72 px`) with a floating Liquid Glass tabular value pill (`800 DPI · ⅒×`) hovering above the thumb.
  - Add optional magnetic detent ticks (`detents?: number[]`, snapping gently within `±3 px` + triggering Spec 03 tactile click) and double-click-to-reset-to-default!
- **Real FLIP Width-Morph Confirm-in-Place (`ConfirmButton.tsx` + `exo-kit`):**
  - Fix `ConfirmButton.tsx:31-37` to interpolate true visual width (`scaleX(from / to)` with inverse-scaled label or WAAPI FLIP clip-box) on `--ease-spring` + subtle bottom hairline countdown for the 4 s disarm window.
  - Extract a shared `confirm_pill(...)` component in `exo-kit` (with `hold_landing_width()`) so destructive actions across `exo-ui` share identical physics.
  - Fix `key_modal.rs:90-101` disabled `.focusable(can_submit)` / `.hit()` guard.
- **Rubber-Band Stacked Toast Fan + Inline Undo (`notice.rs` + `NoticeStack.tsx`):**
  - Replace `notice.rs:94` hard clamp with exponential rubber-band `-24.0 * (1.0 - (-dx.abs() / 48.0).exp())`.
  - Add Sonner-style stacked fan collapse when `> 1` toast is visible (expands cleanly on hover/focus) and support an optional inline action/Undo button on native toasts (e.g., `"Game hidden [Undo]"`, `"Clip saved [Open]"`).

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (including `motion_gate` tests for dynamic-origin popover, slider scrub, confirm morph, and rubber-band toast fan), `cargo build --release --locked --bin exo-ui`.
- `ui/`: `npm run check`, `npm run dead`, `npm run build` (plus unit tests for `Menu` disabled-skip + typeahead, `Slider` perpendicular gain math, and `ConfirmButton` FLIP width).

## 4. Deliver

- Branch `lab/ux-physics-controls` from `origin/main`.
- `improve/ux-physics-controls-report.md`: `motion_gate` results, unit test matrix, before/after recordings/snaps of precision slider, confirm morph, and toast fan.
- Working implementation across `exo-kit`, `exo-ui`, and `ui/src/components/ui/` (no prose comments).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-physics-controls results" --body "<controls upgraded, motion_gate findings 0, ui/native gate results, commit sha>"`. Effort: medium.
