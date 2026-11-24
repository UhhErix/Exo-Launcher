use crate::metrics::{
    PresentMode, RollingMetrics, STUTTER_FACTOR, STUTTER_MIN_OVER_MS, percentile_sorted,
};
use crate::state_machine::{
    DXGI_PRESENT_MPO_START, DXGI_PRESENT_MPO_STOP, DXGI_PRESENT_START, DXGI_PRESENT_STOP,
    DXGI_PRESENT_TEST_FLAG, DXGI_PROVIDER_FIRST, DXGKRNL_BLIT, DXGKRNL_FLIP, DXGKRNL_FLIP_MPO,
    DXGKRNL_PRESENT_HISTORY_DETAILED, DXGKRNL_PROVIDER_FIRST, DXGKRNL_VSYNC_DPC,
    DXGKRNL_VSYNC_DPC_MPO, PAUSE_RESET_GAP_MS, PresentStateMachine, RawPresentEvent,
    classify_event,
};
use serde::{Deserialize, Serialize};
use std::time::Instant;

const QPC_FREQ: i64 = 10_000_000;
const TICKS_PER_MS: f64 = 10_000.0;

#[derive(Clone, Copy, Debug)]
pub struct SyntheticEvent {
    pub provider_first: u32,
    pub event_id: u16,
    pub pid: u32,
    pub tid: u32,
    pub qpc_ticks: i64,
    pub present_flags: u32,
    pub swapchain_ptr: u64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ReplayScenarioReport {
    pub name: String,
    pub frames_exo: u64,
    pub frames_ref: u64,
    pub exo_fps: f64,
    pub ref_fps: f64,
    pub exo_avg_ms: f64,
    pub ref_avg_ms: f64,
    pub avg_ms_err: f64,
    pub max_per_frame_err_ms: f64,
    pub exo_median_ms: f64,
    pub ref_median_ms: f64,
    pub exo_p99_ms: f64,
    pub ref_p99_ms: f64,
    pub p99_ms_err: f64,
    pub exo_low_1pct_fps: f64,
    pub ref_low_1pct_fps: f64,
    pub exo_cpu_present_ms: f64,
    pub ref_cpu_present_ms: f64,
    pub exo_display_latency_ms: Option<f64>,
    pub ref_display_latency_ms: Option<f64>,
    pub exo_present_mode: PresentMode,
    pub ref_present_mode: PresentMode,
    pub present_mode_match: bool,
    pub stutter_count_exo: u64,
    pub stutter_count_ref: u64,
    pub pass: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct OfflineAccuracyReport {
    pub qpc_freq_hz: i64,
    pub scenarios: Vec<ReplayScenarioReport>,
    pub all_passed: bool,
    pub max_per_frame_err_ms: f64,
    pub max_avg_ms_err: f64,
    pub max_p99_ms_err: f64,
    pub throughput_10k_frames_ms: f64,
    pub us_per_frame: f64,
    pub estimated_cpu_pct_at_240fps_12core: f64,
}

fn ms_to_ticks(ms: f64) -> i64 {
    (ms * TICKS_PER_MS).round() as i64
}

fn deterministic_jitter_ms(idx: usize, amplitude_ms: f64) -> f64 {
    let x = ((idx as u64)
        .wrapping_mul(6364136223846793005)
        .wrapping_add(1442695040888963407)
        >> 33) as f64
        / ((1u64 << 31) as f64);
    (x * 2.0 - 1.0) * amplitude_ms
}

fn reference_stutter_count(dts: &[f64]) -> u64 {
    let mut ring = Vec::with_capacity(dts.len());
    let mut scratch = Vec::with_capacity(180);
    let mut stutters = 0u64;
    for &dt in dts {
        if ring.len() >= 30 {
            let lookback = ring.len().min(180);
            scratch.clear();
            scratch.extend_from_slice(&ring[ring.len() - lookback..]);
            scratch.sort_unstable_by(f64::total_cmp);
            let median = scratch[scratch.len() / 2];
            if dt > median * STUTTER_FACTOR && (dt - median) >= STUTTER_MIN_OVER_MS {
                stutters += 1;
            }
        }
        ring.push(dt);
    }
    stutters
}

fn replay_and_compare(
    name: &str,
    pid: u32,
    events: &[SyntheticEvent],
    ref_dts_ms: &[f64],
    ref_cpu_present_ms: f64,
    ref_display_latency_ms: Option<f64>,
    ref_present_mode: PresentMode,
) -> ReplayScenarioReport {
    let mut sm = PresentStateMachine::new(QPC_FREQ);
    sm.watch(&[pid]);
    let mut metrics = RollingMetrics::default();
    let mut exo_dts = Vec::with_capacity(ref_dts_ms.len());

    for ev in events {
        let kind = classify_event(ev.provider_first, ev.event_id);
        let raw = RawPresentEvent {
            pid: ev.pid,
            tid: ev.tid,
            qpc_ticks: ev.qpc_ticks,
            kind,
            event_id: ev.event_id,
            present_flags: ev.present_flags,
            swapchain_ptr: ev.swapchain_ptr,
        };
        if let Some(sample) = sm.ingest(raw) {
            exo_dts.push(sample.frame_time_ms);
            metrics.push(sample);
        }
    }

    let snap = metrics.snapshot();
    let mut max_per_frame_err_ms = 0.0f64;
    for (a, b) in exo_dts.iter().zip(ref_dts_ms.iter()) {
        let err = (a - b).abs();
        if err > max_per_frame_err_ms {
            max_per_frame_err_ms = err;
        }
    }
    let ref_avg_ms = ref_dts_ms.iter().sum::<f64>() / ref_dts_ms.len().max(1) as f64;
    let ref_fps = if ref_avg_ms > 0.0 {
        1000.0 / ref_avg_ms
    } else {
        0.0
    };
    let mut sorted = ref_dts_ms.to_vec();
    sorted.sort_unstable_by(f64::total_cmp);
    let ref_median_ms = percentile_sorted(&sorted, 0.50);
    let ref_p99_ms = percentile_sorted(&sorted, 0.99);
    let ref_low_1pct_fps = if ref_p99_ms > 0.0 {
        1000.0 / ref_p99_ms
    } else {
        0.0
    };
    let stutter_count_ref = reference_stutter_count(ref_dts_ms);

    let avg_ms_err = (snap.avg_ms - ref_avg_ms).abs();
    let p99_ms_err = (snap.p99_ms - ref_p99_ms).abs();
    let present_mode_match = snap.present_mode == ref_present_mode;
    let count_match = snap.frame_count == ref_dts_ms.len() as u64;
    let cpu_ok = (snap.avg_cpu_present_ms - ref_cpu_present_ms).abs() < 0.05;
    let disp_ok = match (snap.avg_display_latency_ms, ref_display_latency_ms) {
        (Some(a), Some(b)) => (a - b).abs() < 0.05,
        (None, None) => true,
        _ => false,
    };
    let pass = count_match
        && present_mode_match
        && max_per_frame_err_ms < 0.001
        && avg_ms_err < 0.001
        && p99_ms_err < 0.005
        && snap.stutter_count == stutter_count_ref
        && cpu_ok
        && disp_ok;

    ReplayScenarioReport {
        name: name.to_string(),
        frames_exo: snap.frame_count,
        frames_ref: ref_dts_ms.len() as u64,
        exo_fps: snap.fps,
        ref_fps,
        exo_avg_ms: snap.avg_ms,
        ref_avg_ms,
        avg_ms_err,
        max_per_frame_err_ms,
        exo_median_ms: snap.median_ms,
        ref_median_ms,
        exo_p99_ms: snap.p99_ms,
        ref_p99_ms,
        p99_ms_err,
        exo_low_1pct_fps: snap.low_1pct_fps,
        ref_low_1pct_fps,
        exo_cpu_present_ms: snap.avg_cpu_present_ms,
        ref_cpu_present_ms,
        exo_display_latency_ms: snap.avg_display_latency_ms,
        ref_display_latency_ms,
        exo_present_mode: snap.present_mode,
        ref_present_mode,
        present_mode_match,
        stutter_count_exo: snap.stutter_count,
        stutter_count_ref,
        pass,
    }
}

fn scenario_dx12_independent_flip_144hz() -> ReplayScenarioReport {
    let pid = 4200;
    let base_ms = 1000.0 / 144.0;
    let cpu_ms = 0.42;
    let disp_ms = 2.10;
    let count = 1200;
    let mut events = Vec::with_capacity(count * 4);
    let mut ref_dts = Vec::with_capacity(count - 1);
    let mut t = QPC_FREQ;
    for i in 0..count {
        let dt_ms = base_ms + deterministic_jitter_ms(i, 0.35);
        let dt_ticks = ms_to_ticks(dt_ms);
        if i > 0 {
            ref_dts.push(dt_ticks as f64 / TICKS_PER_MS);
            t += dt_ticks;
        }
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_START,
            pid,
            tid: 1,
            qpc_ticks: t,
            present_flags: 0,
            swapchain_ptr: 0x1000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_STOP,
            pid,
            tid: 1,
            qpc_ticks: t + ms_to_ticks(cpu_ms),
            present_flags: 0,
            swapchain_ptr: 0x1000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_FLIP_MPO,
            pid,
            tid: 1,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_VSYNC_DPC_MPO,
            pid,
            tid: 1,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    replay_and_compare(
        "dx12_independent_flip_144hz",
        pid,
        &events,
        &ref_dts,
        cpu_ms,
        Some(disp_ms),
        PresentMode::HardwareIndependentFlip,
    )
}

fn scenario_dx11_legacy_flip_with_stutters_60hz() -> ReplayScenarioReport {
    let pid = 4201;
    let base_ms = 1000.0 / 60.0;
    let cpu_ms = 0.55;
    let disp_ms = 4.20;
    let count = 600;
    let mut events = Vec::with_capacity(count * 4);
    let mut ref_dts = Vec::with_capacity(count - 1);
    let mut t = QPC_FREQ;
    for i in 0..count {
        let dt_ms = match i {
            120 => 48.0,
            240 => 54.0,
            360 => 62.0,
            480 => 50.0,
            _ => base_ms + deterministic_jitter_ms(i, 0.25),
        };
        let dt_ticks = ms_to_ticks(dt_ms);
        if i > 0 {
            ref_dts.push(dt_ticks as f64 / TICKS_PER_MS);
            t += dt_ticks;
        }
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_START,
            pid,
            tid: 2,
            qpc_ticks: t,
            present_flags: 0,
            swapchain_ptr: 0x2000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_STOP,
            pid,
            tid: 2,
            qpc_ticks: t + ms_to_ticks(cpu_ms),
            present_flags: 0,
            swapchain_ptr: 0x2000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_FLIP,
            pid,
            tid: 2,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_VSYNC_DPC,
            pid,
            tid: 2,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    replay_and_compare(
        "dx11_legacy_flip_with_stutters_60hz",
        pid,
        &events,
        &ref_dts,
        cpu_ms,
        Some(disp_ms),
        PresentMode::HardwareLegacyFlip,
    )
}

fn scenario_composed_flip_present_test_and_alttab() -> ReplayScenarioReport {
    let pid = 4202;
    let base_ms = 1000.0 / 120.0;
    let cpu_ms = 0.30;
    let disp_ms = 8.40;
    let count = 400;
    let mut events = Vec::with_capacity(count * 4);
    let mut ref_dts = Vec::with_capacity(count - 2);
    let mut t = QPC_FREQ;
    for i in 0..count {
        let dt_ms = if i == 200 {
            PAUSE_RESET_GAP_MS + 400.0
        } else {
            base_ms + deterministic_jitter_ms(i, 0.20)
        };
        let dt_ticks = ms_to_ticks(dt_ms);
        if i > 0 {
            t += dt_ticks;
            if dt_ms <= PAUSE_RESET_GAP_MS {
                ref_dts.push(dt_ticks as f64 / TICKS_PER_MS);
            }
        }
        if i % 10 == 0 {
            events.push(SyntheticEvent {
                provider_first: DXGI_PROVIDER_FIRST,
                event_id: DXGI_PRESENT_START,
                pid,
                tid: 3,
                qpc_ticks: t - ms_to_ticks(0.1),
                present_flags: DXGI_PRESENT_TEST_FLAG,
                swapchain_ptr: 0x3000,
            });
        }
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_START,
            pid,
            tid: 3,
            qpc_ticks: t,
            present_flags: 0,
            swapchain_ptr: 0x3000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_STOP,
            pid,
            tid: 3,
            qpc_ticks: t + ms_to_ticks(cpu_ms),
            present_flags: 0,
            swapchain_ptr: 0x3000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_PRESENT_HISTORY_DETAILED,
            pid,
            tid: 3,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    replay_and_compare(
        "dxgi_composed_flip_present_test_and_alttab",
        pid,
        &events,
        &ref_dts,
        cpu_ms,
        Some(disp_ms),
        PresentMode::ComposedFlip,
    )
}

fn scenario_composed_copy_blt() -> ReplayScenarioReport {
    let pid = 4203;
    let base_ms = 1000.0 / 90.0;
    let cpu_ms = 0.65;
    let disp_ms = 6.50;
    let count = 240;
    let mut events = Vec::with_capacity(count * 3);
    let mut ref_dts = Vec::with_capacity(count - 1);
    let mut t = QPC_FREQ;
    for i in 0..count {
        let dt_ticks = ms_to_ticks(base_ms + deterministic_jitter_ms(i, 0.30));
        if i > 0 {
            ref_dts.push(dt_ticks as f64 / TICKS_PER_MS);
            t += dt_ticks;
        }
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_START,
            pid,
            tid: 4,
            qpc_ticks: t,
            present_flags: 0,
            swapchain_ptr: 0x4000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_STOP,
            pid,
            tid: 4,
            qpc_ticks: t + ms_to_ticks(cpu_ms),
            present_flags: 0,
            swapchain_ptr: 0x4000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_BLIT,
            pid,
            tid: 4,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    replay_and_compare(
        "dxgi_composed_copy_gpu_gdi_90hz",
        pid,
        &events,
        &ref_dts,
        cpu_ms,
        Some(disp_ms),
        PresentMode::ComposedCopyGpuGdi,
    )
}

fn scenario_hardware_composed_mpo_240hz() -> ReplayScenarioReport {
    let pid = 4204;
    let base_ms = 1000.0 / 240.0;
    let cpu_ms = 0.25;
    let disp_ms = 1.60;
    let count = 600;
    let mut events = Vec::with_capacity(count * 3);
    let mut ref_dts = Vec::with_capacity(count - 1);
    let mut t = QPC_FREQ;
    for i in 0..count {
        let dt_ticks = ms_to_ticks(base_ms + deterministic_jitter_ms(i, 0.15));
        if i > 0 {
            ref_dts.push(dt_ticks as f64 / TICKS_PER_MS);
            t += dt_ticks;
        }
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_MPO_START,
            pid,
            tid: 5,
            qpc_ticks: t,
            present_flags: 0,
            swapchain_ptr: 0x5000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGI_PROVIDER_FIRST,
            event_id: DXGI_PRESENT_MPO_STOP,
            pid,
            tid: 5,
            qpc_ticks: t + ms_to_ticks(cpu_ms),
            present_flags: 0,
            swapchain_ptr: 0x5000,
        });
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_VSYNC_DPC_MPO,
            pid,
            tid: 5,
            qpc_ticks: t + ms_to_ticks(disp_ms),
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    replay_and_compare(
        "dxgi_hardware_composed_mpo_240hz",
        pid,
        &events,
        &ref_dts,
        cpu_ms,
        Some(disp_ms),
        PresentMode::HardwareComposedIndependentFlip,
    )
}

fn scenario_vulkan_kernel_fallback_120hz() -> ReplayScenarioReport {
    let pid = 4205;
    let base_ms = 1000.0 / 120.0;
    let count = 480;
    let mut events = Vec::with_capacity(count);
    let mut ref_dts = Vec::with_capacity(count - 1);
    let mut t = QPC_FREQ;
    for i in 0..count {
        let dt_ticks = ms_to_ticks(base_ms + deterministic_jitter_ms(i, 0.25));
        if i > 0 {
            ref_dts.push(dt_ticks as f64 / TICKS_PER_MS);
            t += dt_ticks;
        }
        events.push(SyntheticEvent {
            provider_first: DXGKRNL_PROVIDER_FIRST,
            event_id: DXGKRNL_FLIP,
            pid,
            tid: 6,
            qpc_ticks: t,
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    replay_and_compare(
        "vulkan_kernel_fallback_120hz",
        pid,
        &events,
        &ref_dts,
        0.0,
        None,
        PresentMode::HardwareLegacyFlip,
    )
}

fn measure_10k_replay_throughput_ms() -> f64 {
    let pid = 9001;
    let frames = 10_000usize;
    let step = ms_to_ticks(1000.0 / 240.0);
    let cpu = ms_to_ticks(0.30);
    let mut sm = PresentStateMachine::new(QPC_FREQ);
    sm.watch(&[pid]);
    let mut metrics = RollingMetrics::default();
    let t0 = Instant::now();
    let mut qpc = QPC_FREQ;
    for _ in 0..frames {
        qpc += step;
        if let Some(s) = sm.ingest(RawPresentEvent {
            pid,
            tid: 1,
            qpc_ticks: qpc,
            kind: classify_event(DXGI_PROVIDER_FIRST, DXGI_PRESENT_START),
            event_id: DXGI_PRESENT_START,
            present_flags: 0,
            swapchain_ptr: 0x9000,
        }) {
            metrics.push(s);
        }
        let _ = sm.ingest(RawPresentEvent {
            pid,
            tid: 1,
            qpc_ticks: qpc + cpu,
            kind: classify_event(DXGI_PROVIDER_FIRST, DXGI_PRESENT_STOP),
            event_id: DXGI_PRESENT_STOP,
            present_flags: 0,
            swapchain_ptr: 0x9000,
        });
        let _ = sm.ingest(RawPresentEvent {
            pid,
            tid: 1,
            qpc_ticks: qpc + step / 2,
            kind: classify_event(DXGKRNL_PROVIDER_FIRST, DXGKRNL_FLIP_MPO),
            event_id: DXGKRNL_FLIP_MPO,
            present_flags: 0,
            swapchain_ptr: 0,
        });
    }
    let _ = metrics.snapshot();
    t0.elapsed().as_secs_f64() * 1000.0
}

pub fn run_offline_accuracy_suite() -> OfflineAccuracyReport {
    let scenarios = vec![
        scenario_dx12_independent_flip_144hz(),
        scenario_dx11_legacy_flip_with_stutters_60hz(),
        scenario_composed_flip_present_test_and_alttab(),
        scenario_composed_copy_blt(),
        scenario_hardware_composed_mpo_240hz(),
        scenario_vulkan_kernel_fallback_120hz(),
    ];
    let all_passed = scenarios.iter().all(|s| s.pass);
    let max_per_frame_err_ms = scenarios
        .iter()
        .map(|s| s.max_per_frame_err_ms)
        .fold(0.0f64, f64::max);
    let max_avg_ms_err = scenarios
        .iter()
        .map(|s| s.avg_ms_err)
        .fold(0.0f64, f64::max);
    let max_p99_ms_err = scenarios
        .iter()
        .map(|s| s.p99_ms_err)
        .fold(0.0f64, f64::max);
    let throughput_10k_frames_ms = measure_10k_replay_throughput_ms();
    let us_per_frame = (throughput_10k_frames_ms * 1000.0) / 10_000.0;
    let estimated_cpu_pct_at_240fps_12core = ((us_per_frame * 240.0) / 1_000_000.0 / 12.0) * 100.0;
    OfflineAccuracyReport {
        qpc_freq_hz: QPC_FREQ,
        scenarios,
        all_passed,
        max_per_frame_err_ms,
        max_avg_ms_err,
        max_p99_ms_err,
        throughput_10k_frames_ms,
        us_per_frame,
        estimated_cpu_pct_at_240fps_12core,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn offline_accuracy_suite_matches_presentmon_formulas_and_present_modes() {
        let report = run_offline_accuracy_suite();
        assert_eq!(report.scenarios.len(), 6);
        for s in &report.scenarios {
            assert!(
                s.pass,
                "scenario {} failed: err={:.6} avg_err={:.6} p99_err={:.6} mode={:?}/{:?} stutters={}/{}",
                s.name,
                s.max_per_frame_err_ms,
                s.avg_ms_err,
                s.p99_ms_err,
                s.exo_present_mode,
                s.ref_present_mode,
                s.stutter_count_exo,
                s.stutter_count_ref,
            );
        }
        assert!(report.all_passed);
        assert!(report.max_per_frame_err_ms < 0.001);
        assert!(report.max_avg_ms_err < 0.001);
        assert!(report.max_p99_ms_err < 0.005);
    }

    #[test]
    fn dx11_stutter_scenario_counts_exact_four_hitches() {
        let s = scenario_dx11_legacy_flip_with_stutters_60hz();
        assert!(s.pass);
        assert_eq!(s.stutter_count_exo, 4);
        assert_eq!(s.stutter_count_ref, 4);
        assert_eq!(s.exo_present_mode, PresentMode::HardwareLegacyFlip);
    }
}
