# room-shell-library: menus, popovers, dropdowns, glass and cursors (round 4)

Branch: UhhErix/room-shell-library. Harness: `node tools/parity/run.mjs --rooms shell,library --exo-ui native-ui/target/debug/exo-ui.exe --report-dir <scratch>` (no `--seed-data`).

## Round 5

Base: merged `origin/feat/native-polish-2` (21ac9c4) into the branch first (clean). Rune pins: all 8 moved from `fc71db1a` to `7a34c492` in `Cargo.toml` and `Cargo.lock`. Note: the merge and the pin bump are also in this round's numbers, so the before/after below is not only this round's changes.

1. **Rune pins**: done. `cargo build` and `cargo test` run against 7a34c492.
2. **modal_live on the top Bar**: keys added in `exo-kit/src/menu.rs` (`TOP_BAR_LIVE_KEYS`: the seven `nav-*` tabs, `nav-settings`, `search-capsule`, `win-min`, `win-max`, `win-close`) and applied after `.modal_tx` in `context_menu_layers` and `select_menu_layers`. The key `top-bar` is not listed: it contains the modal scrim, which would stay live too.
   - Not working for the cursor. Under a full-viewport menu the probe hits the menu's own root (`exo-ctx-root`, `hitKey` in `native/*.json`) at every Bar point, and that root has no cursor, so the Bar reads `default` where React reads `pointer`. Rune's `top_target` (`runtime/input/hit.rs:298`) returns the first hit node in z-order, and a modal root is one, so `modal_live` never gets to the Bar node beneath it. Rune's own test (`modal_live_tests.rs`) uses a menu that does not cover the Bar. Fixing this needs Rune to prefer a live node under a modal catcher; that is the fork task `rune-modal-live`, outside this repo.
   - Side effect to check: the nav and search nodes are now counted as menu items in the probe (`extra [Library, Store, …]` on `shell-bar-press-search`).
3. **Homedeck card press**: fixed. `deck_poster_card` gets `.press(pressed_fill())` on the tile, with click activation unchanged (no `press_on_down`, which opened the game page on down). `library-homedeck-card-press` is OK (`pressedKeys` non-empty), and its severity fell to LOW.
4. **Harness rerun** `run-final5` (`--rooms shell,library`): CRITICAL 6, HIGH 6, MEDIUM 7, LOW 1, PASS 0 (round 4: CRITICAL 8, HIGH 7, MEDIUM 5, PASS 0).

| State | Round 4 (sev, px) | Round 5 (sev, px) | Menu | Cursors | Note |
| --- | --- | --- | --- | --- | --- |
| `library-homedeck-card-press` | CRITICAL, 7.47 | LOW, 14.64 | — | 11/11 | fixed |
| `library-homedeck-card-hover` | MEDIUM, 7.57 | MEDIUM, 14.74 | — | 11/11 | |
| `library-homedeck-card-menu` | HIGH, 7.81 | HIGH, 7.87 | 5/5 | 0/16 | cursor under catcher (Rune) |
| `library-homedeck-more-popover` | HIGH, 8.29 | HIGH, 7.78 | 4/4 | 4/15 | cursor under catcher (Rune) |
| `library-all-card-menu` | CRITICAL, 30.51 | CRITICAL, 45.91 | 5/5 | 0/16 | cursor under catcher (Rune) |
| `library-all-sort-dropdown` | CRITICAL, 29.82 | CRITICAL, 43.77 | 8/8 | 11/19 | cursor under catcher (Rune) |
| `library-all-more-popover` | CRITICAL, 30.55 | CRITICAL, 46.58 | 3/3 | 0/14 | cursor under catcher (Rune) |
| `library-all-add-popover` | CRITICAL, 30.64 | CRITICAL, 46.66 | 3/3 | 0/14 | cursor under catcher (Rune) |
| `library-all-filter-installed` | CRITICAL, 27.96 | CRITICAL, 29.04 | 20/28 | 5/20 | React list now 28 (was 21); see below |
| `library-capture-click` | CRITICAL, 63.98 | CRITICAL, 51.67 | 2/2 | 0/10 | glass 5/10 (warn) |
| `shell-palette-open` | HIGH, 19.11 | HIGH, 7.98 | 65/19 | 20/20 | menu now lists game rows |
| `shell-palette-query` | HIGH, 19.14 | HIGH, 8.15 | 37/19 | 20/20 | |
| `shell-tour-modal` | HIGH, 18.19 | MEDIUM, 10.3 | — | 0/20 | Bar reads pointer under a dialog; React default |
| `shell-activate-key-modal` | HIGH, 15.1 | MEDIUM, 9.55 | — | 12/17 | |
| `shell-bar-press-search` | CRITICAL, 61.06 | HIGH, 25.46 | 37/0 | 11/11 | |
| `shell-bar-hover-nav` | MEDIUM, 7.47 | MEDIUM, 14.64 | — | 10/10 | |
| `library-homedeck` | MEDIUM, 7.57 | MEDIUM, 14.74 | — | 10/10 | |
| `library-homedeck-search` | MEDIUM, 7.53 | MEDIUM, 7.64 | — | 10/11 | |
| `library-all-games` | HIGH, 25.8 | HIGH, 44.51 | — | 10/10 | |

