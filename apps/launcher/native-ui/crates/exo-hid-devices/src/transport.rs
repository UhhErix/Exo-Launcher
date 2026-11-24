use serde::{Deserialize, Serialize};
use std::collections::{HashMap, VecDeque};
use std::fmt;
use std::time::{Duration, Instant};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HidDeviceDescriptor {
    pub key: String,
    pub vid: u16,
    pub pid: u16,
    pub usage_page: u16,
    pub usage: u16,
    pub product_name: String,
    pub manufacturer: String,
    pub input_report_len: usize,
    pub output_report_len: usize,
    pub feature_report_len: usize,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub instance_id: Option<String>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HidError {
    NotFound(String),
    NotOpen(String),
    Timeout(String),
    Io(String),
    InvalidReport(String),
    RateLimited { device: String, retry_after_ms: u64 },
    ReadOnlyGuard(String),
    BootloaderBlocked(String),
}

impl fmt::Display for HidError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::NotFound(m) => write!(f, "Device not found: {m}"),
            Self::NotOpen(m) => write!(f, "Device is not open: {m}"),
            Self::Timeout(m) => write!(f, "Device timed out: {m}"),
            Self::Io(m) => write!(f, "HID I/O failed: {m}"),
            Self::InvalidReport(m) => write!(f, "Invalid HID report: {m}"),
            Self::RateLimited {
                device,
                retry_after_ms,
            } => write!(
                f,
                "Write rate-limited for {device}; retry after {retry_after_ms} ms"
            ),
            Self::ReadOnlyGuard(m) => write!(f, "Blocked by read-only safety guard: {m}"),
            Self::BootloaderBlocked(m) => write!(f, "Bootloader/DFU command blocked: {m}"),
        }
    }
}

impl std::error::Error for HidError {}

pub trait HidTransport {
    fn enumerate(&self) -> Result<Vec<HidDeviceDescriptor>, HidError>;
    fn open(&mut self, key: &str) -> Result<(), HidError>;
    fn close(&mut self, key: &str);
    fn is_open(&self, key: &str) -> bool;
    fn write_output(&mut self, key: &str, report_id: u8, payload: &[u8]) -> Result<(), HidError>;
    fn read_input(&mut self, key: &str, timeout_ms: u32) -> Result<Vec<u8>, HidError>;
    fn set_feature(&mut self, key: &str, report_id: u8, payload: &[u8]) -> Result<(), HidError>;
    fn get_feature(&mut self, key: &str, report_id: u8, length: usize)
    -> Result<Vec<u8>, HidError>;
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RecordedWrite {
    pub key: String,
    pub report_id: u8,
    pub payload: Vec<u8>,
}

type MockResponder = Box<dyn FnMut(&str, u8, &[u8]) -> Option<Vec<u8>> + Send>;
type FeatureReplyMap = HashMap<(String, u8), VecDeque<Result<Vec<u8>, HidError>>>;

pub struct MockHidTransport {
    pub devices: Vec<HidDeviceDescriptor>,
    pub opened: HashMap<String, bool>,
    pub written_outputs: Vec<RecordedWrite>,
    pub written_features: Vec<RecordedWrite>,
    pub queued_inputs: HashMap<String, VecDeque<Result<Vec<u8>, HidError>>>,
    pub feature_replies: FeatureReplyMap,
    pub fail_next_write: Option<HidError>,
    pub responder: Option<MockResponder>,
}

impl Default for MockHidTransport {
    fn default() -> Self {
        Self::new()
    }
}

impl MockHidTransport {
    pub fn new() -> Self {
        Self {
            devices: Vec::new(),
            opened: HashMap::new(),
            written_outputs: Vec::new(),
            written_features: Vec::new(),
            queued_inputs: HashMap::new(),
            feature_replies: HashMap::new(),
            fail_next_write: None,
            responder: None,
        }
    }

    pub fn add_device(&mut self, desc: HidDeviceDescriptor) {
        self.devices.push(desc);
    }

