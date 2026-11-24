use crate::effects::EffectMode;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoContextConfig {
    pub hdr_dim_pct: u8,
    pub fullscreen_dim_pct: u8,
    pub night_dim_pct: u8,
    pub battery_low_threshold: u8,
    pub battery_critical_threshold: u8,
}

impl Default for AutoContextConfig {
    fn default() -> Self {
        Self {
            hdr_dim_pct: 10,
            fullscreen_dim_pct: 25,
            night_dim_pct: 30,
            battery_low_threshold: 25,
            battery_critical_threshold: 15,
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContextState {
    pub hdr_active: bool,
    pub fullscreen_game: bool,
    pub night_mode: bool,
    pub wireless: bool,
    pub battery_percent: Option<u8>,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ResolvedLighting {
    pub brightness_pct: u8,
    pub mode: EffectMode,
    pub dimmed: bool,
}

pub fn resolve_context(
    base_brightness_pct: u8,
    base_mode: EffectMode,
    ctx: ContextState,
    cfg: AutoContextConfig,
) -> ResolvedLighting {
    let mut brightness = base_brightness_pct.min(100);
    let mut mode = base_mode;
    let mut dimmed = false;

    if ctx.wireless
        && let Some(pct) = ctx.battery_percent
    {
        if pct < cfg.battery_critical_threshold {
            mode = EffectMode::BatteryAlert { percent: pct };
            brightness = brightness.min(25);
            dimmed = true;
        } else if pct < cfg.battery_low_threshold {
            brightness = brightness.min(15);
            dimmed = true;
        }
    }

    if ctx.hdr_active {
        brightness = brightness.min(cfg.hdr_dim_pct);
        dimmed = true;
    } else if ctx.fullscreen_game {
        brightness = brightness.min(cfg.fullscreen_dim_pct);
        dimmed = true;
    }

    if ctx.night_mode {
        brightness = brightness.min(cfg.night_dim_pct);
        dimmed = true;
    }

    ResolvedLighting {
        brightness_pct: brightness,
        mode,
        dimmed,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transport::Rgb8;

    #[test]
    fn hdr_fullscreen_dims_and_restores_cleanly() {
        let cfg = AutoContextConfig::default();
        let base = EffectMode::Static { color: Rgb8::WHITE };

        let normal = resolve_context(100, base, ContextState::default(), cfg);
        assert_eq!(normal.brightness_pct, 100);
        assert!(!normal.dimmed);

        let hdr = resolve_context(
            100,
            base,
            ContextState {
                hdr_active: true,
                fullscreen_game: true,
                ..Default::default()
            },
            cfg,
        );
        assert_eq!(hdr.brightness_pct, cfg.hdr_dim_pct);
        assert!(hdr.dimmed);

        let restored = resolve_context(100, base, ContextState::default(), cfg);
        assert_eq!(restored.brightness_pct, 100);
        assert!(!restored.dimmed);
    }

    #[test]
    fn wireless_battery_saver_dims_and_alerts_under_15_percent() {
        let cfg = AutoContextConfig::default();
        let base = EffectMode::Static { color: Rgb8::WHITE };

        let low = resolve_context(
            100,
            base,
            ContextState {
                wireless: true,
                battery_percent: Some(20),
                ..Default::default()
            },
            cfg,
        );
        assert!(low.brightness_pct <= 15);
        assert!(low.dimmed);

        let crit = resolve_context(
            100,
            base,
            ContextState {
                wireless: true,
                battery_percent: Some(10),
                ..Default::default()
            },
            cfg,
        );
        assert!(matches!(
            crit.mode,
            EffectMode::BatteryAlert { percent: 10 }
        ));
    }
}
