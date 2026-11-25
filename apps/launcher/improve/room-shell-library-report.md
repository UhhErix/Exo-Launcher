# room-shell-library: menus, popovers, dropdowns, glass and cursors (round 4)

Branch: UhhErix/room-shell-library. Harness: `node tools/parity/run.mjs --rooms shell,library --exo-ui native-ui/target/debug/exo-ui.exe --report-dir <scratch>` (no `--seed-data`).

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
