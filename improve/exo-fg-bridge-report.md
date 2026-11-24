# Exo FG Bridge (`exo-fg-bridge`) — Spec 02 PR B Implementation & RTX 3070 Report

## 1. Summary

Implemented **PR B (`exo-fg-bridge`)** of Spec 02: a clean-room **MIT-licensed** Rust Windows `cdylib` (`exo_fg_bridge.dll`) and DX12 benchmark harness (`exo-fg-bridge-bench.exe`) that routes DLSS-G / Streamline Frame Generation calls to official AMD FidelityFX FSR 3.1 FG (`amd_fidelityfx_dx12.dll`) or Intel XeSS-FG (`libxess_fg.dll`) — serving as the clean-room replacement for `dlssg_for_sm86` on RTX 20/30 cards and providing a minimal read-only `fakenvapi` capability shim for AMD Radeon / Intel Arc in permitted sessions — with zero GPL code, zero ImGui bloat, zero INI files, transactional `SessionMods` deployment + crash-orphan recovery, and strict shared 3-class `AntiCheatClass` enforcement.

## 2. Clean-Room Public Sources (Zero GPL Code)

Written strictly from public MIT/Apache/vendor-published specifications and headers (zero code copied or derived from GPL-3.0 `OptiScaler`, `dlssg-to-fsr3`, or proprietary `dlssg_for_sm86` binary blobs):

1. **Microsoft Direct3D 12 & DXGI Public SDK** (`d3d12.h`, `dxgi1_6.h` via Microsoft `windows-rs` MIT/Apache-2.0 bindings): `IDXGIFactory6`, `ID3D12Device`, `ID3D12CommandQueue`, `ID3D12Resource`, `ID3D12QueryHeap`.
2. **AMD FidelityFX SDK 1.1+ Public MIT Headers** (`GPUOpen-LibrariesAndSDKs/FidelityFX-SDK`, MIT License — `ffx_api.h`, `ffx_api_dx12.h`, `ffx_framegeneration.h`): `FfxApiHeader`, `FfxApiResource`, `FfxDispatchDescFrameGenerationPrepare`, `FfxDispatchDescFrameGeneration`, dynamic `ffxCreateContext`/`ffxConfigure`/`ffxDispatch`/`ffxDestroyContext` loader gated by `exo_upscale::verify::verify_vendor_dll(..., Vendor::Amd, None)`.
3. **Intel XeSS-FG Public SDK Headers** (`intel/xess` — `xess_fg.h`, `xess_d3d12_fg.h`): public XeSS-FG backend selector & resource contract.
4. **NVIDIA Streamline / NGX Public Open-Source Headers** (`NVIDIAGameWorks/Streamline` MIT & `NVIDIA/DLSS` public C ABI — `sl_dlss_g.h`, `nvsdk_ngx_defs.h`): `kFeatureDLSS_G = 1000`, `NVSDK_NGX_Feature_FrameGeneration = 11`, and frame resource translation (`color`, `hudless_color`, `depth`, `motion_vectors`, `jitter`, `mv_scale`, camera near/far/FOV).

## 3. Deliverables

- **Rust `cdylib` + `rlib` + DX12 benchmark (`apps/launcher/native-ui/crates/exo-fg-bridge/`):**
  - `src/lib.rs`: Lock-free minimal `DllMain` (`DisableThreadLibraryCalls` only) + 5 C-ABI exports (`exo_fg_bridge_version`, `exo_fg_bridge_init`, `exo_fg_bridge_query_dlssg_support`, `exo_fg_bridge_evaluate_frame`, `exo_fg_bridge_shutdown`).
  - `src/config.rs`: Zero-INI JSON config (`exo-fg-bridge.json`) enforcing shared `exo_rpc::anticheat::AntiCheatClass` via `exo_upscale::policy::evaluate(...)` as defense-in-depth inside the DLL itself (fails closed as `Unknown`).
  - `src/nvngx_bridge.rs`: DLSS-G / Streamline resource & parameter translator → AMD FidelityFX FSR 3.1 FG descriptors; unlocks DLSS-G support on RTX 20/30 (SM 75 / SM 86) in permitted sessions.
  - `src/ffx_abi.rs`: Public FidelityFX SDK 1.1+ C ABI definitions and Authenticode-verified dynamic loader for signed `amd_fidelityfx_dx12.dll`.
  - `src/fakenvapi_shim.rs`: Clean-room minimal capability query shim for AMD/Intel in permitted sessions (automatically bypassed on real NVIDIA GPUs so native `nvapi64.dll` stays untouched).
  - `src/dxgi_proxy.rs`: System `System32\dxgi.dll` resolution + embedded DXIL SM 6.4 compute shaders (`bench_scene_mv.cso`, `mv_synth.cso`, compiled with `dxc.exe -T cs_6_4 -O3 -WX`).
  - `src/journal.rs`: Transactional `SessionMods` deployment wrapper (deploys `exo_fg_bridge.dll` + `exo-fg-bridge.json`, removes on exit, and recovers 100% after `TerminateProcess` crash).
  - `src/bench_main.rs`: Live RTX 3070 1440p DX12 GPU-timestamped offline synthetic game harness loading `exo_fg_bridge.dll` dynamically via `LoadLibraryW`.
