# Room Parity Report — Apps (`apps-installed`, `apps-startup`)

- **Branch:** `UhhErix/parity-apps` (base: `feat/native-polish-2`)
- **Date:** 2026-11-24
- **Parity command:** `node apps/launcher/tools/parity/run.mjs --rooms apps --host-exe <staged-debug-host> --exo-ui apps/launcher/native-ui/target/debug/exo-ui.exe --seed-from %LOCALAPPDATA%\ExoLauncher`

## Before vs After

| Metric | Before | After |
| --- | --- | --- |
| States PASS | 2/2 (with skeleton & mismatched Startup UI) | **2/2 PASS** |
| CRITICAL / HIGH / MEDIUM / LOW | 0 / 0 / 0 / 0 (glass warning 3/4 on both) | **0 / 0 / 0 / 0** |
| Open / Menus / Cursors / Glass failures | 0 / 0 / 0 / 2 glass | **0 / 0 / 0 / 0** |
| Cursor checks | 21/21 | **21/21** |
| Glass parity (React / Native) | 3/4, 3/4 | **3/3, 3/3** |
| `apps-installed` pixel diff | 4.29 | **1.11** |
| `apps-startup` pixel diff | 3.80 | **0.67** |
| **Mean pixel diff** | **4.04** | **0.89** |

## Summary of Changes

1. **Full 1:1 `StartupView` parity (`exo-rpc/src/pc_types.rs`, `exo-ui/src/pc_state.rs`, `exo-ui/src/pc_wp9_actions.rs`, `exo-ui/src/main.rs`, `exo-ui/src/pc_apps.rs`):**
   - Extended `StartupItem` (`command`, `without`, `icon`) and `StartupRemovedItem` (`icon`) to carry the full host RPC payload.
   - Added `toggle_startup_ids` to batch-toggle grouped startup items by name (matching React's `byName` grouping).
   - Ported React's `StartupView` sections: `Broken (N)`, `Starts with Windows (N)`, `Turned off (N)`, and collapsible `Background tasks & services (N)` (`startup_bg_open`), with 28×28 icon/monogram tiles, `On` / `Off` / `Required` / `App removed` pill tags, `startup_detail` precedence, `Turned off {Mon D, YYYY}` formatting, 72px memory column, and 108px switch/action column.
   - Loaded startup item & removed item icons via `apps.icons` and re-queried `apps.list` in `load_live_state_sync` so native renders the revalidated folder sizes and icon set.
2. **Installed Apps visual & layout 1:1 parity (`exo-ui/src/pc_apps.rs`):**
   - Replaced glass search input with a flat rimmed pill input (exact `3/3` glass match on both states).
   - Matched `#dcdcdc` secondary panel ink (`--exo-glass-panel-secondary`), `#ff7979` danger button label tint, 26px/10px rimmed action pills, gradient top rim on `apps-surface`, chevron sort indicators, lowercase junk detail formatting, visible-at-rest `Uninstall` pills, and collapsible `Needed by Windows, games or Exo` placement for `Allow Edge` / `Add back`.
   - Aligned `ready_row`, `app_table_row`, `startup_line_row`, and column widths (`72px` size, `108px` actions) to sub-pixel parity with React.
3. **Parity harness reliability (`tools/parity/states.mjs`):**
   - Replaced fixed sleep waits on `apps-installed` and `apps-startup` with `waitFor: '.exo-apps-name'` so React never snaps the loading skeleton.
4. **Unit tests (`exo-ui/src/pc_apps.rs`):**
   - Updated uninstall rest-visibility test and added unit tests covering `format_turned_off_day`, `startup_detail` precedence, duplicate-name grouping, `Broken`/`Starts with Windows`/`Turned off` bucketing, and collapsible `Background tasks & services` toggle.
