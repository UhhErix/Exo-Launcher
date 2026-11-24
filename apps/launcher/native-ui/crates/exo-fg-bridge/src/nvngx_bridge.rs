use crate::config::{BridgeBackend, BridgeConfig};
use crate::fakenvapi_shim::{VENDOR_NVIDIA, evaluate_shim};
use crate::ffx_abi::{
    FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION, FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE,
    FfxApiDimensions2D, FfxApiFloatCoords2D, FfxApiHeader, FfxApiResource,
    FfxDispatchDescFrameGeneration, FfxDispatchDescFrameGenerationPrepare, FfxRuntimeStatus,
    probe_ffx_status,
};
use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use std::ffi::c_void;
use std::path::Path;
use std::sync::OnceLock;
use std::time::Instant;

pub const STREAMLINE_FEATURE_DLSS_G: u32 = 1000;
pub const NGX_FEATURE_FRAME_GENERATION: u32 = 11;
pub const EXO_FG_BRIDGE_ABI_VERSION: u32 = 1;

pub const BRIDGE_OK: i32 = 0;
pub const BRIDGE_ERR_POLICY_DENIED: i32 = -1;
pub const BRIDGE_ERR_INVALID_ARGS: i32 = -2;
pub const BRIDGE_ERR_NOT_INITIALIZED: i32 = -3;

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct BridgeFrameInput {
    pub frame_id: u64,
    pub width: u32,
    pub height: u32,
    pub jitter_x: f32,
    pub jitter_y: f32,
    pub mv_scale_x: f32,
    pub mv_scale_y: f32,
    pub frame_time_delta_ms: f32,
    pub camera_near: f32,
    pub camera_far: f32,
    pub camera_fov_y_rad: f32,
    pub reset: u32,
    pub command_list: *mut c_void,
    pub color_resource: *mut c_void,
    pub depth_resource: *mut c_void,
    pub motion_vectors_resource: *mut c_void,
    pub hudless_resource: *mut c_void,
    pub output_resource: *mut c_void,
}

