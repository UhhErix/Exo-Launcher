EXO DATAFLOW: Zero-Alloc QoS Voice/Video Transport & Push-Driven Zero-Poll Music CDP Bridge (task-name: df-10-voice-qos-and-music-push)

SHELL SETUP, in every shell: `Remove-Item Env:GITHUB_TOKEN -ErrorAction SilentlyContinue`. Never print or commit tokens. Read apps/launcher/AGENTS.md first.

## Context

Two high-frequency real-time dataflows run continuously alongside games: (1) Discord voice/video UDP transport, and (2) Exo's Music room bridge to Spotify / YouTube Music / Apple Music. Eliminating per-packet crypto allocations, adding router-level DSCP QoS tagging, killing 33 Hz/50 Hz polling timers, and replacing Music's CDP long-poll loops with push bindings cuts background CPU/RAM jitter during competitive gameplay.

## Today

- **Voice/Video (`ExoLauncher/Services/DiscordClient/Voice/`):**
  - `VoiceTransport.cs:336-341` (`XChaChaFor`) creates and disposes a brand-new `ChaCha20Poly1305` CNG handle + `byte[32]` + `byte[12]` on **every single RTP packet** whenever `aead_xchacha20_poly1305_rtpsize` is negotiated!
  - `VoiceTransport.cs:110, 176, 223` uses synchronous blocking `_udp.Client.Send(packet)` with **no DSCP QoS marking** (`qwave.dll` / `QOS2`).
  - `DiscordSession.Settings.cs:298` runs a **30 ms (`33 Hz`)** polling timer (`KeyTick`) during calls instead of raw keyboard input events; `VoiceConnection.cs:350` runs a **20 ms (`50 Hz`)** `PollVideo` timer.
- **Music (`ExoLauncher/Services/Music*.cs`):**
  - `MusicNowPlaying.cs` and `MusicAppVolume.cs` keep long-running CDP `Runtime.evaluate` loops (5 s / 20 s windows).
  - `MusicAppVolume.cs:86` re-transmits the **entire multi-KB JS wrapper source string (`_page`)** on every single `ReadAsync`/`WriteAsync`/`WatchAsync` evaluation!
  - `app.log` on this PC measured Apple Music `domContentLoadedMs=1500` and hidden WebView2 consumes 150–350 MB RAM.

## 1. Benchmark first, with real numbers

- **Voice/Video bench:** Replay 10,000 synthetic RTP packets (voice 50 pkt/s + 1440p60 video 800 pkt/s) through `VoiceTransport` in both AES-GCM and XChaCha20 modes, measuring GC allocations/sec (via `GC.GetAllocatedBytesForCurrentThread`), µs/packet, and CPU %. Verify DSCP EF (46) / AF41 (34) packet tagging via loopback/WinDivert or `QOSQueryFlow`. Measure call-idle timer wakeups/sec before vs after `WM_INPUT` PTT hook.
- **Music bench:** Measure CDP `Runtime.evaluate` calls/min, bytes sent over DevTools protocol/min, and volume/track-change push latency over 5 minutes of active playback (long-poll vs `Runtime.addBinding` push).
- **Targets:** Voice/video XChaCha20 allocs **→ 0 B/packet** (>10× faster encryption), call keyboard wakeups **33 Hz → 0 Hz**, DSCP EF/AF41 active on Windows 11 (non-admin), Music CDP evaluations while playing **-95%**, track/volume event latency **<5 ms**, next-track art & LRCLIB lyrics latency **→ 0 ms** (prefetched at 80% track progress).

## 2. Architecture & implementation

1. **Zero-Alloc AEAD + DSCP QoS Voice/Video Transport (`VoiceTransport.cs`, `VoiceConnection.cs`):**
   - Replace per-packet `new ChaCha20Poly1305(HChaCha20(...))` in `VoiceTransport.cs:336` with a zero-allocation `Span<byte>` pure-C# / SIMD-accelerated RFC 8439 ChaCha20-Poly1305 + HChaCha20 (or cached subkey span), eliminating 100% of per-packet CNG handle creates & heap arrays!
   - Attach Windows `qwave.dll` (`QOSCreateHandle` + `QOSAddSocketToFlow` with `QOSTrafficTypeVoice` for audio / `QOSTrafficTypeAudioVideo` for video; zero admin required, graceful no-op if unavailable) so home/gaming routers prioritize Discord voice packets ahead of background downloads & game bursts!
   - Tune Opus in-band FEC (`OPUS_SET_INBAND_FEC(1)`, `OPUS_SET_PACKET_LOSS_PERC` dynamically fed from RTCP Receiver Report loss %) for clear voice under Wi-Fi loss.
   - Replace `DiscordSession.Settings.cs:298` 30 ms `KeyTick` timer with `WM_INPUT` (`RIDEV_INPUTSINK` on a message-only window, active ONLY while in a call with PTT/keybinds bound — matching `PRIVACY.md` strictly!) for **0 Hz idle wakeups** and <1 ms PTT actuation!
2. **Push-Driven CDP (`Runtime.addBinding`) + Predictive Music Prefetch (`MusicNowPlaying.cs`, `MusicAppVolume.cs`, `MusicEngineService.cs`):**
   - Inject the unified music observer script ONCE per navigation via `Page.addScriptToEvaluateOnNewDocument` and register `Runtime.addBinding("__exoMusicEvent")`.
   - Listen to `DevToolsProtocolEventReceived("Runtime.bindingCalled")` on the host: track changes, play/pause, seek, and volume changes push immediately from the page in a single tiny JSON message — **zero 5 s / 20 s long-poll loops**, **zero re-transmitted `_page` JS strings** (`MusicAppVolume.cs:86` fixed!).
   - Keep a lightweight 60 s heartbeat safety check only while playing (0 calls while paused/hidden).
   - When track progress crosses 80% and the queue has a next track, pre-warm the next track's cover image and LRCLIB timed lyrics in the background so track transitions paint instantaneously!

## 3. Gates & verification

- `dotnet build ExoLauncher\ExoLauncher.csproj -c Release -p:Platform=x64` & `dotnet test --project ExoLauncher.Tests -c Release -p:Platform=x64` (add `VoiceTransportZeroAllocTests`, RFC 8439 XChaCha20-Poly1305 KAT vectors, `QosFlowTests`, `MusicPushBindingTests`).
- Verify `BannedNativeImportsTests` and `PRIVACY.md` compliance (`WM_INPUT` active only during active calls with keybinds, never logging keys).

## 4. Deliver

- Branch `lab/df-10-voice-qos-and-music-push` from `origin/main`.
- `improve/df-10-voice-qos-and-music-push-report.md` with per-packet alloc/CPU benchmarks, DSCP verification, call wakeup rate, and Music CDP call-count reductions.
- Commit messages end with "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>".

When done: `orca orchestration send --type worker_done --to run:run_922f669e55b1 --task-id <task id> --dispatch-id <dispatch id> --outcome succeeded --subject "df-10 voice QoS & zero-poll music CDP" --body "<XChaCha20 alloc/CPU win, DSCP & wakeup result, Music CDP reduction>"`. Effort: high.
