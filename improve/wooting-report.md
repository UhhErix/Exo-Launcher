# Wooting Keyboard Full-Support Report (`wooting-all`)

## 1. Complete Wooting Support Matrix

Cross-checked against `WootingKb/wooting-rgb-sdk` (`src/wooting-usb.c`, `src/wooting-usb.h`), `WootingKb/wooting-analog-sdk`, `OpenMouse-Project/keyboard-protocol` (`docs/wooting-support.md`), and live HID descriptor inspection on the connected Wooting 60HE v2 (`VID_31E3&PID_1342`, firmware `2.15.0-beta.0`).

### 1.1 Device Table (VID/PID, Interfaces, Features, Layouts)

All `0x31E3` devices mask PIDs by `pid & 0xFFF0` to cover gamepad compatibility low-nibble variants (`base | 0` no gamepad, `base | 1` XInput, `base | 2` Classic/DirectInput).

| Model | Firmware / MCU | VID | Base PID (Variants) | Config Usage Page | Analog Usage Page | Report Framing & Sizes | Max Scan Hz | Actuation Range | Rapid Trigger | Tachyon | Profiles | RGB | Physical Layouts (`FormFactor`) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Wooting One (Legacy) | v1 / Atmel | `0x03EB` | `0xFF01`, `0x2402` | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 129B/257B out | 1000 Hz | 1.5–3.6 mm (Flaretech optical) | No | No | Yes | Yes (per-key + brightness) | TKL ANSI / ISO (`tkl`) |
| Wooting One | v2 / Atmel | `0x31E3` | `0x1100` (`1100`–`1102`) | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 257B big / 4×65B small | 1000 Hz | 1.5–3.6 mm (Flaretech optical) | No | No | Yes | Yes | TKL ANSI / ISO (`tkl`) |
| Wooting Two (Legacy) | v1 / Atmel | `0x03EB` | `0xFF02`, `0x2403` | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 129B/257B out | 1000 Hz | 1.5–3.6 mm (Flaretech optical) | No | No | Yes | Yes | Full + A1–A3 / Mode ANSI / ISO (`full`) |
| Wooting Two | v2 / Atmel | `0x31E3` | `0x1200` (`1200`–`1202`) | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 257B big / 4×65B small | 1000 Hz | 1.5–3.6 mm (Flaretech optical) | No | No | Yes | Yes | Full + A1–A3 / Mode ANSI / ISO (`full`) |
| Wooting Two Lekker Edition | v2 / AVR | `0x31E3` | `0x1210` (`1210`–`1212`) | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 257B big / 4×65B small | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | Full + A1–A3 / Mode ANSI / ISO (`full`) |
| Wooting Two HE (AVR) | v2 / AVR | `0x31E3` | `0x1220` (`1220`–`1222`) | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 257B big / 4×65B small | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | Full + A1–A3 / Mode ANSI / ISO (`full`) |
| Wooting Two HE (ARM) | v2.12+ / ARM | `0x31E3` | `0x1230` (`1230`–`1232`) | `0xFF55` (fallback `0x1337`) | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `HIDP_CAPS.OutputReportByteLength` | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | Full + A1–A3 / Mode ANSI / ISO (`full`) |
| Wooting 60HE (AVR) | v2 / AVR | `0x31E3` | `0x1300` (`1300`–`1302`) | `0x1337` | `0xFF54` | Unnumbered (`0xD0 0xDA`), 8B feat, 257B big / 4×65B small | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | 60% ANSI / ISO (`60`) |
| Wooting 60HE (ARM) | v2.12+ / ARM | `0x31E3` | `0x1310` (`1310`–`1312`) | `0xFF55` (fallback `0x1337`) | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `OutputReportByteLength` | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | 60% ANSI / ISO (`60`) |
| Wooting 60HE+ | v2.12+ / ARM | `0x31E3` | `0x1320` (`1320`–`1322`) | `0xFF55` (fallback `0x1337`) | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `OutputReportByteLength` | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | 60% ANSI / ISO (`60`) |
| Wooting 60HE v2 | v2.15+ / ARM | `0x31E3` | `0x1340` (`1340`–`1342`) | `0xFF55` | `0xFF53` | Numbered multi-report (`0xD1 0xDA`), live `HIDP_CAPS`: 2047B in/out, 8B feat; IDs 1→32B, 2→62B, 3→254B, 4→510B, 5→1022B, 6→2046B | 8000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes | 60% ANSI / ISO / **ANSI Split** / **ISO Split** (`2.75u Space L | 1.25u Fn | 2.25u Space R`) (`60`) |
| Wooting 80HE | v2.12+ / ARM | `0x31E3` | `0x1400` (`1400`–`1402`) | `0xFF55` | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `OutputReportByteLength` | 8000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes (+ 10-seg LED bar) | 80% ANSI / ISO / JIS + 10-seg LED bar & Mode key (`80`) |
| Wooting 80HE+ | v2.12+ / ARM | `0x31E3` | `0x1410` (`1410`–`1412`) | `0xFF55` | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `OutputReportByteLength` | 8000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes (+ 10-seg LED bar) | 80% ANSI / ISO / JIS + 10-seg LED bar & Mode key (`80`) |
| Wooting UwU | v2.12+ / ARM | `0x31E3` | `0x1500` (`1500`–`1502`) | `0xFF55` | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `OutputReportByteLength` | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | **No** | 3-key keypad (`keypad3`) |
| Wooting UwU RGB | v2.12+ / ARM | `0x31E3` | `0x1510` (`1510`–`1512`) | `0xFF55` | `0xFF53` | Numbered multi-report (`0xD1 0xDA`, IDs 1..6), padded to `OutputReportByteLength` | 1000 Hz | 0.1–4.0 mm (Hall effect) | Yes | Yes | Yes | Yes (underglow + per-key) | 3-key keypad (`keypad3`) |

