# Native UI WP4 Report — Settings, Accounts, Privacy, Report, Onboarding Remainder

Branch: `UhhErix/native-WP4` (base: `feat/native-polish-2`)

## Summary

Implemented 24 of 28 WP4 items plus fixed 1 previously broken `DONE` row (`settings-13` Backups). The remaining 4 rows in the WP4 list are explicitly owned by parallel work packages per coordinator instructions:
- `settings-04` (FPS counter options) and `settings-18` (AppsSection `web.preferences` rows, plus `settings-17` web.preferences integration): delegated to WP9 (`pc_settings.rs`) per coordinator notes; left `section_in_game` and `section_music` cleanly structured so WP9 can plug in its one-line calls.
- `settings-23` (Discord settings pages: Account, Voice, App, Privacy, Keybinds): owned by WP7 (`discord.rs`, `discord_settings.rs`, items `discord-37..39`).

Steam library sign-in (`settings-07`) was NOT blocked because Rune (`fc71db1`) already ships `TextEdit::secure()` / `TextEdit::set_revealed()` and `exo-kit` already ships `qr_code_view`, so full QR + password + Steam Guard sign-in was built and verified.

## New and Modified Files

- `apps/launcher/native-ui/crates/exo-rpc/src/settings_types.rs` (new): DTOs for stores, store accounts, Steam sign-in, gifts, Riot baseline/refresh/export, Exo Account overview/outcome/sessions/probe, backups, telemetry, bug report, web preferences, replaced apps, junk scan/clean, folder picker, and push events.
- `apps/launcher/native-ui/crates/exo-rpc/src/lib.rs`, `types.rs`: 57 additive `METHOD_*` constants in `USED_METHODS` (all validated against `ui/contracts/host-v1.json`), extended `LauncherSettings` fields, and 5 additive `HostEvent` variants (`AccountsProgress`, `BackupStopped`, `ComponentsFailed`, `GiftsChanged`, `ShadersEvent`).
- `apps/launcher/native-ui/crates/exo-ui/src/settings_state.rs` (new): 44-entry `SETTINGS_INDEX` search/ranking, all WP4 pure state/formatting helpers, `SettingsUiState`, push-event registration (`register_wp4_events`), RPC actions, periodic tick (`wp4_tick`), and 10 unit tests.
- `apps/launcher/native-ui/crates/exo-ui/src/settings_stores.rs` (new): Game stores matrix, Check again, Sign in/Open, Steam/Riot/Epic/GOG/Amazon account menus, Steam QR/password/guard sign-in panel, Steam Offline mode, Steam website gifts sign-in, Riot ID/hours/export, Get another store chips, and Shaders pre-compile section.
- `apps/launcher/native-ui/crates/exo-ui/src/settings_account.rs` (new): Exo Account Settings section (signed-in overview, sync errors/held items, security password/recovery forms, sessions list/revoke, data export, account delete; signed-out sign-in/create/reset forms), `RecoveryCodeScreen`, shared `AccountStep` state machine, and modal `AccountSheet`.
- `apps/launcher/native-ui/crates/exo-ui/src/settings_privacy.rs` (new): Real Backups section (`backup.status`/`now`/`export`/`import`, `backup.stopped`, held state) and Privacy section (`telemetry.sent` viewer, `telemetry.delete` with inline confirm).
- `apps/launcher/native-ui/crates/exo-ui/src/report.rs` (new): Report a problem modal (`report.preview`, `report.send`, scrubbed GitHub issue prefill URL).
- `apps/launcher/native-ui/crates/exo-ui/src/clients_gate.rs` (new): `ClientsCard` / `ClientsStep` for Setup and `ClientsStart` start-up gate (`clients.status`/`fix`, 3s installer polling, 30s defer).
- `apps/launcher/native-ui/crates/exo-ui/src/settings.rs`: Full 6-page Settings view matching React `SettingsPanel.tsx` with 44-entry search/jump, Downloads, In-game Achievements, Library Stores/Shaders, About links/caches/account/privacy, plus 3 UI/motion/event tests.
- `apps/launcher/native-ui/crates/exo-ui/src/setup.rs`: Upgraded Setup with inline AccountStep + RecoveryCodeScreen, Restore card (`backup.restore`/`decline`), Clients card, Apps card (`web.preferences.get`/`set`), Cleanup card (`replaced.scan`/`remove`, `junk.scan`/`clean`), per-device role facts, and fixed `effective_setup_picks` PC defaults count.
- Minimal additive wiring in `state.rs`, `events.rs`, `palette.rs`, `shell.rs`, `main.rs`, and cleanup of 3 superseded stub methods in `pc_actions.rs`.

