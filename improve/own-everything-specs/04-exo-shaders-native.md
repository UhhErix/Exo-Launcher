# Spec 04 — Exo Shaders Native (`exo-shaders-native`): Clean-Room MIT Rust PSO & Shader Cache Engine

- **Inventory Rank:** #04 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** `shaders/` (`ExoShaders.Service.exe`, GPL-3.0-or-later fork of `SCSKiller 1.4.8`, **~68 MB** disk: duplicate self-contained `.NET 10` CoreCLR + **11.06 MB** `BouncyCastle` + **6.79 MB** `CUE4Parse` + unused `Velopack` + `Fossilize` + `zlib-ng`) (`Services/ShadersAddon.cs`, `ShaderPrepare.cs`, `ShaderRebuild.cs`, `ShaderCacheWatch.cs`)
- **Anti-Cheat Class:**
  - Cache scanning, corruption detection, GPU driver-cache warm-up, Unreal `.upipelinecache` / Steam Fossilize indexing, and DX12/Vulkan offline PSO compilation: **`AC-SAFE`** (never touches game processes or anti-cheat).
  - Any live DX12/Vulkan PSO recorder proxy DLL placed beside a game exe: **`AC-OFFLINE-ONLY`** (strictly `AntiCheatCheck.Kind.None`, session-cleaned on exit).
- **License:** MIT (100% clean-room Rust — permanently eliminates the GPL-3.0 add-on boundary and `exo-shaders-<v>.tar.gz` release tarball overhead!).

---

## 1. Scope

