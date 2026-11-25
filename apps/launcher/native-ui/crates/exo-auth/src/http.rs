#[cfg(windows)]
use std::ffi::c_void;
#[cfg(windows)]
use std::ptr;
#[cfg(windows)]
use windows::Win32::Networking::WinHttp::{
    WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_DISABLE_REDIRECTS, WINHTTP_FLAG_SECURE,
    WINHTTP_OPEN_REQUEST_FLAGS, WINHTTP_OPTION_DISABLE_FEATURE,
    WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, WINHTTP_QUERY_FLAG_NUMBER,
    WINHTTP_QUERY_RAW_HEADERS_CRLF, WINHTTP_QUERY_STATUS_CODE,
    WINHTTP_WEB_SOCKET_BINARY_FRAGMENT_BUFFER_TYPE, WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE,
    WINHTTP_WEB_SOCKET_BUFFER_TYPE, WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE,
    WINHTTP_WEB_SOCKET_UTF8_FRAGMENT_BUFFER_TYPE, WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE,
    WinHttpCloseHandle, WinHttpConnect, WinHttpOpen, WinHttpOpenRequest, WinHttpQueryHeaders,
    WinHttpReadData, WinHttpReceiveResponse, WinHttpSendRequest, WinHttpSetOption,
    WinHttpWebSocketClose, WinHttpWebSocketCompleteUpgrade, WinHttpWebSocketReceive,
    WinHttpWebSocketSend,
};
#[cfg(windows)]
use windows::Win32::UI::Shell::ShellExecuteW;
#[cfg(windows)]
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
#[cfg(windows)]
use windows::core::{PCWSTR, w};

pub struct HttpResponse {
    pub status: u16,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl HttpResponse {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(name))
            .map(|(_, v)| v.as_str())
    }
}

#[cfg(windows)]
struct ParsedUrl {
    secure: bool,
    host: String,
    port: u16,
    path_and_query: String,
}

#[cfg(windows)]
fn parse_url(url: &str) -> Result<ParsedUrl, String> {
    let (secure, default_port, rest) = if let Some(r) = url.strip_prefix("https://") {
        (true, 443u16, r)
    } else if let Some(r) = url.strip_prefix("http://") {
        (false, 80u16, r)
    } else if let Some(r) = url.strip_prefix("wss://") {
        (true, 443u16, r)
    } else if let Some(r) = url.strip_prefix("ws://") {
        (false, 80u16, r)
    } else {
        return Err("unsupported URL scheme".to_string());
    };
    let (authority, path_and_query) = match rest.find('/') {
        Some(idx) => (&rest[..idx], rest[idx..].to_string()),
        None => (rest, "/".to_string()),
    };
    let (host, port) = match authority.rfind(':') {
        Some(idx) => {
            let p = authority[idx + 1..]
                .parse::<u16>()
                .map_err(|_| "invalid port".to_string())?;
            (authority[..idx].to_string(), p)
        }
        None => (authority.to_string(), default_port),
    };
    Ok(ParsedUrl {
        secure,
        host,
        port,
        path_and_query,
    })
}