    pub fn push_input_reply(&mut self, key: &str, reply: Vec<u8>) {
        self.queued_inputs
            .entry(key.to_string())
            .or_default()
            .push_back(Ok(reply));
    }

    pub fn push_input_error(&mut self, key: &str, err: HidError) {
        self.queued_inputs
            .entry(key.to_string())
            .or_default()
            .push_back(Err(err));
    }

    pub fn push_feature_reply(&mut self, key: &str, report_id: u8, reply: Vec<u8>) {
        self.feature_replies
            .entry((key.to_string(), report_id))
            .or_default()
            .push_back(Ok(reply));
    }

    pub fn push_feature_error(&mut self, key: &str, report_id: u8, err: HidError) {
        self.feature_replies
            .entry((key.to_string(), report_id))
            .or_default()
            .push_back(Err(err));
    }

    pub fn set_responder<F>(&mut self, f: F)
    where
        F: FnMut(&str, u8, &[u8]) -> Option<Vec<u8>> + Send + 'static,
    {
        self.responder = Some(Box::new(f));
    }

    pub fn clear_writes(&mut self) {
        self.written_outputs.clear();
        self.written_features.clear();
    }
}

impl HidTransport for MockHidTransport {
    fn enumerate(&self) -> Result<Vec<HidDeviceDescriptor>, HidError> {
        Ok(self.devices.clone())
    }

    fn open(&mut self, key: &str) -> Result<(), HidError> {
        if !self.devices.iter().any(|d| d.key == key) {
            return Err(HidError::NotFound(key.to_string()));
        }
        self.opened.insert(key.to_string(), true);
        Ok(())
    }

    fn close(&mut self, key: &str) {
        self.opened.remove(key);
    }

    fn is_open(&self, key: &str) -> bool {
        self.opened.get(key).copied().unwrap_or(false)
    }

    fn write_output(&mut self, key: &str, report_id: u8, payload: &[u8]) -> Result<(), HidError> {
        if !self.is_open(key) {
            return Err(HidError::NotOpen(key.to_string()));
        }
        if let Some(err) = self.fail_next_write.take() {
            return Err(err);
        }
        self.written_outputs.push(RecordedWrite {
            key: key.to_string(),
            report_id,
            payload: payload.to_vec(),
        });
        if let Some(responder) = self.responder.as_mut()
            && let Some(reply) = responder(key, report_id, payload)
        {
            self.queued_inputs
                .entry(key.to_string())
                .or_default()
                .push_back(Ok(reply));
        }
        Ok(())
    }

    fn read_input(&mut self, key: &str, _timeout_ms: u32) -> Result<Vec<u8>, HidError> {
        if !self.is_open(key) {
            return Err(HidError::NotOpen(key.to_string()));
        }
        match self
            .queued_inputs
            .get_mut(key)
            .and_then(VecDeque::pop_front)
        {
            Some(res) => res,
            None => Err(HidError::Timeout(key.to_string())),
        }
    }

    fn set_feature(&mut self, key: &str, report_id: u8, payload: &[u8]) -> Result<(), HidError> {
        if !self.is_open(key) {
            return Err(HidError::NotOpen(key.to_string()));
        }
        if let Some(err) = self.fail_next_write.take() {
            return Err(err);
        }
        self.written_features.push(RecordedWrite {
            key: key.to_string(),
            report_id,
            payload: payload.to_vec(),
        });
        if let Some(responder) = self.responder.as_mut()
            && let Some(reply) = responder(key, report_id, payload)
        {
            self.feature_replies
                .entry((key.to_string(), report_id))
                .or_default()
                .push_back(Ok(reply));
        }
        Ok(())
    }

