# room-shell-library: menus, popovers, dropdowns, glass and cursors (partial)

Branch: UhhErix/room-shell-library. Not pushed. Harness: `node tools/parity/run.mjs --rooms shell,captures,game,pc,devices,apps,settings,library --seed-data <temp copy of owner data> --exo-ui native-ui/target/debug/exo-ui.exe`.

## Status

Partial. Several items are fixed or verified; the glass, cursor and Filter gaps are open. Six exo-ui unit tests fail in the working tree (HEAD passes 69/69). See "Gate failures" below.

## Before / after (parity harness, 44 states)

| Run | CRITICAL | HIGH | MEDIUM | LOW | PASS | Mean pixel diff |
| --- | --- | --- | --- | --- | --- | --- |
| Before (`run-rest-a`) | 10 | 12 | 14 | 3 | 5 | 17.88 |
| After (`run-rest-b`) | 12 | 11 | 13 | 3 | 5 | 16.41 |

The after run is not a like-for-like regression. The selector fix below makes React open menus that the harness previously missed, so those states now produce real comparisons and surface gaps that were hidden. Changes per state:

- Fixed by the harness selector fix (React now opens, native matches counts): `library-all-sort-dropdown` (native 8 menu items vs React 8), `library-all-add-popover` (3 vs 3), `library-all-more-popover` (3 vs 3).
- Newly visible gaps: `library-all-card-menu`, `library-all-more-popover`, `library-all-sort-dropdown`, `library-all-add-popover` now show cursor mismatches (0/14–0/16) and glass over-count (native 9 vs React 5).
- `library-all-filter-installed`: native 0 menus vs React 28. Native Filter cycles state on tap; React opens a Filter popover with chips. Not fixed.
- `shell-activate-key-modal`: cursor 0/10 → 12/17 (mismatches 5/17).
- `shell-tour-modal`: now reachable; cursor 0/20 (every row mismatched).
- `pc-display-popover`: still fails to open natively (scene=pc). Not fixed.
- `shell-capture-popover`: unchanged; glass and cursor gaps remain.

Screenshots (scratch, not committed): before `run-rest-a/` and `run-rest-a/index.html`; after `run-rest-b/` and `run-rest-b/index.html`. Native and React PNGs are under each run's `native/` and `react/` folders.

## Changes in this commit

- `tools/parity/states.mjs`: sort trigger selector `button[aria-label^="Sort library"]` (the trigger label is "Sort library, <current>"). Installed filter now opens `button[aria-label^="Filter"]` then clicks chip `.exo-library-filter-chips button` "Installed".
- Rune fork rev: `fc71db1a258f67537c65f418cafac872945fac4d` (adds `max_backdrop_blurs`), in Cargo.toml and every `[patch]` entry. Builds with it.
- `exo-kit/src/tokens.rs`: `EXO_MAX_BACKDROP_BLURS` 64 → 16 (per coordinator). Verified the failing tests are not caused by this: same six fail at 64.
- Glass exit transitions no longer fade opacity (per coordinator, item 3):
  - `exo-kit/src/components.rs` `popover_transition_from`: exit is now `scale_anchor(0.96, anchor)` (was opacity).
  - `exo-kit/src/components.rs` `modal_card_transition`: exit is now `scale(0.98)` (was opacity). `modal_scrim_transition` kept opacity (scrim is not glass).
  - `exo-ui/src/notice.rs`: toast exit is `offset(0, 4)` (was offset + opacity); enter already `offset(0, 8)`.
  - `exo-ui/src/shell.rs`: mini player exit drops opacity. Room page slide transition kept (not glass).
- `exo-ui/src/palette.rs`: palette row key `command-palette-<id>` so the probe counts palette options.
- `cargo fmt --all` applied across the tree (formatting only).
- Not touched: discord*.rs, store*.rs, music*.rs.

## Shared exo-kit changes (backward compatible?)

