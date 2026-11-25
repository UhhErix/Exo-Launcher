# Room Settings Parity Report

Branch: `UhhErix/parity-settings` (base: `feat/native-polish-2` @ `2c0b89a1`).
Harness: `node apps/launcher/tools/parity/run.mjs --rooms settings --host-exe <temp-debug-host> --exo-ui native-ui/target/debug/exo-ui.exe --seed-from %LOCALAPPDATA%\ExoLauncher`.

## Before / After Summary

| Metric | Before | After |
| --- | ---: | ---: |
| States checked | 7 | 7 |
| CRITICAL | 0 | **0** |
| HIGH | 3 | **0** |
| MEDIUM | 2 | **0** |
| LOW | 2 | **0** |
| PASS | 0 | **7 / 7** |
| Open failures | 0 | **0** |
| Menu failures | 0 | **0** (3/3) |
| Cursor failures | 4 (incl. 0/14 on dropdown) | **0** (133/133 = 100%) |
| Glass warnings | 7 | **0** (exact 1:1 count on all 7 states) |
| Mean absolute pixel diff | **2.00** | **0.92** |

## Per-State Results

| State | Before (sev, cursors, glass, px) | After (sev, cursors, glass, px) |
| --- | --- | --- |
| `settings-general` | HIGH, 19/20, warn (12/6), 3.38 | **PASS**, 20/20, pass (6/6), **0.72** |
| `settings-library` | HIGH, 19/20, warn (12/5), 3.71 | **PASS**, 20/20, pass (6/6), **1.57** |
| `settings-downloads` | MEDIUM, 20/20, warn (11/6), 1.51 | **PASS**, 20/20, pass (5/5), **0.89** |
| `settings-ingame` | MEDIUM, 19/20, warn (12/5), 1.13 | **PASS**, 20/20, pass (6/6), **0.78** |
| `settings-music` | LOW, 19/19, warn (11/6), 0.73 | **PASS**, 19/19, pass (5/5), **0.55** |
| `settings-about` | LOW, 20/20, warn (16/5), 2.45 | **PASS**, 20/20, pass (10/10), **1.15** |
| `settings-choice-dropdown` | HIGH, 0/14, warn (13/7), 1.08 | **PASS**, 14/14, pass (7/7), **0.81** |

## Root Causes Fixed

1. **Live host data loading for Settings scenes (`exo-ui/src/main.rs`)**: `load_live_state_sync` now loads `backup.status`, `stores.matrix`, `accounts.list`, `steam.account`, `shaders.addon`, `exoAccount.overview`, `web.preferences.get`, `fps.counter`, and `app.checkUpdate` (and `shaders_present` defaults to `false` until confirmed present).
2. **Native host `friends.badge` & Discord boot (`ExoLauncher/Services/{DiscordClient/DiscordSession,WebHostBridge,RuneHostSession}.cs`)**: In headless `--host-pipe` mode (`RuneHostSession`), `App.MainAppWindow` is null, so `friends.badge` previously returned `0` and `DiscordSession.Start()` / `BadgeChanged` were not wired. Added `DiscordSession.CurrentBadge`, wired `DiscordSession.Start()` and `PostFriendsBadge` in `RuneHostSession`, and styled `discord-nav-badge` in `shell.rs` as white (`FG`) with black text (`PRIMARY_FG`) to match React.
3. **Dropdown modal pass-through & geometry (`exo-kit/src/select.rs`, `exo-ui/src/menus.rs`)**: Added `.modal_live(["top-bar", trigger_key.as_str()])` so the Bar and trigger remain `Cursor::Pointer` while a Choice dropdown is open, set menu row cursor to `Cursor::Default`, and matched React's 240px width, 8px padding, 38px row height, and 4px offset.
4. **Liquid Glass parity & occluded glass filtering (`exo-ui/src/settings*.rs`, `tools/parity/react-driver.mjs`)**: Adopted Rune's `LiquidGlass::panel()` preset on `settings_card()` (matching CSS `blur(16px) saturate(1.8) brightness(0.66)` over `rgb(0 0 0 / 0.36)`), set Settings choice triggers to `.flat(true)` and search input to flat rimmed field, and filtered completely occluded background-room glass nodes in `react-driver.mjs`.
5. **Layout, controls & typography parity (`exo-ui/src/settings*.rs`, `exo-ui/src/state.rs`, `exo-ui/src/shell.rs`, `exo-kit/src/components.rs`, `tools/parity/states.mjs`)**:
   - Matched React's `pageForRoom` landing in `toggle_settings()` and updated `settings-general` React steps to select `General`.
   - Replaced `set-fps-position` button with `select_trigger` + `OpenDropdown::FpsPosition`, removed extra subtitle on `OBS overlay URL`, added 14px circular white knob and `Cursor::Pointer` to `slider_track`.
   - Matched `.exo-set-btn.is-danger` dark pill + red label, 14px button horizontal padding, 65px 2-line row height, 16px store icon/control gap, 24px Account section gap, `-0.02em` page title tracking, `FG.alpha(0.15)` active sidebar pill, and 13px nav tab padding.

## Gates

- `cargo fmt --check`: PASS (0)
- `cargo clippy --workspace --locked -- -D warnings`: PASS (0)
- `cargo test --workspace --locked`: PASS (0)
- `cargo build --release --locked -p exo-ui`: PASS (0)
- `dotnet test --project apps/launcher/ExoLauncher.Tests -c Release -p:Platform=x64`: PASS (0)
- `node apps/launcher/tools/check-comments.mjs`: PASS (0)
- `node --test apps/launcher/tools/parity/parity.test.mjs`: PASS (0)
