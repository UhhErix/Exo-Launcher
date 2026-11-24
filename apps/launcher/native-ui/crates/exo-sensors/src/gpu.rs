use std::ffi::CStr;
use std::ptr::null_mut;
use std::time::Instant;

use crate::win32::{DynLib, now_unix_ms};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GpuVendor {
    Nvidia,
    Amd,
    Intel,
    Other,
}

impl GpuVendor {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Nvidia => "nvidia",
            Self::Amd => "amd",
            Self::Intel => "intel",
            Self::Other => "other",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GpuBackendKind {
    NvidiaNvmlNvapi,
    AmdAdlx,
    IntelIgcl,
    WindowsD3dkmt,
}

impl GpuBackendKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::NvidiaNvmlNvapi => "nvidia-nvml-nvapi",
            Self::AmdAdlx => "amd-adlx",
            Self::IntelIgcl => "intel-igcl",
            Self::WindowsD3dkmt => "windows-d3dkmt",
        }
    }
}

pub const THROTTLE_SW_POWER_CAP: u32 = 1 << 0;
pub const THROTTLE_SW_THERMAL: u32 = 1 << 1;
pub const THROTTLE_HW_THERMAL: u32 = 1 << 2;
pub const THROTTLE_HW_POWER_BRAKE: u32 = 1 << 3;
pub const THROTTLE_CURRENT_LIMIT: u32 = 1 << 4;

#[derive(Debug, Clone, PartialEq)]
pub struct PinReading {
    pub pin: u8,
    pub voltage_v: Option<f64>,
    pub current_a: f64,
    pub power_w: Option<f64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ConnectorSnapshot {
    pub source: &'static str,
    pub pins: Vec<PinReading>,
    pub total_current_a: f64,
    pub total_power_w: Option<f64>,
    pub avg_voltage_v: Option<f64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct GpuSnapshot {
    pub timestamp_ms: u64,
    pub backend: GpuBackendKind,
    pub vendor: GpuVendor,
    pub name: String,
    pub driver_version: Option<String>,
    pub core_temp_c: Option<f64>,
    pub hotspot_temp_c: Option<f64>,
    pub memory_temp_c: Option<f64>,
    pub board_power_w: Option<f64>,
    pub power_limit_w: Option<f64>,
    pub enforced_power_limit_w: Option<f64>,
    pub min_power_limit_w: Option<f64>,
    pub max_power_limit_w: Option<f64>,
    pub power_pct: Option<f64>,
    pub gpu_clock_mhz: Option<u32>,
    pub mem_clock_mhz: Option<u32>,
    pub vram_used_mb: Option<u64>,
    pub vram_total_mb: Option<u64>,
    pub gpu_util_pct: Option<u32>,
    pub fan_pct: Option<u32>,
    pub fan_rpm: Option<u32>,
    pub pstate: Option<u32>,
    pub pcie_gen_current: Option<u32>,
    pub pcie_gen_max: Option<u32>,
    pub pcie_width_current: Option<u32>,
    pub pcie_width_max: Option<u32>,
    pub temp_target_c: Option<u32>,
    pub temp_max_c: Option<u32>,
    pub temp_slowdown_c: Option<u32>,
    pub throttle_flags: u32,
    pub connector: Option<ConnectorSnapshot>,
}

pub trait GpuTelemetryBackend {
    fn kind(&self) -> GpuBackendKind;
    fn read(&mut self) -> Option<GpuSnapshot>;
}

pub fn discover_primary_gpu() -> Option<Box<dyn GpuTelemetryBackend>> {
    if let Some(nv) = NvidiaSensorBackend::open() {
        return Some(Box::new(nv));
    }
    if let Some(amd) = AmdAdlxSensorBackend::open() {
        return Some(Box::new(amd));
    }
    if let Some(intel) = IntelIgclSensorBackend::open() {
        return Some(Box::new(intel));
    }
    if let Some(d3d) = D3dkmtSensorBackend::open() {
        return Some(Box::new(d3d));
    }
    None
}

const NVML_SUCCESS: i32 = 0;
const NVML_TEMP_GPU: u32 = 0;
const NVML_CLOCK_GRAPHICS: u32 = 0;
const NVML_CLOCK_MEM: u32 = 2;
const NVML_THRESH_SLOWDOWN: i32 = 1;
const NVML_THRESH_GPU_MAX: i32 = 3;
const NVML_THRESH_TARGET: i32 = 5;

const NVAPI_INIT: u32 = 0x0150_E828;
const NVAPI_ENUM_PHYSICAL_GPUS: u32 = 0xE5AC_921F;
const NVAPI_GPU_GET_PCI_IDENTIFIERS: u32 = 0x2DDF_B66E;
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
type NvmlGetDriver = unsafe extern "C" fn(*mut i8, u32) -> i32;
type NvmlGetHandle = unsafe extern "C" fn(u32, *mut *mut std::ffi::c_void) -> i32;
type NvmlGetName = unsafe extern "C" fn(*mut std::ffi::c_void, *mut i8, u32) -> i32;
type NvmlGetTemp = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u32) -> i32;
type NvmlGetTempThresh = unsafe extern "C" fn(*mut std::ffi::c_void, i32, *mut u32) -> i32;
type NvmlGetU32 = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32) -> i32;
type NvmlGetU32Pair = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32, *mut u32) -> i32;
type NvmlGetU64 = unsafe extern "C" fn(*mut std::ffi::c_void, *mut u64) -> i32;
type NvmlGetClock = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u32) -> i32;
type NvmlGetFanV2 = unsafe extern "C" fn(*mut std::ffi::c_void, u32, *mut u32) -> i32;
type NvmlGetFanRpm = unsafe extern "C" fn(*mut std::ffi::c_void, *mut [u32; 3]) -> i32;
type NvmlGetUtil = unsafe extern "C" fn(*mut std::ffi::c_void, *mut NvmlUtilization) -> i32;
type NvmlGetMem = unsafe extern "C" fn(*mut std::ffi::c_void, *mut NvmlMemory) -> i32;

