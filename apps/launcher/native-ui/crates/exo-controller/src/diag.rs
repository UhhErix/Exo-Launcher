use serde::{Deserialize, Serialize};

pub const DRIFT_WARNING_THRESHOLD_PCT: f64 = 4.0;
pub const CIRCULARITY_BINS: usize = 36;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct RestDiag {
    pub offset_x_pct: f64,
    pub offset_y_pct: f64,
    pub radial_offset_pct: f64,
    pub drift_warning: bool,
    pub recommended_deadzone_pct: f64,
    pub noise_steps: u8,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct CircularityDiag {
    pub covered_bins: usize,
    pub avg_error_pct: f64,
    pub max_overshoot_pct: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PollingDiag {
    pub rate_hz: f64,
    pub p99_ms: f64,
    pub jitter_ms: f64,
    pub repeat_pct: Option<f64>,
    pub samples: usize,
}

pub fn analyze_rest(samples: &[(u8, u8)]) -> Option<RestDiag> {
    if samples.len() < 20 {
        return None;
    }
    const CENTER: f64 = 127.5;
    let n = samples.len() as f64;
    let avg_x = samples.iter().map(|&(x, _)| f64::from(x)).sum::<f64>() / n;
    let avg_y = samples.iter().map(|&(_, y)| f64::from(y)).sum::<f64>() / n;
    let ox = (avg_x - CENTER) / CENTER * 100.0;
    let oy = (avg_y - CENTER) / CENTER * 100.0;
    let radial = (ox * ox + oy * oy).sqrt();
    let min_x = samples.iter().map(|&(x, _)| x).min().unwrap_or(128);
    let max_x = samples.iter().map(|&(x, _)| x).max().unwrap_or(128);
    let min_y = samples.iter().map(|&(_, y)| y).min().unwrap_or(128);
    let max_y = samples.iter().map(|&(_, y)| y).max().unwrap_or(128);
    let noise_steps = (max_x - min_x).max(max_y - min_y);
    let drift_warning = radial > DRIFT_WARNING_THRESHOLD_PCT;
    let recommended_deadzone_pct = if drift_warning {
        (radial + 2.0).ceil().min(20.0)
    } else {
        3.0
    };
    Some(RestDiag {
        offset_x_pct: round2(ox),
        offset_y_pct: round2(oy),
        radial_offset_pct: round2(radial),
        drift_warning,
        recommended_deadzone_pct,
        noise_steps,
    })
}

pub fn analyze_circularity(samples: &[(u8, u8)]) -> Option<CircularityDiag> {
    if samples.len() < 36 {
        return None;
    }
    const CENTER: f64 = 127.5;
    let mut bins = [0.0f64; CIRCULARITY_BINS];
    for &(x, y) in samples {
        let dx = (f64::from(x) - CENTER) / CENTER;
        let dy = (f64::from(y) - CENTER) / CENTER;
        let r = (dx * dx + dy * dy).sqrt();
        if r < 0.70 {
            continue;
        }
        let mut angle = dy.atan2(dx);
        if angle < 0.0 {
            angle += std::f64::consts::TAU;
        }
        let bin =
            ((angle / std::f64::consts::TAU) * CIRCULARITY_BINS as f64) as usize % CIRCULARITY_BINS;
        if r > bins[bin] {
            bins[bin] = r;
        }
    }
    let filled: Vec<f64> = bins.into_iter().filter(|&r| r > 0.0).collect();
    if filled.len() < 18 {
        return None;
    }
    let avg_err =
        filled.iter().map(|&r| (r - 1.0).abs() * 100.0).sum::<f64>() / filled.len() as f64;
    let max_over = filled
        .iter()
        .map(|&r| ((r - 1.0).max(0.0)) * 100.0)
        .fold(0.0f64, f64::max);
    Some(CircularityDiag {
        covered_bins: filled.len(),
        avg_error_pct: round2(avg_err),
        max_overshoot_pct: round2(max_over),
    })
}

pub fn measure_polling(
    timestamps_us: &[u64],
    sensor_clocks: Option<&[u32]>,
) -> Option<PollingDiag> {
    if timestamps_us.len() < 20 {
        return None;
    }
    let mut intervals_ms = Vec::with_capacity(timestamps_us.len() - 1);
    for w in timestamps_us.windows(2) {
        intervals_ms.push(w[1].saturating_sub(w[0]) as f64 / 1000.0);
    }
    let mut sorted = intervals_ms.clone();
    sorted.sort_by(|a, b| a.total_cmp(b));
    let median = sorted[sorted.len() / 2];
    if median <= 0.0 {
        return None;
    }
    let mean = intervals_ms.iter().sum::<f64>() / intervals_ms.len() as f64;
    let var = intervals_ms
        .iter()
        .map(|&x| (x - mean) * (x - mean))
        .sum::<f64>()
        / intervals_ms.len() as f64;
    let p99_idx = ((sorted.len() as f64 * 0.99).ceil() as usize)
        .saturating_sub(1)
        .min(sorted.len() - 1);

    let repeat_pct = sensor_clocks.and_then(|clocks| {
        if clocks.len() != timestamps_us.len() {
            return None;
        }
        let same = clocks.windows(2).filter(|w| w[0] == w[1]).count();
        Some(round2(100.0 * same as f64 / (clocks.len() - 1) as f64))
    });

    Some(PollingDiag {
        rate_hz: (1000.0 / median).round(),
        p99_ms: round2(sorted[p99_idx]),
        jitter_ms: round2(var.sqrt()),
        repeat_pct,
        samples: timestamps_us.len(),
    })
}

fn round2(v: f64) -> f64 {
    (v * 100.0).round() / 100.0
}
