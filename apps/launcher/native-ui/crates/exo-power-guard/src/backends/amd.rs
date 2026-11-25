use std::ffi::CStr;
use std::mem::MaybeUninit;
use std::ptr::NonNull;

use crate::backends::TelemetryBackend;
use crate::types::{BackendKind, GpuSnapshot, GpuVendor};
use crate::win32::{DynLib, now_unix_ms};

const ADLX_OK: i32 = 0;
const SLOT_RELEASE: usize = 1;
const SYSTEM_GET_GPUS: usize = 1;
const SYSTEM_GET_PERF: usize = 9;
const LIST_SIZE: usize = 3;
const LIST_AT: usize = 11;
const GPU_TYPE: usize = 5;
const GPU_NAME: usize = 7;
const GPU_TOTAL_VRAM: usize = 11;
const GPU_TYPE_DISCRETE: i32 = 2;
const PERF_CURRENT_GPU: usize = 18;
const PERF_SUPPORTED_GPU: usize = 21;
const AMD_HOTSPOT_MAX_C: u32 = 110;

type AdlxQueryFullVersion = unsafe extern "C" fn(*mut u64) -> i32;
type AdlxInitialize = unsafe extern "C" fn(u64, *mut *mut std::ffi::c_void) -> i32;
type AdlxTerminate = unsafe extern "C" fn() -> i32;

pub struct AmdAdlxBackend {
    _lib: DynLib,
    terminate: Option<AdlxTerminate>,
    system: NonNull<std::ffi::c_void>,
    gpu: Option<NonNull<std::ffi::c_void>>,
    name: String,
    vram_total_mb: Option<u64>,
}

impl AmdAdlxBackend {
    pub fn open() -> Option<Self> {
        let lib = DynLib::load_system32("amdadlx64.dll")?;
        let query_ver: AdlxQueryFullVersion = unsafe { lib.symbol("ADLXQueryFullVersion") }?;
        let init: AdlxInitialize = unsafe { lib.symbol("ADLXInitialize") }?;
        let terminate: Option<AdlxTerminate> = unsafe { lib.symbol("ADLXTerminate") };

        let mut version = 0u64;
        if unsafe { query_ver(&mut version) } != ADLX_OK {
            return None;
        }
        let mut system_out = MaybeUninit::<*mut std::ffi::c_void>::zeroed();
        if unsafe { init(version, system_out.as_mut_ptr()) } != ADLX_OK {
            return None;
        }
        let system = NonNull::new(unsafe { system_out.assume_init() })?;

        let Some(list) = (unsafe { call_out_ptr(system, SYSTEM_GET_GPUS) }) else {
            if let Some(t) = terminate {
                unsafe {
                    t();
                }
            }
            return None;
        };

        let size: u32 = unsafe {
            let f: unsafe extern "system" fn(*mut std::ffi::c_void) -> u32 =
                vtable_fn(list, LIST_SIZE);
            f(list.as_ptr())
        };

        let mut chosen: Option<NonNull<std::ffi::c_void>> = None;
        let mut chosen_discrete = false;
        for idx in 0..size {
            let mut item_out = MaybeUninit::<*mut std::ffi::c_void>::zeroed();
            let at_rc = unsafe {
                let f: unsafe extern "system" fn(
                    *mut std::ffi::c_void,
                    u32,
                    *mut *mut std::ffi::c_void,
                ) -> i32 = vtable_fn(list, LIST_AT);
                f(list.as_ptr(), idx, item_out.as_mut_ptr())
            };
            let Some(item) = (at_rc == ADLX_OK)
                .then(|| NonNull::new(unsafe { item_out.assume_init() }))
                .flatten()
            else {
                continue;
            };
            let mut gtype = 0i32;
            let is_discrete = unsafe {
                let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut i32) -> i32 =
                    vtable_fn(item, GPU_TYPE);
                f(item.as_ptr(), &mut gtype) == ADLX_OK && gtype == GPU_TYPE_DISCRETE
            };
            if chosen.is_none() || (is_discrete && !chosen_discrete) {
                if let Some(prev) = chosen {
                    unsafe { release(prev) };
                }
                chosen = Some(item);
                chosen_discrete = is_discrete;
            } else {
                unsafe { release(item) };
            }
        }
        unsafe { release(list) };