    fn get_feature(
        &mut self,
        key: &str,
        report_id: u8,
        _length: usize,
    ) -> Result<Vec<u8>, HidError> {
        if !self.is_open(key) {
            return Err(HidError::NotOpen(key.to_string()));
        }
        match self
            .feature_replies
            .get_mut(&(key.to_string(), report_id))
            .and_then(VecDeque::pop_front)
        {
            Some(res) => res,
            None => Err(HidError::Timeout(format!(
                "{key} feature 0x{report_id:02X}"
            ))),
        }
    }
}

#[derive(Clone, Debug)]
pub struct HidWriteRateLimiter {
    min_interval: Duration,
    max_per_window: usize,
    window: Duration,
    history: HashMap<String, VecDeque<Instant>>,
}

impl Default for HidWriteRateLimiter {
    fn default() -> Self {
        Self::new(Duration::from_millis(50), 12, Duration::from_secs(1))
    }
}

impl HidWriteRateLimiter {
    pub fn new(min_interval: Duration, max_per_window: usize, window: Duration) -> Self {
        Self {
            min_interval,
            max_per_window: max_per_window.max(1),
            window,
            history: HashMap::new(),
        }
    }

    pub fn check_and_record(&mut self, device: &str, now: Instant) -> Result<(), HidError> {
        let queue = self.history.entry(device.to_string()).or_default();
        while let Some(&oldest) = queue.front() {
            if now.saturating_duration_since(oldest) >= self.window {
                queue.pop_front();
            } else {
                break;
            }
        }
        if let Some(&last) = queue.back() {
            let elapsed = now.saturating_duration_since(last);
            if elapsed < self.min_interval {
                let retry = self.min_interval.saturating_sub(elapsed).as_millis() as u64;
                return Err(HidError::RateLimited {
                    device: device.to_string(),
                    retry_after_ms: retry.max(1),
                });
            }
        }
        if queue.len() >= self.max_per_window {
            let retry = queue
                .front()
                .map(|&oldest| {
                    self.window
                        .saturating_sub(now.saturating_duration_since(oldest))
                        .as_millis() as u64
                })
                .unwrap_or(50);
            return Err(HidError::RateLimited {
                device: device.to_string(),
                retry_after_ms: retry.max(1),
            });
        }
        queue.push_back(now);
        Ok(())
    }

    pub fn clear(&mut self, device: &str) {
        self.history.remove(device);
    }
}

pub fn build_padded_report(report_id: u8, payload: &[u8], report_len: usize) -> Vec<u8> {
    let target = report_len.max(payload.len().saturating_add(1));
    let mut buf = vec![0u8; target];
    buf[0] = report_id;
    let copy_len = payload.len().min(target.saturating_sub(1));
    buf[1..1 + copy_len].copy_from_slice(&payload[..copy_len]);
    buf
}

#[cfg(windows)]
pub mod win32 {
    use super::{HidDeviceDescriptor, HidError, HidTransport, build_padded_report};
    use std::collections::HashMap;
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;
    use windows::Win32::Devices::DeviceAndDriverInstallation::{
        DIGCF_DEVICEINTERFACE, DIGCF_PRESENT, SP_DEVICE_INTERFACE_DATA,
        SP_DEVICE_INTERFACE_DETAIL_DATA_W, SetupDiDestroyDeviceInfoList,
        SetupDiEnumDeviceInterfaces, SetupDiGetClassDevsW, SetupDiGetDeviceInterfaceDetailW,
    };
    use windows::Win32::Devices::HumanInterfaceDevice::{
        HIDD_ATTRIBUTES, HIDP_CAPS, HidD_FreePreparsedData, HidD_GetAttributes, HidD_GetFeature,
        HidD_GetHidGuid, HidD_GetManufacturerString, HidD_GetPreparsedData, HidD_GetProductString,
        HidD_SetFeature, HidP_GetCaps, PHIDP_PREPARSED_DATA,
    };
    use windows::Win32::Foundation::{
        CloseHandle, ERROR_IO_PENDING, GetLastError, HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0,
    };
    use windows::Win32::Storage::FileSystem::{
        CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ,
        FILE_SHARE_WRITE, OPEN_EXISTING, ReadFile, WriteFile,
    };
    use windows::Win32::System::IO::{CancelIoEx, GetOverlappedResult, OVERLAPPED};
    use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject};
    use windows::core::PCWSTR;

    struct OpenHandle {
        handle: HANDLE,
        input_len: usize,
        output_len: usize,
        feature_len: usize,
    }

