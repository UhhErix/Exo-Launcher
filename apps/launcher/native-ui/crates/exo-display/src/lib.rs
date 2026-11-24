pub mod ddc_ci;
pub mod edid;
pub mod edid_override;
pub mod hdr_auto;
pub mod mhc2_icc;

use edid::{ParsedEdid, parse_edid};
use serde::{Deserialize, Serialize};
use windows::Win32::System::Registry::{
    HKEY, HKEY_LOCAL_MACHINE, KEY_READ, REG_VALUE_TYPE, RegCloseKey, RegEnumKeyExW, RegOpenKeyExW,
    RegQueryValueExW,
};
use windows::core::{PCWSTR, PWSTR};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LiveMonitorEdid {
    pub registry_key: String,
    pub raw_bytes: usize,
    pub parsed: ParsedEdid,
}

fn to_wide(s: &str) -> Vec<u16> {
    let mut w: Vec<u16> = s.encode_utf16().collect();
    w.push(0);
    w
}

fn enum_subkeys(parent: HKEY) -> Vec<String> {
    let mut out = Vec::new();
    let mut idx = 0u32;
    let mut buf = vec![0u16; 256];
    loop {
        let mut len = buf.len() as u32;
        let err = unsafe {
            RegEnumKeyExW(
                parent,
                idx,
                Some(PWSTR(buf.as_mut_ptr())),
                &mut len,
                None,
                None,
                None,
                None,
            )
        };
        if err.is_err() {
            break;
        }
        out.push(String::from_utf16_lossy(&buf[..len as usize]));
        idx += 1;
    }
    out
}

pub fn read_live_monitors_readonly() -> Vec<LiveMonitorEdid> {
    let mut found = Vec::new();
    let root_path = to_wide(r"SYSTEM\CurrentControlSet\Enum\DISPLAY");
    let mut root_key = HKEY::default();
    if unsafe {
        RegOpenKeyExW(
            HKEY_LOCAL_MACHINE,
            PCWSTR(root_path.as_ptr()),
            Some(0),
            KEY_READ,
            &mut root_key,
        )
    }
    .is_err()
    {
        return found;
    }
    for model in enum_subkeys(root_key) {
        let model_w = to_wide(&model);
        let mut model_key = HKEY::default();
        if unsafe {
            RegOpenKeyExW(
                root_key,
                PCWSTR(model_w.as_ptr()),
                Some(0),
                KEY_READ,
                &mut model_key,
            )
        }
        .is_err()
        {
            continue;
        }
        for inst in enum_subkeys(model_key) {
            let dp = to_wide(&format!(r"{inst}\Device Parameters"));
            let mut dp_key = HKEY::default();
            if unsafe {
                RegOpenKeyExW(
                    model_key,
                    PCWSTR(dp.as_ptr()),
                    Some(0),
                    KEY_READ,
                    &mut dp_key,
                )
            }
            .is_err()
            {
                continue;
            }
            let edid_w = to_wide("EDID");
            let mut ty = REG_VALUE_TYPE::default();
            let mut size = 0u32;
            if unsafe {
                RegQueryValueExW(
                    dp_key,
                    PCWSTR(edid_w.as_ptr()),
                    None,
                    Some(&mut ty),
                    None,
                    Some(&mut size),
                )
            }
            .is_ok()
                && (128..=2048).contains(&size)
            {
                let mut buf = vec![0u8; size as usize];
                if unsafe {
                    RegQueryValueExW(
                        dp_key,
                        PCWSTR(edid_w.as_ptr()),
                        None,
                        Some(&mut ty),
                        Some(buf.as_mut_ptr()),
                        Some(&mut size),
                    )
                }
                .is_ok()
                    && let Ok(parsed) = parse_edid(&buf[..size as usize])
                {
                    found.push(LiveMonitorEdid {
                        registry_key: format!(r"DISPLAY\{model}\{inst}"),
                        raw_bytes: size as usize,
                        parsed,
                    });
                }
            }
            unsafe {
                let _ = RegCloseKey(dp_key);
            }
        }
        unsafe {
            let _ = RegCloseKey(model_key);
        }
    }
    unsafe {
        let _ = RegCloseKey(root_key);
    }
    found
}
