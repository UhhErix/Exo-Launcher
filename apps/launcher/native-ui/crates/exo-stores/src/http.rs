use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpStream;
use std::time::Duration;

use crate::security::validate_store_url;

#[derive(Debug, Clone)]
pub struct HttpResponse {
    pub status: u16,
    pub body: Vec<u8>,
}

pub struct ParsedUrl {
    pub https: bool,
    pub host: String,
    pub port: u16,
    pub path: String,
}

pub fn parse_url(url: &str) -> Result<ParsedUrl, String> {
    let (https, rest) = if let Some(r) = url.strip_prefix("https://") {
        (true, r)
    } else if let Some(r) = url.strip_prefix("http://") {
        (false, r)
    } else {
        return Err(format!("Unsupported URL scheme: {url}"));
    };
    let (authority, path) = match rest.find('/') {
        Some(idx) => (&rest[..idx], &rest[idx..]),
        None => (rest, "/"),
    };
    let (host, port) = match authority.rsplit_once(':') {
        Some((h, p)) if !h.contains(']') => (
            h.to_string(),
            p.parse::<u16>()
                .map_err(|_| format!("Invalid port in URL: {url}"))?,
        ),
        _ => (authority.to_string(), if https { 443 } else { 80 }),
    };
    Ok(ParsedUrl {
        https,
        host,
        port,
        path: path.to_string(),
    })
}

pub struct PooledHttpClient {
    allow_loopback: bool,
    timeout: Duration,
    cached_addr: Option<String>,
    stream: Option<BufReader<TcpStream>>,
}

impl PooledHttpClient {
    pub fn new(allow_loopback: bool, timeout: Duration) -> Self {
        Self {
            allow_loopback,
            timeout,
            cached_addr: None,
            stream: None,
        }
    }

    pub fn get(
        &mut self,
        url: &str,
        range: Option<(u64, u64)>,
        bearer_token: Option<&str>,
    ) -> Result<HttpResponse, String> {
        validate_store_url(url, self.allow_loopback)?;
        let parsed = parse_url(url)?;
        if parsed.https {
            return winhttp_get(&parsed, range, bearer_token, self.timeout);
        }
        let addr = format!("{}:{}", parsed.host, parsed.port);
        if self.cached_addr.as_deref() != Some(addr.as_str()) {
            self.stream = None;
            self.cached_addr = Some(addr.clone());
        }

        for attempt in 0..2 {
            if self.stream.is_none() {
                let tcp = TcpStream::connect(&addr)
                    .map_err(|e| format!("HTTP connect {addr} failed: {e}"))?;
                let _ = tcp.set_read_timeout(Some(self.timeout));
                let _ = tcp.set_write_timeout(Some(self.timeout));
                let _ = tcp.set_nodelay(true);
                self.stream = Some(BufReader::with_capacity(65_536, tcp));
            }
            let Some(reader) = self.stream.as_mut() else {
                continue;
            };
            match send_http11_request(reader, &parsed, range, bearer_token) {
                Ok((resp, keep_alive)) => {
                    if !keep_alive {
                        self.stream = None;
                    }
                    return Ok(resp);
                }
                Err(err) => {
                    self.stream = None;
                    if attempt == 1 {
                        return Err(err);
                    }
                }
            }
        }
        Err(format!("HTTP request failed for {url}"))
    }
}

