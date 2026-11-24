use crate::metrics::{STUTTER_FACTOR, STUTTER_MIN_OVER_MS};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, VecDeque};

pub const TYPICAL_WINDOW_SECS: usize = 30;
pub const TYPICAL_MIN_SECS: usize = 5;
pub const SPIKE_CORES: f64 = 0.5;
pub const SPIKE_RATIO: f64 = 3.0;
pub const BASELINE_FLOOR_CORES: f64 = 0.05;
pub const VRAM_FULL_SHARE: f64 = 0.97;
pub const GPU_HOT_DEGREES: i32 = 87;
pub const THROTTLED_SHARE: f64 = 0.85;
pub const THROTTLED_BUSY_PCT: f64 = 50.0;
pub const PAGING_FAULTS_PER_SEC: u64 = 150;
pub const DPC_PCT_ON_ONE_CPU: f64 = 20.0;
pub const SHADER_WRITE_BYTES: u64 = 1 << 20;
pub const SHADER_CPU_RATIO: f64 = 1.3;
pub const LEARN_AFTER: u32 = 2;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct AppLoad {
    pub name: String,
    pub cores: f64,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct SystemSecond {
    pub cpu_percent: f64,
    pub logical_cpus: u32,
    pub gpu_percent: Option<i32>,
    pub gpu_temp_c: Option<i32>,
    pub vram_used: Option<u64>,
    pub vram_total: Option<u64>,
    pub ram_used: u64,
    pub ram_total: u64,
    pub dpc_percent: Option<f64>,
    pub interrupt_percent: Option<f64>,
    pub cpu_performance: Option<f64>,
    pub thermal_limit: Option<f64>,
    pub game_cores: f64,
    pub game_write_bytes: u64,
    pub game_hard_faults: u64,
    pub background: Vec<AppLoad>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StutterCause {
    BackgroundSpike,
    VideoMemoryFull,
    GpuHot,
    CpuThrottled,
    MemoryPaging,
    DriverLatency,
    ShaderCompile,
    Unexplained,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct StutterFinding {
    pub cause: StutterCause,
    pub stutters: u32,
    pub app: Option<String>,
    pub worst_ms: f64,
    pub suggestion: String,
}

#[derive(Clone, Debug, Default)]
pub struct StutterAnalyzer {
    recent_avg_ms: VecDeque<f64>,
    app_baselines: HashMap<String, f64>,
    game_cores_baseline: Option<f64>,
    cpu_perf_baseline: Option<f64>,
    prev_sec: Option<SystemSecond>,
    counts: HashMap<(StutterCause, Option<String>), u32>,
    worst: HashMap<(StutterCause, Option<String>), f64>,
    total_stutters: u32,
}

impl StutterAnalyzer {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn push_second(&mut self, frame_times_ms: &[f64], sec: SystemSecond) -> u32 {
        if frame_times_ms.is_empty() {
            self.prev_sec = Some(sec);
            return 0;
        }
        let avg_ms = frame_times_ms.iter().copied().sum::<f64>() / frame_times_ms.len() as f64;
        let typical = self.typical_frame_ms();
        let mut stutters_this_sec = 0u32;

        if let Some(typ) = typical {
            let mut worst_stutter = 0.0f64;
            for &dt in frame_times_ms {
                if dt > typ * STUTTER_FACTOR && (dt - typ) >= STUTTER_MIN_OVER_MS {
                    stutters_this_sec += 1;
                    if dt > worst_stutter {
                        worst_stutter = dt;
                    }
                }
            }
            if stutters_this_sec > 0 {
                self.total_stutters = self.total_stutters.saturating_add(stutters_this_sec);
                self.attribute(&sec, stutters_this_sec, worst_stutter);
            } else {
                self.update_smooth_baselines(&sec);
            }
        } else {
            self.update_smooth_baselines(&sec);
        }

        if self.recent_avg_ms.len() == TYPICAL_WINDOW_SECS {
            self.recent_avg_ms.pop_front();
        }
        self.recent_avg_ms.push_back(avg_ms);
        self.prev_sec = Some(sec);
        stutters_this_sec
    }

    fn typical_frame_ms(&self) -> Option<f64> {
        if self.recent_avg_ms.len() < TYPICAL_MIN_SECS {
            return None;
        }
        let mut vals: Vec<f64> = self.recent_avg_ms.iter().copied().collect();
        vals.sort_unstable_by(f64::total_cmp);
        Some(vals[vals.len() / 2])
    }

    fn update_smooth_baselines(&mut self, sec: &SystemSecond) {
        const ALPHA: f64 = 0.1;
        for app in &sec.background {
            let key = app.name.to_ascii_lowercase();
            self.app_baselines
                .entry(key)
                .and_modify(|v| *v = *v * (1.0 - ALPHA) + app.cores * ALPHA)
                .or_insert(app.cores);
        }
        self.game_cores_baseline = Some(match self.game_cores_baseline {
            Some(prev) => prev * (1.0 - ALPHA) + sec.game_cores * ALPHA,
            None => sec.game_cores,
        });
        if let Some(perf) = sec.cpu_performance {
            self.cpu_perf_baseline = Some(match self.cpu_perf_baseline {
                Some(prev) => prev * (1.0 - ALPHA) + perf * ALPHA,
                None => perf,
            });
        }
    }

    fn attribute(&mut self, cur: &SystemSecond, count: u32, worst_ms: f64) {
        let mut causes: Vec<(StutterCause, Option<String>)> = Vec::new();
        {
            let window: Vec<&SystemSecond> = match &self.prev_sec {
                Some(prev) => vec![prev, cur],
                None => vec![cur],
            };

            let mut blamed_apps = Vec::new();
            for s in &window {
                for app in &s.background {
                    let key = app.name.to_ascii_lowercase();
                    let base = self
                        .app_baselines
                        .get(&key)
                        .copied()
                        .unwrap_or(0.0)
                        .max(BASELINE_FLOOR_CORES);
                    if app.cores >= SPIKE_CORES
                        && app.cores >= base * SPIKE_RATIO
                        && !blamed_apps
                            .iter()
                            .any(|n: &String| n.eq_ignore_ascii_case(&app.name))
                    {
                        blamed_apps.push(app.name.clone());
                    }
                }
            }
            for app in blamed_apps {
                causes.push((StutterCause::BackgroundSpike, Some(app)));
            }

            if window.iter().any(|s| {
                matches!((s.vram_used, s.vram_total), (Some(u), Some(t)) if t > 0 && (u as f64 / t as f64) >= VRAM_FULL_SHARE)
            }) {
                causes.push((StutterCause::VideoMemoryFull, None));
            }

            if window
                .iter()
                .any(|s| matches!(s.gpu_temp_c, Some(t) if t >= GPU_HOT_DEGREES))
            {
                causes.push((StutterCause::GpuHot, None));
            }

            let cpu_throttled = window.iter().any(|s| {
                if matches!(s.thermal_limit, Some(lim) if lim < 100.0) {
                    return true;
                }
                if let (Some(perf), Some(base)) = (s.cpu_performance, self.cpu_perf_baseline)
                    && base > 0.0
                    && s.cpu_percent >= THROTTLED_BUSY_PCT
                    && perf <= base * THROTTLED_SHARE
                {
                    return true;
                }
                false
            });
            if cpu_throttled {
                causes.push((StutterCause::CpuThrottled, None));
            }

            if window
                .iter()
                .any(|s| s.game_hard_faults >= PAGING_FAULTS_PER_SEC)
            {
                causes.push((StutterCause::MemoryPaging, None));
            }

            if window.iter().any(|s| {
                let logical = s.logical_cpus.max(1) as f64;
                let total = s.dpc_percent.unwrap_or(0.0) + s.interrupt_percent.unwrap_or(0.0);
                total * logical >= DPC_PCT_ON_ONE_CPU
            }) {
                causes.push((StutterCause::DriverLatency, None));
            }

            let shader = window.iter().any(|s| {
                s.game_write_bytes >= SHADER_WRITE_BYTES
                    && self
                        .game_cores_baseline
                        .is_some_and(|base| base > 0.0 && s.game_cores >= base * SHADER_CPU_RATIO)
            });
            if shader {
                causes.push((StutterCause::ShaderCompile, None));
            }
        }

        if causes.is_empty() {
            self.record(StutterCause::Unexplained, None, count, worst_ms);
        } else {
            for (cause, app) in causes {
                self.record(cause, app, count, worst_ms);
            }
        }
    }

    fn record(&mut self, cause: StutterCause, app: Option<String>, count: u32, worst_ms: f64) {
        let key = (cause, app);
        *self.counts.entry(key.clone()).or_insert(0) += count;
        self.worst
            .entry(key)
            .and_modify(|w| {
                if worst_ms > *w {
                    *w = worst_ms;
                }
            })
            .or_insert(worst_ms);
    }

    pub fn total_stutters(&self) -> u32 {
        self.total_stutters
    }

    pub fn learned_background_offenders(&self) -> Vec<String> {
        let mut offenders: Vec<String> = self
            .counts
            .iter()
            .filter_map(|((cause, app), &n)| {
                if *cause == StutterCause::BackgroundSpike && n >= LEARN_AFTER {
                    app.clone()
                } else {
                    None
                }
            })
            .collect();
        offenders.sort();
        offenders
    }

    pub fn findings(&self) -> Vec<StutterFinding> {
        let mut list: Vec<StutterFinding> = self
            .counts
            .iter()
            .map(|((cause, app), &stutters)| StutterFinding {
                cause: *cause,
                stutters,
                app: app.clone(),
                worst_ms: self
                    .worst
                    .get(&(*cause, app.clone()))
                    .copied()
                    .unwrap_or(0.0),
                suggestion: suggestion_for(*cause, app.as_deref()),
            })
            .collect();
        list.sort_by(|a, b| {
            b.stutters
                .cmp(&a.stutters)
                .then_with(|| b.worst_ms.total_cmp(&a.worst_ms))
        });
        list
    }
}

fn suggestion_for(cause: StutterCause, app: Option<&str>) -> String {
    match cause {
        StutterCause::BackgroundSpike => match app {
            Some(name) => format!("Corral {name} onto background CPU sets via Autopilot"),
            None => "Enable Autopilot background CPU sets".into(),
        },
        StutterCause::VideoMemoryFull => {
            "Lower texture quality or shadow resolution to prevent PCIe VRAM spill".into()
        }
        StutterCause::GpuHot => {
            "Run Exo GPU undervolt or raise fan curve ceiling to stop thermal throttling".into()
        }
        StutterCause::CpuThrottled => {
            "Check CPU cooling or enable Exo power profile to avoid thermal clock drops".into()
        }
        StutterCause::MemoryPaging => {
            "Close heavy browser tabs or enable Autopilot smart standby trim".into()
        }
        StutterCause::DriverLatency => {
            "Run Exo Latency Check and enable MSI interrupt mode on the offending driver".into()
        }
        StutterCause::ShaderCompile => {
            "Warm this game's PSO cache in Exo Shaders before launching".into()
        }
        StutterCause::Unexplained => {
            "Apply Exo Frame Cap Planner's Smooth cap to stabilize frame pacing".into()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn smooth_sec() -> SystemSecond {
        SystemSecond {
            cpu_percent: 40.0,
            logical_cpus: 12,
            gpu_percent: Some(80),
            gpu_temp_c: Some(66),
            vram_used: Some(5_000_000_000),
            vram_total: Some(8_000_000_000),
            ram_used: 9_000_000_000,
            ram_total: 16_000_000_000,
            dpc_percent: Some(0.4),
            interrupt_percent: Some(0.2),
            cpu_performance: Some(100.0),
            thermal_limit: None,
            game_cores: 3.0,
            game_write_bytes: 0,
            game_hard_faults: 0,
            background: vec![AppLoad {
                name: "updater.exe".into(),
                cores: 0.05,
            }],
        }
    }

    #[test]
    fn attributes_background_spike_vram_and_shader_compile() {
        let mut a = StutterAnalyzer::new();
        let steady = vec![8.33; 120];
        for _ in 0..6 {
            assert_eq!(a.push_second(&steady, smooth_sec()), 0);
        }

        let mut bg_sec = smooth_sec();
        bg_sec.background = vec![AppLoad {
            name: "updater.exe".into(),
            cores: 1.4,
        }];
        let mut hitch = vec![8.33; 118];
        hitch.push(42.0);
        hitch.push(38.0);
        assert_eq!(a.push_second(&hitch, bg_sec), 2);
        assert_eq!(a.learned_background_offenders(), vec!["updater.exe"]);

        let mut shader_sec = smooth_sec();
        shader_sec.game_cores = 4.6;
        shader_sec.game_write_bytes = 2 << 20;
        let mut hitch2 = vec![8.33; 119];
        hitch2.push(55.0);
        assert_eq!(a.push_second(&hitch2, shader_sec), 1);

        let findings = a.findings();
        assert!(
            findings
                .iter()
                .any(|f| f.cause == StutterCause::BackgroundSpike
                    && f.app.as_deref() == Some("updater.exe"))
        );
        assert!(
            findings
                .iter()
                .any(|f| f.cause == StutterCause::ShaderCompile)
        );
    }
}
