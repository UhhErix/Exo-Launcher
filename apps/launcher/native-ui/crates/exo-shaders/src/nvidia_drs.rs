use serde::{Deserialize, Serialize};
use std::ffi::c_void;
use std::path::Path;
use std::ptr::null_mut;
use windows::Win32::Foundation::{FreeLibrary, HMODULE};
use windows::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
use windows::Win32::System::LibraryLoader::{
    GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
};
use windows::core::{PCSTR, PCWSTR};

pub const PS_SHADERDISKCACHE_MAX_SIZE_ID: u32 = 0x00AC_8497;
pub const CACHE_LIMIT_UNLIMITED_RAW: u32 = 0xFFFF_FFFF;
pub const MIN_FREE_GB_FOR_UNLIMITED: u64 = 50;

const NVAPI_OK: i32 = 0;
const NVAPI_SETTING_NOT_FOUND: i32 = -160;
const NVAPI_INVALID_USER_PRIVILEGE: i32 = -137;

const NVAPI_INIT: u32 = 0x0150_E828;
const NVAPI_DRS_CREATE_SESSION: u32 = 0x0694_D52E;
const NVAPI_DRS_LOAD_SETTINGS: u32 = 0x375D_BD6B;
const NVAPI_DRS_GET_CURRENT_GLOBAL_PROFILE: u32 = 0x617B_FF9F;
const NVAPI_DRS_GET_SETTING: u32 = 0x73BF_8338;
const NVAPI_DRS_SET_SETTING: u32 = 0x577D_D202;
const NVAPI_DRS_DELETE_PROFILE_SETTING: u32 = 0xE4A2_6362;
const NVAPI_DRS_RESTORE_PROFILE_DEFAULT_SETTING: u32 = 0x53F0_381E;
const NVAPI_DRS_SAVE_SETTINGS: u32 = 0xFCBC_7E14;
const NVAPI_DRS_DESTROY_SESSION: u32 = 0xDAD9_CFF8;

const UNICODE_BYTES: usize = 4096;
const SETTING_ID_AT: usize = 4 + UNICODE_BYTES;
const SETTING_TYPE_AT: usize = SETTING_ID_AT + 4;
const SETTING_LOCATION_AT: usize = SETTING_ID_AT + 8;
const CURRENT_PREDEFINED_AT: usize = SETTING_ID_AT + 12;
const PREDEFINED_VALID_AT: usize = SETTING_ID_AT + 16;
const CURRENT_VALUE_AT: usize = SETTING_ID_AT + 20 + 4100;
const NVDRS_SETTING_V1_SIZE: usize = CURRENT_VALUE_AT + 4100;
const NVDRS_SETTING_V1_VERSION: u32 = (NVDRS_SETTING_V1_SIZE as u32) | (1u32 << 16);

type QueryInterfaceFn = unsafe extern "C" fn(u32) -> *const c_void;
type InitFn = unsafe extern "C" fn() -> i32;
type CreateSessionFn = unsafe extern "C" fn(*mut *mut c_void) -> i32;
type SessionOnlyFn = unsafe extern "C" fn(*mut c_void) -> i32;
type GetProfileFn = unsafe extern "C" fn(*mut c_void, *mut *mut c_void) -> i32;
type GetSettingFn = unsafe extern "C" fn(*mut c_void, *mut c_void, u32, *mut u8) -> i32;
type SetSettingFn = unsafe extern "C" fn(*mut c_void, *mut c_void, *const u8) -> i32;
type ResetSettingFn = unsafe extern "C" fn(*mut c_void, *mut c_void, u32) -> i32;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NvidiaCacheLimitStatus {
    pub available: bool,
    pub configurable: bool,
    pub setting_id: u32,
    pub raw_value: Option<u32>,
    pub limit_current: String,
    pub is_driver_default: bool,
    pub largest: bool,
    pub disabled: bool,
    pub off_warning: Option<String>,
    pub free_gb_on_cache_drive: Option<u64>,
}