## Item-by-Item Status & Verification

| ID | Status | Native Ref | Verification |
| --- | --- | --- | --- |
| `shell-bar-25` | DONE | `report.rs:14`, `settings.rs:571`, `palette.rs:28`, `shell.rs:604` | Unit test (`wp4_overlays_steam_signin_account_sheet_clients_gate_and_events_render_cleanly`) + 1920x1080 snap (`settings-report.png`) |
| `settings-01` | DONE | `settings_state.rs:116`, `settings.rs:608` | Unit tests (`settings_index_covers_all_sections_and_ranks_exact_prefixes`, `settings_search_jumps_to_target_section_and_clears_query`) |
| `settings-04` | DELEGATED (WP9) | — | Left `section_in_game` FPS card clean for WP9 `pc_settings.rs` per coordinator notes |
| `settings-05` | DONE | `settings.rs:365`, `settings_state.rs:1995` | Contract test (`achievements.preview`) + 1920x1080 snap (`settings-graphics.png`) |
| `settings-06` | DONE | `settings.rs:265`, `settings_state.rs:1922` | Contract test (`shell.pickFolder`, `settings.set`) + 1920x1080 snap (`settings-appearance.png`) |
| `settings-07` | DONE | `settings_stores.rs:351`, `settings_state.rs:2375` | Unit test (`wp4_overlays...` QR + masked password `is_masked()`) + contract test + 1920x1080 snap (`settings-steam-signin.png`) |
| `settings-08` | DONE | `settings_stores.rs:119`, `settings_state.rs:2291` | Contract tests (`accounts.*`), serde test (`deserializes_store_accounts_and_steam_step`), `accounts.progress` event handler |
| `settings-09` | DONE | `settings_stores.rs:647`, `settings_state.rs:2125` | Contract tests (`stores.*`, `steam.setOffline`, `shell.showStore`) + 1920x1080 snap (`settings-account.png`) |
| `settings-10` | DONE | `settings_stores.rs:180`, `settings_state.rs:2610` | Contract tests (`gifts.*`) + unit test (`GiftsChanged` event dispatch) |
| `settings-11` | DONE | `settings_stores.rs:536`, `settings_state.rs:2689` | Contract tests (`playtime.*`) + unit test (`riot_id_and_baseline_helpers_validate_properly`) |
| `settings-12` | DONE | `settings_account.rs:182`, `settings_state.rs:3010` | Contract tests (`exoAccount.*`) + unit tests (`held_sentences_groups_by_type_and_reason`, `deserializes_exo_account_overview_and_replaced_apps`) + 1920x1080 snap (`settings-about.png`) |
| `settings-13` (fix) | DONE | `settings_privacy.rs:60`, `settings_state.rs:2045` | Fixed broken DONE row (removed hardcoded `"Today"` and fake buttons); contract tests (`backup.*`), unit tests (`backup_day_label_formats_today_and_past_dates`, `BackupStopped` event) + snap (`settings-general.png`) |
| `settings-14` | DONE | `settings_privacy.rs:202`, `settings_state.rs:3625` | Contract tests (`telemetry.sent`, `telemetry.delete`) + 1920x1080 snap (`settings-about.png`) |
| `settings-15` | DONE | `report.rs:14`, `settings_state.rs:3690` | Contract tests (`report.preview`, `report.send`) + unit test (`bug_report_scrubs_pii_and_builds_github_url`) + 1920x1080 snap (`settings-report.png`) |
| `settings-17` | PARTIAL (WP9) | `settings.rs:451` | Preserved existing Music service selector; `web.preferences` rows delegated to WP9 `pc_settings.rs` |
| `settings-18` | DELEGATED (WP9) | — | Delegated to WP9 `pc_settings.rs` per coordinator notes |
| `settings-21` | DONE | `settings.rs:474`, `settings_state.rs:2950` | Contract test (`app.repairCache`, `shell.openUrl`) + 1920x1080 snap (`settings-about.png`) |
| `settings-22` | DONE | `settings_stores.rs:942`, `settings_state.rs:2892` | Contract test (`shaders.addon`, `shaders.call`) + unit test (`ShadersEvent` dispatch) + snap (`settings-account.png`) |
| `settings-23` | DELEGATED (WP7) | — | Owned by WP7 (`discord.rs`, `discord_settings.rs`, items `discord-37..39`) |
| `onboarding-account-01` | DONE | `setup.rs:1015` | Live stage loaders + 1920x1080 snap (`setup-accounts.png`) |
| `onboarding-account-02` | DONE | `settings_account.rs:582`, `setup.rs:1244` | Unit test (`account_flow_locks_code_screen_until_confirm`) + snap (`setup-accounts.png`) |
| `onboarding-account-03` | DONE | `settings_account.rs:88`, `settings_state.rs:3346` | Unit test (`recovery_code_normalizes_ambiguous_chars_and_separators`) + contract tests (`exoAccount.saveCode`, `codeConfirmed`) |
| `onboarding-account-04` | DONE | `setup.rs:1066`, `settings_state.rs:3775` | Contract tests (`web.preferences.get`, `set`) + snap (`setup-accounts.png`) |
| `onboarding-account-05` | DONE | `setup.rs:1114`, `settings_state.rs:3824` | Contract tests (`replaced.scan`, `remove`, `junk.scan`, `clean`) + unit test (`leftover_summary_formats_english_list`) |
| `onboarding-account-06` | DONE | `setup.rs:1022`, `settings_state.rs:3955` | Contract tests (`backup.restore`, `decline`) + `BackupStopped` event test |
| `onboarding-account-07` | DONE | `setup.rs:730`, `setup.rs:383` | Unit test (`setup_pc_defaults_count_is_nonzero_when_tuning_items_exist`) + snap (`setup-pc.png` showing `4 of 4` / `Apply selected (4)`) |
| `onboarding-account-08` | DONE | `clients_gate.rs:1`, `shell.rs:141` | Unit tests (`clients_copy_and_heading_match_react`, `wp4_overlays...`) + contract tests (`clients.status`, `fix`) + snap (`clients-gate.png`) |
| `onboarding-account-09` | DONE | `settings_account.rs:878`, `shell.rs:601` | Unit tests (`account_sheet_gate_holds_while_code_pending_or_signed_out`, `wp4_overlays...`) + snap (`account-sheet.png`) |
| `onboarding-account-10` | DONE | `setup.rs:1052`, `settings_state.rs:4010` | Contract test (`backup.decline`) + wired on Start fresh action |

