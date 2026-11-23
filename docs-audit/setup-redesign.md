# Exo Setup & Devices Redesign (v2, final)

The v2 mockups replace the v1 centered ~900 px glass cards (`docs-audit/onboarding/mockups/` and `docs-audit/devices/mockups/`, kept for comparison) with full-window Exo compositions in `docs-audit/onboarding/mockups-v2/` and `docs-audit/devices/mockups-v2/`. Every screen is rendered at **1920x1080**, **2560x1440** and **1280x720** (PNGs in each folder's `screenshots/`), using real local game covers, device cutouts and store logos. Flow, consent rules, RPCs and copy are unchanged from `docs-audit/onboarding/flow.md` and `docs-audit/devices/plan.md`; only layout changed.

## Final frame (owner decision: direction A, with B's details)

- **Setup frame:** a full-width top stage track on every setup screen. Each stage shows its name, a live sub-line (`4 stores · 215 games`, `4 devices`, `RTX 3070 · Ryzen 5600X`) and its state (Done / Current / Skipped). **No left rail anywhere in setup.**
- **Device screens** add a full-width device strip with real photos under the track (`DualSense Edge`, `Wooting 60HE v2`, `Starlight X`, `ZiiGaat Lush + fifine Mic`).
- **Devices room overview and detail** (`05-overview`, `06-detail-wooting`) keep the app's normal top bar, because they are the room, not setup.
- **Library tour** (`05-interactive-tour-room`) stays inside the real Library room with a coach-mark, so it has no setup track.

## Per-screen layout

### Onboarding (`docs-audit/onboarding/mockups-v2/`)

- **`01-welcome.html`:** full-bleed wall of real covers under a dark gradient. Huge `Welcome to Exo` type with a big 2x2 stat block (`16 installed`, `4 devices`, `RTX 3070`, `~90 seconds`). A floating `Found on this PC` glass panel shows store counts, the four devices, and the read-only notice. Actions: `Start setup` / `Skip setup`. (A's small stat row was swapped for B's 2x2 block.)
- **`02-accounts-library.html`:** `Your library is already here`. Left store rail with real logos, connected counts, and connect rows for the missing stores. A big streaming cover wall (16 installed games) takes the centre. Music service and Discord are brand tiles along the bottom. Right block (from B): `Restore local backup`, `Exo account`, `Game clients`. Header has B's `Add portable folder` and `Runtimes` buttons.
- **`03-devices-handoff.html`:** four full-height panoramic bays with real device photos and pre-scan badges. Footer hands off to the per-device sequence.
- **`04-pc-defaults.html`:** top stage track (PC defaults current). Full-window PC overview (`GeForce RTX 3070`, `Ryzen 5 5600X`, `Local Disk C:`, `Network & Windows`) with the opt-in checklist as a glass panel on the right. Nothing applies until `Apply selected`. This screen had only a B version, so it now follows A's frame.
- **`05-interactive-tour-room.html`:** the real, unblocked Library room with a generously scaled Ctrl+K coach-mark. No setup track, no scrim.
- **`06-done.html`:** full-window completion summary. Cover-wall hero with `You're ready to play`, a 4-column configured-device row, and `Open Library` / `Open Devices`.

### Devices (`docs-audit/devices/mockups-v2/`)

- **`01-setup-start.html`:** setup stage track (Devices current) and device strip above four panoramic bays with real photos. Bottom dock: `Start automatic setup`, `Preview sequence`, `Skip to overview`.
- **`02-setup-dualsense-edge.html`:** large DualSense Edge hero with live stick and trigger callouts. Right panel holds automatic verification and clean defaults.
- **`03-setup-wooting-60he-v2.html`:** full-width ANSI split-spacebar matrix (~1840 px at 1920x1080) under a small real keyboard photo header. Bottom dock holds the Automatic safety backup (`Saved`), actuation (0.4 mm), Rapid Trigger (0.15 mm), Tachyon, and the Chipset USB port note. **B's right settings panel was not used.** The dock keeps the matrix full width, which is A's direction and the reason the owner picked it.
- **`04-setup-audio-eq.html`:** full-width Exo EQ v2 response curve (ZiiGaat Lush AutoEq + JM-1, live per-game overlay, `-3.9 dB` headroom, `Compare A/B`). Bottom band with three columns: Output & APO, Smart switching & loudness guard, Microphone & Exo Voice.
- **`05-overview.html`:** the Devices room overview. Four full-width hardware strips with photos and live stats, `Change` buttons, and a hot-plug banner preview. Normal app top bar.
- **`06-detail-wooting.html`:** the Devices room detail for the keyboard. Normal app top bar, back toolbar, large keyboard photo with telemetry, the key matrix, and a right settings and backup-restore panel.

## Discarded variants

Deleted from the working tree: `01a-welcome`, `01b-welcome`, `02a-accounts-library`, `02b-accounts-library`, `03b-pc-defaults` (onboarding), and `03a-setup-wooting-60he-v2`, `03b-setup-wooting-60he-v2` (devices), with their screenshots. The old variants remain in git history at `becfa07`.

Renumbered to match flow order: `03b-pc-defaults` became `04-pc-defaults`, `04-interactive-tour-room` became `05-interactive-tour-room`, and `05-done` became `06-done`.

## Verification

- Renders: 36 PNGs. Onboarding `mockups-v2/screenshots/` has 18 (6 screens x 3 sizes), and devices `mockups-v2/screenshots/` has 18 (6 screens x 3 sizes).
- Rendered with `chrome-headless-shell` (build 1248) from `%LOCALAPPDATA%\ms-playwright\`. No render processes left running.
- Viewed by eye: every screen at 1920x1080 except the tour room (`05`, unchanged from `becfa07`); Accounts and the Wooting screen at 1280x720. The 2560x1440 PNGs were rendered but not viewed. At 1280x720 the Accounts stage label truncates to `Accounts & ...`.
- Known gap: `04-pc-defaults` cards have empty space inside each card (about 130 px). This is inside cards rather than a dead band, so it is left as is.
