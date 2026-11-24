# Exo Flow (`exo-flow`) — Spec 01 Implementation & RTX 3070 Measurement Report

## 1. Summary

Implemented **Exo Flow** (`apps/launcher/native-ui/crates/exo-flow/`), a free, cross-vendor, zero-injection frame-generation sidecar built on Windows Graphics Capture (WGC), DirectX 12 compute (SM 6.4 **DP4a** + **FP16** fallback + dynamic **NVOF 5.0** probe), adaptive temporal HUD masking, and a DirectComposition flip-model click-through overlay, wired into Exo's native frame-generation router (`FrameGenRouter.cs`, `FrameGenService.cs`, `ExoFlowService.cs`, and `exo-ui/src/frame_gen_row.rs`).

## 2. Architecture & Deliverables

- **Sidecar binary (`apps/launcher/native-ui/crates/exo-flow/`):**
  - `capture.rs`: Zero-copy `Windows.Graphics.Capture` (`IGraphicsCaptureItemInterop::CreateForWindow`) → D3D11 shared handle → DX12 `OpenSharedHandle` texture ingest (no CPU readback in live path).
  - `flow_compute.rs` + `src/shaders/*.hlsl` (compiled to embedded DXIL `.cso` via `dxc.exe -T cs_6_4 -O3 -WX`):
    - `pyramid.hlsl`: Rec.709 luma + 4-pixel u8 packed 4-level pyramid (`1/2 → 1/4 → 1/8 → 1/16`).
    - `flow_dp4a.hlsl`: Coarse-to-fine bidirectional 4×4 patch SSD using hardware `dot4add_u8packed` (DP4a) + parabolic sub-pixel refinement + coarse prior regularization.
    - `flow_fp16.hlsl`: `min16float` fallback for older GPUs without DP4a.
    - `flow_smooth.hlsl`: 3×3 edge-aware bilateral spatial smoothing + temporal EMA + forward-backward consistency occlusion confidence map.
    - `hud_mask.hlsl`: Two-pass temporal pixel-variance + spatial-gradient HUD mask with 3×3 morphological dilation.
    - `synth.hlsl`: Bidirectional occlusion-aware warp + static HUD pinning + optional CAS sharpening / Integer / Lanczos2 scaling.
    - `synth_scene.hlsl`: Deterministic 2560×1440 moving game-world + static crosshair/minimap/health-bar ground-truth generator for automated PSNR/SSIM validation.
  - `flow_nvof.rs`: Dynamic `LoadLibraryExW("nvofapi64.dll", LOAD_LIBRARY_SEARCH_SYSTEM32)` probe & NVOF 5.0 D3D12 instance lifecycle wrapper (zero build-time SDK dependency).
  - `presenter.rs`: DirectComposition (`IDCompositionDevice`/`Target`/`Visual`) click-through flip-model swapchain (`WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED`, `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`, `SetMaximumFrameLatency(1)`), auto-disengage on Alt-Tab/minimize/window-close and `DXGI_ERROR_DEVICE_REMOVED`/`RESET`.
  - `pacer.rs`: Fixed 2x, Fixed 3x, and VRR-adaptive (`monitor_hz - 3`) pacing + recommended base FPS caps + latency guard (< 40 FPS auto-pause, > 95% GPU busy auto-downscale to 1/4 flow).
  - `main.rs`: Named-pipe server (`framegen.flow.start`, `framegen.flow.stop`, `framegen.flow.status`, `framegen.flow.setProfile`) plus `--inspect-gpu` and `--bench` modes.
- **Native Host & Rune UI Routing Integration:**
  - `apps/launcher/ExoLauncher/Services/ExoFlowService.cs`: Managed named-pipe client + sidecar discovery/launcher (zero capture/input P/Invokes; `BannedNativeImportsTests` stays 100% green).
  - `apps/launcher/ExoLauncher/Helpers/FrameGenRouter.cs`: Adds `RouteExternalExoFlow` (`external_exo_flow`, `TierExternal`, `AntiCheatVerdict: "safe"`) gated by the shared `ExoLauncher.Services.AntiCheatClass` rule (`CompetitivePvpOnline` and `Unknown` stay OFF; `OnlineNonCompetitivePve`, `OfflineAntiCheat`, and `NoAntiCheat` allow Exo Flow), and removes paid Lossless Scaling from recommended routes and unavailable hints.
  - `apps/launcher/ExoLauncher/Services/FrameGenService.cs`: Resolves, applies (switching to Borderless where supported), launches sidecar on game start, and stops on route disable.
  - `apps/launcher/native-ui/crates/exo-ui/src/frame_gen_row.rs`: Displays `"Exo Flow"` / `"Exo Flow · Off"` in the native Rune game page.

