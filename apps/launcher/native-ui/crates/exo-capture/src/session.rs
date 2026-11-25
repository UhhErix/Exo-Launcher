use std::fs::File;
use std::io::BufWriter;
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_B8G8R8A8_UNORM;
use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};

use crate::audio_loopback::{WasapiTrackCapture, build_three_track_plan};
use crate::colorspace::GpuColorConverter;
use crate::dxgi_dup::DxgiDuplicator;
use crate::encode::mf_mft::{MfHardwareEncoder, probe_hardware_encoders};
use crate::encode::{EncoderConfig, HardwareVideoEncoder, select_best_codec};
use crate::gpu::GpuDevice;
use crate::mux_mp4::{
    AudioTrackConfig, VideoCodec, VideoTrackConfig, annex_b_to_length_prefixed,
    build_avcc_from_sps_pps, extract_h264_sps_pps, mux_packets_to_fmp4,
};
use crate::replay_ring::{EncodedPacket, ReplayRingBuffer, TrackKind};
use crate::wgc::{WgcCapture, WgcTarget};

#[derive(Debug, Clone)]
pub struct SessionConfig {
    pub target: WgcTarget,
    pub fps: u32,
    pub bitrate_bps: u32,
    pub cursor: bool,
    pub hdr10: bool,
    pub preferred_codec: Option<VideoCodec>,
    pub game_pid: Option<u32>,
    pub discord_pid: Option<u32>,
    pub include_mic: bool,
    pub include_voice: bool,
}

#[derive(Debug, Clone)]
pub struct SessionStats {
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub codec: VideoCodec,
    pub encoder_name: String,
    pub source_kind: &'static str,
    pub frames_captured: u64,
    pub frames_encoded: u64,
    pub keyframes_encoded: u64,
}

#[derive(Debug, Clone)]
pub struct SaveReplayResult {
    pub bytes_written: u64,
    pub video_packets: usize,
    pub duration_ms: u64,
    pub save_latency_ms: f64,
}

struct SharedTrackMeta {
    width: u32,
    height: u32,
    fps: u32,
    codec: VideoCodec,
    hdr10: bool,
    encoder_name: String,
    source_kind: &'static str,
    codec_config: Vec<u8>,
}

pub struct CaptureSession {
    stop: Arc<AtomicBool>,
    ring: Arc<ReplayRingBuffer>,
    meta: Arc<Mutex<SharedTrackMeta>>,
    frames_captured: Arc<AtomicU64>,
    frames_encoded: Arc<AtomicU64>,
    keyframes_encoded: Arc<AtomicU64>,
    audio_captures: Vec<WasapiTrackCapture>,
    worker: Option<JoinHandle<()>>,
}

