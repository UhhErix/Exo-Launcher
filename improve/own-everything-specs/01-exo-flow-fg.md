# Spec 01 — Exo Flow (`exo-flow`): Free Native DX12 Optical-Flow Frame Generation & Scaling

- **Inventory Rank:** #01 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `XL`
- **Replaces:** Lossless Scaling (`AppID 993090`, $7 paid Steam utility; `Helpers/LosslessScalingConfig.cs`, `Helpers/FrameGenRouter.cs` `RouteExternalLossless`)
- **Anti-Cheat Class:** `AC-SAFE` (100% external WGC capture + DirectComposition flip-model overlay — zero DLL injection, zero game hooks, zero `OpenProcess` handles!)
- **License:** MIT (clean-room Exo Rust + WGSL/HLSL compute shaders; optional dynamic load of system-installed NVIDIA Optical Flow SDK DLL `nvofapi64.dll` where present)

---

## 1. Scope

### What It Solves
Today `FrameGenRouter.cs` falls back to paid, closed-source **Lossless Scaling ($7)** via `LosslessScalingConfig.cs` whenever a game lacks native DLSS/FSR/XeSS FG or runs an online anti-cheat that forbids `OptiScaler`/`dlssg-to-fsr3`. Requiring users to purchase and run a third-party app breaks Exo's promise of native, free, zero-config optimization.

### In Scope
- External **Windows.Graphics.Capture (WGC)** zero-copy D3D11/DX12 texture capture of any borderless-windowed game.
- **Dual-backend dense optical flow**:
  1. Hardware **NVIDIA Optical Flow (NVOF 5.0)** via `nvofapi64.dll` on RTX 20/30/40/50 GPUs (sub-millisecond hardware motion vectors!).
  2. Clean-room **DX12 Compute pyramidal DIS / hierarchical block-match optical flow** on AMD Radeon, Intel Arc, and GTX GPUs.
- **Bidirectional occlusion-aware frame synthesis + static-HUD detection mask** (detects low-variance static HUD/crosshair regions across frames to eliminate HUD warping!).
- **Adaptive / Fixed multipliers**: 2×, 3×, and **VRR-Adaptive** (dynamically synthesizes exact intermediate timestamps to lock to `monitor_hz - 3`).
- **Built-in Spatial Scalers**: CAS (Contrast Adaptive Sharpening), integer pixel-art scaling, and Lanczos/Mitchell upscaling.
- **DirectComposition click-through flip-model overlay** (`WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE`) synchronized via DXGI `Present1` + `WaitableObject`.
- Automatic retirement of `Helpers/LosslessScalingConfig.cs` external dependency once validated.

