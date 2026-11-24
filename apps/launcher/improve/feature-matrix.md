# Feature matrix: public React Exo vs native Rune UI

Base: `integrate-polish-2` @ 3bdbdf1. Four finished branches counted as DONE: room-shell-library f3a5ec1, room-discord 78c4947, native-onboarding 515b989, wooting-native 57db49a. Generated 2026-11-23 on branch `UhhErix/feature-inventory`.

**Statuses.** DONE = works and calls the same RPC. PARTIAL = looks right but not wired, half the states, or stub. MISSING = absent natively. BROKEN = wrong RPC, ignored result, dead handler, hardcoded fake data or visibly wrong output.

**Refs.** React refs are relative to `apps/launcher/ui/src`. Native refs are relative to `apps/launcher/native-ui/crates`. A `†` marks a ref that only exists after merging the four branches; its line number comes from a scratch merge of base + those branches (the merge is read-only reference, it does not compile). Lines are a locator for the relevant code, not a claim that the whole file implements the row. A ref with no line means the file exists but the symbol was not found.

**Method.** (1) Walked the React tree (shell, library, game, store, discord ~60 files, music, gpu/pc, apps, devices, captures, settings, onboarding) and `lib/host.ts` (all 345 contract methods, 77 events). (2) Mapped every React file to the RPC methods it can call and every native file to the RPC literals it contains (`rpc.json`). (3) Compared React UI strings against native strings per file. (4) Read the native code of each room and the four branch reports. (5) Smoke-ran the native debug build (room-discord worktree build, includes the Discord work) against a repo-built host with a temp copy of the signed-in data directory (details at the end). Not every row was exercised interactively: rows that rest on code reading say so in Notes.

## Totals

| Room | DONE | PARTIAL | MISSING | BROKEN | Total |
|---|---:|---:|---:|---:|---:|
| Shell & Bar | 6 | 10 | 9 | 0 | 25 |
| Library | 6 | 7 | 6 | 2 | 21 |
| Game page | 22 | 0 | 0 | 0 | 22 |
| Store | 16 | 1 | 0 | 0 | 17 |
| Discord | 25 | 9 | 11 | 0 | 45 |
| Music | 5 | 7 | 9 | 0 | 21 |
| Exo AI / PC | 0 | 8 | 12 | 1 | 21 |
| Apps | 4 | 1 | 7 | 0 | 12 |
| Devices | 3 | 3 | 4 | 1 | 11 |
| Captures | 0 | 4 | 5 | 1 | 10 |
| Settings | 6 | 5 | 12 | 0 | 23 |
| Onboarding & Account | 0 | 4 | 6 | 0 | 10 |
| **Overall** | **93** | **59** | **81** | **5** | **238** |

RPC: React calls 345 of 345 contract methods; native references 134. 211 methods React calls are never called natively. Events: React handles 77 of 77; native handles 35; 42 React-handled events are dropped natively.

## Top 10 problems that make it unusable

1. **Fake data shown as real** (WP1, WP9). Game page "Performance & graphics" card is hardcoded (Up to date: FSR, XeSS, DLSS / Built into the game / Optimize Applied) for every installed game (game_page.rs:149-190, seen in smoke game-page.png); display mode picker does nothing (state.rs:1263); Exo AI "Auto Tune complete" with six static green rows is hardcoded (pc.rs ~1615-1700, seen in smoke pc.png). The user sees confident wrong information.
2. **Settings covers 6 of 13+ sections** (WP4). No store sign-in or account management (Steam/Epic/GOG/EA, accounts.*, steam.signIn*), no Exo Account (create/sign in/recovery/devices/delete), no privacy/telemetry view, no Report a problem, no Apps/web preferences, no FPS/achievement/install-folder/download-hours settings (smoke settings-general.png shows 3 rows).
3. **No folders, no select mode, no gifts** (WP2). Library "New folder" shows a "Folder management coming soon" toast (menus.rs:722, :1591); no FolderSheet/Card/Panel/Cropper, no multi-select bar, no gifts shelf, no drag reorder, no Ctrl+F/Ctrl+A or grid keyboard nav.
4. **Downloads are invisible** (WP3). No Downloads sheet, no Exo Island, no queue/pause-all/resume-all/reorder (downloads.* never called), no progress outside the game page, no What's New / update sheet, no update progress.
5. **Events are dropped: no toasts or system notices** (WP3). 42 of 77 host push events are ignored (discord.notify, anticheat.missing, stores.outage, gpu.driverInstall, keys.found, app.notify, versions.new, gifts.changed, game.optimizeHeld ...). Anti-cheat prompts, store outages and driver installs happen with nothing on screen.
6. **Store game page is broken and bare** (WP5). Smoke store-game.png: offer rows labelled "1" and "25" (raw store ids), raw ISO timestamp in "Historical low", no hero art, no trailer, no screenshots, no system requirements, no wishlist toggle, no reviews list.
7. **Captures: Notes dead, no editors** (WP10). Both Notes buttons are mis-wired (captures.rs ~290 reopens Captures; ~786 reloads it). No notes.* (11 methods), no Snip editor, no send-to, no reel. Exo Capture-not-installed state is the only thing the smoke run could exercise.
8. **Devices: controller, audio format, EQ are stubs** (WP10). Controller detail is a connection/battery card (controller.* 10 methods never called: no calibration, no pad settings, no game profiles, no update). No audio format picker, Exo Voice, AutoEQ (eq.headphones never called). Mouse is a host-embedded panel that the headless snapshot cannot show.
9. **Discord is a chat client without most of Discord** (WP6, WP7). Rooms/DMs/friends/voice render well, but no emoji/GIF/sticker pickers, no file upload, threads, polls, search, saved, inbox content, soundboard, call UI outside the room, server/channel/user settings pages, report, forward, nickname/timeout/ban.
10. **Apps and Music dead-end** (WP9, WP8). Apps: no scan, restore point, undo, installer cache, leftovers confirm, packages. Music: room sits on "Starting Apple Music…" with no sign-in/change-service flow in the smoke run, no artist page, lyrics, sound panel, device picker, friend activity.

## Shell & Bar

Totals: DONE 6, PARTIAL 10, MISSING 9, BROKEN 0 (of 25).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Search capsule (opens palette, Ctrl+K) | components/shell/BarCapsule.tsx:13 | exo-ui/src/shell.rs:32 | DONE | Right-click menu on capsule also present. |
| Room nav tabs (Library, Store, Discord, Music, Devices, Exo AI, Apps) | components/shell/TitleBar.tsx:34 | exo-ui/src/state.rs:45 | DONE | Ctrl+1..7 shortcuts present. |
| Discord unread / mention badge on nav tab | components/shell/DiscordBadge.tsx:6 | — | MISSING | rpc: friends.badge, discord.mentions. No badge anywhere in shell.rs; friends.badge never called. |
| Mini player in bar (title, play/pause, next, volume) | components/shell/MusicMiniPlayer.tsx:103 | exo-ui/src/music.rs:1520 | PARTIAL | rpc: music.control, music.volume. Controls work; React MusicVolume popover, seek and open-now-playing click partial. |
| Capture button + menu (screenshot, clip, open captures) | components/shell/CaptureButton.tsx:27 | exo-ui/src/captures.rs:183 | DONE | rpc: capture.list, capture.act. Popover exit fades per room-shell-library report. |
| Settings gear | components/shell/TitleBar.tsx:34 | exo-ui/src/shell.rs:3 | DONE |  |
| Window controls (min/max/close, drag, double-click maximize) | components/WindowChrome.tsx:6 | exo-ui/src/shell.rs:407 | PARTIAL | rpc: shell.window. shell.window event (maximize state) not handled; maximize icon does not follow host state. |
| Right-click menus on bar items | components/shell/BarCapsule.tsx:13 | exo-ui/src/menus.rs:72 | DONE |  |
| Exo Island (in-call / download capsule) | components/shell/ExoIsland.tsx:6 | — | MISSING | rpc: discord.ring, discord.myVoice, downloads.list. Not in native at all; also ExoIslandCall.tsx (call controls from any room). |
| Downloads sheet (queue, pause, cancel, resume, speed, order) | components/DownloadsSheet.tsx:23 | exo-ui/src/library.rs:398 | PARTIAL | rpc: downloads.list, downloads.pauseAll, downloads.resumeAll, downloads.move, game.pauseInstall, game.cancelInstall. Native only has pause/cancel buttons on the game page; downloads.* never called; no sheet, no queue. |
| What's New sheet (changelog after update) | components/shell/WhatsNewSheet.tsx:19 | — | MISSING | versions.new event not handled. |
| App update flow (check, download progress, install, restart) | components/shell/useAppUpdate.ts:69 | exo-ui/src/settings.rs:295 | PARTIAL | rpc: app.checkUpdate, app.installUpdate. Only Settings > About buttons; no auto toast, no progress, no update sheet. |
| Activate key sheet | components/ActivateKeySheet.tsx:19 | exo-ui/src/key_modal.rs:36 | PARTIAL | rpc: keys.activate, keys.preview. keys.found event (clipboard key found) not handled. |
| Toast / notice stack (with actions, tones, auto-dismiss) | components/shell/NoticeStack.tsx:18 | exo-ui/src/state.rs:2570 | PARTIAL | Plain text notices only; app.notify / notifyClear events not handled; no actions. |
| Anti-cheat missing notice (install anti-cheat prompt) | components/shell/AntiCheatNotices.tsx:31 | — | MISSING | anticheat.missing event not handled. |
| Store outage notices | components/shell/StoreOutageNotices.tsx:57 | — | MISSING | rpc: stores.outage. stores.outage event/RPC absent. |
| Discord DM / ping toasts | components/shell/DiscordNotices.tsx:12 | — | MISSING | rpc: discord.notify, discord.dmLine. discord.notify/dmLine events not handled. |
| Driver install overlay | components/shell/DriverInstallOverlay.tsx:140 | — | MISSING | rpc: gpu.driverInstall. gpu.driverInstall event not handled. |
| Boot gate + error boundary (splash, retry, crash screen) | components/shell/BootGate.tsx:87 | exo-ui/src/main.rs:143 | PARTIAL | rpc: shell.bootstrap, shell.ready. No splash/retry UI; host-down state silently renders empty rooms; ErrorBoundary equivalent absent. |
| Gamepad bar / controller navigation | components/shell/GamepadBar.tsx:41 | exo-ui/src/gamepad.rs:1 | PARTIAL | 173 lines: focus ring + hint bar; no Y/X actions, no per-room mapping, no wiggle. |
| Global keyboard shortcuts (Ctrl+F, Ctrl+A, Alt+arrows, etc.) | components/shell/useRoomController.ts:312 | exo-ui/src/main.rs:78 | PARTIAL | Only Ctrl+K, Ctrl+, , Ctrl+1..7, Escape. Missing Ctrl+F library search, Ctrl+A select-all, Discord Alt/Ctrl nav, Music shortcuts. |
| Command palette (rooms, settings, games, actions) | components/shell/launcherCommands.ts:1 | exo-ui/src/palette.rs:14 | PARTIAL | Has Rooms, PC sections, Settings sections, games, Add game, Refresh, Activate key, Resume setup, Check updates. |
| Palette: Take the tour, Minimize/Close window, Play/Install selected, note commands, Discord DMs/servers/channels, music controls | components/shell/paletteExtras.ts:1 | — | MISSING | rpc: discord.home, music.control. All absent natively. |
| Tour (guided walkthrough) | components/shell/ExoTour.tsx:54 | exo-ui/src/setup.rs:25† | DONE | Delivered by native-onboarding branch (live tour). |
| Report a problem entry points (Settings, palette, error screen) | components/settings/ReportProblem.tsx:20 | — | MISSING | rpc: report.preview, report.send. See Settings. |

## Library