impl Default for BridgeFrameInput {
    fn default() -> Self {
        Self {
            frame_id: 0,
            width: 2560,
            height: 1440,
            jitter_x: 0.0,
            jitter_y: 0.0,
            mv_scale_x: 1.0,
            mv_scale_y: 1.0,
            frame_time_delta_ms: 16.667,
            camera_near: 0.1,
            camera_far: 1000.0,
            camera_fov_y_rad: std::f32::consts::FRAC_PI_3,
            reset: 0,
            command_list: std::ptr::null_mut(),
            color_resource: std::ptr::null_mut(),
            depth_resource: std::ptr::null_mut(),
            motion_vectors_resource: std::ptr::null_mut(),
            hudless_resource: std::ptr::null_mut(),
            output_resource: std::ptr::null_mut(),
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BridgeFrameStats {
    pub frame_id: u64,
    pub generated_frames: u32,
    pub routed_to_fsr31: u32,
    pub bridge_cpu_overhead_us: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BridgeSessionSummary {
    pub active: bool,
    pub backend: BridgeBackend,
    pub ffx_status: FfxRuntimeStatus,
    pub frames_translated: u64,
    pub generated_frames_total: u64,
    pub avg_bridge_cpu_us: f64,
}

struct BridgeRuntimeState {
    config: BridgeConfig,
    ffx_status: FfxRuntimeStatus,
    frames_translated: u64,
    generated_frames_total: u64,
    total_cpu_us: f64,
}

static GLOBAL_BRIDGE: OnceLock<Mutex<Option<BridgeRuntimeState>>> = OnceLock::new();

fn global_slot() -> &'static Mutex<Option<BridgeRuntimeState>> {
    GLOBAL_BRIDGE.get_or_init(|| Mutex::new(None))
}

pub fn should_report_dlssg_supported(
    config: &BridgeConfig,
    vendor_id: u32,
    nvidia_sm_arch: u32,
) -> bool {
    if !config.is_bridge_permitted() {
        return false;
    }
    if vendor_id == VENDOR_NVIDIA {
        return nvidia_sm_arch >= 75;
    }
    let shim = evaluate_shim(vendor_id, config.enable_fake_nvapi_shim, true);
    shim.active
}

pub fn translate_dlssg_to_ffx(
    input: &BridgeFrameInput,
    multiplier: u32,
) -> (
    FfxDispatchDescFrameGenerationPrepare,
    FfxDispatchDescFrameGeneration,
) {
    let dims = FfxApiDimensions2D {
        width: input.width,
        height: input.height,
    };
    let make_res = |ptr: *mut c_void| FfxApiResource {
        resource: ptr,
        width: input.width,
        height: input.height,
        depth: 1,
        mip_count: 1,
        ..Default::default()
    };
    let prep = FfxDispatchDescFrameGenerationPrepare {
        header: FfxApiHeader {
            desc_type: FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION_PREPARE,
            p_next: std::ptr::null_mut(),
        },
        frame_id: input.frame_id,
        flags: 0,
        command_list: input.command_list,
        render_size: dims,
        jitter_offset: FfxApiFloatCoords2D {
            x: input.jitter_x,
            y: input.jitter_y,
        },
        motion_vector_scale: FfxApiFloatCoords2D {
            x: input.mv_scale_x,
            y: input.mv_scale_y,
        },
        frame_time_delta_ms: input.frame_time_delta_ms,
        unused_reset: input.reset != 0,
        camera_near: input.camera_near,
        camera_far: input.camera_far,
        camera_fov_angle_vertical: input.camera_fov_y_rad,
        view_space_to_meters_factor: 1.0,
        depth: make_res(input.depth_resource),
        motion_vectors: make_res(input.motion_vectors_resource),
    };
    let color_src = if !input.hudless_resource.is_null() {
        input.hudless_resource
    } else {
        input.color_resource
    };
    let gen_count = multiplier.saturating_sub(1).clamp(1, 3);
    let mut outputs = [FfxApiResource::default(); 4];
    outputs[0] = make_res(input.output_resource);
    let dispatch = FfxDispatchDescFrameGeneration {
        header: FfxApiHeader {
            desc_type: FFX_API_DISPATCH_DESC_TYPE_FRAMEGENERATION,
            p_next: std::ptr::null_mut(),
        },
        command_list: input.command_list,
        present_color: make_res(color_src),
        outputs,
        num_generated_frames: gen_count,
        reset: input.reset != 0,
        backbuffer_transfer_function: 0,
        min_max_luminance: [0.0, 1000.0],
        frame_id: input.frame_id,
    };
    (prep, dispatch)
}

pub fn init_bridge(config: BridgeConfig) -> Result<BridgeSessionSummary, i32> {
    if !config.is_bridge_permitted() {
        return Err(BRIDGE_ERR_POLICY_DENIED);
    }
    let ffx_path = config.ffx_dll_path.as_deref().map(Path::new);
    let ffx_status = probe_ffx_status(ffx_path);
    let summary = BridgeSessionSummary {
        active: true,
        backend: config.backend,
        ffx_status: ffx_status.clone(),
        frames_translated: 0,
        generated_frames_total: 0,
        avg_bridge_cpu_us: 0.0,
    };
    let mut guard = global_slot().lock();
    *guard = Some(BridgeRuntimeState {
        config,
        ffx_status,
        frames_translated: 0,
        generated_frames_total: 0,
        total_cpu_us: 0.0,
    });
    Ok(summary)
}

pub fn query_dlssg_support(vendor_id: u32, nvidia_sm_arch: u32) -> i32 {
    let guard = global_slot().lock();
    let Some(state) = guard.as_ref() else {
        return 0;
    };
    i32::from(should_report_dlssg_supported(
        &state.config,
        vendor_id,
        nvidia_sm_arch,
    ))
}

pub fn evaluate_bridge_frame(input: &BridgeFrameInput) -> Result<BridgeFrameStats, i32> {
    let t0 = Instant::now();
    let mut guard = global_slot().lock();
    let Some(state) = guard.as_mut() else {
        return Err(BRIDGE_ERR_NOT_INITIALIZED);
    };
    if !state.config.is_bridge_permitted() {
        return Err(BRIDGE_ERR_POLICY_DENIED);
    }
    let (_prep, dispatch) = translate_dlssg_to_ffx(input, state.config.target_multiplier);
    let elapsed_us = t0.elapsed().as_secs_f64() * 1_000_000.0;
    state.frames_translated += 1;
    state.generated_frames_total += dispatch.num_generated_frames as u64;
    state.total_cpu_us += elapsed_us;
    Ok(BridgeFrameStats {
        frame_id: input.frame_id,
        generated_frames: dispatch.num_generated_frames,
        routed_to_fsr31: u32::from(state.config.backend != BridgeBackend::XessFgDx12),
        bridge_cpu_overhead_us: elapsed_us,
    })
}

pub fn shutdown_bridge() -> Option<BridgeSessionSummary> {
    let mut guard = global_slot().lock();
    guard.take().map(|s| BridgeSessionSummary {
        active: false,
        backend: s.config.backend,
        ffx_status: s.ffx_status,
        frames_translated: s.frames_translated,
        generated_frames_total: s.generated_frames_total,
        avg_bridge_cpu_us: if s.frames_translated > 0 {
            s.total_cpu_us / s.frames_translated as f64
        } else {
            0.0
        },
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::fakenvapi_shim::VENDOR_AMD;
    use exo_rpc::anticheat::AntiCheatClass;

    #[test]
    fn rtx_20_and_30_enable_dlssg_to_fsr31_routing_in_permitted_sessions() {
        let cfg = BridgeConfig {
            game_id: "steam:814380".into(),
            game_title: "Sekiro".into(),
            anti_cheat_class: AntiCheatClass::OfflineAntiCheat,
            has_anti_cheat: false,
            enable_fake_nvapi_shim: true,
            ..Default::default()
        };
        assert!(should_report_dlssg_supported(&cfg, VENDOR_NVIDIA, 75));
        assert!(should_report_dlssg_supported(&cfg, VENDOR_NVIDIA, 86));
        assert!(should_report_dlssg_supported(&cfg, VENDOR_AMD, 0));

        let blocked = BridgeConfig {
            anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
            has_anti_cheat: true,
            ..cfg.clone()
        };
        assert!(!should_report_dlssg_supported(&blocked, VENDOR_NVIDIA, 86));
        assert!(!should_report_dlssg_supported(&blocked, VENDOR_AMD, 0));
    }

    #[test]
    fn dlssg_resource_translation_prefers_hudless_color_when_supplied() {
        let mut dummy_color = 1u32;
        let mut dummy_hudless = 2u32;
        let input = BridgeFrameInput {
            frame_id: 99,
            width: 2560,
            height: 1440,
            color_resource: (&mut dummy_color as *mut u32).cast(),
            hudless_resource: (&mut dummy_hudless as *mut u32).cast(),
            ..Default::default()
        };
        let (prep, disp) = translate_dlssg_to_ffx(&input, 2);
        assert_eq!(prep.frame_id, 99);
        assert_eq!(disp.num_generated_frames, 1);
        assert_eq!(disp.present_color.resource, input.hudless_resource);
    }
}
