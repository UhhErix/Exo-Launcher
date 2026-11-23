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

## Follow-up: snap review (coordinator msg_06cba6efaa94)

Snaps: 720p (1280x720), 1080p (1920x1080), 1440p (2560x1440), all 7 setup scenes, `tools/parity/parity-snap.ps1 -NoBuild` with `-DataDir` a temp copy of the data dir. Files are in the scratch folder `...\scratchpad\snaps\h{720,1080,1440}-setup-*.png`, not committed. The 1080p welcome, devices, tour and done snaps and the 720p welcome and 1440p devices snaps were viewed after the final build.

| # | Item | Status |
|---|------|--------|
| 1 | Welcome mosaic over the track / title | Done. The body wrapper clips to its rectangle (`clip_rounded(0.0)` in `view()`); the track is clear in 720p and 1080p. |
| 2 | Welcome right half empty tiles | Done. `cover_pool()` resolves images first and cycles them, so the mosaic fills every tile even when a remote cover is not decoded yet. |
| 3 | "Found on this PC" footer clipped | Done. The footer text wraps inside the panel (`grow` + `min_w(0)`). |
| 4 | Devices stage-3 track "Current" bar too wide | Done. Track pills sit in an outer row, so they hug their width. |
| 5 | Devices Headphones tiny placeholder | Done. Device glyphs come from `device_glyph()` (keyboard, controller, audio, mouse) at bay size. |
| 6 | Devices bays mostly empty | Done. Bays show the kind, a connection pill, the name, maker · model, and spec lines (Connection, Battery, Polling rate, Layout, Color) only where the host has a value. Bay titles align across bays (`fact_rows`). |
| 7 | Done device pills as on-06 | Done. Device cards show a connection pill and a maker · model sub-line (kind when there is no maker). |
| — | Tour "Last played" pill stretched full width | Done in this pass. The pill sits in an outer row. |

Earlier follow-up (coordinator items 1-7, commit `15d4a18`):

| # | Item | Status |
|---|------|--------|
| 1 | Coach marks anchored to real element bounds | Coach anchors to `search-capsule` / `tile-<id>` / `nav-devices` through `coach_anchor`. Coordinator confirmed the top-left placement under the search capsule is correct for the native Bar. |
| 2 | Restore backup and Game clients rows in Accounts | Done (`backup.status`, `clients.status` via `load_backup_and_clients`). |
| 3 | Tour (on-05) and Done (on-06) layouts | Tour: Library room with installed-cover grid, "Last played" hero, real counts, coach card pinned top-right, step 1 of 3. Footer (Back / Skip live tour / Start tour) kept; the mockup has none. Done: "Setup complete" + "You're ready to play" when all five stages are Done. |
| 4 | Welcome mosaic tilted | Done (`rotate(-0.14)`). |
| 5 | Open account keeps the user in setup | Done (return bar, `setup_return`). |
| 6 | Devices bays vs on-03 | Superseded by the snap-review item 6 above. |
| 7 | Parity snaps show real host values | Done. `settle_setup_host` runs before the setup scenes; `--frames 240 --fps 60` lets background image decode finish. |

Also changed:
- Accounts: "1 game on this PC" (singular).
- Track cells: the pill sits under the stage title; sub-lines truncate.
- Setup-done parity scene marks stages 1-5 Done (`setup_go_complete`). `picks` is not set by the fixture, so the snap reads "0 PC defaults applied"; the mockup shows 5. Data-driven, not fixed.

## Follow-up 2: last snap-review items

- Welcome and Done mosaics: blank right-half tiles. Cause: the window draws nothing at rest, and covers decode after the first frames, so the snap captured frames before they landed. The pool was full (24 mosaic, 16 tour) the whole time. Fix: setup snaps now run 900 frames instead of 240 (`tools/parity/parity-snap.ps1`). Covers are also ordered installed-first and cycled over every tile, so a short library never leaves a blank tile. At 900 frames the welcome mosaic fills every tile.
- Tour: the blank cell at 1080p was the same late-decode case; it is filled at 900 frames. Tour covers now cycle too.
- Devices: all five host devices are shown (five columns, same bay layout). Done shows all five device cards as well, to match "5 peripherals set up".
- Known: the fifth device is an audio input (fifine Microphone) and uses the headphones glyph; there is no microphone glyph in exo_kit.

Snaps (900 frames): `...\scratchpad\snaps\h{720,1080,1440}-setup-{welcome,devices,tour,done}.png`. Checked: welcome, devices, tour, done at 1080p; devices and welcome at 720p; devices and done at 1440p (mosaic full, five cards).

## Gates (final, after the snap-review commit)

- `cargo fmt --all -- --check`: pass
- `cargo clippy --workspace --locked -- -D warnings`: pass
- `cargo test --workspace --locked`: pass (4 + 17 (1 ignored) + 69)
- `cargo build --release --locked --bin exo-ui`: pass
- `node tools/check-comments.mjs` (from `apps/launcher`): pass ("Comment ratchet ok")
- No C# changed; dotnet gates not re-run for this change.

## Visual check (parity harness)

Scenes: setup-welcome, setup-accounts, setup-devices, setup-pc, setup-tour, setup-done, setup-coach at 720p, 1080p and 1440p. Checked by eye against the mockups: welcome, devices, tour, done (1080p); welcome (720p); devices (1440p).

## Known gaps (open)

- Tour footer buttons are not in the mockup (kept for Back / Start).
- Done: PC defaults count reads from picks (fixture has none), so it reads "0 applied".
- "Open account" still opens the Settings-side flow rather than inline sign-in (item 5 is the return path, not inline sign-in).
- Per-device bodies (dual-sense edge, audio EQ) are not built (out of scope).

## Parity limitations

- `parity-snap.ps1` compares only against its fixed `refByScene` map, which has no setup entries. Comparison against the mockup PNGs is manual.
- Mockup references: `docs-audit/onboarding/mockups-v2/screenshots/0N-*-1920x1080.png` on `UhhErix/setup-redesign`.

## Processes

- No Exo process was started and left running by this task's runs. A check after the last snap showed no `ExoLauncher` or `exo-ui` process. One `ExoLauncher.exe` (PID 10084) from another session was not started here and was not closed.
- `%LOCALAPPDATA%\ExoLauncher` was not written to.
