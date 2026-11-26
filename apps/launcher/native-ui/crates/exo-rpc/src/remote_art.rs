use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::SystemTime;

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
    "tenor.com",
    "giphy.com",
    "klipy.co",
    "klipy.com",
    "imgur.com",
    "redd.it",
    "gfycat.com",
];

/// Converts Tenor / Giphy / Klipy video-variant GIF URLs (`.mp4` / `.webm`)
/// into their animated `.gif` counterparts so Rune's image decoder can fetch
/// and animate them natively.
pub fn normalize_gif_image_url(url: &str) -> String {
    let trimmed = url.trim();
    let (base, query) = match trimmed.split_once('?') {
        Some((b, q)) => (b, Some(q)),
        None => (trimmed, None),
    };
    let lower = base.to_ascii_lowercase();
    let is_video_ext = lower.ends_with(".mp4") || lower.ends_with(".webm");
    if !is_video_ext {
        return trimmed.to_string();
    }
    let ext_len = if lower.ends_with(".webm") { 5 } else { 4 };
    let stem = &base[..base.len() - ext_len];

    let is_tenor = lower.contains("tenor.com/");
    let is_giphy_or_klipy =
        lower.contains("giphy.com/") || lower.contains("klipy.co/") || lower.contains("klipy.com/");

    if !is_tenor && !is_giphy_or_klipy {
        return trimmed.to_string();
    }

    let mut out = if is_tenor {
        // Tenor embeds encode format in the last 5 chars of the media-id path
        // segment (e.g. `...AAAPo/name.mp4`, `...AAAP1/name.mp4`, `...AAAP3/name.mp4`).
        // Switching that suffix to `AAAAC` returns the animated `.gif`.
        let mut converted = format!("{stem}.gif");
        for code in [
            "AAAPo/", "AAAP1/", "AAAP3/", "AAAPs/", "AAAP0/", "AAAP2/", "AAAP4/", "AAAPv/",
        ] {
            if let Some(pos) = converted.find(code) {
                converted.replace_range(pos..pos + code.len(), "AAAAC/");
                break;
            }
        }
        converted
    } else {
        format!("{stem}.gif")
    };

    if let Some(q) = query
        && !q.is_empty()
    {
        out.push('?');
        out.push_str(q);
    }
    out
}

const MAX_IMAGE_BYTES: usize = 8 * 1024 * 1024;
const MAX_CACHE_BYTES: u64 = 150 * 1024 * 1024;
const TARGET_CACHE_BYTES: u64 = 120 * 1024 * 1024;
const MAX_CACHE_FILES: usize = 1500;
const TARGET_CACHE_FILES: usize = 1200;
const PRUNE_EVERY_WRITES: usize = 32;

static CACHE_WRITES: AtomicUsize = AtomicUsize::new(0);

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

pub fn canonical_cache_key(url: &str) -> String {
    let trimmed = url.trim();
    let Some((base, query)) = trimmed.split_once('?') else {
        return trimmed.to_string();
    };
    let mut kept: Vec<&str> = query
        .split('&')
        .filter(|pair| !pair.is_empty())
        .filter(|pair| {
            let key = pair.split('=').next().unwrap_or("");
            !key.eq_ignore_ascii_case("ex")
                && !key.eq_ignore_ascii_case("is")
                && !key.eq_ignore_ascii_case("hm")
        })
        .collect();
    if kept.is_empty() {
        return base.to_string();
    }
    kept.sort_unstable();
    format!("{base}?{}", kept.join("&"))
}

pub fn cache_path_for(cache_dir: &Path, url: &str) -> PathBuf {
    let key = canonical_cache_key(url);
    let h1 = fnv64(key.as_bytes(), 0xcbf29ce484222325);
    let h2 = fnv64(key.as_bytes(), 0x84222325cbf29ce4 ^ (key.len() as u64));
    let ext = key
        .split('?')
        .next()
        .and_then(|s| s.rsplit('.').next())
        .map(|s| s.to_ascii_lowercase())
        .filter(|e| matches!(e.as_str(), "jpg" | "jpeg" | "png" | "webp" | "gif"))
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
    Some(
        PathBuf::from(base)
            .join("ExoLauncher")
            .join("cache")
            .join("rune-art"),
    )
}

