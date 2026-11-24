# Exo Launcher Dataflow Deep Sweep (`scout-dataflow`)

**Date:** 2026-11-24  
**Scope:** Every byte Exo moves — host↔UI IPC (`exo-rpc` v4 named pipes + 3.x WebView2 bridge + subprocess IPCs), HTTP clients & transport, store & download pipelines, update channel (`updates.getexo.app`), cloud Workers/D1/R2/KV/DO (`account`, `covers`, `reports`, `riot`, `updates`, `relay`), telemetry/crash/triage pipeline, Discord gateway/REST/voice/video, Music CDP engines, caching, retries, compression, batching, wakeups, and offline behavior.  
**Baseline commit:** `8296e31` (`origin/main`, Exo Launcher v3.30.2).  
**Target architecture judged:** v4 native Rust/Rune UI + .NET 10 host, and shipping 3.x WinUI 3 + WebView2/React 19.

---

## 0. Measured Baseline on This PC

All measurements below were captured live on **2026-11-24** on this exact machine (read-only against local state and public cloud endpoints; zero product code modified, zero cloud state mutated).

- **Machine:** Windows 11 Pro Build 26300 | AMD Ryzen 5 5600X (6C/12T) | 15.9 GB RAM | `.NET 10.0.403` | `cargo 1.100.0`.
- **Static inventory (`apps/launcher/`):**
  - `host-v1.json` contract: **342 RPC methods**, **78 host events**.
  - `ExoHttp.Create(...)`: **51 separate call sites** (**12** are per-call `using var http = ExoHttp.Create(...)` disposing their handler immediately!).
  - Background timers in `ExoLauncher/`: **61 timer constructions**.
  - `NetworkDestinations.cs`: **159 host rules**.
  - Cloud Workers: **6 Workers** (`cloud/updates`, `cloud/account`, `cloud/covers`, `cloud/reports`, `cloud/riot`, `relay`).
  - Subprocess IPC engines: **4** (`ExoLauncher.SteamIpc`, `ExoCapture.exe`, `ExoShaders.Service.exe`, `Legendary/gogdl/Nile` CLI runners) + **1** cross-origin iframe relay (`mouseRelay.ts` ↔ `mouse.exo-launcher.local`) + **3** hidden WebView2 CDP engines (Spotify, YouTube Music, Apple Music).

### 0.1 Local Disk & Payload Footprint (`%LOCALAPPDATA%\ExoLauncher`)

