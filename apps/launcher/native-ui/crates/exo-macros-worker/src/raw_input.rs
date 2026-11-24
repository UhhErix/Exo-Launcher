use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct RawDeviceIdentity {
    pub handle: usize,
    pub device_id: String,
    pub vid: Option<u16>,
    pub pid: Option<u16>,
    pub raw_path: String,
}

pub fn parse_raw_device_path(raw_path: &str) -> (String, Option<u16>, Option<u16>) {
    let upper = raw_path.to_ascii_uppercase();
    let vid = extract_hex_tag(&upper, "VID_");
    let pid = extract_hex_tag(&upper, "PID_");
    let device_id = match (vid, pid) {
        (Some(v), Some(p)) => format!("vid_{v:04x}_pid_{p:04x}"),
        _ => {
            let mut h: u64 = 0xcbf29ce484222325;
            for b in raw_path.as_bytes() {
                h ^= u64::from(*b);
                h = h.wrapping_mul(0x100000001b3);
            }
            format!("hid_{h:016x}")
        }
    };
    (device_id, vid, pid)
}

fn extract_hex_tag(upper: &str, tag: &str) -> Option<u16> {
    let idx = upper.find(tag)?;
    let slice = upper.get(idx + tag.len()..idx + tag.len() + 4)?;
    u16::from_str_radix(slice, 16).ok()
}

#[derive(Default)]
pub struct RawDeviceRegistry {
    by_handle: HashMap<usize, RawDeviceIdentity>,
}

impl RawDeviceRegistry {
    pub fn register_path(&mut self, handle: usize, raw_path: String) -> &RawDeviceIdentity {
        let (device_id, vid, pid) = parse_raw_device_path(&raw_path);
        self.by_handle.entry(handle).or_insert(RawDeviceIdentity {
            handle,
            device_id,
            vid,
            pid,
            raw_path,
        })
    }

    pub fn lookup(&self, handle: usize) -> Option<&RawDeviceIdentity> {
        self.by_handle.get(&handle)
    }

    pub fn len(&self) -> usize {
        self.by_handle.len()
    }

    #[cfg(windows)]
    pub fn query_device_name(handle: windows_sys::Win32::Foundation::HANDLE) -> Option<String> {
        use windows_sys::Win32::UI::Input::{GetRawInputDeviceInfoW, RIDI_DEVICENAME};

        let mut len: u32 = 0;
        let rc = unsafe {
            GetRawInputDeviceInfoW(handle, RIDI_DEVICENAME, std::ptr::null_mut(), &raw mut len)
        };
        if rc == u32::MAX || len == 0 {
            return None;
        }
        let mut buf = vec![0u16; len as usize];
        let written = unsafe {
            GetRawInputDeviceInfoW(
                handle,
                RIDI_DEVICENAME,
                buf.as_mut_ptr().cast(),
                &raw mut len,
            )
        };
        if written == u32::MAX {
            return None;
        }
        let end = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        Some(String::from_utf16_lossy(&buf[..end]))
    }

    #[cfg(windows)]
    pub fn register_input_sink(hwnd: windows_sys::Win32::Foundation::HWND) -> bool {
        use windows_sys::Win32::UI::Input::{
            RAWINPUTDEVICE, RIDEV_INPUTSINK, RegisterRawInputDevices,
        };

        let devices = [
            RAWINPUTDEVICE {
                usUsagePage: 0x01,
                usUsage: 0x06,
                dwFlags: RIDEV_INPUTSINK,
                hwndTarget: hwnd,
            },
            RAWINPUTDEVICE {
                usUsagePage: 0x01,
                usUsage: 0x02,
                dwFlags: RIDEV_INPUTSINK,
                hwndTarget: hwnd,
            },
        ];
        unsafe {
            RegisterRawInputDevices(
                devices.as_ptr(),
                devices.len() as u32,
                size_of::<RAWINPUTDEVICE>() as u32,
            ) != 0
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_usb_hid_vid_pid_paths() {
        let (id, vid, pid) =
            parse_raw_device_path(r"\\?\HID#VID_046D&PID_C094&MI_00#7&1a2b3c4d&0&0000#{378de44c}");
        assert_eq!(id, "vid_046d_pid_c094");
        assert_eq!(vid, Some(0x046D));
        assert_eq!(pid, Some(0xC094));

        let (id2, vid2, pid2) = parse_raw_device_path(r"\\?\ACPI#PNP0303#4&123456&0");
        assert!(id2.starts_with("hid_"));
        assert_eq!(vid2, None);
        assert_eq!(pid2, None);
    }
}
