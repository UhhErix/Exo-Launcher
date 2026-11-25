# Exo Agent Kit — Research Survey & Implementation Plan

> **Evidence legend used throughout this document:**
> - `[verified in code]` — inspected directly in cloned source code or this repository (`file:line` cited).
> - `[verified on machine]` — inspected directly in local configs, CLI output, or installed binaries on this Windows workstation.
> - `[independent benchmark]` — third-party evaluation or published paper independent of the tool's author.
> - `[README/docs claim]` — author-reported benchmark, claim, or documentation (not independently verified).

---

## Executive Summary

We run **Claude Code (Opus, orchestrator)** and **Google Antigravity (Gemini 4 Neon XHigh, workers)** under **Orca** on Windows against Exo Launcher (**1,714 C# files**, **996 TS/TSX files**, **40 Rust files** `[verified on machine]`). Latency and token cost in this stack are dominated by four bottlenecks:
1. **Round-trip count & TTFT:** Median Antigravity TTFT is **6.07 s** (79% of median turn) and remote safety checks add **1.57 s P50** serially on `edit_file` (100%) and `run_command` (92.9%), while native `read_file` has **0%** remote checks (**6.69 ms P50**) `[README/docs claim: INK-Q/agy-fast docs/ANALYSIS_REPORT.md]`.
2. **Oversized outputs & cache busts:** Tool returns > ~10 KB spill to disk in Antigravity, costing an extra turn to inspect; inter-turn stalls > 60 s spike prompt-cache miss rates from **1.4%** to **15.9–93.8%**, and carrying screenshots in long context (>180k tokens) drives >60 s stalls to **31.2%** `[README/docs claim: agy-fast ANALYSIS_REPORT.md]`.
3. **Domain blind spots in Exo Launcher:** Agents repeatedly burn 3–8 turns hunting across the pinned Rune checkout (`4,774 pub fn` + `118 pub const fn` `[verified on machine]`), the **342 RPC methods / 77 events** spanning `host-v1.json` + `host.ts` + 62 `WebHostBridge*.cs` partials + `exo-rpc` `[verified on machine]`, multi-stack verification gates (`cargo`, `dotnet`, `npm`, `node --test`), and hardcoded parity capture scripts.
4. **Hook & sync friction under Orca:** Orca already runs **15 synchronous Claude hooks** (`orca-agent-hook.exe`) and **5 Antigravity `.cmd` hooks** (`orca-status`), plus a local `read_file` Node guard `[verified on machine]`, and Orca's `settings-sync.json` syncs `settings.json` but **not** `.claude.json` user-scope MCP servers `[verified on machine]`.

**Recommendation:** Build a single, owned, pinned, offline toolkit (**`exo-agent-kit`**) compiled from TypeScript via `esbuild` into two zero-dependency Node.js ESM bundles (`mcp-server.mjs`, `session-start.mjs`), with **zero runtime network access** and **zero auto-update**. Copy the proven batching/budgeting patterns from `agy-fast` (discarding its auto-updater and CWD hook lookup), fix multi-file outlining for C# and Rust, and prioritize **five high-leverage Exo-specific MCP tools** plus a lean `SessionStart` snapshot.

---

## 1. Survey Table

| # | Project / Area | URL & License | What It Does | Measured / Claimed Gains (with Sources) | Install & Machine Footprint | Risks (Update / Net / Telemetry / Workspace Exec) | Verdict |
|---|---|---|---|---|---|---|---|
| 1 | **agy-fast** (v0.2.10) | [github.com/INK-Q/agy-fast](https://github.com/INK-Q/agy-fast) · MIT `[verified in code]` | 5 batched MCP tools (`batch_search`, `apply_edits`, `read_slices`, `quick_outline`, `run_checks`), `SessionStart` git/dir snapshot, routing rules, 8.8 KB hard inline cap `[verified in code]`. | Author 10-day before/after (14 baseline vs 7 `agy-fast` Antigravity sessions): **-33.7%** turns/10 actions (`-38.2%` at >=150k ctx), **-31.5%** est. quota, **-39.7%** tool-return bytes, **+4.4 pp** cache hit (89.6%->94.0%), 0 disk spills across 134 calls `[README/docs claim: docs/ANALYSIS_REPORT.md]`. | `install.mjs` copies into `~/.gemini/config/plugins/agy-fast/`, edits `~/.gemini/config/mcp_config.json` & `hooks.json`; creates `~/.agy-fast-updater.json` state `[verified in code]`. | **HIGH (2 confirmed):** (1) Auto-updates by default (`mode="auto"`, 4 h interval) from author's GitHub with **no signature check** and executes downloaded `dist/mcp-server.mjs` during staging (`updater-core.ts:7-9,319,579`); (2) `hooks.json:6` resolves `./plugin.json` + `./dist/session-start.mjs` in CWD before home dir -> workspace RCE `[verified in code]`. | **Copy ideas** (batching, water-fill 8.8 KB cap, atomic/`bestEffort` edits, hardened git snapshot). **Skip** installing/vendoring as-is. |
| 2a | **agy-dart** (v1.6.0) | [github.com/zou172866/agy-dart](https://github.com/zou172866/agy-dart) · MIT `[verified in code]` | Bundles `agy-fast` + SQLite FTS5 context-mode (`node:sqlite` `DatabaseSync`, 8 `ctx_*` tools) + sidecar UI + 3,519-line stall watchdog + cache TTL keepalive pings `[verified in code]`. | Claims 98% output compression into FTS5 + `agy-fast` turn reduction; no independent benchmark `[README/docs claim]`. | Plugin under `~/.gemini/config/plugins/agy-dart/`; writes per-project SQLite DBs & registers 5 lifecycle hooks (`SessionStart`, `PreToolUse`, `PostToolUse`, `PreInvocation`, `Stop`) `[verified in code]`. | **HIGH:** Same CWD `.` + `.agents/plugins/agy-dart` lookup in **all 5 hooks** (`hooks.json:6,16,28,37,44`); spawns Node on every `PreToolUse`/`PostToolUse` (stacks latency on Orca hooks); `ctx_fetch_and_index` makes outbound HTTP/HTTPS calls (`context/lib/tools.js:4-5`); `ttlKeepalive: true` (`config.js:63`) spends extra LLM pings `[verified in code]`. | **Copy narrow ideas** (Node built-in `node:sqlite` if FTS5 ever needed; JSON-escaped byte cap `tools.js:111`). **Skip** installing/vendoring. |
| 2b | **agy-fast-io** | [github.com/oil-oil/agy-fast-io](https://github.com/oil-oil/agy-fast-io) · MIT `[verified in code]` | Rules-only Antigravity plugin (`plugin/plugin.json` + 68-line Chinese `plugin/rules/AGENTS.md`): locate-first (`grep -rn`), parallel ranged `read_file` (150–200 lines), Desktop allowlist, `&&`-chained builds, <1,500-char replies `[verified in code]`. | No published quantitative benchmark; targets eliminating serial read/command safety checks `[README/docs claim]`. | Copies `plugin/` to `~/.gemini/config/plugins/agy-fast-io/`; zero MCP, zero hooks, zero executables `[verified in code]`. | **LOW:** Zero network, zero code execution, zero telemetry `[verified in code]`. Unix-centric allowlist (`grep`/`head`/`tail`) does not fit Windows PowerShell; largely duplicates existing `~/.gemini/config/GEMINI.md` rules `[verified on machine]`. | **Copy rules** (Windows/Exo-adapted routing & concise output rules). **Skip** installing. |
| 2c | **agy-staff** (v0.9.3) | [github.com/keli-wen/agy-staff](https://github.com/keli-wen/agy-staff) · MIT `[verified in code]` | Cross-harness plugin letting Claude Code/Codex/Pi spawn `agy` CLI as background workers (`lead`, `brain`, `researcher`, `reviewer`, `implementer`, `jobs`) `[verified in code]`. | Author benchmarks (`bench/README.md`): Claude Opus host spend **-16%** SWE-bench, **-36%** SWE-Atlas QnA, **-39%** DeepSearchQA, but median wall time **rose** 2–8 m -> 13–19 m; Codex switching 30 s polling -> 300 s blocking wait cut wait calls **-68%** and spend **-32%** `[README/docs claim]`. | Installs plugin skill/CLI into `~/.claude/plugins/` or Codex; spawns `agy` subprocesses; writes job state under temp/config dir `[verified in code]`. | **MEDIUM:** Runs `agy` unrestricted by default; duplicates Orca's native multi-agent worktree orchestration & reactive wakeup; `agy` CLI is not installed on PATH here `[verified on machine]`. | **Skip** (Orca already orchestrates Antigravity natively). **Copy benchmark insight:** never poll; reserve delegation for parallel/deep tasks. |
| 3a | **Serena** (v2) | [github.com/oraios/serena](https://github.com/oraios/serena) · GPL-3.0-or-later (SolidLSP: MIT) `[README/docs claim]` | LSP-backed semantic symbol/reference/rename MCP server (C# Roslyn needs .NET 10+ & `pwsh`, Rust `rust-analyzer`, TS `vtsls`/`tsserver`) `[README/docs claim]`. | Official docs: 8–12 calls -> 1 on cross-file refactors `[README/docs claim]`. **However**, `repowise-bench` (n=43, 2026) found -14.8% output tokens but **+42% more tool calls** (10.1 vs 7.2) `[independent benchmark]`; Xu (arXiv, June 2026) found LSP *increased* tokens for localization (+6% Opus, +118% Sonnet) and +12–19% tokens on references (precision 0.76 -> 1.00) `[independent benchmark]`. | `uvx --from git+https://... serena` or local clone; downloads language servers on first run; spawns persistent per-workspace LSP daemons `[README/docs claim]`. | **MEDIUM-HIGH:** Downloads language servers at runtime; heavy RAM/cold-start running Roslyn + `rust-analyzer` + `tsserver` across multiple Orca worktrees; Windows Smart App Control blocks `uv`-built `serena.exe` (issue #2196); GPLv3 copyleft `[README/docs claim]`. | **Skip** for general use (negative ROI on rounds/RAM/startup in multi-worktree Orca; compiler gates cover type-checked refactors). |
| 3b | **ast-grep-mcp** | [github.com/ast-grep/ast-grep-mcp](https://github.com/ast-grep/ast-grep-mcp) · MIT `[README/docs claim]` | 4 AST structural pattern search/lint MCP tools over `ast-grep` (`sg`), supporting C#, Rust, TS `[README/docs claim]`. | Compact text mode uses ~75% fewer tokens than JSON output; no published end-to-end agent turn benchmark `[README/docs claim]`. | `uvx` Python wrapper + `sg` binary (`sg` not currently installed on PATH `[verified on machine]`). | **LOW (if pinned locally):** Offline once installed; stateless (no cross-file index); experimental repo `[README/docs claim]`. | **Skip Phase 1** (optional Phase 2 binary if structural codemods become frequent). |
| 3c | **Ripgrep MCP servers** (`mcp-ripgrep`, `ripgrep-mcp`, `Auu.Mcp.Rg`) | [mcollina/mcp-ripgrep](https://github.com/mcollina/mcp-ripgrep), [shk1447/ripgrep-mcp](https://github.com/shk1447/ripgrep-mcp), [superauu/Auu.Mcp.Rg](https://github.com/superauu/Auu.Mcp.Rg) · MIT `[README/docs claim]` | Single-query MCP wrappers around `rg` (`rg` 15.2.0 is installed on this machine `[verified on machine]`). | No round-reduction advantage over native Claude `Grep` or shell `rg` because queries are unbatched `[verified in code/analysis]`. | `npx` or dotnet tool + MCP entry `[README/docs claim]`. | **HIGH on `mcp-ripgrep` <=0.4.0:** **CVE-2026-113378** OS command injection via unescaped `fileType` passed through a shell `[independent advisory]`; `npx` risks live registry fetches. | **Skip** standalone wrappers. **Copy direct-spawn `rg` (no shell!)** inside our `batch_search`. |
| 3d | **Tree-sitter Repo-Map & Outliners** (`SylphxAI/repomap`, `treesitter-mcp`, `RepoMapper`) | [SylphxAI/repomap](https://github.com/SylphxAI/repomap) (MIT, Rust), [Christoph/treesitter-mcp](https://github.com/Christoph/treesitter-mcp) (MIT), [pdavis68/RepoMapper](https://github.com/pdavis68/RepoMapper) (MIT) `[README/docs claim]` | Tree-sitter AST symbol skeletons / Aider-style PageRank+BM25 repo maps and file outlines `[README/docs claim]`. | `SylphxAI/repomap`: **83.0% Acc@10** on SWE-bench Verified (vs 55.8% BM25), 0.845 NDCG@10, 2,271 files indexed in 2.6 s cold / 0.5 s warm, 17–74 ms p50 query `[README/docs claim]`. `treesitter-mcp`: ~2,071 schema tokens, **2.7x–57x** smaller payloads than raw file/grep across Rust/TS fixtures `[README/docs claim]`. | Binary or npm/Python MCP server; caches local index per repo `[README/docs claim]`. | **LOW (if built offline/pinned):** Local CPU/disk only, no runtime network `[README/docs claim]`. | **Copy idea** (AST-accurate or brace-depth multi-file `quick_outline` for C#/Rust/TS; consider Rust `repomap` only if Phase 2 needs semantic ranking). |
| 3e | **Repomix** (`--mcp`) | [github.com/yamadashy/repomix](https://github.com/yamadashy/repomix) · MIT `[README/docs claim]` | Packs repository subsets into a single XML/MD bundle with optional tree-sitter `--compress` `[README/docs claim]`. | Claims ~70% compression `[README/docs claim]`; independent Nov 2026 test measured **37.7%** (55.5k -> 34.5k tokens on 145 files) with ~4.5k MCP tool-schema overhead `[independent benchmark]`. | `npx repomix --mcp` or global npm install `[README/docs claim]`. | **LOW-MEDIUM:** Unpinned `npx` fetches live packages; packing a 2,750-file monorepo bloats context instead of slicing it `[verified on machine]`. | **Skip**. |
| 4 | **Claude Code** (hooks, outputStyle, ToolSearch) | [code.claude.com/docs](https://code.claude.com/docs/en/hooks) · Proprietary `[README/docs claim]` | Native `Read`/`Edit`/`Grep`/`Glob`, `outputStyle: "Concise"` (already enabled `[verified on machine]`), `SessionStart` stdout injection, deferred MCP `ToolSearch`. | Anthropic reports `ToolSearch` cuts MCP schema tokens by **46.9%** (50–70k tokens on large stacks) `[README/docs claim]`; `Concise` style trims output tokens `[verified on machine]`. | `~/.claude/settings.json`, `~/.claude.json`, plus Orca managed profile `%APPDATA%\orca\claude-profiles\<id>\home\` `[verified on machine]`. | **MEDIUM (operational):** Orca already installs **15 synchronous hooks** (`orca-agent-hook.exe`) on every Claude tool event; adding sync `PostToolUse` formatters/linters stacks latency on every edit. Orca `settings-sync.json` does **not** sync `.claude.json` `mcpServers` `[verified on machine]`. | **Configure carefully:** `SessionStart` hook once per session + small pinned MCP server in **both** `.claude.json` homes; zero per-tool hooks. |
| 5 | **Google Antigravity** (plugins, MCP, hooks, settings) | Local installation & plugin spec `[verified on machine]` | Plugins in `~/.gemini/config/plugins/<name>/` (`plugin.json`, `mcp_config.json`, `hooks.json`, `rules/*.md`, `skills/`), `"eager": true` MCP tools, `autoExecutionPolicy`, `verbosity` `[verified on machine]`. | Eager tools eliminate schema-lookup turns; `EAGER`/`TURBO` + `enableTerminalSandbox: false` + `verbosity: "low"` (all already set `[verified on machine]`) remove confirmation/sandbox overhead `[README/docs claim]`. | `~/.gemini/config/{config.json,hooks.json,mcp_config.json,GEMINI.md,plugins/}`, `~/.gemini/antigravity-cli/settings.json` `[verified on machine]`. | **MEDIUM (operational):** `~/.gemini/config/hooks.json` already runs 5 `orca-status` `.cmd` hooks + `context-read-guard` (`node hooks/guard-read-file.mjs` on every `read_file`) `[verified on machine]`; plugin root `AGENTS.md` is ignored (must use `rules/*.md`) `[verified in code: agy-fast]`. | **Adopt plugin install** with `"eager": true` on core tools, safe absolute-path `SessionStart` hook, and no per-tool hooks. |
| 6 | **Orca CLI & Skills** | Local `orca` CLI & bundled `orchestration` skill `[verified on machine]` | Multi-agent IDE orchestration (`worker-start`, `worker-abandon`, `task-update`, `worktree create`), reactive wakeup, 11 bundled skills `[verified on machine]`. | Parallel `Start-Process` worker launch overlaps ~10 s per-worker readiness waits (up to 4× faster wave start); avoiding base-branch mis-forks prevents wasted worker runs `[verified on machine: references/parallel-start.md]`. | Native Orca CLI + `~/.orca/agent-hooks/` `[verified on machine]`. | **MEDIUM (workflow traps):** `new-child` defaults to repo base (`main`) unless `--base-branch` is passed; wrong restart sequence (`--retry-of` on tripped breaker or `--spec` duplicating a task) fails or duplicates tasks `[verified on machine]`. | **Copy into kit:** `SessionStart` branch warning + deterministic Orca dispatch/recovery helper (optional Rank 9). |

---

## 2. Deep-Dive Notes on Survey Items 1–6

### 2.1 How `INK-Q/agy-fast` Works (`[verified in code]` at v0.2.10)
`agy-fast` compiles strict TypeScript with `esbuild` (`package.json:56`) into zero-runtime-dependency ESM bundles (`dist/mcp-server.mjs`, `dist/session-start.mjs`, `dist/updater.mjs`, `dist/benchmark.mjs`) targeting Node >= 20, enforcing `DEFAULT_INLINE_MAX_BYTES = 8_800` (`src/utils/output-filter.ts:33`) so Antigravity never spills tool results to `output.txt` (> ~10 KB).
- **`batch_search`** (`src/tools/batch-search.ts:1482-1581`): Runs up to 20 concurrent queries (`text`, `regex`, `glob`, `fuzzy`, `git`) via `Promise.all`; spawns `rg` directly with pure-Node fallback; enforces a strict read-only git allowlist (`READONLY_GIT_SUBCOMMANDS` L50-63, `FORBIDDEN_GIT_GLOBAL_FLAGS` L65-77, explicitly blocks `status -v` at L243-260); allocates remaining bytes fairly via `fitSectionsToByteBudget` (L1578).
- **`apply_edits`** (`src/tools/apply-edits.ts:539-991`): Up to 50 files per call; accepts an array or JSON-stringified array (`parseMaybeJsonArray` L542, working around host array-serialization bugs); merges duplicate/symlink targets (L675-740); Phase 1 validates all hunks in memory; Phase 2 writes atomically per file while preserving CRLF/file mode/symlinks (L858-877); `bestEffort` mode commits valid files and returns closest-line hints on failed hunks (L880-938); Phase 3 runs an optional inline `verify` command with compact error folding (L946-986) inside a 170 s shared MCP budget (`MCP_CALL_BUDGET_SEC = 170`, `src/utils/exec-utils.ts:150`).
- **`read_slices` & `quick_outline`** (`src/tools/read-slices.ts:2800-3262`): `quick_outline` delegates up to 40 flat `paths` to `runReadSlices({ outlines: paths })` (L3175-3262) with water-fill byte budgeting (`computeWaterFillBudgets` L3144-3167). `rules/AGENTS.md` steers bulk reads (>150 lines) back to native parallel `read_file` (~7 ms, 46 KB/call, 0 remote checks). **Important Exo defect `[verified in code]`:** `extractSymbols` (`read-slices.ts:2487-2552`) is hand-rolled regex, **not** tree-sitter. `extractRustSymbols` (L1206-1239) matches `pub const fn` (L1209) but skips `indent > 4` (L1229, missing nested `mod`/`impl` methods). `extractCLikeSymbols` (L1241-1310) rejects `indent > 4` (`^([ \t]{0,4})`, L1244/1249), missing C# methods inside block-scoped namespaces, C# properties, and `=>` expression-bodied members.
- **`run_checks`** (`src/tools/run-checks.ts:80-280`): Runs up to 12 commands in a bounded worker pool (`concurrency` 1..8, default 4) under a 170 s budget, with ANSI stripping and Head-Tail error folding (`output-filter.ts`). On Windows, `exec-utils.ts:306-373` unwraps trivial `powershell -Command "..."` wrappers around allowlisted binaries to avoid PowerShell startup overhead.
- **`SessionStart` snapshot** (`src/session-start.ts`, `src/utils/snapshot.ts`): Hard-capped at `MAX_SNAPSHOT_BYTES = 1500` (`session-start.ts:48`) and `DEFAULT_SCAN_TIMEOUT_MS = 250` (`snapshot.ts:9`); executes 4 hardened read-only git commands concurrently (`--no-optional-locks`, `-c core.fsmonitor=false`, `-c diff.external=`, `-c gpg.*=false`, `snapshot.ts:18-60`) plus a 2-level directory scan with OS/home skip lists (L161-200), returning Antigravity `injectSteps: [{ systemMessage }]` (`session-start.ts:247-250`) and failing open `{}` on error.

### 2.2 Which Code-Intelligence Approach Gives the Biggest Round Reduction for C#, Rust, and TS on Windows?
Across **1,714 C# files**, **996 TS/TSX files**, and **40 Rust files** on Windows under multi-worktree Orca:
1. **Batched direct-spawn `rg` + read-only git (`batch_search`)** gives the single highest immediate round reduction (collapses 3–8 locate/grep turns into 1 call, <100 ms warm on Windows with `rg` 15.2.0, zero daemon RAM).
2. **Accurate multi-file symbol outlining (`quick_outline` fixed for C# brace depth, properties, `=>`, and Rust `impl`/`pub const fn`)** eliminates "read 200 lines just to find line numbers" turns across large C# partials (`WebHostBridge.cs` is 201 KB `[verified on machine]`) without LSP cold-start.
3. **Domain-specific static indexes (Rune API + `host-v1.json`)** beat general code-intelligence servers on Exo because they index external cargo checkout code (`~/.cargo/git/checkouts/rune-*`) and cross-language JSON/TS/C#/Rust RPC contracts that no single LSP covers at once.
4. **Why NOT Serena / LSP MCP:** Running Roslyn (.NET 10) + `rust-analyzer` + `vtsls` per Orca worktree eats multiple GB of RAM and 10–40 s cold-start per worktree, while two 2026 independent benchmarks (`repowise-bench`, Xu arXiv) show neutral-to-worse tool calls (+42%) and token usage (+6% Opus) on frontier models for search/localization. Native `dotnet build`, `cargo check`, and `tsc` already provide exact compiler diagnostics during verification.

### 2.3 Claude Code & Antigravity Local Baseline (`[verified on machine]`)
- **Already optimized on this machine (keep as-is):**
  - Claude Code (`~/.claude/settings.json` & Orca managed profile): `"outputStyle": "Concise"` is active.
  - Antigravity (`~/.gemini/config/config.json`): `autoExecutionPolicy: "CASCADE_COMMANDS_AUTO_EXECUTION_EAGER"`, `permissionPreset: "AGENT_PERMISSION_PRESET_TURBO"`, `enableTerminalSandbox: false`, `nonWorkspaceFileAccessPolicy: "AGENT_SETTING_POLICY_ALLOW"`.
  - Antigravity CLI (`~/.gemini/antigravity-cli/settings.json`): `"verbosity": "low"`, `showFeedbackSurvey: false`, `showTips: false`.
- **What to avoid:**
  - **No per-tool hooks (`PreToolUse`/`PostToolUse`)** in either agent. Claude already executes 15 synchronous `orca-agent-hook.exe` calls per lifecycle; Antigravity already executes 5 `orca-status` `.cmd` hooks plus `context-read-guard` (`node hooks/guard-read-file.mjs` on every `read_file`). Adding a formatter/linter hook or `agy-dart` hooks compounds spawn overhead on Windows. *(Audit note: once `quick_outline` and `batch_search` are live, measure `context-read-guard`'s per-`read_file` Node spawn time; it likely adds 40–90 ms to every native `read_file` call.)*
  - **No large (>15-tool) MCP server schemas:** Keep `exo-agent-kit` to **8 tight tools** (~1.2–1.8 KB JSON schema total) so Antigravity can mark them `"eager": true` and Claude avoids burning extra `ToolSearch` turns or inflating the cached prompt prefix.
  - **No screenshots in long sessions:** Keep `parity-snap` visual comparisons either as text metrics in main chat or inspected in fresh/short visual passes to avoid Antigravity's >180k-token stall cliff.

### 2.4 Orca Workflow Traps & Accelerators (`[verified on machine]`)
Verified via `orca --help`, `orca skills list` (11 bundled skills), `orca worktree create --help`, `orca skills get orchestration --reference references/parallel-start.md`, and `--reference references/recovery-and-cleanup.md`:
1. **Base-branch trap:** `orca worktree create` and `worker-start --worktree new-child` default to the repository's default base branch (`main`) when `--base-branch` is omitted (`orca worktree create --help`: *"omit --base-branch to use the repo default base"*). When orchestrating off a feature branch (e.g., `feat/rune-1to1-visual-parity`), omitting `--base-branch <current-branch>` silently forks child worktrees from stale `main`.
2. **Parallel worker start:** `worker-start` blocks ~10 s per worker for readiness. Launching 3–4 workers sequentially wastes 30–40 s; using `Start-Process` with `--spec-file` and per-worker stdout/stderr receipt files (`references/parallel-start.md`) overlaps readiness waits.
3. **Restart-recovery sequence:** After a breaker trip, abandon, or restart, the exact safe sequence (`references/recovery-and-cleanup.md`) is:
   1. Inspect `worker-list` / `worker-show` / `worker-read`.
   2. Fence the stale attempt: `orca orchestration worker-abandon --dispatch <dispatch_id>` (or `worker-stop` if live; `worker-release` only for failed-start terminals).
   3. Reopen the task: `orca orchestration task-update --id <task_id> --status ready --run <run_id>`.
   4. Dispatch into the existing worktree: `orca orchestration worker-start --task <task_id> --run <run_id> --worktree path:<worktree_path> --agent <agent>` (never `--retry-of` on a tripped breaker; never `--spec`, which duplicates the task).

---

## 3. Confirmed Risks (`file:line` Citations)

1. **`agy-fast` auto-update defaults to `"auto"` from author's GitHub with zero cryptographic verification and executes untrusted downloaded code — CONFIRMED `[verified in code]`**
   - `src/utils/updater-core.ts:7-9`: hardcodes `DEFAULT_REPO_URL = "https://github.com/INK-Q/agy-fast.git"` and `DEFAULT_CHECK_INTERVAL_HOURS = 4` *(note: `README.md:276` erroneously states 12 hours while `README.md:266` and code use 4 hours)*.
   - `src/utils/updater-core.ts:319`: `const mode: AutoUpdateMode = envMode ?? cfgMode ?? "auto";`
   - `src/session-start.ts:171,180-192` + `src/utils/updater-core.ts:987-1008`: SessionStart spawns detached `dist/updater.mjs` (`spawnBackgroundUpdateCheck`).
   - `src/utils/updater-core.ts:476-545,858-878`: queries remote git tags / GitHub Releases API and shallow-clones remote code into a staging directory.
   - `src/utils/updater-core.ts:553-642` (`verifyStagedPlugin`): performs **no GPG/Sigstore/SHA-256 signature verification** and **executes the untrusted `stagingDir/dist/mcp-server.mjs`** (`updater-core.ts:579`) as a smoke test before atomically replacing installed files (`updater-core.ts:906-936`).
2. **`agy-fast` `SessionStart` hook resolves `./plugin.json` + `./dist/session-start.mjs` from CWD before home directory — CONFIRMED `[verified in code]`**
   - `hooks.json:6`:
     `for(var d of[e.PLUGIN_ROOT,e.AGY_PLUGIN_ROOT,'.',p.join('.agents','plugins','agy-fast'),h?p.join(h,'.gemini','config','plugins','agy-fast'):''])`
   - When `PLUGIN_ROOT` and `AGY_PLUGIN_ROOT` are unset, it checks `'.'` (`./plugin.json` + `./dist/session-start.mjs`) and `./.agents/plugins/agy-fast` **before** `~/.gemini/config/plugins/agy-fast`, then runs `import(u.pathToFileURL(s).href)`. Opening any untrusted repository containing those two files achieves immediate code execution at session start.
3. **`agy-dart` inherits the same CWD workspace-execution flaw across ALL 5 lifecycle hooks, plus per-tool spawn & network risks — CONFIRMED `[verified in code]`**
   - `hooks.json:6,16,28,37,44` checks `'.'` and `.agents/plugins/agy-dart` before `~/.gemini/config/plugins/agy-dart` (validating only `plugin.json`'s `name === "agy-dart"`, which an untrusted repo trivially sets).
   - Registers synchronous Node hooks on `PreToolUse` and `PostToolUse` for nearly all read/write/exec tools, compounding latency with Orca's existing hooks.
   - `context/lib/tools.js:4-5` enables outbound `http`/`https` (`ctx_fetch_and_index`), and `config.js:63` enables `ttlKeepalive: true` (extra LLM pings).
4. **`mcollina/mcp-ripgrep` (<=0.4.0) OS command injection — CONFIRMED `[independent advisory]`**
   - CVE-2026-113378: unsanitized `fileType` argument interpolated into shell execution. Our kit MUST spawn `rg`, `git`, `cargo`, `dotnet`, and `npm` directly (`shell: false`, argument arrays only).
5. **Orca `settings-sync.json` does NOT sync Claude user-scope MCP servers — CONFIRMED `[verified on machine]`**
   - `%APPDATA%\orca\claude-profiles\<profile-id>\settings-sync.json` syncs `settings.json` keys (`hooks`, `outputStyle`, `enabledPlugins`, etc.) from `~/.claude/settings.json`, **not** `~/.claude.json` where Claude Code stores user-scope `mcpServers`. Installing an MCP server only into `~/.claude.json` leaves Orca-launched Claude sessions without it.
6. **Orca's 15 synchronous Claude hooks + 5 Antigravity `.cmd` hooks — CONFIRMED `[verified on machine]`**
   - In Claude `settings.json`, 15 of 16 `orca-agent-hook.exe` entries are synchronous (only `UserPromptSubmit` sets `"async": true`). Any extra sync `PreToolUse`/`PostToolUse` hook multiplies per-tool latency.

---

## 4. Ranked Tool List for `exo-agent-kit`

All MCP tools enforce an **8.8 KB hard inline output cap** (head-tail error folding + water-fill budgeting across batch sections), **zero runtime network access**, **direct process spawning (`shell: false`)**, and **read-only git flags** (`--no-optional-locks -c core.fsmonitor=false -c diff.external= -c gpg.program=false`).

| Rank | Tool Name | Source Idea | Expected Gain | How Both Claude Code & Antigravity Call It |
|---|---|---|---|---|
| **1** | `exo_run_gates` | Exo `apps/launcher/AGENTS.md` gates + `agy-fast` `run_checks` worker pool & Head-Tail folding | **Very High** (cuts 3–6 serial build/test/lint turns & 10–40 KB noisy build logs -> 1 concurrent call, <=8.8 KB compact pass/fail + first errors; prevents missed gates and false alarm retries on known env failures). | Maps changed files (auto from `git status` or explicit `paths[]`) to exact gates: `native-ui/**` -> `cargo fmt --check`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked`; `ui/**` -> `npm run check`, `npm run dead`; `mouse/**` -> `npm run check`; `ExoLauncher/**` -> `dotnet build -c Release -p:Platform=x64`, `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64`; `tools/e2e/**` -> `node --test tools/e2e/lib/*.test.mjs`; `shaders/**` -> `dotnet test shaders/engine/tests/ExoShaders.Service.Tests`. Tags known env/flaky issues (e.g., Windows build <22000, missing Playwright Chromium for `crawl`, locked `ExoLauncher.exe`, ReadyToRun incremental publish pitfall). **Antigravity:** eager `mcp_exo-kit_exo_run_gates`. **Claude:** `mcp__exo-kit__exo_run_gates` (coordinator & worker gate verification). |
| **2** | `batch_search` | `agy-fast` `batch_search` + direct-spawn `rg` 15.2.0 (`shell: false`) | **Very High** (collapses 3–8 locate/grep/git-log turns into 1 call; avoids Antigravity's 1.57 s serial safety check per shell search and 10 KB disk spills). | Accepts `queries[]` (max 20: `text`, `regex`, `glob`, `fuzzy`, read-only `git`) run concurrently via `Promise.all` with water-fill byte budgeting. **Antigravity:** eager primary search tool (`mcp_exo-kit_batch_search`). **Claude:** used whenever >=2 searches or git+grep lookups are needed in one turn (native `Grep`/`Glob` still fine for a single lookup). |
| **3** | `exo_contract` | Exo `ui/contracts/host-v1.json` (342 methods, 77 events) + `ui/scripts/host-contract-lib.mjs` `dispatchArms` | **High** (cuts 4–8 cross-language lookup turns across TS, 201 KB `WebHostBridge.cs` + 61 partials, and Rust `exo-rpc` -> 1 instant <50 ms call). | Queries by `method`, `event`, or `prefix`/`search`: returns `host-v1.json` reply/payload TS type, exact request parameter signature parsed from `apps/launcher/ui/src/lib/host.ts`, C# dispatcher arm & handler `file:line` across `apps/launcher/ExoLauncher/*Bridge*.cs` (reusing `host-contract-lib.mjs`'s `dispatchArms` logic), and matching Rust RPC DTO/client `file:line` in `apps/launcher/native-ui/crates/exo-rpc/src/`. **Both agents:** single direct MCP call before touching any bridge/RPC feature. |
| **4** | `exo_rune_api` | Exo pinned Rune checkout (`native-ui/Cargo.toml` rev `1735bc2` -> `4,774 pub fn` + `118 pub const fn` + `3 pub async fn` + `5 pub unsafe fn`) | **High** (eliminates multi-turn blind searches in `%USERPROFILE%\.cargo\git\checkouts\rune-*` and prevents false-negative misses like `pub const fn tracking` at `crates/rune-core/src/tok.rs:108`). | Resolves the active worktree's pinned Rune rev from `apps/launcher/native-ui/Cargo.toml`/`Cargo.lock`, parses every `pub [const\|async\|unsafe] fn`, `pub struct`, `pub enum`, `pub trait`, and `impl` block across all Rune crates into a rev-keyed local JSON cache (zero network), and supports exact/substring/type-owner search returning full signatures, doc summaries, and `file:line`. **Both agents:** single call before claiming any Rune widget/style/token capability or absence. |
| **5** | `quick_outline` | `agy-fast` `quick_outline` + `treesitter-mcp` accuracy, fixing `agy-fast`'s 4-space indent bug for C# and Rust | **High** (replaces 3–6 speculative `read_file` turns on huge C#/TS/Rust files with 1 call returning exact symbol line ranges; directly feeds targeted ranged `read_file`/`Read`). | Accepts `paths[]` (max 20) + optional `filter` regex. Uses stateful brace-depth scanning (or optional pinned `web-tree-sitter` WASM in Phase 2) so C# block-scoped namespaces, C# properties, `=>` expression-bodied members, nested Rust `mod`/`impl`/`pub const fn`, and TS/TSX exports/hooks are all captured with `[startLine..endLine]`. **Both agents:** call on unfamiliar files >200 lines, then use native parallel `read_file`/`Read` with exact `StartLine`/`EndLine`. |
| **6** | `exo_snapshot` (+ `SessionStart` hook) | `agy-fast` `SessionStart` snapshot + Exo skill/rule router + Orca base-branch guard | **Medium-High** (saves 2–4 cold-start orientation turns per session; prevents Orca `main` base-branch mis-forks and missed mandatory skills like `native-ui` / `exo-design`). | Runs at `SessionStart` (<100 ms, <=1.5 KB) AND callable on demand as `exo_snapshot`. Emits: active branch + explicit `Orca: pass --base-branch <branch>` reminder when not on `main`, dirty files, last 3 commits, active repo areas (`native-ui`, `ui`, `ExoLauncher`, `mouse`, `shaders`), and exact applicable skills/rules (`apps/launcher/AGENTS.md`, `native-ui`, `exo-design`, `impeccable`, `delete-comment`, `orchestration`). **Antigravity:** injected via `SessionStart` `injectSteps` (absolute path, zero CWD lookup). **Claude:** injected via `SessionStart` stdout into cached prefix. |
| **7** | `exo_parity_snap` | Local `parity-snap.ps1` + `parity-compare.py` (currently hardcoded in temp scratchpad to `C:\Users\Erix\ExoRebuild\...` and a hardcoded brain refs dir `[verified on machine]`) | **Medium-High** (turns ad-hoc multi-step build + pipe host + capture + Python diff into 1 reproducible call; keeps PNGs out of long main-session context to avoid the 3–7x TTFT cliff). | Parameterizes `scenes[]` (from the 12 named scenes: `library`, `library-all`, `game`, `store`, `discord`, `music`, `pc-devices`, `pc`, `pc-apps`, `settings`, `captures`, `palette`), `worktreeRoot`, `hostExe` (auto-detected from worktree or fallback env var `EXO_HOST_EXE`), `refsDir` (from `EXO_PARITY_REFS` or arg), `outDir`, and `noBuild`. Builds `exo-ui --locked`, starts isolated temp named pipe `exo-parity-<8hex>`, captures, runs `parity-compare.py`, guarantees host cleanup in `finally`, and returns compact text diff scores + worst 8×6 grid cells + output PNG paths. **Both agents:** call directly on visual parity tasks; only open PNGs when scores demand visual inspection. |
| **8** | `apply_edits` | `agy-fast` `apply_edits` (atomic/`bestEffort` multi-file hunks + optional inline `verify`) | **High on Antigravity / Low-Medium on Claude** (eliminates Antigravity's serial 1.57 s safety checks across multi-file edits and collapses edit+verify into 1 round; Claude already has fast parallel built-in `Edit`). | Accepts up to 50 file edit specs (array or JSON-stringified array), validates all hunks in memory, preserves CRLF/encoding, supports `bestEffort` with closest-line mismatch hints, and optionally runs a single compact `verify` command (via direct spawn, no shell). **Antigravity:** eager primary tool for >=2 files or multi-hunk refactors. **Claude:** optional (prefer built-in `Edit` for normal edits; use `apply_edits` only for large atomic multi-file hunks). |
| *9 (Bonus)* | `orca_dispatch_helper` *(CLI script, not MCP)* | Orca `references/parallel-start.md` + `references/recovery-and-cleanup.md` + base-branch guard | **Medium (Orchestrator only)** (saves ~20–30 s per 4-worker wave and eliminates manual PowerShell quoting & recovery mistakes). | Small local Node/PS1 CLI wrapper (`exo-orca-wave` / `exo-orca-recover`) invoked by Claude coordinator via shell: (a) launches up to 4 workers concurrently via `Start-Process` with `--spec-file` + receipt logs and **auto-injects `--base-branch <current-branch>`** unless `--base-branch` is explicitly set; (b) executes the exact recovery sequence (`worker-abandon` -> `task-update --status ready` -> `worker-start --task --worktree path:<path>`). Keeping it as a CLI avoids adding a 9th MCP tool to Antigravity workers (which never orchestrate). |

---

## 5. Architecture & Packaging Design

### 5.1 Language Choice: TypeScript + `esbuild` Single-File ESM vs. Rust
**Recommendation: TypeScript compiled with `esbuild` into 2 zero-dependency Node.js ESM bundles (`dist/mcp-server.mjs`, `dist/session-start.mjs`)** targeting the already-installed Node runtime (`node v26.13.0` `[verified on machine]`).

| Criterion | TypeScript + `esbuild` ESM (Recommended) | Rust Single Binary |
|---|---|---|
| **Runtime startup** | ~30–50 ms on Windows (MCP server stays resident over stdio; `SessionStart` runs once/session). | ~5–15 ms. |
| **Build & iteration speed** | `esbuild` bundles in <200 ms; reusing `apps/launcher/ui/scripts/host-contract-lib.mjs` logic is trivial (same language!). | Full `cargo build --release` + `tree-sitter` C compiler toolchain (`cl.exe`/LLVM) per change. |
| **Dependencies & offline pinning** | **Zero runtime dependencies** (Node 22+ built-ins: `node:fs`, `node:child_process`, `node:path`, `node:readline`, and `node:sqlite` if FTS5 is ever added). Only 3 devDeps (`typescript`, `esbuild`, `@types/node`) pinned via `package-lock.json`. | Many crates (`rmcp`/`serde`/`tokio`/`tree-sitter-*`) to vendor/audit. |
| **Windows Defender / Smart App Control** | Runs via trusted signed `node.exe` — zero Smart App Control blocks (unlike unsigned custom `.exe` binaries such as Serena's #2196). | Unsigned custom `.exe` can trigger Defender/Smart App Control scans on rebuild. |
| **When to reconsider Rust** | Only if Phase 2 requires indexing >10,000 files with real-time PageRank+BM25 (like `SylphxAI/repomap`). At Exo's scale (2,750 files), `rg` 15.2.0 + in-memory TS scanners finish in <100 ms. | Unnecessary complexity for Phase 1. |

### 5.2 Hard Security & Operational Guarantees
1. **Zero runtime network access:** No `fetch`, `http`, `https`, `net`, `tls`, `dns`, or `git fetch`/`git ls-remote` imports anywhere in `src/`. Enforce with a static build check (grep/AST assertion failing the build if any network module or `shell: true` appears in `dist/`).
2. **Zero auto-update:** No updater script, no version check, no background detached process. Updates happen only when the owner explicitly pulls and runs `npm run build && node scripts/install.mjs`.
3. **Zero CWD workspace code execution:** Every hook command in Claude and Antigravity uses a **hardcoded absolute path** to the installed bundle (e.g., `node C:/Users/Erix/.exo-agent-kit/dist/session-start.mjs`). Never check `.`, `./plugin.json`, or `.agents/plugins/...`.
4. **No shell interpolation (`shell: false`):** All subprocesses (`rg`, `git`, `cargo`, `dotnet`, `npm`, `node`, `python`) are invoked with `execFile`/`spawn(..., { shell: false })` and explicit argument arrays. Git calls hardcode read-only subcommands and `--no-optional-locks -c core.fsmonitor=false -c diff.external= -c gpg.program=false`.
5. **Strict output budgeting:** Every MCP tool caps JSON-serialized text payloads at `<= 8,800` bytes (configurable lower via `maxBytes`), preventing Antigravity disk spills and Claude context bloat.

### 5.3 Where It Lives
- **Source repository (owned & pinned by us):** Either a dedicated local repo (`C:\Users\Erix\exo-agent-kit`, clean separation so it works across all repos & worktrees) OR `tools/exo-agent-kit/` inside Exo Launcher (pinned to commit history). **Recommended: standalone repo `C:\Users\Erix\exo-agent-kit`** with Exo-specific modules enabled automatically when `apps/launcher/ui/contracts/host-v1.json` is detected in the active workspace root, because both Claude and Antigravity register user-scope MCP servers once globally and work across multiple Orca worktrees simultaneously.
- **Installed runtime artifact:** `%USERPROFILE%\.exo-agent-kit\` containing:
  ```text
  %USERPROFILE%\.exo-agent-kit\
    dist\
      mcp-server.mjs        # Single-file stdio MCP server (8 tools)
      session-start.mjs     # Single-file SessionStart snapshot (<=1.5 KB)
      exo-orca-helper.mjs   # Optional CLI for parallel wave start & recovery
    cache\                  # Rev-keyed Rune API JSON cache (offline, disposable)
    VERSION                 # Local git SHA written by build/install script
  ```

---

## 6. Install Steps (Claude Code & Antigravity)

> **Prerequisite:** Build and copy artifacts to `%USERPROFILE%\.exo-agent-kit\dist\` (`npm ci --offline && npm run build`). Never install via `npx` or GitHub URLs.

### 6.1 Claude Code (Both `~/.claude` AND Orca's Managed Claude Home)
Because Orca's `settings-sync.json` syncs `settings.json` (hooks, outputStyle) from `~/.claude/settings.json` to `%APPDATA%\orca\claude-profiles\<profile-id>\home\settings.json`, **but does NOT sync `.claude.json`** (`mcpServers`) `[verified on machine]`, the MCP registration must be written to **both** `.claude.json` files:

1. **Register user-scope MCP in `~/.claude.json` AND every Orca managed profile's `home/.claude.json`:**
   Add under `"mcpServers"` (using a small idempotent `install.mjs` script, or `claude mcp add` against each `CLAUDE_CONFIG_DIR`):
   ```json
   "exo-kit": {
     "type": "stdio",
     "command": "node",
     "args": ["C:/Users/Erix/.exo-agent-kit/dist/mcp-server.mjs"],
     "env": {
       "EXO_PARITY_REFS": "C:/Users/Erix/.exo-agent-kit/refs",
       "EXO_KIT_NO_NETWORK": "1"
     }
   }
   ```
   Target files on this machine:
   - `%USERPROFILE%\.claude.json`
   - `%APPDATA%\orca\claude-profiles\<profile-id>\home\.claude.json` (discover `<profile-id>` dynamically via `Get-ChildItem "$env:APPDATA\orca\claude-profiles" -Directory`).
2. **Add the `SessionStart` hook ONLY to `~/.claude/settings.json`** (Orca syncs `hooks` into the managed profile automatically; do **not** add any `PreToolUse` or `PostToolUse` hooks):
   ```json
   "SessionStart": [
     {
       "hooks": [
         {
           "type": "command",
           "command": "node C:/Users/Erix/.exo-agent-kit/dist/session-start.mjs --agent claude",
           "timeout": 5
         }
       ]
     }
   ]
   ```
   *(Keep existing `orca-agent-hook.exe` entries untouched; append this as an additional entry in `SessionStart`.)*
3. **Add concise tool-routing rules** to `~/.claude/CLAUDE.md` (or repo `AGENTS.md`): use `exo_contract` before editing bridge/RPC code, `exo_rune_api` before claiming Rune capabilities, `exo_run_gates` for verification, `batch_search` for multi-query exploration, `quick_outline` before reading >200-line files, and keep native `Read`/`Edit` for targeted reads and single edits.

### 6.2 Google Antigravity (Plugin Layout — Recommended)
Install as a pinned local plugin under `%USERPROFILE%\.gemini\config\plugins\exo-agent-kit\` (matching the 7 existing plugins verified in `~/.gemini/config/plugins/` `[verified on machine]`):

1. **`%USERPROFILE%\.gemini\config\plugins\exo-agent-kit\plugin.json`:**
   ```json
   {
     "name": "exo-agent-kit",
     "version": "1.0.0",
     "description": "Pinned offline speed & Exo Launcher domain toolkit"
   }
   ```
2. **`%USERPROFILE%\.gemini\config\plugins\exo-agent-kit\mcp_config.json`** (marks all 8 tools `"eager": true` so Antigravity skips lazy schema-lookup turns):
   ```json
   {
     "mcpServers": {
       "exo-kit": {
         "command": "node",
         "args": ["C:/Users/Erix/.exo-agent-kit/dist/mcp-server.mjs"],
         "env": {
           "EXO_PARITY_REFS": "C:/Users/Erix/.exo-agent-kit/refs",
           "EXO_KIT_NO_NETWORK": "1"
         },
         "tools": {
           "exo_run_gates": { "eager": true },
           "batch_search": { "eager": true },
           "exo_contract": { "eager": true },
           "exo_rune_api": { "eager": true },
           "quick_outline": { "eager": true },
           "exo_snapshot": { "eager": true },
           "exo_parity_snap": { "eager": true },
           "apply_edits": { "eager": true }
         }
       }
     }
   }
   ```
   *(Alternative if not using a plugin: place the same `"exo-kit"` block directly in `%USERPROFILE%\.gemini\config\mcp_config.json`, which is currently empty `[verified on machine]`.)*
3. **`%USERPROFILE%\.gemini\config\plugins\exo-agent-kit\hooks.json`** (hardcoded absolute path — **zero CWD resolution**, **SessionStart only**):
   ```json
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
   ```
4. **`%USERPROFILE%\.gemini\config\plugins\exo-agent-kit\rules\exo-kit.md`** (must live inside `rules/*.md`, NOT plugin root): concise routing table mirroring Claude's rules, plus: use `apply_edits` for >=2 files or multi-hunk edits, `batch_search` for all searches, native parallel `read_file` with `StartLine`/`EndLine` for targeted slices, and never inspect parity PNGs inside a >150k-token session.
5. **Verify existing Antigravity settings remain optimal:** Keep `autoExecutionPolicy: EAGER`, `permissionPreset: TURBO`, `enableTerminalSandbox: false`, `verbosity: low`. Benchmark whether `~/.gemini/config/hooks.json`'s `context-read-guard` (`node hooks/guard-read-file.mjs`) is still worth its per-`read_file` Node spawn overhead once `quick_outline` is installed.

---

## 7. Benchmark Plan (Proving Gain on One Real Exo Task)

### 7.1 Concrete Benchmark Task (Cross-Stack Exo Task)
Pick a deterministic, repeatable, medium-complexity task pinned to a fixed base commit (e.g. `HEAD` of `feat/rune-1to1-visual-parity` or `main` in fresh disposable worktrees):
> **Task:** *"Add a new read-only host RPC method `settings.getRuntimeSummary` returning `{ hostVersion: string; nativeUiReady: boolean; activeRoom: string }` across `apps/launcher/ui/contracts/host-v1.json`, `apps/launcher/ui/src/lib/host.ts`, `apps/launcher/ExoLauncher/WebHostBridge*.cs`, and `apps/launcher/native-ui/crates/exo-rpc/src/`, wire a tracked uppercase section label in `apps/launcher/native-ui/crates/exo-ui/src/rooms/settings.rs` using Rune's `tracking(...)` text style, and pass all relevant `cargo`, `npm`, and `dotnet` gates."*

Why this task is ideal: it hits **every** Exo bottleneck in one realistic feature — C# bridge discovery (`WebHostBridge*.cs`), `host-v1.json` contract alignment, Rune `pub const fn tracking` lookup, multi-file C#/TS/Rust edits, and 3-stack gate verification.

### 7.2 Experimental Protocol (A/B Paired Runs)
- **Matrix (4 cells × 3 trials = 12 runs, all from the exact same base SHA in clean worktrees):**
  1. **Antigravity (Gemini 4 Neon XHigh) — Baseline** (current setup, `exo-kit` disabled).
  2. **Antigravity (Gemini 4 Neon XHigh) — With `exo-agent-kit`** (plugin enabled, all 8 tools eager + `SessionStart`).
  3. **Claude Code (Opus) — Baseline** (current setup, `exo-kit` disabled).
  4. **Claude Code (Opus) — With `exo-agent-kit`** (MCP + `SessionStart` enabled).
- **Controls:** Identical prompt text, warm Cargo/NuGet/npm build caches (pre-warmed once before Trial 1 so cold downloads don't skew Trial 1), no human hints, hard 15-minute timeout per trial, fresh session per trial (no cross-trial cache leakage).

### 7.3 Metrics to Extract Offline from Transcripts & Git
Parse Antigravity `transcript.jsonl` (`%APPDATA%\orca\antigravity-chat\...\transcript.jsonl`) and Claude Code session JSONL (`~/.claude/projects/.../*.jsonl` / Orca profile equivalent) with a small offline script (inspired by `agy-fast`'s `benchmark.mjs`):
1. **Correctness (pass/fail gate):** Independent post-run execution of `npm run check`, `cargo fmt --check`, `cargo clippy --workspace --locked`, `cargo test --workspace --locked`, and `dotnet build -c Release -p:Platform=x64` — must be 100% green and genuinely use Rune `tracking(...)`.
2. **Primary speed metric:** Median **LLM turns** and **total wall-clock seconds** to green gates.
3. **Token & cost metric:** Total input tokens, uncached input tokens, cache-read hit ratio (`cache_read / total_input`), output tokens, and total tool-return bytes uploaded.
4. **Reliability & friction metrics:** Number of disk spills (>10 KB tool outputs in Antigravity), number of wrong-API / false-negative claims (e.g., missing `pub const fn tracking`), number of failed gate retries, and median per-turn latency.

### 7.4 Success Criteria to Ship v1
- **Antigravity:** >= **30% reduction** in median LLM turns AND >= **25% reduction** in wall-clock time, with **0** disk spills and **0** Rune/contract false-negative misses.
- **Claude Code:** >= **20% reduction** in median LLM turns (or >= **20% reduction** in uncached input + output tokens) on the cross-stack task, with **0** per-tool hook latency regression.
- **Safety gate:** Static scan of `dist/*.mjs` confirms **0** network imports, **0** `shell: true` calls, **0** CWD hook paths, and **0** auto-update logic.

---

## 8. Sources & Verification Index

- **Cloned & audited in code (`[verified in code]`):**
  - `INK-Q/agy-fast` v0.2.10 (MIT): `package.json:56`, `hooks.json:6`, `rules/AGENTS.md`, `docs/ANALYSIS_REPORT.md`, `src/session-start.ts:48,171,180-192,247-250`, `src/utils/snapshot.ts:9,18-60,161-200`, `src/utils/output-filter.ts:33`, `src/utils/exec-utils.ts:150,306-373`, `src/utils/updater-core.ts:7-9,319,476-642,858-936,987-1008`, `src/tools/batch-search.ts:50-77,243-260,1482-1581`, `src/tools/apply-edits.ts:539-991`, `src/tools/read-slices.ts:1206-1310,2487-2552,2800-3262`, `src/tools/run-checks.ts:80-280`.
  - `zou172866/agy-dart` v1.6.0 (MIT): `hooks.json:6,16,28,37,44`, `config.js:63`, `context/lib/store.js:3`, `context/lib/store/fts.js:77`, `context/lib/tools.js:4-5,111`, `context/lib/watchdog.js`.
  - `oil-oil/agy-fast-io` (MIT): inspected via GitHub REST API (`plugin/plugin.json`, `plugin/rules/AGENTS.md`).
  - `keli-wen/agy-staff` v0.9.3 (MIT): `README.md`, `bench/README.md`.
- **Exo Launcher & local environment (`[verified on machine]`):**
  - Repo: `apps/launcher/AGENTS.md`, `.agents/skills/native-ui/SKILL.md`, `apps/launcher/ui/contracts/host-v1.json` (342 methods, 77 events), `apps/launcher/ui/src/lib/host.ts`, `apps/launcher/ui/scripts/host-contract-lib.mjs`, `apps/launcher/native-ui/Cargo.toml`, `apps/launcher/ExoLauncher/*Bridge*.cs` (62 files; `WebHostBridge.cs` 201,396 bytes).
  - Pinned Rune checkout: `%USERPROFILE%\.cargo\git\checkouts\rune-2074f851e60eb09a\1735bc2` (`crates/rune-core/src/tok.rs:108` `pub const fn tracking`).
  - Parity scratchpad scripts: `parity-snap.ps1` (lines 6, 14, 18-31) & `parity-compare.py`.
  - Local configs & CLI: `~/.claude/settings.json`, `~/.claude.json`, `%APPDATA%\orca\claude-profiles\<id>\settings-sync.json` & `home/settings.json`, `~/.gemini/config/{config.json,hooks.json,mcp_config.json,GEMINI.md,plugins/}`, `~/.gemini/antigravity-cli/settings.json`, `orca --help`, `orca skills list`, `orca worktree create --help`, `orca skills get orchestration` (`references/parallel-start.md`, `references/recovery-and-cleanup.md`).
- **External references (`[README/docs claim]` & `[independent benchmark]`):**
  - `oraios/serena` (https://github.com/oraios/serena; issue #2196 Windows Smart App Control), `repowise-bench` (2026), Xu et al. LSP-vs-agent arXiv study (June 2026).
  - `ast-grep/ast-grep-mcp` (https://github.com/ast-grep/ast-grep-mcp).
  - `mcollina/mcp-ripgrep` (https://github.com/mcollina/mcp-ripgrep; CVE-2026-113378), `shk1447/ripgrep-mcp`, `superauu/Auu.Mcp.Rg`.
  - `SylphxAI/repomap` (https://github.com/SylphxAI/repomap), `Christoph/treesitter-mcp` (https://github.com/Christoph/treesitter-mcp), `pdavis68/RepoMapper`, `yamadashy/repomix` (https://github.com/yamadashy/repomix; Nov 2026 compression eval).
  - Claude Code docs (`hooks`, `ToolSearch` 46.9% schema token reduction) & Antigravity plugin/MCP docs.