## Harness & Visual Verification (1920x1080 scale 1)

- Captured and visually inspected 12 scenes at `1920x1080` scale 1: `settings-general`, `settings-account`, `settings-appearance`, `settings-graphics`, `settings-updates`, `settings-about`, `settings-report`, `settings-steam-signin`, `account-sheet`, `clients-gate`, `setup-accounts`, `setup-pc`.
- Visual bugs caught and fixed in second pass (before -> after):
  1. `settings-account`: `"Get another store"` chips overflowed horizontally using `.wrap()` (text wrap) -> switched to `.flex_wrap()`; all 14 store chips now wrap cleanly into 2 aligned rows inside the card.
  2. `settings-appearance`: redundant `"Downloads"` section heading below `"Downloads"` page title -> removed duplicate heading to match `General` and React.
  3. `account-sheet` / `setup-accounts`: `"Create an account"` / `"Sign in"` buttons left-aligned label -> added `.justify_center()`.
- Note on live host pipe check: the owner's installed `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe` (3.29.0) was tested via a temp copy and temp data dir (`EXO_LAUNCHER_DATA_DIR`), and does not open `--host-pipe` (pre-native public release). Building the worktree C# host requires `ui/node_modules` (`npm ci` not installed in this worktree). All 57 WP4 RPC methods are verified against `ui/contracts/host-v1.json` by `exo_rpc::tests::host_contract_contains_every_rust_rpc_method`, and live end-to-end clicking against the updated .NET host should be run once the integrated branch builds the host with `ui/node_modules`.