        let Some(chosen) = chosen else {
            if let Some(t) = terminate {
                unsafe {
                    t();
                }
            }
            return None;
        };

        let name = unsafe {
            let mut text: *const i8 = std::ptr::null();
            let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut *const i8) -> i32 =
                vtable_fn(chosen, GPU_NAME);
            if f(chosen.as_ptr(), &mut text) == ADLX_OK && !text.is_null() {
                CStr::from_ptr(text).to_string_lossy().trim().to_string()
            } else {
                "AMD Radeon GPU".to_string()
            }
        };

        let vram_total_mb = unsafe {
            let mut total = 0u32;
            let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut u32) -> i32 =
                vtable_fn(chosen, GPU_TOTAL_VRAM);
            (f(chosen.as_ptr(), &mut total) == ADLX_OK && total > 0).then_some(total as u64)
        };

        Some(Self {
            _lib: lib,
            terminate,
            system,
            gpu: Some(chosen),
            name,
            vram_total_mb,
        })
    }
}

impl Drop for AmdAdlxBackend {
    fn drop(&mut self) {
        unsafe {
            if let Some(gpu) = self.gpu.take() {
                release(gpu);
            }
            if let Some(t) = self.terminate {
                t();
            }
        }
    }
}

impl TelemetryBackend for AmdAdlxBackend {
    fn kind(&self) -> BackendKind {
        BackendKind::AmdAdlx
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let gpu = self.gpu?;
        let perf = unsafe { call_out_ptr(self.system, SYSTEM_GET_PERF) }?;
        let mut support: Option<NonNull<std::ffi::c_void>> = None;
        let mut metrics: Option<NonNull<std::ffi::c_void>> = None;

        let res = unsafe {
            let get_sup: unsafe extern "system" fn(
                *mut std::ffi::c_void,
                *mut std::ffi::c_void,
                *mut *mut std::ffi::c_void,
            ) -> i32 = vtable_fn(perf, PERF_SUPPORTED_GPU);
            let get_cur: unsafe extern "system" fn(
                *mut std::ffi::c_void,
                *mut std::ffi::c_void,
                *mut *mut std::ffi::c_void,
            ) -> i32 = vtable_fn(perf, PERF_CURRENT_GPU);

            let mut sup_out = MaybeUninit::<*mut std::ffi::c_void>::zeroed();
            if get_sup(perf.as_ptr(), gpu.as_ptr(), sup_out.as_mut_ptr()) == ADLX_OK {
                support = NonNull::new(sup_out.assume_init());
            }
            let mut cur_out = MaybeUninit::<*mut std::ffi::c_void>::zeroed();
            if get_cur(perf.as_ptr(), gpu.as_ptr(), cur_out.as_mut_ptr()) == ADLX_OK {
                metrics = NonNull::new(cur_out.assume_init());
            }

            if let (Some(sup), Some(cur)) = (support, metrics) {
                let usage = read_real(sup, cur, 3, 4);
                let gpu_clock = read_int(sup, cur, 4, 5).map(|v| v.max(0) as u32);
                let mem_clock = read_int(sup, cur, 5, 6).map(|v| v.max(0) as u32);
                let core_temp = read_real(sup, cur, 6, 7);
                let hotspot = read_real(sup, cur, 7, 8);
                let total_board_power =
                    read_real(sup, cur, 9, 10).or_else(|| read_real(sup, cur, 8, 9));
                let fan_rpm = read_int(sup, cur, 10, 11).map(|v| v.max(0) as u32);
                let vram_used = read_int(sup, cur, 11, 12).map(|v| v.max(0) as u64);

                Some(GpuSnapshot {
                    timestamp_ms: now_unix_ms(),
                    backend: BackendKind::AmdAdlx,
                    vendor: GpuVendor::Amd,
                    name: self.name.clone(),
                    pci_bus: None,
                    core_temp_c: core_temp,
                    hotspot_temp_c: hotspot,
                    memory_temp_c: None,
                    board_power_w: total_board_power,
                    power_limit_w: None,
                    enforced_power_limit_w: None,
                    min_power_limit_w: None,
                    max_power_limit_w: None,
                    power_pct: None,
                    gpu_clock_mhz: gpu_clock,
                    mem_clock_mhz: mem_clock,
                    vram_used_mb: vram_used,
                    vram_total_mb: self.vram_total_mb,
                    gpu_util_pct: usage.map(|u| u.round().clamp(0.0, 100.0) as u32),
                    fan_pct: None,
                    fan_rpm,
                    pstate: None,
                    pcie_gen: None,
                    pcie_width: None,
                    temp_target_c: hotspot.map(|_| AMD_HOTSPOT_MAX_C - 10),
                    temp_max_c: hotspot.map(|_| AMD_HOTSPOT_MAX_C),
                    temp_slowdown_c: hotspot.map(|_| AMD_HOTSPOT_MAX_C),
                    throttle_flags: 0,
                    connector: None,
                })
            } else {
                None
            }
        };

        unsafe {
            if let Some(cur) = metrics {
                release(cur);
            }
            if let Some(sup) = support {
                release(sup);
            }
            release(perf);
        }
        res
    }
}

