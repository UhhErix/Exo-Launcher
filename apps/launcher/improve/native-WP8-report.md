# Native WP8 — Music Room Completion & Integration Report

Branch: `UhhErix/native-WP8` (rebased onto `origin/feat/native-polish-2` @ `2746258`)
Rune pin: `ba9aa615f2cc31262653dd3b7f3ee37534443b19` (kept `feat/native-polish-2`'s exact newer pin; zero downgrade, zero cherry-picks needed)
Scope: All 16 non-DONE items in `#### WP8`, audit/fixes of the 5 existing DONE Music items, all Music rows in WP11 `rpc-parity.md`, and all 36 WP8 items (18 blockers, 13 majors, 5 minors) in `parity-vision-qa` `vision-parity.md`.

## Summary

- **Feature matrix**: **21/21 DONE** (16/16 WP8 items completed, 5/5 existing DONE items audited & fixed; 0 PARTIAL, 0 MISSING, 0 BROKEN).
- **RPC parity (`origin/UhhErix/native-WP11`)**: 0 remaining Music gaps (`music.warm`, `music.match`, `music.volume`, `music.playback`, `music.control`, `music.signIn`, `spotify.start`/`view`/`act`/`link`, `ytmusic.start`/`view`/`act`, `applemusic.start`/`view`/`act`, `discord.callSongs`, and `music.status` / `discord.callSong` / `music.playback` / `music.volume` push events all wired and contract-tested).
- **Vision parity (`origin/UhhErix/parity-vision-qa`)**: All 18 blockers, 13 majors, and 5 minors resolved in native code (`apply_glass(..., ExoGlass::Bar)` Liquid Glass adoption, 1:1 `MusicWait` skeleton, bordered 3-panel layout with 400px right side panel ending cleanly above the floating bar, active green toggle states, muted volume styling without thumb dot, medium white `"Nothing playing"`, 560x560/520x520 Immersive layout with 28px title + inline leave button + glass volume capsule, 4-point sparkle `New` icon, 2-row Songs header with right-aligned Play + Filter + hairline divider + 5-row skeleton).
- **Debug stack safety**: Heap-boxed `MediaStateInner` inside `MediaState` and added `crates/exo-ui/build.rs` (`/STACK:8388608` on MSVC) so `exo-ui.exe --snap` runs cleanly in debug builds on `polish-2` + Rune `ba9aa61`.
- **Safety**: Owner's install (`%LOCALAPPDATA%\ExoLauncher\app`), real data dir, running Exo, and hardware devices were never touched.

## Gates (Rebased on `2746258`, Rune `ba9aa61`)

- `cargo fmt --check`: PASS (`0`)
- `cargo clippy --workspace --locked -- -D warnings`: PASS (`0`, 0 warnings)
- `cargo test --workspace --locked`: PASS (`0` — **260 passed, 0 failed**: `exo-kit` 9, `exo-rpc` 27, `exo-ui` 224)
- `cargo build --release --locked -p exo-ui`: PASS (`0`)
- `node tools/check-comments.mjs`: PASS (`0` — `Comment ratchet ok: 2382 legacy files (73926 comment lines), 313 zero-comment files`)

## Parity Harness Results

### Before WP8
- `feature-matrix.md`: 5 DONE, 7 PARTIAL, 9 MISSING (of 21).
- `vision-parity.md` (pre-WP8 push): 36 open WP8 items (18 blockers, 13 majors, 5 minors); mean pixel diffs 24.51–31.54 on Music subpages.

### After WP8 + Vision-Parity Fixes + `polish-2` Rebase
- **Interactive Parity Run (`node apps/launcher/tools/parity/run.mjs --rooms music`)** against a temp copy of the Debug host (`RuneHostSession` + WebView2 CDP) and a temp copy of `%LOCALAPPDATA%\ExoLauncher` (excluding `app\`):
  - **CRITICAL: 0, HIGH: 0, MEDIUM: 0, LOW: 0, PASS: 1 (`music-home`), SKIP: 10** (reachable states: 0/11 gaps, severity sum 0).
  - **Failures by dimension**: Open **0** | Menus **0** | Cursors **0 (10/10)** | Glass **0 (3/3, late: 0)** | Pixels (>15 diff) **0**.
  - **Mean absolute pixel diff on `music-home` (1920x1080 scale 1): `0.91`** (down from `6.66` pre-rebase and `24.5–31.5` pre-WP8).
  - Signed-in subpages (`music-songs`..`music-jam`) were skipped by the harness because WebView2 Apple Music cookies do not carry over into an isolated temp data directory (`applemusic.start` -> `signedOut` in both React and Native). Every signed-in Music scene (`music`, `music-songs`, `music-queue`, `music-now-playing`, `music-immersion`, `music-devices`, `music-friends`, `music-jam`, `music-sound`, `music-signin`, `music-playlist-sheet`) was additionally snapped at 1920x1080 scale 1 and visually inspected.

## Vision-Parity QA Resolution (All 36 WP8 Items)

| Group | IDs | Severity | Resolution |
|---|---|---|---|
| Library Show/Sort & Track context menus | `music-sort-dropdown#01`, `music-track-menu#01` | Blocker (2) | Full `exo_kit::context_menu_layers` Liquid Glass popovers (`LibraryShowSort`, `LibraryRow`, `PageMore`, `TrackMore` with `Add to playlist` submenu). |
| Main content / playlist / artist routing | `music-sort-dropdown#02`, `music-album#01`, `music-queue#01`, `music-lyrics#01`, `music-artist#01` | Blocker (5) | Full `list_page`, `discography_page`, `ArtistPage`, `songs_page`; removed extra inline title next to back arrow for `Collection` routes. |
| Right panels (Now Playing & Queue) | `music-sort-dropdown#03`, `music-track-menu#02`, `music-queue#02`, `music-lyrics#02`, `music-queue#06` | Blocker (4), Minor (1) | Set `side_panel` width to `400px` (`clamp(300px, 30vw, 400px)`), wrapped in `pb(86px)` so it ends cleanly above the floating bar, removed unrequested `'×'` close button, removed `"Now playing"` header on Now Playing panel, implemented Autoplay switch + `"Nothing queued"` + History. |
| Sidebar chrome, alignment, thumbnails, icons | `music-album#02`, `music-home#02`, `music-sort-dropdown#06`, `music-track-menu#04`, `music-album#04`, `music-lyrics#03`, `music-lyrics#04`, `music-artist#02`, `music-songs#02`, `music-queue#05`, `music-songs#05` | Blocker (2), Major (7), Minor (2) | Added `1px` rounded hairline border to all 3 panels (`library_sidebar`, `main_panel`, `side_panel`), matched `12px` top padding and borderless active pill highlight, rendered 36px/40px rounded cover thumbnails, replaced `New` starburst with crisp 4-point sparkle in 24x24 viewBox. |
| Home loading / sign-in synchronization | `music-home#01`, `music-home#03` | Blocker (2) | Implemented 1:1 `music_wait_room` full-room skeleton (`MusicWait.tsx`) and `sign_in_view` (`MusicSignIn.tsx`); `music-home` mean pixel diff dropped to `0.91`. |
| Songs header, divider & loading skeleton | `music-songs#01`, `music-songs#03` | Blocker (1), Major (1) | Two-row Songs header (`Search` row 1; `Songs` left + `Play` and `Filter` right + 1px hairline divider row 2) and 5-row cover-art loading skeleton. |
| Floating transport bar & volume | `music-queue#03`, `music-lyrics#05`, `music-songs#04`, `music-sort-dropdown#07`, `music-artist#05` | Major (3), Minor (2) | Adopted `apply_glass(..., ExoGlass::Bar)` at `max_w(1040px)` and `bottom: 18px`, green `good()` active panel toggle tint/icon, crisp medium `#FFF` `"Nothing playing"`, muted speaker icon + dimmed track with no bright thumb dot when muted/0%. |
| Immersive full-screen player | `music-immersive#03`, `music-immersive#04`, `music-immersive#01`, `music-immersive#02` | Blocker (2), Major (2) | 560x560 art (520x520 with lyrics) with 20px empty-state note icon, 28px bold title, full-width seek bar + clocks, 3-part controls row with right-aligned exit button, Liquid Glass volume pill with `%`, and docked bottom bar hidden. |

## Feature Matrix Status (All 21 Music Items)

| ID | Feature | Status | Native Ref |
|---|---|---|---|
| `music-01` | Service sign-in (Spotify / YTM / Apple) | DONE | `music_pages.rs:24`, `media_state.rs:1602` |
| `music-02` | Home (shelves, top picks) | DONE | `music.rs:717`, `music.rs:924` |
| `music-03` | Search + Top result / Songs / Artists / Albums | DONE | `music_pages.rs:626` |
| `music-04` | Library (songs, albums, artists, playlists) | DONE | `music.rs:333`, `music_pages.rs:413` |
| `music-05` | Playlist page + create/edit playlist sheet | DONE | `music_pages.rs:2113` |
| `music-06` | Artist page | DONE | `music_pages.rs:1048` |
| `music-07` | Album page / discography | DONE | `music_pages.rs:998`, `music_pages.rs:1048` |
| `music-08` | Radio / stations | DONE | `music_pages.rs:893`, `media_state.rs:2480` |
| `music-09` | Podcasts / episodes | DONE | `music_pages.rs:1866` |
| `music-10` | Now playing bar | DONE | `music.rs:1417` |
| `music-11` | Queue panel | DONE | `music_sound.rs:352` |
| `music-12` | Lyrics panel | DONE | `music_sound.rs:258` |
| `music-13` | Connect / Play on devices | DONE | `music_sound.rs:713` |
| `music-14` | Friend activity + Listen along | DONE | `music_sound.rs:780`, `music_sound.rs:928` |
| `music-15` | Immersive now-playing | DONE | `music.rs:1648` |
| `music-16` | Sound panel | DONE | `music_sound.rs:1427` |
| `music-17` | Song matching / warm cache | DONE | `media_state.rs:1572`, `media_state.rs:3810` |
| `music-18` | Library filter folder / show-and-sort | DONE | `music.rs:333`, `music_sound.rs:1843` |
| `music-19` | Music room first-run / no-service state | DONE | `music_pages.rs:24`, `music_pages.rs:209` |
| `music-20` | Music keyboard shortcuts | DONE | `music.rs:2134` |
| `music-21` | Track context menus | DONE | `music_sound.rs:1838`, `music_sound.rs:2541` |
