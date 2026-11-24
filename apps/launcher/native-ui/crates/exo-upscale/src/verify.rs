use serde::{Deserialize, Serialize};
use std::fs::File;
use std::io::{BufReader, Read};
use std::path::Path;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Vendor {
    Nvidia,
    Amd,
    Intel,
}

impl Vendor {
    pub const fn expected_publisher(self) -> &'static str {
        match self {
            Self::Nvidia => "NVIDIA Corporation",
            Self::Amd => "Advanced Micro Devices, Inc.",
            Self::Intel => "Intel Corporation",
        }
    }

    pub fn matches_publisher(self, publisher: &str) -> bool {
        let p = publisher.trim();
        match self {
            Self::Nvidia => p.eq_ignore_ascii_case("NVIDIA Corporation"),
            Self::Amd => {
                p.eq_ignore_ascii_case("Advanced Micro Devices, Inc.")
                    || p.eq_ignore_ascii_case("Advanced Micro Devices Inc.")
            }
            Self::Intel => p.eq_ignore_ascii_case("Intel Corporation"),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DllVerification {
    pub path: String,
    pub size_bytes: u64,
    pub blake3: String,
    pub sha256: String,
    pub file_version: Option<String>,
    pub publisher: Option<String>,
    pub authenticode_valid: bool,
    pub win_verify_status: i32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum VerifyError {
    Io(String),
    SignatureInvalid {
        status: i32,
        detail: String,
    },
    UntrustedPublisher {
        expected: String,
        actual: Option<String>,
    },
    HashMismatch {
        expected: String,
        actual: String,
    },
}

impl std::fmt::Display for VerifyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(e) => write!(f, "io error: {e}"),
            Self::SignatureInvalid { status, detail } => {
                write!(
                    f,
                    "Authenticode signature invalid (0x{:08X}): {detail}",
                    *status as u32
                )
            }
            Self::UntrustedPublisher { expected, actual } => write!(
                f,
                "untrusted publisher: expected '{expected}', got '{}'",
                actual.as_deref().unwrap_or("<unsigned>")
            ),
            Self::HashMismatch { expected, actual } => {
                write!(f, "BLAKE3 hash mismatch: expected {expected}, got {actual}")
            }
        }
    }
}

impl std::error::Error for VerifyError {}

pub fn hash_file_blake3_and_sha256(path: &Path) -> Result<(u64, String, String), VerifyError> {
    let file = File::open(path).map_err(|e| VerifyError::Io(format!("{}: {e}", path.display())))?;
    let mut reader = BufReader::with_capacity(64 * 1024, file);
    let mut b3 = blake3::Hasher::new();
    let mut s256 = Sha256::new();
    let mut buf = [0u8; 64 * 1024];
    let mut total = 0u64;
    loop {
        let n = reader
            .read(&mut buf)
            .map_err(|e| VerifyError::Io(format!("{}: {e}", path.display())))?;
        if n == 0 {
            break;
        }
        total += n as u64;
        b3.update(&buf[..n]);
        s256.update(&buf[..n]);
    }
    Ok((
        total,
        b3.finalize().to_hex().to_string(),
        s256.finalize_hex(),
    ))
}

pub fn hash_file_blake3(path: &Path) -> Result<String, VerifyError> {
    let (_, b3, _) = hash_file_blake3_and_sha256(path)?;
    Ok(b3)
}

pub fn inspect_dll(path: &Path) -> Result<DllVerification, VerifyError> {
    let (size_bytes, blake3, sha256) = hash_file_blake3_and_sha256(path)?;
    let file_version = read_pe_file_version(path);
    let (authenticode_valid, win_verify_status, publisher) = verify_authenticode_windows(path);
    Ok(DllVerification {
        path: path.display().to_string(),
        size_bytes,
        blake3,
        sha256,
        file_version,
        publisher,
        authenticode_valid,
        win_verify_status,
    })
}

pub fn verify_vendor_dll(
    path: &Path,
    vendor: Vendor,
    expected_blake3: Option<&str>,
) -> Result<DllVerification, VerifyError> {
    let info = inspect_dll(path)?;
    if let Some(expected) = expected_blake3
        && !info.blake3.eq_ignore_ascii_case(expected)
    {
        return Err(VerifyError::HashMismatch {
            expected: expected.to_string(),
            actual: info.blake3,
        });
    }
    if !info.authenticode_valid {
        return Err(VerifyError::SignatureInvalid {
            status: info.win_verify_status,
            detail: format!("WinVerifyTrust rejected {}", path.display()),
        });
    }
    let publisher_ok = info
        .publisher
        .as_deref()
        .is_some_and(|p| vendor.matches_publisher(p));
    if !publisher_ok {
        return Err(VerifyError::UntrustedPublisher {
            expected: vendor.expected_publisher().to_string(),
            actual: info.publisher,
        });
    }
    Ok(info)
}