impl CaptureSession {
    pub fn start(
        gpu: Arc<GpuDevice>,
        ring: Arc<ReplayRingBuffer>,
        cfg: SessionConfig,
    ) -> Result<Self, String> {
        let caps = probe_hardware_encoders();
        let codec = cfg
            .preferred_codec
            .unwrap_or_else(|| select_best_codec(&caps, cfg.hdr10));

        let stop = Arc::new(AtomicBool::new(false));
        let frames_captured = Arc::new(AtomicU64::new(0));
        let frames_encoded = Arc::new(AtomicU64::new(0));
        let keyframes_encoded = Arc::new(AtomicU64::new(0));

        let meta = Arc::new(Mutex::new(SharedTrackMeta {
            width: 1920,
            height: 1080,
            fps: cfg.fps.clamp(15, 120),
            codec,
            hdr10: cfg.hdr10,
            encoder_name: caps.preferred_backend.clone(),
            source_kind: "wgc",
            codec_config: Vec::new(),
        }));

        let (audio_tx, audio_rx) = mpsc::channel::<EncodedPacket>();
        let mut audio_captures = Vec::new();
        for spec in build_three_track_plan(
            cfg.game_pid,
            cfg.discord_pid,
            cfg.include_mic,
            cfg.include_voice,
        ) {
            if let Ok(cap) = WasapiTrackCapture::start(spec, audio_tx.clone()) {
                audio_captures.push(cap);
            }
        }
        drop(audio_tx);

        let (init_tx, init_rx) = mpsc::sync_channel::<Result<(), String>>(1);
        let stop_w = Arc::clone(&stop);
        let ring_w = Arc::clone(&ring);
        let meta_w = Arc::clone(&meta);
        let cap_cnt = Arc::clone(&frames_captured);
        let enc_cnt = Arc::clone(&frames_encoded);
        let key_cnt = Arc::clone(&keyframes_encoded);

        let worker = thread::Builder::new()
            .name("exo-capture-pipeline".to_string())
            .spawn(move || {
                let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
                run_capture_pipeline(
                    gpu, ring_w, meta_w, cfg, codec, stop_w, cap_cnt, enc_cnt, key_cnt, audio_rx,
                    init_tx,
                );
                unsafe { CoUninitialize() };
            })
            .map_err(|e| format!("spawn capture pipeline failed: {e}"))?;

        match init_rx.recv_timeout(Duration::from_secs(5)) {
            Ok(Ok(())) => Ok(Self {
                stop,
                ring,
                meta,
                frames_captured,
                frames_encoded,
                keyframes_encoded,
                audio_captures,
                worker: Some(worker),
            }),
            Ok(Err(e)) => {
                stop.store(true, Ordering::Release);
                let _ = worker.join();
                Err(e)
            }
            Err(_) => {
                stop.store(true, Ordering::Release);
                let _ = worker.join();
                Err("capture pipeline timed out during initialization".to_string())
            }
        }
    }

    pub fn stats(&self) -> SessionStats {
        let m = self.meta.lock().unwrap_or_else(|e| e.into_inner());
        SessionStats {
            width: m.width,
            height: m.height,
            fps: m.fps,
            codec: m.codec,
            encoder_name: m.encoder_name.clone(),
            source_kind: m.source_kind,
            frames_captured: self.frames_captured.load(Ordering::Relaxed),
            frames_encoded: self.frames_encoded.load(Ordering::Relaxed),
            keyframes_encoded: self.keyframes_encoded.load(Ordering::Relaxed),
        }
    }

    pub fn save_replay_to_file(
        &self,
        path: &Path,
        seconds: Option<u32>,
    ) -> Result<SaveReplayResult, String> {
        let t0 = Instant::now();
        let packets = self.ring.take_last_seconds(seconds);
        let video_count = packets.iter().filter(|p| p.track.is_video()).count();
        if video_count < 2 {
            return Err("Nothing to save yet".to_string());
        }
        let first_ts = packets
            .iter()
            .find(|p| p.track.is_video())
            .map(|p| p.timestamp_100ns)
            .unwrap_or(0);
        let last_ts = packets
            .iter()
            .rev()
            .find(|p| p.track.is_video())
            .map(|p| p.timestamp_100ns)
            .unwrap_or(first_ts);
        let duration_ms = last_ts.saturating_sub(first_ts).max(0) as u64 / 10_000;

        let (vcfg, acfgs) = {
            let m = self.meta.lock().unwrap_or_else(|e| e.into_inner());
            let vcfg = VideoTrackConfig {
                codec: m.codec,
                width: m.width as u16,
                height: m.height as u16,
                timescale: 90_000,
                fps: m.fps,
                hdr10: m.hdr10,
                codec_config: m.codec_config.clone(),
            };
            let mut acfgs = Vec::new();
            for (kind, name) in [
                (TrackKind::AudioGame, "Game"),
                (TrackKind::AudioMic, "Microphone"),
                (TrackKind::AudioVoice, "Voice"),
            ] {
                if packets.iter().any(|p| p.track == kind) {
                    acfgs.push(AudioTrackConfig::aac_lc_48khz_stereo(kind, name));
                }
            }
            (vcfg, acfgs)
        };

        let mp4_bytes = mux_packets_to_fmp4(vcfg, acfgs, &packets);
        if let Some(parent) = path.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let file = File::create(path).map_err(|e| format!("create clip file failed: {e}"))?;
        let mut writer = BufWriter::new(file);
        std::io::Write::write_all(&mut writer, &mp4_bytes)
            .map_err(|e| format!("write clip file failed: {e}"))?;
        std::io::Write::flush(&mut writer).map_err(|e| format!("flush clip file failed: {e}"))?;

        Ok(SaveReplayResult {
            bytes_written: mp4_bytes.len() as u64,
            video_packets: video_count,
            duration_ms,
            save_latency_ms: t0.elapsed().as_secs_f64() * 1000.0,
        })
    }

