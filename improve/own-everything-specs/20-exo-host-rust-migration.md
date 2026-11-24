# Spec 20 — Exo Host Rust Migration (`exo-host` + `exo-wire` + `exo-tune`): Phased 5-Stage Port of the `.NET 10` Host to Unified Rust

- **Inventory Rank:** #20 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `XL` (Phased incrementally over Phases 0–5 with shippable wins at every phase!)
- **Replaces:** `.NET 10` Host (`apps/launcher/ExoLauncher/`, **804 C# files / 207,483 LOC**, `529` P/Invokes, `39` COM interfaces, `~62 MB` WinUI3/WASDK/Win2D + `~58 MB` CoreCLR/BCL/R2R), reflection JSON-RPC (`WebHostBridge*.cs`), and hardcoded C# `GameSettings.*.cs`
- **Anti-Cheat Class:** `AC-SAFE` (preserves strict process isolation for `exo-capture.exe`, `exo-flow.exe`, and opt-in `exo-macros-worker.exe` so `ExoLauncher.exe` never gains banned capture/input symbols!)
- **License:** MIT (100% Exo Rust workspace)

---

## 1. Scope

### What It Solves
Auditing `apps/launcher/ExoLauncher/` directly revealed why staying split across `.NET 10` + Rust forever holds Exo back:
- **Disk bloat:** `ExoLauncher` ships **~120 MB** of `.NET 10` CoreCLR + WinUI 3 + Windows App SDK 2.7 + Win2D (out of `380.02 MB` total installed footprint), plus another `~68 MB` duplicate CoreCLR in Shaders.
- **Memory & Cold Start:** Running two heavy runtimes (`ExoLauncher.exe` CoreCLR GC heap + `exo-ui.exe` Rust/Rune) duplicates DTOs in memory, pays a named-pipe JSON serialization tax (`5,322.9 µs` live RTT for a 200-game `LibraryPayload` measured on this PC!), and pushes total idle RAM to ~280–350+ MB instead of Rune's native **120 MB** baseline.
- **Dual-language tax:** `529` `[DllImport]` and `39` `[ComImport]` declarations in C# are *already* calling Win32/DXGI/ETW/NVAPI/NVML/ADLX/IGCL/WASAPI/HID — plus `363` RPC methods & `83` events must be mirrored manually between C# `WebHostBridge*.cs` and Rust `exo-rpc`!

### In Scope — The 5-Phase Zero-Downtime Migration Plan
Instead of a risky big-bang rewrite, every phase ships measurable wins while keeping `host-v1.json` 100% green:

- **Phase 0 — Immediate Quick Wins & Binary Contract (`2–3 weeks`):**
  1. Enable **`mimalloc`** global allocator + strip unused Rune default features (`embedded-fonts`, `resvg`, `symphonia-ogg`, `dbus`) + `-C target-cpu=x86-64-v3` in `native-ui/Cargo.toml` (proven **9.53× faster** UI tree churn & **4.18×** RPC handoffs on this PC!).
  2. Graduate **`exo-wire`** (`lab/own-rpc-codec` `4a9e3ff`) with a compile-time code generator reading `ui/contracts/host-v1.json` → emits both Rust & C# binary codecs (**11.78× faster** live pipe RTT: `5,322.9 µs → 451.9 µs`, **-33.4%** wire bytes, **1.97 GB/s** raw media pipe, **eliminates reflection JSON** to unblock `.NET NativeAOT`!).
  3. Graduate **`exo-tune`** (`lab/own-tune-dsl` `1fa1b82`) hot-updatable game-config DSL (**269 KB**, **6.36 KB** binary pack, **44.0 µs** eval, compile-time anti-cheat/safety validator).
- **Phase 1 — Headless `.NET NativeAOT` Interim Host (`3–4 weeks`):**
  1. Graduate Rune native `hud_overlay.rs` (Spec #07) & `AchievementBanner` → delete `FpsOverlay.cs` & `AchievementBanner.cs`.
  2. Move lazy WebView2 hosting (`ICoreWebView2CompositionController` per `lab-web-in-rune`) directly into `exo-ui` → delete `MainWindow*.cs` and ALL WinUI 3 / Windows App SDK 2.7 / Win2D NuGet packages from `ExoLauncher.csproj` (**-62 MB disk immediately!**).
  3. Flip `ExoLauncher.csproj` to **`PublishAot=true`** (~18–25 MB headless AOT daemon while Phases 2–4 move domain logic to Rust!).
- **Phase 2 — Hardware, Tuning & Peripherals Crates (`6–8 weeks`, ~45% of host LOC retired):**
  - Graduate Specs #05 (`exo-hid-devices`), #07 (`exo-present`), #08 (`exo-gpu`), #09 (`exo-thermal`), #10 (`exo-sensors`), #11 (`exo-controller`), #12 (`exo-macros`), #13 (`exo-light`), #14 (`exo-autopilot`), #15 (`exo-net-latency`), #16 (`exo-display`), #17 (`exo-audio-apo`) into the Cargo workspace and dispatch their `host-v1` routes directly in-process inside Rust, deleting the corresponding C# `Services/`, `Helpers/`, and `WebHostBridge.*.cs` files!
- **Phase 3 — Stores, Library, Shaders, Capture & Updates (`6–8 weeks`, ~80% of host LOC retired):**
  - Graduate Specs #01 (`exo-flow`), #02 (`exo-upscale`), #04 (`exo-shaders`), #06 (`exo-capture`), #18 (`exo-stores`), #19 (`exo-delta`/`exo-installer`), plus `exo-library` (`rusqlite` WAL), `exo-launch`, `exo-compact`, `exo-hygiene`, and `exo-anti-cheat` (Rust port of `AntiCheatCheck.cs` with 100% golden test parity!).
- **Phase 4 — Discord, Voice & Music Native Crates (`6–8 weeks`, ~98% of host LOC retired):**
  - Graduate Spec #03 (`exo-voice-ml`), `lab-auth-native` (`exo-auth-native`), `exo-discord` (port of `24.1k` LOC `Services/DiscordClient/` with zero-copy Go-Live DX12 textures directly into Rune!), and `exo-music-hub` (metadata/lyrics/adapter in Rust + lazy suspended `--disable-gpu` WebView2 DRM audio sink).
- **Phase 5 — Single-Binary Unification (`2–3 weeks`, 100% Rust!):**
  - Retire the remaining C# bridge & named-pipe hop; `exo-ui` calls `exo-host` traits directly in-process (**0 µs** latency, **0** duplicate DTOs in RAM!).
  - Final shipped binary topology:
    1. `ExoLauncher.exe` (unified Rust/Rune UI + Host, single process!)
    2. `exo-capture.exe` (isolated MIT capture sidecar — preserves `BannedSymbols` capture firewall!)
    3. `exo-flow.exe` (isolated MIT optical-flow FG sidecar — on demand only!)
    4. `exo-macros-worker.exe` (isolated MIT Raw Input sidecar — on demand only!)

### Out of Scope
- Merging screen-capture (`exo-capture.exe`, `exo-flow.exe`) or `SendInput` (`exo-macros-worker.exe`) into `ExoLauncher.exe` (the process-isolation security firewall in `AGENTS.md` MUST be preserved permanently!).
- Breaking `host-v1.json` compatibility during Phases 0–4 while both runtimes coexist.

---

## 2. Native Rust Workspace Architecture (End-State)

```text
apps/launcher/native-ui/        # Unified Cargo Workspace (Rust edition 2024, mimalloc, x86-64-v3)
├── Cargo.toml
├── crates/
│   ├── exo-wire/               # Binary host-v1 codec + compile-time schema codegen (Phases 0-4 bridge)
│   ├── exo-policy/             # AntiCheatCheck (100% parity), NetworkDestinations, PlayerData, Keep ledger
│   ├── exo-tune/               # Declarative .exotune game-config compiler & format-preserving patcher
│   ├── exo-kit/                # Rune Liquid Glass, Void tokens, Goeist, Lucide icons, QR
│   ├── exo-ui/                 # Rune UI rooms + lazy WebView2 CompositionController + DComp HUD
│   ├── exo-host/               # In-process orchestrator unifying all domain crates below:
│   ├── exo-gpu/                # Spec #08 (NVAPI/NVML/ADLX/IGCL/undervolt/clean driver)
│   ├── exo-thermal/            # Spec #09 (GPU + PawnIO system fans + acoustic cal)
│   ├── exo-sensors/            # Spec #10 (HWiNFO + ZenTimings + health sentinel)
│   ├── exo-present/            # Spec #07 (ETW PresentMon/CapFrameX + stutter attribution)
│   ├── exo-autopilot/          # Spec #14 (CPU Sets + ProBalance + 0.507ms timer + standby)
│   ├── exo-net-latency/        # Spec #15 (DPC/ISR + click-to-photon + DNS/bufferbloat/QoS)
│   ├── exo-display/            # Spec #16 (Safe EDID watchdog + MHC2 HDR LUT + DDC/CI)
│   ├── exo-hid-devices/        # Spec #05 (Finalmouse/Wooting/Logitech/Razer HID + USB diag)
│   ├── exo-controller/         # Spec #11 (GameInput + DualSense Edge/Xbox/Switch)
│   ├── exo-light/              # Spec #13 (Win11 LampArray + HID + PawnIO RGB)
│   ├── exo-audio-apo/          # Spec #17 (Clean-room Windows Audio APO + AutoEq)
│   ├── exo-voice-ml/           # Spec #03 (Unified ORT DeepFilterNet3 + TitaNet)
│   ├── exo-upscale/            # Spec #02 (Signed upscaler vault + offline FG bridge)
│   ├── exo-shaders/            # Spec #04 (Clean-room MIT PSO & driver cache engine)
│   ├── exo-stores/             # Spec #18 (Native Epic/GOG/Amazon + Steam IPC)
│   ├── exo-discord/            # Wave 2 (Native Discord Gateway/REST/Voice/Go-Live + QR auth)
│   ├── exo-delta/              # Spec #19 (zstd prefix-dict + BLAKE3 + Ed25519 updater)
│   ├── exo-installer/          # Spec #19 (Direct2D <1.5 MB installer/uninstaller)
│   ├── exo-capture/            # Spec #06 (Isolated sidecar binary exo-capture.exe)
│   ├── exo-flow/               # Spec #01 (Isolated sidecar binary exo-flow.exe)
│   └── exo-macros-worker/      # Spec #12 (Isolated sidecar binary exo-macros-worker.exe)
```

---

## 3. Automation & Intelligence Design

1. **Zero-Drift Schema Codegen:** One build script reads `ui/contracts/host-v1.json` and emits strongly typed Rust & C# encoders/decoders during Phases 0–4 — eliminating manual DTO drift across 363 methods and 83 events!
2. **Continuous Regression Guard:** Every phase runs side-by-side parity tests comparing C# host answers vs Rust crate answers (`AntiCheatCheck`, `FrameGenRouter`, `Keep`, `Library`, `GpuProfile`) before deleting the C# implementation!
3. **Self-Contained Performance Budgets:** CI enforces hard binary-size, idle-RAM, cold-start, and banned-import budgets automatically on every PR so bloat can never creep back in.

---

## 4. Measurement Plan

Grounded in real measurements already captured on this RTX 3070 / Ryzen 5 5600X PC (`deps-best-or-own` & `lab-web-in-rune`) plus phase-exit benchmarks:

| Metric | Current v4 Baseline (Measured on this PC) | Phase 1 Milestone (NativeAOT + Quick Wins) | Phase 5 End-State Target (Unified Rust) | How Measured |
| --- | --- | --- | --- | --- |
| **Installed Disk Footprint** | **380.02 MB** (`1,039` files) | **~195 MB** (-62 MB WinUI3, -55 MB Voice, -68 MB Shaders) | **<75 MB** (**-80%!**) | Directory byte sum |
| **Idle Working Set (Library Room, 3 min)** | Pure Rune `120 MB` + `.NET` host `~180–250 MB` = **~300–370 MB** | **~210 MB** | **<125 MB** (single process, 0 children!) | `tools/measure-memory.ps1` |
| **Cold Start to Interactive Library** | ~1.2–2.2 s (2 runtimes + named-pipe connect) | **<700 ms** | **<250 ms** | QPC startup trace (`StartupTiming`) |
| **RPC / State Dispatch Latency (200-Game Library)** | **5,322.9 µs** (JSON-RPC named pipe, measured!) | **451.9 µs** (`exo-wire`, **11.78× faster**, measured!) | **<10 µs** (in-process Rust call, **>500× faster!**) | `exo-wire` / in-process Criterion bench |
| **UI Frame Allocation Churn (200 × 1,555 nodes)** | **303.0 ms** (`HeapAlloc`, measured!) | **31.8 ms** (`mimalloc`, **9.53×**, measured!) | **<1.0 ms** (`FrameArena`, **>300×**, measured `0.39 ms`!) | Rune frame timing bench |
| **Languages / Build Toolchains Required** | 5 (`dotnet`, `cargo`, `npm`×2, `cmake/meson/vcpkg`, `NSIS`) | 3 | **1 primary (`cargo`)** (+ optional `libopus` C build) | CI workflow audit |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy --workspace --locked -- -D warnings`, `cargo test --workspace --locked`.
2. **Phase-by-Phase Shadow Parity Gate:** Before retiring ANY C# service in Phases 2–4, run deterministic differential tests verifying Rust crate output matches C# `ExoLauncher.Tests` fixtures 100% (especially `AntiCheatCheck`, `NetworkDestinations`, `PlayerData`, `DeletionGate`, `ProcessTermination`!).
3. **Banned-Imports & Security Gate:** Automated PE import table scanner verifies `ExoLauncher.exe` NEVER imports screen-capture (`BitBlt`, `PrintWindow`, `WGC`), synthetic input (`SendInput`, `mouse_event`, `keybd_event`), or unguarded `TerminateProcess` without anti-cheat tree check.
4. **Footprint & Memory Gate:** Automated CI check fails if total release footprint exceeds phase budget or idle WS regresses by `>10%`.
5. **E2E Suite Gate:** Full `tools/e2e` (adapted for pure native Rune) passes on Windows 11 before declaring any phase shipped.

---

## 6. Safety Rules & Hard Stops

- **Zero Big-Bang Rewrite:** Never delete a working C# subsystem until its Rust replacement passes 100% parity tests and real Windows 11 verification.
- **Port Every Hard Stop First (Phase 2 Prereq):** `exo-policy` MUST port `AntiCheatCheck`, `ProcessTermination` (anti-cheat process-tree kill refusal!), `DeletionGate` (recursive-delete guard), `NetworkDestinations` (allowlist test), and `PlayerData` (backup/sync registry) BEFORE any Rust crate touches files, processes, or networks!
- **Preserve Sidecar Firewall:** Screen capture (`exo-capture.exe`, `exo-flow.exe`) and software input synthesis (`exo-macros-worker.exe`) remain strictly isolated separate executables forever.
- **Never Run Owner's Installed Exo** during lab/migration prototyping.
