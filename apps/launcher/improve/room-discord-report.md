# Room Discord parity: report

Branch: `UhhErix/parity-discord` (base: `feat/native-polish-2`). Scope: native Discord room (Rune, `apps/launcher/native-ui`) against the React room at 1920×1080 using deterministic test-instance-only mock data (`DiscordParityMock`, gated on `EXO_E2E_TEST_INSTANCE=1` and `EXO_DISCORD_PARITY_MOCK=1`; zero real Discord sign-in or network activity).

Status: **9/9 PASS (0 CRITICAL, 0 HIGH, 0 MEDIUM, 0 LOW, 0 SKIP).** Mean absolute pixel diff across all 9 states: **3.66**. RPC parity: **66/66** contract Discord/Friends RPC methods (and **69/69** total RPC methods called by the React Discord room, including `music.playback`, `shell.openUrl`, `spotify.link`) wired in `exo-ui`.

## Scores (9 Discord parity states, 1920×1080)

| Metric | Prior report (`room-discord-after-signedin-7`) | Baseline (`discord-baseline`) | Final (`discord-final`) |
| --- | --- | --- | --- |
| PASS | 0 | 2 | **9** |
| CRITICAL | 1 | 2 | **0** |
| HIGH | 3 | 3 | **0** |
| MEDIUM | 5 | 2 | **0** |
| LOW | 0 | 0 | **0** |
| SKIP | 0 | 0 | **0** |
| Open failures | 0 | 1 | **0** |
| Menu failures | 1 | 2 | **0** |
| Cursor failures | 4 | 6 | **0** |
| Glass failures | 0 | 1 (late: 0) | **0** (late: **0**) |
| Pixel diff >15 | 1 | 1 | **0** |
| Mean abs pixel diff | 11.87 | 5.43 | **3.66** |

## Final Per-State Checklist (`discord-final`)

| State | Kind | Severity | Open (R/N) | Menus | Cursors | Glass (R/N) | Pixels |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `discord-home` | room | **PASS** | Y/Y (pass) | n/a | pass (10/10) | pass (3/3) | 1.67 |
| `discord-status-menu` | popover | **PASS** | Y/Y (pass) | pass (10/10) | pass (21/21) | pass (4/4) | 3.21 |
| `discord-dm` | subpage | **PASS** | Y/Y (pass) | n/a | pass (11/11) | pass (3/3) | 3.27 |
| `discord-dm-context-menu` | context-menu | **PASS** | Y/Y (pass) | pass (13/13) | pass (21/21) | pass (4/4) | 5.37 |
| `discord-server-channel` | subpage | **PASS** | Y/Y (pass) | n/a | pass (11/11) | pass (3/3) | 2.86 |
| `discord-channel-context-menu` | context-menu | **PASS** | Y/Y (pass) | pass (6/6) | pass (17/17) | pass (4/4) | 4.02 |
| `discord-voice-call` | subpage | **PASS** | Y/Y (pass) | n/a | pass (11/11) | pass (6/4) | 4.18 |
| `discord-voice-stream` | popover | **PASS** | Y/Y (pass) | pass (3/3) | pass (14/14) | pass (7/5) | 5.42 |
| `discord-settings-sheet` | modal | **PASS** | Y/Y (pass) | n/a | pass (21/21) | pass (7/5) | 2.98 |

## What changed

1. **Test-instance-only Discord parity mock (`DiscordParityMock.cs`, `DiscordSession.cs`, `WebHostBridge.cs`, `react-driver.mjs`, `native-driver.mjs`, `DiscordParityMockTests.cs`)**:
   - Strictly gated on BOTH `EXO_E2E_TEST_INSTANCE=1` and `EXO_DISCORD_PARITY_MOCK=1`; completely inert in normal builds and real sessions.
   - Serves deterministic seeded guilds, DMs, messages, members, profiles, voice, audio, Go Live sources, and account data without signing in to or posting from any real Discord account.
   - Automatically enabled by the parity drivers for Discord runs when no `--seed-dir`/`--seed-from` override is supplied.
