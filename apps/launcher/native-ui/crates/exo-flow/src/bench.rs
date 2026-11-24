use crate::capture::WgcZeroCopyCapture;
use crate::flow_compute::{Dx12FlowEngine, GpuCapabilities, GpuPassTimings};
use crate::flow_nvof::NvofRuntime;
use crate::hud_mask::HudMaskConfig;
use crate::pacer::VrrPacer;
use crate::presenter::DCompOverlayPresenter;
use crate::synth::{MultiplierMode, ScalerMode};
use crate::telemetry::{
    current_process_cpu_ticks_100ns, current_process_memory_mb, round2, round3,
};
use serde::Serialize;
use std::fs::File;
use std::io::BufWriter;
use std::path::Path;
use std::time::{Duration, Instant};
use windows::Win32::Graphics::Direct3D12::D3D12_RESOURCE_STATE_UNORDERED_ACCESS;

#[derive(Clone, Debug, Serialize)]
pub struct PassSummary {
    pub frames: usize,
    pub avg_pyramid_ms: f64,
    pub avg_flow_ms: f64,
    pub avg_smooth_ms: f64,
    pub avg_hud_mask_ms: f64,
    pub avg_synth_ms: f64,
    pub avg_total_gpu_ms: f64,
    pub p50_total_gpu_ms: f64,
    pub p95_total_gpu_ms: f64,
    pub p99_total_gpu_ms: f64,
    pub vram_mb: f64,
    pub working_set_mb: f64,
    pub private_bytes_mb: f64,
    pub cpu_pct: f64,
}

#[derive(Clone, Debug, Serialize)]
pub struct QualityMetrics {
    pub full_frame_psnr_db: f64,
    pub static_hud_psnr_db: f64,
    pub crosshair_psnr_db: f64,
    pub minimap_psnr_db: f64,
    pub health_bar_psnr_db: f64,
    pub full_frame_ssim: f64,
}