type NvapiQueryInterface = unsafe extern "C" fn(u32) -> *const std::ffi::c_void;
type NvapiInitFn = unsafe extern "C" fn() -> i32;
type NvapiEnumGpusFn = unsafe extern "C" fn(*mut *mut std::ffi::c_void, *mut u32) -> i32;
type NvapiPciIdsFn =
    unsafe extern "C" fn(*mut std::ffi::c_void, *mut u32, *mut u32, *mut u32, *mut u32) -> i32;
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

pub struct NvidiaSensorBackend {
    _nvml: DynLib,
    shutdown: NvmlShutdown,
    device: *mut std::ffi::c_void,
    name: String,
    driver_version: Option<String>,
    get_temp: Option<NvmlGetTemp>,
    get_temp_thresh: Option<NvmlGetTempThresh>,
    get_power: Option<NvmlGetU32>,
    get_power_limit: Option<NvmlGetU32>,
    get_enforced_limit: Option<NvmlGetU32>,
    get_power_constraints: Option<NvmlGetU32Pair>,
    get_util: Option<NvmlGetUtil>,
    get_mem: Option<NvmlGetMem>,
    get_clock: Option<NvmlGetClock>,
    get_num_fans: Option<NvmlGetU32>,
    get_fan_v2: Option<NvmlGetFanV2>,
    get_fan_rpm: Option<NvmlGetFanRpm>,
    get_pstate: Option<NvmlGetU32>,
    get_pcie_gen_curr: Option<NvmlGetU32>,
    get_pcie_gen_max: Option<NvmlGetU32>,
    get_pcie_width_curr: Option<NvmlGetU32>,
    get_pcie_width_max: Option<NvmlGetU32>,
    get_clocks_events: Option<NvmlGetU64>,
    nvapi: Option<NvapiAddon>,
}

impl NvidiaSensorBackend {
    pub fn open() -> Option<Self> {
        let lib = DynLib::load_system32("nvml.dll")?;
        let init: NvmlInit = unsafe { lib.symbol("nvmlInit_v2") }?;
        let shutdown: NvmlShutdown = unsafe { lib.symbol("nvmlShutdown") }?;
        if unsafe { init() } != NVML_SUCCESS {
            return None;
        }
        let get_handle: NvmlGetHandle = unsafe { lib.symbol("nvmlDeviceGetHandleByIndex_v2") }?;
        let mut device: *mut std::ffi::c_void = null_mut();
        if unsafe { get_handle(0, &mut device) } != NVML_SUCCESS || device.is_null() {
            unsafe {
                shutdown();
            }
            return None;
        }

        let name = unsafe {
            let mut buf = [0i8; 96];
            lib.symbol::<NvmlGetName>("nvmlDeviceGetName")
                .and_then(|f| {
                    (f(device, buf.as_mut_ptr(), buf.len() as u32) == NVML_SUCCESS)
                        .then(|| CStr::from_ptr(buf.as_ptr()).to_string_lossy().into_owned())
                })
                .unwrap_or_else(|| "NVIDIA GPU".into())
        };

        let driver_version = unsafe {
            let mut buf = [0i8; 80];
            lib.symbol::<NvmlGetDriver>("nvmlSystemGetDriverVersion")
                .and_then(|f| {
                    (f(buf.as_mut_ptr(), buf.len() as u32) == NVML_SUCCESS)
                        .then(|| CStr::from_ptr(buf.as_ptr()).to_string_lossy().into_owned())
                })
        };

        Some(Self {
            get_temp: unsafe { lib.symbol("nvmlDeviceGetTemperature") },
            get_temp_thresh: unsafe { lib.symbol("nvmlDeviceGetTemperatureThreshold") },
            get_power: unsafe { lib.symbol("nvmlDeviceGetPowerUsage") },
            get_power_limit: unsafe { lib.symbol("nvmlDeviceGetPowerManagementLimit") },
            get_enforced_limit: unsafe { lib.symbol("nvmlDeviceGetEnforcedPowerLimit") },
            get_power_constraints: unsafe {
                lib.symbol("nvmlDeviceGetPowerManagementLimitConstraints")
            },
            get_util: unsafe { lib.symbol("nvmlDeviceGetUtilizationRates") },
            get_mem: unsafe { lib.symbol("nvmlDeviceGetMemoryInfo") },
            get_clock: unsafe { lib.symbol("nvmlDeviceGetClockInfo") },
            get_num_fans: unsafe { lib.symbol("nvmlDeviceGetNumFans") },
            get_fan_v2: unsafe { lib.symbol("nvmlDeviceGetFanSpeed_v2") },
            get_fan_rpm: unsafe { lib.symbol("nvmlDeviceGetFanSpeedRPM") },
            get_pstate: unsafe { lib.symbol("nvmlDeviceGetPerformanceState") },
            get_pcie_gen_curr: unsafe { lib.symbol("nvmlDeviceGetCurrPcieLinkGeneration") },
            get_pcie_gen_max: unsafe { lib.symbol("nvmlDeviceGetMaxPcieLinkGeneration") },
            get_pcie_width_curr: unsafe { lib.symbol("nvmlDeviceGetCurrPcieLinkWidth") },
            get_pcie_width_max: unsafe { lib.symbol("nvmlDeviceGetMaxPcieLinkWidth") },
            get_clocks_events: unsafe {
                lib.symbol("nvmlDeviceGetCurrentClocksEventReasons")
                    .or_else(|| lib.symbol("nvmlDeviceGetCurrentClocksThrottleReasons"))
            },
            nvapi: open_nvapi_addon(),
            _nvml: lib,
            shutdown,
            device,
            name,
            driver_version,
        })
    }

