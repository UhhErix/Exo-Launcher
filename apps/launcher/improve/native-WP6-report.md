# WP6 Report — Native Discord Chat Parity

Branch: `UhhErix/native-WP6` (based on `origin/feat/native-polish-2`)
Rune fork pin: `exo-inputs` @ `e6c0716a660a97576b3e3951973c599b82bd11e7` (all 8 pins updated in isolated commit `f116b9a`)

## Summary

All **16 / 16** WP6 feature-matrix items (`discord-03`, `discord-08`, `discord-09`, `discord-10`, `discord-11`, `discord-12`, `discord-14`, `discord-15`, `discord-16`, `discord-17`, `discord-18`, `discord-20`, `discord-35`, `discord-41`, `discord-43`, `discord-44`) are implemented in `apps/launcher/native-ui` against the same `host-v1` RPC methods and push events as the public React reference, along with WP11 Discord RPC/push-event parity (`discord.signIn` auto-start/cancel, `friends.badge`, `discord.clientSettings`, `discord.home`, and all 8 newly registered Discord/Friends push events via `events.rs`).

## Item-by-Item Status & Verification

| ID | Feature | Status | Native Refs | RPC / Events | Verification |
|---|---|---|---|---|---|
| `discord-03` | Create DM / group DM (select friends, 10 limit) | **DONE** | `exo-ui/src/discord_threads.rs:100`, `discord_chat_state.rs` | `discord.channel` (`action: "openDm"`) | Unit test `create_dm_enforces_nine_friend_limit`; 1920×1080 snap `discord-create-dm.png` inspected. |
| `discord-08` | Message rendering: markdown, mentions, embeds, attachments | **DONE** | `exo-ui/src/discord_chat.rs:1129`, `exo-ui/src/discord_md.rs:1` | `discord.invite`, `discord.gift`, `spotify.link`, `discord.colors` | Unit test `parses_bold_code_strike_spoiler_and_mentions` (incl. `<@&role>`, `<t:unix:style>`, `-# ` subtext), `extracts_invites_gifts_and_spotify_jam_links`; 1920×1080 snaps `discord-server.png`, `discord-messages.png`. |
| `discord-09` | Composer: send, reply, edit, typing, multi-line `auto_grow`, file drop/upload, spoiler/alt/Modify, upload-as-file | **DONE** | `exo-ui/src/discord_chat.rs:1870`, `discord_threads.rs` | `discord.send`, `discord.typing`, `discord.upload` | Unit test `expression_picker_and_attachment_modify_and_escape_order`, `discord_chat_message_launch_motion_gate` (both `reduce_motion=false/true`); `virtual_cols` pending upload strip; 1920×1080 snaps. |
| `discord-10` | Emoji picker, GIF picker, sticker picker | **DONE** | `exo-ui/src/discord_pickers.rs:180`, `discord_chat_state.rs` | `discord.emojis`, `discord.emojiFavorites`, `discord.gifs`, `discord.gifFavorites`, `discord.stickers`, `discord.sticker` | Unit test `expression_picker_and_attachment_modify_and_escape_order`, `discord_chat_message_launch_motion_gate`; 1920×1080 snap `discord-picker.png`. |
| `discord-11` | Reactions: add, remove, super reactions, who reacted | **DONE** | `exo-ui/src/discord_pickers.rs:1542`, `discord_chat.rs` | `discord.react`, `discord.reactors`, `discord.reaction` event | Contract-checked RPC DTOs (`exo-rpc`), modal overlay with per-emoji tabs + moderator remove. |
| `discord-12` | Message menu (reply, edit, pin, copy, delete, report, forward, thread, bookmark, remind me, reactions, unread, remove embeds) | **DONE** | `exo-ui/src/menus.rs:1183`, `discord_threads.rs:1019` | `discord.pin`, `discord.delete`, `discord.report`, `discord.forward`, `discord.thread`, `discord.saved`, `discord.messageAction` | Full React-parity `ContextMenuTarget::DiscordMessage` with Remind Me submenu, Forward/New Thread/Report dialogs. |
| `discord-14` | Polls (create, vote, remove vote, end, results) | **DONE** | `exo-ui/src/discord_threads.rs:295`, `discord_chat_state.rs` | `discord.poll`, `discord.pollVote` push event (`events.rs`) | Unit tests `poll_creator_validates_and_submits_and_deeplink_parses`, `poll_vote_event_updates_counts_and_me_voted`; 1920×1080 snap `discord-poll.png`. |
| `discord-15` | Message search (filters, sort, pagination, jump) | **DONE** | `exo-ui/src/discord_search.rs:242`, `discord_chat_state.rs` | `discord.search` | Unit test `parses_inline_search_filters_and_builds_rpc_payload` (`from:`, `in:`, `has:`, `mentions:`, `before:`, `on:`, `after:`, `pinned:`, `authorType:`); 1920×1080 snap `discord-search.png`. |
| `discord-16` | Pins panel | **DONE** | `exo-ui/src/discord_search.rs:169` | `discord.pins`, `discord.pin` | Drawer fetch, jump-to-message, and unpin; 1920×1080 snap `discord-pins.png`. |
| `discord-17` | Inbox (`For you`, `Unreads` with peeks + mark read, `Mentions` with filter toggles + dismiss) | **DONE** | `exo-ui/src/discord_search.rs:550` | `discord.forYou`, `discord.unreads`, `discord.mentions`, `discord.markRead` | 5-tab Inbox drawer (`For you`, `Unreads`, `Mentions`, `Bookmarks`, `Reminders`); 1920×1080 snap `discord-inbox.png`. |
| `discord-18` | Saved messages (`Bookmarks` & `Reminders`) | **DONE** | `exo-ui/src/discord_search.rs:550`, `exo-ui/src/menus.rs:1183` | `discord.saved` | Tabs in Inbox + Bookmark / Remind me submenu in message context menu. |
| `discord-20` | User profile popover/card & full profile (banner, badges, bio, roles + role editor, connections, mutual friends/servers, note) | **DONE** | `exo-ui/src/discord_threads.rs:1536`, `discord.rs` | `discord.profile`, `discord.admin` (role assign/remove) | Member click + avatar/name click + DM sidebar "View full profile" + context menu Profile; unit test Escape dismiss; 1920×1080 snap `discord-profile.png` (caught & fixed `max_h` flex collapse). |
| `discord-35` | Invite dialog (search/invite friends, copy link, edit invite link settings) | **DONE** | `exo-ui/src/discord_threads.rs:686` | `discord.channel` (`action: "invite"`) | Server/channel context menus open Invite dialog; 1920×1080 snap `discord-invite.png`. |
| `discord-41` | Link rows / image & file viewer, `fileText`, image save, modal Lightbox & `virtual_cols` strips | **DONE** | `exo-ui/src/discord_chat.rs:2417`, `discord_chat.rs:232` | `discord.image`, `discord.fileText`, `shell.openUrl` | Wired Rune `Lightbox`/`LightboxState`/`LightboxItem` (prev/next gallery across channel images, zoom, drag-pan, Esc) + Copy/Save/Open original bar, right-click image menu, inline `fileText` preview + expand/collapse, `virtual_cols` horizontal strips for multi-image attachments and pending uploads; unit test `lightbox_gallery_navigation_zoom_and_escape`. |
| `discord-43` | Interactions (slash commands `/`, inline `CommandForm`, bot buttons/selects, `AppModal`, ephemeral dismiss) | **DONE** | `exo-ui/src/discord_pickers.rs:1094`, `discord_pickers.rs:1364` | `discord.apps`, `discord.interaction` push event (`events.rs`) | Unit tests `flattens_nested_slash_subcommands`, `event_registry_dispatches_all_wp6_push_events`. |
| `discord-44` | Mentionable autocomplete (`@user`, `@role`, `@everyone`/`@here`, `#channel`, `:emoji:`) | **DONE** | `exo-ui/src/discord_pickers.rs:815`, `discord_chat_state.rs` | `discord.mentionable` | Unit test `detects_active_mention_tokens_and_expands_mentions`. |

