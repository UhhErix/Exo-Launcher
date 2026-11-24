use crate::synth::HudMaskParams;

#[derive(Clone, Copy, Debug)]
pub struct HudMaskConfig {
    pub diff_thresh: f32,
    pub grad_thresh: f32,
    pub ema_decay: f32,
    pub mask_threshold: f32,
}

impl HudMaskConfig {
    pub fn from_sensitivity(sensitivity: f32) -> Self {
        let s = sensitivity.clamp(0.0, 1.0);
        Self {
            diff_thresh: 0.008 + s * 0.024,
            grad_thresh: 0.06 - s * 0.035,
            ema_decay: 0.80,
            mask_threshold: 0.72 - s * 0.14,
        }
    }

    pub fn shader_params(self, width: u32, height: u32, pass_dilate: bool) -> HudMaskParams {
        HudMaskParams {
            width,
            height,
            diff_thresh: self.diff_thresh,
            grad_thresh: self.grad_thresh,
            ema_decay: self.ema_decay,
            mask_threshold: self.mask_threshold,
            pass_dilate: u32::from(pass_dilate),
            pad0: 0,
        }
    }
}

#[cfg(test)]
pub fn step_temporal_mask(
    cfg: HudMaskConfig,
    prev_ema_static: f32,
    prev_ema_grad: f32,
    pixel_diff: f32,
    spatial_grad: f32,
) -> (f32, f32, f32) {
    let is_static = if pixel_diff < cfg.diff_thresh {
        1.0
    } else {
        0.0
    };
    let ema_static = is_static * (1.0 - cfg.ema_decay) + prev_ema_static * cfg.ema_decay;
    let ema_grad = spatial_grad * (1.0 - cfg.ema_decay) + prev_ema_grad * cfg.ema_decay;
    let smoothstep = |e0: f32, e1: f32, x: f32| {
        let t = ((x - e0) / (e1 - e0).max(1e-5)).clamp(0.0, 1.0);
        t * t * (3.0 - 2.0 * t)
    };
    let base_static = smoothstep(
        cfg.mask_threshold - 0.10,
        cfg.mask_threshold + 0.04,
        ema_static,
    );
    let edge_boost = smoothstep(
        cfg.mask_threshold - 0.18,
        cfg.mask_threshold - 0.02,
        ema_static,
    ) * (ema_grad / cfg.grad_thresh.max(0.01)).clamp(0.0, 1.0);
    (
        ema_static,
        ema_grad,
        base_static.max(edge_boost).clamp(0.0, 1.0),
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn static_high_contrast_hud_converges_to_pinned_mask() {
        let cfg = HudMaskConfig::from_sensitivity(0.5);
        let mut s = 0.0;
        let mut g = 0.0;
        let mut mask = 0.0;
        for _ in 0..16 {
            (s, g, mask) = step_temporal_mask(cfg, s, g, 0.001, 0.25);
        }
        assert!(mask > 0.95, "expected pinned static HUD mask, got {mask}");
    }

    #[test]
    fn moving_world_pixel_stays_unmasked() {
        let cfg = HudMaskConfig::from_sensitivity(0.5);
        let mut s = 0.0;
        let mut g = 0.0;
        let mut mask = 0.0;
        for _ in 0..16 {
            (s, g, mask) = step_temporal_mask(cfg, s, g, 0.15, 0.20);
        }
        assert!(mask < 0.05, "expected moving pixel unmasked, got {mask}");
    }

    #[test]
    fn static_hud_releases_quickly_when_menu_closes() {
        let cfg = HudMaskConfig::from_sensitivity(0.5);
        let mut s = 0.98;
        let mut g = 0.30;
        let mut mask = 1.0;
        for _ in 0..6 {
            (s, g, mask) = step_temporal_mask(cfg, s, g, 0.25, 0.15);
        }
        assert!(mask < 0.1, "expected HUD mask release, got {mask}");
    }
}
