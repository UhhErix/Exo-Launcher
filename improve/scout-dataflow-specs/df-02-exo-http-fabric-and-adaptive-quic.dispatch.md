EXO DATAFLOW: Unified ExoHttp Shared Transport Fabric, Adaptive QUIC Racing & Singleflight Coalescing (task-name: df-02-exo-http-fabric)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

Every network call in Exo's .NET host and v4 Rust `exo-rpc` should share warm multiplexed connections, negotiate compression everywhere, coalesce duplicate in-flight requests, and exploit HTTP/3 QUIC where the user's network actually makes it faster — without ever stalling on UDP-impaired networks.

## Today

- `ExoLauncher/Helpers/ExoHttp.cs:13` creates an isolated `new HttpClient()` with its own `SocketsHttpHandler` on every call. There are **51 separate `ExoHttp.Create(...)` call sites** across `ExoLauncher/`, **12 of which** are per-call `using var http = ExoHttp.Create(...)` (`Telemetry.cs:453`, `CrashReport.cs:291`, `UserReport.cs:90`, `DiscordRest.cs:116`, `SteamStoreAppTypes.cs:38`, `WebHostBridge.Properties.cs:310`, `StoreGifts.cs:237`, `DependencyService.cs:142`, `DependencyService.Dotnet.cs:141`, `EpicNewLauncherInstaller.cs:50`, `RiotHoursClient.cs:102`, `TrackerGgPlaytime.cs:374`).
- `CoverArtService.cs:2149` wraps legacy `HttpClientHandler`; `AppUpdateService.cs:89` pins `HttpVersion.Version11`; `ExoAccountClient.cs:57` omits `AutomaticDecompression` and `PooledConnectionLifetime`.
- `native-ui/crates/exo-rpc/src/remote_art.rs:126-150` calls `WinHttpOpen()` → `WinHttpCloseHandle()` on every single image fetch (no session reuse, HTTP/1.1 only, no decompression).
- **Live PC measurement warning:** On this PC, naive `DefaultRequestVersion = HttpVersion.Version30` timed out after 10,000 ms on `updates.getexo.app` and stalled 4,247 ms on `covers.getexo.app`, while HTTP/2 succeeded cleanly in 298–478 ms!

## 1. Benchmark first, with real numbers

- Measure before & after on this PC:
  - Total unique `SocketsHttpHandler` instances, TLS handshakes, and open sockets during cold startup + Store Home + 50-cover warm + account probe + update check.
  - Cold `store.home` latency (baseline measured in `app.log`: **969 ms**).
  - 50-image fetch burst in Rust `remote_art.rs` (per-call `WinHttpOpen` vs persistent HTTP/2 session).
  - Adaptive QUIC probe on this network (must detect slow/blackholed UDP 443 in <150 ms and stay on HTTP/2 with **0 stalls**) + simulated fast-QUIC loopback/LAN test proving promotion when H3 beats H2.
- **Targets:** Handler pools **51 → 4**, per-call `HttpClient` constructions **12 → 0**, startup TLS handshakes **-40% to -70%**, `store.home` cold **<450 ms**, `remote_art` 50-image burst **≥3× faster**, **0 QUIC timeout stalls**.

## 2. Architecture & implementation

1. **Profiled Shared `ExoHttp` Pools (`Helpers/ExoHttp.cs`):**
   - Provide 4 long-lived static `SocketsHttpHandler` pools with `PooledConnectionLifetime = TimeSpan.FromMinutes(5)`, `PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)`, `EnableMultipleHttp2Connections = true`, `ConnectTimeout = TimeSpan.FromSeconds(8)`:
     - `ExoHttp.Public` (`AutomaticDecompression = All`, `UseCookies = false`, `AllowAutoRedirect = true`)
     - `ExoHttp.Strict` (`AutomaticDecompression = All`, `UseCookies = false`, `AllowAutoRedirect = false` — for auth, accounts, covers, GitHub/upstream verified hops)
     - `ExoHttp.RawRange` (`AutomaticDecompression = None`, `UseCookies = false`, `AllowAutoRedirect = false` — for byte-range `files.pack` & `ResumableDownload`)
     - `ExoHttp.Loopback` (dedicated local Riot/loopback handler)
   - Keep `ExoHttp.Create(...)` defaulting to HTTP/2 (`Version20`, `RequestVersionOrLower`) over the matching shared pool; migrate all 51 call sites (especially the 12 per-call `using` sites and `CoverArtService.cs:2149`).
   - Preserve every `NetworkDestinations.cs` and `BannedSymbols.txt` (`RS0030`) invariant!
2. **Adaptive QUIC / HTTP/3 Circuit-Breaker (`Helpers/QuicHealth.cs`):**
   - Never blindly set `Version30` globally! Instead, key a lightweight per-network-profile state (hashed default gateway / network change event `NetworkChange.NetworkAddressChanged`):
     - On a new network, run a single background non-blocking 150 ms race (`H2` vs `H3` HEAD to `updates.getexo.app`).
     - Promote Cloudflare hosts (`*.getexo.app`, `discord.com`) to `Version30` ONLY when H3 wins cleanly; demote immediately to `Version20` on any `QuicException` or >500 ms H3 stall for 30 minutes.
3. **Singleflight Request Coalescer (`Helpers/Singleflight.cs`):**
   - Deduplicate concurrent identical idempotent GETs (keyed by method + URI + relevant headers) across `CoverArtService`, `StoreService`, `CoverCacheClient`, and `DeviceImages` so hover-warm + click or parallel rooms trigger 1 wire request.
4. **Rust `exo-rpc/src/remote_art.rs` Persistent HTTP/2 Session:**
   - Replace per-image `WinHttpOpen` with a process-wide `OnceLock<WinHttpSession>` with `WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL` (HTTP/2) and `WINHTTP_OPTION_DECOMPRESSION` + bounded concurrency semaphore + in-flight singleflight map.

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64`
- `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (including `NetworkDestinationsTests` + new `ExoHttpPoolTests`, `QuicHealthTests`, `SingleflightTests`).
- `cargo test -p exo-rpc` in `apps/launcher/native-ui`.

## 4. Deliver

- Branch `lab/df-02-exo-http-fabric` from `origin/main`.
- `improve/df-02-exo-http-fabric-report.md` with before/after TLS handshake counts, socket counts, `store.home` ms, `remote_art` burst ms, and adaptive QUIC verification.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-02 ExoHttp fabric & adaptive QUIC" --body "<pool counts, TLS reduction, store/art speedup, QUIC circuit-breaker result>"`. Effort: high.