fn send_http11_request(
    reader: &mut BufReader<TcpStream>,
    parsed: &ParsedUrl,
    range: Option<(u64, u64)>,
    bearer_token: Option<&str>,
) -> Result<(HttpResponse, bool), String> {
    let mut req = format!(
        "GET {} HTTP/1.1\r\nHost: {}:{}\r\nConnection: keep-alive\r\nUser-Agent: ExoStores/1.0\r\n",
        parsed.path, parsed.host, parsed.port
    );
    if let Some((start, end)) = range {
        req.push_str(&format!("Range: bytes={start}-{end}\r\n"));
    }
    if let Some(tok) = bearer_token {
        req.push_str(&format!("Authorization: Bearer {tok}\r\n"));
    }
    req.push_str("\r\n");

    reader
        .get_mut()
        .write_all(req.as_bytes())
        .map_err(|e| format!("HTTP write failed: {e}"))?;

    let mut status_line = String::new();
    let n = reader
        .read_line(&mut status_line)
        .map_err(|e| format!("HTTP read status failed: {e}"))?;
    if n == 0 {
        return Err("HTTP server closed connection".into());
    }
    let status = status_line
        .split_whitespace()
        .nth(1)
        .and_then(|s| s.parse::<u16>().ok())
        .ok_or_else(|| format!("Malformed HTTP status line: {}", status_line.trim()))?;

    let mut content_length: Option<usize> = None;
    let mut chunked = false;
    let mut keep_alive = true;
    loop {
        let mut line = String::new();
        let bytes = reader
            .read_line(&mut line)
            .map_err(|e| format!("HTTP header read failed: {e}"))?;
        if bytes == 0 || line == "\r\n" || line == "\n" {
            break;
        }
        if let Some((name, val)) = line.split_once(':') {
            let k = name.trim().to_ascii_lowercase();
            let v = val.trim().to_ascii_lowercase();
            if k == "content-length" {
                content_length = v.parse::<usize>().ok();
            } else if k == "transfer-encoding" && v.contains("chunked") {
                chunked = true;
            } else if k == "connection" && v.contains("close") {
                keep_alive = false;
            }
        }
    }

    let body = if chunked {
        let mut out = Vec::new();
        loop {
            let mut size_line = String::new();
            reader
                .read_line(&mut size_line)
                .map_err(|e| format!("Chunk size read failed: {e}"))?;
            let hex_len = size_line.split(';').next().unwrap_or("").trim();
            let len = usize::from_str_radix(hex_len, 16)
                .map_err(|_| format!("Bad chunk length: {hex_len}"))?;
            if len == 0 {
                let mut crlf = [0u8; 2];
                let _ = reader.read_exact(&mut crlf);
                break;
            }
            let start = out.len();
            out.resize(start + len, 0);
            reader
                .read_exact(&mut out[start..])
                .map_err(|e| format!("Chunk body read failed: {e}"))?;
            let mut crlf = [0u8; 2];
            let _ = reader.read_exact(&mut crlf);
        }
        out
    } else if let Some(len) = content_length {
        let mut buf = vec![0u8; len];
        reader
            .read_exact(&mut buf)
            .map_err(|e| format!("HTTP body read failed: {e}"))?;
        buf
    } else {
        keep_alive = false;
        let mut buf = Vec::new();
        reader
            .read_to_end(&mut buf)
            .map_err(|e| format!("HTTP EOF read failed: {e}"))?;
        buf
    };

    Ok((HttpResponse { status, body }, keep_alive))
}

