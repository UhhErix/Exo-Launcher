use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::ptr::{null, null_mut};

use crate::types::{ConnectorSnapshot, PerPinSource, PinReading};
use crate::win32::Handle;

pub const WIREVIEW_USB_VID: u16 = 0x0483;
pub const WIREVIEW_USB_PID: u16 = 0x5740;
pub const WIREVIEW_BAUD: u32 = 115_200;
pub const WIREVIEW_FRAME_BYTES: usize = 100;

pub const CMD_READ_VENDOR_DATA: u8 = 0x01;
pub const CMD_READ_UID: u8 = 0x02;
pub const CMD_READ_SENSORS: u8 = 0x04;
pub const CMD_READ_CONFIG: u8 = 0x05;

const OFF_TS: usize = 0;
const OFF_READINGS: usize = 12;
const OFF_TOTAL_POWER: usize = 84;
const OFF_TOTAL_CURRENT: usize = 88;
const OFF_AVG_VOLTAGE: usize = 92;
const OFF_HPWR_CAP: usize = 94;
const OFF_FAULT_STATUS: usize = 96;
const OFF_FAULT_LOG: usize = 98;
const PS_SIZE: usize = 12;

const INVALID_HANDLE_VALUE: Handle = (-1isize) as Handle;
const DIGCF_PRESENT: u32 = 0x0000_0002;
const DIGCF_ALLCLASSES: u32 = 0x0000_0004;
const SPDRP_HARDWAREID: u32 = 0x0000_0001;
const SPDRP_FRIENDLYNAME: u32 = 0x0000_000C;
const GENERIC_READ: u32 = 0x8000_0000;
const GENERIC_WRITE: u32 = 0x4000_0000;
const OPEN_EXISTING: u32 = 3;
const PURGE_RXCLEAR: u32 = 0x0008;
const PURGE_TXCLEAR: u32 = 0x0004;

#[repr(C)]
struct SpDevinfoData {
    cb_size: u32,
    class_guid: [u8; 16],
    dev_inst: u32,
    reserved: usize,
}

#[repr(C)]
struct Dcb {
    dcb_length: u32,
    baud_rate: u32,
    flags: u32,
    w_reserved: u16,
    xon_lim: u16,
    xoff_lim: u16,
    byte_size: u8,
    parity: u8,
    stop_bits: u8,
    xon_char: i8,
    xoff_char: i8,
    error_char: i8,
    eof_char: i8,
    evt_char: i8,
    w_reserved1: u16,
}

#[repr(C)]
struct CommTimeouts {
    read_interval_timeout: u32,
    read_total_timeout_multiplier: u32,
    read_total_timeout_constant: u32,
    write_total_timeout_multiplier: u32,
    write_total_timeout_constant: u32,
}

#[link(name = "setupapi")]
unsafe extern "system" {
    fn SetupDiGetClassDevsW(
        class_guid: *const [u8; 16],
        enumerator: *const u16,
        hwnd_parent: Handle,
        flags: u32,
    ) -> Handle;
    fn SetupDiEnumDeviceInfo(
        device_info_set: Handle,
        member_index: u32,
        device_info_data: *mut SpDevinfoData,
    ) -> i32;
    fn SetupDiGetDeviceRegistryPropertyW(
        device_info_set: Handle,
        device_info_data: *const SpDevinfoData,
        property: u32,
        property_reg_data_type: *mut u32,
        property_buffer: *mut u8,
        property_buffer_size: u32,
        required_size: *mut u32,
    ) -> i32;
    fn SetupDiDestroyDeviceInfoList(device_info_set: Handle) -> i32;
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateFileW(
        lp_file_name: *const u16,
        dw_desired_access: u32,
        dw_share_mode: u32,
        lp_security_attributes: *const std::ffi::c_void,
        dw_creation_disposition: u32,
        dw_flags_and_attributes: u32,
        h_template_file: Handle,
    ) -> Handle;
    fn CloseHandle(h_object: Handle) -> i32;
    fn GetCommState(h_file: Handle, lp_dcb: *mut Dcb) -> i32;
    fn SetCommState(h_file: Handle, lp_dcb: *const Dcb) -> i32;
    fn SetCommTimeouts(h_file: Handle, lp_comm_timeouts: *const CommTimeouts) -> i32;
    fn PurgeComm(h_file: Handle, dw_flags: u32) -> i32;
    fn WriteFile(
        h_file: Handle,
        lp_buffer: *const u8,
        n_number_of_bytes_to_write: u32,
        lp_number_of_bytes_written: *mut u32,
        lp_overlapped: *mut std::ffi::c_void,
    ) -> i32;
    fn ReadFile(
        h_file: Handle,
        lp_buffer: *mut u8,
        n_number_of_bytes_to_read: u32,
        lp_number_of_bytes_read: *mut u32,
        lp_overlapped: *mut std::ffi::c_void,
    ) -> i32;
}

