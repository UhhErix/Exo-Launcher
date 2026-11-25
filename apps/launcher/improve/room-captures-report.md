# Room Parity Report: `captures`

Branch: `UhhErix/parity-captures` (base: `feat/native-polish-2`)

## Before / After Parity (`node tools/parity/run.mjs --rooms captures`)

| Metric | Before | After |
| --- | --- | --- |
| States passing | 0 / 6 (6 HIGH) | **5 / 6 PASS + 1 SKIP (0 CRITICAL, 0 HIGH, 0 MEDIUM, 0 LOW)** |
| Open failures | 0 | **0** |
| Menu mismatches | 3 | **0** (`2/2` on popover) |
| Cursor mismatches | 3 | **0** (`88/88` exact across all 5 reachable states) |
| Glass warnings | 2 | **0** (`10/10`, `10/11`, `10/11`, `9/10`, `9/10`) |
| Mean pixel diff | 21.36 | **4.12** |

### Per-State Breakdown

| State | Before | After | Menus | Cursors | Glass (R/N) |
| --- | --- | --- | --- | --- | --- |
| `shell-capture-popover` | HIGH (14.55) | **PASS (5.61)** | 2/2 | 10/10 | 10/10 |
| `shell-capture-sheet` | HIGH (14.83) | **PASS (3.77)** | n/a | 18/18 | 10/11 |
| `shell-capture-settings` | HIGH (27.52, skip) | **SKIP** (OBS add-on not installed; Settings hidden in React) | n/a | n/a | n/a |
| `shell-capture-notes` | HIGH (14.83) | **PASS (4.00)** | n/a | 20/20 | 10/11 |
| `shell-capture-clip-edit` | HIGH (29.20) | **PASS (4.42)** | n/a | 19/19 | 9/10 |
| `shell-capture-snip-edit` | HIGH (27.25) | **PASS (2.80)** | n/a | 21/21 | 9/10 |

## Root Causes Fixed

1. **`LocalFileHost` `{Token}` segment bug in `exo-rpc` (`exo-rpc/src/types.rs`)**:
   - `LocalFileHost.Url` embeds a leading `{Token}` path segment (`https://captures.exo-launcher.local/{Token}/Desktop/...png`), causing `ArtBase::resolve_capture_file` to fail on every real capture file in the native app (gallery thumbnails, Viewer, and SnipEditor).
   - Fixed `resolve_capture_file` to fall back to stripping the leading token segment when the direct path does not exist, and added unit test `resolve_capture_file_strips_local_file_host_token_segment`.
2. **Capture popover (`captures.rs`)**:
   - Switched to `LiquidGlass::panel()`, aligned vertical offset to `8.0px` (`[1512, 48, 240, 94]`), and added `.modal_live(["top-bar"])` fixing all 10 top-bar cursors (0/10 -> 10/10).
3. **Captures sheet backdrop, gallery, and Viewer (`captures.rs`, `state.rs`)**:
   - Fixed backdrop top padding (`.pt(76.0).pr(24.0).pb(24.0).pl(24.0)` — `.padding(24.0)` had overwritten `.pt(52.0)` and shifted the entire modal 26px too high), switched to `scrim_fill()` (`BG.alpha(0.72)`) and `LiquidGlass::panel()` + `1px` hairline border.
   - Added `AppState::capture_full_image` (`1280px` decode cap) for razor-sharp Viewer imagery, matched `5`-column gallery grid (`chunks(5)`), hid unhovered card delete/fav overlay unless favourited, matched Viewer `Edit`/`Share` icons + 32px icon-only `Favorite`/`Info`/`Delete`/`More` controls + `Cursor::Default` on image stage + `12px` stage padding, and used `LiquidGlass::control()` on `cap-close`.
4. **Notes workspace (`notes.rs`, `main.rs`)**:
   - Added `AppState::hydrate_notes_list_reply` and synchronous `METHOD_NOTES_READ` in `load_live_state_sync` so saved session tabs and active note content open immediately instead of showing `No note open`.
   - Matched React `NotesView` layout: 280px sidebar (`pt(2) pr(12) pb(16) pl(20)`), header (`pb(10)`, `20px 650w` title, `set_btn` + icons, `LiquidGlass::control()` close button), pill tabs, icon-only toolbar (moving `Check spelling` into the `⋯` menu), bordered rounded editor card, and borderless 36px status bar.
5. **Snip editor (`snip_editor.rs`)**:
   - Matched disabled/enabled cursors (`Cursor::NotAllowed` on disabled `Revert`/`Undo`/`Redo`/`Save`), aligned stage to React's exact `940x529` canvas (`size(940, 810)`, `px(12) pb(14)`, `304px` inspector), added icons to `Crop`/`Mark up`/`Blur`/`Copy text` tabs, and rendered Mark up tools in a 2x2 tile grid.
6. **Parity harness (`tools/parity/states.mjs`, `compare.mjs`, `react-driver.mjs`, `native-driver.mjs`)**:
   - Updated `shell-capture-*` React navigation steps to reach Captures gallery, Viewer, and Snip editor cleanly via `Notes -> Captures` when the OBS Capture add-on is not installed.
   - Avoided scoring leftover React popover menus/pixels when `opened.status === 'skip'` in `compare.mjs`.
   - Included translucent-scrim / `exo-sheet-glass` / `inert`-aware glass unocclusion in `react-driver.mjs` and 18-frame settle in `native-driver.mjs` (matching PR #208).

## Shared Files Touched

- `apps/launcher/native-ui/crates/exo-rpc/src/types.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/main.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/state.rs`
- `apps/launcher/tools/parity/states.mjs`
- `apps/launcher/tools/parity/compare.mjs`
- `apps/launcher/tools/parity/react-driver.mjs`
- `apps/launcher/tools/parity/native-driver.mjs`