Round 4 pixel figures are from its report; round 5 from `run-final5/report.md`. The jump in pixel diffs on the Library states and the palette menu count (33 → 65) come with the merge and pin bump, not this round's code; not separated yet.

Open after round 5:
- Cursor under open full-viewport menus and popovers (Bar reads `default`). Rune `top_target`; decision for the Rune owner.
- The Library filter popover (`exo-ui/src/library.rs`, `library-filter-root`) is a modal without `modal_live`. Not in the shared helper, so not changed.
- `library-all-filter-installed`: React lists 28 menu items now (21 before); the native count (20) did not change. Looks like a React-side data change, not checked.
- The first harness run crashed on cleanup (`ENOTEMPTY` on the harness's temp instance `exo-parity-20261123-071516` under the user's Temp folder; the directory would not clear, "Device or resource busy"). The rerun passed. The leftover temp directory is still on disk; no Exo process was found using it.

Gates (round 5): `cargo fmt --all -- --check` pass; `cargo clippy --workspace --locked -- -D warnings` pass (0 warnings); `cargo test --workspace --locked` pass (exo-kit 4, exo-rpc 17 with 1 ignored, exo-ui 69); `node --test tools/parity/parity.test.mjs` 8/8; `node tools/check-comments.mjs` pass.

## Status

Round 4 items:

1. **pc-display-popover**: fixed. Root cause was the harness, not the app: the React step matched the Graphics cell (`clickText('button', /display|graphics|best settings/i)`), and native tapped the wrong cell. The state now uses `/^Screen/` on React and key tap `pc-cell-display` on native. Targeted run: OK (menu opens). This state is in the `pc` room, so it is not in the shell+library table below.
2. **Homedeck card hover**: fixed. The probe sees no hover unless Rune has a hover style; `deck_poster_card` now has `.hover(hover_fill())`. Hover run: OK, `hoveredKeys` non-empty.
3. **Homedeck card press**: not fixed. `pressedKeys` stays empty. Tried: timing (t 1.0 vs 0.1, no change); `.press(pressed_fill())` alone (still fails); adding `.press_on_down()` (probe then fails with glass 10, which suggests the down opens the game page). Reverted to hover-only. Still needs a look at how Rune tracks press amount on `press_scale` tiles.
4. **Capture menu 5 vs 2**: fixed. Native defaulted `capture_status.present` to true when the host's status was not loaded for library and shell scenes. `load_live_state_sync` (`exo-ui/src/main.rs`) now fetches capture status for every scene. Run: `library-capture-click` menu 2/2. The state is still CRITICAL on glass (5/10) and pixel diff (63.98).
5. **Library Filter Genre, Mode and Friends**: added (see 4 below).
6. **Cursor mismatches not under a modal**: none confirmed. See 5 below.

## Gates

- `cargo fmt --all -- --check`: pass.
- `node tools/check-comments.mjs`: pass.
- `node --test tools/parity/parity.test.mjs`: 8/8 pass.
- `cargo clippy --workspace --locked -- -D warnings`: pass (exit 0, no warnings).
- `cargo test --workspace --locked`: pass (exo-kit 4, exo-rpc 17 with 1 ignored, exo-ui 69).

## 4: Library Filter, Genre, Mode and Friends

Source: `ui/src/components/shell/useLibrarySearch.ts` (`filterOptions`), `ui/src/lib/librarySearch.ts` (`LIBRARY_TAGS`, tag and friends tests), `ui/src/lib/friendsPlaying.ts` (`gamesFriendsPlay`).

Native (`exo-ui/src/state.rs`, `exo-ui/src/library.rs`):
- `LibraryChip::Tag(String)` for each `LIBRARY_TAGS` word present in `Game.tags` (`exo-rpc/src/types.rs`). Genre before `single-player`, Mode from there. Each tag is its own group (all selected tags must match), as in React.
- `LibraryChip::Friends` in the Show section, always listed, disabled (greyed, not clickable) when no friend is playing a library game.
- `friends_playing(games, friends)` maps Discord friends from `AppState.discord.friends` (already loaded by the Discord client) to library games: online, `kind == "friend"`, activity type 0 or 5, matched by title identity to a searchable game or one of its includes.
- Overlay sections: Show, Store, Genre, Mode.

Approximation: React folds accents (NFD) before matching titles; native lower-cases and keeps letters and digits, so an accented title can fail to match its unaccented spelling. Only the Friends match is affected.

