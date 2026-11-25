use crate::policy::{PolicyContext, evaluate};
use serde::{Deserialize, Serialize};

pub const DRS_SR_PRESET: u32 = 0x10E4_1DF3;
pub const DRS_RR_PRESET: u32 = 0x10E4_1DF7;
pub const DRS_FG_PRESET: u32 = 0x10E4_1DF1;
pub const DRS_SR_OVERRIDE: u32 = 0x10E4_1E01;
pub const DRS_RR_OVERRIDE: u32 = 0x10E4_1E02;
pub const DRS_FG_OVERRIDE: u32 = 0x10E4_1E03;
pub const DRS_STREAMLINE_DLSS_OVERRIDE: u32 = 0x10E4_1E06;

pub const PRESET_K_TRANSFORMER: u32 = 11;
pub const PRESET_E_RR_TRANSFORMER: u32 = 5;
pub const PRESET_LATEST: u32 = 0x00FF_FFFF;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DlssDrsPreset {
    DriverDefault,
    TransformerK,
    LatestRecommended,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DrsSettingWrite {
    pub setting_id: u32,
    pub setting_hex: String,
    pub name: String,
    pub value: u32,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DrsOverridePlan {
    pub ac_safe: bool,
    pub touches_game_files: bool,
    pub preset: DlssDrsPreset,
    pub writes: Vec<DrsSettingWrite>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NvidiaDrsProbe {
    pub nvidia_gpu_detected: bool,
    pub adapter_name: String,
    pub nvapi_loaded: bool,
    pub driver_version: Option<u32>,
    pub driver_branch: Option<String>,
    pub supports_ngx_override: bool,
}

pub fn build_drs_override_plan(
    policy_ctx: PolicyContext,
    preset: DlssDrsPreset,
    enable_sr: bool,
    enable_rr: bool,
    enable_fg: bool,
) -> DrsOverridePlan {
    let decision = evaluate(policy_ctx);
    let mut writes = Vec::new();
    let (sr_val, rr_val, fg_val, override_flag) = match preset {
        DlssDrsPreset::DriverDefault => (0u32, 0u32, 0u32, 0u32),
        DlssDrsPreset::TransformerK => (
            PRESET_K_TRANSFORMER,
            PRESET_E_RR_TRANSFORMER,
            PRESET_LATEST,
            1u32,
        ),
        DlssDrsPreset::LatestRecommended => (PRESET_LATEST, PRESET_LATEST, PRESET_LATEST, 1u32),
    };

    if enable_sr {
        writes.push(setting(
            DRS_SR_OVERRIDE,
            "DLSS Super Resolution override",
            override_flag,
        ));
        writes.push(setting(
            DRS_SR_PRESET,
            "DLSS Super Resolution preset",
            sr_val,
        ));
        writes.push(setting(
            DRS_STREAMLINE_DLSS_OVERRIDE,
            "Streamline DLSS override",
            override_flag,
        ));
    }
    if enable_rr {
        writes.push(setting(
            DRS_RR_OVERRIDE,
            "DLSS Ray Reconstruction override",
            override_flag,
        ));
        writes.push(setting(
            DRS_RR_PRESET,
            "DLSS Ray Reconstruction preset",
            rr_val,
        ));
    }
    if enable_fg {
        writes.push(setting(
            DRS_FG_OVERRIDE,
            "DLSS Frame Generation override",
            override_flag,
        ));
        writes.push(setting(
            DRS_FG_PRESET,
            "DLSS Frame Generation preset",
            fg_val,
        ));
    }

    DrsOverridePlan {
        ac_safe: decision.drs_override_allowed,
        touches_game_files: false,
        preset,
        writes,
    }
}

fn setting(id: u32, name: &str, value: u32) -> DrsSettingWrite {
    DrsSettingWrite {
        setting_id: id,
        setting_hex: format!("0x{id:08X}"),
        name: name.to_string(),
        value,
    }
}

#[cfg(windows)]
pub fn probe_nvidia_drs_readonly() -> NvidiaDrsProbe {
    use std::ffi::{CStr, c_char, c_void};
    use windows::Win32::Foundation::FreeLibrary;
    use windows::Win32::Graphics::Dxgi::{
        CreateDXGIFactory2, DXGI_CREATE_FACTORY_FLAGS, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
        IDXGIAdapter1, IDXGIFactory6,
    };
    use windows::Win32::System::LibraryLoader::{
        GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
    };
    use windows::core::{s, w};

    let (nvidia_gpu_detected, adapter_name) = unsafe {
        CreateDXGIFactory2::<IDXGIFactory6>(DXGI_CREATE_FACTORY_FLAGS(0))
            .ok()
            .and_then(|f| {
                f.EnumAdapterByGpuPreference::<IDXGIAdapter1>(
                    0,
                    DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
                )
                .ok()
            })
            .and_then(|a| a.GetDesc1().ok())
            .map(|d| {
                let len = d
                    .Description
                    .iter()
                    .position(|&c| c == 0)
                    .unwrap_or(d.Description.len());
                let name = String::from_utf16_lossy(&d.Description[..len]);
                (d.VendorId == 0x10DE, name)
            })
            .unwrap_or((false, "Unknown GPU".into()))
    };

    let module = unsafe { LoadLibraryExW(w!("nvapi64.dll"), None, LOAD_LIBRARY_SEARCH_SYSTEM32) };
    let Ok(hmod) = module else {
        return NvidiaDrsProbe {
            nvidia_gpu_detected,
            adapter_name,
            nvapi_loaded: false,
            driver_version: None,
            driver_branch: None,
            supports_ngx_override: false,
        };
    };

    type QueryInterfaceFn = unsafe extern "C" fn(u32) -> *const c_void;
    type InitFn = unsafe extern "C" fn() -> i32;
    type UnloadFn = unsafe extern "C" fn() -> i32;
    type GetDriverVerFn = unsafe extern "C" fn(*mut u32, *mut c_char) -> i32;

    let mut driver_version = None;
    let mut driver_branch = None;

    unsafe {
        if let Some(raw_qi) = GetProcAddress(hmod, s!("nvapi_QueryInterface")) {
            let qi: QueryInterfaceFn = std::mem::transmute(raw_qi);
            let p_init = qi(0x0150_E828);
            let p_unload = qi(0xD22B_DD7E);
            let p_ver = qi(0x2926_AAAD);
            if !p_init.is_null() {
                let init: InitFn = std::mem::transmute(p_init);
                if init() == 0 {
                    if !p_ver.is_null() {
                        let get_ver: GetDriverVerFn = std::mem::transmute(p_ver);
                        let mut ver = 0u32;
                        let mut branch_buf = [0i8; 64];
                        if get_ver(&mut ver, branch_buf.as_mut_ptr()) == 0 {
                            driver_version = Some(ver);
                            let cstr = CStr::from_ptr(branch_buf.as_ptr());
                            driver_branch = Some(cstr.to_string_lossy().into_owned());
                        }
                    }
                    if !p_unload.is_null() {
                        let unload: UnloadFn = std::mem::transmute(p_unload);
                        let _ = unload();
                    }
                }
            }
        }
        let _ = FreeLibrary(hmod);
    }

    let supports_ngx_override = nvidia_gpu_detected && driver_version.is_some_and(|v| v >= 57200);

    NvidiaDrsProbe {
        nvidia_gpu_detected,
        adapter_name,
        nvapi_loaded: true,
        driver_version,
        driver_branch,
        supports_ngx_override,
    }
}

#[cfg(not(windows))]
pub fn probe_nvidia_drs_readonly() -> NvidiaDrsProbe {
    NvidiaDrsProbe {
        nvidia_gpu_detected: false,
        adapter_name: "Non-Windows".into(),
        nvapi_loaded: false,
        driver_version: None,
        driver_branch: None,
        supports_ngx_override: false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drs_override_plan_is_ac_safe_and_touches_zero_game_files_across_all_classes() {
        for ctx in [
            PolicyContext::competitive_pvp(),
            PolicyContext::unknown(),
            PolicyContext::online_pve(false),
            PolicyContext::offline_anti_cheat(false),
            PolicyContext::no_anti_cheat(),
        ] {
            let plan = build_drs_override_plan(ctx, DlssDrsPreset::TransformerK, true, true, false);
            assert!(plan.ac_safe);
            assert!(!plan.touches_game_files);
            assert!(
                plan.writes
                    .iter()
                    .any(|w| w.setting_id == DRS_SR_OVERRIDE && w.value == 1)
            );
            assert!(
                plan.writes
                    .iter()
                    .any(|w| w.setting_id == DRS_SR_PRESET && w.value == PRESET_K_TRANSFORMER)
            );
            assert!(
                plan.writes
                    .iter()
                    .any(|w| w.setting_id == DRS_STREAMLINE_DLSS_OVERRIDE && w.value == 1)
            );
        }
    }
}