### 1.2 Command Set Used by Exo

| Command | Opcode | Transport | Notes |
|---|---|---|---|
| `CmdGetVersion` | `0x03` | Feature (`HidD_SetFeature`) | Returns firmware version; ARM multi-report prefixes 2-byte payload length before major/minor/patch. |
| `CmdSaveRgbProfile` | `0x08` | Feature | Commits active RGB profile changes to flash after `OutSetRgbProfileCore`. |
| `CmdGetDeviceConfig` | `0x13` | Feature | Returns device layout byte (`legacyV1 ? 9 : 10`): `0` ANSI, `1` ISO, `2` JIS, `3` ANSI split spacebar, `4` ISO split spacebar. |
| `CmdActivateProfile` | `0x17` | Feature | Switches active slot (`p0 = slotIndex`). |
| `CmdGetCurrentProfileIndex` | `0x1B` | Feature | Returns active profile slot index. |
| `CmdGetProfileName` | `0x1E` | Feature | Returns UTF-8 profile name for `p0 = slotIndex`. |
| `CmdWootDevInit` | `0x21` | Feature | Initializes/unlocks Wootility config session before mutating writes. |
| `CmdReloadProfile` | `0x26` | Feature | Applies written profile/RGB buffers to active keyboard state. |
| `CmdGetKeyboardProfile` | `0x27` | Feature | Reads protobuf-encoded keyboard profile base (actuation, RT, Tachyon, etc.). |
| `CmdGetRgbProfileCore` | `0x32` | Feature | Reads protobuf-encoded RGB profile core (brightness, etc.). |
| `OutSetKeyboardBase` | `0x11` | Output (`WriteFile`) | Writes protobuf-encoded keyboard profile base; must be padded to `OutputReportByteLength`. |
| `OutSetRgbProfileCore` | `0x16` | Output (`WriteFile`) | Writes protobuf-encoded RGB profile core; must be padded to `OutputReportByteLength`. |

---

## 2. Verified Root Causes & Fixes Applied

