use std::fs;
use std::io::{self, BufRead, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::{Value, json};
use windows::Win32::Foundation::POINT;
use windows::Win32::Graphics::Gdi::{MONITOR_DEFAULTTOPRIMARY, MonitorFromPoint};
use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};

use exo_capture::encode::mf_mft::{ensure_mf_started, probe_hardware_encoders};
use exo_capture::gpu::{GpuDevice, probe_codec_availability_guarded};
use exo_capture::mux_mp4::{trim_fmp4_keyframes, validate_mp4};
use exo_capture::policy::{
    enable_per_monitor_dpi_v2, suppress_os_error_dialogs, system_total_ram_bytes,
};
use exo_capture::protocol::{
    CaptureConfig, GameTarget, HostRequest, format_error, format_event, format_reply,
};
use exo_capture::replay_ring::ReplayRingBuffer;
use exo_capture::session::{CaptureSession, SessionConfig};
use exo_capture::wgc::WgcTarget;

struct HostWriter {
    out: Mutex<io::Stdout>,
}

impl HostWriter {
    fn new() -> Self {
        Self {
            out: Mutex::new(io::stdout()),
        }
    }

    fn send_line(&self, line: &str) {
        let mut guard = self.out.lock().unwrap_or_else(|e| e.into_inner());
        let _ = writeln!(guard, "{line}");
        let _ = guard.flush();
    }

    fn event(&self, name: &str, data: Value) {
        self.send_line(&format_event(name, data));
    }

    fn reply(&self, id: i64, result: Value) {
        self.send_line(&format_reply(id, result));
    }

    fn fail(&self, id: i64, reason: &str) {
        self.send_line(&format_error(id, reason));
    }
}

struct Daemon {
    config: CaptureConfig,
    game: Option<GameTarget>,
    gpu: Option<Arc<GpuDevice>>,
    ring: Arc<ReplayRingBuffer>,
    session: Option<CaptureSession>,
    last_error: Option<String>,
    gpu_error_logged: bool,
    av1: bool,
    hevc: bool,
    h264: bool,
    preferred_backend: String,
    total_ram: u64,
}

impl Daemon {
    fn new() -> Self {
        let total_ram = system_total_ram_bytes();
        let config = CaptureConfig::default();
        let ring = Arc::new(ReplayRingBuffer::new(
            config.replay_seconds,
            config.replay_max_bytes,
            total_ram,
        ));

        let mut last_error = None;
        let mut gpu_error_logged = false;
        let gpu = match GpuDevice::create(None) {
            Ok(g) => Some(Arc::new(g)),
            Err(e) => {
                eprintln!("{e}");
                last_error = Some(e);
                gpu_error_logged = true;
                None
            }
        };

        let caps = probe_hardware_encoders();
        let av1 = probe_codec_availability_guarded(
            || Ok(caps.av1_hw && gpu.is_some()),
            |l| eprintln!("{l}"),
            &mut last_error,
            &mut gpu_error_logged,
        );
        let hevc = probe_codec_availability_guarded(
            || Ok(caps.hevc_hw && gpu.is_some()),
            |l| eprintln!("{l}"),
            &mut last_error,
            &mut gpu_error_logged,
        );
        let h264 = caps.h264_hw && gpu.is_some();

        Self {
            config,
            game: None,
            gpu,
            ring,
            session: None,
            last_error,
            gpu_error_logged,
            av1,
            hevc,
            h264,
            preferred_backend: caps.preferred_backend,
            total_ram,
        }
    }

    fn state_json(&self) -> Value {
        let stats = self.session.as_ref().map(CaptureSession::stats);
        let buffering = self.session.is_some() && self.config.replay_mode != "off";
        let mut obj = json!({
            "engine": "exo-capture-rs",
            "replayMemoryBudget": true,
            "buffering": buffering,
            "recording": false,
            "callVoices": self.config.call_voices,
            "cleansClips": true,
            "recordingMs": 0,
            "bufferedMs": if buffering { self.ring.retained_ms() } else { 0 },
            "replayRetainedMs": if buffering { self.ring.retained_ms() } else { 0 },
            "bufferBytes": self.ring.bytes(),
            "bufferMaxBytes": self.ring.max_bytes(),
            "codec": stats.as_ref().map(|s| s.codec.display_name()),
            "encoder": stats.as_ref().map(|s| s.encoder_name.as_str()).unwrap_or(self.preferred_backend.as_str()),
            "av1": self.av1,
            "hevc": self.hevc,
            "h264": self.h264,
            "printScreenOk": true,
            "hotkeys": {
                "saveReplay": { "keys": "Ctrl+Alt+F10", "ok": true },
                "record": { "keys": "Ctrl+Alt+F9", "ok": true },
                "snip": { "keys": "Ctrl+Alt+F8", "ok": true },
                "screenshot": { "keys": "Ctrl+Alt+F7", "ok": true },
            }
        });
        if let Some(s) = stats {
            obj["width"] = json!(s.width);
            obj["height"] = json!(s.height);
            obj["fps"] = json!(s.fps);
            obj["target"] = json!(s.source_kind);
        }
        if let Some(ref err) = self.last_error {
            obj["error"] = json!(err);
        }
        obj
    }

