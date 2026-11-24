use serde::{Deserialize, Serialize};

pub const DEFAULT_VRR_FLOOR_HZ: u32 = 48;
pub const MIN_CAP_FPS: u32 = 24;
pub const MEASURED_MIN_FRAMES: usize = 60;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct CapTier {
    pub id: String,
    pub label: String,
    pub cap_fps: Option<u32>,
    pub output_fps: Option<u32>,
    pub predicted_std_dev_ms: f64,
    pub measured: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct FrameCapPlan {
    pub refresh_hz: f64,
    pub vrr_enabled: bool,
    pub in_vrr_range: bool,
    pub fg_multiplier: u32,
    pub output_ceiling_fps: u32,
    pub max_base_cap_fps: u32,
    pub measured_frame_count: usize,
    pub tiers: Vec<CapTier>,
    pub refresh_locked_caps: Vec<u32>,
    pub recommended_cap_fps: u32,
}

pub fn vrr_cap(refresh_hz: f64, vrr_floor_hz: u32) -> u32 {
    if !refresh_hz.is_finite() || refresh_hz <= 0.0 {
        return 0;
    }
    let mut raw = (refresh_hz - (refresh_hz * refresh_hz) / 3600.0).floor() as i64;
    if vrr_floor_hz > 0 {
        raw = raw.max(vrr_floor_hz as i64);
    }
    let max_allowed = (refresh_hz.floor() as i64).max(1);
    raw.clamp(1, max_allowed) as u32
}

pub fn base_cap_for_fg(output_cap_fps: u32, fg_multiplier: u32) -> u32 {
    if output_cap_fps == 0 {
        return 0;
    }
    let mult = fg_multiplier.clamp(1, 4);
    if mult == 1 {
        output_cap_fps
    } else {
        (output_cap_fps / mult).max(1)
    }
}

pub fn refresh_locked_caps(refresh_hz: f64, fg_multiplier: u32) -> Vec<u32> {
    if !refresh_hz.is_finite() || refresh_hz <= 0.0 {
        return Vec::new();
    }
    let whole = refresh_hz.round().max(1.0) as u32;
    let mult = fg_multiplier.clamp(1, 4);
    let mut caps = Vec::new();
    for div in 1..=4 {
        if !whole.is_multiple_of(div) {
            continue;
        }
        let output = whole / div;
        if output < 30 && div > 1 {
            continue;
        }
        let base = base_cap_for_fg(output, mult);
        if base >= MIN_CAP_FPS && !caps.contains(&base) {
            caps.push(base);
        }
    }
    if caps.is_empty() {
        let fallback = base_cap_for_fg(whole, mult);
        if fallback > 0 {
            caps.push(fallback);
        }
    }
    caps
}

pub fn plan_frame_cap(
    refresh_hz: f64,
    vrr_enabled: bool,
    vrr_floor_hz: u32,
    fg_multiplier: u32,
    session_frame_times_ms: &[f64],
) -> FrameCapPlan {
    let eff_hz = if refresh_hz.is_finite() && refresh_hz > 0.0 {
        refresh_hz
    } else {
        60.0
    };
    let floor = if vrr_floor_hz > 0 {
        vrr_floor_hz
    } else {
        DEFAULT_VRR_FLOOR_HZ
    };
    let mult = fg_multiplier.clamp(1, 4);
    let in_vrr_range = vrr_enabled && eff_hz >= floor as f64;
    let output_ceiling = if in_vrr_range {
        vrr_cap(eff_hz, floor)
    } else {
        (eff_hz.round() as u32).max(MIN_CAP_FPS)
    };
    let max_base = base_cap_for_fg(output_ceiling, mult).max(MIN_CAP_FPS);

    let mut valid: Vec<f64> = session_frame_times_ms
        .iter()
        .copied()
        .filter(|dt| dt.is_finite() && *dt >= 0.5 && *dt <= 250.0)
        .collect();
    let measured = valid.len() >= MEASURED_MIN_FRAMES;

    let (smooth_cap, balanced_cap) = if measured {
        valid.sort_unstable_by(f64::total_cmp);
        let p95 = valid[((valid.len() - 1) as f64 * 0.95).ceil() as usize];
        let p99 = valid[((valid.len() - 1) as f64 * 0.99).ceil() as usize];
        let smooth = ((1000.0 / p99).floor() as u32).clamp(MIN_CAP_FPS, max_base);
        let balanced = ((1000.0 / p95).floor() as u32).clamp(smooth, max_base);
        (smooth, balanced)
    } else {
        let smooth = ((max_base as f64 * 0.85).floor() as u32).clamp(MIN_CAP_FPS, max_base);
        let balanced = ((max_base as f64 * 0.94).floor() as u32).clamp(smooth, max_base);
        (smooth, balanced)
    };

    let smooth_std = simulate_cap_stddev(&valid, smooth_cap);
    let balanced_std = simulate_cap_stddev(&valid, balanced_cap);
    let max_std = simulate_cap_stddev(&valid, max_base);

    let tiers = vec![
        CapTier {
            id: "smooth".into(),
            label: "Smooth".into(),
            cap_fps: Some(smooth_cap),
            output_fps: Some(smooth_cap * mult),
            predicted_std_dev_ms: smooth_std,
            measured,
        },
        CapTier {
            id: "balanced".into(),
            label: "Balanced".into(),
            cap_fps: Some(balanced_cap),
            output_fps: Some(balanced_cap * mult),
            predicted_std_dev_ms: balanced_std,
            measured,
        },
        CapTier {
            id: "max".into(),
            label: "Max".into(),
            cap_fps: Some(max_base),
            output_fps: Some(max_base * mult),
            predicted_std_dev_ms: max_std,
            measured,
        },
    ];

    FrameCapPlan {
        refresh_hz: eff_hz,
        vrr_enabled,
        in_vrr_range,
        fg_multiplier: mult,
        output_ceiling_fps: output_ceiling,
        max_base_cap_fps: max_base,
        measured_frame_count: valid.len(),
        tiers,
        refresh_locked_caps: refresh_locked_caps(eff_hz, mult),
        recommended_cap_fps: if measured { balanced_cap } else { max_base },
    }
}

fn simulate_cap_stddev(frames_ms: &[f64], cap_fps: u32) -> f64 {
    if frames_ms.is_empty() || cap_fps == 0 {
        return 0.0;
    }
    let floor_ms = 1000.0 / cap_fps as f64;
    let n = frames_ms.len() as f64;
    let mean = frames_ms.iter().map(|&dt| dt.max(floor_ms)).sum::<f64>() / n;
    let var = frames_ms
        .iter()
        .map(|&dt| {
            let d = dt.max(floor_ms) - mean;
            d * d
        })
        .sum::<f64>()
        / n;
    var.sqrt()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vrr_cap_matches_reflex_curve() {
        assert_eq!(vrr_cap(144.0, 48), 138);
        assert_eq!(vrr_cap(240.0, 48), 224);
        assert_eq!(vrr_cap(360.0, 48), 324);
        assert_eq!(vrr_cap(60.0, 48), 59);
    }

    #[test]
    fn measured_plan_lowers_stddev_with_smooth_cap() {
        let mut frames = vec![6.0; 90];
        frames.extend(vec![11.0; 10]);
        let plan = plan_frame_cap(144.0, true, 48, 1, &frames);
        assert_eq!(plan.output_ceiling_fps, 138);
        assert_eq!(plan.measured_frame_count, 100);
        assert!(plan.tiers[0].predicted_std_dev_ms <= plan.tiers[2].predicted_std_dev_ms);
    }
}