    pub fn stop(mut self) {
        self.stop_inner();
    }

    fn stop_inner(&mut self) {
        self.stop.store(true, Ordering::Release);
        for cap in self.audio_captures.drain(..) {
            cap.stop();
        }
        if let Some(w) = self.worker.take() {
            let _ = w.join();
        }
    }
}

impl Drop for CaptureSession {
    fn drop(&mut self) {
        self.stop_inner();
    }
}

enum ActiveSource {
    Wgc(WgcCapture),
    Dxgi(DxgiDuplicator),
}

fn qpc_100ns() -> i64 {
    let mut counter = 0i64;
    let mut freq = 1i64;
    unsafe {
        let _ = QueryPerformanceCounter(&mut counter);
        let _ = QueryPerformanceFrequency(&mut freq);
    }
    ((counter as i128 * 10_000_000) / (freq.max(1) as i128)) as i64
}

#[allow(clippy::too_many_arguments)]
fn run_capture_pipeline(
    gpu: Arc<GpuDevice>,
    ring: Arc<ReplayRingBuffer>,
    meta: Arc<Mutex<SharedTrackMeta>>,
    cfg: SessionConfig,
    codec: VideoCodec,
    stop: Arc<AtomicBool>,
    frames_captured: Arc<AtomicU64>,
    frames_encoded: Arc<AtomicU64>,
    keyframes_encoded: Arc<AtomicU64>,
    audio_rx: mpsc::Receiver<EncodedPacket>,
    init_tx: mpsc::SyncSender<Result<(), String>>,
) {
    let (mut source, init_w, init_h, source_kind) =
        match WgcCapture::open(Arc::clone(&gpu), cfg.target, cfg.cursor, cfg.hdr10) {
            Ok(wgc) => {
                let w = wgc.width();
                let h = wgc.height();
                (ActiveSource::Wgc(wgc), w, h, "wgc")
            }
            Err(wgc_err) => match DxgiDuplicator::new(Arc::clone(&gpu), 0) {
                Ok(dup) => {
                    let w = dup.width;
                    let h = dup.height;
                    (ActiveSource::Dxgi(dup), w, h, "duplication")
                }
                Err(dup_err) => {
                    let _ = init_tx.send(Err(format!(
                        "WGC failed ({wgc_err}); DXGI duplication fallback failed ({dup_err})"
                    )));
                    return;
                }
            },
        };

    let enc_w = (init_w.max(64)) & !1;
    let enc_h = (init_h.max(64)) & !1;
    let fps = cfg.fps.clamp(15, 120);

    let mut converter = match GpuColorConverter::new(
        Arc::clone(&gpu),
        init_w,
        init_h,
        DXGI_FORMAT_B8G8R8A8_UNORM,
        enc_w,
        enc_h,
        cfg.hdr10,
    ) {
        Ok(c) => c,
        Err(e) => {
            let _ = init_tx.send(Err(e));
            return;
        }
    };

    let enc_cfg = EncoderConfig {
        codec,
        width: enc_w,
        height: enc_h,
        fps,
        bitrate_bps: cfg.bitrate_bps.clamp(1_000_000, 80_000_000),
        gop_frames: fps,
        hdr10: cfg.hdr10,
        low_latency: false,
    };

    let mut encoder = match MfHardwareEncoder::open(Arc::clone(&gpu), enc_cfg) {
        Ok(e) => e,
        Err(_) if codec != VideoCodec::H264 => {
            let mut fallback = enc_cfg;
            fallback.codec = VideoCodec::H264;
            fallback.hdr10 = false;
            match MfHardwareEncoder::open(Arc::clone(&gpu), fallback) {
                Ok(e) => e,
                Err(e2) => {
                    let _ = init_tx.send(Err(e2));
                    return;
                }
            }
        }
        Err(e) => {
            let _ = init_tx.send(Err(e));
            return;
        }
    };

    {
        let mut m = meta.lock().unwrap_or_else(|e| e.into_inner());
        m.width = enc_w;
        m.height = enc_h;
        m.fps = fps;
        m.codec = encoder.codec();
        m.encoder_name = encoder.encoder_name().to_string();
        m.source_kind = source_kind;
    }

    let _ = init_tx.send(Ok(()));

    let frame_period = Duration::from_secs_f64(1.0 / (fps as f64));
    let frame_dur_100ns = 10_000_000i64 / (fps as i64);
    let mut next_tick = Instant::now();
    let mut last_tex = None;

    while !stop.load(Ordering::Acquire) {
        while let Ok(apkt) = audio_rx.try_recv() {
            ring.push(apkt);
        }

        let grabbed = match &mut source {
            ActiveSource::Wgc(wgc) => {
                if wgc.is_closed() {
                    break;
                }
                match wgc.try_next_frame() {
                    Ok(Some(f)) => Some(f.texture),
                    Ok(None) => None,
                    Err(_) => None,
                }
            }
            ActiveSource::Dxgi(dup) => dup.acquire_frame(0).ok().flatten(),
        };

        if let Some(tex) = grabbed {
            frames_captured.fetch_add(1, Ordering::Relaxed);
            last_tex = Some(tex);
        }

        if let Some(ref tex) = last_tex
            && let Ok(nv12_tex) = converter.convert(tex)
        {
            let ts = qpc_100ns();
            if let Ok(outputs) = encoder.encode_gpu_surface(&nv12_tex, ts, frame_dur_100ns, false) {
                for out in outputs {
                    if out.annex_b.is_empty() {
                        continue;
                    }
                    if out.is_keyframe {
                        keyframes_encoded.fetch_add(1, Ordering::Relaxed);
                        if encoder.codec() == VideoCodec::H264
                            && let Some((sps, pps)) = extract_h264_sps_pps(&out.annex_b)
                        {
                            let mut m = meta.lock().unwrap_or_else(|e| e.into_inner());
                            if m.codec_config.is_empty() {
                                m.codec_config = build_avcc_from_sps_pps(&sps, &pps);
                            }
                        }
                    }
                    frames_encoded.fetch_add(1, Ordering::Relaxed);
                    let mp4_sample = if encoder.codec() == VideoCodec::H264 {
                        annex_b_to_length_prefixed(&out.annex_b)
                    } else {
                        out.annex_b
                    };
                    ring.push(EncodedPacket {
                        track: TrackKind::Video,
                        data: mp4_sample,
                        timestamp_100ns: out.timestamp_100ns,
                        duration_100ns: out.duration_100ns.max(frame_dur_100ns),
                        is_keyframe: out.is_keyframe,
                    });
                }
            }
        }

        next_tick += frame_period;
        let now = Instant::now();
        if next_tick > now {
            thread::sleep(next_tick - now);
        } else {
            next_tick = now;
        }
    }

    if let Ok(drained) = encoder.drain() {
        for out in drained {
            if out.annex_b.is_empty() {
                continue;
            }
            frames_encoded.fetch_add(1, Ordering::Relaxed);
            if out.is_keyframe {
                keyframes_encoded.fetch_add(1, Ordering::Relaxed);
            }
            let mp4_sample = if encoder.codec() == VideoCodec::H264 {
                annex_b_to_length_prefixed(&out.annex_b)
            } else {
                out.annex_b
            };
            ring.push(EncodedPacket {
                track: TrackKind::Video,
                data: mp4_sample,
                timestamp_100ns: out.timestamp_100ns,
                duration_100ns: out.duration_100ns.max(frame_dur_100ns),
                is_keyframe: out.is_keyframe,
            });
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::mux_mp4::validate_mp4;
    use windows::Win32::Graphics::Direct3D11::ID3D11RenderTargetView;

    #[test]
    fn gpu_zero_copy_nv12_encode_ring_buffer_and_fmp4_end_to_end() {
        let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
        let Ok(gpu) = GpuDevice::create(None) else {
            unsafe { CoUninitialize() };
            return;
        };
        let gpu = Arc::new(gpu);
        let caps = probe_hardware_encoders();
        if !caps.h264_hw {
            unsafe { CoUninitialize() };
            return;
        }

        let src_tex = gpu
            .create_default_texture(640, 360, DXGI_FORMAT_B8G8R8A8_UNORM)
            .expect("src texture");
        let mut rtv: Option<ID3D11RenderTargetView> = None;
        unsafe {
            gpu.device
                .CreateRenderTargetView(&src_tex, None, Some(&mut rtv))
                .expect("rtv");
        }
        let rtv = rtv.expect("rtv present");

        let mut converter = GpuColorConverter::new(
            Arc::clone(&gpu),
            640,
            360,
            DXGI_FORMAT_B8G8R8A8_UNORM,
            640,
            360,
            false,
        )
        .expect("converter");

        let mut encoder = MfHardwareEncoder::open(
            Arc::clone(&gpu),
            EncoderConfig {
                codec: VideoCodec::H264,
                width: 640,
                height: 360,
                fps: 30,
                bitrate_bps: 4_000_000,
                gop_frames: 30,
                hdr10: false,
                low_latency: false,
            },
        )
        .expect("hw encoder");

        let ring = ReplayRingBuffer::new(30, 64 * 1024 * 1024, 16 * 1024 * 1024 * 1024);
        let mut codec_config = Vec::new();
        let dur_100ns = 333_333i64;
        for i in 0..45i64 {
            let c = (i as f32) / 45.0;
            unsafe {
                gpu.context
                    .ClearRenderTargetView(&rtv, &[0.2 + 0.6 * c, 0.4, 0.8 - 0.5 * c, 1.0]);
            }
            let nv12 = converter.convert(&src_tex).expect("zero-copy nv12");
            let outs = encoder
                .encode_gpu_surface(&nv12, i * dur_100ns, dur_100ns, i == 0)
                .expect("hw encode");
            for out in outs {
                if out.is_keyframe
                    && codec_config.is_empty()
                    && let Some((sps, pps)) = extract_h264_sps_pps(&out.annex_b)
                {
                    codec_config = build_avcc_from_sps_pps(&sps, &pps);
                }
                ring.push(EncodedPacket {
                    track: TrackKind::Video,
                    data: annex_b_to_length_prefixed(&out.annex_b),
                    timestamp_100ns: out.timestamp_100ns,
                    duration_100ns: out.duration_100ns.max(dur_100ns),
                    is_keyframe: out.is_keyframe,
                });
            }
        }
        for out in encoder.drain().expect("drain") {
            if out.is_keyframe
                && codec_config.is_empty()
                && let Some((sps, pps)) = extract_h264_sps_pps(&out.annex_b)
            {
                codec_config = build_avcc_from_sps_pps(&sps, &pps);
            }
            ring.push(EncodedPacket {
                track: TrackKind::Video,
                data: annex_b_to_length_prefixed(&out.annex_b),
                timestamp_100ns: out.timestamp_100ns,
                duration_100ns: out.duration_100ns.max(dur_100ns),
                is_keyframe: out.is_keyframe,
            });
        }

        let pkts = ring.take_last_seconds(None);
        assert!(
            pkts.len() >= 10,
            "expected >=10 encoded video packets, got {}",
            pkts.len()
        );
        assert!(pkts[0].is_keyframe);
        let mp4 = mux_packets_to_fmp4(
            VideoTrackConfig {
                codec: VideoCodec::H264,
                width: 640,
                height: 360,
                timescale: 90_000,
                fps: 30,
                hdr10: false,
                codec_config,
            },
            Vec::new(),
            &pkts,
        );
        let report = validate_mp4(&mp4).expect("real hw-encoded fMP4 is valid");
        assert!(report.video_sample_count >= 10);
        assert!(report.video_keyframe_count >= 1);
        unsafe { CoUninitialize() };
    }
}