2. **Scene routing, message ordering & named-pipe reliability (`main.rs`)**:
   - Renamed duplicate `"discord-voice"` scene (settings variant) to `"discord-voice-settings"` so `"discord-voice"` resolves to the voice call scene.
   - Wrapped `RpcClient::connect` + `shell.bootstrap` and `discord.status` in bounded retry loops in `load_live_state_sync` so consecutive `--snap` / `--parity-probe` invocations never land on `boot-splash` or `dc-signed-out`.
   - Reversed preloaded `discord.messages` for `"discord-server"` and `"discord-voice"` scenes (matching `"discord-messages"` and React's chronological order).
   - Preloaded `discord.golive` (`sources`) and `discord.account` (`me`) for `"discord-voice"`.
3. **Menus, cursors & developer mode (`menus.rs`, `discord_state.rs`)**:
   - Aligned `developer_mode` default to `false` (matching React and `discord_settings.rs`) and gated `Copy user ID` / `Copy channel ID` DM context items behind `developer_mode` (`13/13` DM menu items, `6/6` channel menu items).
   - Passed trigger keys (`dc-me-trigger`, `dc-dm-{id}`, `dc-ch-{id}`) alongside `top-bar` to `context_menu_layers_live` so trigger hover cursors stay `Pointer`.
4. **Voice stage, Go Live popover & sidebar alignment (`discord_voice.rs`, `discord_voice_state.rs`, `discord.rs`)**:
   - Positioned `dc-golive-panel` at React's exact Share-anchored popover geometry (`left: 707`, `bottom: 76`, `900×620`, `px: 20`, `pt: 16`, `pb: 20`), switched to `LiquidGlass::panel()`, and removed the extra header `Refresh` button (`3/3` menu items, `14/14` cursors).
   - Defaulted `show_voice_chat` to `true` with an equal `grow(1.0)` split matching React's voice stage + `#Lobby` chat layout; kept Share button active state strictly on `is_golive`; only synthesized `You` in the voice roster when `roster.is_empty()`.
   - Matched React's `14px` category top spacing (`mt(14.0)`) on `dc-cat-*` rows in the server channel list.
5. **Discord Settings sheet (`discord_settings.rs`, `discord_state.rs`, `discord.rs`)**:
   - Matched React's 6 top-level settings nav items (`Voice and video`, `Chat`, `Notifications`, `Privacy`, `Account`, `Keybinds`), added `.modal_live(["top-bar"])`, switched modal glass to `LiquidGlass::panel()`, and matched React's Account page vertical profile card (with snowflake-derived `Member since` via `discord::format_snowflake_date(&me.id)`) and 62px action rows (`Edit display name`, `Edit: Username`, `Edit: Email`, `Add/Edit: Phone number`, `Status`, `Custom status`, `Password and security`) (`21/21` cursors, `2.98` px diff).
6. **RPC parity (`discord_chat_state.rs`)**:
   - Audited all 66 contract Discord/Friends RPC methods (`discord.*` + `friends.badge`) and 3 cross-room RPCs (`music.playback`, `shell.openUrl`, `spotify.link`) called by the React Discord room: 68 were already wired in `exo-ui`, and the 1 remaining method (`discord.stickerData` / `METHOD_DISCORD_STICKER_DATA`) is now wired via `fetch_sticker_data` during message hydration (**66/66** contract Discord/Friends, **69/69** total React Discord room RPCs wired).

## Verification gates

All strict verification gates pass on rebased HEAD (`origin/feat/native-polish-2`):
- `node tools/parity/run.mjs --rooms discord` (1920×1080): **9/9 PASS**, 0 CRITICAL/HIGH/MEDIUM/LOW/SKIP, 137/137 cursors, 32/32 menu items, 0 glass warnings (0 late), mean abs pixel diff **3.66**.
- `cargo fmt --all --check`: PASS (exit 0).
- `cargo clippy --workspace --locked -- -D warnings`: PASS (exit 0).
- `cargo test --workspace --locked`: PASS (exit 0).
- `cargo build --release --locked --bin exo-ui`: PASS (exit 0).
- `dotnet build ExoLauncher/ExoLauncher.csproj -c Release -p:Platform=x64`: PASS (exit 0).
- `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64`: PASS (exit 0; 8507 total, 0 failed, 8476 succeeded, 31 skipped).
- `node tools/check-comments.mjs`: PASS (exit 0; comment ratchet ok).
- `node --test tools/parity/parity.test.mjs`: PASS (exit 0; 8/8 tests).
