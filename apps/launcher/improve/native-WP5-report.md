# WP5 Report — Store game page and store extras

Branch: `UhhErix/native-WP5` (base: `feat/native-polish-2`)

## Summary

Completed **11 of 12** WP5 items (`store-02`, `store-03`, `store-06`, `store-07`, `store-09`, `store-10`, `store-13`, `store-14`, `store-15`, `store-16`, `store-17`) plus fixes to existing DONE Store items (`StoreTile::similar_to` `Vec<String>` deserialization bug that prevented `store.home` from loading in live mode, debounced 250ms search-as-you-type with clear button and React empty/error copy, pointer cursors across Store controls, synchronous Store image preloading, and rendering all shelves instead of capping at 4).

**1 item (`store-12`, HLS trailers) is `PARTIAL` / blocked on HLS playback:**
- `exo-ui/src/video_decoder.rs` (owned by WP10) only decodes raw elementary-stream `VideoPacket`s (`DiscordVideoFrame`) via `CLSID_CMSH264DecoderMFT` — it has no HTTP/HLS/m3u8 fetching or demuxing.
- `store_media.rs` wires `rune_media::Player::url(hls)` with play/pause/mute/progress controls, poster-until-playing, and automatic poster fallback on failure. Tested empirically against Steam's real `.m3u8` URL (`https://video.akamai.steamstatic.com/.../hls_264_master.m3u8`), desktop Windows Media Foundation `IMFSourceResolver::CreateObjectFromURL` in `rune-media` returns `PlayerState::Failed(...)` for `.m3u8` HLS playlists (desktop MF requires WinRT `AdaptiveMediaSource` or an HLS demuxer). While blocked, the media stage cleanly falls back to the trailer poster (matching React's `mode === 'failed'` behavior) and switches seamlessly between trailers and screenshots.

## Per-Item Status & Verification