Totals: DONE 6, PARTIAL 7, MISSING 6, BROKEN 2 (of 21).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Hero / home deck (selected game, Play, ... menu) | components/shell/HomeDeck.tsx:15 | exo-ui/src/library.rs:409 | PARTIAL | rpc: library.get. Smoke: renders correctly with covers; press on the card is not tracked (room-shell-library report). |
| Shelf row of covers (scroll, arrows, focus ring) | components/shell/LibraryShelf.tsx:1 | exo-ui/src/library.rs:856 | DONE |  |
| Multiple shelves (Continue playing, Recently added, Favorites, Folders) | components/shell/useShelves.ts:51 | exo-ui/src/library.rs:856 | PARTIAL | Single shelf + All games; no dynamic shelf set. |
| All games grid | components/shell/LibraryPane.tsx:23 | exo-ui/src/library.rs:298 | DONE | rpc: library.get.  |
| Library search overlay | components/shell/useLibrarySearch.ts:38 | exo-ui/src/library.rs:10 | DONE | shell-bar-press-search uninvestigated. |
| Sort (name, recent, playtime, size) | components/shell/LibraryHead.tsx:132 | exo-ui/src/library.rs:808 | DONE |  |
| Filter popover (Genre, Mode, Friends, Store) | components/shell/LibraryHead.tsx:4 | exo-ui/src/library.rs:10 | PARTIAL | Genre/Mode/Friends added by room-shell-library; React also filters by store/tag/installed. |
| Select mode + LibrarySelectBar (multi-select, bulk hide/uninstall/folder) | components/shell/LibrarySelectBar.tsx:14 | — | MISSING | Absent. |
| Folders (create, edit, cover cropper, reorder, move earlier/later, delete) | components/shell/FolderSheet.tsx:23 | exo-ui/src/menus.rs:352 | BROKEN | rpc: library.folderArt. menus.rs:722 and :1591 push "Folder management coming soon" toast; FolderSheet/Card/Panel/Cropper all absent. |
| Drag to reorder / drop into folder | components/shell/FolderCard.tsx:22 | — | MISSING | No drag-drop anywhere in native. |
| Gifts shelf (claimable Steam/Epic gifts) | components/shell/GiftsShelf.tsx:75 | — | MISSING | rpc: gifts.list, gifts.answer. gifts.changed event not handled. |
| Add game (+ button, file picker, portable) | components/shell/LibraryHead.tsx:1 | exo-ui/src/library.rs:584 | PARTIAL | Button + palette entry exist; no custom-game metadata editing. |
| Refresh all (library.refreshAll, folder art refresh) | components/shell/useLiveLibraryRefresh.ts:11 | exo-ui/src/state.rs:3035 | PARTIAL | rpc: library.refresh. library.refreshAll never called. |
| Game card context menu: Play / Install | components/shell/GameMenu.tsx:9 | exo-ui/src/menus.rs:311 | DONE | rpc: game.launch.  |
| Game card menu: Play as <account> | components/shell/GameMenu.tsx:76 | — | MISSING | rpc: game.launch. No multi-account launch. |
| Game card menu: Open folder, Properties, Hide/Unhide | components/shell/GameMenu.tsx:78 | exo-ui/src/menus.rs:333 | DONE | rpc: game.openFolder.  |
| Game card menu: New folder / add to folder | components/shell/GameMenu.tsx:82 | exo-ui/src/menus.rs:348 | BROKEN | Shows "Folder management coming soon". |
| Folder card menu (Edit, Move earlier/later, Delete) | components/shell/GameMenu.tsx:25 | — | MISSING |  |
| Running game state + Stop; hold windows (wait for game) | components/shell/useGameHolds.ts:8 | exo-ui/src/library.rs:29 | PARTIAL | rpc: game.stop. game.optimizeHeld / game.optimizedAgain events not handled; no HoldWindow. |
| Keyboard grid nav / type-ahead | components/shell/LibraryPane.tsx:304 | — | MISSING |  |
| Library empty / loading / error states | components/shell/LibraryPane.tsx:30 | exo-ui/src/library.rs:215 | PARTIAL | Empty ok; host error state not surfaced. |

## Game page

Totals: DONE 22, PARTIAL 0, MISSING 0, BROKEN 0 (of 22).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Page hero (art, logo, title, meta) | components/GamePage.tsx:50 | exo-ui/src/game_page.rs:405 | DONE | rpc: game.get. Includes driveHeat warning, live status pill, non-installed ArtworkStrip. |
| Primary action states (Play / Install / Update / Pause / Resume / Queued / Stop / Buy) | components/game/PrimaryAction.tsx:6 | exo-ui/src/game_page.rs:163 | DONE | rpc: game.launch, game.install, game.update, game.pauseInstall, game.cancelInstall, game.stop, game.dequeue, game.resumeDownload, game.progress, shell.openUrl. |
| Download / install progress bar + speed + ETA | components/game/PrimaryAction.tsx:16 | exo-ui/src/game_page.rs:315 | DONE | rpc: game.progress + install.progress event; edge progress bar, bytes, speed, ETA. |
| Performance card (OptimizeLine, shaders, FSR/DLSS summary, HDR, frame gen) | components/game/OptimizeLine.tsx:29 | exo-ui/src/game_perf.rs:187 | DONE | rpc: game.settings, keep.status, dlss.status, dlss.frameGen, gpu.game, hdr.game, hdr.state, shaders.status. Zero fake data. |
| Display mode (windowed / fullscreen / borderless) | components/game/OptimizeLine.tsx:87 | exo-ui/src/game_perf.rs:354 | DONE | rpc: game.settings (windowMode persisted to host). |
| Frame generation row (DLSS FG / AFMF / Lossless) | components/game/FrameGenRow.tsx:28 | exo-ui/src/game_perf.rs:260 | DONE | rpc: dlss.frameGen, gpu.game + Smooth Motion switch. |
| HDR row | components/game/HdrRow.tsx:23 | exo-ui/src/game_perf.rs:277 | DONE | rpc: hdr.game, hdr.state, settings.set (hdrForGames Turn on). |
| Compress row (CompactOS per game) | components/game/CompressRow.tsx:110 | exo-ui/src/game_props.rs:475 | DONE | rpc: compress.state (auto-sample), compress.act + compress.progress event. |
| Shader pre-compile row | components/game/ShaderPrecompile.tsx:10 | exo-ui/src/game_perf.rs:232 | DONE | rpc: shaders.status + shaders.changed event + Installed files Rebuild shaders. |
| Properties: General (launch options, language, Steam overlay) | components/game/GameProperties.tsx:388 | exo-ui/src/game_props.rs:254 | DONE | rpc: game.properties, game.properties.set (fixed steamOverlay on/off). |
| Properties: Installed files (Verify, Browse, Uninstall) | components/game/GameProperties.tsx:452 | exo-ui/src/game_props.rs:647 | DONE | rpc: game.repair, game.uninstall, game.openFolder. |
| Properties: Betas (switch branch, access code) | components/game/GameProperties.tsx:561 | exo-ui/src/game_props.rs:956 | DONE | rpc: game.properties, game.properties.set (beta + secure masked betaCode). |
| Properties: DLC | components/game/GameProperties.tsx:583 | exo-ui/src/game_props.rs:1057 | DONE | rpc: game.properties, game.properties.set (content toggles + confirm + states). |
| Properties: Artwork (refetch, replace, reset) | components/game/Artwork.tsx:19 | exo-ui/src/game_props.rs:196 | DONE | rpc: art.refetch, art.replace, art.reset. |
| Properties: Upscaler files (DLSS/FSR/XeSS swap, restore, consent) | components/game/GameProperties.tsx:504 | exo-ui/src/game_props.rs:869 | DONE | rpc: dlss.consent, dlss.restore, dlss.status. |
| Properties: Anti-cheat choice | components/game/GameProperties.tsx:428 | exo-ui/src/game_props.rs:424 | DONE | rpc: game.properties.set (antiCheatLaunch + confirm). |
| Properties: Cloud saves / Steam Input | components/game/GameProperties.tsx:409 | exo-ui/src/game_props.rs:348 | DONE | rpc: game.properties, game.properties.set (cloudSync, throughSteam, storeProperties). |
| Properties: Move to another library folder / size on disk | components/game/GameProperties.tsx:464 | exo-ui/src/game_props.rs:815 | DONE | rpc: game.properties, game.properties.set (folder + live move progress polling). |
| Achievements (list, progress, rarity) + pop-up | components/game/GameSections.tsx:122 | exo-ui/src/game_page.rs:517 | DONE | rpc: game.extras + game.achievements event + progress bar (in-game banners are host-side). |
| Playtime / last played stats | components/game/GameSections.tsx:1 | exo-ui/src/game_page.rs:505 | DONE | rpc: game.get. Matches React relative last played + omits unknown facts. |
| Friends who play | components/game/GameSections.tsx:8 | exo-ui/src/game_page.rs:41 | DONE | Matches React GameSections.tsx: GameShared Steam Family badge + multi-source GameStore switcher. |
| Store page link / Buy again | components/game/PrimaryAction.tsx:25 | exo-ui/src/game_page.rs:233 | DONE | rpc: shell.openUrl (hostedBuyUrl + Buy again / Buy on Steam / GOG / browser). |

## Store

Totals: DONE 16, PARTIAL 1, MISSING 0, BROKEN 0 (of 17).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Store room Discover tab (hero, shelves, tiles) | components/store/StoreRoom.tsx:49 | exo-ui/src/store.rs:957 | DONE | rpc: store.home. Fixed StoreTile::similar_to Vec<String> deserialization and all shelves render. |
| Store hero hover / pick list | components/store/StoreRoom.tsx:201 | exo-ui/src/store.rs:777 | DONE | 8s auto-rotate with countdown bar, hover pause, reduce-motion pause, active pick click opens game, Steam logo/tags. |
| Wishlist tab | components/store/storeData.ts:265 | exo-ui/src/store.rs:582 | DONE | rpc: store.wishlist. Loads store.wishlist(get), refreshes on store.changed(wishlist), empty/loading/error states. |
| Store search (results, empty, loading) | components/store/StoreRoom.tsx:61 | exo-ui/src/store.rs:724 | DONE | rpc: store.search. Debounced 250ms search-as-you-type, clear button, empty/error states. |
| Tile card: price, discount, store logo | components/store/StoreCard.tsx:87 | exo-ui/src/store.rs:248 | DONE | Steam poster/header fallback, live wishlist bookmark badge, price/discount/was formatting. |
| Tile card: "Library" button on owned games | components/store/StoreCard.tsx:87 | exo-ui/src/store.rs:348 | DONE | Matches React useOwned; hides price/reviews and opens game directly in Library. |
| Check prices / On sale split | components/store/StoreRoom.tsx:385 | exo-ui/src/store.rs:610 | DONE | rpc: store.wishlist. Calls store.wishlist(check), splits On sale now vs Not on sale, handles store.sale push toast. |
| Store game page: offers (open in store) | components/store/StoreGamePage.tsx:45 | exo-ui/src/store_page.rs:1244 | DONE | rpc: store.game, store.open. |
| Store game page: offer rows (store names, prices, historical low) | components/store/StoreGamePage.tsx:45 | exo-ui/src/store_page.rs:1244 | DONE | rpc: store.game, store.gameOffers, store.open, shell.openUrl. Displays real store names (Steam, Epic Games Store, GOG, Ubisoft), GG.deals link, owned Library primary button; StoreLowest::formatted_date() formats ISO dates cleanly. |
| Store game page: hero art / header image | components/store/StoreGamePage.tsx:4 | exo-ui/src/store_page.rs:1143 | DONE | rpc: store.game. Fixed 80px Steam logo slot with title fallback, plus 16:9 media stage with header/hero fallback. |
| Store game page: fact chips (price, reviews, Metacritic, Released) | components/store/StoreGamePage.tsx:243 | exo-ui/src/store_page.rs:1526 | DONE | 10-cell Facts card matching React StoreGamePage. |
| Store game page: trailers (HLS player) | components/store/TrailerPlayer.tsx:24 | exo-ui/src/store_media.rs:172 | PARTIAL | Wired via rune_media::Player::url with play/pause/mute/scrubber and graceful poster fallback; blocked on HLS playback (video_decoder.rs is Discord WebRTC elementary packets only; desktop Media Foundation IMFSourceResolver in rune-media rejects .m3u8 playlists). |
| Store game page: screenshot strip + lightbox | components/store/StoreGamePage.tsx:28 | exo-ui/src/store_media.rs:172 | DONE | 16:9 stage + horizontal thumbnail strip (600x338 thumbs) + fullscreen lightbox overlay with prev/next/counter/Esc/arrow keys. |
| Store game page: system requirements | components/store/StoreGamePage.tsx:29 | exo-ui/src/store_page.rs:1632 | DONE | All 7 hardware rows with This PC comparison marks/tones, Minimum/Recommended rows, and verdict pill. |
| Store game page: tags, About card, More like this | components/store/StoreGamePage.tsx:29 | exo-ui/src/store_page.rs:1168 | DONE | Single-row tag pills, About card, and More like this horizontal shelf footer. |
| Store game page: wishlist toggle | components/store/StoreGamePage.tsx:34 | exo-ui/src/store_page.rs:1357 | DONE | rpc: store.wishlist. Optimistic add/remove with green check and error rollback. |
| Game reviews | components/store/StoreGamePage.tsx:243 | exo-ui/src/store_page.rs:1526 | DONE | rpc: store.gameReviews. Fetches Recent reviews and renders in Facts card alongside all-time Reviews. |

