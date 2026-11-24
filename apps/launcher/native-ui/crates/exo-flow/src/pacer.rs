use crate::synth::{FlowScale, MultiplierMode};

#[allow(dead_code)]
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct GuardDecision {
    pub active: bool,
    pub recommended_flow_scale: FlowScale,
    pub reason: Option<&'static str>,
}

#[derive(Clone, Debug)]
pub struct VrrPacer {
    pub mode: MultiplierMode,
    pub monitor_hz: u32,
    pub vrr_headroom_hz: u32,
    pub smoothed_base_interval_ms: f64,
    pub last_output_ns: u64,
    pub intervals_ms: Vec<f64>,
}

impl VrrPacer {
    pub fn new(mode: MultiplierMode, monitor_hz: u32) -> Self {
        Self {
            mode,
            monitor_hz: monitor_hz.max(60),
            vrr_headroom_hz: 3,
            smoothed_base_interval_ms: 1000.0 / 60.0,
            last_output_ns: 0,
            intervals_ms: Vec::with_capacity(2048),
        }
    }

    pub fn effective_target_hz(&self) -> f64 {
        match self.mode {
            MultiplierMode::Fixed2x => (1000.0 / self.smoothed_base_interval_ms) * 2.0,
            MultiplierMode::Fixed3x => (1000.0 / self.smoothed_base_interval_ms) * 3.0,
            MultiplierMode::VrrAdaptive => {
                self.monitor_hz.saturating_sub(self.vrr_headroom_hz).max(48) as f64
            }
        }
    }

    pub fn update_base_frame_ms(&mut self, delta_ms: f64) {
        let clamped = delta_ms.clamp(2.0, 100.0);
        self.smoothed_base_interval_ms = self.smoothed_base_interval_ms * 0.85 + clamped * 0.15;
    }

    pub fn sub_frame_alphas(&self) -> Vec<f32> {
        match self.mode {
            MultiplierMode::Fixed2x => vec![0.5],
            MultiplierMode::Fixed3x => vec![1.0 / 3.0, 2.0 / 3.0],
            MultiplierMode::VrrAdaptive => {
                let target_interval = 1000.0 / self.effective_target_hz();
                let ratio = (self.smoothed_base_interval_ms / target_interval).clamp(1.0, 4.0);
                let total_slots = ratio.round().max(2.0) as usize;
                let synth_count = total_slots.saturating_sub(1);
                (1..=synth_count)
                    .map(|k| (k as f32) / (total_slots as f32))
                    .collect()
            }
        }
    }

    pub fn target_output_interval_ms(&self) -> f64 {
        1000.0 / self.effective_target_hz().max(30.0)
    }

    pub fn record_present_ns(&mut self, now_ns: u64) {
        if self.last_output_ns != 0 && now_ns > self.last_output_ns {
            let dt = (now_ns - self.last_output_ns) as f64 / 1_000_000.0;
            self.intervals_ms.push(dt);
        }
        self.last_output_ns = now_ns;
    }

    pub fn p99_jitter_ms(&self) -> f64 {
        if self.intervals_ms.len() < 2 {
            return 0.0;
        }
        let target = self.target_output_interval_ms();
        let mut devs: Vec<f64> = self
            .intervals_ms
            .iter()
            .map(|&v| (v - target).abs())
            .collect();
        devs.sort_by(|a, b| a.total_cmp(b));
        let idx = ((devs.len() as f64 * 0.99).floor() as usize).min(devs.len() - 1);
        (devs[idx] * 1000.0).round() / 1000.0
    }
}

#[allow(dead_code)]
pub fn recommended_base_fps_cap(monitor_hz: u32, mode: MultiplierMode) -> u32 {
    let ceiling = monitor_hz.saturating_sub(3).max(60);
    match mode {
        MultiplierMode::Fixed2x => (ceiling / 2).max(40),
        MultiplierMode::Fixed3x => (ceiling / 3).max(40),
        MultiplierMode::VrrAdaptive => {
            if monitor_hz >= 240 {
                80
            } else if monitor_hz >= 144 {
                72
            } else {
                60
            }
        }
    }
}

#[allow(dead_code)]
pub fn evaluate_latency_guard(
    base_fps: f32,
    gpu_busy_pct: f32,
    current: FlowScale,
) -> GuardDecision {
    if base_fps < 40.0 {
        return GuardDecision {
            active: false,
            recommended_flow_scale: FlowScale::Quarter,
            reason: Some("Base FPS below 40 FPS; pausing Exo Flow to avoid floaty latency."),
        };
    }
    if gpu_busy_pct > 95.0 {
        return GuardDecision {
            active: true,
            recommended_flow_scale: FlowScale::Quarter,
            reason: Some("GPU busy above 95%; scaling optical flow to 1/4 resolution."),
        };
    }
    if gpu_busy_pct < 80.0 && current == FlowScale::Quarter {
        return GuardDecision {
            active: true,
            recommended_flow_scale: FlowScale::Half,
            reason: None,
        };
    }
    GuardDecision {
        active: true,
        recommended_flow_scale: current,
        reason: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fixed_2x_and_3x_emit_exact_sub_frame_timestamps() {
        let p2 = VrrPacer::new(MultiplierMode::Fixed2x, 120);
        assert_eq!(p2.sub_frame_alphas(), vec![0.5]);

        let p3 = VrrPacer::new(MultiplierMode::Fixed3x, 180);
        let a3 = p3.sub_frame_alphas();
        assert_eq!(a3.len(), 2);
        assert!((a3[0] - 1.0 / 3.0).abs() < 1e-5);
        assert!((a3[1] - 2.0 / 3.0).abs() < 1e-5);
    }

    #[test]
    fn vrr_adaptive_locks_to_monitor_hz_minus_three() {
        let mut p = VrrPacer::new(MultiplierMode::VrrAdaptive, 144);
        assert!((p.effective_target_hz() - 141.0).abs() < 1e-6);
        p.smoothed_base_interval_ms = 1000.0 / 47.0;
        assert_eq!(p.sub_frame_alphas().len(), 2);
    }

    #[test]
    fn base_fps_cap_pairs_cleanly_with_common_vrr_displays() {
        assert_eq!(recommended_base_fps_cap(120, MultiplierMode::Fixed2x), 58);
        assert_eq!(
            recommended_base_fps_cap(144, MultiplierMode::VrrAdaptive),
            72
        );
        assert_eq!(
            recommended_base_fps_cap(240, MultiplierMode::VrrAdaptive),
            80
        );
    }

    #[test]
    fn latency_guard_pauses_below_40_fps_and_scales_down_above_95_gpu() {
        let low = evaluate_latency_guard(34.0, 70.0, FlowScale::Half);
        assert!(!low.active);
        let busy = evaluate_latency_guard(60.0, 97.5, FlowScale::Half);
        assert!(busy.active);
        assert_eq!(busy.recommended_flow_scale, FlowScale::Quarter);
        let healthy = evaluate_latency_guard(60.0, 72.0, FlowScale::Quarter);
        assert!(healthy.active);
        assert_eq!(healthy.recommended_flow_scale, FlowScale::Half);
    }
}
