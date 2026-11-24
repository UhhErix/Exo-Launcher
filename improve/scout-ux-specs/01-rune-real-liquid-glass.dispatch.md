EXO LAB: True Ray-Refracted Smooth-Union Liquid Glass & Lensing Presence in Rune (`rune-light`) (task-name: ux-rune-real-glass)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read `apps/launcher/AGENTS.md`, `apps/launcher/.agents/skills/exo-design/SKILL.md`, and `apps/launcher/.agents/skills/native-ui/SKILL.md` first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... UI, UX, micro interaction ... to see if we can make something better, more experimental, not seen before, and honestly not even thought of."
> Owner (2026-11-14): "still want Liquid Glass menus and buttons and stuff" — Void black `#000` page, clear vivid glass on controls and floating layers, no gray metal wash (`ui/src/tokens.css:185`).

## Today

- `apps/launcher/native-ui/crates/exo-ui/src/shell.rs:44-89` manually rations blurs with `BlurBudget::compute` because Rune's 2D `backdrop_blur` hard-caps `MAX_BACKDROP_BLURS = 4` (`crates/rune/src/render/backdrop_blur.rs:12`).
- Whenever a popover, modal, or toast+mini-player opens, `BlurBudget` kills top-bar blur simultaneously, visibly snapping capsule fill from `0.52` to `0.78` alpha.
- `exo-kit/src/components.rs:116-182` (`glass_button`, `glass_icon_circle`) omits `.backdrop_blur(...)` entirely; `tokens.rs:89-99` (`glass_specular_sheen`, `glass_rim_top = 0.28`) drifts from `tokens.css:177-186` (`0.17`, `--glassy-sheen: none`).
- Meanwhile, the pinned Rune fork (`rev=1735bc2`) ALREADY contains `crates/rune-light/src/glass.rs` (`Glass`, `Glass::rounded`, `.thickness()`, `.bevel()`, `.dome()`, `.ior()`, `.dispersion()`, `.frost()`, `.presence(p)`, `.union(rect, radius, k, morph)`, `GlassLight::from_emitter`, `GlassRenderer::render`) — completely unused by `exo-ui`!

## 1. Benchmark & prototype first, with real numbers

- **Target metrics:**
  - Single-pass `GlassRenderer` GPU cost: `<= 0.5 ms` at `1920x1080` and `<= 0.9 ms` at `3840x2160` (RTX 3070, DX12 timestamp queries).
  - Simultaneous glass elements without drop/flicker: `>= 12` (eliminating `MAX_BACKDROP_BLURS = 4` rationing).
  - Legibility contrast measured from rendered pixels over pure-white (`#fff`), vibrant hero art, and pure-black (`#000`): every word `>= 4.5:1` (`< 3.0:1` fails).
  - Motion verification via Rune `motion_gate`: `0` findings (`flicker`, `overshoot`, `jerk`, `never-settles`, `wakes-after-end`).
- **Compare:** current `backdrop_blur` + `BlurBudget` vs `rune-light::GlassRenderer` vs `rune-fx::LensParams`.

## 2. Build the refractive glass layer in `exo-kit` + `exo-ui`

- **Single-pass floating glass:** Route all Bar capsules, Exo Island, popovers, Ctrl+K, toasts, Music transport bar, and controls-over-art (`glass_button`, `glass_icon_circle`) through `rune-light::GlassRenderer` via Rune's `Renderer::draw_with_backdrop` effect hook. Remove `BlurBudget` blur-snapping completely.
- **Lensing presence (`Glass::presence(p)`):** Popovers, toasts, and Exo Island enter by lensing smoothly (`p: 0.0 -> 1.0` on `SPRING_BASE`, scaling thickness, dome, bevel, and shadow together) rather than flat opacity fading. Under `reduce_motion`, fall back to clean opacity fade only.
- **Smooth-union metaball morphing (`Glass::union`):**
  - When a Discord call or download starts, Exo Island physically buds out of the Rooms capsule via `Glass::union(rect, r, k, morph)` before separating into its slot.
  - When a menu/popover opens from a glass trigger button (Capture, More, Sort, Search -> Palette), the glass rim stretches seamlessly out of the trigger button's geometry.
- **Restrained Void calibration:** Match `tokens.css` discipline — `ior = 1.48`, subtle `dispersion <= 0.12`, zero fake white wash over Void `#000`, `glass_rim_top = 0.17`, and a local backdrop luminance contrast guard so text stays `>= 4.5:1` over white art without graying black backgrounds.
- **Hardware safety tier:** Auto-bypass to fast `backdrop_blur` / still glass on WARP/software adapters, High Contrast mode, or if frame GPU time exceeds `2.0 ms`.

## 3. Verify & Gate

- Run from `apps/launcher/native-ui/`: `cargo fmt --all`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked` (including `motion_gate` tests and new contrast + GPU budget tests), `cargo build --release --locked --bin exo-ui`.
- Capture before/after 1920×1080 and 3840×2160 headless `--snap` PNGs over dark Void, bright hero art, and open Capture/Palette popovers, verifying zero blur-budget snap and `>= 4.5:1` contrast.
- Never deploy over `%LOCALAPPDATA%\ExoLauncher`.

## 4. Deliver

- Branch `lab/ux-rune-real-glass` from `origin/main`.
- `improve/ux-rune-real-glass-report.md`: GPU frame-time table (1080p/1440p/4K), contrast measurements over white/art/black, before/after PNG comparisons, and fallback behavior.
- Working integration in `exo-kit` + `exo-ui` (no prose comments, all tokens in `exo-kit/src/tokens.rs`).
- Commit messages end with `"Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "ux-rune-real-glass results" --body "<GPU ms 1080p/4K, max simultaneous glass slabs, contrast min ratio, commit sha>"`. Effort: high.