#[derive(Debug, Clone, PartialEq)]
pub struct WireViewFrame {
    pub connector: ConnectorSnapshot,
    pub temp_in_c: f64,
    pub temp_out_c: f64,
    pub ext_temp1_c: f64,
    pub ext_temp2_c: f64,
    pub fault_log_bits: u16,
}

pub struct WireViewTransport {
    port_name: String,
    verified: bool,
}

impl WireViewTransport {
    pub fn discover() -> Option<Self> {
        let ports = find_stm32_com_ports();
        for port in ports {
            let mut t = Self {
                port_name: port,
                verified: false,
            };
            if t.probe_handshake() {
                t.verified = true;
                return Some(t);
            }
        }
        None
    }

    pub fn port_name(&self) -> &str {
        &self.port_name
    }

    pub fn read(&mut self) -> Option<ConnectorSnapshot> {
        if !self.verified && !self.probe_handshake() {
            return None;
        }
        let handle = open_com_port(&self.port_name)?;
        let res = (|| {
            unsafe {
                PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
            }
            let cmd = [CMD_READ_SENSORS];
            if !write_all(handle, &cmd) {
                return None;
            }
            let mut buf = [0u8; WIREVIEW_FRAME_BYTES];
            if !read_exact(handle, &mut buf) {
                return None;
            }
            decode_wireview_frame(&buf).map(|f| f.connector)
        })();
        unsafe {
            CloseHandle(handle);
        }
        res
    }

    fn probe_handshake(&self) -> bool {
        let Some(handle) = open_com_port(&self.port_name) else {
            return false;
        };
        let ok = (|| {
            unsafe {
                PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
            }
            let cmd = [CMD_READ_VENDOR_DATA];
            if !write_all(handle, &cmd) {
                return false;
            }
            let mut header = [0u8; 3];
            if !read_exact(handle, &mut header) {
                return false;
            }
            header[0] == 0xEF && matches!(header[1], 0x05 | 0x06)
        })();
        unsafe {
            CloseHandle(handle);
        }
        ok
    }
}

pub fn find_stm32_com_ports() -> Vec<String> {
    let mut out = Vec::new();
    let set = unsafe {
        SetupDiGetClassDevsW(null(), null(), null_mut(), DIGCF_PRESENT | DIGCF_ALLCLASSES)
    };
    if set.is_null() || set == INVALID_HANDLE_VALUE {
        return out;
    }

    let mut idx = 0u32;
    loop {
        let mut info = SpDevinfoData {
            cb_size: std::mem::size_of::<SpDevinfoData>() as u32,
            class_guid: [0; 16],
            dev_inst: 0,
            reserved: 0,
        };
        if unsafe { SetupDiEnumDeviceInfo(set, idx, &mut info) } == 0 {
            break;
        }
        idx += 1;
        if let Some(hwids) = read_dev_prop_utf16(set, &info, SPDRP_HARDWAREID) {
            let upper = hwids.to_ascii_uppercase();
            if upper.contains("VID_0483")
                && upper.contains("PID_5740")
                && let Some(friendly) = read_dev_prop_utf16(set, &info, SPDRP_FRIENDLYNAME)
                && let Some(com) = extract_com_port(&friendly)
            {
                out.push(com);
            }
        }
    }

    unsafe {
        SetupDiDestroyDeviceInfoList(set);
    }
    out
}

pub fn extract_com_port(friendly: &str) -> Option<String> {
    let start = friendly.rfind("(COM")?;
    let rest = &friendly[start + 1..];
    let end = rest.find(')')?;
    let candidate = &rest[..end];
    (candidate.starts_with("COM")
        && candidate.len() > 3
        && candidate[3..].bytes().all(|b| b.is_ascii_digit()))
    .then(|| candidate.to_string())
}