## Discord

Totals: DONE 25, PARTIAL 9, MISSING 11, BROKEN 0 (of 45).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Sign-in (QR / token) and account switch | components/FriendsRoomRoute.tsx:1 | exo-ui/src/discord.rs:690 | DONE | rpc: discord.signIn, discord.accounts. accounts RPC namespace (add/switch) not called. |
| DM list, pinned DMs, unread dots, search | components/discord/Messages.tsx:17 | exo-ui/src/discord.rs:1817 | DONE | rpc: discord.home. Message requests + group edit (Messages.tsx) missing. |
| Create DM / group DM (select friends, 10 limit) | components/discord/Messages.tsx:226 | exo-ui/src/discord_threads.rs:100 | DONE | WP6: Create DM / group DM modal with friend search and 9-friend (10-member) limit via discord.channel. |
| Friends tab (Online / All / Pending / Blocked / Add friend) | components/discord/HomePage.tsx:1 | exo-ui/src/discord.rs:2602 | DONE | rpc: discord.friends. Smoke: renders Active now/Online with rich profile cards, search, Add friend. |
| Server rail (icons, unread pips, folders, reorder) | components/discord/DiscordClient.tsx:1 | exo-ui/src/discord.rs:2195 | DONE | rpc: discord.home, discord.server. Smoke: rail with unread pips renders; folder tiles exist (band_folder_tile); drag reorder absent. |
| Channel list (categories, text, voice, collapse) | components/discord/Channels.tsx:4 | exo-ui/src/discord.rs:2030 | DONE | rpc: discord.channels. Channel status set/clear (voice) skipped per room-discord report. |
| Message list (virtualized, jump to present, unread bar) | components/discord/Chat.tsx:13 | exo-ui/src/discord_chat.rs:181 | DONE | rpc: discord.messages.  |
| Message rendering: markdown, mentions, embeds, attachments | components/discord/Message.tsx:11 | exo-ui/src/discord_chat.rs:1129 | DONE | WP6: Full markdown (bold/italic/underline/strike/code/spoiler/links/mentions/channels/roles/subtext/timestamps), embeds, stickers, forwarded snapshots, thread-start/pin/call/system rows, Spotify Jam / gift / invite link cards. |
| Composer: send, reply, edit, typing | components/discord/Composer.tsx:154 | exo-ui/src/discord_chat.rs:1870 | DONE | WP6: Multi-line auto_grow composer, drafts per channel, reply @On/@Off, edit, typing, OS file drop + virtual_cols pending strip, spoiler/alt/Modify attachment, upload-as-file, char counter. |
| Emoji picker, GIF picker, sticker picker | components/discord/Composer.tsx:5 | exo-ui/src/discord_pickers.rs:180 | DONE | WP6: Expression picker with Emoji, GIF (trending + favorites + search), and Sticker tabs via discord.emojis, discord.emojiFavorites, discord.gifs, discord.gifFavorites, discord.stickers. |
| Reactions: add, remove, who reacted | components/discord/Message.tsx:414 | exo-ui/src/discord_pickers.rs:1542 | DONE | WP6: Normal + super reactions add/remove via discord.react, plus Who Reacted dialog with per-emoji tabs and moderator remove via discord.reactors. |
| Message menu (reply, edit, pin, copy, delete, report, forward) | components/discord/Message.tsx:1 | exo-ui/src/menus.rs:1183 | DONE | WP6: Complete DiscordMessage context menu: Reply, Edit, Pin/Unpin, Forward, Create thread, Bookmark, Remind me submenu, View/Clear reactions, Mark unread, Copy text/link/ID, Remove embeds, Delete, Report. |
| Threads and forum channels | components/discord/Forum.tsx:4 | — | MISSING | rpc: discord.thread, discord.threads.  |
| Polls (create, vote, results) | components/discord/PollCreator.tsx:23 | exo-ui/src/discord_threads.rs:295 | DONE | WP6: Poll creator modal, inline poll card voting/remove-vote/end-poll/results via discord.poll, and real-time discord.pollVote push event registered in events.rs. |
| Message search (filters) | components/discord/SearchBar.tsx:1 | exo-ui/src/discord_search.rs:242 | DONE | WP6: Search drawer with inline filter chips (from:, in:, has:, mentions:, before:, on:/during:, after:, pinned:, authorType:), quick filters, Newest/Oldest/Relevant sort, pagination, and jump via discord.search. |
| Pins panel | components/discord/ChatHeader.tsx:1 | exo-ui/src/discord_search.rs:169 | DONE | WP6: Pins drawer with discord.pins fetch, jump-to-message, and unpin. |
| Inbox (mentions, unreads) | components/discord/Inbox.tsx:16 | exo-ui/src/discord_search.rs:550 | DONE | WP6: Full Inbox panel with For you (discord.forYou), Unreads (message peeks + mark channel/server read), Mentions (discord.mentions + filter toggles + dismiss), Bookmarks, and Reminders. |
| Saved messages | components/discord/MessageTools.tsx:1 | exo-ui/src/discord_search.rs:550 | DONE | WP6: Saved messages Bookmarks & Reminders tabs in Inbox plus Bookmark and Remind me actions in message menu via discord.saved. |
| Member list (roles, status, presence) | components/discord/MemberList.tsx:1 | exo-ui/src/discord.rs:1246 | DONE | rpc: discord.members.  |
| User profile popover/card (roles, mutual, note, badges) | components/discord/profileParts.tsx:1 | exo-ui/src/discord_threads.rs:1536 | DONE | WP6: Full DM user profile sidebar + popout & full modal with banner, overlapping avatar, badges, bio, Member since, roles + role editor (discord.admin), connections, mutual friends/servers, and editable note via discord.profile. |
| User menu (message, call, nickname, timeout, kick/ban, report) | components/discord/UserMenu.tsx:71 | exo-ui/src/menus.rs:1288† | PARTIAL | No change nickname, timeout, transfer ownership, report. |
| Status menu (online / idle / dnd / invisible / custom) | components/discord/MeMenus.tsx:1 | exo-ui/src/discord.rs:1435 | DONE | rpc: discord.setStatus, discord.status.  |
| Voice channel join / leave / mute / deafen | components/discord/voice.tsx:31 | exo-ui/src/discord_voice.rs:2 | DONE | rpc: discord.voice.  |
| Voice call stage (tiles, speaking ring, blurred avatar backdrop, Members column, fit-to-stage solo tile) | components/discord/voice.tsx:262 | exo-ui/src/discord_voice.rs:686 | PARTIAL | rpc: discord.voice. Missing backdrop, right Members column, solo tile sizing; speaking ring unverified (room-discord report). |
| Camera and screen share (Go Live picker, preview, stream sound) | components/discord/GoLivePicker.tsx:1 | exo-ui/src/discord_voice.rs:229 | PARTIAL | rpc: discord.golive, discord.stream, discord.camera. Popover position wrong; no "Change what you share", hide preview. |
| Stage channels (start, audience, move to audience) | components/discord/voice.tsx:367 | — | MISSING |  |
| Soundboard + call songs | components/discord/voice.tsx:213 | — | MISSING | rpc: discord.soundboard, discord.sound, discord.callSongs. discord.callSong event not handled. |
| Mic test / monitor | components/discord/settings/VoicePage.tsx:253 | — | MISSING | rpc: discord.micTest, discord.micMonitor. discord.micLevel event not handled. |
| Incoming-call ring / call UI over any room | components/shell/ExoIslandCall.tsx:20 | — | MISSING | rpc: discord.call. discord.call / discord.tts events not handled. |
| Server menu (invite, create channel/category/event, notifications, leave) | components/discord/ServerMenu.tsx:1 | exo-ui/src/menus.rs:1092† | PARTIAL | Missing create category/event, privacy settings, per-server profile, report. |
| Channel menu (mute, mark read, edit, delete, copy link) | components/discord/Channels.tsx:20 | exo-ui/src/menus.rs:960† | PARTIAL | Channel status items skipped. |
| Server settings (overview, roles, emoji, moderation, automod, onboarding, guide) | components/discord/ServerSettings.tsx:45 | — | MISSING | rpc: discord.admin. 10+ settings pages: settingsRoles, settingsExpressions, settingsModeration, settingsAutomod, settingsOnboarding, settingsGuide, settingsAccess, settingsRoleLinks, settingsPages, ServerSettings. None natively. |
| Channel settings (overview, permissions, slowmode) | components/discord/ChannelSettings.tsx:34 | — | MISSING | rpc: discord.admin.  |
| Server pages (events, resources, get started, members) | components/discord/serverPages.tsx:1 | — | MISSING | rpc: discord.admin. events.tsx: create/start/edit/cancel events. |
| Invite dialog | components/discord/InviteDialog.tsx:30 | exo-ui/src/discord_threads.rs:686 | DONE | WP6: Invite dialog with friend search + send invite, copy invite link, and Edit invite link settings (expiry/max uses/temporary) via discord.channel action invite. |
| Shop (collectibles, bundles, payment) | components/discord/ShopPage.tsx:124 | exo-ui/src/discord.rs:3434 | PARTIAL | rpc: discord.shop. Placeholder title only. |
| User settings: Account / Profile | components/discord/settings/AccountPages.tsx:1 | exo-ui/src/discord.rs:3871† | PARTIAL | rpc: discord.account, discord.userSettings. discord.userSettings event not handled. |
| User settings: Voice and video (devices, volumes, noise suppression, echo, AGC) | components/discord/settings/VoicePage.tsx:19 | — | PARTIAL | rpc: discord.clientSettings. clientSettings noise suppression / echo / AGC not wired (room-discord report). |
| User settings: Notifications / App / Privacy / Keybinds | components/discord/settings/AppPages.tsx:1 | — | MISSING | rpc: discord.userSettings, discord.account. discord.keybind event not handled (global push-to-talk). |
| Report message/user/server | components/discord/Report.tsx:2 | — | MISSING | rpc: discord.report.  |
| Link rows / image & file viewer, fileText, image save | components/discord/extras.tsx:1 | exo-ui/src/discord_chat.rs:2417 | DONE | WP6: Rune modal Lightbox gallery (prev/next, zoom, drag-pan, Esc) + Copy/Save/Open original actions (discord.image, shell.openUrl), right-click image menu, discord.fileText inline preview + expand/collapse, virtual_cols multi-image strips. |
| Colors / font / Streamer mode | components/discord/DiscordClient.tsx:1 | — | MISSING | rpc: discord.colors, discord.font. discord.colors event not handled. |
| Interactions (slash commands, bot apps, buttons) | components/discord/Composer.tsx:443 | exo-ui/src/discord_pickers.rs:1094 | DONE | WP6: Slash command list + inline CommandForm with option autocomplete, bot message components (buttons/selects), AppModal overlay, ephemeral dismiss via discord.apps & discord.interaction push event. |
| Mentionable autocomplete | components/discord/Composer.tsx:32 | exo-ui/src/discord_pickers.rs:815 | DONE | WP6: Mentionable autocomplete for @user, @role, @everyone/@here, #channel, and :emoji: with raw token expansion via discord.mentionable. |
| Top-bar cursor over open menu | components/discord/Chat.tsx:1 | — | PARTIAL | Blocked on a Rune change (room-discord report). |

## Music