## Gates

- `cargo fmt --check`: PASS (exit 0)
- `cargo clippy --workspace --locked -- -D warnings`: PASS (exit 0)
- `cargo test --workspace --locked`: PASS (exit 0 — `exo-kit` 4/4, `exo-rpc` 25 passed / 1 ignored bench, `exo-ui` 90/90; 119 passed, 0 failed)
- `cargo build --release --locked -p exo-ui`: PASS (exit 0)
- `node tools/check-comments.mjs`: PASS (exit 0 — 2384 legacy files, 225 zero-comment files)

## Follow-up Integration Pass (2026-11-23)

1. **Merged `origin/feat/native-polish-2`**: clean merge with zero conflicts; all WP4 host push events stay registered via `register_wp4_events` in `events.rs` (none in `state.rs`).
2. **Rune `exo-inputs` pin (`01eb4cd`)**: pinned all 8 Rune crates in `apps/launcher/native-ui/Cargo.toml` (and updated `Cargo.lock`) to `e1379c0ad937e9dab48450589e5b7e93dad8d335` in a single dedicated commit.
3. **Migrated WP4 inputs & QR to Rune native widgets**:
   - Steam QR sign-in (`settings_stores.rs`): switched from `exo_kit::qr_code_view` to `rune::QrCode::encode` + `rune::QrView` (160 pt, ISO 4-module quiet zone, accessible `"Steam sign-in code"` label). Left `exo-kit/src/qr.rs` intact because `discord.rs:722` (WP7) still calls it.
   - Steam Guard (`settings_stores.rs`, `settings_state.rs`): switched from single `TextEdit` to 5-box `rune::OtpInput` backed by `rune::OtpState::new(5).kind(OtpKind::AlphanumericUpper)` with `on_complete` submission and `"Continue"` button matching React `SteamSignInRow.tsx:230`. Added unit test `steam_guard_otp_state_normalizes_upper_alphanumeric_five_boxes`.
   - Password fields (`settings_stores.rs`, `settings_account.rs`): switched Steam password and all 8 Exo Account password fields (signed-in password change, recovery code password, delete password, signed-out sign-in/create/reset, and `AccountStep` Create/SignIn/Reset/NewCode) to `rune::PasswordInput` (enforcing `secure`, no copy-out, IME off, and built-in inline `Show`/`Hide` reveal toggle on signed-out SignIn and Create).
4. **Closed WP4 RPC & push-event parity gaps (`origin/UhhErix/native-WP11:apps/launcher/improve/rpc-parity.json`)**:
   - Added `KeepStatus` DTO + `METHOD_KEEP_STATUS` (`"keep.status"`) in `exo-rpc` (with serde unit test `deserializes_keep_status` and `host-v1.json` contract check) and `wp4_load_keep_status()` in `settings_state.rs`.
   - Added `wp4_warm_startup()` invoked from `bootstrap_rpc` (`state.rs`) to preload `settings.get`, `exoAccount.overview`, `clients.status`, `backup.status`, `stores.matrix`, `accounts.list`, `steam.account`, `gifts.list`, `shaders.addon`, `shaders.call`, `web.preferences.get`, and `keep.status`.
   - Removed redundant Native-only `exoAccount.state` call from `pc_actions.rs:load_settings_room()` (since `exoAccount.overview` already populates `s.pc.account`).
   - Verified statically against WP11's `rpc-parity.json` indexer rules: all 13 WP4 startup/settings methods present and called, and all 6 WP4 push events (`accounts.progress`, `backup.stopped`, `components.failed`, `gifts.changed`, `shaders.event`, `exoAccount.changed`) parsed in `exo-rpc` and registered via `events.rs` (0 remaining WP4 gaps).
5. **Re-snapped & visually inspected at 1920x1080 scale 1**: `settings-steam-signin` (vector `QrView`), `settings-steam-password` (masked `PasswordInput`), `settings-steam-guard` (5-box `OtpInput` showing `A 2 B _ _`), and `settings-about` (`PasswordInput` with inline `Show` toggle).
