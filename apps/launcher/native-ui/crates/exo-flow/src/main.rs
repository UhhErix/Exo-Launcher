mod bench;
mod capture;
mod flow_compute;
mod flow_nvof;
mod hud_mask;
mod pacer;
mod presenter;
mod shaders;
mod synth;
mod telemetry;

use crate::capture::WgcZeroCopyCapture;
use crate::flow_compute::Dx12FlowEngine;
use crate::flow_nvof::NvofRuntime;
use crate::hud_mask::HudMaskConfig;
use crate::pacer::VrrPacer;
use crate::presenter::DCompOverlayPresenter;
use crate::synth::{FlowBackendKind, FlowProfile};
use crate::telemetry::{FlowTelemetry, PipeCommand, PipeResponse, current_process_memory_mb};
use std::path::PathBuf;
use std::time::{Duration, Instant};
use windows::Win32::Foundation::{CloseHandle, ERROR_PIPE_CONNECTED, HANDLE, RECT};
use windows::Win32::Graphics::Direct3D12::D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
use windows::Win32::Storage::FileSystem::{
    FlushFileBuffers, PIPE_ACCESS_DUPLEX, ReadFile, WriteFile,
};
use windows::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_MESSAGE,
    PIPE_TYPE_MESSAGE, PIPE_WAIT,
};
use windows::Win32::UI::WindowsAndMessaging::GetWindowRect;
use windows::core::{HSTRING, PCWSTR};

struct ActiveSession {
    target_hwnd: isize,
    profile: FlowProfile,
    engine: Dx12FlowEngine,
    capture: WgcZeroCopyCapture,
    presenter: DCompOverlayPresenter,
    pacer: VrrPacer,
    use_dp4a: bool,
    backend_name: String,
    last_base_frame: Instant,
    telemetry: FlowTelemetry,
}

impl ActiveSession {
    fn start(hwnd: u64, profile: FlowProfile) -> Result<Self, String> {
        let mut rc = RECT::default();
        unsafe {
            GetWindowRect(windows::Win32::Foundation::HWND(hwnd as *mut _), &mut rc)
                .map_err(|e| format!("GetWindowRect: {e}"))?;
        }
        let width = (rc.right - rc.left).max(64) as u32;
        let height = (rc.bottom - rc.top).max(64) as u32;
        let engine = Dx12FlowEngine::new(width, height)?;
        let use_dp4a = match profile.backend {
            FlowBackendKind::ComputeFp16 => false,
            _ => engine.caps.shader_model_6_4_dp4a,
        };
        let backend_name = if use_dp4a {
            "dx12_compute_dp4a".to_string()
        } else {
            "dx12_compute_fp16".to_string()
        };
        let capture = WgcZeroCopyCapture::start(hwnd as isize, &engine.device)?;
        let presenter = DCompOverlayPresenter::new(
            &engine.factory,
            &engine.device,
            &engine.queue,
            rc.left,
            rc.top,
            width,
            height,
            true,
        )?;
        let pacer = VrrPacer::new(profile.multiplier, profile.target_hz);
        let (ws, priv_b) = current_process_memory_mb();
        let telemetry = FlowTelemetry {
            active: true,
            backend: backend_name.clone(),
            compute_path: if use_dp4a {
                "dp4a".into()
            } else {
                "fp16".into()
            },
            vram_mb: engine.query_vram_mb(),
            working_set_mb: ws,
            private_bytes_mb: priv_b,
            ..Default::default()
        };
        Ok(Self {
            target_hwnd: hwnd as isize,
            profile,
            engine,
            capture,
            presenter,
            pacer,
            use_dp4a,
            backend_name,
            last_base_frame: Instant::now(),
            telemetry,
        })
    }