Totals: DONE 5, PARTIAL 7, MISSING 9, BROKEN 0 (of 21).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Service sign-in (Spotify / YouTube Music / Apple Music) | components/music/MusicSignIn.tsx:1 | exo-ui/src/music.rs:736 | DONE | rpc: music.signIn, spotify.start, ytmusic.start, applemusic.start. spotify.link (Spotify Connect link) not called. |
| Home (shelves, top picks) | components/music/pages.tsx:32 | exo-ui/src/music.rs:7 | DONE | rpc: spotify.view, ytmusic.view. Empty-state copy ("Nothing on Home yet") missing. |
| Search + Top result / Songs / Artists / Albums | components/music/pages.tsx:98 | exo-ui/src/music.rs:910 | PARTIAL | rpc: spotify.view. No Top result card, Station, Episodes sections. |
| Library (songs, albums, artists, playlists) | components/music/MusicPlayer.tsx:33 | exo-ui/src/music.rs:276 | DONE |  |
| Playlist page + create playlist sheet (name, description, cover) | components/music/PlaylistSheet.tsx:35 | exo-ui/src/music.rs:364 | PARTIAL | rpc: spotify.act, ytmusic.act, applemusic.act. Create only; no description/cover/rename/delete. |
| Artist page (header, top songs, discography, fans also like, on tour) | components/music/artist.tsx:1 | — | MISSING | rpc: spotify.view.  |
| Album page / discography | components/music/pages.tsx:65 | exo-ui/src/music.rs:398 | PARTIAL | No discography page, singles/EPs/compilations. |
| Radio / stations | components/music/pages.tsx:94 | exo-ui/src/music.rs:319 | PARTIAL | Nav exists, station start unverified. |
| Podcasts / episodes | components/music/pages.tsx:121 | — | MISSING |  |
| Now playing bar (seek, shuffle, repeat, like, volume) | components/music/MusicPlayer.tsx:8 | exo-ui/src/music.rs:1180 | DONE | rpc: music.control, music.volume, music.playback.  |
| Queue panel (next, history, autoplay, reorder, clear) | components/music/panels.tsx:28 | exo-ui/src/music.rs:992 | PARTIAL | rpc: music.playback. Read-only list capped at 30; no history, autoplay, reorder. |
| Lyrics panel | components/music/panels.tsx:13 | — | MISSING | rpc: music.playback.  |
| Connect / Play on devices | components/music/panels.tsx:145 | — | MISSING | rpc: spotify.link.  |
| Friend activity + Listen along | components/music/panels.tsx:204 | — | MISSING | rpc: spotify.view.  |
| Immersive now-playing | components/music/Immersive.tsx:19 | exo-ui/src/music.rs:1429 | DONE |  |
| Sound panel (exclusive mode, crossfade, gapless, AutoMix, normalize, EQ) | components/music/SoundPanel.tsx:44 | — | MISSING | rpc: audio.formats, audio.setFormat, eq.headphones.  |
| Song matching / warm cache (YTM resolves Spotify tracks) | components/music/MusicPlayer.tsx:1 | — | MISSING | rpc: music.match, music.warm.  |
| Library filter folder / show-and-sort | components/music/MusicPlayer.tsx:501 | — | MISSING |  |
| Music room first-run / no-service state | components/music/MusicSignIn.tsx:1 | exo-ui/src/music.rs:717 | PARTIAL | rpc: applemusic.start. Smoke: room sits on "Starting Apple Music…" with empty Home and "Nothing playing" (this data copy is not signed in to a service; React shows MusicSignIn / MusicWait with Change service). |
| Music keyboard shortcuts | components/shell/useRoomController.ts:4 | — | MISSING |  |
| Track context menus (add to playlist, go to artist, share) | components/music/controls.tsx:1 | exo-ui/src/menus.rs:1 | PARTIAL | Few items. |

## Exo AI / PC

Totals: DONE 0, PARTIAL 8, MISSING 12, BROKEN 1 (of 21).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Exo AI room: Auto Tune card (stage list, live progress, undo) | components/gpu/AutoTune.tsx:4 | exo-ui/src/pc.rs:1344 | BROKEN | rpc: system.tune, autopilot.state, exoAi.state, exoAi.set. pc.rs:~1615-1700: six stage rows and "Auto Tune complete" are hardcoded; descriptions like "No bloatware found", "Graphics & chipset drivers up to date" are static text, not read from the host. |
| Exo AI: Run again / Optimizing PC cinema | components/gpu/AutoTuneCinema.tsx:151 | exo-ui/src/pc.rs:1289 | PARTIAL | rpc: exoAi.set. Button calls exoAi.set; no cinema, no stage events, no Not now. |
| Auto Tune: PawnIO / unsupported CPU/GPU notice | components/gpu/AutoTune.tsx:139 | — | MISSING | rpc: shell.openUrl, system.tune.  |
| PC overview (hardware, Windows version, Secure Boot, TPM, memory integrity, BIOS) | components/gpu/PcOverview.tsx:1 | exo-ui/src/pc.rs:781 | PARTIAL | rpc: system.about, system.drives, system.tuning. Name/drives/tuning only; no Secure Boot/TPM/BIOS/recovery rows; bios.update never called. |
| Autopilot (state, what it found / did, pause, turn back on) | components/gpu/Autopilot.tsx:3 | exo-ui/src/pc.rs:1219 | PARTIAL | rpc: autopilot.state, autopilot.set. Shows state only; autopilot.set never called so cannot pause/resume. |
| Power & Autopilot group, system tweaks (restore, restart required) | components/gpu/SystemGroup.tsx:50 | — | MISSING | rpc: system.tune, system.health.  |
| Interrupts (MSI, DPC latency measure, drivers hold) | components/gpu/InterruptRows.tsx:19 | — | MISSING | rpc: system.interrupts.  |
| Display tab (refresh rate, HDR, calibrate, G-Sync, best settings) | components/gpu/DisplayTab.tsx:1 | exo-ui/src/pc.rs:1591 | PARTIAL | rpc: gpu.display, hdr.state. Opens a menu; gpu.display / calibration not called; only hdr.state read. |
| GPU driver row (latest, install, driver type, profile restore) | components/gpu/DriverRow.tsx:116 | — | MISSING | rpc: gpu.driver, gpu.profile. gpu.driver / gpu.driverInstall events not handled. |
| Driver profile (low latency, boost) | components/gpu/DriverProfile.tsx:26 | exo-ui/src/pc.rs:1013 | PARTIAL | rpc: gpu.profile. Read only. |
| Drivers & updates (chipset, Windows Update, BIOS, restart) | components/gpu/DriversSection.tsx:108 | — | MISSING | rpc: system.drivers, system.health, bios.update, controller.read. drivers.step / bios.progress events not handled. |
| AFMF 3 row | components/gpu/Afmf3Row.tsx:12 | — | MISSING | rpc: gpu.afmf3.  |
| GPU control panel open | components/gpu/PcPartMenus.tsx:69 | — | MISSING | rpc: gpu.controlPanel.  |
| Best Setup (use best settings) | components/gpu/BestSetup.tsx:107 | — | MISSING | rpc: system.health.  |
| Storage room (drives, scan space, drive settings) | components/gpu/StorageRoom.tsx:117 | exo-ui/src/pc.rs:1238 | PARTIAL | rpc: system.drives. Drives list; scan space and drive settings absent. |
| Compress games (all, idle, after updates) | components/gpu/CompressGames.tsx:14 | — | MISSING | rpc: compress.all, compress.act, compress.settings. compress.progress event not handled; native only compress.list. |
| Game runtimes (VC++/.NET list, install) | components/runtimes/RuntimesPanel.tsx:1 | — | MISSING | rpc: runtimes.list, runtimes.install. runtimes.progress event not handled. |
| Shaders board (pre-compile all, rebuild, cancel) | components/shaders/ShadersBoard.tsx:25 | exo-ui/src/pc.rs:1846 | PARTIAL | rpc: shaders.status, shaders.prepare.start, shaders.prepare.games, shaders.prepare.cancel, shaders.rebuild. Cancel / rebuild missing; shaders.event not handled. |
| Keep (CPU/GPU keep-awake status) | components/gpu/PcOverview.tsx:69 | — | MISSING | rpc: keep.status.  |
| FPS counter options | components/settings/GeneralSection.tsx:127 | — | MISSING | rpc: fps.counter. See Settings. |
| PC part menus (CPU, GPU, RAM, disk context menus) | components/gpu/PcPartMenus.tsx:69 | exo-ui/src/pc.rs:1 | PARTIAL | Display + others present; actions inside menus limited. |

## Apps

Totals: DONE 4, PARTIAL 1, MISSING 7, BROKEN 0 (of 12).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Installed apps list (icons, size, publisher, search) | components/apps/AppsPanel.tsx:110 | exo-ui/src/pc.rs:1307 | DONE | rpc: apps.list, apps.icons.  |
| Uninstall (confirm, progress, leftovers) | components/apps/AppsPanel.tsx:77 | exo-ui/src/pc_actions.rs:181 | PARTIAL | rpc: apps.uninstall. apps.confirm / apps.clean (leftover scan) / apps.leave not called. |
| Tidy / clean up apps | components/apps/AppsPanel.tsx:120 | exo-ui/src/pc_actions.rs:24 | DONE | rpc: apps.tidy.  |
| Scan for unused apps ("Not opened in three months") | components/apps/AppsPanel.tsx:323 | — | MISSING | rpc: apps.scan.  |
| Restore point + Undo | components/apps/AppsPanel.tsx:98 | — | MISSING | rpc: apps.restorePoint, apps.undo.  |
| Installer cache (scan, clean) | components/apps/AppsPanel.tsx:821 | — | MISSING | rpc: apps.installerCache, apps.installerCacheClean.  |
| Default apps open | components/apps/AppsPanel.tsx:744 | — | MISSING | rpc: apps.openDefaults.  |
| Replaced apps (remove duplicates) | components/apps/AppsPanel.tsx:195 | — | MISSING | rpc: replaced.remove.  |
| Packages: plan / remove / add back | components/gpu/autoTuneStages.ts:212 | — | MISSING | rpc: apps.packages, apps.packagesPlan, apps.packagesRemove, apps.packageAddBack.  |
| Startup apps (list, remove, restore, clean) | components/apps/AppsPanel.tsx:14 | exo-ui/src/pc.rs:2158 | DONE | rpc: startup.list, startup.remove, startup.clean, startup.restore.  |
| Junk files (scan, clean) | components/apps/AppsPanel.tsx:3 | exo-ui/src/pc.rs:1066 | DONE | rpc: junk.scan, junk.clean.  |
| Edge allow, web preferences (default browser behaviour) | components/settings/AppsSection.tsx:1 | — | MISSING | rpc: apps.allowEdge, web.preferences.get, web.preferences.set.  |

## Devices

Totals: DONE 3, PARTIAL 3, MISSING 4, BROKEN 1 (of 11).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Device list (mouse, keyboard, audio, controller) + images + Rescan | components/DevicesRoom.tsx:48 | exo-ui/src/pc.rs:553 | DONE | rpc: devices.list, devices.image.  |
| Mouse panel (DPI, buttons, lighting, presets, profiles) | lib/mouseRelay.ts:1 | exo-ui/src/pc_actions.rs:858 | PARTIAL | rpc: mouse.panel.open, mouse.presets.get, mouse.presets.set, mouse.profiles.get, mouse.profiles.set, hid.list, hid.open, system.mouseCore. Native embeds the host mouse panel window; presets/profiles/hid RPCs not called by native (the embedded panel uses them). Smoke: the snapshot shows only a "Wireless" card for the dongle; the host-embedded mouse window is not captured by --snap, so embed could not be verified headless. |
| Keyboard page (Wooting: layouts, actuation, rapid trigger, onboard profiles) | components/devices/KeyboardPage.tsx:46 | exo-ui/src/pc.rs:181† | DONE | rpc: keyboard.*. wooting-native branch: real layouts, capability gating, error recovery. Tachyon mode / USB port rows to verify. |
| Audio page (output/input, volume, mute, format, Exo Voice, noise suppression) | components/devices/AudioPage.tsx:23 | exo-ui/src/pc.rs:401 | PARTIAL | rpc: audio.read, audio.setVolume, audio.setMute, audio.formats, audio.setFormat. No format picker, Exo Voice, Voice Focus, input sensitivity. |
| EQ (Exo EQ, AutoEQ headphones, Equalizer APO install) | components/devices/EqPanels.tsx:1 | exo-ui/src/pc.rs:45 | PARTIAL | rpc: eq, eq.headphones. eq.headphones (AutoEQ index) never called; no Equalizer APO install/restart flow. |
| Controller page (battery, update, quality, measure, Steam Input) | components/devices/ControllerPage.tsx:24 | exo-ui/src/pc.rs:71 | BROKEN | rpc: controller.read, controller.watch, controller.measure, controller.quality, controller.test, steamInput.state. Native shows only connection/battery/polling card for controllers. controller.* (10 methods) never called. |
| Controller calibration (sticks, triggers, undo) | components/devices/Calibration.tsx:2 | — | MISSING | rpc: controller.calibrate, controller.test.  |
| Pad settings (light bar, player light, vibration, trigger effects, sound) | components/devices/PadSettings.tsx:2 | — | MISSING | rpc: controller.settings, controller.sound.  |
| Game profiles (per-game pad profiles) | components/devices/GameProfiles.tsx:38 | — | MISSING | rpc: controller.gameProfiles, controller.gameProfilesSave.  |
| Device detail fallback (connection, battery, polling) | components/DevicesRoom.tsx:1 | exo-ui/src/pc.rs:587 | DONE |  |
| HID relay events (hid.changed, hid.input, controller.state) | lib/mouseRelay.ts:1 | — | MISSING | rpc: hid.*. Events not handled. |

