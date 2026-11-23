# Room Discord parity: report

Branch: `UhhErix/room-discord` (worktree `room-discord`). Not pushed. Scope: native Discord room (Rune, `apps/launcher/native-ui`) against the React room, signed in.

Status: **partial.** Menus, cursors, glass, the DM menu fixture and the voice call stage moved. Two items need a decision: the top-bar cursor under an open menu (Rune fork change) and the Go Live popover geometry. Settings/DM/server layout parity was measured, not rebuilt.

## Scores (signed-in parity harness, 9 Discord states)

| Metric | Before (`parity-baseline-signedin`) | After (`room-discord-after-signedin-6`) |
| --- | --- | --- |
| CRITICAL | 6 | 1 |
| HIGH | 3 | 3 |
| MEDIUM | 0 | 5 |
| PASS / SKIP | 0 / 0 | 0 / 0 |
| Open failures | 4 | 0 |
| Menu failures | 3 | 0 |
| Cursor failures | 8 | 5 |
| Glass failures | 9 | 0 |
| Pixel diff >15 | 1 | 1 (`discord-voice-stream`, 21.51) |
| Mean abs pixel diff | 12.87 | 11.79 |

The after run now includes the two voice states (`discord-voice-call`, `discord-voice-stream`); the previous "after" (`-5`) ran seven states and skipped them. Severity counts are therefore not a like-for-like delta on their own. Per state, the after run is:

| State | Menu | Cursor | Pixel diff | Severity |
| --- | --- | --- | --- | --- |
| discord-voice-stream | n/a | 10/14 | 21.51 | CRITICAL |
| discord-dm-context-menu | 15/15 | 11/21 | 12.10 | HIGH |
| discord-status-menu | 10/10 | 10/21 | 9.58 | HIGH |
| discord-channel-context-menu | 8/8 | 8/19 | 7.79 | HIGH |
| discord-settings-sheet | n/a | 18/21 | 13.87 | MEDIUM |
| discord-dm | n/a | 11/11 | 12.81 | MEDIUM |
| discord-voice-call | n/a | 11/11 | 12.02 | MEDIUM |
| discord-home | n/a | 10/10 | 9.23 | MEDIUM |
| discord-server-channel | n/a | 11/11 | 7.17 | MEDIUM |

Screenshots (folders are untracked scratch, not committed):
- Before, native: `apps/launcher/improve/parity-baseline-signedin/native/*.png`
- Before, React: `apps/launcher/improve/parity-baseline-signedin/react/*.png`
- After, native: `apps/launcher/improve/room-discord-after-signedin-6/native/*.png`
- After, React: `apps/launcher/improve/room-discord-after-signedin-6/react/*.png`
- Voice stage, iteration folder: `apps/launcher/improve/room-discord-voice-3/`
- Reports: `.../parity-baseline-signedin/report.md`, `.../room-discord-after-signedin-6/report.md`

## What changed

1. **Channel right-click menu order and separators match React** (`menus.rs`, `0ce9e62`).
2. **Sidebar spacing matches React** (`discord.rs`, `0ce9e62`): divider `my(11)`, category row height 34 with top padding 3. The channel menu now opens in the harness (menu 0 → 8).
3. **Channel unread dot** uses `unread || mentions > 0` (`discord.rs`, `0ce9e62`).
4. **Submenu trigger cursor** (`exo-kit/src/menu.rs`, `995d766`). The parent menu stays modal; a submenu no longer blocks it.
5. **Cursor test** `phase5e_bar_and_menu_items_report_pointer_cursor` (`main.rs`, `995d766`).
6. **Harness DM fixture** (`react-driver.mjs`, `4e43460`): the harness no longer overwrites the owner's settings, so DM pins and order match React. The DM menu went from CRITICAL (wrong target user) to 15/15 with no missing items.
7. **Voice call stage** (`discord_voice.rs`, `main.rs`, `2a77e55`): self tile with name badge bottom-left; "Connecting" / "Voice connected" pill; labelled call bar (Mute, Deafen, Camera | Share, Soundboard | Leave) with Leave as a labelled button; sidebar call row shows the Leave icon only; camera, share and soundboard row shown only when connected; chat pane no longer overflows the window (outer row `grow(1.0)`).
8. **Go Live opens as a popover above the call bar** (`discord_voice.rs`, `e766fc6`). It was a centred modal. Dismiss layer is transparent (no dim).
9. **Harness hygiene** (`seed.mjs`, `react-driver.mjs`, `fa43d80`): prose comments removed so `check-comments` passes; `states.mjs` (`2a77e55`): voice-call has no empty script; voice-stream taps `dc-voice-share` so the snap shows Go Live.