    impl Drop for OpenHandle {
        fn drop(&mut self) {
            if !self.handle.is_invalid() {
                unsafe {
                    let _ = CloseHandle(self.handle);
                }
            }
        }
    }

    pub struct Win32HidTransport {
        read_only_guard: bool,
        path_by_key: HashMap<String, Vec<u16>>,
        caps_by_key: HashMap<String, (usize, usize, usize)>,
        handles: HashMap<String, OpenHandle>,
    }

    impl Win32HidTransport {
        pub fn new(read_only_guard: bool) -> Self {
            Self {
                read_only_guard,
                path_by_key: HashMap::new(),
                caps_by_key: HashMap::new(),
                handles: HashMap::new(),
            }
        }

        pub fn enumerate_read_only() -> Result<Vec<HidDeviceDescriptor>, HidError> {
            let mut transport = Self::new(true);
            transport.refresh_paths()
        }

        pub fn refresh_paths(&mut self) -> Result<Vec<HidDeviceDescriptor>, HidError> {
            let mut out = Vec::new();
            self.path_by_key.clear();
            self.caps_by_key.clear();
            unsafe {
                let guid = HidD_GetHidGuid();
                let info = SetupDiGetClassDevsW(
                    Some(&guid),
                    PCWSTR::null(),
                    None,
                    DIGCF_PRESENT | DIGCF_DEVICEINTERFACE,
                )
                .map_err(|e| HidError::Io(e.to_string()))?;

                let mut index = 0u32;
                loop {
                    let mut iface = SP_DEVICE_INTERFACE_DATA {
                        cbSize: std::mem::size_of::<SP_DEVICE_INTERFACE_DATA>() as u32,
                        ..Default::default()
                    };
                    if SetupDiEnumDeviceInterfaces(info, None, &guid, index, &mut iface).is_err() {
                        break;
                    }
                    index += 1;

                    let mut needed = 0u32;
                    let _ = SetupDiGetDeviceInterfaceDetailW(
                        info,
                        &iface,
                        None,
                        0,
                        Some(&mut needed),
                        None,
                    );
                    if needed == 0 {
                        continue;
                    }
                    let mut buf = vec![0u8; needed as usize];
                    let detail = buf.as_mut_ptr() as *mut SP_DEVICE_INTERFACE_DETAIL_DATA_W;
                    (*detail).cbSize =
                        std::mem::size_of::<SP_DEVICE_INTERFACE_DETAIL_DATA_W>() as u32;
                    if SetupDiGetDeviceInterfaceDetailW(
                        info,
                        &iface,
                        Some(detail),
                        needed,
                        None,
                        None,
                    )
                    .is_err()
                    {
                        continue;
                    }

                    let path_ptr = (*detail).DevicePath.as_ptr();
                    let mut len = 0usize;
                    while *path_ptr.add(len) != 0 {
                        len += 1;
                    }
                    let mut wide = Vec::with_capacity(len + 1);
                    wide.extend_from_slice(std::slice::from_raw_parts(path_ptr, len));
                    wide.push(0);

                    let handle = match CreateFileW(
                        PCWSTR(wide.as_ptr()),
                        0,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        None,
                        OPEN_EXISTING,
                        FILE_ATTRIBUTE_NORMAL,
                        None,
                    ) {
                        Ok(h) if !h.is_invalid() => h,
                        _ => continue,
                    };

                    let mut attrs = HIDD_ATTRIBUTES {
                        Size: std::mem::size_of::<HIDD_ATTRIBUTES>() as u32,
                        ..Default::default()
                    };
                    let ok_attrs = HidD_GetAttributes(handle, &mut attrs);

                    let mut caps = HIDP_CAPS::default();
                    let mut preparsed = PHIDP_PREPARSED_DATA::default();
                    let ok_caps = HidD_GetPreparsedData(handle, &mut preparsed) && {
                        let status = HidP_GetCaps(preparsed, &mut caps);
                        let _ = HidD_FreePreparsedData(preparsed);
                        status.0 >= 0
                    };

                    let product_name = read_hid_string(handle, true);
                    let manufacturer = read_hid_string(handle, false);
                    let _ = CloseHandle(handle);

                    if !ok_attrs || !ok_caps {
                        continue;
                    }

                    let path_str = OsString::from_wide(&wide[..len])
                        .to_string_lossy()
                        .to_string();
                    let key = format!(
                        "hid:{:04x}:{:04x}:{:04x}:{:04x}:{}",
                        attrs.VendorID, attrs.ProductID, caps.UsagePage, caps.Usage, index
                    );
                    let in_len = caps.InputReportByteLength as usize;
                    let out_len = caps.OutputReportByteLength as usize;
                    let feat_len = caps.FeatureReportByteLength as usize;
                    self.path_by_key.insert(key.clone(), wide);
                    self.caps_by_key
                        .insert(key.clone(), (in_len, out_len, feat_len));
                    out.push(HidDeviceDescriptor {
                        key,
                        vid: attrs.VendorID,
                        pid: attrs.ProductID,
                        usage_page: caps.UsagePage,
                        usage: caps.Usage,
                        product_name,
                        manufacturer,
                        input_report_len: in_len,
                        output_report_len: out_len,
                        feature_report_len: feat_len,
                        instance_id: Some(path_str),
                    });
                }
                let _ = SetupDiDestroyDeviceInfoList(info);
            }
            Ok(out)
        }
    }

