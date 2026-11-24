use std::ptr::null_mut;
use std::time::Instant;

use exo_sensors::win32::DynLib;

const NVAPI_INIT: u32 = 0x0150_E828;
const NVAPI_DRS_CREATE_SESSION: u32 = 0x0694_D52E;
const NVAPI_DRS_LOAD_SETTINGS: u32 = 0x375D_BD6B;
const NVAPI_DRS_GET_CURRENT_GLOBAL_PROFILE: u32 = 0x617B_FF9F;
const NVAPI_DRS_GET_SETTING: u32 = 0x73BF_8338;
const NVAPI_DRS_DESTROY_SESSION: u32 = 0xDAD9_CFF8;

pub const DRS_SHADER_CACHE_SIZE: u32 = 0x19B1_E9E4;
pub const DRS_LOW_LATENCY_MODE: u32 = 0x0005_F543;
pub const DRS_POWER_MANAGEMENT: u32 = 0x1057_EB71;
pub const DRS_TEXTURE_FILTERING_QUALITY: u32 = 0x00CE_2691;
pub const DRS_VSYNC: u32 = 0x00A8_79CF;
pub const DRS_MAX_FRAME_RATE: u32 = 0x1083_5016;
pub const DRS_REBAR_ENABLE: u32 = 0x000F_00BA;

const UNICODE_BYTES: usize = 4096;
const SETTING_ID_AT: usize = 4 + UNICODE_BYTES;
const SETTING_TYPE_AT: usize = SETTING_ID_AT + 4;
const SETTING_PREDEFINED_AT: usize = SETTING_ID_AT + 12;
const CURRENT_VALUE_AT: usize = SETTING_ID_AT + 20 + 4100;
const NVDRS_SETTING_V1_SIZE: usize = CURRENT_VALUE_AT + 4100;

type QueryInterfaceFn = unsafe extern "C" fn(u32) -> *const std::ffi::c_void;
type InitFn = unsafe extern "C" fn() -> i32;
type CreateSessionFn = unsafe extern "C" fn(*mut *mut std::ffi::c_void) -> i32;
type SessionOnlyFn = unsafe extern "C" fn(*mut std::ffi::c_void) -> i32;
type GetProfileFn = unsafe extern "C" fn(*mut std::ffi::c_void, *mut *mut std::ffi::c_void) -> i32;
type GetSettingFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut std::ffi::c_void, u32, *mut u8) -> i32;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DrsSettingReadout {
    pub id: u32,
    pub label: &'static str,
    pub raw_value: Option<u32>,
    pub display_value: String,
    pub is_user_override: bool,
}

#[derive(Debug, Clone)]
pub struct NvidiaDrsSnapshot {
    pub read_latency_ms: f64,
    pub settings: Vec<DrsSettingReadout>,
}