pub fn prune_cache_dir(
    cache_dir: &Path,
    max_bytes: u64,
    target_bytes: u64,
    max_files: usize,
    target_files: usize,
) -> usize {
    let Ok(read_dir) = std::fs::read_dir(cache_dir) else {
        return 0;
    };
    let mut entries: Vec<(PathBuf, u64, SystemTime)> = Vec::new();
    let mut total_bytes: u64 = 0;
    for entry in read_dir.flatten() {
        let path = entry.path();
        let Ok(meta) = entry.metadata() else {
            continue;
        };
        if !meta.is_file() {
            continue;
        }
        if path.extension().and_then(|e| e.to_str()) == Some("tmp") {
            continue;
        }
        let len = meta.len();
        let modified = meta.modified().unwrap_or(SystemTime::UNIX_EPOCH);
        total_bytes = total_bytes.saturating_add(len);
        entries.push((path, len, modified));
    }
    if total_bytes <= max_bytes && entries.len() <= max_files {
        return 0;
    }
    entries.sort_by_key(|(_, _, modified)| *modified);
    let mut remaining = entries.len();
    let mut removed = 0;
    for (path, len, _) in entries {
        if total_bytes <= target_bytes && remaining <= target_files {
            break;
        }
        if std::fs::remove_file(&path).is_ok() {
            total_bytes = total_bytes.saturating_sub(len);
            remaining = remaining.saturating_sub(1);
            removed += 1;
        }
    }
    removed
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
    let writes = CACHE_WRITES.fetch_add(1, Ordering::Relaxed) + 1;
    if writes.is_multiple_of(PRUNE_EVERY_WRITES) {
        prune_cache_dir(
            cache_dir,
            MAX_CACHE_BYTES,
            TARGET_CACHE_BYTES,
            MAX_CACHE_FILES,
            TARGET_CACHE_FILES,
        );
    }
    Some(target)
}

#[cfg(not(windows))]
pub fn fetch_cached_image(_url: &str, _cache_dir: &Path) -> Option<PathBuf> {
    None
}

#[cfg(windows)]
struct SharedSession(*mut std::ffi::c_void);

#[cfg(windows)]
unsafe impl Send for SharedSession {}

#[cfg(windows)]
unsafe impl Sync for SharedSession {}

#[cfg(windows)]
fn shared_winhttp_session() -> Option<*mut std::ffi::c_void> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::null;
    use std::sync::OnceLock;
    use windows_sys::Win32::Networking::WinHttp::{
        WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, WINHTTP_DECOMPRESSION_FLAG_DEFLATE,
        WINHTTP_DECOMPRESSION_FLAG_GZIP, WINHTTP_OPTION_DECOMPRESSION,
        WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL, WINHTTP_OPTION_REDIRECT_POLICY,
        WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP, WINHTTP_PROTOCOL_FLAG_HTTP2,
        WinHttpOpen, WinHttpSetOption, WinHttpSetTimeouts,
    };

    static SESSION: OnceLock<Option<SharedSession>> = OnceLock::new();
    let entry = SESSION.get_or_init(|| {
        let agent: Vec<u16> = OsStr::new("ExoLauncher-Rune/1.0")
            .encode_wide()
            .chain(Some(0))
            .collect();
        let raw = unsafe {
            WinHttpOpen(
                agent.as_ptr(),
                WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                null(),
                null(),
                0,
            )
        };
        if raw.is_null() {
            return None;
        }
        unsafe {
            WinHttpSetTimeouts(raw, 4000, 4000, 4000, 8000);
            let policy = WINHTTP_OPTION_REDIRECT_POLICY_DISALLOW_HTTPS_TO_HTTP;
            WinHttpSetOption(
                raw,
                WINHTTP_OPTION_REDIRECT_POLICY,
                (&policy as *const u32).cast(),
                std::mem::size_of::<u32>() as u32,
            );
            let proto = WINHTTP_PROTOCOL_FLAG_HTTP2;
            WinHttpSetOption(
                raw,
                WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL,
                (&proto as *const u32).cast(),
                std::mem::size_of::<u32>() as u32,
            );
            let decomp = WINHTTP_DECOMPRESSION_FLAG_GZIP | WINHTTP_DECOMPRESSION_FLAG_DEFLATE;
            WinHttpSetOption(
                raw,
                WINHTTP_OPTION_DECOMPRESSION,
                (&decomp as *const u32).cast(),
                std::mem::size_of::<u32>() as u32,
            );
        }
        Some(SharedSession(raw))
    });
    entry.as_ref().map(|s| s.0)
}

