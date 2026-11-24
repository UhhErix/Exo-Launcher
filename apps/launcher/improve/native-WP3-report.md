# WP3 — Shell chrome: Island, Downloads, updater, notices, boot gate, shortcuts, palette

Branch: `UhhErix/native-WP3` (base: `feat/native-polish-2`).
Step 0 commit (push-event registry `events.rs`, pushed alone first): `6158403a9ac436702c753018941ef5c7264d72c5`.

## Summary

All **18/18** non-DONE WP3 items in `apps/launcher/improve/feature-matrix.md` (`#### WP3`) and `apps/launcher/improve/feature-matrix.json` are implemented and marked `DONE`. No items are blocked on Rune fork features. All 6 previously `DONE` Shell & Bar rows (`shell-bar-01`, `02`, `05`, `06`, `08`, `24`) remain verified and intact.

## Gates

Run in `apps/launcher/native-ui` (and `apps/launcher` for comment/parity checks) on final tree:

- `cargo fmt --all -- --check`: **PASS** (exit 0)
- `cargo clippy --workspace -- -D warnings`: **PASS** (exit 0, 0 warnings)
- `cargo test --workspace`: **PASS** (exit 0 — **124 passed, 0 failed, 1 ignored**: `exo-kit` 4/4, `exo-rpc` 22/22 + 1 ignored bench, `exo-ui` 98/98 including all motion gates and 22 new WP3 unit tests)
- `cargo build --release -p exo-ui`: **PASS** (exit 0)
- `node --test tools/parity/parity.test.mjs`: **PASS** (8/8)
- `node tools/check-comments.mjs`: **0 violations** in any `native-ui` or modified file (only pre-existing stale ratchet entries in untouched `mouse/` and `ui/` files)

## Item-by-Item Status & Verification

