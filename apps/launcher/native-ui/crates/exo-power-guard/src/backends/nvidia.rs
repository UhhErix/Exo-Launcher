use std::ffi::CStr;
use std::ptr::null_mut;

use crate::backends::TelemetryBackend;
use crate::types::{
    BackendKind, ConnectorSnapshot, GpuSnapshot, GpuVendor, PerPinSource, PinReading,
    THROTTLE_HW_POWER_BRAKE, THROTTLE_HW_THERMAL, THROTTLE_SW_POWER_CAP, THROTTLE_SW_THERMAL,
};
use crate::win32::{DynLib, now_unix_ms};

const NVML_SUCCESS: i32 = 0;
const NVML_TEMP_GPU: u32 = 0;
const NVML_CLOCK_GRAPHICS: u32 = 0;
const NVML_CLOCK_MEM: u32 = 2;
const NVML_THRESH_SLOWDOWN: i32 = 1;
const NVML_THRESH_GPU_MAX: i32 = 3;
const NVML_THRESH_TARGET: i32 = 5;

const NVML_EVENT_SW_POWER_CAP: u64 = 0x0000_0000_0000_0004;
const NVML_EVENT_SW_THERMAL_SLOWDOWN: u64 = 0x0000_0000_0000_0020;
const NVML_EVENT_HW_THERMAL_SLOWDOWN: u64 = 0x0000_0000_0000_0040;
const NVML_EVENT_HW_POWER_BRAKE: u64 = 0x0000_0000_0000_0080;

const NVAPI_INIT: u32 = 0x0150_E828;
const NVAPI_ENUM_PHYSICAL_GPUS: u32 = 0xE5AC_921F;
const NVAPI_GPU_GET_PCI_IDENTIFIERS: u32 = 0x2DDF_B66E;
const NVAPI_GPU_GET_BUS_ID: u32 = 0x1BE0_B8E5;
const NVAPI_GPU_GET_THERMAL_SETTINGS: u32 = 0xE364_0A56;
const NVAPI_I2C_READ_EX: u32 = 0x4D7B_0709;

const ASTRAL_SUBSYSTEM_IDS: [u32; 5] = [
    0x89EA_1043,
    0x8A61_1043,
    0x89EC_1043,
    0x89E3_1043,
    0x89DE_1043,
];

type NvmlInit = unsafe extern "C" fn() -> i32;
type NvmlShutdown = unsafe extern "C" fn() -> i32;
type NvmlDeviceGetHandleByIndex = unsafe extern "C" fn(u32, *mut *mut std::ffi::c_void) -> i32;
type NvmlDeviceGetName = unsafe extern "C" fn(*mut std::ffi::c_void, *mut i8, u32) -> i32;
type NvmlDeviceGetTemp = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u32) -> i32;
type NvmlDeviceGetTempThresh = unsafe extern "C" fn(*mut std::ffi::c_void, i32, *mut u32) -> i32;
type NvmlDeviceGetU32 = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32) -> i32;
type NvmlDeviceGetU32Pair = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32, *mut u32) -> i32;
type NvmlDeviceSetU32 = unsafe extern "C" fn(*mut std::ffi::c_void, u32) -> i32;
type NvmlDeviceGetU64 = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u64) -> i32;
type NvmlDeviceGetClock = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u32) -> i32;
type NvmlDeviceGetFanV2 = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u32) -> i32;
type NvmlDeviceGetFanRpm = unsafe extern "C" fn(*mut std::ffi::c_void, *mut [u32; 3]) -> i32;
type NvmlDeviceGetUtil = unsafe extern "C" fn(*mut std::ffi::c_void, *mut NvmlUtilization) -> i32;
type NvmlDeviceGetMem = unsafe extern "C" fn(*mut std::ffi::c_void, *mut NvmlMemory) -> i32;
type NvmlDeviceGetPci = unsafe extern "C" fn(*mut std::ffi::c_void, *mut NvmlPciInfo) -> i32;

type NvapiQueryInterface = unsafe extern "C" fn(u32) -> *const std::ffi::c_void;
type NvapiInitFn = unsafe extern "C" fn() -> i32;
type NvapiEnumGpusFn = unsafe extern "C" fn(*mut *mut std::ffi::c_void, *mut u32) -> i32;
type NvapiPciIdsFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32, *mut u32, *mut u32, *mut u32) -> i32;
type NvapiBusIdFn = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32) -> i32;
type NvapiThermalFn = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u8) -> i32;
type NvapiI2cReadExFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut NvI2cInfoV3, *mut u32) -> i32;

