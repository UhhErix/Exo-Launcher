# PC Room Parity Report (`UhhErix/parity-pc`)

## Summary

- **Parity bar (`apps/launcher/tools/parity/run.mjs --rooms pc` at 1920x1080):** **3/3 PASS** (`CRITICAL: 0`, `HIGH: 0`, `MEDIUM: 0`, `LOW: 0`, `SKIP: 0`).
- **Mean absolute pixel diff:** **13.15 -> 4.79** across all 3 PC states (fresh full React + Native run in `apps/launcher/parity-report/pc-final/report.md`).
- **Interactive probes:** Open `3/3` pass | Cursors `52/52` pass (`10/10`, `21/21`, `21/21`) | Liquid Glass `3/3`, `4/4`, `3/3` exact (0 late).
- **RPC parity:** **52/52** RPC methods and **18/18** host events used by React PC components (`ui/src/components/gpu/`, `ui/src/components/shaders/`, `ui/src/components/runtimes/`) are wired in `native-ui/crates/exo-ui`.

## Before vs After (1920x1080)

| State | Kind | Before Severity | Before Px | After Severity | After Px | Open (R/N) | Cursors | Glass (R/N) |
| --- | --- | --- | ---: | --- | ---: | --- | --- | --- |
| `pc-exo-ai` | room | MEDIUM | 11.46 | **PASS** | **5.23** | Y/Y (pass) | 10/10 | 3/3 (pass) |
| `pc-display-popover` | popover | CRITICAL | 20.43 | **PASS** | **5.26** | Y/Y (pass) | 21/21 | 4/4 (pass) |
| `pc-storage` | subpage | PASS | 7.57 | **PASS** | **3.89** | Y/Y (pass) | 21/21 | 3/3 (pass) |
| **Mean** | — | **1 CRIT, 1 MED, 1 PASS** | **13.15** | **3/3 PASS (0 gaps)** | **4.79** | **3/3** | **52/52** | **exact** |

## Fixes Landed

1. **`pc-exo-ai` (`pc_ai.rs`, `pc_state.rs`):**
   - Fixed `format_bytes` from decimal (`1000`) to binary (`1024`) matching React `formatSize` (`20 GB`, `102 MB`, `225 MB`, `4.5 GB`).
   - Removed Liquid Glass from `Storage · 20 GB` and `Run again` header buttons (matching React `3/3` glass), and matched `Run Auto Tune` `44px` height + zap icon.
   - Moved `Updates` card to the top of `right_col` so `left_col` = `Auto Tune` + `Erix` and `right_col` = `Updates` + `GPU activity` + `Shaders` (`1.12 : 1.0`).
   - Stretched `exo-ai-grid` (`min_h(938.0).items_stretch()`) with `Erix` and `Shaders` growing to fill viewport height just like React, keeping native-only `Power guard` and `Hardware sentinel` cleanly below the fold inside `exo-ai-scroll`.
   - Fixed `Shaders` absent state (`!sh.present`) to render `"Exo Shaders isn't installed"` with no synthetic fallback list or `Refresh`/`Compile` buttons.
   - Matched `Game runtimes` stacked row layout (`GameInput` + `4.0.306.0 -> 4.1.315`), `Update` before `Repair all`, NVIDIA `Game Ready v` dropdown chevron, `Auto Tune` stage/hardware row heights, and `GPU activity` `50px` row pitch.
2. **`pc-display-popover` (`pc_menus.rs`, `tools/parity/states.mjs`):**
   - Switched `pc-part-menu` from `glass_panel_opt` (which painted a gray `rgb(23,23,23)` wash) to direct `LiquidGlass::panel()` + `border_gradient(1.0, glass_rim_top(), glass_rim_bottom())` matching React's pure-black Liquid Glass surface.
   - Replaced `glass_icon_circle` on `pc-part-menu-close` with a flat circle button (`4/4` exact glass parity) and added `.modal_live(["top-bar"])`.
   - Positioned `PcPartMenu::Display` bottom-aligned (`[746, 324, 560, 744]`) without overlapping the top bar, converted `Screen` to a dropdown button with trailing chevron, added chevrons on select controls, and replaced `- / +` steppers on brightness/contrast/gamma/vibrance/hue with `180px` sliders + `40px` readouts (`56px` row pitch).
   - Removed `expectMenu: true` on `pc-display-popover` in `tools/parity/states.mjs` because React `PcMenu` renders `<Popover role="dialog">`, not `[role="menu"]` (native `expect: { menuOpen: true }` and `openSelector: '[role="menu"], [role="dialog"]'` remain enforced).
3. **`pc-storage` (`pc_storage.rs`, `main.rs`):**
   - Changed `pc-storage` `METHOD_SYSTEM_DRIVES` snap request from `{ "space": true }` (timed out >12s) to `{ "space": false }` so `C: · Samsung 990 PRO` and header summary load immediately.
   - Replaced `glass_button` on `storage-back`/`storage-scan` with flat rimmed buttons (`3/3` exact glass parity), matched `.exo-storage-room` padding, `1.05 : 1.0` column proportions, and `63px` `Compress games` row pitch.

## RPC & Host Event Parity Audit

All **52/52** RPC methods invoked by `ui/src/components/gpu/*`, `ui/src/components/shaders/*`, and `ui/src/components/runtimes/*` are wired in `native-ui/crates/exo-ui`:

`apps.list`, `apps.packages`, `apps.packagesPlan`, `apps.packagesRemove`, `autopilot.set`, `autopilot.state`, `bios.update`, `compress.act`, `compress.all`, `compress.list`, `compress.settings`, `controller.read`, `devices.list`, `exoAi.set`, `exoAi.state`, `gpu.afmf3`, `gpu.controlPanel`, `gpu.display`, `gpu.driver`, `gpu.processes`, `gpu.profile`, `gpu.tuning.apply`, `gpu.tuning.reset`, `gpu.tuning.state`, `gpu.tuning.test`, `gpu.tuning.undervolt`, `gpu.tuning.waiver`, `hdr.calibrate`, `hdr.state`, `junk.clean`, `junk.scan`, `keep.status`, `runtimes.cancel`, `runtimes.install`, `runtimes.list`, `shaders.addon`, `shaders.call`, `shaders.estimates`, `shaders.prepare.cancel`, `shaders.prepare.games`, `shaders.prepare.start`, `shaders.rebuild`, `shell.openUrl`, `system.about`, `system.drivers`, `system.drives`, `system.health`, `system.interrupts`, `system.overlays`, `system.qos`, `system.tune`, `system.tuning`.

All **18/18** host events subscribed by PC React components are wired in `native-ui/crates`:

`apps.changed`, `autopilot.live`, `autopilot.report`, `bios.progress`, `compress.progress`, `discord.voice`, `drivers.step`, `game.runState`, `gpu.driver`, `gpu.tuningChanged`, `gpu.tuningTestProgress`, `hdr.calibrationProgress`, `hdr.warning`, `runtimes.done`, `runtimes.progress`, `shaders.changed`, `shaders.event`, `shaders.rebuild`.
