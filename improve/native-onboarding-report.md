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

## Follow-up (coordinator items 1-7)

Snaps: 720p (1280x720), 1080p (1920x1080), 1440p (2560x1440), all 7 setup scenes, `tools/parity/parity-snap.ps1` with `-DataDir` a temp copy of the data dir (device-images and covers copied in). Files are in the scratch folder `...\scratchpad\snaps\h{720,1080,1440}-setup-*.png`, not committed.

| # | Item | Status |
|---|------|--------|
| 1 | Coach marks anchored to real element bounds | Partly. Coach is anchored to `search-capsule` / `tile-<id>` / `nav-devices` through `coach_anchor`. In the 1080p snap the card sits top-left under the Bar's search capsule, not right-aligned as the mockup shows (the native Bar puts search on the left, the mockup on the right). Whether the anchor resolved or fell back to the top-left position is not verified in code. Open. |
| 2 | Restore backup and Game clients rows in Accounts | Done in code (`backup.status`, `clients.status` via `load_backup_and_clients`). Values are in the settle path; the Accounts snap shows the rows. |
| 3 | Tour (on-05) and Done (on-06) layouts | Tour: now the Library room (installed-cover grid, "Last played" hero, real counts) with the coach card pinned top-right at the mockup position, step 1 of 3. The footer (Back / Skip live tour / Start tour) stays; the mockup has none. Two grid cells are empty where a game has no cover. Done: "Setup complete" + "You're ready to play" when all five stages are Done; paragraph built from real counts. Device cards have no spec sub-line and no status pills (the host gives no data for them, nothing is invented). Open. |
| 4 | Welcome mosaic tilted | Done (`rotate(-0.14)`). |
| 5 | Open account keeps the user in setup | Done (return bar, `setup_return`). |
| 6 | Devices bays vs on-03 | Checked on the 1080p snap. Dongle first, four bays (host lists five; the fifth is not shown). Bay headers and tags follow the host's `kind`/`tag`. Mockup has spec lines and pills that the host does not provide. Open. |
| 7 | Parity snaps show real host values | Done. `settle_setup_host` runs before the setup scenes (PC cards, drives, GPU, Accounts, backup, clients, devices, device images, covers). `--frames 240 --fps 60` lets the background image decode finish. No "Checking" in the 1080p/1440p snaps except Version when the host returns no version. |

Also changed:
- Accounts: "1 game on this PC" (singular). Code only, not seen in a snap.
- Track cells: the pill sits under the stage title; sub-lines truncate. Visible in all snaps.
- Setup-done parity scene marks stages 1-5 Done (`setup_go_complete`), which is the state on-06 shows. `picks` is not set by the fixture, so the snap reads "0 PC defaults applied"; mockup shows 5. Data-driven, not fixed.

## Gates (final, after the follow-up commit)

- `cargo fmt --all -- --check`: pass
- `cargo clippy --workspace --locked -- -D warnings`: pass
- `cargo test --workspace --locked`: pass (4 + 17 (1 ignored) + 69)
- `cargo build --release --locked --bin exo-ui`: pass
- `node tools/check-comments.mjs` (from `apps/launcher`): pass ("Comment ratchet ok")
- No C# changed; dotnet gates not re-run for this follow-up.

## Visual check (1920x1080, parity harness)

Scenes: setup-welcome, setup-accounts, setup-devices, setup-pc, setup-tour, setup-done, setup-coach (`tools/parity/parity-snap.ps1`, `-NoBuild`, temp data dir). Checked: track, Accounts & stores, PC defaults (the picks panel no longer overlaps cards), coach overlay in the real Library room, live tour, Done.

## Known gaps (open after the follow-up)

- Coach mark placement: card is top-left under the Bar's search capsule; mockup is top-right. Anchor resolution unverified.
- Tour footer buttons are not in the mockup (kept for Back / Start).
- Devices: host has five devices, four bays shown; no spec lines or status pills (no host data).
- Done: device cards have no spec sub-line or status pills; PC defaults count reads from picks (fixture has none).
- "Open account" still opens the Settings-side flow rather than inline sign-in (item 5 is the return path, not inline sign-in).
- Per-device bodies (dual-sense edge, audio EQ) are not built (out of scope).

## Parity limitations

- `parity-snap.ps1` compares only against its fixed `refByScene` map, which has no setup entries. Comparison against the mockup PNGs is manual (done for the snaps above, by eye).
- Mockup references: `docs-audit/onboarding/mockups-v2/screenshots/0N-*-1920x1080.png` on `UhhErix/setup-redesign`.

## Processes

- No Exo process was started and left running by this task's runs. One `ExoLauncher.exe` (PID 10084) is running from another session's scratchpad (`...\c001cd68...\scratchpad\soak\before-debug\`). It was not started here and was not closed.
- `%LOCALAPPDATA%\ExoLauncher` was not written to.
