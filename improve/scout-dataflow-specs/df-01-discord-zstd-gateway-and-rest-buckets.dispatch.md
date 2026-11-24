EXO DATAFLOW: Streaming Zstd Discord Gateway, Proactive REST Rate-Limit Buckets & 0-ms Channel Prefetch (task-name: df-01-discord-zstd-gateway)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

Exo's own Discord client (`Services/DiscordClient/`) is judged against Discord Desktop (Electron) and must beat it on cold-connect speed, wire bytes, and channel-switch responsiveness across both 3.x and v4 Rune.

## Today

- `ExoLauncher/Services/DiscordClient/DiscordSession.cs:370` connects to `wss://gateway.discord.gg/?encoding=json&v=9` with **zero compression** (requiring a 256 MB receive limit at `:373`). Every cold READY payload for a multi-server user transfers 3–15 MB of uncompressed JSON.
- `ExoLauncher/Services/DiscordClient/DiscordRest.cs:730-736` has **no proactive rate-limit bucket tracking** (`X-RateLimit-Bucket`, `X-RateLimit-Remaining`, `X-RateLimit-Reset-After`); it only backs off *after* hitting HTTP 429. `app.log` on this PC recorded 63 slow REST calls (`>=700 ms`).
- `DiscordRest.cs:116` (`LoadBuildAsync`) builds a per-call `ExoHttp.Create(20s)` with **no `AutomaticDecompression`**, downloading up to 8 uncompressed multi-MB JS bundles.
- `ZstdSharp.Port 0.8.8` is **already shipped** in `ExoLauncher/ExoLauncher.csproj:74` — zero new dependencies needed.

## 1. Benchmark first, with real numbers

- Capture baseline vs upgraded metrics using unit/integration tests and a live test harness (never log tokens or message text):
  - Cold gateway READY wire bytes, decompressed bytes, and wall-clock time-to-READY (`/?encoding=json&v=9` vs `/?encoding=json&v=9&compress=zstd-stream`).
  - 10-minute live gateway traffic bytes (uncompressed vs `zstd-stream`).
  - Burst REST suite (30 concurrent channel/reaction/message metadata calls against a mock/replay harness): 429 count, total completion ms, p95 latency.
  - `LoadBuildAsync` wire bytes with vs without `DecompressionMethods.All`.
- **Targets:** Gateway wire bytes **-85% to -92%**, cold READY latency **3–5× faster**, burst unexpected 429s **→ 0**, `LoadBuildAsync` wire bytes **-70%+**, cached channel switch **<1 ms**.

## 2. Build the improvements

1. **Streaming Zstd Gateway (`DiscordSession.cs`):**
   - Connect with `/?encoding=json&v=9&compress=zstd-stream`.
   - Maintain a persistent `ZstdSharp.Decompressor` / streaming `DecompressionStream` per gateway connection (reset on reconnect/new session; `Zstd` frames flush at the end of each binary WebSocket message).
   - Parse directly from pooled UTF-8 byte buffer (`ArrayPool<byte>`) into `JsonDocument.Parse(ReadOnlyMemory<byte>)` — zero UTF-16 intermediate string.
   - Automatic fallback to uncompressed JSON if Discord ever closes with decode error (`4002`), logged once.
2. **Proactive REST Rate-Limit Bucket Scheduler (`DiscordRest.cs`):**
   - Parse `X-RateLimit-Bucket`, `X-RateLimit-Remaining`, `X-RateLimit-Reset-After`, and `X-RateLimit-Global` on every response.
   - Map normalized route (`method + major param channel/guild/webhook`) → bucket ID, pacing requests smoothly before hitting 0 remaining so bursty UI navigation never triggers 429s or anti-abuse flags.
   - Fix `LoadBuildAsync` (`DiscordRest.cs:116`) to use `AutomaticDecompression = DecompressionMethods.All` and a shared handler.
3. **Predictive Channel Ring Cache + Hover Prefetch:**
   - Keep a bounded LRU ring (last 12 opened channels, updated live by gateway `MESSAGE_CREATE`/`UPDATE`/`DELETE`) in `DiscordCache` so switching back to a recently viewed channel answers `discord.messages` in **<1 ms / 0 RTT** while validating in the background only if the gateway disconnected.
4. **Optional Phase 2 (research spike):** Evaluate `encoding=etf` (Erlang Term Format) + `compress=zstd-stream` decoder on synthetic READY/MESSAGE_CREATE fixtures to quantify CPU savings vs JSON.

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64`
- `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (add unit tests covering zstd streaming frame boundaries, corrupted frame fallback, bucket pacing, global rate-limit pause, and cache invalidation on gateway drop).
- `npm run check` in `ui/` if any UI hook changes.

## 4. Deliver

- Branch `lab/df-01-discord-zstd-gateway` from `origin/main`.
- `improve/df-01-discord-zstd-gateway-report.md` with before/after measurements (READY bytes, READY ms, 10-min bytes, 429 counts, `LoadBuildAsync` bytes).
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-01 discord zstd gateway & buckets" --body "<before/after bytes & READY ms, 429 count, test status>"`. Effort: medium.
