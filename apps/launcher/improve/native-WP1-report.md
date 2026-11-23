# WP1 Report — Game page and game actions

Branch: `UhhErix/native-WP1` (based on `feat/native-polish-2`).
Scope: All 19 non-DONE WP1 items in `apps/launcher/improve/feature-matrix.md` + verification/fixes across the 3 pre-existing DONE Game page rows (`game-page-01`, `game-page-11`, `game-page-20`).

## Summary

- **Items completed:** 19/19 WP1 items moved to `DONE` (Game page total: 22/22 `DONE`, 0 `PARTIAL`, 0 `MISSING`, 0 `BROKEN`).
- **Blocked items:** 0 (zero Rune fork changes required; Rune rev in `native-ui/Cargo.toml` left untouched).
- **Files added:**
  - `apps/launcher/native-ui/crates/exo-rpc/src/game_types.rs`
  - `apps/launcher/native-ui/crates/exo-ui/src/game_actions.rs`
  - `apps/launcher/native-ui/crates/exo-ui/src/game_perf.rs`
  - `apps/launcher/native-ui/crates/exo-ui/src/game_props.rs`
- **Files updated:**
  - `apps/launcher/native-ui/crates/exo-rpc/src/types.rs` (missing DTO fields on `Game`, `GameVariant`, `GameExtras`, `GameProperties`, `LauncherSettings`; 4 typed `HostEvent` variants: `GameAchievements`, `GameOptimizedAgain`, `GameOptimizeHeld`, `CompressProgress`)
  - `apps/launcher/native-ui/crates/exo-rpc/src/lib.rs` (new WP1 `METHOD_*` constants, `USED_METHODS`, event parsers, and unit tests)
  - `apps/launcher/native-ui/crates/exo-kit/src/icons.rs` (additive `icon_upload`, `icon_rotate_ccw`)
  - `apps/launcher/native-ui/crates/exo-ui/src/game_page.rs`
  - `apps/launcher/native-ui/crates/exo-ui/src/state.rs` (minimal additive wiring: `game_page: GamePageState`, 6 `OpenDropdown` variants, 4 one-line `HostEvent` dispatch arms, `load_game_page_data`, `poll_game_move_if_due`, `on_game_run_state`, `on_game_shaders_changed`)
  - `apps/launcher/native-ui/crates/exo-ui/src/menus.rs` (delegates `OpenDropdown::Game*` to `game_props::dropdown_overlay`)
  - `apps/launcher/native-ui/crates/exo-ui/src/main.rs` (live host RPC loading for `game`/`game-*` scenes + `game-props-betas`, `game-props-dlc`, `game-props-artwork` scenes)
  - `apps/launcher/tools/parity/states.mjs` (added `game-page-props-betas`, `game-page-props-dlc`, `game-page-props-artwork`; fixed obsolete `/properties/` click step on `game-page-props-files`)
  - `apps/launcher/improve/feature-matrix.md` and `apps/launcher/improve/feature-matrix.json`

## Per-item status and verification