#[repr(C)]
struct NvmlUtilization {
    gpu: u32,
    memory: u32,
}

#[repr(C)]
struct NvmlMemory {
    total: u64,
    free: u64,
    used: u64,
}

#[repr(C)]
struct NvmlPciInfo {
    bus_id_legacy: [u8; 16],
    domain: u32,
    bus: u32,
    device: u32,
    pci_device_id: u32,
    pci_subsystem_id: u32,
    bus_id: [u8; 32],
}

#[repr(C)]
struct NvI2cInfoV3 {
    version: u32,
    display_mask: u32,
    is_ddc_port: u8,
    i2c_dev_address: u8,
    _pad0: [u8; 6],
    i2c_reg_address: *mut u8,
    reg_addr_size: u32,
    _pad1: u32,
    data: *mut u8,
    size: u32,
    i2c_speed: u32,
    i2c_speed_khz: u32,
    port_id: u8,
    _pad2: [u8; 3],
    is_port_id_set: u32,
}

struct NvapiAddon {
    _lib: DynLib,
    gpu_handle: *mut std::ffi::c_void,
    thermal_fn: Option<NvapiThermalFn>,
    i2c_read_fn: Option<NvapiI2cReadExFn>,
    is_astral: bool,
}

pub struct NvidiaBackend {
    _nvml: DynLib,
    shutdown: NvmlShutdown,
    device: *mut std::ffi::c_void,
    name: String,
    pci_bus: Option<u32>,
    get_temp: Option<NvmlDeviceGetTemp>,
    get_temp_thresh: Option<NvmlDeviceGetTempThresh>,
    get_power: Option<NvmlDeviceGetU32>,
    get_power_limit: Option<NvmlDeviceGetU32>,
    get_enforced_limit: Option<NvmlDeviceGetU32>,
    get_power_constraints: Option<NvmlDeviceGetU32Pair>,
    get_util: Option<NvmlDeviceGetUtil>,
    get_mem: Option<NvmlDeviceGetMem>,
    get_clock: Option<NvmlDeviceGetClock>,
    get_num_fans: Option<NvmlDeviceGetU32>,
    get_fan_v2: Option<NvmlDeviceGetFanV2>,
    get_fan_rpm: Option<NvmlDeviceGetFanRpm>,
    get_pstate: Option<NvmlDeviceGetU32>,
    get_pcie_gen: Option<NvmlDeviceGetU32>,
    get_pcie_width: Option<NvmlDeviceGetU32>,
    get_clocks_events: Option<NvmlDeviceGetU64>,
    nvapi: Option<NvapiAddon>,
}