    fn step_once(&mut self) -> Result<bool, String> {
        if DCompOverlayPresenter::should_disengage_for_target(self.target_hwnd) {
            return Ok(false);
        }
        let Some((cap_ms, shared_res)) = self.capture.try_acquire_latest() else {
            return Ok(true);
        };
        let dt_ms = self.last_base_frame.elapsed().as_secs_f64() * 1000.0;
        self.last_base_frame = Instant::now();
        self.pacer.update_base_frame_ms(dt_ms);
        self.engine.ingest_external_frame(shared_res)?;

        let hud_cfg = HudMaskConfig::from_sensitivity(self.profile.hud_sensitivity);
        for alpha in self.pacer.sub_frame_alphas() {
            let t = self.engine.run_flow_and_synth(
                self.use_dp4a,
                alpha,
                self.profile.hud_mask_enabled,
                hud_cfg,
                self.profile.scaler,
                self.profile.sharpness,
            )?;
            self.presenter.present_from_resource(
                &self.engine.queue,
                self.engine.out_resource(),
                D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            )?;
            let (ws, priv_b) = current_process_memory_mb();
            self.telemetry = FlowTelemetry {
                active: true,
                backend: self.backend_name.clone(),
                compute_path: if self.use_dp4a {
                    "dp4a".into()
                } else {
                    "fp16".into()
                },
                capture_ms: cap_ms,
                pyramid_ms: t.pyramid_ms,
                flow_ms: t.flow_ms,
                hud_mask_ms: t.hud_mask_ms,
                synth_ms: t.synth_ms,
                total_gpu_ms: t.total_gpu_ms,
                real_fps: 1000.0 / self.pacer.smoothed_base_interval_ms.max(1.0),
                output_fps: self.pacer.effective_target_hz(),
                p99_jitter_ms: self.pacer.p99_jitter_ms(),
                vram_mb: self.engine.query_vram_mb(),
                working_set_mb: ws,
                private_bytes_mb: priv_b,
                cpu_pct: 0.0,
            };
        }
        Ok(true)
    }
}

