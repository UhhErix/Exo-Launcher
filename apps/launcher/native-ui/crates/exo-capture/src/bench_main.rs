use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::{Value, json};
use windows::Win32::Foundation::{FILETIME, HANDLE, HMODULE, HWND, LPARAM, LRESULT, POINT, WPARAM};
use windows::Win32::Graphics::Direct3D::D3D_DRIVER_TYPE_HARDWARE;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION, D3D11CreateDeviceAndSwapChain,
    ID3D11Device, ID3D11DeviceContext, ID3D11RenderTargetView, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_MODE_DESC, DXGI_RATIONAL, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_PRESENT, DXGI_SWAP_CHAIN_DESC, DXGI_SWAP_EFFECT_DISCARD, DXGI_USAGE_RENDER_TARGET_OUTPUT,
    IDXGISwapChain,
};
use windows::Win32::Graphics::Gdi::{MONITOR_DEFAULTTOPRIMARY, MonitorFromPoint};
use windows::Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx, CoUninitialize};
use windows::Win32::System::Performance::{QueryPerformanceCounter, QueryPerformanceFrequency};
use windows::Win32::System::ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS};
use windows::Win32::System::Threading::{
    GetCurrentProcess, GetProcessIoCounters, GetProcessTimes, IO_COUNTERS, OpenProcess,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::WindowsAndMessaging::{
    CS_HREDRAW, CS_VREDRAW, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, MSG,
    PM_REMOVE, PeekMessageW, RegisterClassW, SW_SHOWNOACTIVATE, ShowWindow, TranslateMessage,
    WINDOW_EX_STYLE, WNDCLASSW, WS_OVERLAPPEDWINDOW, WS_VISIBLE,
};
use windows::core::{PCWSTR, w};

use exo_capture::colorspace::GpuColorConverter;
use exo_capture::encode::mf_mft::{MfHardwareEncoder, ensure_mf_started, probe_hardware_encoders};
use exo_capture::encode::{EncoderConfig, HardwareVideoEncoder};
use exo_capture::gpu::GpuDevice;
use exo_capture::mux_mp4::{
    VideoCodec, VideoTrackConfig, annex_b_to_length_prefixed, build_avcc_from_sps_pps,
    extract_h264_sps_pps, mux_packets_to_fmp4, validate_mp4,
};
use exo_capture::policy::{
    enable_per_monitor_dpi_v2, suppress_os_error_dialogs, system_total_ram_bytes,
};
use exo_capture::replay_ring::{EncodedPacket, ReplayRingBuffer, TrackKind};
use exo_capture::session::{CaptureSession, SessionConfig};
use exo_capture::wgc::WgcTarget;

#[derive(Debug, Clone, Copy)]
struct ProcessSample {
    cpu_100ns: u64,
    working_set_bytes: u64,
    pagefile_bytes: u64,
    io_write_bytes: u64,
}

fn filetime_u64(ft: FILETIME) -> u64 {
    ((ft.dwHighDateTime as u64) << 32) | (ft.dwLowDateTime as u64)
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

fn sample_process(handle: HANDLE) -> Option<ProcessSample> {
    let mut create = FILETIME::default();
    let mut exit = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    unsafe { GetProcessTimes(handle, &mut create, &mut exit, &mut kernel, &mut user) }.ok()?;

    let mut mem = PROCESS_MEMORY_COUNTERS {
        cb: std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
        ..Default::default()
    };
    unsafe {
        GetProcessMemoryInfo(
            handle,
            &mut mem,
            std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32,
        )
    }
    .ok()?;

    let mut io = IO_COUNTERS::default();
    unsafe { GetProcessIoCounters(handle, &mut io) }.ok()?;

    Some(ProcessSample {
        cpu_100ns: filetime_u64(kernel) + filetime_u64(user),
        working_set_bytes: mem.WorkingSetSize as u64,
        pagefile_bytes: mem.PagefileUsage as u64,
        io_write_bytes: io.WriteTransferCount,
    })
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) }
}

