use windows::Win32::Foundation::FreeLibrary;
use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows::core::{PCSTR, w};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct NvencProbe {
    pub available: bool,
    pub major: u32,
    pub minor: u32,
}

type FnGetMaxSupportedVersion = unsafe extern "system" fn(*mut u32) -> i32;

pub fn probe_nvenc_runtime() -> NvencProbe {
    let lib = unsafe { LoadLibraryW(w!("nvEncodeAPI64.dll")) };
    let Ok(module) = lib else {
        return NvencProbe {
            available: false,
            major: 0,
            minor: 0,
        };
    };

    let proc = unsafe {
        GetProcAddress(
            module,
            PCSTR(c"NvEncodeAPIGetMaxSupportedVersion".as_ptr().cast()),
        )
    };
    let probe = if let Some(func) = proc {
        let get_ver: FnGetMaxSupportedVersion = unsafe { std::mem::transmute(func) };
        let mut version = 0u32;
        let status = unsafe { get_ver(&mut version) };
        if status == 0 {
            NvencProbe {
                available: true,
                major: version >> 4,
                minor: version & 0x0F,
            }
        } else {
            NvencProbe {
                available: false,
                major: 0,
                minor: 0,
            }
        }
    } else {
        NvencProbe {
            available: false,
            major: 0,
            minor: 0,
        }
    };

    unsafe {
        let _ = FreeLibrary(module);
    }
    probe
}
