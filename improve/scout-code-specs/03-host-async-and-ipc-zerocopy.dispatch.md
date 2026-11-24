EXO LAB: Kill ThreadPool Starvation, Sync-Over-Async, UI-Thread RPC Serialization & Blocking Rune Boot (task-name: host-async-and-ipc-zerocopy)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## What the owner asked (2026-11-24)

> "check every single thing that we have in the app ... how everything gets sent ... the code itself ... every little nook and cranny, to see if we can make something better, more experimental, not seen before."
> Vision: instant, zero-hitch responsiveness on every PC — never stall a Play button, room switch, or Discord message behind background scans.

## Today (Measured in `app.log` & Codebase Audit)

- Real `app.log` RPC latency spikes on Ryzen 5 5600X:
  - `controller.read` max **20,088 ms** (P95 14,584 ms), `discord.profile` **16,133 ms**, `discord.messages` **16,103 ms**, `applemusic.act` **15,048 ms**, `system.drivers` **13,886 ms**, `dlss.status` **7,126 ms**, `dlss.frameGen` **6,554 ms**, `junk.scan` **5,742 ms**, `system.drives` **3,999 ms**, `stores.matrix` **343 ms** at boot (`pool=33/0` active threads!).
- Root causes located by audit:
  1. `ExoLauncher/Program.cs:48`: `ThreadPool.SetMinThreads(Math.Max(workers, 32), Math.Max(io, 16))` band-aid hiding **122 sync-over-async** calls (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`), **29 `Thread.Sleep`**, and **1,256 `lock (`** across `ExoLauncher/`.
  2. `ExoLauncher/Services/WebHostBridge.cs:624-996`: Almost every RPC arm uses `.ConfigureAwait(true)`, resuming onto the single UI `DispatcherQueue` thread to serialize JSON and run post-processing!
  3. `ExoLauncher/Services/WebHostBridge.cs:538-551` & `NamedPipeHostTransport.cs:27, 87-91`: `OnLibraryUpdated` serializes a giant LOH UTF-16 `string` and re-encodes UTF-8 `byte[]` (bypassing `SerializeEventUtf8`); `_reliable` is `Channel.CreateUnbounded<byte[]>()` (OOM risk if client stalls).
  4. `ExoLauncher/Services/AppServices.cs:23-46`: **8 non-thread-safe `??=` lazy singletons** (`ShadersHost`, `HdrHost`, `HeadlessCapture`, `HeadlessMusic`, `MusicEngine`, `ShaderPrepare`, `Downloads`, `Compressor`).
  5. `ExoLauncher/Services/MusicEngineService.cs:361-391`: No backoff/circuit-breaker on WebView2 `COMException: The requested resource is in use.` -> **271 WARNs** in `app.log`.
  6. `ExoLauncher/Helpers/ExoHttp.cs:13-39`: 34 separate `HttpClient` pools (9 per-call `using var http = ExoHttp.Create(...)` causing TCP/TLS churn) + `new byte[64 * 1024]` heap allocation per `ReadBodyLimitedAsync`.
  7. `native-ui/crates/exo-ui/src/state.rs:502-573`: `preload_visible_covers_sync` calls `img.wait()` synchronously on up to **~200 images** on the UI thread during startup!
  8. `native-ui/crates/exo-rpc/src/pipe.rs:69`: fixed **40 ms `thread::sleep`** on `ERROR_FILE_NOT_FOUND` startup race; `exo-rpc/src/lib.rs:566-573`: `json!({...})` DOM heap allocation before `to_string()` on every RPC; unbounded `mpsc` channels & unbatched `WriteFile` syscalls.

## 1. Benchmark Harness First

- Build a repeatable local stress benchmark (host `--host-pipe` + Rust RPC client harness): trigger `junk.scan` + `dlss.status` + `system.drives` + `library.refresh` concurrently while firing 200 interactive RPCs (`game.get`, `settings.get`, `discord.send` mock) and measuring P50/P95/P99 latency, GC Gen0/Gen1/Gen2 allocations (`dotnet-counters` / `GC.GetTotalAllocatedBytes`), thread count, and `exo-ui` cold boot to first frame.

## 2. Host Concurrency & Zero-Copy Fixes

1. **3-Lane Bounded Priority Scheduler (`RpcScheduler`):** Classify every RPC into `Interactive` (never waits on scans), `Io` (network/disk reads), or `BackgroundScan` (`junk.scan`, `dlss.status`, `system.drives`, `system.drivers`, `apps.tidy` — capped at `min(4, cores/2)` concurrency). Eliminate the `SetMinThreads(32, 16)` band-aid once sync-over-async hotspots are fixed.
2. **Offload Serialization Off the UI Thread:** Default `WebHostBridge.cs` dispatch arms to `.ConfigureAwait(false)` and run `PostResponse` serialization on the ThreadPool; only hop to `_queue` inside the small set of arms that touch WinUI/WebView2 STA APIs.
3. **Zero-Allocation Pipe Writes & Bounded Channels:** Route `OnLibraryUpdated` through `SerializeEventUtf8` + pooled `ArrayBufferWriter<byte>`; bound `_reliable` channel in `NamedPipeHostTransport.cs:27` with clean disconnect on overflow.
4. **Fix Concrete Concurrency Bugs:**
   - Make all 8 lazy singletons in `AppServices.cs:23-46` thread-safe (`Lazy<T>`).
   - Add exponential backoff + serialization to `MusicEngineService.cs:361-391`.
   - Share a pooled `SocketsHttpHandler` (`PooledConnectionLifetime = 2m`) in `ExoHttp.Create` and `ArrayPool<byte>` in `ReadBodyLimitedAsync`.
   - Replace the top sync-over-async clusters (`SystemTuning.cs`, `InstalledSizeCache.cs`, `DlssSwapService.SessionMods.cs`, `PcHealth.cs`, `NetworkBest.Native.cs`, `SteamClientIpc.cs`) with true `async/await` + `CancellationToken`.
   - Guard all 3 production `async void` methods (`MainWindow.xaml.cs:96`, `WebPageGuard.cs:94`, `StoreAuthWindow.cs:172`).

## 3. Native UI (`exo-rpc` & `exo-ui`) Fixes + Experimental Shared-Memory Fast Lane

- Replace `preload_visible_covers_sync` blocking `img.wait()` (`exo-ui/src/state.rs:502-573`) with async `ImageCache` warm-up driven by `Waker` (keep synchronous wait ONLY behind `--sync-preload` for deterministic parity screenshots!).
- Replace `pipe.rs:69` `sleep(40ms)` with exponential backoff (`2 → 4 → 8 → 16 ms`).
- Replace `exo-rpc/src/lib.rs:566-573` `json!({...})` DOM allocation with a borrowed `#[derive(Serialize)] struct RpcRequest<'a, P>` serialized straight to `Vec<u8>` + writer coalescing batching.
- **Experimental fast lane (proof):** Prototype a Windows shared-memory SPSC ring buffer (`CreateFileMappingW` + `MapViewOfFile` + atomic head/tail + `Event` wake) between `NamedPipeHostTransport` and `exo-rpc` for 60 Hz `controller.live` + Discord video frames, measuring latency & CPU vs named-pipe framing.

## 4. Targets & Deliver

- **Targets:** Interactive RPC P99 `<20 ms` under full background scan load; `-40%` RPC GC allocation bytes; zero `MusicEngineService` retry storms; `exo-ui` first-frame unblocked (`<50 ms` to render shell); zero threadpool oversubscription (`<=16` threads peak).
- Branch `lab/host-async-and-ipc-zerocopy` from `origin/main`.
- `improve/host-async-and-ipc-zerocopy-report.md`: before/after latency percentiles, GC alloc bytes, thread counts, shared-memory ring-buffer benchmark, and full gate outputs (`ExoLauncher.Tests`, `cargo test --workspace --locked`, `ui` `npm run check`).
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "host async and IPC zero-copy results" --body "<interactive RPC P99 before/after, GC alloc delta, exo-ui startup delta, gate results>"`. Effort: high.
