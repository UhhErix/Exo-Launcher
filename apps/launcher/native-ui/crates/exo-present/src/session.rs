use crate::metrics::FrameMetrics;
use crate::stutter::{StutterCause, StutterFinding};
use serde::{Deserialize, Serialize};

pub const REGRESSION_THRESHOLD_PCT: f64 = -8.0;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SessionSummary {
    pub game_id: String,
    pub exe_name: String,
    pub driver_version: String,
    pub profile_hash: String,
    pub duration_secs: f64,
    pub frame_count: u64,
    pub avg_fps: f64,
    pub low_1pct_fps: f64,
    pub low_01pct_fps: f64,
    pub avg_ms: f64,
    pub p99_ms: f64,
    pub stutter_count: u64,
    pub top_cause: Option<StutterCause>,
}

impl SessionSummary {
    pub fn from_metrics(
        game_id: impl Into<String>,
        exe_name: impl Into<String>,
        driver_version: impl Into<String>,
        profile_hash: impl Into<String>,
        duration_secs: f64,
        metrics: &FrameMetrics,
        findings: &[StutterFinding],
    ) -> Self {
        Self {
            game_id: game_id.into(),
            exe_name: exe_name.into(),
            driver_version: driver_version.into(),
            profile_hash: profile_hash.into(),
            duration_secs,
            frame_count: metrics.frame_count,
            avg_fps: metrics.fps,
            low_1pct_fps: metrics.low_1pct_fps,
            low_01pct_fps: metrics.low_01pct_fps,
            avg_ms: metrics.avg_ms,
            p99_ms: metrics.p99_ms,
            stutter_count: metrics.stutter_count,
            top_cause: findings.first().map(|f| f.cause),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SessionComparison {
    pub fps_delta_pct: f64,
    pub low_1pct_delta_pct: f64,
    pub p99_ms_delta_pct: f64,
    pub stutter_delta: i64,
    pub regressed: bool,
    pub summary: String,
}

pub fn compare_sessions(before: &SessionSummary, after: &SessionSummary) -> SessionComparison {
    let pct = |a: f64, b: f64| -> f64 {
        if a.abs() < 1e-6 {
            0.0
        } else {
            ((b - a) / a) * 100.0
        }
    };
    let fps_delta_pct = pct(before.avg_fps, after.avg_fps);
    let low_1pct_delta_pct = pct(before.low_1pct_fps, after.low_1pct_fps);
    let p99_ms_delta_pct = pct(before.p99_ms, after.p99_ms);
    let stutter_delta = after.stutter_count as i64 - before.stutter_count as i64;
    let regressed =
        fps_delta_pct <= REGRESSION_THRESHOLD_PCT || low_1pct_delta_pct <= REGRESSION_THRESHOLD_PCT;
    let summary = if regressed {
        format!(
            "Regression detected: avg FPS {:+.1}%, 1% low {:+.1}% ({} -> {})",
            fps_delta_pct, low_1pct_delta_pct, before.driver_version, after.driver_version
        )
    } else {
        format!(
            "Stable: avg FPS {:+.1}%, 1% low {:+.1}% ({} -> {})",
            fps_delta_pct, low_1pct_delta_pct, before.driver_version, after.driver_version
        )
    };
    SessionComparison {
        fps_delta_pct,
        low_1pct_delta_pct,
        p99_ms_delta_pct,
        stutter_delta,
        regressed,
        summary,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn flags_driver_regression_over_8_percent() {
        let before = SessionSummary {
            game_id: "g1".into(),
            exe_name: "game.exe".into(),
            driver_version: "566.14".into(),
            profile_hash: "abc".into(),
            duration_secs: 60.0,
            frame_count: 7200,
            avg_fps: 120.0,
            low_1pct_fps: 95.0,
            low_01pct_fps: 80.0,
            avg_ms: 8.33,
            p99_ms: 10.5,
            stutter_count: 1,
            top_cause: None,
        };
        let mut after = before.clone();
        after.driver_version = "566.36".into();
        after.low_1pct_fps = 82.0;
        let cmp = compare_sessions(&before, &after);
        assert!(cmp.regressed);
        assert!(cmp.low_1pct_delta_pct < -10.0);
    }
}