## 3. RTX 3070 Measured Results (2560×1440, 1,000 Frames)

Source artifact: [`improve/lab-exo-flow/bench.json`](lab-exo-flow/bench.json)

### Hardware & NVOF 5.0 Probe
| Metric | Measured Value |
|---|---|
| GPU | NVIDIA GeForce RTX 3070 (Vendor `0x10DE`, `8017.0 MB` VRAM) |
| DX12 Capabilities | `native_fp16: true`, `shader_model_6_4_dp4a: true` |
| `nvofapi64.dll` (System32) | Loaded (`dll_loaded: true`) |
| NVOF 5.0 API + D3D12 Instance | `api_version_5_0_ok: true`, `d3d12_instance_created: true`, `status_code: 0` |

### GPU Pass Timings, VRAM, RAM, and CPU (2560×1440, 1,000 Frames)
| Metric | DX12 Compute DP4a (`dot4add_u8packed`) | DX12 Compute FP16 (`min16float`) | Target (Spec 01) |
|---|---:|---:|---:|
| Pyramid (4 levels, both frames) | `0.072 ms` | `0.072 ms` | — |
| Optical Flow (bidirectional 4-level) | `0.689 ms` | `1.845 ms` | DP4a **2.68× faster** |
| Flow Smooth + Occlusion | `0.048 ms` | `0.042 ms` | — |
| HUD Mask (temporal + 3×3 dilate) | `0.194 ms` | `0.170 ms` | — |
| Frame Synthesis (warp + HUD pin) | `0.388 ms` | `0.377 ms` | — |
| **Total GPU Frame Cost (avg)** | **`1.391 ms`** | **`2.507 ms`** | `< 1.8 ms` / `< 3.0 ms` |
| Total GPU Cost (p50 / p95 / p99) | `1.307 / 1.745 / 1.854 ms` | `2.406 / 2.912 / 3.043 ms` | — |
| Allocated VRAM Delta (above 14.61 MB baseline) | **`128.87 MB`** (`148.17 MB` total) | **`128.87 MB`** (`148.17 MB` total) | `< 180 MB` |
| Process Working Set / Private Bytes | `138.16 MB` / `310.72 MB` | `138.17 MB` / `310.72 MB` | — |
| Process CPU Usage | **`0.41%`** | **`0.31%`** | `< 3%` |

### Image Quality — HUD Mask OFF vs ON (2560×1440 vs Analytical `t = 0.5` Ground Truth)
Visual proof artifacts: [`ground_truth.png`](lab-exo-flow/ground_truth.png), [`hud_mask_off.png`](lab-exo-flow/hud_mask_off.png), [`hud_mask_on.png`](lab-exo-flow/hud_mask_on.png).

| Region / Metric | HUD Mask OFF (DP4a) | HUD Mask ON (DP4a) | HUD Mask ON (FP16) | Improvement (OFF → ON) |
|---|---:|---:|---:|---:|
| Full Frame PSNR | `29.16 dB` | **`31.83 dB`** | **`31.95 dB`** | `+2.67 dB` |
| Full Frame SSIM | `0.899` | **`0.935`** | **`0.936`** | `+0.036` |
| **Static HUD Overall PSNR** | `15.64 dB` | **`51.47 dB`** | **`51.47 dB`** | **`+35.83 dB`** |
| Minimap PSNR | `16.40 dB` | **`99.00 dB`** (bit-exact) | **`99.00 dB`** | `+82.60 dB` |
| Health Bar PSNR | `14.11 dB` | **`99.00 dB`** (bit-exact) | **`99.00 dB`** | `+84.89 dB` |
| Crosshair Region PSNR (incl. moving surround) | `10.41 dB` | **`29.83 dB`** | **`29.83 dB`** | `+19.42 dB` |

### Latency, DirectComposition Pacing, and Honest Environment Notes
- **Compute-pipeline added latency at 60 FPS base:** `16.67 ms` (1 buffered base frame) + `1.391 ms` (DP4a compute) = **`18.06 ms`** (`1.08` base frames).
- **Measured DirectComposition swapchain presentation (1,000 frames, hidden offscreen window on locked workstation):**
  - `frames_presented`: `1000`, `device_lost_count`: `0`
  - `avg_present_ms`: `7.275 ms` → total measured added latency at 60 FPS = **`25.33 ms`** (`1.52` base frames)
  - `p99_interval_jitter_ms`: `26.676 ms` (note: measured on a locked desktop where Windows DWM throttles hidden DirectComposition swapchains; interactive unlocked desktop 120 Hz pacing requires a live desktop session).
  - `wgc_capture_probe_ok`: `false` (`CreateForWindow` returned `0x80070057` on the hidden non-visible window while the workstation was locked; live borderless game WGC capture must be verified on an unlocked desktop session).

