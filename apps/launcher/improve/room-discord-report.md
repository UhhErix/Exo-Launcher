# Room Discord parity: report

Branch: `UhhErix/room-discord` (worktree `room-discord`). Scope: native Discord room (Rune, `apps/launcher/native-ui`) against the React room, signed in.

## Round 3 (task_1cdae989baa7, on 2324296)

Status: **partial.** Outcome `failed`: the feature audit was not run live, and DM and server layouts were not rebuilt end to end.

Scores, signed-in run `room-discord-after-signedin-8` (9 states, exit 0):

| Metric | After 7 | After 8 |
| --- | --- | --- |
| CRITICAL | 1 | 0 |
| HIGH | 3 | 3 |
| MEDIUM | 5 | 6 |
| Menu failures | 1 (`discord-voice-stream` 0/0) | 0 |
| Cursor failures | 4 | 4 |
| Pixel diff >15 | 1 (voice-stream 20.81) | 1 (voice-call 17.37) |
| Mean abs pixel diff | 11.87 | 10.51 |

Changes in this round:
1. **Menu 0/0 (item 4), fixed.** Cause: the React selector `.exo-dc-golive-menu` exists only while live, so React found 0 items while the picker was open. Native probe also did not collect the picker tabs, and `resolveProbeExe` picked a stale `target/release` binary. Fixed: `states.mjs` now opens `.exo-dc-golive` and counts `[role="tab"]`; `run_parity_probe` collects `dc-gl-tab-*` tabs; `resolveProbeExe` delegates to `resolveExoUiExe` (debug first). Result: 2/2.
2. **Go Live picker layout (item 3).** While picking, the modal shows only the tabs (Applications / Screens), the close control and the grid. Quality, audio and footer appear after a source is chosen. `DiscordVoiceUiState.golive_chosen` drives this. Visual compare: tabs at the React position, popover at the React x.
3. **Voice tile (item 2), implemented, not visually verified.** Avatar backdrop: image at 1.4× tile, `saturate(1.3)`, `blur(36)`, black overlay at alpha 0.58 (React brightness 0.42). Speaking ring: 2 px `good()` border, opacity transition on `SPRING_QUICK`. The voice-call compare cannot confirm this: the React reference is in the "Connecting" state with an empty stage, while Rune shows an avatar tile. The two sides are not the same state.
4. **Settings sheet (item 1), partly fixed.** The native state tapped React coordinates, which hit a different control, so the sheet never opened. It now taps `dc-me-settings`. Sheet rect equals React's (550,126, 820×880). Remaining settings cursor misses (9/21) are the top-bar cursor under the modal, which is fork-blocked.
5. **DM and server layouts (item 1), not done end to end.** Native widths already match React (DM pane 400, server channels 300, chat at x320). No further rebuild or measurement was made after the sheet fix.

Feature audit (item 5). No live Discord session was run; the sandbox network was unreliable. Harness-proven only:

| Feature | Status |
| --- | --- |
| Open status menu / DM context menu / channel context menu | ✓ harness (menu counts match React) |
| Go Live picker open, share popover | ✓ harness |
| Send, edit, delete, reply, react to messages | not verified live |
| Attachments, emoji picker | not verified live |
| Friend add / accept / remove | not verified live |
| Server and channel switching, unread badges | not verified live |
| Join / leave voice, mute / deafen | not verified live |
| Go Live start / stop, DM calls | not verified live |

Gates passed on this source before the run; no source changed after that. Harness notes: `--exo-ui` override; `exo-ui.exe` is locked while another session runs `smoke.mjs`, so the build used `CARGO_TARGET_DIR=…/rd-check`.

Open after round 3: top-bar cursor under open menus (Rune fork, `ModalScope`, not touched); feature live verification; DM and server end-to-end; voice tile visual check against a React tile in the same state; element blur cost per tile (one element blur per tile with an avatar; not a backdrop, so not counted against the cap of 4).

## Round 2 (archived)