#[cfg(windows)]
pub fn read_pe_file_version(path: &Path) -> Option<String> {
    use std::ffi::c_void;
    use windows::Win32::Storage::FileSystem::{
        GetFileVersionInfoSizeW, GetFileVersionInfoW, VS_FIXEDFILEINFO, VerQueryValueW,
    };
    use windows::core::{HSTRING, PCWSTR, w};

    let wide = HSTRING::from(path.as_os_str());
    let mut handle = 0u32;
    let size =
        unsafe { GetFileVersionInfoSizeW(PCWSTR::from_raw(wide.as_ptr()), Some(&mut handle)) };
    if size == 0 {
        return None;
    }
    let mut buf = vec![0u8; size as usize];
    unsafe {
        GetFileVersionInfoW(
            PCWSTR::from_raw(wide.as_ptr()),
            Some(0),
            size,
            buf.as_mut_ptr().cast(),
        )
        .ok()?;
    }
    let mut ptr: *mut c_void = std::ptr::null_mut();
    let mut len = 0u32;
    let ok = unsafe { VerQueryValueW(buf.as_ptr().cast(), w!("\\"), &mut ptr, &mut len).as_bool() };
    if !ok || ptr.is_null() || (len as usize) < std::mem::size_of::<VS_FIXEDFILEINFO>() {
        return None;
    }
    let ffi = unsafe { *(ptr as *const VS_FIXEDFILEINFO) };
    if ffi.dwSignature != 0xFEEF_04BD {
        return None;
    }
    let major = (ffi.dwFileVersionMS >> 16) & 0xFFFF;
    let minor = ffi.dwFileVersionMS & 0xFFFF;
    let build = (ffi.dwFileVersionLS >> 16) & 0xFFFF;
    let rev = ffi.dwFileVersionLS & 0xFFFF;
    Some(format!("{major}.{minor}.{build}.{rev}"))
}

#[cfg(not(windows))]
pub fn read_pe_file_version(_path: &Path) -> Option<String> {
    None
}

#[cfg(windows)]
fn verify_authenticode_windows(path: &Path) -> (bool, i32, Option<String>) {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::Security::WinTrust::{
        WINTRUST_ACTION_GENERIC_VERIFY_V2, WINTRUST_DATA, WINTRUST_DATA_0, WINTRUST_FILE_INFO,
        WTD_CHOICE_FILE, WTD_REVOKE_NONE, WTD_STATEACTION_CLOSE, WTD_STATEACTION_VERIFY,
        WTD_UI_NONE, WinVerifyTrust,
    };
    use windows::core::{HSTRING, PCWSTR};

    let wide = HSTRING::from(path.as_os_str());
    let mut file_info = WINTRUST_FILE_INFO {
        cbStruct: std::mem::size_of::<WINTRUST_FILE_INFO>() as u32,
        pcwszFilePath: PCWSTR::from_raw(wide.as_ptr()),
        ..Default::default()
    };
    let mut trust_data = WINTRUST_DATA {
        cbStruct: std::mem::size_of::<WINTRUST_DATA>() as u32,
        dwUIChoice: WTD_UI_NONE,
        fdwRevocationChecks: WTD_REVOKE_NONE,
        dwUnionChoice: WTD_CHOICE_FILE,
        Anonymous: WINTRUST_DATA_0 {
            pFile: &mut file_info,
        },
        dwStateAction: WTD_STATEACTION_VERIFY,
        ..Default::default()
    };
    let mut action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
    let status = unsafe {
        WinVerifyTrust(
            HWND::default(),
            &mut action,
            (&mut trust_data as *mut WINTRUST_DATA).cast(),
        )
    };
    trust_data.dwStateAction = WTD_STATEACTION_CLOSE;
    unsafe {
        let _ = WinVerifyTrust(
            HWND::default(),
            &mut action,
            (&mut trust_data as *mut WINTRUST_DATA).cast(),
        );
    }

    let publisher = extract_embedded_signer_name(&wide);
    (status == 0, status, publisher)
}