### Reference Comparison (Published Characteristics vs Measured Exo Flow)
| Solution | Injection / Anti-Cheat | GPU Support | Added Latency (60 FPS Base) | GPU Cost (1440p) | HUD Protection |
|---|---|---|---|---|---|
| **Exo Flow (measured, RTX 3070)** | Zero-injection WGC + DComp overlay (AC-safe) | Any DX12 GPU (DP4a + FP16 + NVOF probe) | `18.06 ms` compute (`25.33 ms` incl. hidden-DComp present) / `1.08–1.52` frames | **`1.39 ms` DP4a / `2.51 ms` FP16** | Adaptive temporal HUD mask (`51.47 dB` static HUD) |
| **FSR 3 FG** *(published ref)* | In-game engine integration (or DLL mod) | Cross-vendor DX12 | ~1 base frame + Anti-Lag 2 / Reflex | ~1–2 ms (engine MVs) | Native engine UI pass (no HUD warp) |
| **AMD AFMF 2** *(published ref)* | Driver-level swapchain hook | AMD RX 6000+ / 700M+ only | ~1–2 frames (pauses on fast motion) | ~1–2 ms | Heuristic fast-motion fallback |
| **LSFG 3.1** *(published ref)* | Zero-injection WGC + overlay (paid) | Cross-vendor DX12 | ~1–2 frames | ~1.5–3 ms | ML optical flow |

## 4. Security & Zero-Injection Verification (`dumpbin /imports`)

Audited `apps/launcher/native-ui/target/release/exo-flow.exe` with MSVC `dumpbin /imports`:
- **Banned injection/hook imports (`WriteProcessMemory`, `CreateRemoteThread`, `SetWindowsHookEx`, `VirtualAllocEx`, `OpenProcess`):** **0 matches**.
- **Actual imported DLLs:** `d3d12.dll`, `d3d11.dll`, `dxgi.dll`, `dcomp.dll`, `combase.dll`, `api-ms-win-core-winrt-l1-1-0.dll`, `psapi.dll`, `user32.dll`, `kernel32.dll`, `oleaut32.dll`, `ntdll.dll`, CRT runtime.
- `ExoLauncher.Tests.BannedNativeImportsTests` passed (`0` violations in C# host).

## 5. Verification Gates

| Gate | Command | Result |
|---|---|---|
| Rust format | `cargo fmt --all -- --check` | **PASS** (exit 0) |
| Rust clippy | `cargo clippy --workspace --locked -- -D warnings` | **PASS** (exit 0, 0 warnings) |
| Rust workspace tests | `cargo test --workspace --locked` | **PASS** (`exo-flow`: 10/10, `exo-ui`: 218/218, `exo-kit`, `exo-rpc` — 0 failed) |
| Rust release binaries | `cargo build --release --locked --bin exo-flow --bin exo-ui` | **PASS** (exit 0) |
| Host build + FrameGen & safety tests | `dotnet test` build + `ExoLauncher.Tests.exe` (`FrameGenRouterTests`, `BannedNativeImportsTests`, `LosslessScalingConfigTests`, `AntiCheatCheckTests`, `FluidMotionFramesTests`) | **PASS** (264/264 passed, 0 failed) |
| Zero-comment ratchet | `node tools/check-comments.mjs` | **PASS** (exit 0) |
| Zero-injection import audit | `dumpbin /imports exo-flow.exe` | **PASS** (0 banned imports) |

## 6. Remaining Follow-Up (Not Verified on Locked Desktop)

1. Interactive unlocked-desktop validation of live borderless game WGC capture (`WgcZeroCopyCapture::start`) and visible 120 Hz/144 Hz/240 Hz VRR DirectComposition pacing (`p99` jitter on an active unlocked display).
2. Optional full `nvOFInit` + `nvOFExecuteD3D12` compute-replacement path (the NVOF 5.0 D3D12 instance creation probe is already verified working on RTX 3070 with `status_code: 0`, while DX12 DP4a compute already runs at `1.391 ms` total at 1440p).