unsafe fn vtable_fn<T: Copy>(obj: NonNull<std::ffi::c_void>, slot: usize) -> T {
    let vtable = unsafe { *(obj.as_ptr() as *const *const *const std::ffi::c_void) };
    let ptr = unsafe { *vtable.add(slot) };
    unsafe { std::mem::transmute_copy(&ptr) }
}

unsafe fn call_out_ptr(
    obj: NonNull<std::ffi::c_void>,
    slot: usize,
) -> Option<NonNull<std::ffi::c_void>> {
    let mut out = MaybeUninit::<*mut std::ffi::c_void>::zeroed();
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut *mut std::ffi::c_void) -> i32 =
        unsafe { vtable_fn(obj, slot) };
    if unsafe { f(obj.as_ptr(), out.as_mut_ptr()) } != ADLX_OK {
        return None;
    }
    NonNull::new(unsafe { out.assume_init() })
}

unsafe fn release(obj: NonNull<std::ffi::c_void>) {
    let f: unsafe extern "system" fn(*mut std::ffi::c_void) -> i64 =
        unsafe { vtable_fn(obj, SLOT_RELEASE) };
    unsafe {
        f(obj.as_ptr());
    }
}

unsafe fn is_supported(support: NonNull<std::ffi::c_void>, slot: usize) -> bool {
    let mut on = 0u8;
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut u8) -> i32 =
        unsafe { vtable_fn(support, slot) };
    unsafe { f(support.as_ptr(), &mut on) == ADLX_OK && on != 0 }
}

unsafe fn read_real(
    support: NonNull<std::ffi::c_void>,
    metrics: NonNull<std::ffi::c_void>,
    sup_slot: usize,
    val_slot: usize,
) -> Option<f64> {
    if !unsafe { is_supported(support, sup_slot) } {
        return None;
    }
    let mut val = 0.0_f64;
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut f64) -> i32 =
        unsafe { vtable_fn(metrics, val_slot) };
    (unsafe { f(metrics.as_ptr(), &mut val) } == ADLX_OK && val.is_finite()).then_some(val)
}

unsafe fn read_int(
    support: NonNull<std::ffi::c_void>,
    metrics: NonNull<std::ffi::c_void>,
    sup_slot: usize,
    val_slot: usize,
) -> Option<i32> {
    if !unsafe { is_supported(support, sup_slot) } {
        return None;
    }
    let mut val = 0i32;
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut i32) -> i32 =
        unsafe { vtable_fn(metrics, val_slot) };
    (unsafe { f(metrics.as_ptr(), &mut val) } == ADLX_OK).then_some(val)
}