impl NvidiaBackend {
    pub fn open() -> Option<Self> {
        let lib = DynLib::load_system32("nvml.dll")?;
        let init: NvmlInit = unsafe { lib.symbol("nvmlInit_v2") }?;
        let shutdown: NvmlShutdown = unsafe { lib.symbol("nvmlShutdown") }?;
        if unsafe { init() } != NVML_SUCCESS {
            return None;
        }
        let get_handle: NvmlDeviceGetHandleByIndex =
            unsafe { lib.symbol("nvmlDeviceGetHandleByIndex_v2") }?;
        let mut device: *mut std::ffi::c_void = null_mut();
        if unsafe { get_handle(0, &mut device) } != NVML_SUCCESS || device.is_null() {
            unsafe {
                shutdown();
            }
            return None;
        }

        let name = unsafe {
            let mut buf = [0i8; 96];
            if let Some(f) = lib.symbol::<NvmlDeviceGetName>("nvmlDeviceGetName") {
                if f(device, buf.as_mut_ptr(), buf.len() as u32) == NVML_SUCCESS {
                    CStr::from_ptr(buf.as_ptr()).to_string_lossy().into_owned()
                } else {
                    "NVIDIA GPU".to_string()
                }
            } else {
                "NVIDIA GPU".to_string()
            }
        };

        let pci_bus = unsafe {
            lib.symbol::<NvmlDeviceGetPci>("nvmlDeviceGetPciInfo_v3")
                .and_then(|f| {
                    let mut info = std::mem::zeroed::<NvmlPciInfo>();
                    if f(device, &mut info) == NVML_SUCCESS {
                        Some(info.bus)
                    } else {
                        None
                    }
                })
        };

        let get_temp = unsafe { lib.symbol("nvmlDeviceGetTemperature") };
        let get_temp_thresh = unsafe { lib.symbol("nvmlDeviceGetTemperatureThreshold") };
        let get_power = unsafe { lib.symbol("nvmlDeviceGetPowerUsage") };
        let get_power_limit = unsafe { lib.symbol("nvmlDeviceGetPowerManagementLimit") };
        let get_enforced_limit = unsafe { lib.symbol("nvmlDeviceGetEnforcedPowerLimit") };
        let get_power_constraints =
            unsafe { lib.symbol("nvmlDeviceGetPowerManagementLimitConstraints") };
        let get_util = unsafe { lib.symbol("nvmlDeviceGetUtilizationRates") };
        let get_mem = unsafe { lib.symbol("nvmlDeviceGetMemoryInfo") };
        let get_clock = unsafe { lib.symbol("nvmlDeviceGetClockInfo") };
        let get_num_fans = unsafe { lib.symbol("nvmlDeviceGetNumFans") };
        let get_fan_v2 = unsafe { lib.symbol("nvmlDeviceGetFanSpeed_v2") };
        let get_fan_rpm = unsafe { lib.symbol("nvmlDeviceGetFanSpeedRPM") };
        let get_pstate = unsafe { lib.symbol("nvmlDeviceGetPerformanceState") };
        let get_pcie_gen = unsafe { lib.symbol("nvmlDeviceGetCurrPcieLinkGeneration") };
        let get_pcie_width = unsafe { lib.symbol("nvmlDeviceGetCurrPcieLinkWidth") };
        let get_clocks_events = unsafe {
            lib.symbol("nvmlDeviceGetCurrentClocksEventReasons")
                .or_else(|| lib.symbol("nvmlDeviceGetCurrentClocksThrottleReasons"))
        };

        let nvapi = open_nvapi_addon(pci_bus);

        Some(Self {
            _nvml: lib,
            shutdown,
            device,
            name,
            pci_bus,
            get_temp,
            get_temp_thresh,
            get_power,
            get_power_limit,
            get_enforced_limit,
            get_power_constraints,
            get_util,
            get_mem,
            get_clock,
            get_num_fans,
            get_fan_v2,
            get_fan_rpm,
            get_pstate,
            get_pcie_gen,
            get_pcie_width,
            get_clocks_events,
            nvapi,
        })
    }

    fn read_temp_threshold(&self, kind: i32) -> Option<u32> {
        let f = self.get_temp_thresh?;
        let mut val = 0u32;
        if unsafe { f(self.device, kind, &mut val) } == NVML_SUCCESS && val > 0 && val < 150 {
            Some(val)
        } else {
            None
        }
    }

    fn read_nvapi_mem_temp(&self) -> Option<f64> {
        let addon = self.nvapi.as_ref()?;
        let thermal = addon.thermal_fn?;
        let mut buf = [0u8; 68];
        let version: u32 = 68 | (2 << 16);
        buf[0..4].copy_from_slice(&version.to_le_bytes());
        if unsafe { thermal(addon.gpu_handle, 15, buf.as_mut_ptr()) } != 0 {
            return None;
        }
        let count = i32::from_le_bytes(buf[4..8].try_into().ok()?).clamp(0, 3) as usize;
        for i in 0..count {
            let off = 8 + i * 20;
            let temp = i32::from_le_bytes(buf[off + 12..off + 16].try_into().ok()?);
            let target = u32::from_le_bytes(buf[off + 16..off + 20].try_into().ok()?);
            if target == 2 && temp > 0 && temp < 150 {
                return Some(temp as f64);
            }
        }
        None
    }

    fn read_astral_connector(&self) -> Option<ConnectorSnapshot> {
        let addon = self.nvapi.as_ref()?;
        if !addon.is_astral {
            return None;
        }
        let i2c_read = addon.i2c_read_fn?;
        let mut reg: [u8; 1] = [0x80];
        let mut data = [0u8; 24];
        let mut info = NvI2cInfoV3 {
            version: (std::mem::size_of::<NvI2cInfoV3>() as u32) | (3 << 16),
            display_mask: 0,
            is_ddc_port: 0,
            i2c_dev_address: 0x2B << 1,
            _pad0: [0; 6],
            i2c_reg_address: reg.as_mut_ptr(),
            reg_addr_size: 1,
            _pad1: 0,
            data: data.as_mut_ptr(),
            size: 24,
            i2c_speed: 0xFFFF,
            i2c_speed_khz: 4,
            port_id: 1,
            _pad2: [0; 3],
            is_port_id_set: 1,
        };
        let mut bytes_read = 0u32;
        if unsafe { i2c_read(addon.gpu_handle, &mut info, &mut bytes_read) } != 0 {
            return None;
        }
        Some(decode_astral_i2c_frame(&data))
    }
}

