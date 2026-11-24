use crate::backend::SensorKind;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CurvePoint {
    pub temp_c: f32,
    pub pwm_pct: u8,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum MixMode {
    Max,
    WeightedAverage,
    CoolantDelta,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResonanceNotch {
    pub low_pwm_pct: u8,
    pub high_pwm_pct: u8,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FanCurveConfig {
    pub points: Vec<CurvePoint>,
    pub mix_mode: MixMode,
    pub sensors: Vec<(SensorKind, f32)>,
    pub hysteresis_up_c: f32,
    pub hysteresis_down_c: f32,
    pub spike_hold_ticks: u32,
    pub max_step_up_per_tick: u8,
    pub max_step_down_per_tick: u8,
    pub is_cpu_domain: bool,
    pub hard_floor_pwm_pct: u8,
    pub notch: Option<ResonanceNotch>,
}

impl Default for FanCurveConfig {
    fn default() -> Self {
        Self {
            points: vec![
                CurvePoint {
                    temp_c: 35.0,
                    pwm_pct: 30,
                },
                CurvePoint {
                    temp_c: 55.0,
                    pwm_pct: 45,
                },
                CurvePoint {
                    temp_c: 70.0,
                    pwm_pct: 68,
                },
                CurvePoint {
                    temp_c: 80.0,
                    pwm_pct: 88,
                },
                CurvePoint {
                    temp_c: 85.0,
                    pwm_pct: 100,
                },
            ],
            mix_mode: MixMode::Max,
            sensors: vec![(SensorKind::GpuCore, 1.0)],
            hysteresis_up_c: 2.0,
            hysteresis_down_c: 4.0,
            spike_hold_ticks: 3,
            max_step_up_per_tick: 20,
            max_step_down_per_tick: 4,
            is_cpu_domain: false,
            hard_floor_pwm_pct: 25,
            notch: None,
        }
    }
}

pub fn mix_sensor_temps(mode: MixMode, readings: &[(f32, f32)]) -> Option<f32> {
    if readings.is_empty() {
        return None;
    }
    match mode {
        MixMode::Max => readings
            .iter()
            .map(|(t, _)| *t)
            .fold(None, |acc, t| Some(acc.map_or(t, |a: f32| a.max(t)))),
        MixMode::WeightedAverage => {
            let mut sum = 0.0f32;
            let mut wsum = 0.0f32;
            for (t, w) in readings {
                let weight = w.max(0.0);
                sum += t * weight;
                wsum += weight;
            }
            if wsum > 0.0 {
                Some(sum / wsum)
            } else {
                Some(readings[0].0)
            }
        }
        MixMode::CoolantDelta => {
            let base = readings[0].0;
            let ambient = readings.get(1).map(|(t, _)| *t).unwrap_or(25.0);
            Some((base - ambient).max(0.0) + 30.0)
        }
    }
}

pub fn interpolate_curve(points: &[CurvePoint], temp_c: f32) -> u8 {
    if points.is_empty() {
        return 50;
    }
    if temp_c <= points[0].temp_c {
        return points[0].pwm_pct.min(100);
    }
    for win in points.windows(2) {
        let a = win[0];
        let b = win[1];
        if temp_c <= b.temp_c {
            let span = (b.temp_c - a.temp_c).max(0.001);
            let t = ((temp_c - a.temp_c) / span).clamp(0.0, 1.0);
            let val = a.pwm_pct as f32 + (b.pwm_pct as f32 - a.pwm_pct as f32) * t;
            return val.round().clamp(0.0, 100.0) as u8;
        }
    }
    points.last().map(|p| p.pwm_pct.min(100)).unwrap_or(100)
}

pub fn enforce_thermal_floor(
    raw_pwm: u8,
    temp_c: f32,
    is_cpu_domain: bool,
    configured_floor: u8,
) -> u8 {
    let mut pwm = raw_pwm.min(100);
    let active_threshold = if is_cpu_domain { 55.0 } else { 60.0 };
    if temp_c > active_threshold {
        pwm = pwm.max(configured_floor.max(25));
    }
    if temp_c >= 80.0 {
        pwm = pwm.max(85);
    }
    if temp_c >= 85.0 {
        pwm = 100;
    }
    pwm.min(100)
}

#[derive(Debug, Clone)]
pub struct ChannelController {
    pub config: FanCurveConfig,
    last_pwm: u8,
    latched_temp_c: f32,
    pending_up_ticks: u32,
}

impl ChannelController {
    pub fn new(config: FanCurveConfig) -> Self {
        let init_pwm = config.points.first().map(|p| p.pwm_pct).unwrap_or(30);
        let init_temp = config.points.first().map(|p| p.temp_c).unwrap_or(35.0);
        Self {
            config,
            last_pwm: init_pwm,
            latched_temp_c: init_temp,
            pending_up_ticks: 0,
        }
    }

    pub fn step(&mut self, temp_c: f32) -> u8 {
        if temp_c >= 80.0 {
            self.latched_temp_c = temp_c;
            self.pending_up_ticks = 0;
            let raw = interpolate_curve(&self.config.points, temp_c);
            let safe = enforce_thermal_floor(
                raw,
                temp_c,
                self.config.is_cpu_domain,
                self.config.hard_floor_pwm_pct,
            );
            self.last_pwm = safe;
            return safe;
        }

        if temp_c > self.latched_temp_c + self.config.hysteresis_up_c {
            self.pending_up_ticks += 1;
            if self.pending_up_ticks >= self.config.spike_hold_ticks.max(1) {
                self.latched_temp_c = temp_c;
                self.pending_up_ticks = 0;
            }
        } else {
            self.pending_up_ticks = 0;
            if temp_c < self.latched_temp_c - self.config.hysteresis_down_c {
                self.latched_temp_c = temp_c;
            }
        }

        let target = interpolate_curve(&self.config.points, self.latched_temp_c);
        let mut stepped = if target > self.last_pwm {
            self.last_pwm
                .saturating_add(self.config.max_step_up_per_tick)
                .min(target)
        } else if target < self.last_pwm {
            self.last_pwm
                .saturating_sub(self.config.max_step_down_per_tick)
                .max(target)
        } else {
            target
        };

        if let Some(notch) = self.config.notch
            && stepped >= notch.low_pwm_pct
            && stepped <= notch.high_pwm_pct
        {
            stepped = if target >= self.last_pwm {
                notch.high_pwm_pct.saturating_add(1).min(100)
            } else {
                notch.low_pwm_pct.saturating_sub(1)
            };
        }

        let safe = enforce_thermal_floor(
            stepped,
            temp_c,
            self.config.is_cpu_domain,
            self.config.hard_floor_pwm_pct,
        );
        self.last_pwm = safe;
        safe
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn thermal_safety_floor_overrides_malicious_zero_curve() {
        let zero_curve = FanCurveConfig {
            points: vec![
                CurvePoint {
                    temp_c: 20.0,
                    pwm_pct: 0,
                },
                CurvePoint {
                    temp_c: 100.0,
                    pwm_pct: 0,
                },
            ],
            hard_floor_pwm_pct: 0,
            ..Default::default()
        };
        let mut ctrl = ChannelController::new(zero_curve);
        assert!(ctrl.step(65.0) >= 25);
        assert!(ctrl.step(80.0) >= 85);
        assert_eq!(ctrl.step(85.0), 100);
        assert_eq!(ctrl.step(95.0), 100);
    }

    #[test]
    fn suppresses_two_second_cpu_spike_and_ramps_on_sustained_load() {
        let cfg = FanCurveConfig {
            is_cpu_domain: true,
            spike_hold_ticks: 3,
            ..Default::default()
        };
        let mut ctrl = ChannelController::new(cfg);
        for _ in 0..3 {
            ctrl.step(45.0);
        }
        let baseline = ctrl.step(45.0);
        assert_eq!(ctrl.step(60.0), baseline);
        assert_eq!(ctrl.step(60.0), baseline);
        assert_eq!(ctrl.step(45.0), baseline);

        ctrl.step(72.0);
        ctrl.step(72.0);
        let sustained = ctrl.step(72.0);
        assert!(sustained > baseline);
    }

    #[test]
    fn avoids_resonance_notch_band() {
        let cfg = FanCurveConfig {
            points: vec![
                CurvePoint {
                    temp_c: 30.0,
                    pwm_pct: 30,
                },
                CurvePoint {
                    temp_c: 60.0,
                    pwm_pct: 45,
                },
            ],
            spike_hold_ticks: 1,
            notch: Some(ResonanceNotch {
                low_pwm_pct: 42,
                high_pwm_pct: 48,
            }),
            ..Default::default()
        };
        let mut ctrl = ChannelController::new(cfg);
        let out = ctrl.step(60.0);
        assert!(
            !(42..=48).contains(&out),
            "pwm {out} landed inside resonance notch"
        );
    }
}