pub fn read_global_drs_readonly() -> Option<NvidiaDrsSnapshot> {
    let t0 = Instant::now();
    let lib = DynLib::load_system32("nvapi64.dll")?;
    let qi_ptr = lib.symbol_raw("nvapi_QueryInterface")?;
    let qi: QueryInterfaceFn = unsafe { std::mem::transmute(qi_ptr) };

    let init_ptr = unsafe { qi(NVAPI_INIT) };
    if init_ptr.is_null() {
        return None;
    }
    let init: InitFn = unsafe { std::mem::transmute(init_ptr) };
    if unsafe { init() } != 0 {
        return None;
    }

    let create_ptr = unsafe { qi(NVAPI_DRS_CREATE_SESSION) };
    let load_ptr = unsafe { qi(NVAPI_DRS_LOAD_SETTINGS) };
    let global_ptr = unsafe { qi(NVAPI_DRS_GET_CURRENT_GLOBAL_PROFILE) };
    let get_ptr = unsafe { qi(NVAPI_DRS_GET_SETTING) };
    let destroy_ptr = unsafe { qi(NVAPI_DRS_DESTROY_SESSION) };
    if create_ptr.is_null()
        || load_ptr.is_null()
        || global_ptr.is_null()
        || get_ptr.is_null()
        || destroy_ptr.is_null()
    {
        return None;
    }

    let create: CreateSessionFn = unsafe { std::mem::transmute(create_ptr) };
    let load: SessionOnlyFn = unsafe { std::mem::transmute(load_ptr) };
    let get_global: GetProfileFn = unsafe { std::mem::transmute(global_ptr) };
    let get_setting: GetSettingFn = unsafe { std::mem::transmute(get_ptr) };
    let destroy: SessionOnlyFn = unsafe { std::mem::transmute(destroy_ptr) };

    let mut session: *mut std::ffi::c_void = null_mut();
    if unsafe { create(&mut session) } != 0 || session.is_null() {
        return None;
    }

    let out = (|| {
        if unsafe { load(session) } != 0 {
            return None;
        }
        let mut profile: *mut std::ffi::c_void = null_mut();
        if unsafe { get_global(session, &mut profile) } != 0 || profile.is_null() {
            return None;
        }

        let catalog: [(u32, &'static str); 7] = [
            (DRS_SHADER_CACHE_SIZE, "Shader cache size"),
            (DRS_LOW_LATENCY_MODE, "Low latency mode"),
            (DRS_POWER_MANAGEMENT, "Power management"),
            (DRS_TEXTURE_FILTERING_QUALITY, "Texture filtering"),
            (DRS_VSYNC, "Vertical sync"),
            (DRS_MAX_FRAME_RATE, "Max frame rate"),
            (DRS_REBAR_ENABLE, "Resizable BAR"),
        ];

        let mut buf = vec![0u8; NVDRS_SETTING_V1_SIZE];
        let mut settings = Vec::with_capacity(catalog.len());

        for (id, label) in catalog {
            buf.fill(0);
            let version = (NVDRS_SETTING_V1_SIZE as u32) | (1u32 << 16);
            buf[0..4].copy_from_slice(&version.to_le_bytes());

            let rc = unsafe { get_setting(session, profile, id, buf.as_mut_ptr()) };
            if rc == 0 {
                let stype = u32::from_le_bytes(
                    buf[SETTING_TYPE_AT..SETTING_TYPE_AT + 4]
                        .try_into()
                        .unwrap(),
                );
                let predefined = u32::from_le_bytes(
                    buf[SETTING_PREDEFINED_AT..SETTING_PREDEFINED_AT + 4]
                        .try_into()
                        .unwrap(),
                );
                if stype == 0 {
                    let val = u32::from_le_bytes(
                        buf[CURRENT_VALUE_AT..CURRENT_VALUE_AT + 4]
                            .try_into()
                            .unwrap(),
                    );
                    let is_user = predefined == 0;
                    settings.push(DrsSettingReadout {
                        id,
                        label,
                        raw_value: Some(val),
                        display_value: format_drs_value(id, Some(val)),
                        is_user_override: is_user,
                    });
                    continue;
                }
            }
            settings.push(DrsSettingReadout {
                id,
                label,
                raw_value: None,
                display_value: "Driver default".into(),
                is_user_override: false,
            });
        }

        Some(NvidiaDrsSnapshot {
            read_latency_ms: t0.elapsed().as_secs_f64() * 1000.0,
            settings,
        })
    })();

    unsafe {
        destroy(session);
    }
    out
}

pub fn format_drs_value(id: u32, raw: Option<u32>) -> String {
    let Some(v) = raw else {
        return "Driver default".into();
    };
    match id {
        DRS_SHADER_CACHE_SIZE => match v {
            0 => "Disabled".into(),
            0xFFFF_FFFF => "Unlimited".into(),
            mb => format!("{mb} MB"),
        },
        DRS_LOW_LATENCY_MODE => match v {
            0 => "Off".into(),
            1 => "On".into(),
            2 => "Ultra".into(),
            _ => format!("0x{v:X}"),
        },
        DRS_POWER_MANAGEMENT => match v {
            0 => "Optimal power".into(),
            1 => "Prefer maximum performance".into(),
            5 => "Normal".into(),
            _ => format!("0x{v:X}"),
        },
        DRS_TEXTURE_FILTERING_QUALITY => match v {
            0 => "Quality".into(),
            10 => "Performance".into(),
            20 => "High performance".into(),
            0xFFFF_FFF6 => "High quality".into(),
            _ => format!("0x{v:X}"),
        },
        DRS_VSYNC => match v {
            0x0841_6747 => "Force off".into(),
            0x4781_4940 => "Force on".into(),
            0x1888_8888 => "Adaptive".into(),
            _ => "Use 3D app setting".into(),
        },
        DRS_MAX_FRAME_RATE => {
            if v == 0 {
                "Off".into()
            } else {
                format!("{v} fps")
            }
        }
        DRS_REBAR_ENABLE => {
            if v != 0 {
                "Enabled".into()
            } else {
                "Disabled".into()
            }
        }
        _ => format!("0x{v:X}"),
    }
}
