# Room Parity Report — Game (`game-page`, `game-page-hover-primary`, `game-page-props-*`)

- **Branch:** `UhhErix/parity-game` (base: `feat/native-polish-2`)
- **Date:** 2026-11-24
- **Parity command:** `node apps/launcher/tools/parity/run.mjs --rooms game --host-exe <staged-debug-host> --exo-ui apps/launcher/native-ui/target/debug/exo-ui.exe --seed-from %LOCALAPPDATA%\ExoLauncher`

## Before vs After

| Metric | Before | After |
| --- | --- | --- |
| States PASS | 3/8 (5 MEDIUM) | **8/8 PASS** |
| CRITICAL / HIGH / MEDIUM / LOW | 0 / 0 / 5 / 0 | **0 / 0 / 0 / 0** |
| Open / Menus / Cursors / Glass / Pixels failures | 0 / 0 / 0 / 0 / 5 | **0 / 0 / 0 / 0 / 0** |
| Cursor checks | 88/88 | **88/88** |
| Glass parity (React / Native) | 8/9, 10/7, 8/7 | **10/10 on all 8 states** |
| `game-page` | 21.49 (MEDIUM) | **2.02 (PASS)** |
| `game-page-hover-primary` | 21.68 (MEDIUM) | **2.11 (PASS)** |
| `game-page-props-files` | 5.78 (PASS) | **2.11 (PASS)** |
| `game-page-props-anticheat` | 5.74 (PASS) | **2.02 (PASS)** |
| `game-page-props-dlss` | 5.98 (PASS) | **2.11 (PASS)** |
| `game-page-props-betas` | 21.50 (MEDIUM) | **1.94 (PASS)** |
| `game-page-props-dlc` | 21.56 (MEDIUM) | **2.02 (PASS)** |
| `game-page-props-artwork` | 21.56 (MEDIUM) | **1.94 (PASS)** |
| **Mean pixel diff** | **15.66** | **2.03** |

> Note: Of the remaining `2.03` mean pixel diff across the 1920×1080 hero-art page, `~1.13` comes from external/other-worker areas left untouched in this PR (`top bar` `0.39`, `frame_gen_row.rs` + `frame_cap_row.rs` `0.31`, and WebView2 vs wgpu hero/cover image resampling `0.43`); the Game page's own cards and rows contribute `~0.90` (`Upscalers` `0.013`, `Shaders` `0.014`, all 4 `Graphics features` rows combined `0.120`, card rects exact `[32, 474, 984, 301]`, `[32, 791, 984, 362]`, `[1032, 474, 856, 301]`).

## Summary of Changes

1. **Live scene art & RPC preloading (`exo-ui/src/main.rs`, `exo-ui/src/state.rs`):**
   - Pre-fetched remote `portrait_art_candidates()` and `hero_url` for the open game in `load_live_state_sync` and preloaded `cover_image(sel, scale)` in `preload_visible_covers_sync` (fixes blank black cover poster on live Game pages).
   - Loaded `METHOD_GAME_FRAME_CAP` and `METHOD_GAME_GRAPHICS_FEATURES` in `load_live_state_sync` and re-queried `METHOD_GAME_EXTRAS` when achievements are still warming so the Game page renders live probe results and `1 / 34 Achievements` on first snap.
   - Aligned `PARITY_SCENES` `game-props-*` fallback tab indices with React's section resolution.
2. **2-column layout, Liquid Glass panels, and hero fidelity (`exo-ui/src/game_page.rs`, `exo-ui/src/game_perf.rs`, `exo-ui/src/game_props.rs`):**
   - Moved `Graphics features` into the left column (`grow(1.15)`) under `Performance & graphics` beside `Properties` (`grow(1.0)`), matching React's exact `[32, 474, 984, 301]`, `[32, 791, 984, 362]`, and `[1032, 474, 856, 301]` geometry.
   - Adopted `LiquidGlass::panel()` on `Performance & graphics`, `Graphics features`, and `Properties` cards, `LiquidGlass::control()` on the hero stats pill, and flat rimmed controls on `Launch options` and `Language` (`10/10` exact glass parity across all 8 states).
   - Sized game logos from `logo.size()` aspect ratio (left-bottom aligned, max 440×112), always rendered the `Upscalers` row when `dlss` is loaded, matched `-0.015em` card heading tracking, `#dcdcdc` panel secondary ink, `61px` perf/graphics rows, `44px` properties rows, icon-less `Open folder`/`Uninstall`/`Verify files` pills, and `#ff7979` danger text on `Uninstall`.
3. **Parity harness consistency (`tools/parity/states.mjs`):**
   - Unified all 8 `game-*` states to open the featured HomeDeck game via `.exo-home-more-btn` and wait for `#exo-props-tab-general, .exo-game-props-tab` so React and Native compare the same installed game and loaded Properties/Graphics cards across all 8 states.
