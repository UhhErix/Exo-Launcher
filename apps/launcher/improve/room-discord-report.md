# Room Discord parity: report

Branch: `UhhErix/room-discord` (worktree `room-discord`). Not pushed. Scope: native Discord room (Rune, `apps/launcher/native-ui`) against the React room, signed in.

Status: **partial.** Menus, cursors, glass and open-state gaps moved a lot. The voice call rebuild, the black/glass audit and the settings/DM/server parity pass were not started (see "Not done").

## Scores (signed-in parity harness, 9 Discord states)

| Metric | Before (`parity-baseline-signedin`) | After (`room-discord-after-signedin-5`) |
| --- | --- | --- |
| CRITICAL | 6 | 1 |
| HIGH | 3 | 2 |
| MEDIUM | 0 | 4 |
| PASS / SKIP | 0 / 0 | 1 / 1 |
| Open failures | 4 | 0 |
| Menu failures | 3 | 1 |
| Cursor failures | 8 | 4 |
| Glass failures | 9 | 0 |
| Pixel diff >15 | 1 | 0 |
| Mean abs pixel diff | 12.87 | 7.99 |

Screenshots:
- Before, native: `apps/launcher/improve/parity-baseline-signedin/native/*.png`
- Before, React reference: `apps/launcher/improve/parity-baseline-signedin/react/*.png`
- After, native: `apps/launcher/improve/room-discord-after-signedin-5/native/*.png`
- After, React reference (same as before, React unchanged): `apps/launcher/improve/room-discord-after-signedin-5/react/*.png`
- Reports: `.../parity-baseline-signedin/report.md`, `.../room-discord-after-signedin-5/report.md`

## What changed

1. **Channel right-click menu order and separators match React** (`menus.rs`, commit `0ce9e62`). Mark as read, Invite people, Copy link, separator, Mute channel / Unmute channel, Notification settings (hidden for voice and stage), separator, Edit channel, Delete channel (danger), Copy channel ID (developer mode only).
2. **Sidebar spacing matches React** (`discord.rs`, commit `0ce9e62`): divider `my(11)`, category row height 34 with top padding 3. This brings the first channel row into the React click target, so the channel menu opens in the harness (menu 0 → 8).
3. **Channel unread dot** uses `unread || mentions > 0` (`discord.rs`, commit `0ce9e62`).
4. **Submenu trigger cursor** (`exo-kit/src/menu.rs`, commit `995d766`). A submenu was modal, which blocked the parent menu's rows. Hovering Idle or Do not disturb while its submenu was open gave the default cursor. The parent stays modal; the submenu does not. Submenu items still take clicks (checked: clicking `dc-status-idle-15` sets status to idle and closes the menu).
5. **Cursor test** `phase5e_bar_and_menu_items_report_pointer_cursor` (`main.rs`, commit `995d766`): nav tab and search are Pointer; status menu items, including submenu triggers and submenu items, are Pointer.
6. **Earlier in the session** (`21a09f8`): secondary-press closure signatures, friend More button opens at its element, channel rows open the channel and voice channels join, account settings username/status fixes.

## Gates

- `cargo fmt --all` clean; `cargo clippy --workspace --locked -- -D warnings` clean.
- `cargo test --workspace --locked`: all pass (exo-ui 71 passed; 17 passed and 1 ignored in another crate).
- `cargo build --release --locked --bin exo-ui` ok.
- `node tools/check-comments.mjs` ok (ratchet ok).
- `node --test tools/parity/parity.test.mjs`: 0 fail.
- Dotnet not run: host not touched.

## Remaining gaps (from the after run)

- **Top bar cursors (4 cursor gaps in menu states).** The context-menu scrim covers the top bar while a menu is open, so nav tabs and search report Default. React keeps them pointer. The fix is to pin the scrim below the bar, but that changes click-through over the bar, so it needs a decision on what a bar click does while a menu is open. Not changed.
- **DM context menu (critical).** React right-clicked `coltondiff` ("Pin DM", "Mute @coltondiff"). Native right-clicked `umlazy` ("Unpin DM", "Mute @umlazy"). This looks like DM list order or pin-state fixture difference, not the menu builder. Not investigated.
- **Channel menu voice status items** (Set / Clear channel status) are not implemented; the target lacks the fields.
- **Settings sheet, DM view, server channel**: only cursor gaps were looked at (link rows like getexo.app and the voidenergy link should be Pointer). Layout parity not audited.

## Not done

- Voice call UI rebuild to React parity (tiles, speaking ring, controls, Go Live, soundboard, call bar). Not started. The `discord-voice-call` and `discord-voice-stream` states run in the harness, but the UI was not rebuilt.
- Black background audit against React and a glass audit within the 4-blur budget, beyond what the harness reports (the harness reports glass=3 or 4 per state, no failures).
- clientSettings wiring for noise suppression, echo cancellation and AGC (cross-layer; not started).

## Harness notes (for whoever re-runs this)

- `--exo-ui` must be a debug build (`cargo build --locked --bin exo-ui`). The release build lacks Rune's `tooling` feature, so native snapshots fail with "this build has no agent tooling".
- `startWebTestInstance` wipes `--data-dir` on every React pass (`fresh: true`). A combined React+native run therefore drops the signed-in state, and native shows the sign-in card. Run native with `--skip-react` against a React reference copied from `parity-baseline-signedin/react`, and recopy the signed-in data before each native run:
  `robocopy %LOCALAPPDATA%\ExoLauncher <temp>\exo-room-discord-signedin /E /XD app app.incoming-dev-* app.previous-dev-*` (read-only from the owner's data; the `app` folder is excluded).
- A React-only run in this environment showed the splash screen and "click target not found" on most states. Not investigated further.