- **Native Host & Rune UI Routing:**
  - `apps/launcher/ExoLauncher/Helpers/FrameGenRouter.cs`: Adds `RouteModExoFgBridge` (`mod_exo_fg_bridge`, `"Exo FG Bridge (FSR 3.1)"`) replacing `dlssg_for_sm86` on RTX 20/30 when `exo_fg_bridge.dll` is present, strictly governed by shared `AntiCheatClass` (`CompetitivePvpOnline` & `Unknown` blocked; `OnlineNonCompetitivePve` opt-in behind `BanRiskConfirmation`; `OfflineAntiCheat` in official offline mode with `OfflineOneTimeNote`; `NoAntiCheat` clean).
  - `apps/launcher/ExoLauncher/Services/ExoUpscaleService.cs` & `FrameGenService.cs`: Discovers `exo_fg_bridge.dll` and gates activation behind shared `AntiCheatClass` consent checks.
  - `apps/launcher/native-ui/crates/exo-ui/src/frame_gen_row.rs`: Displays `"Exo FG Bridge"` for `"mod_exo_fg_bridge"`.
  - `apps/launcher/ExoLauncher.Tests/FrameGenRouterTests.cs`: Unit tests covering RTX 20/30 routing across all 4 `GameAntiCheatClass` classes + `Unknown`.

## 4. RTX 3070 Measured Results (2560×1440 Offline DX12 MV Harness)

Source artifact: [`improve/lab-exo-fg-bridge/bench.json`](lab-exo-fg-bridge/bench.json)

| Metric | Before (Native 1× DX12) | After (`exo_fg_bridge.dll` 2×) | Delta / Target |
|---|---:|---:|---|
| GPU / SM Arch Tested | NVIDIA GeForce RTX 3070 (`SM 86`) | NVIDIA GeForce RTX 3070 (`SM 86`) | `rtx30DlssgToFsr31Enabled: true` |
| Resolution & Measured Frames | `2560×1440` (`210` base frames) | `2560×1440` (`210` base → `420` output) | — |
| **Effective FPS** | **`98.45 FPS`** | **`193.71 FPS`** | **`1.97×`** gain |
| **Output Frame Time (avg)** | `10.158 ms` | **`5.162 ms`** | `-49.2%` |
| **Output Frame Time (p50)** | `10.025 ms` | **`5.077 ms`** | `-49.4%` |
| **Output Frame Time (p95)** | `10.609 ms` | **`5.314 ms`** | `-49.9%` |
| **Output Frame Time (p99)** | `12.772 ms` | **`5.782 ms`** | `-54.7%` |
| GPU Scene Render (DX12 Timestamp) | `9.831 ms` | `9.831 ms` | `0.000 ms` scene regression |
| GPU Bridge MV-Synth Cost | `0.000 ms` | **`0.087 ms`** | Total bridge overhead **`0.090 ms`** (`< 0.2 ms` target) |
| Bridge C-ABI Dispatch Overhead | `0.000 µs` | **`2.776 µs`** | Zero ImGui, zero INI |
| **Render-to-Present Latency** | `10.158 ms` | **`20.572 ms`** | **`+10.414 ms`** (`+1.03` base frames) |
| Allocated VRAM Delta | — | `72.42 MB` | — |
| `SessionMods` Crash-Orphan Recovery (`TerminateProcess`) | — | **`100%`** (`2/2` files removed, `0` errors) | Target `100%` (**PASS**) |

## 5. Exports, Imports & Verification Gates

- **`dumpbin /exports exo_fg_bridge.dll`:** Clean 5-function C ABI (`exo_fg_bridge_version`, `exo_fg_bridge_init`, `exo_fg_bridge_query_dlssg_support`, `exo_fg_bridge_evaluate_frame`, `exo_fg_bridge_shutdown`).
- **`dumpbin /imports exo_fg_bridge.dll`:** **0** banned imports (`WriteProcessMemory`, `CreateRemoteThread`, `SetWindowsHookEx`, `VirtualAllocEx`, `OpenProcess`), **0** ImGui dependencies.

| Gate | Command | Result |
|---|---|---|
| Rust format | `cargo fmt -p exo-fg-bridge -- --check` + `rustfmt --edition 2024 --check frame_gen_row.rs` | **PASS** (exit 0) |
| Rust clippy | `cargo clippy -p exo-fg-bridge --locked -- -D warnings` | **PASS** (exit 0, 0 warnings) |
| Rust unit tests | `cargo test -p exo-fg-bridge --locked` | **PASS** (9/9 passed, 0 failed) |
| Rust release build | `cargo build --release --locked -p exo-fg-bridge` | **PASS** (exit 0) |
| C# build + tests | `dotnet build ExoLauncher.Tests -c Release -p:Platform=x64` + `FrameGenRouterTests`, `ExoUpscalePolicyTests`, `AntiCheatClassTests`, `BannedNativeImportsTests` | **PASS** (0 warnings, 37/37 passed) |
| Zero-comment ratchet | `node tools/check-comments.mjs` | **PASS** (exit 0) |

## 6. Follow-Up (Locked Desktop Note)

- The workstation desktop was locked (`GetForegroundWindow() == 0`), so interactive live-game capture on a no-anti-cheat title (e.g. Sekiro / Thymesia-class) with PresentMon is tracked as an unlocked-desktop follow-up. All DX12 GPU timestamps, C-ABI bridge invocations through loaded `exo_fg_bridge.dll`, and `SessionMods` `TerminateProcess` crash-orphan recovery were executed live on the owner's RTX 3070.
