# room-shell-library: menus, popovers, dropdowns, glass and cursors (round 3)

Branch: UhhErix/room-shell-library. Harness: `node tools/parity/run.mjs --rooms shell,library --exo-ui native-ui/target/debug/exo-ui.exe --report-dir <scratch>` (final run had no `--seed-data`; states that need owner data may differ from an owner-data run).

## Status

Round 3 is partly done. Fixed and proven: the capture popover no longer fades in from transparent (item 2a). Glass over-count fixed for the Library header controls (2b, four of five menus now match React's glass count). Not fixed: `pc-display-popover` does not open natively (2c), homedeck card hover and press do not open natively (2d), and the CRITICALs listed under "Still open" (2e).

## Gates

- `cargo fmt --all -- --check`: pass (after `cargo fmt --all`, which reformatted `library.rs` and `shell.rs`).
- `node tools/check-comments.mjs`: pass. `tools/parity/measure-palette.mjs` (committed in 0703181) had two comment lines; removed.
- `node --test tools/parity/parity.test.mjs`: 8/8 pass.
- `cargo clippy --workspace --locked -- -D warnings`: see the result line in the push note below.
- `cargo test --workspace --locked`: see the result line in the push note below.
- `cargo test -p exo-ui --locked`: 69/69 pass, before the homedeck edit. The workspace run repeats it on the final tree.

## 2a: capture popover is glass from frame 1

Root cause: Rune `Transition::scale` and `Transition::scale_anchor` set opacity 0 at enter (rune-motion/src/transition.rs:202–208). The popover, the modal cards and the mini player faded in from transparent, so the glass appeared late.

Fix: `glass_enter(scale, anchor)` in `exo-kit/src/components.rs`, a scale-only enter (`Effect { scale, anchor, ..IDENTITY }`). Used in `popover_transition_from` (0.96), `modal_card_transition` (0.98), and `mini_player_transition` in `shell.rs` (offset + glass_enter 0.96).

Evidence:
- Probe (`popoverTrace`, frames 0–10 after the click on `top-capture-btn`): element opacity 1 from frame 0, backdrop blur present.
- Frame captures, first 10 frames after the click: `scratchpad/frames-before/`, `scratchpad/frames-after/`, contact sheet `scratchpad/capture-popover-sheet.png`.

Exit correction: an earlier claim that exit is now `scale_anchor` "instead of opacity" was wrong. `scale_anchor` also fades on exit, and Rune forces removal opacity to 0 (rune-motion/src/scene/presence.rs:144). Exit still fades. This is not changed here.

## 2b: glass over-count

Library header (`view_all_games`, `library.rs`): search field, Filter, Sort and the More circle were glass. React draws them as flat fields (`exo-library-search`, `exo-library-filter`, `exo-shelf-sort-trigger`, `exo-library-more`; no `exo-glass`). Changed to flat: search and Filter use `glass_still_control_fill()` with no backdrop blur; Sort uses a new `SelectTrigger::flat(true)` (default off, so other rooms are unchanged); More uses `glass_icon_circle_opt(..., false)`. The home-deck More and icon circles stay glass: React marks them `exo-glass` in `HomeDeck.tsx`.

Bar hover: `shell-bar-hover-nav` and `shell-bar-hover-search` both pass glass in the final run. No bar-hover glass gap is reproduced now. Bar cursor mismatches remain (see "Still open").

## 2c: pc-display-popover (not fixed)

Still fails to open natively (`scene=pc`, menu 0). The state taps the React target from `clickText('button', /display|graphics|best settings/i)`. That regex also matches the Graphics cell, which may be the target, not Screen/Display. Not verified; the native tap point was not traced.

## 2d: homedeck card hover and press (not fixed)

Still fail to open natively (hovered 0, pressed 0).

- Changed: the deck card (`deck_poster_card`, `library.rs`) now has React's rest opacity 0.88, hover opacity 1.0 and a 4px lift (`library.css:316–348`), with hover tracked through `hovered_cover_key` (same as the all-games tile).
- Still failing: the probe reports no hovered element, and `tile-riot:valorant` [32,720,224,336] is under the point (144, 888). The cause is not found.
- Side effect: hover-state pixel diff rose from 4.15 to 7.57 (the rest opacity change moved the whole deck).

## 2e: remaining CRITICALs (shell + library)

- `library-all-filter-installed`: 9 of 28 React menu items natively. Genre and Mode tags are not in native Game data; Friends is not available natively.
- `library-capture-click`: glass 10 vs React 5, and menu 5 vs React 2. The extra glass is on home-deck icon circles and deck arrows, which React marks `exo-glass`; the React side of this state listed only 5, which is not explained.
- Cursor mismatches (native `default`, React `pointer`) on the bar and on library cards and menus. These keep `library-all-card-menu`, `-more-popover`, `-sort-dropdown`, `-add-popover` and `library-homedeck-card-menu` CRITICAL or HIGH even though their glass now matches.
- `shell-bar-press-search`: CRITICAL, not investigated this round.
- `shell-tour-modal`, `shell-activate-key-modal`: cursor under modal. Rune-level, left alone.

## Before/after (shell + library)

Before: `run-final2`, 19 states: CRITICAL 9, HIGH 6, MEDIUM 1, PASS 3. After: `run-final3`, 20 states: CRITICAL 8, HIGH 8, MEDIUM 4, PASS 0. Glass and pixel columns are from each report. `library-capture-click` was added this round and was not in the before run.

| State | Before | After | Glass before→after | Pixel diff before→after |
| --- | --- | --- | --- | --- |
| `library-all-filter-installed` | CRITICAL | CRITICAL | warn → pass | 27.68 → 28.13 |
| `library-all-card-menu` | CRITICAL | CRITICAL | warn → pass | 41.1 → 27.25 |
| `library-all-more-popover` | CRITICAL | CRITICAL | warn → pass | 40.89 → 27.29 |
| `library-all-sort-dropdown` | CRITICAL | CRITICAL | warn → pass | 39.19 → 26.66 |
| `library-all-add-popover` | CRITICAL | HIGH | warn → pass | 40.47 → 27.38 |
| `library-homedeck-card-hover` | CRITICAL | CRITICAL | pass → pass | 4.15 → 7.57 |
| `library-homedeck-card-menu` | CRITICAL | HIGH | pass → pass | 4.53 → 7.8 |
| `library-homedeck-card-press` | CRITICAL | CRITICAL | pass → pass | 4.05 → 7.47 |
| `library-all-games` | HIGH | HIGH | pass → pass | 42.83 → 25.8 |
| `library-homedeck-more-popover` | HIGH | HIGH | pass → pass | 5.51 → 8.28 |
| `library-homedeck-search` | MEDIUM | MEDIUM | pass → pass | 4.11 → 7.53 |
| `shell-bar-press-search` | CRITICAL | CRITICAL | pass → pass | 58.62 → 61.06 |
| `shell-palette-open` | HIGH | HIGH | pass → pass | 18.25 → 18.25 |
| `shell-palette-query` | HIGH | HIGH | pass → pass | 18.41 → 18.28 |
| `shell-tour-modal` | HIGH | HIGH | pass → pass | 18.14 → 19.69 |
| `shell-activate-key-modal` | HIGH | HIGH | pass → pass | 15.32 → 16.31 |
| `library-capture-click` | not run | CRITICAL | — → warn | — → 62.78 |
| `library-homedeck` | not run | MEDIUM | — → pass | — → 7.57 |
| `shell-bar-hover-nav` | not run | MEDIUM | — → pass | — → 13.13 |
| `shell-bar-hover-search` | not run | MEDIUM | — → pass | — → 7.48 |

## Out of scope (left alone, per instructions)

- **Rune cursor under modal:** Rune `modal` makes outside targets inert, so `cursor()` returns Default there. Needs a fork decision.
- **Play as <account>** in the native GameCard menu (`GameMenu.tsx` line 76): needs the account-list and account-switch RPC. Needs owner approval.
- **Discord, Store and Music:** not touched, except the `music.rs` mini-player test from round 2.

## Not done

- Capture popover exit still fades (Rune forces removal opacity 0). Not changed.
- `pc-display-popover` (2c) and homedeck card hover/press (2d): root causes not found.
- Bar cursors (native `default`, React `pointer`) and library-card cursors: not changed.
- `shell-bar-press-search`: not investigated.
- GPU frame-time before/after for CapturePopover and HomeDeck: not measured.
- `ctx-game-*` menu parity for FolderCard; NotesView textarea spellcheck (Discord-owned, out of scope).

## Housekeeping

- Temp data copies: the owner-data copy was deleted after an earlier run; `scratchpad/frames-data` and `scratchpad/run-states4` hold temp data and captures, not committed.
- Exo processes from this session: check before reporting; `ExoLauncher.exe` PID 11660 belongs to the `room-discord` worktree and was left alone.