| ID | Feature | Status | Native ref | How verified |
|---|---|---|---|---|
| `shell-bar-03` | Discord unread / mention badge on nav tab | DONE | `exo-ui/src/shell.rs:339` | `friends.badge` RPC + push event registered in `events.rs`, red `99+` pill on Discord nav tab. Verified via `shell::tests::discord_badge_and_window_state_events_update_chrome`, 1920x1080 `island` snap, and live host RPC. |
| `shell-bar-04` | Mini player in bar (title, play/pause, next, volume) | DONE | `exo-ui/src/music.rs:1521`, `exo-ui/src/shell.rs:360` | Fixed `previous` RPC verb, interactive seek drag (`music.control` `seek`), disabled states (`can_prev/play/pause/next/seek`), volume slider/mute remembering `last_audible`, Medium volume popover (`shell.rs:360`), `icon_volume_low`. Verified via `wp3_mini_player_seek_and_mute_state`, `top_bar_nav_x_is_invariant`, `mini_player_stays_inline`, `phase3`, `phase4`, motion gates, and 1920x1080 `island` snap. |
| `shell-bar-07` | Window controls (min/max/close, drag, double-click maximize) | DONE | `exo-ui/src/shell.rs:753` | `shell.windowState` RPC + `shell.window` event tracking `maximized` and `maximizeHover`; Restore/Maximize icons mirror host state; buttons wired to `Tx::minimize`, `Tx::toggle_maximized`, `Tx::close_window`. Verified via `shell::tests::discord_badge_and_window_state_events_update_chrome`, `titlebar_tools_capsule_matches_react_geometry`, and live host RPC. |
| `shell-bar-09` | Exo Island (in-call / download capsule) | DONE | `exo-ui/src/island.rs:324` | In-call capsule outside Discord (speaking dot, channel/server, up to 4 faces + `+N`, right-click per-user mute/volume menu via `set_discord_user_mix`, `Live` badge, FaceTime-style duration timer, Mute/Deafen/Leave) + incoming-call ring capsule (`discord.call` / `discord.ring` Answer/Decline). Verified via 3 `island::tests::*` unit tests and 1920x1080 `island` snap. |
| `shell-bar-10` | Downloads sheet (queue, pause, cancel, resume, speed, order) | DONE | `exo-ui/src/downloads.rs:412` | Full 880x760 glass sheet matching React (`downloads.list`, `downloads.pauseAll`, `downloads.resumeAll`, `downloads.move` `{id, by}`, `game.dequeue`, `game.resumeDownload`, `game.pauseInstall`, `game.cancelInstall`, `game.autoUpdate`, `install.queue`/`install.progress` events), wired from Library More menu (`menus.rs`) and Command Palette. Verified via 2 `downloads::tests::*` unit tests, 1920x1080 `downloads` snap, live host temp-copy run, and React-vs-Native parity harness (**PASS**, 12/12 cursors, pixel diff 4.69). |
| `shell-bar-11` | What's New sheet (changelog after update) | DONE | `exo-ui/src/whats_new.rs:930` | Embeds `ui/src/lib/whatsNew.json` and `CHANGELOG.md`, `compare_versions`, `missed_releases`, auto-opens after version upgrade, persists `whatsNewSeen` via `settings.set`, handles `versions.new` event. Verified via `whats_new::tests::*`, 1920x1080 `whats-new` snap, live temp-copy click on `Continue`, and React-vs-Native parity harness (pixel diff 7.02, modal geometry aligned 1:1). |
| `shell-bar-12` | App update flow (check, download progress, install, restart) | DONE | `exo-ui/src/whats_new.rs:1090` | Startup `app.checkUpdate` with 800ms boot hold, manual check (`{ manual: true }`), `app.installUpdate`, `app.updateState`/`app.updateProgress` push events, `UpdateSheet` with progress bar & retry, NoticeStack alerts. Verified via `whats_new::tests::update_phase_and_words_match_react_contract` and 1920x1080 `update-sheet` snap. |
| `shell-bar-13` | Activate key sheet | DONE | `exo-ui/src/key_modal.rs:182` | Steam/Epic/GOG chips, `keys.preview` respecting user edits, `Copy key`/`Copied`, inline error, Epic clipboard copy before `keys.activate`, `keys.found` push event. Verified via `key_modal::tests::*`, live temp-copy click (`key-store-epic`), and React-vs-Native parity harness (`shell-activate-key-modal`, pixel diff 6.17). |
| `shell-bar-14` | Toast / notice stack (with actions, tones, auto-dismiss) | DONE | `exo-ui/src/notice.rs:960` | Bottom-right stack (lifted 88px in Music), titles, `where_label`, count badge, 36px avatar/tone glyph, confirm actions, edge progress bar, click-to-open, lasting/sticky semantics, 8-item waiting queue, `app.notify`/`app.notifyClear`. Verified via 4 `notice_stack_*` tests + motion audit. |
| `shell-bar-15` | Anti-cheat missing notice (install anti-cheat prompt) | DONE | `exo-ui/src/notice.rs:139` | `anticheat.missing` handler in `events.rs` with Install action invoking `runtimes.install`. Verified via `notice::tests::anticheat_missing_pushes_install_action_notice`. |
| `shell-bar-16` | Store outage notices | DONE | `exo-ui/src/notice.rs:174` | `stores.outage` push event + initial `stores.outages` fetch on connect, `Play offline` action calling `stores.playOffline`. Verified via `notice::tests::store_outage_pushes_and_clears_notice`. |
| `shell-bar-17` | Discord DM / ping toasts | DONE | `exo-ui/src/notice.rs:179` | `discord.notify`, `discord.notifyClear`, `discord.dmLine` handlers with count coalescing and click-to-open DM/channel navigation. Verified via `notice::tests::discord_notify_coalesces_counts_and_clears`. |
| `shell-bar-18` | Driver install overlay | DONE | `exo-ui/src/boot.rs:598` | Opaque `#000` overlay listening to `gpu.driver` and `gpu.driverInstall` with monotonic phases, milestone rail, `Hide until it installs`, `Not now`/`Restart now` (`bios.update` `{ action: "windows" }`), `Copy logs` (`app.logsFor`). Verified via `boot::tests::driver_install_phase_never_moves_backwards_except_failed` and 1920x1080 `driver-install` snap. |
| `shell-bar-19` | Boot gate + error boundary (splash, retry, crash screen) | DONE | `exo-ui/src/boot.rs:448` | `BootSplash` while connecting/update-hold, `shell.painted` sent once on setup, `BootGate` error card (`Try again`, `Open logs`, `Copy logs`), `catch_unwind` room `ErrorBoundary`. Verified via `boot::tests::*`, 1920x1080 `boot-error` snap, and live failed-pipe render. |
| `shell-bar-20` | Gamepad bar / controller navigation | DONE | `exo-ui/src/gamepad.rs:152` | Xbox/PlayStation/Nintendo glyphs & detection, XInput `START`/`BACK`/`LT`/`RT` polling, context-aware bar with paused Controller Lab state, per-room subtab cycling. Verified via `gamepad::tests::gamepad_kind_and_hints_match_react` and `phase2`. |
| `shell-bar-21` | Global keyboard shortcuts (Ctrl+F, Ctrl+A, Alt+arrows, etc.) | DONE | `exo-ui/src/main.rs:131` | Modal holds during DriverInstall/UpdateSheet/WhatsNew, `Ctrl+K`, `Ctrl+,`, `Ctrl+1..7`, `Ctrl+F` (Library/Store/Discord), `Ctrl+A` (Library), `Ctrl+Shift+M`/`D` (Discord mute/deafen), `Alt+Left`/`Right` room history/back, `Alt+Up`/`Down` (+Shift unread) Discord channels, `Shift+Escape` mark guild read, `Space` in Music, `Escape` dismiss. Verified via `wp3_global_shortcuts_and_modal_holds`. |
| `shell-bar-22` | Command palette (rooms, settings, games, actions) | DONE | `exo-ui/src/palette.rs:526` | Rooms, PC/Settings sections, Library games, Downloads, What's new, Add game, Refresh, Activate key, Resume/Take tour, Check updates, Minimize/Close window. Verified via `palette::tests::*`, 1920x1080 `palette` snap, live temp-copy run, and parity harness (`shell-palette-open` 20/20 cursors, 0 missing items, pixel diff 9.05). |
| `shell-bar-23` | Palette extras (Play/Install selected, notes, Discord, Music) | DONE | `exo-ui/src/palette.rs:42` | Loads `notes.list`, `discord.home`, `discord.channels` on open; Play/Install selected game, New note/Notes/note items, Discord DMs/servers/channels, Music play-pause/next/previous/open. Verified via `palette::tests::palette_extras_include_selected_game_notes_discord_and_music` and live signed-in temp-copy run. |

