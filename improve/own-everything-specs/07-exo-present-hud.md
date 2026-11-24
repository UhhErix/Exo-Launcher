# Spec 07 — Exo Present & Frame Lab (`exo-present`): Native Rust ETW PresentMon/CapFrameX Engine + Rune Zero-Injection Overlay

- **Inventory Rank:** #07 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** External **Intel PresentMon**, **CapFrameX**, **MSI Afterburner / RTSS** OSD & frame-time graphs, AND Exo's own C# WinUI3/Win2D `FpsOverlay.cs` (`Helpers/PresentTrace.cs`, `Services/FpsCounter.cs`, `Services/SessionRecorder.cs`, `Services/StutterAnalysis.cs`, `Helpers/CapLab*.cs`)
- **Anti-Cheat Class:** `AC-SAFE` (100% read-only Windows ETW + external topmost click-through DirectComposition overlay — zero DLL injection, zero game hooks, works in VALORANT/Vanguard, CS2, Fortnite/EAC, R6/BattlEye!)
- **License:** MIT (clean-room Rust using Windows `advapi32`/`tdh` ETW + Rune DX12 DirectComposition)

---

## 1. Scope

### What It Solves
1. Today `FpsOverlay.cs` is one of the last things forcing **WinUI 3 + Windows App SDK 2.7 + Win2D (~62 MB!)** to stay inside `ExoLauncher.exe`!
2. Exo's C# `PresentTrace.cs` counts basic FPS via ETW (`DXGI`, `D3D9`, `DxgKrnl`), but lacks PresentMon v2's **`GPUBusy`**, **`DisplayLatency`**, **`ClickToPhoton`**, and CapFrameX-grade **automatic stutter attribution & driver/patch A/B regression analysis**.
3. RTSS hooks directly into games' DirectX/Vulkan swapchains (frequently triggering anti-cheat warnings or crashes in new DX12/FG titles).

### In Scope
- **`exo-present` (Rust ETW Real-Time Consumer):**
  - Subscribes on-demand (strictly while a game is foreground!) to `Microsoft-Windows-DXGI`, `Microsoft-Windows-D3D9`, `Microsoft-Windows-DxgKrnl`, `Microsoft-Windows-Dwm-Core`, and optional lightweight `Kernel-Process`/`PerfInfo` (DPC spikes) using PID-filtered ETW sessions (`ExoPresentTrace`).
  - Computes real-time rolling & per-session metrics: **FPS**, **1% Low**, **0.1% Low**, **Frametime ms**, **GPUBusy ms** (GPU-bound vs CPU-bound ratio!), **DisplayLatency ms**, **Dropped/Torn frames**, and **Stutter count** (`>2× rolling median`).