#[cfg(windows)]
fn winhttp_get(
    parsed: &ParsedUrl,
    range: Option<(u64, u64)>,
    bearer_token: Option<&str>,
    timeout: Duration,
) -> Result<HttpResponse, String> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::{null, null_mut};

    type HInternet = *mut std::ffi::c_void;
    const WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY: u32 = 4;
    const WINHTTP_FLAG_SECURE: u32 = 0x0080_0000;
    const WINHTTP_QUERY_STATUS_CODE: u32 = 19;
    const WINHTTP_QUERY_FLAG_NUMBER: u32 = 0x2000_0000;
    const WINHTTP_ADDREQ_FLAG_ADD: u32 = 0x2000_0000;

    #[link(name = "winhttp")]
    unsafe extern "system" {
        fn WinHttpOpen(
            psz_agent_w: *const u16,
            dw_access_type: u32,
            psz_proxy_w: *const u16,
            psz_proxy_bypass_w: *const u16,
            dw_flags: u32,
        ) -> HInternet;
        fn WinHttpSetTimeouts(
            h_internet: HInternet,
            n_resolve_timeout: i32,
            n_connect_timeout: i32,
            n_send_timeout: i32,
            n_receive_timeout: i32,
        ) -> i32;
        fn WinHttpConnect(
            h_session: HInternet,
            pswz_server_name: *const u16,
            n_server_port: u16,
            dw_reserved: u32,
        ) -> HInternet;
        fn WinHttpOpenRequest(
            h_connect: HInternet,
            pwsz_verb: *const u16,
            pwsz_object_name: *const u16,
            pwsz_version: *const u16,
            pwsz_referrer: *const u16,
            ppwsz_accept_types: *const *const u16,
            dw_flags: u32,
        ) -> HInternet;
        fn WinHttpAddRequestHeaders(
            h_request: HInternet,
            lpsz_headers: *const u16,
            dw_headers_length: u32,
            dw_modifiers: u32,
        ) -> i32;
        fn WinHttpSendRequest(
            h_request: HInternet,
            lpsz_headers: *const u16,
            dw_headers_length: u32,
            lp_optional: *mut std::ffi::c_void,
            dw_optional_length: u32,
            dw_total_length: u32,
            dw_context: usize,
        ) -> i32;
        fn WinHttpReceiveResponse(h_request: HInternet, lp_reserved: *mut std::ffi::c_void) -> i32;
        fn WinHttpQueryHeaders(
            h_request: HInternet,
            dw_info_level: u32,
            pwsz_name: *const u16,
            lp_buffer: *mut std::ffi::c_void,
            lpdw_buffer_length: *mut u32,
            lpdw_index: *mut u32,
        ) -> i32;
        fn WinHttpReadData(
            h_request: HInternet,
            lp_buffer: *mut u8,
            dw_number_of_bytes_to_read: u32,
            lpdw_number_of_bytes_read: *mut u32,
        ) -> i32;
        fn WinHttpCloseHandle(h_internet: HInternet) -> i32;
    }

    fn to_wide(s: &str) -> Vec<u16> {
        OsStr::new(s)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect()
    }

    let agent = to_wide("ExoStores/1.0");
    let session = unsafe {
        WinHttpOpen(
            agent.as_ptr(),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            null(),
            null(),
            0,
        )
    };
    if session.is_null() {
        return Err("WinHttpOpen failed".into());
    }
    let ms = timeout.as_millis().min(i32::MAX as u128) as i32;
    unsafe {
        WinHttpSetTimeouts(session, ms, ms, ms, ms);
    }

    let host_w = to_wide(&parsed.host);
    let conn = unsafe { WinHttpConnect(session, host_w.as_ptr(), parsed.port, 0) };
    if conn.is_null() {
        unsafe {
            WinHttpCloseHandle(session);
        }
        return Err("WinHttpConnect failed".into());
    }

    let verb = to_wide("GET");
    let path_w = to_wide(&parsed.path);
    let req = unsafe {
        WinHttpOpenRequest(
            conn,
            verb.as_ptr(),
            path_w.as_ptr(),
            null(),
            null(),
            null(),
            WINHTTP_FLAG_SECURE,
        )
    };
    if req.is_null() {
        unsafe {
            WinHttpCloseHandle(conn);
            WinHttpCloseHandle(session);
        }
        return Err("WinHttpOpenRequest failed".into());
    }

    let mut headers = String::new();
    if let Some((start, end)) = range {
        headers.push_str(&format!("Range: bytes={start}-{end}\r\n"));
    }
    if let Some(tok) = bearer_token {
        headers.push_str(&format!("Authorization: Bearer {tok}\r\n"));
    }
    if !headers.is_empty() {
        let hw = to_wide(&headers);
        unsafe {
            WinHttpAddRequestHeaders(
                req,
                hw.as_ptr(),
                (hw.len() - 1) as u32,
                WINHTTP_ADDREQ_FLAG_ADD,
            );
        }
    }

    let result = (|| {
        if unsafe { WinHttpSendRequest(req, null(), 0, null_mut(), 0, 0, 0) } == 0 {
            return Err("WinHttpSendRequest failed".into());
        }
        if unsafe { WinHttpReceiveResponse(req, null_mut()) } == 0 {
            return Err("WinHttpReceiveResponse failed".into());
        }
        let mut status: u32 = 0;
        let mut status_len: u32 = std::mem::size_of::<u32>() as u32;
        let _ = unsafe {
            WinHttpQueryHeaders(
                req,
                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                null(),
                (&mut status as *mut u32).cast(),
                &mut status_len,
                null_mut(),
            )
        };
        let mut body = Vec::new();
        let mut chunk = vec![0u8; 65_536];
        loop {
            let mut read = 0u32;
            if unsafe { WinHttpReadData(req, chunk.as_mut_ptr(), chunk.len() as u32, &mut read) }
                == 0
            {
                return Err("WinHttpReadData failed".into());
            }
            if read == 0 {
                break;
            }
            body.extend_from_slice(&chunk[..read as usize]);
        }
        Ok(HttpResponse {
            status: status as u16,
            body,
        })
    })();

    unsafe {
        WinHttpCloseHandle(req);
        WinHttpCloseHandle(conn);
        WinHttpCloseHandle(session);
    }
    result
}

#[cfg(not(windows))]
fn winhttp_get(
    _parsed: &ParsedUrl,
    _range: Option<(u64, u64)>,
    _bearer_token: Option<&str>,
    _timeout: Duration,
) -> Result<HttpResponse, String> {
    Err("WinHTTP HTTPS transport requires Windows".into())
}