## Parity Harness Before / After (1920x1080, scale 1)

Harness command:
`node tools/parity/run.mjs --rooms shell --exo-ui native-ui/target/debug/exo-ui.exe --seed-data <temp-copy-of-LOCALAPPDATA-ExoLauncher> --report-dir <scratch>`

Added harness states in `tools/parity/states.mjs` and `tools/parity/native-driver.mjs`:
- `shell-downloads-sheet` (React: Library More › Downloads; Native: `--scene downloads`)
- `shell-whats-new-sheet` (React: Settings › About › Changelog; Native: `--scene whats-new`)

Added standalone native scenes in `exo-ui/src/main.rs` (all captured and visually inspected at 1920x1080 scale 1):
`downloads`, `whats-new`, `update-sheet`, `island`, `driver-install`, `boot-error`.

| State | Run 1 (before boot-hold & geometry polish) | Final Run (after fixes) | Notes |
|---|---|---|---|
| `shell-bar-hover-nav` | CRITICAL (fail open, glass 0, px 5.76) | **PASS** (score 0, cursors 10/10, glass 9/9, px 5.76) | Fixed `load_live_state_sync` clearing `whats_new.boot_hold_active`. |
| `shell-bar-hover-search` | CRITICAL (fail open, glass 0) | **PASS** (score 0, cursors 11/11, glass 9/9, px 5.76) | Fixed `load_live_state_sync` clearing `whats_new.boot_hold_active`. |
| `shell-downloads-sheet` | HIGH (React MISS, 720px width, 1/12 cursors, px 4.80) | **PASS** (score 0, cursors 12/12, glass 9/11, px 4.69) | Matched `.exo-game-overlay-stage` 880x760, scrim `top: 52px`, disabled `Cursor::NotAllowed`. |
| `shell-whats-new-sheet` | HIGH (React MISS, height 688px, px 9.51) | HIGH (glass warn, px **7.02**, modal aligned 1:1) | Matched `.exo-whats-new` 540x760, centered `Continue` pill. Remaining 7.02 diff is Settings › About background (WP4). |
| `shell-activate-key-modal` | HIGH (glass 0, px 9.66) | MEDIUM (glass 10/13 pass, cursors 15/17, px **6.17**) | Pixel diff reduced from 15.1 (baseline) / 9.66 -> 6.17. |
| `shell-palette-open` | CRITICAL (glass 0, menu 0/19, px 8.57) | HIGH (cursors **20/20 pass**, glass 10/7 pass, missing 0, px 9.05) | All 19 React items present; extra items come from non-virtualized scroll list. |
| `shell-palette-query` | CRITICAL (glass 0, menu 0/19) | HIGH (cursors 19/20, glass 10/7 pass, px 9.05) | Pre-existing probe focus behavior on `--scene palette` script typing. |
| `shell-bar-press-search` | CRITICAL (fail open) | HIGH (open pass, cursors 11/11, glass 9/10, px 26.13) | Pre-existing press-on-down opens palette immediately on pointer down. |
| `shell-tour-modal` | CRITICAL (fail open) | CRITICAL (pre-existing `tourStep` summary field) | Owned by `setup.rs` (`shell-bar-24`, native-onboarding). |