    unsafe fn read_hid_string(handle: HANDLE, product: bool) -> String {
        let mut buf = [0u16; 128];
        let ok = unsafe {
            if product {
                HidD_GetProductString(handle, buf.as_mut_ptr() as *mut _, (buf.len() * 2) as u32)
            } else {
                HidD_GetManufacturerString(
                    handle,
                    buf.as_mut_ptr() as *mut _,
                    (buf.len() * 2) as u32,
                )
            }
        };
        if !ok {
            return String::new();
        }
        let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        OsString::from_wide(&buf[..end])
            .to_string_lossy()
            .trim()
            .to_string()
    }

    impl HidTransport for Win32HidTransport {
        fn enumerate(&self) -> Result<Vec<HidDeviceDescriptor>, HidError> {
            Self::enumerate_read_only()
        }

        fn open(&mut self, key: &str) -> Result<(), HidError> {
            if self.handles.contains_key(key) {
                return Ok(());
            }
            let wide = self
                .path_by_key
                .get(key)
                .ok_or_else(|| HidError::NotFound(key.to_string()))?;
            let (in_len, out_len, feat_len) =
                self.caps_by_key.get(key).copied().unwrap_or((64, 64, 64));
            let access = if self.read_only_guard {
                0x8000_0000u32
            } else {
                0x8000_0000u32 | 0x4000_0000u32
            };
            let handle = unsafe {
                CreateFileW(
                    PCWSTR(wide.as_ptr()),
                    access,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    None,
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    None,
                )
                .map_err(|e| HidError::Io(e.to_string()))?
            };
            if handle == INVALID_HANDLE_VALUE {
                return Err(HidError::Io(format!("Invalid handle for {key}")));
            }
            self.handles.insert(
                key.to_string(),
                OpenHandle {
                    handle,
                    input_len: in_len,
                    output_len: out_len,
                    feature_len: feat_len,
                },
            );
            Ok(())
        }

        fn close(&mut self, key: &str) {
            self.handles.remove(key);
        }

        fn is_open(&self, key: &str) -> bool {
            self.handles.contains_key(key)
        }

