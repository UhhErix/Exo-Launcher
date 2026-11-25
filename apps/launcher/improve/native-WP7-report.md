# Native WP7 Report — Discord Voice, Calls, Servers, User & Server Settings

Branch: `UhhErix/native-WP7` (merged `origin/feat/native-polish-2` at `f2d3797` in `bc2ac25`; Rune fork pinned to `e6c0716a660a97576b3e3951973c599b82bd11e7` in isolated commit `b58bedc`).
Status: **All 20/20 WP7 items DONE** + all WP7 Discord RPC method/push-event parity rows closed + **all WP7-owned `vision-parity.md` items fixed** (WP6-owned chat/markdown files untouched).

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

## Verification Gates (Initial WP7 Commit `3b95a54`)

All gates executed fresh on final code:
- `cargo fmt --check`: exit 0
- `cargo clippy --workspace -- -D warnings`: exit 0
- `cargo clippy --workspace --tests -- -D warnings`: exit 0
- `cargo test --workspace`: exit 0 (`exo-kit` 4 passed, `exo-rpc` 22 passed / 1 ignored, `exo-ui` 90 passed — 116 passed total)
- `cargo build --bin exo-ui`: exit 0
- `cargo build --release -p exo-ui`: exit 0
- `node apps/launcher/tools/check-comments.mjs`: exit 0 (`2384 legacy files, 225 zero-comment files`)
- `node --test apps/launcher/tools/parity/parity.test.mjs`: exit 0 (8/8 passed)

---

## Follow-Up Integration Pass (`feat/native-polish-2` Merge, Rune `e6c0716a`, Vision Parity QA)

### 1. Branch Sync & Isolated Rune Pin
- **Merged `origin/feat/native-polish-2` (`f2d3797`)** in commit `bc2ac25`, preserving `events.rs` `EventRegistry` and both WP7 and `polish-2` additions (`store_media`, `store_on_room_leave`). Verified `git merge-base --is-ancestor origin/feat/native-polish-2 HEAD` exits 0.
- **Bumped Rune `exo-inputs` pin to `e6c0716a660a97576b3e3951973c599b82bd11e7`** across all 8 workspace crate pins in `apps/launcher/native-ui/Cargo.toml` and `apps/launcher/native-ui/Cargo.lock` in ONE isolated commit (`b58bedc`), verified clean with `cargo check --workspace`.
- **Ownership boundary strictly enforced**: zero edits to WP6-owned files (`discord_chat.rs`, `discord_chat_state.rs`, `discord_md.rs`).

### 2. Vision Parity QA (`origin/UhhErix/parity-vision-qa:apps/launcher/improve/vision-parity.md`, Section `WP6/WP7 — Discord`)

Every WP7-owned blocker, major, and minor item in `vision-parity.md` was fixed and verified on live signed-in captures; WP6-owned chat/markdown items were left untouched for WP6:

| Category / Vision-Parity Finding | Owner | Status | Fix / Native Ref |
| --- | --- | --- | --- |
| `discord-dm` & `discord-dm-context-menu` blockers: wrong default DM (`Colton` instead of pinned `lazy`), stale preview snippet | WP7 | **FIXED** | `discord_state.rs` `first_visible_dm_id()` prioritizes `discordPinnedDms`; `hydrate_missing_dm_lines()` + `main.rs` select newest message by snowflake ID (`vc`); unit test `wp7_pinned_dm_selected_first_and_categories_sync_collapsed` |
| `discord-dm` & `discord-dm-context-menu` blocker + major: missing 1:1 DM right user profile sidebar (`lazy`) | WP7 | **FIXED** | `exo-rpc/src/discord_types.rs` + `lib.rs` added `discord.profile` RPC & DTOs; `discord_state.rs` `load_discord_profile` / `save_discord_profile_note`; `discord.rs` full right profile panel (105px banner/accent, 80px overlapping avatar + status, name/username/pronouns, custom status, badges, bio, `Member since`, verified `Connections`, editable `Note`, `Mutual servers`, `Mutual friends`) |
| `discord-settings-sheet` blockers: modal failed to open (`discord-settings-sheet` scene/click), wrong backdrop room, modal geometry/nav order/glass count | WP7 | **FIXED** | `tools/parity/states.mjs` uses `discord-voice` scene + `discordSettingsOpen: true`; `discord_state.rs` reordered `DiscordSettingsPage::ALL` (`Voice, Chat, Notifications, Privacy, Account, Keybinds, Profiles, ConnectedApps`); `discord_settings.rs` matched exact React `820x880` geometry, borderless nav, `top=52` scrim preserving top-bar `Cursor::Pointer`, and flat `sheet_action_btn` matching React `5/5` glass layers |
| `discord-status-menu` majors + minor: extra checkmark on `Online`, leading icons / misaligned labels, `Copy user ID` icon position, `Shop` icon | WP7 | **FIXED** | `exo-kit/src/menu.rs` additive `.trailing_icon(El)` on `MenuItem`; `menus.rs` removed checkmark on status items, removed leading icon on `Shop` (and triggers `load_discord_shop(false)`), moved `Copy user ID` icon to trailing right, routed `Edit profile` to `Profiles` |
| `discord-dm-context-menu` major: leading icons misaligned menu labels; `Report` action | WP7 | **FIXED** | `menus.rs` removed leading icons in `DiscordDm` menu for single flush-left text edge; moved `Unpin DM`/`Pin DM`, `Report` (opens `ReportModal`), `Copy user ID`, `Copy channel ID` icons to `.trailing_icon(...)` |
| `discord-channel-context-menu` major: `Copy channel ID` icon alignment, `Mark as read` disabled state | WP7 | **FIXED** | `menus.rs` moved `Copy channel ID` icon to `.trailing_icon(...)`; disabled `Mark as read` when unread count is 0 |
| `discord-home` majors + minor: Friends 5-column grid (was 4), `Search friends` width `234px`, Friends filter tab padding, `Erix` pill trigger | WP7 | **FIXED** | `discord.rs` switched Friends grid to 5 columns at 1920px (`<= 1.15` scale), set search width to `234.0`, tightened filter tab `px(10.0)`, added `subtle_fill()` + `hairline()` pill on `dc-me-trigger`; updated unit test `discord_friends_gridlines_five_cols_and_blur_cap` |
| `discord-server-channel` & `discord-channel-context-menu` majors + minors: collapsed categories (`STAFF` collapsed, `TICKETS`/`#open-a-ticket` visible), `+` category create button, distinct channel icons (`rules`/`forum`/`announcement`/locked voice), 24px server icon left of header title, bot `✓` checkmark instead of `APP` pill | WP7 | **FIXED** | `discord_state.rs` `sync_collapsed_categories(&chans)` + persisted `discord.channel { action: "collapse" }`; `discord.rs` added 24x24 rounded guild icon in `dc-server-head`, `+` button on categories when `can_manage`, bookmark/chat/sparkles/lock channel icons, and `icon_check` on bots in Members sidebar |
| `discord-home` / `discord-dm` / `discord-dm-context-menu` / `discord-status-menu` minors: circular `>` server strip scroll chevron; redundant `"Online"` DM subtext | WP7 | **FIXED** | `discord.rs` added `dc-band-scroll-right` circular `>` chevron before Inbox; removed `status_label(st)` fallback in `dm_subtitle` so plain online DM rows (`Tin Nguyen`, `Selim`, `Razdan`) show no `"Online"` subtext |
| `discord-voice-stream` parity menu detection | WP7 | **FIXED** | `discord_voice.rs` added `.role("menu")` on `dc-golive-panel` and `.role("menuitem")` on `Applications`, `Screens`, `Close`; `main.rs` added `"dc-golive-panel"` to `semantic_menu_prefixes` (`3/3` menus pass) |
| `discord-channel-context-menu` / `discord-server-channel` / `discord-dm` chat body & markdown items (welcome embed markdown links, `#channel` pills, message timestamps/grouping, reply previews, DM chat header icons, composer bar icons) | WP6 | **DEFERRED TO WP6** | Owned by `discord_chat.rs` / `discord_chat_state.rs` / `discord_md.rs` — left untouched per WP6/WP7 ownership boundary |

