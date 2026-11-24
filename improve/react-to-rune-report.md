# React / WebView2 → Native Rust (Rune Fork) Feasibility & Migration Plan

- **Date:** 2026-11-24
- **Audit commit:** `2746258` (`feat/native-polish-2`, Rune fork `UhhErix/rune` `exo` pinned at `ba9aa615`)
- **Test PC:** Windows 11 Pro Build 26300 · AMD Ryzen 5 5600X (6C/12T) · 15.9 GB RAM · NVIDIA GeForce RTX 3070 (driver `32.0.16.2135`)
- **Scope:** Research and planning only (no product code modified). Covers Exo Launcher (`ui/`, `mouse/`, `ExoLauncher/`, `shaders/`, `capture/`, `setup/`), Exo Cinema (`UhhErix/Exo-Cinema`), and the Rune fork (`UhhErix/rune`, `exo` branch).

## Executive Summary

**Yes — moving Exo's product UIs off React/WebView2 onto our Rune fork is not only feasible, it is already ~90% complete for the main launcher UI on `feat/native-polish-2` and delivers a measured 57% working-set / 51% private-memory cut and ~5× faster startup.**

Headline measured numbers on this PC:

| Configuration | Processes | Threads | Working Set | Private Bytes | WebView2 Procs | Startup (to ready) |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| **Public 3.30.2 (WebView2)** — live idle (~3 min, PID 19708, excl. Capture) | 8 | 255 | **944.6 MB** | **797.4 MB** | 7 (`534.1 / 525.8 MB`) | `630–777 ms` visible · `~1,246 ms` usable |
| **Public 3.30.2 (WebView2)** — E2E post-rooms idle (`perf.idle-cpu`) | 8+ | — | **1,280.5 MB** | — | 7+ | `window 152 ms` · `wv2 visible 777 ms` |
| **Native v4 (`feat/native-polish-2`)** — Library settled, **0 WebView2** | **2** | **98** | **403.5 MB** | **394.2 MB** | **0 (`0 / 0 MB`)** | **`110.6 ms`** window · **`136 ms`** pipe→library |
| **Delta: Native v4 vs Public 3.30.2 idle** | **-6 (-75%)** | **-157 (-62%)** | **-541.1 MB (-57.3%)** | **-403.2 MB (-50.6%)** | **-7 (-100%)** | **~5× faster** |
| **Native host + Exo Mouse WebView2 open** (`mouse.panel.open` delta) | **+6** | **+167** | **+330.1 MB** | **+204.5 MB** | **+6** | **`1,056.1 ms`** cold open |
| **Native host 4s AFTER `mouse.panel.close`** (retained leak!) | **+4** | **+120** | **+225.5 MB** | **+163.6 MB** | **+4** | Shared `CoreWebView2Environment` never torn down |
| **Exo Cinema 1.5.2** — lean idle (`app.log` full boot: `mpv 80.5 ms`) | 6 | 103 | **222.1 MB** (lean) / ~380–480 MB active | **126.2 MB** (lean) | 5 (`95.7 / 25.5 MB` lean) | `1,563.9 ms` (`+706 ms` from WebView2) |

Key architectural findings:
1. **The main launcher UI (`apps/launcher/ui`, 107.8K LOC) is already mostly ported:** `native-ui` (`exo-rpc` + `exo-kit` + `exo-ui`, **135,480 LOC across 90 Rust files**) already covers **348 / 363 RPC methods (95.9%; 96.7% of applicable)** and **81 / 83 push events (97.6%)**. WP1–WP7, WP9–WP10, onboarding, Wooting, GPU tuning/HDR/Cap Lab, and Liquid Glass are all merged on `feat/native-polish-2`. Only **WP8 (Music room UI)**, 12 functional RPCs, Lottie stickers, and vision-parity polish remain for v4.0.
2. **Opening Exo Mouse once in native mode destroys half the memory win:** `MousePanelService` spawns 6 `msedgewebview2.exe` processes (+330.1 MB WS / +204.5 MB Private, 1,056 ms cold open), and **4 processes (+225.5 MB WS / +163.6 MB Private) stay resident forever after closing** because `WebViewEnvironmentFactory` caches the shared environment.
3. **Do NOT rewrite `@openmouse/protocol` (88.6K runtime LOC across 55 mouse vendors, AGPL-3.0) in Rust:** It uses zero DOM/CSS/Canvas APIs (only `navigator.hid` + `TextEncoder`/`TextDecoder`). Run it unmodified in an **AGPL-3.0 headless `ExoMouse.Service.exe` embedding QuickJS-NG (`rquickjs`, ~5–8 MB RAM)** with a Win32 HID bridge over NDJSON/pipe, matching `ExoShaders.Service.exe`'s GPL isolation model, paired with a native Rune UI (~20–35 MB total vs 330 MB WebView2).
4. **Exo Cinema (`UhhErix/Exo-Cinema`) is a low-hanging fruit:** Its playback is already native (`libmpv-2.dll`, 80.5 ms init). Its WebView2 overlay (`wwwroot/`) is only **10,074 LOC of vanilla JS/CSS/HTML with ZERO React and ZERO npm dependencies**. Adding a transparent DirectComposition Rune window over `libmpv`'s `wid` HWND eliminates WebView2 from Exo Cinema completely (~100–350 MB WS saved, ~700 ms faster startup) while preserving 100% of libmpv HDR10/Dolby Vision/d3d11vpp/GLSL.
5. **Four surfaces cannot cleanly leave a browser** (DRM music engines: Spotify/Apple Music/YouTube Music; Stripe Elements PCI card entry in `wwwroot/pay`; Epic/GOG OAuth + anti-bot captchas in `StoreAuthWindow`; bot-gated store web flows in `StoreGifts` & `StoreLinks`). For these, use **system-browser hand-off** where possible and **truly ephemeral isolated WebView2 environments** that dispose their `CoreWebView2Environment` immediately on close so zero `msedgewebview2.exe` processes linger.

---

## 1. Inventory of Every Web-Rendered Surface Across Exo

### 1.1 Complete Surface Inventory