fn run_pipe_server(pipe_name: &str) -> Result<(), String> {
    let full = if pipe_name.starts_with(r"\\.\pipe\") {
        pipe_name.to_string()
    } else {
        format!(r"\\.\pipe\{pipe_name}")
    };
    let wide = HSTRING::from(full.as_str());
    let mut session: Option<ActiveSession> = None;

    loop {
        let pipe: HANDLE = unsafe {
            CreateNamedPipeW(
                PCWSTR::from_raw(wide.as_ptr()),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
                1,
                65536,
                65536,
                0,
                None,
            )
        };
        if pipe.is_invalid() {
            return Err("CreateNamedPipeW failed".into());
        }
        let connected = unsafe {
            match ConnectNamedPipe(pipe, None) {
                Ok(()) => true,
                Err(e) => e.code() == ERROR_PIPE_CONNECTED.to_hresult(),
            }
        };
        if !connected {
            unsafe {
                let _ = CloseHandle(pipe);
            }
            continue;
        }

        let mut buf = vec![0u8; 16384];
        let mut read = 0u32;
        if unsafe { ReadFile(pipe, Some(&mut buf), Some(&mut read), None) }.is_ok() && read > 0 {
            let line = String::from_utf8_lossy(&buf[..read as usize]);
            let resp = handle_command(line.trim(), &mut session);
            if let Ok(payload) = serde_json::to_vec(&resp) {
                let mut written = 0u32;
                unsafe {
                    let _ = WriteFile(pipe, Some(&payload), Some(&mut written), None);
                    let _ = FlushFileBuffers(pipe);
                }
            }
        }
        unsafe {
            let _ = DisconnectNamedPipe(pipe);
            let _ = CloseHandle(pipe);
        }

        if let Some(sess) = &mut session {
            if let Ok(keep) = sess.step_once() {
                if !keep {
                    session = None;
                }
            } else {
                session = None;
            }
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}

fn handle_command(raw: &str, session: &mut Option<ActiveSession>) -> PipeResponse {
    match serde_json::from_str::<PipeCommand>(raw) {
        Ok(PipeCommand::Start { hwnd, profile }) => match ActiveSession::start(hwnd, profile) {
            Ok(s) => {
                let t = s.telemetry.clone();
                *session = Some(s);
                PipeResponse {
                    ok: true,
                    message: Some("Exo Flow started".into()),
                    telemetry: t,
                }
            }
            Err(e) => PipeResponse {
                ok: false,
                message: Some(e),
                telemetry: FlowTelemetry::default(),
            },
        },
        Ok(PipeCommand::Stop) => {
            *session = None;
            let (ws, priv_b) = current_process_memory_mb();
            PipeResponse {
                ok: true,
                message: Some("Exo Flow stopped".into()),
                telemetry: FlowTelemetry {
                    active: false,
                    working_set_mb: ws,
                    private_bytes_mb: priv_b,
                    ..Default::default()
                },
            }
        }
        Ok(PipeCommand::Status) => {
            let telemetry = session
                .as_ref()
                .map(|s| s.telemetry.clone())
                .unwrap_or_else(|| {
                    let (ws, priv_b) = current_process_memory_mb();
                    FlowTelemetry {
                        active: false,
                        working_set_mb: ws,
                        private_bytes_mb: priv_b,
                        ..Default::default()
                    }
                });
            PipeResponse {
                ok: true,
                message: None,
                telemetry,
            }
        }
        Ok(PipeCommand::SetProfile { profile }) => {
            if let Some(s) = session.as_mut() {
                s.profile = profile;
                s.pacer = VrrPacer::new(s.profile.multiplier, s.profile.target_hz);
                PipeResponse {
                    ok: true,
                    message: Some("Profile updated".into()),
                    telemetry: s.telemetry.clone(),
                }
            } else {
                PipeResponse {
                    ok: false,
                    message: Some("No active Exo Flow session".into()),
                    telemetry: FlowTelemetry::default(),
                }
            }
        }
        Err(e) => PipeResponse {
            ok: false,
            message: Some(format!("invalid command: {e}")),
            telemetry: FlowTelemetry::default(),
        },
    }
}

fn main() {
    let mut args = std::env::args().skip(1);
    let mut do_bench = false;
    let mut inspect_gpu = false;
    let mut out_dir = PathBuf::from("improve/lab-exo-flow");
    let mut frames = 1000usize;
    let mut pipe_name: Option<String> = None;

    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--bench" => do_bench = true,
            "--inspect-gpu" => inspect_gpu = true,
            "--out-dir" => {
                if let Some(v) = args.next() {
                    out_dir = PathBuf::from(v);
                }
            }
            "--frames" => {
                if let Some(v) = args.next()
                    && let Ok(n) = v.parse::<usize>()
                {
                    frames = n.max(10);
                }
            }
            "--pipe" => {
                pipe_name = args.next();
            }
            _ => {}
        }
    }

    if inspect_gpu {
        match Dx12FlowEngine::new(64, 64) {
            Ok(eng) => {
                let (_, nvof) = NvofRuntime::probe_and_create(&eng.device, &eng.queue);
                println!(
                    "{}",
                    serde_json::to_string_pretty(&serde_json::json!({
                        "gpu": eng.caps,
                        "nvof_dll_loaded": nvof.dll_loaded,
                        "nvof_5_0_ok": nvof.api_version_5_0_ok,
                        "nvof_d3d12_created": nvof.d3d12_instance_created,
                        "nvof_note": nvof.note,
                    }))
                    .unwrap()
                );
            }
            Err(e) => {
                eprintln!("inspect-gpu failed: {e}");
                std::process::exit(1);
            }
        }
        return;
    }

    if do_bench {
        match bench::run_full_benchmark(&out_dir, frames) {
            Ok(report) => {
                println!("{}", serde_json::to_string_pretty(&report).unwrap());
            }
            Err(e) => {
                eprintln!("bench failed: {e}");
                std::process::exit(1);
            }
        }
        return;
    }

    let pipe = pipe_name.unwrap_or_else(|| format!("exo-flow-{}", std::process::id()));
    if let Err(e) = run_pipe_server(&pipe) {
        eprintln!("exo-flow pipe server error: {e}");
        std::process::exit(1);
    }
}