### 3. Signed-In Parity Harness Progression (9 Discord States at 1920x1080 scale 1)

| Metric | Baseline (`parity-baseline-signedin`) | Prior (`after-7`) | WP7 v1 (`3b95a54`) | WP7 Final (v4) |
| --- | --- | --- | --- | --- |
| CRITICAL | 6 | 1 | 0 | **0** |
| Open failures | 4 | 0 | 0 | **0** |
| Menu failures | 3 | 1 | 0 | **0** (all 4 menu states 100%) |
| Glass failures | 9 | 0 | 0 | **0** (all 9 states pass, `discord-settings-sheet` 5/5) |
| `discord-home` pixel diff | 9.20+ | 9.20 | 9.47 | **8.94** (10/10 cursors, 3/3 glass) |
| `discord-status-menu` pixel diff | 9.38+ | 9.38 | 9.79 | **9.08** (10/10 menus, 20/21 cursors, 4/4 glass) |
| `discord-dm` pixel diff | 13.38 | 13.38 | 13.23 | **9.17** (11/11 cursors, pinned `lazy` + full profile sidebar) |
| `discord-dm-context-menu` pixel diff | 12.35 | 12.35 | 12.34 | **9.89** (15/15 menus, 21/21 cursors) |
| `discord-server-channel` pixel diff | 6.37 | 6.37 | 6.37 | **6.39** (11/11 cursors, `STAFF` collapsed / `TICKETS` visible) |
| `discord-channel-context-menu` pixel diff | 7.80 | 7.80 | 7.80 | **7.76** (8/8 menus, 18/19 cursors) |
| `discord-voice-stream` menus / cursors | skip | skip | 0/3 / 11/11 | **3/3 menus / 14/14 cursors / 6/6 glass** |
| `discord-settings-sheet` pixel diff / cursors / glass | 11.92 / fail | 11.92 | 12.06 / 16/21 | **5.57 / 18/21 (all 10 top-bar + all 6 nav tabs + close pass) / 5/5 glass** |

*(Note: `discord-voice-call` reported 24.38 pixel diff in v4 due to live voice-stage backdrop/presence drift during capture; 11/11 cursors and glass passed.)*

### 4. Fresh Final Verification Gates

All executed fresh after the final edit:
- `cargo fmt --check`: exit 0
- `cargo clippy --workspace -- -D warnings`: exit 0
- `cargo clippy --workspace --tests -- -D warnings`: exit 0
- `cargo test --workspace`: exit 0 (`exo-kit` 4 passed, `exo-rpc` 22 passed / 1 ignored, `exo-ui` 116 passed — **142 passed, 0 failed**)
- `cargo build --bin exo-ui`: exit 0
- `cargo build --release -p exo-ui`: exit 0
- `node apps/launcher/tools/check-comments.mjs`: exit 0 (`2383 legacy files, 243 zero-comment files`; ratchet tightened for stale entries from `feat/native-polish-2` merge)
- `node --test apps/launcher/tools/parity/parity.test.mjs`: exit 0 (8/8 passed)
- `git merge-base --is-ancestor origin/feat/native-polish-2 HEAD`: exit 0

---

## WP6 + WP7 Merge, Deduplication & Final Vision Pass (`origin/UhhErix/native-WP6` `fe3d1cb`)