| # | Product / Surface | Location | Source Size (Files / LOC) | Built Size | Runtime Web / npm Dependencies | Native Rune Status (`feat/native-polish-2` @ `2746258`) | Parity / Gap Summary |
|---:|---|---|---:|---:|---|---|---|
| 1 | **Exo Launcher main UI** | `apps/launcher/ui/src/` | 489 files / 107,796 LOC (`components/`: 309 / 59,336) | 7.81 MB (241 files in `wwwroot/`) | `react` 19.3, `react-dom`, `clsx`, `dashjs` 5.3, `hls.js` 1.7, `lottie-web` 5.13, `qrcode-generator` 2.0, `skullet` 0.0.3; plus hand-rolled Discord MD, Notes MD, `emoji.json` (118 KB), Snip canvas, SVG curves | **Ported (~90%+)** in `apps/launcher/native-ui` (90 `.rs` files / 135,480 LOC) | **348/363 RPCs (95.9%)**, **81/83 events (97.6%)**. WP1–7, WP9–10, onboarding, Wooting, perf rows, Liquid Glass merged. Main gaps: **WP8 Music UI** (16 items), 12 functional RPCs, Lottie stickers, 70-state vision polish. |
| 2 | **Exo Mouse (Devices > Mouse)** | `apps/launcher/mouse/src/` + `vendor/openmouse-protocol/` | UI: 38 files / 9,364 LOC; Protocol: 255 runtime TS / 88,584 LOC (+220 tests / 47,605 LOC) across 55 vendors | 1.79 MB (142 files in `wwwroot/mouse/`) | `react` 19.3, `react-dom`, `@openmouse/protocol` (`0.60.0-exo.1`, **AGPL-3.0-or-later**) | **Embedded WebView2 child window** (`MousePanelService.cs` 578 LOC + `MousePanelWindow.cs` 428 LOC) | No native Mouse UI yet; `mouse.diag.*` (4 RPCs) and `system.mouseCore` not yet in `exo-ui`. Highest single WebView2 cost inside native mode (+330 MB WS open / +226 MB leaked). |
| 3 | **Music Web Engines** (Spotify, Apple Music, YouTube Music) | `ExoLauncher/Services/MusicEngineService*.cs` (1,566 LOC), `MusicWebHostWindow.cs` (271 LOC), `Helpers/{Spotify,AppleMusic,YouTubeMusic}Native.cs`, `Assets/Extensions/{exo-shield,sponsorblock}` | Host: ~1,837 C# LOC + extensions (`exo-shield` 5.6 KB, `sponsorblock` ~2.5 MB w/ locales) | — | Third-party web players (`open.spotify.com`, `music.apple.com`, `music.youtube.com`) + Widevine EME DRM + MusicKit JS + Spotify `SocialConnectAPI` + Chrome MV3 extensions | **WebView2 stays as hidden audio/API engine**; Exo's own UI is in `exo-ui/src/music.rs` (WP8 pending) | No legal/stable browserless path for Apple Music or YouTube Music; Spotify `librespot` violates ToS & drops lyrics/library/Jam. Keep strictly on-demand & tear down WebView2 on 15 min idle. |
| 4 | **Achievement Banner Overlay** | `ExoLauncher/AchievementBanner.cs` (326 LOC) + `Assets/Overlay/banner.html` | 1 HTML/CSS/JS file / 258 LOC | ~11 KB (+ Goeist font) | Zero npm deps; pure CSS `@keyframes` + `CoreWebView2CompositionController` transparent overlay | **Still WebView2** (ephemeral: closes 20 s after last unlock; also served at `/achievements` for OBS browser source) | Straightforward to replace the desktop overlay with a transparent click-through Rune window (keep `banner.html` only for OBS HTTP browser source). |
| 5 | **Store Gifts (`gifts-webview`)** | `ExoLauncher/Services/StoreGifts.cs` (386 LOC) | 386 C# LOC (no bundled JS UI) | — | Hidden WebView2 (`gifts-webview` profile) scraping/calling `store.steampowered.com/gifts` & `store.epicgames.com` (`egs-platform-service`) | **Still WebView2** (host-side, UI shelf already native in `exo-ui/src/gifts.rs`) | Epic's gifting endpoint rejects non-browser TLS/bot checks; Steam uses browser session cookies. Keep ephemeral WebView2 torn down immediately after check/claim. |
| 6 | **Store OAuth / Sign-In Window** | `ExoLauncher/Services/StoreAuthWindow.cs` (315 LOC) + `MainWindow.WebCard.cs` (125 LOC) | 440 C# LOC | — | Interactive WebView2 for Epic (Legendary) & GOG (gogdl) sign-in, captchas, federated Google/Apple/Xbox OAuth | **Still WebView2** (on-demand only during account link) | Cannot replace captchas/federated OAuth natively. Move to **system browser + localhost/deep-link OAuth** where supported, else ephemeral WebView2 disposed on window close. |
| 7 | **Store Offer Redirect Resolver** | `ExoLauncher/Services/StoreLinks.cs` (111 LOC) | 111 C# LOC | — | Hidden private WebView2 following CheapShark redirect links (anti-bot gated) to resolve `steam://` / `com.epicgames.launcher://` / `goggalaxy://` | **Still WebView2** (ephemeral) | Only runs on click of non-Steam Store "Get" button. Either fall back to system browser or keep ephemeral WebView2 with full environment teardown. |
| 8 | **Discord Shop Card Form (`wwwroot/pay`)** | `apps/launcher/ui/public/pay/{index.html,pay.js}` | 2 files / ~200 LOC (7.0 KB) | 7.0 KB | Stripe Elements (`https://js.stripe.com/v3/`) on isolated origin `pay.exo-launcher.local` | **Not ported natively** (PCI-DSS SAQ-A boundary) | Raw card inputs cannot move to native Rust without full PCI-DSS SAQ-D compliance. Hand off to system browser (Discord Billing / Stripe Checkout) or ephemeral WebView2 on demand. |
| 9 | **Exo Cinema (`UhhErix/Exo-Cinema` v1.5.2)** | `src/ExoCinema/wwwroot/` (C# host: 122 files / 16,792 LOC + `libmpv-2.dll`) | 18 web files / **10,074 LOC** (500 KB) | 1.35 MB (38 files in `wwwroot/`) | **Zero React, zero npm dependencies.** Pure vanilla JS/CSS/HTML (`app.js` 106 KB, `profile.js` 81 KB, `profile-canvas.js` 33 KB, `player.js` 33 KB, `profile-effect.js` 11 KB 2D canvas particles) | **No Rune port yet.** Video playback is already 100% native via `libmpv-2.dll` (`wid` HWND, D3D11/d3d11va, GLSL shaders) | High-value v4/Cinema 2.0 target: only 10K LOC vanilla JS UI over native mpv. Transparent DirectComposition Rune window over `mpv` HWND removes WebView2 completely. |
| 10 | **Launcher & Cinema Installers (`setup/`)** | `apps/launcher/setup/` + `ui/setup.html`; `Exo-Cinema/setup/` + `setup/page/` | Launcher: `main.cpp` 33 KB + React setup entry; Cinema: `main.cpp` 29 KB + 3 files (24 KB) | Bundles 11.5 MB `WebView2LoaderStatic.lib` | Static WebView2 C++ host | **Still WebView2** (one-time installer binary) | Runs only during install/upgrade (zero steady-state RAM impact). Low priority; optional tiny Rust/Rune or Win32 installer later removes 11.5 MB static lib & WebView2 bootstrap dependency. |
| 11 | **Exo Shaders (`apps/launcher/shaders/`)** | `apps/launcher/shaders/` | **0 web files** (482 C# files / 148,299 LOC) | Headless `ExoShaders.Service.exe` | None (NDJSON over stdio; GPL-3.0-or-later) | **Already native!** UI (`ShadersBoard.tsx`) is already ported in `exo-ui` (`pc_ai.rs`, `game_perf.rs`) | 100% web-free already. |
| 12 | **Exo Capture (`apps/launcher/capture/`)** | `apps/launcher/capture/` | **0 web files** | Separate `ExoCapture.exe` (MIT + OBS GPL hook) | None | **Already native!** Captures grid, clip trimmer, Snip editor, and Notes are ported in `exo-ui` (`captures.rs`, `snip_editor.rs`, `notes.rs`) | 100% web-free already. |
| 13 | **Debug Chrome Preview (`ChromePreview.cs`)** | `ExoLauncher/Helpers/ChromePreview.cs` | 71 C# LOC (`#if DEBUG`) | Not shipped in Release | Debug-only WebView2 preview harness | `#if DEBUG` only | Delete when WebView2 service chrome is retired. |
| — | *Out of scope (public web / edge)* | `UhhErix/Exo` (`site/`, `workers/`), `apps/launcher/cloud/*`, `apps/launcher/relay/` | Public `getexo.app` website & Cloudflare Workers | — | Web-native public services | N/A | Public web/edge services — not desktop WebView2 hosts. |

### 1.2 Native-UI Port Status Breakdown (`feat/native-polish-2` @ `2746258`)

Comparing `apps/launcher/ui/contracts/host-v1.json` (363 methods, 83 events), `apps/launcher/improve/feature-matrix.md` (241 tracked features), `apps/launcher/improve/rpc-parity.md`, and `apps/launcher/improve/vision-parity.md` (70 states):

- **Static RPC coverage on `HEAD` (`2746258`):** **348 / 363 methods (95.9%)**.
  - 3 missing methods are **intentionally N/A** in native mode because `exo-ui` owns its native `winit`/Rune window directly: `shell.close`, `shell.maximize`, `shell.minimize`.
  - **12 genuine remaining RPC methods**:
    - Exo Mouse (5): `mouse.diag.cancel`, `mouse.diag.fix`, `mouse.diag.measure`, `mouse.diag.status`, `system.mouseCore`
    - Music WP8 (2): `music.match`, `music.warm`
    - Misc UI (5): `apps.history`, `deps.list`, `dlss.updateAll`, `exoAccount.markAsked`, `steam.downloads`
- **Static push event coverage on `HEAD` (`2746258`):** **81 / 83 events (97.6%)**.
  - `discord.video.frame` is **intentionally bypassed** in native mode: `NamedPipeHostTransport` streams raw binary frames over the dedicated `<pipe>-media` pipe (`video_decoder.rs`), avoiding base64 JSON overhead.
  - Only **1 event genuinely unhandled**: `runtimes.done`.
- **Feature & Vision Parity (`improve/feature-matrix.md`, `improve/vision-parity.md`):**
  - Merged WPs: **WP1** (Game page: 22/22 DONE), **WP2** (Library: 21/21 DONE), **WP3** (Shell/Bar/Palette/Downloads/Notices: 24/25 DONE; only `Report a problem` entry points flagged), **WP4** (Settings), **WP5** (Store: 16/17 DONE; HLS trailers now supported in Rune `ba9aa61`), **WP6 + WP7** (Discord chat/voice/admin/settings), **WP9** (Exo AI/PC/Storage/Apps: 36/36 DONE), **WP10** (Devices audio/EQ/controller, Captures, Snip editor, Notes: 18/18 DONE), plus `native-onboarding`, `wooting-native`, `native-perf-rows`, and `native-glass-adopt`.
  - **Unmerged / Incomplete Room:** **WP8 (Music room UI)** — 16 items remaining (artist page, podcasts, lyrics, Connect/devices, friend activity, sound/EQ panel, song matching, shortcuts, playlist/album/queue/search polish; accounted for 18 of 40 open vision blockers in `vision-parity.md`).
  - **Rune-side visual parity backlog:** `vision-parity.md` logged 64 Rune/exo-kit visual polish items (2 blockers, 29 major, 33 minor — mostly spacing, typography metrics, glass/rim accuracy, and card sizing) prior to `native-glass-adopt`; needs a fresh 70-state re-score after WP8 lands.

---

## 2. Web Dependency & Capability → Native Route Map

Every runtime npm package and hand-rolled browser capability across `ui/`, `mouse/`, `ExoLauncher/`, and `Exo-Cinema`:

| Dependency / Capability | Used In | Route Category | Recommended Native Route | Effort | Notes / Caveats |
|---|---|---|---|---|---|
| `react` 19 + `react-dom` + `clsx` | `ui/`, `mouse/` | **Already in Rune** | `rune` declarative `El` tree (`col`/`row`/`el`/`text`) + `exo-kit` (`tokens.rs`, `glass.rs`, `components.rs`, `menu.rs`) | Done (Launcher); M (Mouse) | State lives in typed Rust structs (`*_state.rs`), updates via `Tx` messages. |
| `hls.js` 1.7.4 | `ui/` (`TrailerPlayer.tsx`) | **Already in Rune fork** | `rune-media` `Player::hls` / `Player::url` (commit `f14c64d`, pinned `ba9aa61`: WinHTTP + HLS `.m3u8` ABR + Media Foundation HW decode) | Done (verify on Store) | `exo-ui/src/store_media.rs:102` already calls `Player::url`. |
| `dashjs` 5.3.0 (AV1 DASH trailers) | `ui/` (`TrailerPlayer.tsx`) | **New Rune fork feature (Optional P2)** | Add `.mpd` manifest parser + segment loader to `rune-media` (reusing existing `windows/mf.rs` + optional `dav1d` software fallback for GPUs without AV1 HW decode) | M (1–2 wk) | Not a v4 blocker: every Steam trailer also ships H.264 HLS, which `rune-media` already plays natively. |
| `qrcode-generator` 2.0.4 | `ui/` (`Qr.tsx`, Discord/Jam QR) | **Already in Rune fork** | `rune-image` `QrCode` + `rune::QrView` (`59444f3`) & `exo-kit/src/qr.rs` | Done | Zero JS/SVG needed. |
| `skullet` 0.0.3 (skeleton loaders) | `ui/` (`Wait.tsx`, 15+ screens) | **Already in Rune / `exo-kit`** | Native shimmer/pulse skeleton helper in `exo-kit` driven by Rune springs/opacity (respecting `reduce_motion` & window-inactive freeze) | S (1–2 d) | Trivial helper; no DOM shadow-root hack needed. |
| `lottie-web` 5.13.0 | `ui/` (`LottieSticker.tsx` — Discord official sticker packs) | **Rust crate + Rune fork** | **`velato` + `vello_cpu`** (pure Rust, MIT/Apache-2.0) or **`rlottie-rs`** (C++ Samsung rlottie bindings) wrapped in `rune-image`/`rune-fx` to rasterize frames into `Pixels` on demand with LRU cap (40 stickers) & hover/visibility gating | M (1 wk) | `velato` is pure Rust & MIT/Apache-2.0 (cleanest license & static build); `rlottie` has broader Lottie expression coverage. Start with `velato` + static PNG fallback for unsupported features. |
| **Markdown (Discord & Notes)** | `ui/` (`discord/markdown.tsx`, `NotesView.tsx`) | **Already in Rune / `exo-ui`** | Already native: `exo-ui/src/discord_md.rs` (638 LOC, custom Discord syntax: mentions, spoilers, timestamps, code, quotes) & `exo-ui/src/notes.rs` (2,906 LOC, `TextEdit` + `El::spans_with` + `El::spellcheck_ranges`, `f1ee9ba`) + `rune-kit/src/markdown.rs` | Done | `pulldown-cmark` available if Notes needs full CommonMark/GFM tables later. |
| **Unicode & Custom Emoji** | `ui/` (`emoji.json` 118 KB, `EmojiPicker.tsx`, `unicodeEmoji.ts`) | **Already in Rune / `exo-ui`** | `exo-ui/src/discord_pickers.rs` (custom CDN emoji via `ImageCache` + Unicode emoji via `parley_engine`/DirectWrite Segoe UI Emoji color glyphs); expand `UNICODE_EMOJI_GROUPS` from `emoji.json` via build-time codegen | S (1–2 d) | Check full 118 KB `emoji.json` dataset coverage vs current curated subset in `discord_pickers.rs`. |
| **Charts / Interactive Curves** (GPU Fan/VF curves, Stick deadzone, Sensor Angle dial) | `ui/` (`FanCurveEditor.tsx`, `VfCurveEditor.tsx`, `ControllerPage.tsx`), `mouse/` (`SensorAngle.tsx`) | **Already in `exo-ui` + New Rune 2D Canvas helper** | `exo-ui/src/gpu_tuning.rs` (`vf_curve_view`, `fan_curve_view`) already native; add a lightweight **`rune` 2D vector/canvas draw primitive** (lines, arcs, polylines, draggable handles) for Exo Mouse's `SensorAngle` dial & Exo Cinema's `profile-effect.js` particles | S–M (3–5 d) | Avoids abusing dozens of positioned `el` nodes for curves/dials/particles. |
| **Image Annotation & Cropper** (Snip editor, Folder cropper) | `ui/` (`SnipEditor.tsx`, `FolderCropper.tsx`) | **Already in Rune fork** | `rune` `ImageCanvas` (`3093207`: annotation, 8-handle crop rect, undo/redo, PNG export) + `exo-ui/src/snip_editor.rs` & `folders.rs` | Done | Already shipped on `feat/native-polish-2`. |
| **Discord Client & Voice/Video** | `ui/` (`components/discord/`), `ExoLauncher/Services/DiscordClient/` | **Already native** | Host C# `Services/DiscordClient/` owns Gateway/REST/Voice/DAVE; `exo-ui/src/discord*.rs` owns UI; `exo-ui/src/video_decoder.rs` decodes WebRTC elementary video over binary `-media` named pipe via Media Foundation | Done | Only remaining polish: Lottie stickers & vision-parity cleanup. |
| **`@openmouse/protocol` (`0.60.0-exo.1`, AGPL-3.0)** | `apps/launcher/mouse/` (55 mouse vendors, 88.6K runtime TS LOC) | **Rust crate (`rquickjs` QuickJS-NG) + separate AGPL process** | Bundle `@openmouse/protocol` to single ES module, execute inside a separate **AGPL-3.0 `ExoMouse.Service.exe`** embedding **`rquickjs`** (QuickJS-NG, ~5–8 MB RAM, ~5 ms startup) + **`hidapi` / Win32 HID** shim implementing `navigator.hid` + `encoding_rs` (`gbk`/`utf-16le`) `TextDecoder` shim; talk over NDJSON stdio/pipe (same isolation model as GPL `ExoShaders.Service.exe`) | M (2–3 wk) | **Preserves 100% of 55 vendors & upstream mergeability** without rewriting 88.6K LOC of HID byte codecs! Keeps MIT `ExoLauncher`/`exo-ui` strictly isolated from AGPL-3.0. |
| **Transparent Overlay over Game (`AchievementBanner`)** | `ExoLauncher/AchievementBanner.cs` + `banner.html` | **New Rune fork feature** | Add **transparent premultiplied DirectComposition window + `WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_TOPMOST`** support to `rune-gpu`/`winit`; render the 5 tier animations with Rune springs | S–M (1 wk) | Eliminates `CoreWebView2CompositionController` in-game overlay; keep `banner.html` only for OBS HTTP `/achievements` browser source. |
| **Transparent UI over `libmpv` (`Exo Cinema`)** | `UhhErix/Exo-Cinema` (`wwwroot/`, 10,074 LOC vanilla JS) | **New Rune fork feature (shared w/ AchievementBanner)** | Reuse the same **transparent DirectComposition Rune window** layered over `libmpv`'s child `wid` HWND in the host window; port the 10K LOC vanilla JS UI using shared `exo-kit` | M–L (3–4 wk) | Zero frame copies; preserves 100% of `libmpv` HDR10/PQ/HLG/Dolby Vision, `d3d11vpp` (`nvidia-true-hdr`), and GLSL shaders. |
| **Music Web Players (Spotify, Apple Music, YouTube Music)** | `MusicEngineService.cs`, `MusicWebHostWindow.cs` | **NO GOOD NATIVE ROUTE (DRM / ToS)** | **Least-web option:** Keep hidden WebView2 strictly **on-demand** (zero launch at app startup unless user opens Music/plays), **fix `WebViewEnvironmentFactory`** to use a dedicated disposable environment for Music so the 15-min idle sleep **actually kills all `msedgewebview2.exe` processes** instead of leaving 4 Chromium processes resident | S (3–5 d for teardown) | Spotify `librespot` violates ToS & loses lyrics/library/Jam (`docs/architecture/music-native.md`); Apple MusicKit JS requires Widevine/FairPlay; YouTube Music SABR/PO-token extraction is brittle & ToS-hostile. |
| **Discord Shop Stripe Card Entry (`wwwroot/pay`)** | `ui/public/pay/{index.html,pay.js}` | **NO GOOD NATIVE ROUTE (PCI-DSS SAQ-A)** | **Least-web option:** **System browser hand-off** (open Stripe Checkout / Discord Billing in default browser) OR **ephemeral isolated WebView2** spawned only when adding a card and disposed immediately on submit/cancel | S (1–2 d) | Native Rust card inputs would pull Exo into PCI-DSS SAQ-D scope and violate Stripe's Terms. |
| **Epic & GOG Account Sign-In (`StoreAuthWindow`)** | `ExoLauncher/Services/StoreAuthWindow.cs` | **NO GOOD NATIVE ROUTE (Captchas / Federated OAuth)** | **Least-web option:** Prefer **system browser OAuth + loopback redirect** where Epic/GOG permit it; otherwise keep `StoreAuthWindow` as an **ephemeral isolated WebView2** whose environment & temp processes terminate immediately when sign-in closes | S (2–3 d) | Captchas, anti-bot scripts, and "Sign in with Google/Apple/Xbox" require a stock browser engine. |
| **Store Gifts (`gifts-webview`) & CheapShark Redirects (`StoreLinks`)** | `StoreGifts.cs`, `StoreLinks.cs` | **NO GOOD NATIVE ROUTE (Anti-bot / Browser Cookies)** | **Least-web option:** For `StoreLinks`, open CheapShark URL directly in the user's system browser (or keep ephemeral isolated WebView2 torn down after 1 redirect). For `StoreGifts`, keep `gifts-webview` **strictly on-demand** (manual refresh / periodic isolated worker) and tear down the WebView2 environment right after each poll instead of keeping it resident | S (2–3 d) | Epic's `egs-platform-service` and CheapShark reject non-browser TLS fingerprints; Steam gifts rely on Valve's browser session cookies. |

---

## 3. Automated Transformation: Can TSX Be Machine-Translated to Rune/Rust?

We audited React/TSX component patterns in `apps/launcher/ui/src/` and `apps/launcher/mouse/src/` against `apps/launcher/native-ui/crates/exo-ui/src/` and `exo-kit/src/`.

### 3.1 Evaluation of Approaches

| Method | Feasibility | What Works | Where It Breaks Down | Verdict |
|---|---|---|---|---|
| **1. Deterministic AST Codemod / Transpiler** (SWC/OXC/ts-morph → Rust) | **Low (~15–20% coverage)** | Purely mechanical artifacts: CSS design tokens → `exo-kit/src/tokens.rs`, Lucide SVG paths → `exo-kit/src/icons.rs`, `host-v1.json` → `exo-rpc` DTO structs, `emoji.json` → static Rust tables, simple stateless presentational trees. | Fails fundamentally on: (a) **React hooks (`useState`, `useEffect`, `useMemo`, `useRef`, closures)** vs Rust borrow checker + single-owner `&mut AppState` + `Tx` message bus; (b) **CSS cascade, `:hover`, media/container queries, Tailwind utilities, `@keyframes`** vs explicit `El` builder chains + Rune springs; (c) **Async `Promise` chains & cancellation** vs `exo_rpc` callback channels; (d) **Imperative DOM/Canvas refs** (`getBoundingClientRect`, `IntersectionObserver`, `getContext('2d')`). Produces unidiomatic, uncompilable Rust that takes longer to fix than writing cleanly. | **Use ONLY for static data/contract codegen** (tokens, icons, emoji tables, RPC DTOs). Do NOT build a TSX→Rust transpiler. |
| **2. Pure Ad-hoc LLM Porting (no harness)** | **Medium** | Fast initial scaffolding of views and state structs. | Drifts on spacing, typography, glass rims, edge states, and RPC wiring — proven by the initial `vision-parity.md` pass finding 440 visual diffs and 211 uncalled RPCs before verification loops were added. | Insufficient alone. |
| **3. Component-Mapping Table + Agent-Driven File-by-File Porting + Parity Verification Loop** | **High (Already Proven!)** | **Already validated at scale on this exact repo:** took `exo-ui` from scratch to **135,480 LOC, 348/363 RPCs, 81/83 events**, and drove 70/70 visual states through automated diffing (`tools/parity/parity-snap.ps1` + `parity-compare.py` + `exo_parity_snap` + `tools/rpc-parity` + Gemini vision QA at only **$3.14 total API cost** for all 70 states!). | Requires disciplined upfront component/token mapping (`exo-kit`), deterministic fixtures (`whatsNewSeen`, stable sort defaults), and running `cargo clippy` + `exo_parity_snap` + `rpc-parity` per PR. | **Recommended method.** |

### 3.2 Recommended Porting Pipeline (for WP8, Exo Mouse, and Exo Cinema)

1. **Deterministic pre-pass (scripts):** Generate/sync any missing tokens (`exo-kit/src/tokens.rs`), icons (`exo-kit/src/icons.rs`), or static tables (e.g., `emoji.json`, device image tables) from source assets first.
2. **Component-mapping brief:** Give each porting agent an explicit **React/Web → `exo-kit`/Rune mapping table** (below) plus the exact target source files and RPC contract slice.
3. **File-by-file / room-by-room agent port:** Port state (`*_state.rs`), RPC wiring (`exo-rpc`), and view (`*.rs`) in bounded work packages (like WP1–WP10).
4. **Automated 3-gate parity loop:**
   - **Build/logic gate:** `cargo fmt --all && cargo clippy --workspace --locked && cargo test --workspace --locked`
   - **Contract gate:** `tools/rpc-parity` (verify zero uncalled RPCs, zero dropped push events, no chattiness regressions)
   - **Visual parity gate:** `exo_parity_snap` (`tools/parity/parity-snap.ps1` + `parity-compare.py` 8×6 grid diff + 2×2 overlay heatmaps) until `meanAbsDiff` and cell diffs clear visual QA.

### 3.3 Core Component-Mapping Reference (`ui/` & `mouse/` → `exo-kit` / Rune)

| React / Web Pattern | Rune / `exo-kit` Target | Notes |
|---|---|---|
| `<div className="...flex-col / flex-row">` | `col()` / `row()` / `el()` | Explicit gap/padding/sizing from `exo_kit` tokens |
| `tokens.css` variables (`--bg`, `--fg`, `--hover-fill`) | `exo_kit::{BG, FG, hover_fill, pressed_fill, hairline, ...}` | Zero literal colors in `exo-ui` |
| `.exo-glass` / `glass.css` | `exo_kit::{exo_glass, apply_glass, ExoGlass, glass_rim}` | Uses Rune `ba9aa61` native DX12 Liquid Glass |
| `<Button variant="primary \| ghost">` / `.exo-set-btn` | `exo_kit::{primary_button, glass_button, glass_icon_circle}` | Spring press (`PRESS_SCALE`, `SPRING_QUICK`) |
| `<Switch>` (`components/Switch.tsx`) | `exo_kit::switch_pill` | Single switch control |
| `<Slider>` (`components/ui/Slider.tsx`) | `exo_kit` slider / Rune range control | Matches `chrome.css` / `mouse.css` single slider |
| `<Wait>` (`skullet`) | `exo_kit` shimmer skeleton helper | Respects `reduce_motion` & inactive freeze |
| `<Qr>` (`qrcode-generator`) | `rune::QrView` / `exo_kit::qr` | Pure Rust encoder (`59444f3`) |
| Context menus & popovers | `exo_kit::menu` + `El::modal_live` | Includes cursor-under-menu fix |
| Horizontal carousels / large lists | `virtualize_x` / `virtual_cols` (`ab91423`) + `ScrollSpec` | Keeps large libraries/shelves O(visible) |
| `<TrailerPlayer>` (`hls.js` / `dashjs`) | `rune_media::Player::hls` / `Player::url` (`f14c64d`) | Native WinHTTP + Media Foundation |
| `<SnipEditor>` / `<FolderCropper>` (`<canvas>`) | `rune::ImageCanvas` (`3093207`) | Annotations, 8-handle crop, undo/redo, PNG export |
| `<NotesView>` (`<textarea>` + spellcheck) | `rune::TextEdit` + `El::spans_with` + `El::spellcheck_ranges` (`f1ee9ba`) | Multi-line selection, undo, spellcheck |
| Drag-and-drop reorder / file drop | Rune pointer drag-and-drop + `DropReorder` (`97d850a`) | Native OS file drop supported |

---

## 4. Real Measurements on This PC

All measurements were collected directly on this PC (`Windows 11 Pro Build 26300`, `Ryzen 5 5600X`, `15.9 GB RAM`, `RTX 3070`) on **2026-11-24**, using isolated temporary `EXO_LAUNCHER_DATA_DIR` / `EXO_CINEMA_DATA` directories for controlled tests so installed user data was untouched.

### 4.1 Public 3.30.2 (WebView2) — Baseline

**A. Live Installed Public App (`%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe` 3.30.2, PID 19708, ~3 min uptime):**

| Process / Role | PID | Working Set | Private Bytes | Threads |
|---|---:|---:|---:|---:|
| `ExoLauncher.exe` (WinUI 3 + .NET 10 host) | 19708 | 410.5 MB | 271.6 MB | 53 |
| `msedgewebview2.exe` (`renderer` — main React UI) | 11912 | 182.2 MB | 121.0 MB | 26 |
| `msedgewebview2.exe` (`browser`) | 16448 | 136.9 MB | 42.7 MB | 51 |
| `msedgewebview2.exe` (`gpu-process`) | 10252 | 121.4 MB | 254.3 MB | 49 |
| `msedgewebview2.exe` (`utility: NetworkService`) | 17944 | 49.1 MB | 15.7 MB | 21 |
| `msedgewebview2.exe` (`crashpad-handler`) | 11416 | 21.1 MB | 4.6 MB | 12 |
| `msedgewebview2.exe` (`utility: StorageService`) | 21104 | 21.0 MB | 7.4 MB | 10 |
| `msedgewebview2.exe` (`renderer` — suspended Apple Music) | 19720 | 2.4 MB | 80.1 MB | 24 |
| **WebView2 subtotal (7 processes)** | — | **534.1 MB** | **525.8 MB** | **193** |
| **TOTAL (Host + WebView2, excl. `ExoCapture.exe` 44.8 MB WS)** | **8 procs** | **944.6 MB** | **797.4 MB** | **246** |

*(At peak immediately after boot, `app.log` also recorded `550 MB WS / 366 MB Priv` across 6 procs before Apple Music & full library settled; the same-day official E2E run on this PC at `release-3.30.2/apps/launcher/e2e-reports/20261124-134711` measured **`perf.idle-cpu` = 1,280.5 MB** working set after cycling all rooms, `exo 0.31%` + `webview2 0.86%` of a core, JS heap `39.85 MB`, `12,876` DOM nodes, `3,880` listeners, `perf.library-scroll` `16.84%` late frames, and `perf.room-switch` `p50 120 ms` / `Discord p95 138 ms`.)*

**Public 3.30.2 Startup Timings (`%LOCALAPPDATA%\ExoLauncher\logs\app.log` + E2E):**
- `managedEntryMs = 329 ms`, `winuiMs = 296 ms`
- `webview-init-start = 179 ms` → `webview-core-ready = 394 ms` → `webview-navigation-complete = 531 ms` → `webview-visible = 630 ms` (E2E: `window-constructed 152 ms`, `webview-visible 777 ms`)
- **`UI Library usable after 917 ms`** (+ `329 ms` pre-managed entry ≈ **`1,246 ms`** wall-clock from process launch)

---

### 4.2 Native UI (`feat/native-polish-2` @ `2746258`) — Library Idle, ZERO WebView2

Measured using Release `ExoLauncher.exe --host-pipe=...` + Release `exo-ui.exe` (`33.63 MB` binary, with Liquid Glass `native-glass-adopt` merged) seeded with the same 215-game library metadata, settled 18 seconds on Library:

| Process / Role | Working Set | Private Bytes | Threads | WebView2 Procs |
|---|---:|---:|---:|---:|
| `exo-ui.exe` (Rust / Rune DX12 + Liquid Glass + 215-game cover cache) | 210.1 MB | 294.7 MB | 41 | 0 |
| `ExoLauncher.exe` (headless .NET 10 host, no WinUI MainWindow) | 193.4 MB | 99.5 MB | 57 | 0 |
| **TOTAL (Native v4 on Library)** | **403.5 MB** | **394.2 MB** | **98** | **0** |

**Native v4 Startup Timings (measured live):**
- **`exo-ui.exe` native window handle ready:** **`110.6 ms`** from process spawn
- **`RuneHostSession` pipe listening → `first library.get` (215 games):** **`136 ms`** (`14:10:32.697Z` → `14:10:32.833Z`; `libraryMs=70 ms`, `projectionMs=19 ms`)
- **Savings vs Public 3.30.2 idle:** **-541.1 MB WS (-57.3%)**, **-403.2 MB Private (-50.6%)**, **-6 processes**, **-157 threads**, and **~4–5× faster startup** (and **-877.0 MB WS / -68.5%** vs post-rooms E2E `1,280.5 MB`!).
- **Optimization note:** `exo-ui.exe`'s 294.7 MB Private Bytes is largely DX12 swapchain/shader pipelines + Liquid Glass render targets + uncompressed RGBA decoded cover textures in `ImageCache`. Adding an idle/minimized texture eviction & tighter `ImageCache` budget in Rune can realistically push `exo-ui.exe` down to **~130–160 MB WS / ~150–190 MB Private**.

---

### 4.3 Exo Mouse Embedded as WebView2 vs Projected Native Cost

Measured directly by calling `mouse.panel.open` (`visible: true`, `1400×844`) over the named pipe against a clean headless `ExoLauncher.exe --host-pipe=...` (0 WebView2 before call), letting it settle 10 s, then calling `mouse.panel.close` and waiting 4 s:

| Stage | Host (`ExoLauncher.exe`) WS / Priv | WebView2 Procs | WebView2 WS | WebView2 Private | WebView2 Threads | Latency |
|---|---:|---:|---:|---:|---:|---:|
| **Before `mouse.panel.open`** | 122.7 / 71.9 MB | **0** | **0.0 MB** | **0.0 MB** | 0 | — |
| **While Exo Mouse WebView2 open (10s settled)** | 134.9 / 58.6 MB | **6** | **+330.1 MB** | **+204.5 MB** | **+167** | **`1,056.1 ms`** |
| **4s AFTER `mouse.panel.close` (leaked!)** | 134.9 / 58.5 MB | **4** | **+225.5 MB** | **+163.6 MB** | **+120** | — |

Per-process WebView2 breakdown while Exo Mouse is open:
- `wv2:browser`: **112.8 MB WS / 33.6 MB Priv** (55 threads) — *stays resident after close (113.9 MB WS)*
- `wv2:gpu-process`: **81.0 MB WS / 121.4 MB Priv** (43 threads) — *stays resident after close (81.3 MB WS)*
- `wv2:renderer` (Exo Mouse React + `@openmouse/protocol`): **65.1 MB WS / 28.2 MB Priv** (20 threads) — *exits on close*
- `wv2:utility (NetworkService)`: **41.2 MB WS / 11.6 MB Priv** (27 threads) — *exits on close*
- `wv2:utility (StorageService)`: **20.0 MB WS / 7.3 MB Priv** (12 threads) — *stays resident after close (20.3 MB WS)*
- `wv2:crashpad-handler`: **10.0 MB WS / 2.4 MB Priv** (10 threads) — *stays resident after close (10.0 MB WS)*

**Projected Native Exo Mouse Cost (AGPL `ExoMouse.Service.exe` on `rquickjs` + Native Rune UI):**
- **Option A (Best RAM — clean-room MIT UI in `exo-ui` + AGPL `ExoMouse.Service.exe` daemon):**
  - `ExoMouse.Service.exe` (Rust + QuickJS-NG running bundled `@openmouse/protocol` + `hidapi`/Win32 HID): **~6–10 MB WS / ~5–8 MB Private**, 2–4 threads, **~10–25 ms** cold start (can even run on-demand while Devices is open and exit on close!).
  - Incremental UI state + device images inside existing `exo-ui.exe`: **~10–20 MB WS** (reuses existing DX12 device, fonts, glass pipeline, and `ImageCache`).
  - **Total Native Cost: ~16–30 MB WS / ~15–25 MB Private, <30 ms open.**
  - **Net Savings vs WebView2:** **-300 to -314 MB WS (-91% to -95%)**, **-180 to -190 MB Private**, **-5 to -6 processes**, **-160+ threads**, and **~1,025 ms faster open** — plus **0 MB leaked after closing**!
- **Option B (Fastest legal path — standalone AGPL `exo-mouse-ui.exe` Rune child window):**
  - Separate Rune binary with QuickJS-NG in-process (reparented into `exo-ui` window via `SetParent`), closed completely when leaving Devices > Mouse: **~60–90 MB WS** while open, **0 MB** when closed. Still saves **~240–270 MB WS** while open and **225.5 MB WS** leaked after close.

---

### 4.4 Exo Cinema (1.5.2) — Measured Baseline & Projected Native Cost

Measured live (`%LOCALAPPDATA%\ExoCinema\app\ExoCinema.exe` 1.5.2 isolated with `EXO_CINEMA_DATA`) + historical normal startup log (`%LOCALAPPDATA%\ExoCinema\app.log`):
- **Memory (lean background launch — before active renderer):** `ExoCinema.exe` **126.4 MB WS / 100.7 MB Priv** (39 threads) + 5 `msedgewebview2.exe` processes **95.7 MB WS / 25.5 MB Priv** (64 threads) = **222.1 MB WS / 126.2 MB Priv** (6 procs, 103 threads). Once active with Jellyfin library + profile canvas rendered, the Chromium renderer + GPU process add ~150–250 MB WS (total **~370–470 MB WS**).
- **Startup (`%LOCALAPPDATA%\ExoCinema\app.log`):**
  - `process 649.4 ms` → `window 855.7 ms` → **`mpv 80.5 ms`** → `web-env 1,077.9 ms` → `web-navigate 1,162.4 ms` → `page-paint 1,319.5 ms` → **`page-ready 1,563.9 ms`**.
  - **WebView2 accounts for 706 ms (45%) of Exo Cinema's startup**, whereas `libmpv` itself is ready in **80.5 ms**!
- **Projected Native Exo Cinema (Rune transparent overlay over `libmpv`):**
  - **Expected RAM:** **~180–240 MB WS total** (C# or Rust host + `libmpv` + thin Rune overlay), **0 WebView2 processes**, saving **~150–250 MB WS** active and **~600–700 ms** on startup (target ready time **~300–500 ms**).

---

### 4.5 Expected Total Memory & Startup With NO WebView2

| Scenario | Current Public (WebView2) | Target Native (0 WebView2) | Expected Savings |
|---|---|---|---|
| **Exo Launcher idle on Library (normal session)** | 944.6 MB WS / 797.4 MB Priv (8 procs, ~1.25 s startup) | **~320–400 MB WS / ~300–390 MB Priv** (2 procs, **~250–350 ms** startup)* | **-545 to -625 MB WS (-58% to -66%)**, **-6 procs**, **~4× faster** |
| **Exo Launcher with Devices > Mouse open** | ~1,050–1,280 MB WS (8–9 procs) | **~340–430 MB WS** (2–3 procs, <30 ms open) | **-700 to -850 MB WS**, **-6 procs** |
| **Exo Launcher after visiting all rooms (non-Music)** | 1,280.5 MB WS (E2E) | **~380–480 MB WS** (2 procs, 0 leaked Chromium procs) | **-800 to -900 MB WS (-63% to -70%)** |
| **Exo Launcher + active Music DRM stream (on-demand)** | ~1,050–1,350 MB WS | **~600–750 MB WS** while playing → drops back to **~350–400 MB WS** 15 min after pause (true teardown) | **-450 to -600 MB WS** while playing; **0 MB** WebView2 when not using Music |
| **Exo Cinema active (Jellyfin + mpv playback)** | ~370–470 MB WS + mpv buffers (6–7 procs, ~1.56 s startup) | **~180–240 MB WS** + mpv buffers (1–2 procs, **~0.4–0.5 s** startup) | **-150 to -250 MB WS**, **-5 procs**, **~3× faster** |

*\*Lower end of target range (~320 MB WS) reflects Rune `ImageCache`/DX12 idle trim; upper end (~403 MB WS) is already achieved today on `feat/native-polish-2`.*

---

## 5. Ordered Migration Plan (Sorted by Memory Saved per Unit of Effort)

### 5.1 Execution Roadmap

| Priority | Work Item | Effort | Memory / Perf Win | Dependencies / Notes |
|---:|---|---|---|---|
| **P0** | **1. Fix `WebViewEnvironmentFactory` ephemeral teardown** | **XS (2–3 days)** | **Immediately stops +225.5 MB WS / +163.6 MB Priv (4 Chromium procs) permanent leak** whenever Mouse, Gifts, StoreLinks, Auth, or idle Music closes | Create isolated disposable `CoreWebView2Environment` instances for ephemeral hosts (Auth, Gifts, StoreLinks, Pay, Mouse until P1) and dispose/release environment references when last controller closes so `msedgewebview2.exe` exits cleanly. |
| **P1** | **2. Ship v4.0 Native Launcher (`native-ui` completion: WP8 Music UI + 12 missing RPCs + 70-state vision polish + Lottie stickers)** | **M (2–3 weeks)** | **-541 to -877 MB WS / -403 MB Priv, -6 procs, ~5× faster startup** for every v4 user | Complete WP8 (`music.rs`, `music_pages.rs`, `music_sound.rs`, `music.match`, `music.warm`), wire 5 misc RPCs + `runtimes.done`, add `velato` Lottie sticker support in Rune, re-run 70-state `exo_parity_snap` + `rpc-parity` to green. |
| **P2** | **3. Native Exo Mouse (`ExoMouse.Service.exe` AGPL on `rquickjs` + Native Rune Mouse UI)** | **M (3–4 weeks)** | **-300 to -314 MB WS / -180 MB Priv, -6 procs, -1,025 ms open latency** | Highest single WebView2 win left inside native v4! Build AGPL-3.0 `ExoMouse.Service.exe` (Rust + `rquickjs` QuickJS-NG + `hidapi`/Win32 HID + `encoding_rs` GBK/UTF-16LE shim + bundled `@openmouse/protocol`) speaking a generic NDJSON/pipe device schema (`mouse/CONTRACT.md`, mirroring `shaders/engine/CONTRACT.md`), then build a clean-room MIT Rune UI in `exo-ui` (or standalone AGPL Rune child binary) + wire the 5 `mouse.diag.*`/`system.mouseCore` RPCs. |
| **P3** | **4. Native Achievement Banner Overlay** | **S (1 week)** | Eliminates `CoreWebView2CompositionController` + Chromium process tree spun up during gameplay (~180–250 MB peak spike during games!) | Depends on Rune fork item #1 (transparent click-through DirectComposition window). Keep `Assets/Overlay/banner.html` strictly for OBS HTTP `/achievements` browser source. |
| **P4** | **5. Hard-Web Surface Hardening (Browser Hand-off + Strict Ephemeral WebView2)** | **S (1 week)** | Guarantees **0 resident WebView2** for Stripe Pay, Store Auth, Store Gifts, StoreLinks, and paused Music | Hand off `wwwroot/pay` Stripe & `StoreLinks` CheapShark to system browser where UX allows; run `StoreAuthWindow`, `StoreGifts`, and `MusicEngineService` in isolated disposable environments that terminate all `msedgewebview2.exe` processes immediately when idle/closed. |
| **P5** | **6. Native Exo Cinema 2.0 on Rune (Transparent Overlay over `libmpv`)** | **M–L (3–5 weeks)** | **-150 to -250 MB WS active, -5 procs, ~700 ms faster startup (~3×)** | Reuses Rune fork item #1 (transparent DirectComposition window over `libmpv` `wid` HWND) + `exo-kit` design tokens/glass. Port the 10,074 LOC vanilla JS/CSS (`app.js`, `player.js`, `profile.js`, `profile-canvas.js`, `profile-effect.js`) via the agent + parity-snapshot pipeline. |
| **P6** | **7. Optional: Native Rune `exo-ui` Memory Diet + DASH AV1 + Native Installers (`setup/`)** | **S–M (2–3 weeks)** | **-60 to -100 MB WS** on `exo-ui.exe` idle; AV1 Store trailers; removes 11.5 MB `WebView2LoaderStatic.lib` from installers | Trim `ImageCache` LRU & evict GPU atlases when minimized; optional `.mpd` DASH in `rune-media`; optional tiny Rust installer replacing C++/WebView2 `setup/`. |

### 5.2 Named Rune Fork (`UhhErix/rune`, `exo` Branch) Work Items

Every new capability needed in our Rune fork (beyond what is already shipped at `ba9aa61`):

1. **`rune-gpu` / `winit`: Transparent Premultiplied DirectComposition Window + Click-Through Flags (`WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST`)** — *Priority: High (P3 & P5) · Effort: M (1 wk).* Unlocks BOTH native `AchievementBanner` over games AND `Exo Cinema` transparent UI over `libmpv` `wid` without copying HDR video frames.
2. **`rune-image` / `rune-fx`: Lottie Animation Renderer (`velato` + `vello_cpu` or `rlottie-rs`)** — *Priority: High (P1) · Effort: M (1 wk).* Replaces `lottie-web` for Discord animated stickers with bounded LRU frame caching (max 40 animations) and hover/visibility/reduce-motion gating.
3. **`rune` / `rune-kit`: 2D Vector / Curve Canvas Primitive** — *Priority: Medium (P2 & P5) · Effort: S (3–5 d).* Lightweight immediate/retained 2D vector path, arc, and particle primitive for Exo Mouse's `SensorAngle` calibration dial, smoother GPU Fan/VF curves, and Exo Cinema's `profile-effect.js` ambient canvas.
4. **`rune-image` / `rune-gpu`: Idle Memory Diet & Minimized Atlas Eviction** — *Priority: Medium (P6) · Effort: S (3–5 d).* Evict decoded image atlases and trim transient staging/blur buffers when the window is hidden or minimized to push `exo-ui.exe` idle Private Bytes down from 294.7 MB toward ~150 MB.
5. **`rune` / `exo-kit`: Vision-Parity Micro-Fixes** — *Priority: High (P1) · Effort: S–M (1 wk).* Close remaining Rune-side font metrics, shadow/rim precision, and popover anchoring deltas from the 70-state `vision-parity.md` backlog (post-`native-glass-adopt` re-check).
6. **`rune-media`: DASH (`.mpd`) + AV1 Manifest Support** — *Priority: Low (P6) · Effort: M (1–2 wk).* Optional upgrade over existing native HLS (`f14c64d`) to match React's AV1 DASH preference on Steam trailers.

### 5.3 Risks & Mitigations

| Risk | Severity | Impact | Mitigation |
|---|---|---|---|
| **AGPL-3.0 license contamination (Exo Mouse)** | **High (Legal)** | Linking `@openmouse/protocol` or copying `mouse/src` into MIT `exo-ui` violates `AGENTS.md` and obligates MIT Exo Launcher under AGPL-3.0. | Strictly separate `ExoMouse.Service.exe` as an AGPL-3.0 process speaking a clean generic NDJSON/pipe schema (same pattern as GPL `ExoShaders.Service.exe`), publish its source at `getexo.app/source`, and either write a clean-room MIT UI in `exo-ui` against that schema or ship a separate AGPL `exo-mouse-ui.exe` child window. |
| **Music DRM / Platform ToS & Account Bans** | **High (Legal / User Trust)** | Replacing Spotify/Apple/YTM web players with `librespot`/Widevine extraction risks user account bans, breaks Lyrics/Jam/MusicKit, and invites DMCA/ToS action. | Never strip DRM or bundle third-party keys. Keep Music web engines on-demand in a disposable WebView2 environment with hard 15-min idle teardown (0 MB when unused). |
| **PCI-DSS Scope Creep (`wwwroot/pay` Stripe)** | **High (Compliance)** | Collecting raw card numbers in native Rust puts Exo under PCI-DSS SAQ-D. | Never render raw card fields natively; hand off to system browser (Discord Billing / Stripe Checkout) or ephemeral WebView2. |
| **`@openmouse/protocol` QuickJS Edge Cases (`TextDecoder("gbk")`, WebHID timing)** | **Medium (Technical)** | `hitscan/xvi-inputs.ts` uses `TextDecoder("gbk")`; some vendor drivers rely on `navigator.hid` input report listeners and precise timers. | Implement `TextDecoder` in `ExoMouse.Service.exe` via `encoding_rs` (full GBK/UTF-16LE/ASCII support) and run `@openmouse/protocol`'s existing **220 unit tests (47.6K LOC)** directly inside the `rquickjs` harness as a CI gate! |
| **DirectComposition + `libmpv` HDR / Fullscreen Focus (Exo Cinema)** | **Medium (Technical)** | Layering a transparent DX12 Rune window over `libmpv`'s `wid` HWND must preserve HDR10/Dolby Vision pass-through, Win11 caption hit-testing, and minimize/fullscreen transitions. | Prototype a minimal 200-line spike (transparent Rune window over `libmpv` playing HDR10 MKV with `nvidia-true-hdr`) before porting Cinema's 10K LOC UI. |
| **`exo-ui.exe` DX12 Private Bytes on Low-VRAM / iGPU PCs** | **Medium (Perf)** | On RTX 3070, `exo-ui.exe` currently sits at 210.1 MB WS / 294.7 MB Private (still half of WebView2, but higher than ideal for 8 GB iGPU laptops). | Rune already has low-end GPU Liquid Glass auto-downgrade (`f458d7c`); pair with P6 `ImageCache` LRU & minimize eviction and verify on an iGPU laptop before v4 GA. |

### 5.4 How This Fits v4 vs 3.x

- **3.x stays WebView2 (maintenance only):** Ship critical bugfixes and store adapter updates on `main` / `release/3.x` without destabilizing shipping users. No new React/WebView2 architectural investments.
- **v4.0 = Native Rune Launcher (`feat/native-polish-2` + P0 + P1):**
  - Ships `exo-ui.exe` as the default UI over `NamedPipeHostTransport` (`.NET 10` host unchanged, `host-v1.json` contract unchanged).
  - Delivers **0 WebView2 processes at startup and during normal Library / Store / Discord / PC / Captures / Settings use** (~400 MB WS, ~110–140 ms window/library startup).
  - P0 guarantees that even if a user opens Exo Mouse, Store Auth, Store Gifts, or Music, closing/idling them tears down `msedgewebview2.exe` cleanly with zero permanent leak.
- **v4.1 / v4.2 = Zero-WebView2 Desktop Surfaces (P2 + P3 + P4):**
  - P2 ships Native Exo Mouse (`ExoMouse.Service.exe` + Rune UI), permanently removing the last major bundled React app from Exo Launcher.
  - P3 ships Native Achievement Banner over games (removing the last in-game WebView2).
  - `apps/launcher/ui/` (React) and `apps/launcher/mouse/` (React) can then be deleted cleanly per `AGENTS.md` Rule 4 ("Delete, don't flag") once 3.x reaches end-of-life!
- **Exo Cinema 2.0 (P5):** Adopts the same `exo-kit` + transparent DirectComposition Rune overlay over `libmpv`, uniting both Exo desktop products on one shared native Rust/Rune stack with **zero WebView2 in normal operation across the entire Exo desktop suite**.