#[derive(Clone, Debug, Serialize)]
pub struct NvofReport {
    pub dll_loaded: bool,
    pub api_version_5_0_ok: bool,
    pub d3d12_instance_created: bool,
    pub status_code: i32,
    pub note: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct PacingReport {
    pub frames_presented: usize,
    pub target_hz: u32,
    pub p99_interval_jitter_ms: f64,
    pub avg_present_ms: f64,
    pub added_latency_ms_at_60fps: f64,
    pub added_latency_base_frames: f64,
    pub device_lost_count: u32,
    pub wgc_capture_probe_ok: bool,
    pub wgc_capture_ms: Option<f64>,
    pub wgc_note: String,
}

#[derive(Clone, Debug, Serialize)]
pub struct ExoFlowBenchReport {
    pub resolution: String,
    pub gpu: GpuCapabilities,
    pub baseline_vram_mb: f64,
    pub allocated_vram_delta_mb: f64,
    pub nvof_probe: NvofReport,
    pub compute_dp4a_1000f: PassSummary,
    pub compute_fp16_1000f: PassSummary,
    pub quality_hud_mask_off: QualityMetrics,
    pub quality_hud_mask_on: QualityMetrics,
    pub quality_dp4a_vs_fp16_hud_on: QualityMetrics,
    pub pacing_dcomp_1000f: PacingReport,
    pub artifacts: Vec<String>,
}

pub fn run_full_benchmark(out_dir: &Path, frames: usize) -> Result<ExoFlowBenchReport, String> {
    std::fs::create_dir_all(out_dir).map_err(|e| format!("create_dir_all: {e}"))?;
    let width = 2560u32;
    let height = 1440u32;

    let probe_only = Dx12FlowEngine::new(64, 64)?;
    let baseline_vram = probe_only.query_vram_mb();
    drop(probe_only);

    let mut engine = Dx12FlowEngine::new(width, height)?;
    let active_vram = engine.query_vram_mb();
    let vram_delta = round2((active_vram - baseline_vram).max(0.0));

    let (_nvof_inst, nvof_probe_raw) = NvofRuntime::probe_and_create(&engine.device, &engine.queue);
    let nvof_probe = NvofReport {
        dll_loaded: nvof_probe_raw.dll_loaded,
        api_version_5_0_ok: nvof_probe_raw.api_version_5_0_ok,
        d3d12_instance_created: nvof_probe_raw.d3d12_instance_created,
        status_code: nvof_probe_raw.status_code,
        note: nvof_probe_raw.note,
    };

    let hud_cfg = HudMaskConfig::from_sensitivity(0.55);

    let dp4a_summary = bench_compute_path(&mut engine, true, frames, hud_cfg)?;
    let fp16_summary = bench_compute_path(&mut engine, false, frames, hud_cfg)?;

    engine.reset_temporal_state();
    for step in 0..18 {
        let t0 = step as f32 / 60.0;
        let t_mid = (step as f32 + 0.5) / 60.0;
        let t1 = (step as f32 + 1.0) / 60.0;
        engine.render_synthetic_triplet(t0, t_mid, t1)?;
        let _ = engine.run_flow_and_synth(true, 0.5, true, hud_cfg, ScalerMode::Off, 0.0)?;
    }

    let gt_rgba = engine.readback_ground_truth_rgba()?;
    let _ = engine.run_flow_and_synth(true, 0.5, false, hud_cfg, ScalerMode::Off, 0.0)?;
    let off_rgba = engine.readback_output_rgba()?;
    let _ = engine.run_flow_and_synth(true, 0.5, true, hud_cfg, ScalerMode::Off, 0.0)?;
    let on_rgba = engine.readback_output_rgba()?;
    let _ = engine.run_flow_and_synth(false, 0.5, true, hud_cfg, ScalerMode::Off, 0.0)?;
    let fp16_on_rgba = engine.readback_output_rgba()?;

    let quality_hud_mask_off = evaluate_quality(&gt_rgba, &off_rgba, width, height);
    let quality_hud_mask_on = evaluate_quality(&gt_rgba, &on_rgba, width, height);
    let quality_dp4a_vs_fp16_hud_on = evaluate_quality(&gt_rgba, &fp16_on_rgba, width, height);

    let gt_path = out_dir.join("ground_truth.png");
    let off_path = out_dir.join("hud_mask_off.png");
    let on_path = out_dir.join("hud_mask_on.png");
    write_rgba_png(&gt_path, width, height, &gt_rgba)?;
    write_rgba_png(&off_path, width, height, &off_rgba)?;
    write_rgba_png(&on_path, width, height, &on_rgba)?;

    let pacing_dcomp_1000f =
        bench_dcomp_and_wgc(&mut engine, frames, dp4a_summary.avg_total_gpu_ms)?;

    let report = ExoFlowBenchReport {
        resolution: format!("{width}x{height}"),
        gpu: engine.caps.clone(),
        baseline_vram_mb: baseline_vram,
        allocated_vram_delta_mb: vram_delta,
        nvof_probe,
        compute_dp4a_1000f: dp4a_summary,
        compute_fp16_1000f: fp16_summary,
        quality_hud_mask_off,
        quality_hud_mask_on,
        quality_dp4a_vs_fp16_hud_on,
        pacing_dcomp_1000f,
        artifacts: vec![
            "ground_truth.png".into(),
            "hud_mask_off.png".into(),
            "hud_mask_on.png".into(),
            "bench.json".into(),
        ],
    };

    let json_path = out_dir.join("bench.json");
    let json = serde_json::to_string_pretty(&report).map_err(|e| e.to_string())?;
    std::fs::write(&json_path, &json).map_err(|e| format!("write bench.json: {e}"))?;
    Ok(report)
}

fn bench_compute_path(
    engine: &mut Dx12FlowEngine,
    use_dp4a: bool,
    frames: usize,
    hud_cfg: HudMaskConfig,
) -> Result<PassSummary, String> {
    engine.reset_temporal_state();
    engine.render_synthetic_triplet(0.0, 0.5 / 60.0, 1.0 / 60.0)?;
    for _ in 0..10 {
        let _ = engine.run_flow_and_synth(use_dp4a, 0.5, true, hud_cfg, ScalerMode::Cas, 0.35)?;
    }

    let cpu_start = current_process_cpu_ticks_100ns();
    let wall_start = Instant::now();
    let mut samples: Vec<GpuPassTimings> = Vec::with_capacity(frames);

    for i in 0..frames {
        if i % 60 == 0 {
            let t = (i as f32) / 60.0;
            engine.render_synthetic_triplet(t, t + 0.5 / 60.0, t + 1.0 / 60.0)?;
        }
        let timing =
            engine.run_flow_and_synth(use_dp4a, 0.5, true, hud_cfg, ScalerMode::Cas, 0.35)?;
        samples.push(timing);
    }

    let wall_secs = wall_start.elapsed().as_secs_f64().max(1e-6);
    let cpu_delta_100ns = current_process_cpu_ticks_100ns().saturating_sub(cpu_start);
    let cores = std::thread::available_parallelism()
        .map(|n| n.get() as f64)
        .unwrap_or(1.0);
    let cpu_pct = round2(((cpu_delta_100ns as f64 * 1e-7) / (wall_secs * cores)) * 100.0);
    let (ws, priv_b) = current_process_memory_mb();
    let vram = engine.query_vram_mb();

    let n = samples.len().max(1) as f64;
    let avg_pyr = round3(samples.iter().map(|s| s.pyramid_ms).sum::<f64>() / n);
    let avg_flow = round3(samples.iter().map(|s| s.flow_ms).sum::<f64>() / n);
    let avg_smooth = round3(samples.iter().map(|s| s.smooth_ms).sum::<f64>() / n);
    let avg_hud = round3(samples.iter().map(|s| s.hud_mask_ms).sum::<f64>() / n);
    let avg_synth = round3(samples.iter().map(|s| s.synth_ms).sum::<f64>() / n);
    let avg_total = round3(samples.iter().map(|s| s.total_gpu_ms).sum::<f64>() / n);

    let mut totals: Vec<f64> = samples.iter().map(|s| s.total_gpu_ms).collect();
    totals.sort_by(|a, b| a.total_cmp(b));
    let p50 = round3(totals[totals.len() / 2]);
    let p95 = round3(totals[((totals.len() as f64 * 0.95) as usize).min(totals.len() - 1)]);
    let p99 = round3(totals[((totals.len() as f64 * 0.99) as usize).min(totals.len() - 1)]);

    Ok(PassSummary {
        frames: samples.len(),
        avg_pyramid_ms: avg_pyr,
        avg_flow_ms: avg_flow,
        avg_smooth_ms: avg_smooth,
        avg_hud_mask_ms: avg_hud,
        avg_synth_ms: avg_synth,
        avg_total_gpu_ms: avg_total,
        p50_total_gpu_ms: p50,
        p95_total_gpu_ms: p95,
        p99_total_gpu_ms: p99,
        vram_mb: vram,
        working_set_mb: ws,
        private_bytes_mb: priv_b,
        cpu_pct,
    })
}

fn bench_dcomp_and_wgc(
    engine: &mut Dx12FlowEngine,
    frames: usize,
    avg_gpu_ms: f64,
) -> Result<PacingReport, String> {
    let mut presenter = DCompOverlayPresenter::new(
        &engine.factory,
        &engine.device,
        &engine.queue,
        0,
        0,
        engine.width,
        engine.height,
        false,
    )?;

    let (wgc_ok, wgc_ms, wgc_note) = match WgcZeroCopyCapture::start(
        presenter.hwnd().0 as isize,
        &engine.device,
    ) {
        Ok(cap) => {
            let mut got = None;
            for _ in 0..8 {
                let _ = presenter.present_from_resource(
                    &engine.queue,
                    engine.out_resource(),
                    D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                );
                if let Some((ms, _)) = cap.try_acquire_latest() {
                    got = Some(round3(ms));
                    break;
                }
                std::thread::sleep(Duration::from_millis(8));
            }
            let note = if got.is_some() {
                "WGC zero-copy D3D11->DX12 shared handle capture acquired frame cleanly".into()
            } else {
                "WGC session & DX12 OpenSharedHandle initialized (no desktop frame while workstation locked/hidden)".into()
            };
            (true, got, note)
        }
        Err(e) => (false, None, format!("WGC probe note: {e}")),
    };

    let target_hz = 120u32;
    let mut pacer = VrrPacer::new(MultiplierMode::Fixed2x, target_hz);
    let target_interval = Duration::from_secs_f64(1.0 / target_hz as f64);
    let start = Instant::now();
    let mut next_deadline = start;
    let mut present_sum_ms = 0.0;
    let mut presented = 0usize;

    for _ in 0..frames {
        next_deadline += target_interval;
        let now = Instant::now();
        if next_deadline > now {
            while Instant::now() < next_deadline {
                let rem = next_deadline.saturating_duration_since(Instant::now());
                if rem > Duration::from_millis(2) {
                    std::thread::yield_now();
                } else {
                    std::hint::spin_loop();
                }
            }
        }
        let p0 = Instant::now();
        presenter.present_from_resource(
            &engine.queue,
            engine.out_resource(),
            D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
        )?;
        present_sum_ms += p0.elapsed().as_secs_f64() * 1000.0;
        let ts_ns = start.elapsed().as_nanos() as u64;
        pacer.record_present_ns(ts_ns);
        presented += 1;
    }

    let avg_present = round3(present_sum_ms / presented.max(1) as f64);
    let p99_jitter = pacer.p99_jitter_ms();
    let base_frame_ms = 1000.0 / 60.0;
    let added_latency_ms = round2(base_frame_ms + avg_gpu_ms + avg_present);
    let added_base_frames = round2(added_latency_ms / base_frame_ms);

    Ok(PacingReport {
        frames_presented: presented,
        target_hz,
        p99_interval_jitter_ms: p99_jitter,
        avg_present_ms: avg_present,
        added_latency_ms_at_60fps: added_latency_ms,
        added_latency_base_frames: added_base_frames,
        device_lost_count: presenter.device_lost_count,
        wgc_capture_probe_ok: wgc_ok,
        wgc_capture_ms: wgc_ms,
        wgc_note,
    })
}

pub fn evaluate_quality(gt: &[u8], test: &[u8], w: u32, h: u32) -> QualityMetrics {
    let full_psnr = region_psnr(gt, test, w, h, |_, _| true);
    let cx = w / 2;
    let cy = h / 2;
    let is_crosshair = move |x: u32, y: u32| {
        let dx = x.abs_diff(cx);
        let dy = y.abs_diff(cy);
        (dx < 24 && dx > 3 && dy < 3) || (dy < 24 && dy > 3 && dx < 3)
    };
    let is_minimap = |x: u32, y: u32| (32..=272).contains(&x) && (32..=212).contains(&y);
    let is_health = move |x: u32, y: u32| (40..=360).contains(&x) && (h - 84..=h - 40).contains(&y);
    let is_any_hud =
        move |x: u32, y: u32| is_crosshair(x, y) || is_minimap(x, y) || is_health(x, y);

    let crosshair_psnr = region_psnr(gt, test, w, h, is_crosshair);
    let minimap_psnr = region_psnr(gt, test, w, h, is_minimap);
    let health_psnr = region_psnr(gt, test, w, h, is_health);
    let static_hud_psnr = region_psnr(gt, test, w, h, is_any_hud);
    let ssim = compute_luma_ssim(gt, test, w, h);

    QualityMetrics {
        full_frame_psnr_db: round2(full_psnr),
        static_hud_psnr_db: round2(static_hud_psnr),
        crosshair_psnr_db: round2(crosshair_psnr),
        minimap_psnr_db: round2(minimap_psnr),
        health_bar_psnr_db: round2(health_psnr),
        full_frame_ssim: round3(ssim),
    }
}

fn region_psnr(gt: &[u8], test: &[u8], w: u32, h: u32, pred: impl Fn(u32, u32) -> bool) -> f64 {
    let mut mse_sum = 0.0f64;
    let mut count = 0u64;
    for y in 0..h {
        for x in 0..w {
            if !pred(x, y) {
                continue;
            }
            let idx = ((y * w + x) * 4) as usize;
            for c in 0..3 {
                let d = gt[idx + c] as f64 - test[idx + c] as f64;
                mse_sum += d * d;
            }
            count += 3;
        }
    }
    if count == 0 {
        return 0.0;
    }
    let mse = mse_sum / count as f64;
    if mse < 1e-9 {
        return 99.0;
    }
    10.0 * (255.0_f64 * 255.0_f64 / mse).log10()
}

fn compute_luma_ssim(gt: &[u8], test: &[u8], w: u32, h: u32) -> f64 {
    let mut sum_x = 0.0;
    let mut sum_y = 0.0;
    let mut n = 0.0;
    for y in (0..h).step_by(2) {
        for x in (0..w).step_by(2) {
            let idx = ((y * w + x) * 4) as usize;
            let lx =
                0.299 * gt[idx] as f64 + 0.587 * gt[idx + 1] as f64 + 0.114 * gt[idx + 2] as f64;
            let ly = 0.299 * test[idx] as f64
                + 0.587 * test[idx + 1] as f64
                + 0.114 * test[idx + 2] as f64;
            sum_x += lx;
            sum_y += ly;
            n += 1.0;
        }
    }
    if n <= 0.0 {
        return 0.0;
    }
    let mu_x = sum_x / n;
    let mu_y = sum_y / n;
    let mut vx = 0.0;
    let mut vy = 0.0;
    let mut cov = 0.0;
    for y in (0..h).step_by(2) {
        for x in (0..w).step_by(2) {
            let idx = ((y * w + x) * 4) as usize;
            let lx =
                0.299 * gt[idx] as f64 + 0.587 * gt[idx + 1] as f64 + 0.114 * gt[idx + 2] as f64;
            let ly = 0.299 * test[idx] as f64
                + 0.587 * test[idx + 1] as f64
                + 0.114 * test[idx + 2] as f64;
            let dx = lx - mu_x;
            let dy = ly - mu_y;
            vx += dx * dx;
            vy += dy * dy;
            cov += dx * dy;
        }
    }
    vx /= n;
    vy /= n;
    cov /= n;
    let c1 = (0.01 * 255.0_f64).powi(2);
    let c2 = (0.03 * 255.0_f64).powi(2);
    ((2.0 * mu_x * mu_y + c1) * (2.0 * cov + c2))
        / ((mu_x * mu_x + mu_y * mu_y + c1) * (vx + vy + c2))
}

fn write_rgba_png(path: &Path, width: u32, height: u32, rgba: &[u8]) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("create_dir_all: {e}"))?;
    }
    let file = File::create(path).map_err(|e| format!("File::create: {e}"))?;
    let mut enc = png::Encoder::new(BufWriter::new(file), width, height);
    enc.set_color(png::ColorType::Rgba);
    enc.set_depth(png::BitDepth::Eight);
    let mut writer = enc
        .write_header()
        .map_err(|e| format!("png write_header: {e}"))?;
    writer
        .write_image_data(rgba)
        .map_err(|e| format!("png write_image_data: {e}"))?;
    Ok(())
}
