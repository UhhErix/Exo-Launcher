use std::ffi::{CString, OsStr};
use std::os::windows::ffi::OsStrExt;
use std::path::PathBuf;
use std::ptr::null_mut;
use std::time::{SystemTime, UNIX_EPOCH};

pub type Hmodule = *mut std::ffi::c_void;
pub type Handle = *mut std::ffi::c_void;

#[repr(C)]
struct ProcessMemoryCountersEx {
    cb: u32,
    page_fault_count: u32,
    peak_working_set_size: usize,
    working_set_size: usize,
    quota_peak_paged_pool_usage: usize,
    quota_paged_pool_usage: usize,
    quota_peak_non_paged_pool_usage: usize,
    quota_non_paged_pool_usage: usize,
    pagefile_usage: usize,
    peak_pagefile_usage: usize,
    private_usage: usize,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct FileTime {
    low: u32,
    high: u32,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn LoadLibraryW(lp_lib_file_name: *const u16) -> Hmodule;
    fn GetProcAddress(h_module: Hmodule, lp_proc_name: *const i8) -> *const std::ffi::c_void;
    fn FreeLibrary(h_lib_module: Hmodule) -> i32;
    fn GetSystemDirectoryW(lp_buffer: *mut u16, u_size: u32) -> u32;
    fn GetCurrentProcess() -> Handle;
    fn GetProcessTimes(
        h_process: Handle,
        creation: *mut FileTime,
        exit: *mut FileTime,
        kernel: *mut FileTime,
        user: *mut FileTime,
    ) -> i32;
}

#[link(name = "psapi")]
unsafe extern "system" {
    fn K32GetProcessMemoryInfo(
        process: Handle,
        ppsmem_counters: *mut ProcessMemoryCountersEx,
        cb: u32,
    ) -> i32;
}

pub struct DynLib {
    handle: Hmodule,
}

unsafe impl Send for DynLib {}
unsafe impl Sync for DynLib {}

impl DynLib {
    pub fn load_system32(dll_name: &str) -> Option<Self> {
        let mut path = system32_dir()?;
        path.push(dll_name);
        if !path.exists() {
            return None;
        }
        Self::load_exact_path(&path)
    }

    pub fn load_exact_path(path: &std::path::Path) -> Option<Self> {
        if !path.is_file() {
            return None;
        }
        let wide: Vec<u16> = OsStr::new(path.as_os_str())
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let handle = unsafe { LoadLibraryW(wide.as_ptr()) };
        if handle.is_null() {
            None
        } else {
            Some(Self { handle })
        }
    }

    pub fn symbol_raw(&self, name: &str) -> Option<*const std::ffi::c_void> {
        let cstr = CString::new(name).ok()?;
        let ptr = unsafe { GetProcAddress(self.handle, cstr.as_ptr()) };
        (!ptr.is_null()).then_some(ptr)
    }

    pub(crate) unsafe fn symbol<T: Copy>(&self, name: &str) -> Option<T> {
        let ptr = self.symbol_raw(name)?;
        Some(unsafe { std::mem::transmute_copy(&ptr) })
    }
}

impl Drop for DynLib {
    fn drop(&mut self) {
        if !self.handle.is_null() {
            unsafe {
                FreeLibrary(self.handle);
            }
            self.handle = null_mut();
        }
    }
}

pub fn system32_dir() -> Option<PathBuf> {
    let mut buf = [0u16; 260];
    let len = unsafe { GetSystemDirectoryW(buf.as_mut_ptr(), buf.len() as u32) };
    if len == 0 || (len as usize) >= buf.len() {
        return None;
    }
    Some(PathBuf::from(String::from_utf16_lossy(
        &buf[..len as usize],
    )))
}

pub fn now_unix_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

#[derive(Debug, Clone, Copy)]
pub struct ProcessMemoryMb {
    pub working_set_mb: f64,
    pub private_mb: f64,
    pub peak_working_set_mb: f64,
}

pub fn current_process_memory() -> Option<ProcessMemoryMb> {
    let mut counters = ProcessMemoryCountersEx {
        cb: std::mem::size_of::<ProcessMemoryCountersEx>() as u32,
        page_fault_count: 0,
        peak_working_set_size: 0,
        working_set_size: 0,
        quota_peak_paged_pool_usage: 0,
        quota_paged_pool_usage: 0,
        quota_peak_non_paged_pool_usage: 0,
        quota_non_paged_pool_usage: 0,
        pagefile_usage: 0,
        peak_pagefile_usage: 0,
        private_usage: 0,
    };
    let ok = unsafe {
        K32GetProcessMemoryInfo(
            GetCurrentProcess(),
            &mut counters,
            std::mem::size_of::<ProcessMemoryCountersEx>() as u32,
        )
    };
    if ok == 0 {
        return None;
    }
    const MB: f64 = 1024.0 * 1024.0;
    Some(ProcessMemoryMb {
        working_set_mb: counters.working_set_size as f64 / MB,
        private_mb: counters.private_usage as f64 / MB,
        peak_working_set_mb: counters.peak_working_set_size as f64 / MB,
    })
}

pub fn current_process_cpu_ms() -> Option<f64> {
    let (mut c, mut e, mut k, mut u) = (
        FileTime::default(),
        FileTime::default(),
        FileTime::default(),
        FileTime::default(),
    );
    let ok = unsafe { GetProcessTimes(GetCurrentProcess(), &mut c, &mut e, &mut k, &mut u) };
    if ok == 0 {
        return None;
    }
    let kt = ((k.high as u64) << 32) | (k.low as u64);
    let ut = ((u.high as u64) << 32) | (u.low as u64);
    Some((kt + ut) as f64 / 10_000.0)
}
