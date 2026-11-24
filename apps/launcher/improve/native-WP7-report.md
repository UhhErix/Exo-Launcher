# Native WP7 Report — Discord Voice, Calls, Servers, User & Server Settings

Branch: `UhhErix/native-WP7` (based on `origin/feat/native-polish-2`; Rune fork pinned to `ac59c6746f3619ed62b4eef307d16f15c4e7e5a0`).
Status: **All 20/20 WP7 items DONE** + all WP7 Discord RPC method and push-event parity rows closed.

## Summary of Changes

- **Rune pin bump** (commit `118f214`): pointed all 8 Rune workspace pins in `apps/launcher/native-ui/Cargo.toml` and `Cargo.lock` to `exo-inputs` commit `ac59c6746f3619ed62b4eef307d16f15c4e7e5a0`.
- **RPC & typed push-event parity (`exo-rpc`, `exo-ui/src/events.rs`)**:
  - Added typed DTOs and `USED_METHODS` constants for all 14 missing Discord/Friends methods (`discord.account`, `discord.admin`, `discord.callSongs`, `discord.colors`, `discord.font`, `discord.micMonitor`, `discord.micTest`, `discord.report`, `discord.shop`, `discord.sound`, `discord.thread`, `discord.threads`, `discord.userSettings`, `friends.badge`) and fixed `discord.soundboard` (`action: "list"` + multi-guild payload) and `GoLiveSource.game` (`Option<String>`).
  - Added typed `HostEvent` variants and registered handlers via `AppState::register_event` in `exo-ui/src/events.rs` (zero push-event arms added to `state.rs`) for all 15 missing push events: `discord.call`, `discord.callSong`, `discord.camera`, `discord.colors`, `discord.dmLine`, `discord.keybind`, `discord.micLevel`, `discord.notify`, `discord.notifyClear`, `discord.open`, `discord.tts`, `discord.userSettings`, `discord.videoKeyframe`, `discord.interaction`, `discord.pollVote`.
  - Matched React's startup and sign-in RPC sequence: `discord.voice({ action: "state" })`, `discord.stream({ action: "state" })`, `discord.clientSettings`, `friends.badge`, `discord.userSettings`, `discord.callSongs`, auto-starting `discord.signIn` when entering `Room::Discord` signed out and cancelling `discord.signIn({ cancel: true })` when leaving.
- **New modules**:
  - `apps/launcher/native-ui/crates/exo-ui/src/discord_settings.rs` — full 8-page User Settings modal (Account, Profiles, Voice & Video, Chat, Notifications, Privacy, Keybinds, Connected Apps), Custom Status modal, live mic meter/monitor, Streamer Mode, guild colors, and `discord.font` preload.
  - `apps/launcher/native-ui/crates/exo-ui/src/discord_admin.rs` — Forum/Threads view + post composer, Server Guide/Browse/Members pages, Events dialog, Channel Settings (Overview + tri-state Permissions), 23-section Server Settings modal, Shop catalog/collection/checkout, dynamic Report decision-tree modal, and member/channel/server admin dialogs.
- **Updated modules**: `discord_voice.rs`, `discord_voice_state.rs`, `discord.rs`, `discord_state.rs`, `menus.rs`, `shell.rs`, `events.rs`, `state.rs`, `main.rs`, and additive `exo-kit/src/menu.rs` (`context_menu_layers_live`).

## Item-by-Item Status (20/20 DONE)