## WP11 RPC & Push-Event Parity

All WP6-scope Discord/Friends RPCs and push events from `rpc-parity.md` are typed in `exo-rpc` (verified against `host-v1.json` by `host_contract_contains_every_rust_rpc_method`) and registered in `crates/exo-ui/src/events.rs` (zero `state.rs` push-event match arms):

- **Methods wired:** `discord.signIn` (auto-started on signed-out Discord room + cancelled on room leave), `discord.clientSettings`, `discord.home`, `friends.badge`, `discord.emojis`, `discord.emojiFavorites`, `discord.gifs`, `discord.gifFavorites`, `discord.stickers`, `discord.sticker`, `discord.reactors`, `discord.poll`, `discord.search`, `discord.mentions`, `discord.forYou`, `discord.saved`, `discord.profile`, `discord.admin`, `discord.mentionable`, `discord.colors`, `discord.invite`, `discord.gift`, `discord.fileText`, `discord.image`, `discord.thread`, `discord.threads`, `discord.apps`, `discord.report`, `discord.forward`, `discord.messageAction`, `spotify.link`, `shell.openUrl`.
- **Push events registered in `events.rs`:** `discord.pollVote`, `discord.interaction`, `discord.colors`, `discord.open`, `discord.dmLine`, `discord.notify`, `discord.notifyClear`, `friends.badge`.

