EXO LAB: Zero-Injection Unified DirectComposition In-Game Glass Overlay (Kills WebView2 Achievement Stutter) (task-name: ux-unified-game-overlay)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md` and `apps/launcher/.agents/skills/exo-design/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... feature, menu, framework, UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."
> Vision: "the best performance from every part of every PC." Hard stop: NEVER inject into or touch anti-cheat processes.

## Today

- `apps/launcher/ExoLauncher/FpsOverlay.cs`: Draws an anti-cheat-safe DirectComposition Host Backdrop Liquid Glass FPS pill (`< 1 MB` RAM, Fresnel rim, Goeist tabular figures, `WDA_EXCLUDEFROMCAPTURE`, `WS_EX_LAYERED | WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT`).
- `apps/launcher/ExoLauncher/AchievementBanner.cs:151-156`: **Critical performance bug** — every time an achievement unlocks (after 20 s idle), Exo **cold-spawns a transparent WebView2 instance mid-game** (`CreateCoreWebView2CompositionControllerAsync`, ~80–150 MB RAM + CPU/GPU spike causing frame-time stutter while playing!).
- There is no anti-cheat-safe in-game visibility for Discord voice speakers, Music track whisper, Capture saved/replay toast, or a quick hold-to-peek HUD without alt-tabbing.

## 1. Benchmark first, with real numbers

- **Baseline (`AchievementBanner.cs` today vs DirectComposition prototype):**
  - Measure cold unlock latency (ms from `AchievementWatch.Unlocked` to first visible frame), peak Private Working Set delta (MB), and 1%/0.1%-low FPS impact during a borderless DX12 render workload (non-anti-cheat benchmark or synthetic DX12 test window — never launch VALORANT/League!).
  - **Targets:**
    - `0 MB` WebView2 spawn overhead (`<= 2.5 MB` total working set for the unified overlay).
    - `<= 0.25 ms/frame` GPU composition cost while visible; `0.00 ms` when idle.
    - `<= 25 ms` achievement unlock-to-first-pixel (vs ~400–900 ms WebView2 cold boot).
    - `0` banned imports (`BannedNativeImportsTests` green, zero DLL injection, zero synthetic input).

## 2. Build the Unified Zero-Injection In-Game Glass Island

- **Replace `AchievementBanner`'s WebView2 completely:** Render achievement unlock cards (icon, rarity glow/badge, title, description, sound) natively via `Windows.UI.Composition` Host Backdrop Liquid Glass + Direct2D/DirectWrite (`Goeist-Variable.ttf`), sharing the battle-tested `GlassHost` and `SHQueryUserNotificationState` FSE queue from `FpsOverlay.cs` and `AchievementBanner.cs`.
- **Unified Ambient Micro-Strip (click-through, `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`):**
  - **FPS + Optional 1%-Low Frametime Sparkline:** Ultra-compact 60-sample GPU frametime micro-graph beside `142 FPS`, using `MinWidth` anti-jitter latch.
  - **Discord Voice Speaker Micro-Pill:** Quietly displays active speaker name + speaking ring + mic-muted dot when in a call (auto-hides when silent).
  - **Transient 3 s Whispers:** Music track change (`Midnight City · M83`), Capture saved (`Replay saved · 2:00`), Auto-profile applied (`Valorant · 800 DPI · Footsteps EQ`).
- **Hold-to-Peek Interactive Glass Quick-HUD (Borderless/Windowed games, opt-in hotkey e.g. `Shift+Tab` / `Alt+E` or Controller chord):**
  - Pressing the hotkey toggles off `WS_EX_TRANSPARENT` + unfurls a sleek 120Hz Liquid Glass strip at top-center (Discord Mic Mute/Deafen/Call Volume, Save Replay/Snip, Music Play/Skip/Volume, Mouse DPI / Audio EQ quick-switch, FPS/Sparkline toggle, Live GPU temp/wattage).
  - Releasing / pressing `Esc` restores `WS_EX_TRANSPARENT` and returns foreground focus cleanly to the game in `< 16 ms` without minimizing.
  - Respects `WDA_EXCLUDEFROMCAPTURE` (toggleable so streamers can choose whether captures/streams show the HUD).

## 3. Verify & Gate

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64`
- `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (including `BannedNativeImportsTests`, `FpsOverlay` glass spec tests, new `AchievementBanner` DirectComposition rendering & FSE queue tests).
- Capture screen screenshots of the unified Liquid Glass pill, achievement card, frametime sparkline, and Quick-HUD over both bright and dark borderless test windows; verify `>= 4.5:1` contrast.
- Never deploy over `%LOCALAPPDATA%\ExoLauncher`.

## 4. Deliver

- Branch `lab/ux-unified-game-overlay` from `origin/main`.
- `improve/ux-unified-game-overlay-report.md`: before/after RAM/latency/frametime benchmarks, screenshots over light/dark backgrounds, anti-cheat safety verification.
- Clean working implementation retiring `AchievementBanner`'s WebView2 (`Assets/Overlay/banner.html` deleted if fully superseded per AGENTS.md "Delete, don't flag").
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-unified-game-overlay results" --body "<RAM saved MB, unlock latency before/after ms, 0.1% low delta, commit sha>"`. Effort: high.