| ID | Feature | Status | Native Ref | Verification |
| --- | --- | --- | --- | --- |
| `discord-13` | Threads and forum channels | DONE | `exo-ui/src/discord_admin.rs:2027` | Unit tests + `discord.threads`/`discord.thread` RPC wiring + sidebar active threads |
| `discord-21` | User menu (message, call, nickname, timeout, kick/ban, transfer ownership, report) | DONE | `exo-ui/src/menus.rs:1502` | `discord.admin('person')` auto-fetch on menu open + dialogs + unit tests |
| `discord-24` | Voice call stage (tiles, speaking ring, blurred avatar backdrop, Members column, fit-to-stage solo tile) | DONE | `exo-ui/src/discord_voice.rs:26` | `fit_tiles` unit test + live signed-in parity (`discord-voice-call`, `El::blur(36.0)` backdrop visually verified) |
| `discord-25` | Camera and screen share (2-step Go Live picker, preview, stream sound, Hide/Show preview, Change what you share) | DONE | `exo-ui/src/discord_voice.rs:658` | Live signed-in parity (`discord-voice-stream`: 11/11 cursors, 10.35 pixel diff, Step 1 anchored over Share) |
| `discord-26` | Stage channels (start/edit/end stage, request to speak, join stage, audience, move to audience) | DONE | `exo-ui/src/discord_voice.rs:1872` | StageBanner + StageAudience + speaker tile demote wired to `discord.voice` stage actions |
| `discord-27` | Soundboard + call songs | DONE | `exo-ui/src/discord_voice.rs:1124` | Live signed-in snapshot (`voice-soundboard.png`) + `discord.callSong` event unit test |
| `discord-28` | Mic test / monitor | DONE | `exo-ui/src/discord_settings.rs:1639` | `discord.micTest`/`discord.micMonitor` + `discord.micLevel` event unit test + `mic_advice` unit test |
| `discord-29` | Incoming-call ring / call UI over any room | DONE | `exo-ui/src/discord_voice.rs:2634` | `wp7_voice_island_incoming_call_golive_and_settings_render_cleanly` over `Room::Library` + `discord.call` event test |
| `discord-30` | Server menu (invite, create channel/category/event, notifications, privacy, per-server profile, move to, report, leave) | DONE | `exo-ui/src/menus.rs:1117` | `move_server_in_rail` + `server_section_visibility` unit tests + live signed-in menu |
| `discord-31` | Channel menu (mute, mark read, edit, delete, copy link, Set/Clear voice channel status) | DONE | `exo-ui/src/menus.rs:948` | Live signed-in parity (`discord-channel-context-menu`: 8/8 menu items, 18/19 cursors) |
| `discord-32` | Server settings (23 permission-gated sections) | DONE | `exo-ui/src/discord_admin.rs:1555` | `server_section_visibility_respects_permissions_and_community` unit test + `discord.admin` CRUD |
| `discord-33` | Channel settings (overview, slowmode, age-restricted, voice limits, tri-state permissions, delete) | DONE | `exo-ui/src/discord_admin.rs:1434` | `perm_tri_state_round_trips_allow_inherit_deny` unit test + `discord.admin` save/overwrite/delete |
| `discord-34` | Server pages (Server Guide, Events dialog, Browse channels, Members) | DONE | `exo-ui/src/discord_admin.rs:2305` | Live signed-in snapshot (`server-members.png`) + sidebar rows verified in `discord-voice-call` |
| `discord-36` | Shop (collectibles, bundles, payment, collection, equip/remove) | DONE | `exo-ui/src/discord_admin.rs:2754` | `format_shop_price` + `wp7_voice_island_incoming_call_golive_and_settings_render_cleanly` render check |
| `discord-37` | User settings: Account / Profile / Logged-in devices / Custom status | DONE | `exo-ui/src/discord_settings.rs:1187` | Live signed-in snapshot (`settings-account.png`) + `user_settings_patch_merges_partially` test |
| `discord-38` | User settings: Voice and video (devices, piecewise volumes, voice cleanup, NS/echo/AGC, sensitivity, Only my voice, PTT, attenuation, screen share) | DONE | `exo-ui/src/discord_settings.rs:1639` | `volume_curve_round_trips_unity_and_endpoints` + `mic_advice_and_speech_time_match_react` + full render check |
| `discord-39` | User settings: Notifications / Chat / Privacy / Keybinds / Connected apps | DONE | `exo-ui/src/discord_settings.rs:2311` | `keybind_label_orders_modifiers_first` + `keybind_event_toggles_streamer_mode` + full render check |
| `discord-40` | Report message/user/server (dynamic decision tree) | DONE | `exo-ui/src/discord_admin.rs:1868` | Breadcrumb traversal, multi-select, `free_text`, block/ignore/delete actions wired to `discord.report` |
| `discord-42` | Colors / font / Streamer mode | DONE | `exo-ui/src/discord_settings.rs:793` | `discord.colors` event test, `DisplayNameFont` -> `discord.font` preload, Streamer Mode banner & toggle |
| `discord-45` | Top-bar cursor over open menu | DONE | `exo-kit/src/menu.rs:351` | `context_menu_layers_live` + `El::modal_live(["top-bar"])`; verified by `wp7_top_bar_cursor_stays_pointer_over_open_discord_menus` AND 10/10 top-bar pointer cursor probes across all 3 menu states in live parity |

