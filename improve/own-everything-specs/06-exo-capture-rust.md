# Spec 06 — Exo Capture Native (`exo-capture-rs`): Zero-Copy DX12/WGC + Hardware NVENC/AMF/QSV & Clean-Room Capture

- **Inventory Rank:** #06 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `L`
- **Replaces:** `.NET 10` `ExoCapture.exe` (4.56 MB Vortice D3D11/MF) + **6 GPL-2.0 OBS Studio binaries** (`graphics-hook32/64.dll`, `inject-helper32/64.exe`, `get-graphics-offsets32/64.exe` in `capture/third_party/obs/`), plus replaces external **OBS Replay**, **NVIDIA ShadowPlay**, and **Medal.tv** (`Services/Capture*.cs`)
- **Anti-Cheat Class:**
  - Default **WGC + DXGI Desktop Duplication** capture: **`AC-SAFE`** (works on 100% of anti-cheat games including Vanguard, EAC, BattlEye with zero hooks!).
  - Optional legacy exclusive-fullscreen hook fallback (`exo_cap_hook.dll`): **`AC-OFFLINE-ONLY`** (`AntiCheatCheck.Kind.None` only; never injected into `Found`/`PossiblyVac`/`Unknown`/`Riot`).
- **License:** MIT (clean-room Rust — eliminates GPL-2.0 OBS Studio binary bundling & `addon-source.mjs fetch-obs` release packaging!).

---

## 1. Scope

### What It Solves
1. Exo Capture already avoids FFmpeg by using hardware Media Foundation encoders, which is great — **but** it still bundles 6 GPL-2.0 OBS binaries (`capture/third_party/obs/`) for legacy fallback and runs as a separate `.NET 10` Vortice app.
2. Gamers still run OBS (high manual config) or Medal.tv (300–600 MB Electron + ads) or ShadowPlay (vendor-locked, single mixed audio track, breaks HDR/multi-track).

### In Scope
- Isolated native Rust binary `exo-capture.exe` (preserving `AGENTS.md` Hard Stop: **zero screen capture APIs inside `ExoLauncher.exe` or `exo-ui.exe`**!).
- **Zero-Copy GPU Capture Pipeline:**
  - Primary: **Windows.Graphics.Capture (WGC)** per-window capture (`IsBorderRequired = false`, `IsCursorCaptureEnabled` toggle) + **DXGI Desktop Duplication** fallback, feeding D3D11/DX12 NV12/P010 (10-bit HDR!) GPU surfaces directly to hardware encoders without a single CPU pixel readback!
  - Direct **NVENC SDK / AMD AMF / Intel QSV** hardware encoding (H.264, HEVC 10-bit HDR, AV1 10-bit on RTX 40+/RX 7000+/Arc) with Windows Media Foundation MFT fallback.
- **Zero-Disk-Wear RAM Ring-Buffer Instant Replay:** Stores encoded Annex-B/OBU GOP chunks in a bounded memory ring buffer (30 s – 5 min, configurable MB budget) — **zero SSD writes** until the user hits Save Replay!
- **Multi-Track WASAPI Per-App Audio Isolation:** Uses Windows 11 `ActivateAudioInterfaceAsync` process-loopback (`AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS`) to record **3 separate AAC/Opus tracks** in the MP4 container: (1) Game audio, (2) Microphone, (3) Discord/Browser — so users can mute Discord chatter in a saved clip in 1 click!
- **Instant Zero-Reencode MP4 Muxer + Discord Shrink + Snip:** Pure-Rust fragmented MP4 (`mp4`) muxer, instant keyframe trim (`<200 ms`), hardware-transcoded Discord 10 MB/50 MB shrink, and HDR-to-SDR tone-mapped PNG/WebP snips.
- **Clean-Room Offline Hook (`exo_cap_hook.dll`, optional):** Tiny clean-room MIT DX11/DX12/Vulkan present-copy DLL used **strictly** when `AntiCheatCheck.Kind.None` AND WGC returns blank on a legacy exclusive-fullscreen game.

### Out of Scope
- Copying any code from GPL-2.0 OBS Studio `win-capture`.
- Putting capture imports into `ExoLauncher.exe` or `exo-ui.exe` (must remain in `exo-capture.exe`).

---

## 2. Native Rust Crate Layout

