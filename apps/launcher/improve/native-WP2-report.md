# WP2 Report — Library: Folders, Select Mode, Gifts, Shelves, Drag and Drop

- **Branch:** `UhhErix/native-WP2` (based on `feat/native-polish-2` @ `5790322`)
- **Rune fork pin commit:** `f62929b` — all 8 Rune git deps in `apps/launcher/native-ui/Cargo.toml` (+ `Cargo.lock`) pinned to `exo-inputs` @ `be0700fa3c2e4d6cfa8a93ea4e1662b750210a29` in a single standalone commit.
- **Scope:** All 15 WP2 items in `apps/launcher/improve/feature-matrix.md` and `apps/launcher/improve/feature-matrix.json` — **15/15 DONE**, plus fixed the existing hidden-games filter bug in `state.rs`.

## Item-by-Item Status & Verification

| ID | Feature | Status | Native Refs | Host RPCs / Events | Verification |
| --- | --- | --- | --- | --- | --- |
| `library-01` | Hero / home deck (selected game, Play, `...` menu) | **DONE** | `library.rs:766`, `library_state.rs:363` | `library.get`, `game.launch`, `game.stop` | Unit test (`continue_order_ranks_running_installing_played_installed_rest`), motion gate test (`library_cover_flight_nav_pill_and_deck_motion_gate`), live signed-in `library-homedeck` + `library-homedeck-more-popover` snaps visually verified. |
| `library-03` | Multiple shelves (Continue playing, All games, Folders, Hidden games) | **DONE** | `library_state.rs:1136`, `state.rs` | `library.get`, `settings.set` | Unit tests (`continue_order_*`, `folder_crud_and_reorder_match_react_semantics`), fixed `state.rs` bug that excluded hidden games even when `library_show_hidden == true`, verified switching All games ↔ Hidden games in live host-pipe E2E run. |
| `library-07` | Filter popover (Show, Store, Genre, Mode, Friends) | **DONE** | `library.rs:363`, `library_state.rs:652` | `discord.friends` warm-up | Unit tests (`natural_language_search_and_understood_chips_match_react`), live signed-in `library-all-filter-installed` snap verified (caught and fixed 300px clipping by matching React's 420px flex-wrap popover), live E2E toggle + clear verified. |
| `library-08` | Select mode + `LibrarySelectBar` (multi-select, bulk hide/uninstall/folder) | **DONE** | `select_bar.rs:12`, `library_state.rs` | `settings.set`, `game.uninstall`, `library.removeCustom` | Runtime test (`wp2_folders_select_bar_gifts_and_empty_error_render_within_blur_cap`), live signed-in `library-all-select-mode` snap visually verified, live E2E Select all → Add to folder → New folder verified. |
| `library-09` | Folders (create, edit, cover cropper, reorder, move earlier/later, delete) | **DONE** | `folders.rs:110`, `folders.rs:589`, `library_state.rs:402` | `library.folderArt`, `settings.set` | Unit tests (`folder_crud_and_reorder_match_react_semantics`, `crop_fit_clamps_within_image_bounds`), runtime blur-cap test, standalone + live signed-in `library-all-folders`, `library-all-folder-open`, `library-all-new-folder-sheet` snaps verified (caught & fixed collapsed FolderPanel grid height). |
| `library-10` | Drag to reorder / drop into folder + OS file drop | **DONE** | `folders.rs:110`, `library.rs:472`, `library.rs:746` | `settings.set`, `library.folderArt`, `library.addCustom` | Implemented using Rune `exo-inputs` (`El::draggable`, `drag_preview`, `on_drop`, `accepts_drop`, `DropReorder`, `on_drop_files`, `accepts_extensions`); unit tests cover folder reorder, in-folder game reorder, and drop-into-folder state transforms. |
| `library-11` | Gifts shelf (claimable Steam/Epic gifts) | **DONE** | `gifts.rs:13`, `library_state.rs:232` | `gifts.list`, `gifts.answer`; push event `gifts.changed` via `AppState::register_event` | RPC contract test (`host_contract_contains_every_rust_rpc_method`), event parser test (`parses_all_newly_wired_host_events`), runtime blur-cap test, standalone `library-gifts` snap visually verified. |
| `library-12` | Add game (`+` button, file picker, portable) | **DONE** | `library_state.rs:1971`, `menus.rs`, `library.rs:746` | `shell.pickProgram`, `library.addCustom` | RPC contract verified (`shell.pickProgram` + `library.addCustom`), `+` menu (`New folder`, `Add a game`) + OS file drop (`.exe`/`.lnk`/`.bat`/`.cmd`) wired. |
| `library-13` | Refresh all (`library.refreshAll`, folder art refresh, 30-min rescan) | **DONE** | `library_state.rs:2051`, `library.rs:638` | `library.refreshAll` | RPC contract verified, `LibraryRefreshNotice` + cover shine + 30-min rescan timer wired, clicked `Refresh library` in live host-pipe E2E run. |
| `library-15` | Game card menu: `Play as <account>` | **DONE** | `menus.rs:697`, `library_state.rs` | `accounts.list`, `accounts.switch`, `game.launch` | RPC contract verified (`accounts.list`, `accounts.switch`, `game.launch`), Riot saved accounts loaded on bootstrap/menu and switched prior to launch. |
| `library-17` | Game card menu: New folder / add to folder | **DONE** | `menus.rs:730`, `library_state.rs` | `settings.set` | Replaced `"Folder management coming soon"` stub toast with `New folder` + per-folder toggle checkmarks matching `GameMenu.tsx`. |
| `library-18` | Folder card menu (Edit, Move earlier/later, Delete) | **DONE** | `menus.rs:765`, `folders.rs` | `settings.set` | Wired `ContextMenuTarget::FolderCard` with `Edit folder`, `Move earlier`, `Move later`, and `Delete folder` with in-place `"Delete? Games stay"` confirmation. |
| `library-19` | Running game state + Stop; hold windows (`wait for game`) | **DONE** | `library_state.rs:276`, `library_state.rs:2111` | `game.stop`; push events `game.optimizeHeld`, `game.optimizedAgain` via `AppState::register_event` | Unit test (`press_outcome_matches_game_hold_contract`), event parser test (`parses_all_newly_wired_host_events`), per-game `Starting`/`Closing` holds with timed release. |
| `library-20` | Keyboard grid nav / type-ahead | **DONE** | `library_state.rs:962`, `library_state.rs:2313`, `main.rs` | — | Unit test (`grid_navigation_and_typeahead_work`); handles `Ctrl+F`, `Ctrl+A`, `Escape`, HomeDeck `Left`/`Right`, Grid `Arrow`/`Home`/`End`/`PageUp`/`PageDown`/`Enter`, and letter/digit type-ahead. |
| `library-21` | Library empty / loading / error states | **DONE** | `library.rs:693`, `library_state.rs` | `library.get` | Runtime blur-cap test (`wp2_folders_select_bar_gifts_and_empty_error_render_within_blur_cap`), standalone `library-empty` and `library-error` snaps visually verified. |

## Shared Hotspot & Architecture Hygiene

- **Push events:** Registered `gifts.changed`, `game.optimizeHeld`, and `game.optimizedAgain` through `AppState::register_event` inside `library_state.rs::register_library_wp2_events` — zero new `state.rs` match arms.
- **RPC contract:** Added typed methods & response models in `exo-rpc` (`accounts.list`, `accounts.switch`, `gifts.list`, `gifts.answer`, `library.folderArt`, `library.refreshAll`, `shell.pickFolder`, `shell.pickProgram`), all validated against `host-v1.json` by `host_contract_contains_every_rust_rpc_method`.
- **Blur cap:** Every WP2 overlay and card stays well under `EXO_MAX_BACKDROP_BLURS = 16` (measured 4–10 across all 17 live signed-in states; 0 late glass).

## Parity Harness Before / After

- **Added parity states** in `apps/launcher/tools/parity/states.mjs`: `library-all-select-mode`, `library-all-new-folder-sheet`, `library-all-folders`, `library-all-folder-open` (plus standalone scenes `library-folders`, `library-folder-open`, `library-folder-sheet`, `library-select`, `library-gifts`, `library-empty`, `library-error` in `main.rs`).
- **Before WP2:** Library matrix was **DONE 6, PARTIAL 7, MISSING 6, BROKEN 2** (of 21). Folders showed a `"Folder management coming soon"` stub toast; select mode, gifts, drag-drop, folder menus, Riot `Play as`, hold windows, error state, and keyboard grid nav were absent; `Hidden games` view was always empty due to `state.rs` pre-filtering hidden games.
- **After WP2:** Library matrix is **DONE 21, PARTIAL 0, MISSING 0, BROKEN 0** (of 21).
- **Live signed-in verification:** Ran `tools/parity/run.mjs --rooms library --skip-react` against an isolated temp copy of repo-built `ExoLauncher.exe` (`--host-pipe`) and a temp copy of `%LOCALAPPDATA%\ExoLauncher` (excluding `app\`), capturing all 17 live Library states at `1920x1080` scale 1 (Open failures: **0**, Glass late: **0**, glass count 4–10, real covers loaded). Visual inspection of live snaps caught two defects which were fixed and re-captured:
  1. `FolderPanel` grid scroll container collapsed inside an auto-height panel → fixed with explicit computed `grid_h` in `folders.rs`.
  2. `library_filter_overlay` clipped `Played lately` and `Friends playing` at `300px` → matched React's `420px` `.exo-library-filter-pop` with `row().flex_wrap().gap(6.0)`.
- **Live interactive E2E check:** Executed a scripted `exo-ui.exe --pipe <temp-pipe>` session against the temp host exercising HomeDeck More → `Refresh library` → `Select games` → `Select all` → `Add to folder` → `New folder` → `Create` → Filter (`Installed` → `Clear all`) → Sort (`Hidden games` → `All games`), exiting cleanly with code `0`.
- **Unverified / Note:** The React WebView2 CDP pass in `tools/parity/run.mjs` timed out (`app.exo-launcher.local page not reachable on 127.0.0.1:<port>`) without disturbing the owner's running Exo instance (never touched per instructions), so automated pixel-diff scoring against live WebView2 could not run in this environment; all React parity checks were verified directly against `apps/launcher/ui/src` source + CSS tokens and visual inspection of native 1920×1080 captures.

## Verification Gates

All gates ran fresh after the final edit:
- `cargo fmt --check`: **PASS (0)**
- `cargo clippy --workspace -- -D warnings`: **PASS (0)**
- `cargo test --workspace`: **PASS (0)** — 4 `exo-kit` + 22 `exo-rpc` + 86 `exo-ui` = **112 passed, 0 failed**
- `cargo build --release -p exo-ui`: **PASS (0)**
- `node apps/launcher/tools/check-comments.mjs`: **PASS (0)**
- `node --test apps/launcher/tools/parity/parity.test.mjs`: **PASS (0)** — 8/8 passed