| ID | Feature | Status | Native ref | RPC / Events | Verification |
|---|---|---|---|---|---|
| `game-page-01` | Page hero (art, logo, title, meta) | DONE | `exo-ui/src/game_page.rs:405` | `game.get` | Verified on live host (`wp1-game.png`), `driveHeat` pill + floating `LiveStatus` + non-installed `ArtworkStrip`. |
| `game-page-02` | Primary action states | DONE | `exo-ui/src/game_page.rs:163` | `game.launch`, `game.install`, `game.update`, `game.pauseInstall`, `game.cancelInstall`, `game.stop`, `game.dequeue`, `game.resumeDownload`, `game.progress`, `shell.openUrl` | Unit tests (`entitlement_and_primary_action_match_react`) + live host Play state. |
| `game-page-03` | Download / install progress bar + speed + ETA | DONE | `exo-ui/src/game_page.rs:315` | `game.progress`, `install.progress` | Unit tests (`transfer_progress_speed_and_eta_format_correctly`) + edge progress bar, bytes, speed, ETA, pause/cancel buttons. |
| `game-page-04` | Performance card | DONE | `exo-ui/src/game_perf.rs:187` | `game.settings`, `keep.status`, `dlss.status`, `dlss.frameGen`, `gpu.game`, `hdr.game`, `hdr.state`, `shaders.status`, `game.optimizedAgain`, `game.optimizeHeld`, `game.runState` | Hardcoded fake text deleted; verified live on Call of Duty: MW4 (`Up to date: FSR 4.1.2, XeSS 1.3.4.95, DLSS 310.9.16`, `Get Exo Shaders`, `Built into the game`, `Applied`) + unit tests (`connected_mode_has_no_fake_perf_data_and_handles_events`, `performance_words_match_react_for_all_states`). |
| `game-page-05` | Display mode (window mode) | DONE | `exo-ui/src/game_perf.rs:354` | `game.settings` | Local-only no-op removed; wired to `game.settings` `windowMode` (`Borderless` / `Fullscreen`) and verified in dropdown test + live host capture. |
| `game-page-06` | Frame generation row | DONE | `exo-ui/src/game_perf.rs:260` | `dlss.frameGen`, `gpu.game`, `game.settings` (Smooth Motion) | `FrameGenModRow` + `FrameGenRow` (AMD Fluid Motion Frames / Intel XeSS 2x/3x/4x) + NVIDIA Smooth Motion switch verified by unit tests and live host capture. |
| `game-page-07` | HDR row | DONE | `exo-ui/src/game_perf.rs:277` | `hdr.game`, `hdr.state`, `settings.set` | Auto/Always/Never dropdown + `Turn on` action when `hdrForGames` is off; verified in `game_page_interactive_perf_props_and_hero_states`. |
| `game-page-08` | Compress row (CompactOS) | DONE | `exo-ui/src/game_props.rs:475` | `compress.state`, `compress.act`, `compress.progress` | Verified live on Call of Duty: MW4 (`Saved 225 MB` + `Uncompress`) + unit tests (`compress_and_properties_sections_match_react`, `connected_mode_has_no_fake_perf_data_and_handles_events`). |
| `game-page-09` | Shader pre-compile row | DONE | `exo-ui/src/game_perf.rs:232`, `game_props.rs:795` | `shaders.status`, `shaders.changed`, `game.properties.set` (`shaders`) | Verified live on Call of Duty: MW4 (`Shaders: Get Exo Shaders` with external link icon in Performance card + `Shaders: 550 MB / Rebuild shaders` in Installed files). |
| `game-page-10` | Properties: General | DONE | `exo-ui/src/game_props.rs:254` | `game.properties`, `game.properties.set` | Fixed `steamOverlay` payload from `"true"`/`"false"` to `"on"`/`"off"` to match C# `WebHostBridge.Properties.cs:385`; real `languageChoices` with download confirm; `launchOptions` + `launchOptionsNote`. Verified live (`wp1-game.png`) and unit tests. |
| `game-page-11` | Properties: Installed files | DONE | `exo-ui/src/game_props.rs:647` | `game.repair`, `game.uninstall`, `game.openFolder` | Verified live (`wp1-game-props-files.png`): `Size on disk` (`77 GB`), `Open folder`, `Uninstall` (with `Remove` / `Remove? Files stay` for local games, `handoffOnly`/`queued` handling), `Verify files` confirm. |
| `game-page-12` | Properties: Betas | DONE | `exo-ui/src/game_props.rs:956` | `game.properties`, `game.properties.set` (`beta`, `betaCode`) | Real `betaChoices` dropdown with download confirm + Rune `.secure()` password-masked `Access code` input + `Check` button. Verified live (`wp1-game-props-betas.png`) and unit tests. |
| `game-page-13` | Properties: DLC | DONE | `exo-ui/src/game_props.rs:1057` | `game.properties`, `game.properties.set` (`content`) | Real `GameContent` rows with sizes, toggle switches + inline confirm (`Remove it?` / `Download <size>?`), and non-toggle state text. Verified live on Call of Duty: MW4 (`wp1-game-props-dlc.png`). |
| `game-page-14` | Properties: Artwork | DONE | `exo-ui/src/game_props.rs:196` | `art.replace`, `art.reset`, `art.refetch` | Removed broken `refresh_library` handler; wired `Replace cover`, `Reset cover` (when custom), and `Refresh art` in both Properties > Artwork and non-installed `ArtworkStrip`. Verified live (`wp1-game-props-artwork.png`). |
| `game-page-15` | Properties: Upscaler files | DONE | `exo-ui/src/game_props.rs:869` | `dlss.status`, `dlss.consent`, `dlss.restore` | Verified live on Call of Duty: MW4 (`Upscaler changes: Allowed / Stop allowing` with Stop allowing / Stop and restore menu + `Upscaler files: Restore originals`), plus ban-risk confirm (`Risk an Easy Anti-Cheat ban?`) and unit tests. |
| `game-page-16` | Properties: Anti-cheat choice | DONE | `exo-ui/src/game_props.rs:424` | `game.properties.set` (`antiCheatLaunch`) | Choice dropdown (`Launch with anti-cheat` / `Launch without anti-cheat (offline)`) + `Play offline only?` confirm. Verified in `game_page_interactive_perf_props_and_hero_states`. |
| `game-page-17` | Properties: Cloud saves / Steam Input | DONE | `exo-ui/src/game_props.rs:348` | `game.properties`, `game.properties.set` (`cloudSync`, `throughSteam`, `storeProperties`) | `Cloud saves: Sync now`, `Steam Input` switch (`throughSteam`), and `Controller in <store>` (`storeProperties`) verified in `game_page_interactive_perf_props_and_hero_states`. |
| `game-page-18` | Properties: Move library folder / size on disk | DONE | `exo-ui/src/game_props.rs:815` | `game.properties`, `game.properties.set` (`folder`) | `Size on disk` + `Library folder` choice with move confirm + live exponential-backoff move polling (`2s -> 8s`) with progress bar. Verified in unit tests. |
| `game-page-19` | Achievements + live updates | DONE | `exo-ui/src/game_page.rs:517` | `game.extras`, `game.achievements` | Typed `HostEvent::GameAchievements` + sheet cache + progress bar verified live (`12 / 175`) and unit tests. Note: in-game achievement unlock banners are rendered by the .NET host (`AchievementSource.cs`), matching React. |
| `game-page-20` | Playtime / last played stats | DONE | `exo-ui/src/game_page.rs:505` | `game.get` | Updated to match React `formatRelativeLastPlayed` (`8 hr ago`, `Yesterday`, `Aug 21`) and omit unknown facts cleanly. |
| `game-page-21` | Friends who play / Steam Family & sources | DONE | `exo-ui/src/game_page.rs:41` | `settings.set` (`preferredGameSources`) | Matches React `GameSections.tsx`: `GameShared` (`Shared by <name>` / `Steam Family` pill with `icon_users`) + multi-store `GameStore` source switcher persisting `preferredGameSources`. Verified in `game_page_interactive_perf_props_and_hero_states`. |
| `game-page-22` | Store page link / Buy again | DONE | `exo-ui/src/game_page.rs:233` | `shell.openUrl` | `hosted_buy_url`, `buy_label` (`Buy on Steam` / `Buy on GOG` / `Buy in browser`), and `Buy again` for `entitlementState == "notOwned"` opening via `shell.openUrl`. Verified in `entitlement_and_primary_action_match_react`. |