struct SyntheticGameWindow {
    hwnd: HWND,
    _device: ID3D11Device,
    context: ID3D11DeviceContext,
    swap_chain: IDXGISwapChain,
    rtv: ID3D11RenderTargetView,
}

impl SyntheticGameWindow {
    fn create(width: u32, height: u32) -> Result<Self, String> {
        let class_name = w!("ExoCaptureSynthBenchWnd");
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            lpszClassName: class_name,
            ..Default::default()
        };
        unsafe {
            RegisterClassW(&wc);
        }

        let hwnd = unsafe {
            CreateWindowExW(
                WINDOW_EX_STYLE(0),
                class_name,
                PCWSTR(w!("Exo Synthetic Benchmark Window").as_ptr()),
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                40,
                40,
                width as i32,
                height as i32,
                None,
                None,
                None,
                None,
            )
        }
        .map_err(|e| format!("CreateWindowExW failed: {e}"))?;

        unsafe {
            let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        }

        let sc_desc = DXGI_SWAP_CHAIN_DESC {
            BufferDesc: DXGI_MODE_DESC {
                Width: width,
                Height: height,
                RefreshRate: DXGI_RATIONAL {
                    Numerator: 60,
                    Denominator: 1,
                },
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                ..Default::default()
            },
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            OutputWindow: hwnd,
            Windowed: true.into(),
            SwapEffect: DXGI_SWAP_EFFECT_DISCARD,
            Flags: 0,
        };

        let mut swap_chain = None;
        let mut device = None;
        let mut context = None;
        unsafe {
            D3D11CreateDeviceAndSwapChain(
                None,
                D3D_DRIVER_TYPE_HARDWARE,
                HMODULE::default(),
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                None,
                D3D11_SDK_VERSION,
                Some(&sc_desc),
                Some(&mut swap_chain),
                Some(&mut device),
                None,
                Some(&mut context),
            )
        }
        .map_err(|e| format!("D3D11CreateDeviceAndSwapChain failed: {e}"))?;

        let swap_chain = swap_chain.ok_or("swap_chain null")?;
        let device = device.ok_or("device null")?;
        let context = context.ok_or("context null")?;
        let back_buf: ID3D11Texture2D =
            unsafe { swap_chain.GetBuffer(0) }.map_err(|e| format!("GetBuffer(0) failed: {e}"))?;
        let mut rtv = None;
        unsafe { device.CreateRenderTargetView(&back_buf, None, Some(&mut rtv)) }
            .map_err(|e| format!("CreateRenderTargetView failed: {e}"))?;
        let rtv = rtv.ok_or("rtv null")?;

