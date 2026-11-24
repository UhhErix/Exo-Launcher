# host-v1 Record & Replay Parity Tool

Records what the reference React/WebView2 UI and the native Rune UI each ask the .NET host for during identical user actions, then diffs the traces into `improve/rpc-parity.md` and `improve/rpc-parity.json`.

## How it works

1. **Opt-in host trace (`EXO_RPC_TRACE=<file.jsonl>`)** — hooked once at `WebHostBridge`'s shared dispatch, reply, and push-event points (`ExoLauncher/Services/RpcTrace.cs`). Off by default (one cached boolean check when unset). Writes schema shapes (`*_shape`: keys and JSON types only) and never values, tokens, emails, messages, or paths.
2. **Action runner (`tools/rpc-parity/run.mjs`)** — executes `tools/rpc-parity/actions.json` against two disposable test instances (never touching `%LOCALAPPDATA%\ExoLauncher\app` and refusing to run while a game is running):
   - **Web**: launched via `tools/test-instance.ps1` with `EXO_UI=web`, driven over CDP (`tools/e2e/cdp.mjs`).
   - **Native**: launched as `ExoLauncher.exe --ui=native` in its own temp `EXO_LAUNCHER_DATA_DIR`, driven over Windows UI Automation (`tools/rpc-parity/uia.ps1`) against Rune's AccessKit tree.
3. **Diff generator (`tools/rpc-parity/diff.mjs`)** — slices the host trace per action, resolves React callers in `ui/src/`, compares called methods, push events, and chattiness (>2× call ratio), and writes `improve/rpc-parity.md` + `improve/rpc-parity.json` sorted by biggest gaps first.

## Running

Prerequisites (from `apps/launcher/`):

- Build Debug host: `dotnet build ExoLauncher\ExoLauncher.csproj -c Debug -p:Platform=x64`
- Build native UI: `cargo build --release --bin exo-ui` inside `native-ui/`
- Second monitor attached (test instances open only on the secondary display) and no game running.

Run full capture + diff:

```pwsh
node tools/rpc-parity/run.mjs
```

Optional flags:

- `--only=<id1,id2>` — run a subset of actions from `tools/rpc-parity/actions.json`
- `--out=<dir>` — directory for raw/per-action trace slices (default: `tools/rpc-parity/out/<timestamp>`)
- `--web-only` / `--native-only` — record one side only
- `--exe=<path>` — override `ExoLauncher.exe` path
- `--ui-exe=<path>` — override `exo-ui.exe` path

Regenerate the diff report from an existing capture directory without re-launching Exo:

```pwsh
node tools/rpc-parity/diff.mjs --dir <capture-dir>
```

Run unit tests for the parity tools:

```pwsh
node --test tools/rpc-parity/*.test.mjs
```

## Limits (what cannot be driven)

- **Installed Exo / signed-in accounts**: Test instances use a fresh disposable `EXO_LAUNCHER_DATA_DIR`, so authenticated flows (Discord DMs/guilds, Exo Account sync, Music provider playback, store sign-in) stay signed out unless credentials exist in the store client itself. Actions that require a signed-in Discord session (such as `discord-dm`) are marked `not drivable` with the reason when no DM list is present.
- **Missing UI controls on native**: Controls that exist in React but do not exist in `exo-ui` (for example, the Library `Downloads` sheet) cannot be invoked over UIA and are recorded honestly as `not drivable` rather than faked.
- **Destructive / game-launching actions**: Game launches, installs, uninstalls, store sign-ins, and elevated actions are intentionally excluded so the recorder stays non-destructive and safe.
- **Background push events**: Push events only appear in a live trace if the host emits them during the action window (for example, `library.updated`, `install.queue`, `store.changed`). To keep event gaps visible even when an event did not fire during an idle run, `diff.mjs` also cross-references static push-event subscriptions in `ui/src/` against `HostEvent` handlers in `native-ui/crates/exo-rpc/`.
- **Time-window attribution**: Action boundaries use UTC millisecond time windows (plus `debug.mark` round-trip fences on web). Asynchronous background host events arriving during an action window are attributed to the action active when they arrived.