## Task-by-task status

**a. Voice call UI (main deliverable): partly done.**
- Done: items 7 and 8 above. Labels and icons on the bar, Leave state, Connecting pill, call row, tile size and badge.
- Not done: the React tile's blurred avatar backdrop; the right-hand Members column (native shows the chat pane); fit-to-stage solo tile (fixed 1120x630 for now); speaking ring not verified against React; soundboard popover not compared.
- Go Live: React anchors the picker to the right of the dock's Share button (about 895 px wide, left edge at the Share button). Native dock is centred, so its Share button sits at a different x. The popover is now above the dock but not at React's position. This is the remaining CRITICAL (`discord-voice-stream`, pixel 21.51).

**b. Black background audit: measured, no panel-level fix made.**
- Measured on `discord-voice-call` and `discord-voice-stream` (React-black pixels, native value there): mean 7.4 / 6.4 / 6.7 and 4.5 / 3.9 / 3.9 (RGB, out of 255). Pixels above 12: 5.0% and 2.6%.
- Where it comes from: most of the lift in the top 120 px is avatar images that React did not load in its snap (the React server rail shows placeholder dots). The rest is `glass_top_sheen()` (`discord.rs:62`, white at 3.5% over the top 16 px of every Discord panel). Not changed: it is a shared panel treatment, and React's own sheen was not checked.
- Pure black is already the panel base (`panel_frame` fills `BG`).

**c. Settings sheet, DM view, server channel: measured, not rebuilt.**
- Settings sheet cursor 18/21 (was 9/21 in the previous after run). The remaining cursor failures are at coordinates where the native sheet sits at a different position from React's; the nav rows and close button already set Pointer.
- Link rows (`getexo.app/help#discord-accounts` in `DiscordSignIn.tsx:11`; About links in `AboutSection.tsx`): the native Discord room has no such link rows (no help link on sign-in, no About links in settings), so there is no element to give Pointer. Adding them is feature work, not parity fixing. Not done.
- Server rail shows more avatars than React in the snap. React's snap did not load them; not investigated further.

## Blocked: top-bar cursor under an open menu

Needs a Rune change (`runtime/input/focus.rs`, `ModalScope::build`): nodes before the modal in tree order (the top bar) are inert. An experiment pinning the scrim below the bar fixed hover and let the click switch tabs, but removing `modal_tx` broke keyboard entry (`top_bar_context_menus_right_click_keyboard_and_submenus` fails). No fork change made. Waiting on the orchestrator's decision (msg_6d0e4ed000e8). Affects `bar:search` and `bar:nav:*` in the three menu states (5 cursor failures).

## Gates

- `cargo fmt --all --check` clean.
- `cargo clippy --workspace --locked -- -D warnings` clean.
- `cargo test --workspace --locked`: exo-ui 71 passed; other crates 4, 17 (1 ignored), 0, 0 passed.
- `cargo build --release --locked --bin exo-ui` ok.
- `node tools/check-comments.mjs` ok (ratchet ok).
- `node --test tools/parity/parity.test.mjs`: 8 pass, 0 fail.
- Dotnet not run: host not touched.

## Not done

- Black/glass audit beyond the measurement above (sheen decision).
- Link rows with Pointer (no native elements exist yet).
- Members column, avatar backdrop and speaking ring for the voice stage.
- clientSettings wiring for noise suppression, echo cancellation and AGC (cross-layer; skipped by instruction).
- Channel voice-status items (Set / Clear channel status): skipped by instruction.

## Harness notes (for whoever re-runs this)

- The native driver resolves `native-ui/target/debug/exo-ui.exe` before release, and `--exo-ui` did not override it in my runs. Build the debug binary (`cargo build --locked --bin exo-ui`) before every native pass. I got false "after" snaps from a stale debug binary, and the release binary lacks the agent tooling, so it cannot be used for snaps at all.
- `startWebTestInstance` wipes `--data-dir` on every React pass (`fresh: true`). A combined React+native run drops the signed-in state. Run native with `--skip-react` against a React reference, and copy the signed-in data before each native run:
  `robocopy %LOCALAPPDATA%\ExoLauncher <temp>\exo-room-discord-signedin /E /XD app app.incoming-dev-* app.previous-dev-*` (read-only from the owner's data; the `app` folder is excluded).
- A React-only run in this environment showed the splash screen and "click target not found" on most states. Not investigated further.