#[cfg(windows)]
fn extract_embedded_signer_name(wide: &windows::core::HSTRING) -> Option<String> {
    use std::ffi::c_void;
    use windows::Win32::Security::Cryptography::{
        CERT_FIND_SUBJECT_CERT, CERT_INFO, CERT_NAME_SIMPLE_DISPLAY_TYPE,
        CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED, CERT_QUERY_FORMAT_FLAG_BINARY,
        CERT_QUERY_OBJECT_FILE, CMSG_SIGNER_INFO, CMSG_SIGNER_INFO_PARAM, CertCloseStore,
        CertFindCertificateInStore, CertFreeCertificateContext, CertGetNameStringW, CryptMsgClose,
        CryptMsgGetParam, CryptQueryObject, HCERTSTORE, PKCS_7_ASN_ENCODING, X509_ASN_ENCODING,
    };

    let mut store = HCERTSTORE::default();
    let mut msg: *mut c_void = std::ptr::null_mut();
    let ok = unsafe {
        CryptQueryObject(
            CERT_QUERY_OBJECT_FILE,
            wide.as_ptr().cast(),
            CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
            CERT_QUERY_FORMAT_FLAG_BINARY,
            0,
            None,
            None,
            None,
            Some(&mut store),
            Some(&mut msg),
            None,
        )
        .is_ok()
    };
    if !ok || store.is_invalid() || msg.is_null() {
        return None;
    }

    let mut signer_size = 0u32;
    let got_size =
        unsafe { CryptMsgGetParam(msg, CMSG_SIGNER_INFO_PARAM, 0, None, &mut signer_size).is_ok() };
    let mut result = None;
    if got_size && signer_size > 0 {
        let mut buf = vec![0u8; signer_size as usize];
        if unsafe {
            CryptMsgGetParam(
                msg,
                CMSG_SIGNER_INFO_PARAM,
                0,
                Some(buf.as_mut_ptr().cast()),
                &mut signer_size,
            )
            .is_ok()
        } {
            let signer = unsafe { &*(buf.as_ptr() as *const CMSG_SIGNER_INFO) };
            let cert_info = CERT_INFO {
                Issuer: signer.Issuer,
                SerialNumber: signer.SerialNumber,
                ..Default::default()
            };
            let cert = unsafe {
                CertFindCertificateInStore(
                    store,
                    X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                    0,
                    CERT_FIND_SUBJECT_CERT,
                    Some((&cert_info as *const CERT_INFO).cast()),
                    None,
                )
            };
            if !cert.is_null() {
                let mut name_buf = [0u16; 256];
                let chars = unsafe {
                    CertGetNameStringW(
                        cert,
                        CERT_NAME_SIMPLE_DISPLAY_TYPE,
                        0,
                        None,
                        Some(&mut name_buf),
                    )
                };
                if chars > 1 {
                    let len = (chars as usize).saturating_sub(1).min(name_buf.len());
                    result = Some(String::from_utf16_lossy(&name_buf[..len]));
                }
                unsafe {
                    let _ = CertFreeCertificateContext(Some(cert));
                }
            }
        }
    }
    unsafe {
        let _ = CryptMsgClose(Some(msg));
        let _ = CertCloseStore(Some(store), 0);
    }
    result
}

#[cfg(not(windows))]
fn verify_authenticode_windows(_path: &Path) -> (bool, i32, Option<String>) {
    (false, -1, None)
}

struct Sha256 {
    state: [u32; 8],
    buf: [u8; 64],
    buf_len: usize,
    total_bits: u64,
}

