# Exo Native UI — Rune Fork Performance Profile & Subsystem Ownership Plan

- **Hardware / OS:** NVIDIA GeForce RTX 3070 (Driver `32.0.16.2135`, 8 GiB VRAM), Windows 11 x64
- **Exo Baseline:** `origin/feat/native-polish-2` (`2746258`), Rune fork pin `ba9aa615f2cc31262653dd3b7f3ee37534443b19`
- **Rune Fork Changes:** `UhhErix/rune` branch `feat/exo-own-perf` (`385448863a55785cce872ef8789cf1f473b02aca`, based on `origin/exo` `f87daae9`)
- **Exo Branch:** `feat/native-rune-own-perf` (PR target: `feat/native-polish-2`)
- **Test Isolation:** All runs executed against repo-built `ExoLauncher.exe --host-pipe=<ephemeral>` with an isolated temp `EXO_LAUNCHER_DATA_DIR` or standalone 12-game demo mode. The owner's installed Exo and devices were never started or modified.
- **Tooling Used:** Win32 `GetProcessMemoryInfo` / `GetProcessTimes` / `\GPU Process Memory(*)` counters, Rune `frame-profile` (`RUNE_FRAME_LOG` per-stage ns JSONL + `--window-script`), Rune `alloc-sites` (`procwatch::alloc::Counting` 32 KiB sampled callstacks + `DbgHelp` PDB symbolization + `section nt-heaps` + `section rune-gpu` + `wgpu/counters` `device.generate_allocator_report()`), `cargo-bloat v0.12.1`, MSVC `dumpbin /headers`, `tools/parity/parity-compare.py`. (`xperf`, `pixtool`, and `tracy` were not installed on the host; `wpr` was present and Rune's built-in Windows `procwatch` + `wgpu/counters` + `RUNE_FRAME_LOG` provided direct byte-level D3D12 suballocator and nanosecond per-frame attribution.)

---

## Phase 1: Baseline Profile (Measured Numbers Only)

### 1.1 Process Memory, Idle CPU & GPU VRAM (Unmodified Release `exo-ui.exe`)

Measured on clean zero-load Release `exo-ui.exe` (`lto = "thin"`, `codegen-units = 1`, `strip = true`, `panic = "abort"`):

| Scenario | Sample Window | Working Set | Peak WS | Private Bytes | Threads | Handles | GPU Dedicated | GPU Shared | GPU Committed | Idle CPU (20s window) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Standalone Demo (12 games) | T+5s | 137.27 MiB | 140.02 MiB | 192.27 MiB | 23 | 514 | 52.85 MiB | 55.73 MiB | 108.58 MiB | — |
| Standalone Demo (12 games) | T+25s (post-rest) | 104.02 MiB | 140.02 MiB | 192.27 MiB | 22 | 512 | 52.85 MiB | 55.73 MiB | 108.58 MiB | 62.50 ms (`0.311%` 1-core / `0.026%` sys); run 2: 31.25 ms (`0.155%` 1-core) |
| Dev Repo Host (temp DataDir) | T+5s (CPU-boot, pre-handover) | 58.31 MiB | 58.31 MiB | 67.01 MiB | 21 | 424 | 7.04 MiB | 3.30 MiB | 10.34 MiB | — |
| Dev Repo Host (temp DataDir) | T+25s (GPU handed over) | 106.26 MiB | 127.95 MiB | 177.11 MiB | 23 | 504 | 52.83 MiB | 44.36 MiB | 97.18 MiB | 140.62 ms (`0.702%` 1-core incl. handover + 1 Hz heartbeat) |
| Dev Repo Host (populated scan) | T+25s (6 image pages loaded) | 200.79 MiB | 260.63 MiB | 347.46 MiB | 35 | 723 | 149.00 MiB | 68.73 MiB | 217.73 MiB | 484.38 ms (`2.401%` 1-core during active scan/bootstrap) |

**Key observation on wgpu DX12 footprint:** Comparing Dev Repo Host T+5s (still on `cpu_boot`) against T+25s (after DX12 GPU handover on the exact same idle screen) isolates the wgpu DX12 + NVIDIA UMD (`nvwgf2umx.dll`) + DComp swapchain initialization delta: **+47.95 MiB Working Set**, **+110.10 MiB Private Bytes**, **+45.79 MiB GPU Dedicated**, and **+41.06 MiB GPU Shared**.

### 1.2 Idle Memory Breakdown by Subsystem (1920x1080, Symbolized `procwatch` + `wgpu/counters`)

#### A. Live Rust Heap & Windows NT Heap Breakdown

| Subsystem | Standalone Demo T+14s (Post-Rest) | Share | Connected Host (Populated Library) | Share | Primary Allocation Sites |
|---|---:|---:|---:|---:|---|
| **Glyph atlases & fonts** | **4.16 MiB** | **54.0%** | **13.47 MiB** *(incl. 9.11 MiB `ImageAtlas` CPU mirror)* | **25.5%** | `rune_text::atlas::Atlas::alloc` (**4.00 MiB** eager `vec![0; 2048*2048]`) + `rune_image::pixels::ImageAtlas::place_at` (7.11 + 2.00 MiB in Host) |
| **Image decode & caches** | 0.00 MiB | 0.0% | **26.99 MiB** | **51.1%** | `rune_image::mips::mip_chain` (`decode_capped`, 26.99 MiB across 90 samples) |
| **Exo app state / RPC / views** | 2.21 MiB | 28.7% | 10.81 MiB | 20.5% | `exo_ui::video_decoder::nv12_to_rgba` (0.88 MiB demo), `RawVec` growth, `StoreTile` JSON, `runtime::types::Node` (0.18 MiB), `rune_motion::scene::Node` (0.17–0.33 MiB) |
| **Rune runtime / window / a11y** | 0.56 MiB | 7.3% | 0.96 MiB | 1.8% | `winit::keyboard_layout::LayoutCache` (0.31 MiB), `GlassPaintState` (0.23 MiB), `Presented` (0.11 MiB), `Damage::plan` (0.07 MiB) |
| **wgpu / renderer CPU side** | 0.50 MiB | 6.5% | 0.50 MiB | 0.9% | `wgpu_hal::dx12::create_shader_module` (0.25 MiB), `Renderer::prepare_with_precision` (0.07 MiB) |
| **Layout tree (`rune-taffy`)** | 0.12 MiB | 1.6% | 0.11 MiB | 0.2% | `rune_taffy::store::RetainedSlot` (0.05–0.11 MiB), `CompactStyle` (0.06 MiB) |
| **Other (`rune`)** | 0.14 MiB | 1.9% | 0.06 MiB | 0.1% | Misc `RuntimeCore::build` scratch buffers |
| **Total Rust Heap Live / Peak** | **7.73 MiB / 7.82 MiB** | **100%** | **52.89 MiB / 106.84 MiB** | **100%** | `procwatch::alloc::Counting` exact counter (0 dropped) |
| **Windows NT Heaps (6 heaps)** | Busy `14.46 MiB` / Committed `13.02–19.22 MiB` / Uncommitted `5.83–15.44 MiB` / Big(>512K) `4.88 MiB` | — | Busy `61.59 MiB` / Committed `32.74 MiB` / Uncommitted `17.73 MiB` / Big(>512K) `45.45 MiB` | — | Process-wide NT Heap overhead & virtual commit slack (`Committed - Busy = +4.35 MiB` at T+9s demo) |

#### B. GPU Memory Breakdown (`wgpu/counters` + D3D12 `gpu-allocator` Report, 1920x1080)

| D3D12 Resource / Pool | Standalone Demo T+14s (Post-Rest) | Connected Host (Populated) | Notes / Root Cause |
|---|---:|---:|---|
| **`effect backdrop`** | **25.31 MiB (2x)** | **25.31 MiB (2x)** | 1x `Bgra8Unorm` 1920x1080 (`8.44 MiB`) + **1x unused `Rgba16Float` 1920x1080 (`16.87 MiB`)** allocated unconditionally whenever any unrelated isolated layer exists (`upload.rs:351`) |
| **`isolated group` (`layers`)** | **5.25 MiB (7x)** | **25.44 MiB (9x)** | `Rgba16Float` (8 B/px); Host had `layer-live = 20.00 MiB (1x)` + `layer-spare = 5.44 MiB (8x)`; never released on visible rest (`give_back(false)`) |
| **`image atlas`** | 0.06 MiB (1x) | **20.50 MiB (8x)** | 6 live `image-pages` (`17.11 MiB` raw, `20.50 MiB` suballocated) |
| **`last frame` (`damaged::Retained`)** | 0.00 MiB (0x) | **8.44 MiB (1x)** | Full-window 1920x1080 texture kept forever despite `redraws_in_place()` returning `Err("effect")` on 100% of Liquid Glass frames |
| **`liquid glass frost pyramid`** | **2.06 MiB (1x)** | **4.56 MiB (1x)** | 7-mip `Rgba16Float` down pyramid; never freed on visible rest because `self.backdrop_used == true` |
| **`rune staging` (upload chunks)** | 0.25 MiB (2x) | 3.38 MiB (5x) | Staging buffers retained on visible rest (only `> 4 MiB` chunks trimmed) |
| **Glyph atlas + outlines** | 0.75 MiB (2x) | 1.25 MiB (3x) | GPU `glyph atlas` is only **0.50 MiB** (`2048x256` rows) + `0.25–0.75 MiB` `glyph outlines` |
| **Instances, uniforms, probes, wgpu internal** | 3.26 MiB | 3.56 MiB | `instances` (0.69–0.81 MiB), `zero init` (0.50 MiB), `layer projection` (0.44–0.56 MiB), Liquid Glass sampler/uniform/probe buffers |
| **Total D3D12 Allocated (`textures` + `buffers`)** | **36.94 MiB** (`15 tex / 31 buf`) | **92.44 MiB** (`26 tex / 39 buf`) | `views`: 88 (Demo) / 265 (Host); `bind_groups`: 29 (Demo) / 86 (Host); `pipelines`: 12 |
| **Total D3D12 Reserved (`gpu-allocator`)** | **64.00 MiB** (5 blocks) | **136.00 MiB** (7 blocks) | `27.06 MiB` (Demo) / `43.56 MiB` (Host) D3D12 heap block slack (32–42%) |

### 1.3 Cold Start Split by Phase (Process Start to First Frame)

Measured via `EXO_BOOT_LOG` timestamps + `RUNE_FRAME_LOG` + Win32 `FindVisibleWindow` + headless `gpu init`:

| Phase | Standalone Demo | Connected Dev Host | What Happens |
|---|---:|---:|---|
| 1. `CreateProcess` -> `main_enter` | 1.2 ms | 0.2 ms | PE image load (`33.63 MiB`), CRT/Win32 DLL init |
| 2. `main_enter` -> `before_app_run` | 11.2 ms | 48.1 ms | `App::new(spec)`, `gamepad::start_polling`, `Tray::new`, `exo-rpc-connect` thread spawn |
| 3. `before_app_run` -> `setup_state_enter` | 40.1 ms | 13.7 ms | winit `EventLoop` + HWND creation, `TextSystem::for_window` (DirectWrite + `Goeist-Variable.ttf`) |
| 4. `setup_state` (`preload_visible_covers_sync`) | 1.2 ms | 0.4 ms | Waker install, cover cache init, `shell.painted` notification |
| 5. `setup_state_exit` -> `on_window` (HWND ready) | 6.2 ms | 6.6 ms | Win32 window creation & DComp cloak setup |
| 6. Frame 0 CPU-boot build + paint | 60–90 ms *(view 10.8–15.7 ms, commit 25.1–32.2 ms, layout 9.5–10.1 ms, text 7.5–8.2 ms, paint 26.1–30.2 ms)* | 120–232 ms *(with live host RPC bootstrap)* | Initial view tree, `rune-taffy` layout, Goeist glyph shaping/rasterization, CPU-boot raster |
| 7. Window Visible (`IsWindowVisible`, cloak lifted) | **324.2–530.5 ms** | **41.5–694.9 ms** *(depends on host pipe readiness)* | First frame presented to DWM |
| 8. Background GPU Handover (wgpu DX12 init) | **243–444 ms** (async after CPU frame 0) | **243–444 ms** (async) | DX12 adapter enumeration, `request_device`, 12 DXBC render pipelines, first GPU frame, DComp visual swap |

### 1.4 Frame Time Distributions (1920x1080 on RTX 3070, `RUNE_FRAME_LOG`)

| Workload (1920x1080) | Frames | Total p50 | Total p95 | Total p99 | Max | `build` p50 (`paint` p50) | `encode` p50 | **`submit` p50 / p99** | `present` p50 | Redraw Path |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| **Idle (Standalone Demo, 2.0–9.5s)** | **0** | 0.000 ms | 0.000 ms | 0.000 ms | 0.000 ms | — | — | — | — | Truly asleep (`ControlFlow::Wait`) |
| **Library Scroll (Demo, 10.0–13.5s)** | 36 | **4.170 ms** | 10.781 ms | **87.591 ms** | 87.591 ms | 0.358 ms (0.242 ms) | 0.363 ms | **2.769 / 17.364 ms** | 0.421 ms | 100% `whole: effect` (36/36) |
| **Library Scroll (Host, 10.0–13.5s)** | 49 | **3.061 ms** | 10.350 ms | **35.078 ms** | 35.078 ms | 0.641 ms (0.503 ms) | 0.251 ms | **1.309 / 32.160 ms** | 0.412 ms | 100% `whole: effect` (49/49) |
| **Game Page Open + Flight (Demo, 14.0–17.5s)** | 81 | **3.891 ms** | 6.575 ms | **14.656 ms** | 134.462 ms | 0.940 ms (0.716 ms) | 0.344 ms | **2.098 / 4.705 ms** | 0.411 ms | 90% `most of it changed`, 7% `effect`; frame-0 open `build=96.2 ms` (`view 43.2, commit 57.0, layout 6.4, text 5.1, paint 23.7`) |
| **Glass/Blur Animations: Palette + Settings (Demo, 18.0–23.5s)** | 256 | **4.206 ms** | 5.633 ms | **8.067 ms** | 19.048 ms | 0.893 ms (0.700 ms) | 0.343 ms | **2.453 / 3.681 ms** | 0.411 ms | 80% `whole: effect` (204/256), 20% `many changed` (51/256); 245–406 nodes, 775–1109 instances |

**Key observation on frame time:** On steady-state interactive frames, `view`, `commit`, `layout`, and `text` are **0.000 ms p50** (memoized/retained). **`submit` (`queue.submit` through wgpu-core -> D3D12) dominates at 54%–68% of total frame time (`2.10–3.41 ms p50`)**, because every single frame redraws 100% of the 1920x1080 screen (`whole: effect`) and runs ~12+ render passes (isolated layers, full-viewport backdrop copy, 7-mip `Rgba16Float` Liquid Glass frost pyramid, luminance probe, glass composite).

### 1.5 Binary Size Breakdown (`exo-ui.exe` Release = `35,265,024 B` / `33.63 MiB`)

**PE Sections (`dumpbin /headers`):** `.text` = `27.53 MiB` (`28,868,680 B`), `.rdata` = `5.79 MiB` (`6,068,438 B`), `.pdata` = `236.4 KiB`, `.reloc` = `71.3 KiB`, `.data` = `18.5 KiB`.

**Top Crates (`cargo-bloat --release --crates`, unstripped `.text = 28.7 MiB`, total file `35.5 MiB`):**

| Crate / Stack | `.text` Size | `.text` % | Notes |
|---|---:|---:|---|
| `exo_ui` | 7.1 MiB | 24.6% | Monomorphized `El<AppState>` builder calls across 12 rooms |
| `rune` | 5.4 MiB | 18.7% | Largest methods: `RuntimeCore::build` (156.5 KiB), `apply` (54.2 KiB), `paint_content` (45.2 KiB), `dispatch_action` (31.1 KiB) |
| `std` | 3.0 MiB | 10.3% | Rust standard library |
| `serde_json` + `exo_rpc` + `serde_core` | 3.7 MiB | 12.7% | JSON-RPC DTO serialization/deserialization |
| **Text stack** (`rune_text` 1.1M, `skrifa` 443K, `harfrust` 390K, `read_fonts` 292K, `swash` 242K, `rune_edit` 189K, `zeno` 180K, `rune_spell` 85K, `fontique` 81K, `parley_engine` 57K, `yazi` 48K) | **3.10 MiB** | **10.8%** | Plus **1.25 MiB `.rdata`** unused embedded Inter fonts (`default` feature `embedded-fonts`) |
| **wgpu + naga stack** (`naga` 1.1M, `wgpu_core` 946K, `wgpu_hal` 311K, `wgpu` 72K, `wgpu_types` 48K) | **2.48 MiB** | **8.6%** | Full WGSL compiler `naga` linked despite precompiled DXBC shaders on Windows |
| **Image decode** (`image` 502K, `image_webp` 241K, `rune_image` 205K, `png` 77K, `zune_jpeg` 68K) | **1.09 MiB** | **3.8%** | Cover art decoding & mip generation |
| **Unused SVG stack** (`usvg` 230K, `tiny_skia` 185K, `tiny_skia_path` 87K, `resvg` 80K) | **581.7 KiB** | **2.0%** | Pulled by `rune` default feature `svg`; **0 callers** in Exo native-ui |
| `rune_media` | 327.5 KiB | 1.1% | Video playback support |
| `accesskit_windows` + `accesskit_consumer` | 275.8 KiB | 1.0% | Windows UI Automation |
| `rune_motion` + `taffy` | 319.5 KiB | 1.1% | Springs & layout math |
| `winit` | 145.5 KiB | 0.5% | Win32 event loop & windowing |

---

## Phase 2: Ranked Subsystems & Replace/Rewrite Plans

### 2.1 Subsystem Ranking (Highest Cost to Lowest Cost)

| Rank | Subsystem | Measured Baseline Cost | Root Cause | Potential Win |
|---:|---|---|---|---|
| **1** | **Compositor & Glass** (`rune::render::{upload, layers, liquid_glass, backdrop_blur, damaged}`) | **69.3% (Host) / 84.2% (Demo)** of D3D12 allocations wasted at rest (`32.6–67.1 MiB`); **16.6 MiB** unused `Rgba16Float` layer backdrop even while active; `submit` = **2.1–3.4 ms p50** (58–68% of frame); 100% `whole: effect` redraws | Unconditional `layers::FORMAT` backdrop in `upload.rs:351` & `pipelines.rs:278`; `give_back(false)` keeps all backdrops, frost pyramid, scratch layers & `last frame` alive whenever `backdrop_used == true`; `damaged.rs:182` disables partial damage whenever Liquid Glass exists; 7-mip `Rgba16Float` pyramid runs every frame via wgpu | **-16.9 MiB active / -34.5 MiB rest (-93%!) D3D12 VRAM** immediately; **<1 ms frame time** with damage-scoped glass / DComp |
| **2** | **Renderer & wgpu Usage** (`rune-gpu`, `wgpu-core`, `wgpu-hal`, `naga`, `gpu-allocator`) | **+110 MiB Private Bytes** & **+46 MiB GPU Ded / +41 MiB GPU Shared** on DX12 init vs CPU-boot; **2.48 MiB `.text`** (`naga` 1.1 MiB + `wgpu-core` 946 KiB); **32–42% D3D12 block slack** (`64–136 MiB` reserved); **243–444 ms** GPU init | Cross-platform `wgpu-core` validation, state tracking, barrier insertion, `naga` WGSL parser/IR, and coarse 32–64 MiB `gpu-allocator` heaps for a Windows-only DX12 app | **-1.1 MiB `.text`** removing `naga`; **-40 to -70 MiB Private/VRAM**, **-150 ms GPU init**, and **<0.5 ms `submit`** with a thin direct D3D12 backend (`windows` crate, analogous to Rune's native Metal backend) |
| **3** | **Text (Shaping & Glyph Atlas)** (`rune-text`, `parley`, `skrifa`, `harfrust`) | **4.00 MiB CPU heap** (`54.0%` of Demo idle heap!) for a `0.50 MiB` GPU atlas; **3.10 MiB `.text`** + **1.25 MiB `.rdata`** unused Inter `embedded-fonts`; cold-start text shaping `7.5–8.2 ms` (steady-state `0.000 ms p50`) | `Atlas::alloc` & `AtlasPage::new` eagerly allocate `vec![0; 2048*2048]` (4 MiB) and never shrink on eviction, even though GPU atlas starts at `256` rows (`512 KiB`); `exo-ui` enabled `rune` default `embedded-fonts` | **-3.50 MiB CPU heap per window (-45% Demo heap)** + **-1.25 MiB `.rdata`** immediately with lazy row-backed CPU atlas + stripping `embedded-fonts` |
| **4** | **Image Decode & Cache** (`rune-image`, `ImageAtlas`, `exo-ui/src/image_cache.rs`) | **56.6 MiB combined in Host mode**: `26.99 MiB` decoded `mip_chain` (`51.1%` of Host Rust heap) + `9.11 MiB` CPU `ImageAtlas` mirror + `20.50 MiB` GPU `image atlas`; `1.09 MiB` `.text` | Triple residency of visible game covers: `Decoded` full mip chain in `Pixels::keep` + CPU RGBA copy in `ImageAtlas::pages[i].data` + GPU texture | **-9.1 MiB CPU RAM** by dropping uploaded `ImageAtlas` CPU backing pages after GPU upload; **-15 to -20 MiB CPU RAM** by keeping only the target display mip |
| **5** | **Event Loop & Redraw Scheduling** (`rune::window`, `damaged.rs`, `exo-ui::shell`) | `0 frames` / `0.00–0.16%` 1-core CPU in Demo idle; **100% full-screen redraws (`whole: effect`)** on scroll/hover/typing; `tick_shell_heartbeat` wakes UI at 1 Hz in Host mode (preventing 10s `REST_AFTER` in Host mode!) | `damaged.rs:182` rejects partial damage globally if any Liquid Glass exists anywhere in the window; `tick_shell_heartbeat` uses `tx.wake_at(last + 1.0)` on the UI loop | **50–80% less GPU work** on hover/typing via glass-aware damage rects; moving heartbeat to background RPC thread unlocks 10s rest reclamation (`-40+ MiB` VRAM) in Host mode |
| **6** | **Allocator** (Windows CRT `HeapAlloc` vs `mimalloc`) | NT Heap: `4.35–5.37 MiB` committed slack, `15–18 MiB` uncommitted VA; `12k–24k` allocs on frame 0; **`mimalloc` tested and REJECTED** (`+33 MiB` Demo / `+140 MiB` Host Private Bytes regression across 23–47 threads!) | Multi-threaded desktop UI (23–47 threads across decode, RPC, Win32, DX12) suffers segment-per-thread bloat under `mimalloc`; Windows 11 Segment Heap / LFH is tighter | Keep Windows system allocator (or arena/bump scratch pools inside `RuntimeCore::build` to cut frame-0 alloc count without global per-thread segments) |
| **7** | **Animation** (`rune-motion`) | `0.17–0.33 MiB` heap, `165.8 KiB` `.text`, `paint` `0.61–0.72 ms p50` | Analytic springs are already lightweight on CPU; cost during animations is downstream full-frame glass compositing | Low standalone CPU cost; benefits automatically from #1 and #2 |
| **8** | **Layout** (`rune-taffy`) | `0.09–0.12 MiB` heap, `153.7 KiB` `.text`, **`0.000 ms p50` / `0.005–0.276 ms p99`** steady-state (`6.4 ms` cold Game Page layout) | **Already replaced in Rune** (`RetainedSlot` + 64B `CompactStyle` + 96B `CompactCache` + flat child arenas) | Already near-optimal; no rewrite warranted |

### 2.2 Top 5 Replace-or-Rewrite Plans

1. **Compositor & Glass — Lazy Scratch/Backdrop Management + Damage-Scoped Glass (Built Part 1 in Phase 3; Part 2 Next)**
   - **Implementation:** (Part 1, shipped in Phase 3) Track per-layer backdrop demand via `Layers::has_active_target_containing`, prune unused `Rgba16Float` layer backdrops, and reclaim all transient GPU scratch textures (`backdrops`, `FrostPyramid`, non-cache `layers.targets`, `damage.retained`, upload staging) on visible rest with immediate device poll. (Part 2, next) Extend `damaged.rs` to allow partial-rect redraws when the damage union does not intersect any Liquid Glass shape's expanded frost apron (`region + 2 * sigma`), reusing cached glass pixels in `Retained` instead of re-running the 7-mip pyramid on hover/caret frames. For static floating glass over unchanging backgrounds (e.g., Settings/Palette chrome), cache the downsampled frost pyramid across frames when no instance below the glass epoch changed.
   - **Expected numbers:** Part 1 delivers **-16.9 MiB active / -34.5 MiB rest (-93%) D3D12 VRAM** (verified below). Part 2 cuts hover/caret/typing frame `submit` from **2.4 ms -> <0.6 ms** and eliminates 70%+ of full-screen Liquid Glass passes.

2. **Direct D3D12 Backend for Rune on Windows (`rune-gpu::dx12`, replacing `wgpu-core` + `naga`)**
   - **Implementation:** Mirror Rune's existing native macOS Metal backend (`crates/rune-gpu/src/metal/`) with a thin Windows-only D3D12 backend using the `windows` crate + precompiled DXBC shaders (`render/dxbc.rs`, already shipped in Rune) + dedicated transient vs persistent `ID3D12Heap` pools (so freeing transient backdrops/layers actually returns entire D3D12 heaps to WDDM, lowering OS `GPU_Ded` too!). Drop `wgpu-core`, `wgpu-hal`, and `naga` from the Windows release feature set.
   - **Expected numbers:** **-2.48 MiB `.text`** (removes `naga` + `wgpu-core` + `wgpu-hal`), **-150 to -250 ms** GPU init cold start, **-40 to -70 MiB** Private + GPU Dedicated VRAM (separate transient D3D12 heap freed at rest), **`submit` p50 from 2.4 ms -> <0.4 ms**.

3. **Shared Lazy Row-Backed CPU Glyph Atlas with Eviction Shrink (Built in Phase 3) + DirectWrite-Only Shaping Option**
   - **Implementation:** (Shipped in Phase 3) Replace eager `4.00 MiB` (`2048x2048`) `Atlas::data` / `AtlasPage::data` allocations with lazy power-of-two row growth starting at `ATLAS_CPU_START_ROWS = 256` (`512 KiB`, matching GPU `ATLAS_START_ROWS`), shrinking back to `0 B` on `evict_page`; move `embedded-fonts` to `[dev-dependencies]` and strip unused `svg` (`resvg`/`usvg`/`tiny-skia`). (Optional follow-up) Release CPU glyph page mirror once uploaded to GPU on non-CPU-boot frames, re-rasterizing only on device loss.
   - **Expected numbers:** **-3.50 MiB CPU heap per window (-45% Demo Rust heap)** and **-2.08 MiB binary size** (verified below).

4. **Upload-and-Drop `ImageAtlas` + Single-Mip Cover Cache (`rune-image`)**
   - **Implementation:** After `Renderer::upload_images` copies dirty rows of an `ImageAtlas` page into its GPU texture, drop `page.data` (`Vec<u8>`) on CPU instead of retaining a `9.11 MiB` duplicate RGBA mirror forever (keep `Pixels` generation + slot metadata so device loss re-uploads from `ImageCache`). In `exo-ui`, pass target cover display size to `decode_capped` without generating a full multi-level CPU mip pyramid (`mip_chain`) when covers render at a single card size (`-33%` decoded bytes), and evict off-screen cover `Decoded` buffers on `on_rest` while keeping GPU atlas slots warm.
   - **Expected numbers:** **-9.1 MiB** CPU heap immediately from `ImageAtlas` mirror removal + **-10 to -18 MiB** Host CPU heap from single-mip/evictable covers (**-20 to -27 MiB** Host Private Bytes).

5. **Decoupled Heartbeat & Glass-Aware Damage Scheduler (`exo-ui/src/shell.rs` + `rune::window`)**
   - **Implementation:** Move `shell.heartbeat` (`1.0 s` keepalive RPC to .NET host) from `poll_state` (`tx.wake_at(last + 1.0)`) onto a background timer thread on `RpcClient` (using `Arc<AtomicBool>` for window visibility). That stops the 1 Hz UI-thread wakeups in Connected Host mode so Rune's 10-second `REST_AFTER` timer actually fires when the user is idle on Library, triggering full GPU scratch reclamation (**-30 to -45 MiB** D3D12 VRAM at Host idle) and bringing Connected Host idle CPU from **0.7–0.9% -> ~0.00%**.
   - **Expected numbers:** Connected Host idle CPU **-> ~0.00%**, Connected Host idle D3D12 VRAM **49.4 MiB -> ~16 MiB** (`-33 MiB` additional drop on top of Phase 3).

---

## Phase 3: Built in the Fork & Exo + Measured Before/After

### 3.1 What We Built

1. **Lazy Growable CPU Glyph Atlas with Eviction Shrink** (`UhhErix/rune` `feat/exo-own-perf` commit `f699e3c3`):
   - `crates/rune-text/src/atlas.rs`: Added `ATLAS_CPU_START_ROWS = 256` (`512 KiB`) and `ensure_rows()`; `Atlas::alloc` grows page backing lazily in row powers of two (`256 -> 512 -> 1024 -> 2048`), `AtlasPage::new` starts empty (`0 B`), and `evict_page` clears and shrinks backing storage to `0 B`. Added unit test `cpu_atlas_grows_lazily_and_reclaims_on_eviction`.
   - `crates/rune/src/render/cpu_boot/raster/glyph.rs` & `record.rs`: Updated CPU-boot glyph rasterizer bounds checks to use actual page row height (`mask.len() / ATLAS_SIZE`) and padded `RCB1` recording encode for backwards compatibility.
2. **Lazy Layer Backdrop Allocation + Visible-Rest GPU Scratch Reclamation** (`UhhErix/rune` `feat/exo-own-perf` commits `f699e3c3`, `38544886`):
   - `crates/rune/src/render/{upload,pipelines,layers,backdrop_blur,liquid_glass}.rs`: Added `Layers::has_active_target_containing` so the `16.87 MiB` `Rgba16Float` layer backdrop texture is allocated **only** when a backdrop-reading effect actually resides inside an active isolated layer, pruning and unbinding stale `layers::FORMAT` backdrops otherwise. Added unit test `layer_backdrop_is_allocated_only_when_an_effect_draws_inside_a_layer`.
   - `crates/rune/src/render/{layers,damaged}.rs` & `crates/rune/src/window/host/lifecycle.rs`: `Renderer::give_back(false, ...)` now releases all transient scratch GPU resources on visible rest (`backdrops`, `backdrop_blur` & `liquid_glass` frost pyramids, non-cache scratch `layers.targets`, and `damaged::Retained` `last frame` texture) while keeping `El::cache` targets resident; `damaged::damage_drawn` also drops `last frame` whenever `redraws_in_place().is_err()`. On visible rest (after 10 s idle), `lifecycle.rs` flushes and polls the already-idle device and trims upload staging chunks.
3. **Strip Unused Rune Default Features & Bump Fork Pin** (`apps/launcher/native-ui/Cargo.toml`, `crates/exo-ui/Cargo.toml`):
   - Bumped all 8 Rune git pin entries to `385448863a55785cce872ef8789cf1f473b02aca`.
   - Set `rune` to `default-features = false, features = ["accessibility"]` (dropping unused `svg` `resvg`/`usvg`/`tiny-skia`, `ogg-audio`, `dbus`, `metal-backend`, and moving `embedded-fonts` Inter `1.25 MiB` to `[dev-dependencies]` for unit tests only).
4. **Allocator Experiment (`mimalloc` — Tested & Rejected on Evidence):**
   - Temporarily installed `mimalloc` v0.1 as `#[global_allocator]` and ran the identical Release RTX 3070 suite: Standalone Private Bytes regressed **192.27 -> 225.31 MiB (+33.04 MiB)** and Connected Host Private Bytes regressed **347.46 -> 487.83 MiB (+140.37 MiB)** (Working Set **200.79 -> 268.86 MiB, +68.07 MiB**) due to per-thread 32 MiB segment arenas across 23–47 threads. Reverted `mimalloc`; kept Windows 11 system allocator.

### 3.2 Measured Before vs After Comparison

#### A. Binary Size & Idle Process Metrics (Clean Release `exo-ui.exe`)

| Metric | Before (`ba9aa615`) | After (`38544886`) | Delta |
|---|---:|---:|---:|
| **`exo-ui.exe` Release Size** | 35,265,024 B (`33.63 MiB`) | **33,086,464 B (`31.55 MiB`)** | **`-2.08 MiB` (`-6.2%`)** |
| PE `.text` / `.rdata` | `27.53 MiB` / `5.79 MiB` | **`26.73 MiB` / `4.52 MiB`** | **`-0.80 MiB` / `-1.27 MiB`** |
| Standalone Demo Private Bytes (T+25s) | `192.27 MiB` | **`189.88 MiB`** | **`-2.39 MiB`** |
| Standalone Demo Idle CPU (20s window) | `31.25–62.50 ms` (`0.155–0.311%` 1-core) | **`0.00 ms` (`0.000%` 1-core)** | **`-100%` (0 redraws)** |
| Standalone Demo Cold Start (visible window) | `324.2–530.5 ms` | **`340.6 ms`** | Within run-to-run noise |

#### B. Rust Heap & D3D12 Suballocator Breakdown (1920x1080, Symbolized `procwatch` + `wgpu/counters`)

| Metric (1920x1080) | Before (`ba9aa615`) | After (`38544886`) | Delta |
|---|---:|---:|---:|
| **Demo Active (T+4s) — Rust Heap Live** | `7.77 MiB` | **`4.24 MiB`** | **`-3.53 MiB` (`-45.4%`)** |
| **Demo Active (T+4s) — Glyph Atlas CPU Backing** | `4.00 MiB` | **`0.50 MiB`** | **`-3.50 MiB` (`-87.5%`)** |
| **Demo Active (T+4s) — D3D12 Allocated** | `36.94 MiB` (`15 tex / 31 buf`) | **`19.94 MiB` (`14 tex / 31 buf`)** | **`-17.00 MiB` (`-46.0%`)** |
| **Demo Active (T+4s) — `effect backdrop`** | `25.31 MiB` (2x) | **`8.44 MiB` (1x)** | **`-16.87 MiB` (`-66.7%`)** |
| **Demo Post-Rest (T+15s) — Rust Heap Live** | `7.73 MiB` | **`4.18 MiB`** | **`-3.55 MiB` (`-45.9%`)** |
| **Demo Post-Rest (T+15s) — D3D12 Allocated** | **`36.94 MiB`** (`15 tex / 31 buf`) | **`2.44 MiB` (`5 tex / 15 buf`)** | **`-34.50 MiB` (`-93.4%`)** |
| **Demo Post-Rest (T+15s) — D3D12 Texture Memory** | **`33.56 MiB`** | **`0.94 MiB`** | **`-32.62 MiB` (`-97.2%`)** |
| **Demo Post-Rest (T+15s) — D3D12 Buffer Memory** | `3.38 MiB` | **`1.50 MiB`** | **`-1.88 MiB` (`-55.6%`)** |
| **Demo Post-Rest (T+15s) — Views / BindGroups** | `88 / 29` | **`11 / 3`** | **`-87.5%` / `-89.7%`** |
| **Demo Post-Rest (T+15s) — `backdrops` / `isolated group` / `frost pyramid`** | `25.31` / `5.25` / `2.06 MiB` | **`0.00` / `0.00` / `0.00 MiB`** | **`-32.62 MiB` (`-100%`)** |
| **Demo Post-Rest (T+15s) — NT Heap Busy / Big(>512K)** | `14.46 MiB` / `4.88 MiB` | **`10.77 MiB` / `1.38 MiB`** | **`-3.69 MiB` / `-3.50 MiB`** |
| **Connected Host — Rust Heap Live** | `52.89 MiB` | **`39.72 MiB`** | **`-13.17 MiB` (`-24.9%`)** |
| **Connected Host — D3D12 Allocated** | **`92.44 MiB`** (`26 tex / 39 buf`) | **`49.44 MiB` (`13 tex / 20 buf`)** | **`-43.00 MiB` (`-46.5%`)** |
| **Connected Host — D3D12 `effect backdrop` / `last frame` / `layer-spare` / `staging`** | `25.31` / `8.44` / `5.44` / `3.38 MiB` | **`8.44` / `0.00` / `0.00` / `0.25 MiB`** | **`-33.88 MiB`** |
| **Connected Host — Views / BindGroups** | `265 / 86` | **`68 / 25`** | **`-74.3%` / `-70.9%`** |

*(Note: OS-reported `GPU_Ded` in Windows Perf Counters stayed at `52.85 MiB` in Demo rest even as D3D12 suballocated memory dropped `36.94 -> 2.44 MiB` because `gpu-allocator` packs persistent tiny resources like `glyph atlas` (0.5 MiB) and `zero init` (0.5 MiB) into the same 32–64 MiB `ID3D12Heap` blocks as transient textures. Separating transient scratch into its own D3D12 heap — Plan #2 — will turn that `-34.5 MiB` suballocator reclamation directly into OS-visible WDDM VRAM reclamation too.)*

#### C. Frame Times (1920x1080 on RTX 3070, `RUNE_FRAME_LOG`)

| Workload (1920x1080) | Before p50 / p99 | After p50 / p99 | Delta (p50 / p99) |
|---|---:|---:|---:|
| **Idle (2.0–9.5s)** | `0 frames` | **`0 frames`** | Unchanged (0 redraws) |
| **Library Scroll** | `4.170 ms` / `87.591 ms` | **`3.757 ms` / `36.731 ms`** | **`-9.9%` / `-58.1%`** (`submit` p50 `2.769 -> 2.396 ms`) |
| **Game Page Open + Flight** | `3.891 ms` / `14.656 ms` (max `134.5 ms`) | **`3.953 ms` / `10.076 ms`** (max `116.2 ms`) | **`+1.6%` / `-31.3%`** (max `-13.6%`) |
| **Glass/Blur (Palette + Settings)** | `4.206 ms` / `8.067 ms` | **`3.933 ms` / `6.737 ms`** | **`-6.5%` / `-16.5%`** (`submit` p50 `2.453 -> 2.271 ms`) |

#### D. 1920x1080 Visual Parity (`parity-compare.py` + SHA-256)

| Scene (1920x1080, scale 1) | Before SHA-256 (prefix) | After SHA-256 (prefix) | `mean abs diff` | Status |
|---|---|---|---|---|
| `library` | `3ED261B9B9C8BC34` | `3ED261B9B9C8BC34` | `0.0` | **Bit-for-bit identical** |
| `game` | `67D3F8FF1445C37F` | `67D3F8FF1445C37F` | `0.0` | **Bit-for-bit identical** |
| `settings` | `1181C5B88972F743` | `1181C5B88972F743` | `0.0` | **Bit-for-bit identical** |
| `palette` | `E999BE1B81EC5BE2` | `E999BE1B81EC5BE2` | `0.0` | **Bit-for-bit identical** |
| `store` | `D6016B5CDEB78890` | `D6016B5CDEB78890` | `0.0` | **Bit-for-bit identical** |
| `discord` | `3077C823568D9A95` | `3077C823568D9A95` | `0.0` | **Bit-for-bit identical** |

---

## Verification & Gates

- **Rune fork (`UhhErix/rune` `feat/exo-own-perf` @ `385448863a55785cce872ef8789cf1f473b02aca`):**
  - `cargo test -p rune-text --lib`: **141 passed, 0 failed** (including `lazy_cpu_tests::cpu_atlas_grows_lazily_and_reclaims_on_eviction` and `four_hundred_scales_never_exhaust_the_atlas_or_poison_glyphs`).
  - `cargo test -p rune --lib -- backdrop_tests backdrop_blur_tests liquid_glass cpu_boot::tests layers::plan_tests layers::cache_tests damage_tests`: **59 passed, 0 failed** (including `layer_backdrop_is_allocated_only_when_an_effect_draws_inside_a_layer`).
- **Exo Launcher (`feat/native-rune-own-perf`):**
  - `cargo clippy --workspace --locked`: **0 errors, 0 new warnings**.
  - `cargo test --workspace --locked`: **218/218 `exo-ui` + `exo-kit` + `exo-rpc` tests passed**.
  - `cargo build --release --locked --bin exo-ui`: **succeeded (`31.55 MiB`)**.
  - 6-scene 1920x1080 parity comparison (`library`, `game`, `settings`, `palette`, `store`, `discord`): **0.0 mean abs diff, exact SHA-256 match**.

---

## Wave 2: Full-Authority Fork & App Optimizations (`e36d0e1a`)

Under the expanded full-authority mandate (`UhhErix/rune` + `Exo-Launcher` + fork sync workflow), Wave 2 targeted the remaining top idle CPU, heap, VRAM, and glass/damage bottlenecks while rebasing cleanly onto `UhhErix/rune@origin/exo` (`24e16562`, `#5` `rune-ui`):

### Fork & App Changes Built

1. **Merge-Based Weekly Upstream Sync (`UhhErix/rune` `6ee424af` — `.github/workflows/exo-sync.yml`):**
   - Replaced the 6-hourly `git rebase --empty=drop upstream/dev` + `--force-with-lease` push to `origin/exo` with a weekly (`cron: '17 9 * * 1'`) + manual `workflow_dispatch` merge onto an ephemeral `sync/upstream-dev` branch that runs the Windows core crate test suite, regenerates DX12 uber bytecode on generated-file conflicts, preserves `exo-sync-pre-<exoShort>` and `exo-sync-<upstreamShort>` safety tags, and opens/updates a review PR into `exo`. `origin/exo` is never force-pushed or rebased away.
2. **GPU-Resident `ImageAtlas` CPU Backing Release at Rest (`UhhErix/rune` `b6f81de3` — `crates/rune-image/src/pixels.rs`, `crates/rune/src/render/upload.rs`):**
   - Added `ImagePage::gpu_resident` (set after `Renderer::upload_images` writes rows to the GPU texture) and `sealed`.
   - `ImageAtlas::rest()` frees `page.data = Vec::new()` on every clean `gpu_resident` page while keeping its GPU texture and slots intact, eliminating duplicate CPU RGBA backing mirrors (`17.11 MiB -> 0.00 MiB` in Connected Host mode).
   - Streaming/animated updates to a slot on a sealed page migrate cleanly onto a fresh unsealed page, and `ImageAtlas::invalidate()` drops slots whose CPU backing was released so device-loss / park recovery re-copies cleanly from `Pixels`.
   - Verified with unit tests `pixels::tests::rest_releases_cpu_backing_for_gpu_resident_pages_and_migrates_updates` and `render::image_tests::rest_frees_image_atlas_cpu_backing_while_gpu_texture_renders_identically`.
3. **Static Liquid Glass Frost Pyramid Cache & Glass-Aware Partial Damage (`UhhErix/rune` `e36d0e1a` — `crates/rune/src/render/{pass,upload,liquid_glass,liquid_glass/renderer,damaged}.rs`, `crates/rune/src/render.rs`):**
   - `Uploads::write_instances` / `write_patched` now track `first_changed` and non-layer `changed_bounds`. When a single-epoch Liquid Glass frame has unchanged backdrop dependencies and no overlapping instance changes before epoch 0, `draw_liquid_glass` reuses the resident `effect backdrop` and 6-mip `Rgba16Float` frost pyramid directly inside the main render pass (zero pass split, zero backdrop copy, zero 6-mip downsample passes).
   - `Renderer::arm_damage` / `arm_loop` / `draw_armed` allow partial-damage redraws when Liquid Glass is present and every armed damage rect is strictly disjoint from all Liquid Glass epoch sample regions while `liquid_glass_signature()` is unchanged, skipping glass draws outside the borrowed damage bound and avoiding `Retained` (`last frame`) allocation when rects overlap glass.
   - Verified with GPU pixel-exact unit tests `static_backdrop_reuses_liquid_glass_epoch0_pyramid_and_matches_fresh_render` and `glass_aware_partial_damage_redraws_disjoint_rect_and_declines_overlapping_rect`.
4. **Decouple `shell.heartbeat` from Rune UI Event Loop (`apps/launcher/native-ui/crates/exo-{rpc,ui}`):**
   - Root cause of Connected Host idle CPU (`0.70–0.94%`) and missing rest reclamation: `shell.rs::tick_shell_heartbeat` scheduled `tx.wake_at(last + 1.0)` AND invoked `call_host_async("shell.heartbeat", ...)` (which called unconditional `handle.update(...)` on reply), forcing 1 full 1920x1080 GPU redraw every second and continuously resetting Rune's `REST_AFTER = 10s` deadline.
   - Moved `shell.heartbeat` onto a dedicated background `exo-heartbeat` thread holding `Weak<RpcClient>` + `WINDOW_VISIBLE: AtomicBool` (updated in `.on_visibility`), sending `rpc.call_raw("shell.heartbeat", ...)` directly without waking Rune's UI loop.

### Wave 2 Measured Before / After (RTX 3070, 1920x1080, Apples-to-Apples Base)

#### E. Connected Host Idle & Post-Rest Reclamation (`ExoLauncher.exe` in Isolated Temp `EXO_LAUNCHER_DATA_DIR`)

| Metric (Connected Host, 1920x1080) | Baseline (`ba9aa615`) | Wave 1 (`38544886`, rest blocked) | Wave 2 (`e36d0e1a` + bg heartbeat) | Delta vs Baseline |
|---|---:|---:|---:|---:|
| **Idle Frames Drawn (`T+16s..T+26s`)** | `10 frames` (1 Hz forever) | `10 frames` (1 Hz forever) | **`0 frames`** | **`-100%`** |
| **Idle 1-Core CPU (`T+6s..T+26s`)** | `0.70–0.94%` | `0.70–0.94%` | **`0.156%`** (`0.031s / 20s`, incl. 1 Hz RPC thread) | **`-80%`** |
| **Rust Heap Live (Active -> Post-Rest)** | `52.89 MiB` (never dropped) | `39.72 MiB` (never dropped) | `49.35 MiB` -> **`24.61 MiB`** | **`-28.28 MiB` (`-53.5%`)** |
| **`ImageAtlas` CPU Backing (Active -> Post-Rest)** | `7.11+ MiB` (kept forever) | `7.11+ MiB` (kept forever) | `17.11 MiB` (6 pages) -> **`0.00 MiB`** | **`-100%`** (GPU textures kept) |
| **D3D12 Allocated (`section gpu`, Post-Rest)** | `92.44 MiB` (`26 tex / 39 buf`) | `49.44 MiB` (`13 tex / 20 buf`) | **`20.31 MiB` (`11 tex / 15 buf`)** | **`-72.13 MiB` (`-78.0%`)** |
| **D3D12 `layer-live` / `layer-spare` / `backdrops`** | `20.00` / `5.44` / `25.31 MiB` | `20.00` / `0.00` / `8.44 MiB` | **`0.00` / `0.00` / `0.00 MiB`** | **`-50.75 MiB` (`-100%`)** |
| **D3D12 Views / BindGroups (Post-Rest)** | `265 / 86` | `68 / 25` | **`29 / 9`** | **`-89.1%` / `-89.5%`** |
| **OS Working Set (Active T+6s -> Post-Rest T+26s)** | `~230 MiB` (no drop) | `~230 MiB` (no drop) | `230.08 MiB` -> **`197.29 MiB`** | **`-32.79 MiB` reclaimed at rest** |
| **OS Private Bytes (Active T+6s -> Post-Rest T+26s)** | `~372 MiB` (no drop) | `~372 MiB` (no drop) | `371.64 MiB` -> **`337.89 MiB`** | **`-33.75 MiB` reclaimed at rest** |

#### F. Back-to-Back Demo Frame Times & 1920x1080 Visual Parity

- **Back-to-back idle-GPU RTX 3070 run (`scenario-suite.json`, `exo-ui-baseline-prof.exe` vs `exo-ui-wave2-prof.exe`):**
  - `idle [1.5..9.5s]`: `0 frames` vs **`0 frames`**.
  - `game_page [13.9..17.8s]`: `p50 2.666 ms / p99 6.293 ms` vs **`p50 2.795 ms / p99 5.965 ms`** (within run-to-run GPU boost jitter; standalone run before B2B measured `p50 2.632 ms / p99 4.544 ms`).
  - `glass_palette_settings [17.9..22.0s]`: `p50 2.556 ms / p99 5.827 ms` vs **`p50 2.588 ms / p99 4.794 ms` (`-17.7%` p99)** (`submit p50 1.373 -> 1.421 ms`; multi-epoch Palette modal + Settings scroll animate glass directly, whereas single-epoch static-backdrop and disjoint-rect partial damage wins are verified directly in `liquid_glass::tests`).
- **6-Scene 1920x1080 Visual Parity (`exo-ui-baseline-prof.exe` vs `exo-ui-wave2-prof.exe`, `parity-compare.py` + SHA-256):**
  - `library` (`8479EC2698C1AB5F`), `game` (`035B3E7478C1CF44`), `settings` (`7E31C61303E19BFA`), `palette` (`5F427D26E0C038D8`), `store` (`41185FE816C92252`), `discord` (`5134E592B3E9A78E`): **6/6 exact SHA-256 match, `mean abs diff = 0.0`**.

#### G. Wave 2 Verification & Gates

- **Rune fork (`UhhErix/rune` `feat/exo-own-perf` @ `e36d0e1a0b8f03e7ce24b69aff5e74b41af926f7`, rebased on `origin/exo` `24e16562`):**
  - `.github/workflows/exo-sync.yml`: PowerShell AST check passed (`0 errors, 1086 tokens`).
  - `cargo test -p rune-image --lib`: **62 passed, 0 failed** (2 ignored).
  - `cargo test -p rune --lib -- liquid_glass::tests damage_tests backdrop_tests backdrop_blur_tests image_tests layers::plan_tests layers::cache_tests cpu_boot::tests`: **60 passed, 0 failed**.
- **Exo Launcher (`feat/native-rune-own-perf`):**
  - `cargo clippy --workspace --locked -- -D warnings`: **0 errors, 0 warnings**.
  - `cargo test --workspace --locked`: **254 passed, 0 failed** (`9 exo-rpc` + `27 exo-kit` + `218 exo-ui`, 1 ignored).
  - Rebased onto latest `origin/feat/native-polish-2` (`aba685c2`) with all 10 Rune git rev entries pinned to merged `UhhErix/rune@exo` tip `caaa6a71c732fe7f3bc623d60283961f15ac1cdf` (`UhhErix/rune#6`, containing `24e16562`) and full workspace verification re-run.