Status: **partial.** Round 2 (items 2–6 of the owner's decisions) moved the Go Live popover, the call bar, the members column, the sheen and the solo tile. Still open: the top-bar cursor under menus (Rune fork, not touched), the avatar backdrop and speaking ring on the voice stage, the settings/DM/server layout (measured only), and the Go Live picker's inner layout.

## Scores (signed-in parity harness, 9 Discord states)

| Metric | Before (`parity-baseline-signedin`) | After 6 (`room-discord-after-signedin-6`) | After 7 (`room-discord-after-signedin-7`) |
| --- | --- | --- | --- |
| CRITICAL | 6 | 1 | 1 |
| HIGH | 3 | 3 | 3 |
| MEDIUM | 0 | 5 | 5 |
| Open failures | 4 | 0 | 0 |
| Menu failures | 3 | 0 | 1 (`discord-voice-stream`, see below) |
| Cursor failures | 8 | 5 | 4 |
| Glass failures | 9 | 0 | 0 |
| Pixel diff >15 | 1 | 1 | 1 (`discord-voice-stream`, 20.81) |
| Mean abs pixel diff | 12.87 | 11.79 | 11.87 |

Per state, after 7:

| State | Menu | Cursor | Pixel diff | Severity |
| --- | --- | --- | --- | --- |
| discord-voice-stream | fail (0/0) | 14/14 | 20.81 | CRITICAL |
| discord-status-menu | 10/10 | 10/21 | 10.21 | HIGH |
| discord-dm-context-menu | 15/15 | 11/21 | 12.15 | HIGH |
| discord-channel-context-menu | 8/8 | 8/19 | 7.82 | HIGH |
| discord-settings-sheet | n/a | 18/21 | 14.40 | MEDIUM |
| discord-dm | n/a | 11/11 | 12.87 | MEDIUM |
| discord-voice-call | n/a | 11/11 | 11.53 | MEDIUM |
| discord-home | n/a | 10/10 | 9.86 | MEDIUM |
| discord-server-channel | n/a | 11/11 | 7.21 | MEDIUM |

Pixel changes against after 6: voice-call 12.02 → 11.53, voice-stream 21.51 → 20.81 (better). Home 9.23 → 9.86, status 9.58 → 10.21 and settings 13.87 → 14.40 (worse). The rise on home and status is most likely the sheen change (item 3 below); not isolated yet.

Open: the `discord-voice-stream` menu check reports fail with 0 React and 0 native items and no missing entries. It was `n/a` in after 6. Not investigated; it is the reason that state scores CRITICAL.

Screenshots (folders are untracked scratch, not committed):
- Before, native: `apps/launcher/improve/parity-baseline-signedin/native/*.png`
- After 7, native: `apps/launcher/improve/room-discord-after-signedin-7/native/*.png`, React reference in `.../room-discord-after-signedin-7/react/*.png` (copied from after 6)
- After 7 compare sheets: `.../room-discord-after-signedin-7/sheets/*-compare.png`
- Voice stage iterations: `apps/launcher/improve/room-discord-voice-3/` (React), `.../room-discord-voice-4/` (native, after the round-2 layout)
- Reports: `.../room-discord-after-signedin-7/report.md`, `.../room-discord-voice-4/report.md`

## What changed

Round 1 (commits `0ce9e62`, `995d766`, `2a77e55`, `e766fc6`, `4e43460`, `fa43d80`, `78c4947`):
1. Channel right-click menu order and separators match React (`menus.rs`).
2. Sidebar spacing matches React: divider `my(11)`, category row 34 high with top padding 3 (`discord.rs`).
3. Channel unread dot uses `unread || mentions > 0` (`discord.rs`).
4. Submenu trigger cursor; the parent menu stays modal (`exo-kit/src/menu.rs`).
5. Cursor test `phase5e_bar_and_menu_items_report_pointer_cursor` (`main.rs`).
6. Harness DM fixture no longer overwrites the owner's settings (`react-driver.mjs`).
7. Voice call stage: self tile with name badge, "Connecting" / "Voice connected" pill, labelled call bar, Leave as a labelled button, sidebar call row icon only (`discord_voice.rs`, `main.rs`).
8. Go Live opens as a popover above the call bar, no dim (`discord_voice.rs`).
9. Harness hygiene: prose comments removed (`seed.mjs`, `react-driver.mjs`); `states.mjs` voice-call and voice-stream definitions.

Round 2 (this push, uncommitted until the commit below):
- **Item 2, Go Live popover position.** Width 900, height 620, left edge at the Share button (x 952), top at 384, bottom at 1004. React measured: x 952, y 384, w 900, h 620. Built from the call bar's own geometry (bar 696 wide, centred; Share at offset 340), so it stays on Share at any window width. The header now holds the tabs, Refresh and close (the title row is gone, as in React's picker).
- **Item 3, black sheen.** React has a sheen: top pixel 14/255 fading to 0 over about 22 px. `glass_top_sheen` now uses white alpha 0.055 over 22 px (was 0.035 over 16). No pixel-level match yet beyond this.
- **Item 4, link rows.** React's signed-in Discord room has no getexo.app or voidenergy link rows. The only getexo.app link is the sign-in help link (`DiscordSignIn.tsx:11`, shown only when signed out). The member activity lines (`sapph.xyz`, `/help`) are plain text in React too (`ActivityLine.tsx` renders a `span`). Native matches, so nothing was added and nothing got a Pointer cursor.
- **Item 5, call bar.** Fixed widths as React: Mute 89, Deafen 102, Camera 105, Share 94, Soundboard 132, Leave 94, bar 696 wide, buttons 40 high, dividers in 4 px slots. Predicted x positions: 620, 717, 827, 952, 1054, 1206. Not measured in the tree yet.
- **Item 5, members column.** The voice view now shows the members panel on the right (300 wide, x 1610) unless the channel chat is open. Group labels read `Founder (1)`, header reads `Members`, as React's `MemberList.tsx`. The server members panel (`discord.rs`, `right_members_panel`) shares this code.
- **Item 5, solo tile.** 1240 × 697.5 (React's size at 1920 × 1080), vertically centred in the stage (stage bottom padding 63). Fixed size, not fit-to-stage.

## Task-by-task status

**a. Voice call UI: mostly done.** Call bar widths, popover anchor, members column and solo tile are in. Not done: the blurred avatar backdrop (React uses `blur(36px)` on the avatar behind a tile), the speaking ring (React's is an inset 2 px `good` ring via `::after`; native uses a 2 px border, not compared), fit-to-stage for the solo tile, and the soundboard popover.

**b. Black background audit: sheen decided, rest measured.**
- Measured on `discord-voice-call` and `discord-voice-stream`: native lift over pure black, mean 7.4 / 6.4 / 6.7 and 4.5 / 3.9 / 3.9 (RGB). Most of the top-120 px lift is avatar images React did not load in its snap.
- Sheen: changed to React's measured alpha (item 3 above).

**c. Settings sheet, DM view, server channel: measured, not rebuilt.** Settings sheet cursor 18/21. The remaining cursor failures are at coordinates where the native sheet sits at a different position from React's; the nav rows and close button already set Pointer. DM and server-channel layouts are unchanged this round.

**Go Live picker inner layout: not matched.** React's picker, while choosing a source, shows only the tabs, a status line and the source grid. Native also shows the resolution, frame rate, audio and footer rows in that state. This is why the popover's tabs sit 33 px lower than React's and why the four cursor probes at React's tab positions miss. Matching it means restructuring the picking state (React's second step is the preview and Go Live). Not done.

## Blocked: top-bar cursor under an open menu

Needs a Rune change (`runtime/input/focus.rs`, `ModalScope::build`): nodes before the modal in tree order (the top bar) are inert. An experiment pinning the scrim below the bar fixed hover and let the click switch tabs, but removing `modal_tx` broke keyboard entry (`top_bar_context_menus_right_click_keyboard_and_submenus` fails). No fork change made, per instruction. Affects `bar:search` and `bar:nav:*` in the status, DM and channel menu states (the cursor failures above).

The same cause shows on the voice stage: while the Go Live popover is open, its dismiss layer and the modal scope make the call bar read as default (`discord-voice-stream` probe: hit `dc-golive-dismiss` at the Share button). The voice-stream cursor check passes this round because the dismiss layer now stops at the bar's top edge, and the call bar is outside it.

## Link rows (item 4), summary

No getexo.app or voidenergy rows exist in React's signed-in Discord room. Nothing added.

## Gates (HEAD at push)

- `cargo fmt --all --check`: clean.
- `cargo clippy --workspace --locked -- -D warnings`: clean.
- `cargo test --workspace --locked`: exo-ui 71 passed; other crates 4, 17 (1 ignored), 0, 0 passed.
- `cargo build --release --locked --bin exo-ui`: ok.
- `node tools/check-comments.mjs`: ratchet ok.
- `node --test tools/parity/parity.test.mjs`: exit 0.
- Signed-in 9-state harness: run, see the table above.
- Dotnet not run: host not touched.

## Not done

- Voice stage: avatar backdrop, speaking ring check, fit-to-stage solo tile, soundboard popover.
- Go Live picker: picking-state layout (tabs, status line, grid only) and its tab positions.
- Settings sheet cursor (18/21), DM and server-channel layout (measured, not rebuilt).
- `discord-voice-stream` menu check (0/0 fail), cause unknown.
- Top-bar cursor under an open menu (Rune fork change, owner decision pending).
- clientSettings wiring for noise suppression, echo cancellation and AGC (cross-layer; skipped by instruction).
- Channel voice-status items (Set / Clear channel status): skipped by instruction.

## Harness notes (for whoever re-runs this)

- The native driver resolves `native-ui/target/debug/exo-ui.exe` before release, and `--exo-ui` did not override it in my runs. Build the debug binary (`cargo build --locked --bin exo-ui`) before every native pass. A failed build leaves the stale binary in place and the harness still reports OK; check the build output first.
- `startWebTestInstance` wipes `--data-dir` on every React pass (`fresh: true`). Run native with `--skip-react` against a React reference copied into the report folder, and copy the signed-in data before each native run:
  `robocopy %LOCALAPPDATA%\ExoLauncher <temp>\exo-room-discord-signedin /E /XD app app.incoming-dev-* app.previous-dev-*` (robocopy exit 1 means files copied; the `app` folder is excluded).
- A React-only run in this environment showed the splash screen and "click target not found" on most states. Not investigated further.