impl NvidiaCacheLimitStatus {
    pub fn unavailable() -> Self {
        Self {
            available: false,
            configurable: false,
            setting_id: PS_SHADERDISKCACHE_MAX_SIZE_ID,
            raw_value: None,
            limit_current: "Managed by driver".into(),
            is_driver_default: true,
            largest: false,
            disabled: false,
            off_warning: None,
            free_gb_on_cache_drive: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CacheLimitPreset {
    Disabled,
    DriverDefault,
    Megabytes(u32),
    Unlimited,
}

pub fn preset_for_index(index: u32) -> Result<CacheLimitPreset, String> {
    match index {
        0 => Ok(CacheLimitPreset::Disabled),
        1 => Ok(CacheLimitPreset::DriverDefault),
        2 => Ok(CacheLimitPreset::Megabytes(128)),
        3 => Ok(CacheLimitPreset::Megabytes(256)),
        4 => Ok(CacheLimitPreset::Megabytes(512)),
        5 => Ok(CacheLimitPreset::Megabytes(1024)),
        6 => Ok(CacheLimitPreset::Megabytes(4096)),
        7 => Ok(CacheLimitPreset::Megabytes(5120)),
        8 => Ok(CacheLimitPreset::Megabytes(10240)),
        9 => Ok(CacheLimitPreset::Megabytes(16384)),
        10 => Ok(CacheLimitPreset::Megabytes(32768)),
        11 => Ok(CacheLimitPreset::Megabytes(102400)),
        12 => Ok(CacheLimitPreset::Unlimited),
        other => Err(format!(
            "Unsupported NVIDIA shader cache size index {other} (valid: 0..=12)."
        )),
    }
}

pub fn format_cache_limit_value(raw: Option<u32>) -> String {
    match raw {
        None => "Driver default".into(),
        Some(0) => "Disabled".into(),
        Some(CACHE_LIMIT_UNLIMITED_RAW) => "Unlimited".into(),
        Some(mb) if mb >= 1024 && mb.is_multiple_of(1024) => format!("{} GB", mb / 1024),
        Some(mb) => format!("{mb} MB"),
    }
}

pub fn status_from_raw(
    raw: Option<u32>,
    free_gb_on_cache_drive: Option<u64>,
) -> NvidiaCacheLimitStatus {
    let disabled = raw == Some(0);
    let largest = raw == Some(CACHE_LIMIT_UNLIMITED_RAW);
    let is_driver_default = raw.is_none();
    let off_warning = if disabled {
        Some(
            "NVIDIA Shader Cache Size is Disabled. Games will recompile shaders every launch."
                .into(),
        )
    } else {
        None
    };
    NvidiaCacheLimitStatus {
        available: true,
        configurable: true,
        setting_id: PS_SHADERDISKCACHE_MAX_SIZE_ID,
        raw_value: raw,
        limit_current: format_cache_limit_value(raw),
        is_driver_default,
        largest,
        disabled,
        off_warning,
        free_gb_on_cache_drive,
    }
}

pub fn validate_preset_for_free_space(
    preset: CacheLimitPreset,
    free_gb: Option<u64>,
) -> Result<(), String> {
    if preset == CacheLimitPreset::Unlimited
        && let Some(gb) = free_gb
        && gb < MIN_FREE_GB_FOR_UNLIMITED
    {
        return Err(format!(
            "Refusing Unlimited NVIDIA shader cache with only {gb} GB free (minimum {MIN_FREE_GB_FOR_UNLIMITED} GB required)."
        ));
    }
    Ok(())
}

pub fn free_gb_for_path(path: &Path) -> Option<u64> {
    let mut wide: Vec<u16> = path.as_os_str().to_string_lossy().encode_utf16().collect();
    wide.push(0);
    let mut free_avail = 0u64;
    let ok = unsafe {
        GetDiskFreeSpaceExW(PCWSTR(wide.as_ptr()), Some(&mut free_avail), None, None).is_ok()
    };
    if ok {
        Some(free_avail / (1024 * 1024 * 1024))
    } else {
        None
    }
}

struct LoadedNvApi {
    module: HMODULE,
    qi: QueryInterfaceFn,
}

impl Drop for LoadedNvApi {
    fn drop(&mut self) {
        unsafe {
            let _ = FreeLibrary(self.module);
        }
    }
}

impl LoadedNvApi {
    fn load() -> Result<Self, String> {
        let mut wide: Vec<u16> = "nvapi64.dll".encode_utf16().collect();
        wide.push(0);
        let module = unsafe {
            LoadLibraryExW(PCWSTR(wide.as_ptr()), None, LOAD_LIBRARY_SEARCH_SYSTEM32)
                .map_err(|e| format!("nvapi64.dll unavailable: {e}"))?
        };
        let proc_name = b"nvapi_QueryInterface\0";
        let Some(proc_fn) = (unsafe { GetProcAddress(module, PCSTR(proc_name.as_ptr())) }) else {
            unsafe {
                let _ = FreeLibrary(module);
            }
            return Err("nvapi_QueryInterface not exported".into());
        };
        let qi: QueryInterfaceFn = unsafe { std::mem::transmute(proc_fn) };
        let init_ptr = unsafe { qi(NVAPI_INIT) };
        if init_ptr.is_null() {
            unsafe {
                let _ = FreeLibrary(module);
            }
            return Err("NvAPI_Initialize not found".into());
        }
        let init: InitFn = unsafe { std::mem::transmute(init_ptr) };
        let rc = unsafe { init() };
        if rc != NVAPI_OK {
            unsafe {
                let _ = FreeLibrary(module);
            }
            return Err(format!("NvAPI_Initialize failed with status {rc}"));
        }
        Ok(Self { module, qi })
    }

    fn resolve(&self, id: u32) -> Result<*const c_void, String> {
        let p = unsafe { (self.qi)(id) };
        if p.is_null() {
            Err(format!("NvAPI interface 0x{id:08X} unavailable"))
        } else {
            Ok(p)
        }
    }
}

struct DrsSession<'a> {
    api: &'a LoadedNvApi,
    session: *mut c_void,
    profile: *mut c_void,
}

impl<'a> DrsSession<'a> {
    fn open_global(api: &'a LoadedNvApi) -> Result<Self, String> {
        let create: CreateSessionFn =
            unsafe { std::mem::transmute(api.resolve(NVAPI_DRS_CREATE_SESSION)?) };
        let load: SessionOnlyFn =
            unsafe { std::mem::transmute(api.resolve(NVAPI_DRS_LOAD_SETTINGS)?) };
        let get_global: GetProfileFn =
            unsafe { std::mem::transmute(api.resolve(NVAPI_DRS_GET_CURRENT_GLOBAL_PROFILE)?) };
        let destroy: SessionOnlyFn =
            unsafe { std::mem::transmute(api.resolve(NVAPI_DRS_DESTROY_SESSION)?) };

        let mut session: *mut c_void = null_mut();
        let rc = unsafe { create(&mut session) };
        if rc != NVAPI_OK || session.is_null() {
            return Err(format!("NvAPI_DRS_CreateSession failed ({rc})"));
        }
        let rc = unsafe { load(session) };
        if rc != NVAPI_OK {
            unsafe {
                let _ = destroy(session);
            }
            return Err(format!("NvAPI_DRS_LoadSettings failed ({rc})"));
        }
        let mut profile: *mut c_void = null_mut();
        let rc = unsafe { get_global(session, &mut profile) };
        if rc != NVAPI_OK || profile.is_null() {
            unsafe {
                let _ = destroy(session);
            }
            return Err(format!("NvAPI_DRS_GetCurrentGlobalProfile failed ({rc})"));
        }
        Ok(Self {
            api,
            session,
            profile,
        })
    }

    fn read_dword(&self, setting_id: u32) -> Result<Option<u32>, String> {
        let get_setting: GetSettingFn =
            unsafe { std::mem::transmute(self.api.resolve(NVAPI_DRS_GET_SETTING)?) };
        let mut buf = vec![0u8; NVDRS_SETTING_V1_SIZE];
        buf[0..4].copy_from_slice(&NVDRS_SETTING_V1_VERSION.to_le_bytes());
        let rc = unsafe { get_setting(self.session, self.profile, setting_id, buf.as_mut_ptr()) };
        if rc == NVAPI_SETTING_NOT_FOUND {
            return Ok(None);
        }
        if rc != NVAPI_OK {
            return Err(format!("NvAPI_DRS_GetSetting failed ({rc})"));
        }
        let stype = u32::from_le_bytes(
            buf[SETTING_TYPE_AT..SETTING_TYPE_AT + 4]
                .try_into()
                .unwrap(),
        );
        let location = u32::from_le_bytes(
            buf[SETTING_LOCATION_AT..SETTING_LOCATION_AT + 4]
                .try_into()
                .unwrap(),
        );
        let predefined = u32::from_le_bytes(
            buf[CURRENT_PREDEFINED_AT..CURRENT_PREDEFINED_AT + 4]
                .try_into()
                .unwrap(),
        );
        if location == 0 && predefined == 0 && stype == 0 {
            let val = u32::from_le_bytes(
                buf[CURRENT_VALUE_AT..CURRENT_VALUE_AT + 4]
                    .try_into()
                    .unwrap(),
            );
            Ok(Some(val))
        } else {
            Ok(None)
        }
    }

    fn write_dword(&self, setting_id: u32, value: u32) -> Result<(), String> {
        let set_setting: SetSettingFn =
            unsafe { std::mem::transmute(self.api.resolve(NVAPI_DRS_SET_SETTING)?) };
        let mut buf = vec![0u8; NVDRS_SETTING_V1_SIZE];
        buf[0..4].copy_from_slice(&NVDRS_SETTING_V1_VERSION.to_le_bytes());
        buf[SETTING_ID_AT..SETTING_ID_AT + 4].copy_from_slice(&setting_id.to_le_bytes());
        buf[CURRENT_VALUE_AT..CURRENT_VALUE_AT + 4].copy_from_slice(&value.to_le_bytes());
        let rc = unsafe { set_setting(self.session, self.profile, buf.as_ptr()) };
        if rc == NVAPI_INVALID_USER_PRIVILEGE {
            return Err("Windows denied permission to modify the NVIDIA global profile.".into());
        }
        if rc != NVAPI_OK {
            return Err(format!("NvAPI_DRS_SetSetting failed ({rc})"));
        }
        Ok(())
    }

    fn reset_to_default(&self, setting_id: u32) -> Result<(), String> {
        let get_setting: GetSettingFn =
            unsafe { std::mem::transmute(self.api.resolve(NVAPI_DRS_GET_SETTING)?) };
        let mut buf = vec![0u8; NVDRS_SETTING_V1_SIZE];
        buf[0..4].copy_from_slice(&NVDRS_SETTING_V1_VERSION.to_le_bytes());
        let rc = unsafe { get_setting(self.session, self.profile, setting_id, buf.as_mut_ptr()) };
        if rc == NVAPI_SETTING_NOT_FOUND {
            return Ok(());
        }
        if rc != NVAPI_OK {
            return Err(format!("NvAPI_DRS_GetSetting before reset failed ({rc})"));
        }
        let location = u32::from_le_bytes(
            buf[SETTING_LOCATION_AT..SETTING_LOCATION_AT + 4]
                .try_into()
                .unwrap(),
        );
        let predefined = u32::from_le_bytes(
            buf[CURRENT_PREDEFINED_AT..CURRENT_PREDEFINED_AT + 4]
                .try_into()
                .unwrap(),
        );
        if location != 0 || predefined != 0 {
            return Ok(());
        }
        let has_predefined = u32::from_le_bytes(
            buf[PREDEFINED_VALID_AT..PREDEFINED_VALID_AT + 4]
                .try_into()
                .unwrap(),
        ) != 0;
        let reset_id = if has_predefined {
            NVAPI_DRS_RESTORE_PROFILE_DEFAULT_SETTING
        } else {
            NVAPI_DRS_DELETE_PROFILE_SETTING
        };
        let reset_fn: ResetSettingFn = unsafe { std::mem::transmute(self.api.resolve(reset_id)?) };
        let rc = unsafe { reset_fn(self.session, self.profile, setting_id) };
        if rc != NVAPI_OK && rc != NVAPI_SETTING_NOT_FOUND {
            return Err(format!("NvAPI_DRS reset setting failed ({rc})"));
        }
        Ok(())
    }

    fn save(&self) -> Result<(), String> {
        let save_fn: SessionOnlyFn =
            unsafe { std::mem::transmute(self.api.resolve(NVAPI_DRS_SAVE_SETTINGS)?) };
        let rc = unsafe { save_fn(self.session) };
        if rc == NVAPI_INVALID_USER_PRIVILEGE {
            return Err("Windows denied permission to save the NVIDIA global profile.".into());
        }
        if rc != NVAPI_OK {
            return Err(format!("NvAPI_DRS_SaveSettings failed ({rc})"));
        }
        Ok(())
    }
}

impl Drop for DrsSession<'_> {
    fn drop(&mut self) {
        if let Ok(ptr) = self.api.resolve(NVAPI_DRS_DESTROY_SESSION) {
            let destroy: SessionOnlyFn = unsafe { std::mem::transmute(ptr) };
            unsafe {
                let _ = destroy(self.session);
            }
        }
    }
}

pub fn read_nvidia_cache_limit() -> NvidiaCacheLimitStatus {
    let free_gb = std::env::var("LOCALAPPDATA")
        .ok()
        .and_then(|local| free_gb_for_path(Path::new(&local)));
    let Ok(api) = LoadedNvApi::load() else {
        return NvidiaCacheLimitStatus::unavailable();
    };
    let Ok(session) = DrsSession::open_global(&api) else {
        return NvidiaCacheLimitStatus::unavailable();
    };
    match session.read_dword(PS_SHADERDISKCACHE_MAX_SIZE_ID) {
        Ok(raw) => status_from_raw(raw, free_gb),
        Err(_) => NvidiaCacheLimitStatus::unavailable(),
    }
}

pub fn apply_nvidia_cache_limit(
    index: u32,
    free_gb_override: Option<u64>,
) -> Result<NvidiaCacheLimitStatus, String> {
    let preset = preset_for_index(index)?;
    let free_gb = free_gb_override.or_else(|| {
        std::env::var("LOCALAPPDATA")
            .ok()
            .and_then(|local| free_gb_for_path(Path::new(&local)))
    });
    validate_preset_for_free_space(preset, free_gb)?;

    let api = LoadedNvApi::load()?;
    let session = DrsSession::open_global(&api)?;
    match preset {
        CacheLimitPreset::DriverDefault => {
            session.reset_to_default(PS_SHADERDISKCACHE_MAX_SIZE_ID)?;
        }
        CacheLimitPreset::Disabled => {
            session.write_dword(PS_SHADERDISKCACHE_MAX_SIZE_ID, 0)?;
        }
        CacheLimitPreset::Megabytes(mb) => {
            session.write_dword(PS_SHADERDISKCACHE_MAX_SIZE_ID, mb)?;
        }
        CacheLimitPreset::Unlimited => {
            session.write_dword(PS_SHADERDISKCACHE_MAX_SIZE_ID, CACHE_LIMIT_UNLIMITED_RAW)?;
        }
    }
    session.save()?;
    let read_back = session.read_dword(PS_SHADERDISKCACHE_MAX_SIZE_ID)?;
    Ok(status_from_raw(read_back, free_gb))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_cache_limit_indices_and_formats_values() {
        assert_eq!(preset_for_index(0).unwrap(), CacheLimitPreset::Disabled);
        assert_eq!(
            preset_for_index(1).unwrap(),
            CacheLimitPreset::DriverDefault
        );
        assert_eq!(
            preset_for_index(6).unwrap(),
            CacheLimitPreset::Megabytes(4096)
        );
        assert_eq!(
            preset_for_index(8).unwrap(),
            CacheLimitPreset::Megabytes(10240)
        );
        assert_eq!(preset_for_index(12).unwrap(), CacheLimitPreset::Unlimited);
        assert!(preset_for_index(13).is_err());

        assert_eq!(format_cache_limit_value(None), "Driver default");
        assert_eq!(format_cache_limit_value(Some(0)), "Disabled");
        assert_eq!(format_cache_limit_value(Some(512)), "512 MB");
        assert_eq!(format_cache_limit_value(Some(10240)), "10 GB");
        assert_eq!(
            format_cache_limit_value(Some(CACHE_LIMIT_UNLIMITED_RAW)),
            "Unlimited"
        );
    }

    #[test]
    fn enforces_free_space_guard_for_unlimited() {
        assert!(validate_preset_for_free_space(CacheLimitPreset::Unlimited, Some(49)).is_err());
        assert!(validate_preset_for_free_space(CacheLimitPreset::Unlimited, Some(50)).is_ok());
        assert!(
            validate_preset_for_free_space(CacheLimitPreset::Megabytes(10240), Some(10)).is_ok()
        );
    }

    #[test]
    fn status_flags_disabled_and_unlimited_honestly() {
        let disabled = status_from_raw(Some(0), Some(200));
        assert!(disabled.disabled);
        assert!(!disabled.largest);
        assert!(disabled.off_warning.is_some());

        let unlimited = status_from_raw(Some(CACHE_LIMIT_UNLIMITED_RAW), Some(200));
        assert!(unlimited.largest);
        assert!(!unlimited.disabled);
        assert!(!unlimited.is_driver_default);
        assert!(unlimited.off_warning.is_none());

        let default = status_from_raw(None, Some(200));
        assert!(default.is_driver_default);
        assert!(!default.largest);
        assert_eq!(default.limit_current, "Driver default");
    }

    #[test]
    fn reads_live_nvidia_drs_without_mutating_profile() {
        let status = read_nvidia_cache_limit();
        assert_eq!(status.setting_id, PS_SHADERDISKCACHE_MAX_SIZE_ID);
        if Path::new(r"C:\Windows\System32\nvapi64.dll").is_file() {
            assert!(status.available);
            assert!(status.configurable);
            assert!(!status.limit_current.is_empty());
        }
    }
}