## Captures

Totals: DONE 0, PARTIAL 4, MISSING 5, BROKEN 1 (of 10).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Capture sheet grid (thumbnails, filters, chips, On this day) | components/captures/CapturesSheet.tsx:6 | exo-ui/src/captures.rs:755 | PARTIAL | rpc: capture.list, capture.item. Smoke: renders "Exo Capture is not installed" with the temp data copy (tools excluded), so the grid itself was not exercised. Missing on-this-day, chips filter empty state, waiting-for-game-end state. |
| Select, bulk delete, rename, open folder | components/captures/CapturesSheet.tsx:80 | exo-ui/src/captures.rs:307 | PARTIAL | rpc: capture.act, capture.edit.  |
| Send to (Discord, share) | components/captures/CapturesSheet.tsx:341 | — | MISSING | rpc: capture.send.  |
| Clip editor (trim, play, export) | components/captures/ClipEditor.tsx:180 | exo-ui/src/captures.rs:377 | PARTIAL | rpc: capture.edit, capture.item. video.frame / keyframe events handled; export unverified. |
| Snip editor (mark up, crop, copy text OCR, save) | components/captures/SnipEditor.tsx:167 | — | MISSING | rpc: capture.saveImage, capture.text.  |
| Notes buttons (capture menu + sheet header) | components/captures/CapturesSheet.tsx:4 | exo-ui/src/captures.rs:786 | BROKEN | captures.rs:~290 "Notes" menu row just reopens Captures; captures.rs:~786 header "Notes" button calls load_captures(). Neither opens notes. |
| Notes (Markdown editor, files, session, photo drop, spellcheck) | components/captures/NotesView.tsx:351 | — | MISSING | rpc: notes.list, notes.read, notes.save, notes.saveAs, notes.act, notes.open, notes.photo, notes.session, notes.spell, notes.textFiles. Entire Notes feature absent: 11 notes.* methods, notes.open event, note palette commands, Ctrl+N / Ctrl+O. |
| Capture settings (replay length, quality, save-to, storage cap, auto-delete, voice chat) | components/captures/CaptureSettings.tsx:4 | exo-ui/src/captures.rs:708 | PARTIAL | rpc: capture.settings. Most rows; Max storage, Auto-delete, Record voice chat, Print Screen snips to verify. |
| Highlight reel ("Making your reel") | components/captures/CapturesSheet.tsx:8 | — | MISSING | rpc: capture.reel.  |
| capture.shrunk event (compaction) | components/captures/CapturesSheet.tsx:298 | — | MISSING |  |

## Settings

Totals: DONE 6, PARTIAL 5, MISSING 12, BROKEN 0 (of 23).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Settings search + jump (38 entries) | components/SettingsPanel.tsx:48 | exo-ui/src/settings.rs:418 | PARTIAL | Search box filters the 6 native sections only; React indexes 38 entries across 13+ sections. Smoke: sidebar shows General, Library, Downloads, In game, Music, About. |
| General: text size, reduce motion, show tools | components/settings/GeneralSection.tsx:18 | exo-ui/src/settings.rs:99 | DONE | rpc: settings.get, settings.set.  |
| General: launch on startup | components/settings/GeneralSection.tsx:1 | exo-ui/src/settings.rs:99 | DONE | rpc: startup.list.  |
| General: FPS counter (position, 1% low, on/off) | components/settings/GeneralSection.tsx:127 | — | MISSING | rpc: fps.counter.  |
| General: achievement pop-up sound, test pop-up, OBS overlay URL | components/settings/GeneralSection.tsx:93 | — | MISSING | rpc: achievements.preview.  |
| General: install folder, download hours, auto-update games, update upscalers in anti-cheat games | components/settings/GeneralSection.tsx:175 | exo-ui/src/settings.rs:207 | PARTIAL | rpc: settings.set. Downloads section has some toggles; no install folder picker or hours. |
| Store accounts: sign in to Steam (QR / password / guard) | components/settings/SteamSignInRow.tsx:1 | — | MISSING | rpc: steam.signInQr, steam.signInPassword, steam.signInGuard, steam.signInPoll, steam.signInCancel, steam.signOut, steam.account.  |
| Store accounts: Epic / GOG / Ubisoft / EA accounts (add, switch, remove, import) | components/settings/StoreAccounts.tsx:2 | — | MISSING | rpc: accounts.list, accounts.add, accounts.switch, accounts.remove, accounts.signOut, accounts.epicAdd, accounts.epicImport, accounts.epicRemove, accounts.epicNewLauncher. accounts.progress event not handled. |
| Stores section (auth state, check, matrix, offline mode, Riot ID, get another store) | components/settings/StoresSection.tsx:132 | exo-ui/src/settings.rs:192 | PARTIAL | rpc: stores.auth, stores.check, stores.matrix, stores.playOffline, steam.setOffline, playtime.riotRefresh. Native has Riot sync + keep store clients quiet only. |
| Gifts (Steam gifts sign-in) | components/settings/StoresSection.tsx:94 | — | MISSING | rpc: gifts.list, gifts.steamSignIn, gifts.steamSignOut.  |
| Riot data export import / baseline | components/settings/StoresSection.tsx:397 | — | MISSING | rpc: playtime.importRiotExport, playtime.setRiotBaseline.  |
| Exo Account (create, sign in, password, recovery code, devices, export, delete) | components/settings/AccountSection.tsx:21 | exo-ui/src/settings.rs:289 | PARTIAL | rpc: exoAccount.state, exoAccount.sync, exoAccount.signOut, exoAccount.create, exoAccount.signIn, exoAccount.changePassword, exoAccount.newRecoveryCode, exoAccount.reset, exoAccount.sessions, exoAccount.revoke, exoAccount.export, exoAccount.delete. Only state, sync, sign out natively. |
| Backups (now, export, import, status, restore) | components/settings/BackupSection.tsx:19 | exo-ui/src/settings.rs:133 | DONE | rpc: backup.status, backup.now, backup.export, backup.import. native settings.rs has now/export/import buttons per summary; calls observed only for status, so wiring needs verification. |
| Privacy: what was sent, delete my data, telemetry opt-out | components/settings/PrivacySection.tsx:10 | — | MISSING | rpc: telemetry.sent, telemetry.delete.  |
| Report a problem (preview payload, attach logs, send, open on GitHub) | components/settings/ReportProblem.tsx:20 | — | MISSING | rpc: report.preview, report.send.  |
| In-game settings (overlay, FPS hotkeys) | components/SettingsPanel.tsx:1 | exo-ui/src/settings.rs:234 | DONE | rpc: settings.set.  |
| Music settings (service) | components/SettingsPanel.tsx:1 | exo-ui/src/settings.rs:7 | PARTIAL | rpc: settings.set.  |
| Apps settings (web preferences) | components/settings/AppsSection.tsx:31 | — | MISSING | rpc: web.preferences.get, web.preferences.set.  |
| About: version, Check for updates, Install update | components/settings/AboutSection.tsx:105 | exo-ui/src/settings.rs:289 | DONE | rpc: app.checkUpdate, app.installUpdate.  |
| About: Take the tour, Resume setup, Open logs, Repair cache | components/settings/AboutSection.tsx:117 | exo-ui/src/settings.rs:289 | DONE | rpc: app.openLogs, app.repairCache.  |
| About: Clear caches, Changelog, Licenses, Support Exo | components/settings/AboutSection.tsx:91 | — | MISSING | rpc: app.log, app.logsFor. Repair cache only. |
| Shader pre-compile settings | components/SettingsPanel.tsx:1 | — | MISSING | rpc: shaders.status.  |
| Discord settings pages (Account, Voice, App, Privacy, Keybinds) | components/discord/settings/AccountPages.tsx:1 | — | MISSING | rpc: discord.userSettings. See Discord room. |

## Onboarding & Account

Totals: DONE 0, PARTIAL 4, MISSING 6, BROKEN 0 (of 10).

| Feature | React ref | Native ref | Status | Notes |
|---|---|---|---|---|
| Setup flow (welcome, scan, stores, account, apps, cleanup, restore) | components/OnboardingPanel.tsx:1 | exo-ui/src/setup.rs:93† | PARTIAL | rpc: shell.bootstrap. native-onboarding branch: 6 stages built; React has AccountStep/AppsStep/CleanupStep/RestoreStep with RPC calls missing. |
| Setup: Account step (create / sign in / recovery code, probe) | components/onboarding/AccountStep.tsx:16 | exo-ui/src/setup.rs:866† | PARTIAL | rpc: exoAccount.probe, exoAccount.create, exoAccount.signIn, exoAccount.reset, exoAccount.newRecoveryCode. "Open account" does not sign in inline; exoAccount.* not called. |
| Setup: Recovery code screen (save, confirm) | components/onboarding/RecoveryCodeScreen.tsx:9 | — | MISSING | rpc: exoAccount.saveCode, exoAccount.codeConfirmed.  |
| Setup: Apps step (web preferences) | components/onboarding/AppsStep.tsx:15 | — | MISSING | rpc: web.preferences.get, web.preferences.set.  |
| Setup: Cleanup step (replaced apps remove) | components/onboarding/CleanupStep.tsx:25 | — | MISSING | rpc: replaced.remove, apps.packagesPlan.  |
| Setup: Restore step (restore from backup) | components/onboarding/RestoreStep.tsx:11 | — | MISSING | rpc: backup.restore, backup.decline. backup.stopped event not handled. |
| Setup: per-device bodies; PC defaults count | components/onboarding/PcStep.tsx:1 | exo-ui/src/setup.rs:165† | PARTIAL | rpc: devices.list. Per-device bodies not built; PC defaults count reads 0 in fixture. |
| Clients gate (store clients missing / fix) | components/clients/ClientsCard.tsx:1 | — | PARTIAL | rpc: clients.status, clients.fix. clients.status called; clients.fix never; no gate UI. components.failed event not handled. |
| Account sheet (from bar) / sign-in state | components/account/AccountShell.tsx:18 | — | MISSING | rpc: exoAccount.probe.  |
| Backup answer prompt (first-run restore/decline) | components/onboarding/RestoreStep.tsx:1 | — | MISSING | rpc: backup.decline.  |

## RPC coverage

### Methods React calls that native never calls (211)

