use std::collections::HashMap;

pub trait TokenVault {
    fn seal(&mut self, key: &str, plaintext: &[u8]) -> Result<Vec<u8>, String>;
    fn unseal(&self, key: &str, sealed: &[u8]) -> Result<Vec<u8>, String>;
}

#[derive(Debug, Default)]
pub struct MockMemoryVault {
    sealed_records: HashMap<String, Vec<u8>>,
}

impl MockMemoryVault {
    pub fn record_count(&self) -> usize {
        self.sealed_records.len()
    }
}

impl TokenVault for MockMemoryVault {
    fn seal(&mut self, key: &str, plaintext: &[u8]) -> Result<Vec<u8>, String> {
        let mut out = Vec::with_capacity(plaintext.len() + 4);
        out.extend_from_slice(b"MOCK");
        for (i, &b) in plaintext.iter().enumerate() {
            out.push(b ^ ((i as u8).wrapping_mul(31).wrapping_add(0x5A)));
        }
        self.sealed_records.insert(key.to_string(), out.clone());
        Ok(out)
    }

    fn unseal(&self, _key: &str, sealed: &[u8]) -> Result<Vec<u8>, String> {
        let payload = sealed
            .strip_prefix(b"MOCK")
            .ok_or_else(|| "Invalid mock vault blob".to_string())?;
        let mut out = Vec::with_capacity(payload.len());
        for (i, &b) in payload.iter().enumerate() {
            out.push(b ^ ((i as u8).wrapping_mul(31).wrapping_add(0x5A)));
        }
        Ok(out)
    }
}

#[cfg(windows)]
mod win_dpapi {
    use std::ptr::{null, null_mut};

    const CRYPTPROTECT_UI_FORBIDDEN: u32 = 0x1;

    #[repr(C)]
    struct DataBlob {
        cb_data: u32,
        pb_data: *mut u8,
    }

    #[link(name = "crypt32")]
    unsafe extern "system" {
        fn CryptProtectData(
            p_data_in: *const DataBlob,
            sz_data_descr: *const u16,
            p_optional_entropy: *const DataBlob,
            pv_reserved: *mut std::ffi::c_void,
            p_prompt_struct: *const std::ffi::c_void,
            dw_flags: u32,
            p_data_out: *mut DataBlob,
        ) -> i32;
        fn CryptUnprotectData(
            p_data_in: *const DataBlob,
            ppsz_data_descr: *mut *mut u16,
            p_optional_entropy: *const DataBlob,
            pv_reserved: *mut std::ffi::c_void,
            p_prompt_struct: *const std::ffi::c_void,
            dw_flags: u32,
            p_data_out: *mut DataBlob,
        ) -> i32;
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn LocalFree(h_mem: *mut std::ffi::c_void) -> *mut std::ffi::c_void;
    }

    pub fn protect(plaintext: &[u8], entropy: &[u8]) -> Result<Vec<u8>, String> {
        let in_blob = DataBlob {
            cb_data: plaintext.len() as u32,
            pb_data: plaintext.as_ptr() as *mut u8,
        };
        let ent_blob = DataBlob {
            cb_data: entropy.len() as u32,
            pb_data: entropy.as_ptr() as *mut u8,
        };
        let mut out_blob = DataBlob {
            cb_data: 0,
            pb_data: null_mut(),
        };
        let ok = unsafe {
            CryptProtectData(
                &in_blob,
                null(),
                if entropy.is_empty() {
                    null()
                } else {
                    &ent_blob
                },
                null_mut(),
                null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out_blob,
            )
        };
        if ok == 0 || out_blob.pb_data.is_null() {
            return Err("DPAPI CryptProtectData failed".into());
        }
        let bytes =
            unsafe { std::slice::from_raw_parts(out_blob.pb_data, out_blob.cb_data as usize) }
                .to_vec();
        unsafe {
            LocalFree(out_blob.pb_data.cast());
        }
        Ok(bytes)
    }

    pub fn unprotect(sealed: &[u8], entropy: &[u8]) -> Result<Vec<u8>, String> {
        let in_blob = DataBlob {
            cb_data: sealed.len() as u32,
            pb_data: sealed.as_ptr() as *mut u8,
        };
        let ent_blob = DataBlob {
            cb_data: entropy.len() as u32,
            pb_data: entropy.as_ptr() as *mut u8,
        };
        let mut out_blob = DataBlob {
            cb_data: 0,
            pb_data: null_mut(),
        };
        let ok = unsafe {
            CryptUnprotectData(
                &in_blob,
                null_mut(),
                if entropy.is_empty() {
                    null()
                } else {
                    &ent_blob
                },
                null_mut(),
                null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut out_blob,
            )
        };
        if ok == 0 || out_blob.pb_data.is_null() {
            return Err("DPAPI CryptUnprotectData failed".into());
        }
        let bytes =
            unsafe { std::slice::from_raw_parts(out_blob.pb_data, out_blob.cb_data as usize) }
                .to_vec();
        unsafe {
            LocalFree(out_blob.pb_data.cast());
        }
        Ok(bytes)
    }
}

#[derive(Debug, Default)]
pub struct DpapiVault;

impl TokenVault for DpapiVault {
    fn seal(&mut self, key: &str, plaintext: &[u8]) -> Result<Vec<u8>, String> {
        #[cfg(windows)]
        {
            win_dpapi::protect(plaintext, key.as_bytes())
        }
        #[cfg(not(windows))]
        {
            let _ = (key, plaintext);
            Err("DPAPI is only available on Windows".into())
        }
    }

    fn unseal(&self, key: &str, sealed: &[u8]) -> Result<Vec<u8>, String> {
        #[cfg(windows)]
        {
            win_dpapi::unprotect(sealed, key.as_bytes())
        }
        #[cfg(not(windows))]
        {
            let _ = (key, sealed);
            Err("DPAPI is only available on Windows".into())
        }
    }
}
