use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::ptr::{null, null_mut};

use crate::win32::Handle;

const INVALID_HANDLE_VALUE: Handle = (-1isize) as Handle;
const FILE_SHARE_READ: u32 = 0x0000_0001;
const FILE_SHARE_WRITE: u32 = 0x0000_0002;
const OPEN_EXISTING: u32 = 3;
const IOCTL_STORAGE_QUERY_PROPERTY: u32 = 0x002D_1400;
const BUS_TYPE_NVME: u8 = 17;

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
    fn DeviceIoControl(
        h_device: Handle,
        dw_io_control_code: u32,
        lp_in_buffer: *const u8,
        n_in_buffer_size: u32,
        lp_out_buffer: *mut u8,
        n_out_buffer_size: u32,
        lp_bytes_returned: *mut u32,
        lp_overlapped: *mut std::ffi::c_void,
    ) -> i32;
}

#[derive(Debug, Clone, PartialEq)]
pub struct NvmeDriveHealth {
    pub disk_number: u32,
    pub model: String,
    pub is_nvme: bool,
    pub temp_c: Option<i16>,
    pub warn_temp_c: Option<i16>,
    pub critical_temp_c: Option<i16>,
    pub life_used_pct: Option<u8>,
    pub spare_pct: Option<u8>,
    pub spare_threshold_pct: Option<u8>,
    pub critical_warning: u8,
    pub media_errors: u64,
    pub unsafe_shutdowns: u64,
    pub throttle_events: u64,
    pub throttle_seconds: u64,
}

impl NvmeDriveHealth {
    pub fn is_throttling_or_hot(&self) -> bool {
        let warn_limit = self.warn_temp_c.unwrap_or(72);
        self.temp_c.is_some_and(|t| t >= warn_limit)
            || self.critical_warning & 0x02 != 0
            || self.throttle_events > 0
    }
}

pub fn read_all_nvme_drives() -> Vec<NvmeDriveHealth> {
    let mut out = Vec::new();
    for idx in 0..16u32 {
        if let Some(d) = read_drive(idx) {
            out.push(d);
        }
    }
    out
}

