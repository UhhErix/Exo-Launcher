pub mod config;
pub mod dxgi_proxy;
pub mod fakenvapi_shim;
pub mod ffx_abi;
pub mod journal;
pub mod nvngx_bridge;

use crate::config::BridgeConfig;
use crate::nvngx_bridge::{
    BRIDGE_ERR_INVALID_ARGS, BRIDGE_OK, BridgeFrameInput, BridgeFrameStats,
    EXO_FG_BRIDGE_ABI_VERSION, evaluate_bridge_frame, init_bridge, query_dlssg_support,
    shutdown_bridge,
};

#[cfg(windows)]
#[allow(clippy::missing_safety_doc)]
#[unsafe(no_mangle)]
pub unsafe extern "system" fn DllMain(
    hinst: windows::Win32::Foundation::HMODULE,
    reason: u32,
    _reserved: *mut std::ffi::c_void,
) -> i32 {
    use windows::Win32::System::LibraryLoader::DisableThreadLibraryCalls;
    const DLL_PROCESS_ATTACH: u32 = 1;
    if reason == DLL_PROCESS_ATTACH {
        unsafe {
            let _ = DisableThreadLibraryCalls(hinst);
        }
    }
    1
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_fg_bridge_version() -> u32 {
    EXO_FG_BRIDGE_ABI_VERSION
}

#[allow(clippy::missing_safety_doc)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_fg_bridge_init(config_json_utf8: *const u8, len: usize) -> i32 {
    if config_json_utf8.is_null() || len == 0 {
        return BRIDGE_ERR_INVALID_ARGS;
    }
    let slice = unsafe { std::slice::from_raw_parts(config_json_utf8, len) };
    let Ok(cfg) = BridgeConfig::from_json_bytes(slice) else {
        return BRIDGE_ERR_INVALID_ARGS;
    };
    match init_bridge(cfg) {
        Ok(_) => BRIDGE_OK,
        Err(code) => code,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_fg_bridge_query_dlssg_support(vendor_id: u32, nvidia_sm_arch: u32) -> i32 {
    query_dlssg_support(vendor_id, nvidia_sm_arch)
}

#[allow(clippy::missing_safety_doc)]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn exo_fg_bridge_evaluate_frame(
    input: *const BridgeFrameInput,
    out_stats: *mut BridgeFrameStats,
) -> i32 {
    if input.is_null() || out_stats.is_null() {
        return BRIDGE_ERR_INVALID_ARGS;
    }
    let frame_in = unsafe { &*input };
    match evaluate_bridge_frame(frame_in) {
        Ok(stats) => {
            unsafe {
                *out_stats = stats;
            }
            BRIDGE_OK
        }
        Err(code) => code,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn exo_fg_bridge_shutdown() -> i32 {
    let _ = shutdown_bridge();
    BRIDGE_OK
}
