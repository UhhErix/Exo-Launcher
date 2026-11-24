EXO DATAFLOW: Unified Content-Addressed Art Cache, Cross-Run Cache Survival & Batch Edge-Cached covers.getexo.app (task-name: df-06-unified-art-cache)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

On this PC today, artwork consumes **355.43 MB** split across two redundant caches (`covers\` 227.92 MB + `cache\rune-art\` 127.51 MB), Chromium's cover HTTP cache gets 100% busted on every launch, and `covers.getexo.app` hits KV one title at a time with `Cache-Control: no-store`.

## Today (Measured on This PC)

- `covers\` = **1,322 files, 227.92 MB** (bounded by `CoverArtService.CacheMaintenance.cs` at 512 MiB target).
- `cache\rune-art\` (`native-ui/crates/exo-rpc/src/remote_art.rs:61-90`) = **1,201 files, 127.51 MB** — **zero size cap, zero eviction, zero deduplication** with `covers\`, and opens a brand-new `WinHttpOpen` per image!
- `ExoLauncher/Helpers/LocalFileHost.cs:26, 32` injects a random per-run `Token` into `covers.exo-launcher.local/<token>/...` and `thumbs.exo-launcher.local/<token>/...`, invalidating 100% of Chromium's disk cache on every single launch.
- `cloud/covers/src/worker.js:468` returns `'Cache-Control': 'no-store'` even on 30-day KV hits (`HIT_TTL_SECONDS = 30 * 86400`), preventing Cloudflare's edge CDN from caching hits.
- `ExoLauncher/Services/CoverCacheClient.cs:18` sends 1 HTTP request per game title (`GET /v1/cover?title=...`), causing N sequential/parallel Worker invocations on cold library warm.

## 1. Benchmark first, with real numbers

- Measure on this PC:
  - Total disk bytes across `covers\` + `cache\rune-art\` before vs after unified content-addressed deduplication.
  - Cold 50-title cover resolution against `cloud/covers` (50 single GETs vs 1 `POST /v1/covers/batch` + edge cache warm hit).
  - Cold Library first-paint image decode time in 3.x WebView2 (restarting Exo: current random-token 0% cache hit vs persistent cache-surviving URLs) and in v4 Rune (local unified store direct read vs `remote_art.rs` HTTP fetch).
- **Targets:** Duplicate art storage **355 MB → <180 MB**, `covers.getexo.app` KV reads **-95%**, edge hit TTFB **<15 ms**, 50-title lookup **50 RTTs → 1 RTT**, 3.x cold restart cover cache hit rate **0% → >95%**.

## 2. Architecture & implementation

1. **Unified Content-Addressed Local Art Store (`CoverArtService` + `exo-rpc/src/remote_art.rs`):**
   - Route `exo-ui` (v4 Rune) to read local covers/thumbnails directly from `%LOCALAPPDATA%\ExoLauncher\covers\` (via `ArtBase.coversDir`) and share ONE bounded LRU cache (`cache\remote-art`, capped at 128 MB / 1,500 files with atomic LRU maintenance) for non-library remote art (Store, Discord, Music).
   - Add content-hash deduplication (identical cover bytes hardlinked/shared once across variants & stores) and pre-sized thumbnail generation on write so Rune loads exact card-size bitmaps with zero full-res decode spikes.
   - Replace per-image `WinHttpOpen` in `remote_art.rs` with persistent HTTP/2 session + singleflight in-flight deduplication.
2. **Batch + Edge-Cached `covers.getexo.app` (`cloud/covers/src/worker.js` + `CoverCacheClient.cs`):**
   - On `GET /v1/cover` hits (`ok: true`), return `Cache-Control: public, max-age=86400, stale-while-revalidate=604800` + `CDN-Cache-Control: max-age=604800` and check `caches.default` BEFORE `env.COVERS.get` (95%+ KV read reduction!).
   - Add `POST /v1/covers/batch` accepting up to 50 `{ title, store?, id? }` entries: checks edge cache + batched KV reads in parallel, runs bounded store lookups only for misses within the shared budget, returns `{ results: [...] }` in 1 RTT.
   - Add in-isolate singleflight map so concurrent identical misses share 1 upstream store probe.
3. **Cache-Surviving Public Art Serving in 3.x (`LocalFileHost.cs`):**
   - Keep the per-run random `Token` for private user files (`captures`, `capture-thumbs`, `notes`, `profile`).
   - For public store cover art & thumbnails (`covers.exo-launcher.local`, `thumbs.exo-launcher.local`), use a stable per-install secret token (stored in `%LOCALAPPDATA%\ExoLauncher`, still unguessable by third-party frames + enforced by `IsExoRequest`) with `Cache-Control: private, max-age=604800, immutable` (filename already embeds content stamp!) so Chromium's HTTP cache survives across launches!

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (including `PlayerDataTests`, `NetworkDestinationsTests`, `LocalFileHostTests`, `CoverCacheClientTests`).
- `npm test` in `apps/launcher/cloud/covers` (workerd tests for edge cache headers, batch endpoint, rate limits; read-only on production — never deploy!).
- `cargo test -p exo-rpc` in `apps/launcher/native-ui`.

## 4. Deliver

- Branch `lab/df-06-unified-art-cache` from `origin/main`.
- `improve/df-06-unified-art-cache-report.md` with disk footprint, batch RTT savings, KV read reduction, and cold-restart hit rates.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-06 unified art cache & batch covers" --body "<disk savings, batch RTT/KV reduction, cache hit rate, test status>"`. Effort: medium.