## Live Behaviour Check (Temp Copy)

- Copied `%LOCALAPPDATA%\ExoLauncher` (excluding `app/`, `Games/`, `drivers/`, `updates/`, `gifts-webview/`) into an isolated temporary directory under `$env:TEMP` and launched against a repo-built Debug `ExoLauncher.exe` via `--host-pipe` + `EXO_LAUNCHER_DATA_DIR` (never touching `%LOCALAPPDATA%\ExoLauncher\app`, the real data dir, or the owner's running Exo).
- Verified live RPC bootstrap (`shell.bootstrap`), `friends.badge`, `shell.windowState`, `downloads.list` (showing real empty state `"Nothing is downloading."`), `palette` extras (`notes.list`, `discord.home`, `discord.channels`), interactive `whats-new-continue` click dismissal, `activate-key` store chip switching (`key-store-epic`), and live `BootGate` error screen on failed pipe connection.
- Removed all temporary directories after verification.

## Follow-up: `feat/native-polish-2` merge, Rune `exo-inputs` pin bump, & Shell RPC Parity

- **Merge (`f017fa8`)**: Merged `origin/feat/native-polish-2` (`df56021`: WP1, WP5, WP11, `events.rs` `EventRegistry`, `RpcTrace.cs`), keeping `EventRegistry` and registering all WP3 shell/bar/downloads/island/whats_new/boot/notice/key_modal handlers alongside WP1/WP5.
- **Isolated Rune pin bump (`d600f89`)**: Bumped all 8 Rune pins in `apps/launcher/native-ui/Cargo.toml` (+ `Cargo.lock`) to `exo-inputs` `e6c0716a660a97576b3e3951973c599b82bd11e7` in one isolated commit.
- **Virtualized palette list (`palette.rs`)**: Wrapped palette rows in an inner `.growth(GrowthSpec::default()).virtualize(scaled(40.0)).reveal_selected(true)` column inside the scroll viewport with keyed group headers.
- **Shell RPC & push-event parity (`shell.rs`, `boot.rs`, `notice.rs`, `state.rs`, `main.rs`, `exo-rpc/src/lib.rs`, `tools/rpc-parity/run.mjs`)**:
  - Wired `shell.heartbeat` (1 Hz when connected, 0 wakes offline), `app.pendingOpen`, `telemetry.track`, `shell.setChromeLayout`, `app.log`, `shell.prepare`, `shell.ready`, `shell.painted`, `notes.pending`, `art.base`, `capture.status`, and startup `music.volume`.
  - Fixed `apply_settings` to respect `onboardingComplete: true` and boolean `uiPrefs.tourSeen` (preventing onboarding wizard from covering top-bar tabs on already-onboarded instances).
  - Registered `backup.stopped` and `components.failed` in `notice.rs` and added explicit `parse_event` arms in `exo-rpc/src/lib.rs` for all 19 WP3 shell push events.
  - Updated `tools/rpc-parity/run.mjs` native `downloads` action to open Library `More` -> `Downloads`.

### `tools/rpc-parity` Before / After (`node tools/rpc-parity/run.mjs`)

| Metric | Baseline (`df56021`) | After WP3 follow-up |
|---|---:|---:|
| Native drivable actions | 16 / 18 | **17 / 18** (`downloads` now drivable on native; only `discord-dm` signed out) |
| Unique RPC methods called by Native | 34 | **62** (+28) |
| Methods React called that Native never called | 53 | **33** (**0** in WP3 shell/bar/palette/downloads/notice/boot/gamepad) |
| Static React push events unhandled by Native | 45 | **23** (**0** in WP3 shell/bar/palette/downloads/notice/boot/gamepad) |

### Follow-up Gates (all exit 0)

- `cargo fmt --all -- --check`: **PASS** (0)
- `cargo clippy --workspace -- -D warnings`: **PASS** (0)
- `cargo test --workspace`: **PASS** (**143 passed, 0 failed, 1 ignored**: `exo-kit` 4/4, `exo-rpc` 22/22 + 1 ignored, `exo-ui` 117/117)
- `cargo build --release -p exo-ui`: **PASS** (0)
- `node tools/check-comments.mjs`: **PASS** (`Comment ratchet ok: 2384 legacy files, 232 zero-comment files`)
- `node --test tools/rpc-parity/parity.test.mjs`: **PASS** (4/4)

## Vision Parity QA Delta (on top of `15c904c`)

Addressed every WP3-owned open blocker and major item from `origin/UhhErix/parity-vision-qa` (`apps/launcher/improve/vision-parity.json`) without modifying any commit at or before `15c904c`.

### Code Changes

- [`shell.rs`](../native-ui/crates/exo-ui/src/shell.rs):
  - Removed `.press_on_down()` on `search-capsule` so holding pointer down shows the pressed capsule state without prematurely opening the command palette (`shell-bar-press-search#01`).
  - Matched React's top navigation tab horizontal padding (`12.5px` at `>=1260px`, `10.0px` below `1260px`) so `nav-capsule` spans `~629px` centered at `x=960` on 1920×1080 (`[645.4..1274.6]`), aligning 1:1 with React (`~628–636px`, `x=645..1273`).
  - Changed the Capture camera green dot condition from `l.replay` to `l.buffering`, matching React `CaptureButton.tsx:74` (`shell-capture-popover#03`).
  - Kept the active room nav pill active when Captures sheet is open (`s.room == room`) and extended `has_modal_scrim` with `s.media.captures_open || s.discord.settings_open || s.setup.beat.is_some()` so the top bar dims behind modal sheets identically to React (`shell-capture-sheet#05`, `discord-settings-sheet#03`).
  - Rendered `setup::tour_layers` in the top modal layer above `top_bar` and added 3 regression unit tests (`search_capsule_press_does_not_open_palette_until_release`, `nav_capsule_centered_at_react_width_on_1080p`, `tour_modal_opens_centered_nine_step_welcome_and_steps_through_rooms`).
- [`setup.rs`](../native-ui/crates/exo-ui/src/setup.rs):
  - Replaced the 3-step top-left interactive coachmark with React's 9-step `ExoTour` (`TOUR_STEPS[0..9]`, `tour_layers`, `tour_next`, `tour_back`, `tour_go`): step 0 renders the centered 408px `Welcome to Exo` (`1 / 9`) modal over a full-screen scrim with badge icon, close `X`, body, hint, 9 clickable pagination dots, `Skip`, and white `Start tour >` button; steps 1–8 switch rooms and anchor below each target control with shortcut pills, `Back`, `Next >` / `Done` (`shell-tour-modal#01`, `shell-tour-modal#02`).
- [`main.rs`](../native-ui/crates/exo-ui/src/main.rs), [`palette.rs`](../native-ui/crates/exo-ui/src/palette.rs), [`state.rs`](../native-ui/crates/exo-ui/src/state.rs):
  - Fixed `PARITY_SCENES` `"palette"` and `"captures"` to keep `Room::Library` active instead of switching to `Room::Settings` (`shell-palette-open#01`, `shell-palette-query#01`, `shell-capture-popover#02`).
  - Auto-focused `"palette-input"` on palette scene startup (`palette_needs_focus` in `poll_state` + `run_parity_probe` + `handle_palette_key` fallback) and aligned all 13 React `PC_SECTIONS` and 6 `SETTINGS_PAGES` search keywords so typing `'a'` filters 1:1 with React (`shell-palette-query#02`).
  - Emitted `"tourStep": rt.state.setup.beat` in `--parity-probe` `stateSummary` so `expect: { tourOpen: true }` passes, and wired `Key::Left` for `tour_back()`.
  - Defaulted `MiniBarSlot` to `Compact` so frame 0 on 1280px viewports never overlaps the center nav capsule.
- [`key_modal.rs`](../native-ui/crates/exo-ui/src/key_modal.rs):
  - Matched React `ActivateKeySheet.tsx` `.exo-field`: 32px pill-shaped input (`RADIUS_PILL`) with no leading key icon, 420px card width, 13px semibold title (`type_section()`), removed extra footer hairline divider, and matched disabled cursors (`Copy key` = `Default`, `Open <Store>` = `NotAllowed`) (`shell-activate-key-modal#04`).
- [`music.rs`](../native-ui/crates/exo-ui/src/music.rs):
  - Adjusted inline/medium mini-player widths (`lcd_w = 132.0 / 80.0`, `vol_w = 52.0`) to maintain `>=12px` clearance from the widened 629px center nav pill at 1920px and 1480px.

### Vision Parity Per-Item Disposition (WP3 Open Blockers & Majors)

- **Fixed (WP3 blockers, 6/7)**:
  - `shell-bar-press-search#01`: **FIXED** — pixel diff `26.18 -> 5.65`, **PASS** (score 0, 11/11 cursors, 9/9 glass).
  - `shell-tour-modal#01`, `shell-tour-modal#02`: **FIXED** — `opened: false -> true`, pixel diff `22.59 -> 2.55`, **PASS** (score 0, 20/20 cursors, 10/10 glass).
  - `shell-palette-open#01`: **FIXED** — Library room behind palette, pixel diff `3.61`, 20/20 cursors, 10/10 glass, 0 missing items.
  - `shell-palette-query#01`, `shell-palette-query#02`: **FIXED** — Library room behind palette + focused input filtering `'a'` across `Go to`/`Exo AI`/`Settings`/`Library`, pixel diff `3.69`, 20/20 cursors, 10/10 glass, 0 missing items.
- **Verified Present / Compare-Sheet Label Occlusion Artifact (1 blocker + 6 majors)**:
  - `apps-installed#01` (blocker), `library-all-more-popover#01`, `settings-general#03`, `discord-dm#01`, `devices-keyboard#01`, `discord-channel-context-menu#01`, `discord-dm-context-menu#03`: Verified on raw `apps-installed.png` and `apps-installed.tree.json` that `search-capsule` renders crisply at `[10, 8, 200, 36]` (`apps-installed` scored **PASS**, score 0, 10/10 cursors, 3.80 pixel diff). The Vision QA false positive was caused by `tools/parity/compare.mjs` drawing the `"RUNE"` label badge directly over `x=0..100, y=0..36` of the top-right quadrant on 50%-scaled compare sheets.
- **Fixed (WP3 majors)**:
  - Top nav pill width/padding/centering across all rooms (`library-homedeck#02`, `shell-bar-hover-nav#01`, `shell-bar-hover-search#01`, `devices-overview#01`, `apps-installed#02`, `store-discover#02`, `settings-ingame#04`, `library-all-more-popover#02`, `library-all-sort-dropdown#05`, `library-homedeck-more-popover#02`, `library-homedeck-card-menu#01`, `library-homedeck-card-hover#01`, `library-homedeck-search#01`, `library-homedeck-card-press#01`, `library-all-card-menu#05`, `game-page-hover-primary#04`, `library-capture-click#02`, `library-all-add-popover#02`): **FIXED** — `nav-capsule` now measures `~629px` centered at `x=960` (`645.4..1274.6`). `shell-bar-hover-nav`, `shell-bar-hover-search`, `shell-bar-press-search`, and `apps-installed` all score **PASS (0)**.
  - `shell-capture-popover#02`, `shell-capture-popover#03`: **FIXED** — Library room active with white pill, no spurious green dot when `buffering == false` (pixel diff `5.75`, 2/2 menus, 10/10 glass; remaining hover cursor mismatch is in WP8 `captures.rs` dismiss scrim).
  - `shell-capture-sheet#05`, `discord-settings-sheet#03`: **FIXED** — active nav pill stays lit and dimmed behind `top-bar-scrim`.
  - `shell-activate-key-modal#04`: **FIXED** — pill input with no leading icon, 420px card, no footer divider, pixel diff `6.16 -> 5.85`, **PASS** (score 0, 17/17 cursors).
  - `shell-palette-open#04`: Verified `palette_results()` filters out `local:add` and sorts alphabetically; live host `2XKO` entry comes from the host `games.list` payload on the test machine, 0 missing items, pixel diff `3.61`.
- **Attributed to Other WPs (not modified in WP3)**:
  - `pc-display-popover#01` (WP9 `pc.rs`), `pc-exo-ai#02` (WP9 `pc_ai.rs`), `store-search#02/#03` (WP5 `store.rs`), `store-game-page#02` (WP5 `store_page.rs`), `discord-home#02` & `discord-server-channel#02` (WP6 `discord.rs`), `devices-keyboard#03` (WP10 `pc_devices.rs`), `shell-bar-hover-nav#04` (WP2 `library.rs`).

### Live Parity Results (1920×1080 scale 1, `node tools/parity/run.mjs`)

| State | Open (R/N) | Cursors | Glass | Pixel Diff (Before -> After) | Verdict |
|---|---|---|---|---:|---|
| `shell-tour-modal` | Y/Y | 20/20 | 10/10 | 22.59 -> **2.55** | **PASS (0)** |
| `shell-bar-press-search` | Y/Y | 11/11 | 9/9 | 26.18 -> **5.65** | **PASS (0)** |
| `shell-activate-key-modal` | Y/Y | 17/17 | 10/13 | 6.16 -> **5.85** | **PASS (0)** |
| `shell-bar-hover-nav` | Y/Y | 10/10 | 9/9 | 5.76 -> **5.61** | **PASS (0)** |
| `shell-bar-hover-search` | Y/Y | 11/11 | 9/9 | 5.76 -> **5.61** | **PASS (0)** |
| `apps-installed` | Y/Y | 10/10 | 3/2 | — -> **3.80** | **PASS (0)** |
| `shell-palette-open` | Y/Y | 20/20 | 10/10 | — -> **3.61** | 0 missing items (virtualized list count 52 vs DOM slice 19) |
| `shell-palette-query` | Y/Y | 20/20 | 10/10 | — -> **3.69** | 0 missing items (virtualized list count 54 vs DOM slice 19) |
| `shell-capture-popover` | Y/Y | 0/10 (WP8 scrim) | 10/10 | — -> **5.75** | 2/2 menus, Library active, no replay dot |
| `shell-capture-sheet` | Y/Y | 13/19 (WP8) | 10/5 (WP8) | — -> **7.78** | Library active pill dimmed under scrim |
| `settings-general` | Y/Y | 11/20 (WP11) | 12/6 (WP11) | — -> **3.90** | Search capsule verified at `[10, 8, 200, 36]` |

### Vision Parity Delta Gates (all exit 0)

- `cargo fmt --all -- --check`: **PASS** (0)
- `cargo clippy --workspace -- -D warnings`: **PASS** (0)
- `cargo test --workspace`: **PASS** (**146 passed, 0 failed, 1 ignored**: `exo-kit` 4/4, `exo-rpc` 22/22 + 1 ignored, `exo-ui` 120/120)
- `cargo build --release -p exo-ui`: **PASS** (0)
- `node tools/check-comments.mjs`: **PASS** (`Comment ratchet ok: 2384 legacy files (73978 comment lines), 232 zero-comment files`)
- `node --test tools/parity/parity.test.mjs`: **PASS** (8/8)

### 70-State Vision Parity Update (`origin/UhhErix/parity-vision-qa` @ `1478414`)

Verified all 38 newly added WP3 entries (1 new blocker, 15 new majors, 22 new minors) from the 70/70 vision-parity update against `6c30db6`:

- **New blocker (`discord-voice-call#06`) + new Search/Tools majors (`discord-voice-call#07`, `music-sort-dropdown#04/#05`, `music-album#03`, `music-track-menu#03`)**:
  - Verified directly on `exo-ui --scene discord-voice` and `--scene music-songs` PNG + `.tree.json`: `search-capsule` renders at `[10, 8, 200, 36]`, `titlebar-tools` renders at `[1717, 8, 193, 36]` with `top-capture-btn` (`[1720, 12, 32, 28]`), `nav-settings` (`[1756, 11, 34, 30]`), and window controls (`opacity: 1`). No occlusion on raw frames (compare-sheet `"RUNE"` label artifact / pre-WP3 `526b2df` baseline).
- **New `shell-capture-settings#03` (major)**:
  - **FIXED in `6c30db6`** — verified on `exo-ui --scene captures-settings`: `top-bar-scrim` (`[0, 0, 1920, 52]`) dims the top bar under the modal, `nav-active-pill` stays lit on `Library` (`[648.39, 11, 90.16, 30]`), and the capture camera green dot is gated on `l.buffering` (absent on live host when not buffering).
- **New top-nav centering/width majors (`store-game-media-tab#05`, `music-track-menu#05`, `music-album#05`, `game-page-props-anticheat#04`, `devices-audio#06`, `devices-mouse#04`) + new capture-dot / nav minors**:
  - **FIXED in `6c30db6`** — `nav-capsule` renders at `[645.39, 8, 629.22, 36]` (centered at `x=960.0`) across every room and `replay_on` checks `l.buffering` instead of `l.replay`.
- **Non-WP3 items among the new majors**:
  - `devices-controller#03`, `devices-mouse#03` (Devices subheader `Rescan` pill): WP10 `pc_devices.rs`.
  - `music-immersive#05` (Immersive close button): WP7 `music.rs`.



