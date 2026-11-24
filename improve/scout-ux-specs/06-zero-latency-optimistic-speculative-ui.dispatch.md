EXO LAB: Zero-Latency Optimistic UI, Pointer-Trajectory Pre-Warming & GPU Rolling Odometer Stats (task-name: ux-zero-latency-optimistic)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... UI, UX, micro interaction ... perceived latency, input-to-photon in the UI, frame pacing of the UI itself ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."

## Today

- **0 optimistic updates across both UIs:** `batch_search` confirms `0` uses of `useOptimistic` in `ui/src/components/`; every setting toggle, wishlist click, Discord reaction/message, audio mute, and game property awaits an IPC round-trip (`await host.*` in 3.x; `rpc.call_raw` -> event round-trip in `native-ui`) before painting the new state.
- **Reactive-only data fetching:** Opening a GamePage or StoreGamePage waits until `on_press` to call `game.extras`, `game.properties`, or `store.game` and decode high-res hero textures.
- **Static snapping telemetry:** Download speed, playtime, GPU temp, storage GB, and prices redraw as abruptly snapping text instead of smooth rolling figures, even though Rune's pinned checkout (`rev=1735bc2`) ALREADY ships `rune_kit::containers::rolling_text::RollingText` and GPU-retained springs (`Renderer::with_springs`, `write_spring_node`)!

## 1. Benchmark first, with real numbers

- **Instrument input-to-photon & open-to-art-ready latency:**
  - `toggle_switch -> pixel_change` (Settings, Game Properties, Discord Mute, Store Wishlist).
  - `library_tile_press -> game_page_hero_art_and_extras_painted` (p50 / p95 ms).
  - `store_tile_press -> store_game_page_ready` (p50 / p95 ms).
  - Animated frame CPU time during live telemetry updates (download MB/s + progress bar).
- **Targets:**
  - Local optimistic mutation input-to-photon: `<= 8.3 ms` (`1` frame @ 120 Hz) in `exo-ui`, `<= 16 ms` in `ui/`.
  - Perceived GamePage / StoreGamePage open-to-ready latency: `>= 60%` reduction via pointer-trajectory pre-warm.
  - Animated telemetry frame CPU time: `<= 0.3 ms` p95 in Release `exo-ui`.

## 2. Build the Zero-Latency Engine

- **Optimistic Mutation Ledger (`exo-ui/src/state.rs` + `ui/src/lib/optimistic.ts`):**
  - Wrap idempotent mutations (Settings booleans, Game Properties switches, Store wishlist toggle, Discord reactions & message send, Audio mute/volume, Favorite/Pin) in a lightweight `(mutation_id, previous_snapshot, applied_at)` ledger.
  - Apply UI state on **frame 0** immediately on pointer-down / Enter; if the host returns `{ ok: false }` or times out, cleanly roll back to `previous_snapshot` with a subtle inline error shake + plain-English `ErrorNote` / `NoticeTone::Bad`.
- **80 ms Pointer-Trajectory Cone Pre-Warming:**
  - Maintain a 6-sample `(x, y, t)` pointer ring buffer. When the pointer is **decelerating** (approaching a target, not sweeping across the window), project an 80 ms forward 20° cone against visible room tabs, game tiles, store tiles, and popover triggers.
  - Pre-warm at most `2` concurrent targets (local disk cover decode + idempotent `game.extras` / `store.game` / room state warm, strictly cached with TTL so repeated crossings cost zero IPC).
- **GPU Rolling Odometers & Spatial Skeletons:**
  - Wire `rune_kit::RollingText` (with `SPRING_QUICK` / tabular numerals `'tnum' 1`, respecting `reduce_motion`) onto live numeric readouts: download speed/ETA, free storage GB, active playtime, achievements count, GPU/CPU temps, and Store prices.
  - Replace raw text loading fallbacks in `exo-ui` (`Store`, `Discord`, `PC`, `Music`) with spatial AMOLED skeleton placeholders matching exact final card geometry (zero CLS / layout jump on data arrival).

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (unit tests for optimistic rollback on host refusal, pointer-cone deceleration filter + rate limit, `motion_gate` zero flicker/jerk), `cargo build --release --locked --bin exo-ui`.
- `ui/`: `npm run check`, `npm run dead`, `npm run build`.
- Record before/after p50/p95 latency tables in the report (never fake a measurement).

## 4. Deliver

- Branch `lab/ux-zero-latency-optimistic` from `origin/main`.
- `improve/ux-zero-latency-optimistic-report.md`: before/after latency benchmarks (toggle, GamePage open, Store open, telemetry frame CPU), rollback test results, and cache-hit stats.
- Working implementation across `native-ui` + `ui/` (no prose comments).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-zero-latency-optimistic results" --body "<toggle ms, GamePage p50 before/after ms, telemetry CPU ms, commit sha>"`. Effort: medium.