| ID | Feature | Status | Native Ref | Verification |
|---|---|---|---|---|
| `store-02` | Store hero hover / pick list | **DONE** | `exo-ui/src/store.rs:777` | 8s auto-rotate with progress bar, hover pause (`hero_hovered`), reduce-motion pause, Steam `logo.png` + `HeroTags` (`Coming …`, `On your wishlist`), single-item full-width mode, clicking active pick opens game page. Verified by unit test (`wishlist_optimistic_toggle_and_hero_rotation_work`), live parity snap (`store-discover.png`), and live interactive double-tap probe (`hero-dot-1` -> `storeGameOpen: true`). |
| `store-03` | Wishlist tab | **DONE** | `exo-ui/src/store.rs:582` | Calls `store.wishlist` (`action: "get"`), refreshes on `store.changed(what: "wishlist")`, handles loading/empty (`"Your wishlist is empty"`)/error+retry states. Note: React `StoreRoom.tsx:520` has no count badge on the Wishlist tab. Verified visually in `store-wishlist.png` against React. |
| `store-06` | Tile card: "Library" button on owned games | **DONE** | `exo-ui/src/store.rs:348`, `store_page.rs:133` | Exact port of React `useOwned` (Steam `appId`, `includes`, `variants`, non-Steam title fold; skips `is_add_portable` and never cross-matches Steam games by title). Hides price/reviews and renders compact `Library ->` button. Verified by unit tests (`owned_matching_uses_steam_id_and_non_steam_title`, `store_wishlist_splits_sale_and_owned_cards_show_library_button`), live `store-search.png` (`ELDEN RING` & `ELDEN RING NIGHTREIGN` show `Library ->`), and live click probe (`store-tile-lib-search-1245620` -> `room: "Library", openGameId: "steam:1245620"`). |
| `store-07` | Check prices / On sale split | **DONE** | `exo-ui/src/store.rs:610`, `store_page.rs:341`, `exo-rpc/src/events.rs:111` | Calls `store.wishlist` (`action: "check"`) with busy/done states, splits `"On sale now"` (unowned discounted) vs `"Not on sale"`, registers & parses `store.sale` event in `exo-rpc` (`HostEvent::StoreSale`), and shows React's notice toast (`"{name} is on sale on Steam: {price} (-{discount}%)."`). Verified by unit tests (`store_wishlist_splits_sale_and_owned_cards_show_library_button`, `score_controller_languages_and_sale_formatting_match_react`, `exo-rpc` `parses_all_newly_wired_host_events`) and live `store-wishlist` check-prices click probe. |
| `store-09` | Store game page: offer rows (store names, prices, historical low) | **DONE** | `exo-ui/src/store_page.rs:1244`, `exo-rpc/src/types.rs:943` | Replaced invented raw-ID offer card with React's 1:1 `BuyPanel`: primary `Get on Steam` / `Wishlist on Steam` pill (or `Library ->` when owned), `GG.deals` button (`shell.openUrl` + `gg_deals_url`), and secondary official launcher row (`store.gameOffers`) using `StoreOffer::display_name()` (`Steam`, `Epic Games Store`, `GOG`, `Ubisoft` — never raw `"1"` / `"25"`). Added `StoreLowest::formatted_date()` (ISO -> `"Nov 2026"`). Verified by unit tests (`offer_display_name_and_lowest_date_never_leak_raw_ids_or_iso`, `gg_deals_slug_matches_react`) and live `store-game-page.png` (`EA SPORTS FC™ 27`: `Get on Steam -50% $34.99 $69.99` + `Epic Games Store $69.99`). |
| `store-10` | Store game page: hero art / header image | **DONE** | `exo-ui/src/store_page.rs:1143`, `store_media.rs:172` | Fixed 80px Steam `logo.png` slot falling back to 20px title text on 404 (matching React `HeroLogo`), plus 16:9 media stage falling back to `game.header` / `art.best_hero()` / Steam hero art. Verified on live `store-game-page.png`. |
| `store-12` | Store game page: trailers (HLS player) | **PARTIAL** | `exo-ui/src/store_media.rs:172` | Wired `rune_media::Player::url(hls)` with play/pause/mute/scrubber controls, poster-until-playing, reduce-motion no-autoplay, and clean poster fallback on failure. Blocked on HLS playback: `video_decoder.rs` only handles raw WebRTC elementary `VideoPacket`s and `rune-media`'s desktop Media Foundation `IMFSourceResolver::CreateObjectFromURL` fails on Steam `.m3u8` streams. |
| `store-13` | Store game page: screenshot strip + lightbox | **DONE** | `exo-ui/src/store_media.rs:172`, `store_media.rs:462` | 16:9 media stage + horizontal thumbnail strip (trailers with centered play badge, screenshots using `.600x338.jpg` thumbnails), plus fullscreen lightbox overlay mounted in `shell.rs` with counter (`1 / 10`), prev/next arrows, close button, click-scrim close, and `Esc`/`Left`/`Right` keyboard navigation. Added `store-lightbox` parity scene. Verified by unit tests (`thumb_of_screenshot_rewrites_full_hd_suffix`, `default_shown_media_prefers_first_trailer_with_hls`, `lightbox_steps_and_wraps`, `store_game_page_renders_all_sections_and_lightbox`) and live `final-lightbox-v2.png` snap. |
| `store-14` | Store game page: system requirements | **DONE** | `exo-ui/src/store_page.rs:1632` | Full 7-part table (`OS`, `Processor`, `Graphics`, `Memory`, `Storage`, `DirectX`, `Network`) with This PC comparison check/cross icons & tones, `Minimum`/`Recommended` lines, and verdict badge (`Runs at Recommended`, `Meets Minimum`, `Meets the requirements`, `Below Minimum`). Verified on live `store-game-page.png` and `store-game-requirements.png` against React. |
| `store-15` | Store game page: tags, About card, More like this | **DONE** | `exo-ui/src/store_page.rs:1168`, `1468`, `1911` | Single-row tag pills (8 tags), `About` card with full wrapped description, and `More like this` horizontal shelf footer (up to 12 deduplicated items starting from source shelf). Verified on live `store-game-page.png` and unit tests. |
| `store-16` | Store game page: wishlist toggle | **DONE** | `exo-ui/src/store_page.rs:1357` | Optimistic `Wishlist` / `On wishlist` button (bookmark vs green check) calling `store.wishlist` (`add`/`remove`) with rollback on host error; remains visible when an owned game is still wishlisted. Verified by unit test (`wishlist_optimistic_toggle_and_hero_rotation_work`) and live `store-game-page.png`. |
| `store-17` | Game reviews | **DONE** | `exo-ui/src/store_page.rs:1526`, `exo-rpc/src/lib.rs:201` | Added `METHOD_STORE_GAME_REVIEWS` (`store.gameReviews`) and `StoreRecentReviews` DTO; fetches recent reviews alongside `store.game` and renders `Recent reviews` (`word · percent% of count`) in the 10-cell `Facts` card alongside all-time `Reviews`. Verified on live `store-game-page.png` (`Mixed · 40% of 4,230` and Cyberpunk 2077 `Very Positive · 94% of 15,652`). |

## Parity Harness Before / After (1920x1080 scale 1, signed-in temp copy)

Ran `apps/launcher/tools/parity/run.mjs --rooms store` against a temp copy of the repo Debug host + temp copy of `%LOCALAPPDATA%\ExoLauncher` (never touching the installed app or live data dir).

