# Native WP8 — Music Room Completion Report

Branch: `UhhErix/native-WP8` (base `feat/native-polish-2`)
Scope: All 16 non-DONE items in `#### WP8` (`music-03`, `music-05`–`music-09`, `music-11`–`music-14`, `music-16`–`music-21`) plus verification and fixes across the 5 existing DONE Music items (`music-01`, `music-02`, `music-04`, `music-10`, `music-15`).

## Summary

- **Items completed**: 16/16 WP8 items (`DONE`), 5/5 existing Music items audited and fixed (`DONE`). Total Music matrix: **21/21 DONE** (0 PARTIAL, 0 MISSING, 0 BROKEN).
- **Blocked items**: None.
- **Rune pin**: Unchanged (`fc71db1a258f67537c65f418cafac872945fac4d`).
- **Owner install / real data dir**: Untouched; owner's running Exo (`pid 12468`) untouched.

## Gates

All required gates ran after the final edit and passed with exit code `0`:

- `cargo fmt --check`: PASS
- `cargo clippy --workspace --locked -- -D warnings`: PASS (0 warnings)
- `cargo test --workspace --locked`: PASS (108 passed, 0 failed: `exo-kit` 4, `exo-rpc` 22, `exo-ui` 82)
- `cargo build --release --locked -p exo-ui`: PASS
- `node tools/check-comments.mjs`: PASS (`Comment ratchet ok: 2384 legacy files (73885 comment lines), 225 zero-comment files`)

## Files Changed