fn read_dev_prop_utf16(set: Handle, info: &SpDevinfoData, prop: u32) -> Option<String> {
    let mut reg_type = 0u32;
    let mut required = 0u32;
    unsafe {
        SetupDiGetDeviceRegistryPropertyW(
            set,
            info,
            prop,
            &mut reg_type,
            null_mut(),
            0,
            &mut required,
        );
    }
    if required == 0 || required > 8192 {
        return None;
    }
    let mut bytes = vec![0u8; required as usize];
    let ok = unsafe {
        SetupDiGetDeviceRegistryPropertyW(
            set,
            info,
            prop,
            &mut reg_type,
            bytes.as_mut_ptr(),
            required,
            &mut required,
        )
    };
    if ok == 0 {
        return None;
    }
    let u16s: Vec<u16> = bytes
        .as_chunks::<2>()
        .0
        .iter()
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    Some(String::from_utf16_lossy(&u16s))
}

fn open_com_port(port: &str) -> Option<Handle> {
    let path = format!("\\\\.\\{port}");
    let wide: Vec<u16> = OsStr::new(&path)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let h = unsafe {
        CreateFileW(
            wide.as_ptr(),
            GENERIC_READ | GENERIC_WRITE,
            0,
            null(),
            OPEN_EXISTING,
            0,
            null_mut(),
        )
    };
    if h.is_null() || h == INVALID_HANDLE_VALUE {
        return None;
    }

    let mut dcb: Dcb = unsafe { std::mem::zeroed() };
    dcb.dcb_length = std::mem::size_of::<Dcb>() as u32;
    if unsafe { GetCommState(h, &mut dcb) } == 0 {
        unsafe {
            CloseHandle(h);
        }
        return None;
    }
    dcb.baud_rate = WIREVIEW_BAUD;
    dcb.byte_size = 8;
    dcb.parity = 0;
    dcb.stop_bits = 0;
    if unsafe { SetCommState(h, &dcb) } == 0 {
        unsafe {
            CloseHandle(h);
        }
        return None;
    }
    let timeouts = CommTimeouts {
        read_interval_timeout: 50,
        read_total_timeout_multiplier: 10,
        read_total_timeout_constant: 250,
        write_total_timeout_multiplier: 10,
        write_total_timeout_constant: 250,
    };
    if unsafe { SetCommTimeouts(h, &timeouts) } == 0 {
        unsafe {
            CloseHandle(h);
        }
        return None;
    }
    Some(h)
}

fn write_all(h: Handle, data: &[u8]) -> bool {
    let mut written = 0u32;
    let ok = unsafe {
        WriteFile(
            h,
            data.as_ptr(),
            data.len() as u32,
            &mut written,
            null_mut(),
        ) != 0
    };
    ok && written as usize == data.len()
}

fn read_exact(h: Handle, buf: &mut [u8]) -> bool {
    let mut offset = 0usize;
    while offset < buf.len() {
        let mut got = 0u32;
        let ok = unsafe {
            ReadFile(
                h,
                buf[offset..].as_mut_ptr(),
                (buf.len() - offset) as u32,
                &mut got,
                null_mut(),
            )
        };
        if ok == 0 || got == 0 {
            return false;
        }
        offset += got as usize;
    }
    true
}

