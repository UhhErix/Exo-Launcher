# Native unified onboarding (Rune): report

Branch: `UhhErix/native-onboarding` (from `origin/feat/native-polish-2` at `21ac9c4`). Not merged.

## What was built

New module `apps/launcher/native-ui/crates/exo-ui/src/setup.rs`, replacing `tour.rs` (deleted).

- Top stage track: six stages with sub-lines (Instant local scan, 4 stores · 18 games, Looking for devices, This PC, 3 hands-on actions, Summary). Cells show Current / Done / Skipped pills. "Skip setup" button. Track is padded right so it clears the window caption buttons.
- Welcome: mosaic backdrop, copy bottom-left, found panel bottom-right.
- Accounts & stores: store rows from the host's library data (Found · N games / Not found on this PC), Exo account card (`exoAccount.state`), Music and Discord cards that open their rooms.
- Devices: bays from `devices.list` and `devices.image`. Each bay opens Devices through `setup_open_device`. Per-device bodies are not built.
- PC defaults: 2x2 live spec cards (GPU, CPU, drives, network) from `system.about`, `system.drives` and `system.tuning`. Picks panel (560 wide, in flow) lists tuning items whose default is opt-out. Nothing runs until "Apply selected (n)"; apply runs `system.tuning` on/off per changed item, sequentially.
- Live tour: the three beats (Ctrl+K search, open a game, open a device) run in the real Library and Devices rooms with coach marks. Beat progress is detected from real state.
- Done: four summary cards and buttons to Library, Devices, or resume skipped steps.
- Settings › Setup row ("Resume setup") and Ctrl+K "Resume setup".
- Progress persists in the `setupProgress` UI pref (`PlayerData.cs`, not synced; scope added to `PlayerChoicesSyncTests.cs`). `tourSeen` is written when the tour finishes.
- Every step is skippable (Skip step, Skip setup) and resumable from the track, the return bar, Settings or Ctrl+K.
- Consent: nothing is written, installed or signed in until the user clicks. Tuning and sign-in actions only run from explicit buttons.

Review points:
- Wooting header thumbnail crop: the source PNG (1200x800) had the keyboard in the middle third, so the pill showed a small keyboard in a lot of black. Added `docs-audit/assets/devices/wooting-60he-v2-thumb.png` (keyboard bounds + 10 px pad, 1097x392) and pointed `03-setup-wooting-60he-v2.html` at it. Native does not crop: it draws the host image with `SurfaceFit::Fit`. If the native Devices header needs the tight crop, the host's device image has to change, not the UI.
- PC defaults empty middles: `04-pc-defaults.html` `.hw-grid` changed from full-height rows (`1.15fr 1fr`) to content-height rows (`auto auto`, `align-content: start`). Native cards are content-height (about 140 px); the picks panel fills the right column.

## Gates

- `cargo fmt --all -- --check`: pass
- `cargo clippy --workspace --locked -- -D warnings`: pass
- `cargo test --workspace --locked`: pass (4 + 17 (1 ignored) + 69)
- `cargo build --release --locked --bin exo-ui`: pass
- `node tools/check-comments.mjs` (from `apps/launcher`): pass ("Comment ratchet ok")
- Host: `dotnet build ExoLauncher/ExoLauncher.csproj -c Release -p:Platform=x64`: 0 warnings, 0 errors. `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64`: pass (8118 succeeded, 28 skipped, exit 0).

## Visual check (1920x1080, parity harness)

Scenes: setup-welcome, setup-accounts, setup-devices, setup-pc, setup-tour, setup-done, setup-coach (`tools/parity/parity-snap.ps1`, `-NoBuild`, temp data dir). Checked: track, Accounts & stores, PC defaults (the picks panel no longer overlaps cards), coach overlay in the real Library room, live tour, Done.

## Known gaps

- Coach marks are fixed-position cards, not anchored to their targets.
- Restore backup and Game clients rows are not built (need `backup.status` / `clients.status`).
- "Open account" leaves the flow for Settings instead of signing in inline.
- Live tour stage and Done layout differ from `05`/`06` mockups in spacing and card count.
- Welcome mosaic is not tilted.
- Devices bays were not verified visually against mockup `01`/`05` after the last changes.
- Per-device bodies (dual-sense edge, audio EQ) are not built; the Devices stage hands off to the Devices room.

## Parity limitations

- The parity snapshot captures one frame. Host data arrives asynchronously, so several panels read "Checking" in snapshots (`Checking graphics`, `-0 GB free of -0 GB`). Real values were confirmed only in the running app's data path, not in the snapshot.
- `parity-snap.ps1` compares only against its fixed `refByScene` map, which has no setup entries. Comparison against the mockup PNGs is manual.
- The mockup PNGs in `docs-audit/*/mockups-v2/screenshots` were not regenerated after the two HTML fixes; regenerate them before comparing again.

## Processes

- No Exo process was started and left running by this task's runs. One `ExoLauncher.exe` (PID 10084) is running from another session's scratchpad (`...\c001cd68...\scratchpad\soak\before-debug\`). It was not started here and was not closed.
- `%LOCALAPPDATA%\ExoLauncher` was not written to.
