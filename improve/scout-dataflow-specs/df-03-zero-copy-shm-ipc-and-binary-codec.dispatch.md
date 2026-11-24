EXO DATAFLOW: Zero-Copy Shared-Memory Ring IPC, Binary Framing & DXGI Shared-Texture Video Pipeline (task-name: df-03-zero-copy-shm-ipc)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

In v4, Exo's native Rust/Rune UI (`exo-ui.exe`) talks to the .NET 10 host (`ExoLauncher.exe`) over local IPC. Making that link sub-15 µs for control RPCs, delta-patched for library updates, and literally **0-byte CPU copy (GPU→GPU)** for Go Live & Discord video beats Electron Mojo, Tauri, Steam, and Discord Desktop.

## Today

- `ExoLauncher/Services/NamedPipeHostTransport.cs` + `native-ui/crates/exo-rpc/src/{lib.rs, pipe.rs}` use newline-delimited UTF-8 JSON over named pipes (`64 KB` buffers) plus a secondary `-media` pipe.
- **Measured on this Ryzen 5 5600X PC:**
  - Small RPC over named pipe + UTF-8→UTF-16→`JsonDocument` (`EmitLine:240` + `TryParseRequest:107`): **p50=36.4 µs, p95=309.9 µs, p99=2,479.9 µs**.
  - Shared-memory SPSC ring + binary framing prototype on this same PC: **p50=10.2 µs (3.6×), p95=19.8 µs (15.6×), p99=98.3 µs (25.2×)**.
  - 128 KB video frame over media pipe (`2 WriteAsync` in C# `:396-397` + `3 ReadFile` + zero-init `vec![0u8; frame_len]` in Rust `lib.rs:702-727`): **431.4 µs/frame (289.8 MB/s)** vs **257.3 µs/frame (485.9 MB/s)** in an 8-slot SHM ring.
- **Active bugs today:**
  - `exo-rpc/src/lib.rs:640` sends `params: { "id": ... }` for `rpc.cancel`, while `WebHostBridge.cs:603` reads `"requestId"` — **100% of Rune cancellations silently dropped!**
  - `NamedPipeHostTransport.cs:19` caps inbound messages at `4 MB`, killing the pipe on `capture.saveImage` (90 MB), `library.folderArt` (20 MB), and `discord.camera` (8 MB).
  - `WebHostBridge.cs:538-551` re-serializes + SHA-256 hashes the entire 105 KB library on every cover/playtime tick, and ~95% of RPC switch arms use `.ConfigureAwait(true)` bouncing completions to the WinUI UI thread.
  - 3.x `WebViewHostTransport.cs:82` calls `CreateSharedBuffer` per video frame (60 OS section creates/sec).

## 1. Benchmark first, with real numbers

- Run `cargo test -p exo-rpc bench_dispatch_cost --release -- --ignored --nocapture` and a live C#↔Rust end-to-end IPC benchmark measuring:
  - Small RPC round-trip p50/p95/p99 (10,000 calls).
  - 250-game `library.get` + single-game cover/playtime mutation (`library.updated` vs `library.patch`).
  - 1440p60 Go Live / Discord video frame latency & CPU % (media pipe vs SHM ring vs DXGI shared texture handle).
- **Targets:** Small RPC **p50 <15 µs, p99 <100 µs**; single-game mutation wire bytes **105 KB → <500 B (210×)**; Go Live / video CPU copy **→ 0 B/s** via DXGI shared handle (with SHM ring fallback at >450 MB/s); 3.x `CreateSharedBuffer` allocs **60/s → 0/s**.

## 2. Architecture & implementation

1. **Fix Immediate Correctness Bugs First:**
   - Fix `rpc.cancel` parameter name (`requestId`) in `exo-rpc/src/lib.rs:640` (and accept both `"requestId"` and `"id"` in `WebHostBridge.cs:603`).
   - Eliminate UTF-8→UTF-16→UTF-8 double transcode in `NamedPipeHostTransport.EmitLine` + `ExoBridgeProtocol.TryParseRequest(ReadOnlyMemory<byte>)`.
   - Align `NamedPipeHostTransport` inbound limit with `ExoBridgeProtocol.MaxCharactersFor(method)` (or stream bulk uploads over SHM).
   - Switch non-UI RPC arms in `WebHostBridge.cs` from `.ConfigureAwait(true)` to `.ConfigureAwait(false)`!
2. **Lock-Free Dual SPSC Shared-Memory Ring Transport (`ShmHostTransport.cs` + `exo-rpc/src/shm.rs`):**
   - Bootstrap over the existing ACL-hardened named pipe (`shell.bootstrap` negotiates `shmName`, ring sizes, and protocol capability flags: `shm-v1`, `msgpack-v1`, `dxgi-video-v1`).
   - Two cache-line-aligned lock-free SPSC rings (Host→UI 4 MB, UI→Host 2 MB) backed by an ACL-hardened `MemoryMappedFile` + two Windows auto-reset events (or `WaitOnAddress` hybrid spin-then-sleep for <10 µs wakeups).
   - Compact binary frame header (`magic u16, flags u16, msg_type u8, codec u8, len u32, req_id u64`) carrying UTF-8 JSON (v1) or binary MessagePack/bincode (v2) with zero per-frame allocations!
   - Keep `NamedPipeHostTransport` as automatic fallback if SHM setup fails.
3. **Zero-Copy GPU→GPU DXGI Shared Texture Handle Video Path (v4):**
   - When `ExoCapture` or the host's hardware MF decoder produces a decoded/captured D3D11/D3D12 NV12/BGRA surface, export an NT shared handle (`IDXGIResource1::CreateSharedHandle`) + shared fence handle (`DuplicateHandle` into `exo-ui.exe`), and send only a 48-byte descriptor over the SHM ring (`handle u64, fence u64, fence_val u64, width u16, height u16, fmt u8, pts f64`).
   - `exo-ui` imports the shared `ID3D12Resource` directly into `wgpu` DX12 and renders with **0 bytes copied through CPU RAM**. Fall back cleanly to the pooled SHM bitstream ring when hardware decode isn't available.
4. **Incremental `library.patch` Events & 3.x Shared-Buffer Pool:**
   - Diff game entries by per-game revision/hash in `WebHostBridge.OnLibraryUpdated` and emit `library.patch` (`{ upsert: [...], remove: [...] }`) when <25% of games changed.
   - In `WebViewHostTransport.cs`, replace per-frame `CreateSharedBuffer` with a reusable 3-slot shared buffer pool.

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64`
- `cargo test --workspace` in `apps/launcher/native-ui`
- `npm run check` in `ui/` (and `npm run contracts:write` for `library.patch` / bootstrap fields)

## 4. Deliver

- Branch `lab/df-03-zero-copy-shm-ipc` from `origin/main`.
- `improve/df-03-zero-copy-shm-ipc-report.md` with real p50/p95/p99 RPC numbers, `library.patch` byte savings, and 1440p60 video CPU/GPU copy telemetry.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-03 zero-copy SHM IPC & DXGI video" --body "<RPC p50/p99 improvement, library.patch savings, DXGI video CPU copy result>"`. Effort: high.
