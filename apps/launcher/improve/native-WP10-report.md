# WP10 Report — Devices (Controller, Audio, EQ, Mouse Relay) and Captures + Notes

## Summary

All 18 non-DONE items in WP10 (`devices-02`, `devices-04`, `devices-05`, `devices-06`, `devices-07`, `devices-08`, `devices-09`, `devices-11`, `captures-01`..`captures-10`) are implemented to 1:1 feature and visual parity with the React/WebView2 reference, and all 3 existing DONE rows in Devices (`devices-01`, `devices-03`, `devices-10`) were audited and fixed where bugs were found.

- **Base branch:** `origin/feat/native-polish-2` (`5790322`)
- **Rune fork pin bump:** `eba2d7f` (`exo-inputs` `93c8885994a0233debe8a2da7c52326d1382c4eb`, all 8 Rune crates)
- **Push events:** all 5 newly wired host push events (`controller.state`, `hid.changed`, `hid.input`, `capture.shrunk`, `notes.open`) are registered via `EventRegistry` / `AppState::register_event` in `crates/exo-ui/src/events.rs` (zero `state.rs` match arms).
- **RPC contract coverage:** 30+ new `METHOD_*` constants and typed DTOs added in `crates/exo-rpc/src/device_types.rs`, `media_types.rs`, `events.rs`, `lib.rs` and validated against `ui/contracts/host-v1.json` by `host_contract_contains_every_rust_rpc_method`.

## Pre-existing Bugs Found & Fixed

1. **DualSense Edge misrouted to Wooting Keyboard page** (`pc_devices.rs`, `pc_actions.rs`, `main.rs`): `dev.lab || dev.kind == "keyboard"` treated every `lab == true` controller as a keyboard. Fixed to route strictly by `dev.kind` (`"audio"` -> `devices_audio::audio_page`, `"controller"` -> `devices_controller::controller_page`, `"keyboard"` -> `wooting_detail`).
2. **Audio microphone ID always `None`** (`pc_actions.rs`, `main.rs`): matched `d.role == Some("mic")` instead of `"input"` (React/host contract uses `"output"` and `"input"`). Fixed to match `"output"` and `"input"`.
3. **`AudioEndpoint.format` dropped from host replies** (`exo-rpc`): `format` was `Option<String>` instead of `{ rate, bits, channels }`. Fixed to typed `AudioFormatInfo`.
4. **Broken Notes buttons** (`captures.rs`): all 3 Notes popover buttons reopened Captures and the Captures sheet header "Notes" button called `load_captures()`. Fixed to open `NotesView` (`open_notes(None, true)` for New note, `open_notes(None, false)` for Notes) and added "Capture settings" to the popover.
5. **Backdrop blur budget overflow** (caught by parity harness): initial `device_btn` and sheet interior buttons applied `GLASS_CONTROL_SIGMA`, reaching 9–15 blurs on Captures/Notes/Settings. Removed interior button blurs and applied `GLASS_PANEL_SIGMA` to the Captures/Notes sheet, bringing all Captures/Notes states to `glass = 4` (exact `EXO_MAX_BACKDROP_BLURS`).
6. **Captures 4-column grid card width & inspector text wrapping** (caught by 1920x1080 snap inspection): `clip_card` used `.w_full()` inside a 4-column `row()`, causing cards to stretch full-width; fixed to `.grow(1.0).min_w(0.0)` and added `.wrap()` to ClipEditor and SnipEditor inspector helper text.

## Item-by-Item Status & Verification