    fn read_temp_threshold(&self, kind: i32) -> Option<u32> {
        let f = self.get_temp_thresh?;
        let mut val = 0u32;
        (unsafe { f(self.device, kind, &mut val) } == NVML_SUCCESS && val > 0 && val < 150)
            .then_some(val)
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

impl Drop for NvidiaSensorBackend {
    fn drop(&mut self) {
        unsafe {
            (self.shutdown)();
        }
    }
}

impl GpuTelemetryBackend for NvidiaSensorBackend {
    fn kind(&self) -> GpuBackendKind {
        GpuBackendKind::NvidiaNvmlNvapi
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let u32_fn = |opt: Option<NvmlGetU32>| -> Option<u32> {
            let f = opt?;
            let mut v = 0u32;
            (unsafe { f(self.device, &mut v) } == NVML_SUCCESS).then_some(v)
        };

        let core_temp_c = self.get_temp.and_then(|f| {
            let mut v = 0u32;
            (unsafe { f(self.device, NVML_TEMP_GPU, &mut v) } == NVML_SUCCESS).then_some(v as f64)
        });
        let board_power_w = u32_fn(self.get_power).map(|mw| mw as f64 / 1000.0);
        let power_limit_w = u32_fn(self.get_power_limit).map(|mw| mw as f64 / 1000.0);
        let enforced_power_limit_w = u32_fn(self.get_enforced_limit).map(|mw| mw as f64 / 1000.0);
        let (min_power_limit_w, max_power_limit_w) = self
            .get_power_constraints
            .and_then(|f| {
                let (mut mn, mut mx) = (0u32, 0u32);
                (unsafe { f(self.device, &mut mn, &mut mx) } == NVML_SUCCESS)
                    .then_some((Some(mn as f64 / 1000.0), Some(mx as f64 / 1000.0)))
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

        let fan_count = u32_fn(self.get_num_fans).map(|c| c.min(8)).unwrap_or(1);
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
        let pstate = u32_fn(self.get_pstate).filter(|&v| v < 16);
        let throttle_flags = self
            .get_clocks_events
            .and_then(|f| {
                let mut bits = 0u64;
                (unsafe { f(self.device, &mut bits) } == NVML_SUCCESS)
                    .then_some(map_nvml_bits(bits))
            })
            .unwrap_or(0);

        Some(GpuSnapshot {
            timestamp_ms: now_unix_ms(),
            backend: GpuBackendKind::NvidiaNvmlNvapi,
            vendor: GpuVendor::Nvidia,
            name: self.name.clone(),
            driver_version: self.driver_version.clone(),
            core_temp_c,
            hotspot_temp_c: None,
            memory_temp_c: self.read_nvapi_mem_temp(),
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
            pcie_gen_current: u32_fn(self.get_pcie_gen_curr),
            pcie_gen_max: u32_fn(self.get_pcie_gen_max),
            pcie_width_current: u32_fn(self.get_pcie_width_curr),
            pcie_width_max: u32_fn(self.get_pcie_width_max),
            temp_target_c: self.read_temp_threshold(NVML_THRESH_TARGET),
            temp_max_c: self.read_temp_threshold(NVML_THRESH_GPU_MAX),
            temp_slowdown_c: self.read_temp_threshold(NVML_THRESH_SLOWDOWN),
            throttle_flags,
            connector: self.read_astral_connector(),
        })
    }
}

pub fn map_nvml_bits(bits: u64) -> u32 {
    let mut out = 0u32;
    if bits & 0x04 != 0 {
        out |= THROTTLE_SW_POWER_CAP;
    }
    if bits & 0x20 != 0 {
        out |= THROTTLE_SW_THERMAL;
    }
    if bits & 0x40 != 0 {
        out |= THROTTLE_HW_THERMAL;
    }
    if bits & 0x80 != 0 {
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
    let mut v_sum = 0.0;
    for pin in 0..6 {
        let current_a = words[pin * 2] as f64 / 1000.0;
        let voltage_v = words[pin * 2 + 1] as f64 / 1000.0;
        let power_w = current_a * voltage_v;
        total_current += current_a;
        total_power += power_w;
        v_sum += voltage_v;
        pins.push(PinReading {
            pin: (pin + 1) as u8,
            voltage_v: Some(voltage_v),
            current_a,
            power_w: Some(power_w),
        });
    }
    ConnectorSnapshot {
        source: "asus-astral-i2c",
        pins,
        total_current_a: total_current,
        total_power_w: Some(total_power),
        avg_voltage_v: Some(v_sum / 6.0),
    }
}

fn open_nvapi_addon() -> Option<NvapiAddon> {
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
    let gpu = handles[0];
    let pci_fn: Option<NvapiPciIdsFn> = {
        let p = unsafe { qi(NVAPI_GPU_GET_PCI_IDENTIFIERS) };
        (!p.is_null()).then(|| unsafe { std::mem::transmute(p) })
    };
    let is_astral = pci_fn
        .and_then(|f| {
            let (mut d, mut s, mut r, mut e) = (0, 0, 0, 0);
            (unsafe { f(gpu, &mut d, &mut s, &mut r, &mut e) } == 0)
                .then_some(ASTRAL_SUBSYSTEM_IDS.contains(&s))
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
        gpu_handle: gpu,
        thermal_fn,
        i2c_read_fn,
        is_astral,
    })
}

pub struct AmdAdlxSensorBackend {
    _lib: DynLib,
    terminate: Option<unsafe extern "C" fn() -> i32>,
    system: *mut std::ffi::c_void,
    gpu: *mut std::ffi::c_void,
    name: String,
    vram_total_mb: Option<u64>,
}

impl AmdAdlxSensorBackend {
    pub fn open() -> Option<Self> {
        let lib = DynLib::load_system32("amdadlx64.dll")?;
        let query_ver: unsafe extern "C" fn(*mut u64) -> i32 =
            unsafe { lib.symbol("ADLXQueryFullVersion") }?;
        let init: unsafe extern "C" fn(u64, *mut *mut std::ffi::c_void) -> i32 =
            unsafe { lib.symbol("ADLXInitialize") }?;
        let terminate: Option<unsafe extern "C" fn() -> i32> =
            unsafe { lib.symbol("ADLXTerminate") };
        let mut ver = 0u64;
        if unsafe { query_ver(&mut ver) } != 0 {
            return None;
        }
        let mut system: *mut std::ffi::c_void = null_mut();
        if unsafe { init(ver, &mut system) } != 0 || system.is_null() {
            return None;
        }
        let mut list: *mut std::ffi::c_void = null_mut();
        if unsafe { adlx_call_out(system, 1, &mut list) } != 0 || list.is_null() {
            if let Some(t) = terminate {
                unsafe {
                    t();
                }
            }
            return None;
        }
        let size: u32 = unsafe {
            let f: unsafe extern "system" fn(*mut std::ffi::c_void) -> u32 = adlx_vt(list, 3);
            f(list)
        };
        let mut chosen: *mut std::ffi::c_void = null_mut();
        for idx in 0..size {
            let mut item: *mut std::ffi::c_void = null_mut();
            let rc = unsafe {
                let f: unsafe extern "system" fn(
                    *mut std::ffi::c_void,
                    u32,
                    *mut *mut std::ffi::c_void,
                ) -> i32 = adlx_vt(list, 11);
                f(list, idx, &mut item)
            };
            if rc != 0 || item.is_null() {
                continue;
            }
            let mut gtype = 0i32;
            let discrete = unsafe {
                let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut i32) -> i32 =
                    adlx_vt(item, 5);
                f(item, &mut gtype) == 0 && gtype == 2
            };
            if chosen.is_null() || discrete {
                if !chosen.is_null() {
                    unsafe { adlx_release(chosen) };
                }
                chosen = item;
                if discrete {
                    break;
                }
            } else {
                unsafe { adlx_release(item) };
            }
        }
        unsafe { adlx_release(list) };
        if chosen.is_null() {
            if let Some(t) = terminate {
                unsafe {
                    t();
                }
            }
            return None;
        }
        let name = unsafe {
            let mut p: *const i8 = std::ptr::null();
            let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut *const i8) -> i32 =
                adlx_vt(chosen, 7);
            if f(chosen, &mut p) == 0 && !p.is_null() {
                CStr::from_ptr(p).to_string_lossy().trim().to_string()
            } else {
                "AMD Radeon GPU".into()
            }
        };
        let vram_total_mb = unsafe {
            let mut total = 0u32;
            let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut u32) -> i32 =
                adlx_vt(chosen, 11);
            (f(chosen, &mut total) == 0 && total > 0).then_some(total as u64)
        };
        Some(Self {
            _lib: lib,
            terminate,
            system,
            gpu: chosen,
            name,
            vram_total_mb,
        })
    }
}

impl Drop for AmdAdlxSensorBackend {
    fn drop(&mut self) {
        unsafe {
            if !self.gpu.is_null() {
                adlx_release(self.gpu);
            }
            if let Some(t) = self.terminate {
                t();
            }
        }
    }
}

impl GpuTelemetryBackend for AmdAdlxSensorBackend {
    fn kind(&self) -> GpuBackendKind {
        GpuBackendKind::AmdAdlx
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let mut perf: *mut std::ffi::c_void = null_mut();
        let mut sup: *mut std::ffi::c_void = null_mut();
        let mut cur: *mut std::ffi::c_void = null_mut();
        let res = unsafe {
            if adlx_call_out(self.system, 9, &mut perf) != 0 || perf.is_null() {
                None
            } else {
                let f_sup: unsafe extern "system" fn(
                    *mut std::ffi::c_void,
                    *mut std::ffi::c_void,
                    *mut *mut std::ffi::c_void,
                ) -> i32 = adlx_vt(perf, 21);
                let f_cur: unsafe extern "system" fn(
                    *mut std::ffi::c_void,
                    *mut std::ffi::c_void,
                    *mut *mut std::ffi::c_void,
                ) -> i32 = adlx_vt(perf, 18);
                if f_sup(perf, self.gpu, &mut sup) != 0
                    || sup.is_null()
                    || f_cur(perf, self.gpu, &mut cur) != 0
                    || cur.is_null()
                {
                    None
                } else {
                    let usage = adlx_real(sup, cur, 3, 4);
                    let gclk = adlx_int(sup, cur, 4, 5).map(|v| v.max(0) as u32);
                    let mclk = adlx_int(sup, cur, 5, 6).map(|v| v.max(0) as u32);
                    let temp = adlx_real(sup, cur, 6, 7);
                    let hotspot = adlx_real(sup, cur, 7, 8);
                    let watts = adlx_real(sup, cur, 9, 10).or_else(|| adlx_real(sup, cur, 8, 9));
                    let fan = adlx_int(sup, cur, 10, 11).map(|v| v.max(0) as u32);
                    let vram_used = adlx_int(sup, cur, 11, 12).map(|v| v.max(0) as u64);
                    Some(GpuSnapshot {
                        timestamp_ms: now_unix_ms(),
                        backend: GpuBackendKind::AmdAdlx,
                        vendor: GpuVendor::Amd,
                        name: self.name.clone(),
                        driver_version: None,
                        core_temp_c: temp,
                        hotspot_temp_c: hotspot,
                        memory_temp_c: None,
                        board_power_w: watts,
                        power_limit_w: None,
                        enforced_power_limit_w: None,
                        min_power_limit_w: None,
                        max_power_limit_w: None,
                        power_pct: None,
                        gpu_clock_mhz: gclk,
                        mem_clock_mhz: mclk,
                        vram_used_mb: vram_used,
                        vram_total_mb: self.vram_total_mb,
                        gpu_util_pct: usage.map(|u| u.round().clamp(0.0, 100.0) as u32),
                        fan_pct: None,
                        fan_rpm: fan,
                        pstate: None,
                        pcie_gen_current: None,
                        pcie_gen_max: None,
                        pcie_width_current: None,
                        pcie_width_max: None,
                        temp_target_c: hotspot.map(|_| 100),
                        temp_max_c: hotspot.map(|_| 110),
                        temp_slowdown_c: hotspot.map(|_| 110),
                        throttle_flags: 0,
                        connector: None,
                    })
                }
            }
        };
        unsafe {
            if !cur.is_null() {
                adlx_release(cur);
            }
            if !sup.is_null() {
                adlx_release(sup);
            }
            if !perf.is_null() {
                adlx_release(perf);
            }
        }
        res
    }
}

unsafe fn adlx_vt<T: Copy>(obj: *mut std::ffi::c_void, slot: usize) -> T {
    let vt = unsafe { *(obj as *const *const *const std::ffi::c_void) };
    let p = unsafe { *vt.add(slot) };
    unsafe { std::mem::transmute_copy(&p) }
}

unsafe fn adlx_call_out(
    obj: *mut std::ffi::c_void,
    slot: usize,
    out: *mut *mut std::ffi::c_void,
) -> i32 {
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut *mut std::ffi::c_void) -> i32 =
        unsafe { adlx_vt(obj, slot) };
    unsafe { f(obj, out) }
}

unsafe fn adlx_release(obj: *mut std::ffi::c_void) {
    let f: unsafe extern "system" fn(*mut std::ffi::c_void) -> i64 = unsafe { adlx_vt(obj, 1) };
    unsafe {
        f(obj);
    }
}

unsafe fn adlx_supported(sup: *mut std::ffi::c_void, slot: usize) -> bool {
    let mut on = 0u8;
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut u8) -> i32 =
        unsafe { adlx_vt(sup, slot) };
    unsafe { f(sup, &mut on) == 0 && on != 0 }
}