## Visual Parity & Motion Gate Before / After

Added 8 new native parity scenes in `crates/exo-ui/src/main.rs`: `discord-picker`, `discord-pins`, `discord-search`, `discord-inbox`, `discord-poll`, `discord-profile`, `discord-create-dm`, `discord-invite`.

All 11 Discord scenes were captured at `1920x1080` scale `1` via `exo-ui --scene <name> --snap ...` and visually inspected:
- **Issue caught & fixed (before → after):**
  1. `dc-expr-picker` initially pinned `(None, Some(12.0), None, Some(64.0))` (stretching top-to-bottom on the left) and triggered `motion_gate` pixel snaps on frame 109 due to fractional section-header heights during Rune `presence_anim` settle (`build.rs:249`). Fixed `.pin(None, None, Some(12.0), Some(64.0))` (bottom-right above emoji button), deterministic 10-column emoji rows, explicit integer header height `.h(16.0)`, and `SPRING_FLIGHT` on `send_with_launch` → **`discord_chat_message_launch_motion_gate` passes with 0 findings for both `reduce_motion=false` and `reduce_motion=true`**.
  2. `discord-profile` initially collapsed below the banner because `col().max_h(640.0)` contained `.grow(1.0).min_h(0.0).scroll(...)`. Fixed to explicit `.h(if dlg.full { 580.0 } else { 460.0 })` → full profile card (banner, avatar, Message/Add friend buttons, name/handle, note editor) renders cleanly in the re-snap.
  3. `discord-chat` timeline `feed` initially omitted `.grow(1.0).justify_end()`, floating short conversations to the top and shifting by `+85.8pt` under `reduce_motion=true`. Restored `.grow(1.0).justify_end()` and fixed demo DM key `"dm-ada"` → matches React bottom-anchored chat layout and passes `motion_gate`.
  4. `discord-invite` scene initially swapped `(guild_name, channel_id)` arguments. Fixed to `open_invite_dialog(&gname, &cid, Some("general"))` → re-snap shows `"Invite friends to Exo HQ"` / `"Recipients will land in #general"`.