- Owned Music modules:
  - `apps/launcher/native-ui/crates/exo-ui/src/media_state.rs` — full Music service adapter matrix, RPC clients, pagination, Apple ratings second-pass merge, Queue/Lyrics/Devices/Friends/Jam/Sound/Credits state, Listen Along (`discord.callSongs`, `discord.friends`, `music.match` 200-entry cache, drift sync), `music.warm`, `spotify.link`, sleep timer, crash-restart leash, and 5 new unit test suites.
  - `apps/launcher/native-ui/crates/exo-ui/src/music.rs` — full sidebar library (Show/Sort, Create menu, folder stack, inline folder naming, pin/row menus), Home empty state, transport bar (`previous` fix, SongVerdict heart + YTM dislike, `Play here`, YTM Song/Video toggle, drag/wheel volume slider, More panels), Immersive view (interactive seek + live synced Lyrics), mini-player hover warm + volume popover, full keyboard shortcut handler (`handle_key`), and shortcut/signed-out UI tests.
  - `apps/launcher/native-ui/crates/exo-ui/src/music_pages.rs` *(new)* — `sign_in_view`, `error_view`, `wait_view`, `music_wait_room` (1:1 React `MusicWait` skeleton), `change_service_button`, `songs_page`, `search_page`, `browse_page`, `discography_page`, `list_page` (Album/Playlist/Show/Artist pages with About/Biography/On tour/Featuring/Fans also like/Appears on/Discovered on/Artist's playlists), `track_row_full`, and `playlist_sheet_overlay` (JPEG cover upload `>=300x300`, name, description, YTM privacy).
  - `apps/launcher/native-ui/crates/exo-ui/src/music_sound.rs` *(new)* — all 7 right-hand panels (`NowPlaying` + Lyrics/Related, `Queue`, `Devices`, `Friends`, `Jam` + QR + `ListenAlong`, `Sound` + Spotify EQ/crossfade/exclusive/gapless/AutoMix/normalize/Smart Shuffle/mono + quality/speed/sleep timer, `Credits`) and all Music context menus (`music_menus_overlay` via `exo_kit::context_menu_layers`).
- Additive RPC & event plumbing:
  - `apps/launcher/native-ui/crates/exo-rpc/src/media_types.rs` — complete DTOs matching `ui/src/lib/player.ts` and `host-v1.json`.
  - `apps/launcher/native-ui/crates/exo-rpc/src/discord_types.rs` — additive `end`, `sync_id`, `uri` on `DiscordRich` for Listen Along (+ test initializer updates in `discord.rs` and `discord_state.rs`).
  - `apps/launcher/native-ui/crates/exo-rpc/src/types.rs` & `lib.rs` — `HostEvent::MusicStatus` / `DiscordCallSong`, `METHOD_MUSIC_MATCH` / `METHOD_MUSIC_WARM` / `METHOD_SPOTIFY_LINK` / `METHOD_DISCORD_CALL_SONGS` / `METHOD_SHELL_OPEN_URL` (verified by `host_contract_contains_every_rust_rpc_method`).
  - `apps/launcher/native-ui/crates/exo-ui/src/events.rs` — `HostEventKind::MusicStatus` and `DiscordCallSong` plus typed handlers (`on_music_playback_changed`, `on_music_status`, `on_discord_call_song`, `on_listen_along_presence`).
- Minimal shared hooks & parity states:
  - `apps/launcher/native-ui/crates/exo-ui/src/shell.rs` — `on_hover_change` `warm_music()` on Music nav tab and one-line `music::mini_volume_popover(s, budget.overlay)`.
  - `apps/launcher/native-ui/crates/exo-ui/src/state.rs` — fixed `set_music_service` to write `"exo.musicWebService"` (matching React) instead of `"musicService"`.
  - `apps/launcher/native-ui/crates/exo-ui/src/main.rs` — module declarations, `music::handle_key` in `on_key`, `s.music_tick(now)` in `poll_state`, live scene preloads and new scenes (`music-devices`, `music-friends`, `music-jam`, `music-sound`, `music-signin`, `music-playlist-sheet`), and `music_menu.is_some()` in `popup_menu_open` / `contextMenuOpen`.
  - `apps/launcher/tools/parity/states.mjs` — added `music-sound` and `music-jam` parity states.
  - `apps/launcher/improve/feature-matrix.md` & `feature-matrix.json` — updated all 21 Music rows to `DONE`.

## Per-Item Status & Verification

| ID | Feature | Status | Native Ref | Verification |
|---|---|---|---|---|
| `music-01` | Service sign-in (Spotify / YTM / Apple) | DONE | `music_pages.rs:24`, `media_state.rs:1602` | Fixed `exo.musicWebService` pref key, added `spotify.link` resolution, verified `signed_out_music_room_shows_signin_and_change_service` test + live temp-host `Sign in to Apple Music` capture. |
| `music-02` | Home (shelves, top picks) | DONE | `music.rs:717`, `music.rs:924` | Added `"Nothing on Home yet"` empty state and 1:1 `MusicWait` skeleton; verified `music_home_pills_equal_width_and_shelf_cadence` and 1920x1080 snap. |
| `music-03` | Search + Top result / Songs / Artists / Albums | DONE | `music_pages.rs:626` | Server filter pills + client kind chips, Top result + 4 Songs side-by-side, all groups (Artists/Albums/Playlists/Stations/Videos/Podcasts/Episodes/Audiobooks). |
| `music-04` | Library (songs, albums, artists, playlists) | DONE | `music.rs:333`, `music_pages.rs:413` | Fixed Apple `liked_name` (`"Favorites"`), added Songs page sort/filter/pagination + Apple ratings merge; verified `music_sidebar_labels_align_and_toolbar_shows_section_title` + `music-songs` snap. |
| `music-05` | Playlist page + create/edit playlist sheet | DONE | `music_pages.rs:2113` | Create/edit/rename/delete playlist sheet, JPEG cover upload (`>=300x300`), description, YTM privacy, track reorder/remove, Add to playlist submenu; verified `music-playlist-sheet` snap. |
| `music-06` | Artist page | DONE | `music_pages.rs:1048` | Header, top songs, Albums/Singles/Compilations, On tour, Featuring, Fans also like, Appears on, Discovered on, Artist's playlists, About/Biography. |
| `music-07` | Album page / discography | DONE | `music_pages.rs:998`, `music_pages.rs:1048` | Full Album/Playlist/Show/Discography pages with PageHeader, Show more pagination, Facts, track rows. |
| `music-08` | Radio / stations | DONE | `music_pages.rs:893`, `media_state.rs:2480` | Explore/New/Radio browse views with moods/stations and `*.act("radio")` station starts. |
| `music-09` | Podcasts / episodes | DONE | `music_pages.rs:1866` | Show/Podcast/Audiobook pages, Your Episodes library entry, episode dates, `Played` / `min left` badges, Save/Remove episode. |
| `music-10` | Now playing bar | DONE | `music.rs:1417`, `music.rs:2075` | Fixed `"previous"` control action (was `"prev"`), added SongVerdict (heart + YTM dislike), `Play here`, YTM Song/Video toggle, drag/wheel volume slider + mini-player volume popover (`mini_player_stays_inline_and_clears_nav_and_room_headers`, `phase4_music_mini_player_and_captures_theater`). |
| `music-11` | Queue panel | DONE | `music_sound.rs:352` | Now playing, Next in queue/Playing next with Move up/down/Remove/Clear, Autoplay toggle, Next up, History; verified `music-queue` snap. |
| `music-12` | Lyrics panel | DONE | `music_sound.rs:258` | Synced clickable lyrics with live clock, duet alignment, gap dots, YTM Lyrics/Related tabs in Now Playing panel + Immersive view; verified `lyrics_clock_and_jam_token_and_crash_leash` + `music-now-playing` snap. |
| `music-13` | Connect / Play on devices | DONE | `music_sound.rs:713` | `Play on` devices panel (`spotify.view("devices")`, `spotify.act("transfer")`), `Play here` button when playing elsewhere; verified `music-devices` snap. |
| `music-14` | Friend activity + Listen along | DONE | `music_sound.rs:780`, `music_sound.rs:928` | Friend activity (newest-first, clickable song/artist/context), Spotify Jam (Start/Join via `spotify.link`/Copy invite/QR/End confirm/Leave/guest switches/members), Discord Listen Along (`discord.callSongs` + `discord.friends` + `music.match` + drift sync); verified `listen_along_sync_and_matching_rules` + `music-friends`/`music-jam` snaps. |
| `music-15` | Immersive now-playing | DONE | `music.rs:1648` | Large art, clickable artists, interactive seek bar + clock, verdict/transport controls, volume slider, side-by-side live synced Lyrics, `F`/Escape/Up/Down shortcuts; verified `music-immersion` snap. |
| `music-16` | Sound panel | DONE | `music_sound.rs:1427` | Matches React `SoundPanel.tsx` 1:1 (`*.view("audio")`, `*.act("audio"/"speed")`, `music.control("pause")`): quality, exclusive mode, crossfade + slider, gapless, AutoMix, normalize, Smart Shuffle, mono, EQ + vertical band sliders, playback speed, sleep timer; verified `sleep_timer_and_mute_restore_behave_like_react` + `music-sound` snap. |
| `music-17` | Song matching / warm cache | DONE | `media_state.rs:1572`, `media_state.rs:3810` | Throttled `music.warm` on Music nav tab and mini-player hover, `music.match` with 200-entry cache for cross-service Listen Along, `spotify.link` URL resolver; verified by `host_contract_contains_every_rust_rpc_method` and unit tests. |
| `music-18` | Library filter folder / show-and-sort | DONE | `music.rs:333`, `music_sound.rs:1843` | Show & Sort menu, Create menu, folder stack navigation, inline folder create/rename, pin/unpin, move to folder; verified `share_urls_and_act_encoding_match_react_adapters`. |
| `music-19` | Music room first-run / no-service state | DONE | `music_pages.rs:24`, `music_pages.rs:209` | Full `MusicSignIn`, `NotPremium`, `ErrorState` (Try again + Change music service -> Settings > Music), 1:1 `MusicWait` skeleton, crash-restart leash (max 2 restarts / 60s); verified live against temp copy of signed-in data dir where Apple Music returned `signedOut` and Native immediately displayed `Sign in to Apple Music` + `Change music service`. |
| `music-20` | Music keyboard shortcuts | DONE | `music.rs:2134` | Full Spotify/Apple/YouTube/Immersive shortcut map wired in `main.rs::on_key`; verified `music_shortcuts_cover_spotify_youtube_and_immersive`. |
| `music-21` | Track context menus | DONE | `music_sound.rs:1838`, `music_sound.rs:2541` | Full `exo_kit::context_menu_layers` menus for TrackMore (with `Add to playlist` submenu), PageMore, LibraryRow, LibraryShowSort, LibraryCreate, MorePanels, SongsSort, SoundQuality/Speed/Sleep. |

## Parity Harness & Behaviour Verification

### Before WP8
- `feature-matrix.md`: 5 DONE, 7 PARTIAL, 9 MISSING (of 21).
- Live smoke reported: room sat on `"Starting Apple Music…"` with empty Home and `"Nothing playing"` instead of `MusicSignIn` / `MusicWait` with Change service; `set_music_service` persisted wrong pref key (`"musicService"` instead of `"exo.musicWebService"`); `music_control` sent `"prev"` instead of `"previous"`; no Artist/Discography/Podcasts/Lyrics/Devices/Friends/Jam/Sound/Shortcuts/Context menus.

### After WP8
- **Interactive Parity Run (`tools/parity/run.mjs --rooms music`)** against a temp copy of the Debug host (`RuneHostSession` + WebView2 CDP) and a temp copy of `%LOCALAPPDATA%\ExoLauncher` (excluding `app\`):
  - **CRITICAL: 0, HIGH: 0**; **Open failures: 0**, **Menu failures: 0**, **Cursor failures: 0 (10/10 and 11/11)**, **Glass failures: 0 (late: 0)**, **Pixels (>15 diff): 0**.
  - Mean pixel diff on `music-home` (1920x1080 scale 1): **6.66** (even comparing React's `MusicWait` frame against Native's resolved Apple Music `MusicSignIn` screen because the seeded data copy had `web-client-preferences.json` set to `"apple"` and Apple Music WebView2 returned `signedOut`).
  - Visual inspection of `react/music-home.png` vs native caught two polish items that were then fixed and re-verified:
    1. Added 1:1 `music_wait_room` full-room skeleton matching React's `MusicWait.tsx` while `MusicRoomState::Loading`.
    2. Softened `PlaylistSheet` input placeholders from `fg_muted()` to `fg_faint()`.
- **1920x1080 scale-1 scene snaps verified visually**: `music`, `music-songs`, `music-queue`, `music-now-playing`, `music-immersion`, `music-devices`, `music-friends`, `music-jam`, `music-sound`, `music-signin`, `music-playlist-sheet` — all 11 rendered cleanly with zero layout overlap, crisp Void black `#000` background, and bounded Liquid Glass blur budget (`<= 4`).
- **Live host note**: In the temp copy of `%LOCALAPPDATA%\ExoLauncher`, `web-client-preferences.json` selects Apple Music (`"apple"`), and WebView2 cookies do not carry over as signed-in into an isolated temp data directory, so both React and Native enter the unsigned/wait flow (`applemusic.start` -> `signedOut` -> `Sign in to Apple Music` + `Change music service`). All signed-in pages, panels, RPC payload shapes, and state transitions were verified against `host-v1.json` (`host_contract_contains_every_rust_rpc_method`), unit tests, and 1920x1080 scene renders.