impl Drop for NvidiaBackend {
    fn drop(&mut self) {
        unsafe {
            (self.shutdown)();
        }
    }
}

impl TelemetryBackend for NvidiaBackend {
    fn kind(&self) -> BackendKind {
        BackendKind::NvidiaNvmlNvapi
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let core_temp_c = self.get_temp.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, NVML_TEMP_GPU, &mut v) } == NVML_SUCCESS).then_some(v as f64)
        });

        let board_power_w = self.get_power.and_then(|f| {
            let mut mw = 0u32;
            (unsafe { f(self.device, &mut mw) } == NVML_SUCCESS).then_some(mw as f64 / 1000.0)
        });

        let power_limit_w = self.get_power_limit.and_then(|f| {
            let mut mw = 0u32;
            (unsafe { f(self.device, &mut mw) } == NVML_SUCCESS).then_some(mw as f64 / 1000.0)
        });

        let enforced_power_limit_w = self.get_enforced_limit.and_then(|f| {
            let mut mw = 0u32;
            (unsafe { f(self.device, &mut mw) } == NVML_SUCCESS).then_some(mw as f64 / 1000.0)
        });

        let (min_power_limit_w, max_power_limit_w) = self
            .get_power_constraints
            .and_then(|f| {
                let (mut min_mw, mut max_mw) = (0u32, 0u32);
                (unsafe { f(self.device, &mut min_mw, &mut max_mw) } == NVML_SUCCESS)
                    .then_some((Some(min_mw as f64 / 1000.0), Some(max_mw as f64 / 1000.0)))
            })
            .unwrap_or((None, None));

        let power_pct = match (board_power_w, enforced_power_limit_w.or(power_limit_w)) {
            (Some(w), Some(lim)) if lim > 0.0 => Some((w / lim) * 100.0),
            _ => None,
        };

        let gpu_util_pct = self.get_util.and_then(|f| {
            let mut u = NvmlUtilization { gpu: 0, memory: 0 };
            (unsafe { f(self.device, &mut u) } == NVML_SUCCESS).then_some(u.gpu)
        });

        let (vram_used_mb, vram_total_mb) = self
            .get_mem
            .and_then(|f| {
                let mut m = NvmlMemory {
                    total: 0,
                    free: 0,
                    used: 0,
                };
                (unsafe { f(self.device, &mut m) } == NVML_SUCCESS)
                    .then_some((Some(m.used >> 20), Some(m.total >> 20)))
            })
            .unwrap_or((None, None));

        let gpu_clock_mhz = self.get_clock.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, NVML_CLOCK_GRAPHICS, &mut v) } == NVML_SUCCESS).then_some(v)
        });

        let mem_clock_mhz = self.get_clock.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, NVML_CLOCK_MEM, &mut v) } == NVML_SUCCESS).then_some(v)
        });

        let fan_count = self
            .get_num_fans
            .and_then(|f| {
                let mut c = 0u32;
                (unsafe { f(self.device, &mut c) } == NVML_SUCCESS).then_some(c.min(8))
            })
            .unwrap_or(1);

        let fan_pct = self.get_fan_v2.and_then(|f| {
            let mut best: Option<u32> = None;
            for idx in 0..fan_count {
                let mut speed = 0u32;
                if unsafe { f(self.device, idx, &mut speed) } == NVML_SUCCESS {
                    best = Some(best.map_or(speed, |b| b.max(speed)));
                }
            }
            best
        });

        let fan_rpm = self.get_fan_rpm.and_then(|f| {
            let mut best: Option<u32> = None;
            for idx in 0..fan_count {
                let mut info = [12 | (1u32 << 24), idx, 0];
                if unsafe { f(self.device, &mut info) } == NVML_SUCCESS {
                    best = Some(best.map_or(info[2], |b| b.max(info[2])));
                }
            }
            best
        });

        let pstate = self.get_pstate.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, &mut v) } == NVML_SUCCESS && v < 16).then_some(v)
        });

        let pcie_gen = self.get_pcie_gen.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, &mut v) } == NVML_SUCCESS).then_some(v)
        });

        let pcie_width = self.get_pcie_width.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, &mut v) } == NVML_SUCCESS).then_some(v)
        });

        let throttle_flags = self
            .get_clocks_events
            .and_then(|f| {
                let mut bits = 0u64;
                (unsafe { f(self.device, &mut bits) } == NVML_SUCCESS)
                    .then_some(map_nvml_throttle_bits(bits))
            })
            .unwrap_or(0);

        let memory_temp_c = self.read_nvapi_mem_temp();
        let connector = self.read_astral_connector();

        Some(GpuSnapshot {
            timestamp_ms: now_unix_ms(),
            backend: BackendKind::NvidiaNvmlNvapi,
            vendor: GpuVendor::Nvidia,
            name: self.name.clone(),
            pci_bus: self.pci_bus,
            core_temp_c,
            hotspot_temp_c: None,
            memory_temp_c,
            board_power_w,
            power_limit_w,
            enforced_power_limit_w,
            min_power_limit_w,
            max_power_limit_w,
            power_pct,
            gpu_clock_mhz,
            mem_clock_mhz,
            vram_used_mb,
            vram_total_mb,
            gpu_util_pct,
            fan_pct,
            fan_rpm,
            pstate,
            pcie_gen,
            pcie_width,
            temp_target_c: self.read_temp_threshold(NVML_THRESH_TARGET),
            temp_max_c: self.read_temp_threshold(NVML_THRESH_GPU_MAX),
            temp_slowdown_c: self.read_temp_threshold(NVML_THRESH_SLOWDOWN),
            throttle_flags,
            connector,
        })
    }
}