#[cfg(windows)]
fn winhttp_get(host: &str, path: &str) -> Option<Vec<u8>> {
    use std::ffi::OsStr;
    use std::os::windows::ffi::OsStrExt;
    use std::ptr::null;
    use windows_sys::Win32::Networking::WinHttp::{
        WINHTTP_FLAG_SECURE, WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_QUERY_STATUS_CODE,
        WinHttpCloseHandle, WinHttpConnect, WinHttpOpenRequest, WinHttpQueryDataAvailable,
        WinHttpQueryHeaders, WinHttpReadData, WinHttpReceiveResponse, WinHttpSendRequest,
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

    let session = shared_winhttp_session()?;
    let host_w = to_wide(host);
    let conn = Handle(unsafe { WinHttpConnect(session, host_w.as_ptr(), 443, 0) });
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
        if unsafe {
            WinHttpReadData(
                req.0,
                out[start..].as_mut_ptr().cast(),
                chunk_len as u32,
                &mut read,
            )
        } == 0
        {
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
        assert!(parse_allowed_https("https://media.tenor.com/abcAAAAC/wave.gif").is_some());
        assert!(parse_allowed_https("https://media1.giphy.com/media/xyz/giphy.gif").is_some());
        assert!(parse_allowed_https("https://static.klipy.com/ii/1/a.gif").is_some());
        assert!(parse_allowed_https("http://shared.fastly.steamstatic.com/a.jpg").is_none());
        assert!(parse_allowed_https("https://127.0.0.1/secret.png").is_none());
        assert!(parse_allowed_https("https://evil.example/a.png").is_none());
        assert!(parse_allowed_https("https://shared.fastly.steamstatic.com:8443/a.jpg").is_none());
    }

    #[test]
    fn normalize_gif_image_url_rewrites_tenor_and_giphy_video_variants() {
        assert_eq!(
            normalize_gif_image_url("https://media.tenor.com/abc123AAAPo/cat-dance.mp4"),
            "https://media.tenor.com/abc123AAAAC/cat-dance.gif"
        );
        assert_eq!(
            normalize_gif_image_url("https://media.tenor.com/abc123AAAP3/cat-dance.webm?c=1"),
            "https://media.tenor.com/abc123AAAAC/cat-dance.gif?c=1"
        );
        assert_eq!(
            normalize_gif_image_url("https://media.giphy.com/media/123/giphy.mp4"),
            "https://media.giphy.com/media/123/giphy.gif"
        );
        assert_eq!(
            normalize_gif_image_url("https://media.tenor.com/abc123AAAAM/cat-dance.gif"),
            "https://media.tenor.com/abc123AAAAM/cat-dance.gif"
        );
        assert_eq!(
            normalize_gif_image_url("https://cdn.discordapp.com/attachments/1/2/clip.mp4"),
            "https://cdn.discordapp.com/attachments/1/2/clip.mp4"
        );
    }

    #[test]
    fn canonical_cache_key_strips_volatile_discord_signature_params() {
        let a = "https://cdn.discordapp.com/attachments/1/2/photo.png?ex=67000001&is=66f00001&hm=aaa&width=400";
        let b = "https://cdn.discordapp.com/attachments/1/2/photo.png?width=400&ex=67999999&is=67888888&hm=zzz";
        assert_eq!(canonical_cache_key(a), canonical_cache_key(b));
        assert_eq!(
            canonical_cache_key(a),
            "https://cdn.discordapp.com/attachments/1/2/photo.png?width=400"
        );
        let dir = Path::new("cache");
        assert_eq!(cache_path_for(dir, a), cache_path_for(dir, b));

        let c = "https://cdn.discordapp.com/avatars/1/hash.webp?ex=1&is=2&hm=3";
        assert_eq!(
            canonical_cache_key(c),
            "https://cdn.discordapp.com/avatars/1/hash.webp"
        );
    }

    #[test]
    fn prune_cache_dir_evicts_oldest_files_when_over_budget() {
        let dir =
            std::env::temp_dir().join(format!("exo-remote-art-prune-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).expect("create temp dir");

        let f1 = dir.join("a.png");
        let f2 = dir.join("b.png");
        let f3 = dir.join("c.png");
        let tmp = dir.join("inprogress.tmp");
        std::fs::write(&f1, vec![1u8; 40]).expect("write f1");
        std::thread::sleep(std::time::Duration::from_millis(25));
        std::fs::write(&f2, vec![2u8; 40]).expect("write f2");
        std::thread::sleep(std::time::Duration::from_millis(25));
        std::fs::write(&f3, vec![3u8; 40]).expect("write f3");
        std::fs::write(&tmp, vec![4u8; 200]).expect("write tmp");

        assert_eq!(prune_cache_dir(&dir, 200, 100, 10, 5), 0);
        assert!(f1.is_file() && f2.is_file() && f3.is_file());

        let removed = prune_cache_dir(&dir, 100, 50, 10, 5);
        assert_eq!(removed, 2);
        assert!(!f1.is_file());
        assert!(!f2.is_file());
        assert!(f3.is_file());
        assert!(tmp.is_file());

        let _ = std::fs::remove_dir_all(&dir);
    }
}
