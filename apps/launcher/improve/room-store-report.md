# Store Room Parity Report (`UhhErix/parity-store`)

Target: `apps/launcher/tools/parity/run.mjs --rooms store` (native `exo-ui` vs React WebView2 at 1920x1080, seeded from `%LOCALAPPDATA%\ExoLauncher`).

## Summary (Before vs After)

| Metric | Baseline (`store-base`) | Final (`store-pass4`) |
| --- | --- | --- |
| States passing | **1 / 7** | **7 / 7** |
| Severity breakdown | 4 CRITICAL, 0 HIGH, 1 MEDIUM, 1 LOW | **0 CRITICAL, 0 HIGH, 0 MEDIUM, 0 LOW** |
| Open failures | 1 (`store-card-hover`) | **0 / 7** |
| Menu failures | 2 (`store-game-media-tab`, `store-game-requirements`) | **0 / 7** (22/22 exact) |
| Cursor failures | 0 | **0 / 7** (95/95 exact) |
| Glass failures (late) | 4 (late: 1) | **0 / 7** (late: 0) |
| Mean pixel diff | **26.73** | **4.14** |

## Final Per-State Checklist (`store-pass4`)

| State | Kind | Severity | Open (R/N) | Menus | Cursors | Glass | Pixel Diff |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `store-discover` | room | **PASS** | Y/Y | n/a | 10/10 | 11/11 | **4.70** |
| `store-card-hover` | hover | **PASS** | Y/Y | n/a | 11/11 | 11/11 | **5.06** |
| `store-wishlist` | tab | **PASS** | Y/Y | n/a | 11/11 | 10/10 | **1.07** |
| `store-search` | subpage | **PASS** | Y/Y | n/a | 11/11 | 4/4 | **1.95** |
| `store-game-page` | subpage | **PASS** | Y/Y | n/a | 11/11 | 15/14 | **4.83** |
| `store-game-media-tab` | tab | **PASS** | Y/Y | 11/11 | 20/20 | 8/8 | **5.55** |
| `store-game-requirements` | subpage | **PASS** | Y/Y | 11/11 | 21/21 | 8/8 | **5.83** |

## RPC Parity Check (`store` room)

Audited every host RPC method called by React Store (`StoreRoom.tsx`, `StoreGamePage.tsx`, `StoreCard.tsx`, `TrailerPlayer.tsx`, `storeData.ts`, `stores.ts`, `storeSearch.ts`) against `apps/launcher/native-ui/crates/exo-ui`:

| Host RPC Method | React Caller | Native `exo-ui` Wiring | Status |
| --- | --- | --- | --- |
| `store.home` | `storeData.ts` | `state.rs` (`METHOD_STORE_HOME`) | Wired |
| `store.wishlist` | `storeData.ts`, `StoreRoom.tsx`, `StoreGamePage.tsx` | `store_page.rs` (`METHOD_STORE_WISHLIST`) | Wired |
| `store.search` | `storeSearch.ts` | `store_page.rs` (`METHOD_STORE_SEARCH`) | Wired |
| `store.game` | `storeData.ts`, `StoreGamePage.tsx` | `store_page.rs` (`METHOD_STORE_GAME`) | Wired |
| `store.gameOffers` | `storeData.ts`, `StoreGamePage.tsx` | `store_page.rs` (`METHOD_STORE_GAME_OFFERS`) | Wired |
| `store.gameReviews` | `storeData.ts`, `StoreGamePage.tsx` | `store_page.rs` (`METHOD_STORE_GAME_REVIEWS`) | Wired |
| `store.open` | `StoreGamePage.tsx`, `stores.ts` | `store_page.rs` (`METHOD_STORE_OPEN`) | Wired |
| `shell.openUrl` | `StoreGamePage.tsx` | `store_page.rs` (`METHOD_SHELL_OPEN_URL`) | Wired |

**RPC parity: 8 / 8 methods wired.**

## Root Causes Fixed

1. **Discover / Wishlist / Search layout & glass parity (`store.rs`)**:
   - Matched `.exo-store-grid` responsive card width (`store_grid_card_width`, 158.18px at 1920px) and `SurfaceFit::Fit` over dark gradient for landscape DLC capsules.
   - Applied `ExoGlass::Control` to the Discover/Wishlist tab pill group, wishlist bookmark badges, and hero tag/name pills; removed backdrop blur from `store-refresh` and shelf prev/next icon circles (`glass_icon_circle_opt(..., false)`).
   - Widened search bar to `360.0` to match React.
   - Added `.hover_group()` on `store_tile_card_sized` so hovering a shelf card tracks Rune hover state without adding an unwanted fill overlay (`store-card-hover` PASS).
2. **Store game page geometry, surfaces, glass, and scroll clipping (`store_page.rs`, `store_media.rs`, `shell.rs`)**:
   - Matched React's `360.0` right column / buy panel width, `34.0` back button height, `853.0` media stage height, `182.5 x 102.66` thumbnail dimensions, and `ExoGlass::Control` on the 8 tag pills, wishlist button, and GG.deals button.
   - Removed late backdrop blur on `sg-trailer-play` / `sg-trailer-mute` (`glass_icon_circle_opt(..., false)`), eliminating all late glass failures.
   - Added bottom legibility gradient scrim on trailer media to match React `<video controls>` shading.
   - Switched `about_card`, `facts_card`, `specs_card`, and `sg-error` fills from `surface_card()` to `exo_kit::BG` (matching `--exo-float: #000`), matched `.exo-sgp-h` (`18px`) and `.exo-sgp-text` (`20px`) line heights, and aligned bottom padding (`pb(34.0)`) and More like this top spacing (`pt(12.0)`).
   - Wrapped the Store game page scroll container in `pt(TITLEBAR_HEIGHT * 2.0)` (`104.0`) in `shell.rs` with `page.pt(4.0)` in `store_page.rs` so scroll=0 stays at `y=108` while scrolled content clips cleanly at `y=104` below the header band just like React.
3. **Media strip menu probe & scrolled state scripts (`main.rs`, `tools/parity/states.mjs`)**:
   - Added `"sg-media-strip"` to `semantic_menu_prefixes` in `main.rs` so visible `role="option"` trailer/screenshot thumbnails match React's 11/11 menu items on scrolled states.
   - Added wheel scroll actions (`dy: -629` and `dy: -900`) to `store-game-media-tab` and `store-game-requirements` in `tools/parity/states.mjs`.

## Shared Files Touched (Small & Additive)

- `apps/launcher/native-ui/crates/exo-ui/src/main.rs` (+1 line: `"sg-media-strip"` in `semantic_menu_prefixes`)
- `apps/launcher/native-ui/crates/exo-ui/src/shell.rs` (conditional `pt(TITLEBAR_HEIGHT * 2.0)` wrapper for `Room::Store && s.store_has_page_open()`)
- `apps/launcher/tools/parity/states.mjs` (Store scrolled state wheel events)
