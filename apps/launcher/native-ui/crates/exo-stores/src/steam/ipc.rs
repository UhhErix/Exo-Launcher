use std::collections::HashMap;
use std::path::PathBuf;

use exo_rpc::anticheat::AntiCheatClass;

use crate::security::StoreGamePolicy;

pub const CLIENT_APP_MANAGER_VERSION: &str = "CLIENTAPPMANAGER_INTERFACE_VERSION001";
pub const CLIENT_ENGINE_VERSIONS: &[&str] = &[
    "CLIENTENGINE_INTERFACE_VERSION005",
    "CLIENTENGINE_INTERFACE_VERSION004",
];

pub const ENGINE_SLOT_CREATE_STEAM_PIPE: usize = 0;
pub const ENGINE_SLOT_RELEASE_PIPE: usize = 1;
pub const ENGINE_SLOT_CONNECT_TO_GLOBAL_USER: usize = 3;
pub const ENGINE_SLOT_GET_CLIENT_APP_MANAGER: usize = 43;

pub const APP_SLOT_INSTALL_APP: usize = 0;
pub const APP_SLOT_UNINSTALL_APP: usize = 1;
pub const APP_SLOT_GET_APP_INSTALL_STATE: usize = 4;
pub const APP_SLOT_GET_UPDATE_INFO: usize = 20;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SteamIpcVerb {
    Install,
    Update,
    Uninstall,
    State,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SteamIpcResult {
    Ok,
    CommandFailed(i32),
    ClientUnavailable,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct SteamUpdateProgress {
    pub time_update_start: u32,
    pub bytes_to_download: u64,
    pub bytes_downloaded: u64,
    pub bytes_to_process: u64,
    pub bytes_processed: u64,
}

pub trait SteamIpcTransport {
    fn install_or_update(&mut self, app_id: u32) -> SteamIpcResult;
    fn uninstall(&mut self, app_id: u32) -> SteamIpcResult;
    fn install_state(&mut self, app_id: u32) -> Option<i32>;
    fn update_progress(&mut self, app_id: u32) -> Option<SteamUpdateProgress>;
}

#[derive(Debug, Default)]
pub struct MockSteamClientIpc {
    pub states: HashMap<u32, i32>,
    pub installs: Vec<u32>,
    pub uninstalls: Vec<u32>,
    pub progress: HashMap<u32, SteamUpdateProgress>,
}

impl SteamIpcTransport for MockSteamClientIpc {
    fn install_or_update(&mut self, app_id: u32) -> SteamIpcResult {
        self.installs.push(app_id);
        self.states.insert(app_id, 4);
        SteamIpcResult::Ok
    }

    fn uninstall(&mut self, app_id: u32) -> SteamIpcResult {
        self.uninstalls.push(app_id);
        self.states.insert(app_id, 0);
        SteamIpcResult::Ok
    }

    fn install_state(&mut self, app_id: u32) -> Option<i32> {
        self.states.get(&app_id).copied()
    }

    fn update_progress(&mut self, app_id: u32) -> Option<SteamUpdateProgress> {
        self.progress.get(&app_id).copied()
    }
}

pub fn steam_launch_uri(app_id: u32, anti_cheat: AntiCheatClass) -> (String, StoreGamePolicy) {
    let policy = StoreGamePolicy::for_class(anti_cheat);
    (format!("steam://rungameid/{app_id}"), policy)
}

pub fn discover_steam_root_readonly() -> Option<PathBuf> {
    let candidates = [
        PathBuf::from(r"C:\Program Files (x86)\Steam"),
        PathBuf::from(r"C:\Program Files\Steam"),
    ];
    candidates
        .into_iter()
        .find(|p| p.join("steamclient64.dll").is_file())
}

#[cfg(windows)]
pub struct LiveSteamClientIpc {
    steam_root: PathBuf,
}

#[cfg(windows)]
impl LiveSteamClientIpc {
    pub fn new(steam_root: PathBuf) -> Self {
        Self { steam_root }
    }

    pub fn discover() -> Option<Self> {
        discover_steam_root_readonly().map(Self::new)
    }

    pub fn steam_root(&self) -> &PathBuf {
        &self.steam_root
    }

    fn with_connected_manager<
        T,
        F: FnOnce(*mut std::ffi::c_void, &[*const std::ffi::c_void]) -> T,
    >(
        &self,
        f: F,
    ) -> Option<T> {
        use std::ffi::{CString, OsStr};
        use std::os::windows::ffi::OsStrExt;
        use std::ptr::null_mut;

        const LOAD_WITH_ALTERED_SEARCH_PATH: u32 = 8;

        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn LoadLibraryExW(
                lp_lib_file_name: *const u16,
                h_file: *mut std::ffi::c_void,
                dw_flags: u32,
            ) -> *mut std::ffi::c_void;
            fn GetProcAddress(
                h_module: *mut std::ffi::c_void,
                lp_proc_name: *const i8,
            ) -> *const std::ffi::c_void;
        }

        type CreateInterfaceFn = unsafe extern "C" fn(*const i8, *mut i32) -> *mut std::ffi::c_void;
        type CreatePipeFn = unsafe extern "system" fn(*mut std::ffi::c_void) -> i32;
        type ReleasePipeFn = unsafe extern "system" fn(*mut std::ffi::c_void, i32) -> u8;
        type ConnectUserFn = unsafe extern "system" fn(*mut std::ffi::c_void, i32) -> i32;
        type GetAppMgrFn = unsafe extern "system" fn(
            *mut std::ffi::c_void,
            i32,
            i32,
            *const i8,
        ) -> *mut std::ffi::c_void;

        let dll_path = self.steam_root.join("steamclient64.dll");
        if !dll_path.is_file() {
            return None;
        }
        let wide: Vec<u16> = OsStr::new(&dll_path)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let module =
            unsafe { LoadLibraryExW(wide.as_ptr(), null_mut(), LOAD_WITH_ALTERED_SEARCH_PATH) };
        if module.is_null() {
            return None;
        }
        let create_sym = CString::new("CreateInterface").ok()?;
        let create_ptr = unsafe { GetProcAddress(module, create_sym.as_ptr()) };
        if create_ptr.is_null() {
            return None;
        }
        let create_iface: CreateInterfaceFn = unsafe { std::mem::transmute(create_ptr) };

        let mut engine = null_mut();
        for ver in CLIENT_ENGINE_VERSIONS {
            let ver_c = CString::new(*ver).ok()?;
            let mut rc = 0i32;
            let p = unsafe { create_iface(ver_c.as_ptr(), &mut rc) };
            if !p.is_null() {
                engine = p;
                break;
            }
        }
        if engine.is_null() {
            return None;
        }

        let engine_vt = unsafe { *(engine as *const *const *const std::ffi::c_void) };
        if engine_vt.is_null() {
            return None;
        }
        let create_pipe: CreatePipeFn =
            unsafe { std::mem::transmute(*engine_vt.add(ENGINE_SLOT_CREATE_STEAM_PIPE)) };
        let release_pipe: ReleasePipeFn =
            unsafe { std::mem::transmute(*engine_vt.add(ENGINE_SLOT_RELEASE_PIPE)) };
        let connect_user: ConnectUserFn =
            unsafe { std::mem::transmute(*engine_vt.add(ENGINE_SLOT_CONNECT_TO_GLOBAL_USER)) };
        let get_mgr: GetAppMgrFn =
            unsafe { std::mem::transmute(*engine_vt.add(ENGINE_SLOT_GET_CLIENT_APP_MANAGER)) };

        let pipe = unsafe { create_pipe(engine) };
        if pipe == 0 {
            return None;
        }
        let user = unsafe { connect_user(engine, pipe) };
        if user == 0 {
            unsafe {
                release_pipe(engine, pipe);
            }
            return None;
        }
        let mgr_ver = CString::new(CLIENT_APP_MANAGER_VERSION).ok()?;
        let manager = unsafe { get_mgr(engine, user, pipe, mgr_ver.as_ptr()) };
        if manager.is_null() {
            unsafe {
                release_pipe(engine, pipe);
            }
            return None;
        }
        let mgr_vt_ptr = unsafe { *(manager as *const *const *const std::ffi::c_void) };
        if mgr_vt_ptr.is_null() {
            unsafe {
                release_pipe(engine, pipe);
            }
            return None;
        }
        let mgr_vt =
            unsafe { std::slice::from_raw_parts(mgr_vt_ptr, APP_SLOT_GET_UPDATE_INFO + 1) };
        let out = f(manager, mgr_vt);
        unsafe {
            release_pipe(engine, pipe);
        }
        Some(out)
    }
}

#[cfg(windows)]
impl SteamIpcTransport for LiveSteamClientIpc {
    fn install_or_update(&mut self, app_id: u32) -> SteamIpcResult {
        type InstallAppFn =
            unsafe extern "system" fn(*mut std::ffi::c_void, u32, *const i8, i32, u8) -> i32;
        match self.with_connected_manager(|mgr, vt| {
            let func: InstallAppFn = unsafe { std::mem::transmute(vt[APP_SLOT_INSTALL_APP]) };
            unsafe { func(mgr, app_id, std::ptr::null(), 0, 0) }
        }) {
            Some(0) => SteamIpcResult::Ok,
            Some(code) => SteamIpcResult::CommandFailed(code),
            None => SteamIpcResult::ClientUnavailable,
        }
    }

    fn uninstall(&mut self, app_id: u32) -> SteamIpcResult {
        type UninstallAppFn = unsafe extern "system" fn(*mut std::ffi::c_void, u32, u8) -> i32;
        match self.with_connected_manager(|mgr, vt| {
            let func: UninstallAppFn = unsafe { std::mem::transmute(vt[APP_SLOT_UNINSTALL_APP]) };
            unsafe { func(mgr, app_id, 1) }
        }) {
            Some(0) => SteamIpcResult::Ok,
            Some(code) => SteamIpcResult::CommandFailed(code),
            None => SteamIpcResult::ClientUnavailable,
        }
    }

    fn install_state(&mut self, app_id: u32) -> Option<i32> {
        type GetStateFn = unsafe extern "system" fn(*mut std::ffi::c_void, u32) -> i32;
        self.with_connected_manager(|mgr, vt| {
            let func: GetStateFn =
                unsafe { std::mem::transmute(vt[APP_SLOT_GET_APP_INSTALL_STATE]) };
            unsafe { func(mgr, app_id) }
        })
    }

    fn update_progress(&mut self, app_id: u32) -> Option<SteamUpdateProgress> {
        type GetUpdateFn = unsafe extern "system" fn(*mut std::ffi::c_void, u32, *mut u8) -> u32;
        self.with_connected_manager(|mgr, vt| {
            let func: GetUpdateFn = unsafe { std::mem::transmute(vt[APP_SLOT_GET_UPDATE_INFO]) };
            let mut raw = [0u8; 512];
            unsafe {
                func(mgr, app_id, raw.as_mut_ptr());
            }
            SteamUpdateProgress {
                time_update_start: u32::from_le_bytes([raw[0], raw[1], raw[2], raw[3]]),
                bytes_to_download: u64::from_le_bytes([
                    raw[8], raw[9], raw[10], raw[11], raw[12], raw[13], raw[14], raw[15],
                ]),
                bytes_downloaded: u64::from_le_bytes([
                    raw[16], raw[17], raw[18], raw[19], raw[20], raw[21], raw[22], raw[23],
                ]),
                bytes_to_process: u64::from_le_bytes([
                    raw[24], raw[25], raw[26], raw[27], raw[28], raw[29], raw[30], raw[31],
                ]),
                bytes_processed: u64::from_le_bytes([
                    raw[32], raw[33], raw[34], raw[35], raw[36], raw[37], raw[38], raw[39],
                ]),
            }
        })
    }
}
