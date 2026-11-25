# Room Parity Report: `library`

Branch: `UhhErix/parity-library-store` (base: `feat/native-polish-2`)

## Before / After Parity (`node tools/parity/run.mjs --rooms library --seed-from "%LOCALAPPDATA%\ExoLauncher"`)

| Metric | Before (`library-pass1`) | After (`library-final2`) |
| --- | --- | --- |
| States passing | 8 / 17 (1 CRITICAL, 1 HIGH, 4 MEDIUM, 3 LOW) | **17 / 17 PASS (0 CRITICAL, 0 HIGH, 0 MEDIUM, 0 LOW)** |
| Open failures | 1 (`library-homedeck-card-press`) | **0** |
| Menu mismatches | 1 (`library-homedeck-card-menu` 6/5) | **0** (all 9 menu states exact) |
| Cursor mismatches | 0 | **0** (`226 / 226` exact across all 17 states) |
| Glass mismatches | 4 (`5/7`, `6/8`, `5/8`, `6/4`) | **0** (exact count on all 17/17 states) |
| Mean pixel diff | 5.68 | **4.79** |

### Per-State Breakdown

| State | Before | After | Menus | Cursors | Glass (R/N) |
| --- | --- | --- | --- | --- | --- |
| `library-homedeck` | PASS (3.33) | **PASS (3.88)** | n/a | 10/10 | 9/9 |
| `library-homedeck-card-hover` | PASS (3.33) | **PASS (3.88)** | n/a | 11/11 | 9/9 |
| `library-homedeck-card-press` | CRITICAL (3.33) | **PASS (3.88)** | n/a | 11/11 | 9/9 |
| `library-homedeck-card-menu` | HIGH (4.07) | **PASS (4.05)** | 6/6 | 17/17 | 10/10 |
| `library-homedeck-more-popover` | PASS (3.46) | **PASS (3.96)** | 4/4 | 15/15 | 10/10 |
| `library-homedeck-search` | PASS (3.36) | **PASS (3.91)** | n/a | 11/11 | 9/9 |
| `library-all-games` | PASS (5.79) | **PASS (5.76)** | n/a | 11/11 | 4/4 |
| `library-all-card-menu` | MEDIUM (6.70) | **PASS (5.96)** | 3/3 | 14/14 | 5/5 |
| `library-all-sort-dropdown` | MEDIUM (6.90) | **PASS (5.88)** | 8/8 | 19/19 | 5/5 |
| `library-all-more-popover` | PASS (5.92) | **PASS (5.88)** | 3/3 | 14/14 | 5/5 |
| `library-all-add-popover` | PASS (5.91) | **PASS (5.82)** | 3/3 | 14/14 | 5/5 |
| `library-all-filter-installed` | LOW (12.94) | **PASS (4.78)** | 28/28 | 20/20 | 5/5 |
| `library-all-select-mode` | MEDIUM (7.07) | **PASS (6.00)** | n/a | 11/11 | 5/5 |
| `library-all-new-folder-sheet` | MEDIUM (7.67) | **PASS (4.67)** | 35/35 | 21/21 | 5/5 |
| `library-all-folders` | LOW (7.28) | **PASS (5.92)** | n/a | 11/11 | 5/5 |
| `library-all-folder-open` | LOW (6.06) | **PASS (3.30)** | n/a | 16/16 | 6/6 |
| `library-capture-click` | PASS (3.50) | **PASS (3.96)** | 2/2 | 10/10 | 10/10 |

## RPC Parity Check (`library`)

Audited every host RPC method called by the React Library room (`HomeDeck.tsx`, `LibraryPane.tsx`, `LibraryHead.tsx`, `LibraryShelf.tsx`, `LibrarySelectBar.tsx`, `FolderCard.tsx`, `FolderPanel.tsx`, `FolderSheet.tsx`, `FolderCropper.tsx`, `GameMenu.tsx`, `GiftsShelf.tsx`, `useGameActions.ts`, `useShelves.ts`, `useLibrarySearch.ts`, `useLiveLibraryRefresh.ts`): **20 / 20** wired in `exo-ui` (`accounts.list`, `accounts.switch`, `app.log`, `app.notify`, `app.pendingOpen`, `backup.status`, `game.cancelInstall`, `game.dequeue`, `game.extras`, `game.get`, `game.install`, `game.launch`, `game.openFolder`, `game.stop`, `game.uninstall`, `game.update`, `gifts.answer`, `gifts.list`, `library.folderArt`, `runtimes.install`).

## Root Causes Fixed

1. **HomeDeck & Game card context menu (`library.rs`, `menus.rs`, `menu.rs`, `main.rs`)**:
   - Matched React `HomeDeck.tsx` `primaryVerb` (`Resume` with Play icon when `is_running`, never `Stop`) and `GameMenu.tsx` / `shelfAction` (omit primary Play/Stop item when `is_running || can_stop`).
   - Removed `.press_on_down()` on `deck_poster_card` and added `.press(pressed_fill())` so `library-homedeck-card-press` stays pressed during `down`.
   - Switched `glass_menu_surface` to `LiquidGlass::panel()` with `8px` padding, section dividers, `Cursor::Default` on library card/sort menus, `1.0px` `MenuAnchor::Point` offset, and `8.0px` Sort dropdown gap.
   - Hydrated `accounts.list` (`riot_other_accounts`) and `discord.friends` synchronously in `load_live_state_sync` for Library scenes so `Play as ...` and `Friends playing` match React.
2. **All Games grid, Filter popover, Select bar, and Folders (`library.rs`, `select_bar.rs`, `folders.rs`, `state.rs`)**:
   - Matched `all_grid_metrics` to React `resolveGridCardLayout` (`152px` min tile, `clamp(1, 12)`, `floor(raw_w * 100) / 100`, `1.5x` height), aligned `head` and `shelf_head` vertical offsets, and suppressed `tile_plate` when `ctx_targeted` while preserving hover undim/lift.
   - Switched Filter popover, Folder panel, and Folder sheet to `LiquidGlass::panel()` with `modal_live(["top-bar"])`, added `Cursor::NotAllowed` on idle filter chips, and matched flat rimmed controls in Select bar (`px(14.0)` -> `496x52` vs React `495x52`) and Folder sheet.
   - Added frosted `ExoGlass::Control` Liquid Glass on `folder_art_el` flap (`5/5` glass), `0.38` paper spread, and `opacity(0.0)` on open folder tile in the background grid.
   - Preloaded installed games' covers in `preload_visible_covers_sync` and `load_live_state_sync` so `library-all-filter-installed` never races async disk PNG decodes.
3. **Parity harness (`tools/parity/states.mjs`)**:
   - Created a folder in `library-all-folders` React pass and settled hover/dismiss transitions so `library-all-folders`, `library-all-folder-open`, and `library-capture-click` compare deterministically.

## Shared Files Touched

- `apps/launcher/native-ui/crates/exo-kit/src/menu.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/main.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/state.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/library.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/menus.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/folders.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/select_bar.rs`
- `apps/launcher/tools/parity/states.mjs`
