use crate::transport::Rgb8;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "mode")]
pub enum EffectMode {
    Static {
        color: Rgb8,
    },
    Breathing {
        color: Rgb8,
        period_ms: u32,
    },
    ExoVoidMinimal,
    ThermalReactive {
        cool: Rgb8,
        warm: Rgb8,
        hot: Rgb8,
        temp_c: f32,
    },
    BatteryAlert {
        percent: u8,
    },
    AmbientSync {
        edge_rgb: Rgb8,
    },
}

impl EffectMode {
    pub fn is_static(self) -> bool {
        matches!(
            self,
            Self::Static { .. } | Self::ExoVoidMinimal | Self::AmbientSync { .. }
        )
    }
}

pub fn render_frame(
    mode: EffectMode,
    zone_count: usize,
    elapsed_ms: u64,
    brightness_pct: u8,
) -> Vec<Rgb8> {
    let base = match mode {
        EffectMode::Static { color } => color,
        EffectMode::ExoVoidMinimal => Rgb8::EXO_ACCENT.scaled(25),
        EffectMode::AmbientSync { edge_rgb } => edge_rgb,
        EffectMode::Breathing { color, period_ms } => {
            let period = period_ms.max(200) as f64;
            let phase = ((elapsed_ms as f64) % period) / period;
            let wave = 0.5 - 0.5 * (phase * std::f64::consts::TAU).cos();
            let scale = (15.0 + wave * 85.0).round() as u8;
            color.scaled(scale)
        }
        EffectMode::ThermalReactive {
            cool,
            warm,
            hot,
            temp_c,
        } => {
            if temp_c <= 55.0 {
                cool
            } else if temp_c >= 80.0 {
                hot
            } else if temp_c < 70.0 {
                lerp_rgb(cool, warm, (temp_c - 55.0) / 15.0)
            } else {
                lerp_rgb(warm, hot, (temp_c - 70.0) / 10.0)
            }
        }
        EffectMode::BatteryAlert { percent } => {
            if percent < 15 {
                let phase = ((elapsed_ms % 1200) as f64) / 1200.0;
                let wave = 0.5 - 0.5 * (phase * std::f64::consts::TAU).cos();
                Rgb8::AMBER.scaled((20.0 + wave * 80.0).round() as u8)
            } else if percent < 25 {
                Rgb8::AMBER.scaled(30)
            } else {
                Rgb8::EXO_ACCENT
            }
        }
    };
    vec![base.scaled(brightness_pct); zone_count]
}

fn lerp_rgb(a: Rgb8, b: Rgb8, t: f32) -> Rgb8 {
    let t = t.clamp(0.0, 1.0);
    let mix = |x: u8, y: u8| -> u8 { (x as f32 + (y as f32 - x as f32) * t).round() as u8 };
    Rgb8::new(mix(a.r, b.r), mix(a.g, b.g), mix(a.b, b.b))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn static_and_thermal_render_deterministically() {
        let f = render_frame(
            EffectMode::Static {
                color: Rgb8::new(100, 200, 50),
            },
            4,
            0,
            50,
        );
        assert_eq!(f, vec![Rgb8::new(50, 100, 25); 4]);
        let cool = Rgb8::new(0, 120, 255);
        let warm = Rgb8::new(255, 180, 0);
        let hot = Rgb8::new(255, 20, 20);
        assert_eq!(
            render_frame(
                EffectMode::ThermalReactive {
                    cool,
                    warm,
                    hot,
                    temp_c: 45.0
                },
                1,
                0,
                100
            )[0],
            cool
        );
        assert_eq!(
            render_frame(
                EffectMode::ThermalReactive {
                    cool,
                    warm,
                    hot,
                    temp_c: 85.0
                },
                1,
                0,
                100
            )[0],
            hot
        );
    }
}