### Temp-Copy Live Host Check Note
Attempted a live temp-copy host test copying `%LOCALAPPDATA%\ExoLauncher\app` and `%LOCALAPPDATA%\ExoLauncher` (excluding `app\`, `EBWebView`, and caches) to `%TEMP%` without touching the real install/data dir or closing the owner's Exo. The installed release `ExoLauncher.exe` is the public WebView2 build (predating `--host-pipe`), and building the worktree C# host (`ExoLauncher.csproj`) is gated by `BuildWebUi` requiring `ui/node_modules` and `mouse/node_modules` (absent in this worktree). Consequently, live signed-in `--host-pipe` round-trips against Discord servers were not exercised interactively in this worktree; every RPC method name/payload schema and push-event parser is contract-verified against `host-v1.json` in `exo-rpc` and unit-tested in `exo-ui`, and all 11 Discord UI scenes were visually verified at `1920x1080` scale `1`.

## Gates

All required gates ran after final edits and passed with exit code 0:
- `cargo fmt --check` — **PASS**
- `cargo clippy --workspace -- -D warnings` — **PASS**
- `cargo test --workspace` — **PASS** (`exo-kit` 4/4, `exo-rpc` 22/22 + 1 ignored bench, `exo-ui` 86/86 incl. all WP6 unit tests & motion gates)
- `cargo build --release -p exo-ui` — **PASS**
- `node apps/launcher/tools/check-comments.mjs` — **PASS** (0 comment-ban violations)

## Follow-Up Pass (Merge `feat/native-polish-2` + Vision-Parity Blockers & Majors)

### 1. Branch Integration (`d1aabf6` → `4d0c4a5`)
- Merged `origin/feat/native-polish-2` through `a78c217` (including WP1, WP4, WP5, WP9, WP11, perf branches, and `EventRegistry` in `crates/exo-ui/src/events.rs`) into `UhhErix/native-WP6`.
- Preserved `EventRegistry` and every handler from both sides (`register_shell`, `register_library`, `register_media`, `register_discord` incl. all 8 WP6 Discord/Friends push events, `register_pc`, `register_store`, `register_game`, `register_wp4_events`).
- Preserved all 8 Rune fork pins at `e6c0716a660a97576b3e3951973c599b82bd11e7` in `Cargo.toml` / `Cargo.lock`.
- Deduplicated `OpenUrlReply` (using `game_types::OpenUrlReply`) and `AppState::open_external_url` (using `game_actions.rs`).
- Preserved merged `feature-matrix.json` / `feature-matrix.md` counts (WP1 + WP4 + WP5 + WP9 + all 16 WP6 rows DONE: `DONE: 122, PARTIAL: 50, MISSING: 62, BROKEN: 4`, Discord `25/45`).

### 2. Vision-Parity Fixes (`origin/UhhErix/parity-vision-qa:apps/launcher/improve/vision-parity.md`)
- **`discord-dm` & `discord-dm-context-menu` active DM & preview (blockers):** Added `AppState::default_dm_id()` preferring the first pinned DM (`settings.ui_prefs["discordPinnedDms"]` + local overrides) over the first unpinned DM across `discord_state.rs` and `main.rs` preload; added `backfill_dm_line()` + `dm_preview_from_message()` and reversed `discord.messages` preload order in `main.rs` so pinned DM rows immediately display their last-message preview line; removed redundant `"Online"` subtext fallback from `dm_subtitle`.
- **`discord-dm` & `discord-dm-context-menu` right profile sidebar (blockers):** Replaced bare sidebar with `discord_threads::dm_profile_sidebar()` rendering the 104px banner art/accent band (supporting `accent_color`, `theme_colors`, `banner_color`, and `banner` image), overlapping 76px/68px circular avatar cutout, colored display name, `@username` + pronouns, badge chips, bio/activity, snowflake-derived `Member since`, `Connections`, inline editable `Note`, and clickable `Mutual servers` / `Mutual friends` rows; auto-fetched `discord.profile` on DM open and preloaded in `main.rs` `discord-messages`.
- **`discord-channel-context-menu` & `discord-server-channel` markdown, channel pills & embeds (OPEN blockers):** Added `render_markdown_with_channels()` / `parse_inline_with_channels()` in `discord_md.rs` parsing `[label](https://...)` into underlined link labels (no raw brackets/URLs) and `<#channel_id>` into resolved `#<channel-name>` dark mention pills; updated `embed_card` to parse markdown + channel mentions in both description and field values, render borderless soft-filled embed surfaces with 3px accent bar, and render `footer.icon_url` / `proxy_icon_url`; updated bot badge to `✓ App`.
- **Chat headers, timestamps, dividers, pinned notice, replies & composer (majors):**
  - DM header: inline name + status (`Offline`/`Online`/activity) on left + 4 icon-only buttons (`Phone`, `Video`, `Search`, `...`) on right.
  - Channel header: bolder `15px` channel title, `"{guild_name} · {topic}"` prefix, and icon-only `Search` + `...` buttons.
  - Message feed: 12-hour AM/PM timestamps (`time_short_12h`), `"Month D, YYYY"` date dividers (`day_label`), pinned-message system row (`type == 6`), reply preview with curved connector + 16px sender avatar, and suppressed spurious `"Welcome to #<channel>"` hero banner when messages exist.
  - Composer: `"Message @<name>"` / `"Message #<channel>"` placeholders (no ellipsis), smiley emoji picker button, and outlined dark circle upward-arrow send button (`icon_arrow_up`, solid white when ready).

### 3. Follow-Up Gates (Fresh)
- `cargo fmt --check` — **PASS** (exit 0)
- `cargo clippy --workspace -- -D warnings` — **PASS** (exit 0)
- `cargo test --workspace` — **PASS** (exit 0: `exo-kit` 4/4, `exo-rpc` 26/26 + 1 ignored live test, `exo-ui` 132/132 incl. new `default_dm_prefers_pinned_and_backfills_preview_and_formats_time` and link/channel markdown test)
- `cargo build --release -p exo-ui` — **PASS** (exit 0)
- `node apps/launcher/tools/check-comments.mjs` — **0 Rust / `native-ui` violations** (only the 12 pre-existing JSX baseline files from commit `526b2df` report stale ratchet entries because `ui/node_modules` is absent in this Orca worktree; `comments-ratchet.json` left untouched)
- Visual parity re-snaps (`discord-messages`, `discord-server`, `discord-profile`, `discord-picker` at 1920×1080 scale 1) — visually verified clean.