| Namespace | Methods |
|---|---|
| discord (30) | account, admin, apps, callSongs, colors, emojiFavorites, emojis, fileText, font, forYou, gifFavorites, gifs, image, mentionable, mentions, micMonitor, micTest, poll, profile, reactors, report, saved, shop, sound, sticker, stickerData, stickers, thread, threads, userSettings |
| apps (15) | allowEdge, clean, confirm, history, installerCache, installerCacheClean, leave, openDefaults, packageAddBack, packages, packagesPlan, packagesRemove, restorePoint, scan, undo |
| exoAccount (14) | changePassword, codeConfirmed, create, delete, export, markAsked, newRecoveryCode, overview, probe, reset, revoke, saveCode, sessions, signIn |
| shell (12) | close, heartbeat, maximize, minimize, openUrl, painted, pickFolder, pickProgram, prepare, setChromeLayout, showStore, windowState |
| notes (11) | act, list, open, pending, photo, read, save, saveAs, session, spell, textFiles |
| controller (10) | calibrate, gameProfiles, gameProfilesSave, measure, quality, read, settings, sound, test, watch |
| accounts (9) | add, epicAdd, epicImport, epicNewLauncher, epicRemove, list, remove, signOut, switch |
| steam (9) | account, downloads, setOffline, signInCancel, signInGuard, signInPassword, signInPoll, signInQr, signOut |
| dlss (7) | consent, downloadNewer, frameGen, newer, restore, status, updateAll |
| hid (7) | close, list, open, receiveFeatureReport, receiveInputReport, sendFeatureReport, sendReport |
| backup (5) | decline, export, import, now, restore |
| game (5) | autoUpdate, dequeue, progress, resumeDownload, settings |
| gpu (5) | afmf3, controlPanel, display, driver, game |
| stores (5) | auth, check, matrix, outages, playOffline |
| system (5) | drivers, health, interrupts, mouseCore, tune |
| app (4) | log, logsFor, notify, pendingOpen |
| compress (4) | act, all, settings, state |
| downloads (4) | list, move, pauseAll, resumeAll |
| gifts (4) | answer, list, steamSignIn, steamSignOut |
| mouse (4) | presets.get, presets.set, profiles.get, profiles.set |
| art (3) | refetch, replace, reset |
| capture (3) | saveImage, send, text |
| runtimes (3) | cancel, install, list |
| shaders (3) | prepare.cancel, rebuild, status |
| telemetry (3) | delete, sent, track |
| audio (2) | formats, setFormat |
| library (2) | folderArt, refreshAll |
| music (2) | match, warm |
| playtime (2) | importRiotExport, setRiotBaseline |
| replaced (2) | remove, scan |
| report (2) | preview, send |
| web (2) | preferences.get, preferences.set |
| achievements (1) | preview |
| autopilot (1) | set |
| bios (1) | update |
| clients (1) | fix |
| deps (1) | list |
| eq (1) | headphones |
| fps (1) | counter |
| friends (1) | badge |
| hdr (1) | game |
| keep (1) | status |
| spotify (1) | link |
| steamInput (1) | state |
| store (1) | gameReviews |

Native-only or both: 134 methods referenced natively. Methods in the contract that neither side calls by literal name: 0.

### Push events React handles that native drops (42)

`accounts.progress`, `anticheat.missing`, `app.open`, `backup.stopped`, `bios.progress`, `capture.shrunk`, `components.failed`, `compress.progress`, `controller.state`, `discord.call`, `discord.callSong`, `discord.colors`, `discord.dmLine`, `discord.interaction`, `discord.keybind`, `discord.micLevel`, `discord.notify`, `discord.notifyClear`, `discord.open`, `discord.pollVote`, `discord.tts`, `discord.userSettings`, `discord.video.frame`, `discord.video.keyframe`, `drivers.step`, `game.achievements`, `game.optimizedAgain`, `game.optimizeHeld`, `gifts.changed`, `gpu.driver`, `gpu.driverInstall`, `hid.changed`, `hid.input`, `keys.found`, `notes.open`, `runtimes.progress`, `shaders.event`, `shaders.rebuild`, `shell.window`, `store.sale`, `stores.outage`, `versions.new`

### Push events handled by both (35)

`app.updateProgress`, `app.updateState`, `apps.changed`, `autopilot.live`, `autopilot.report`, `capture.reel`, `capture.saved`, `capture.state`, `devices.changed`, `discord.camera`, `discord.changed`, `discord.golive`, `discord.golive.stats`, `discord.golive.viewers`, `discord.members`, `discord.message`, `discord.presence`, `discord.reaction`, `discord.signin`, `discord.speaking`, `discord.state`, `discord.typing`, `discord.unread`, `discord.video`, `discord.voice`, `exoAccount.changed`, `game.runState`, `install.progress`, `install.queue`, `launch.status`, `library.updated`, `music.playback`, `music.volume`, `shaders.changed`, `store.changed`

## Smoke run

Setup: repo-built `ExoLauncher.exe` (integrate-polish-2 Release build) copied to a temp install dir, started with `--host-pipe=<unique>`, `EXO_LAUNCHER_DATA_DIR=<temp copy of %LOCALAPPDATA%\ExoLauncher without app\, Games\, drivers\, tools\, voice\, cache\>` and `EXO_NO_ACTIVATE=1` (same as the parity harness), so it never touched the installed app or the real data dir. Native: `exo-ui.exe --pipe <pipe> --scene <name> --size 1920x1080 --scale 1 --snap out.png --tree t.json` from the room-discord worktree debug build (the freshest build that includes a merged branch; the base-only build was still compiling when the run happened). Each of the 50 parity scenes was snapped once; failed ones were retried once.

Limits: scenes are scripted state snapshots, not live clicking. The headless snap cannot show host-embedded windows (the mouse panel) and the temp data copy has no Exo Capture install. A real click-through of Discord voice, music playback and capture still needs a human or a harness with input scripts.

| Scene | Exit | Time | PNG bytes |
|---|---:|---:|---:|
| library | 4294967295 | 5776 ms | 2085680 |
| library-all | 0 (retry; first run exit -1, no PNG) | 11617 ms | ok on retry |
| all-games | 0 | 10175 ms | 1104571 |
| library-more | 0 | 12522 ms | 2088073 |
| library-search | 0 | 12171 ms | 2085750 |
| game | 0 | 7466 ms | 850686 |
| game-page | 0 | 8388 ms | 851599 |
| game-props-files | 0 | 18795 ms | 856764 |
| game-props-anticheat | 0 | 9272 ms | 846283 |
| game-props-dlss | 0 | 7228 ms | 847542 |
| palette | 0 | 1782 ms | 60591 |
| store | 0 (retry; first run exit -1, no PNG) | 12961 ms | ok on retry |
| store-wishlist | 0 | 10192 ms | 469382 |
| store-search | 0 | 12982 ms | 2390562 |
| store-game | 0 | 12040 ms | 70664 |
| tour | 0 | 10957 ms | 1545242 |
| activate-key | 0 | 24556 ms | 1553381 |
| settings | 0 | 1985 ms | 57415 |
| settings-general | 0 | 1331 ms | 57415 |
| settings-account | 0 | 1288 ms | 60943 |
| settings-appearance | 0 | 1464 ms | 52633 |
| settings-graphics | 0 | 1230 ms | 52820 |
| settings-updates | 0 | 1823 ms | 46442 |
| settings-about | 0 | 1335 ms | 58624 |
| pc | 0 | 3833 ms | 122132 |
| pc-apps | 0 | 2768 ms | 130056 |
| pc-apps-startup | 0 | 2468 ms | 81403 |
| pc-storage | 0 | 1594 ms | 34000 |
| pc-devices | 0 | 4427 ms | 378947 |
| pc-devices-mouse | 0 | 2103 ms | 29466 |
| pc-devices-keyboard | 0 | 7654 ms | 61007 |
| pc-devices-audio | 0 | 2402 ms | 26162 |
| pc-devices-controller | 0 | 5479 ms | 31143 |
| music | 0 | 17331 ms | 39225 |
| music-songs | 0 | 24756 ms | 39812 |
| music-search | 0 | 25799 ms | 39398 |
| music-queue | 0 | 22523 ms | 43125 |
| music-now-playing | 0 | 19366 ms | 43945 |
| music-immersion | 0 | 14110 ms | 33817 |
| captures | 0 | 1547 ms | 60881 |
| captures-sheet | 0 | 4955 ms | 31178 |
| captures-settings | 0 | 4503 ms | 40660 |
| capture-clip | 0 | 4534 ms | 31178 |
| discord | 0 | 5199 ms | 468180 |
| discord-status-menu | 0 | 5123 ms | 474886 |
| discord-messages | 0 | 14961 ms | 263577 |
| discord-servers | 0 | 8003 ms | 471833 |
| discord-server | 0 | 5529 ms | 283449 |
| discord-voice | 0 | 3253 ms | 201404 |
| discord-signin | 0 | 2346 ms | 37857 |

Findings from the run:

- **Crash/flake:** `library-all` and `store` each exited -1 (0xFFFFFFFF) with no PNG on the first attempt and passed on retry. The earlier standalone `library` run also exited -1 once while writing its PNG. Treat the first connection to a cold host as flaky; reproduce before blaming a room.
- **Slow first frame:** `store` first frame took 2.87 s (921 instances); `library` / `library-all` first frame about 0.14 to 0.19 s.
- **Visibly wrong:** game page performance card is fake text (see Game page); Exo AI Auto Tune card is static and always "complete"; Store game page offer rows show store ids "1" / "25" and a raw ISO timestamp; Shaders board tiles on the Exo AI page render blank cover areas; game Properties "Launch options" field renders empty and the lower half of the game page is blank.
- **Dead handlers:** both Notes buttons in Captures reopen/reload Captures instead of opening notes; "New folder" in the game menu raises a "coming soon" toast.
- **Looks right:** Library hero/shelf, Store Discover (hero, side list, shelves with real prices and reviews), Discord Home (server rail with unread pips, DM list, Friends Online/All/Pending/Blocked, Add friend) rendered with the real signed-in data.
- **Not verifiable headless:** the mouse panel embed, Wooting live device, music playback, voice, capture. The music scene sat on "Starting Apple Music…" with empty Home (this data copy has no signed-in music service).

## Work packages

Rule for all packages: native-ui only; keep tests green via `exo_run_gates`; Rune changes go through the fork (`UhhErix/rune` `exo` branch). The one shared hotspot is the host push-event match in `state.rs`: land WP3's `events.rs` registry (size S) first, then every package registers its handlers there instead of editing the same match. `pc.rs` (3385 lines) holds Exo AI, Apps, Storage and Devices together: WP9 step 0 splits it before WP10 starts on Devices.

### Top 10 blockers first

1. Fake data shown as real → WP1, WP9
2. Settings covers 6 of 13+ sections → WP4
3. No folders, no select mode, no gifts → WP2
4. Downloads are invisible → WP3
5. Events are dropped: no toasts or system notices → WP3
6. Store game page is broken and bare → WP5
7. Captures: Notes dead, no editors → WP10
8. Devices: controller, audio format, EQ are stubs → WP10
9. Discord is a chat client without most of Discord → WP6, WP7
10. Apps and Music dead-end → WP9, WP8

### Packages

