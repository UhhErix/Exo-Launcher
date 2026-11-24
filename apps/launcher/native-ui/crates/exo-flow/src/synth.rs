use bytemuck::{Pod, Zeroable};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MultiplierMode {
    Fixed2x,
    Fixed3x,
    VrrAdaptive,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ScalerMode {
    Off,
    Cas,
    Integer,
    Lanczos,
}

impl ScalerMode {
    pub fn shader_code(self) -> u32 {
        match self {
            Self::Off => 0,
            Self::Cas => 1,
            Self::Integer => 2,
            Self::Lanczos => 3,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FlowBackendKind {
    Auto,
    Nvof,
    ComputeDp4a,
    ComputeFp16,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum FlowScale {
    Half,
    Quarter,
}

impl FlowScale {
    #[allow(dead_code)]
    pub fn divisor(self) -> u32 {
        match self {
            Self::Half => 2,
            Self::Quarter => 4,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct FlowProfile {
    pub multiplier: MultiplierMode,
    pub target_hz: u32,
    pub backend: FlowBackendKind,
    pub flow_scale: FlowScale,
    pub hud_mask_enabled: bool,
    pub hud_sensitivity: f32,
    pub scaler: ScalerMode,
    pub sharpness: f32,
}

impl Default for FlowProfile {
    fn default() -> Self {
        Self {
            multiplier: MultiplierMode::Fixed2x,
            target_hz: 120,
            backend: FlowBackendKind::Auto,
            flow_scale: FlowScale::Half,
            hud_mask_enabled: true,
            hud_sensitivity: 0.5,
            scaler: ScalerMode::Cas,
            sharpness: 0.35,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct PyramidParams {
    pub dst_w: u32,
    pub dst_h: u32,
    pub src_w: u32,
    pub src_h: u32,
    pub pass_mode: u32,
    pub pad0: u32,
    pub pad1: u32,
    pub pad2: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct FlowSearchParams {
    pub level_w: u32,
    pub level_h: u32,
    pub coarse_w: u32,
    pub coarse_h: u32,
    pub has_coarse: u32,
    pub search_radius: i32,
    pub reg_weight: f32,
    pub pad0: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct FlowSmoothParams {
    pub width: u32,
    pub height: u32,
    pub edge_sigma: f32,
    pub temporal_alpha: f32,
    pub occ_threshold: f32,
    pub has_prev_mv: u32,
    pub pad0: u32,
    pub pad1: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct HudMaskParams {
    pub width: u32,
    pub height: u32,
    pub diff_thresh: f32,
    pub grad_thresh: f32,
    pub ema_decay: f32,
    pub mask_threshold: f32,
    pub pass_dilate: u32,
    pub pad0: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct SynthParams {
    pub dst_w: u32,
    pub dst_h: u32,
    pub src_w: u32,
    pub src_h: u32,
    pub alpha_t: f32,
    pub hud_mask_enabled: u32,
    pub scaler_mode: u32,
    pub sharpness: f32,
    pub mv_scale_x: f32,
    pub mv_scale_y: f32,
    pub pad0: u32,
    pub pad1: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, Pod, Zeroable)]
pub struct SynthSceneParams {
    pub width: u32,
    pub height: u32,
    pub time_sec: f32,
    pub pad0: u32,
}
