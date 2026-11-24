EXO DATAFLOW: Privacy-Preserving On-Device Sketch Telemetry (DDSketch Histograms + Rollups + Brotli) (task-name: df-07-on-device-sketch-telemetry)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

Exo sends always-on anonymous telemetry (`docs/decisions/2026-11-09-telemetry-always-on.md`, `PRIVACY.md`). Today it ships a noisy stream of individual timestamped events that wastes 95% of wire bytes and D1 SQLite rows while giving zero latency percentiles. Replacing raw event streams with **on-device aggregated sketches** gives **10× better performance telemetry** with **95% less data** and **strictly stronger privacy (zero user behavior sequence leaves the PC)**.

## Today (Measured on This PC)

- `ExoLauncher/Helpers/Telemetry.cs` recorded **1,016 lines (258,130 B; avg 254.1 B/event)** in `%LOCALAPPDATA%\ExoLauncher\telemetry\sent.jsonl`.
- **736 of 1,016 events (72.4%)** are repetitive `room.opened` events, each repeating the full 180-byte JSON envelope (`schema`, `app`, `version`, `channel`, `install_id`, `session_id`, `ts`).
- **Active bugs:** `PerfEvery = TimeSpan.FromMinutes(10)` (`Telemetry.cs:32`) is unused; `_ticks` (`:290`) runs every 1 minute calling `SampleMemory()` **10× more often** than `PRIVACY.md` promises, AND `FlushAsync` (`:356, 369`) executes `finally { PersistQueue(); }` rewriting `queue.jsonl` every 60 s even when nothing changed!
- `Telemetry.cs:453` constructs a new `HttpClient` per POST, sends uncompressed JSON (real 64 KB slice compresses to **3,947 B = 6.0%** with Brotli!), and `Telemetry.cs:99` dynamically compiles a `Regex` for `machineName` on every string prop!
- `cloud/reports/src/telemetry-store.js` stores every raw event row in D1 SQLite for 90 days before rolling up to `aggregates`.

## 1. Benchmark first, with real numbers

- Replay a scrubbed snapshot of this PC's `telemetry/sent.jsonl` (1,016 events, 258 KB) through:
  - Current v1 raw JSON batching vs
  - v2 On-Device Rollup + DDSketch (relative accuracy α = 0.02, 2% quantile error) + Brotli POST.
- Measure exact payload bytes, D1 row writes, merge accuracy (compare true p50/p90/p95/p99 against merged DDSketch quantiles across 100 synthetic clients), and client CPU/disk I/O over a 1-hour session.
- **Targets:** Wire bytes **-95%**, D1 row writes **-95%**, quantile accuracy **≤2% relative error on p50/p95/p99**, per-action timestamps sent **→ 0**, idle disk writes **60/hr → 0/hr**.

## 2. Architecture & implementation

1. **Fix Immediate `Telemetry.cs` Bugs:**
   - Honor `PerfEvery = 10 min`, gate `PersistQueue()` behind a dirty bit, move timer to 5 min (or `ExoScheduler`), use shared `ExoHttp.Strict`, precompile the `machineName` scrub regex once, and skip regex scrub on static allowlisted enum tokens.
2. **On-Device Rollup + DDSketch Engine (`Helpers/TelemetrySketch.cs`):**
   - Pure ~80-line DDSketch implementation (bucket index `floor(log_gamma(x))`, sparse `Dictionary<int, int>` bin counts, exact associative merge `A + B`).
   - Accumulate per session / 15-minute window on-device:
     - **Counts:** `rooms: { library: N, discord: N, ... }`, `actions: { launch: N, ... }`, `errors: { ... }`.
     - **Sketches (true tail latency!):** `startup_ms`, `library_load_ms`, `rpc_ms`, `store_home_ms`, `discord_ready_ms`, `update_mbps`, `memory_mb` (sampled every 10 min!).
   - Emit ONE compact `schema: 2` rollup summary per flush/session-exit (zero per-click timestamps, zero sequence order!).
   - Compress outbound body with `BrotliStream` (`Content-Encoding: br`, fallback gzip/plain).
   - Keep `View what's sent` (`telemetry\sent.jsonl`) showing the exact human-readable JSON summary that left the PC (even clearer for users than 20 raw event lines!).
3. **Cloud Ingestion & Associative Sketch Merging (`cloud/reports/src/{telemetry.js, telemetry-store.js}`):**
   - Accept both `schema: 1` (legacy 3.x installs) and `schema: 2` (sketch rollups) with `Content-Encoding: br|gzip` decompression.
   - Store `schema: 2` directly into compact daily rollup tables (`metric_counters`, `metric_sketches`) — merging sketches associatively by `(day, app, version, metric)` while keeping `install_id` deletion support via a tiny per-install index!
   - Update `PRIVACY.md` to accurately document the stronger zero-sequence rollup format.

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (add `TelemetrySketchTests` verifying quantile error <2%, scrubber invariants, 10-min cadence, dirty-flag persistence, and `DeleteAsync`).
- `npm test` in `apps/launcher/cloud/reports` (add schema v2 validation, sketch merge, and delete-by-install-id tests; read-only on production — never deploy!).

## 4. Deliver

- Branch `lab/df-07-on-device-sketch-telemetry` from `origin/main`.
- `improve/df-07-on-device-sketch-telemetry-report.md` with exact byte/row reduction tables, p50/p95/p99 merge accuracy proofs, and privacy comparison.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-07 on-device sketch telemetry" --body "<byte/D1 reduction, quantile accuracy, bug fixes, test status>"`. Effort: medium.
