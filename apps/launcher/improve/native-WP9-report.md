# WP9 — Exo AI / PC, Storage, Apps Native Parity Report

Branch: `UhhErix/native-WP9` (base `feat/native-polish-2`, merged `6158403` from `UhhErix/native-WP3` for `events.rs` registry, STEP 0 split committed as `152877e`).

## Summary

- **29 / 29 WP9 items completed (`DONE`)**, plus all 4 previously `DONE` Apps rows (`apps-01`, `apps-03`, `apps-10`, `apps-11`) audited and migrated to `pc_apps.rs`.
- **0 blocked items**; no Rune fork changes required; `apps/launcher/native-ui/Cargo.toml` Rune rev pin left untouched.
- **STEP 0** split `pc.rs` into `pc_ai.rs`, `pc_apps.rs`, `pc_devices.rs`, and `pc_storage.rs` in its own commit (`152877e`, accepted by coordinator).
- Removed the hardcoded static Auto Tune rows in `pc_ai.rs` and wired the real 6-stage sequential Auto Tune pipeline + real `system.tune` hardware tuning flow.
- Registered all 7 WP9 raw push events (`gpu.driver`, `drivers.step`, `bios.progress`, `compress.progress`, `runtimes.progress`, `shaders.event`, `shaders.rebuild`) through `events::register_pc` (`AppState::register_event`).

## Item-by-Item Status & Verification

