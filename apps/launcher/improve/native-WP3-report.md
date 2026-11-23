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