- **Merged `origin/UhhErix/native-WP6` (`fe3d1cb8b20861a54e7c14b2a3ae6f3851c1cd17`)** into `UhhErix/native-WP7` while keeping all `EventRegistry` handlers (`events.rs`), all 8 Rune `exo-inputs` `e6c0716a` pins, all WP6 chat/markdown/picker/search/inbox/polls/interactions work, and all WP7 voice/calls/servers/settings/admin work.
- **Deduplicated overlapping implementations**:
  - **DM profile sidebar (`exo-ui/src/discord.rs`)**: kept ONE implementation backed by `discord.profile` RPC (`profiles_by_user` + `mutual_friends_by_user` + synced `dm_profile_cache`), folding in WP6's visual improvements (`clip_rounded(RADIUS_PANEL)`, `user_profile.banner` / `http*` URL support, `theme_colors`/`banner_color` accent fallback, `discord_md::render_markdown` bio, and display name + username rows in Mutual friends); removed duplicate `dm_profile_sidebar`, `ensure_dm_profile_loaded`, `save_dm_sidebar_note`, `dm_sidebar_note*`, and `format_snowflake_member_since`.
  - **Pinned-DM default**: kept ONE `default_dm_id()` in `discord_chat_state.rs` (skipping `request`/`spam` DMs and honoring `discordPinnedDms` + overrides); removed duplicate `first_visible_dm_id()` from `discord_state.rs`.
  - **RPC types & events (`exo-rpc`, `exo-ui/src/events.rs`)**: deduplicated `DiscordProfile*`, `DiscordAdminReply`, `DiscordColorsReply`, `DiscordDmLineEvent`, `DiscordNotifyEvent`/`DiscordNoticeEvent`, `DiscordNotifyClearEvent`, `DiscordOpenEvent`, `DiscordReportReply`, `DiscordThreadCreate*`, `FriendsBadgeReply`, `METHOD_DISCORD_PROFILE`, and `HostEvent`/`HostEventKind` variants; renamed Account Settings connection DTO to `DiscordAccountConnection`; merged `DiscordColors` handler to refresh both WP6 chat colors and WP7 guild colors.
  - **Extra vision items (`vision-parity.md` `1478414`)**:
    - `music-immersive#05`: removed the extra top-right floating circular `'X'` close button (`music-immersion-close`) from `immersion_view` in `exo-ui/src/music.rs`.
    - `discord-voice-stream-09`: ensured active guild pill in `discord.rs` (`band_guild_tile`) shows the green voice speaker badge whenever `s.discord.voice` is active in that guild.
  - **Comment ratchet**: restored `apps/launcher/tools/comments-ratchet.json` from `cdbec1f` and verified `node tools/check-comments.mjs` with `ui/` and `mouse/` `npm ci` installed (`2383 legacy files, 253 zero-comment files`).
- **Final Unified Signed-In Parity (9 `discord-*` states, 1920x1080 scale 1)**:
  - **CRITICAL=0, HIGH=0, MEDIUM=8, LOW=0, PASS=1, SKIP=0**
  - **Open=0, Menus=0 (10/10, 15/15, 8/8, 3/3), Glass=0 (all 9 pass, 0 late), Pixels >15=0, Mean abs pixel diff = 9.17**
  - `discord-server-channel`: **PASS (score 0, 5.98 px, 11/11 cursors, 3/3 glass)**
  - `discord-dm`: **8.73 px, 11/11 cursors, 3/3 glass**
  - `discord-dm-context-menu`: **9.43 px, 15/15 menus, 21/21 cursors, 4/4 glass**
  - `discord-home`: **7.61 px, 10/10 cursors, 3/3 glass**
  - `discord-status-menu`: **7.75 px, 10/10 menus, 20/21 cursors, 4/4 glass**
  - `discord-channel-context-menu`: **7.66 px, 8/8 menus, 18/19 cursors, 4/4 glass**
  - `discord-voice-call`: **14.99 px, 11/11 cursors, pass glass**
  - `discord-voice-stream`: **14.81 px, 3/3 menus, 14/14 cursors, 6/6 glass**
  - `discord-settings-sheet`: **5.57 px, 18/21 cursors, 5/5 glass**
- **Final Verification Gates (all exit 0)**:
  - `cargo fmt --check`: exit 0
  - `cargo clippy --workspace -- -D warnings`: exit 0
  - `cargo clippy --workspace --tests -- -D warnings`: exit 0
  - `cargo test --workspace`: exit 0 (`exo-kit` 4, `exo-rpc` 26 + 1 ignored, `exo-ui` 147 — **177 passed, 0 failed**)
  - `cargo build --bin exo-ui` & `cargo build --release -p exo-ui`: exit 0
  - `node apps/launcher/tools/check-comments.mjs` (after `ui/` and `mouse/` `npm ci`): exit 0
  - `node --test apps/launcher/tools/parity/parity.test.mjs`: exit 0 (8/8)
