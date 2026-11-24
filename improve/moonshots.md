# Exo Launcher Moonshot Backlog — Features, Tech & Safe Game Mods (2026-11-24)

> **Mandate:** *"Improve any feature to impossible lengths. Try anything. Experiment, cook ur own new stuff. Make new technology and software."* — widened to **every nook and cranny**.
>
> **Scope Division Across Scouts:**
> - **This backlog (`moonshot-scout`):** **Features, core technology, and safe game mods** across all Exo rooms, host services, `ExoCapture`, `ExoShaders`, `ExoMouse`/peripherals, `voice/`, `cloud/`, `relay/`, `shield/`, and `Exo-Cinema`.
> - **Sibling scouts (handed off cleanly in the Coverage Checklist below):** `scout-ux` (UI/UX/micro-interactions), `scout-dataflow` (internal IPC/network/telemetry/updates), `scout-code` (code/build/CI/release), and `deps` workers (dependencies).
>
> **Architectural Lens:** Judge everything as if Exo is the native Rust/Rune UI (`apps/launcher/native-ui`, wgpu DX12) talking over `NamedPipeHostTransport` (`host-v1.json`) to the `.NET 10` host (`apps/launcher/ExoLauncher`).
>
> **Hard Safety Lines (Non-Negotiable for Every Lab & Proposal):**
> 1. **Hands off the owner's real hardware & installed Exo:** NEVER write to the owner's real mouse, keyboard, controller, or any HID/hardware device (read-only inspection is OK; test writes only against emulated/mock transports). NEVER start or touch the owner's installed Exo (`%LOCALAPPDATA%\ExoLauncher\app`); always run an isolated build with dev mocks.
> 2. **Zero anti-cheat interference:** Never touch, kill, hook, or inject into anti-cheat games (Vanguard, EAC, BattlEye, FACEIT, ACE — `Services/AntiCheatCheck.cs` is law). Game mods apply strictly to `AntiCheatCheck.None` (or Steam offline non-AC launch entries via `Services/DlssSwapService.SessionMods.cs`, removed on exit).
> 3. **Zero DRM circumvention & zero copyright violation:** Never bypass store ownership/DRM, never redistribute copyrighted game shader bytecode or BIOS binaries, and preserve strict license boundaries (`mouse/` AGPL-3.0, `shaders/` GPL-3.0, `capture/third_party/obs/` GPL-2.0 stay isolated from MIT `ExoLauncher`).

---

## Explicit Exclusions (Already Covered by Running Labs & Workers)

1. **Frame generation everywhere** (`framegen-everywhere`, `perf-framegen-line`).
2. **Graphics unlocks** (`unlock-everything` — Reflex, RT/PT, upscalers, hidden settings).
3. **React → Rune renderer** (`lab-react-renderer`).
4. **Web, auth, and DRM in Rune** (`lab-web-in-rune`, `lab-auth-native`, `lab-drm-native`).
5. **Microphone noise suppression** (`lab-voice-ns`).
6. **React → Rune audit** (`react-to-rune-audit`).
7. **WP8 native parity** (`wp-WP8` and active native parity WPs 1–11).

**Boundary with `perf-program.md` (Waves 1–2):** Tactical fixes (`perf-ai-bugs`, `perf-hw-phase1`, `perf-os-tweaks`, `perf-cap-lab`, `perf-gpu-tuning`, `perf-voice-loudness`, `perf-ci`, `perf-game-profiles`, `perf-mouse-diag`, `perf-display`) are not repeated; moonshots build *beyond* them.

---

## 1. Complete Nook-and-Cranny Feature Coverage Checklist

Every Exo feature across host services, helpers, rooms, add-ons, cloud Workers, and Exo-Cinema — **all 64 surfaces explicitly audited with a verdict**:

| # | Subsystem | Feature / Surface | Primary Codebase Paths | Status | Verdict |
|---:|---|---|---|---|---|
| 1 | Launch | Unified game launch orchestration & queue | `Services/LaunchOrchestrator*.cs` | Reviewed | **Moonshot (#1, #4)** — Pre-launch asset prewarm + speculative backend warmup |
| 2 | Launch | Steam IPC install/update/uninstall/properties | `ExoLauncher.SteamIpc/`, `Services/SteamLibraryExtras.cs` | Reviewed | **Moonshot (#4)** — Speculative IPC warm; internal IPC handed to `scout-dataflow` |
| 3 | Launch | Steam sign-in, account switching, offline/overlay/beta | `Services/SteamSignIn/`, `WebHostBridge.Steam*.cs` | Reviewed | **Excluded / Handoff** — Auth in `lab-auth-native`; UX in `scout-ux` |
| 4 | Launch | Epic (`legendary`), GOG (`gogdl`), Amazon (`nile`) agents | `Services/EpicAuthService.cs`, `Services/Gog*.cs` | Reviewed | **Moonshot (#4, #18)** — Speculative token warm + LAN MeshCache |
| 5 | Launch | Riot client hiding, account switch, `vgtray` toggle | `Services/RiotLedger.cs`, `Services/RiotModes.cs`, `Services/TrayIconRemover.cs` | Reviewed | **At Frontier / Hard Stop** — Never touch Riot games or `vgk`/`vgc` |
| 6 | Launch | Xbox, EA, Ubisoft, Battle.net, Rockstar, itch, MC, Roblox, Paradox, Wargaming, custom `.exe` | `Services/OfficialClientCatalog.cs`, `Services/InstalledGameCatalog.cs` | Reviewed | **Moonshot (#24, #30)** — Universal save time machine + Xbox/UE config support |
| 7 | Launch | Store Quiet & hidden store runtime suspension | `Adapters/StoreQuiet.cs`, `Adapters/HiddenStoreRuntime.cs` | Reviewed | **Moonshot (#4, #19)** — Speculative wake + Quiet Curtain expansion |
| 8 | Library | Unified grid, Continue shelf, folders, cover art | `Services/LibraryService.cs`, `Services/FolderArtStore.cs`, `Services/CoverArtService*.cs` | Reviewed | **Moonshot (#10)** + **Handoff** — mmap instant paint (#10); grid UX to `scout-ux` |
| 9 | Library | Achievements (Steam/Epic/GOG) & watch | `Services/Achievements/`, `Services/AchievementService.cs` | Reviewed | **At Frontier / Handoff** — Data sync to `scout-dataflow`; toast UX to `scout-ux` |
| 10 | Library | Playtime tracking & Riot tracker.gg aggregate | `Services/PlaytimeService.cs`, `Services/ReportedPlaytime.cs`, `cloud/riot/` | Reviewed | **Moonshot (#5, #32)** — Feeds hot/cold compression tiering & cross-PC handoff |
| 11 | Library | Game activation keys & notes | `Services/GameKeyActivation.cs`, `Services/Notes.cs` | Reviewed | **At Frontier / Handoff** — UI polish handed to `scout-ux` |
| 12 | Library | Save-game management & cross-PC state | *(missing — only Exo prefs sync today)* | Reviewed | **Moonshot (#24, #32)** — Copy-on-Write Save Time Machine + Desktop↔Handheld split |
| 13 | Store | Steam-ranked shelves, search, trailers, calendar, wishlist, gifts | `Services/StoreService*.cs`, `Services/StoreGifts.cs` | Reviewed | **Handoff (`scout-ux` / `scout-dataflow`)** — Store browsing UX & caching |
| 14 | Store | Hardware "Will It Run?" fit check | `Services/StoreService.Fit.cs`, `Services/StoreService.Specs.cs` | Reviewed | **Moonshot (#28)** — Calibrated RealFPS + VRAM-fit predictor |
| 15 | Store | Multi-PC downloads & LAN transfers | `Services/DownloadPolicy.cs`, `Services/PendingDownloadStore.cs` | Reviewed | **Moonshot (#18)** — Zero-config LAN P2P MeshCache across stores/drivers/shaders |
| 16 | Upscalers | Signed DLSS/FSR/XeSS DLL swapper | `Services/DlssSwapService*.cs` | Reviewed | **Excluded — Active Lab** (`unlock-everything`) |
| 17 | Frame Gen | OptiScaler, SM86, MFG, Smooth Motion, AFMF, Lossless Scaling | `Services/DlssSwapService.FrameGen*.cs`, `Helpers/FluidMotionFrames.cs` | Reviewed | **Excluded — Active Lab** (`framegen-everywhere`, `perf-framegen-line`) |
| 18 | Game Mods | Non-AC single-player engine/HDR/ultrawide/FPS-cap mods | `Services/DlssSwapService.SessionMods.cs`, `Services/AntiCheatCheck.cs` | Reviewed | **Moonshot (#12, #25)** — RenoDX true native HDR + Safe Single-Player Mod Forge |
| 19 | Shaders | Exo Shaders (SCSKiller fork) offline compiler | `shaders/`, `Services/ShadersAddon.cs`, `Services/ScsKillerStatus.cs` | Reviewed | **Moonshot (#1, #35, #38)** — Prewarm + Zone Shader Oracle + Zero-Bytecode PSO Cloud |
| 20 | Shaders | Idle post-driver rebuild, UE Prepare, `Engine.ini`, wipe watch | `Services/ShaderRebuild.cs`, `Services/ShaderPrepare.cs`, `Services/UnrealShaderIni.cs`, `Services/ShaderCacheWatch.cs` | Reviewed | **Moonshot (#1, #17)** — ASD integration + Driver Time-Machine cache preservation |
| 21 | Autopilot | Foreground game priority, EcoQoS demotion, timer res, suspend | `Services/Autopilot*.cs`, `Services/AutopilotSafety.cs` | Reviewed | **Moonshot (#6)** — Closed-loop Bayesian per-game auto-tuner + 92% GPU-busy pacer |
| 22 | Autopilot | PowerPlan & CPU-topology profiles (X3D/Zen 5/Intel Hybrid) | `Services/PowerPlan.cs`, `Services/PowerProfiles.cs`, `Services/Setups.cs` | Reviewed | **Moonshot (#6, #36)** — Dynamic EPP sweep + Silicon Binning Hardware Twin |
| 23 | Autopilot | SystemTuning Playbook, Win11 ViVe features, Game Bar/Mode | `Services/SystemTuning*.cs`, `Services/WindowsFeatures.cs`, `Services/WindowsGame*.cs` | Reviewed | **Moonshot (#6, #19)** — Beyond `perf-os-tweaks`: closed-loop verification + Quiet Curtain |
| 24 | Autopilot | PCIe MSI/MSI-X & interrupt priority/spread | `Services/InterruptMode*.cs`, `Services/MouseCore.Layout.cs` | Reviewed | **Moonshot (#36)** — Silicon/IRQ floor fingerprinting (`perf-mouse-diag` covers USB/ISR) |
| 25 | Auto-Tune | Ryzen CO (PawnIO) & NVIDIA/AMD GPU OC/UV | `Services/AutoTune*.cs`, `Services/TuneRyzen.cs`, `Services/TuneNvidia.cs`, `Helpers/AmdAdlx.Tuning.cs` | Reviewed | **Moonshot (#15, #36)** — Beyond `perf-gpu-tuning`: FPS/W Pareto knee + Silicon Twin |
| 26 | Game Config | Per-game hardware-scaled configs & Keep drift guard | `Services/GameSettings*.cs`, `Services/Keep*.cs` | Reviewed | **Moonshot (#30, #32)** — Engine-family auto-config (UE/Unity/Source 2) + cross-PC split |
| 27 | Exo AI | Idle drift coordinator & NVML fan curves | `Services/ExoAiCoordinator.cs`, `Services/ExoAiFans.cs` | Reviewed | **Moonshot (#7, #15)** — ETW Stutter Forensic Doctor + Mic-aware cross-vendor thermal |
| 28 | Telemetry | Zero-injection ETW `PresentTrace` & Liquid Glass FPS Overlay | `Helpers/PresentTrace.cs`, `FpsOverlay.cs`, `Services/FpsCounter.cs` | Reviewed | **Moonshot (#6, #11, #27)** — Click-to-Photon Lab + Exo Bench Blackbox |
| 29 | Telemetry | Live stutter attribution & kernel DPC/ISR `LatencyTrace` | `Services/StutterAnalysis.cs`, `Services/AutopilotWatch.cs`, `Helpers/LatencyTrace.cs` | Reviewed | **Moonshot (#7)** — 500 ms circular ETW flight recorder + causal graph |
| 30 | Capture | DXGI Duplication, OBS hook fallback, MFT encode, ReplayBuffer | `capture/ExoCapture/{Capture,Encode,Gpu}/` | Reviewed | **Moonshot (#8)** — Zero-copy direct NVENC/AMF/QSV (<0.8% FPS) + RAM-only replay |
| 31 | Capture | Clip editor, Snip overlay, CaptureShrink, Reel/Share | `capture/ExoCapture/{Edit,Media,Ui}/`, `Services/Capture*.cs` | Reviewed | **Moonshot (#14)** — Zero-FPS-cost multimodal auto-highlights + VMAF HDR AV1 shrink |
| 32 | Discord | Native chat, servers, DMs, threads, forums, polls, moderation | `Services/DiscordClient/Discord*.cs` | Reviewed | **Handoff (`scout-ux` / `scout-dataflow`)** — Chat UX & gateway/cache optimization |
| 33 | Discord | Voice mic noise suppression & TitaNet "Only My Voice" | `voice/`, `Services/DiscordClient/Voice/{NoiseSuppressor,MyVoice,EchoCanceller}.cs` | Reviewed | **Excluded — Active Lab** (`lab-voice-ns`) |
| 34 | Discord | Voice loudness leveling & mic sense/tune | `Services/DiscordClient/Voice/{InboundCleaner,MicSense,VoiceTune}.cs` | Reviewed | **Moonshot (#15, #22)** — Beyond `perf-voice-loudness`: MicSense→fan coupling + spatial voice |
| 35 | Discord | Voice transport, Opus decode, DAVE E2EE, Soundboard | `Services/DiscordClient/Voice/{VoiceTransport,LiveAudio,DaveSession,Soundboard}.cs` | Reviewed | **Moonshot (#22)** — Cocktail-party spatial seating + Opus 1.5 neural PLC/NoLACE |
| 36 | Discord | Go Live screen/game video streaming & CallRelay/FriendLinks | `Services/DiscordClient/{GoLiveCapture,FriendLinks,CallRelayClient}.cs`, `relay/` | Reviewed | **Moonshot (#20, #34)** — Direct-NVENC Go Live + Exo-to-Exo P2P + Encrypted LAN-Play Mesh |
| 37 | Music | Hidden web players (Spotify, Apple Music, YTM), lyrics, Jam | `Services/MusicEngineService*.cs`, `Helpers/{Spotify,AppleMusic,YouTubeMusic}Native.cs` | Reviewed | **Moonshot (#26)** — <75 MB audio-only host + R128 + Game DJ (`lab-drm-native` owns Rune DRM) |
| 38 | Devices | Exo Mouse (AGPL OpenMouse + Finalmouse TMR + MouseCore + UsbPorts) | `mouse/`, `Services/MouseCore*.cs`, `Services/UsbPorts*.cs` | Reviewed | **Moonshot (#23)** — Polling-vs-FPS auto-guard (strictly read-only/mock on owner HW) |
| 39 | Devices | Keyboards (`WootingKeyboard` HID backend, UI unwired) | `Services/WootingKeyboard*.cs` | Reviewed | **Moonshot (#23)** — Universal Hall-Effect/QMK/VIA/VIAL per-game Rapid Trigger engine |
| 40 | Devices | Controllers (`ControllerLab` polling/calibration/profiles) | `Services/ControllerLab*.cs` | Reviewed | **Moonshot (#23)** — DualSense adaptive hair-trigger & audio-to-haptics bridge (emulated lab first) |
| 41 | Devices | Audio (`ExoEq`, `EqualizerApo`, `AutoEqCatalog`, `AppVolume`) | `Services/ExoEq*.cs`, `Services/EqualizerApo.cs`, `Services/AppVolume.cs` | Reviewed | **Moonshot (#13, #16, #33)** — Personalized SOFA HRTF + WASAPI small-buffer Core + Acoustic Sixth Sense |
| 42 | PC Health | XMP/ReBAR/PCIe/iGPU/HAGS checks, Chipset & ASUS BIOS checks | `Services/PcHealth*.cs`, `Services/ChipsetUpdate.cs`, `Services/BiosUpdate.cs` | Reviewed | **Moonshot (#21, #36)** — All-vendor BIOS/AGESA/NVMe Radar + Silicon Binning Twin |
| 43 | Drivers | Clean GPU driver updates, advisories, profile keep/restore | `Services/GpuDriverUpdate.cs`, `Services/Driver*.cs` | Reviewed | **Moonshot (#17)** — Atomic Driver Time-Machine (<30 s rollback w/ warm shaders) |
| 44 | Drivers | NVAPI / ADLX / IGCL global & per-game GPU profiles | `Helpers/GpuProfile*.cs`, `Helpers/{NvidiaProfiles,AmdAdlx,IntelIgcl}*.cs` | Reviewed | **Moonshot (#6)** — Closed-loop live tuning of LL/Anti-Lag/IGCL + 92% GPU-busy pacer |
| 45 | Display | Refresh rate, color format/depth, scaling, RTX Video, DDC | `WebHostBridge.Display.cs`, `Helpers/{NvidiaDisplay,NvidiaScaling,NvidiaVideo,MonitorDdc}.cs` | Reviewed | **Moonshot (#2, #31)** — Hardware MHC2 LUT + OLED DDC guard + Cinema VSR |
| 46 | HDR | Per-game Auto HDR & SDR-in-HDR gamma 2.2 | `Services/GameHdr*.cs`, `Services/HdrHostService.cs`, `Helpers/SdrInHdr.cs` | Reviewed | **Moonshot (#2, #12)** — Beyond `perf-display`: MHC2 sRGB-in-HDR + RenoDX native HDR mods |
| 47 | Network | NIC power/RSS tuning, TCP restore, DoH DNS, Wi-Fi Game Mode | `Services/NetworkBest*.cs`, `Services/NetworkTune*.cs`, `Services/WifiGameMode.cs`, `Services/CloudflareDns.cs` | Reviewed | **Moonshot (#9, #34)** — Ping/Jitter X-Ray + Bufferbloat Shaper + Multipath UDP + LAN Mesh |
| 48 | Memory | StandbyPurger, Memory Compression, pagefile, cache | `Services/StandbyPurger.cs`, `Services/SystemTuning.Playbook.cs` | Reviewed | **Moonshot (#3)** — Predictive standby + Background VRAM Pre-Eviction + Large Pages |
| 49 | Storage | NVMe SMART, `DriveHeat`, DirectStorage BypassIO diagnostics | `Services/Drives*.cs`, `Services/DriveHeat.cs` | Reviewed | **Moonshot (#5, #15, #21)** — BypassIO Auto-Healer + NVMe pre-cooling + Firmware Radar |
| 50 | Storage | Transparent WOF game compression (`GameCompression`) | `Services/GameCompression*.cs`, `Services/GameCompressor.cs` | Reviewed | **Moonshot (#5)** — Play-recency Hot/Warm/Cold auto-tiering & drive smart swap |
| 51 | Hygiene | JunkFiles cleaner, StartupGuard, Bloat/Edge/ReplacedApps removal | `Services/Junk*.cs`, `Services/Startup*.cs`, `Services/Apps*.cs`, `Services/EdgeRemoval.cs` | Reviewed | **Moonshot (#19)** — Game-session Quiet Curtain pausing cloud sync/updaters |
| 52 | Runtimes | Game dependencies & `.NET`/VC++/DX runtime installer | `Services/DependencyService*.cs` | Reviewed | **At Frontier / Keep Lean** — Already deterministic & safe; deps owned by `deps` workers |
| 53 | Shield | Exo Shield WebView extension (`shield/`) | `shield/rules/*` | Reviewed | **Moonshot (#19, #26)** — Tracker stripping + music WebView DOM timer suspension |
| 54 | Account | Exo Account auth, device list, settings/playtime sync, backup | `Services/Account/`, `Services/LauncherBackup.cs`, `cloud/account/` | Reviewed | **Moonshot (#24, #32)** — E2EE Save Time Machine + Cross-PC Session Handoff |
| 55 | Cloud | `cloud/covers` art proxy | `cloud/covers/` | Reviewed | **Handoff (`scout-dataflow`)** — Edge AVIF/WebP LOD pipeline noted in #29 |
| 56 | Cloud | `cloud/reports` anonymous telemetry, crashes & GitHub triage | `cloud/reports/` | Reviewed | **Moonshot (#17, #28, #36)** — Powers Driver Radar, RealFPS & Silicon Twin (core pipeline to `scout-dataflow`) |
| 57 | Cloud | `cloud/updates` R2 release/addon-source mirror & delta updater | `cloud/updates/`, `Services/AppUpdateService*.cs` | Reviewed | **Moonshot (#21)** + **Handoff (`scout-dataflow`)** — Hosts signed Firmware Radar; delta transport to `scout-dataflow` |
| 58 | Shell | Rust/Rune native UI (`exo-ui`, `exo-kit`, `exo-rpc`) | `native-ui/crates/*` | Reviewed | **Moonshot (#10)** — Dormant Game Mode (<6 MB / 0 VRAM) + <180 ms cold start; parity in WP1–11 |
| 59 | Shell | `.NET 10` host footprint & `BackgroundTrim` | `ExoLauncher/Program.cs`, `Helpers/BackgroundTrim.cs`, `Services/RuneHostSession.cs` | Reviewed | **Moonshot (#10)** — Headless non-WinUI NativeAOT/trimmed host (<25 MB total in-game) |
| 60 | Shell | Command Palette (`Ctrl+K`) & in-game overlay ergonomics | `native-ui/crates/exo-ui/src/palette.rs`, `ui/src/blocks/command-menu-1.tsx` | Reviewed | **Handoff (`scout-ux`)** — Zero-injection Rune overlay capsule noted for joint UX/tech |
| 61 | Shell | Onboarding, Tour, Settings, Privacy, What's New | `native-ui/.../{tour,settings}.rs`, `ui/src/components/{onboarding,settings}/` | Reviewed | **Handoff (`scout-ux`)** |
| 62 | Build/CI | E2E harness, gates, Release pipeline, comment ratchet | `tools/`, `.github/workflows/`, `ExoLauncher.Tests/` | Reviewed | **Handoff (`scout-code`)** |
| 63 | Companion | **Exo-Cinema** media player (Jellyfin/Emby/Stremio, shared art/updates) | `Exo-Cinema` repo, `docs/architecture/cinema-art-cache.md` | Reviewed | **Moonshot (#31)** — Native `libplacebo` HDR + RTX VSR/Anime4K + 23.976 Hz VRR/QMS |
| 64 | Security | `NetworkDestinations`, `BannedSymbols`, `PlayerData`, `SecretFileAcl`, `ExoDpapi` | `ExoLauncher/BannedSymbols.txt`, `Services/NetworkDestinations.cs`, `Services/PlayerData.cs` | Reviewed | **At Frontier / Keep Lean** — Preserved as strict invariants across all moonshots |

---

## 2. Zero-to-One Inventions Nobody Has Built Yet (New Tech & Safe Game Mods)

Every item below is **genuinely new technology or a first-of-its-kind launcher capability** — not a copy of an existing commercial tool:

1. **Automated RenoDX True Native HDR & Safe Single-Player Mod Forge (`#12 lab-true-hdr-renodx`, `#25 lab-sp-mod-forge`):** No game launcher automates **RenoDX** (open-source ReShade add-on that replaces broken SDR/washed-out HDR tone-mappers inside game shaders with true 16-bit native HDR at ~0% GPU cost) or safe single-player engine fix mods (ultrawide aspect unlockers, 60 fps cap unlockers, skip-intro, Special K pacing) using Exo's `AntiCheatCheck.None` + `SessionMods.cs` + 30-second crash rollback!
2. **Closed-Loop Bayesian Auto-Tuner + 92%-GPU-Busy Adaptive Frame-Budget Pacer (`#6 lab-closed-loop-tuner`):** Nobody has built a closed-loop ETW optimizer that both (a) statistically A/B-tests CPU sets, timer resolution, EPP, and driver latency knobs across real gameplay (`p < 0.01`), *and* (b) dynamically slides the driver FPS cap to hold the GPU right at **90–95% `MsGPUActive`**, delivering Reflex-class zero render-queue latency even in games that never shipped Reflex or Anti-Lag 2!
3. **Zero-LDAT 10-Second Acoustic + Phone Optical Click-to-Photon Calibrator (`#11 lab-click-to-photon`):** Software tools like PresentMon 2.x stop at the PC boundary (missing mouse switch/wireless + monitor pixel GtG), while NVIDIA LDAT costs $1,000. Exo correlates a 48 kHz microphone click transient (20.8 µs precision) against `WM_INPUT` QPC + an optional phone 240 fps flash test to measure true **physical** click-to-photon latency with hardware you already own!
4. **Mic-Aware GPU Thermal & Acoustic Coupling (`#15 lab-thermal-acoustic`):** Because Exo owns both the GPU power/fan controller *and* the Discord microphone (`MicSense.cs`), Exo is the first software on Earth that senses when GPU fan acoustics approach your mic gate floor during a call and smoothly trades 1–2% FPS for 25–35% fewer watts and -8 dBA fan noise!
5. **Passive ETW `FileIo` Extent-Map Prewarm & Zone Shader Oracle (`#1 lab-asset-prewarm`, `#35 lab-shader-prefetch-oracle`):** Records exact cold-boot & map-load byte ranges passively via ETW and pre-warms them via `PrefetchVirtualMemory` + driver shader cache reads on Library hover before you even click Play!
6. **Zero-Config Encrypted LAN-Play Mesh Inside Discord Voice (`#34 lab-exo-lan-mesh`):** One-click WireGuard/QUIC P2P virtual LAN directly inside an Exo Discord call — kills Hamachi/Radmin/ZeroTier forever for modded co-op & classic LAN games!
7. **Passive Acoustic "Sixth Sense" Dynamic Clarity & Hearing Guard (`#33 lab-acoustic-coach`):** Pure WASAPI multi-band dynamic loudness equalizer inside `ExoEq` APO that caps ear-damaging 105 dB explosions to safe studio ceilings while keeping quiet 1.5–4 kHz footsteps/reloads crisp — zero game hooks, 100% legal & anti-cheat safe.
8. **Zero-Bytecode Crowd-Compiled PSO Metadata Cloud (`#38 lab-shader-cloud`):** Shares tiny (<2 MB) numeric DX12/Vulkan PSO state-descriptor recipes (zero copyrighted game shader bytecode!) so `ExoShaders` pre-compiles 95%+ of runtime-dynamic pipelines offline on day 1 before first launch across every store!
9. **Passive Silicon Binning Hardware Twin (`#36 lab-hardware-twin`):** Fingerprints your exact CPU/GPU/RAM/SSD/IRQ efficiency percentile against anonymous k-anonymized telemetry so Exo predicts *before testing* whether undervolting, cooling, or memory tuning will move the needle on your exact rig!

---

## 3. Master Ranked Backlog (Ranked by Player-Felt Impact ÷ Effort)

**Scoring Rubric:**
- **Player-Felt Impact (`1.0–10.0`)**: Magnitude × frequency of real, noticeable gain in FPS 1% lows, latency, load time, stutter elimination, visual HDR fidelity, audio clarity, noise, or system footprint.
- **Effort Weight**: `S = 1.0` (~1–3 days), `M = 1.5` (~1–2 weeks), `L = 2.5` (~3–5 weeks), `XL = 4.0` (~6–10 weeks).
- **Score** = `Impact ÷ Effort Weight` (strictly descending). Top 15 have ready-to-dispatch specs in `improve/moonshot-specs/`.

| Rank | Lab Spec Slug | Moonshot | Domain | Measurable Target | Impact | Effort | Score | Rel |
|---:|---|---|---|---|---:|:---:|---:|:---:|
| **1** | `lab-asset-prewarm` | Predictive Pre-Launch Asset & Shader Cache Prewarm | Launch / Shaders / Storage | **-30–55%** cold boot/load time; **-60%** first-5m streaming stutters | 9.0 | S | **9.00** | 3.x |
| **2** | `lab-oled-vrr-mhc2` | OLED Anti-Flicker VRR Governor & Hardware MHC2 Color LUT | Display / HDR | **-70%** LFC flicker crossings; **ΔE < 1.5** sRGB clamp in HDR at **0.0% GPU** | 8.4 | S | **8.40** | 3.x |
| **3** | `lab-memory-shield` | Predictive Standby & VRAM-Pressure Shield + Large Pages | Memory / Standby | **-90%** RAM/VRAM eviction stutters on 16GB/8GB rigs; **+3–8%** JVM/emu FPS | 7.8 | S | **7.80** | 3.x |
| **4** | `lab-instant-launch` | Speculative Zero-Wait Game Launch & Store Backend Warmup | Launch / Backends | **-40–65%** click-to-first-Present time; **0 MB** idle overhead | 7.4 | S | **7.40** | 3.x |
| **5** | `lab-storage-hyperdrive` | DirectStorage BypassIO Auto-Healer & Play-Recency Compression | Storage / NVMe | **+30–45%** free SSD space; **0%** hot-game load regression; **100%** BypassIO fix | 6.8 | S | **6.80** | 3.x |
| **6** | `lab-closed-loop-tuner` | Closed-Loop Bayesian Auto-Tuner & 92%-GPU-Busy Adaptive Pacer | Autopilot / Tuning | **+5–18%** 1% low FPS; **-15–40%** stutters (`p < 0.01`); **-20–35%** render-queue latency | 10.0 | M | **6.67** | 3.x |
| **7** | `lab-etw-stutter-ai` | On-Device ETW Micro-Window Stutter Forensic Engine ("Exo Doctor") | Exo AI / Diagnostics | **>90%** root-cause attribution accuracy on synthetic stutters; **<0.5%** CPU | 9.6 | M | **6.40** | 3.x |
| **8** | `lab-zerocopy-capture` | Zero-Copy Direct NVENC / AMF / QSV Capture & RAM-Only Replay | Exo Capture | **<0.8%** avg FPS / **<1.2%** 1% low cost at 1440p60 HDR AV1; **0 MB/s** SSD wear | 9.5 | M | **6.33** | 3.x |
| **9** | `lab-net-path-xray` | Multiplayer Ping/Jitter X-Ray, Live Bufferbloat Shaper & Multipath UDP | Network (Game) | **-80%** local bufferbloat jitter; **-50–90%** UDP loss on dual-path | 9.3 | M | **6.20** | v4 |
| **10** | `lab-micro-footprint` | Exo Micro-Footprint: NativeAOT Headless Host + Dormant Rune | Shell / Core Tech | **<45 MB** idle RAM; **<25 MB / 0 MB VRAM** in-game; **<180 ms** cold start | 9.2 | M | **6.13** | 3.x |
| **11** | `lab-click-to-photon` | Hardware-Free Click-to-Photon Latency Lab (+ 10s Phone/Audio Calib) | Telemetry / Input | **±0.5 ms** PC-pipeline & **±1.5 ms** physical accuracy vs LDAT; **0** hooks | 9.0 | M | **6.00** | v4 |
| **12** | `lab-true-hdr-renodx` | Automated RenoDX True Native HDR & Safe Single-Player Mod Forge | Game Mods / HDR | **0% GPU** native 16-bit HDR (beats RTX HDR's -12% cost) across 150+ non-AC games | 8.9 | M | **5.93** | v4 |
| **13** | `lab-personalized-hrtf` | Personalized SOFA HRTF Spatial Audio + 2-Min Ear Matcher | Devices / Audio | **<3.5 ms** latency; **+35%** front/back & elevation accuracy; **$0** (beats Atmos) | 8.8 | M | **5.87** | v4 |
| **14** | `lab-auto-highlights` | Zero-FPS-Cost Multimodal Auto-Highlights & VMAF HDR AV1 Shrink | Capture / Voice | **≥85%** highlight recall at **<0.2%** in-game CPU; **-65%** clip size @ VMAF ≥93 | 8.6 | M | **5.73** | 3.x |
| **15** | `lab-thermal-acoustic` | Acoustic-Per-FPS Sweet-Spot & Mic-Aware Thermal Governor | Exo AI / Fans / Power | **-20–35%** GPU watts & **-6–10 dBA** noise for **≤2%** FPS loss (NV/AMD/Intel) | 8.4 | M | **5.60** | 3.x |
| 16 | `lab-zero-latency-audio` | Ultra-Low-Latency WASAPI Small-Buffer Audio Core & Game Mixer | Audio / Voice / Music | **-15–25 ms** audio latency (<5 ms total); **0%** limiter clipping; auto-duck | 8.2 | M | **5.47** | v4 |
| 17 | `lab-driver-timemachine` | Zero-Stutter Driver Time-Machine & Community Regression Radar | PC Health / Drivers | **<30 s** driver + shader-cache rollback; auto-flags **>5%** FPS regressions | 8.1 | M | **5.40** | 3.x |
| 18 | `lab-lan-p2p-cache` | Zero-Config LAN P2P Game, Driver & Shader MeshCache | Store / Downloads | **5–10×** faster LAN installs (up to 900 MB/s); **0 WAN** dupes across stores | 8.0 | M | **5.33** | v4 |
| 19 | `lab-shield-firewall` | Exo Shield++ Game-Session Quiet Curtain & Tracker Stripper | Shield / Hygiene | **-95%** background disk/net interruptions during gameplay | 5.3 | S | **5.30** | 3.x |
| 20 | `lab-golive-ultra` | Discord Go Live Ultra: Direct-NVENC + Exo-to-Exo Sub-30ms P2P | Discord Video | **<1.0%** FPS tax at 1440p120 HDR; **<35 ms** glass-to-glass Exo P2P | 7.9 | M | **5.27** | 3.x |
| 21 | `lab-firmware-radar` | Universal Motherboard BIOS, AGESA/Microcode & NVMe Radar | PC Health / Cloud | **95%+** ASUS/MSI/GB/ASRock & top-50 NVMe coverage; **0** vendor bloat | 5.2 | S | **5.20** | 3.x |
| 22 | `lab-discord-spatial-voice` | Discord Cocktail-Party Spatial Seating & Opus 1.5 Neural PLC | Discord Voice | **+40%** 4-talker comprehension; **+0.8 PESQ** at 10% packet loss | 7.7 | M | **5.13** | v4 |
| 23 | `lab-unified-input` | Universal Hall-Effect/OpenMouse/DualSense & Polling-vs-FPS Guard | Devices / Input | Replaces **600–1,200 MB** vendor suites; recovers **+8–15%** 1% lows on 8 kHz | 7.6 | M | **5.07** | 3.x |
| 24 | `lab-save-timemachine` | Copy-on-Write Universal Save-Game Time Machine & E2EE Sync | Library / Account | **<250 ms** post-exit snapshot; **90%+** dedupe; 15,000+ games covered | 7.5 | M | **5.00** | v4 |
| 25 | `lab-sp-mod-forge` | Safe Single-Player Engine Fix Forge (Ultrawide, 60fps Unlock, Skip-Intro) | Game Mods | **1-click** pinned non-AC fixes w/ 30 s crash rollback across 100+ SP classics | 7.4 | M | **4.93** | v4 |
| 26 | `lab-music-audiophile` | Ultra-Lean <75 MB Background Music Host + R128 & Game DJ | Music Room | **-70%** music RAM (<75 MB) & **0.0%** GPU in-game; **±0.5 LUFS** parity | 7.3 | M | **4.87** | 3.x |
| 27 | `lab-exo-bench` | Hardware Telemetry Blackbox, CPU/GPU Bottleneck % & Diff Cards | Telemetry / Bench | **<0.2%** CPU overhead; **<1.5 MB/hr**; **±3%** CPU/GPU-bound attribution | 7.1 | M | **4.73** | v4 |
| 28 | `lab-store-realfps` | Hardware-Calibrated Store RealFPS Predictor & VRAM-Fit Advisor | Store / Cloud | **±12%** FPS MAE across top 500 Steam games; **<50 ms** edge lookup | 7.0 | M | **4.67** | 3.x |
| 29 | `lab-edge-delta-cdn` | Content-Defined Zstd Delta Patches & AVIF Multi-LOD Cover Edge *(joint `scout-dataflow`)* | Cloud / Updates | **<2 MB** median Exo update (<1 s); **-70%** cover art bytes; **3×** decode | 4.6 | S | **4.60** | 3.x |
| 30 | `lab-universal-config-ai` | Engine-Family Safe Config Intelligence (UE4/5, Unity, Source 2+) | Per-Game Settings | **18 → 200+** validated titles + generic engines; **+15–35%** FPS over auto | 8.8 | L | **3.52** | 3.x |
| 31 | `lab-cinema-madvr` | Exo-Cinema Native Hardware Super-Res, libplacebo HDR & 23.976 Hz | Exo-Cinema | **0** dropped/repeated frames/2hr; **<150 ms** TTFF; 4K HDR + VSR/Anime4K | 8.5 | L | **3.40** | v4 |
| 32 | `lab-session-handoff` | Cross-PC Seamless Session Handoff (Desktop ↔ Handheld Config Split) | Account / Sync | **<3 s** save-to-launch handoff; **0** desktop/handheld graphics clobbers | 8.0 | L | **3.20** | v4 |
| 33 | `lab-acoustic-coach` | Passive Acoustic "Sixth Sense" Footstep Clarity & Hearing Protection APO | Devices / Audio | **-18 dB** explosion peak clip guard; **+6 dB** quiet-detail SNR; **<1 ms** APO | 4.5 | M | **3.00** | v4 |
| 34 | `lab-exo-lan-mesh` | Zero-Config Encrypted LAN-Play Mesh inside Exo Discord Voice | Discord / Network | **1-click** P2P LAN play (<5 ms overhead); replaces Hamachi/ZeroTier | 7.4 | L | **2.96** | v4 |
| 35 | `lab-shader-prefetch-oracle` | Zero-Hook Zone-Transition Predictive Asset & Shader Oracle | Shaders / Storage | **-50%** mid-game level/zone traversal hitches on open-world UE5 titles | 7.2 | L | **2.88** | v4 |
| 36 | `lab-hardware-twin` | Passive Silicon Binning Hardware Twin & Optimization ROI Predictor | PC Health / AI | Predicts OC/UV/RAM gain within **±20%** before tuning; **0** stress runs | 4.2 | M | **2.80** | v4 |
| 37 | `lab-neural-hdr-itm` | Cross-Vendor Zero-Injection Inverse Tone Mapper for AC Games | Display / HDR | **<2% GPU** true OLED black-floor Auto HDR on NV/AMD/Intel (beats RTX HDR) | 6.8 | L | **2.72** | v4 |
| 38 | `lab-shader-cloud` | Crowd-Compiled PSO Metadata Recipe Cloud (Zero Copyright Blobs) | Shaders / Cloud | **95%+** day-1 pre-launch PSO coverage across all stores; **<2 MB** recipe | 9.6 | XL | **2.40** | v4 |

---

## 4. Deep Frontier Analysis & Proposals (All 38 Moonshots)

### #1. Predictive Pre-Launch Asset & Shader Cache Prewarm (`lab-asset-prewarm`)
- **Today in Exo:** `ShaderRebuild.cs` and `ShaderPrepare.cs` compile shaders while idle; `LaunchOrchestrator.cs` launches games cold from disk.
- **2026 Frontier:** Microsoft **Advanced Shader Delivery (ASD)** (SODB → PSDB via DirectX Agility SDK, rolling out on Xbox/Steam/Epic for new DX12 titles); Windows `PrefetchVirtualMemory`; Valve Steam Deck Fossilize. Nobody on Windows combines per-game passive ETW asset I/O recording with pre-launch RAM page-cache warming + driver shader blob warming across all stores.
- **The Exo Moonshot:** Passive ETW `Microsoft-Windows-Kernel-File` (`Read` opcode) records exact `(file, offset, length)` extents read during the first 60 seconds of cold startup + driver `DXCache`/`GLCache`/`D3DSCache` files into a compact per-game hot-extent manifest (strictly capped to ≤25% free RAM with a >4 GB free-RAM floor). On Library hover/selection or Play, Exo asynchronously warms those extents into Windows' Standby Page Cache via `CreateFileMapping` + `PrefetchVirtualMemory`, and surfaces Windows 11 ASD verification alongside `Exo Shaders` priority hotlists.
- **Beats & Target:** Beats cold NVMe/SATA loads, SysMain, and Vulkan-only Steam Shader Pre-Caching. Target: **-30–55% cold boot & first-map load time**, **-60% first-5-minute streaming micro-stutters** on ≥32 GB RAM PCs.
- **Evidence:** `PrefetchVirtualMemory` (`kernel32.dll`) issues coalesced async page-cache reads with zero process injection; ETW `Kernel-File` gives exact byte offsets per PID.
- **Risk:** Anti-cheat: **None** (read-only file mapping + passive ETW). Stability: **Low** (hard free-RAM floor prevents `StandbyPurger` fights). Legal: **None**.
- **Effort / Release:** **S** · **3.x**
- **48-Hour Experiment:** Record first-45s `FileIo` reads for 3 cold-boot games (clearing standby cache between runs), compare 5 cold vs 5 `PrefetchVirtualMemory`-warmed launches in an isolated dev build; **prove** if median time-to-menu drops ≥25% and first-minute 0.1% lows improve ≥15%.

### #2. OLED Anti-Flicker VRR Governor & Hardware MHC2 Color Pipeline (`lab-oled-vrr-mhc2`)
- **Today in Exo:** `GameHdr.cs` toggles HDR per game; `SdrInHdr.cs` patches gamma 2.2; `MonitorDdc.cs` adjusts brightness (`perf-display` owns laptop eDP HDR).
- **2026 Frontier:** `MHC2Gen` (`dantmnf/MHC2`) generates Windows 11 MHC2 ICC profiles with hardware GPU display LUTs (0% shader cost) for sRGB gamut clamping + gamma 2.2 inside HDR; `novideo_srgb` is NVIDIA-only and breaks in HDR.
- **The Exo Moonshot:** (1) Generate native Windows 11 **MHC2** ICC profiles from EDID primaries + HDR calibration min/max nits via `WcsAssociateColorProfileWithDevice`, giving hardware-LUT **ΔE < 1.5 sRGB gamut clamp in HDR** + true 2.2 gamma + OLED black-floor fix across **NVIDIA, AMD, and Intel at 0.00% GPU shader cost**. (2) Real-time `PresentTrace` LFC-crossing & frame-time variance detector that stabilizes OLED VRR flicker via smooth driver cap / `EnableDwmVrr` advisory + optional DDC/CI idle dimming with atomic profile backup/restore.
- **Beats & Target:** Beats `novideo_srgb`, DisplayCAL, Windows HDR Calibration. Target: **-70% LFC crossings/min**, **ΔE < 1.5 sRGB clamp in HDR** at **0.00% GPU cost**.
- **Evidence:** Windows 11 WDDM 3.0+ `mscms.dll` natively loads MHC2 3×3 matrix + 1D LUT into GPU scanout hardware on all 3 vendors; MIT `dantmnf/MHC2` proves exact tag format.
- **Risk:** Anti-cheat: **None**. Stability: **Low** (atomic ICC backup/restore; never permanently overwrite owner's monitor DDC/ICC during lab testing). Legal: **None**.
- **Effort / Release:** **S** · **3.x**
- **48-Hour Experiment:** Generate an MHC2 ICC profile from EDID + gamma 2.2 LUT, validate 0.00% GPU overhead & exact gamut/gamma LUT transform in an isolated test harness (reverting immediately), and verify ≥70% LFC crossing reduction on a synthetic fluctuating framerate workload.

### #3. Predictive Standby & VRAM-Pressure Shield + Opt-In Large Pages (`lab-memory-shield`)
- **Today in Exo:** `StandbyPurger.cs` silently fails non-elevated; no background VRAM eviction before hitting 8–12 GB VRAM cliffs; zero `SeLockMemoryPrivilege` Large Pages.
- **2026 Frontier:** ISLC (reactive tray app, RAM-only); MemReduct (blind working-set flush). Nobody coordinates predictive RAM standby trimming *plus* background VRAM pre-eviction before a game hits the VRAM wall.
- **The Exo Moonshot:** (1) On-demand zero-idle-RAM elevated standby-purge helper firing predictively at 85% pressure or between matches; (2) **Background VRAM Pre-Eviction** reading PDH `\GPU Process Memory(*)\Local Usage` — when a foreground game exceeds 85% dedicated VRAM, safely trims/suspends non-essential browser/Electron GPU helpers (strictly whitelisting anti-cheat, capture, audio, and active Discord calls) to reclaim **0.5–2.5 GB VRAM** before `DxgKrnl` eviction stutters hit; (3) Opt-in `SeLockMemoryPrivilege` Large Pages for supported JVMs/emulators.
- **Beats & Target:** Beats ISLC, MemReduct, stock Windows eviction. Target: **-90% RAM/VRAM-eviction stutters** on 16 GB RAM / 8 GB VRAM GPUs, **+3–8% FPS** on Large-Page JVMs/emulators.
- **Evidence:** `NtSetSystemInformation(80, 4)`, `LsaAddAccountRights(SeLockMemoryPrivilege)`, PDH GPU Process Memory counters.
- **Risk:** Anti-cheat: **None** (never touches game/AC processes). Stability: **Low–Med** (whitelist active media/calls; resume cleanly on alt-tab). Legal: **None**.
- **Effort / Release:** **S** · **3.x**
- **48-Hour Experiment:** On this PC's 8 GB RTX 3070 with Chrome + background apps open under a >7.2 GB VRAM DX12 load, compare `PresentTrace` 1% lows & eviction stutters before vs after VRAM pre-eviction + predictive standby purge; **prove** if 1% lows improve ≥15% or eviction stutters drop ≥75%.

### #4. Speculative Zero-Wait Game Launch & Store Backend Warmup (`lab-instant-launch`)
- **Today in Exo:** `LaunchOrchestrator.cs` wakes hidden store clients reactively after clicking Play, incurring 8–25 s delays.
- **2026 Frontier:** Playnite/GOG Galaxy/Steam/Epic all wake reactively; no launcher uses browser-style hover-intent speculation (`≥500 ms` hover predicts click >60%) to pre-warm store IPC/auth.
- **The Exo Moonshot:** Instrument every millisecond of `Click → Store IPC Ready → Cloud Sync → Keep/HDR → Process Spawn → First DXGI Present` via ETW/`PresentTrace`. Hovering a Library tile ≥500 ms or opening a Game Page silently wakes only the target store backend IPC/auth token and pre-validates `GameSettings.Keep` + `HdrPlan` at EcoQoS — auto-sleeping after 90 s if unused and never launching a game speculatively.
- **Beats & Target:** Beats Playnite, GOG Galaxy, Steam, Epic cold starts. Target: **-40–65% click-to-first-Present time**, **0 MB steady-state RAM**.
- **Evidence:** `LaunchOrchestrator.cs`, `ExoLauncher.SteamIpc`, and `HiddenStoreRuntime.cs` already separate backend readiness from launch.
- **Risk:** Anti-cheat: **None**. Stability: **Low** (idempotent wake + 90 s idle sleep). Legal: **None** (official store clients only).
- **Effort / Release:** **S** · **3.x**
- **48-Hour Experiment:** Instrument launch stages across Steam/Epic/GOG titles and compare 5 cold vs 5 hover-prewarmed launches in a dev build; **prove** ≥35% reduction in click-to-first-Present time with 0 MB net idle RAM after cooldown.

### #5. DirectStorage BypassIO Auto-Healer & Play-Recency Tiered Compression (`lab-storage-hyperdrive`)
- **Today in Exo:** `Drives.cs` diagnoses BypassIO read-only; `GameCompression.cs` compresses globally without play-recency tiering.
- **2026 Frontier:** CompactGUI (manual, no DirectStorage healing, no recency tiering); Steam Storage Manager (no compression).
- **The Exo Moonshot:** (1) **BypassIO Auto-Healer** using `FSCTL_MANAGE_BYPASS_IO` to identify vetoing minifilters/flags and auto-fix Exo-controllable blockers (decompressing accidentally compressed DirectStorage files, clearing sparse flags, targeted TRIM). (2) **Play-Recency Tiering**: active games (<14 days) stay uncompressed on the fastest NVMe; cold games (>30 days untouched) auto-compress with LZX while idle (saving 30–45% SSD space, strictly obeying `AntiCheatCheck.Change.Compress`) and auto-rehydrate hot streaming archives when queued; plus 1-click multi-drive smart swap.
- **Beats & Target:** Beats CompactGUI, Steam Storage Manager. Target: **+30–45% free SSD capacity**, **0% hot-game load regression**, **100% BypassIO restore** on fixable volumes.
- **Evidence:** `FSCTL_MANAGE_BYPASS_IO` (Win11 22H2+), existing `GameCompressor.cs` WOF APIs, `PlaytimeService.cs`.
- **Risk:** Anti-cheat: **Low** (strictly obeys `AntiCheatCheck.Change.Compress` — never touches Riot/Vanguard or AC executables). Stability: **Low**. Legal: **None**.
- **Effort / Release:** **S** · **3.x**
- **48-Hour Experiment:** Heal a WOF-blocked DirectStorage test directory via `FSCTL_MANAGE_BYPASS_IO` and benchmark cold LZX compression + hot re-hydration load times across 2 large titles; **prove** ≥35% space saved and <3% load-time delta.

### #6. Closed-Loop Bayesian Per-Game Auto-Tuner & 92%-GPU-Busy Adaptive Pacer (`lab-closed-loop-tuner`)
- **Today in Exo:** `Autopilot.cs` applies one static heuristic to all games; `perf-cap-lab` adds a manual 20 s cap A/B test.
- **2026 Frontier:** Intel APO (~25-game whitelist, Intel-only), AMD PPM (static), Process Lasso (manual), NVIDIA Reflex (only games that integrate it). Nobody has built a **closed-loop statistical auto-tuner + dynamic GPU-saturation pacer**.
- **The Exo Moonshot:** Two breakthroughs in one engine driven by `PresentTrace` ETW (`MsFrameTime`, 1% low, 0.1% low, `MsGPUActive`):
  1. **Contextual Bandit / Bayesian Knob Optimizer**: across real gameplay, sweeps safe, reversible knobs per game — background `SetProcessDefaultCpuSets` isolation (and game CPU sets strictly on `AntiCheatCheck.None` only!), timer res (0.5/1.0/default), power EPP (0/10/30), driver low-latency (NVAPI/ADLX/IGCL) — with matched-scene workload gating and Welch's t-test / Mann-Whitney U (`p < 0.01`) + automatic rollback on regression.
  2. **Dynamic 92%-GPU-Busy Adaptive Frame-Budget Pacer (Nobody Has Built This)**: watches `MsGPUActive / MsFrameTime` every 250 ms and gently slides the driver FPS cap (NVAPI/ADLX/IGCL) to keep the GPU right at **90–95% utilization** — preventing the 99% GPU-saturation render-queue spike and delivering Reflex-class latency reduction even in games *without* Reflex or Anti-Lag 2!
- **Beats & Target:** Beats Intel APO, AMD PPM, Process Lasso, NVIDIA App, static RTSS caps. Target: **+5–18% 1% low FPS**, **-15–40% stutters (`p < 0.01`)**, and **-20–35% render-queue latency** in GPU-bound non-Reflex games.
- **Evidence:** `PresentTrace.cs` + `DxgKrnl` `MsGPUActive` + `SetProcessDefaultCpuSets` + `NtSetTimerResolution` + NVAPI/ADLX/IGCL live cap/LL APIs.
- **Risk:** Anti-cheat: **Zero** (strictly obeys `AntiCheatCheck` — never touches game handles on AC games). Stability: **Low** (crash-safe journal + auto-rollback). Legal: **None**.
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Sweep 4 live knobs + the 92%-GPU-busy dynamic cap across a repeatable 3-minute GPU-bound benchmark loop over 3 independent runs; **prove** ≥95% repeatability, ≥5% 1% low gain (`p < 0.01`), and ≥20% render-queue latency drop.

### #7. On-Device ETW Micro-Window Stutter Forensic Engine ("Exo Doctor") (`lab-etw-stutter-ai`)
- **Today in Exo:** `StutterAnalysis.cs` uses 7 coarse 1-second-average rules that smear 25 ms stutters into invisibility.
- **2026 Frontier:** CapFrameX (charts only), LatencyMon (uncorrelated to frames), WPA (kernel-expert manual tool).
- **The Exo Moonshot:** Maintain a **500 ms circular in-memory ETW flight recorder** (`EVENT_TRACE_BUFFERING_MODE`, ≤16 MB RAM, <0.5% CPU, 0 MB VRAM in-game) capturing CSwitch preemptions, Hard Page Faults with backing file paths, DPC/ISR routine→driver symbols, `DxgKrnl` GPU preemption/VRAM evictions, and slow `FileIo` reads. On any `PresentTrace` hitch (`> max(2.5× median, median + 10 ms)`), freeze `[t-350 ms .. t+50 ms]` into a deterministic **Causal Graph Engine** (with optional idle-only small ONNX/DirectML plain-English explainer) that names the exact culprit + attaches a 1-click verified remediation.
- **Beats & Target:** Beats CapFrameX, LatencyMon, WPA. Target: **>90% attribution accuracy** across 6 synthetic stutter fault-injection tests at **<0.5% CPU / 0 MB VRAM**.
- **Evidence:** Windows ETW `EVENT_TRACE_BUFFERING_MODE` circular flush; `PresentTrace.cs` & `LatencyTrace.cs` already implement ETW & driver-symbol mapping.
- **Risk:** Anti-cheat: **None** (100% passive kernel ETW). Stability: **Low** (≤16 MB cap, auto-disable if CPU >1%). Legal: **None**.
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Build the circular ETW snapshotter triggered by `PresentTrace` spikes and inject 6 controlled stutters (CPU hog, hard page-fault burst, VRAM spike, slow disk I/O, heavy DPC, shader compile) during live DX12 rendering; **prove** ≥85% (target >90%) attribution within ±50 ms at <0.5% CPU.

### #8. Zero-Copy Direct NVENC / AMF / QSV Capture & RAM-Only Replay (`lab-zerocopy-capture`)
- **Today in Exo:** `ExoCapture/Encode/VideoEncoder.cs` uses Media Foundation MFT (`IMFTransform`), incurring ~3–6% GPU/FPS cost and lacking fine codec controls.
- **2026 Frontier:** OBS Studio (~2–5% loss), NVIDIA ShadowPlay (~2–4% loss + constant SSD write wear), Medal.tv (~5–8% loss).
- **The Exo Moonshot:** Direct C-ABI vendor bindings (`nvEncodeAPI64.dll`, `amfrt64.dll`, Intel `libvpl.dll`/oneVPL) fed straight from `IDXGIOutputDuplication` via a single-pass HLSL compute shader (cursor blend + RGB/FP16 → NV12/P010 + optional HDR tonemap directly into registered D3D11 ring buffers — zero CPU copies!), storing encoded HEVC/AV1 GOPs in a fixed `VirtualAlloc` RAM circular ring buffer for **0 MB/s SSD wear** until Save Replay is pressed (MFT kept as fallback).
- **Beats & Target:** Beats OBS, ShadowPlay, ReLive, Medal.tv, Steam Recording. Target: **<0.8% avg FPS / <1.2% 1% low impact** at 1440p60 10-bit HDR AV1/HEVC, **0 MB/s SSD writes**, **<15 MB working set** (excl. replay buffer).
- **Evidence:** NVIDIA Video Codec SDK, AMD AMF (MIT), Intel oneVPL (MIT) accept direct `ID3D11Texture2D` handles with zero staging.
- **Risk:** Anti-cheat: **None** (Desktop Duplication only; OBS hook fallback remains blocked on Riot/FACEIT/ESEA). Stability: **Low** (MFT fallback on driver reset). Legal: **None**.
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Benchmark Baseline vs current MFT `VideoEncoder.cs` vs Direct `nvEncodeAPI64.dll` vs OBS at 1440p60 HEVC during a 3-minute GPU-bound DX12 benchmark on this PC's RTX 3070; **prove** Direct NVENC holds <1.0% avg FPS loss and beats MFT on FPS & frametime variance.

### #9. Multiplayer Ping/Jitter X-Ray, Live Bufferbloat Shaper & Multipath UDP (`lab-net-path-xray`)
- **Today in Exo:** `NetworkBest.cs`/`NetworkTune.cs` apply static NIC/DNS settings; `WifiGameMode.cs` stops Wi-Fi scans. *(Internal Exo IPC/updater networking is handed to `scout-dataflow`; this owns player-facing multiplayer game networking.)*
- **2026 Frontier:** ExitLag ($10/mo proprietary multipath UDP), PingPlotter ($40+), OpenWrt SQM/L4S (requires custom router).
- **The Exo Moonshot:** (1) Passive **Live Game-Server X-Ray** (`GetExtendedUdpTable` / ETW `Microsoft-Windows-TCPIP` + hop MTR + active gateway-RTT-vs-throughput Bufferbloat Fingerprinting); (2) **Host-Side Adaptive Bandwidth Pacer & QoS Shaper** dynamically pacing non-game host traffic (cloud sync, downloads, uploads) to 80% of measured uplink/downlink + opt-in DSCP EF/CS4 tagging during matches to kill local bufferbloat without a gaming router; (3) Opt-in **WinTun Multipath UDP Redundant Striping** duplicating small UDP game packets across 2 routes with sequence deduplication.
- **Beats & Target:** Beats ExitLag, WTFast, GearUP, PingPlotter, Killer Networking. Target: **-80% local bufferbloat jitter**, **-50–90% effective UDP packet loss** on dual-path striping.
- **Evidence:** `iphlpapi.dll`, ETW `TCPIP`, `qWave.dll` / `NetQosPolicy`, signed **WinTun** driver (MIT/GPL-2.0).
- **Risk:** Anti-cheat: **Zero** on Layers 1–2; Layer 3 WinTun strictly opt-in with per-game VPN notice (zero game injection). Stability: **Low** (atomic route/QoS cleanup). Legal: **None**.
- **Effort / Release:** **M** · **v4**
- **48-Hour Experiment:** Saturate 80% local uplink while pinging a live server/1.1.1.1; **prove** Layer 1–2 cuts p99 RTT jitter ≥70% and Layer 3 dual-path cuts simulated 5% UDP loss ≥70%.

### #10. Exo Micro-Footprint: The Lightest Launcher on Earth (`lab-micro-footprint`)
- **Today in Exo:** WinUI 3 + WebView2 takes ~280–450 MB; even with Rune, the `.NET 10` host drags in WinUI 3/XAML (~120–180 MB) and Rune keeps its wgpu swapchain/textures alive in-game.
- **2026 Frontier:** Steam (~400–700 MB), Epic (~450 MB), GOG Galaxy (~300 MB), Playnite (~180 MB), Discord (~500 MB).
- **The Exo Moonshot:** (1) **Headless Non-WinUI `.NET 10` Host** (benchmarking NativeAOT vs trimmed R2R with `DOTNET_gcServer=0` and zero WinUI 3/XAML/WebView2 DLLs in-process; ephemeral WebView2 auth helper spawns only for sign-in and exits immediately); (2) **Rune "Dormant Game Mode"** dropping wgpu swapchain + cover textures + glyph cache on game launch (<6 MB working set & 0 MB VRAM, <80 ms Alt-Tab wake); (3) mmap binary library cache instant-paint (<180 ms cold start).
- **Beats & Target:** Beats Steam, Epic, GOG, Playnite, Discord by **5–15×**. Target: **<45 MB idle**, **<25 MB & 0 MB VRAM in-game**, **0.00% idle CPU**, **<180 ms cold start**.
- **Evidence:** `.NET 10` NativeAOT + `wgpu::Surface` drop + existing `NamedPipeHostTransport`.
- **Risk:** Anti-cheat: **None**. Stability: **Med** (verify AOT JSON source-gen coverage & clean DX12 surface recreation). Legal: **None**.
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Prototype headless non-WinUI host + Rune Dormant Mode in an isolated dev build (never starting installed Exo); **prove** combined in-game private bytes <30 MB, VRAM = 0 MB, wake <100 ms.

### #11. Hardware-Free End-to-End Click-to-Photon Latency Lab (`lab-click-to-photon`)
- **Today in Exo:** `PresentTrace.cs` measures frametimes; `MouseCore.cs` measures polling jitter; zero click-to-photon pipeline measurement exists.
- **2026 Frontier:** Intel PresentMon 2.x (PC pipeline only, misses mouse switch/wireless & panel GtG); NVIDIA LDAT ($1,000 hardware).
- **The Exo Moonshot:** (1) Pure-ETW 5-stage pipeline joining `MouseCore` read-only RawInput QPC + `Microsoft-Windows-Win32k` + `DXGI` `Present` + `DxgKrnl` `Flip`/`VSyncDPC` for exact **HID → OS → Sim → Render Queue → GPU → Scanout** attribution across all GPUs; (2) **Zero-LDAT 10-Second Physical Calibrator**: 48 kHz WASAPI acoustic click onset (20.8 µs/sample) vs `WM_INPUT` QPC measures physical mouse+wireless delay, plus optional 120/240 fps phone-camera flash page measures panel processing+GtG!
- **Beats & Target:** Beats PresentMon 2.x and $1,000 NVIDIA LDAT. Target: **±0.5 ms** PC-pipeline vs PresentMon 2.x, **±1.5 ms** physical total vs LDAT, **<0.3% CPU**, **0 hooks**.
- **Evidence:** MIT PresentMon 2.x documents exact ETW GUIDs; 48 kHz WASAPI audio gives 20.8 µs acoustic onset resolution.
- **Risk:** Anti-cheat: **None** (100% passive ETW + read-only RawInput; zero HID writes!). Stability: **Low**. Legal: **None**.
- **Effort / Release:** **M** · **v4**
- **48-Hour Experiment:** Validate ETW `AllInputToPhoton` side-by-side against Intel PresentMon 2.x CSV on 2 games (Reflex Off vs On) plus 20-click acoustic mouse latency repeatability; **prove** ≤1.0 ms median error vs PresentMon 2.x and ±0.5 ms acoustic repeatability.

### #12. Automated RenoDX True Native HDR & Safe Single-Player Mod Forge (`lab-true-hdr-renodx`)
- **Today in Exo:** `GameHdr.cs` toggles Windows HDR; `SdrInHdr.cs` fixes desktop gamma 2.2; `DlssSwapService.SessionMods.cs` already safely manages per-session non-AC mods with SHA-256 verification and 30 s crash rollback. Zero native HDR reconstruction or game-mod automation exists.
- **2026 Frontier:** Windows Auto HDR (raised blacks, SDR-mapped, limited whitelist); NVIDIA RTX HDR (NVIDIA-only, **10–15% FPS hit**); **RenoDX** (`clshortfuse/renodx`, MIT/open-source ReShade add-on framework) patches individual game tone-mapper shaders to output **true native 16-bit floating-point HDR with ~0% GPU overhead** across 150+ major PC games (Cyberpunk 2077, Elden Ring, RE4, Witcher 3, Baldur's Gate 3, UE4/UE5 titles) — yet installation is completely manual today and **no launcher automates it**!
- **The Exo Moonshot:** Integrate an automated, SHA-256-pinned **Exo True HDR (RenoDX) Engine** strictly for `AntiCheatCheck.None` games (and Steam offline non-AC sessions via `SessionMods.cs`): automatically detects supported titles in the user's Library, pulls the pinned open-source ReShade + RenoDX `.addon64` + user's monitor peak-nits/black-floor calibration from `HdrHostService.cs` (pre-configuring RenoDX peak brightness & UI paper-white automatically — zero manual slider tweaking!), applies with byte-checked restore + 30-second crash rollback, and cleanly removes every file when toggled off!
- **Beats & Target:** Beats Windows Auto HDR (washed-out raised blacks) and NVIDIA RTX HDR (-10–15% FPS cost, NVIDIA-only). Target: **0–1% GPU overhead** (vs RTX HDR's -12%), **true native 16-bit HDR dynamic range & OLED 0-nit black floor** across 150+ supported non-AC titles on **NVIDIA, AMD, and Intel**, **100% atomic clean uninstall**.
- **Evidence:** `clshortfuse/renodx` is open-source (MIT) with clean per-game `.addon64` binaries & INI config; `DlssSwapService.FrameGen.cs` & `SessionMods.cs` already prove Exo's SHA-256 pinned mod deployment, anti-cheat gating, and 30 s crash rollback!
- **Risk:** Anti-cheat: **Zero** when strictly gated to `AntiCheatCheck.None` (never Riot, never EAC/BattlEye/ACE/VAC/Unknown). Stability: **Low** (30 s crash rollback + byte-exact uninstall). Legal: **None** (MIT open-source add-ons, never alters game binaries on disk, zero DRM bypass).
- **Effort / Release:** **M** · **v4**
- **48-Hour Experiment:** Automate pinned ReShade + RenoDX deployment & peak-nits pre-configuration via `SessionMods` pattern on 2 supported non-AC games on this PC (isolated dev build), comparing stock HDR/Auto HDR vs RTX HDR vs RenoDX on FPS (`PresentTrace`), peak luminance, and OLED black floor; **prove** <1.5% FPS cost, ≥8% faster than RTX HDR, and byte-exact clean removal.

### #13. Personalized SOFA HRTF Spatial Audio + 2-Min Ear Matcher (`lab-personalized-hrtf`)
- **Today in Exo:** `ExoEq.cs` + `EqualizerApo.cs` + `AutoEqCatalog.cs` manage Equalizer APO & AutoEQ parametric presets; zero spatial audio virtualization.
- **2026 Frontier:** Dolby Atmos ($15, generic HRTF), DTS:X ($20, generic), Immerse ($20/yr), SteelSeries Sonar (bloated), HeSuVi (manual 2018 UI), Valve Steam Audio (`libphonon`, Apache-2.0), SADIE II / SONICOM open `.sofa` datasets.
- **The Exo Moonshot:** Compile low-latency 8-channel 7.1→binaural HRIR convolution + AutoEQ headphone compensation into Exo's existing APO pipeline using `libmysofa` (BSD-3) / Steam Audio (Apache-2.0) and open `.sofa` datasets, paired with a **2-Minute Interactive Psychoacoustic Ear Matcher** (adaptive A/B tournament of elevation & front-back sweeps across 16 perceptually clustered eigen-HRTFs + optional local ear-photo ITD scaling).
- **Beats & Target:** Beats Dolby Atmos, DTS:X, Immerse, Sonar, HeSuVi. Target: **<3.5 ms latency**, **<0.8% CPU**, **+35% front/back & elevation accuracy** over generic HRTF, **$0**.
- **Evidence:** `libmysofa` / Steam Audio natively load SOFA; Equalizer APO natively runs multi-channel `Convolution:` WAVs; Exo already manages APO.
- **Risk:** Anti-cheat: **None**. Stability: **Low** (test offline convolution / isolated APO config in lab — never clobber owner's live Exo EQ config!). Legal: Open CC-BY/BSD SOFA attribution in `THIRD_PARTY_NOTICES.md` (zero proprietary Dolby/Creative HRIRs).
- **Effort / Release:** **M** · **v4**
- **48-Hour Experiment:** Generate 8-channel HRIR WAVs from 5 distinct SADIE II/SONICOM `.sofa` profiles, measure offline/APO convolution latency & CPU cost, and run a blind 20-trial localization test; **prove** <5 ms latency, <1% CPU, ≥+25% accuracy over generic HRTF.

### #14. Zero-FPS-Cost Multimodal Auto-Highlights & VMAF HDR AV1 Shrink (`lab-auto-highlights`)
- **Today in Exo:** `ExoCapture` has manual Replay + NVIDIA-only SDR `CaptureShrink`; `VoiceChain.cs` owns clean mic & call audio.
- **2026 Frontier:** Medal.tv (5–8% FPS hit), SteelSeries Moments (brittle game list), Overwolf Outplayed (injection + ads).
- **The Exo Moonshot:** **Stage 1 (In-Game, <0.2% CPU, 0% GPU, zero hooks)** fuses `VoiceChain` mic/Discord excitement bursts + read-only RawInput action intensity + WASAPI game-audio transient flux into lightweight replay markers. **Stage 2 (Post-Match / Idle on DirectML/NPU)** scores only those 15-second candidate windows with a tiny keyword + visual action scorer. Plus upgrades `CaptureShrink` to **3-slice VMAF/SSIMULACRA2-targeted AV1/HEVC shrink** across NVIDIA, AMD, and Intel including **10-bit HDR** (VMAF ≥93, -65% size).
- **Beats & Target:** Beats Medal.tv, SteelSeries Moments, Overwolf Outplayed. Target: **≥85% recall** at **<0.2% in-game CPU / 0% GPU**, **-65% clip size at VMAF ≥93** (incl. HDR).
- **Evidence:** Exo already owns `VoiceChain` taps, WASAPI loopback, ONNX Runtime 1.31.1, and hardware encoders.
- **Risk:** Anti-cheat: **None** (zero hooks, read-only input/audio). Stability: **Low** (Stage 2 runs strictly idle). Legal: **None** (100% local).
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Score 3 recorded gameplay VODs with ground-truth timestamps via Stage 1 triggers + run 3-slice VMAF AV1/HEVC shrink on 5 clips (incl. 1 HDR clip); **prove** ≥80% recall at ≤3× over-proposal and ≥55% shrink at VMAF ≥92.

### #15. Acoustic-Per-FPS Sweet-Spot & Mic-Aware Thermal Governor (`lab-thermal-acoustic`)
- **Today in Exo:** `ExoAiFans.cs` controls NVIDIA index 0 only; zero AMD/Intel fan curves; no FPS/W efficiency knee; no mic acoustic coupling.
- **2026 Frontier:** MSI Afterburner (manual, no FPS/W, no mic awareness, no Intel); FanControl (manual, no game FPS).
- **The Exo Moonshot:** Cross-vendor NVIDIA (NVAPI/NVML), AMD (ADLX), and Intel (IGCL) governor with: (1) **Automated FPS-per-Watt Knee Finder** stepping power ceiling to find where ≥98% FPS costs 25–35% less power & -6–10 dBA; (2) **Mic-Aware Acoustic Coupling** reading `MicSense.cs` room noise floor during Discord calls to trim GPU power 5–10% when fan noise approaches the mic gate; (3) **Predictive Hotspot & `DriveHeat` NVMe Pre-Cooling** with hard ≥80 °C/85 °C clamps and guaranteed VBIOS auto restore.
- **Beats & Target:** Beats MSI Afterburner, FanControl, stock VBIOS curves. Target: **-20–35% GPU watts & -6–10 dBA** for **≤2% FPS loss**, cross-vendor NV/AMD/Intel.
- **Evidence:** Official NVML, ADLX, and IGCL power/fan APIs + existing `PresentTrace` & `MicSense`.
- **Risk:** Anti-cheat: **None**. Stability: **Low** (hard thermal override + guaranteed VBIOS auto restore; test strictly in isolated dev build). Legal: **None**.
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Run a 4-step NVML power-limit sweep (100/85/75/65%) on this PC's RTX 3070 during a GPU-bound DX12 load logging `PresentTrace` FPS, watts, temp, RPM, and `MicSense` dBFS; **prove** ≥20% power cut & ≥4 dB lower ambient mic noise for ≤2.5% FPS loss.

---

### Ranks #16 – #38 (Full Extended Backlog)

#### #16. Ultra-Low-Latency WASAPI Small-Buffer Audio Core & Game Mixer (`lab-zero-latency-audio`)
- **Today:** `AppVolume.cs` sets basic sliders; Windows `audiodg.exe` buffers 10–20 ms and pumps via `CAudioLimiter`.
- **Moonshot:** `IAudioClient3` minimal-period shared stream lock (2.0–2.67 ms a la MIT `REAL` with automatic XRUN glitch step-up + sleep/wake recovery), automatic `-0.3 dBFS` headroom guard in `ExoEq` APO (zero `CAudioLimiter` distortion), 44.1/48 kHz mismatch alerts, and a 4-bus game-aware auto-ducking mixer (Game/Discord/Music/System).
- **Beats & Target:** Beats SteelSeries Sonar, Voicemeeter, Wave Link. Target: **-15–25 ms audio latency** (<5 ms shared), **0% limiter clipping**, **<0.3% CPU**.
- **Evidence:** `IAudioClient3::InitializeSharedAudioStream` + existing `CoreAudioInterop.cs`. **Risk:** Anti-cheat: None; Stability: Low (XRUN fallback). **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Measure loopback chirp round-trip latency before/after `IAudioClient3` minimal-period lock + 10-min zero-XRUN stress test; prove ≥8 ms latency drop & 0 glitches.

#### #17. Zero-Stutter Driver Time-Machine & Community Regression Radar (`lab-driver-timemachine`)
- **Today:** `GpuDriverUpdate.cs` + `DriverRestore.cs` install clean drivers without shader-cache snapshots or automated FPS regression detection.
- **Moonshot:** Atomic driver update transaction: snapshots pre-update `PresentTrace` baselines + compressed `DXCache`/`GLCache` sidecar; auto-detects >5% post-update FPS regressions; offers **<30 s 1-click Driver Time-Machine rollback** restoring both the signed prior DriverStore package *and* its warm shader cache; crowdsources anonymous `(gpu, driver, game, p50, p1_low, crash_rate)` advisories via `cloud/reports`.
- **Beats & Target:** Beats NVCleanstall, DDU, GeForce Experience, Adrenalin. Target: **<30 s rollback with warm shaders**, **>5% regression auto-flag** in 1 session.
- **Evidence:** `pnputil` DriverStore retention + deterministic `%LOCALAPPDATA%` shader paths. **Risk:** None. **Effort / Rel:** **M** · **3.x**.
- **48-Hour Experiment:** Snapshot current driver + `DXCache` + baseline `PresentTrace`, test automated cache archive & restore on a wiped cache; prove <45 s restore with 0 shader recompile stutters.

#### #18. Zero-Config LAN P2P Game, Driver & Shader MeshCache (`lab-lan-p2p-cache`)
- **Today:** Every household/LAN PC re-downloads 50–150 GB patches, drivers, and shaders over WAN.
- **Moonshot:** mDNS-discovered, PIN-paired **Exo MeshCache** serving BLAKE3 1 MB chunks of Epic/GOG/unencrypted game folders, signed GPU driver installers, upscaler DLLs, Exo updates, and matching-GPU+driver shader caches over multi-stream QUIC/HTTP3 at 1–10 Gbps LAN line rate — cryptographically verified against the receiving user's own official licensed store manifest.
- **Beats & Target:** Beats Steam LAN Transfer (Steam-only) and LANCache.net (Linux Docker server). Target: **5–10× faster LAN installs** (up to 900 MB/s on 10GbE), **0 WAN dupes**.
- **Evidence:** `.NET 10` `System.Net.Quic` (MsQuic) + BLAKE3 (>3 GB/s). **Risk:** Legal: strictly manifest-verified against licensed store installs (zero DRM/piracy sharing). **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Loopback/LAN QUIC BLAKE3 transfer of a 2 GB game folder + driver package with manifest verification; prove ≥80% NIC line rate & 100% hash integrity.

#### #19. Exo Shield++: Game-Session Quiet Curtain & Tracker Stripper (`lab-shield-firewall`)
- **Today:** `shield/` declutters Spotify/YTM WebViews; `perf-os-tweaks` pauses WU/DO/BITS.
- **Moonshot:** Reversible **Game-Session Quiet Curtain** pausing OneDrive/Dropbox/Google Drive sync engines, Adobe/Electron updaters, and indexers on game launch (resuming cleanly on exit via crash-safe journal), plus expanded `shield/rules/network.json` tracker & auto-play stripping.
- **Beats & Target:** Beats Focus Assist, O&O ShutUp10++, Razer Cortex. Target: **-95% non-game disk/network spikes** during gameplay.
- **Evidence:** OneDrive clean pause CLI + `Autopilot` journal + `shield/` DNR rules. **Risk:** Anti-cheat: None (`AutopilotSafety` allowlist). **Effort / Rel:** **S** · **3.x**.
- **48-Hour Experiment:** Run active OneDrive sync + background tabs during a game session; prove ≥90% cut in non-game ETW `FileIo`/network spikes & 100% clean resume.

#### #20. Discord Go Live Ultra: Direct-NVENC AV1/HEVC + Exo-to-Exo Sub-30ms P2P (`lab-golive-ultra`)
- **Today:** `GoLiveCapture.cs` + `H264Rtp.cs`/`H265Rtp.cs` encode via ExoCapture MFT through Discord media servers.
- **Moonshot:** Direct-NVENC/AMF/QSV zero-copy encode + GPU BT.2390 HDR→SDR tonemap + sharp text mode for desktop shares, plus opt-in **Exo-to-Exo E2EE Direct QUIC/ICE Video Upgrade** (signaled via `FriendLinks`/`CallRelayClient`) for **1440p120 HDR / 4K60 at <35 ms glass-to-glass** between consenting Exo friends without bypassing Discord Nitro servers/entitlements.
- **Beats & Target:** Beats Discord Desktop Go Live and Parsec. Target: **<1.0% FPS cost** at 1440p120, **<35 ms glass-to-glass** Exo P2P.
- **Evidence:** Direct GPU encode→QUIC + existing `CallRelayClient.cs` & `native-ui/src/video_decoder.rs`. **Risk:** Anti-cheat: None; Legal: strictly P2P over Exo peers, never spoofing Nitro. **Effort / Rel:** **M** · **3.x**.
- **48-Hour Experiment:** Pipe direct-NVENC HEVC + GPU HDR→SDR tonemap over loopback QUIC to `video_decoder.rs`; prove <35 ms glass-to-glass & <1.2% game FPS loss.

#### #21. Universal Motherboard BIOS, AGESA/Microcode & Top-50 NVMe Radar (`lab-firmware-radar`)
- **Today:** `BiosUpdate.cs` checks ASUS only; `Drives.cs` only knows latest firmware for Samsung 990 PRO.
- **Moonshot:** Signed <150 KB hardware firmware advisory feed published by `cloud/updates` covering ASUS, MSI, Gigabyte, ASRock (AGESA/Intel 0x12B/0x12F stability alerts) + top-50 NVMe drives, plus cross-vendor 1-click USB BIOS Flash Prep (`BiosPrep.cs` supporting ASUS/MSI/GB/ASRock naming & SHA-256 verification — never flashing from Windows!).
- **Beats & Target:** Beats Armoury Crate, MSI Center, GCC, Samsung Magician. Target: **95%+ enthusiast coverage**, **0 vendor bloat**.
- **Evidence:** Existing `cloud/updates/src/watch.js` cron + SMBIOS/NVMe strings in `PcAbout.cs`/`Drives.cs`. **Risk:** Official links/hashes only (never redistributing BIOS binaries). **Effort / Rel:** **S** · **3.x**.
- **48-Hour Experiment:** Scrape/normalize 20 ASUS/MSI/GB/ASRock boards + 20 NVMe models and verify against this PC's read-only SMBIOS & NVMe firmware; prove ≥95% accurate matching.

#### #22. Discord Cocktail-Party Spatial Seating & Opus 1.5 Neural PLC (`lab-discord-spatial-voice`)
- **Today:** `DiscordClient/Voice/*` mixes inbound voices flat with standard Opus PLC (`lab-voice-ns` owns mic NS; `perf-voice-loudness` owns LUFS).
- **Moonshot:** Subtle binaural HRIR spatial seating (-45°, -15°, +15°, +45°) leveraging Cherry's Cocktail Party Effect (**+6–9 dB SNR** in overlapping callouts) + receiver-side **Opus 1.5+ neural PLC / NoLACE** (works on standard Discord Opus packets, zero server changes!).
- **Beats & Target:** Beats Discord Desktop & TeamSpeak 5. Target: **+40% 4-talker comprehension**, **+0.8 PESQ** at 10% packet loss, **<1.2% CPU**.
- **Evidence:** Opus 1.5+ (`--enable-deep-plc`, BSD-3) + 64-tap fixed-azimuth HRIR panning (<0.05% CPU/speaker). **Risk:** None. **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Build `libopus 1.5+` with `--enable-deep-plc` in `voice/`, score 10% burst packet loss vs current Opus PLC (PESQ/DNSMOS), and benchmark 6-speaker HRIR panning; prove ≥+0.5 PESQ & <1.5% CPU.

#### #23. Universal Hall-Effect / OpenMouse / DualSense Engine & Polling-vs-FPS Guard (`lab-unified-input`)
- **Today:** `mouse/` covers OpenMouse; `WootingKeyboard.cs` has Wooting backend; `ControllerLab.cs` measures jitter.
- **Moonshot:** Clean-room open HID profiles for Wooting + QMK/VIA/VIAL + Keychron/DrunkDeer/MonsGeek Hall-Effect keyboards + DualSense adaptive hair-trigger curves (disabling illegal SOCD where banned) + **8 kHz Polling-vs-FPS Guard** detecting game engines choking on 8 kHz polling via `PresentTrace` and auto-recommending/switching to 2 kHz/4 kHz per game.
- **Beats & Target:** Beats G HUB + Synapse + iCUE + Wootility + DSX combined. Target: eliminates **600–1,200 MB vendor bloat**, recovers **+8–15% 1% lows** on 8 kHz-sensitive engines.
- **Evidence:** Open HID specs for Wooting/QMK/DualSense + `PresentTrace`.
- **Risk:** **HARD RULE:** NEVER write to the owner's real mouse, keyboard, or controller during dev/lab runs — use mock/emulated HID transport for all write tests, and read-only RawInput inspection for the Polling-vs-FPS detector! Anti-cheat: None (no illegal SOCD). Legal: keep AGPL isolated in `mouse/`.
- **Effort / Release:** **M** · **3.x**
- **48-Hour Experiment:** Read-only `PresentTrace` benchmark of frame-time impact across mouse movement in 2 engines + unit/mock-HID protocol verification for QMK/VIA/DualSense output reports (zero writes to owner's real hardware!).

#### #24. Copy-on-Write Universal Save-Game Time Machine & Zero-Knowledge Sync (`lab-save-timemachine`)
- **Today:** Exo Account syncs only Exo prefs/playtime; non-Steam/modded games have no versioned save protection.
- **Moonshot:** Content-addressed BLAKE3+zstd Copy-on-Write Save Time Machine hooked to `GameStartDetection` using PCGamingWiki's 15,000+ game save manifest — instant <250 ms launch/exit snapshots, 1-click Game Page Rewind, optional E2EE zero-knowledge sync (AES-256-GCM key derived from Exo Account password) to LAN peers or user's cloud/R2.
- **Beats & Target:** Beats Steam Cloud, Ludusavi, GameSave Manager. Target: **<250 ms snapshot**, **90%+ dedupe**, 15,000+ games, **100% E2EE**.
- **Evidence:** `ludusavi-manifest` (CC-BY-SA/MIT) + BLAKE3/zstd (>500 MB/s). **Risk:** Anti-cheat: None (closed-game read-only copy). **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Match local installed games against the manifest, run 5 launch/exit BLAKE3+zstd snapshots + restore test; prove ≥90% path match, <250 ms snapshot, byte-exact restore.

#### #25. Safe Single-Player Engine Fix Forge (`lab-sp-mod-forge`)
- **Today:** Single-player PC classics ship broken (60 fps locks, 16:9 pillarboxing on ultrawide, unskippable intros, broken frame pacing); players hunt sketchy Nexus/GitHub zips manually.
- **Moonshot:** Extend Exo's SHA-256-pinned `SessionMods.cs` + `AntiCheatCheck.None` pipeline into a curated, open-source **Single-Player Engine Fix Forge** (ultrawide/21:9/32:9 fixes like Flawless/Lyall's MIT `*Fix` repos, 60 fps cap unlockers for Elden Ring/Sekiro/Skyrim/Dark Souls in offline non-AC mode, skip-intro, FOV, Special K pacing) with byte-checked restore and 30-second crash rollback!
- **Beats & Target:** Beats manual GitHub/PCGamingWiki hunting, Vortex/MO2 bloat, paid Patreon mods. Target: **1-click verified non-AC engine fixes** across 100+ SP classics with **100% clean rollback**.
- **Evidence:** Lyall's open-source MIT fix collection (`UE4SS`/ASI `*Fix` repos) + existing `DlssSwapService.SessionMods.cs` & `VerifiedGitHubReleaseDownloader.cs`.
- **Risk:** Anti-cheat: **Zero** (strictly `AntiCheatCheck.None` or offline Steam non-AC session only, auto-removed on exit). Legal: Strictly open-source MIT/BSD/Apache fixes (zero DRM bypass, zero paid/pirated assets).
- **Effort / Release:** **M** · **v4**
- **48-Hour Experiment:** Package 3 pinned open-source MIT single-player fixes (ultrawide + intro-skip + FPS-cap unlock) through `SessionMods.cs` on non-AC titles in a dev build; prove 1-click install, measurable FPS/aspect fix, 30 s crash rollback, and byte-exact removal.

#### #26. Ultra-Lean <75 MB Background Music Host + Cross-Service R128 & Game DJ (`lab-music-audiophile`)
- **Today:** Hidden Spotify/Apple Music/YTM WebView2 hosts consume 200–350 MB RAM and 2–5% CPU/GPU while gaming.
- **Moonshot:** Lock hidden music WebViews into a `--disable-gpu-compositing`, paused-paint, DOM-timer-throttled EcoQoS sandbox (**<75 MB RAM, 0.0% GPU** during games, zero DRM circumvention!), add per-PID WASAPI loopback **EBU R128 Loudness Normalization** across all 3 services, and add a **Game-Reactive DJ** pausing/ducking on in-game dialogue or Discord speech.
- **Beats & Target:** Beats Spotify Desktop (500 MB), Apple Music app, browser YTM. Target: **-70% RAM (<75 MB)**, **0.0% GPU**, **±0.5 LUFS** cross-service parity.
- **Evidence:** WebView2 offscreen throttling + `AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS` (Win10 20348+). **Risk:** Zero DRM bypass; verify Widevine EME audio stays glitch-free. **Effort / Rel:** **M** · **3.x**.
- **48-Hour Experiment:** Run hidden Spotify + YTM WebView2 with `--disable-gpu-compositing` + DOM pause + EcoQoS; prove glitch-free Widevine audio at <90 MB RAM & 0.0% GPU.

#### #27. Hardware Telemetry Blackbox, CPU/GPU Bottleneck % & Diff Cards (`lab-exo-bench`)
- **Today:** `AutopilotWatch.cs` keeps 10 basic stutter summaries; no synchronized time-series blackbox or diff cards.
- **Moonshot:** CapFrameX killer baked into Exo: <1.5 MB/hr compressed blackbox recording `PresentTrace` + `MsGPUActive`/`MsCPUBusy` + temps/watts/VRAM, computing exact **CPU-Bound % vs GPU-Bound %**, **FPS/Watt**, and **Smoothness Index**, with native Rune Before/After Diff Cards.
- **Beats & Target:** Beats CapFrameX, Afterburner OSD, FrameView. Target: **<0.2% CPU**, **<1.5 MB/hr**, **±3% bottleneck accuracy** vs PresentMon GPU-busy.
- **Evidence:** MIT PresentMon `DxgKrnl` DMA packet `MsGPUActive` + delta-varint/zstd. **Risk:** None. **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Add `MsGPUActive` to `PresentTrace`, verify CPU-bound vs GPU-bound % on low-res vs 4K scenes against PresentMon 2.x; prove ±5% agreement at <0.3% CPU.

#### #28. Hardware-Calibrated Store RealFPS Predictor & VRAM-Fit Advisor (`lab-store-realfps`)
- **Today:** `StoreService.Fit.cs` uses static publisher min/rec spec strings.
- **Moonshot:** Anonymous k-anonymized (`k ≥ 20`) `PresentTrace` aggregates in `cloud/reports` + client-side hardware TFLOPS/bandwidth/VRAM scaling curve to predict exact FPS & VRAM fit on every Store page (*"RTX 3070 @ 1440p: ~94 FPS avg / 68 1% low — High for 8 GB VRAM"*).
- **Beats & Target:** Beats Steam Store, CanYouRunIt, PCGameBenchmark. Target: **±12% FPS MAE** across top 500 Steam games, **<50 ms** edge lookup.
- **Evidence:** Existing `cloud/reports` anonymous telemetry + hardware scaling curves. **Risk:** Privacy: strict k-anonymity & `PRIVACY.md` disclosure. **Effort / Rel:** **M** · **3.x**.
- **48-Hour Experiment:** Seed hardware-scaling matrix from public GPU ratios + 20 local `PresentTrace` captures across 2 resolutions; prove leave-one-out FPS MAE ≤15%.

#### #29. Content-Defined Zstd Delta Patches & AVIF Multi-LOD Cover Edge *(Joint with `scout-dataflow`)* (`lab-edge-delta-cdn`)
- **Today:** `AppUpdateService.Delta.cs` does basic deltas; `cloud/covers` proxies raw covers.
- **Moonshot:** `zstd --patch-from` deltas across Exo + Rune + add-ons (<2 MB / <1 s updates) + immutable AVIF/WebP exact-LOD cover tiles in `cloud/covers`.
- **Beats & Target:** Beats Electron/Squirrel 100 MB updates. Target: **<2 MB median update**, **-70% cover bytes**, **3× faster GPU decode**.
- **Evidence:** BSD-3 `zstd --patch-from` (96–99% savings). **Risk:** Full SHA-256 verification + full-installer fallback; honor `cinema-art-cache.md` legal exclusions. **Effort / Rel:** **S** · **3.x**.
- **48-Hour Experiment:** Compute `zstd --patch-from` deltas across Exo 3.29.1→3.30.0→3.30.1; prove median delta <3 MB.

#### #30. Engine-Family Safe Config Intelligence (UE4/5, Unity, Source 2, RE, Frostbite) (`lab-universal-config-ai`)
- **Today:** `GameSettings.Catalog.cs` supports 18 hand-coded games and skips Xbox/MS Store (`perf-game-profiles` adds ~6 more).
- **Moonshot:** Read-only engine fingerprinting on disk + PCGamingWiki schema + VRAM/HDR/aspect-ratio formulas generating hardware-calibrated **Competitive / Balanced / OLED HDR** `OwnInterface` text configs (incl. accessible Xbox AppData configs) with diff preview and atomic rollback.
- **Beats & Target:** Beats NVIDIA App Optimal Settings & AMD HYPR-RX. Target: **18 → 200+ validated titles + generic UE4/5 & Unity**, **+15–35% FPS** over auto defaults.
- **Evidence:** Standard UE/Unity config schemas + `GameSettings.Keep.cs`. **Risk:** Strictly `OwnInterface` text configs (never Riot, never Marvel Rivals `Engine.ini`). **Effort / Rel:** **L** · **3.x**.
- **48-Hour Experiment:** Synthesize hardware-scaled `GameUserSettings.ini` across 5 uncataloged UE games; prove ≥+15% FPS on ≥4/5 with atomic undo.

#### #31. Exo-Cinema Native Hardware Super-Res, `libplacebo` HDR & 23.976 Hz Sync (`lab-cinema-madvr`)
- **Today:** `Exo-Cinema` uses standard playback; `cinema-art-cache.md` notes uncached catalogs & oversized posters.
- **Moonshot:** Native `libplacebo` + D3D11VA zero-copy renderer with RTX VSR / AMD Video Upscale / Intel VSR / Anime4K-FSRCNNX shaders, dynamic per-frame BT.2390 HDR→SDR & DoVi tone mapping, automatic 23.976/48/120 Hz VRR/QMS refresh matching, and `poster/small` + SWR local caching.
- **Beats & Target:** Beats Plex, Stremio, Jellyfin Media Player, VLC. Target: **0 dropped/repeated frames per 2-hr 23.976 fps film**, **<150 ms TTFF**, 4K HDR + VSR/Anime4K at **<15% GPU**.
- **Evidence:** LGPL-2.1 `libplacebo` + `ID3D11VideoProcessor` (RTX VSR already wired in `NvidiaVideo.cs`). **Risk:** LGPL dynamic linking + `cinema-art-cache.md` legal guardrails. **Effort / Rel:** **L** · **v4**.
- **48-Hour Experiment:** Play a 4K HDR10 23.976 fps test clip in a standalone `libplacebo` + D3D11VA harness; prove 0 dropped/repeated frames over 10 min & <15% GPU on RTX 3070.

#### #32. Cross-PC Seamless Session Handoff (Desktop ↔ Handheld Config Split) (`lab-session-handoff`)
- **Today:** Steam Cloud clobbers desktop 4K Ultra configs onto laptops/handhelds (ROG Ally/Legion Go) on every switch.
- **Moonshot:** Separate gameplay saves (#24 E2EE Save Time Machine) from per-device hardware graphics profiles (#30 + `GameSettings.Keep` + `AccountDevices.cs`): exit desktop → save syncs in <3 s → pick up handheld → Exo pulls exact checkpoint while keeping handheld 1080p/FSR/40 Hz graphics intact!
- **Beats & Target:** Beats Steam Cloud & Xbox Play Anywhere. Target: **<3 s save-to-launch handoff**, **0 desktop↔handheld config clobbers**.
- **Evidence:** `GameSettings.Keep.cs` already reapplies per-PC configs pre-launch. **Risk:** None (offline conflict UI). **Effort / Rel:** **L** · **v4**.
- **48-Hour Experiment:** Simulate a Desktop↔Laptop switch across 2 Steam Cloud games; prove clean save transfer with 0 graphics-config clobbers.

#### #33. Passive Acoustic "Sixth Sense" Footstep Clarity & Hearing Protection APO (`lab-acoustic-coach`)
- **Today:** Gamers crank headset volume to hear quiet footsteps and suffer painful 105+ dB gunshot/explosion spikes.
- **Moonshot:** Zero-hook multi-band dynamic loudness equalizer inside `ExoEq` APO: applies transparent lookahead brickwall limiting on sub-bass/explosion peaks (capping output to safe ≤80–85 dB equivalent) while gently applying upward expansion on quiet 1.5–4 kHz detail — improving acoustic clarity *and* protecting players' hearing simultaneously!
- **Beats & Target:** Beats Sound Lock (crude global volume slider that pumps audio) and raw game mixes. Target: **-18 dB peak explosion ceiling**, **+6 dB quiet-detail SNR**, **<1 ms APO latency**, **100% anti-cheat safe**.
- **Evidence:** Standard 3-band Linkwitz-Riley crossover + soft-knee dynamics in Equalizer APO / custom VST/APO DSP. **Risk:** None (pure audio dynamics, zero game hooks). **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Process recorded FPS combat audio (loud explosions + quiet footsteps) through a 3-band dynamic APO prototype; measure peak reduction dB, footstep SNR gain, and <1 ms latency.

#### #34. Zero-Config Encrypted LAN-Play Mesh inside Exo Discord Voice (`lab-exo-lan-mesh`)
- **Today:** Co-op & classic LAN games require Hamachi, Radmin VPN, ZeroTier, or router port-forwarding.
- **Moonshot:** One-click **Exo LAN Party** button inside any Discord voice room: exchanges ephemeral WireGuard/Noise keys over Exo's existing `FriendLinks` / `CallRelayClient`, spins up a clean WinTun virtual LAN subnet (`10.240.x.x`) with UDP broadcast forwarding + automatic QoS priority — play any LAN-capable modded/classic game together in 3 seconds!
- **Beats & Target:** Beats Hamachi, Radmin VPN, ZeroTier, manual port-forwarding. Target: **<5 s room-to-LAN ready**, **<5 ms P2P tunnel overhead**, **0 port forwarding**.
- **Evidence:** WinTun + WireGuard/Noise-IK + STUN/ICE NAT hole-punching + existing `relay/` Worker fallback. **Risk:** Anti-cheat: strictly opt-in virtual NIC for LAN games (never active during Vanguard/FACEIT). **Effort / Rel:** **L** · **v4**.
- **48-Hour Experiment:** Establish a 2-peer WinTun + WireGuard/QUIC tunnel with UDP LAN broadcast relay (e.g., Minecraft LAN discovery packet); prove discovery & <5 ms added RTT.

#### #35. Zero-Hook Zone-Transition Predictive Asset & Shader Oracle (`lab-shader-prefetch-oracle`)
- **Today:** Open-world UE5 games suffer traversal hitches when crossing world-partition boundaries.
- **Moonshot:** Passive ETW `FileIo` Markov transition model: learns which world-partition `.ucas`/`.pak` chunks consistently precede each other during gameplay (`Chunk A → Chunk B` with >70% probability) and asynchronously pre-warms `Chunk B` via `PrefetchVirtualMemory` 3–5 seconds *before* the player crosses the boundary!
- **Beats & Target:** Beats cold streaming and OS sequential read-ahead (which misses non-contiguous `.ucas` offsets). Target: **-50% mid-game traversal stutters** in open-world UE5 games, **0 hooks**.
- **Evidence:** ETW `Kernel-File` exposes exact `(file, offset)` sequences in real time; Markov transition table fits in <500 KB RAM. **Risk:** Anti-cheat: None (read-only prefetch). **Effort / Rel:** **L** · **v4**.
- **48-Hour Experiment:** Record `FileIo` chunk sequence during a 10-minute open-world traversal loop, fit a 1st-order Markov prefetcher, and replay the traversal; prove ≥40% reduction in traversal page-fault/read stalls.

#### #36. Passive Silicon Binning Hardware Twin & Optimization ROI Predictor (`lab-hardware-twin`)
- **Today:** Users blindly run 30-minute stress tests or tweaks without knowing if their silicon has headroom.
- **Moonshot:** Passively fingerprints your exact CPU effective boost-vs-voltage, GPU boost-vs-wattage, memory latency, NVMe QD1 read, and IRQ noise floor during normal play, comparing against k-anonymized `cloud/reports` percentiles for the exact same SKU — predicting exact FPS/watt ROI before running any Auto-Tune!
- **Beats & Target:** Beats CTR/Hydra (heavy manual stress) & UserBenchmark (inaccurate). Target: predicts OC/UV/tuning ROI within **±20%** with **0 synthetic stress runs**.
- **Evidence:** `GpuSensors`, `PcAbout`, `PresentTrace`, `Drives` already collect every passive signal. **Risk:** None (read-only telemetry). **Effort / Rel:** **M** · **v4**.
- **48-Hour Experiment:** Collect passive efficiency fingerprints across 3 workloads on this PC and verify correlation against actual NVML power-curve sweep ROI from `#15`.

#### #37. Cross-Vendor Zero-Injection Inverse Tone Mapper for AC Games (`lab-neural-hdr-itm`)
- **Today:** `#12` (RenoDX) handles non-AC games, leaving anti-cheat/multiplayer SDR games stuck with Windows Auto HDR (washed-out raised blacks) or NVIDIA-only RTX HDR (-12% FPS).
- **Moonshot:** Lightweight DirectComposition / DXGI swapchain-adjacent inverse tone-mapping shader (or Windows 11 MHC2 + custom DWM composition surface where supported, zero DLL injection into the game!) implementing a perceptual BT.2446 Method C / inverse-filmic highlight expansion with **true 0-nit OLED black floor** across NVIDIA, AMD, and Intel at **<2% GPU cost**.
- **Beats & Target:** Beats Windows Auto HDR & NVIDIA RTX HDR (-12% GPU, NV-only). Target: **<2% GPU cost**, **0-nit black floor**, cross-vendor NV/AMD/Intel, **0 game injection**.
- **Evidence:** DirectComposition / `IDXGIOutputDuplication` + HLSL BT.2446 compute shader runs in <0.3 ms at 1440p on modern GPUs. **Risk:** Verify zero added frame-presentation latency in borderless flip-model; kill immediately if presentation latency >1 frame! **Effort / Rel:** **L** · **v4**.
- **48-Hour Experiment:** Prototype a BT.2446 HLSL compute shader on a borderless SDR game frame measuring GPU ms and PresentTrace added display latency; kill if added latency >1 frame or GPU >2.5%.

#### #38. Crowd-Compiled PSO Metadata Recipe Cloud (`lab-shader-cloud`)
- **Today:** `Exo Shaders` (`shaders/`) misses dynamic runtime-only PSOs until you play once; Microsoft ASD only covers newly authored opt-in games.
- **Moonshot:** Opt-in Cloudflare R2 cloud storing **ONLY tiny (<2 MB) numeric PSO pipeline-state metadata recipes** (root signature / blend / rasterizer / local pak shader-hash tuples — **zero copyrighted HLSL/DXIL bytecode or compiled driver blobs!**). Local `ExoShaders.Service.exe` downloads the recipe and compiles 100% of those pipelines locally against the user's own installed game files on day 1 before first launch across every store!
- **Beats & Target:** Beats Steam Shader Pre-Caching (Vulkan/Steam-only, GB blobs), Microsoft ASD (new opt-in only), local-only SCSKiller. Target: **95%+ day-1 pre-launch PSO coverage** across all stores, **<2 MB recipe**, **0 bytes copyrighted shader binary**.
- **Evidence:** SCSKiller already decouples PSO description capture from offline compilation against local pak files. **Risk:** Contributors record strictly on `AntiCheatCheck.None`; consumers compile offline with game closed; strict zero-bytecode schema validator. **Effort / Rel:** **XL** · **v4**.
- **48-Hour Experiment:** Strip all shader bytecode from a recorded `ExoShaders` UE5 PSO set leaving only state descriptors + local pak hashes, import on a wiped driver cache, and compile offline; prove ≥90% reduction in first-run shader compile stutters from a <2 MB metadata file.

---

## 5. Recommended Dispatch Waves & Synergy Map

- **Wave A — Quick Wins (Effort S, Ranks #1–#5):** `01-lab-asset-prewarm`, `02-lab-oled-vrr-mhc2`, `03-lab-memory-shield`, `04-lab-instant-launch`, `05-lab-storage-hyperdrive`. Zero file overlap, every one provable in <48 hours.
- **Wave B — Flagship Core Tech & Game Mods (Effort M, Ranks #6–#12):** `06-lab-closed-loop-tuner`, `07-lab-etw-stutter-ai`, `08-lab-zerocopy-capture`, `09-lab-net-path-xray`, `10-lab-micro-footprint`, `11-lab-click-to-photon`, `12-lab-true-hdr-renodx`. `#11` + `#7` feed `#6`; `#8` unlocks `#14` & `#20`; `#2` + `#12` make Exo the undisputed OLED HDR king.
- **Wave C — Sensory & Thermal Dominance (Effort M, Ranks #13–#15):** `13-lab-personalized-hrtf`, `14-lab-auto-highlights`, `15-lab-thermal-acoustic`.
