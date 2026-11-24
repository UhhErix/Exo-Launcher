use std::ptr::null_mut;

use crate::backends::TelemetryBackend;
use crate::types::{
    BackendKind, GpuSnapshot, GpuVendor, THROTTLE_CURRENT_LIMIT, THROTTLE_HW_THERMAL,
    THROTTLE_SW_POWER_CAP,
};
use crate::win32::{DynLib, now_unix_ms};

const CTL_RESULT_SUCCESS: i32 = 0;
const CTL_IMPL_VERSION_1_1: u32 = (1 << 16) | 1;
const CTL_ADAPTER_INTEGRATED: u32 = 1;

#[repr(C)]
struct CtlInitArgs {
    size: u32,
    version: u8,
    _pad: [u8; 3],
    app_version: u32,
    flags: u32,
    supported_version: u32,
    app_uid: [u8; 16],
}

#[repr(C)]
struct CtlDevicePropertiesRaw {
    bytes: [u8; 320],
}

#[repr(C)]
#[derive(Clone, Copy)]
struct CtlOcTelemetryItem {
    supported: u8,
    units: i32,
    data_type: i32,
    value: u64,
}

#[repr(C)]
struct CtlPowerTelemetry {
    size: u32,
    version: u8,
    _pad0: [u8; 3],
    time_stamp: CtlOcTelemetryItem,
    gpu_energy_counter: CtlOcTelemetryItem,
    gpu_voltage: CtlOcTelemetryItem,
    gpu_current_clock_freq: CtlOcTelemetryItem,
    gpu_current_temp: CtlOcTelemetryItem,
    global_activity_counter: CtlOcTelemetryItem,
    render_compute_activity_counter: CtlOcTelemetryItem,
    media_activity_counter: CtlOcTelemetryItem,
    gpu_power_limited: u8,
    gpu_temp_limited: u8,
    gpu_current_limited: u8,
    gpu_voltage_limited: u8,
    gpu_utilization_limited: u8,
    _pad1: [u8; 3],
    vram_energy_counter: CtlOcTelemetryItem,
    vram_voltage: CtlOcTelemetryItem,
    vram_current_clock_freq: CtlOcTelemetryItem,
    vram_current_effective_freq: CtlOcTelemetryItem,
    vram_read_bandwidth_counter: CtlOcTelemetryItem,
    vram_write_bandwidth_counter: CtlOcTelemetryItem,
    vram_current_temp: CtlOcTelemetryItem,
    vram_power_limited: u8,
    vram_temp_limited: u8,
    vram_current_limited: u8,
    vram_voltage_limited: u8,
    _pad2: [u8; 4],
    total_card_energy_counter: CtlOcTelemetryItem,
    psu: [CtlPsuInfo; 5],
    fan_speed: [CtlOcTelemetryItem; 5],
}

#[repr(C)]
#[derive(Clone, Copy)]
struct CtlPsuInfo {
    supported: u8,
    psu_type: i32,
    energy_counter: CtlOcTelemetryItem,
    voltage: CtlOcTelemetryItem,
}

type CtlInitFn = unsafe extern "C" fn(*mut CtlInitArgs, *mut *mut std::ffi::c_void) -> i32;
type CtlCloseFn = unsafe extern "C" fn(*mut std::ffi::c_void) -> i32;
type CtlEnumDevicesFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32, *mut *mut std::ffi::c_void) -> i32;
type CtlGetPropsFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut CtlDevicePropertiesRaw) -> i32;
type CtlPowerTelemetryGetFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut CtlPowerTelemetry) -> i32;

pub struct IntelIgclBackend {
    _lib: DynLib,
    close: CtlCloseFn,
    api: *mut std::ffi::c_void,
    device: *mut std::ffi::c_void,
    name: String,
    power_fn: CtlPowerTelemetryGetFn,
    prev_energy_j: Option<f64>,
    prev_ts_s: Option<f64>,
}