`popover_transition_from` and `modal_card_transition` change exit only (enter unchanged, same signature). Any caller gets a scale exit instead of an opacity exit. Backward compatible at the API level; visual exit behaviour changes for every glass popover/modal in native. `EXO_MAX_BACKDROP_BLURS` is a constant (16); callers that relied on 64 headroom would see dropped blurs.

## Gates

- `cargo fmt --all -- --check`: pass (after format).
- `cargo clippy --workspace --locked -- -D warnings`: pass.
- `cargo build --release --locked --bin exo-ui`: pass (2m57s).
- `node tools/check-comments.mjs`: pass.
- `node --test tools/parity/parity.test.mjs`: pass.
- `cargo test --workspace --locked`: **FAIL** (6 exo-ui tests, below).

## Gate failures (open)

HEAD `c15f4ad` passes `cargo test -p exo-ui` 69/69 in a clean worktree. The working tree fails 6:

1. `tests::blur_budget_and_glass_palette_invariants`: palette `max_palette_h` is 580 in `palette.rs`; the test asserts ≤520. HEAD has 520. Working-tree change from the prior session, not mine. Needs a decision: is 580 the intended parity height?
2. `music::tests::mini_player_stays_visible_when_capture_or_library_menu_opens`: asserts `!budget.mini_player` while a menu is open; the working-tree `BlurBudget::compute` turns mini_player budget on. Prior-session change. Needs a decision.
3. `tests::dropdowns_replace_all_five_fake_selects_with_pointer_and_keyboard`: `dd-text-size-menu` blur not emitted at layer_depth 0.
4. `tests::top_bar_context_menus_right_click_keyboard_and_submenus`: `ctx-search` exo-ctx-menu blur not emitted at layer_depth 0.
5. `settings::tests::settings_section_switch_motion_gate`: pop-in findings on `settings-pane-Account` and `settings-pane-About` (mounted at opacity 1, no transition).
6. `library::tests::library_cover_flight_nav_pill_and_deck_motion_gate`: pop-in findings on `room-store`, `room-music`, `room-library`.

Items 3–6 come from working-tree changes from the prior session in menus/select/settings/library/main. I did not isolate my transition edits from them; the room-level pop-in findings do not touch the edited helpers. Not fixed in this pass.

## Not done (open)

- Cursor under open menus (native default vs React pointer on the bar and modal rows): Rune `modal` makes outside targets inert, so `cursor()` returns Default. Needs a Rune-level decision (fork change), so escalating rather than changing it here.
- Library Filter popover (native cycles; React popover with chips).
- "Play as <account>" items in the native GameCard menu (`GameMenu.tsx` line 76). Needs account-list and account-switch RPC (exo-rpc, host, native): touches Riot account switching, so not built without approval.
- `ctx-game-*` menu parity for FolderCard context menu, NotesView textarea spellcheck menu, ExoIslandCall (Discord-owned; out of scope).
- Full onContextMenu sweep table: not completed in this pass.
- Glass fixes: capture popover from frame 1, bar hover glass, library glass over-count (9 vs 5), pc-storage/pc-exo-ai glass, game page glass and tabs, Devices/Apps/Settings glass, settings choice dropdown glass (native 1 vs 8 in earlier baseline), palette glass (native 1 vs 8).
- Homedeck card hover/press: native still "failed to open (scene=library)".
- GPU frame-time before/after for CapturePopover and HomeDeck: not measured.
- Scratch owner-data copy deleted. No Exo or exo-ui process left running from this session. `ExoLauncher.exe` PID 10084 and node MCP servers belong to other sessions and were left alone.

## Blockers for the coordinator

1. Rune cursor under modal (inert-target cursor reset): needs a fork decision.
2. Palette 580 vs 520 and mini-player budget: which is the intended behaviour? Then update the test or the code.
3. Play-as accounts: approve the account-switch RPC work (touches Riot credentials).