pub fn decode_wireview_frame(buf: &[u8]) -> Option<WireViewFrame> {
    if buf.len() < WIREVIEW_FRAME_BYTES {
        return None;
    }
    let temp_in_c = read_i16(buf, OFF_TS) as f64 / 10.0;
    let temp_out_c = read_i16(buf, OFF_TS + 2) as f64 / 10.0;
    let ext_temp1_c = read_i16(buf, OFF_TS + 4) as f64 / 10.0;
    let ext_temp2_c = read_i16(buf, OFF_TS + 6) as f64 / 10.0;

    let mut pins = Vec::with_capacity(6);
    let mut sum_current = 0.0;
    for i in 0..6 {
        let base = OFF_READINGS + i * PS_SIZE;
        let voltage_v = read_i16(buf, base) as f64 / 1000.0;
        let current_a = read_u32(buf, base + 4) as f64 / 1000.0;
        let power_w = voltage_v * current_a;
        sum_current += current_a;
        pins.push(PinReading {
            pin: (i + 1) as u8,
            voltage_v: Some(voltage_v),
            current_a,
            power_w: Some(power_w),
        });
    }

    let reported_total_power_w = read_u32(buf, OFF_TOTAL_POWER) as f64 / 1000.0;
    let reported_total_current_a = read_u32(buf, OFF_TOTAL_CURRENT) as f64 / 1000.0;
    let avg_voltage_v = read_u16(buf, OFF_AVG_VOLTAGE) as f64 / 1000.0;
    let rated_cap_w = match buf[OFF_HPWR_CAP] {
        0 => Some(600),
        1 => Some(450),
        2 => Some(300),
        3 => Some(150),
        _ => None,
    };
    let fault_status = read_u16(buf, OFF_FAULT_STATUS);
    let fault_log = read_u16(buf, OFF_FAULT_LOG);

    let total_current_a = if reported_total_current_a > 0.0 {
        reported_total_current_a
    } else {
        sum_current
    };

    let connector = ConnectorSnapshot {
        source: PerPinSource::WireViewPro2,
        connector_index: 1,
        pins,
        total_current_a,
        total_power_w: Some(reported_total_power_w),
        avg_voltage_v: Some(avg_voltage_v),
        connector_temps_c: vec![temp_in_c, temp_out_c, ext_temp1_c, ext_temp2_c],
        rated_cap_w,
        hw_fault_bits: fault_status,
    };

    Some(WireViewFrame {
        connector,
        temp_in_c,
        temp_out_c,
        ext_temp1_c,
        ext_temp2_c,
        fault_log_bits: fault_log,
    })
}

pub fn encode_wireview_frame_for_test(
    pin_mv: [i16; 6],
    pin_ma: [u32; 6],
    temps_deci_c: [i16; 4],
    cap_code: u8,
    fault_status: u16,
    fault_log: u16,
) -> [u8; WIREVIEW_FRAME_BYTES] {
    let mut buf = [0u8; WIREVIEW_FRAME_BYTES];
    for (idx, t) in temps_deci_c.iter().enumerate() {
        buf[OFF_TS + idx * 2..OFF_TS + idx * 2 + 2].copy_from_slice(&t.to_le_bytes());
    }
    let mut total_mw = 0u64;
    let mut total_ma = 0u32;
    let mut sum_mv = 0i64;
    for i in 0..6 {
        let base = OFF_READINGS + i * PS_SIZE;
        buf[base..base + 2].copy_from_slice(&pin_mv[i].to_le_bytes());
        buf[base + 4..base + 8].copy_from_slice(&pin_ma[i].to_le_bytes());
        total_ma = total_ma.saturating_add(pin_ma[i]);
        sum_mv += pin_mv[i] as i64;
        total_mw = total_mw.saturating_add(((pin_mv[i].max(0) as u64) * (pin_ma[i] as u64)) / 1000);
    }
    buf[OFF_TOTAL_POWER..OFF_TOTAL_POWER + 4].copy_from_slice(&(total_mw as u32).to_le_bytes());
    buf[OFF_TOTAL_CURRENT..OFF_TOTAL_CURRENT + 4].copy_from_slice(&total_ma.to_le_bytes());
    buf[OFF_AVG_VOLTAGE..OFF_AVG_VOLTAGE + 2].copy_from_slice(&((sum_mv / 6) as u16).to_le_bytes());
    buf[OFF_HPWR_CAP] = cap_code;
    buf[OFF_FAULT_STATUS..OFF_FAULT_STATUS + 2].copy_from_slice(&fault_status.to_le_bytes());
    buf[OFF_FAULT_LOG..OFF_FAULT_LOG + 2].copy_from_slice(&fault_log.to_le_bytes());
    buf
}

fn read_i16(buf: &[u8], off: usize) -> i16 {
    i16::from_le_bytes([buf[off], buf[off + 1]])
}

fn read_u16(buf: &[u8], off: usize) -> u16 {
    u16::from_le_bytes([buf[off], buf[off + 1]])
}

fn read_u32(buf: &[u8], off: usize) -> u32 {
    u32::from_le_bytes([buf[off], buf[off + 1], buf[off + 2], buf[off + 3]])
}