| State | Before (initial pass, before `similar_to` & cursor/stage fixes) | After (final pass) | Notes |
|---|---|---|---|
| `store-discover` | Stuck on `"Loading the store…"` (`store.home` serde failed on `similarTo: string[]`), glass=4, cursors 10/10 | Real Store loaded (hero, 6 picks, all shelves & posters), glass=8 (PASS vs React 9), cursors **10/10 PASS** | Pixel diff 81.93 is solely because the React capture's Steam CDN images were still blank black squares at capture time while Native preloaded & rendered all real Steam posters/hero art. |
| `store-card-hover` | Cursors 10/11 FAIL (`state:target@(94,820)` default), no cards rendered | Cursors **11/11 PASS**, glass=8 (PASS), card lifts -3px on hover (`store-hover-test.png` verified) | Harness `open` check expects `hoveredAny` flag; standalone `--script` hover snap ran 67 frames cleanly with exit 0. |
| `store-wishlist` | Cursors 10/11 FAIL (`state:target@(128,78)` default), blank black posters | Cursors **11/11 PASS**, all 6 wishlisted posters + prices + reviews + `Check prices` rendered cleanly | Pixel diff 13.9 (vs blank-poster React capture); layout/text alignment matches React pixel-for-pixel. |
| `store-search` | Blank black posters | Cursors **11/11 PASS**, glass=3 (PASS), owned `Library ->` buttons on `ELDEN RING` & `ELDEN RING NIGHTREIGN`, prices/reviews on DLC & other results | Pixel diff 16.33 (native renders real posters vs black React placeholders). |
| `store-game-page` | Opened fallback Cyberpunk 2077 (wrong game), pixel diff 49.99, blank logo slot, 600px stage | Opens exact same game (`EA SPORTS FC™ 27`), pixel diff dropped **49.99 -> 26.83**, cursors **11/11 PASS**, 1:1 header/tags/BuyPanel/Facts/Specs/16:9 stage | Remaining pixel diff is mainly trailer video frame vs poster frame & subtle glass count (React counts individual pill borders as glass). |
| `store-game-requirements` | Pixel diff 50.7, wrong game | Pixel diff dropped **50.7 -> 26.83**, cursors **11/11 PASS**, all 7 spec rows + `Runs at Recommended` badge match React | — |
| `store-lightbox` (new scene) | Did not exist | Fullscreen 1920x1080 screenshot lightbox verified (`final-lightbox-v2.png`: `1 / 10` counter, `X` close below titlebar, `<`/`>` nav arrows) | — |

## Gates Run

All gates run in `apps/launcher/native-ui` (plus `check-comments`):
- `cargo fmt --check`: **PASS** (exit 0)
- `cargo clippy --workspace --locked -- -D warnings`: **PASS** (exit 0, 0 warnings)
- `cargo test --workspace --locked`: **PASS** (exit 0 — **109 passed, 0 failed**: 4 `exo-kit`, 22 `exo-rpc`, 83 `exo-ui`, including `store_tabs_hero_flight_and_shelf_paging_motion_gate` and 10 new WP5 unit tests)
- `cargo build --release --locked -p exo-ui`: **PASS** (exit 0)
- `node apps/launcher/tools/check-comments.mjs` (`native-ui` scope): **PASS** (0 comment violations in all created/edited `.rs` files)

## Files Modified / Created

- `apps/launcher/native-ui/crates/exo-rpc/src/events.rs` (`StoreSaleEvent`)
- `apps/launcher/native-ui/crates/exo-rpc/src/types.rs` (`StoreGame`, `StoreOffer`, `StoreLowest`, `StoreMedia`, `StoreSpec`, `StoreRecentReviews`, `StoreWishlistReply`, `StoreTile`, `StoreArt::steam_url`, `GameVariant`, `HostEvent::StoreSale`)
- `apps/launcher/native-ui/crates/exo-rpc/src/lib.rs` (`METHOD_STORE_GAME_REVIEWS`, `METHOD_SHELL_OPEN_URL`, `store.sale` event parsing + test)
- `apps/launcher/native-ui/crates/exo-kit/src/icons.rs` (`icon_arrow_right`, `icon_bookmark_filled`)
- `apps/launcher/native-ui/crates/exo-ui/src/store.rs` (Store Discover, Hero hover/auto-rotate/logo/tags, Wishlist tab + On sale split + Check prices, Search as-you-type, owned `Library` button, shared shelf row, unit + motion tests)
- `apps/launcher/native-ui/crates/exo-ui/src/store_page.rs` (new: `StoreGamePage` 1:1 with React + Store state/RPC methods + unit tests)
- `apps/launcher/native-ui/crates/exo-ui/src/store_media.rs` (new: `MediaViewer` stage/strip/trailer player + fullscreen Lightbox overlay + unit tests)
- `apps/launcher/native-ui/crates/exo-ui/src/state.rs` (minimal wiring: `store_ext`, `store.sale`, `store.changed(wishlist)`, `preload_visible_covers_sync`, `dismiss_top_layer`)
- `apps/launcher/native-ui/crates/exo-ui/src/shell.rs` (mount lightbox overlay + modal scrim)
- `apps/launcher/native-ui/crates/exo-ui/src/main.rs` (module registration, `poll_state` tick, lightbox keyboard nav, parity scene settling & `store-lightbox` scene)
- `apps/launcher/improve/feature-matrix.md` & `apps/launcher/improve/feature-matrix.json` (updated WP5 rows)
- `apps/launcher/improve/native-WP5-report.md` (this report)