fn read_drive(disk_number: u32) -> Option<NvmeDriveHealth> {
    let path = format!("\\\\.\\PhysicalDrive{disk_number}");
    let wide: Vec<u16> = OsStr::new(&path)
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    let h = unsafe {
        CreateFileW(
            wide.as_ptr(),
            0,
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

    let result = (|| {
        let desc = query_storage_property(h, 0, 1024)?;
        let (model, bus) = parse_device_descriptor(&desc);
        let is_nvme = bus == BUS_TYPE_NVME;

        let (mut temp_c, warn_temp_c, critical_temp_c) = query_storage_property(h, 52, 512)
            .and_then(|b| parse_temperature_property(&b))
            .unwrap_or((None, None, None));

        let smart = if is_nvme {
            query_nvme_smart_log(h).and_then(|b| parse_nvme_smart_log(&b))
        } else {
            None
        };

        if temp_c.is_none()
            && let Some(ref s) = smart
        {
            temp_c = s.composite_temp_c;
        }

        Some(NvmeDriveHealth {
            disk_number,
            model,
            is_nvme,
            temp_c,
            warn_temp_c,
            critical_temp_c,
            life_used_pct: smart.as_ref().map(|s| s.life_used_pct),
            spare_pct: smart.as_ref().map(|s| s.spare_pct),
            spare_threshold_pct: smart.as_ref().map(|s| s.spare_threshold_pct),
            critical_warning: smart.as_ref().map_or(0, |s| s.critical_warning),
            media_errors: smart.as_ref().map_or(0, |s| s.media_errors),
            unsafe_shutdowns: smart.as_ref().map_or(0, |s| s.unsafe_shutdowns),
            throttle_events: smart.as_ref().map_or(0, |s| s.throttle_events),
            throttle_seconds: smart.as_ref().map_or(0, |s| s.throttle_seconds),
        })
    })();

    unsafe {
        CloseHandle(h);
    }
    result
}

fn query_storage_property(h: Handle, prop_id: u32, out_len: usize) -> Option<Vec<u8>> {
    let mut query = [0u8; 12];
    query[0..4].copy_from_slice(&prop_id.to_le_bytes());
    let mut out = vec![0u8; out_len];
    let mut returned = 0u32;
    let ok = unsafe {
        DeviceIoControl(
            h,
            IOCTL_STORAGE_QUERY_PROPERTY,
            query.as_ptr(),
            query.len() as u32,
            out.as_mut_ptr(),
            out.len() as u32,
            &mut returned,
            null_mut(),
        )
    };
    if ok == 0 || returned == 0 {
        return None;
    }
    out.truncate(returned as usize);
    Some(out)
}

fn query_nvme_smart_log(h: Handle) -> Option<Vec<u8>> {
    let data_len = 512usize;
    let mut query = vec![0u8; 8 + 40 + data_len];
    query[0..4].copy_from_slice(&50u32.to_le_bytes());
    query[8..12].copy_from_slice(&3u32.to_le_bytes());
    query[12..16].copy_from_slice(&2u32.to_le_bytes());
    query[16..20].copy_from_slice(&0x02u32.to_le_bytes());
    query[24..28].copy_from_slice(&40u32.to_le_bytes());
    query[28..32].copy_from_slice(&(data_len as u32).to_le_bytes());

    let mut answer = vec![0u8; query.len()];
    let mut returned = 0u32;
    let ok = unsafe {
        DeviceIoControl(
            h,
            IOCTL_STORAGE_QUERY_PROPERTY,
            query.as_ptr(),
            query.len() as u32,
            answer.as_mut_ptr(),
            answer.len() as u32,
            &mut returned,
            null_mut(),
        )
    };
    if ok == 0 || (returned as usize) < 48 {
        return None;
    }
    let offset = u32::from_le_bytes(answer[24..28].try_into().ok()?) as usize;
    let size = (u32::from_le_bytes(answer[28..32].try_into().ok()?) as usize).min(data_len);
    let start = 8 + offset;
    if offset >= 40 && size > 0 && start + size <= returned as usize {
        Some(answer[start..start + size].to_vec())
    } else {
        None
    }
}

pub fn parse_device_descriptor(buf: &[u8]) -> (String, u8) {
    if buf.len() < 32 {
        return ("Storage Drive".into(), 0);
    }
    let bus = buf[28];
    let vendor_off = u32::from_le_bytes(buf[12..16].try_into().unwrap()) as usize;
    let product_off = u32::from_le_bytes(buf[16..20].try_into().unwrap()) as usize;
    let vendor = read_cstr_at(buf, vendor_off);
    let product = read_cstr_at(buf, product_off);
    let model = match (vendor.is_empty(), product.is_empty()) {
        (true, true) => "Storage Drive".into(),
        (true, false) => product,
        (false, true) => vendor,
        (false, false)
            if product
                .to_ascii_lowercase()
                .starts_with(&vendor.to_ascii_lowercase()) =>
        {
            product
        }
        (false, false) => format!("{vendor} {product}"),
    };
    (model, bus)
}

fn read_cstr_at(buf: &[u8], off: usize) -> String {
    if off == 0 || off >= buf.len() {
        return String::new();
    }
    let end = buf[off..]
        .iter()
        .position(|&b| b == 0)
        .map_or(buf.len(), |p| off + p);
    String::from_utf8_lossy(&buf[off..end]).trim().to_string()
}

pub fn parse_temperature_property(buf: &[u8]) -> Option<(Option<i16>, Option<i16>, Option<i16>)> {
    if buf.len() < 40 {
        return None;
    }
    let count = u16::from_le_bytes([buf[12], buf[13]]);
    if count == 0 {
        return None;
    }
    let crit = i16::from_le_bytes([buf[8], buf[9]]);
    let warn = i16::from_le_bytes([buf[10], buf[11]]);
    let now = i16::from_le_bytes([buf[26], buf[27]]);
    let valid_temp = |v: i16| (v > -40 && v < 150).then_some(v);
    let valid_lim = |v: i16| (v > 0 && v < 150).then_some(v);
    Some((valid_temp(now), valid_lim(warn), valid_lim(crit)))
}

#[derive(Debug, Clone, PartialEq)]
pub struct NvmeSmartParsed {
    pub critical_warning: u8,
    pub composite_temp_c: Option<i16>,
    pub spare_pct: u8,
    pub spare_threshold_pct: u8,
    pub life_used_pct: u8,
    pub unsafe_shutdowns: u64,
    pub media_errors: u64,
    pub throttle_events: u64,
    pub throttle_seconds: u64,
}

pub fn parse_nvme_smart_log(log: &[u8]) -> Option<NvmeSmartParsed> {
    if log.len() < 232 {
        return None;
    }
    let temp_k = u16::from_le_bytes([log[1], log[2]]) as i32;
    let composite_temp_c = if (233..423).contains(&temp_k) {
        Some((temp_k - 273) as i16)
    } else {
        None
    };
    let u64_at = |at: usize| u64::from_le_bytes(log[at..at + 8].try_into().unwrap());
    let u32_at = |at: usize| u32::from_le_bytes(log[at..at + 4].try_into().unwrap()) as u64;

    Some(NvmeSmartParsed {
        critical_warning: log[0],
        composite_temp_c,
        spare_pct: log[3],
        spare_threshold_pct: log[4],
        life_used_pct: log[5],
        unsafe_shutdowns: u64_at(144),
        media_errors: u64_at(160),
        throttle_events: u32_at(216) + u32_at(220),
        throttle_seconds: u32_at(224) + u32_at(228),
    })
}
