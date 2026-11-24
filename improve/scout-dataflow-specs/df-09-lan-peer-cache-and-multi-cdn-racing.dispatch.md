EXO DATAFLOW: Zero-Trust Encrypted LAN Peer Cache (mDNS) & Multi-Stream Hedged Downloads (task-name: df-09-lan-peer-cache)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

In households, dorms, or LAN parties with 2+ PCs running Exo (or a player with a desktop + laptop), re-downloading Exo updates, upscalers, runtimes, store tools, and Epic/GOG/Amazon game archives over WAN wastes hours. Meanwhile, Exo's own `ResumableDownload.cs` still downloads on a single stream and hashes from disk after writing.

## Today

- `ExoLauncher/Helpers/ResumableDownload.cs:108-125` runs a **single** HTTP stream, allocates a linked `CancellationTokenSource` + timer + `AsTask()` box on **every 64 KB chunk** (`:110, 148-155` ≈ 2,140 allocs per 137 MB installer!), writes to disk, closes the file, and relies on callers to re-read the entire file from disk to compute SHA-256!
- `ExoLauncher/Services/UpstreamSource.cs:69-73` tries upstream (GitHub / DLSS Swapper) sequentially and only falls back to `updates.getexo.app/v1/cache/` after a full failure or multi-second timeout.
- `ExoLauncher/Adapters/Cli/EpicCdn.cs` measures Epic CDNs well, but GOG, Amazon, runtimes, and Exo updates have no multi-CDN racing or LAN peer cache.
- Zero LAN discovery or peer-to-peer chunk sharing exists in Exo today.

## 1. Benchmark first, with real numbers

- Benchmark on this PC:
  - 137 MB download over loopback/LAN: current single-stream `ResumableDownload.cs` + post-write SHA-256 vs **4-stream parallel range + inline `IncrementalHash` + zero per-chunk CTS allocation**.
  - Simulated upstream stall (2 s TTFB delay on GitHub mock) with sequential fallback vs **800 ms hedged race** against `updates.getexo.app` cache mock.
  - Two local Exo test processes discovering each other via Windows native mDNS and transferring SHA-256-verified chunks over TLS-PSK (measure discovery ms, transfer MB/s, CPU %, and hash verification overhead).
- **Targets:** WAN large download throughput **1.5–3× faster**, post-download hash wait **→ 0 ms**, upstream stall tail **-80%**, LAN peer transfer **>500 MB/s on loopback / saturating 1–2.5 GbE LAN**, zero library metadata leakage.

## 2. Architecture & implementation

1. **Upgraded `ResumableDownload.cs` (Zero-Alloc Parallel Range + Inline SHA-256):**
   - Replace per-chunk `CreateLinkedTokenSource` with a single reusable idle watchdog (`Timer` / `CancellationTokenSource.TryReset()`).
   - For files ≥8 MB when server advertises `Accept-Ranges: bytes` and strong `ETag`, fetch 4 parallel byte-range segments into pre-allocated file offsets (with atomic `.json` bitmap of completed segments for instant crash resume!).
   - Compute SHA-256 inline via `IncrementalHash` on single-stream transfers (and parallel per-segment SHA-256 / tree verify or single sequential pass while pages are hot in OS page cache), eliminating cold disk re-reads.
2. **Hedged Upstream/Cache Racing (`Services/UpstreamSource.cs`):**
   - When `SourcesFor(cacheUrl)` has both an upstream URL and Exo's R2 cache URL (`updates.getexo.app/v1/cache/`), start upstream first; if response headers don't arrive within **800 ms**, fire a hedged request to Exo's R2 cache in parallel, take whichever returns 200 OK first, and cancel the loser! Both paths enforce the exact same SHA-256 / Authenticode checks.
3. **Zero-Trust Encrypted LAN Peer Cache (`Services/LanCache/`):**
   - Opt-in (or default on for **Same Exo Account** devices; toggle in Settings › Downloads: "Share updates & downloads on local network").
   - Discovery via Windows 11 native mDNS (`DnsServiceRegister` / `DnsServiceBrowse` in `dnsapi.dll`, `_exo-cache._tcp.local`, **zero external deps**), strictly bound to RFC 1918 / link-local interfaces.
   - Privacy & Security:
     - Peers NEVER list their games or files! A requester sends a blinded probe (`HMAC(lanKey, sha256)`) for the exact content SHA-256 it already has from an official manifest (`files.json`, pinned tool/upscaler/runtime, or Epic/GOG/Amazon archive).
     - Transport encrypted with TLS 1.3 (ephemeral self-signed cert pinned via mDNS TXT fingerprint + PSK derived from Exo Account or friend ECDH key).
     - Every received chunk is verified against the official SHA-256 manifest before touching disk (zero trust in peer integrity!).
     - Automatic yield: outbound LAN serving pauses immediately whenever `GameRunningSignal.Running` is true or CPU/disk load exceeds threshold.

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (add `ResumableDownloadParallelTests`, `HedgedUpstreamTests`, `LanCacheProtocolTests` including corrupt-peer-chunk rejection, subnet enforcement, and game-running pause).
- Update `PRIVACY.md` & `NetworkDestinations.cs` tests if any new setting/behavior is documented.

## 4. Deliver

- Branch `lab/df-09-lan-peer-cache` from `origin/main`.
- `improve/df-09-lan-peer-cache-report.md` with throughput benchmarks, hedged stall numbers, LAN transfer MB/s, and security threat-model checklist.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-09 LAN peer cache & hedged downloads" --body "<parallel download speedup, hedged tail reduction, LAN MB/s, security/test status>"`. Effort: high.