pub fn map_nvml_throttle_bits(bits: u64) -> u32 {
    let mut out = 0u32;
    if bits & NVML_EVENT_SW_POWER_CAP != 0 {
        out |= THROTTLE_SW_POWER_CAP;
    }
    if bits & NVML_EVENT_SW_THERMAL_SLOWDOWN != 0 {
        out |= THROTTLE_SW_THERMAL;
    }
    if bits & NVML_EVENT_HW_THERMAL_SLOWDOWN != 0 {
        out |= THROTTLE_HW_THERMAL;
    }
    if bits & NVML_EVENT_HW_POWER_BRAKE != 0 {
        out |= THROTTLE_HW_POWER_BRAKE;
    }
    out
}

pub fn decode_astral_i2c_frame(raw: &[u8; 24]) -> ConnectorSnapshot {
    let mut words = [0u16; 12];
    for (i, slot) in words.iter_mut().enumerate() {
        let rev = 11 - i;
        *slot = u16::from_be_bytes([raw[rev * 2], raw[rev * 2 + 1]]);
    }
    let mut pins = Vec::with_capacity(6);
    let mut total_current = 0.0;
    let mut total_power = 0.0;
    let mut voltage_sum = 0.0;
    for pin in 0..6 {
        let current_a = words[pin * 2] as f64 / 1000.0;
        let voltage_v = words[pin * 2 + 1] as f64 / 1000.0;
        let power_w = current_a * voltage_v;
        total_current += current_a;
        total_power += power_w;
        voltage_sum += voltage_v;
        pins.push(PinReading {
            pin: (pin + 1) as u8,
            voltage_v: Some(voltage_v),
            current_a,
            power_w: Some(power_w),
        });
    }
    ConnectorSnapshot {
        source: PerPinSource::AsusAstralI2c,
        connector_index: 1,
        pins,
        total_current_a: total_current,
        total_power_w: Some(total_power),
        avg_voltage_v: Some(voltage_sum / 6.0),
        connector_temps_c: Vec::new(),
        rated_cap_w: Some(600),
        hw_fault_bits: 0,
    }
}

