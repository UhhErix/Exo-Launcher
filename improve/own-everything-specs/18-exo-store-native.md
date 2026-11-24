# Spec 18 — Exo Store Engine (`exo-stores`): Clean-Room MIT Rust Epic/GOG/Amazon Clients & Native Steam IPC

- **Inventory Rank:** #18 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `XL`
- **Replaces:** 3 downloaded GPL-3.0 Python/PyInstaller CLIs (**`Legendary` ~17 MB**, **`gogdl` ~12 MB**, **`Nile` ~15 MB** = **~44 MB** of embedded Python runtimes!) AND `.NET` NativeAOT `ExoLauncher.SteamIpc.exe` (2.86 MB) (`Adapters/Cli/LegendaryCli.cs`, `GogdlCli.cs`, `NileCli.cs`, `PinnedToolCache.cs`, `ExoLauncher.SteamIpc/`)
- **Anti-Cheat Class:** `AC-SAFE` (official authenticated storefront APIs, official CDN chunk downloads, official Steam client IPC — **ZERO DRM circumvention, ZERO CDN scraping, ZERO fake store replacement!**)
- **License:** MIT (100% clean-room Rust — eliminates all 3 GPL-3.0 third-party store CLIs!)

---

## 1. Scope

### What It Solves
1. Every Epic, GOG, or Amazon library scan, install, update, verify, or launch currently spawns a **12–17 MB PyInstaller executable** (`legendary.exe`, `gogdl.exe`, `nile.exe`) that unpacks an embedded Python interpreter on startup (**~400–800 ms** cold start per invocation!), logs text to stdout that C# fragilely regex-parses (`CliProgress.cs`), and carries a GPL-3.0 runtime boundary (`PinnedToolCache` hashes 17 MB/12 MB files to verify them).
2. `ExoLauncher.SteamIpc.exe` is a separate `.NET` NativeAOT child process (`2.86 MB`) talking to Steam's client — moving it to Rust unifies every store backend in one workspace!

### In Scope
- **4 Clean-Room Native Rust Store Backends (Zero Subprocess Spawning, Zero Python):**
  1. **`epic` (Replaces `Legendary`):** Authenticated Epic Games OAuth session (DPAPI-sealed token), library metadata & optional-content (`DLC`/selective language packs) query, binary manifest v2/v3 parser, **parallel multi-CDN racing** (absorbs `EpicCdn.cs` 256 KB probe!), concurrent chunk download + zlib/zstd decompression + SHA-1/SHA-256 verification directly to pre-allocated NVMe files, cloud-save sync, and official game launch with valid Exchange Code (zero DRM bypass!).
  2. **`gog` (Replaces `gogdl`):** Authenticated GOG Galaxy OAuth session, library/builds/depots v2 manifest parser, parallel multi-stream chunk downloader + MD5/SHA-256 verify, Galaxy achievements & cloud-save sync, offline installer/game launch.
  3. **`amazon` (Replaces `Nile`):** Authenticated Amazon Games Fuel API client, library & protobuf/JSON manifest downloader, parallel verified chunk installer & launcher.
  4. **`steam_ipc` (Replaces `ExoLauncher.SteamIpc.exe`):** Clean-room Rust Steam client IPC helper + VDF/ACF/appinfo parser (`SteamKeyValues`, `SteamAppInfoNames`) driving the user's installed official Steam client cleanly (`AGENTS.md` Hard Stop #2 respected!).
- **Unified Resumable High-Speed Chunk Pipeline (`downloader.rs`):**
  - HTTP/2 + HTTP/3 connection pooling, automatic CDN failover, byte-range resume after disconnect, NVMe sequential write coalescing, and automatic bandwidth throttling when a game launches!