unsafe fn adlx_real(
    sup: *mut std::ffi::c_void,
    cur: *mut std::ffi::c_void,
    ss: usize,
    vs: usize,
) -> Option<f64> {
    if !unsafe { adlx_supported(sup, ss) } {
        return None;
    }
    let mut v = 0.0_f64;
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut f64) -> i32 =
        unsafe { adlx_vt(cur, vs) };
    (unsafe { f(cur, &mut v) } == 0 && v.is_finite()).then_some(v)
}

unsafe fn adlx_int(
    sup: *mut std::ffi::c_void,
    cur: *mut std::ffi::c_void,
    ss: usize,
    vs: usize,
) -> Option<i32> {
    if !unsafe { adlx_supported(sup, ss) } {
        return None;
    }
    let mut v = 0i32;
    let f: unsafe extern "system" fn(*mut std::ffi::c_void, *mut i32) -> i32 =
        unsafe { adlx_vt(cur, vs) };
    (unsafe { f(cur, &mut v) } == 0).then_some(v)
}

pub struct IntelIgclSensorBackend {
    _lib: DynLib,
    close: unsafe extern "C" fn(*mut std::ffi::c_void) -> i32,
    api: *mut std::ffi::c_void,
    device: *mut std::ffi::c_void,
    name: String,
    power_fn: unsafe extern "C" fn(*mut std::ffi::c_void, *mut CtlPowerTelemetry) -> i32,
    prev_energy_j: Option<f64>,
    prev_ts_s: Option<f64>,
}

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
#[derive(Clone, Copy)]
struct CtlOcTelemetryItem {
    supported: u8,
    units: i32,
    data_type: i32,
    value: u64,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct CtlPsuInfo {
    supported: u8,
    psu_type: i32,
    energy_counter: CtlOcTelemetryItem,
    voltage: CtlOcTelemetryItem,
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

impl IntelIgclSensorBackend {
    pub fn open() -> Option<Self> {
        let lib = DynLib::load_system32("ControlLib.dll")?;
        let init: unsafe extern "C" fn(*mut CtlInitArgs, *mut *mut std::ffi::c_void) -> i32 =
            unsafe { lib.symbol("ctlInit") }?;
        let close: unsafe extern "C" fn(*mut std::ffi::c_void) -> i32 =
            unsafe { lib.symbol("ctlClose") }?;
        let enum_dev: unsafe extern "C" fn(
            *mut std::ffi::c_void,
            *mut u32,
            *mut *mut std::ffi::c_void,
        ) -> i32 = unsafe { lib.symbol("ctlEnumerateDevices") }?;
        let power_fn: unsafe extern "C" fn(*mut std::ffi::c_void, *mut CtlPowerTelemetry) -> i32 =
            unsafe { lib.symbol("ctlPowerTelemetryGet") }?;
        let mut args = CtlInitArgs {
            size: std::mem::size_of::<CtlInitArgs>() as u32,
            version: 0,
            _pad: [0; 3],
            app_version: (1 << 16) | 1,
            flags: 0,
            supported_version: 0,
            app_uid: [0; 16],
        };
        let mut api: *mut std::ffi::c_void = null_mut();
        if unsafe { init(&mut args, &mut api) } != 0 || api.is_null() {
            return None;
        }
        let mut count = 0u32;
        if unsafe { enum_dev(api, &mut count, null_mut()) } != 0 || count == 0 {
            unsafe {
                close(api);
            }
            return None;
        }
        let mut devices = vec![null_mut(); (count as usize).min(8)];
        let mut cap = devices.len() as u32;
        if unsafe { enum_dev(api, &mut cap, devices.as_mut_ptr()) } != 0 || cap == 0 {
            unsafe {
                close(api);
            }
            return None;
        }
        Some(Self {
            _lib: lib,
            close,
            api,
            device: devices[0],
            name: "Intel Graphics".into(),
            power_fn,
            prev_energy_j: None,
            prev_ts_s: None,
        })
    }
}

impl Drop for IntelIgclSensorBackend {
    fn drop(&mut self) {
        if !self.api.is_null() {
            unsafe {
                (self.close)(self.api);
            }
        }
    }
}

impl GpuTelemetryBackend for IntelIgclSensorBackend {
    fn kind(&self) -> GpuBackendKind {
        GpuBackendKind::IntelIgcl
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let mut tel: CtlPowerTelemetry = unsafe { std::mem::zeroed() };
        tel.size = std::mem::size_of::<CtlPowerTelemetry>() as u32;
        tel.version = 1;
        if unsafe { (self.power_fn)(self.device, &mut tel) } != 0 {
            return None;
        }
        let ts = ctl_f64(&tel.time_stamp);
        let ej =
            ctl_f64(&tel.total_card_energy_counter).or_else(|| ctl_f64(&tel.gpu_energy_counter));
        let watts = match (ej, ts, self.prev_energy_j, self.prev_ts_s) {
            (Some(e), Some(t), Some(pe), Some(pt)) if t > pt && e >= pe => {
                Some((e - pe) / (t - pt))
            }
            _ => None,
        };
        if let (Some(e), Some(t)) = (ej, ts) {
            self.prev_energy_j = Some(e);
            self.prev_ts_s = Some(t);
        }
        let mut throttle_flags = 0u32;
        if tel.gpu_power_limited != 0 {
            throttle_flags |= THROTTLE_SW_POWER_CAP;
        }
        if tel.gpu_temp_limited != 0 {
            throttle_flags |= THROTTLE_HW_THERMAL;
        }
        if tel.gpu_current_limited != 0 {
            throttle_flags |= THROTTLE_CURRENT_LIMIT;
        }
        Some(GpuSnapshot {
            timestamp_ms: now_unix_ms(),
            backend: GpuBackendKind::IntelIgcl,
            vendor: GpuVendor::Intel,
            name: self.name.clone(),
            driver_version: None,
            core_temp_c: ctl_f64(&tel.gpu_current_temp),
            hotspot_temp_c: None,
            memory_temp_c: ctl_f64(&tel.vram_current_temp),
            board_power_w: watts,
            power_limit_w: None,
            enforced_power_limit_w: None,
            min_power_limit_w: None,
            max_power_limit_w: None,
            power_pct: None,
            gpu_clock_mhz: ctl_f64(&tel.gpu_current_clock_freq).map(|v| v.max(0.0) as u32),
            mem_clock_mhz: ctl_f64(&tel.vram_current_clock_freq).map(|v| v.max(0.0) as u32),
            vram_used_mb: None,
            vram_total_mb: None,
            gpu_util_pct: None,
            fan_pct: None,
            fan_rpm: tel
                .fan_speed
                .iter()
                .filter_map(ctl_f64)
                .map(|v| v.max(0.0) as u32)
                .max(),
            pstate: None,
            pcie_gen_current: None,
            pcie_gen_max: None,
            pcie_width_current: None,
            pcie_width_max: None,
            temp_target_c: Some(90),
            temp_max_c: Some(100),
            temp_slowdown_c: Some(100),
            throttle_flags,
            connector: None,
        })
    }
}

fn ctl_f64(item: &CtlOcTelemetryItem) -> Option<f64> {
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

const STATS_SIZE: usize = 0x328;
const RESULT_AT: usize = 24;
const INPUT_AT: usize = 0x320;

#[repr(C, packed(4))]
#[derive(Clone, Copy)]
struct AdapterInfo {
    handle: u32,
    luid: i64,
    sources: u32,
    precise_regions: i32,
}

#[repr(C)]
struct EnumAdapters2 {
    count: u32,
    adapters: *mut AdapterInfo,
}

#[repr(C)]
struct QueryAdapterInfo {
    adapter: u32,
    kind: i32,
    data: *mut std::ffi::c_void,
    size: u32,
}

#[repr(C)]
struct AdapterPerfData {
    physical_adapter_index: u32,
    _pad0: u32,
    memory_frequency: u64,
    max_memory_frequency: u64,
    max_memory_frequency_oc: u64,
    memory_bandwidth: u64,
    pcie_bandwidth: u64,
    fan_rpm: u32,
    power_deci_pct: u32,
    temperature_deci_c: u32,
    power_state_override: u8,
    _pad1: [u8; 3],
}

#[link(name = "gdi32")]
unsafe extern "system" {
    fn D3DKMTEnumAdapters2(list: *mut EnumAdapters2) -> i32;
    fn D3DKMTCloseAdapter(handle: *const u32) -> i32;
    fn D3DKMTQueryStatistics(stats: *mut u8) -> i32;
    fn D3DKMTQueryAdapterInfo(query: *mut QueryAdapterInfo) -> i32;
}

pub struct D3dkmtSensorBackend {
    adapter: u32,
    luid: i64,
    segments: u32,
    nodes: u32,
    dedicated_bytes: u64,
    prev_running: Vec<i64>,
    prev_at: Vec<Instant>,
    primed: bool,
}

impl D3dkmtSensorBackend {
    pub fn open() -> Option<Self> {
        let mut infos = [AdapterInfo {
            handle: 0,
            luid: 0,
            sources: 0,
            precise_regions: 0,
        }; 16];
        let mut list = EnumAdapters2 {
            count: infos.len() as u32,
            adapters: infos.as_mut_ptr(),
        };
        if unsafe { D3DKMTEnumAdapters2(&mut list) } != 0 || list.count == 0 {
            return None;
        }
        let mut stats = [0u8; STATS_SIZE];
        let mut best: Option<(u32, i64, u32, u32, u64)> = None;
        for info in infos.iter().take((list.count as usize).min(16)) {
            let mut memory = 0u64;
            let mut segments = 0u32;
            let mut nodes = 0u32;
            if d3d_query(&mut stats, 0, info.luid, 0) {
                segments = u32::from_le_bytes(stats[RESULT_AT..RESULT_AT + 4].try_into().unwrap());
                nodes = u32::from_le_bytes(stats[RESULT_AT + 4..RESULT_AT + 8].try_into().unwrap());
                for seg in 0..segments {
                    if d3d_query(&mut stats, 3, info.luid, seg) && d3d_dedicated(&stats) {
                        memory +=
                            u64::from_le_bytes(stats[RESULT_AT..RESULT_AT + 8].try_into().unwrap());
                    }
                }
            }
            if best.is_none_or(|b| memory > b.4) {
                if let Some(old) = best {
                    unsafe {
                        D3DKMTCloseAdapter(&old.0);
                    }
                }
                best = Some((info.handle, info.luid, segments, nodes, memory));
            } else {
                let h = info.handle;
                unsafe {
                    D3DKMTCloseAdapter(&h);
                }
            }
        }
        let (adapter, luid, segments, nodes, dedicated_bytes) = best?;
        if nodes == 0 {
            unsafe {
                D3DKMTCloseAdapter(&adapter);
            }
            return None;
        }
        let now = Instant::now();
        Some(Self {
            adapter,
            luid,
            segments,
            nodes,
            dedicated_bytes,
            prev_running: vec![0; nodes as usize],
            prev_at: vec![now; nodes as usize],
            primed: false,
        })
    }
}

impl Drop for D3dkmtSensorBackend {
    fn drop(&mut self) {
        unsafe {
            D3DKMTCloseAdapter(&self.adapter);
        }
    }
}

impl GpuTelemetryBackend for D3dkmtSensorBackend {
    fn kind(&self) -> GpuBackendKind {
        GpuBackendKind::WindowsD3dkmt
    }

    fn read(&mut self) -> Option<GpuSnapshot> {
        let mut perf: AdapterPerfData = unsafe { std::mem::zeroed() };
        let mut q = QueryAdapterInfo {
            adapter: self.adapter,
            kind: 62,
            data: (&mut perf as *mut AdapterPerfData).cast(),
            size: std::mem::size_of::<AdapterPerfData>() as u32,
        };
        let has_perf = unsafe { D3DKMTQueryAdapterInfo(&mut q) } == 0;

        let mut stats = [0u8; STATS_SIZE];
        let mut used: Option<u64> = None;
        for seg in 0..self.segments {
            if d3d_query(&mut stats, 3, self.luid, seg) && d3d_dedicated(&stats) {
                let cur =
                    u64::from_le_bytes(stats[RESULT_AT + 16..RESULT_AT + 24].try_into().unwrap());
                used = Some(used.unwrap_or(0) + cur);
            }
        }

        let mut busiest = 0.0_f64;
        let now = Instant::now();
        for node in 0..self.nodes {
            if !d3d_query(&mut stats, 5, self.luid, node) {
                continue;
            }
            let running = i64::from_le_bytes(stats[RESULT_AT..RESULT_AT + 8].try_into().unwrap());
            let idx = node as usize;
            let dt = now.duration_since(self.prev_at[idx]).as_secs_f64();
            if self.primed && dt > 0.0 {
                let busy = ((running - self.prev_running[idx]) as f64) / 1e7 / dt;
                if busy > busiest {
                    busiest = busy;
                }
            }
            self.prev_running[idx] = running;
            self.prev_at[idx] = now;
        }
        let util = if self.primed {
            Some((busiest.clamp(0.0, 1.0) * 100.0).round() as u32)
        } else {
            self.primed = true;
            None
        };

        Some(GpuSnapshot {
            timestamp_ms: now_unix_ms(),
            backend: GpuBackendKind::WindowsD3dkmt,
            vendor: GpuVendor::Other,
            name: "WDDM Primary GPU (D3DKMT)".into(),
            driver_version: None,
            core_temp_c: has_perf
                .then_some(perf.temperature_deci_c as f64 / 10.0)
                .filter(|&c| c > 0.0 && c < 150.0),
            hotspot_temp_c: None,
            memory_temp_c: None,
            board_power_w: None,
            power_limit_w: None,
            enforced_power_limit_w: None,
            min_power_limit_w: None,
            max_power_limit_w: None,
            power_pct: has_perf
                .then_some(perf.power_deci_pct as f64 / 10.0)
                .filter(|&p| p > 0.0 && p <= 200.0),
            gpu_clock_mhz: None,
            mem_clock_mhz: has_perf
                .then_some((perf.memory_frequency / 1_000_000) as u32)
                .filter(|&m| m > 0),
            vram_used_mb: used.map(|b| b >> 20),
            vram_total_mb: (self.dedicated_bytes > 0).then_some(self.dedicated_bytes >> 20),
            gpu_util_pct: util,
            fan_pct: None,
            fan_rpm: has_perf
                .then_some(perf.fan_rpm)
                .filter(|&r| r > 0 && r < 20_000),
            pstate: None,
            pcie_gen_current: None,
            pcie_gen_max: None,
            pcie_width_current: None,
            pcie_width_max: None,
            temp_target_c: None,
            temp_max_c: None,
            temp_slowdown_c: None,
            throttle_flags: 0,
            connector: None,
        })
    }
}

fn d3d_dedicated(stats: &[u8; STATS_SIZE]) -> bool {
    u32::from_le_bytes(stats[RESULT_AT + 40..RESULT_AT + 44].try_into().unwrap()) == 0
}

fn d3d_query(stats: &mut [u8; STATS_SIZE], kind: i32, luid: i64, input: u32) -> bool {
    stats.fill(0);
    stats[0..4].copy_from_slice(&kind.to_le_bytes());
    stats[4..12].copy_from_slice(&luid.to_le_bytes());
    stats[INPUT_AT..INPUT_AT + 4].copy_from_slice(&input.to_le_bytes());
    unsafe { D3DKMTQueryStatistics(stats.as_mut_ptr()) == 0 }
}
