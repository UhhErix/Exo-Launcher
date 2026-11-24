use crate::curve::{CurvePoint, ResonanceNotch};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AcousticSample {
    pub pwm_pct: u8,
    pub rpm: u32,
    pub db_spl: f32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AcousticCalibrationResult {
    pub spin_up_pwm_pct: u8,
    pub quiet_knee_pwm_pct: u8,
    pub resonance_notch: Option<ResonanceNotch>,
    pub quiet_curve: Vec<CurvePoint>,
}

pub fn analyze_acoustic_sweep(samples: &[AcousticSample]) -> Option<AcousticCalibrationResult> {
    if samples.len() < 3 {
        return None;
    }
    let spin_up_pwm_pct = samples
        .iter()
        .find(|s| s.rpm > 0)
        .map(|s| s.pwm_pct)
        .unwrap_or(30)
        .max(25);

    let mut quiet_knee_pwm_pct = samples.last()?.pwm_pct;
    let mut resonance_notch = None;

    for i in 1..samples.len() {
        let prev = samples[i - 1];
        let cur = samples[i];
        let d_pwm = (cur.pwm_pct.saturating_sub(prev.pwm_pct)).max(1) as f32;
        let slope = (cur.db_spl - prev.db_spl) / d_pwm;
        if slope > 0.45 && quiet_knee_pwm_pct == samples.last()?.pwm_pct {
            quiet_knee_pwm_pct = prev.pwm_pct.max(spin_up_pwm_pct);
        }
        if i + 1 < samples.len() {
            let next = samples[i + 1];
            let bump = cur.db_spl - (prev.db_spl + next.db_spl) * 0.5;
            if bump >= 3.0 && resonance_notch.is_none() {
                resonance_notch = Some(ResonanceNotch {
                    low_pwm_pct: cur.pwm_pct.saturating_sub(3),
                    high_pwm_pct: cur.pwm_pct.saturating_add(3).min(100),
                });
            }
        }
    }

    let quiet_curve = vec![
        CurvePoint {
            temp_c: 35.0,
            pwm_pct: spin_up_pwm_pct,
        },
        CurvePoint {
            temp_c: 65.0,
            pwm_pct: quiet_knee_pwm_pct,
        },
        CurvePoint {
            temp_c: 80.0,
            pwm_pct: 85,
        },
        CurvePoint {
            temp_c: 85.0,
            pwm_pct: 100,
        },
    ];

    Some(AcousticCalibrationResult {
        spin_up_pwm_pct,
        quiet_knee_pwm_pct,
        resonance_notch,
        quiet_curve,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_spin_up_quiet_knee_and_resonance_notch() {
        let sweep = vec![
            AcousticSample {
                pwm_pct: 0,
                rpm: 0,
                db_spl: 28.0,
            },
            AcousticSample {
                pwm_pct: 25,
                rpm: 700,
                db_spl: 28.5,
            },
            AcousticSample {
                pwm_pct: 40,
                rpm: 1050,
                db_spl: 30.0,
            },
            AcousticSample {
                pwm_pct: 50,
                rpm: 1350,
                db_spl: 36.0,
            },
            AcousticSample {
                pwm_pct: 60,
                rpm: 1650,
                db_spl: 32.0,
            },
            AcousticSample {
                pwm_pct: 70,
                rpm: 1950,
                db_spl: 39.5,
            },
            AcousticSample {
                pwm_pct: 85,
                rpm: 2400,
                db_spl: 48.0,
            },
        ];
        let res = analyze_acoustic_sweep(&sweep).expect("valid sweep");
        assert_eq!(res.spin_up_pwm_pct, 25);
        assert_eq!(res.quiet_knee_pwm_pct, 40);
        let notch = res
            .resonance_notch
            .expect("should detect 50% resonance bump");
        assert!(notch.low_pwm_pct <= 50 && notch.high_pwm_pct >= 50);
    }
}