## Verification Gates

All run in `apps/launcher/native-ui` (plus `check-comments.mjs` in `apps/launcher`):
- `cargo fmt --check`: exit `0`
- `cargo clippy --workspace --all-targets --locked -- -D warnings`: exit `0` (0 warnings)
- `cargo test --workspace --locked`: exit `0` (`exo-ui`: 78 passed / 0 failed, including 6 new WP1 tests + both motion gate tests; `exo-rpc`: all contract & event parser tests passed including 4 new WP1 events; `exo-kit`: passed)
- `cargo build --release --locked -p exo-ui`: exit `0`
- `node apps/launcher/tools/check-comments.mjs`: 0 violations in any `native-ui/` file (pre-existing stale ratchet entries remain only in untouched `mouse/` and `ui/` files)

## Parity Harness & Live Host Results (1920x1080, scale 1)

Run against a repo-built Debug `ExoLauncher.exe` and a temp copy of the signed-in data directory (`%TEMP%\exo-wp1-data`, never touching `%LOCALAPPDATA%\ExoLauncher\app` or real data dir):

- **Before WP1:** `game_page.rs:154-200` showed hardcoded fake Performance rows (`Up to date: FSR, XeSS, DLSS`, `Built into the game`, `Optimize: Applied`), `Display mode` was a local-only no-op, `Steam overlay` sent invalid `"true"`/`"false"`, `Artwork` called `refresh_library`, Betas/DLC were read-only placeholders, and Compress/HDR/Shaders/Upscalers/Anti-cheat/Cloud saves/Steam Input/Move folder/Buy again/Queued/Resume were missing.
- **After WP1 (`tools/parity/run.mjs --rooms game`):**
  - 8 states checked: **CRITICAL=0, HIGH=0**, MEDIUM=4, LOW=4 (0 Open failures on active states, 0 Cursor failures 11/11, 0 Glass failures 10/10 & 10/8).
  - Mean pixel diff dropped from **15.03** (initial WP1 pass) to **14.93** after matching inactive Properties tab pills, plain `Steam overlay` label, and `Access code` width/placeholder; remaining pixel diff (~14.3) is purely because React's `All games` click opened alphabetical #1 (*Beast of Reincarnation*) while native `game` scene opened most-recently-played (*Call of Duty: Modern Warfare 4*).
  - Visual inspection of all 5 live-host captures (`game`, `game-props-files`, `game-props-betas`, `game-props-dlc`, `game-props-artwork`) confirmed 1:1 visual and data parity with React.