### Root Causes Verified
1. **Unpadded HID Output Reports (`WootingKeyboard.cs`)**: The previous host wrote raw 33-byte arrays directly to the `0xFF55` HID output handle. On Windows `hidclass.sys`, `WriteFile` requires buffers matching exact `HIDP_CAPS.OutputReportByteLength` (2047 bytes on 60HE v2, report IDs 1–6), causing writes to fail with `ERROR_INVALID_USER_BUFFER` / `ERROR_GEN_FAILURE` or be dropped.
2. **Missing Command Sequencing (`WootingKeyboard.cs`)**: Writes never sent `CmdWootDevInit` (`0x21`) beforehand, never sent `CmdSaveRgbProfile` (`0x08`) for RGB changes, and never sent `CmdReloadProfile` (`0x26`) afterward, so firmware ignored or did not apply updates.
3. **No Read-Back Verification & Swallowed Errors (`WootingKeyboard.cs`, `KeyboardPage.tsx`)**: Write failures were swallowed or returned optimistic success without re-reading the profile from the device, causing the UI to display success when nothing changed on hardware.
4. **Broken Firmware Version & Layout Parsing (`WootingKeyboard.cs`, `Peripherals.Native.cs`)**:
   - `DecodeVersion` on ARM multi-report replies read the 2-byte length prefix as major/minor, showing `2.15.0-beta.0` as `9.0.2`.
   - `DecodeDeviceLayout` had an off-by-one offset (`8/9` instead of `9/10`).
   - `Peripherals.Native.WootingConfig` matched any `report[0] == 1` without checking `0xD1 0xDA 0x13`, allowing unsolicited `0x49` heartbeat packets to hijack layout detection.
5. **Incomplete Model Catalog & Missing Split-Spacebar / Form-Factor Layouts (`Peripherals.cs`, `KeyboardMap.tsx`, `keyboardLayouts.ts`)**:
   - Legacy Atmel PIDs `0x2402`/`0x2403` were missing from `Peripherals.cs` and `Peripherals.Apps.cs`.
   - `WootingKeyboard.cs` did not clamp optical keyboards (`1.5–3.6 mm`), did not gate Rapid Trigger / RGB per model (e.g., UwU has no RGB; One/Two have no RT), did not patch Tachyon field 9 alongside field 5, and clamped Rapid Trigger minimum to `0.15 mm` instead of `0.10 mm`.
   - React `KeyboardMap` only rendered a generic layout without the 60HE v2 split spacebar (`2.75u Space L | 1.25u Fn | 2.25u Space R`), 80HE 10-segment LED bar + Mode key, ISO/JIS keys, Full A1–A3/Mode keys, or UwU 3-key keypad.

### Key Changes Made
- **Host (`apps/launcher/ExoLauncher/Services/`)**:
  - `WootingKeyboard.cs`: Added complete 17-entry `Catalog` (`0x31E3` `& 0xFFF0` + legacy `0x03EB`), `IWootingTransport` abstraction, `HidWootingTransport.Inspect` querying `HidP_GetCaps` & `HidP_GetValueCaps` (`ParseOutputReportSizes`), `EncodeProfileOutputReport` padding to `OutputReportByteLength` with report ID ladder selection, `SplitOutputPackets` supporting multi-report / 257B big / 4×65B pre-v2.12 small packets, `HidWootingTransport.TransactFeature` assembling multi-packet 65B small replies, `CmdWootDevInit` → auto-backup → `OutSetKeyboardBase` / `OutSetRgbProfileCore` + `CmdSaveRgbProfile` → `CmdReloadProfile` → `ReadLocked` + `VerifyProfileReadBack`, Tachyon field 5+9 patching, `MinRtMm = 0.10`, and fixed `DecodeVersion` + `DecodeDeviceLayout`.
  - `WootingKeyboard.Backups.cs`: Backs up and restores both `CmdGetKeyboardProfile` (`0x27`) and `CmdGetRgbProfileCore` (`0x32`) with schema v2 (v1 backwards-compatible), padded output framing, `CmdSaveRgbProfile` + `CmdReloadProfile`, and strict byte-for-byte read-back verification per slot.
  - `WebHostBridge.Keyboard.cs`: Exposes `formFactor`, `hasRapidTrigger`, `hasRgb`, `minActuationMm`, `maxActuationMm`, `layoutCode`, `rgbBrightness`, and accepts `rgbBrightness` in `keyboard.settings`.
  - `Peripherals.cs`, `Peripherals.Apps.cs`, `Peripherals.Native.cs`: Added legacy Atmel PIDs `0x2402`/`0x2403` and strict `0xD1 0xDA 0x13` frame filtering in `WootingConfig`.