| Package | Size | Open items | Items (non-DONE) by status | Owned files / modules | Dependencies |
|---|---|---:|---|---|---|
| **WP1 Game page and game actions** | L | 19 | M 9, P 7, B 3 | native-ui/crates/exo-ui/src/game_page.rs (+ new game_props.rs, game_perf.rs); state.rs game_* fns only via a new game_actions.rs | None for RPC (game.settings, game.properties.set, dlss.*, gpu.game, hdr.game, compress.*, art.*, keep.status, shaders.status all exist). Rune: file picker is host-side (shell), none needed. |
| **WP2 Library: folders, select mode, gifts, shelves, drag and drop** | L | 15 | M 6, P 7, B 2 | library.rs (+ new folders.rs, select_bar.rs, gifts.rs); game-card items in menus.rs limited to the GameCard target | Rune fork: drag-and-drop (pointer drag source/target, drop-to-reorder) and file drop events. RPC: library.folderArt, gifts.* exist. |
| **WP3 Shell chrome: Island, Downloads, updater, notices, boot gate, shortcuts, palette** | L | 18 | M 8, P 10, B 0 | shell.rs, notice.rs, palette.rs, gamepad.rs, main.rs on_key, new downloads.rs, island.rs, whats_new.rs, boot.rs, events.rs (event dispatch registry) | Land events.rs first (S): one push-event registry that rooms register handlers into, so WP6/7/8/9/10 stop editing the same state.rs match. Island call controls need WP7 voice state. |
| **WP4 Settings, accounts, privacy, report, onboarding remainder** | L | 28 | M 19, P 9, B 0 | settings.rs (+ new settings_accounts.rs, settings_stores.rs, settings_account.rs, settings_privacy.rs, report.rs); setup.rs account/apps/cleanup/restore steps | Rune fork: masked (password) text input, one-time-code / QR image display. RPC: all present (accounts.*, steam.signIn*, exoAccount.*, stores.*, telemetry.*, report.*, web.preferences.*, backup.restore). |
| **WP5 Store game page and store extras** | M | 12 | M 9, P 2, B 1 | store.rs (+ new store_page.rs, store_media.rs) | Trailers need an HLS path: reuse video_decoder.rs (owned by WP10 for captures; coordinate read-only reuse). Rune fork: image lightbox / horizontal strip virtualisation if not present. RPC: store.gameReviews and storeWishlist(check) exist. |
| **WP6 Discord chat: composer, pickers, threads, polls, search, inbox, profile** | L | 16 | M 9, P 7, B 0 | discord_chat.rs, discord_chat_state.rs, discord_md.rs (+ new discord_pickers.rs, discord_search.rs, discord_threads.rs) | Rune fork: file drop onto composer, multi-line text with caret/selection already used? (verify), animated sticker (Lottie) decoder optional. RPC present: discord.emojis/gifs/stickers/thread(s)/poll/search/saved/mentions/forYou/upload. |
| **WP7 Discord voice, calls, servers, user and server settings** | L | 20 | M 11, P 9, B 0 | discord.rs, discord_state.rs, discord_voice.rs, discord_voice_state.rs (+ new discord_settings.rs, discord_admin.rs) | Needs WP3 events.rs for discord.call / callSong / micLevel / keybind / tts / userSettings events and the Island hook. Rune fork: backdrop blur sampling of avatar for call stage; cursor under menu (room-discord report). |
| **WP8 Music room completion** | L | 16 | M 9, P 7, B 0 | music.rs, media_state.rs (+ new music_pages.rs, music_sound.rs) | None for Rune. RPC: music.match/warm, spotify.link, audio.formats/setFormat, eq.headphones exist. Mini-player volume popover belongs here (bar item in shell.rs: one-line hook only). |
| **WP9 Exo AI / PC, Storage, Apps** | L | 29 | M 19, P 9, B 1 | pc.rs (Exo AI + Apps + Storage parts), pc_actions.rs, pc_state.rs; STEP 0 (S): split pc.rs into pc_ai.rs / pc_apps.rs / pc_devices.rs so WP10 can work in pc_devices.rs without conflicts | RPC present: system.tune/health/interrupts/drivers, gpu.display/driver/afmf3/controlPanel, bios.update, compress.*, runtimes.*, apps.scan/clean/confirm/restorePoint/undo/installerCache/packages*, replaced.remove, keep.status, autopilot.set. |
| **WP10 Devices (controller, audio, EQ, mouse relay) and Captures + Notes** | L | 18 | M 9, P 7, B 2 | pc_devices.rs (after WP9 step 0) or new devices_controller.rs / devices_audio.rs / devices_eq.rs; captures.rs (+ new notes.rs, snip_editor.rs, clip_export.rs); video_decoder.rs | Rune fork: multi-line editor with selection/undo, image canvas with marks/crop, file drop. RPC present: controller.* (10), audio.formats/setFormat, eq.headphones, capture.*, notes.* (11), hid.* only if mouse relay is done natively instead of the embedded host panel. |

#### WP1 Game page and game actions (L, 19 items)

Owns: native-ui/crates/exo-ui/src/game_page.rs (+ new game_props.rs, game_perf.rs); state.rs game_* fns only via a new game_actions.rs

Depends on: None for RPC (game.settings, game.properties.set, dlss.*, gpu.game, hdr.game, compress.*, art.*, keep.status, shaders.status all exist). Rune: file picker is host-side (shell), none needed.

- `game-page-02` [DONE] Primary action states (Play / Install / Update / Pause / Resume / Queued / Stop / Buy)
- `game-page-03` [DONE] Download / install progress bar + speed + ETA
- `game-page-04` [DONE] Performance card (OptimizeLine, shaders, FSR/DLSS summary, HDR, frame gen)
- `game-page-05` [DONE] Display mode (windowed / fullscreen / borderless)
- `game-page-06` [DONE] Frame generation row (DLSS FG / AFMF / Lossless)
- `game-page-07` [DONE] HDR row
- `game-page-08` [DONE] Compress row (CompactOS per game)
- `game-page-09` [DONE] Shader pre-compile row
- `game-page-10` [DONE] Properties: General (launch options, language, Steam overlay)
- `game-page-12` [DONE] Properties: Betas (switch branch, access code)
- `game-page-13` [DONE] Properties: DLC
- `game-page-14` [DONE] Properties: Artwork (refetch, replace, reset)
- `game-page-15` [DONE] Properties: Upscaler files (DLSS/FSR/XeSS swap, restore, consent)
- `game-page-16` [DONE] Properties: Anti-cheat choice
- `game-page-17` [DONE] Properties: Cloud saves / Steam Input
- `game-page-18` [DONE] Properties: Move to another library folder / size on disk
- `game-page-19` [DONE] Achievements (list, progress, rarity) + pop-up
- `game-page-21` [DONE] Friends who play
- `game-page-22` [DONE] Store page link / Buy again

#### WP2 Library: folders, select mode, gifts, shelves, drag and drop (L, 15 items)

Owns: library.rs (+ new folders.rs, select_bar.rs, gifts.rs); game-card items in menus.rs limited to the GameCard target

Depends on: Rune fork: drag-and-drop (pointer drag source/target, drop-to-reorder) and file drop events. RPC: library.folderArt, gifts.* exist.

- `library-01` [PARTIAL] Hero / home deck (selected game, Play, ... menu)
- `library-03` [PARTIAL] Multiple shelves (Continue playing, Recently added, Favorites, Folders)
- `library-07` [PARTIAL] Filter popover (Genre, Mode, Friends, Store)
- `library-08` [MISSING] Select mode + LibrarySelectBar (multi-select, bulk hide/uninstall/folder)
- `library-09` [BROKEN] Folders (create, edit, cover cropper, reorder, move earlier/later, delete)
- `library-10` [MISSING] Drag to reorder / drop into folder
- `library-11` [MISSING] Gifts shelf (claimable Steam/Epic gifts)
- `library-12` [PARTIAL] Add game (+ button, file picker, portable)
- `library-13` [PARTIAL] Refresh all (library.refreshAll, folder art refresh)
- `library-15` [MISSING] Game card menu: Play as <account>
- `library-17` [BROKEN] Game card menu: New folder / add to folder
- `library-18` [MISSING] Folder card menu (Edit, Move earlier/later, Delete)
- `library-19` [PARTIAL] Running game state + Stop; hold windows (wait for game)
- `library-20` [MISSING] Keyboard grid nav / type-ahead
- `library-21` [PARTIAL] Library empty / loading / error states

#### WP3 Shell chrome: Island, Downloads, updater, notices, boot gate, shortcuts, palette (L, 18 items)

Owns: shell.rs, notice.rs, palette.rs, gamepad.rs, main.rs on_key, new downloads.rs, island.rs, whats_new.rs, boot.rs, events.rs (event dispatch registry)

Depends on: Land events.rs first (S): one push-event registry that rooms register handlers into, so WP6/7/8/9/10 stop editing the same state.rs match. Island call controls need WP7 voice state.

- `shell-bar-03` [MISSING] Discord unread / mention badge on nav tab
- `shell-bar-04` [PARTIAL] Mini player in bar (title, play/pause, next, volume)
- `shell-bar-07` [PARTIAL] Window controls (min/max/close, drag, double-click maximize)
- `shell-bar-09` [MISSING] Exo Island (in-call / download capsule)
- `shell-bar-10` [PARTIAL] Downloads sheet (queue, pause, cancel, resume, speed, order)
- `shell-bar-11` [MISSING] What's New sheet (changelog after update)
- `shell-bar-12` [PARTIAL] App update flow (check, download progress, install, restart)
- `shell-bar-13` [PARTIAL] Activate key sheet
- `shell-bar-14` [PARTIAL] Toast / notice stack (with actions, tones, auto-dismiss)
- `shell-bar-15` [MISSING] Anti-cheat missing notice (install anti-cheat prompt)
- `shell-bar-16` [MISSING] Store outage notices
- `shell-bar-17` [MISSING] Discord DM / ping toasts
- `shell-bar-18` [MISSING] Driver install overlay
- `shell-bar-19` [PARTIAL] Boot gate + error boundary (splash, retry, crash screen)
- `shell-bar-20` [PARTIAL] Gamepad bar / controller navigation
- `shell-bar-21` [PARTIAL] Global keyboard shortcuts (Ctrl+F, Ctrl+A, Alt+arrows, etc.)
- `shell-bar-22` [PARTIAL] Command palette (rooms, settings, games, actions)
- `shell-bar-23` [MISSING] Palette: Take the tour, Minimize/Close window, Play/Install selected, note commands, Discord DMs/servers/channels, music controls

#### WP4 Settings, accounts, privacy, report, onboarding remainder (L, 28 items)

Owns: settings.rs (+ new settings_accounts.rs, settings_stores.rs, settings_account.rs, settings_privacy.rs, report.rs); setup.rs account/apps/cleanup/restore steps

Depends on: Rune fork: masked (password) text input, one-time-code / QR image display. RPC: all present (accounts.*, steam.signIn*, exoAccount.*, stores.*, telemetry.*, report.*, web.preferences.*, backup.restore).

- `shell-bar-25` [DONE] Report a problem entry points (Settings, palette, error screen)
- `settings-01` [DONE] Settings search + jump (38 entries)
- `settings-04` [MISSING] General: FPS counter (position, 1% low, on/off)
- `settings-05` [DONE] General: achievement pop-up sound, test pop-up, OBS overlay URL
- `settings-06` [DONE] General: install folder, download hours, auto-update games, update upscalers in anti-cheat games
- `settings-07` [DONE] Store accounts: sign in to Steam (QR / password / guard)
- `settings-08` [DONE] Store accounts: Epic / GOG / Ubisoft / EA accounts (add, switch, remove, import)
- `settings-09` [DONE] Stores section (auth state, check, matrix, offline mode, Riot ID, get another store)
- `settings-10` [DONE] Gifts (Steam gifts sign-in)
- `settings-11` [DONE] Riot data export import / baseline
- `settings-12` [DONE] Exo Account (create, sign in, password, recovery code, devices, export, delete)
- `settings-14` [DONE] Privacy: what was sent, delete my data, telemetry opt-out
- `settings-15` [DONE] Report a problem (preview payload, attach logs, send, open on GitHub)
- `settings-17` [PARTIAL] Music settings (service)
- `settings-18` [MISSING] Apps settings (web preferences)
- `settings-21` [DONE] About: Clear caches, Changelog, Licenses, Support Exo
- `settings-22` [DONE] Shader pre-compile settings
- `settings-23` [MISSING] Discord settings pages (Account, Voice, App, Privacy, Keybinds)
- `onboarding-account-01` [DONE] Setup flow (welcome, scan, stores, account, apps, cleanup, restore)
- `onboarding-account-02` [DONE] Setup: Account step (create / sign in / recovery code, probe)
- `onboarding-account-03` [DONE] Setup: Recovery code screen (save, confirm)
- `onboarding-account-04` [DONE] Setup: Apps step (web preferences)
- `onboarding-account-05` [DONE] Setup: Cleanup step (replaced apps remove)
- `onboarding-account-06` [DONE] Setup: Restore step (restore from backup)
- `onboarding-account-07` [DONE] Setup: per-device bodies; PC defaults count
- `onboarding-account-08` [DONE] Clients gate (store clients missing / fix)
- `onboarding-account-09` [DONE] Account sheet (from bar) / sign-in state
- `onboarding-account-10` [DONE] Backup answer prompt (first-run restore/decline)

#### WP5 Store game page and store extras (M, 12 items)

Owns: store.rs (+ new store_page.rs, store_media.rs)

Depends on: Trailers need an HLS path: reuse video_decoder.rs (owned by WP10 for captures; coordinate read-only reuse). Rune fork: image lightbox / horizontal strip virtualisation if not present. RPC: store.gameReviews and storeWishlist(check) exist.

- `store-02` [DONE] Store hero hover / pick list
- `store-03` [DONE] Wishlist tab
- `store-06` [DONE] Tile card: "Library" button on owned games
- `store-07` [DONE] Check prices / On sale split
- `store-09` [DONE] Store game page: offer rows (store names, prices, historical low)
- `store-10` [DONE] Store game page: hero art / header image
- `store-12` [PARTIAL] Store game page: trailers (HLS player — UI + rune_media::Player::url wired with poster fallback; blocked on desktop MF/video_decoder HLS support)
- `store-13` [DONE] Store game page: screenshot strip + lightbox
- `store-14` [DONE] Store game page: system requirements
- `store-15` [DONE] Store game page: tags, About card, More like this
- `store-16` [DONE] Store game page: wishlist toggle
- `store-17` [DONE] Game reviews