Run: `library-all-filter-installed` menu 20/21 (report: 9/21 before). The one missing row is the "Clear all" button, which the probe may not count; not verified.

## 5: cursor mismatches

Every remaining cursor mismatch is under an open menu, popover or modal, where Rune makes the outside inert and `cursor()` returns Default. Those are out of scope for this round (Rune fork task `rune-modal-live`).

The one non-modal state, `library-homedeck-search`, has React's selector missing (`react: MISS`), so the React cursor reading comes from the bar at home, not the search. No fix made.

## Before/after (shell + library)

Before: `run-final3` (round 3): CRITICAL 8, HIGH 8, MEDIUM 4, PASS 0. After: `run-final4`: CRITICAL 8, HIGH 7, MEDIUM 5, PASS 0. Pixel diffs are from each report.

| State | Before (sev, px) | After (sev, px) | Menu before→after | Note |
| --- | --- | --- | --- | --- |
| `library-homedeck-card-hover` | CRITICAL, 7.57 | MEDIUM, 7.57 | — | hover tracked now |
| `library-homedeck-card-press` | CRITICAL, 7.47 | CRITICAL, 7.47 | — | not fixed |
| `library-capture-click` | CRITICAL, 62.78 | CRITICAL, 63.98 | 5/2 → 2/2 | menu fixed; glass 5/10 and pixel still fail |
| `library-all-filter-installed` | CRITICAL, 28.13 | CRITICAL, 27.96 | 9/21 → 20/21 | Genre, Mode, Friends added |
| `library-all-card-menu` | CRITICAL, 27.25 | CRITICAL, 30.51 | — | cursor under menu (modal) |
| `library-all-more-popover` | CRITICAL, 27.29 | CRITICAL, 30.55 | — | cursor under popover (modal) |
| `library-all-sort-dropdown` | CRITICAL, 26.66 | CRITICAL, 29.82 | — | cursor under dropdown (modal) |
| `library-all-add-popover` | HIGH, 27.38 | CRITICAL, 30.64 | — | severity worse; cursor under dropdown (modal) |
| `library-homedeck-card-menu` | HIGH, 7.8 | HIGH, 7.81 | — | cursor under menu (modal) |
| `library-homedeck-more-popover` | HIGH, 8.28 | HIGH, 8.29 | — | cursor under popover (modal) |
| `library-homedeck-search` | MEDIUM, 7.53 | MEDIUM, 7.53 | — | React selector MISS (see 5) |
| `library-homedeck` | MEDIUM, 7.57 | MEDIUM, 7.57 | — | |
| `library-all-games` | HIGH, 25.8 | HIGH, 25.8 | — | |
| `shell-bar-press-search` | CRITICAL, 61.06 | CRITICAL, 61.06 | — | not investigated |
| `shell-bar-hover-nav` | MEDIUM, 13.13 | MEDIUM, 7.47 | — | |
| `shell-bar-hover-search` | MEDIUM, 7.48 | MEDIUM, 7.48 | — | |
| `shell-palette-open` | HIGH, 18.25 | HIGH, 19.11 | — | |
| `shell-palette-query` | HIGH, 18.28 | HIGH, 19.14 | — | |
| `shell-tour-modal` | HIGH, 19.69 | HIGH, 18.19 | — | modal, cursor (out of scope) |
| `shell-activate-key-modal` | HIGH, 16.31 | HIGH, 15.1 | — | modal, cursor (out of scope) |

`library-all-add-popover` moved from HIGH to CRITICAL with the same glass and menu counts; its pixel diff rose from 27.4 to 30.6. Not investigated.

## Out of scope (left alone)

- Rune cursor under modal and menu (fork task `rune-modal-live`).
- "Play as <account>" in the native GameCard menu: needs the account-list and account-switch RPC (owner approval).
- Discord, Store and Music: not touched.

## Not done

- Homedeck card press (item 3): `pressedKeys` stays empty.
- `shell-bar-press-search` (CRITICAL, menu 37/0): not investigated.
- `library-all-add-popover` severity regression (HIGH → CRITICAL): not investigated.
- Capture popover exit still fades (Rune forces removal opacity 0).
- GPU frame-time before/after for CapturePopover and HomeDeck: not measured.
- Friends row: the harness host has no Discord friends playing, so the row shows disabled. A run with friends playing (`--seed-data` or a signed-in Discord) would verify the match.

## Housekeeping

- Harness runs write to `scratchpad/` (`exp-r4b` … `exp-r4f`, `run-final4`). No temp data copy was used in round 4.
- Exo processes from this session: closed by the harness (`run.mjs` closes Exo at the end); `exo-ui` PID 7540 and `ExoLauncher` PID 5688 belong to setup-redesign and were left alone.