| ID | Feature | Status | Native Ref | Verification |
|---|---|---|---|---|
| `devices-01` | Device list + images + Rescan | DONE | `exo-ui/src/pc_devices.rs:703` | Live React vs Native parity on owner's hardware (StarlightX Dongle, Wooting 60HE v2, ZiiGaat Lush, DualSense Edge): Open PASS, Cursors 10/10 PASS, pixel diff **1.42 / 255**. |
| `devices-02` | Mouse panel (DPI, buttons, lighting, presets, profiles) | DONE | `exo-ui/src/pc_actions.rs:858` | Preserves AGPL boundary by embedding host `MousePanelService` WebView2 via `mouse.panel.open/bounds/close`; added `hid.*`, `mouse.presets.*`, `mouse.profiles.*`, `system.mouseCore` RPC constants and `hid.changed`/`hid.input` event handlers. Verified `devices-mouse` live native probe OK. |
| `devices-03` | Keyboard page (Wooting) | DONE | `exo-ui/src/pc_devices.rs:102` | Fixed controller misrouting (`kind == "keyboard"`); added USB port row; `wooting_native_layouts_controls_and_error_recovery` passes + live `devices-keyboard` probe OK. |
| `devices-04` | Audio page (output/input, volume, mute, format, Exo Voice, noise suppression) | DONE | `exo-ui/src/devices_audio.rs:1883` | Implemented full `AudioPage`: output & mic format pickers (`audio.formats`, `audio.setFormat`), draggable volume sliders (`audio.setVolume`), mute switches (`audio.setMute`), Exo Voice / Talks by / Input sensitivity / Voice Focus, 3s polling. Verified on live ZiiGaat Lush + fifine Microphone + unit tests. |
| `devices-05` | EQ (Exo EQ, AutoEQ headphones, Equalizer APO install) | DONE | `exo-ui/src/devices_audio.rs:1143` | Implemented `ApoPanel` (install/reinstall/restart), searchable AutoEQ `HeadphonePanel` (`eq.headphones`), `SoundPanel` (Compare bypass, game profiles, headroom, other outputs), `NightPanel` (Windows Night light sync + 48 half-hour slots), 4s polling. Verified on live ZiiGaat Lush EQ (`-3.9 dB` headroom, `Super Review · JM-1 / IEF 2025`) + unit tests. |
| `devices-06` | Controller page (battery, update, quality, measure, Steam Input) | DONE | `exo-ui/src/devices_controller.rs:2714` | Implemented `ControllerPage`: live Rune `canvas` stick visualizers + rest/edge tests, triggers + Edge stop sliders, button chatter test, Edge back/Fn indicators + 4 on-board slots, polling measurement, USB port, firmware update link (`shell.openUrl`), Steam Input (`steamInput.state`). Verified side-by-side against React on owner's DualSense Edge: Open PASS, Cursors 11/11 PASS, pixel diff **6.44 / 255**. |
| `devices-07` | Controller calibration (sticks, triggers, undo) | DONE | `exo-ui/src/devices_controller.rs:2211` | Implemented 7-phase `Calibration` dialog (`Intro`..`Done`) with 36-bin perimeter sweep tracker, `controller.calibrate` / `controller.test`, Undo, and Keep confirmation. Verified via unit tests (`sweep_bin_ignores_center_and_tracks_perimeter`, `stick_verdict_distinguishes_good_and_needs_calibration`, `axis_percent_and_deadzone_match_react_formulas`). |
| `devices-08` | Pad settings (light bar, player light, vibration, trigger effects, sound) | DONE | `exo-ui/src/devices_controller.rs:1850` | Implemented USB-gated `PadSettings` (`controller.settings`) for Light bar, Player light, Microphone, Speaker, Vibration, Trigger effects, plus Windows `Sound in Windows` (`controller.sound`). Verified live on owner's DualSense Edge (read-only verification; zero un-restored hardware writes, zero firmware touches). |
| `devices-09` | Game profiles (per-game pad profiles) | DONE | `exo-ui/src/devices_controller.rs:1972` | Implemented `GameProfiles` list (`controller.gameProfiles`), editor modal (`controller.gameProfilesSave`), and searchable installed-game picker with cross-profile deduplication (`save_profile_list_moves_games_from_other_profiles` test + live render). |
| `devices-10` | Device detail fallback | DONE | `exo-ui/src/pc_devices.rs:703` | Updated routing and native ref after `pc.rs` split. |
| `devices-11` | HID relay events (`hid.changed`, `hid.input`, `controller.state`) | DONE | `exo-ui/src/events.rs:425` | Added typed `HostEvent::{ControllerState, HidChanged, HidInput}` in `exo-rpc`, registered via `AppState::register_event` in `events.rs`, handled in `devices_controller.rs`, tested in `exo-rpc::tests::parses_all_newly_wired_host_events`. |
| `captures-01` | Capture sheet grid (thumbnails, filters, chips, On this day) | DONE | `exo-ui/src/captures.rs:3001` | Implemented 4-column 16:9 grid grouped by day, `On this day` resurfacing, Clips/Screenshots/Favorites + game chips, `capture.status` startup load, frosted Liquid Glass sheet, React empty/error states. Verified 1920x1080 demo & live parity (pixel diff **8.10 / 255**, `glass=4`) + unit tests. |
| `captures-02` | Select, bulk delete, rename, open folder | DONE | `exo-ui/src/captures.rs:3034` | Implemented Select mode checkboxes, bulk delete, `Make reel N`, corner Favorite & Delete, Info pane, More menu (`Copy text`, `Make smaller`, `Show in folder`), `capture.item` / `capture.act`. Verified in `captures-sheet` & `phase4_music_mini_player_and_captures_theater`. |
| `captures-03` | Send to (Discord, share) | DONE | `exo-ui/src/captures.rs:1549` | Implemented Share menu (`Send to Discord` multi-target DM/channel modal using `discord.home`, `discord.channels`, `capture.send` + optional note, and `Copy picture` / `Copy file`). |
| `captures-04` | Clip editor (trim, play, export) | DONE | `exo-ui/src/captures.rs:1744` | Implemented `ClipEditor` with draggable `TrimBar`, `0.5x`/`1x`/`2x` speed, `Loop`, Play/Audio transport, Trim save and Export presets (`Discord` <10MB, `16:9`, `9:16`) via `capture.edit`. Verified 1920x1080 `captures-clip-edit` snap + `phase4_music_mini_player_and_captures_theater`. |
| `captures-05` | Snip editor (mark up, crop, copy text OCR, save) | DONE | `exo-ui/src/snip_editor.rs:567` | Built on Rune `ImageCanvas` / `ImageCanvasState` (`CanvasTool::{Crop, Arrow, Rect, Freehand}`): Crop presets (`Free`, `16:9`, `9:16`, `Square`) + Whole picture reset, Mark up (`Arrow`, `Box`, `Highlight`, `Draw`), cell-average RGBA `Blur`, `Copy text` OCR (`capture.text` + `Tx::copy`), Revert/Undo/Redo/Save PNG (`capture.saveImage`), Ctrl+Z/Ctrl+Y/Esc shortcuts. Verified 1920x1080 `captures-snip-edit` snap + 3 unit tests. |
| `captures-06` | Notes buttons (capture menu + sheet header) | DONE | `exo-ui/src/captures.rs:1330` | Fixed popover (`New note`, `Notes`, `Capture settings`) and sheet header `Notes` button to open `NotesView`. Verified live popover menu parity **2/2 PASS** when Capture not installed and 7/7 in demo mode. |
| `captures-07` | Notes (Markdown editor, files, session, photo drop, spellcheck) | DONE | `exo-ui/src/notes.rs:1833` | Implemented full `NotesView` with all 11 `notes.*` RPCs + `notes.open` event: sidebar search, tabs, inline rename, unsaved/external close confirm, Markdown toolbar + live preview with clickable checklists & Photos images, Text/Markdown toggle, Find & Replace (`Aa`/Word/Prev/Next/Replace/Replace all), photo drag-drop (<=25 MB via `on_drop_files` + `notes.photo`), native `spellcheck` + `notes.spell`, 450ms autosave, palette commands (`New note`, `Notes`, search matches), Ctrl+N/O/S/Shift+S/W/F/H/Tab/B/I. Verified live React vs Native parity (Open PASS, `glass=4`, pixel diff **8.36 / 255**) + 6 unit tests. |
| `captures-08` | Capture settings | DONE | `exo-ui/src/captures.rs:2623` | Implemented full `CaptureSettings`: Instant replay, Replay length + retained/limit details, Frame rate, Quality slider + encoder line, Record voice chat, live hotkey recorder + conflict warnings, Print Screen snips, Save to Open/Change (`shell.pickFolder`), Max storage, Auto-delete after. Verified 1920x1080 demo & live snaps (`glass=4`, pixel diff **8.15 / 255**). |
| `captures-09` | Highlight reel ("Making your reel") | DONE | `exo-ui/src/captures.rs:2923` | Implemented `reel_of_game`, `reel_of_picked`, `capture.reel` / `capture.reelCancel`, `capture.reel` event handling, and `ReelLine` banner (progress bar, waiting-for-game-end, canWait prompt with `Wait` & `Cancel`). Verified via `reel_of_game_picks_newest_session_and_prefers_two_starred` test. |
| `captures-10` | `capture.shrunk` event (compaction) | DONE | `exo-ui/src/captures.rs:375` | Added typed `HostEvent::CaptureShrunk` in `exo-rpc`, registered via `AppState::register_event` in `events.rs`, updates item size/URL and viewer state in place, tested in `exo-rpc::tests::parses_all_newly_wired_host_events`. |

