use std::ptr::{null, null_mut};

use crate::types::{ConnectorSnapshot, PerPinSource, PinReading};
use crate::win32::Handle;

pub const MSI_USB_VID: u16 = 0x0DB0;
pub const MSI_AI1300TS_PID: u16 = 0xAA6F;
pub const MSI_AI1600TS_PID: u16 = 0x808C;
pub const MSI_REPORT_BYTES: usize = 65;

const INVALID_HANDLE_VALUE: Handle = (-1isize) as Handle;
const DIGCF_PRESENT: u32 = 0x0000_0002;
const DIGCF_DEVICEINTERFACE: u32 = 0x0000_0010;
const GENERIC_READ: u32 = 0x8000_0000;
const FILE_SHARE_READ: u32 = 0x0000_0001;
const FILE_SHARE_WRITE: u32 = 0x0000_0002;
const OPEN_EXISTING: u32 = 3;

pub const MSI_HID_DISCOVERY_ACCESS: u32 = 0;
pub const MSI_HID_OPEN_ACCESS: u32 = GENERIC_READ;

#[repr(C)]
struct SpDeviceInterfaceData {
    cb_size: u32,
    interface_class_guid: [u8; 16],
    flags: u32,
    reserved: usize,
}

#[repr(C)]
struct HiddAttributes {
    size: u32,
    vendor_id: u16,
    product_id: u16,
    version_number: u16,
}

#[link(name = "hid")]
unsafe extern "system" {
    fn HidD_GetHidGuid(hid_guid: *mut [u8; 16]);
    fn HidD_GetAttributes(hid_device_object: Handle, attributes: *mut HiddAttributes) -> u8;
}

#[link(name = "setupapi")]
unsafe extern "system" {
    fn SetupDiGetClassDevsW(
        class_guid: *const [u8; 16],
        enumerator: *const u16,
        hwnd_parent: Handle,
        flags: u32,
    ) -> Handle;
    fn SetupDiEnumDeviceInterfaces(
        device_info_set: Handle,
        device_info_data: *const std::ffi::c_void,
        interface_class_guid: *const [u8; 16],
        member_index: u32,
        device_interface_data: *mut SpDeviceInterfaceData,
    ) -> i32;
    fn SetupDiGetDeviceInterfaceDetailW(
        device_info_set: Handle,
        device_interface_data: *const SpDeviceInterfaceData,
        device_interface_detail_data: *mut u8,
        device_interface_detail_data_size: u32,
        required_size: *mut u32,
        device_info_data: *mut std::ffi::c_void,
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
    fn ReadFile(
        h_file: Handle,
        lp_buffer: *mut u8,
        n_number_of_bytes_to_read: u32,
        lp_number_of_bytes_read: *mut u32,
        lp_overlapped: *mut std::ffi::c_void,
    ) -> i32;
}

pub trait MsiHidReadOnlySource {
    fn read_input_report(&mut self, buf: &mut [u8; MSI_REPORT_BYTES]) -> usize;
}

#[derive(Debug, Clone)]
pub struct MockMsiHidSource {
    pub report: [u8; MSI_REPORT_BYTES],
    pub read_calls: usize,
}

impl MockMsiHidSource {
    pub fn new(report: [u8; MSI_REPORT_BYTES]) -> Self {
        Self {
            report,
            read_calls: 0,
        }
    }
}

impl MsiHidReadOnlySource for MockMsiHidSource {
    fn read_input_report(&mut self, buf: &mut [u8; MSI_REPORT_BYTES]) -> usize {
        self.read_calls += 1;
        buf.copy_from_slice(&self.report);
        MSI_REPORT_BYTES
    }
}

pub fn read_primary_from_source<S: MsiHidReadOnlySource>(src: &mut S) -> Option<ConnectorSnapshot> {
    let mut reply = [0u8; MSI_REPORT_BYTES];
    let got = src.read_input_report(&mut reply);
    if got < 34 {
        return None;
    }
    let conns = decode_connector_currents(&reply);
    if conns[0].total_current_a >= conns[1].total_current_a {
        Some(conns[0].clone())
    } else {
        Some(conns[1].clone())
    }
}

pub struct MsiPsuTransport {
    path_wide: Vec<u16>,
    model: &'static str,
}

impl MsiPsuTransport {
    pub fn discover() -> Option<Self> {
        let mut hid_guid = [0u8; 16];
        unsafe {
            HidD_GetHidGuid(&mut hid_guid);
        }
        let set = unsafe {
            SetupDiGetClassDevsW(
                &hid_guid,
                null(),
                null_mut(),
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
            )
        };
        if set.is_null() || set == INVALID_HANDLE_VALUE {
            return None;
        }

        let mut found = None;
        let mut idx = 0u32;
        loop {
            let mut iface = SpDeviceInterfaceData {
                cb_size: std::mem::size_of::<SpDeviceInterfaceData>() as u32,
                interface_class_guid: [0; 16],
                flags: 0,
                reserved: 0,
            };
            if unsafe { SetupDiEnumDeviceInterfaces(set, null(), &hid_guid, idx, &mut iface) } == 0
            {
                break;
            }
            idx += 1;

            let mut required = 0u32;
            unsafe {
                SetupDiGetDeviceInterfaceDetailW(
                    set,
                    &iface,
                    null_mut(),
                    0,
                    &mut required,
                    null_mut(),
                );
            }
            if required <= 4 || required > 4096 {
                continue;
            }
            let mut detail = vec![0u8; required as usize];
            let cb_detail: u32 = if cfg!(target_pointer_width = "64") {
                8
            } else {
                6
            };
            detail[0..4].copy_from_slice(&cb_detail.to_le_bytes());
            if unsafe {
                SetupDiGetDeviceInterfaceDetailW(
                    set,
                    &iface,
                    detail.as_mut_ptr(),
                    required,
                    &mut required,
                    null_mut(),
                )
            } == 0
            {
                continue;
            }

            let wide_slice = &detail[4..];
            let path_wide: Vec<u16> = wide_slice
                .as_chunks::<2>()
                .0
                .iter()
                .map(|c| u16::from_le_bytes([c[0], c[1]]))
                .collect();

            let h = unsafe {
                CreateFileW(
                    path_wide.as_ptr(),
                    MSI_HID_DISCOVERY_ACCESS,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    null(),
                    OPEN_EXISTING,
                    0,
                    null_mut(),
                )
            };
            if h.is_null() || h == INVALID_HANDLE_VALUE {
                continue;
            }
            let mut attrs = HiddAttributes {
                size: std::mem::size_of::<HiddAttributes>() as u32,
                vendor_id: 0,
                product_id: 0,
                version_number: 0,
            };
            let ok = unsafe { HidD_GetAttributes(h, &mut attrs) != 0 };
            unsafe {
                CloseHandle(h);
            }
            if ok && let Some(model) = msi_model_name(attrs.vendor_id, attrs.product_id) {
                found = Some(Self { path_wide, model });
                break;
            }
        }

        unsafe {
            SetupDiDestroyDeviceInfoList(set);
        }
        found
    }

    pub fn model(&self) -> &'static str {
        self.model
    }

    pub fn read_primary_connector(&self) -> Option<ConnectorSnapshot> {
        let h = unsafe {
            CreateFileW(
                self.path_wide.as_ptr(),
                MSI_HID_OPEN_ACCESS,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                null(),
                OPEN_EXISTING,
                0,
                null_mut(),
            )
        };
        if h.is_null() || h == INVALID_HANDLE_VALUE {
            return None;
        }
        let res = (|| {
            let mut reply = [0u8; MSI_REPORT_BYTES];
            let mut got = 0u32;
            if unsafe {
                ReadFile(
                    h,
                    reply.as_mut_ptr(),
                    reply.len() as u32,
                    &mut got,
                    null_mut(),
                )
            } == 0
                || got < 34
            {
                return None;
            }
            let conns = decode_connector_currents(&reply);
            if conns[0].total_current_a >= conns[1].total_current_a {
                Some(conns[0].clone())
            } else {
                Some(conns[1].clone())
            }
        })();
        unsafe {
            CloseHandle(h);
        }
        res
    }
}