        Ok(Self {
            hwnd,
            _device: device,
            context,
            swap_chain,
            rtv,
        })
    }

    fn pump_messages(&self) {
        let mut msg = MSG::default();
        while unsafe { PeekMessageW(&mut msg, None, 0, 0, PM_REMOVE) }.as_bool() {
            unsafe {
                let _ = TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
    }

    fn render_one(&self, tick: u32) -> f64 {
        self.pump_messages();
        let f0 = Instant::now();
        let t = (tick as f32) * 0.03;
        let color = [
            0.25 + 0.25 * t.sin().abs(),
            0.35 + 0.35 * (t * 0.7).cos().abs(),
            0.45 + 0.30 * (t * 1.3).sin().abs(),
            1.0,
        ];
        unsafe {
            self.context.ClearRenderTargetView(&self.rtv, &color);
            let _ = self.swap_chain.Present(1, DXGI_PRESENT(0));
        }
        f0.elapsed().as_secs_f64() * 1000.0
    }

    fn render_for(&self, duration: Duration) -> (f64, f64, f64, usize) {
        let start = Instant::now();
        let mut frame_ms = Vec::with_capacity(2048);
        let mut tick = 0u32;
        while start.elapsed() < duration {
            frame_ms.push(self.render_one(tick));
            tick = tick.wrapping_add(1);
        }
        let elapsed = start.elapsed().as_secs_f64().max(0.001);
        let fps = (frame_ms.len() as f64) / elapsed;
        frame_ms.sort_by(|a, b| a.total_cmp(b));
        let p50 = percentile(&frame_ms, 50.0);
        let p95 = percentile(&frame_ms, 95.0);
        (fps, p50, p95, frame_ms.len())
    }
}

impl Drop for SyntheticGameWindow {
    fn drop(&mut self) {
        unsafe {
            let _ = DestroyWindow(self.hwnd);
        }
    }
}

fn percentile(sorted: &[f64], pct: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    let idx = (((sorted.len() - 1) as f64) * pct / 100.0).round() as usize;
    sorted[idx.min(sorted.len() - 1)]
}

fn try_start_wgc_session(
    gpu: Arc<GpuDevice>,
    ring: Arc<ReplayRingBuffer>,
    synth: &SyntheticGameWindow,
    target: WgcTarget,
) -> Result<CaptureSession, String> {
    let (tx, rx) = std::sync::mpsc::sync_channel(1);
    thread::spawn(move || {
        let res = CaptureSession::start(
            gpu,
            ring,
            SessionConfig {
                target,
                fps: 60,
                bitrate_bps: 16_000_000,
                cursor: true,
                hdr10: false,
                preferred_codec: Some(VideoCodec::H264),
                game_pid: None,
                discord_pid: None,
                include_mic: false,
                include_voice: false,
            },
        );
        let _ = tx.send(res);
    });
    let mut tick = 0u32;
    let deadline = Instant::now() + Duration::from_secs(6);
    while Instant::now() < deadline {
        synth.render_one(tick);
        tick = tick.wrapping_add(1);
        if let Ok(res) = rx.try_recv() {
            return res;
        }
    }
    Err("WGC session start timed out".to_string())
}

fn benchmark_direct_gpu_pipeline(
    gpu: Arc<GpuDevice>,
    synth: &SyntheticGameWindow,
    width: u32,
    height: u32,
    seconds: u64,
    out_dir: &Path,
) -> Result<Value, String> {
    let vram_before = gpu.query_vram_usage().map(|v| v.current_bytes).unwrap_or(0);
    let ring = Arc::new(ReplayRingBuffer::new(
        60,
        512 * 1024 * 1024,
        system_total_ram_bytes(),
    ));
    let stop_flag = Arc::new(AtomicBool::new(false));
    let stop_enc = Arc::clone(&stop_flag);
    let ring_enc = Arc::clone(&ring);
    let gpu_enc = Arc::clone(&gpu);
    let (ready_tx, ready_rx) = std::sync::mpsc::sync_channel::<Result<String, String>>(1);

    let enc_thread = thread::spawn(move || {
        let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
        let init_res = (|| -> Result<_, String> {
            let src_tex =
                gpu_enc.create_default_texture(width, height, DXGI_FORMAT_B8G8R8A8_UNORM)?;
            let mut rtv: Option<ID3D11RenderTargetView> = None;
            unsafe {
                gpu_enc
                    .device
                    .CreateRenderTargetView(&src_tex, None, Some(&mut rtv))
            }
            .map_err(|e| format!("CreateRenderTargetView(bench) failed: {e}"))?;
            let rtv = rtv.ok_or("rtv null")?;
            let converter = GpuColorConverter::new(
                Arc::clone(&gpu_enc),
                width,
                height,
                DXGI_FORMAT_B8G8R8A8_UNORM,
                width,
                height,
                false,
            )?;
            let encoder = MfHardwareEncoder::open(
                Arc::clone(&gpu_enc),
                EncoderConfig {
                    codec: VideoCodec::H264,
                    width,
                    height,
                    fps: 60,
                    bitrate_bps: 16_000_000,
                    gop_frames: 60,
                    hdr10: false,
                    low_latency: false,
                },
            )?;
            Ok((src_tex, rtv, converter, encoder))
        })();

        let (src_tex, rtv, mut converter, mut encoder) = match init_res {
            Ok(tuple) => {
                let _ = ready_tx.send(Ok(tuple.3.encoder_name().to_string()));
                tuple
            }
            Err(e) => {
                let _ = ready_tx.send(Err(e));
                unsafe { CoUninitialize() };
                return (0u64, 0u64, 0u64, Vec::new());
            }
        };

        let frame_period = Duration::from_secs_f64(1.0 / 60.0);
        let dur_100ns = 166_667i64;
        let mut next = Instant::now();
        let mut tick = 0u32;
        let mut codec_config = Vec::new();
        let mut captured = 0u64;
        let mut encoded = 0u64;
        let mut keyframes = 0u64;

        while !stop_enc.load(Ordering::Acquire) {
            let t = (tick as f32) * 0.05;
            let color = [
                0.2 + 0.6 * t.sin().abs(),
                0.3 + 0.5 * (t * 0.8).cos().abs(),
                0.4 + 0.4 * (t * 1.1).sin().abs(),
                1.0,
            ];
            unsafe {
                gpu_enc.context.ClearRenderTargetView(&rtv, &color);
            }
            captured += 1;
            if let Ok(nv12) = converter.convert(&src_tex)
                && let Ok(outs) = encoder.encode_gpu_surface(&nv12, qpc_100ns(), dur_100ns, false)
            {
                for out in outs {
                    if out.annex_b.is_empty() {
                        continue;
                    }
                    if out.is_keyframe {
                        keyframes += 1;
                        if codec_config.is_empty()
                            && let Some((sps, pps)) = extract_h264_sps_pps(&out.annex_b)
                        {
                            codec_config = build_avcc_from_sps_pps(&sps, &pps);
                        }
                    }
                    encoded += 1;
                    ring_enc.push(EncodedPacket {
                        track: TrackKind::Video,
                        data: annex_b_to_length_prefixed(&out.annex_b),
                        timestamp_100ns: out.timestamp_100ns,
                        duration_100ns: out.duration_100ns.max(dur_100ns),
                        is_keyframe: out.is_keyframe,
                    });
                }
            }
            tick = tick.wrapping_add(1);
            next += frame_period;
            let now = Instant::now();
            if next > now {
                thread::sleep(next - now);
            } else {
                next = now;
            }
        }
        if let Ok(drained) = encoder.drain() {
            for out in drained {
                if !out.annex_b.is_empty() {
                    encoded += 1;
                    if out.is_keyframe {
                        keyframes += 1;
                    }
                    ring_enc.push(EncodedPacket {
                        track: TrackKind::Video,
                        data: annex_b_to_length_prefixed(&out.annex_b),
                        timestamp_100ns: out.timestamp_100ns,
                        duration_100ns: out.duration_100ns.max(dur_100ns),
                        is_keyframe: out.is_keyframe,
                    });
                }
            }
        }
        unsafe { CoUninitialize() };
        (captured, encoded, keyframes, codec_config)
    });

    let encoder_name = ready_rx
        .recv_timeout(Duration::from_secs(5))
        .map_err(|_| "direct GPU encoder init timed out".to_string())??;

    thread::sleep(Duration::from_millis(200));
    let cur_proc = unsafe { GetCurrentProcess() };
    let self_before = sample_process(cur_proc).ok_or("sample self before")?;
    let t0 = Instant::now();
    let (rust_fps, rust_p50, rust_p95, rust_frames) =
        synth.render_for(Duration::from_secs(seconds));
    let wall = t0.elapsed().as_secs_f64().max(0.001);
    let self_after = sample_process(cur_proc).ok_or("sample self after")?;
    let vram_after = gpu.query_vram_usage().map(|v| v.current_bytes).unwrap_or(0);
    let replay_disk_writes = self_after
        .io_write_bytes
        .saturating_sub(self_before.io_write_bytes);

    stop_flag.store(true, Ordering::Release);
    let (captured, encoded, keyframes, codec_config) =
        enc_thread.join().map_err(|_| "enc thread panicked")?;

    let save_t0 = Instant::now();
    let packets = ring.take_last_seconds(Some(30));
    let mp4_bytes = mux_packets_to_fmp4(
        VideoTrackConfig {
            codec: VideoCodec::H264,
            width: width as u16,
            height: height as u16,
            timescale: 90_000,
            fps: 60,
            hdr10: false,
            codec_config,
        },
        Vec::new(),
        &packets,
    );
    let _ = fs::create_dir_all(out_dir);
    let clip_path = out_dir.join("rust-direct-bench.mp4");
    fs::write(&clip_path, &mp4_bytes).map_err(|e| format!("write clip failed: {e}"))?;
    let save_latency_ms = save_t0.elapsed().as_secs_f64() * 1000.0;
    let validation = validate_mp4(&mp4_bytes)?;

    let cpu_sec = self_after.cpu_100ns.saturating_sub(self_before.cpu_100ns) as f64 / 10_000_000.0;
    let logical_cpus = thread::available_parallelism()
        .map(|n| n.get() as f64)
        .unwrap_or(1.0);
    let cpu_pct_total = (cpu_sec / (wall * logical_cpus)) * 100.0;
    let rust_bin_bytes = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.join("exo-capture.exe")))
        .and_then(|p| fs::metadata(p).ok())
        .map(|m| m.len())
        .unwrap_or(0);

    Ok(json!({
        "binaryBytes": rust_bin_bytes,
        "sourceKind": "d3d11-zero-copy-gpu-surface",
        "encoderName": encoder_name,
        "codec": "H.264",
        "framesCaptured": captured,
        "framesEncoded": encoded,
        "keyframesEncoded": keyframes,
        "synthFps": rust_fps,
        "synthFrameP50Ms": rust_p50,
        "synthFrameP95Ms": rust_p95,
        "synthFrames": rust_frames,
        "cpuPercentTotal": cpu_pct_total,
        "cpuSeconds": cpu_sec,
        "workingSetMb": self_after.working_set_bytes as f64 / (1024.0 * 1024.0),
        "privateMb": self_after.pagefile_bytes as f64 / (1024.0 * 1024.0),
        "vramDeltaMb": vram_after.saturating_sub(vram_before) as f64 / (1024.0 * 1024.0),
        "replayBufferDiskWriteBytes": replay_disk_writes,
        "saveLatencyMs": save_latency_ms,
        "savedClipBytes": mp4_bytes.len(),
        "savedVideoPackets": packets.len(),
        "savedDurationMs": validation.video_duration_ms,
        "mp4Validated": {
            "tracks": validation.track_count,
            "fragments": validation.fragment_count,
            "videoSamples": validation.video_sample_count,
            "videoKeyframes": validation.video_keyframe_count,
        }
    }))
}