impl Sha256 {
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];

    fn new() -> Self {
        Self {
            state: [
                0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
                0x5be0cd19,
            ],
            buf: [0u8; 64],
            buf_len: 0,
            total_bits: 0,
        }
    }

    fn update(&mut self, mut data: &[u8]) {
        self.total_bits = self.total_bits.wrapping_add((data.len() as u64) * 8);
        if self.buf_len > 0 {
            let need = 64 - self.buf_len;
            let take = need.min(data.len());
            self.buf[self.buf_len..self.buf_len + take].copy_from_slice(&data[..take]);
            self.buf_len += take;
            data = &data[take..];
            if self.buf_len == 64 {
                let block = self.buf;
                self.compress(&block);
                self.buf_len = 0;
            }
        }
        while data.len() >= 64 {
            let mut block = [0u8; 64];
            block.copy_from_slice(&data[..64]);
            self.compress(&block);
            data = &data[64..];
        }
        if !data.is_empty() {
            self.buf[..data.len()].copy_from_slice(data);
            self.buf_len = data.len();
        }
    }

    fn compress(&mut self, block: &[u8; 64]) {
        let mut w = [0u32; 64];
        for i in 0..16 {
            w[i] = u32::from_be_bytes([
                block[i * 4],
                block[i * 4 + 1],
                block[i * 4 + 2],
                block[i * 4 + 3],
            ]);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }
        let [mut a, mut b, mut c, mut d, mut e, mut f, mut g, mut h] = self.state;
        for (i, &wi) in w.iter().enumerate() {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let t1 = h
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(Self::K[i])
                .wrapping_add(wi);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let t2 = s0.wrapping_add(maj);
            h = g;
            g = f;
            f = e;
            e = d.wrapping_add(t1);
            d = c;
            c = b;
            b = a;
            a = t1.wrapping_add(t2);
        }
        let vals = [a, b, c, d, e, f, g, h];
        for (s, v) in self.state.iter_mut().zip(vals) {
            *s = s.wrapping_add(v);
        }
    }

    fn finalize_hex(mut self) -> String {
        self.buf[self.buf_len] = 0x80;
        self.buf_len += 1;
        if self.buf_len > 56 {
            for b in &mut self.buf[self.buf_len..64] {
                *b = 0;
            }
            let block = self.buf;
            self.compress(&block);
            self.buf = [0u8; 64];
            self.buf_len = 0;
        } else {
            for b in &mut self.buf[self.buf_len..56] {
                *b = 0;
            }
        }
        self.buf[56..64].copy_from_slice(&self.total_bits.to_be_bytes());
        let block = self.buf;
        self.compress(&block);
        let mut out = String::with_capacity(64);
        for word in self.state {
            use std::fmt::Write;
            let _ = write!(&mut out, "{word:08x}");
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn pick_real_signed_dll() -> Option<PathBuf> {
        let candidates = [
            r"C:\Windows\System32\nvapi64.dll",
            r"C:\Windows\System32\nvofapi64.dll",
            r"C:\Windows\System32\d3d12.dll",
            r"C:\Windows\System32\kernel32.dll",
        ];
        candidates.iter().map(PathBuf::from).find(|p| p.exists())
    }

    #[test]
    fn sha256_matches_nist_test_vector() {
        let mut h = Sha256::new();
        h.update(b"abc");
        assert_eq!(
            h.finalize_hex(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn authenticode_rejects_one_byte_tampered_dll_and_wrong_publisher() {
        let Some(src) = pick_real_signed_dll() else {
            return;
        };
        let orig = inspect_dll(&src).expect("inspect real signed dll");
        assert!(orig.file_version.is_some());
        assert_eq!(orig.blake3.len(), 64);
        assert_eq!(orig.sha256.len(), 64);

        let tmp_dir =
            std::env::temp_dir().join(format!("exo-upscale-tamper-{}", std::process::id()));
        let _ = std::fs::create_dir_all(&tmp_dir);
        let copy_path = tmp_dir.join("tampered.dll");
        let mut bytes = std::fs::read(&src).expect("read signed dll");
        std::fs::write(&copy_path, &bytes).expect("write clean copy");

        let half = bytes.len() / 2;
        if let Some(mid) = bytes.get_mut(half) {
            *mid ^= 0xFF;
        }
        std::fs::write(&copy_path, &bytes).expect("write tampered copy");
        let tampered = inspect_dll(&copy_path).expect("inspect tampered dll");
        assert!(!tampered.authenticode_valid);

        let err = verify_vendor_dll(&copy_path, Vendor::Nvidia, None).unwrap_err();
        assert!(matches!(err, VerifyError::SignatureInvalid { .. }));

        let d3d12 = PathBuf::from(r"C:\Windows\System32\d3d12.dll");
        if d3d12.exists() {
            let ms_err = verify_vendor_dll(&d3d12, Vendor::Nvidia, None).unwrap_err();
            assert!(
                matches!(
                    ms_err,
                    VerifyError::UntrustedPublisher { .. } | VerifyError::SignatureInvalid { .. }
                ),
                "unexpected error: {ms_err:?}"
            );
        }

        let _ = std::fs::remove_dir_all(&tmp_dir);
    }

    #[test]
    fn real_nvidia_system_dll_passes_vendor_publisher_check_when_present() {
        let nvapi = PathBuf::from(r"C:\Windows\System32\nvapi64.dll");
        if !nvapi.exists() {
            return;
        }
        let info = verify_vendor_dll(&nvapi, Vendor::Nvidia, None)
            .expect("nvapi64.dll should pass NVIDIA WinVerifyTrust");
        assert!(info.authenticode_valid);
        assert!(
            info.publisher
                .as_deref()
                .is_some_and(|p| Vendor::Nvidia.matches_publisher(p))
        );
        assert!(info.file_version.is_some());
    }
}