pub fn msi_model_name(vid: u16, pid: u16) -> Option<&'static str> {
    if vid != MSI_USB_VID {
        return None;
    }
    match pid {
        MSI_AI1300TS_PID => Some("MPG Ai1300TS"),
        MSI_AI1600TS_PID => Some("MPG Ai1600TS"),
        _ => None,
    }
}

pub fn decode_linear11(lo: u8, hi: u8) -> f64 {
    let word = u16::from_le_bytes([lo, hi]);
    let raw_exp = (word as i16) >> 11;
    let mantissa = (word & 0x07FF) as f64;
    mantissa * 2.0_f64.powi(raw_exp as i32)
}

pub fn encode_linear11_unsigned(value: f64, exp: i8) -> [u8; 2] {
    let scaled = (value / 2.0_f64.powi(exp as i32))
        .round()
        .clamp(0.0, 2047.0) as u16;
    let exp_bits = ((exp as i16) & 0x1F) as u16;
    let word = (exp_bits << 11) | (scaled & 0x07FF);
    word.to_le_bytes()
}

pub fn decode_connector_currents(reply: &[u8; MSI_REPORT_BYTES]) -> [ConnectorSnapshot; 2] {
    std::array::from_fn(|connector_idx| {
        let mut pins = Vec::with_capacity(6);
        let mut total_current_a = 0.0;
        for pin in 0..6 {
            let off = 9 + connector_idx * 12 + pin * 2;
            let current_a = decode_linear11(reply[off], reply[off + 1]);
            total_current_a += current_a;
            pins.push(PinReading {
                pin: (pin + 1) as u8,
                voltage_v: None,
                current_a,
                power_w: None,
            });
        }
        ConnectorSnapshot {
            source: PerPinSource::MsiPsuHid,
            connector_index: (connector_idx + 1) as u8,
            pins,
            total_current_a,
            total_power_w: None,
            avg_voltage_v: None,
            connector_temps_c: Vec::new(),
            rated_cap_w: Some(600),
            hw_fault_bits: 0,
        }
    })
}