### What It Solves
Today `addons\shaders\engine\` accounts for **17.9% (~68 MB)** of Exo's entire installed footprint because it ships a *second* self-contained `.NET 10` runtime plus heavy C# libraries (`BouncyCastle.Cryptography.dll` 11.06 MB, `CUE4Parse.dll` 6.79 MB, unused `Velopack`) under GPL-3.0. Meanwhile, every GPU driver update wipes driver shader caches (`DXCache`/`GLCache`/`DxCache`), causing severe traversal stutters on users' next game launch.

### In Scope
- **Pure-Rust MIT Shader Cache Scanner & Health Engine:**
  - Indexes & verifies Windows/vendor driver caches: NVIDIA (`%LOCALAPPDATA%\NVIDIA\DXCache`, `GLCache`, `%PROGRAMDATA%\NVIDIA Corporation\NV_Cache`), AMD (`%LOCALAPPDATA%\AMD\DxCache`, `DxcCache`), Intel (`%LOCALAPPDATA%\Intel\ShaderCache`), and Microsoft (`%LOCALAPPDATA%\D3DSCache`).
  - Indexes game-level shader caches: Steam Fossilize (`steamapps\shadercache\<appid>`), Unreal Engine 4/5 (`.ushaderprecache`, `.upipelinecache`, `Saved\*.ushaderbytecode`), Unity, Source 2, and Call of Duty / Frostbite regenerated caches (`AntiCheatCheck.Category.OwnInterface` while closed).
- **Pure-Rust Offline PSO Compiler (`exo-pso-warm`):**
  - Compiles extracted DXIL/DXBC/SPIR-V pipeline states directly on the user's GPU via headless DX12 (`ID3D12Device::CreateGraphicsPipelineState` / `CreateComputePipelineState` with `ID3D12PipelineLibrary`) and Vulkan (`VkPipelineCache`) so the GPU driver populates its native hardware cache *before* the user launches the game!
  - Native Rust UE4/UE5 `.pak` / IoStore (`.utoc`/`.ucas`) header & shader-code reader using lean Rust `aes`, `sha2`, `zstd`, `lz4_flex` (replacing 11 MB BouncyCastle + 6.8 MB CUE4Parse!), with optional on-demand pinned `oodle-data-shared.dll` load *only* for Oodle-compressed UE archives (`KEEP`, Row #54).
- **Driver-Update Idle Pre-Warmer:** Automatically detects GPU driver version changes (`DriverIdentity`) and silently re-warms the user's top recently played games while the PC is idle (`PcIdle`) at low GPU priority (`D3D12_COMMAND_QUEUE_PRIORITY_NORMAL` + thermal ceiling).

### Out of Scope
- Copying or reusing any GPL-3.0 source code from `SCSKiller` (this MUST be 100% clean-room MIT Rust).
- Redistributing copyrighted game shader bytecode or decryption keys (everything is extracted/compiled strictly locally on the user's own PC from their own legally installed games).

---

## 2. Native Rust Crate Layout

```text
crates/exo-shaders/
├── Cargo.toml                  # windows (Direct3D12, DXGI), ash (Vulkan), zstd, lz4_flex, aes, sha2, blake3, rayon
├── src/
│   ├── lib.rs
│   ├── driver_cache.rs         # NVIDIA DXCache/GLCache, AMD DxCache, Intel, D3DSCache indexer & orphan pruner
│   ├── game_cache.rs           # Per-game shader cache discovery (UE4/5, Steam Fossilize, CoD, Source 2, Unity)
│   ├── ue_archive.rs           # Clean-room UE4/UE5 .pak & .utoc/.ucas shader-bytecode extractor (optional Oodle FFI)
│   ├── fossilize.rs            # Clean-room Steam Fossilize archive reader for Vulkan/DXVK pipelines
│   ├── pso_dx12.rs             # Headless DX12 multi-threaded PSO compiler (populates driver DXCache directly)
│   ├── pso_vk.rs               # Headless Vulkan VkPipelineCache warmer
│   ├── scheduler.rs            # Idle-only thermal-capped background warm queue (pauses instantly on user input/game)
│   └── policy.rs               # Enforces AntiCheatCheck rules (never touch running games or AC binaries)
```

---

## 3. Automation & Intelligence Design

1. **Zero-Click Post-Driver-Update Recovery:** When Exo detects a new GPU driver version, it inspects the last 5 played games, checks their driver-cache hit status, and automatically warms their PSO caches during idle windows (with live progress in the PC room and instant pause within `<50 ms` if the user moves the mouse or launches a game).
2. **Stutter-Triggered Auto-Rebuild:** `exo-present` (Spec #07) flags shader-compilation stutters (high CPU-thread D3D12/driver-compiler bursts during frame spikes); Exo automatically offers/schedules a clean cache rebuild for that specific game.
3. **Smart Cache Size & Quota Guard:** Automatically ensures NVIDIA DRS Shader Cache Size is set to `10 GB` or `Unlimited` (`exo-gpu`, Spec #08) instead of NVIDIA's cramped 4 GB default that silently evicts active games' caches!

---

## 4. Measurement Plan

Tested on this RTX 3070 / Ryzen 5 5600X PC against real read-only driver cache directories (`DXCache`, `D3DSCache`) + synthetic DX12 HLSL/DXIL shader corpus (500 test pipelines):

| Metric | Baseline (`ExoShaders.Service.exe` / SCSKiller) | Target (`exo-shaders-native`) | How Measured |
| --- | --- | --- | --- |
| **Installed Footprint** | **~68 MB** (duplicate .NET 10 + BouncyCastle + CUE4Parse + Velopack) | **<3.0 MB** single Rust crate/binary (**-65 MB**) | Release build directory byte sum |
| **Full Driver + Library Cache Scan** | ~1.5–4.0 s (.NET startup + scan) | **<150 ms** (parallel `rayon` scan, 0 CLR startup) | QPC timer across real local cache dirs |
| **500-PSO Synthetic DX12 Warm-Up** | N/A (baseline benchmarked on same 500 DXIL shaders) | Multi-threaded compile scaling ≥4× over single-threaded; **<50 ms** pause latency on simulated user activity | DX12 headless compiler harness |
| **License & Source Tarball Overhead** | GPL-3.0 + R2 tarball build/upload every release | **MIT** (0 extra tarballs, 0 copyleft isolation hacks) | License scan (`cargo-deny`) |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-shaders --locked -- -D warnings`, `cargo test -p exo-shaders --locked`.
2. **License Purity Gate:** `cargo-deny check licenses` proves zero GPL/AGPL crates in `exo-shaders`.
3. **Thermal & Idle Pause Gate:** Automated test verifies `scheduler.rs` suspends PSO compilation within `<50 ms` of simulated foreground game start or GPU temp `>78 °C`.
4. **Anti-Cheat Policy Gate:** Verifies zero file writes inside any `Found`/`PossiblyVac`/`Unknown`/`Riot` game folder while running, and zero recorder DLL placement outside `Kind.None`.
5. **No External Phone-Home Gate:** Zero network calls outside `NetworkDestinations` (specifically zero SCSKiller/Patreon endpoints).

---

## 6. Safety Rules & Hard Stops

- **Clean-Room Rule:** Write every line from scratch using public DX12/Vulkan/UE/Fossilize format specs; NEVER copy code from `apps/launcher/shaders/` (`SCSKiller`).
- **Never Touch Owner's Real Shader Caches During Dev Tests:** Lab tests run strictly against temporary directories (`TEMP`) and read-only scans; never wipe `%LOCALAPPDATA%\NVIDIA\DXCache` without explicit user action.
- **No Copyrighted Keys/Shaders:** Never commit game decryption keys or compiled game shaders to the repo or cloud.
- **Thermal & VRAM Protection:** Cap concurrent PSO compile threads to `max(1, physical_cores - 2)` and abort immediately on `DXGI_ERROR_DEVICE_REMOVED` or GPU `>= 80 °C`.