```text
crates/exo-capture/             # Separate binary exo-capture.exe (preserves capture isolation firewall!)
├── Cargo.toml                  # windows (WGC, Direct3D11, Direct3D12, DXGI, MediaFoundation, Audio), parking_lot
├── src/
│   ├── main.rs                 # Named-pipe / exo-wire control server (start/stop/save_replay/snip/shrink/go_live)
│   ├── wgc.rs                  # Zero-copy WGC GraphicsCaptureSession -> ID3D11Texture2D / shared DX12 handle
│   ├── dxgi_dup.rs             # DXGI OutputDuplication fallback (full display + HDR metadata)
│   ├── colorspace.rs           # GPU D3D11/DX12 VideoProcessor / Compute shader: RGBA/HDR10 -> NV12/P010 + SDR tonemap
│   ├── encode/
│   │   ├── mod.rs              # Auto-selects AV1 > HEVC > H264 HW encoder (NVENC / AMF / QSV / MF MFT)
│   │   ├── nvenc.rs            # Direct nvEncodeAPI64.dll zero-copy D3D11/12 input + low-latency Go-Live mode
│   │   └── mf_mft.rs           # Universal Media Foundation async HW MFT (AMD/Intel/fallback)
│   ├── audio_loopback.rs       # Win11 per-process WASAPI loopback (Game + Mic + Discord isolated tracks)
│   ├── replay_ring.rs          # Lock-free GOP-aligned RAM ring buffer (0 disk writes until Save!)
│   ├── mux_mp4.rs              # Zero-copy pure-Rust multi-track MP4 muxer + instant keyframe trimmer
│   ├── highlights.rs           # Optional zero-memory-touch audio/optical peak auto-bookmark detector
│   └── go_live.rs              # Streams raw encoded frames over exo-wire binary pipe to Discord Go Live
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config Hardware Auto-Tuning:** Inspects GPU generation, monitor resolution/Hz, and HDR state → automatically picks AV1 (RTX 40+/RX 7000+/Arc) or HEVC 10-bit (HDR) or H.264 (universal compatibility), optimal VBR/CQP bitrate, and sets DXGI GPU thread priority `-7` (never starves game frames!).
2. **Smart Game Session Lifecycle:** Starts replay buffer silently only when a game enters foreground (`LaunchOrchestrator` signal), flushes RAM ring buffer 30 s after exit → **0 MB RAM / 0% GPU when not gaming**.
3. **1-Click Discord Share:** Automatically picks the highest-quality codec & bitrate to fit under Discord's exact upload tier (10 MB free / 50 MB Basic / 500 MB Nitro) in a single hardware pass.
4. **Automatic Audio Separation:** Automatically binds Track 1 to the running game's PID tree, Track 2 to the user's mic, and Track 3 to Discord/Music — zero manual OBS audio-routing scenes!

---

## 4. Measurement Plan

Run against a synthetic 1440p 120 FPS DX12 animated test window on this RTX 3070 PC (zero touches to installed Exo or real games):

| Metric | Baseline (OBS / Current `.NET` `ExoCapture`) | Target (`exo-capture-rs`) | How Measured |
| --- | --- | --- | --- |
| **Game FPS Impact (1440p60 Replay Active)** | OBS ~2–4% GPU; Medal ~4–8% | **<1.5% GPU / <1.0% CPU** | Synthetic DX12 benchmark FPS with replay OFF vs ON |
| **Zero-Copy Verification** | 0 CPU texture Staging copies | **0 `ID3D11DeviceContext::Map` on capture frames** | GPU instrumentation counter + code gate |
| **SSD Disk Writes During 5-Min Replay** | Disk-buffered tools: ~1.5 GB/5 min | **0 bytes** until Save pressed | ETW / `GetProcessIoCounters` |
| **Binary Footprint** | `4.56 MB` `.NET` + `6` GPL-2.0 OBS binaries | **<2.5 MB** single MIT binary, **0** OBS binaries | File size & license audit |
| **Replay Save Latency (60 s clip)** | ~1–3 s | **<250 ms** (RAM ring → MP4 mux flush) | QPC timer on Save trigger |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-capture --locked -- -D warnings`, `cargo test -p exo-capture --locked`.
2. **Banned-Symbols Isolation Gate:** `ExoLauncher.Tests/BannedNativeImportsTests` proves capture APIs exist ONLY in `exo-capture.exe`.
3. **MP4 Integrity Gate:** Recorded synthetic 10 s 3-track (video + game audio + mic) clip parses cleanly with valid timestamps, sync, and keyframes (`mp4` validator test).
4. **GPL-Free Gate:** `capture/third_party/obs/` deleted; `cargo-deny` confirms 0 GPL dependencies.
5. **Anti-Cheat Policy Gate:** Optional legacy hook refuses execution on any game not verified `AntiCheatCheck.Kind.None`.

---

## 6. Safety Rules & Hard Stops

- **Capture Firewall (`AGENTS.md`):** Screen capture (`WGC`, `DXGI Duplication`, `BitBlt`, `PrintWindow`) MUST stay exclusively in `exo-capture.exe`, never in `ExoLauncher.exe` or `exo-ui.exe`.
- **Privacy Guard:** Never capture when the user has not explicitly enabled Replay/Record/Snip; pause capture automatically on UAC/secure desktops and sensitive auth windows (`StoreAuthWindow`, Discord login).
- **Memory Cap Enforcement:** Hard-cap RAM ring buffer (`min(user_mb, 15% of system RAM)`) so replay can never cause OOM pressure in games.
- **No Anti-Cheat Hooks:** Default WGC/DXGI Duplication works on all anti-cheat games without ever touching the game process.