fn open_nvapi_addon(target_bus: Option<u32>) -> Option<NvapiAddon> {
    let lib = DynLib::load_system32("nvapi64.dll")?;
    let qi: NvapiQueryInterface = unsafe { lib.symbol("nvapi_QueryInterface") }?;

    let init_ptr = unsafe { qi(NVAPI_INIT) };
    if init_ptr.is_null() {
        return None;
    }
    let init: NvapiInitFn = unsafe { std::mem::transmute(init_ptr) };
    if unsafe { init() } != 0 {
        return None;
    }

    let enum_ptr = unsafe { qi(NVAPI_ENUM_PHYSICAL_GPUS) };
    if enum_ptr.is_null() {
        return None;
    }
    let enum_gpus: NvapiEnumGpusFn = unsafe { std::mem::transmute(enum_ptr) };
    let mut handles = [null_mut(); 64];
    let mut count = 0u32;
    if unsafe { enum_gpus(handles.as_mut_ptr(), &mut count) } != 0 || count == 0 {
        return None;
    }

    let bus_fn: Option<NvapiBusIdFn> = {
        let p = unsafe { qi(NVAPI_GPU_GET_BUS_ID) };
        (!p.is_null()).then(|| unsafe { std::mem::transmute(p) })
    };

    let mut chosen = handles[0];
    if let (Some(want), Some(bf)) = (target_bus, bus_fn) {
        for &h in handles.iter().take((count as usize).min(64)) {
            let mut bus = 0u32;
            if unsafe { bf(h, &mut bus) } == 0 && bus == want {
                chosen = h;
                break;
            }
        }
    }

    let pci_fn: Option<NvapiPciIdsFn> = {
        let p = unsafe { qi(NVAPI_GPU_GET_PCI_IDENTIFIERS) };
        (!p.is_null()).then(|| unsafe { std::mem::transmute(p) })
    };
    let is_astral = pci_fn
        .and_then(|f| {
            let (mut dev, mut subsys, mut rev, mut ext) = (0u32, 0u32, 0u32, 0u32);
            (unsafe { f(chosen, &mut dev, &mut subsys, &mut rev, &mut ext) } == 0)
                .then_some(ASTRAL_SUBSYSTEM_IDS.contains(&subsys))
        })
        .unwrap_or(false);

    let thermal_fn = {
        let p = unsafe { qi(NVAPI_GPU_GET_THERMAL_SETTINGS) };
        (!p.is_null()).then(|| unsafe { std::mem::transmute(p) })
    };
    let i2c_read_fn = {
        let p = unsafe { qi(NVAPI_I2C_READ_EX) };
        (!p.is_null()).then(|| unsafe { std::mem::transmute(p) })
    };

    Some(NvapiAddon {
        _lib: lib,
        gpu_handle: chosen,
        thermal_fn,
        i2c_read_fn,
        is_astral,
    })
}

pub fn try_set_nvidia_power_limit_w(target_w: f64) -> Result<f64, String> {
    if !target_w.is_finite() || target_w <= 0.0 {
        return Err("Invalid target power limit".into());
    }
    let lib = DynLib::load_system32("nvml.dll").ok_or("nvml.dll unavailable")?;
    let init: NvmlInit = unsafe { lib.symbol("nvmlInit_v2") }.ok_or("nvmlInit_v2 missing")?;
    let shutdown: NvmlShutdown =
        unsafe { lib.symbol("nvmlShutdown") }.ok_or("nvmlShutdown missing")?;
    if unsafe { init() } != NVML_SUCCESS {
        return Err("nvmlInit_v2 failed".into());
    }
    let result = (|| {
        let get_handle: NvmlDeviceGetHandleByIndex =
            unsafe { lib.symbol("nvmlDeviceGetHandleByIndex_v2") }
                .ok_or("nvmlDeviceGetHandleByIndex_v2 missing")?;
        let get_constraints: NvmlDeviceGetU32Pair =
            unsafe { lib.symbol("nvmlDeviceGetPowerManagementLimitConstraints") }
                .ok_or("nvmlDeviceGetPowerManagementLimitConstraints missing")?;
        let set_limit: NvmlDeviceSetU32 =
            unsafe { lib.symbol("nvmlDeviceSetPowerManagementLimit") }
                .ok_or("nvmlDeviceSetPowerManagementLimit missing")?;

        let mut dev: *mut std::ffi::c_void = null_mut();
        if unsafe { get_handle(0, &mut dev) } != NVML_SUCCESS || dev.is_null() {
            return Err("No NVIDIA GPU found".into());
        }
        let (mut min_mw, mut max_mw) = (0u32, 0u32);
        if unsafe { get_constraints(dev, &mut min_mw, &mut max_mw) } != NVML_SUCCESS
            || min_mw == 0
            || max_mw < min_mw
        {
            return Err("Card does not expose power limit constraints".into());
        }
        let clamped_mw = ((target_w * 1000.0).round() as u32).clamp(min_mw, max_mw);
        let rc = unsafe { set_limit(dev, clamped_mw) };
        if rc != NVML_SUCCESS {
            return Err(format!("nvmlDeviceSetPowerManagementLimit returned {rc}"));
        }
        Ok(clamped_mw as f64 / 1000.0)
    })();
    unsafe {
        shutdown();
    }
    result
}
