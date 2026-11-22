# Round 3, Lane P1 Report — Music RPC Fast Path & Layout Polish

Branch: `UhhErix/r3-music-fast` (base: `feat/rune-1to1-visual-parity` @ `bc7812a`)

---

## 1. Live Signed-In Playing Apple Music Benchmark (Before vs After)

### Methodology & Slow-Path Reproduction
The 460–500 ms `music.control` latency seen in the owner's `app.log` **only occurs on a signed-in, actively playing Apple Music web player (`Source == "page"`)**. When no track is loaded (`status == "none"`), `MediaSessionService.ControlAsync` returns `ok = false` in ~0.14 ms without touching WebView2 CDP.

To measure the real slow path identically on both builds (`docs-audit/bench-music-live.mjs`):
1. Launch Release `ExoLauncher.exe --host-pipe=<name>` (Before = `feat/rune-1to1-visual-parity` `bc7812a`; After = `UhhErix/r3-music-fast`).
2. Call `applemusic.start` until `state == "running"`.
3. Mute immediately via `music.volume { level: 0, muted: true }`.
4. Verify signed-in room views load (`home`, `library`, `recent`), then start silent playback on playlist `Replay All Time` (`am:playlists:pl.rp-yzCVg7AMvb`) via `applemusic.act { action: "play", uri }` and wait until `music.playback` reports a live Apple Music track (`SUKI` by `Eric Reprid`).
5. Time **30× `music.control` (play/pause)**, **30× `music.control` (`next`)**, **30× `music.volume` (`muted: true, level: 0`)**, and **10× `applemusic.view`** across all 6 views (`player`, `home`, `library`, `recent`, `queue`, `status`).

### Before vs After Table (Release Host, Signed-In Playing Apple Music)

| RPC Method | Iters (ok) | Before p50 (ms) | After p50 (ms) | Delta p50 | Before p95 (ms) | After p95 (ms) | Delta p95 | Before max (ms) | After max (ms) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `music.control` (`next`) | 30 (30/30) | **441.48** | **0.66** | **-99.85%** (669×) | **661.57** | **1.82** | **-99.72%** (364×) | 665.90 | 1.83 |
| `music.control` (`play/pause`) | 30 (30/30) | **12.48** | **0.70** | **-94.39%** (17.8×) | **20.52** | **3.39** | **-83.48%** (6.1×) | **1593.60** | **7.34** |
| `music.volume` (`muted=true, level=0`) | 30 (30/30) | 0.90 | 0.83 | -7.8% | 1.95 | 1.07 | -45.1% | 3.16 | 1.17 |
| `applemusic.view` `status` | 10 (10/10) | 1.68 | **0.38** | **-77.4%** (4.4×) | 2.12 | 3.36 | +58.5%* | 2.12 | 3.36 |
| `applemusic.view` `player` | 10 (10/10) | 1.71 | **0.49** | **-71.3%** (3.5×) | 268.82** | 399.71** | *(HTTPS per new track)* | 268.82 | 399.71 |
| `applemusic.view` `recent` | 10 (10/10) | 3.16 | **1.40** | **-55.7%** (2.3×) | 62.55 | **2.02** | **-96.8%** | 62.55 | 2.02 |
| `applemusic.view` `queue` (~30 KB) | 10 (10/10) | 3.11 | **2.19** | **-29.6%** | 4.36 | 4.04 | -7.3% | 4.36 | 4.04 |
| `applemusic.view` `home` (~111 KB) | 10 (10/10) | 13.33 | 12.26 | -8.0% | 70.35 | **17.37** | **-75.3%** | 70.35 | 17.37 |
| `applemusic.view` `library` (HTTPS) | 10 (10/10) | 361.97 | 290.06 | -19.9% | 442.73 | 398.12 | -10.1% | 442.73 | 398.12 |
| `applemusic.act` `shuffle` (warm, 40×) | 40 (40/40) | 1.89 | **0.35** | **-81.5%** (5.4×) | 2.31 | **0.40** | **-82.7%** | 7.70 | 5.54 |
| Native `media_state::music_control` cascade | per press | **7 RPCs** | **0 RPCs** | **-100%** | — | — | — | — | — |

\*Single 3.36 ms GC/dispatcher jitter sample in n=10 `status` (remaining 9/10 samples were 0.36–0.51 ms).
\*\*`applemusic.view("player")` awaits Apple's HTTPS `/v1/catalog/...` + `personal(...)` endpoints once after a track change (~270–400 ms), then serves warm calls in **0.41–0.49 ms** (vs 1.61–1.71 ms before).

### Why the Owner's Log Showed ~460–500 ms `music.control` and ~314–427 ms `applemusic.view`
1. **`music.control` (~462–487 ms p50, max 1,655 ms):** Confirmed 1-to-1 on the Before build — `AppleMusicAdapter.control` returned `Promise.resolve(m.skipToNextItem() / m.play()).then(() => true)` over `Runtime.evaluate(awaitPromise: true)`, blocking for **441.48 ms p50 / 661.57 ms p95** on `next` and **1,593.60 ms max** on initial `play` resume while MusicKit fetched HLS/DRM segments. Making `AppleMusicAdapter.control` fire-and-ack dropped `next` to **0.66 ms p50** and `play/pause` to **0.70 ms p50** while tracks still skipped cleanly (`SUKI` → `Limitless II`).
2. **`applemusic.view` (~314–427 ms p50, 731 hits):** `applemusic.view("library")` always fetches Apple's live HTTPS `/v1/me/library/...` endpoint (**361.97 ms p50 / 442.73 ms p95** Before; **290.06 ms p50 / 398.12 ms p95** After), and `applemusic.view("player")` hits HTTPS once per new track (~270–400 ms). Previously, `exo-ui`'s `media_state.rs::music_control` unconditionally called `s.load_music_room()` → `s.load_music_views()` (`library`, `home`, `recent`, `player`) after **every single media button press**, creating a 7-RPC cascade (~400+ ms burst) on every keypress. Removing that cascade eliminates the repeated `library`/`home`/`recent`/`player` hits during playback controls, and caching the ~64 KB `AppleMusicNative` + `MusicNowPlaying` JS adapters per document navigation cuts warm CDP call overhead by 55–81% (and removes the redundant `"1"` ping in `WebMusicActCoreAsync`).