## WP11 RPC Parity Follow-up (`dlss.status`, `dlss.newer`, `dlss.downloadNewer`, WP1 push events)

Audited `origin/UhhErix/native-WP11:apps/launcher/improve/rpc-parity.md` and `.json` (`d1d9589`) against WP1's game-page / game-actions surface:

- **Push events (4/4 handled, 0 WP1 gaps remaining):** `game.achievements`, `game.optimizedAgain`, `game.optimizeHeld`, and `compress.progress` are all registered as typed `HostEvent` parsers in `exo-rpc/src/lib.rs` (`parse_event`) and handled in `game_actions.rs`. Verified via WP11's `indexNativeHandlers` (`tools/parity/lib.mjs`): all 4 return `true`.
- **RPC methods ported to close WP1 gaps:**
  - `dlss.status` (6 calls on `startup` in React via `ui/src/lib/upscalerCache.ts:34` / `LauncherApp.tsx:477`): ported 1:1 startup preloading (`schedule_upscaler_startup_work` -> `1s` delay -> `preload_upscaler_statuses` over top 8 visible games & installed variants, concurrency 2, 2s budget) plus 30-min case-insensitive TTL cache (cap 512) and inflight dedup/invalidation in `game_actions.rs`.
  - `dlss.newer` + `dlss.downloadNewer` (`ui/src/components/LauncherApp.tsx:111-118`, `ui/src/lib/upscalerNotice.ts`): added `DlssNewerItem`/`DlssNewerReply` DTOs, `METHOD_DLSS_NEWER`/`METHOD_DLSS_DOWNLOAD_NEWER` constants in `exo-rpc`, 20s startup check (`check_dlss_newer`), interactive `"Download"` action button on toast notices (`notice.rs` / `state.rs`), and `download_newer_upscalers`.
  - `keep.status` + all game-page RPCs (`game.get`, `game.extras`, `game.progress`, `game.properties`, `game.properties.set`, `game.settings`, `dlss.frameGen`, `dlss.restore`, `dlss.consent`, `gpu.game`, `hdr.game`, `shaders.status`, `compress.state`, `compress.act`, `art.replace`, `art.reset`, `art.refetch`): already wired in WP1 (`50fce5d`) and confirmed `true` in WP11's `indexNativeHandlers`.
- **Verification after follow-up edits:**
  - `cargo fmt --check`: exit `0`
  - `cargo clippy --workspace --all-targets --locked -- -D warnings`: exit `0`
  - `cargo test --workspace --locked`: exit `0` (`exo-kit` 4/4, `exo-rpc` 22/22, `exo-ui` 82/82 including 4 new `upscaler_cache_tests`)
  - `cargo build --release --locked -p exo-ui`: exit `0`
  - `node apps/launcher/tools/check-comments.mjs`: 0 violations in `native-ui/`
  - **Live WP11 `EXO_RPC_TRACE` run against release `exo-ui.exe`** (using WP11's Debug `ExoLauncher.exe` and disposable data dir): observed `"dlss.status": 6` (and `8` on full warm) + `"dlss.newer": 1` on startup/session, matching React 1:1.
  - **WP11 `indexNativeHandlers` static check:** 4/4 WP1 push events = `true`, 21/21 WP1 RPC methods = `true` (0 remaining WP1 rows).