- **Automatic Stutter Root-Cause Attribution (`stutter_ai.rs`):**
  - Correlates every frametime spike (`>16 ms` jump above rolling median) against live signals:
    - *Shader Compile Stutter* (CPU thread spike + driver cache write + GPU idle bubble) → triggers `exo-shaders` (Spec #04) recommendation.
    - *Background CPU Hog* (non-game PID CPU burst) → feeds `exo-autopilot` (Spec #14) to corral that process.
    - *Driver DPC/ISR Spike* (correlates with `exo-net-latency` Spec #15) → names exact `.sys` driver.
    - *VRAM Oversubscription / Paging* (`GpuProcessMonitor` shared memory burst) → recommends texture/resolution tweak via `exo-tune`.
    - *GPU Thermal/Power Throttle* (correlates with `exo-sensors` Spec #10) → links to `exo-gpu` undervolt / `exo-thermal` fans.
- **Rune Zero-Injection Overlay (`exo-hud`):**
  - Lightweight borderless, topmost, click-through (`WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE`) Rune/DirectComposition surface matching Exo's `DESIGN.md` Liquid Glass + Goeist Mono tokens!
  - Replaces `FpsOverlay.cs` and `AchievementBanner.cs` — **unblocking WinUI 3 / Win2D removal in Spec #20 Phase 1!**
- **Automated Session History & Driver/Patch A/B Compare:**
  - Automatically stores compact binary session summaries (`<2 KB`/session in SQLite) tagged with `game_version` + `gpu_driver_version` + `exo_profile_hash`, surfacing instant before/after comparisons whenever a GPU driver or game updates!

### Out of Scope
- In-game swapchain hooking (RTSS-style injection is intentionally rejected for anti-cheat safety).

---

## 2. Native Rust Crate Layout

```text
crates/exo-present/
├── Cargo.toml                  # windows (Etw, ProcessStatus, Dxgi), parking_lot, serde
├── src/
│   ├── lib.rs                  # Public trace handle + ring-buffer snapshot stream
│   ├── etw_session.rs          # PID-filtered StartTraceW / EnableTraceEx2 / OpenTraceW / ProcessTrace
│   ├── state_machine.rs        # PresentMon-style swapchain lifecycle state machine (Present -> Queue -> Flip -> Display)
│   ├── metrics.rs              # Lock-free circular histogram (FPS, 1%/0.1% lows, GPUBusy, DisplayLatency, P99)
│   ├── stutter_attr.rs         # Multi-signal stutter classifier (Shader / CPU-Hog / VRAM / Thermal / DPC / Pacing)
│   ├── cap_planner.rs          # Closed-loop FrameCapPlanner (computes ideal VRR/Reflex/LFC cap from live data)
│   └── session_store.rs        # Compact per-game/driver-version A/B comparison history

crates/exo-ui/src/
└── hud_overlay.rs              # Native Rune DComp click-through FPS/frametime/achievement HUD (replaces FpsOverlay.cs!)
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config Always-Learning Benchmark:** Never asks the user to press "Start Capture" like CapFrameX — silently logs compact frame-pacing health for every game session at `<0.3%` CPU, and immediately alerts after a driver update if average FPS or 1% lows dropped by `>8%`!
2. **Closed-Loop Frame-Cap Recommendation:** Automatically detects VRR display ceiling + whether a game is CPU- or GPU-bound (`GPUBusy / FrameTime`) and suggests/applies the exact stutter-free FPS cap (`monitor_hz - 3` or stable 1% low ceiling).
3. **Permission Auto-Setup:** Checks `Performance Log Users` / ETW session ACL (`ExoFpsCounter` GUID) once cleanly, matching `PresentTrace.cs` non-admin runtime model.
4. **Adaptive Overlay Visibility:** Auto-hides on desktop/alt-tab, auto-dims on OLED monitors (`pixel-shift` every 3 minutes to prevent burn-in!), and renders zero frames when hidden.

---

## 4. Measurement Plan

Measured against a synthetic 120 FPS DX12 test window (including injected artificial 45 ms stutter pulses to test attribution accuracy):

| Metric | Baseline (C# `PresentTrace` + Win2D `FpsOverlay` / RTSS) | Target (`exo-present` + Rune HUD) | How Measured |
| --- | --- | --- | --- |
| **ETW Consumer CPU Overhead (144 FPS game)** | C# ETW ~0.6–1.2% CPU + GC pauses | **<0.3% CPU, 0 GC pauses** | `GetProcessTimes` over 60 s trace |
| **Overlay RAM & Disk Savings** | WinUI3/Win2D stack (~62 MB disk + Win2D visual tree) | **0 MB** extra disk (lives in `exo-ui`), **<8 MB** WS | Install size diff & `GetProcessMemoryInfo` |
| **Present Event Accuracy vs PresentMon CLI** | Reference baseline | **±0.5% FPS / ±0.3 ms** frametime agreement | Simultaneous trace against official PresentMon CLI on synthetic DX12 app |
| **Stutter Attribution Recall (Synthetic Faults)** | None in current `PresentTrace` | **≥90% correct classification** across synthetic CPU-hog, VRAM, and shader stutter tests | Deterministic fault-injection test suite |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-present --locked -- -D warnings`, `cargo test -p exo-present --locked`.
2. **State Machine Unit Tests:** Replay recorded synthetic ETW event streams covering DX12 flip-model, DX11 blit, Vulkan kernel-flip fallback, `DXGI_PRESENT_TEST` filtering, and Alt-Tab focus loss.
3. **Stale ETW Session Cleanup Test:** Simulate crash (`TerminateProcess`) and verify next startup cleanly stops orphaned `ExoPresentTrace` real-time session before opening a new one.
4. **Zero-Hook Gate:** Verify zero `SetWindowsHookEx` / `CreateRemoteThread` / injection imports.

---

## 6. Safety Rules & Hard Stops

- **Zero Game Touching:** Never open a handle to or inject into any game process; ETW is 100% kernel-passive read-only telemetry.
- **No Privileged Permanence:** Never require Exo to run as Administrator; use the least-privilege session GUID ACL model already established in `Helpers/PresentTrace.cs`.
- **OLED Burn-In Protection:** Any persistent HUD overlay MUST implement subtle automatic pixel-shifting and transparency dimming.
- **Never Run Installed Exo:** Run synthetic DX12 window tests strictly in isolation.
