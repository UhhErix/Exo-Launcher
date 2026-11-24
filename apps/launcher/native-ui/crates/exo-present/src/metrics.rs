use serde::{Deserialize, Serialize};

pub const ROLLING_CAPACITY: usize = 4096;
pub const STUTTER_FACTOR: f64 = 2.5;
pub const STUTTER_MIN_OVER_MS: f64 = 8.0;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum PresentMode {
    #[default]
    Unknown,
    HardwareLegacyFlip,
    HardwareIndependentFlip,
    HardwareComposedIndependentFlip,
    ComposedFlip,
    ComposedCopyGpuGdi,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct FrameSample {
    pub pid: u32,
    pub qpc_ticks: i64,
    pub frame_time_ms: f64,
    pub cpu_present_ms: f64,
    pub gpu_busy_ms: Option<f64>,
    pub display_latency_ms: Option<f64>,
    pub dropped: bool,
    pub kernel_source: bool,
    pub present_mode: PresentMode,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct FrameMetrics {
    pub frame_count: u64,
    pub window_frames: usize,
    pub fps: f64,
    pub avg_ms: f64,
    pub median_ms: f64,
    pub p95_ms: f64,
    pub p99_ms: f64,
    pub p999_ms: f64,
    pub max_ms: f64,
    pub low_1pct_fps: f64,
    pub low_01pct_fps: f64,
    pub avg_cpu_present_ms: f64,
    pub avg_gpu_busy_ms: Option<f64>,
    pub avg_display_latency_ms: Option<f64>,
    pub dropped_frames: u64,
    pub dropped_pct: f64,
    pub stutter_count: u64,
    pub events_lost: u64,
    pub present_mode: PresentMode,
}

#[derive(Clone, Debug)]
pub struct RollingMetrics {
    ring: Vec<f64>,
    cursor: usize,
    filled: usize,
    total_frames: u64,
    total_ms: f64,
    total_cpu_present_ms: f64,
    gpu_busy_sum: f64,
    gpu_busy_count: u64,
    display_lat_sum: f64,
    display_lat_count: u64,
    dropped_frames: u64,
    stutter_count: u64,
    events_lost: u64,
    max_ms: f64,
    last_present_mode: PresentMode,
    scratch: Vec<f64>,
}

impl Default for RollingMetrics {
    fn default() -> Self {
        Self::with_capacity(ROLLING_CAPACITY)
    }
}

impl RollingMetrics {
    pub fn with_capacity(capacity: usize) -> Self {
        let cap = capacity.max(64);
        Self {
            ring: vec![0.0; cap],
            cursor: 0,
            filled: 0,
            total_frames: 0,
            total_ms: 0.0,
            total_cpu_present_ms: 0.0,
            gpu_busy_sum: 0.0,
            gpu_busy_count: 0,
            display_lat_sum: 0.0,
            display_lat_count: 0,
            dropped_frames: 0,
            stutter_count: 0,
            events_lost: 0,
            max_ms: 0.0,
            last_present_mode: PresentMode::Unknown,
            scratch: Vec::with_capacity(cap),
        }
    }

    pub fn clear(&mut self) {
        self.cursor = 0;
        self.filled = 0;
        self.total_frames = 0;
        self.total_ms = 0.0;
        self.total_cpu_present_ms = 0.0;
        self.gpu_busy_sum = 0.0;
        self.gpu_busy_count = 0;
        self.display_lat_sum = 0.0;
        self.display_lat_count = 0;
        self.dropped_frames = 0;
        self.stutter_count = 0;
        self.events_lost = 0;
        self.max_ms = 0.0;
        self.last_present_mode = PresentMode::Unknown;
    }

    pub fn record_events_lost(&mut self, lost: u64) {
        self.events_lost = self.events_lost.saturating_add(lost);
    }

    pub fn push(&mut self, sample: FrameSample) -> bool {
        let dt = sample.frame_time_ms;
        if !dt.is_finite() || dt <= 0.05 || dt > 10_000.0 {
            return false;
        }
        let is_stutter = self.detect_stutter(dt);
        if is_stutter {
            self.stutter_count = self.stutter_count.saturating_add(1);
        }
        self.ring[self.cursor] = dt;
        self.cursor = (self.cursor + 1) % self.ring.len();
        if self.filled < self.ring.len() {
            self.filled += 1;
        }
        self.total_frames = self.total_frames.saturating_add(1);
        self.total_ms += dt;
        if sample.cpu_present_ms.is_finite() && sample.cpu_present_ms >= 0.0 {
            self.total_cpu_present_ms += sample.cpu_present_ms;
        }
        if let Some(gb) = sample.gpu_busy_ms
            && gb.is_finite()
            && gb >= 0.0
        {
            self.gpu_busy_sum += gb;
            self.gpu_busy_count += 1;
        }
        if let Some(dl) = sample.display_latency_ms
            && dl.is_finite()
            && dl >= 0.0
        {
            self.display_lat_sum += dl;
            self.display_lat_count += 1;
        }
        if sample.dropped {
            self.dropped_frames = self.dropped_frames.saturating_add(1);
        }
        if dt > self.max_ms {
            self.max_ms = dt;
        }
        if sample.present_mode != PresentMode::Unknown {
            self.last_present_mode = sample.present_mode;
        }
        is_stutter
    }

    fn detect_stutter(&mut self, dt_ms: f64) -> bool {
        if self.filled < 30 {
            return false;
        }
        let lookback = self.filled.min(180);
        self.scratch.clear();
        let len = self.ring.len();
        for i in 1..=lookback {
            let idx = (self.cursor + len - i) % len;
            self.scratch.push(self.ring[idx]);
        }
        self.scratch.sort_unstable_by(f64::total_cmp);
        let median = self.scratch[self.scratch.len() / 2];
        dt_ms > median * STUTTER_FACTOR && (dt_ms - median) >= STUTTER_MIN_OVER_MS
    }

    pub fn recent_frame_times(&self, max_count: usize) -> Vec<f64> {
        let count = self.filled.min(max_count);
        let len = self.ring.len();
        let mut out = Vec::with_capacity(count);
        let start = (self.cursor + len - count) % len;
        for i in 0..count {
            out.push(self.ring[(start + i) % len]);
        }
        out
    }

    pub fn snapshot(&mut self) -> FrameMetrics {
        if self.filled == 0 {
            return FrameMetrics {
                events_lost: self.events_lost,
                ..Default::default()
            };
        }
        self.scratch.clear();
        if self.filled == self.ring.len() {
            self.scratch.extend_from_slice(&self.ring);
        } else {
            self.scratch.extend_from_slice(&self.ring[..self.filled]);
        }
        let window_sum: f64 = self.scratch.iter().copied().sum();
        let avg_ms = window_sum / self.scratch.len() as f64;
        let fps = if avg_ms > 0.0 { 1000.0 / avg_ms } else { 0.0 };

        self.scratch.sort_unstable_by(f64::total_cmp);
        let median_ms = percentile_sorted(&self.scratch, 0.50);
        let p95_ms = percentile_sorted(&self.scratch, 0.95);
        let p99_ms = percentile_sorted(&self.scratch, 0.99);
        let p999_ms = percentile_sorted(&self.scratch, 0.999);
        let low_1pct_fps = if p99_ms > 0.0 { 1000.0 / p99_ms } else { 0.0 };
        let low_01pct_fps = if p999_ms > 0.0 { 1000.0 / p999_ms } else { 0.0 };
        let avg_cpu_present_ms = if self.total_frames > 0 {
            self.total_cpu_present_ms / self.total_frames as f64
        } else {
            0.0
        };
        let avg_gpu_busy_ms =
            (self.gpu_busy_count > 0).then(|| self.gpu_busy_sum / self.gpu_busy_count as f64);
        let avg_display_latency_ms = (self.display_lat_count > 0)
            .then(|| self.display_lat_sum / self.display_lat_count as f64);
        let dropped_pct = if self.total_frames > 0 {
            (self.dropped_frames as f64 * 100.0) / self.total_frames as f64
        } else {
            0.0
        };

        FrameMetrics {
            frame_count: self.total_frames,
            window_frames: self.filled,
            fps,
            avg_ms,
            median_ms,
            p95_ms,
            p99_ms,
            p999_ms,
            max_ms: self.max_ms,
            low_1pct_fps,
            low_01pct_fps,
            avg_cpu_present_ms,
            avg_gpu_busy_ms,
            avg_display_latency_ms,
            dropped_frames: self.dropped_frames,
            dropped_pct,
            stutter_count: self.stutter_count,
            events_lost: self.events_lost,
            present_mode: self.last_present_mode,
        }
    }
}

pub fn percentile_sorted(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    let clamped = p.clamp(0.0, 1.0);
    let idx = ((sorted.len() - 1) as f64 * clamped).ceil() as usize;
    sorted[idx.min(sorted.len() - 1)]
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(dt: f64) -> FrameSample {
        FrameSample {
            pid: 100,
            qpc_ticks: 0,
            frame_time_ms: dt,
            cpu_present_ms: 0.4,
            gpu_busy_ms: Some(dt * 0.7),
            display_latency_ms: Some(dt * 1.1),
            dropped: false,
            kernel_source: false,
            present_mode: PresentMode::HardwareIndependentFlip,
        }
    }

    #[test]
    fn steady_120_fps_metrics_match_expected() {
        let mut m = RollingMetrics::default();
        for _ in 0..240 {
            assert!(!m.push(sample(1000.0 / 120.0)));
        }
        let s = m.snapshot();
        assert_eq!(s.frame_count, 240);
        assert!((s.fps - 120.0).abs() < 0.05);
        assert!((s.low_1pct_fps - 120.0).abs() < 0.05);
        assert_eq!(s.stutter_count, 0);
        assert!((s.avg_gpu_busy_ms.unwrap() - (1000.0 / 120.0) * 0.7).abs() < 0.05);
    }

    #[test]
    fn stutter_detection_ignores_tiny_high_fps_bump_and_catches_real_hitch() {
        let mut m = RollingMetrics::default();
        for _ in 0..60 {
            assert!(!m.push(sample(2.5)));
        }
        assert!(!m.push(sample(6.5)));
        assert!(m.push(sample(25.0)));
        let s = m.snapshot();
        assert_eq!(s.stutter_count, 1);
        assert!((s.max_ms - 25.0).abs() < 0.01);
    }
}