fn benchmark_daemon_child(
    exe_path: &Path,
    synth: &SyntheticGameWindow,
    seconds: u64,
    out_dir: &Path,
    validate_saved_mp4: bool,
) -> Result<Value, String> {
    let _ = fs::remove_dir_all(out_dir);
    let _ = fs::create_dir_all(out_dir);
    let mut child = Command::new(exe_path)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("spawn {} failed: {e}", exe_path.display()))?;

    let mut stdin = child.stdin.take().ok_or("no stdin")?;
    let stdout = child.stdout.take().ok_or("no stdout")?;
    let ready_flag = Arc::new(AtomicBool::new(false));
    let saved_path: Arc<std::sync::Mutex<Option<String>>> = Arc::new(std::sync::Mutex::new(None));
    let ready_c = Arc::clone(&ready_flag);
    let saved_c = Arc::clone(&saved_path);

    let reader_handle = thread::spawn(move || {
        let reader = BufReader::new(stdout);
        for line in reader.lines().map_while(Result::ok) {
            if let Ok(v) = serde_json::from_str::<Value>(&line) {
                if v.get("event").and_then(Value::as_str) == Some("ready") {
                    ready_c.store(true, Ordering::Release);
                } else if v.get("event").and_then(Value::as_str) == Some("saved") {
                    let p = v
                        .get("data")
                        .and_then(|d| d.get("path"))
                        .and_then(Value::as_str)
                        .unwrap_or("")
                        .to_string();
                    if let Ok(mut g) = saved_c.lock() {
                        *g = Some(p);
                    }
                }
            }
        }
    });

    let wait_ready = Instant::now();
    while !ready_flag.load(Ordering::Acquire) && wait_ready.elapsed() < Duration::from_secs(10) {
        synth.pump_messages();
        thread::sleep(Duration::from_millis(20));
    }

    let cfg_msg = json!({
        "id": 1,
        "method": "configure",
        "params": {
            "replayMode": "always",
            "replaySeconds": 30,
            "maxFps": 60,
            "quality": 50,
            "cursor": true,
            "folder": out_dir.to_string_lossy(),
        }
    });
    let _ = writeln!(stdin, "{cfg_msg}");
    let _ = stdin.flush();
    thread::sleep(Duration::from_millis(1200));

    let proc_handle = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, child.id()) }
        .map_err(|e| format!("OpenProcess({}) failed: {e}", exe_path.display()))?;
    let before = sample_process(proc_handle).ok_or("sample child before failed")?;
    let t0 = Instant::now();
    let (fps, p50, p95, frames) = synth.render_for(Duration::from_secs(seconds));
    let wall = t0.elapsed().as_secs_f64().max(0.001);
    let after = sample_process(proc_handle).ok_or("sample child after failed")?;

    let save_t0 = Instant::now();
    let save_msg = json!({ "id": 2, "method": "replay.save", "params": {} });
    let _ = writeln!(stdin, "{save_msg}");
    let _ = stdin.flush();
    while saved_path.lock().ok().and_then(|g| g.clone()).is_none()
        && save_t0.elapsed() < Duration::from_secs(6)
    {
        thread::sleep(Duration::from_millis(2));
    }
    let save_latency_ms = save_t0.elapsed().as_secs_f64() * 1000.0;
    let saved_file = saved_path.lock().ok().and_then(|g| g.clone());

    let _ = writeln!(
        stdin,
        "{}",
        json!({ "id": 3, "method": "shutdown", "params": {} })
    );
    let _ = stdin.flush();
    drop(stdin);
    let _ = child.wait();
    let _ = reader_handle.join();

    let cpu_sec = after.cpu_100ns.saturating_sub(before.cpu_100ns) as f64 / 10_000_000.0;
    let logical_cpus = thread::available_parallelism()
        .map(|n| n.get() as f64)
        .unwrap_or(1.0);
    let cpu_pct_total = (cpu_sec / (wall * logical_cpus)) * 100.0;
    let bin_bytes = fs::metadata(exe_path).map(|m| m.len()).unwrap_or(0);

    let validated = if validate_saved_mp4 {
        saved_file.as_deref().and_then(|p| {
            let bytes = fs::read(p).ok()?;
            let rep = validate_mp4(&bytes).ok()?;
            Some(json!({
                "bytes": bytes.len(),
                "tracks": rep.track_count,
                "fragments": rep.fragment_count,
                "videoSamples": rep.video_sample_count,
                "videoKeyframes": rep.video_keyframe_count,
                "durationMs": rep.video_duration_ms,
            }))
        })
    } else {
        None
    };

    Ok(json!({
        "binaryBytes": bin_bytes,
        "synthFps": fps,
        "synthFrameP50Ms": p50,
        "synthFrameP95Ms": p95,
        "synthFrames": frames,
        "cpuPercentTotal": cpu_pct_total,
        "cpuSeconds": cpu_sec,
        "workingSetMb": after.working_set_bytes as f64 / (1024.0 * 1024.0),
        "privateMb": after.pagefile_bytes as f64 / (1024.0 * 1024.0),
        "replayBufferDiskWriteBytes": after.io_write_bytes.saturating_sub(before.io_write_bytes),
        "saveLatencyMs": save_latency_ms,
        "savedEventReceived": saved_file.is_some(),
        "mp4Validated": validated,
    }))
}

