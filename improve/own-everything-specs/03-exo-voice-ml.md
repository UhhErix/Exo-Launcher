# Spec 03 — Exo Voice ML (`exo-voice-ml`): Unified ONNX Runtime DeepFilterNet3 + TitaNet Native Engine

- **Inventory Rank:** #03 · **Verdict:** `BUILD-OWN-NOW` · **Effort:** `M`
- **Replaces:** `exo-deepfilter.dll` (**50.64 MB**, embeds Rust `tract` + 37.21 MB FP32 weights, **1,932 ms** cold load, **+79.9 MB RSS**) AND `sherpa-onnx-c-api.dll` (**4.55 MB**, wraps `onnxruntime.dll` solely for an 80-bin mel filterbank + single-threaded TitaNet) (`voice/deepfilter/`, `Services/DiscordClient/Voice/SpeakerModel.cs`, `NoiseSuppressor.cs`)
- **Anti-Cheat Class:** `AC-SAFE` (local Discord voice microphone DSP only)
- **License:** MIT (Exo Rust DSP + `ort` / direct `onnxruntime.dll` C API); DeepFilterNet3 weights (MIT/Apache-2.0); TitaNet-small weights (CC-BY-4.0)

---

## 1. Scope

### What It Solves
Real measurements on this PC (`research/deps-best-or-own`) proved massive waste in Exo's voice ML stack:
- `exo-deepfilter.dll` is **50.64 MB** (13.3% of Exo's entire 380.02 MB install!) because it statically compiles a **second** ML runtime (`tract`, 13.16 MB `.text`) and embeds 37.21 MB of uncompressed FP32 ONNX weights directly into `.rdata`, costing **1,932 ms** cold load and **+79.9 MB RSS** — even though `onnxruntime.dll` (16.45 MB) is *already shipped* in the exact same folder!
- `sherpa-onnx-c-api.dll` (**4.55 MB**) exists solely to compute an 80-bin Kaldi/NeMo mel filterbank and call `onnxruntime.dll` with hardcoded `num_threads=1` (`52.85 ms` mean / `97.83 ms` p95 per embedding).

### In Scope
- Single pure-Rust crate `exo-voice-ml` (compiled as a tiny `<800 KB` DLL for the C# bridge today and linked directly into Rust `exo-discord` in Phase 4) driving the **single** already-shipped `onnxruntime.dll` via its C API (`OrtApi`):
  1. **DeepFilterNet3 Pipeline:** Pure-Rust 48 kHz streaming STFT/ISTFT (`realfft` / `rustfft`), 32-band ERB filterbank, complex DF-order-5 multi-frame filtering, feeding **FP16 / dynamic-INT8 quantized** DeepFilterNet3 ONNX models (`enc.onnx`, `erb_dec.onnx`, `df_dec.onnx` — ~5–8 MB total on disk, downloaded/cached on first voice join or bundled compressed).
  2. **TitaNet-small Only-My-Voice Pipeline:** Pure-Rust 16 kHz resampling + 80-bin log-mel spectrogram + CMVN normalization feeding `titanet_small.onnx` with `intra_op_num_threads = 2` (and optional INT8 quantization / DirectML EP toggle).
  3. **Smart Voice-Activity & Adaptive Gate:** Lightweight energy + neural ERB SNR estimator driving automatic noise-floor calibration and speech-preserving gain smoothing.
- Deletes `exo-deepfilter.dll` (50.64 MB), `sherpa-onnx-c-api.dll` (4.55 MB), `org.k2fsa.sherpa.onnx.runtime.win-x64` NuGet, and `voice/deepfilter/` `tract` vendoring.

### Out of Scope
- Replacing `onnxruntime.dll` itself (that is deferred to Wave 3 Row #37 *only* if custom AVX2 Rust kernels beat ORT latency).
- Changing Discord voice network transport, Opus, or DAVE E2EE.

---

## 2. Native Rust Crate Layout

```text
crates/exo-voice-ml/
├── Cargo.toml                  # rustfft, realfft, rubato (or minimal polyphase resampler), libloading (onnxruntime.dll)
├── src/
│   ├── lib.rs                  # Public Rust API + thin C ABI exports for Phase 0-3 .NET host interop
│   ├── ort_ffi.rs              # Direct zero-overhead OrtApi C bindings (dynamic load of onnxruntime.dll)
│   ├── stft.rs                 # 48 kHz 960-sample Vorbis-window STFT/ISTFT (10 ms hop, lock-free ring)
│   ├── erb.rs                  # 32-band ERB + 96-bin complex DF feature extractor & deep-filter synthesis
│   ├── deepfilter.rs           # DeepFilterNet3 ORT session manager (FP16/INT8 weights, state tensors, attenuation limit)
│   ├── mel.rs                  # 16 kHz 80-bin NeMo-compatible log-mel spectrogram (replaces sherpa-onnx!)
│   ├── titanet.rs              # TitaNet-small ORT session (num_threads=2, L2-normalized cosine similarity)
│   └── auto_calibrate.rs       # Passive room noise-floor estimator & 15 s zero-click voiceprint enrollment
```

---

## 3. Automation & Intelligence Design

1. **Zero-Config Adaptive Suppression:** Continuously measures ambient room noise (keyboard clicks, PC fans, AC hum) during non-speech intervals and automatically tunes DeepFilterNet3's `atten_lim_db` (`12 dB` in a quiet room for studio warmth → `100 dB` during heavy mechanical keyboard / fan load) so the user's voice never sounds robotic.
2. **Passive "Only My Voice" Enrollment:** Instead of forcing users through a manual voice-recording wizard, silently collects high-SNR solo speech segments during the first 15 s of a call (with opt-in toggle) and refines the speaker centroid over time.
3. **Zero-Idle-RAM Lifecycle:** ORT sessions load lazily on first voice-channel join and unload cleanly 60 s after leaving voice, keeping idle launcher RAM at **0 MB** for voice ML.
4. **Automatic CPU Thread Selection:** Sets `intra_op_num_threads = 2` on ≥4-core CPUs (proven optimal in `deps-best-or-own`) and pins inference thread priority to `THREAD_PRIORITY_ABOVE_NORMAL` without touching Core 0.

---

## 4. Measurement Plan

Measured on this Ryzen 5 5600X PC against deterministic 48 kHz noisy speech WAV fixtures (clean speech + keyboard + fan + background talker, using DNS-Challenge/synthetic mixes):

| Metric | Baseline (Measured on this PC) | Target (`exo-voice-ml`) | How Measured |
| --- | --- | --- | --- |
| **Bundled DLL Disk Footprint** | `50.64 MB` (`exo-deepfilter`) + `4.55 MB` (`sherpa-onnx`) = **55.19 MB** | **<1.0 MB** DLL + ~6 MB quantized DF3 model (**-48 to -55 MB**) | Exact file byte count |
| **DeepFilter Cold Load Time** | **1,932 ms** (`tract` graph compile) | **<200 ms** (ORT pre-optimized `.ort`/ONNX) | QPC timer from init to first frame |
| **DeepFilter Active RSS** | **+79.9 MB** (`tract`) | **<22 MB** delta RSS | `GetProcessMemoryInfo` before/after |
| **DeepFilter Per-Frame Inference (10 ms hop)** | ~1.5–2.5 ms | **<1.2 ms** mean, **<2.5 ms** p99 (RTF <0.12) | 3,000-frame benchmark loop |
| **TitaNet Embedding Latency** | **52.85 ms** mean / **97.83 ms** p95 (`num_threads=1`) | **≤31.7 ms** mean / **≤46 ms** p95 (already proven at `num_threads=2`!) | 200-utterance timing sweep |
| **Speech Quality Preservation (PESQ / Cosine Sim)** | FP32 DeepFilterNet3 & sherpa-onnx TitaNet reference | **≤0.05 PESQ delta** vs FP32; **>0.995 cosine similarity** vs sherpa-onnx mel frontend | Automated reference WAV diff suite |

---

## 5. Verification Gates

1. `cargo fmt --all -- --check`, `cargo clippy -p exo-voice-ml --locked -- -D warnings`, `cargo test -p exo-voice-ml --locked`.
2. **Numerical Parity Gate:** Pure-Rust 80-bin mel frontend produces TitaNet embeddings with cosine similarity `>= 0.995` against `sherpa-onnx-c-api.dll` on 20 reference utterances.
3. **Real-Time Budget Gate:** Zero deadline misses (`<10 ms` frame time) over a 60-second simulated 48 kHz stream.
4. **Memory Leak Gate:** 10× init/process/destroy cycles show `<1 MB` net RSS growth.
5. **Release Build Gate:** `tools/Build-Release.ps1` verifies `exo-deepfilter.dll` and `sherpa-onnx-c-api.dll` are absent and `exo_voice_ml.dll` loads cleanly.

---

## 6. Safety Rules & Hard Stops

- **Privacy:** Voiceprints (`titanet` 192-D float embeddings) stay strictly local in DPAPI/user app data (`PlayerData.cs`) and are NEVER uploaded to telemetry or cloud.
- **Model Integrity:** Downloaded/cached ONNX weights must be verified against pinned SHA-256 hashes before loading into `onnxruntime.dll`.
- **Fail-Open Audio Safety:** If ORT initialization fails on an unsupported CPU, bypass noise suppression cleanly with 0 audio glitches and report one clear status line to `app.log`.
- **No Owner Exo Interference:** Test strictly via standalone CLI audio benchmark harness; never open or disrupt a live Discord call.