    fn ensure_gpu(&mut self) -> Result<Arc<GpuDevice>, String> {
        if let Some(ref g) = self.gpu {
            return Ok(Arc::clone(g));
        }
        match GpuDevice::create(None) {
            Ok(g) => {
                let arc = Arc::new(g);
                self.gpu = Some(Arc::clone(&arc));
                self.last_error = None;
                self.gpu_error_logged = false;
                Ok(arc)
            }
            Err(e) => {
                self.last_error = Some(e.clone());
                if !self.gpu_error_logged {
                    self.gpu_error_logged = true;
                    eprintln!("{e}");
                }
                Err(e)
            }
        }
    }

    fn ensure_session_for_mode(&mut self) {
        let want = match self.config.replay_mode.as_str() {
            "always" => true,
            "games" => self.game.is_some(),
            _ => false,
        };
        if !want {
            if let Some(s) = self.session.take() {
                s.stop();
            }
            self.ring.clear();
            return;
        }
        if self.session.is_some() {
            return;
        }
        let Ok(gpu) = self.ensure_gpu() else {
            return;
        };
        let monitor = unsafe { MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY) };
        let cfg = SessionConfig {
            target: WgcTarget::Monitor(monitor.0 as isize),
            fps: self.config.max_fps,
            bitrate_bps: self.config.target_bitrate_bps(),
            cursor: self.config.cursor,
            hdr10: false,
            preferred_codec: None,
            game_pid: self.game.as_ref().and_then(|g| g.pid),
            discord_pid: None,
            include_mic: !self.config.mic.is_empty(),
            include_voice: self.config.call_voices,
        };
        match CaptureSession::start(gpu, Arc::clone(&self.ring), cfg) {
            Ok(sess) => {
                self.session = Some(sess);
                self.last_error = None;
            }
            Err(e) => {
                self.last_error = Some(e);
            }
        }
    }

    fn new_clip_path(&self, suffix: &str) -> PathBuf {
        let ts = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0);
        let prefix = self
            .game
            .as_ref()
            .map(|g| sanitize_filename(&g.title))
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| "Desktop".to_string());
        PathBuf::from(&self.config.folder).join(format!("{prefix}{suffix}-{ts}.mp4"))
    }

    fn handle(&mut self, req: &HostRequest, writer: &HostWriter) -> Result<Value, String> {
        match req.method.as_str() {
            "configure" => {
                self.config.apply_json(&req.params);
                self.ring.reconfigure(
                    self.config.replay_seconds,
                    self.config.replay_max_bytes,
                    self.total_ram,
                );
                self.ensure_session_for_mode();
                Ok(self.state_json())
            }
            "game" => {
                self.game = GameTarget::from_json(&req.params);
                if let Some(ref g) = self.game {
                    let dec = g.hook_decision();
                    eprintln!(
                        "Game target '{}' (antiCheat={:?}): hook allowed={} ({})",
                        g.title, g.anti_cheat_class, dec.allowed, dec.reason
                    );
                }
                self.ensure_session_for_mode();
                Ok(self.state_json())
            }
            "status" => Ok(self.state_json()),
            "replay.save" => {
                if self.config.replay_mode == "off" {
                    return Err("Instant replay is off".to_string());
                }
                let Some(ref sess) = self.session else {
                    return Err("Nothing to save yet".to_string());
                };
                let path = self.new_clip_path(" clip");
                let res = sess.save_replay_to_file(&path, Some(self.config.replay_seconds))?;
                writer.event(
                    "saved",
                    json!({
                        "path": path.to_string_lossy(),
                        "kind": "clip",
                        "game": self.game.as_ref().map(|g| g.id.as_str()).unwrap_or(""),
                        "durationMs": res.duration_ms,
                        "bytes": res.bytes_written,
                        "saveLatencyMs": res.save_latency_ms,
                    }),
                );
                Ok(self.state_json())
            }
            "trim" => {
                let src = req
                    .params
                    .get("path")
                    .and_then(Value::as_str)
                    .ok_or("missing path")?;
                let start_ms = req
                    .params
                    .get("startMs")
                    .and_then(Value::as_u64)
                    .unwrap_or(0);
                let end_ms = req
                    .params
                    .get("endMs")
                    .and_then(Value::as_u64)
                    .ok_or("missing endMs")?;
                let bytes = fs::read(src).map_err(|e| format!("read clip failed: {e}"))?;
                let trimmed = trim_fmp4_keyframes(&bytes, start_ms, end_ms)?;
                let dst = self.new_clip_path(" trim");
                if let Some(parent) = dst.parent() {
                    let _ = fs::create_dir_all(parent);
                }
                fs::write(&dst, &trimmed).map_err(|e| format!("write trim failed: {e}"))?;
                Ok(json!({ "path": dst.to_string_lossy(), "bytes": trimmed.len() }))
            }
            "probe" => {
                let src = req
                    .params
                    .get("path")
                    .and_then(Value::as_str)
                    .ok_or("missing path")?;
                let bytes = fs::read(src).map_err(|e| format!("read clip failed: {e}"))?;
                let rep = validate_mp4(&bytes)?;
                Ok(json!({
                    "trackCount": rep.track_count,
                    "fragments": rep.fragment_count,
                    "videoSamples": rep.video_sample_count,
                    "videoKeyframes": rep.video_keyframe_count,
                    "durationMs": rep.video_duration_ms,
                    "maxAvDriftMs": rep.max_av_drift_ms,
                }))
            }
            other => Err(format!("Unsupported request: {other}")),
        }
    }
}

