EXO LAB: Fluid Adaptive-Density Canvas, Responsive Grids & Zero-Dead-Space Rooms (`native-ui`) (task-name: ux-adaptive-density)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... UI, UX, micro interaction ... every little nook and cranny, to see if we can make something better, more experimental, not seen before, and honestly not even thought of."

## Today (Verified via 12 Live 1920×1080 Parity Snapshots)

- `apps/launcher/native-ui/crates/exo-ui/src/library.rs:20-22`: Hardcodes `ALL_GRID_COLS = 11`, `ALL_CARD_W = 163.63`, `ALL_CARD_H = 245.45` — overflows at `1280×720` / `1440×900` and underfills `2560×1440` / `3840×2160`!
- `apps/launcher/native-ui/crates/exo-ui/src/pc.rs:87,134`: Device showcase media hardcodes `.size(471.0, 314.0)` (overflows narrower viewports), `.hover(BG)` gives zero hover feedback, and `pc.rs:36` maps microphones (`Shure MV7 USB Microphone`) to `icon_headphones`!
- `apps/launcher/native-ui/crates/exo-ui/src/game_page.rs:168-201,332-341`: `Borderless v`, `Upscalers >`, and `English v` are dead non-interactive visuals; `Frame generation` is hardcoded `"Built into the game"`; ~45% of card height and lower 30% of 1080p viewport sit empty.
- `apps/launcher/native-ui/crates/exo-ui/src/store.rs:83,188-195`: Hero lacks price/CTA, original prices lack strikethrough, right rail leaves ~65% void, and StoreGamePage banner is cramped at `440×210`.
- `apps/launcher/native-ui/crates/exo-ui/src/discord.rs`: `Active now` cards lack quick Join/Watch/Listen-Along buttons and lower 75% of Friends Online pane sits blank.
- `apps/launcher/native-ui/crates/exo-ui/src/music.rs`: Bottom transport capsule lacks a seek scrubber; `Best New Songs` fills <50% width.

## 1. Benchmark & Multi-Resolution Matrix First

- Snap ALL 12 scenes at **4 resolutions** (`1280×720`, `1920×1080`, `2560×1440`, `3440×1440`) before and after.
- **Targets:**
  - `0` horizontal overflows or clipped labels across all 4 resolutions and `100% / 125% / 150%` text scales.
  - Unintended blank viewport area reduced to `<= 20%` on GamePage, Store, Discord, Devices, and Music (while preserving Exo's quiet Void aesthetic — no clutter cards or filler copy!).
  - Smooth modality-adaptive density switch (`Compact` / `Comfortable` / `TenFoot` auto-engaged on Gamepad input) in `<= 160 ms`.

## 2. Build the Adaptive-Density Engine & Fix Every Room

- **Viewport + Modality Adaptive Density (`exo-kit/src/tokens.rs`):**
  - Introduce `DensityMode` (`Compact`, `Comfortable`, `TenFoot`) driven by user preference + automatic `TenFoot` boost when `InputMode::Gamepad` activates.
  - Add a shared `responsive_columns(avail_width, min_card_w, max_card_w, gap) -> (usize, f32)` helper driven by Rune's `El::on_layout` (already used in `shell.rs:412`).
- **Room-by-Room High-Signal Fixes:**
  - **Library (`library.rs`):** Replace `ALL_GRID_COLS = 11` with `responsive_columns`; replace `Sort` click-cycling (`library.rs:764`) with a real anchored glass dropdown menu; show compact subtle facts (playtime / friends playing / shader ready) in the hero lockup.
  - **GamePage (`game_page.rs`):** Wire REAL `Optimize` / `Upscalers` / `Language` / `Frame generation` controls (removing all dead pills!); add a clean bottom hairline band with per-game **Recent Captures strip**, **Friends who play / playing now**, and **Per-Game Device Profile pill** (hidden cleanly when empty).
  - **Devices (`pc.rs`):** Fix `pc.rs:36` (`icon_mic` for `role == Some("input")`), fix `pc.rs:87` proportional image sizing, fix `pc.rs:134` hover lift, and display live micro-telemetry pills on each showcase card (`800 DPI · 4000 Hz`, `0.1 mm RT`, `Footsteps EQ`, `Noise suppression on`).
  - **Store (`store.rs`):** Add hero price/discount/wishlist/open CTA, struck-through original prices (`was_str`), full-width responsive deal rows, and full-bleed StoreGamePage hero.
  - **Discord (`discord.rs`):** Render full Online friends list below Active Now + 1-click `Join` / `Watch` / `Listen along` pills directly on Active Now cards; remove banned `glass_top_sheen()` (`discord.rs:57-70`).
  - **Music (`music.rs`):** Add live seek/scrubber bar to the floating bottom transport capsule; move 8 duplicated inline icons (`music.rs:85-192`) into `exo-kit/src/icons.rs`.
  - **Settings (`settings.rs`):** Wire `Text size` & `Music service` real dropdown menus (fixing dead `dropdown_pill`), fix Backup `Export`/`Import` (`settings.rs:157-163`), and rename misnamed `SettingsSection` variants.

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked`, `cargo build --release --locked --bin exo-ui`.
- Capture full `1280×720`, `1920×1080`, `2560×1440`, and `3440×1440` before/after `--snap` sets and verify zero clipping/overflow and clean contrast.

## 4. Deliver

- Branch `lab/ux-adaptive-density` from `origin/main`.
- `improve/ux-adaptive-density-report.md`: 4-resolution parity matrix, dead-control fix checklist, before/after snaps.
- Working implementation across `exo-kit` + `exo-ui` (no prose comments, all tokens in `exo-kit`).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-adaptive-density results" --body "<resolutions verified 4/4, dead controls fixed count, gate results, commit sha>"`. Effort: medium.