---

## 2. Confirmation: Music Room Loading After Removing the 7-RPC Cascade

Removing `s.load_music_room()` from `media_state.rs::music_control` **only** stopped media button presses (`play`/`pause`/`toggle`/`next`/`prev`) from re-fetching the entire room. Every room-loading and now-playing path remains intact:

- **Home, Library, Recent, Player room load:**
  - Navigating to `Room::Music` (`apps/launcher/native-ui/crates/exo-ui/src/state.rs:563`), switching music service (`media_state.rs:762`), or clicking Refresh/More (`music.rs:365, 769, 1363`) still calls `AppState::load_music_room()` (`media_state.rs:537`), which calls `music.playback`, `music.volume`, `applemusic.start`, and then `load_music_views()` (`media_state.rs:594`) to populate `library`, `home`, `recent`, and `player`.
  - **Live host verification (After build):** `applemusic.view("home")` loaded **16 sections** (110.9 KB), `applemusic.view("library")` loaded **3 items**, `applemusic.view("recent")` loaded **8 items**.
- **Now-playing updates (what replaced the 7-RPC cascade on controls):**
  - Immediate optimistic play/pause flip in `media_state.rs:850-864`.
  - Live host-pushed `music.playback` events (`MusicEngineService.cs:597-602` → `exo-ui/src/main.rs:1072-1074` `HostEvent::MusicPlayback(pb) => s.media.playback = pb`). During the live After benchmark, **21 `music.playback` push events** arrived automatically across the 90 control calls as the player paused, resumed, and skipped from `SUKI` to `Limitless II`.
  - Targeted fallback: `needs_music_playback_refresh(control_ok, playback)` (`media_state.rs:1122-1127`) triggers **0** follow-up RPCs when `control_ok` is true for `MusicEngineService` (`source == ""` or `"page"`), and falls back to a **single** `METHOD_MUSIC_PLAYBACK` RPC (`refresh_music_playback`, `media_state.rs:836-848`) only on control failure (`!control_ok`) or headless GSMTC playback (`source == "session"`).

---

## 3. Motion & Layout Plan Task 8 — Music Items & Mini-Player Visibility

- **Sidebar gridline alignment (`music.rs`):** Normalized `nav_row` leading icon container to centered `36×36` (`min_h(44.0)`, `gap(10.0)`) and library artwork from `44.0` to `36.0` (`gap(10.0)`, `RECENT_ART_RADIUS = 9.0`), wrapped both labels in `grow(1.0).min_w(0.0)` columns so all sidebar labels lock to `x = 54 px` (eliminating the 28 px jump), and inset `side_head` by `.px(8.0)`.
- **Toolbar section title (`music.rs`):** Populated the empty left side of the main-pane toolbar row with the active section title (`Home`, `Songs`, `New`, `Radio`, etc.) in `scaled_title(s)` vertically centered with `music-search-box`.
- **Mini-player visibility during capture/library menus (`music.rs`):** Verified React `TitleBar.tsx:126` keeps `<MusicMiniPlayer>` visible while capture/library menus are open (`hidden={view === 'music'}`), removed `|| s.capture_menu_open` and `|| s.library_more_open` from `mini_player`, and confirmed via unit test that `shell::BlurBudget::compute` keeps `mini_player` blur off (`<= 2` active blurs `<= MAX_BACKDROP_BLURS = 4`).
- **Parity stills (1920×1080):**
  - Before: `improve/before-music.png`, `improve/before-music-compare.png`
  - After: `improve/after-music.png`, `improve/after-music-compare.png` (`mean abs diff 3.4`; diff localized strictly to the aligned sidebar column and new `Home` heading; `1 frames at 60 fps, gpu init 219.8 ms, first frame 395.01 ms, 1246 instances, 951 layout nodes, 675 glyphs, 0 animating frames at rest`).

---

## 4. Root Causes Fixed & Gate Verification

### Files Modified
- `apps/launcher/ExoLauncher/Helpers/AppleMusicNative.cs`
- `apps/launcher/ExoLauncher/Services/MusicNowPlaying.cs`
- `apps/launcher/ExoLauncher/Services/MusicEngineService.cs`
- `apps/launcher/ExoLauncher/Services/MusicEngineService.Rooms.cs`
- `apps/launcher/ExoLauncher.Tests/MusicEngineReturnTests.cs`
- `apps/launcher/native-ui/crates/exo-ui/src/media_state.rs`
- `apps/launcher/native-ui/crates/exo-ui/src/music.rs`
- `improve/report.md`

### Gate Exit Codes
- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` — **0** (0 warnings, 0 errors)
- `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (`TEMP`/`TMP` = `C:\Temp\exo-tests`) — **0** (7979 succeeded, 0 failed, 28 skipped; 11/11 `MusicEngineReturnTests`)
- `node tools/check-comments.mjs` — **0**
- `cargo fmt --all -- --check` — **0**
- `cargo clippy --workspace --locked -- -D warnings` — **0**
- `cargo test --workspace --locked` — **0** (41/41 passed including all `motion_audit::*` and `blur_budget` tests)
- `cargo build --release --locked --bin exo-ui` — **0**