fn main() {
    suppress_os_error_dialogs();
    enable_per_monitor_dpi_v2();
    let _ = unsafe { CoInitializeEx(None, COINIT_MULTITHREADED) };
    let _ = ensure_mf_started();

    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut seconds = 6u64;
    let mut width = 1920u32;
    let mut height = 1080u32;
    let mut dotnet_exe: Option<PathBuf> = None;
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--seconds" if i + 1 < args.len() => {
                seconds = args[i + 1].parse().unwrap_or(6).clamp(2, 30);
                i += 2;
            }
            "--1440p" => {
                width = 2560;
                height = 1440;
                i += 1;
            }
            "--compare-dotnet" if i + 1 < args.len() => {
                dotnet_exe = Some(PathBuf::from(&args[i + 1]));
                i += 2;
            }
            _ => i += 1,
        }
    }

    let gpu = Arc::new(GpuDevice::create(None).expect("hardware GPU required for benchmark"));
    let caps = probe_hardware_encoders();
    let synth = SyntheticGameWindow::create(width, height).expect("synthetic D3D11 window failed");

    let (base_fps, base_p50, base_p95, base_frames) =
        synth.render_for(Duration::from_secs(seconds));

    let out_dir = std::env::temp_dir().join("exo-capture-bench");
    let vram_before = gpu.query_vram_usage().map(|v| v.current_bytes).unwrap_or(0);
    let ring = Arc::new(ReplayRingBuffer::new(
        60,
        512 * 1024 * 1024,
        system_total_ram_bytes(),
    ));

    let wgc_try = try_start_wgc_session(
        Arc::clone(&gpu),
        Arc::clone(&ring),
        &synth,
        WgcTarget::Window(synth.hwnd.0 as isize),
    )
    .or_else(|_| {
        let mon = unsafe { MonitorFromPoint(POINT { x: 0, y: 0 }, MONITOR_DEFAULTTOPRIMARY) };
        try_start_wgc_session(
            Arc::clone(&gpu),
            Arc::clone(&ring),
            &synth,
            WgcTarget::Monitor(mon.0 as isize),
        )
    });

    let (rust_report, wgc_note) = match wgc_try {
        Ok(session) => {
            thread::sleep(Duration::from_millis(250));
            let cur_proc = unsafe { GetCurrentProcess() };
            let self_before = sample_process(cur_proc).expect("sample self before");
            let t0 = Instant::now();
            let (rust_fps, rust_p50, rust_p95, rust_frames) =
                synth.render_for(Duration::from_secs(seconds));
            let wall = t0.elapsed().as_secs_f64().max(0.001);
            let self_after = sample_process(cur_proc).expect("sample self after");
            let vram_after = gpu.query_vram_usage().map(|v| v.current_bytes).unwrap_or(0);
            let stats = session.stats();

            let replay_disk_writes_during_buffer = self_after
                .io_write_bytes
                .saturating_sub(self_before.io_write_bytes);

            let clip_path = out_dir.join("rust-replay-bench.mp4");
            let save_res = session
                .save_replay_to_file(&clip_path, Some(30))
                .expect("rust replay save failed");
            let saved_bytes = fs::read(&clip_path).expect("read saved mp4");
            let validation = validate_mp4(&saved_bytes).expect("saved mp4 failed integrity check");
            session.stop();

            let cpu_sec =
                self_after.cpu_100ns.saturating_sub(self_before.cpu_100ns) as f64 / 10_000_000.0;
            let logical_cpus = thread::available_parallelism()
                .map(|n| n.get() as f64)
                .unwrap_or(1.0);
            let cpu_pct_total = (cpu_sec / (wall * logical_cpus)) * 100.0;
            let rust_bin_bytes = std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(|d| d.join("exo-capture.exe")))
                .and_then(|p| fs::metadata(p).ok())
                .map(|m| m.len())
                .unwrap_or(0);

            (
                json!({
                    "binaryBytes": rust_bin_bytes,
                    "sourceKind": stats.source_kind,
                    "encoderName": stats.encoder_name,
                    "codec": stats.codec.display_name(),
                    "framesCaptured": stats.frames_captured,
                    "framesEncoded": stats.frames_encoded,
                    "keyframesEncoded": stats.keyframes_encoded,
                    "synthFps": rust_fps,
                    "fpsDeltaPct": if base_fps > 0.0 { ((rust_fps - base_fps) / base_fps) * 100.0 } else { 0.0 },
                    "synthFrameP50Ms": rust_p50,
                    "synthFrameP95Ms": rust_p95,
                    "synthFrames": rust_frames,
                    "cpuPercentTotal": cpu_pct_total,
                    "cpuSeconds": cpu_sec,
                    "workingSetMb": self_after.working_set_bytes as f64 / (1024.0 * 1024.0),
                    "privateMb": self_after.pagefile_bytes as f64 / (1024.0 * 1024.0),
                    "vramDeltaMb": vram_after.saturating_sub(vram_before) as f64 / (1024.0 * 1024.0),
                    "replayBufferDiskWriteBytes": replay_disk_writes_during_buffer,
                    "saveLatencyMs": save_res.save_latency_ms,
                    "savedClipBytes": save_res.bytes_written,
                    "savedVideoPackets": save_res.video_packets,
                    "savedDurationMs": save_res.duration_ms,
                    "mp4Validated": {
                        "tracks": validation.track_count,
                        "fragments": validation.fragment_count,
                        "videoSamples": validation.video_sample_count,
                        "videoKeyframes": validation.video_keyframe_count,
                    }
                }),
                "live-wgc".to_string(),
            )
        }
        Err(reason) => {
            let direct = benchmark_direct_gpu_pipeline(
                Arc::clone(&gpu),
                &synth,
                width,
                height,
                seconds,
                &out_dir,
            )
            .expect("direct GPU pipeline benchmark failed");
            (direct, format!("fallback-direct-gpu ({reason})"))
        }
    };

    let rust_exe_path = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.join("exo-capture.exe")));
    let rust_daemon_report = rust_exe_path.as_ref().filter(|p| p.exists()).map(|p| {
        benchmark_daemon_child(p, &synth, seconds, &out_dir.join("rust-daemon"), true)
            .unwrap_or_else(|e| json!({ "error": e }))
    });

    let dotnet_report = dotnet_exe.as_ref().map(|p| {
        benchmark_daemon_child(p, &synth, seconds, &out_dir.join("dotnet"), false)
            .unwrap_or_else(|e| json!({ "error": e }))
    });

    let _ = fs::remove_dir_all(&out_dir);

    let report = json!({
        "gpu": gpu.info.name,
        "resolution": format!("{width}x{height}"),
        "durationSec": seconds,
        "capturePath": wgc_note,
        "nvencApi": caps.nvenc_api_version,
        "encoders": {
            "av1Hw": caps.av1_hw,
            "hevcHw": caps.hevc_hw,
            "h264Hw": caps.h264_hw,
            "backend": caps.preferred_backend,
        },
        "baselineReplayOff": {
            "synthFps": base_fps,
            "synthFrameP50Ms": base_p50,
            "synthFrameP95Ms": base_p95,
            "synthFrames": base_frames,
        },
        "exoCaptureRustInProcess": rust_report,
        "exoCaptureRustDaemon": rust_daemon_report,
        "exoCaptureDotnetDaemon": dotnet_report,
    });
    println!("{}", serde_json::to_string_pretty(&report).unwrap());
    unsafe { CoUninitialize() };
}