impl IntelIgclBackend {
    pub fn open() -> Option<Self> {
        let lib = DynLib::load_system32("ControlLib.dll")?;
        let init: CtlInitFn = unsafe { lib.symbol("ctlInit") }?;
        let close: CtlCloseFn = unsafe { lib.symbol("ctlClose") }?;
        let enum_dev: CtlEnumDevicesFn = unsafe { lib.symbol("ctlEnumerateDevices") }?;
        let power_fn: CtlPowerTelemetryGetFn = unsafe { lib.symbol("ctlPowerTelemetryGet") }?;
        let props_fn: Option<CtlGetPropsFn> = unsafe { lib.symbol("ctlGetDeviceProperties") };

        let mut args = CtlInitArgs {
            size: std::mem::size_of::<CtlInitArgs>() as u32,
            version: 0,
            _pad: [0; 3],
            app_version: CTL_IMPL_VERSION_1_1,
            flags: 0,
            supported_version: 0,
            app_uid: [0; 16],
        };
        let mut api: *mut std::ffi::c_void = null_mut();
        if unsafe { init(&mut args, &mut api) } != CTL_RESULT_SUCCESS || api.is_null() {
            return None;
        }

        let mut count = 0u32;
        if unsafe { enum_dev(api, &mut count, null_mut()) } != CTL_RESULT_SUCCESS || count == 0 {
            unsafe {
                close(api);
            }
            return None;
        }
        let mut devices = vec![null_mut(); (count as usize).min(8)];
        let mut cap = devices.len() as u32;
        if unsafe { enum_dev(api, &mut cap, devices.as_mut_ptr()) } != CTL_RESULT_SUCCESS
            || cap == 0
        {
            unsafe {
                close(api);
            }
            return None;
        }
        devices.truncate(cap as usize);

        let mut chosen = devices[0];
        let mut name = "Intel Graphics".to_string();
        if let Some(get_props) = props_fn {
            for &dev in &devices {
                let mut raw = CtlDevicePropertiesRaw { bytes: [0; 320] };
                let mut luid = 0i64;
                raw.bytes[0..4].copy_from_slice(&320u32.to_le_bytes());
                let luid_ptr = (&mut luid as *mut i64) as usize;
                raw.bytes[8..16].copy_from_slice(&luid_ptr.to_le_bytes());
                raw.bytes[16..20].copy_from_slice(&8u32.to_le_bytes());
                if unsafe { get_props(dev, &mut raw) } == CTL_RESULT_SUCCESS {
                    let flags = u32::from_le_bytes(raw.bytes[188..192].try_into().unwrap());
                    let nul = raw.bytes[88..188]
                        .iter()
                        .position(|&b| b == 0)
                        .unwrap_or(100);
                    let parsed = String::from_utf8_lossy(&raw.bytes[88..88 + nul])
                        .trim()
                        .to_string();
                    if (flags & CTL_ADAPTER_INTEGRATED) == 0 {
                        chosen = dev;
                        if !parsed.is_empty() {
                            name = parsed;
                        }
                        break;
                    } else if !parsed.is_empty() && name == "Intel Graphics" {
                        name = parsed;
                    }
                }
            }
        }

        Some(Self {
            _lib: lib,
            close,
            api,
            device: chosen,
            name,
            power_fn,
            prev_energy_j: None,
            prev_ts_s: None,
        })
    }
}

impl Drop for IntelIgclBackend {
    fn drop(&mut self) {
        if !self.api.is_null() {
            unsafe {
                (self.close)(self.api);
            }
            self.api = null_mut();
        }
    }
}

impl TelemetryBackend for IntelIgclBackend {
    fn kind(&self) -> BackendKind {
        BackendKind::IntelIgcl
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let mut tel: CtlPowerTelemetry = unsafe { std::mem::zeroed() };
        tel.size = std::mem::size_of::<CtlPowerTelemetry>() as u32;
        tel.version = 1;
        if unsafe { (self.power_fn)(self.device, &mut tel) } != CTL_RESULT_SUCCESS {
            return None;
        }

        let ts_s = item_f64(&tel.time_stamp);
        let energy_j =
            item_f64(&tel.total_card_energy_counter).or_else(|| item_f64(&tel.gpu_energy_counter));

        let board_power_w = match (energy_j, ts_s, self.prev_energy_j, self.prev_ts_s) {
            (Some(e), Some(t), Some(pe), Some(pt)) if t > pt && e >= pe => {
                Some((e - pe) / (t - pt))
            }
            _ => None,
        };
        if let (Some(e), Some(t)) = (energy_j, ts_s) {
            self.prev_energy_j = Some(e);
            self.prev_ts_s = Some(t);
        }

        let core_temp_c = item_f64(&tel.gpu_current_temp);
        let memory_temp_c = item_f64(&tel.vram_current_temp);
        let gpu_clock_mhz = item_f64(&tel.gpu_current_clock_freq).map(|v| v.max(0.0) as u32);
        let mem_clock_mhz = item_f64(&tel.vram_current_clock_freq).map(|v| v.max(0.0) as u32);
        let fan_rpm = tel
            .fan_speed
            .iter()
            .filter_map(item_f64)
            .map(|v| v.max(0.0) as u32)
            .max();

        let mut throttle_flags = 0u32;
        if tel.gpu_power_limited != 0 || tel.vram_power_limited != 0 {
            throttle_flags |= THROTTLE_SW_POWER_CAP;
        }
        if tel.gpu_temp_limited != 0 || tel.vram_temp_limited != 0 {
            throttle_flags |= THROTTLE_HW_THERMAL;
        }
        if tel.gpu_current_limited != 0 || tel.vram_current_limited != 0 {
            throttle_flags |= THROTTLE_CURRENT_LIMIT;
        }

        Some(GpuSnapshot {
            timestamp_ms: now_unix_ms(),
            backend: BackendKind::IntelIgcl,
            vendor: GpuVendor::Intel,
            name: self.name.clone(),
            pci_bus: None,
            core_temp_c,
            hotspot_temp_c: None,
            memory_temp_c,
            board_power_w,
            power_limit_w: None,
            enforced_power_limit_w: None,
            min_power_limit_w: None,
            max_power_limit_w: None,
            power_pct: None,
            gpu_clock_mhz,
            mem_clock_mhz,
            vram_used_mb: None,
            vram_total_mb: None,
            gpu_util_pct: None,
            fan_pct: None,
            fan_rpm,
            pstate: None,
            pcie_gen: None,
            pcie_width: None,
            temp_target_c: Some(90),
            temp_max_c: Some(100),
            temp_slowdown_c: Some(100),
            throttle_flags,
            connector: None,
        })
    }
}

fn item_f64(item: &CtlOcTelemetryItem) -> Option<f64> {
    if item.supported == 0 {
        return None;
    }
    let val = match item.data_type {
        0 => (item.value as i32) as f64,
        1 => (item.value as u32) as f64,
        2 => f32::from_bits(item.value as u32) as f64,
        3 => f64::from_bits(item.value),
        4 => (item.value as i64) as f64,
        5 => item.value as f64,
        _ => return None,
    };
    val.is_finite().then_some(val)
}
