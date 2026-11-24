EXO LAB: Zero-Keystroke Predictive Action Bar & Morphing Omni-Palette (task-name: ux-predictive-palette)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... menu, UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."

## Today

- `apps/launcher/native-ui/crates/exo-ui/src/palette.rs`:
  - Only supports 5 `PaletteTarget` variants (`Room`, `OpenGame`, `RefreshLibrary`, `ActivateKey`, `StartTour`); cannot launch games directly, search settings, jump to Discord DMs/channels, control Music, trigger PC/AutoTune actions, or switch device profiles.
  - Every library game renders the exact same generic `icon_gamepad` instead of its cover thumbnail.
  - Bug at `palette.rs:58-71`: `Key::Down` / `Key::Up` mutates `s.palette_index` without calling `tx.scroll_into_view(...)`, pushing selection off-screen past row ~10.
  - Literal `TextStyle::new(scaled(12.0), 500.0)` at `palette.rs:145`.
- `apps/launcher/ui/src/components/blocks/command-menu-1.tsx` + `ui/src/components/shell/launcherCommands.ts`:
  - Broader index (`SETTINGS_INDEX`, Discord, rooms, games), but strictly reactive substring search — zero frecency ranking, zero fuzzy subsequence matching, zero sub-actions (`Tab`), and zero zero-keystroke contextual predictions.

## 1. Benchmark & offline accuracy test first

- **Build an offline replay benchmark** of local session transitions (synthetic + anonymized local launch sequences: weekday evening squad play, weekend solo RPG, post-download install, friend-in-game join, controller-connect play).
- **Metrics:**
  - Zero-keystroke Top-1 and Top-3 hit rate (target: `Top-1 >= 45%`, `Top-3 >= 75%` on returning sessions).
  - Fuzzy search latency over `5,000` indexed items (games + settings + Discord DMs/channels + PC actions + devices): `<= 1.5 ms` p99 in Rust (`<= 4 ms` in TS).
  - Keystrokes to play/act from anywhere in Exo: `1` (`Ctrl+K` -> `Enter`).

## 2. Build the Predictive Engine + Morphing Omni-Palette

- **Local Zero-Cloud Context Predictor (strictly on-device, zero PII sent, persisted via `PlayerData.cs`):**
  - Combines 5 live signals Exo already holds in `AppState` / `LauncherApp`:
    1. Friends currently playing an installed game or sitting in a voice channel (`"Join Ada & Kai · Counter-Strike 2 + Voice"`).
    2. First-order Markov + time-of-day/day-of-week frecency decay (`exp(-age / 7d)`).
    3. Freshly finished install/update or pending shader precompile (`"Play Helldivers 2 · Updated 4 min ago"`).
    4. Controller just connected (`"Play Elden Ring · Controller ready"`).
    5. Active music/capture context (`"Save last 2 min replay"`, `"Mute Discord mic"`).
  - Surfaces at most 2 high-confidence (`score >= 0.65`) rows under `"Suggested"` when Ctrl+K opens empty — never nags or hijacks focus.
- **Raycast-grade Verb & Sub-Action Model:**
  - `Enter` runs the primary verb (`Play` for installed games, `Toggle` for boolean settings inline right inside the palette, `Join` for voice, `Jump` for rooms/DMs).
  - `Shift+Enter` opens details (GamePage / Settings section).
  - `Tab` (or `->`) pivots inline into a **secondary action drawer** for the selected row (e.g., for a game: `Open page`, `Optimize`, `Properties`, `Open install folder`, `Launch without anti-cheat`, `Switch Riot/Steam account`).
- **Visual & Motion Polish (`v4` + `3.x`):**
  - Morph the top-left Search capsule into the Command Palette via `.matched("search-palette-morph")` (and `Glass::union` when Spec 01 lands).
  - Real cover art thumbnails (`28×28` rounded) for games, real friend avatars for Discord DMs, live toggle switches for inline settings, crisp `<kbd>` shortcut badges (`Ctrl+1..7`, `Enter`, `Tab`, `Esc`), and matched-character highlight.
  - Fix `palette.rs:58-71` (`tx.scroll_into_view`), `palette.rs:145` (token), and wire `.focus_return("search-capsule")`.

## 3. Verify & Gate

- `native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (unit tests for predictor accuracy, fuzzy scoring, `scroll_into_view`, `motion_gate`), `cargo build --release --locked --bin exo-ui`.
- `ui/`: `npm run check`, `npm run dead`, `npm run build`.
- `ExoLauncher.Tests`: `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (if `PlayerData.cs` registers local frecency store).
- Capture before/after `--snap` of `palette` scene showing thumbnails, Suggested group, inline switches, and Tab sub-actions.

## 4. Deliver

- Branch `lab/ux-predictive-palette` from `origin/main`.
- `improve/ux-predictive-palette-report.md`: offline Top-1/Top-3 replay accuracy table, 5k-item search latency p50/p99, before/after palette snaps.
- Working implementation across `native-ui` (and `ui/` command menu parity).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-predictive-palette results" --body "<Top-1/Top-3 accuracy, 5k query p99 ms, gate results, commit sha>"`. Effort: high.