### Out of Scope
- In-game DLL injection or swapchain hooking (that belongs strictly to `AC-OFFLINE-ONLY` Spec #02).
- Exclusive-fullscreen (FSE) hooking (Exo Flow automatically guides/switches games to borderless-windowed flip model, where Windows 11 MPO delivers FSE-equivalent latency).

---

## 2. Native Rust Crate Layout

Location: `apps/launcher/native-ui/crates/exo-flow/` (runs as an isolated sidecar `exo-flow.exe` so capture/WGC never touches `exo-ui.exe` or `ExoLauncher.exe` and respects `BannedSymbols.txt` capture isolation!):

```text
crates/exo-flow/
├── Cargo.toml                  # windows (WGC, Direct3D12, DXGI, DirectComposition), wgpu/raw D3D12, bytemuck
├── src/
│   ├── main.rs                 # Named-pipe / exo-wire control listener + lifecycle supervisor
│   ├── capture.rs              # WGC GraphicsCaptureItem -> shared DXGI HANDLE -> ID3D12Resource (zero CPU copy)
│   ├── flow_nvof.rs            # Dynamic nvofapi64.dll wrapper (RTX HW optical flow, 4x4 grid, bidirectional)
│   ├── flow_compute.rs         # DX12 Compute 4-level Gaussian pyramid + patch-match/DIS optical flow + temporal smoothing
│   ├── hud_mask.rs             # Multi-frame temporal variance detector to pin static HUD/crosshairs
│   ├── synth.rs                # Bidirectional forward/backward warp + occlusion disocclusion blend + sharpening/scaling
│   ├── pacer.rs                # VRR-aware sub-frame QPC pacer (fixed 2x/3x or adaptive target Hz)
│   ├── presenter.rs            # Borderless DirectComposition swapchain + WaitableObject + click-through HWND
│   ├── shaders/                # Compiled DXIL (.cso) / HLSL compute shaders (pyramid, flow, hud_mask, warp, cas)
│   └── telemetry.rs            # Reports capture_ms, flow_ms, synth_ms, real_fps, output_fps over exo-wire
```

- **Contract Integration:** Add `framegen.flow.*` methods (`start`, `stop`, `status`, `setProfile`) to `host-v1.json` / `exo-wire` and make `RouteExoFlow` the default `TierExternal` fallback in `FrameGenRouter`.

---

## 3. Automation & Intelligence Design

1. **Zero-Config Auto-Routing:** When a user turns on Frame Generation for any game:
   - Native in-game FG → use native.
   - Offline game with upscaler → Spec #02.
   - Online anti-cheat game, legacy DX9/10/11/OpenGL/emulated game, or 60-FPS-capped title → **Exo Flow activates automatically on launch**.
2. **Automatic Base-FPS Pairing:** Pairs automatically with `FrameCapPlanner` to cap base FPS to an exact clean divisor of the display's VRR ceiling (e.g., `60 → 120` on 120 Hz, `72 → 144` on 144 Hz, `80 → 240` on 240 Hz) and ensures GPU headroom (`<85%` base GPU load) so optical flow never starves the game.
3. **Closed-Loop Latency & Artifact Guard:** Reads live `exo-present` metrics; if base FPS drops below 40 FPS (where external FG feels floaty) or GPU busy exceeds 95%, Exo Flow automatically scales down flow resolution (`1/2 → 1/4`) or notifies the user cleanly instead of stuttering.
4. **Per-Game Memory:** Remembers optimal mode (2× vs Adaptive, NVOF vs Compute, HUD mask sensitivity) per game ID automatically.

---

## 4. Measurement Plan

Run on an isolated synthetic 60 FPS animated DX12 test window (`exo-flow-bench`, **never** touching installed Exo or anti-cheat games):

| Metric | Baseline (No FG / LSFG 3.1 published behavior) | Target (`exo-flow`) | How Measured |
| --- | --- | --- | --- |
| **GPU Optical Flow + Synth Time (1440p, RTX 3070)** | LSFG 3.1 external overhead (~2–4 ms est.) | **<1.8 ms** (NVOF HW) / **<3.0 ms** (DX12 Compute) | DX12 `ID3D12QueryHeap` GPU timestamps over 1,000 frames |
| **Output Frame Pacing (p99 interval jitter)** | Unpaced drops/spikes | **<1.2 ms** p99 variance at 120 Hz | `PresentTrace` ETW on `exo-flow` swapchain |
| **Added Input Latency (Base 60 FPS → 120 FPS)** | 1–2 base frames | **≤1.25 base frames** (~20 ms at 60 FPS) | Raw Input timestamp → `exo-flow` present QPC delta |
| **Static HUD Distortion (PSNR on fixed crosshair/HUD)** | Warping without HUD mask | **>40 dB PSNR** on static HUD overlay vs ground truth | Automated frame-dump comparator on synthetic scene |
| **Sidecar Idle / Active RAM & CPU** | LSFG ~150+ MB | **0 MB** idle (not running) / **<80 MB WS, <1.5% CPU** active | `GetProcessMemoryInfo` + `GetProcessTimes` |

Output artifact: `improve/lab-exo-flow/bench.json` + side-by-side synthetic frame captures (`hud_mask_off.png` vs `hud_mask_on.png`).

---

## 5. Verification Gates

1. `cargo fmt --all -- --check` & `cargo clippy --workspace --locked -- -D warnings`.
2. `cargo test -p exo-flow --locked` (unit tests for VRR pacer math, HUD-mask threshold logic, DXIL shader compilation, pipe protocol).
3. **Capture Isolation Gate:** `ExoLauncher.Tests/BannedNativeImportsTests` confirms zero WGC/capture imports in `ExoLauncher.exe` or `exo-ui.exe` — capture stays strictly inside `exo-flow.exe`.
4. **Zero-Injection Gate:** Verify `exo-flow.exe` imports zero `WriteProcessMemory`, `CreateRemoteThread`, `SetWindowsHookEx`, or `VirtualAllocEx` symbols (`dumpbin /imports` check).
5. **Visual & Pacing Gate:** Synthetic 1440p 60→120 FPS run completes 1,000 frames with 0 device-lost errors and clean exit/cleanup.

---

## 6. Safety Rules & Hard Stops

- **Zero Anti-Cheat Risk:** Never open a handle to, inject into, or modify any game process or game directory. WGC operates strictly through Windows DWM compositor handles.
- **No Paid Tools / No Proprietary Code Theft:** Do NOT decompile or inspect Lossless Scaling binaries. Implement all compute shaders and NVOF API calls strictly from public NVIDIA Optical Flow SDK documentation and clean-room computer-vision algorithms.
- **Thermal / GPU Starvation Failsafe:** Auto-disengage if GPU device lost (`DXGI_ERROR_DEVICE_REMOVED`) or if game window minimizes/loses focus; immediately release overlay HWND so desktop is never stuck.
- **Never run installed Exo** during lab development; test strictly against standalone synthetic harness.