- **React UI (`apps/launcher/ui/src/`)**:
  - `components/devices/keyboardLayouts.ts`: Accurate row/unit geometry for `60` (ANSI, ISO, JIS, ANSI split, ISO split), `80` (with 10-segment LED bar & Mode key), `tkl`, `full` (with A1–A3/Mode), and `keypad3` (UwU).
  - `components/devices/KeyboardMap.tsx` & `KeyboardPage.tsx` & `styles/devices.css`: Renders exact model layout with interactive key buttons, model-aware actuation bounds, Rapid Trigger slider (`0.10–2.35 mm`), Lighting panel (RGB brightness slider gated on `hasRgb`), and explicit `WriteErrorBanner` surfacing real host errors.
  - `lib/host.ts` & `lib/hostMock.ts`: Updated `KeyboardRead` and `keyboard.settings` contracts and mock data.
- **Tests**:
  - `apps/launcher/ExoLauncher.Tests/WootingKeyboardTests.cs`: Unit tests for full VID/PID + gamepad variant catalog coverage, capability matrix, golden byte arrays for ARM multi-report and standard commands, `OutputReportByteLength` padding & report ID selection, 4×65B small-packet splitting, real 60HE v2 & legacy reply decoding, protobuf round-trip preserving unknown fields + Tachyon field 9, command sequencing, error surfacing (read-back mismatch, firmware rejection, write failure, stuck slot), and byte-for-byte backup/restore.
  - `apps/launcher/ui/test/keyboardLayouts.test.ts`: Unit tests for unit widths, unique key IDs, split spacebar geometry (`2.75u + 1.25u + 2.25u = 6.25u`), 80HE LED bar, ISO/JIS/UwU layouts, and model-name mapping across all 13 models.

---

## 3. Live Hardware Verification Evidence (Owner's Wooting 60HE v2)

Executed directly against the compiled Release `ExoLauncher.dll` on the owner's connected keyboard (no firmware flashing; disposable test harness; temporary test backup files removed from `%LOCALAPPDATA%\ExoLauncher\keyboard-backups` after verification).

- **Detected Device**:
  - ID: `usb-31e3-1342-95be6909a013` (`VID_31E3&PID_1342`)
  - Model: `Wooting 60HE v2` | FormFactor: `60` | Layout: `ANSI · Split spacebar` (`ansi-split`)
  - Firmware: `2.15.0-beta.0` (previously misreported as `9.0.2`)
  - MaxScanHz: `8000` | Profiles: `[{ Index: 0, Name: "General" }]`
- **Live `HIDP_CAPS` on `MI_02` (`0xFF55`)**:
  - `InputReportByteLength = 2047`, `OutputReportByteLength = 2047`, `FeatureReportByteLength = 8`
  - `OutputReportSizes = (1, 32), (2, 62), (3, 254), (4, 510), (5, 1022), (6, 2046)`
- **Step 1 — Initial Read & Backup**:
  - Slot 0 Base (`0x27`, 22 bytes): `0A140880E0011001180120E684012801300238014801`
    - SHA-256: `b6122a5b786c23381a7c2c95bf1b6432e5e6262a6b896217c1622bfaf6c95e54`
    - Decoded: Actuation `3.0 mm`, Rapid Trigger `true` (`0.15 mm`), Tachyon `true`
  - Slot 0 RGB Core (`0x32`, 15 bytes): `0A0B08B30110FFFF0318FFFF032200`
    - SHA-256: `7e450ecf6d5b7909185fa31a58377d80942a660f7a65bf552db9a3022f3cb63c`
    - Decoded: Brightness `70%` (`179/255`)
  - Backup saved (`wooting-1342-20261123-074526.json`) and verified byte-for-byte against live device before any write.
- **Step 2 — Live Mutation & Read-Back Confirmation**:
  - Called `WootingKeyboard.UpdateProfile`: Actuation `3.00 → 1.20 mm`, Rapid Trigger `0.15 → 0.25 mm`, RGB brightness `70% → 45%`.
  - Returned `Ok: true`; fresh `WootingKeyboard.Read` confirmed `ActuationMm: 1.2`, `RapidTriggerMm: 0.25`, `RgbBrightness: 45`.