## Visual & Interactive Parity Harness (Before vs After)

Harness run at `1920x1080` scale `1` using `apps/launcher/tools/parity/run.mjs` against a disposable copy seeded from `%LOCALAPPDATA%\ExoLauncher` (excluding `app\`) and the repo Debug host (never touching the installed app, real data dir, or owner's running Exo). Added 3 new harness states (`shell-capture-notes`, `shell-capture-clip-edit`, `shell-capture-snip-edit`) in `apps/launcher/tools/parity/states.mjs`.

| State | Before (Pre-WP10 / Initial Pass) | After (Final WP10 Pass) |
|---|---|---|
| `devices-overview` | Unfiltered device cards; DualSense Edge misrouted | Open **PASS**, Cursors **10/10 PASS**, pixel diff **1.42 / 255** |
| `devices-controller` | BROKEN (static connection card only; `controller.*` uncalled) | Open **PASS**, Cursors **11/11 PASS**, pixel diff **6.44 / 255** (live DualSense Edge) |
| `devices-audio` | PARTIAL (no format picker, Exo Voice, AutoEQ, APO, Night Shift; mic always `None`) | Live ZiiGaat Lush + fifine Mic + Exo EQ + Exo Voice rendered cleanly; Cursors **10/10 PASS** |
| `shell-capture-popover` | Notes buttons broken (reopened Captures); missing Capture settings row | Open **PASS**, Menus **2/2 PASS** (live), Glass **PASS** |
| `shell-capture-sheet` | Initial WP10 pass: `glass=9`, single full-width card bug | Open **PASS**, `glass=4`, 4-col 16:9 grid, pixel diff **8.10 / 255** |
| `shell-capture-notes` | MISSING (entire Notes feature absent); initial pass: `glass=9`, empty tab bar hairline | Open **PASS**, `glass=4`, frosted glass sheet, empty tab bar hidden, pixel diff **8.36 / 255** |
| `shell-capture-settings` | PARTIAL; initial pass: `glass=15` | `glass=4`, all Recording + Storage controls, pixel diff **8.15 / 255** |
| `shell-capture-clip-edit` | PARTIAL; initial pass: `glass=9`, clipped inspector hint text | `glass=3`, wrapped inspector copy, draggable TrimBar + Export presets |
| `shell-capture-snip-edit` | MISSING; initial pass: `glass=9` | `glass=3`, Rune `ImageCanvas` Crop/Markup/Blur/OCR verified |

## Gates Verified

All commands run fresh after the final edit:

- `cargo fmt --check` (in `apps/launcher/native-ui`): **exit 0**
- `cargo clippy --workspace --all-targets -- -D warnings`: **exit 0** (0 warnings)
- `cargo test --workspace`: **exit 0** (`exo-kit` 4 passed, `exo-rpc` 22 passed / 1 ignored bench, `exo-ui` 94 passed — including 19 new WP10 unit tests)
- `cargo build --release -p exo-ui`: **exit 0**
- `node apps/launcher/tools/check-comments.mjs`: **exit 0** (225 zero-comment files; 0 comments in new/edited files)
- `node --test apps/launcher/tools/parity/parity.test.mjs`: **exit 0** (8/8 passed)
