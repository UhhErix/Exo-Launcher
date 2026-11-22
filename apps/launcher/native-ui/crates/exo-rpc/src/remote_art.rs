use std::path::{Path, PathBuf};

const ALLOWED_IMAGE_HOSTS: &[&str] = &[
    "steamstatic.com",
    "akamaihd.net",
    "steampowered.com",
    "steamgriddb.com",
    "epicgames.com",
    "gog-statics.com",
    "igdb.com",
    "discordapp.com",
    "discordapp.net",
    "ytimg.com",
    "mzstatic.com",
    "scdn.co",
];

const MAX_IMAGE_BYTES: usize = 8 * 1024 * 1024;

pub fn parse_allowed_https(url: &str) -> Option<(String, String)> {
    let rest = url.strip_prefix("https://")?;
    let (authority, path_and_query) = match rest.find('/') {
        Some(idx) => (&rest[..idx], &rest[idx..]),
        None => (rest, "/"),
    };
    if authority.is_empty() || authority.contains('@') || authority.contains(':') {
        return None;
    }
    let host = authority.to_ascii_lowercase();
    let allowed = ALLOWED_IMAGE_HOSTS
        .iter()
        .any(|suffix| host == *suffix || host.ends_with(&format!(".{suffix}")));
    if !allowed {
        return None;
    }
    Some((host, path_and_query.to_string()))
}

pub fn cache_path_for(cache_dir: &Path, url: &str) -> PathBuf {
    let h1 = fnv64(url.as_bytes(), 0xcbf29ce484222325);
    let h2 = fnv64(url.as_bytes(), 0x84222325cbf29ce4 ^ (url.len() as u64));
    let ext = url
        .split('?')
        .next()
        .and_then(|s| s.rsplit('.').next())
        .map(|s| s.to_ascii_lowercase())
        .filter(|e| matches!(e.as_str(), "jpg" | "jpeg" | "png" | "webp"))
        .unwrap_or_else(|| "img".into());
    cache_dir.join(format!("{h1:016x}{h2:016x}.{ext}"))
}

fn fnv64(bytes: &[u8], seed: u64) -> u64 {
    let mut h = seed;
    for &b in bytes {
        h ^= b as u64;
        h = h.wrapping_mul(0x100000001b3);
    }
    h
}

pub fn default_cache_dir() -> Option<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA")?;
    Some(PathBuf::from(base).join("ExoLauncher").join("cache").join("rune-art"))
}

#[cfg(windows)]
pub fn fetch_cached_image(url: &str, cache_dir: &Path) -> Option<PathBuf> {
    let (host, path) = parse_allowed_https(url)?;
    let target = cache_path_for(cache_dir, url);
    if target.is_file() {
        return Some(target);
    }
    let bytes = winhttp_get(&host, &path)?;
    if bytes.is_empty() || bytes.len() > MAX_IMAGE_BYTES {
        return None;
    }
    std::fs::create_dir_all(cache_dir).ok()?;
    let tmp = target.with_extension("tmp");
    std::fs::write(&tmp, &bytes).ok()?;
    if std::fs::rename(&tmp, &target).is_err() {
        let _ = std::fs::remove_file(&tmp);
        return if target.is_file() { Some(target) } else { None };
    }
    Some(target)
}

#[cfg(not(windows))]
pub fn fetch_cached_image(_url: &str, _cache_dir: &Path) -> Option<PathBuf> {
    None
}

#[cfg(windows)]
fn winhttp_get(host: &str, path: &str) -> Option<Vec<u8>> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::null;
    use windows_sys::Win32::Networking::WinHttp::{
        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_FLAG_SECURE, WINHTTP_OPTION_REDIRECT_POLICY,
        WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP, WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_QUERY_STATUS_CODE, WinHttpCloseHandle, WinHttpConnect, WinHttpOpen,
        WinHttpOpenRequest, WinHttpQueryDataAvailable, WinHttpQueryHeaders, WinHttpReadData,
        WinHttpReceiveResponse, WinHttpSendRequest, WinHttpSetOption, WinHttpSetTimeouts,
    };

    fn to_wide(s: &str) -> Vec<u16> {
        OsStr::new(s).encode_wide().chain(Some(0)).collect()
    }

    struct Handle(*mut std::ffi::c_void);
    impl Drop for Handle {
        fn drop(&mut self) {
            if !self.0.is_null() {
                unsafe {
                    WinHttpCloseHandle(self.0);
                }
            }
        }
    }

    let agent = to_wide("ExoLauncher-Rune/1.0");
    let session = Handle(unsafe {
        WinHttpOpen(agent.as_ptr(), WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, null(), null(), 0)
    });
    if session.0.is_null() {
        return None;
    }
    unsafe {
        WinHttpSetTimeouts(session.0, 4000, 4000, 4000, 8000);
        let policy = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
        WinHttpSetOption(
            session.0,
            WINHTTP_OPTION_REDIRECT_POLICY,
            (&policy as *const u32).cast(),
            std::mem::size_of::<u32>() as u32,
        );
    }

    let host_w = to_wide(host);
    let conn = Handle(unsafe { WinHttpConnect(session.0, host_w.as_ptr(), 443, 0) });
    if conn.0.is_null() {
        return None;
    }

    let get_w = to_wide("GET");
    let path_w = to_wide(path);
    let req = Handle(unsafe {
        WinHttpOpenRequest(
            conn.0,
            get_w.as_ptr(),
            path_w.as_ptr(),
            null(),
            null(),
            null(),
            WINHTTP_FLAG_SECURE,
        )
    });
    if req.0.is_null() {
        return None;
    }

    let sent = unsafe { WinHttpSendRequest(req.0, null(), 0, std::ptr::null(), 0, 0, 0) };
    if sent == 0 {
        return None;
    }
    if unsafe { WinHttpReceiveResponse(req.0, std::ptr::null_mut()) } == 0 {
        return None;
    }

    let mut status: u32 = 0;
    let mut status_len = std::mem::size_of::<u32>() as u32;
    let queried = unsafe {
        WinHttpQueryHeaders(
            req.0,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            null(),
            (&mut status as *mut u32).cast(),
            &mut status_len,
            std::ptr::null_mut(),
        )
    };
    if queried == 0 || status != 200 {
        return None;
    }

    let mut out = Vec::new();
    loop {
        let mut avail: u32 = 0;
        if unsafe { WinHttpQueryDataAvailable(req.0, &mut avail) } == 0 {
            return None;
        }
        if avail == 0 {
            break;
        }
        let chunk_len = (avail as usize).min(64 * 1024);
        if out.len() + chunk_len > MAX_IMAGE_BYTES {
            return None;
        }
        let start = out.len();
        out.resize(start + chunk_len, 0);
        let mut read: u32 = 0;
        if unsafe { WinHttpReadData(req.0, out[start..].as_mut_ptr().cast(), chunk_len as u32, &mut read) } == 0 {
            return None;
        }
        out.truncate(start + read as usize);
        if read == 0 {
            break;
        }
    }
    Some(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn allowlist_accepts_steam_cdn_and_rejects_local_or_arbitrary_hosts() {
        assert!(parse_allowed_https("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1091500/header.jpg").is_some());
        assert!(parse_allowed_https("http://shared.fastly.steamstatic.com/a.jpg").is_none());
        assert!(parse_allowed_https("https://127.0.0.1/secret.png").is_none());
        assert!(parse_allowed_https("https://evil.example/a.png").is_none());
        assert!(parse_allowed_https("https://shared.fastly.steamstatic.com:8443/a.jpg").is_none());
    }
}