| Artifact / Directory | Measured Size / Count | Dataflow Significance |
| --- | --- | --- |
| `library-cache.json` | 105,260 B (102.8 KB, 215 games) | Re-serialized + SHA-256 hashed whole on every `OnLibraryUpdated` (`WebHostBridge.cs:538`) |
| `account-sync.json` | 106,754 B (104.3 KB) | Atomically rewritten to SSD **every 30 s** (`AccountSyncHost.cs:91, 156`) = **~300 MB/day** idle disk writes |
| `store\home-v5.json` | 155,581 B (151.9 KB) | Cold fetch took **969 ms** in `app.log` (`sellersMs=968 upcomingMs=790 saleMs=721 wishedMs=611`) |
| `telemetry\sent.jsonl` | 258,130 B (1,016 lines; avg **254.1 B/event**) | **736 / 1,016 (72.4%)** are repetitive `room.opened` events; Brotli shrinks 64 KB slice to **3,947 B (6.0%)** |
| `covers\` | 1,322 files, **227.92 MB** | Served via per-run random `LocalFileHost.Token` (busts 100% of Chromium HTTP cache every launch) |
| `cache\rune-art\` | 1,201 files, **127.51 MB** | Unbounded duplicate art cache in `exo-rpc/src/remote_art.rs` (zero eviction, **355.43 MB** combined art!) |
| `app\` (installed v3.30.2) | 1,039 files, **380.02 MB** | Top files: `exo-deepfilter.dll` (50.64 MB), `ExoLauncher.dll` (30.05 MB), `onnxruntime.dll` (16.45 MB) |
| `logs\app.log` | 1,370,257 B (12,705 lines) | 4,874 `PERF` lines, **63** `PERF discord rest >=700 ms` warnings, 632 `Trim:` lines, 200 `Update:` lines |

### 0.2 Live Release & Compression Measurements (`updates.getexo.app/v1/launcher/latest` = `v3.30.2`)

| Asset / Payload | Raw Size | Compressed / Wire Size | Notes |
| --- | --- | --- | --- |
| `ExoLauncher-Setup.exe` | 137,041,221 B (130.69 MiB) | 130.69 MiB | Used whenever a game is running (`AppUpdateService.Delta.cs:175` refuses delta!) |
| `files.pack` (1,039 files, zstd-19) | 381.05 MiB raw | 155.21 MiB (162,750,245 B) | File-level range pack; `files.json` = 180,949 B |
| `ExoLauncher.dll` (single hotfix floor) | 30.05 MiB (31,514,624 B) | **10.03 MiB** in `files.pack` (gzip 16.24 MiB, br 13.68 MiB) | Changes on every C# release; forces ≥10–12.5 MiB download for 1-line fixes |
| `wwwroot/*` (241 files) | 7.81 MiB | **2.42 MiB** in `files.pack` | Re-downloaded whenever UI bundle hash changes |
| 250-game `library.get` JSON | 107,089 B | gzip **8,819 B (8.2%)**, br **4,435 B (4.1%)** | Sent uncompressed on every full library push |
| Real 64 KB `telemetry\sent.jsonl` | 65,536 B | gzip **5,364 B (8.2%)**, br **3,947 B (6.0%)** | Sent uncompressed JSON today (`Telemetry.cs:455`) |

### 0.3 Live Cloud Protocol & Latency Measurements (Read-Only, Ryzen 5 5600X)

Measured with .NET 10 `SocketsHttpHandler` (`Accept-Encoding: br, gzip`, cold request + warm request with `If-None-Match` when ETag present):

| Endpoint | HTTP/1.1 Cold / Warm+INM | HTTP/2 Cold / Warm+INM | HTTP/3 Cold / Warm+INM | Live Finding |
| --- | --- | --- | --- | --- |
| `updates.getexo.app/v1/launcher/latest` | 1,794 ms / 90.7 ms (**200**, 481 B br) | **376 ms** / 109.2 ms (**200**, 481 B br) | **TIMEOUT (10,000 ms!)** | **BUG:** CF Brotli weakened ETag to `W/"launcher-v3.30.2"`; Worker `revalidated()` failed match -> returned **200 instead of 304**! |
| `covers.getexo.app/health` | 607 ms / 91.4 ms (200, `no-store`) | **298 ms** / 212.0 ms (200) | 4,247 ms (H2 fallback) / 654.9 ms (H3) | `Cache-Control: no-store` on all responses; naive H3 stalls 4.2 s! |
| `riot.getexo.app/health` | 353 ms / **36.7 ms** (200, `no-store`) | 478 ms / 138.5 ms (200) | 626 ms (H2) / 353.3 ms (H3) | H3 slower than H1/H2 on this ISP/UDP path |
| `accounts.getexo.app/v1/account` | 303 ms / 110.1 ms (401, identity) | 698 ms / **107.0 ms** (401) | **298 ms** (H2) / 340.9 ms (H3) | All 4 hosts advertise `alt-svc: h3=":443"`, but naive `Version30` regresses tail latency without Happy Eyeballs + QUIC circuit-breaker |

### 0.4 Live IPC Microbenchmarks on This PC (Ryzen 5 5600X)

| Benchmark | Measured Result | Source |
| --- | --- | --- |
| `exo-rpc` Rust `LIB_DISPATCH_READER` (108 KB, n=50) | p50 = **0.158 ms**, p95 = **1.288 ms**, max = **3.784 ms** | `cargo test -p exo-rpc bench_dispatch_cost --release -- --ignored` |
| `exo-rpc` Rust `LIB_DISPATCH_TOTAL` (250 games) | p50 = **0.702 ms**, p95 = **3.268 ms**, max = **4.466 ms** | Same |
| `exo-rpc` `VIDEO_FRAME_JSON_FALLBACK` (87.5 KB b64) | p50 = **0.363 ms**, p95 = **0.848 ms**, 60fps CPU = **20.57 ms/s** | Same |
| `exo-rpc` `VIDEO_FRAME_BINARY` (65.6 KB media pipe) | p50 = **0.001 ms**, p95 = **0.001 ms**, 60fps CPU = **0.05 ms/s** (411× less parse CPU) | Same |
| .NET `IPC_PIPE_JSON_SMALL` (NamedPipe + UTF8→UTF16→JSON, n=1000) | p50 = **36.4 µs**, p95 = **309.9 µs**, p99 = **2,479.9 µs** | Standalone .NET 10 bench on this PC |
| .NET `IPC_SHM_BIN_SMALL` (MMF SPSC ring + binary, n=1000) | p50 = **10.2 µs (3.6×)**, p95 = **19.8 µs (15.6×)**, p99 = **98.3 µs (25.2×)** | Same |
| .NET `VIDEO_128KB_PIPE` (2 writes + 3 reads + alloc, 600f) | **431.4 µs/frame** (**289.8 MB/s**) | Same |
| .NET `VIDEO_128KB_SHM` (8-slot MMF ring, zero alloc, 600f) | **257.3 µs/frame** (**485.9 MB/s**, **1.68×**) | Same (and DXGI shared handle = **0 B CPU copy**) |

---

## 1. Exhaustive Coverage Checklist

Every file, endpoint, transport, pipeline, and cache in the `scout-dataflow` domain was inspected and marked below. Nothing skipped silently.

| # | Area / Component | Exact Files / Endpoints | Verdict |
| --- | --- | --- | --- |
| 1 | Host transport abstraction | `ExoLauncher/Services/IHostTransport.cs`, `RuneHostSession.cs`, `BridgeRequestRegistry.cs` | Reviewed — clean interface; lacks binary/SHM RPC channel, zero-copy GPU handle handoff, and per-method UTF-8 path |
| 2 | Named-pipe RPC server (v4) | `ExoLauncher/Services/NamedPipeHostTransport.cs` | Reviewed — **BUG:** `MaxMessageBytes=4 MB` kills pipe on large requests; UTF-8→UTF-16→UTF-8 double transcode; 2 `WriteAsync` syscalls per video frame; single `_stream` drain per tick |
| 3 | WebView2 bridge transport (3.x) | `ExoLauncher/Services/WebViewHostTransport.cs` | Reviewed — **Hot-path issue:** `CreateSharedBuffer` creates/maps a new OS section on every video frame (60/sec); UI-thread bounce on every `PostJson` |
| 4 | JSON-RPC wire & framing | `ExoLauncher/Services/ExoBridgeProtocol.cs`, `WebHostBridge.cs` (3,592 lines + 49 partial files), `ui/contracts/host-v1.json` (342 methods, 78 events) | Reviewed — **BUG:** `rpc.cancel` mismatch with `exo-rpc`; reflection `System.Text.Json`; duplicate `JsonOpts`; `OnLibraryUpdated` serializes+hashes whole 105 KB library; `.ConfigureAwait(true)` bounces completions to UI thread; ValueTuple serialization audited (remaining tuples in `WebHostBridge.*.cs` are internal helpers unpacked before response, though no analyzer/guard prevents future regressions) |
| 5 | Rust native RPC client (v4) | `native-ui/crates/exo-rpc/src/{lib.rs, pipe.rs, events.rs, types.rs, discord_types.rs, media_types.rs, pc_types.rs, base64.rs}` | Reviewed — **BUG:** `cancel()` sends `"id"` instead of `"requestId"`; `media_read_loop` does 3 `ReadFile` syscalls + `vec![0u8; frame_len]` zero-fill alloc per 60fps frame; writer has no batching |
| 6 | Rust remote art fetcher (v4) | `native-ui/crates/exo-rpc/src/remote_art.rs` | Reviewed — **BUG / Major bottleneck:** new `WinHttpOpen` session per image (no pooling, HTTP/1.1 only, no decompression), unbounded `cache\rune-art\` (**127.51 MB / 1,201 files**), no singleflight, duplicates host `covers\` |
| 7 | React UI bridge & workers (3.x) | `ui/src/lib/{host.ts, hostWire.ts, hostContract.ts, bridgeCancellation.ts, discordVideo.ts, discordVideo.worker.ts, goLive.ts, heartbeat.ts, heartbeatCore.ts}` | Reviewed — solid contract generation & WebCodecs worker; `sharedbufferreceived` copies bytes out because buffer is immediately released; no RPC batching/coalescing |
| 8 | Exo Mouse iframe relay | `ui/src/lib/mouseRelay.ts`, `ExoLauncher/Services/Hid/{HidService.cs, HidInterop.cs}`, `WebHostBridge.Hid.cs` | Reviewed — clean AGPL isolation; `installedGames()` calls `host.getLibrary()` over full JSON RPC; `hid.input` base64-encodes every report through JSON |
| 9 | Local virtual file host (3.x) | `ExoLauncher/Helpers/LocalFileHost.cs` | Reviewed — strong origin/token security, **Perf issue:** random per-run `Token` invalidates 100% of Chromium's HTTP disk cache on every launch; UI-thread deferral round-trip per image |
| 10 | Subprocess IPC — SteamIpc | `ExoLauncher.SteamIpc/{Program.cs, SteamAppProperties.cs, SteamOwnership.cs, SteamUninstallReadiness.cs}` | Reviewed — clean isolated `steamclient64.dll` worker; uses source-gen `WatchJsonContext`; process spawn per one-shot action |
| 11 | Subprocess IPC — Exo Capture | `ExoLauncher/Services/CaptureAddon.cs`, `capture/ExoCapture/{Host.cs, Live/GoLive.cs, Controller.cs}` | Reviewed — NDJSON stdio + duplicated anonymous pipe for Go Live frames; `DeepClone()` + `Task.Run(WriteAsync)` per call; Go Live frames travel CPU pipe → Host → Media Pipe → `exo-ui` (3 copies!) instead of DXGI shared handle |
| 12 | Subprocess IPC — Exo Shaders | `ExoLauncher/Services/ShadersAddon.cs`, `shaders/engine/src/ExoShaders.Service/{Host.cs, Protocol.cs}` | Reviewed — NDJSON stdio with clean idle-exit & at-rest snapshot; `CompilingAsync` fetches entire `snapshot` JSON just to read 2 bools |
| 13 | Subprocess CLI runners | `ExoLauncher/Adapters/Cli/{CliRunner.cs, DownloadTuning.cs, EpicCdn.cs, LegendaryCli.cs, GogdlCli.cs, NileCli.cs, RiotCli.cs, SteamProtocol.cs}` | Reviewed — `EpicCdn` 256 KB probe + `DownloadTuning` RAM-aware workers are great; no equivalent CDN probe for GOG/Amazon/updates; no LAN peer cache |
| 14 | HTTP factory & transport | `ExoLauncher/Helpers/ExoHttp.cs`, `Services/ExoHttpTransport.cs`, `Helpers/ByteBudgetStream.cs`, `NetworkDestinations.cs` | Reviewed — **Major bottleneck:** 51 separate `ExoHttp.Create` sites (12 per-call `using`), no shared `SocketsHttpHandler` pool, no default `AutomaticDecompression`, no `PooledConnectionLifetime`, no HTTP/2/adaptive-H3 policy, no request coalescing |
| 15 | Resumable & verified downloads | `ExoLauncher/Helpers/ResumableDownload.cs`, `Services/UpstreamSource.cs`, `Services/VerifiedGitHubReleaseDownloader.cs`, `Services/DownloadPolicy.cs`, `Services/PendingDownloadStore.cs`, `Services/LaunchOrchestrator.{DownloadRecovery,Queue}.cs`, `Services/InstallerCache.cs` | Reviewed — strong ETag/If-Range integrity & anti-reparse hardening; **Perf issues:** per-64KB-chunk linked CTS allocation (`ResumableDownload.cs:110`), single stream, hashes file from disk *after* writing instead of inline `IncrementalHash`, no hedged upstream/R2 racing |
| 16 | Store & catalog pipeline | `ExoLauncher/Services/StoreService*.cs` (`Query`, `Items`, `Game`, `ForYou`, `Calendar`, `Specs`, `Fit`), `Services/SteamSignIn/{SteamProtoWire.cs, SteamWebApi.cs}`, `Services/StoreGifts.cs` | Reviewed — good disk caching & `store.game` in-flight dedupe; cold `store.home` takes ~969 ms (4 parallel Steam queries + GetItems); `StoreService` uses HTTP/1.1 default on Steam; `SteamStoreAppTypes.cs:38` uses per-call `HttpClientHandler` |
| 17 | Artwork & local caches | `ExoLauncher/Services/CoverArtService*.cs`, `CoverCacheClient.cs`, `CoverMissMemory.cs`, `LibraryDiskCache.cs`, `AnswerCache.cs`, `LatestCache.cs`, `InstalledSizeCache.cs`, `DeviceImages.cs`, `docs/CACHE_POLICY.md` | Reviewed — `CoverArtService` uses legacy `HttpClientHandler` (`:2149`); single-item `covers.getexo.app` queries; 25+ scattered JSON cache files with independent serialization |
| 18 | Client update pipeline | `ExoLauncher/Services/AppUpdateService.cs`, `AppUpdateService.Delta.cs`, `AppUpdateService.Staged.cs`, `AppFiles.cs`, `UpdateFlow.cs`, `UpdateOnOpen.cs`, `UpdateHandOff.cs`, `UpdateOutcome.cs`, `Helpers/UpdateStartupHealth.cs`, `tools/AppFiles/Program.cs` | Reviewed — **Strong foundation** (zstd-19 `files.pack` + range coalescing + hardlink reuse); **Gaps:** pins `HttpVersion.Version11` (`:89`), refuses delta while a game runs (`Delta.cs:175` -> 137 MB fallback), file-level granularity forces 10.03 MB+ `ExoLauncher.dll` download per hotfix, doesn't send `If-None-Match` on `/latest` |
| 19 | Cloud Worker: `updates.getexo.app` | `cloud/updates/src/{worker.js, github.js, watch.js, announce.js}`, `wrangler.toml`, `cache-mirror.mjs`, `source-mirror.mjs` | Reviewed — **BUG:** `revalidated()` (`worker.js:205`) fails on CF-weakened `W/` ETags (verified live -> 200 instead of 304!); `IMMUTABLE_SECONDS = 3600` (1h) too short for immutable tagged assets; no binary-patch endpoint |
| 20 | Cloud Worker: `accounts.getexo.app` + Client Sync | `cloud/account/src/{worker.js, accounts.js, crypto.js, http.js, limits.js, rotation.js}`, `migrations/*`, `ExoLauncher/Services/Account/{ExoAccountClient.cs, AccountSync.cs, AccountSyncHost.cs, AccountSyncMap.cs, AccountSyncPending.cs}` | Reviewed — **Major bottleneck:** client polls every **30 s** forever + rewrites 104 KB `account-sync.json` every 30 s; Worker runs 3 D1 queries + AES key unwrap even when `record.seq <= after`; `ExoAccountClient` lacks `AutomaticDecompression` & HTTP/2; zero push-wake |
| 21 | Cloud Worker: `covers.getexo.app` | `cloud/covers/src/{worker.js, match.js, limits.json}` | Reviewed — **BUG/Bottleneck:** returns `Cache-Control: no-store` (`:468`) on 30-day KV hits, bypassing CF edge CDN; single-title endpoint only (no batching); no singleflight coalescing on concurrent misses |
| 22 | Cloud Worker: `riot.getexo.app` | `cloud/riot/src/{worker.js, aggregate.js, limits.json}`, `Adapters/Owned/RiotHoursClient.cs` | Reviewed — good KV 6h result / 30d match caching & parallel cluster fetch; returns `Cache-Control: no-store` even on cached 6h hits |
| 23 | Cloud Worker: `exo-relay` (DO) | `relay/src/worker.js`, `ExoLauncher/Services/DiscordClient/{CallRelayClient.cs, CallRelayFormat.cs, CallSongs.cs, FriendLinks.cs}` | Reviewed — **Best-in-repo cloud pattern!** WebSocket Hibernation DO + MLS DAVE exporter + AES-256-GCM + padded binary frames. Currently hardcodes `workers.dev` URL (`CallRelayClient.cs:23`); could be generalized for account push-wake & LAN signaling |
| 24 | Telemetry & Crash Reports + Triage | `ExoLauncher/Helpers/{Telemetry.cs, CrashReport.cs, CrashReportLimiter.cs, AppLog.cs, AppLog.Report.cs, UserReport.cs}`, `cloud/reports/src/{worker.js, telemetry.js, telemetry-store.js, crash-store.js, fingerprint.js, triage.js, issues.js, github.js, user-report.js, validate.js, limits.js}`, `cloud/reports/triage.mjs` | Reviewed — **Multiple bugs:** `Telemetry.cs` 1-min tick breaks 10-min memory promise & rewrites `queue.jsonl` every minute; per-call `HttpClient`; uncompressed JSON; `CrashReport.ToJson` pretty-prints on wire; `Telemetry.Scrub` compiles dynamic Regex per prop; D1 stores raw rows for 90 days; `fingerprint.js` doesn't strip async plumbing frames; Workers AI triage has zero repo/commit context |
| 25 | Discord Gateway, REST, Voice & Video | `ExoLauncher/Services/DiscordClient/{DiscordSession*.cs, DiscordRest*.cs, GatewayBackoff.cs, GatewaySendLimit.cs, DiscordCache*.cs, GoLiveCapture.cs}`, `Voice/{VoiceTransport.cs, VoiceConnection.cs, DaveSession.cs, H264Rtp.cs, H265Rtp.cs, VoiceLatency.cs, VoiceChain.cs}`, `native-ui/crates/exo-ui/src/video_decoder.rs` | Reviewed — **Huge wins:** Gateway uses **uncompressed** JSON (`DiscordSession.cs:370`, no `zstd-stream`!); REST lacks proactive `X-RateLimit-Bucket` tracking & `LoadBuildAsync` lacks decompression; `VoiceTransport.XChaChaFor` allocates new CNG handle per packet; no DSCP QoS; 20ms/30ms call timers; video crosses 3 CPU copies into Rune |
| 26 | Music & Streaming + Shield | `ExoLauncher/Services/{MusicEngineService*.cs, HeadlessMusicService.cs, MediaSessionService.cs, MusicAppVolume.cs, MusicNowPlaying.cs}`, `Helpers/{SpotifyNative.cs, YouTubeMusicNative.cs, AppleMusicNative.cs}`, `shield/rules/network.json` | Reviewed — clever zero-token CDP architecture; **Bottlenecks:** `Runtime.evaluate` long-poll timers (5s/20s) instead of `Runtime.addBinding` push; hidden WebView2 consumes 150–350 MB RAM & 1,500 ms `domContentLoadedMs` (Apple Music measured in `app.log`); `MusicAppVolume` re-sends entire JS wrapper expression on every `CallAsync` (`MusicAppVolume.cs:86`) |
| 27 | Background Wakeups & Offline Behavior | All 61 `Timer`/`PeriodicTimer` sites, `MainWindow.Resources.cs`, `Helpers/PcIdle.cs`, `Services/WebHostBridge.SteamOffline.cs` | Reviewed — uncoordinated wakeup storm across 61 timers; offline behavior works well for Library/Launch/Account-queue/Telemetry-queue, weak for Store/Covers/Music metadata caching & graceful degradation |

---

## 2. Plain Bugs & Quick Wins Found (21 Items)

Every item below is verified directly in source or by live measurement on this PC.

| # | Severity | Location (`file:line`) | Issue & Immediate Fix |
| --- | --- | --- | --- |
| **B1** | **High (Bug)** | `native-ui/crates/exo-rpc/src/lib.rs:640` vs `ExoLauncher/Services/WebHostBridge.cs:603` | `exo-rpc::RpcClient::cancel()` sends `params: { "id": id }`, but `WebHostBridge` reads `params["requestId"]`. **100% of Rune UI RPC cancellations are silently ignored.** Fix: send `"requestId"` in `exo-rpc` (and accept both in host). |
| **B2** | **High (Bug)** | `cloud/updates/src/worker.js:201-207` | `revalidated()` checks exact ETag string equality. Cloudflare Brotli weakens `"launcher-v3.30.2"` to `W/"launcher-v3.30.2"`. Verified live: `If-None-Match` returns **200** instead of **304**. Fix: RFC 9110 weak comparison (`strip /^W\//`). |
| **B3** | **High (Bug)** | `ExoLauncher/Helpers/Telemetry.cs:32, 290, 314-331, 356, 369` | `PerfEvery = 10 min` is unused; 1-min timer calls `SampleMemory()` **every minute** (10× `PRIVACY.md` promise), and `FlushAsync`'s `finally { PersistQueue(); }` atomically rewrites `queue.jsonl` **every 60 s** even when returning early on `_nextSend`. Fix: track dirty flag + enforce 10-min cadence + move timer to 5 min. |
| **B4** | **High (Bug)** | `ExoLauncher/Services/NamedPipeHostTransport.cs:19, 214` vs `ExoBridgeProtocol.cs:57-72` | `MaxMessageBytes = 4 MB` throws `InvalidDataException` and drops the RPC pipe on `capture.saveImage` (up to 90 MB), `library.folderArt` (20 MB), and `discord.camera` (8 MB). Fix: raise/align limit or route bulk uploads over binary/SHM channel. |
| **B5** | **High (Perf)** | `ExoLauncher/Services/Account/AccountSyncHost.cs:89-91, 156` | Polls `accounts.getexo.app` every **30 s** forever (even in-game or single-PC) AND rewrites 104 KB `account-sync.json` to disk every 30 s (**~300 MB/day** SSD writes) solely to bump `LastSyncAt`. Fix: keep `LastSyncAt` in memory or persist only when `Cursor`/`Dirty`/`Items` change; gate/back off polling. |
| **B6** | **High (Perf)** | `ExoLauncher/Services/DiscordClient/DiscordSession.cs:370` | Connects to Discord Gateway without compression (`/?encoding=json&v=9`) despite `ZstdSharp.Port 0.8.8` already shipped. Fix: add `&compress=zstd-stream` (85–92% less traffic). |
| **B7** | **High (Perf)** | `native-ui/crates/exo-rpc/src/remote_art.rs:72-195` | Calls `WinHttpOpen`/`CloseHandle` per image (zero TLS/TCP reuse, HTTP/1.1, no decompression), and `cache\rune-art\` has **no size cap or eviction** (already **127.51 MB / 1,201 files** on this PC). Fix: reuse `HINTERNET` session + enable HTTP/2 + unify with host `covers\`. |
| **B8** | **Med (Perf)** | `cloud/covers/src/worker.js:468` | `json()` hardcodes `'Cache-Control': 'no-store'` on 30-day KV hits, bypassing Cloudflare CDN edge caching. Fix: `public, max-age=86400, stale-while-revalidate=604800` on `ok: true` hits. |
| **B9** | **Med (Perf)** | `cloud/account/src/worker.js:384-390` | `GET /v1/sync?after=` unwraps AES `item_key` and queries `items` even when `record.seq <= after`. Fix: short-circuit immediately with `{ items: [], cursor: after, more: false }` or `304 Not Modified` on `ETag: "seq-<seq>"`. |
| **B10** | **Med (Perf)** | `ExoLauncher/Services/DiscordClient/Voice/VoiceTransport.cs:336-341` | `XChaChaFor` creates & disposes a new `ChaCha20Poly1305` CNG handle + 2 heap arrays per RTP packet. Fix: reuse or switch to managed/span-based XChaCha20-Poly1305 without per-packet CNG handle creation. |
| **B11** | **Med (Perf)** | `ExoLauncher/Services/AppUpdateService.cs:89` & `AppUpdateService.Delta.cs:175` | Pins `HttpVersion.Version11` on `updates.getexo.app` (4 TCP+TLS handshakes for `RangesAtOnce=4`), AND refuses delta while a game runs (falling back to 137 MB Setup). Fix: `Version20` + allow delta staging while gaming. |
| **B12** | **Med (Perf)** | `ExoLauncher/Services/DiscordClient/DiscordRest.cs:116` | `LoadBuildAsync` creates a per-call `HttpClient` without `AutomaticDecompression`, downloading up to 8 uncompressed multi-MB JS bundles. Fix: reuse handler with `DecompressionMethods.All`. |
| **B13** | **Med (Perf)** | `ExoLauncher/Helpers/CrashReport.cs:194, 257, 291` & `Telemetry.cs:99, 453` | Pretty-prints JSON on wire (`WriteIndented=true`) then re-parses; per-call `HttpClient`; uncompressed POSTs; `Telemetry.Scrub` dynamically compiles `Regex.Replace` for `machineName` on every prop. Fix: compact JSON, static `HttpClient`, precompiled `machineName` regex, gzip/br request body. |
| **B14** | **Med (Perf)** | `ExoLauncher/Helpers/ResumableDownload.cs:110, 148-155` | `WithIdleAsync` allocates a linked `CancellationTokenSource` + timer + `AsTask()` box on every 64 KB chunk (~2,140 allocs per installer) and hashes from disk after write. Fix: reusable timer / `TryReset` + inline `IncrementalHash`. |
| **B15** | **Med (Perf)** | `ExoLauncher/Services/WebViewHostTransport.cs:82` | `CreateSharedBuffer` creates and closes a new OS shared-memory section per video frame (60/sec). Fix: pre-allocated triple-buffered shared buffer ring. |
| **B16** | **Med (Perf)** | `ExoLauncher/Helpers/LocalFileHost.cs:26, 32` | Per-run random `Token` in cover/thumbnail URLs invalidates 100% of Chromium's HTTP cache on every launch. Fix: keep security via custom request header / scoped origin or stable per-install HMAC key for public cover art. |
| **B17** | **Med (Perf)** | `ExoLauncher/Services/NamedPipeHostTransport.cs:240, 396-397` & `exo-rpc/src/lib.rs:702-727` | Host double-transcodes UTF-8→UTF-16→UTF-8 per RPC and does 2 `WriteAsync` syscalls per video frame; Rust does 3 `ReadFile` syscalls + zero-init `vec![0u8; frame_len]`. Fix: parse UTF-8 `ReadOnlyMemory<byte>` directly; combine header+frame write; pool frame buffers. |
| **B18** | **Med (Perf)** | `ExoLauncher/Services/WebHostBridge.cs:538-551, 624-1015` | `OnLibraryUpdated` serializes + SHA-256 hashes all 105 KB on every single cover/playtime change; ~95% of RPC switch arms use `.ConfigureAwait(true)` bouncing to UI thread before serializing. Fix: incremental `library.patch` + `.ConfigureAwait(false)` on non-UI handlers. |
| **B19** | **Med (Perf)** | `ExoLauncher/Services/CoverArtService.cs:2149` + 12 `using var http = ExoHttp.Create` sites | Legacy `HttpClientHandler` + 12 per-call `HttpClient` constructions fragment pools and skip decompression. Fix: migrate all 51 sites onto shared `ExoHttp` pools. |
| **B20** | **Low (Cost)** | `cloud/updates/src/worker.js:40` | `IMMUTABLE_SECONDS = 3600` (1 hour) for immutable tag-scoped release assets (`files.pack`, `Setup.exe`) instead of `31536000, immutable`. Fix: 1-year edge cache on immutable tag files. |
| **B21** | **Low (Quality)** | `cloud/reports/src/fingerprint.js:33-38` | Stack fingerprinting keeps top 3 frames verbatim, collapsing unrelated async crashes onto `TaskAwaiter.ThrowForNonSuccess` / `ExceptionDispatchInfo.Throw`. Fix: skip CLR/WinUI async plumbing frames before picking top 3. |
| **B22** | **Low (Perf)** | `ExoLauncher/Services/MusicAppVolume.cs:86` | Re-sends the entire multi-KB JS wrapper source string (`_page`) on every `ReadAsync`/`WriteAsync`/`WatchAsync` CDP evaluation instead of `installed` sentinel like `MusicNowPlaying.cs:110` already does. Fix: reuse `window.__exoVolume` sentinel. |

---

## 3. Ranked Breakthrough Ideas (Player-Felt Impact ÷ Effort)

Ranked by **player-felt impact ÷ effort**. Every idea specifies what it beats, a measurable target, evidence it is possible, risk, effort (`S`/`M`/`L`/`XL`), target (`3.x`, `v4`, or `3.x + v4`), and a **<2-day proof experiment**. The Top 10 have full ready-to-dispatch specs in `improve/scout-dataflow-specs/`.

---

### Rank 1: Streaming Zstd Discord Gateway + Proactive REST Rate-Limit Buckets (`df-01`)
- **Current state:** `DiscordSession.cs:370` connects to `wss://gateway.discord.gg/?encoding=json&v=9` with **zero compression** (requiring a 256 MB receive cap for READY!). `DiscordRest.cs:730` only reacts *after* hitting HTTP 429; `LoadBuildAsync` (`:116`) downloads up to 8 uncompressed JS bundles. `app.log` recorded **63** slow Discord REST calls (`>=700 ms`).
- **Best in the world today:** Discord Desktop (Electron) uses `compress=zstd-stream` + `encoding=etf` + client-side `X-RateLimit-Bucket` queues + IndexedDB channel cache.
- **Beyond SOTA:** Streaming Zstd (`compress=zstd-stream`) on native C# (`ZstdSharp.Port 0.8.8`, zero new deps) + optional binary ETF decoder (`encoding=etf`, zero JSON parsing on hot `MESSAGE_CREATE`/`PRESENCE_UPDATE`!) + proactive `X-RateLimit-Bucket` token scheduler + **predictive channel prefetch** (preload top 3 recent DMs/channels on hover/idle into an in-memory ring so channel clicks are **0 ms / 0 RTT**).
- **Beats & measurable target:** Beats Discord Desktop's cold-connect & channel-switch latency. Target: **85–92% smaller gateway bytes**, **3–5× faster READY** (<350 ms vs 1.2–2.5 s), **0 unexpected 429s** under burst testing, **<1 ms** cached channel switch.
- **Evidence it's possible:** `ZstdSharp.Port 0.8.8` is already referenced in `ExoLauncher.csproj:74`; Discord's gateway natively supports `compress=zstd-stream` (persistent `ZSTD_DCtx` across binary WebSocket messages ending in frame flush) and returns `X-RateLimit-Bucket`/`Remaining`/`Reset-After` on every REST response.
- **Risk:** Low — clean fallback to uncompressed JSON if gateway closes with decode error (4002); ETF is optional phase 2 behind a feature test.
- **Effort:** **S** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Connect a test harness to Discord Gateway with `&compress=zstd-stream` vs uncompressed, log exact READY bytes + wall-clock READY ms + 10-minute live session wire bytes; replay 50 burst REST calls comparing reactive 429 vs bucket-paced throughput.

---

### Rank 2: Unified `ExoHttp` Shared Transport Fabric + Adaptive QUIC Racing + Singleflight (`df-02`)
- **Current state:** 51 separate `ExoHttp.Create()` call sites (12 per-call `using`), mixed `SocketsHttpHandler` and legacy `HttpClientHandler` (`CoverArtService.cs:2149`), missing `AutomaticDecompression`, HTTP/1.1 pinning (`AppUpdateService.cs:89`), and `remote_art.rs` creating a fresh `WinHttpOpen` per image. Our live probe proved naive `Version30` stalls 4.2 s–10 s on UDP-impaired paths while HTTP/2 succeeds cleanly in 298–478 ms!
- **Best in the world today:** Chromium's network stack (shared connection pool, HTTP/2 multiplexing, Happy Eyeballs v3, per-network QUIC brokenness tracker, automatic Brotli/gzip, request coalescing).
- **Beyond SOTA:** **Profiled Shared `ExoHttp` Fabric** (4 shared pools by security profile: `Public`, `NoRedirectNoCookies`, `RawRange`, `Loopback`) with HTTP/2 default + **Adaptive QUIC Happy-Eyeballs Circuit-Breaker** (background 1-packet QUIC probe per network SSID/gateway hash; promotes Cloudflare hosts to HTTP/3 ONLY when measured p50 beats H2 by >15% with 0% loss, immediately demotes on stall!) + **Singleflight in-flight GET coalescer** (duplicate concurrent requests to Steam/covers/store share 1 wire call) + persistent `HINTERNET` HTTP/2 pool in Rust `remote_art.rs`.
- **Beats & measurable target:** Beats naive .NET `HttpClient` and Chromium's per-origin overhead. Target: **51 → 4 handler pools**, **0 per-call `HttpClient` allocations**, **40–70% fewer TLS handshakes** on cold start, **≥2× faster** `store.home` (969 ms → <450 ms) & `remote_art` bursts, **0 QUIC stalls** on UDP-impaired networks.
- **Evidence it's possible:** .NET 10 `SocketsHttpHandler` supports `PooledConnectionLifetime`, `EnableMultipleHttp2Connections`, `AutomaticDecompression = All`, and per-request `Version`/`VersionPolicy`; WinHTTP supports `WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL` (HTTP/2 + HTTP/3) on a shared session handle.
- **Risk:** Low-Medium — cookie/redirect isolation must remain strict per security profile (verified by `NetworkDestinationsTests` + dedicated redirect/auth tests).
- **Effort:** **M** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Benchmark cold startup + Store Home + 50-image cover burst + 10 concurrent `updates`/`covers`/`store` requests before vs after shared `ExoHttp` fabric + adaptive QUIC probe, logging TLS handshake count, total sockets, and p50/p95 wall-clock ms.

---

### Rank 3: Zero-Copy Shared-Memory Ring IPC + Binary Codec + DXGI Shared Texture Video Pipeline (`df-03`)
- **Current state:** Named-pipe NDJSON (`NamedPipeHostTransport.cs` + `exo-rpc/src/lib.rs`) measured **p50=36.4 µs, p95=309.9 µs, p99=2,479.9 µs** for small RPCs, **431.4 µs/frame** for 128 KB video (5 kernel syscalls + zero-init alloc per frame), **4 MB cap bug** killing image/camera RPCs, **105 KB full-library re-serialization** on every cover/playtime update, and 3.x `WebViewHostTransport.cs:82` creating a new OS shared section per video frame.
- **Best in the world today:** Electron/Chromium Mojo IPC (shared memory buffers + binary serialization), Steam's protobuf IPC, OBS's shared texture hook.
- **Beyond SOTA (never seen before in a launcher):**
  1. **Dual-Ring Lock-Free Shared Memory IPC (`MemoryMappedFile` + WaitOnAddress/Event)** with compact binary framing (MessagePack / rkyv-compatible fixed headers + tag IDs) for v4 host↔Rune -> measured **3.6× lower p50 (10.2 µs)** and **25.2× lower p99 (98.3 µs)** on this PC!
  2. **True Zero-Copy GPU→GPU Video Path (`IDXGIResource1::CreateSharedHandle` + D3D12 keyed mutex / fence):** `ExoCapture` and the host's Media Foundation hardware decoder pass a **64-bit NT shared texture handle** to `exo-ui` (Rune / `wgpu` DX12) instead of copying 128 KB–8 MB frames over pipes and re-uploading to VRAM. **0 bytes of CPU pixel/bitstream copy** for Go Live preview, Discord stream watch, and Clip/Snip preview!
  3. **Incremental `library.patch` Delta Events** (field-level diff per game ID instead of 105 KB full snapshots) + **Source-Generated `System.Text.Json`** on the fallback JSON path.
- **Beats & measurable target:** Beats Electron Mojo, Tauri IPC, and Discord's video pipeline. Target: RPC p99 **2,480 µs → <100 µs**, `library.updated` wire bytes on cover/playtime tick **105 KB → <500 B (210×)**, Go Live / Discord video CPU copy **30 MB/s → 0 B/s**, 3.x WebView2 `CreateSharedBuffer` allocs **60/s → 0/s**.
- **Evidence it's possible:** Our standalone bench already achieved **10.2 µs p50 / 98.3 µs p99** on this PC; `CaptureAddon.cs:138` already uses `DuplicateHandle` between processes; `wgpu-hal` DX12 supports importing `ID3D12Resource` from NT shared handles via `OpenSharedHandle`.
- **Risk:** Medium — cross-process memory layout & DXGI fence synchronization require fuzz/disconnect testing and automatic fallback to named-pipe media stream if GPU adapter differs.
- **Effort:** **L** | **Applies to:** **v4** (SHM ring + DXGI handle + binary codec) & **3.x** (incremental `library.patch` + triple-buffered WebView2 shared buffer + UTF-8 zero-transcode).
- **<2-day proof experiment:** Prototype C#↔Rust SPSC shared-memory ring + binary codec + DXGI shared texture handle test app opening a 1440p60 NV12/BGRA texture in `wgpu` DX12; measure p50/p99 RPC latency, frame latency, and Task Manager Copy/3D engine overhead.

---

### Rank 4: Event-Driven Account Sync (Durable Object Push-Wake) & Unified Coalesced Wakeup Scheduler (`df-04`)
- **Current state:** `AccountSyncHost.cs:91` polls `GET https://accounts.getexo.app/v1/sync` **every 30 seconds unconditionally** (2,880 HTTP requests/day, ~8,640 D1 queries/day per user, and **2,880 full 104 KB SSD rewrites/day = ~300 MB/day**!). Plus **61 uncoordinated timers** wake Exo continuously even while gaming.
- **Best in the world today:** iCloud / Steam Cloud push notifications + Windows Coalesced Timers (`SetWaitableTimerEx`).
- **Beyond SOTA:**
  1. **Zero-Cost Durable Object Push-Wake for Account Sync:** Reuse the exact Cloudflare **WebSocket Hibernation Durable Object** pattern already proven in `relay/src/worker.js`! A single hibernated DO per active multi-device account holds a silent WebSocket that costs **$0 while idle** and pushes a 12-byte `{ seq }` bump in **<150 ms** the moment another PC calls `PUT /v1/sync`. Single-device accounts don't even open a socket (pull on start + on focus after >5 min).
  2. **Server `seq <= after` Short-Circuit + `ETag: "seq-<n>"` / `304`** in `cloud/account/src/worker.js:384` (cuts idle D1 queries & crypto unwraps to near-zero).
  3. **Dirty-Only Persistence** in `AccountSyncStore.Save` (eliminates 2,880 daily SSD writes).
  4. **Game-Aware Unified Wakeup Coalescer:** Central `ExoScheduler` aligns all non-realtime background jobs onto one coalesced tick, suspends non-essential timers completely while a game is running (`GameRunningSignal.Running`), and replaces `DiscordSession.Settings.cs:298` 30 ms keyboard polling with `WM_INPUT` / raw key events.
- **Beats & measurable target:** Beats Steam, Epic, and Discord idle power/CPU/SSD wear. Target: idle network requests **2,880/day → <10/day (99.6% reduction)**, idle SSD writes **~300 MB/day → <1 MB/day**, cross-PC sync latency **30,000 ms → <150 ms (200× faster)**, in-game background timer wakeups **<1/min**.
- **Evidence it's possible:** `relay/src/worker.js` already runs Cloudflare DO WebSocket Hibernation; `GameRunningSignal` and `PcIdle` already exist in `ExoLauncher`.
- **Risk:** Low-Medium — DO push is strictly a wake hint (client still pulls authoritative state from D1 on wake), so a missed push never loses data.
- **Effort:** **M** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Instrument ETW/PowerCfg wakeup rate + `ProcMon`/file-write counter + D1 query count over a 30-minute idle + gaming session before vs after dirty-save guard, `seq` short-circuit, and DO push-wake prototype.

---

### Rank 5: Sub-File Binary Delta Updates (Zstd `--patch-from` + FastCDC) & Play-While-Staging (`df-05`)
- **Current state:** `AppUpdateService.Delta.cs` + `AppFiles.cs` already do zstd-19 file-level updates (`files.pack`), which skips unchanged DLLs. **However**, `ExoLauncher.dll` (30.05 MB raw -> **10.03 MB** compressed) and `wwwroot/*` (**2.42 MB** compressed) change on *every* release, forcing ≥12.5 MB per tiny hotfix (and in v4 `exo-ui.exe` adds another ~8–15 MB). Worse: `AppUpdateService.Delta.cs:175` refuses delta while a game runs -> downloads **137.04 MB** `Setup.exe`; `AppUpdateService.cs:89` pins HTTP/1.1; `cloud/updates/src/worker.js:205` has the live `W/` ETag bug.
- **Best in the world today:** Chrome Courgette / Zucchini binary diffing, Steam depot chunking, Cloudflare R2 immutable CDN.
- **Beyond SOTA:**
  1. **Zero-Dep Zstd Prefix/Dictionary Patching (`ZSTD_CCtx_refPrefix` / `ZSTD_DCtx_refPrefix`)** using the ALREADY-SHIPPED `ZstdSharp.Port 0.8.8`! For the top 3 hot binaries (`ExoLauncher.dll`, `exo-ui.exe`, `ExoCapture.exe`), CI emits a tiny `patch-<prevTag>-to-<tag>.zstd` (using the previous release's binary as the zstd reference prefix, optionally with PE/R2R section-aligned FastCDC chunks for multi-version jumps!). Client reconstructs exact target bytes and verifies the exact SHA-256 already in `files.json` — zero trust change!
  2. **Background Delta Staging While Gaming:** Allow `TryDeltaAsync` to download & verify the tiny patch into `app.incoming-*` while a game runs, then record `staged-delta.json` so `Apply-ExoDelta.ps1` swaps cleanly on next start (eliminating the 137 MB fallback!).
  3. **Fix live `W/` ETag 304 bug** (`worker.js:205`) + send `If-None-Match` from `AppUpdateService` + bump `IMMUTABLE_SECONDS` from 1h to 1y + upgrade `AppUpdateService` to HTTP/2 (`Version20`).
- **Beats & measurable target:** Beats Electron Squirrel/NSIS, Epic, and Battle.net updaters. Target: hotfix download **12.5 MB → <800 KB (15× smaller)**, while-gaming update **137 MB → <1 MB (130× smaller)**, update apply time **<2 s**, `/latest` poll bytes **481 B → 0 B (304)**.
- **Evidence it's possible:** `ZstdSharp.Port 0.8.8` exposes `Compressor.LoadDictionary` / `ZSTD_CCtx_refPrefix` (long-distance matching `windowLog=27` covers 128 MB, easily spanning the 30 MB `ExoLauncher.dll`!).
- **Risk:** Low — reconstructed file must match exact SHA-256 in `files.json`; any mismatch cleanly falls back to `files.pack` range fetch.
- **Effort:** **M** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Take two real consecutive Exo releases (`v3.30.1` and `v3.30.2` `ExoLauncher.dll`), run zstd-19 `--patch-from` (`--long=27`) and FastCDC chunking in a script, measure exact patch bytes + decompress speed + SHA-256 verification time.

---

### Rank 6: Unified Content-Addressed Art Cache + Batch Edge-Cached `covers.getexo.app` (`df-06`)
- **Current state:** Two completely separate art caches on this PC: `covers\` (**227.92 MB**, 1,322 files) + `cache\rune-art\` (**127.51 MB**, 1,201 files, unbounded!) = **355.43 MB**. `remote_art.rs` does a fresh `WinHttpOpen` per image. `LocalFileHost.cs:26` rotates a random token every launch, busting 100% of Chromium's HTTP disk cache. `cloud/covers/src/worker.js:468` returns `Cache-Control: no-store` on 30-day KV hits, and only accepts 1 game per HTTP request.
- **Best in the world today:** Playnite/Steam local disk cache + CDN-cached batch metadata API.
- **Beyond SOTA:**
  1. **Single Unified Content-Addressed Art Store (`art-store/`):** Both host and Rune v4 (`exo-ui`) share ONE bounded, content-hashed LRU store (de-duplicating identical posters/banners/avatars across Library, Store, Discord, and Music, with direct memory-mapped read in Rune + pre-decoded BC7/QOI/WebP GPU-ready mip thumbnails!).
  2. **Batch + Edge-Cached `covers.getexo.app`:** Add `POST /v1/covers/batch` (up to 50 titles in 1 HTTP/2 RTT) + `Cache-Control: public, max-age=86400, stale-while-revalidate=604800` on `GET /v1/cover` hits + Worker-side `caches.default` check before KV + in-isolate singleflight coalescing for concurrent misses.
  3. **Cache-Surviving 3.x Serving:** Keep `LocalFileHost` isolation via `Sec-Fetch-Site` / custom header / stable per-install token for public game art (rotating token only for private user captures/snips) so Chromium's HTTP cache hits **100%** across restarts!
- **Beats & measurable target:** Beats Steam Big Picture & Playnite cold grid paint. Target: duplicate disk space **355 MB → <180 MB**, `covers.getexo.app` KV reads **-95%**, edge hit TTFB **91–298 ms → <15 ms**, cold 50-game cover lookup **50 RTTs → 1 RTT**, v4 Rune remote image burst **5× faster**.
- **Evidence it's possible:** `ArtBase` already sends `coversDir` to `exo-rpc` (`exo-rpc/src/lib.rs:1058`); Cloudflare Cache API + KV already exist in `cloud/covers`.
- **Risk:** Low — private captures/snips keep their per-run rotating token so security boundary is preserved.
- **Effort:** **M** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Benchmark 50-title cover resolution (single vs batch + edge cache) in `cloud/covers` workerd test + measure cold Rune Library paint with unified local cache vs `WinHttpOpen`-per-image.

---

### Rank 7: Privacy-Preserving On-Device Aggregated Sketch Telemetry (`df-07`)
- **Current state:** `Telemetry.cs` emits 1 JSON envelope per action (measured avg **254.1 B/event**, **72.4%** repetitive `room.opened`), wakes every 1 min (bug), rewrites `queue.jsonl` every 1 min, sends uncompressed JSON over per-call `HttpClient`, and stores every raw event row in Cloudflare D1 SQLite for 90 days! Meanwhile, it only has 5 coarse memory buckets and **zero** latency percentiles (p50/p95/p99) for room switches, RPCs, voice, or frame pacing.
- **Best in the world today:** Mozilla Glean (ping-based batched metrics), Apple Private Telemetry / Prio, Datadog DDSketch.
- **Beyond SOTA (genuinely new for a desktop launcher):** **On-Device Sketch & Rollup Telemetry ("Zero-Sequence Privacy")** — instead of sending an ordered stream of individual timestamped clicks (which leaks user behavior sequence and wastes 95% of bytes), Exo rolls up metrics locally over a session/day into:
  - **Counter maps:** `room.opened: { library: 14, discord: 9, pc: 3 }` (736 events = 187 KB → **80 bytes = 2,300× smaller**!);
  - **Compact DDSketch / Log2-Linear HDR Histograms** (32–64 bytes per metric!) for `startup_ms`, `library_load_ms`, `rpc_p99_ms`, `store_home_ms`, `voice_rtt_ms`, `update_mbps` — **mergeable mathematically on the server** so Exo gets true global **p50 / p90 / p95 / p99 / p99.9** across versions/builds without ever seeing a single user's timeline!
  - **Brotli-compressed POST** (`Content-Encoding: br`, measured **16.6× smaller** on real `sent.jsonl`) + Cloudflare Analytics Engine / R2 Parquet rollups instead of raw row-per-event D1 bloat.
- **Beats & measurable target:** Beats Electron/Sentry/PostHog/Mozilla Glean on bytes-per-insight and privacy. Target: telemetry wire bytes & D1 writes **-95%**, zero per-action timestamps leaving the PC, 10× richer performance visibility (exact p50/p99 across all rooms & pipelines), `View what's sent` becomes a crystal-clear human-readable summary card!
- **Evidence it's possible:** DDSketch (Masson et al., VLDB 2019) is ~60 lines of pure C#/JS with exact relative-error quantile guarantees and trivial associative bin-addition merging in D1/Workers; `BrotliStream` is built into .NET 10 and Workers decompress `Content-Encoding: br` automatically.
- **Risk:** Low — requires updating `PRIVACY.md` and `cloud/reports/src/telemetry.js` schema v2 alongside v1 backward compatibility.
- **Effort:** **M** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Replay this PC's real `telemetry/sent.jsonl` (1,016 lines, 258 KB) through a DDSketch + rollup encoder + Brotli; verify exact compressed payload size, server merge accuracy (<1% error on p99), and D1 row count reduction (1,016 rows → 1 row).

---

### Rank 8: Source-Aware Self-Healing Crash Triage & Signed Client Mitigation Feed (`df-08`)
- **Current state:** `cloud/reports/src/fingerprint.js` takes top 3 raw frames (collapsing async crashes onto `TaskAwaiter`/`ExceptionDispatchInfo`). `issues.js:125` runs Workers AI (`llama-3.3-70b`) on the first report with zero knowledge of Exo's repo, commits, or previous fixes. Triage is manual (`node triage.mjs`). Clients cannot heal from a crash storm until a full new release ships.
- **Best in the world today:** Sentry Suspected Commits + Crashlytics Velocity Alerts + Chrome Finch kill-switches.
- **Beyond SOTA (experimental / self-healing):**
  1. **Plumbing-Aware Fingerprinting + Commit-Diff Attribution:** Strip CLR/WinUI/Rust async plumbing frames; on a new crash group in `vX.Y.Z`, Worker fetches GitHub `compare/vPrev...vX.Y.Z` file list and matches touched classes/methods in the stack trace -> attaches **Suspected Commit + PR + Confidence** directly to the GitHub issue!
  2. **Closed-Loop Auto-Verification:** When a fix ships in `vX.Y.Z+1`, track crash-free sessions per version; automatically comment "Verified fixed in vX.Y.Z+1 (0 reports across N sessions)" or reopen immediately on velocity spike.
  3. **Signed Self-Healing Mitigation Feed (Zero-Release Recovery):** Extend the existing Ed25519-signed `/v1/advisories.json` (`DriverAdvisories.cs`) with bounded, declarative **safe-mode mitigations** (e.g., `disable_hevc_encode_for_driver`, `force_software_video_decode`, `disable_quic`, `prefer_full_installer_for_version`). When a crash fingerprint crosses a velocity threshold (e.g., >50 installs in 1h), the team signs a 200-byte mitigation (or client auto-trips local 2-crash circuit-breaker per subsystem!) so players heal in minutes instead of waiting for a build!
- **Beats & measurable target:** Beats Sentry + Electron crash reporters. Target: duplicate/misgrouped crash issues **-70%**, mean time to root-cause commit **hours → <30 seconds**, mean time to mitigate crash storm for players **days → <1 hour** (and **instant** via local subsystem circuit breaker!).
- **Evidence it's possible:** `cloud/reports/src/github.js` already authenticates to GitHub API; `cloud/updates/advisories.mjs` + `DriverAdvisories.cs` already implement Ed25519-signed JSON policies; `MainWindow.SafeMode.cs` already exists.
- **Risk:** Medium — signed mitigations must be strictly allowlisted boolean/enum switches (never arbitrary code/paths) and require offline maintainer Ed25519 signature (never auto-signed by AI).
- **Effort:** **L** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Run new frame-skipping fingerprinter + commit-diff correlator on 20 historical crash excerpts/unit fixtures + verify local 2-crash subsystem circuit-breaker and Ed25519 mitigation parser in `ExoLauncher.Tests`.

---

### Rank 9: Encrypted LAN Peer Cache (mDNS + Content-Addressed Chunks) & Multi-Stream Hedged Downloads (`df-09`)
- **Current state:** `ResumableDownload.cs` is single-stream, allocates a linked CTS per 64 KB chunk, and hashes the file from disk *after* writing. `UpstreamSource.cs` tries upstream sequentially then falls back to `updates.getexo.app` only after full failure/timeout. `EpicCdn.cs` probes Epic CDNs, while GOG/Amazon/updates/tools have no CDN racing or LAN sharing.
- **Best in the world today:** Steam Local Network Game Transfers (Steam-only), Windows Delivery Optimization, `aria2` multi-stream range downloads.
- **Beyond SOTA (never seen before across multi-store launchers):**
  1. **Exo LAN Mesh Cache (Zero-Trust Content-Addressed P2P):** Any two Exo PCs on the same LAN discover each other via mDNS (`_exo-cache._tcp.local`, opt-in or same Exo account / Exo friend key). Before hitting WAN for `files.pack` update chunks, upscaler DLLs, store tools, runtimes, OR Epic/GOG/Amazon downloaded archives, Exo queries LAN peers by **SHA-256 content hash** and pulls verified chunks at **1–10 Gbps LAN speed** over TLS-PSK (derived from Exo account or ephemeral ECDH)! Zero trust needed in peer honesty because every chunk is verified against the official manifest SHA-256!
  2. **Parallel Range + Inline `IncrementalHash` + Hedged Racing in `ResumableDownload.cs`:** 4 concurrent HTTP/2 range streams, zero per-chunk CTS allocation, streaming SHA-256 computed *during* download (eliminates second full-file disk read!), plus **hedged tail racing** (if upstream GitHub/vendor stalls >800 ms on TTFB, race `updates.getexo.app/v1/cache/` in parallel and cancel the loser!).
- **Beats & measurable target:** Beats Steam LAN Transfer (works across updates, upscalers, runtimes, AND Epic/GOG/Amazon) and `ResumableDownload`. Target: LAN multi-PC download speed **5–20× faster** (saturating 1–2.5 GbE LAN), WAN large download throughput **1.5–3× faster**, post-download hash wait **→ 0 ms**, GitHub/vendor stall tail **-80%**.
- **Evidence it's possible:** `AppFiles.cs` already indexes every app file by SHA-256; `VerifiedGitHubReleaseDownloader` already pins every tool/model/upscaler by SHA-256; Windows 11 supports native `DnsServiceRegister`/`DnsServiceBrowse` mDNS APIs with zero extra dependencies.
- **Risk:** Medium — must strictly bind to private LAN subnets (RFC 1918 / link-local), rate-limit outbound LAN serving while peer is gaming (`GameRunningSignal`), and never leak library lists (query by blinded/exact SHA-256 only!).
- **Effort:** **L** | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Benchmark 137 MB file download over loopback/LAN with 4-stream HTTP/2 + inline `IncrementalHash` vs current `ResumableDownload.cs`, plus prototype mDNS discovery + SHA-256 chunk serve between two local Exo test processes.

---

### Rank 10: Zero-Alloc QoS Voice/Video Transport & Push-Based Zero-Poll Music Bridge (`df-10`)
- **Current state:**
  - **Voice/Video:** `VoiceTransport.cs:336` allocates a new `ChaCha20Poly1305` CNG handle per RTP packet in XChaCha20 mode; synchronous blocking `_udp.Client.Send`; no DSCP QoS tagging; `VoiceConnection.cs:350` polls video every 20 ms (`50 Hz`) and `DiscordSession.Settings.cs:298` polls push-to-talk keys every 30 ms (`33 Hz`).
  - **Music:** `MusicNowPlaying.cs` and `MusicAppVolume.cs` run CDP `Runtime.evaluate` long-poll loops (`MusicAppVolume.cs:86` re-sends the full JS wrapper on every call); hidden Chromium WebView2 costs **150–350 MB RAM** and **1,500 ms** Apple Music `domContentLoadedMs` (measured in `app.log`).
- **Best in the world today:** Discord WebRTC (C++ libwebrtc with DSCP EF/AF41, Transport-CC, Opus FEC/RED, hardware encoder pacing) and Spotify Connect / native SMTC.
- **Beyond SOTA:**
  1. **Zero-Alloc QoS Media Transport:** Replace per-packet `ChaCha20Poly1305` CNG creation with pure-Span/reusable key-schedule AEAD; tag voice UDP with **Windows `QOS2` / DSCP EF (46)** and video with **AF41 (34)** so gaming routers prioritize voice over downloads & game bursts; add **adaptive Opus in-band FEC + packet-loss-aware bitrate/RED**; replace 30 ms `KeyTick` timer with `WM_INPUT` raw keyboard notifications (**0 Hz** idle wakeups in calls!).
  2. **Push-Driven CDP (`Runtime.addBinding`) + Native Lyrics/Art Prefetch:** Replace both `MusicNowPlaying` and `MusicAppVolume` 5s/20s long-poll timers with a single persistent `Runtime.addBinding("__exoMusicPush")` event channel (instant push on track/play/volume change, **0 polling timers**, **0 re-transmitted JS wrappers**!), and pre-fetch next-track artwork/LRCLIB lyrics when 80% through the current song so track transitions are instant.
- **Beats & measurable target:** Beats Discord Desktop's voice jitter under load and eliminates Music CDP polling wakeups. Target: voice UDP packet-send allocs **→ 0 B/packet**, call keyboard-poll wakeups **33 Hz → 0 Hz**, voice jitter under saturated upload **-40% to -60%** (with DSCP EF), Music CDP evaluations while playing **-95%** (push only on change), next-track art/lyrics latency **→ 0 ms**.
- **Evidence it's possible:** Windows `qwave.dll` (`QOSCreateHandle` / `QOSAddSocketToFlow` with `QOSTrafficTypeVoice`) works without admin privileges on Windows 11; WebView2 `CallDevToolsProtocolMethodAsync("Runtime.addBinding")` + `DevToolsProtocolEventReceived("Runtime.bindingCalled")` are standard CDP primitives already supported by Exo's WebView2 version (`1.0.4314.35`).
- **Risk:** Medium — `qwave.dll` DSCP marking is best-effort (silently skipped if router strips DSCP); `Runtime.addBinding` requires re-binding after page navigation (already tracked by `MarkNavigationStarting`).
- **Effort:** **XL** (can ship in two clean M phases: Voice QoS/Zero-Alloc + Music CDP Push) | **Applies to:** **3.x + v4**
- **<2-day proof experiment:** Measure UDP packet allocs/sec & CPU in `VoiceTransport` XChaCha20 mode (1,000 pkt/s synthetic load) before vs after zero-alloc AEAD + `QOS2` DSCP Wireshark/socket check; prototype `Runtime.addBinding` push in a test WebView2 page measuring CDP call count over 5 minutes of playback.

---

## 4. Beyond Top 10: Experimental & Frontier Dataflow Ideas (Ranks 11–18)

| Rank | Idea | Beats & Measurable Target | Effort | Target | <2-Day Proof Experiment |
| --- | --- | --- | --- | --- | --- |
| **11** | **Unified Local SQLite WAL Cache Store** replacing 25+ scattered JSON files (`library-cache`, `account-sync`, `store/*`, `upscaler-cache`, `installed-sizes`, `playtime`, `keep`, `drive-heat`) | Eliminates whole-file atomic rewrites & 105–155 KB cold JSON parses; `SQLitePCLRaw` + `Microsoft.Data.Sqlite` **already shipped** (`ExoLauncher.csproj:72`). Target: cold startup cache read **-60%**, incremental writes **100× less disk I/O**. | L | 3.x + v4 | Benchmark SQLite WAL point-update vs `LibraryDiskCache.Save` + `AccountSyncStore.Save` over 500 mutations. |
| **12** | **Source-Generated `System.Text.Json` + Arena Writer for `WebHostBridge`** across all 342 RPCs | Eliminates reflection serialization (`ExoBridgeProtocol.cs:74`) and `ArrayBufferWriter` + `.ToArray()` heap churn per RPC. Enables full Native AOT / tighter trimming on host! Target: RPC serialize CPU **-50%**, host startup JIT **-150 ms**. | L | 3.x + v4 | Convert top 15 hot RPC DTOs (`library.get`, `discord.*`, `store.home`) to `[JsonSerializable]` and benchmark with BenchmarkDotNet. |
| **13** | **Client-Side Zero-Knowledge E2EE Account Sync (CRDT + Per-Device Key Wrapping)** | Today `accounts.getexo.app` holds `item_key` wrapped under server keyring (server-side encryption at rest, not true E2EE). Deriving a vault key from password+recovery-code via Argon2id on the PC makes `accounts.getexo.app` truly zero-knowledge (even a server compromise reveals 0 settings/playtime/covers) while keeping conflict-free CRDT merges! | XL | v4 | Prototype client-side Argon2id + AES-256-GCM item envelope + recovery-code unwrap in `AccountSyncTests`. |
| **14** | **Predictive Markov Room-Prefetch & Pre-Warm Engine** | Track local transition probabilities (`Library → Discord`, `Library → Game Page → Store`) on-device; pre-warm DNS/TLS + hydrate room cache 200 ms before click (or on Bar tab hover >80 ms) with cancellation on pointer leave. Target: perceived room load **→ 0 ms** for top-2 transitions. | S | 3.x + v4 | Log transition hit-rate on local `sent.jsonl` `room.opened` sequence; wire hover-warm for `store.home` & `discord.home`. |
| **15** | **Binary Shared-Memory IPC for `ExoCapture` & `ExoShaders`** | Replace NDJSON stdio (`CaptureAddon.cs`, `ShadersAddon.cs`) with lightweight length-prefixed binary frames + shared ring for Go Live H.264/HEVC bitstreams & shader compile progress. Eliminates JSON `DeepClone` + `Task.Run(WriteAsync)` overhead. | M | v4 | Benchmark `ExoCapture` Go Live 1440p60 bitstream latency over anonymous pipe vs MMF ring. |
| **16** | **Cloudflare Edge-Cached Store Home & Price Aggregator (`store.getexo.app` optional mirror)** | Today every Exo client independently hits Steam `IStoreQueryService` + `GetItems` + `CheapShark` (cold `store.home` = **969 ms** measured in `app.log`, and CheapShark 429s hit users!). A tiny CF Worker caching public region-keyed (`US`/`EU`/`GB`) Steam ranked lists + CheapShark deals for 15 min at the edge cuts cold Store Home from **969 ms → <40 ms** (24× faster!) while keeping `ForYou` personalization 100% local on the PC! | M | 3.x + v4 | Prototype read-only CF Worker caching Steam `home` by country code + measure TTFB from this PC (<40 ms vs 969 ms). |
| **17** | **WebTransport / QUIC Datagrams for Exo Relay & Call Presence** | Upgrade `relay/src/worker.js` from TCP WebSockets to WebTransport / QUIC unreliable datagrams for ephemeral call-song & typing/presence updates (eliminating TCP head-of-line blocking on lossy Wi-Fi while keeping WebSocket fallback). | L | v4 | Test CF Workers WebTransport support + measure packet-loss tail latency vs WSS under 3% synthetic loss. |
| **18** | **Live Dataflow & Network Budget Inspector (Dev/Pro Diagnostic Drawer)** | Real-time per-host byte counter, RTT sparkline, active connection pool view, timer wakeup rate, and IPC queue depth built directly on `ExoHttp` + `NamedPipeHostTransport` counters (zero external tools needed to catch regressions like B3/B5 in E2E!). | M | 3.x + v4 | Add lightweight `Interlocked` byte/request/wakeup counters to `ExoHttp` & `ExoScheduler` and assert budgets in `tools/e2e`. |

---

## 5. Summary Matrix of Ready-to-Dispatch Top 10 Specs

All 10 specs live in `improve/scout-dataflow-specs/` in `lab-voice-ns.dispatch.md` format:

| # | Spec File | Effort | Target | Headline Target |
| --- | --- | --- | --- | --- |
| 1 | `df-01-discord-zstd-gateway-and-rest-buckets.dispatch.md` | S | 3.x + v4 | -85–92% gateway bytes, 3–5× faster READY, 0 burst 429s |
| 2 | `df-02-exo-http-fabric-and-adaptive-quic.dispatch.md` | M | 3.x + v4 | 51→4 pools, 0 per-call clients, adaptive QUIC (0 stalls), singleflight |
| 3 | `df-03-zero-copy-shm-ipc-and-binary-codec.dispatch.md` | L | v4 (+3.x) | RPC p99 2,480→<100 µs, 0 B CPU video copy via DXGI shared handle, `library.patch` |
| 4 | `df-04-sync-event-push-and-zero-idle-wakeups.dispatch.md` | M | 3.x + v4 | -99.6% idle sync calls, -300 MB/day SSD writes, <150 ms cross-PC sync |
| 5 | `df-05-subfile-binary-delta-and-background-staging.dispatch.md` | M | 3.x + v4 | Hotfix 12.5 MB→<800 KB, in-game update 137 MB→<1 MB, fix live 304 bug |
| 6 | `df-06-unified-art-cache-and-batch-cover-edge.dispatch.md` | M | 3.x + v4 | -175 MB duplicate art, -95% KV reads, 50→1 cover RTTs, cross-run cache hits |
| 7 | `df-07-on-device-sketch-telemetry.dispatch.md` | M | 3.x + v4 | -95% bytes & D1 writes, zero timeline tracking, true p50/p99 histograms |
| 8 | `df-08-self-healing-crash-triage.dispatch.md` | L | 3.x + v4 | -70% misgrouped crashes, <30 s commit attribution, signed zero-release mitigations |
| 9 | `df-09-lan-peer-cache-and-multi-cdn-racing.dispatch.md` | L | 3.x + v4 | 1–10 Gbps LAN peer transfers, 4-stream HTTP/2 + inline SHA-256 + hedged fallback |
| 10 | `df-10-direct-voice-video-qos-and-zero-cdp-music.dispatch.md` | XL | 3.x + v4 | 0 B/pkt voice allocs, DSCP EF QoS, 0 Hz key poll, push-only Music CDP |