| ID | Feature | Status | Native Ref | RPCs / Events | Verification |
|---|---|---|---|---|---|
| `exo-ai-pc-01` | Auto Tune card (stage list, live progress, undo) | DONE | `exo-ui/src/pc_ai.rs:372`, `exo-ui/src/pc_wp9_actions.rs` | `system.tune`, `autopilot.state`, `exoAi.state`, `exoAi.set` | Unit tests (`wp9_tests`), live host `pc` snap (shows real RTX 3070 & Ryzen 5 5600X hardware rows + dynamic stage counts) |
| `exo-ai-pc-02` | Run again / Optimizing PC cinema | DONE | `exo-ui/src/pc_menus.rs:1865`, `exo-ui/src/pc_wp9_actions.rs` | `exoAi.set`, sequential 6-stage pipeline | Unit tests (`auto_tune_stage_count_and_restart_flag_work_from_host_state`) |
| `exo-ai-pc-03` | PawnIO / unsupported CPU/GPU notice | DONE | `exo-ui/src/pc_ai.rs:392` | `shell.openUrl`, `system.tune` | Unit test (`auto_tune_pawnio_and_unsupported_notice_render`) |
| `exo-ai-pc-04` | PC overview (hardware, Windows, Secure Boot, TPM, memory integrity, BIOS) | DONE | `exo-ui/src/pc_ai.rs:1790`, `exo-ui/src/pc_menus.rs:1169` | `system.about`, `system.drives`, `system.tuning`, `bios.update` | Live host `pc` & `pc-menu-board` snaps (verified real CPU/GPU/RAM/VG248/B550-F/2.5 Gbps/Win11 26H2/BIOS 3645/Secure Boot On/TPM 2.0/Memory integrity On) |
| `exo-ai-pc-05` | Autopilot (state, what it found/did, pause, turn back on) | DONE | `exo-ui/src/pc_menus.rs:1089` | `autopilot.state`, `autopilot.set` | Live host `pc-menu-board` snap (Active status, Memory Integrity Protected, real MW4 & Wuthering Waves session reports with expandable Details) |
| `exo-ai-pc-06` | Power & Autopilot group, system tweaks (restore, restart required) | DONE | `exo-ui/src/pc_menus.rs:1169` | `system.tune`, `system.health`, `system.tuning` | Unit tests + live host `pc-menu-board` snap |
| `exo-ai-pc-07` | Interrupts (MSI, DPC latency measure, drivers hold) | DONE | `exo-ui/src/pc_menus.rs:1169` | `system.interrupts` | Unit test (`pc_part_menus_and_storage_render_interactive_host_controls` verifies `ir-apply`) |
| `exo-ai-pc-08` | Display tab (refresh rate, HDR, calibrate, G-Sync, best settings) | DONE | `exo-ui/src/pc_menus.rs:571` | `gpu.display`, `hdr.state` | Unit test (`disp-best-disp1`, `disp-refresh`) + live host `pc-menu-display` snap (VG248 / onn switcher, 164.92 Hz, RGB, 8-bit, Full, DDC brightness/contrast, color sliders) |
| `exo-ai-pc-09` | GPU driver row (latest, install, driver type, profile restore) | DONE | `exo-ui/src/pc_menus.rs:259` | `gpu.driver`, `gpu.profile`, event `gpu.driver` | Unit test (push event `gpu.driver`) + live host `pc-menu-graphics` snap (NVIDIA 621.35 Up to date, Game Ready, Reinstall + license consent dialog) |
| `exo-ai-pc-10` | Driver profile (low latency, boost) | DONE | `exo-ui/src/pc_menus.rs:356` | `gpu.profile` | Live host `pc-menu-graphics` snap (Exo's driver profile Applied + Undo, Resizable BAR On, Frame generation switch) |
| `exo-ai-pc-11` | Drivers & updates (chipset, Windows Update, BIOS, restart) | DONE | `exo-ui/src/pc_ai.rs:733` | `system.drivers`, `system.health`, `bios.update`, `controller.read`, events `drivers.step`, `bios.progress` | Unit test (push events `drivers.step`, `bios.progress`) + live host `pc` snap |
| `exo-ai-pc-12` | AFMF 3 row | DONE | `exo-ui/src/pc_menus.rs:356` | `gpu.afmf3` | Unit test + `host_contract_contains_every_rust_rpc_method` |
| `exo-ai-pc-13` | GPU control panel open | DONE | `exo-ui/src/pc_menus.rs:356` | `gpu.controlPanel` | `host_contract_contains_every_rust_rpc_method` + wired press handler in Graphics menu |
| `exo-ai-pc-14` | Best Setup (use best settings) | DONE | `exo-ui/src/pc_menus.rs:1169`, `exo-ui/src/pc_wp9_actions.rs` | `system.health`, `system.tuning`, `system.interrupts`, `gpu.profile`, `gpu.display` | Unit test (`best_steps_list` + `pc_part_menus_and_storage_render_interactive_host_controls`) |
| `exo-ai-pc-15` | Storage room (drives, scan space, drive settings) | DONE | `exo-ui/src/pc_storage.rs:522` | `system.drives` | Unit test (`storage-room`, `storage-scan`) + live host `pc-storage` snap (Samsung 990 PRO, 573 GB free of 1.8 TB, Healthy, 43 °C, 94% health, Games/Caches/Leftovers/Other, Largest games) |
| `exo-ai-pc-16` | Compress games (all, idle, after updates) | DONE | `exo-ui/src/pc_storage.rs:345` | `compress.list`, `compress.all`, `compress.act`, `compress.settings`, event `compress.progress` | Unit test (`compress.progress` event + `compress_words`) + live host `pc-storage` snap (Saved 21.6 GB, idle & after-updates toggles, per-game Uncompress rows) |
| `exo-ai-pc-17` | Game runtimes (VC++/.NET list, install) | DONE | `exo-ui/src/pc_ai.rs:564` | `runtimes.list`, `runtimes.install`, event `runtimes.progress` | Unit test (`runtimes-install` button + `runtimes.progress` event) |
| `exo-ai-pc-18` | Shaders board (pre-compile all, rebuild, cancel) | DONE | `exo-ui/src/pc_ai.rs:2060` | `shaders.status`, `shaders.prepare.start`, `shaders.prepare.games`, `shaders.prepare.cancel`, `shaders.rebuild`, `shaders.call`, events `shaders.event`, `shaders.rebuild` | Unit test (`shaders.event` + `shaders.rebuild` push events) + live host `pc` snap (8 games, Refresh, Compile) |
| `exo-ai-pc-19` | Keep (CPU/GPU keep-awake status) | DONE | `exo-ui/src/pc_ai.rs:1790` | `keep.status` | Unit test + live host `pc` snap |
| `exo-ai-pc-20` | FPS counter options | DONE | `exo-ui/src/settings.rs:234` | `fps.counter` | Unit test (`fps_counter_options_and_web_preferences_render_in_settings`) + live host `settings-graphics` snap (Show FPS counter ON, Top left, all 9 toggles matching owner settings) |
| `exo-ai-pc-21` | PC part menus (Graphics, Display, Motherboard context menus) | DONE | `exo-ui/src/pc_menus.rs:2272` | Anchored popovers + Esc/backdrop dismiss | Live host `pc-menu-board`, `pc-menu-graphics`, `pc-menu-display` snaps + interactive `--parity-probe` click verification |
| `apps-02` | Uninstall (confirm, progress, leftovers) | DONE | `exo-ui/src/pc_apps.rs:259` | `apps.uninstall`, `apps.confirm`, `apps.clean`, `apps.leave` | Unit tests (`apps_uninstall_hidden_at_rest_reveals_on_hover_and_confirms_in_place`, `apps_hover_and_confirm_motion_gate`, `apps_leftovers_unused_filter_and_installer_cache_helpers`) |
| `apps-04` | Scan for unused apps ("Not opened in three months") | DONE | `exo-ui/src/pc_apps.rs:531` | `apps.scan` | Unit test (`is_unused_app` + `parse_ymd_days`) |
| `apps-05` | Restore point + Undo | DONE | `exo-ui/src/pc_apps.rs:259` | `apps.restorePoint`, `apps.undo` | Unit test (`leftover_cleared_line`, `apps-clean-tidy`) + RPC contract test |
| `apps-06` | Installer cache (scan, clean) | DONE | `exo-ui/src/pc_apps.rs:531` | `apps.installerCache`, `apps.installerCacheClean` | Unit test (`apps-inst-cache-clean` rendered and wired) |
| `apps-07` | Default apps open | DONE | `exo-ui/src/pc_apps.rs:225` | `apps.openDefaults` | Unit test (`apps-defaults-tidy` rendered and wired) |
| `apps-08` | Replaced apps (remove duplicates) | DONE | `exo-ui/src/pc_apps.rs:531` | `replaced.remove` | RPC contract test + Ready-for-you row in `pc_apps.rs` |
| `apps-09` | Packages: plan / remove / add back | DONE | `exo-ui/src/pc_apps.rs:531`, `exo-ui/src/pc_wp9_actions.rs` | `apps.packages`, `apps.packagesPlan`, `apps.packagesRemove`, `apps.packageAddBack` | RPC contract test + Auto Tune bloatware stage & Apps add-back rows |
| `apps-12` | Edge allow, web preferences | DONE | `exo-ui/src/pc_apps.rs:531`, `exo-ui/src/settings.rs:320` | `apps.allowEdge`, `web.preferences.get`, `web.preferences.set` | Unit test (`set-yt-notifications`) + live host `pc-apps` snap (`Microsoft Edge is blocked from returning` -> `Allow Edge`) + `settings-updates` snap |

## Harness Before / After & Live Verification

- **Before**:
  - `pc.rs` was a monolithic file with hardcoded static Auto Tune stage descriptions (`"No bloatware found"`, `"Graphics & chipset drivers up to date"`), no `system.tune` hardware flow, no cinema overlay, minimal part menus, no Storage drive breakdown/compression, no Apps leftover review/undo/select/installer cache/Edge allow, no Settings FPS counter options or web preferences, and 0 of the 7 WP9 push events handled.
- **After**:
  - Added harness scenes `pc-menu-board`, `pc-menu-graphics`, `pc-menu-display` and full WP9 host settling in `main.rs`.
  - Captured and visually inspected all 9 scenes at `1920x1080` scale 1 against a temporary copy of `ExoLauncher.exe` and a temporary copy of `%LOCALAPPDATA%\ExoLauncher` (excluding `app\`):
    - `pc`, `pc-menu-board`, `pc-menu-graphics`, `pc-menu-display`, `pc-storage`, `pc-apps`, `pc-apps-startup`, `settings-graphics`, `settings-updates`.
    - Visual polish pass caught and fixed part-menu scroll max-height (`620 -> 480`) so `Motherboard` and `Display` popovers stay cleanly below the top bar.
  - Verified live interactive clicks via `--parity-probe --script` against the temp host (`open-graphics`, `open-display`, `open-board`, `open-and-close-graphics`, `open-storage`, `apps-startup`): `blurs <= 2` (within `EXO_MAX_BACKDROP_BLURS`), zero errors in temp `logs\app.log`, all temp copies cleaned up afterward.
  - Neither `%LOCALAPPDATA%\ExoLauncher\app` nor the real `%LOCALAPPDATA%\ExoLauncher` data dir was modified, and the owner's running Exo was never touched.

## Gates

- `cargo fmt --check`: PASS (exit 0)
- `cargo clippy --workspace --locked -- -D warnings`: PASS (exit 0, 0 warnings)
- `cargo test --workspace --locked`: PASS (exit 0 — `exo-kit`: 4 passed; `exo-rpc`: 22 passed, 1 ignored; `exo-ui`: 82 passed)
- `cargo build --release --locked -p exo-ui`: PASS (exit 0)
- Comment ban check on all touched/new Rust files: PASS (0 prose comments across all 12 Rust files)
