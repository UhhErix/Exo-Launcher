use crate::InstallError;
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::Path;

pub const ALLOWED_UPDATE_HOST: &str = "updates.getexo.app";
pub const MAX_DOWNLOAD_BYTES: u64 = 512 * 1024 * 1024;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParsedUrl {
    pub https: bool,
    pub host: String,
    pub port: u16,
    pub path: String,
}

pub fn parse_and_validate_url(url: &str, allow_loopback: bool) -> Result<ParsedUrl, InstallError> {
    let (https, rest) = if let Some(r) = url.strip_prefix("https://") {
        (true, r)
    } else if allow_loopback && let Some(r) = url.strip_prefix("http://") {
        (false, r)
    } else {
        return Err(InstallError::SafetyRefusal(format!(
            "unsupported update url scheme: {url}"
        )));
    };

    let (authority, path) = match rest.find('/') {
        Some(idx) => (&rest[..idx], &rest[idx..]),
        None => (rest, "/"),
    };
    if authority.contains('@') || path.contains("..") {
        return Err(InstallError::SafetyRefusal(format!(
            "unsafe update url: {url}"
        )));
    }
    let (host, port) = match authority.rsplit_once(':') {
        Some((h, p)) => {
            let port = p
                .parse::<u16>()
                .map_err(|_| InstallError::InvalidArg("invalid url port".into()))?;
            (h.to_ascii_lowercase(), port)
        }
        None => (authority.to_ascii_lowercase(), if https { 443 } else { 80 }),
    };

    let allowed = (https && host == ALLOWED_UPDATE_HOST && port == 443)
        || (allow_loopback && host == "127.0.0.1");
    if !allowed {
        return Err(InstallError::SafetyRefusal(format!(
            "disallowed update host: {host}:{port}"
        )));
    }

    Ok(ParsedUrl {
        https,
        host,
        port,
        path: path.to_string(),
    })
}

#[cfg(windows)]
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
pub fn download_resumable_to_file(
    url: &str,
    dest: &Path,
    allow_loopback: bool,
) -> Result<[u8; 32], InstallError> {
    use windows::Win32::Networking::WinHttp::{
        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_ADDREQ_FLAG_ADD, WINHTTP_ADDREQ_FLAG_REPLACE,
        WINHTTP_FLAG_SECURE, WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_QUERY_STATUS_CODE,
        WinHttpAddRequestHeaders, WinHttpCloseHandle, WinHttpConnect, WinHttpOpen,
        WinHttpOpenRequest, WinHttpQueryDataAvailable, WinHttpQueryHeaders, WinHttpReadData,
        WinHttpReceiveResponse, WinHttpSendRequest,
    };
    use windows::core::PCWSTR;

    let parsed = parse_and_validate_url(url, allow_loopback)?;
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent)?;
    }

    let existing_len = dest.metadata().map(|m| m.len()).unwrap_or(0);

    let ua = to_wide("ExoInstaller/4.0");
    let session = unsafe {
        WinHttpOpen(
            PCWSTR(ua.as_ptr()),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            PCWSTR::null(),
            PCWSTR::null(),
            0,
        )
    };
    if session.is_null() {
        return Err(InstallError::Network("WinHttpOpen failed".into()));
    }

    let host_w = to_wide(&parsed.host);
    let conn = unsafe { WinHttpConnect(session, PCWSTR(host_w.as_ptr()), parsed.port, 0) };
    if conn.is_null() {
        unsafe {
            let _ = WinHttpCloseHandle(session);
        }
        return Err(InstallError::Network("WinHttpConnect failed".into()));
    }

    let verb_w = to_wide("GET");
    let path_w = to_wide(&parsed.path);
    let flags = if parsed.https {
        WINHTTP_FLAG_SECURE
    } else {
        Default::default()
    };
    let req = unsafe {
        WinHttpOpenRequest(
            conn,
            PCWSTR(verb_w.as_ptr()),
            PCWSTR(path_w.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            std::ptr::null(),
            flags,
        )
    };
    if req.is_null() {
        unsafe {
            let _ = WinHttpCloseHandle(conn);
            let _ = WinHttpCloseHandle(session);
        }
        return Err(InstallError::Network("WinHttpOpenRequest failed".into()));
    }

    let res = (|| -> Result<[u8; 32], InstallError> {
        if existing_len > 0 {
            let hdr = to_wide(&format!("Range: bytes={existing_len}-\r\n"));
            let _ = unsafe {
                WinHttpAddRequestHeaders(
                    req,
                    &hdr[..hdr.len() - 1],
                    WINHTTP_ADDREQ_FLAG_ADD | WINHTTP_ADDREQ_FLAG_REPLACE,
                )
            };
        }

        unsafe {
            WinHttpSendRequest(req, None, None, 0, 0, 0)
                .map_err(|e| InstallError::Network(format!("WinHttpSendRequest: {e}")))?;
            WinHttpReceiveResponse(req, std::ptr::null_mut())
                .map_err(|e| InstallError::Network(format!("WinHttpReceiveResponse: {e}")))?;
        }

        let mut status: u32 = 0;
        let mut status_len = std::mem::size_of::<u32>() as u32;
        unsafe {
            WinHttpQueryHeaders(
                req,
                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                PCWSTR::null(),
                Some(&mut status as *mut u32 as *mut core::ffi::c_void),
                &mut status_len,
                std::ptr::null_mut(),
            )
            .map_err(|e| InstallError::Network(format!("WinHttpQueryHeaders: {e}")))?;
        }

        let mut file = match status {
            206 if existing_len > 0 => OpenOptions::new().create(true).append(true).open(dest)?,
            200 => OpenOptions::new()
                .create(true)
                .write(true)
                .truncate(true)
                .open(dest)?,
            other => {
                return Err(InstallError::Network(format!(
                    "unexpected HTTP status {other}"
                )));
            }
        };

        let mut total_written = if status == 206 { existing_len } else { 0 };
        let mut buf = vec![0u8; 64 * 1024];
        loop {
            let mut avail = 0u32;
            unsafe {
                WinHttpQueryDataAvailable(req, &mut avail).map_err(|e| {
                    InstallError::Network(format!("WinHttpQueryDataAvailable: {e}"))
                })?;
            }
            if avail == 0 {
                break;
            }
            let want = (avail as usize).min(buf.len()) as u32;
            let mut got = 0u32;
            unsafe {
                WinHttpReadData(
                    req,
                    buf.as_mut_ptr() as *mut core::ffi::c_void,
                    want,
                    &mut got,
                )
                .map_err(|e| InstallError::Network(format!("WinHttpReadData: {e}")))?;
            }
            if got == 0 {
                break;
            }
            total_written += got as u64;
            if total_written > MAX_DOWNLOAD_BYTES {
                return Err(InstallError::Network("download exceeded size limit".into()));
            }
            file.write_all(&buf[..got as usize])?;
        }
        file.flush()?;
        drop(file);
        let (digest, _) = exo_delta::hash_file_streaming(dest)?;
        Ok(digest)
    })();

    unsafe {
        let _ = WinHttpCloseHandle(req);
        let _ = WinHttpCloseHandle(conn);
        let _ = WinHttpCloseHandle(session);
    }
    res
}

#[cfg(not(windows))]
pub fn download_resumable_to_file(
    url: &str,
    _dest: &Path,
    allow_loopback: bool,
) -> Result<[u8; 32], InstallError> {
    let _ = parse_and_validate_url(url, allow_loopback)?;
    Err(InstallError::Network(
        "WinHTTP only available on Windows".into(),
    ))
}