### Out of Scope
- **HARD STOP (`AGENTS.md` #2):** Never fake Steam/Riot/Xbox/EA/Ubisoft/Battle.net replacement, never bypass DRM, never scrape unauthenticated CDNs, never use `DepotDownloader` for Steam (Steam installs/updates MUST continue through the official Steam client IPC!).
- Copying any GPL-3.0 code from `Legendary`, `gogdl`, or `Nile` (implement strictly clean-room from public API contracts and standard manifest formats!).

---

## 2. Native Rust Crate Layout

```text
crates/exo-stores/
├── Cargo.toml                  # reqwest/winhttp, tokio, flate2, zstd, sha1, sha2, blake3, serde, windows
├── src/
│   ├── lib.rs                  # Trait StoreBackend + unified StoreCatalog + InstallJobEvent stream
│   ├── downloader.rs           # Shared parallel chunk downloader, multi-CDN racer, NVMe pre-alloc & resume ledger
│   ├── epic/
│   │   ├── auth.rs             # OAuth token refresh + DPAPI vault + one-time launch Exchange Code
│   │   ├── manifest.rs         # Clean-room Epic binary/JSON chunk manifest parser & delta patch planner
│   │   └── client.rs           # Library list, DLC selective install, cloud saves, official launch
│   ├── gog/
│   │   ├── auth.rs             # GOG Galaxy OAuth refresh
│   │   ├── manifest.rs         # GOG v2 depot/chunk manifest parser & delta planner
│   │   └── client.rs           # Library, builds, achievements, cloud saves, launch
│   ├── amazon/
│   │   ├── auth.rs             # Amazon device/OAuth token handler
│   │   └── client.rs           # Fuel library & chunk download/launch
│   └── steam/
│       ├── vdf.rs              # Zero-copy binary & text Valve KeyValues / appinfo.vdf / libraryfolders.vdf parser
│       └── ipc.rs              # Native Rust Steam client IPC (replaces ExoLauncher.SteamIpc.exe!)
```

---

## 3. Automation & Intelligence Design

1. **Automatic Fastest-CDN Racing:** Before every Epic/GOG/Amazon download, probes every CDN host named by the store's manifest in parallel (`256 KB` timed fetch) and pins all workers to the lowest-RTT / highest-throughput edge automatically — seamlessly failing over mid-download if a CDN stalls!
2. **Smart Delta Updates:** Compares local installed chunk hashes against the new manifest and downloads **only changed chunks** (plus supports selective DLC/high-res-texture/language skipping so users save 20–50 GB of disk automatically!).
3. **Speculative Auth & Pre-Launch Warmup:** Refreshes expiring OAuth tokens silently before the user clicks Play, cutting Epic/GOG launch delay from ~2–3 s down to `<200 ms`.
4. **Automatic Game-Session Bandwidth Throttle:** Detects when any game starts and pauses or throttles background store downloads automatically so ping never spikes mid-match!

---

## 4. Measurement Plan

All protocol, manifest parsing, delta diffing, resume, and CDN failover tested against local mock HTTP/2 test servers with synthetic chunk manifests (zero real store account credentials or real game installs/uninstalls in agent tests!):

| Metric | Baseline (`Legendary` 17 MB + `gogdl` 12 MB + `Nile` 15 MB + `SteamIpc` 2.86 MB) | Target (`exo-stores`) | How Measured |
| --- | --- | --- | --- |
| **On-Disk Store Tool Footprint** | **~46.86 MB** (3 PyInstaller binaries + NativeAOT helper) | **<3.0 MB** compiled inside Exo (**-44 MB**) | Release binary delta |
| **CLI Cold-Start Overhead per Operation** | **400–800 ms** PyInstaller unpack per call | **0 ms** (in-process Rust async task!) | QPC timer vs subprocess spawn |
| **Local Mock 1 GB Chunk Download + Verify Throughput** | Python single/GIL-bound chunk reassembly | **>800 MB/s** loopback saturation (multi-threaded decompress + hash + pre-alloc write) | Local loopback benchmark server |
| **Binary VDF / `appinfo.vdf` Parse Time (20 MB file)** | C# `SteamAppInfoNames` | **<25 ms** zero-copy Rust parser | Criterion benchmark on synthetic VDF |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-stores --locked -- -D warnings`, `cargo test -p exo-stores --locked`.
2. **Mock Store E2E Harness:** Full synthetic install → interrupt mid-chunk → resume → corrupt 1 byte on disk → verify & repair → delta update → uninstall lifecycle passes with 100% SHA-256 integrity across Epic, GOG, and Amazon mock servers.
3. **Path-Traversal & Security Gate:** Reject any manifest filename containing `..`, absolute paths, ADS (`:`), or symlinks escaping the game install root.
4. **Network & License Gate:** All contacted domains match `NetworkDestinations.cs` / `PRIVACY.md`; `cargo-deny` confirms zero GPL dependencies.

---

## 6. Safety Rules & Hard Stops

- **Zero DRM Circumvention:** Always authenticate with the user's own official tokens and pass valid store launch/exchange tokens; never crack or bypass ownership checks.
- **Steam Hard Stop (`AGENTS.md`):** Steam installs, updates, and uninstalls MUST go strictly through the official Steam client IPC (`steam/ipc.rs`), NEVER direct Steam CDN scraping or DepotDownloader.
- **No Real Account / Real Game Mutations in Tests:** Never touch user tokens, never uninstall real games, never launch VALORANT/League during tests.
- **Clean-Room Rule:** Zero GPL-3.0 code copied from `Legendary`, `gogdl`, or `Nile`.
