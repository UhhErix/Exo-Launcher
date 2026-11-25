# Room Parity Report: `shell`

Branch: `UhhErix/parity-shell` (base: `feat/native-polish-2`)

## Before / After Parity (`node tools/parity/run.mjs --rooms shell`)

| Metric | Before | After |
| --- | --- | --- |
| States passing | 3 / 9 (3 HIGH, 2 MEDIUM, 1 LOW) | **9 / 9 PASS (0 CRITICAL, 0 HIGH, 0 MEDIUM, 0 LOW)** |
| Open failures | 0 | **0** |
| Menu mismatches | 2 (`52/19`, `54/19`) | **0** (`19/19`, `19/19`) |
| Cursor mismatches | 1 (`downloads` 2/12) | **0** (`134/134` exact across all 9 states) |
| Glass warnings | 6 | **0** (`10/10`, `10/10`, `10/10`, `9/10`, `11/11`, `10/10`, `9/9`, `9/9`, `9/9`) |
| Mean pixel diff | 7.27 | **2.63** |

### Per-State Breakdown

| State | Before | After | Menus | Cursors | Glass (R/N) |
| --- | --- | --- | --- | --- | --- |
| `shell-palette-open` | HIGH (8.42) | **PASS (1.64)** | 19/19 | 20/20 | 10/10 |
| `shell-palette-query` | HIGH (7.62) | **PASS (1.91)** | 19/19 | 20/20 | 10/10 |
| `shell-activate-key-modal` | MEDIUM (8.26) | **PASS (1.26)** | n/a | 17/17 | 10/10 |
| `shell-downloads-sheet` | HIGH (7.24) | **PASS (1.27)** | n/a | 12/12 | 9/10 |
| `shell-whats-new-sheet` | MEDIUM (10.87) | **PASS (5.55)** | n/a | 13/13 | 11/11 |
| `shell-tour-modal` | LOW (6.29) | **PASS (1.88)** | n/a | 20/20 | 10/10 |
| `shell-bar-hover-nav` | PASS (5.57) | **PASS (3.39)** | n/a | 10/10 | 9/9 |
| `shell-bar-hover-search` | PASS (5.57) | **PASS (3.39)** | n/a | 11/11 | 9/9 |
| `shell-bar-press-search` | PASS (5.61) | **PASS (3.42)** | n/a | 11/11 | 9/9 |

## Root Causes Fixed

1. **Command palette (`palette.rs`, `state.rs`, `main.rs`)**:
   - Aligned vertical offset to React `top-[12%]` (`130.0px`, was `68.0px`) and switched panel material to `LiquidGlass::panel()`.
   - Stacked `detail` vertically under `title` inside `min_h(44.0)` rows and matched section header spacing to React `command-menu-1.tsx`.
   - Filtered palette game candidates through `shelf_shelves()` (`library + hidden`) to match React `launcherCommands.ts`.
   - Filtered off-viewport options in `--parity-probe` menu collection to match React `isVisible(el)`.
2. **Downloads sheet (`downloads.rs`)**:
   - Added `.modal_live(["top-bar"])` to fix all 10 top-bar cursor mismatches (2/12 -> 12/12).
   - Switched to `LiquidGlass::panel()`, `scrim_fill()` (`BG.alpha(0.72)`) spanning the titlebar, and flat rimmed `set_btn` for `Pause all`.
3. **Activate key modal (`key_modal.rs`)**:
   - Switched card to `LiquidGlass::panel()` + `scrim_fill()` (`BG.alpha(0.72)`).
   - Replaced nested glass `input_box`, `Copy key`, and `Cancel` with flat rimmed controls (`glass_still_control_fill` / `set_btn`).
   - Styled disabled `Open <Store>` pill with subtle translucent fill + faint text matching `.exo-set-btn.is-primary:disabled`.
4. **What's New sheet (`whats_new.rs`, `main.rs`)**:
   - Added `whats-new` to `load_live_state_sync` Settings RPC loader and parsed `app.checkUpdate` via `AppState::apply_update_check_json` so upcoming release (`Coming in Exo ...`) and `Install update` populate synchronously.
   - Switched to `LiquidGlass::panel()`, `scrim_fill()`, flat rimmed `Continue`, `28px` section gap, `18px` item gap, `20px` icons, `1.35x` point line-height, and removed unconditional top fade at scroll 0.
5. **Tour modal (`setup.rs`)**:
   - Switched `exo-tour-card` to `LiquidGlass::panel()`.
6. **HomeDeck background behind shell modals (`library.rs`, `state.rs`, `main.rs`)**:
   - Removed duplicate `inactive_scrim` on top of `.opacity(0.88)` in `deck_poster_card` and added active card `outline(2.0, 3.0, FG)`.
   - Sized HomeDeck hero logo from `logo.size()` aspect ratio (max 460x112, left-bottom aligned) and removed extra `min_h(40.0)` on right-side 34px circles.
   - Bumped `cover_image` decode cap from `320.0` to `384.0` to eliminate downscale/upscale blur on 336px cards.
   - Pre-fetched remote HomeDeck covers in `load_live_state_sync` for all HomeDeck-backed modal scenes.
7. **Parity harness accuracy (`tools/parity/react-driver.mjs`, `tools/parity/native-driver.mjs`)**:
   - Fixed `isUnoccluded` to walk `document.elementsFromPoint` across translucent modal scrims (`alpha < 0.95`), resolve `pointer-events: none` `.exo-sheet-glass` layers via parent, and temporarily lift `[inert]` during the synchronous glass scan while keeping opaque-room occlusion intact.
   - Added `--frames 18 --fps 60` on non-scripted `--snap` runs in `native-driver.mjs` so async local disk cover PNG decodes settle before capture.

## Shared Files Touched

- `apps/launcher/native-ui/crates/exo-ui/src/main.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/state.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/library.rs`
- `apps/launcher/tools/parity/react-driver.mjs`
- `apps/launcher/tools/parity/native-driver.mjs`