- **Step 3 — Restore & Byte-for-Byte Verification**:
  - Called `WootingKeyboard.RestoreBackup` with the saved backup → returned `Ok: true`.
  - Fresh raw HID read-back matched the original backup **byte-for-byte**:
    - Base `baseMatch=True`, SHA-256 `b6122a5b786c23381a7c2c95bf1b6432e5e6262a6b896217c1622bfaf6c95e54`
    - RGB Core `rgbMatch=True`, SHA-256 `7e450ecf6d5b7909185fa31a58377d80942a660f7a65bf552db9a3022f3cb63c`
    - Final decoded state restored: Actuation `3.0 mm`, Rapid Trigger `0.15 mm`, RGB Brightness `70%`, Tachyon `true`.

---

## 4. Verification Gates

| Gate | Exit Code | Result |
|---|---|---|
| `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` | `0` | 0 Warnings, 0 Errors |
| `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` | `0` | 7,976 total / 7,948 passed / 0 failed / 28 skipped (standard Windows `TEMP` used to avoid Orca 66-char temp path MAX_PATH overflow in unrelated `LeftoverScanTests`) |
| `cd ui && npm run check` | `0` | `tsc`, `contracts`, ESLint, `lint:css`, `css:prune:check`, 320/320 UI tests passed (7 skipped), `check-comments` |
| `node tools/check-comments.mjs` | `0` | 2,389 legacy files, 176 zero-comment files |
| `cd ui && npm run dead` | `0` | 0 dead files/exports (`knip`) |

---

## 5. `native-ui/` Follow-Up List (Untouched per Hard Stop)

The following `native-ui/` locations must be updated when the native Devices room is rebuilt so it matches the new host contract and error/layout behavior:

1. **`apps/launcher/native-ui/crates/exo-rpc/src/pc_types.rs`**
   - `:544` `PeripheralCard.wooting: bool` — host emits `lab: bool`; rename/alias (`#[serde(default, alias = "wooting")] pub lab: bool`).
   - `:602-633` `KeyboardReadReply` — add `form_factor`, `has_rapid_trigger`, `has_rgb`, `min_actuation_mm`, `max_actuation_mm`, `layout_code`, `rgb_brightness`, `background_service_running`; change `rapid_trigger` and `tachyon` from `bool` to `Option<bool>` for models lacking them (e.g. Wooting One/Two).
   - `:637-646` `KeyboardBackupSummary` — add `product_id` and `firmware`.

2. **`apps/launcher/native-ui/crates/exo-ui/src/pc_actions.rs`**
   - `:566-578` `load_pc_devices` — currently passes `{}` without keyboard `id` and discards `!kb.ok` error messages.
   - `:633-651` `switch_keyboard_profile` — optimistically mutates `active_profile` and swallows `!kb.ok` failures without reverting or surfacing `kb.message`.
   - `:653-701` `update_keyboard_settings` — optimistically mutates state, swallows `!kb.ok` errors, and omits `rgbBrightness` parameter.
   - `:703-736` `backup_keyboard_profiles` / `restore_keyboard_backup` — drop `kb.message` on failure.

3. **`apps/launcher/native-ui/crates/exo-ui/src/pc.rs`**
   - `:942-1003` `wooting_key_matrix` — hardcodes a 60% ANSI single `6.25u SPACE` (`:961`), ignoring `form_factor` (`60`/`80`/`tkl`/`full`/`keypad3`) and `layout_code` (`ansi-split`/`iso-split`/`iso`/`jis`), and keys have no `.hit()` / `.on_press()` interactivity.
   - `:1005-1206` `wooting_card` — hardcodes `0.1..4.0` actuation clamp (`:1104`, `:1109`) ignoring optical `1.5..3.6` bounds, omits Rapid Trigger mm control, omits RGB brightness control, does not gate RT/RGB on `has_rapid_trigger`/`has_rgb`, does not disable controls when `!can_write`, and omits an inline write-error banner.
   - `:1225-1229` `devices_tab` — hides the Wooting card completely when `!kb.ok` instead of rendering the error + retry state.