fn sanitize_filename(name: &str) -> String {
    name.chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() || c == ' ' || c == '-' || c == '_' {
                c
            } else {
                '_'
            }
        })
        .collect::<String>()
        .trim()
        .to_string()
}

fn run_cli_encoders() -> i32 {
    let caps = probe_hardware_encoders();
    let gpu_name = GpuDevice::create(None)
        .map(|g| g.info.name)
        .unwrap_or_else(|e| format!("unavailable ({e})"));
    println!(
        "{}",
        json!({
            "gpu": gpu_name,
            "nvencApi": caps.nvenc_api_version,
            "av1Hw": caps.av1_hw,
            "hevcHw": caps.hevc_hw,
            "h264Hw": caps.h264_hw,
            "backend": caps.preferred_backend,
        })
    );
    0
}

fn run_cli_check(paths: &[String]) -> i32 {
    let mut exit = 0;
    for p in paths {
        match fs::read(Path::new(p))
            .map_err(|e| e.to_string())
            .and_then(|b| validate_mp4(&b))
        {
            Ok(rep) => println!(
                "OK {p}: tracks={} fragments={} videoSamples={} keyframes={} durationMs={} maxAvDriftMs={}",
                rep.track_count,
                rep.fragment_count,
                rep.video_sample_count,
                rep.video_keyframe_count,
                rep.video_duration_ms,
                rep.max_av_drift_ms
            ),
            Err(e) => {
                eprintln!("FAIL {p}: {e}");
                exit = 1;
            }
        }
    }
    exit
}

fn main() {
    suppress_os_error_dialogs();
    enable_per_monitor_dpi_v2();
    let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
    let _ = ensure_mf_started();

    let args: Vec<String> = std::env::args().skip(1).collect();
    if let Some(first) = args.first() {
        if first == "--encoders" {
            let code = run_cli_encoders();
            unsafe { CoUninitialize() };
            std::process::exit(code);
        }
        if first == "--check" {
            let code = run_cli_check(&args[1..]);
            unsafe { CoUninitialize() };
            std::process::exit(code);
        }
    }

    let writer = HostWriter::new();
    let mut daemon = Daemon::new();
    let ready = daemon.state_json();
    eprintln!(
        "exo-capture-rs ready on {:?}, backend={}",
        daemon.gpu.as_ref().map(|g| g.info.name.as_str()),
        daemon.preferred_backend
    );
    writer.event("ready", ready);

    let stdin = io::stdin();
    for line in stdin.lock().lines() {
        let Ok(raw) = line else {
            break;
        };
        if raw.trim().is_empty() {
            continue;
        }
        let Ok(req) = serde_json::from_str::<HostRequest>(&raw) else {
            continue;
        };
        if req.method == "shutdown" {
            break;
        }
        match daemon.handle(&req, &writer) {
            Ok(res) => writer.reply(req.id, res),
            Err(err) => writer.fail(req.id, &err),
        }
    }

    if let Some(sess) = daemon.session.take() {
        sess.stop();
    }
    unsafe { CoUninitialize() };
}