## Signed-In Parity Harness (Before vs After, 9 Discord States at 1920x1080 scale 1)

Run against a temp copy of signed-in `%LOCALAPPDATA%\ExoLauncher` data (excluding `app*`) with repo `ExoLauncher.exe` (`--host-pipe`) and signed-in React reference captures:

| Metric | Baseline (`parity-baseline-signedin`) | Prior (`room-discord-after-signedin-7`) | WP7 After (`exo-wp7-parity-signedin`) |
| --- | --- | --- | --- |
| CRITICAL | 6 | 1 | **0** |
| HIGH | 3 | 3 | **0** |
| MEDIUM | 0 | 5 | **9** |
| Open failures | 4 | 0 | **0** |
| Menu failures | 3 | 1 | **0** |
| Cursor failures | 8 | 4 | **3** |
| Glass failures | 9 | 0 | **0** |
| Pixel diff > 15 | 1 | 1 | **0** |
| Mean abs pixel diff | 12.87 | 11.87 | **10.58** |

Per-state breakdown (WP7 After):

| State | Open | Menus | Cursors | Glass | Pixel Diff | Severity |
| --- | --- | --- | --- | --- | --- | --- |
| `discord-home` | pass | n/a | 10/10 | pass | 9.47 | MEDIUM |
| `discord-status-menu` | pass | 10/10 | 20/21 (10/10 top-bar pass) | pass | 9.79 | MEDIUM |
| `discord-dm` | pass | n/a | 11/11 | pass | 13.23 | MEDIUM |
| `discord-dm-context-menu` | pass | 15/15 | 21/21 | pass | 12.34 | MEDIUM |
| `discord-server-channel` | pass | n/a | 11/11 | pass | 6.37 | MEDIUM |
| `discord-channel-context-menu` | pass | 8/8 | 18/19 (10/10 top-bar pass) | pass | 7.80 | MEDIUM |
| `discord-voice-call` | skip (React N / Native Y) | n/a | 11/11 | pass | 13.82 | MEDIUM |
| `discord-voice-stream` | skip (React N / Native Y) | n/a | 11/11 | pass | 10.35 | MEDIUM |
| `discord-settings-sheet` | pass | n/a | 16/21 | pass | 12.06 | MEDIUM |

Additional live 1920x1080 snapshots verified against the temp signed-in copy:
- `settings-account.png`: Discord settings modal opened via `dc-me-settings`, showing live signed-in user avatar, status, and all 8 settings sections.
- `server-members.png`: Server `Members` page opened via `dc-guild-members` with search, Check prune, and Prune controls.
- `voice-soundboard.png`: Voice stage solo tile with `El::blur(36.0)` blurred avatar backdrop and floating Soundboard popover with filter, mute/volume controls, and live loading state.

## Verification Gates

All gates executed fresh on final code:
- `cargo fmt --check`: exit 0
- `cargo clippy --workspace -- -D warnings`: exit 0
- `cargo clippy --workspace --tests -- -D warnings`: exit 0
- `cargo test --workspace`: exit 0 (`exo-kit` 4 passed, `exo-rpc` 22 passed / 1 ignored, `exo-ui` 90 passed — 116 passed total)
- `cargo build --bin exo-ui`: exit 0
- `cargo build --release -p exo-ui`: exit 0
- `node apps/launcher/tools/check-comments.mjs`: exit 0 (`2384 legacy files, 225 zero-comment files`)
- `node --test apps/launcher/tools/parity/parity.test.mjs`: exit 0 (8/8 passed)
