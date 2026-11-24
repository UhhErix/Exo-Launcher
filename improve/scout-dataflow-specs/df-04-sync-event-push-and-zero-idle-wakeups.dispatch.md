EXO DATAFLOW: Event-Driven Account Sync (DO Push-Wake) & Unified Game-Aware Wakeup Coalescer (task-name: df-04-sync-push-and-wakeups)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

Exo's vision is the best performance from every part of every PC — which means **near-zero CPU wakeups, zero idle SSD wear, and zero background network chatter** while gaming or idle, while delivering instant (<150 ms) sync across a player's PCs.

## Today

- `ExoLauncher/Services/Account/AccountSyncHost.cs:89-91` runs a 30-second unconditional timer (`SyncEvery = TimeSpan.FromSeconds(30)`) that sends `GET https://accounts.getexo.app/v1/sync?after=<cursor>` **2,880 times/day per PC** — even while a game is running, even when minimized, and even when the user only has 1 PC!
- `AccountSyncHost.cs:156` calls `AccountSyncStore.Save(dataDir, engine.State)` on **every single 30-second tick**, rewriting the entire **106,754-byte `account-sync.json`** atomically to SSD **~2,880 times/day (~300 MB/day SSD wear)** solely to update `LastSyncAt`!
- `cloud/account/src/worker.js:384-390` executes `sessionOf` + `recordOf` + WebCrypto AES `itemKeyOf` unwrap + `SELECT ... FROM items` on every 30-second poll even when `record.seq <= after` (zero new items!).
- `ExoLauncher/Helpers/Telemetry.cs:32, 290, 356, 369` wakes every 1 minute (instead of 5/10 min) and rewrites `telemetry\queue.jsonl` on every tick because `finally { PersistQueue(); }` executes on early return!
- Across `ExoLauncher/`, **61 independent `Timer`/`PeriodicTimer` constructions** wake the host at uncoordinated intervals (including `DiscordSession.Settings.cs:298` 30 ms keyboard polling and `WebHostBridge.SteamOffline.cs:53` 15 s file reads).

## 1. Benchmark first, with real numbers

- Measure on this PC over a 15-minute idle session and a 15-minute simulated-game session:
  - Total HTTP requests/hour to `accounts.getexo.app`, total D1 queries simulated/executed, total SSD bytes written to `%LOCALAPPDATA%\ExoLauncher` (`ProcMon` / `FileSystemWatcher` + counter), and host CPU wakeups/minute.
  - Cross-PC sync propagation latency (PUT on PC A → applied on PC B): baseline ~15 s avg / 30 s max vs DO push-wake.
- **Targets:** Idle account sync HTTP requests **2,880/day → <10/day (-99.6%)**, idle SSD writes **~300 MB/day → <1 MB/day**, cross-PC sync latency **30 s → <150 ms**, background timer wakeups while gaming **<1/min**.

## 2. Architecture & implementation

1. **Zero-Waste Server Short-Circuit (`cloud/account/src/worker.js`):**
   - In `getItems`, check `record.seq <= after` BEFORE `itemKeyOf` and `pageAfter`: return `304 Not Modified` if `If-None-Match: "seq-<seq>"` matches, else `{ items: [], cursor: after, more: false }` with `ETag: "seq-<seq>"`.
   - Return `deviceCount` on `GET /v1/account` and sync replies so single-PC installs know they have no other devices to sync against!
2. **Cloudflare Durable Object WebSocket Hibernation Push-Wake (`cloud/account`):**
   - Reuse the exact WebSocket Hibernation DO architecture already battle-tested in `relay/src/worker.js`!
   - When `deviceCount > 1` and Exo is not idle/suspended, maintain a hibernated WebSocket to `/v1/sync/watch` (0 compute cost while quiet!).
   - When `landItems()` commits a new `seq` in D1, notify the account's DO to broadcast a 16-byte `{ seq }` wake frame to other connected devices → instant pull in **<150 ms**!
   - Fallback adaptive polling when socket disconnected: 5 min while active, 30 min while idle, **paused while `GameRunningSignal.Running`**, immediate pull on app launch / window foreground after >2 min away.
3. **Dirty-Bit Persistence Guard (`AccountSyncHost.cs` + `Telemetry.cs`):**
   - Keep `LastSyncAt` in memory (or write only when `Cursor`, `Dirty`, `Items`, `Held`, or `Unapplied` actually mutated).
   - Fix `Telemetry.cs` (`PerfEvery = 10 min`, dirty-flag before `PersistQueue()`, 5-min timer).
4. **Unified Game-Aware Coalesced Wakeup Scheduler (`Helpers/ExoScheduler.cs`):**
   - Consolidate periodic non-realtime background tasks (`AccountSync`, `Telemetry`, `DownloadPolicy`, `SteamOffline`, `SteamClient`, `VersionWatch`, `ShaderPrepare`) onto a single coalesced clock with explicit `GamingBehavior` (`PauseWhileGaming`, `SlowWhileGaming`, `Always`).
   - Replace `DiscordSession.Settings.cs:298` 30 ms `KeyTick` timer with `WM_INPUT` raw keyboard input notifications (**0 Hz** idle wakeups in calls!).

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64`
- `npm test` in `apps/launcher/cloud/account` (add workerd tests for `seq` short-circuit, `304`, and DO push-wake; read-only on production — never deploy!).

## 4. Deliver

- Branch `lab/df-04-sync-push-and-wakeups` from `origin/main`.
- `improve/df-04-sync-push-and-wakeups-report.md` with before/after idle HTTP calls, D1 query counts, SSD bytes written, wakeup rate, and cross-PC sync latency.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-04 DO sync push & coalesced wakeups" --body "<HTTP/SSD/wakeup reductions, cross-PC latency, test results>"`. Effort: high.