#### WP6 Discord chat: composer, pickers, threads, polls, search, inbox, profile (L, 16 items)

Owns: discord_chat.rs, discord_chat_state.rs, discord_md.rs (+ new discord_pickers.rs, discord_search.rs, discord_threads.rs)

Depends on: Rune fork: file drop onto composer, multi-line text with caret/selection already used? (verify), animated sticker (Lottie) decoder optional. RPC present: discord.emojis/gifs/stickers/thread(s)/poll/search/saved/mentions/forYou/upload.

- `discord-03` [DONE] Create DM / group DM (select friends, 10 limit) — `exo-ui/src/discord_threads.rs:100`
- `discord-08` [DONE] Message rendering: markdown, mentions, embeds, attachments — `exo-ui/src/discord_chat.rs:1129`
- `discord-09` [DONE] Composer: send, reply, edit, typing — `exo-ui/src/discord_chat.rs:1870`
- `discord-10` [DONE] Emoji picker, GIF picker, sticker picker — `exo-ui/src/discord_pickers.rs:180`
- `discord-11` [DONE] Reactions: add, remove, who reacted — `exo-ui/src/discord_pickers.rs:1542`
- `discord-12` [DONE] Message menu (reply, edit, pin, copy, delete, report, forward) — `exo-ui/src/menus.rs:1183`
- `discord-14` [DONE] Polls (create, vote, results) — `exo-ui/src/discord_threads.rs:295`
- `discord-15` [DONE] Message search (filters) — `exo-ui/src/discord_search.rs:242`
- `discord-16` [DONE] Pins panel — `exo-ui/src/discord_search.rs:169`
- `discord-17` [DONE] Inbox (mentions, unreads) — `exo-ui/src/discord_search.rs:550`
- `discord-18` [DONE] Saved messages — `exo-ui/src/discord_search.rs:550`
- `discord-20` [DONE] User profile popover/card (roles, mutual, note, badges) — `exo-ui/src/discord_threads.rs:1536`
- `discord-35` [DONE] Invite dialog — `exo-ui/src/discord_threads.rs:686`
- `discord-41` [DONE] Link rows / image & file viewer, fileText, image save — `exo-ui/src/discord_chat.rs:2417`
- `discord-43` [DONE] Interactions (slash commands, bot apps, buttons) — `exo-ui/src/discord_pickers.rs:1094`
- `discord-44` [DONE] Mentionable autocomplete — `exo-ui/src/discord_pickers.rs:815`

#### WP7 Discord voice, calls, servers, user and server settings (L, 20 items)

Owns: discord.rs, discord_state.rs, discord_voice.rs, discord_voice_state.rs (+ new discord_settings.rs, discord_admin.rs)

Depends on: Needs WP3 events.rs for discord.call / callSong / micLevel / keybind / tts / userSettings events and the Island hook. Rune fork: backdrop blur sampling of avatar for call stage; cursor under menu (room-discord report).

- `discord-13` [MISSING] Threads and forum channels
- `discord-21` [PARTIAL] User menu (message, call, nickname, timeout, kick/ban, report)
- `discord-24` [PARTIAL] Voice call stage (tiles, speaking ring, blurred avatar backdrop, Members column, fit-to-stage solo tile)
- `discord-25` [PARTIAL] Camera and screen share (Go Live picker, preview, stream sound)
- `discord-26` [MISSING] Stage channels (start, audience, move to audience)
- `discord-27` [MISSING] Soundboard + call songs
- `discord-28` [MISSING] Mic test / monitor
- `discord-29` [MISSING] Incoming-call ring / call UI over any room
- `discord-30` [PARTIAL] Server menu (invite, create channel/category/event, notifications, leave)
- `discord-31` [PARTIAL] Channel menu (mute, mark read, edit, delete, copy link)
- `discord-32` [MISSING] Server settings (overview, roles, emoji, moderation, automod, onboarding, guide)
- `discord-33` [MISSING] Channel settings (overview, permissions, slowmode)
- `discord-34` [MISSING] Server pages (events, resources, get started, members)
- `discord-36` [PARTIAL] Shop (collectibles, bundles, payment)
- `discord-37` [PARTIAL] User settings: Account / Profile
- `discord-38` [PARTIAL] User settings: Voice and video (devices, volumes, noise suppression, echo, AGC)
- `discord-39` [MISSING] User settings: Notifications / App / Privacy / Keybinds
- `discord-40` [MISSING] Report message/user/server
- `discord-42` [MISSING] Colors / font / Streamer mode
- `discord-45` [PARTIAL] Top-bar cursor over open menu

#### WP8 Music room completion (L, 16 items)

Owns: music.rs, media_state.rs (+ new music_pages.rs, music_sound.rs)

Depends on: None for Rune. RPC: music.match/warm, spotify.link, audio.formats/setFormat, eq.headphones exist. Mini-player volume popover belongs here (bar item in shell.rs: one-line hook only).

- `music-03` [PARTIAL] Search + Top result / Songs / Artists / Albums
- `music-05` [PARTIAL] Playlist page + create playlist sheet (name, description, cover)
- `music-06` [MISSING] Artist page (header, top songs, discography, fans also like, on tour)
- `music-07` [PARTIAL] Album page / discography
- `music-08` [PARTIAL] Radio / stations
- `music-09` [MISSING] Podcasts / episodes
- `music-11` [PARTIAL] Queue panel (next, history, autoplay, reorder, clear)
- `music-12` [MISSING] Lyrics panel
- `music-13` [MISSING] Connect / Play on devices
- `music-14` [MISSING] Friend activity + Listen along
- `music-16` [MISSING] Sound panel (exclusive mode, crossfade, gapless, AutoMix, normalize, EQ)
- `music-17` [MISSING] Song matching / warm cache (YTM resolves Spotify tracks)
- `music-18` [MISSING] Library filter folder / show-and-sort
- `music-19` [PARTIAL] Music room first-run / no-service state
- `music-20` [MISSING] Music keyboard shortcuts
- `music-21` [PARTIAL] Track context menus (add to playlist, go to artist, share)

#### WP9 Exo AI / PC, Storage, Apps (L, 29 items)

Owns: pc.rs (Exo AI + Apps + Storage parts), pc_actions.rs, pc_state.rs; STEP 0 (S): split pc.rs into pc_ai.rs / pc_apps.rs / pc_devices.rs so WP10 can work in pc_devices.rs without conflicts

Depends on: RPC present: system.tune/health/interrupts/drivers, gpu.display/driver/afmf3/controlPanel, bios.update, compress.*, runtimes.*, apps.scan/clean/confirm/restorePoint/undo/installerCache/packages*, replaced.remove, keep.status, autopilot.set.

- `exo-ai-pc-01` [BROKEN] Exo AI room: Auto Tune card (stage list, live progress, undo)
- `exo-ai-pc-02` [PARTIAL] Exo AI: Run again / Optimizing PC cinema
- `exo-ai-pc-03` [MISSING] Auto Tune: PawnIO / unsupported CPU/GPU notice
- `exo-ai-pc-04` [PARTIAL] PC overview (hardware, Windows version, Secure Boot, TPM, memory integrity, BIOS)
- `exo-ai-pc-05` [PARTIAL] Autopilot (state, what it found / did, pause, turn back on)
- `exo-ai-pc-06` [MISSING] Power & Autopilot group, system tweaks (restore, restart required)
- `exo-ai-pc-07` [MISSING] Interrupts (MSI, DPC latency measure, drivers hold)
- `exo-ai-pc-08` [PARTIAL] Display tab (refresh rate, HDR, calibrate, G-Sync, best settings)
- `exo-ai-pc-09` [MISSING] GPU driver row (latest, install, driver type, profile restore)
- `exo-ai-pc-10` [PARTIAL] Driver profile (low latency, boost)
- `exo-ai-pc-11` [MISSING] Drivers & updates (chipset, Windows Update, BIOS, restart)
- `exo-ai-pc-12` [MISSING] AFMF 3 row
- `exo-ai-pc-13` [MISSING] GPU control panel open
- `exo-ai-pc-14` [MISSING] Best Setup (use best settings)
- `exo-ai-pc-15` [PARTIAL] Storage room (drives, scan space, drive settings)
- `exo-ai-pc-16` [MISSING] Compress games (all, idle, after updates)
- `exo-ai-pc-17` [MISSING] Game runtimes (VC++/.NET list, install)
- `exo-ai-pc-18` [PARTIAL] Shaders board (pre-compile all, rebuild, cancel)
- `exo-ai-pc-19` [MISSING] Keep (CPU/GPU keep-awake status)
- `exo-ai-pc-20` [MISSING] FPS counter options
- `exo-ai-pc-21` [PARTIAL] PC part menus (CPU, GPU, RAM, disk context menus)
- `apps-02` [PARTIAL] Uninstall (confirm, progress, leftovers)
- `apps-04` [MISSING] Scan for unused apps ("Not opened in three months")
- `apps-05` [MISSING] Restore point + Undo
- `apps-06` [MISSING] Installer cache (scan, clean)
- `apps-07` [MISSING] Default apps open
- `apps-08` [MISSING] Replaced apps (remove duplicates)
- `apps-09` [MISSING] Packages: plan / remove / add back
- `apps-12` [MISSING] Edge allow, web preferences (default browser behaviour)

#### WP10 Devices (controller, audio, EQ, mouse relay) and Captures + Notes (L, 18 items)

Owns: pc_devices.rs (after WP9 step 0) or new devices_controller.rs / devices_audio.rs / devices_eq.rs; captures.rs (+ new notes.rs, snip_editor.rs, clip_export.rs); video_decoder.rs

Depends on: Rune fork: multi-line editor with selection/undo, image canvas with marks/crop, file drop. RPC present: controller.* (10), audio.formats/setFormat, eq.headphones, capture.*, notes.* (11), hid.* only if mouse relay is done natively instead of the embedded host panel.

- `devices-02` [PARTIAL] Mouse panel (DPI, buttons, lighting, presets, profiles)
- `devices-04` [PARTIAL] Audio page (output/input, volume, mute, format, Exo Voice, noise suppression)
- `devices-05` [PARTIAL] EQ (Exo EQ, AutoEQ headphones, Equalizer APO install)
- `devices-06` [BROKEN] Controller page (battery, update, quality, measure, Steam Input)
- `devices-07` [MISSING] Controller calibration (sticks, triggers, undo)
- `devices-08` [MISSING] Pad settings (light bar, player light, vibration, trigger effects, sound)
- `devices-09` [MISSING] Game profiles (per-game pad profiles)
- `devices-11` [MISSING] HID relay events (hid.changed, hid.input, controller.state)
- `captures-01` [PARTIAL] Capture sheet grid (thumbnails, filters, chips, On this day)
- `captures-02` [PARTIAL] Select, bulk delete, rename, open folder
- `captures-03` [MISSING] Send to (Discord, share)
- `captures-04` [PARTIAL] Clip editor (trim, play, export)
- `captures-05` [MISSING] Snip editor (mark up, crop, copy text OCR, save)
- `captures-06` [BROKEN] Notes buttons (capture menu + sheet header)
- `captures-07` [MISSING] Notes (Markdown editor, files, session, photo drop, spellcheck)
- `captures-08` [PARTIAL] Capture settings (replay length, quality, save-to, storage cap, auto-delete, voice chat)
- `captures-09` [MISSING] Highlight reel ("Making your reel")
- `captures-10` [MISSING] capture.shrunk event (compaction)

### Host RPC / Rune gaps summary

- **Host RPC:** none of the 345 contract methods is missing; every gap above is native not calling an existing method. Exceptions to verify while working: `store.gameReviews` and `storeWishlist("check")` shapes, `capture.send`, `notes.*` payloads. Events listed in the RPC section need `events.rs` first.
- **Rune fork changes needed:** drag and drop (WP2), file drop on composer and notes (WP6, WP10), masked password input and QR/image display (WP4), multi-line editor with selection and undo (WP10 notes, WP6 composer if not present), image canvas for marks/crop (WP10), image lightbox/strip (WP5), avatar-backdrop blur and cursor-under-menu (WP7).