#[cfg(windows)]
fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(windows)]
pub fn http_request(
    method: &str,
    url: &str,
    headers: &[(&str, &str)],
    body: Option<&[u8]>,
    follow_redirects: bool,
) -> Result<HttpResponse, String> {
    let parsed = parse_url(url)?;
    let agent = to_wide("ExoLauncher/1.0");
    let host_w = to_wide(&parsed.host);
    let method_w = to_wide(method);
    let path_w = to_wide(&parsed.path_and_query);

    unsafe {
        let session = WinHttpOpen(
            PCWSTR(agent.as_ptr()),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            PCWSTR::null(),
            PCWSTR::null(),
            0,
        );
        if session.is_null() {
            return Err("WinHttpOpen failed".to_string());
        }
        let connect = WinHttpConnect(session, PCWSTR(host_w.as_ptr()), parsed.port, 0);
        if connect.is_null() {
            let _ = WinHttpCloseHandle(session);
            return Err("WinHttpConnect failed".to_string());
        }
        let flags = if parsed.secure {
            WINHTTP_FLAG_SECURE
        } else {
            WINHTTP_OPEN_REQUEST_FLAGS(0)
        };
        let request = WinHttpOpenRequest(
            connect,
            PCWSTR(method_w.as_ptr()),
            PCWSTR(path_w.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            ptr::null(),
            flags,
        );
        if request.is_null() {
            let _ = WinHttpCloseHandle(connect);
            let _ = WinHttpCloseHandle(session);
            return Err("WinHttpOpenRequest failed".to_string());
        }

        if !follow_redirects {
            let disable = WINHTTP_DISABLE_REDIRECTS.to_ne_bytes();
            let _ = WinHttpSetOption(
                Some(request),
                WINHTTP_OPTION_DISABLE_FEATURE,
                Some(&disable),
            );
        }

        let mut hdr_text = String::new();
        for (k, v) in headers {
            hdr_text.push_str(k);
            hdr_text.push_str(": ");
            hdr_text.push_str(v);
            hdr_text.push_str("\r\n");
        }
        let hdr_wide: Vec<u16> = hdr_text.encode_utf16().collect();
        let hdr_opt = if hdr_wide.is_empty() {
            None
        } else {
            Some(hdr_wide.as_slice())
        };
        let (opt_ptr, opt_len) = match body {
            Some(b) if !b.is_empty() => (Some(b.as_ptr() as *const c_void), b.len() as u32),
            _ => (None, 0u32),
        };

        if let Err(e) = WinHttpSendRequest(request, hdr_opt, opt_ptr, opt_len, opt_len, 0) {
            let _ = WinHttpCloseHandle(request);
            let _ = WinHttpCloseHandle(connect);
            let _ = WinHttpCloseHandle(session);
            return Err(format!("WinHttpSendRequest failed: {e}"));
        }
        if let Err(e) = WinHttpReceiveResponse(request, ptr::null_mut()) {
            let _ = WinHttpCloseHandle(request);
            let _ = WinHttpCloseHandle(connect);
            let _ = WinHttpCloseHandle(session);
            return Err(format!("WinHttpReceiveResponse failed: {e}"));
        }

        let mut status_code = 0u32;
        let mut status_len = std::mem::size_of::<u32>() as u32;
        let _ = WinHttpQueryHeaders(
            request,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            PCWSTR::null(),
            Some(&mut status_code as *mut u32 as *mut c_void),
            &mut status_len,
            ptr::null_mut(),
        );

        let mut raw_hdr_len = 0u32;
        let _ = WinHttpQueryHeaders(
            request,
            WINHTTP_QUERY_RAW_HEADERS_CRLF,
            PCWSTR::null(),
            None,
            &mut raw_hdr_len,
            ptr::null_mut(),
        );
        let mut parsed_headers = Vec::new();
        if raw_hdr_len > 0 {
            let mut buf = vec![0u16; (raw_hdr_len as usize / 2) + 1];
            let mut actual = raw_hdr_len;
            if WinHttpQueryHeaders(
                request,
                WINHTTP_QUERY_RAW_HEADERS_CRLF,
                PCWSTR::null(),
                Some(buf.as_mut_ptr() as *mut c_void),
                &mut actual,
                ptr::null_mut(),
            )
            .is_ok()
            {
                let count = (actual as usize / 2).min(buf.len());
                let text = String::from_utf16_lossy(&buf[..count]);
                for line in text.split("\r\n").skip(1) {
                    if let Some((k, v)) = line.split_once(':') {
                        parsed_headers.push((k.trim().to_string(), v.trim().to_string()));
                    }
                }
            }
        }

        let mut body_out = Vec::new();
        let mut chunk = [0u8; 8192];
        loop {
            let mut read = 0u32;
            if WinHttpReadData(
                request,
                chunk.as_mut_ptr() as *mut c_void,
                chunk.len() as u32,
                &mut read,
            )
            .is_err()
            {
                break;
            }
            if read == 0 {
                break;
            }
            body_out.extend_from_slice(&chunk[..read as usize]);
            if body_out.len() > 4 * 1024 * 1024 {
                break;
            }
        }

        let _ = WinHttpCloseHandle(request);
        let _ = WinHttpCloseHandle(connect);
        let _ = WinHttpCloseHandle(session);

        Ok(HttpResponse {
            status: status_code as u16,
            headers: parsed_headers,
            body: body_out,
        })
    }
}

#[cfg(windows)]
pub struct WinHttpWs {
    session: *mut c_void,
    connect: *mut c_void,
    ws: *mut c_void,
}

#[cfg(windows)]
unsafe impl Send for WinHttpWs {}
#[cfg(windows)]
unsafe impl Sync for WinHttpWs {}

#[cfg(windows)]
impl WinHttpWs {
    pub fn connect(url: &str, headers: &[(&str, &str)]) -> Result<Self, String> {
        let parsed = parse_url(url)?;
        let agent = to_wide("ExoLauncher/1.0");
        let host_w = to_wide(&parsed.host);
        let path_w = to_wide(&parsed.path_and_query);

        unsafe {
            let session = WinHttpOpen(
                PCWSTR(agent.as_ptr()),
                WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                PCWSTR::null(),
                PCWSTR::null(),
                0,
            );
            if session.is_null() {
                return Err("WinHttpOpen failed".to_string());
            }
            let connect = WinHttpConnect(session, PCWSTR(host_w.as_ptr()), parsed.port, 0);
            if connect.is_null() {
                let _ = WinHttpCloseHandle(session);
                return Err("WinHttpConnect failed".to_string());
            }
            let flags = if parsed.secure {
                WINHTTP_FLAG_SECURE
            } else {
                WINHTTP_OPEN_REQUEST_FLAGS(0)
            };
            let request = WinHttpOpenRequest(
                connect,
                w!("GET"),
                PCWSTR(path_w.as_ptr()),
                PCWSTR::null(),
                PCWSTR::null(),
                ptr::null(),
                flags,
            );
            if request.is_null() {
                let _ = WinHttpCloseHandle(connect);
                let _ = WinHttpCloseHandle(session);
                return Err("WinHttpOpenRequest failed".to_string());
            }

            if let Err(e) =
                WinHttpSetOption(Some(request), WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, None)
            {
                let _ = WinHttpCloseHandle(request);
                let _ = WinHttpCloseHandle(connect);
                let _ = WinHttpCloseHandle(session);
                return Err(format!("WinHttpSetOption(WebSocket) failed: {e}"));
            }

            let mut hdr_text = String::new();
            for (k, v) in headers {
                hdr_text.push_str(k);
                hdr_text.push_str(": ");
                hdr_text.push_str(v);
                hdr_text.push_str("\r\n");
            }
            let hdr_wide: Vec<u16> = hdr_text.encode_utf16().collect();
            let hdr_opt = if hdr_wide.is_empty() {
                None
            } else {
                Some(hdr_wide.as_slice())
            };

            if let Err(e) = WinHttpSendRequest(request, hdr_opt, None, 0, 0, 0) {
                let _ = WinHttpCloseHandle(request);
                let _ = WinHttpCloseHandle(connect);
                let _ = WinHttpCloseHandle(session);
                return Err(format!("WinHttpSendRequest(WS) failed: {e}"));
            }
            if let Err(e) = WinHttpReceiveResponse(request, ptr::null_mut()) {
                let _ = WinHttpCloseHandle(request);
                let _ = WinHttpCloseHandle(connect);
                let _ = WinHttpCloseHandle(session);
                return Err(format!("WinHttpReceiveResponse(WS) failed: {e}"));
            }

            let ws = WinHttpWebSocketCompleteUpgrade(request, None);
            let _ = WinHttpCloseHandle(request);
            if ws.is_null() {
                let _ = WinHttpCloseHandle(connect);
                let _ = WinHttpCloseHandle(session);
                return Err("WinHttpWebSocketCompleteUpgrade failed".to_string());
            }

            Ok(Self {
                session,
                connect,
                ws,
            })
        }
    }

    pub fn send_text(&self, text: &str) -> Result<(), String> {
        let code = unsafe {
            WinHttpWebSocketSend(
                self.ws,
                WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE,
                Some(text.as_bytes()),
            )
        };
        if code != 0 {
            return Err(format!("WinHttpWebSocketSend failed: {code}"));
        }
        Ok(())
    }

    pub fn recv_text(&self) -> Result<Option<String>, String> {
        let mut msg = Vec::new();
        let mut chunk = [0u8; 8192];
        loop {
            let mut read = 0u32;
            let mut kind = WINHTTP_WEB_SOCKET_BUFFER_TYPE(0);
            let code = unsafe {
                WinHttpWebSocketReceive(
                    self.ws,
                    chunk.as_mut_ptr() as *mut c_void,
                    chunk.len() as u32,
                    &mut read,
                    &mut kind,
                )
            };
            if code != 0 {
                return Err(format!("WinHttpWebSocketReceive failed: {code}"));
            }
            if kind == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE {
                return Ok(None);
            }
            msg.extend_from_slice(&chunk[..read as usize]);
            if kind == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE
                || kind == WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE
            {
                return Ok(Some(String::from_utf8_lossy(&msg).into_owned()));
            }
            if kind != WINHTTP_WEB_SOCKET_UTF8_FRAGMENT_BUFFER_TYPE
                && kind != WINHTTP_WEB_SOCKET_BINARY_FRAGMENT_BUFFER_TYPE
            {
                return Ok(None);
            }
            if msg.len() > 1024 * 1024 {
                return Err("WebSocket message exceeded 1 MiB".to_string());
            }
        }
    }

    pub fn close(&self) {
        unsafe {
            let _ = WinHttpWebSocketClose(self.ws, 1000, None, 0);
        }
    }
}

#[cfg(windows)]
impl Drop for WinHttpWs {
    fn drop(&mut self) {
        unsafe {
            let _ = WinHttpCloseHandle(self.ws);
            let _ = WinHttpCloseHandle(self.connect);
            let _ = WinHttpCloseHandle(self.session);
        }
    }
}

#[cfg(windows)]
pub fn open_system_browser(url: &str) -> Result<(), String> {
    if !url.starts_with("https://") {
        return Err("refusing non-https external URL".to_string());
    }
    let wide = to_wide(url);
    let res = unsafe {
        ShellExecuteW(
            None,
            w!("open"),
            PCWSTR(wide.as_ptr()),
            PCWSTR::null(),
            PCWSTR::null(),
            SW_SHOWNORMAL,
        )
    };
    if res.0 as isize <= 32 {
        return Err(format!("ShellExecuteW failed: {}", res.0 as isize));
    }
    Ok(())
}

pub fn url_encode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        if b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_' | b'.' | b'~') {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

pub fn url_decode(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'+' {
            out.push(b' ');
            i += 1;
        } else if bytes[i] == b'%' && i + 2 < bytes.len() {
            if let (Some(hi), Some(lo)) = (hex_val(bytes[i + 1]), hex_val(bytes[i + 2])) {
                out.push((hi << 4) | lo);
                i += 3;
                continue;
            }
            out.push(bytes[i]);
            i += 1;
        } else {
            out.push(bytes[i]);
            i += 1;
        }
    }
    String::from_utf8_lossy(&out).into_owned()
}

fn hex_val(b: u8) -> Option<u8> {
    match b {
        b'0'..=b'9' => Some(b - b'0'),
        b'a'..=b'f' => Some(b - b'a' + 10),
        b'A'..=b'F' => Some(b - b'A' + 10),
        _ => None,
    }
}
