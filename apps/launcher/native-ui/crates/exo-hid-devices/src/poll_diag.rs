use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum PollingHealth {
    Good,
    Warn,
    Bad,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PollingReport {
    pub sample_count: usize,
    pub nominal_hz: u16,
    pub measured_hz: f64,
    pub mean_interval_us: f64,
    pub p50_interval_us: f64,
    pub p95_interval_us: f64,
    pub p99_interval_us: f64,
    pub jitter_pct: f64,
    pub late_packets: usize,
    pub health: PollingHealth,
    pub warning: Option<String>,
}

#[derive(Clone, Debug, Default)]
pub struct PollingAnalyzer {
    intervals_us: Vec<f64>,
    last_timestamp_us: Option<u64>,
}

impl PollingAnalyzer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn clear(&mut self) {
        self.intervals_us.clear();
        self.last_timestamp_us = None;
    }

    pub fn push_timestamp_us(&mut self, ts_us: u64) {
        if let Some(prev) = self.last_timestamp_us
            && ts_us > prev
        {
            let delta = (ts_us - prev) as f64;
            if (50.0..=50_000.0).contains(&delta) {
                self.intervals_us.push(delta);
            }
        }
        self.last_timestamp_us = Some(ts_us);
    }

    pub fn push_interval_us(&mut self, delta_us: f64) {
        if (50.0..=50_000.0).contains(&delta_us) {
            self.intervals_us.push(delta_us);
        }
    }

    pub fn len(&self) -> usize {
        self.intervals_us.len()
    }

    pub fn is_empty(&self) -> bool {
        self.intervals_us.is_empty()
    }

    pub fn report(&self) -> Option<PollingReport> {
        if self.intervals_us.is_empty() {
            return None;
        }
        let mut sorted = self.intervals_us.clone();
        sorted.sort_by(|a, b| a.total_cmp(b));
        let n = sorted.len();
        let sum: f64 = sorted.iter().sum();
        let mean = sum / n as f64;
        let measured_hz = if mean > 0.0 { 1_000_000.0 / mean } else { 0.0 };
        let nominal_hz = snap_nominal_hz(measured_hz);
        let expected_us = 1_000_000.0 / nominal_hz as f64;
        let p50 = percentile(&sorted, 0.50);
        let p95 = percentile(&sorted, 0.95);
        let p99 = percentile(&sorted, 0.99);
        let variance = sorted
            .iter()
            .map(|&v| {
                let d = v - expected_us;
                d * d
            })
            .sum::<f64>()
            / n as f64;
        let stddev = variance.sqrt();
        let jitter_pct = (stddev / expected_us) * 100.0;
        let late_threshold = expected_us * 1.5;
        let late_packets = sorted.iter().filter(|&&v| v > late_threshold).count();
        let (health, warning) = if jitter_pct > 25.0 {
            (
                PollingHealth::Bad,
                Some(format!(
                    "Polling jitter is {jitter_pct:.1}% at {nominal_hz} Hz — check USB port or wireless receiver distance."
                )),
            )
        } else if jitter_pct > 15.0 {
            (
                PollingHealth::Warn,
                Some(format!(
                    "Polling jitter is {jitter_pct:.1}% at {nominal_hz} Hz (above 15% threshold)."
                )),
            )
        } else {
            (PollingHealth::Good, None)
        };

        Some(PollingReport {
            sample_count: n,
            nominal_hz,
            measured_hz,
            mean_interval_us: mean,
            p50_interval_us: p50,
            p95_interval_us: p95,
            p99_interval_us: p99,
            jitter_pct,
            late_packets,
            health,
            warning,
        })
    }
}

fn snap_nominal_hz(hz: f64) -> u16 {
    const RATES: [u16; 7] = [125, 250, 500, 1000, 2000, 4000, 8000];
    let mut best = 1000u16;
    let mut best_dist = f64::MAX;
    for r in RATES {
        let dist = (hz - r as f64).abs();
        if dist < best_dist {
            best_dist = dist;
            best = r;
        }
    }
    best
}

fn percentile(sorted: &[f64], p: f64) -> f64 {
    if sorted.is_empty() {
        return 0.0;
    }
    let idx = ((sorted.len() - 1) as f64 * p).round() as usize;
    sorted[idx.min(sorted.len() - 1)]
}