        fn write_output(
            &mut self,
            key: &str,
            report_id: u8,
            payload: &[u8],
        ) -> Result<(), HidError> {
            if self.read_only_guard {
                return Err(HidError::ReadOnlyGuard(key.to_string()));
            }
            let open = self
                .handles
                .get(key)
                .ok_or_else(|| HidError::NotOpen(key.to_string()))?;
            let buf = build_padded_report(report_id, payload, open.output_len);
            unsafe {
                let evt = CreateEventW(None, true, false, PCWSTR::null())
                    .map_err(|e| HidError::Io(e.to_string()))?;
                let mut ov = OVERLAPPED {
                    hEvent: evt,
                    ..Default::default()
                };
                let mut written = 0u32;
                let res = WriteFile(open.handle, Some(&buf), Some(&mut written), Some(&mut ov));
                if res.is_err() {
                    let err = GetLastError();
                    if err != ERROR_IO_PENDING {
                        let _ = CloseHandle(evt);
                        return Err(HidError::Io(format!("WriteFile failed: {err:?}")));
                    }
                    let wait = WaitForSingleObject(evt, 1000);
                    if wait != WAIT_OBJECT_0 {
                        let _ = CancelIoEx(open.handle, Some(&ov));
                        let _ = CloseHandle(evt);
                        return Err(HidError::Timeout(key.to_string()));
                    }
                    if GetOverlappedResult(open.handle, &ov, &mut written, false).is_err() {
                        let _ = CloseHandle(evt);
                        return Err(HidError::Io(format!("Overlapped write failed on {key}")));
                    }
                }
                let _ = CloseHandle(evt);
            }
            Ok(())
        }

        fn read_input(&mut self, key: &str, timeout_ms: u32) -> Result<Vec<u8>, HidError> {
            let open = self
                .handles
                .get(key)
                .ok_or_else(|| HidError::NotOpen(key.to_string()))?;
            let mut buf = vec![0u8; open.input_len.max(64)];
            unsafe {
                let evt = CreateEventW(None, true, false, PCWSTR::null())
                    .map_err(|e| HidError::Io(e.to_string()))?;
                let mut ov = OVERLAPPED {
                    hEvent: evt,
                    ..Default::default()
                };
                let mut read = 0u32;
                let res = ReadFile(open.handle, Some(&mut buf), Some(&mut read), Some(&mut ov));
                if res.is_err() {
                    let err = GetLastError();
                    if err != ERROR_IO_PENDING {
                        let _ = CloseHandle(evt);
                        return Err(HidError::Io(format!("ReadFile failed: {err:?}")));
                    }
                    let wait = WaitForSingleObject(evt, timeout_ms);
                    if wait != WAIT_OBJECT_0 {
                        let _ = CancelIoEx(open.handle, Some(&ov));
                        let _ = CloseHandle(evt);
                        return Err(HidError::Timeout(key.to_string()));
                    }
                    if GetOverlappedResult(open.handle, &ov, &mut read, false).is_err() {
                        let _ = CloseHandle(evt);
                        return Err(HidError::Io(format!("Overlapped read failed on {key}")));
                    }
                }
                let _ = CloseHandle(evt);
                buf.truncate(read as usize);
            }
            Ok(buf)
        }

        fn set_feature(
            &mut self,
            key: &str,
            report_id: u8,
            payload: &[u8],
        ) -> Result<(), HidError> {
            if self.read_only_guard {
                return Err(HidError::ReadOnlyGuard(key.to_string()));
            }
            let open = self
                .handles
                .get(key)
                .ok_or_else(|| HidError::NotOpen(key.to_string()))?;
            let buf = build_padded_report(report_id, payload, open.feature_len);
            let ok =
                unsafe { HidD_SetFeature(open.handle, buf.as_ptr() as *const _, buf.len() as u32) };
            if ok {
                Ok(())
            } else {
                Err(HidError::Io(format!("HidD_SetFeature failed on {key}")))
            }
        }

        fn get_feature(
            &mut self,
            key: &str,
            report_id: u8,
            length: usize,
        ) -> Result<Vec<u8>, HidError> {
            let open = self
                .handles
                .get(key)
                .ok_or_else(|| HidError::NotOpen(key.to_string()))?;
            let mut buf = vec![0u8; length.max(open.feature_len).max(2)];
            buf[0] = report_id;
            let ok = unsafe {
                HidD_GetFeature(open.handle, buf.as_mut_ptr() as *mut _, buf.len() as u32)
            };
            if ok {
                Ok(buf)
            } else {
                Err(HidError::Io(format!("HidD_GetFeature failed on {key}")))
            }
        }
    }
}
