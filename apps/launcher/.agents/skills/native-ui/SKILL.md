---
name: native-ui
description: "Exo's native Rust UI on Rune (anomalyco/rune, wgpu DX12) in apps/launcher/native-ui: the exo-rpc, exo-kit and exo-ui crates. Use for any change to a .rs file, Cargo.toml or Cargo.lock there, a native room (Library, Store, game page, Discord, Music, PC, Settings, Bar, palette, tour), its tokens, components or icons, or the host-v1 named-pipe client. Not for the React ui/, mouse/ or the .NET host."
---

# native-ui

The native UI replaces only the React/WebView2 UI; the .NET host stays and talks to it over the `host-v1.json` named-pipe contract (`docs/decisions/2026-11-20-native-ui-rune.md`, `docs/architecture/host-transport.md`). Run every cargo command from `apps/launcher/native-ui/`.

## Layout

- `crates/exo-rpc`: the pipe client (`pipe.rs`, `lib.rs`) and the DTOs the host sends (`types.rs`, `pc_types.rs`, `media_types.rs`, `discord_types.rs`, `remote_art.rs`). No Rune dependency. Its tests load `ui/contracts/host-v1.json` with `include_str!`; that file is the source of truth for every method and field.
- `crates/exo-kit`: the shared look. `tokens.rs` (colours, type, springs, the Goeist font from `ExoLauncher/Assets/Fonts`), `components.rs`, `icons.rs`, `qr.rs`.
- `crates/exo-ui`: the `exo-ui` binary. `main.rs` (app and routing), `state.rs` plus one `*_state.rs` per room for state, the room views (`library.rs`, `store.rs`, `game_page.rs`, `discord*.rs`, `music.rs`, `pc.rs`, `pc_actions.rs`, `settings.rs`, `shell.rs`, `palette.rs`, `tour.rs`, `key_modal.rs`), `captures.rs`, `video_decoder.rs` (Media Foundation), `gamepad.rs`.

## Rune API

Exo pins the `exo` branch of our Rune fork (`https://github.com/UhhErix/rune`, upstream `anomalyco/rune` plus Exo's features) to one git rev in `Cargo.toml`; `[workspace.dependencies]` and every `[patch.crates-io]` entry use the same rev. Never guess its API. Read the source cargo already fetched in the fork's checkout folder under `%USERPROFILE%\.cargo\git\checkouts\rune-*\<first 7 characters of the rev>\` (pick the `rune-*` folder for the fork's URL, and read its `crates/`, `examples/`, and `AGENTS.md`). The `.github/workflows/native-ui-rune-bump.yml` workflow bumps the pin automatically when the fork's `exo` branch moves; otherwise bump the rev only when asked, and always in every entry at once.

## Rules

- The look is the web UI's: DESIGN.md is the brief and `exo-design` is the checklist. Every colour, size and spring comes from `exo-kit/src/tokens.rs`; a new value is a new token there, never a literal in `exo-ui`. Void black page, Goeist, sentence case, no emoji in chrome.
- The host keeps secrets, filesystem paths and store work. `exo-ui` shows bounded DTOs from `exo-rpc`. A new pipe method or field needs the host side and `host-v1.json` in the same change (`npm run contracts:write` from `ui/` after an intentional change).
- No prose comments (AGENTS.md's comment ban; native-ui has none today).
- AGENTS.md's hard stops and enforced rules apply here too. Smallest complete change; delete what it replaces.

## Gates

From `apps/launcher/native-ui/`:

1. `cargo fmt --all`
2. `cargo clippy --workspace --locked`: add no new warnings.
3. `cargo test --workspace --locked` (what CI, `.github/workflows/native-ui-ci.yml`, and `tools/Build-Release.ps1` run).
4. `cargo build --release --locked --bin exo-ui`

A clean build is not done. Done is the room on screen: with the host's publish output in place, `tools\swap-dev.ps1` puts `native-ui\target\release\exo-ui.exe` into the installed app (`%LOCALAPPDATA%\ExoLauncher\app`); then look at the room you changed and read `%LOCALAPPDATA%\ExoLauncher\logs\app.log`. Where that cannot run, say so and list what still needs a look.