---

## 6. `native-ui/` Section 5 Follow-Up: Done (branch `UhhErix/wooting-native`)

Section 5 is implemented on `UhhErix/wooting-native` (base `origin/feat/native-polish-2`, carrying `acdd604` via `a131364`), not merged. Changes:

- **`exo-rpc/pc_types.rs`**: `PeripheralCard.lab` (alias `wooting`); `KeyboardReadReply` gains `form_factor`, `has_rapid_trigger`, `has_rgb`, `min/max_actuation_mm`, `layout_code`, `rgb_brightness`, `background_service_running`; `rapid_trigger` and `tachyon` are `Option<bool>`; `KeyboardBackupSummary` gains `product_id`, `firmware`; new `KeyboardBackupResult`.
- **`exo-rpc/keyboard_layouts.rs`** (new): builds the layout from `ui/contracts/keyboard-layouts.json`, the same file `keyboardLayouts.ts` now imports, so both sides read one geometry source. The builder logic mirrors the TS; no automated TS-vs-Rust diff exists.
- **`exo-ui/pc_actions.rs`**: keyboard `id` sent on read, backup, restore, profile and settings; optimistic changes revert to a snapshot on any failure; `kb.message` surfaced; `rgbBrightness` sent; actuation and RT clamped to the model's bounds.
- **`exo-ui/pc.rs`**: `wooting_key_matrix` draws the real layout per form factor and layout code (60 ANSI/ISO/JIS/split, 80 with LED bar and Mode, TKL, full with A1–A3/Mode, keypad3), keys are pressable. `wooting_card` uses model bounds, gates RT and RGB controls on capabilities, disables controls when `!can_write`, shows an inline write-error banner and a read-only banner, and has a retry card when the read fails.

**Gates (all run from this worktree):**
- `cargo fmt --all -- --check`: exit 0.
- `cargo clippy --workspace --locked -- -D warnings`: exit 0.
- `cargo test --workspace --locked`: exit 0 (exo-ui 70 passed, including `wooting_native_layouts_controls_and_error_recovery`; exo-rpc 22 passed).
- `dotnet build` (Release x64): 0 warnings, 0 errors.
- `dotnet test ExoLauncher.Tests`: Passed.
- `node tools/check-comments.mjs`: ok (2384 legacy files, 223 zero-comment files).
- `ui`: `tsc -p tsconfig.app.json`, `node --test test/keyboardLayouts.test.ts`, and `npm run contracts` all exit 0.
- **Not run:** `npm run lint` and ESLint on the changed TS. `oxlint` and `eslint` fail to start in this worktree's `ui/node_modules` (missing package), so this is an environment problem, not a code result.

**Live check on the owner's 60HE v2** (host built from this worktree, run with `--host-pipe` on a test pipe, no installed app touched, host closed after):
- Baseline read: actuation 3.0 mm, RT on at 0.15 mm, Tachyon on, RGB 70%, firmware 2.15.0-beta.0, product ID 4930.
- Backup written. Change to actuation 1.2 mm, RT 0.25 mm, RGB 45%: read back confirmed all three.
- Restore: read back returned 3.0 / 0.15 / 70 / Tachyon on.
- Byte-for-byte: a second backup after restore has base and RGB blobs identical to the pre-change backup. SHA-256 of the base body is `b6122a5b…6c95e54` and of the RGB core is `7e450ecf…cb63c`, the same as section 3.
- Native UI: a debug `exo-ui --snap --scene pc-devices` render against the live host shows the Wooting 60HE v2 card with its "Move to CPU USB" state. The keyboard detail card (key matrix and controls) was not captured headlessly because no parity scene opens it; the unit test covers it.
- Cleanup: removed the two backups this run wrote (`…083531.json` and the `-01` auto-backup). The pre-existing `wooting-1342-20261122-132614.json` is untouched.

**Not done:** the installed-app check (`tools/e2e` or screen check of the detail card on Windows 11), the lint gate above, and a visual pass of the card in the installed app. The branch is pushed; it is not merged.
