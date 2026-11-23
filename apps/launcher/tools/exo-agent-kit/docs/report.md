# exo-agent-kit v1 Build & Verification Report

## Summary

Built `exo-agent-kit` v1 at `apps/launcher/tools/exo-agent-kit/` as a pinned, offline, zero-runtime-dependency TypeScript + esbuild toolkit for Claude Code and Google Antigravity, following `docs/plan.md` (copied verbatim from `research-agent-kit/docs-audit/agent-kit-plan.md`).

### Built Bundles (`npm run build`)

| Bundle | Entrypoint | Output Size | Purpose |
| --- | --- | ---: | --- |
| `dist/mcp-server.mjs` | `src/mcp-server.ts` | 106,811 B | Hand-rolled JSON-RPC 2.0 stdio MCP server (no MCP SDK) |
| `dist/session-start.mjs` | `src/session-start.ts` | 9,881 B | `SessionStart` hook (`--agent claude \| antigravity`, <= 1.5 KB, fail-open) |
| `dist/exo-orca-helper.mjs` | `src/exo-orca-helper.ts` | 9,680 B | Tool 9 CLI (`wave` parallel launcher + `recover` 3-step sequence) |

Pinned `devDependencies` (zero `dependencies`, committed `package-lock.json`):
- `typescript`: `7.0.2`
- `esbuild`: `0.28.2`
- `@types/node`: `26.6.6`

## Tool List (Rank Order)

| Rank | Tool | Type | Highlights |
| ---: | --- | --- | --- |
| 1 | `exo_run_gates` | MCP | Auto-maps git-changed or explicit paths to Exo gates (`cargo-fmt`, `cargo-clippy`, `cargo-test`, `ui-check`, `ui-dead`, `mouse-check`, `host-build`, `host-test`, `e2e-lib-test`, `parity-test`, `shaders-test`, `comments`, `kit-build`, `kit-test`), grouped worker pool, env-failure tags, head-tail folding |
| 2 | `batch_search` | MCP | Up to 20 concurrent `text`, `regex`, `glob`, `fuzzy`, and read-only `git` queries via direct-spawn `rg` (`shell: false`) + pure-Node fallback, water-fill <= 8.8 KB budgeting |
| 3 | `exo_contract` | MCP | Indexes `host-v1.json`, `ui/src/lib/host.ts`, `ExoLauncher/Services/WebHostBridge*.cs` (balanced-brace dispatcher + partial handler declarations + event emitters), and `native-ui/crates/exo-rpc/src/*.rs` |
| 4 | `exo_rune_api` | MCP | Resolves pinned Rune rev from `native-ui/Cargo.toml` / `Cargo.lock`, parses `pub [const\|async\|unsafe] fn`, `struct`, `enum`, `trait`, `type`, `const`, `impl` into offline rev-keyed JSON cache |
| 5 | `quick_outline` | MCP | Up to 20 files, stateful brace-depth outliner for C# (any indent depth, block namespaces, properties, `=>` members), Rust (nested `mod`/`impl`/`pub const fn`), and TS/TSX/JS with `[startLine..endLine]` ranges |
| 6 | `exo_snapshot` (+ `session-start.mjs`) | MCP + Hook | Hardened read-only git branch/dirty/commits snapshot, explicit `Orca: pass --base-branch <branch>` guard off `main`, active areas, and applicable skills |
| 7 | `exo_parity_snap` | MCP | Wraps in-repo `apps/launcher/tools/parity/parity-snap.ps1` and `parity-compare.py`; refuses installed `%LOCALAPPDATA%\ExoLauncher\app\ExoLauncher.exe` and `%LOCALAPPDATA%\ExoLauncher` data dir; confines write outputs (`outDir`, `dataDir`, `compare.outPath`) to workspace root |
| 8 | `apply_edits` | MCP | Up to 50 files, array or JSON-stringified array, atomic & `bestEffort` modes, CRLF/mode preservation, closest-line hints, strict workspace write confinement, allowlisted direct-spawn `verify` |
| 9 | `exo-orca-helper` | CLI | `wave` (1..4 concurrent `orca orchestration worker-start` with auto-injected `--base-branch <current-branch>` + receipt logs) and `recover` (`worker-abandon`/`worker-stop` -> `task-update --status ready` -> `worker-start --worktree path:...`) |

## Safety boundary

Because `exo-agent-kit` MCP tools run locally without Antigravity's remote per-edit/per-command safety checks, the kit enforces its own filesystem and subprocess boundaries in code and at build time:

1. **Workspace-confined writes (`resolveConfinedWritePath` / `resolveConfinedDir` in `src/utils/paths.ts`):**
   - Removed the misleading `resolveSafePath` name (`resolveUnconfinedPath` is used strictly for read-only search/outline/reference inputs).
   - Every write target in `apply_edits` and `exo_parity_snap` (`outDir`, `dataDir`, `compare.outPath`; defaults now land in gitignored `apps/launcher/work/`) is canonicalized via `fs.realpathSync` (following symlinks and Windows junctions for existing paths, dangling symlinks, and the nearest existing ancestor of new files) and confined to `ws.repoRoot`.
   - Rejects any target outside `ws.repoRoot` (absolute paths, `..` traversal, symlink/junction escapes) and rejects any path equal to or inside `.git/`.
   - `resolveWorkspacePaths` also refuses filesystem roots (e.g. `C:\`, catching a real `C:\.git` on this workstation during testing), `%USERPROFILE%`, and `C:\Users` as repo roots.
   - `exo_rune_api` writes exclusively to its own rev-keyed cache directory (`resolveCacheDir()`).
2. **Strict `apply_edits` `verify` allowlist (`validateVerifySpec` in `src/tools/apply-edits.ts`):**
   - Replaced the shell denylist with an explicit bare-binary allowlist (no path separators in `verify.command`) and confined `verify.cwd` inside `ws.repoRoot` (excluding `.git/`).
   - Allowed commands: `cargo` (`fmt`/`clippy`/`test`/`build`/`check`, no `--config`/`-Z`), `dotnet` (`build`/`test`), `npm` (`npm run <script>` only, no `--script-shell`/`--prefix`/`--*config`), `node` (`node --test <repo path>` or `node <repo script>`, strictly forbidding `-e`/`-p`/`-r`/`--eval`/`--print`/`--import`/`--require`/`--loader`/`--env-file` and short-flag clusters containing `e`/`p`/`r`), `python`/`python3` (`-I <repo script>` only, no `-c`/`-m`), `rg` (no `--pre`/`--pre-glob`), and `git` routed through `runReadOnlyGit`.
   - Any other binary (e.g. `rundll32`, `mshta`, shells) or disallowed flag is refused before edits are written.
3. **Build-time enforcement (`scripts/verify-guarantees.mjs`):**
   - Every tool under `src/tools/` (other than `exo-rune-api.ts`) that calls a filesystem write API (`writeFileSync`, `appendFileSync`, `renameSync`, `copyFileSync`, `rmSync`, `unlinkSync`, `createWriteStream`, `mkdirSync`) must call `resolveConfinedWritePath()`.
   - Forbids the name `resolveSafePath`, requires `realpathSync` + `.git` checks in `src/utils/paths.ts`, and requires `ALLOWED_VERIFY_COMMANDS`/`validateVerifySpec` (with no `resolveUnconfinedPath`) in `src/tools/apply-edits.ts`.
   - Unit-tested in `test/kit.test.mjs` with both a planted `shell: true` file and a planted unrestricted write tool (`src/tools/planted-writer.ts`), plus refusal tests for outside absolute paths, `..` escapes, Windows junction escapes, `.git/` writes, `node -e`/`--eval`/`-p`/`--import`/`-r`, `python -c`/`-I -c`, `rundll32`, `mshta`, and `verify.cwd` outside repo root.

## Build-Time Security Guarantees (`scripts/verify-guarantees.mjs`)

Checked automatically during `npm run build` across all 16 `src/` files and 3 `dist/` bundles:
- Zero network module imports (`http`, `https`, `http2`, `net`, `tls`, `dgram`, `dns`, `undici`, `node:*`) and zero runtime network APIs (`fetch`, `WebSocket`, `XMLHttpRequest`, `EventSource`).
- Zero `shell: true` / shell path options and zero `exec`/`execSync` calls.
- `DEFAULT_INLINE_MAX_BYTES = 8800` enforced in `src/utils/budget.ts` and `src/protocol/mcp.ts`.
- Read-only git flags (`--no-optional-locks`, `core.fsmonitor=false`, `diff.external=`, `gpg.program=false`) enforced in `src/utils/git.ts` with no direct `git` spawns bypassing it.
- Confined write boundary (`resolveConfinedWritePath`) and `apply_edits` verify allowlist enforced across `src/tools/`.
- Zero hardcoded Windows user profile paths (`[A-Za-z]:\Users\<name>`) in `src/` or `dist/`.
- Zero CWD workspace plugin lookups (`./plugin.json`, `.agents/plugins`) and zero auto-update code.

## Verification Gates

| Gate | Command | Result |
| --- | --- | --- |
| Clean install | `npm ci --no-audit --no-fund` | Exit 0 (6 dev packages, 0 runtime deps) |
| Typecheck + Bundle + Guarantees | `npm run build` | Exit 0 (`tsc --noEmit` + 3 esbuild bundles + 16 src / 3 dist verified) |
| Unit + Integration Tests | `npm test` (`node --test test/kit.test.mjs`) | Exit 0 (15/15 pass in 3.40 s) |
| Stdio MCP Smoke Test | `npm run smoke` (`node test/mcp-smoke.mjs`) | Exit 0 (all 8 tools + `initialize` + `tools/list`, all <= 8,800 JSON bytes) |
| Dry-Run Install | `node scripts/install.mjs` | Exit 0 (no files written; `%USERPROFILE%\.exo-agent-kit` untouched) |
| Comment Ratchet | `node apps/launcher/tools/check-comments.mjs` | Exit 0 (0 comment lines across all 24 kit files) |

## MCP Stdio Smoke Test Results

Captured by `node test/mcp-smoke.mjs` spawning `dist/mcp-server.mjs` over stdio against this repository:

| Step | Latency (ms) | Wire Response (B) | Text Payload JSON (B) | Status |
| --- | ---: | ---: | ---: | --- |
| `initialize` | 34.2 | 150 | — | OK |
| `tools/list` (8 tools) | 0.4 | 5,847 | — | OK |
| `tools/call:exo_run_gates` (`parity-test`) | 95.0 | 171 | 100 | OK |
| `tools/call:batch_search` (2 concurrent queries) | 35.8 | 625 | 554 | OK |
| `tools/call:exo_contract` (`game.get`) | 157.9 | 667 | 596 | OK |
| `tools/call:exo_rune_api` (`tracking`, cold index) | 344.1 | 876 | 805 | OK |
| `tools/call:quick_outline` (`exo-rpc/src/lib.rs`) | 5.6 | 927 | 856 | OK |
| `tools/call:exo_snapshot` | 55.7 | 1,250 | 1,179 | OK |
| `tools/call:exo_parity_snap` (`parity-compare.py`) | 84.4 | 462 | 391 | OK |
| `tools/call:apply_edits` | 4.0 | 138 | 66 | OK |

## Dry-Run Install Output (`node scripts/install.mjs`)

```text
=== exo-agent-kit install (DRY RUN — no files written) ===
Target runtime dir: C:\Users\Erix\.exo-agent-kit
  [WOULD COPY] C:\Users\Erix\orca\workspaces\Exo-Launcher\agent-kit\apps\launcher\tools\exo-agent-kit\dist\mcp-server.mjs -> C:\Users\Erix\.exo-agent-kit\dist\mcp-server.mjs
  [WOULD COPY] C:\Users\Erix\orca\workspaces\Exo-Launcher\agent-kit\apps\launcher\tools\exo-agent-kit\dist\session-start.mjs -> C:\Users\Erix\.exo-agent-kit\dist\session-start.mjs
  [WOULD COPY] C:\Users\Erix\orca\workspaces\Exo-Launcher\agent-kit\apps\launcher\tools\exo-agent-kit\dist\exo-orca-helper.mjs -> C:\Users\Erix\.exo-agent-kit\dist\exo-orca-helper.mjs
  [WOULD COPY] C:\Users\Erix\orca\workspaces\Exo-Launcher\agent-kit\apps\launcher\tools\exo-agent-kit\dist\VERSION -> C:\Users\Erix\.exo-agent-kit\VERSION
  [WOULD MKDIR] C:\Users\Erix\.exo-agent-kit\cache
Pass --apply to write the bundles to %USERPROFILE%\.exo-agent-kit\.

=== Manual Config Edits (NEVER auto-applied by this script) ===

1. Claude Code MCP registration — merge into "mcpServers" in BOTH:
   - C:\Users\Erix\.claude.json
   - C:\Users\Erix\AppData\Roaming\orca\claude-profiles\eed89569-842a-40f2-8daf-1057358520ce\home\.claude.json
{
  "exo-kit": {
    "type": "stdio",
    "command": "node",
    "args": [
      "C:/Users/Erix/.exo-agent-kit/dist/mcp-server.mjs"
    ],
    "env": {
      "EXO_PARITY_REFS": "C:/Users/Erix/.exo-agent-kit/refs",
      "EXO_KIT_NO_NETWORK": "1"
    }
  }
}

2. Claude Code SessionStart hook — append to "hooks.SessionStart" ONLY in:
   - C:\Users\Erix\.claude\settings.json
   (Orca settings-sync.json syncs settings.json into managed profiles automatically; keep existing orca-agent-hook.exe entries and do NOT add PreToolUse/PostToolUse hooks.)
{
  "hooks": [
    {
      "type": "command",
      "command": "node C:/Users/Erix/.exo-agent-kit/dist/session-start.mjs --agent claude",
      "timeout": 5
    }
  ]
}

3. Google Antigravity pinned plugin — create under C:\Users\Erix\.gemini\config\plugins\exo-agent-kit:

   3a. C:\Users\Erix\.gemini\config\plugins\exo-agent-kit\plugin.json
{
  "name": "exo-agent-kit",
  "version": "1.0.0",
  "description": "Pinned offline speed & Exo Launcher domain toolkit"
}

   3b. C:\Users\Erix\.gemini\config\plugins\exo-agent-kit\mcp_config.json
{
  "mcpServers": {
    "exo-kit": {
      "command": "node",
      "args": [
        "C:/Users/Erix/.exo-agent-kit/dist/mcp-server.mjs"
      ],
      "env": {
        "EXO_PARITY_REFS": "C:/Users/Erix/.exo-agent-kit/refs",
        "EXO_KIT_NO_NETWORK": "1"
      },
      "tools": {
        "exo_run_gates": {
          "eager": true
        },
        "batch_search": {
          "eager": true
        },
        "exo_contract": {
          "eager": true
        },
        "exo_rune_api": {
          "eager": true
        },
        "quick_outline": {
          "eager": true
        },
        "exo_snapshot": {
          "eager": true
        },
        "exo_parity_snap": {
          "eager": true
        },
        "apply_edits": {
          "eager": true
        }
      }
    }
  }
}

   3c. C:\Users\Erix\.gemini\config\plugins\exo-agent-kit\hooks.json
{
  "SessionStart": [
    {
      "hooks": [
        {
          "type": "command",
          "command": "node C:/Users/Erix/.exo-agent-kit/dist/session-start.mjs --agent antigravity",
          "timeout": 5
        }
      ]
    }
  ]
}

   3d. C:\Users\Erix\.gemini\config\plugins\exo-agent-kit\rules\exo-kit.md
# exo-agent-kit Routing Rules

- Use `exo_contract` before editing bridge or RPC code (`host-v1.json`, `host.ts`, `WebHostBridge*.cs`, `exo-rpc`).
- Use `exo_rune_api` before claiming any Rune widget, style, or token capability or absence.
- Use `exo_run_gates` to run mapped verification gates concurrently with folded <=8.8 KB output.
- Use `batch_search` for multi-query search/grep/read-only-git exploration.
- Use `quick_outline` on unfamiliar files >200 lines before ranged `read_file` calls.
- Use `apply_edits` for >=2 files or multi-hunk edits.
- Use `exo_parity_snap` for native parity captures/diffs; do not inspect parity PNGs inside >150k-token sessions.
```

## Deviations from Plan (with Reasons)

1. **In-repo location (`apps/launcher/tools/exo-agent-kit/`) instead of standalone repo (Plan §5.3):** Required directly by the task specification so the kit is versioned inside Exo Launcher alongside `apps/launcher/tools/parity/`.
2. **Pre-install cache directory fallback (`src/utils/paths.ts`):** When `%USERPROFILE%\.exo-agent-kit` does not yet exist (or `EXO_KIT_CACHE_DIR` is overridden), `exo_rune_api` writes its offline rev-keyed cache to `os.tmpdir()/exo-agent-kit-cache` so tests and pre-install runs never create `%USERPROFILE%\.exo-agent-kit`. Once `install.mjs --apply` creates `%USERPROFILE%\.exo-agent-kit`, the cache automatically lives in `%USERPROFILE%\.exo-agent-kit\cache\` per Plan §5.3.
3. **Direct `npm-cli.js` execution (`src/utils/exec.ts`):** On Windows, `spawn('npm', ..., { shell: false })` fails because `npm` is a `.cmd` shim. `resolveNpmInvocation` locates `node_modules/npm/bin/npm-cli.js` beside `process.execPath` and runs it via `node` with `shell: false`, avoiding any `cmd.exe` shell execution.
4. **`exo_parity_snap` direct `compare` mode + local `UhhErix/parity-harness` fast-forward:** Fast-forwarded `UhhErix/parity-harness` (`65b8f05`, direct child of base `5cae130`) per task spec so `apps/launcher/tools/parity/` is present. Added an optional `compare` parameter on `exo_parity_snap` wrapping `python -I parity-compare.py --json -` so diff scoring is directly testable and callable without building or launching `ExoLauncher.exe`.
5. **`apps/launcher/tools/comments-ratchet.json` tightening:** Base `HEAD` had 12 pre-existing stale ratchet entries in `mouse/` and `ui/` that caused `node apps/launcher/tools/check-comments.mjs` to exit 1. Ran `node apps/launcher/tools/check-comments.mjs --update` (updating only `tools/comments-ratchet.json`, touching zero app code) so the gate exits 0 with all 24 new kit files at 0 comments.
