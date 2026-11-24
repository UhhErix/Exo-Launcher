EXO DATAFLOW: Sub-File Binary Delta Updates (Zstd Patch-From + FastCDC), Play-While-Staging & ETag 304 Fix (task-name: df-05-binary-delta-updates)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

Exo already ships file-level zstd-19 range updates (`files.pack`), which skip unchanged DLLs. Moving to **sub-file binary deltas** shrinks routine hotfix updates from **12.5+ MB down to <800 KB**, eliminates the **137 MB** fallback when a game is running, and fixes a live production 304 bug on `updates.getexo.app`.

## Today (Verified Live on `v3.30.2`)

- Full `ExoLauncher-Setup.exe`: **137.04 MB**; `files.pack`: **155.21 MiB** (1,039 files, 381.05 MiB raw).
- **`ExoLauncher.dll` alone** is **30.05 MiB raw → 10.03 MiB compressed** in `files.pack`, plus `wwwroot/*` (**2.42 MiB** compressed). Even a 1-line C# fix forces every player to re-download **≥12.5 MiB** (and in v4 `exo-ui.exe` adds another ~8–15 MiB!).
- `AppUpdateService.Delta.cs:175` **refuses delta whenever a game is running** (`if (GameRunning()) return Refuse(...)`), forcing a **137.04 MB** full installer download instead of staging the small delta!
- `AppUpdateService.cs:89` pins `HttpVersion.Version11` + `EnableMultipleHttp2Connections = false` even though `Delta.cs:17` fires `RangesAtOnce = 4` concurrent Range requests against Cloudflare R2.
- **Live production bug verified on this PC:** `cloud/updates/src/worker.js:201-207` (`revalidated()`) checks exact ETag string equality (`"launcher-v3.30.2"`), while Cloudflare Brotli weakens the response ETag to `W/"launcher-v3.30.2"` — so `If-None-Match` returns **200 instead of 304**! Also `IMMUTABLE_SECONDS = 3600` (`worker.js:40`) expires immutable tag files at the edge after 1 hour.

## 1. Benchmark first, with real numbers

- Using two real consecutive Exo releases (`v3.30.1` → `v3.30.2`, or two locally built Release publishes with a 1-file C# change + 1-file Rust `exo-ui` change):
  - Measure exact wire bytes for: (a) Full `Setup.exe`, (b) current `files.pack` file-level delta, (c) **Zstd `--patch-from` (`ZSTD_CCtx_refPrefix`, `windowLog=27`)** binary patch on `ExoLauncher.dll` & `exo-ui.exe`, and (d) **FastCDC chunked delta** (for multi-version jumps N-3 → N).
  - Measure patch apply time + RAM peak + SHA-256 verification time on this PC.
  - Verify live `If-None-Match` 304 behavior in `cloud/updates` workerd tests.
- **Targets:** Hotfix download **12.5 MiB → <800 KiB (≥15× smaller)**, while-gaming update **137 MiB → <1 MiB**, patch reconstruction **<500 ms**, `/latest` ETag hit **481 B → 0 B (304)**.

## 2. Architecture & implementation

1. **Zero-Dep Zstd Prefix Binary Patching (`AppFiles.cs`, `tools/AppFiles`, `AppUpdateService.Delta.cs`):**
   - Use the ALREADY-SHIPPED `ZstdSharp.Port 0.8.8` (`refPrefix` / `LoadDictionary` with `ZSTD_c_windowLog = 27`, 128 MB window, `enableLongDistanceMatching = 1`) to emit optional `patches` entries in `files.json` (keyed by `(path, baseSha256)` → range in `patches.pack` or `files.pack`) for the top hot binaries (`ExoLauncher.dll`, `exo-ui.exe`, `ExoCapture.exe`, `wwwroot` bundles) against the last 3 stable releases.
   - Client checks if the installed file's SHA-256 matches an offered `baseSha256`: if so, downloads the tiny patch range, reconstructs the new file using the installed file as prefix dictionary, and verifies the **exact target SHA-256** already in `files.json`! Any mismatch seamlessly falls back to the full file range in `files.pack`.
   - Evaluate FastCDC (16 KB avg chunk) for arbitrary-version jumps (>3 releases behind) so even stale installs get sub-file deltas.
2. **Play-While-Staging for Delta Updates (`AppUpdateService.Delta.cs` & `AppUpdateService.Staged.cs`):**
   - Remove the hard refusal when `GameRunning()` during download/staging: download & verify the tiny delta into `app.incoming-<id>` + `staged-delta.json` at low I/O priority while the player games, then swap cleanly in <1 s on next Exo restart (exactly like staged `Setup.exe`, without downloading 137 MB!).
3. **Transport & Edge Fixes (`AppUpdateService.cs` & `cloud/updates/src/worker.js`):**
   - Fix `revalidated()` in `cloud/updates/src/worker.js:205` to implement RFC 9110 weak ETag comparison (`normalizeTag = t => t.trim().replace(/^W\//, '')`).
   - Send `If-None-Match` from `AppUpdateService.LoadLatestReleaseAsync` with cached ETag.
   - Upgrade `AppUpdateService.CreateClient` (`:89`) from `HttpVersion.Version11` to `HttpVersion.Version20` (`EnableMultipleHttp2Connections = true`).
   - Raise `IMMUTABLE_SECONDS` (`worker.js:40`) for immutable tag-addressed assets to `31536000` (`immutable`).

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (add round-trip patch tests, corrupt-patch fallback tests, staged-delta-while-gaming tests).
- `npm test` in `apps/launcher/cloud/updates` (add weak `W/` ETag 304 regression test; read-only on production — never deploy!).

## 4. Deliver

- Branch `lab/df-05-binary-delta-updates` from `origin/main`.
- `improve/df-05-binary-delta-updates-report.md` with exact byte comparisons across `Setup.exe` vs `files.pack` vs zstd `--patch-from` vs FastCDC, apply times, and test output.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-05 sub-file binary delta updates" --body "<hotfix byte savings, play-while-staging result, ETag 304 fix status>"`. Effort: high.
