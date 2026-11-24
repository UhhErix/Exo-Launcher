#[cfg(not(windows))]
use rune::NativeSurface;
use rune::Waker;
#[cfg(windows)]
use rune::{ExternalHostEvent, ExternalLayer, NativeSurface};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NativeMediaSource {
    ClearHls(String),
    ClearProgressive(String),
    ClearDash {
        mpd_url: String,
    },
    PlayReadyDash {
        mpd_url: String,
        license_url: String,
        security_level: u32,
    },
}

impl NativeMediaSource {
    pub fn classify(url: &str, playready_license_url: Option<&str>) -> Self {
        let lower = url.to_ascii_lowercase();
        let path_part = lower.split('?').next().unwrap_or(&lower);
        if path_part.ends_with(".mpd") || lower.contains("/manifest.mpd") {
            if let Some(la) = playready_license_url.filter(|s| !s.is_empty()) {
                return Self::PlayReadyDash {
                    mpd_url: url.to_string(),
                    license_url: la.to_string(),
                    security_level: 3000,
                };
            }
            return Self::ClearDash {
                mpd_url: url.to_string(),
            };
        }
        if path_part.ends_with(".m3u8") {
            return Self::ClearHls(url.to_string());
        }
        Self::ClearProgressive(url.to_string())
    }

    pub fn uses_mf_dcomp(&self) -> bool {
        matches!(self, Self::ClearDash { .. } | Self::PlayReadyDash { .. })
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct CaptionCue {
    pub start_sec: f64,
    pub end_sec: f64,
    pub text: String,
}

#[derive(Debug, Clone)]
pub struct DashTrack {
    pub mime_type: String,
    pub codecs: String,
    pub bandwidth: u64,
    #[allow(dead_code)]
    pub width: u32,
    pub height: u32,
    pub init_url: String,
    pub segment_urls: Vec<String>,
    pub playready_pro_b64: Option<String>,
    #[allow(dead_code)]
    pub default_kid: Option<String>,
}

#[derive(Debug, Clone)]
pub struct ParsedDash {
    pub video: DashTrack,
    #[allow(dead_code)]
    pub audio: Option<DashTrack>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum NativeMediaState {
    #[default]
    Loading,
    Playing,
    Paused,
    Failed,
}

#[allow(dead_code)]
pub fn parse_webvtt(vtt: &str) -> Vec<CaptionCue> {
    let mut cues = Vec::new();
    let lines: Vec<&str> = vtt.lines().collect();
    let mut i = 0;
    while i < lines.len() {
        let line = lines[i].trim();
        if let Some((a, b)) = line.split_once("-->") {
            let start_sec = parse_vtt_time(a.trim());
            let end_token = b.split_whitespace().next().unwrap_or("");
            let end_sec = parse_vtt_time(end_token);
            i += 1;
            let mut text_lines = Vec::new();
            while i < lines.len() && !lines[i].trim().is_empty() {
                text_lines.push(strip_vtt_tags(lines[i].trim()));
                i += 1;
            }
            let text = text_lines.join("\n");
            if !text.is_empty() {
                cues.push(CaptionCue {
                    start_sec,
                    end_sec,
                    text,
                });
            }
        } else {
            i += 1;
        }
    }
    cues
}

#[allow(dead_code)]
fn parse_vtt_time(s: &str) -> f64 {
    let parts: Vec<&str> = s.split(':').collect();
    match parts.as_slice() {
        [h, m, sec] => {
            h.parse::<f64>().unwrap_or(0.0) * 3600.0
                + m.parse::<f64>().unwrap_or(0.0) * 60.0
                + sec.replace(',', ".").parse::<f64>().unwrap_or(0.0)
        }
        [m, sec] => {
            m.parse::<f64>().unwrap_or(0.0) * 60.0
                + sec.replace(',', ".").parse::<f64>().unwrap_or(0.0)
        }
        _ => 0.0,
    }
}

#[allow(dead_code)]
fn strip_vtt_tags(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let mut in_tag = false;
    for c in s.chars() {
        match c {
            '<' => in_tag = true,
            '>' => in_tag = false,
            _ if !in_tag => out.push(c),
            _ => {}
        }
    }
    out
}

pub fn build_playready_pssh(pro_bytes: &[u8]) -> Vec<u8> {
    const PLAYREADY_SYS_ID: [u8; 16] = [
        0x9a, 0x04, 0xf0, 0x79, 0x98, 0x40, 0x42, 0x86, 0xab, 0x92, 0xe6, 0x5b, 0xe0, 0x88, 0x5f,
        0x95,
    ];
    let total = 32u32 + pro_bytes.len() as u32;
    let mut box_bytes = Vec::with_capacity(total as usize);
    box_bytes.extend_from_slice(&total.to_be_bytes());
    box_bytes.extend_from_slice(b"pssh");
    box_bytes.extend_from_slice(&0u32.to_be_bytes());
    box_bytes.extend_from_slice(&PLAYREADY_SYS_ID);
    box_bytes.extend_from_slice(&(pro_bytes.len() as u32).to_be_bytes());
    box_bytes.extend_from_slice(pro_bytes);
    box_bytes
}

pub fn parse_dash_mpd(mpd_url: &str, xml: &str, max_segments: usize) -> Result<ParsedDash, String> {
    let base_dir = mpd_url
        .rsplit_once('/')
        .map(|(prefix, _)| format!("{prefix}/"))
        .unwrap_or_else(|| mpd_url.to_string());
    let top_base = extract_tag_body(xml, "BaseURL").unwrap_or_default();
    let root_base = join_url(&base_dir, &top_base);

    let mut video_candidates = Vec::new();
    let mut audio_candidates = Vec::new();

    for set in split_elements(xml, "AdaptationSet") {
        let set_open = open_tag(set);
        let set_mime = attr(set_open, "mimeType").unwrap_or_default();
        let set_content = attr(set_open, "contentType").unwrap_or_default();
        let set_codecs = attr(set_open, "codecs").unwrap_or_default();
        let set_pro = extract_tag_body(set, "mspr:pro");
        let set_kid = attr(set, "cenc:default_KID");
        let set_base = join_url(
            &root_base,
            &extract_tag_body(set, "BaseURL").unwrap_or_default(),
        );
        let set_tpl = first_element(set, "SegmentTemplate");

        for rep in split_elements(set, "Representation") {
            let rep_open = open_tag(rep);
            let id = attr(rep_open, "id").unwrap_or_default();
            let mime = attr(rep_open, "mimeType").unwrap_or_else(|| set_mime.clone());
            let codecs = attr(rep_open, "codecs").unwrap_or_else(|| set_codecs.clone());
            let bandwidth = attr(rep_open, "bandwidth")
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(0);
            let width = attr(rep_open, "width")
                .and_then(|v| v.parse::<u32>().ok())
                .unwrap_or(0);
            let height = attr(rep_open, "height")
                .and_then(|v| v.parse::<u32>().ok())
                .unwrap_or(0);
            let rep_base = join_url(
                &set_base,
                &extract_tag_body(rep, "BaseURL").unwrap_or_default(),
            );
            let rep_tpl = first_element(rep, "SegmentTemplate").or(set_tpl);
            let Some(tpl) = rep_tpl else {
                continue;
            };
            let tpl_open = open_tag(tpl);
            let Some(init_pat) = attr(tpl_open, "initialization") else {
                continue;
            };
            let Some(media_pat) = attr(tpl_open, "media") else {
                continue;
            };
            let start_num = attr(tpl_open, "startNumber")
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(1);

            let init_rel = expand_template(&init_pat, &id, bandwidth, None, None);
            let init_url = join_url(&rep_base, &init_rel);
            let segment_urls = build_segment_urls(
                tpl,
                &rep_base,
                &media_pat,
                &id,
                bandwidth,
                start_num,
                max_segments,
            );
            if segment_urls.is_empty() {
                continue;
            }
            let track = DashTrack {
                mime_type: mime.clone(),
                codecs,
                bandwidth,
                width,
                height,
                init_url,
                segment_urls,
                playready_pro_b64: extract_tag_body(rep, "mspr:pro").or_else(|| set_pro.clone()),
                default_kid: attr(rep, "cenc:default_KID").or_else(|| set_kid.clone()),
            };
            let is_video = mime.starts_with("video/") || set_content == "video" || height > 0;
            let is_audio = mime.starts_with("audio/") || set_content == "audio";
            if is_video {
                video_candidates.push(track);
            } else if is_audio {
                audio_candidates.push(track);
            }
        }
    }

    if video_candidates.is_empty() {
        return Err("no DASH video Representation with SegmentTemplate found".into());
    }
    video_candidates.sort_by_key(|t| {
        let avc = u8::from(t.codecs.starts_with("avc1"));
        let le_1080 = u8::from(t.height <= 1080);
        (avc, le_1080, t.height, t.bandwidth)
    });
    let video = video_candidates.pop().ok_or("no DASH video candidate")?;
    audio_candidates.sort_by_key(|t| {
        let aac = u8::from(t.codecs.starts_with("mp4a"));
        (aac, t.bandwidth)
    });
    let audio = audio_candidates.pop();
    Ok(ParsedDash { video, audio })
}

fn build_segment_urls(
    tpl_xml: &str,
    base: &str,
    media_pat: &str,
    id: &str,
    bandwidth: u64,
    start_num: u64,
    max_segments: usize,
) -> Vec<String> {
    let mut out = Vec::new();
    if media_pat.contains("$Time") {
        let mut cur_t: u64 = 0;
        let mut idx = 0usize;
        for s_tag in split_s_tags(tpl_xml) {
            if let Some(t) = attr(s_tag, "t").and_then(|v| v.parse::<u64>().ok()) {
                cur_t = t;
            }
            let d = attr(s_tag, "d")
                .and_then(|v| v.parse::<u64>().ok())
                .unwrap_or(0);
            let r = attr(s_tag, "r")
                .and_then(|v| v.parse::<i64>().ok())
                .unwrap_or(0);
            let count = if r < 0 {
                max_segments.saturating_sub(idx)
            } else {
                (r as usize + 1).min(max_segments.saturating_sub(idx))
            };
            for _ in 0..count {
                let rel = expand_template(media_pat, id, bandwidth, None, Some(cur_t));
                out.push(join_url(base, &rel));
                cur_t = cur_t.saturating_add(d);
                idx += 1;
                if idx >= max_segments {
                    return out;
                }
            }
        }
    } else {
        for i in 0..max_segments {
            let n = start_num + i as u64;
            let rel = expand_template(media_pat, id, bandwidth, Some(n), None);
            out.push(join_url(base, &rel));
        }
    }
    out
}

fn expand_template(
    pat: &str,
    id: &str,
    bandwidth: u64,
    number: Option<u64>,
    time: Option<u64>,
) -> String {
    let mut out = pat
        .replace("&amp;", "&")
        .replace("$RepresentationID$", id)
        .replace("$Bandwidth$", &bandwidth.to_string());
    if let Some(n) = number {
        out = replace_formatted_token(&out, "Number", n);
    }
    if let Some(t) = time {
        out = replace_formatted_token(&out, "Time", t);
    }
    out
}

fn replace_formatted_token(s: &str, name: &str, val: u64) -> String {
    let prefix = format!("${name}");
    let mut cur = s.to_string();
    while let Some(start) = cur.find(&prefix) {
        let rest = &cur[start + prefix.len()..];
        let Some(end_rel) = rest.find('$') else {
            break;
        };
        let fmt = &rest[..end_rel];
        let formatted = if let Some(spec) = fmt.strip_prefix('%') {
            let width = spec
                .trim_end_matches(|c: char| c.is_ascii_alphabetic())
                .parse::<usize>()
                .unwrap_or(0);
            if width > 0 {
                format!("{val:0width$}")
            } else {
                val.to_string()
            }
        } else {
            val.to_string()
        };
        let full_end = start + prefix.len() + end_rel + 1;
        cur.replace_range(start..full_end, &formatted);
    }
    cur
}

fn join_url(base: &str, rel: &str) -> String {
    let rel = rel.trim();
    if rel.is_empty() {
        return base.to_string();
    }
    if rel.starts_with("http://") || rel.starts_with("https://") {
        return rel.to_string();
    }
    if base.ends_with('/') {
        format!("{base}{}", rel.trim_start_matches('/'))
    } else {
        format!("{base}/{}", rel.trim_start_matches('/'))
    }
}

fn open_tag(el: &str) -> &str {
    el.find('>').map(|i| &el[..=i]).unwrap_or(el)
}

fn attr(tag: &str, name: &str) -> Option<String> {
    for quote in ['"', '\''] {
        let needle = format!("{name}={quote}");
        let mut pos = 0usize;
        while let Some(rel) = tag[pos..].find(&needle) {
            let idx = pos + rel;
            let boundary_ok = idx == 0
                || tag.as_bytes()[idx - 1].is_ascii_whitespace()
                || tag.as_bytes()[idx - 1] == b'<';
            if boundary_ok {
                let rest = &tag[idx + needle.len()..];
                if let Some(end) = rest.find(quote) {
                    return Some(rest[..end].to_string());
                }
            }
            pos = idx + needle.len();
        }
    }
    None
}

fn extract_tag_body(xml: &str, tag: &str) -> Option<String> {
    let open = format!("<{tag}");
    let close = format!("</{tag}>");
    let start = xml.find(&open)?;
    let after = &xml[start + open.len()..];
    let gt = after.find('>')?;
    let body_start = start + open.len() + gt + 1;
    let end = xml[body_start..].find(&close)?;
    Some(xml[body_start..body_start + end].trim().to_string())
}

fn first_element<'a>(xml: &'a str, tag: &str) -> Option<&'a str> {
    split_elements(xml, tag).into_iter().next()
}

fn split_elements<'a>(xml: &'a str, tag: &str) -> Vec<&'a str> {
    let mut res = Vec::new();
    let open = format!("<{tag}");
    let close = format!("</{tag}>");
    let mut pos = 0usize;
    while let Some(rel) = xml[pos..].find(&open) {
        let start = pos + rel;
        let after_open = &xml[start + open.len()..];
        let next_ch = after_open.chars().next();
        if !matches!(next_ch, Some(' ' | '>' | '/' | '\r' | '\n' | '\t')) {
            pos = start + open.len();
            continue;
        }
        let Some(gt_rel) = xml[start..].find('>') else {
            break;
        };
        let gt = start + gt_rel;
        if gt > start && xml.as_bytes()[gt - 1] == b'/' {
            res.push(&xml[start..=gt]);
            pos = gt + 1;
            continue;
        }
        if let Some(close_rel) = xml[gt + 1..].find(&close) {
            let end = gt + 1 + close_rel + close.len();
            res.push(&xml[start..end]);
            pos = end;
        } else {
            break;
        }
    }
    res
}

fn split_s_tags(xml: &str) -> Vec<&str> {
    let mut res = Vec::new();
    let mut pos = 0usize;
    while let Some(rel) = xml[pos..].find("<S ") {
        let start = pos + rel;
        let Some(gt) = xml[start..].find('>') else {
            break;
        };
        res.push(&xml[start..=start + gt]);
        pos = start + gt + 1;
    }
    res
}

#[cfg(windows)]
static NEXT_LAYER_ID: AtomicU64 = AtomicU64::new(1000);

#[cfg(windows)]
fn next_layer_id() -> u64 {
    NEXT_LAYER_ID.fetch_add(1, Ordering::Relaxed)
}

#[derive(Default)]
struct SharedSessionState {
    state: NativeMediaState,
    error: Option<String>,
    opm_status: Option<String>,
    duration: Option<f64>,
    current_time: f64,
    muted: bool,
    cues: Vec<CaptionCue>,
}

pub struct NativeMediaSession {
    surface: NativeSurface,
    shared: Arc<Mutex<SharedSessionState>>,
    #[cfg(windows)]
    cmd_tx: Arc<Mutex<Option<std::sync::mpsc::Sender<SessionCmd>>>>,
    #[cfg(windows)]
    shutdown: Arc<AtomicBool>,
}

#[cfg(windows)]
enum SessionCmd {
    Play,
    Pause,
    SetMuted(bool),
    Seek(f64),
    Resize(i32, i32),
    Stop,
}

impl NativeMediaSession {
    pub fn start(source: NativeMediaSource, muted: bool, waker: Option<Waker>) -> Self {
        let surface = NativeSurface::new();
        if let Some(ref w) = waker {
            surface.set_waker(w.clone());
        }
        #[cfg(windows)]
        surface.seed_opaque_mask(16, 9);

        let shared = Arc::new(Mutex::new(SharedSessionState {
            state: NativeMediaState::Loading,
            muted,
            ..Default::default()
        }));

        #[cfg(windows)]
        {
            let cmd_tx: Arc<Mutex<Option<std::sync::mpsc::Sender<SessionCmd>>>> =
                Arc::new(Mutex::new(None));
            let started = Arc::new(AtomicBool::new(false));
            let shutdown = Arc::new(AtomicBool::new(false));

            let surf_cb = surface.clone();
            let shared_cb = Arc::clone(&shared);
            let cmd_cb = Arc::clone(&cmd_tx);
            let started_cb = Arc::clone(&started);
            let shutdown_cb = Arc::clone(&shutdown);
            let waker_cb = waker;

            surface.set_external_host(Some(Arc::new(move |ev: ExternalHostEvent| {
                if let Some(tx) = cmd_cb.lock().unwrap().as_ref() {
                    let _ = tx.send(SessionCmd::Resize(ev.rect[2].max(16), ev.rect[3].max(16)));
                }
                if started_cb.swap(true, Ordering::SeqCst) {
                    return;
                }
                let (tx, rx) = std::sync::mpsc::channel();
                *cmd_cb.lock().unwrap() = Some(tx);
                let src = source.clone();
                let surf = surf_cb.clone();
                let st = Arc::clone(&shared_cb);
                let sd = Arc::clone(&shutdown_cb);
                let wk = waker_cb.clone();
                let hwnd = ev.hwnd;
                let w = ev.rect[2].max(320);
                let h = ev.rect[3].max(180);
                let _ = std::thread::Builder::new()
                    .name("exo-mf-native-media".into())
                    .spawn(move || {
                        win_mf::run_session_thread(src, hwnd, w, h, muted, surf, st, sd, wk, rx);
                    });
            })));

            Self {
                surface,
                shared,
                cmd_tx,
                shutdown,
            }
        }

        #[cfg(not(windows))]
        {
            let _ = source;
            Self { surface, shared }
        }
    }

    pub fn surface(&self) -> &NativeSurface {
        &self.surface
    }

    pub fn state(&self) -> NativeMediaState {
        self.shared.lock().unwrap().state
    }

    #[allow(dead_code)]
    pub fn time(&self) -> f64 {
        self.shared.lock().unwrap().current_time
    }

    #[allow(dead_code)]
    pub fn duration(&self) -> Option<f64> {
        self.shared.lock().unwrap().duration
    }

    #[allow(dead_code)]
    pub fn opm_status(&self) -> Option<String> {
        self.shared.lock().unwrap().opm_status.clone()
    }

    #[allow(dead_code)]
    pub fn active_caption(&self) -> Option<String> {
        let g = self.shared.lock().unwrap();
        let t = g.current_time;
        g.cues
            .iter()
            .find(|c| t >= c.start_sec && t <= c.end_sec)
            .map(|c| c.text.clone())
    }

    #[allow(dead_code)]
    pub fn set_captions(&self, cues: Vec<CaptionCue>) {
        self.shared.lock().unwrap().cues = cues;
    }

    pub fn play(&self) {
        #[cfg(windows)]
        if let Some(tx) = self.cmd_tx.lock().unwrap().as_ref() {
            let _ = tx.send(SessionCmd::Play);
        }
    }

    pub fn pause(&self) {
        #[cfg(windows)]
        if let Some(tx) = self.cmd_tx.lock().unwrap().as_ref() {
            let _ = tx.send(SessionCmd::Pause);
        }
    }

    pub fn set_muted(&self, muted: bool) {
        self.shared.lock().unwrap().muted = muted;
        #[cfg(windows)]
        if let Some(tx) = self.cmd_tx.lock().unwrap().as_ref() {
            let _ = tx.send(SessionCmd::SetMuted(muted));
        }
    }

    #[allow(dead_code)]
    pub fn seek(&self, secs: f64) {
        #[cfg(windows)]
        if let Some(tx) = self.cmd_tx.lock().unwrap().as_ref() {
            let _ = tx.send(SessionCmd::Seek(secs.max(0.0)));
        }
    }

    pub fn stop(&self) {
        #[cfg(windows)]
        {
            self.shutdown.store(true, Ordering::SeqCst);
            if let Some(tx) = self.cmd_tx.lock().unwrap().take() {
                let _ = tx.send(SessionCmd::Stop);
            }
            self.surface.set_external_layer(None);
            self.surface.set_external_host(None);
        }
    }
}

impl Drop for NativeMediaSession {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(windows)]
mod win_mf {
    use super::*;
    use std::collections::HashMap;
    use std::sync::Condvar;
    use std::sync::atomic::AtomicU32;
    use std::time::{Duration, Instant};
    use windows::Foundation::Collections::{IPropertySet, PropertySet};
    use windows::Media::Protection::{
        IMediaProtectionManager, IMediaProtectionManager_Vtbl, IMediaProtectionPMPServer,
    };
    use windows::Win32::Foundation::{PROPERTYKEY, RECT};
    use windows::Win32::Graphics::Direct3D::*;
    use windows::Win32::Graphics::Direct3D11::*;
    use windows::Win32::Media::MediaFoundation::*;
    use windows::Win32::Networking::WinHttp::*;
    use windows::Win32::System::Com::StructuredStorage::{CreateStreamOnHGlobal, PROPVARIANT};
    use windows::Win32::System::Com::*;
    use windows::Win32::UI::Shell::PropertiesSystem::{
        IPropertyStore, PSCreateMemoryPropertyStore,
    };
    use windows::core::{BSTR, GUID, HRESULT, HSTRING, IUnknown, Interface, PCWSTR, implement};

    const CLSID_EME_STORE_ACTIVATE: GUID = GUID::from_u128(0x2df7b51e_797b_4d06_be71_d14a52cf8421);
    const GUID_EME_CLASS_NAME: GUID = GUID::from_u128(0x77631a31_e5e7_4785_bf17_20f57b224802);
    const GUID_EME_OBJECT_STREAM: GUID = GUID::from_u128(0x3e73735c_e6c0_481d_8260_ee5db1343b5f);
    const PKEY_CDM_INPRIVATE_STOREPATH: PROPERTYKEY = PROPERTYKEY {
        fmtid: GUID::from_u128(0x730cb3ac_51dc_49da_a578_b95386b62afe),
        pid: 1,
    };
    const PKEY_CDM_STOREPATH: PROPERTYKEY = PROPERTYKEY {
        fmtid: GUID::from_u128(0x77d993b9_ba61_4bb7_92c6_18c86a189c06),
        pid: 2,
    };

    #[derive(Clone)]
    struct SendCom<T>(T);
    unsafe impl<T> Send for SendCom<T> {}
    unsafe impl<T> Sync for SendCom<T> {}

    fn http_get(url: &str) -> Result<Vec<u8>, String> {
        http_request("GET", url, None, None)
    }

    fn http_post_playready(url: &str, soap_xml: &[u8]) -> Result<Vec<u8>, String> {
        let headers = "Content-Type: text/xml; charset=utf-8\r\nSOAPAction: \"http://schemas.microsoft.com/DRM/2007/03/protocols/AcquireLicense\"\r\n";
        http_request("POST", url, Some(headers), Some(soap_xml))
    }

    fn http_request(
        method: &str,
        url: &str,
        extra_headers: Option<&str>,
        body: Option<&[u8]>,
    ) -> Result<Vec<u8>, String> {
        let (https, host, port, path) = split_url(url)?;
        unsafe {
            let ua = HSTRING::from("ExoNativeMedia/1.0");
            let session = WinHttpOpen(
                PCWSTR(ua.as_ptr()),
                WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
                PCWSTR::null(),
                PCWSTR::null(),
                0,
            );
            if session.is_null() {
                return Err("WinHttpOpen failed".into());
            }
            let _ = WinHttpSetTimeouts(session, 8000, 8000, 12000, 12000);
            let host_h = HSTRING::from(host.as_str());
            let conn = WinHttpConnect(session, PCWSTR(host_h.as_ptr()), port, 0);
            if conn.is_null() {
                let _ = WinHttpCloseHandle(session);
                return Err("WinHttpConnect failed".into());
            }
            let method_h = HSTRING::from(method);
            let path_h = HSTRING::from(path.as_str());
            let flags = if https {
                WINHTTP_FLAG_SECURE
            } else {
                WINHTTP_OPEN_REQUEST_FLAGS(0)
            };
            let req = WinHttpOpenRequest(
                conn,
                PCWSTR(method_h.as_ptr()),
                PCWSTR(path_h.as_ptr()),
                PCWSTR::null(),
                PCWSTR::null(),
                std::ptr::null(),
                flags,
            );
            if req.is_null() {
                let _ = WinHttpCloseHandle(conn);
                let _ = WinHttpCloseHandle(session);
                return Err("WinHttpOpenRequest failed".into());
            }
            if let Some(hdrs) = extra_headers {
                let hdr_h = HSTRING::from(hdrs);
                let _ = WinHttpAddRequestHeaders(
                    req,
                    hdr_h.as_ref(),
                    WINHTTP_ADDREQ_FLAG_ADD | WINHTTP_ADDREQ_FLAG_REPLACE,
                );
            }
            let body_ptr = body.map(|b| b.as_ptr() as *const core::ffi::c_void);
            let body_len = body.map(|b| b.len() as u32).unwrap_or(0);
            let send_res = WinHttpSendRequest(req, None, body_ptr, body_len, body_len, 0);
            if send_res.is_err() || WinHttpReceiveResponse(req, std::ptr::null_mut()).is_err() {
                let _ = WinHttpCloseHandle(req);
                let _ = WinHttpCloseHandle(conn);
                let _ = WinHttpCloseHandle(session);
                return Err("WinHttp send/receive failed".into());
            }
            let mut status = 0u32;
            let mut status_len = std::mem::size_of::<u32>() as u32;
            let _ = WinHttpQueryHeaders(
                req,
                WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                PCWSTR::null(),
                Some(&mut status as *mut _ as *mut _),
                &mut status_len,
                std::ptr::null_mut(),
            );
            let mut out = Vec::new();
            let mut avail = 0u32;
            while WinHttpQueryDataAvailable(req, &mut avail).is_ok() && avail > 0 {
                let mut chunk = vec![0u8; avail as usize];
                let mut read = 0u32;
                if WinHttpReadData(req, chunk.as_mut_ptr() as *mut _, avail, &mut read).is_err() {
                    break;
                }
                out.extend_from_slice(&chunk[..read as usize]);
            }
            let _ = WinHttpCloseHandle(req);
            let _ = WinHttpCloseHandle(conn);
            let _ = WinHttpCloseHandle(session);
            if !(200..300).contains(&status) {
                return Err(format!("HTTP {status}"));
            }
            Ok(out)
        }
    }

    fn split_url(url: &str) -> Result<(bool, String, u16, String), String> {
        let (https, rest) = if let Some(r) = url.strip_prefix("https://") {
            (true, r)
        } else if let Some(r) = url.strip_prefix("http://") {
            (false, r)
        } else {
            return Err("unsupported URL scheme".into());
        };
        let (authority, path) = match rest.find('/') {
            Some(i) => (&rest[..i], &rest[i..]),
            None => (rest, "/"),
        };
        let (host, port) = match authority.rsplit_once(':') {
            Some((h, p)) => (
                h.to_string(),
                p.parse::<u16>().unwrap_or(if https { 443 } else { 80 }),
            ),
            None => (authority.to_string(), if https { 443 } else { 80 }),
        };
        Ok((https, host, port, path.to_string()))
    }

    #[derive(Default)]
    struct EngineNotifyState {
        can_play: AtomicBool,
        first_frame: AtomicBool,
        error_code: AtomicU32,
        error_hr: AtomicU32,
        cvar: (Mutex<()>, Condvar),
    }

    #[implement(IMFMediaEngineNotify)]
    struct NotifyImpl {
        state: Arc<EngineNotifyState>,
    }

    impl IMFMediaEngineNotify_Impl for NotifyImpl_Impl {
        fn EventNotify(&self, event: u32, param1: usize, param2: u32) -> windows::core::Result<()> {
            match MF_MEDIA_ENGINE_EVENT(event as i32) {
                MF_MEDIA_ENGINE_EVENT_CANPLAY | MF_MEDIA_ENGINE_EVENT_CANPLAYTHROUGH => {
                    self.state.can_play.store(true, Ordering::SeqCst);
                }
                MF_MEDIA_ENGINE_EVENT_FIRSTFRAMEREADY => {
                    self.state.first_frame.store(true, Ordering::SeqCst);
                }
                MF_MEDIA_ENGINE_EVENT_ERROR => {
                    self.state.error_code.store(param1 as u32, Ordering::SeqCst);
                    self.state.error_hr.store(param2, Ordering::SeqCst);
                }
                _ => {}
            }
            self.state.cvar.1.notify_all();
            Ok(())
        }
    }

    #[derive(Default)]
    struct MseState {
        opened: AtomicBool,
        cvar: (Mutex<()>, Condvar),
    }

    #[implement(IMFMediaSourceExtensionNotify)]
    struct MseNotifyImpl {
        state: Arc<MseState>,
    }

    impl IMFMediaSourceExtensionNotify_Impl for MseNotifyImpl_Impl {
        fn OnSourceOpen(&self) {
            self.state.opened.store(true, Ordering::SeqCst);
            self.state.cvar.1.notify_all();
        }
        fn OnSourceEnded(&self) {}
        fn OnSourceClose(&self) {}
    }

    #[implement(IMFMediaEngineExtension)]
    struct MseEngineExtension {
        mse: SendCom<IMFMediaSourceExtension>,
        trusted_source: Mutex<Option<SendCom<IMFMediaSource>>>,
    }

    impl IMFMediaEngineExtension_Impl for MseEngineExtension_Impl {
        fn CanPlayType(
            &self,
            _audioonly: windows_core::BOOL,
            _mimetype: &BSTR,
        ) -> windows::core::Result<MF_MEDIA_ENGINE_CANPLAY> {
            Ok(MF_MEDIA_ENGINE_CANPLAY_PROBABLY)
        }

        fn BeginCreateObject(
            &self,
            bstrurl: &BSTR,
            _pbytestream: windows_core::Ref<IMFByteStream>,
            r#type: MF_OBJECT_TYPE,
            _ppiunknowncancelcookie: windows_core::OutRef<IUnknown>,
            pcallback: windows_core::Ref<IMFAsyncCallback>,
            punkstate: windows_core::Ref<IUnknown>,
        ) -> windows::core::Result<()> {
            let url = bstrurl.to_string();
            let unk: IUnknown = if r#type == MF_OBJECT_MEDIASOURCE && url.starts_with("trusted://")
            {
                let guard = self.trusted_source.lock().unwrap();
                let Some(SendCom(src)) = guard.as_ref() else {
                    return Err(windows::core::Error::from(HRESULT(0x80004005u32 as i32)));
                };
                src.cast()?
            } else if r#type == MF_OBJECT_MEDIASOURCE && url.starts_with("mse://") {
                self.mse.0.cast()?
            } else {
                return Err(windows::core::Error::from(HRESULT(0x80004001u32 as i32)));
            };
            unsafe {
                let result =
                    MFCreateAsyncResult(Some(&unk), pcallback.as_ref(), punkstate.as_ref())?;
                result.SetStatus(HRESULT(0))?;
                MFInvokeCallback(&result)?;
            }
            Ok(())
        }

        fn CancelObjectCreation(
            &self,
            _punkcancelcookie: windows_core::Ref<IUnknown>,
        ) -> windows::core::Result<()> {
            Ok(())
        }

        fn EndCreateObject(
            &self,
            presult: windows_core::Ref<IMFAsyncResult>,
        ) -> windows::core::Result<IUnknown> {
            let res = presult.ok()?;
            unsafe { res.GetObject() }
        }
    }

    #[derive(Default)]
    struct BufState {
        updating: AtomicBool,
        cvar: (Mutex<()>, Condvar),
    }

    #[implement(IMFSourceBufferNotify)]
    struct BufNotifyImpl {
        state: Arc<BufState>,
    }

    impl IMFSourceBufferNotify_Impl for BufNotifyImpl_Impl {
        fn OnUpdateStart(&self) {
            self.state.updating.store(true, Ordering::SeqCst);
        }
        fn OnAbort(&self) {
            self.state.updating.store(false, Ordering::SeqCst);
            self.state.cvar.1.notify_all();
        }
        fn OnError(&self, _hr: HRESULT) {
            self.state.updating.store(false, Ordering::SeqCst);
            self.state.cvar.1.notify_all();
        }
        fn OnUpdate(&self) {
            self.state.updating.store(false, Ordering::SeqCst);
            self.state.cvar.1.notify_all();
        }
        fn OnUpdateEnd(&self) {
            self.state.updating.store(false, Ordering::SeqCst);
            self.state.cvar.1.notify_all();
        }
    }

    #[derive(Default)]
    struct CdmSessionState {
        challenge: Mutex<Option<Vec<u8>>>,
        cvar: (Mutex<()>, Condvar),
    }

    #[implement(IMFContentDecryptionModuleSessionCallbacks)]
    struct CdmCallbacks {
        state: Arc<CdmSessionState>,
    }

    impl IMFContentDecryptionModuleSessionCallbacks_Impl for CdmCallbacks_Impl {
        fn KeyMessage(
            &self,
            _msg_type: MF_MEDIAKEYSESSION_MESSAGETYPE,
            message: *const u8,
            message_size: u32,
            _dest: &PCWSTR,
        ) -> windows::core::Result<()> {
            if !message.is_null() && message_size > 0 {
                let bytes =
                    unsafe { std::slice::from_raw_parts(message, message_size as usize).to_vec() };
                *self.state.challenge.lock().unwrap() = Some(bytes);
                self.state.cvar.1.notify_all();
            }
            Ok(())
        }

        fn KeyStatusChanged(&self) -> windows::core::Result<()> {
            self.state.cvar.1.notify_all();
            Ok(())
        }
    }

    #[implement(IMFPMPHostApp)]
    struct PmpHostApp {
        pmp_host: IMFPMPHost,
    }

    impl IMFPMPHostApp_Impl for PmpHostApp_Impl {
        fn LockProcess(&self) -> windows::core::Result<()> {
            unsafe { self.pmp_host.LockProcess() }
        }
        fn UnlockProcess(&self) -> windows::core::Result<()> {
            unsafe { self.pmp_host.UnlockProcess() }
        }
        fn ActivateClassById(
            &self,
            id: &PCWSTR,
            stream: windows::core::Ref<'_, IStream>,
            riid: *const GUID,
            activated: *mut *mut core::ffi::c_void,
        ) -> windows::core::Result<()> {
            unsafe {
                let mut attrs: Option<IMFAttributes> = None;
                MFCreateAttributes(&mut attrs, 2)?;
                let attrs = attrs
                    .ok_or_else(|| windows::core::Error::from(HRESULT(0x80004005u32 as i32)))?;
                attrs.SetString(&GUID_EME_CLASS_NAME, *id)?;
                if let Some(st) = stream.as_ref() {
                    let mut stat = STATSTG::default();
                    st.Stat(&mut stat, STATFLAG_NONAME)?;
                    let size = stat.cbSize as usize;
                    let mut blob = vec![0u8; size];
                    let mut read = 0u32;
                    let _ = st.Seek(0, STREAM_SEEK_SET, None);
                    let _ = st.Read(blob.as_mut_ptr() as *mut _, size as u32, Some(&mut read));
                    attrs.SetBlob(&GUID_EME_OBJECT_STREAM, &blob[..read as usize])?;
                }
                let mem_stream =
                    CreateStreamOnHGlobal(windows::Win32::Foundation::HGLOBAL::default(), true)?;
                MFSerializeAttributesToStream(&attrs, 0, &mem_stream)?;
                let _ = mem_stream.Seek(0, STREAM_SEEK_SET, None);
                let unk: IUnknown = self
                    .pmp_host
                    .CreateObjectByCLSID(&CLSID_EME_STORE_ACTIVATE, &mem_stream)?;
                unk.query(riid, activated).ok()
            }
        }
    }

    #[repr(C)]
    struct HybridProtectionManager {
        cpm_vtbl: *const IMFContentProtectionManager_Vtbl,
        mpm_vtbl: *const IMediaProtectionManager_Vtbl,
        ref_count: AtomicU32,
        props: IPropertySet,
    }

    impl HybridProtectionManager {
        const CPM_VTBL: IMFContentProtectionManager_Vtbl = IMFContentProtectionManager_Vtbl {
            base__: windows_core::IUnknown_Vtbl {
                QueryInterface: Self::qi_cpm,
                AddRef: Self::add_ref_cpm,
                Release: Self::release_cpm,
            },
            BeginEnableContent: Self::begin_enable_content,
            EndEnableContent: Self::end_enable_content,
        };

        const MPM_VTBL: IMediaProtectionManager_Vtbl = IMediaProtectionManager_Vtbl {
            base__: windows_core::IInspectable_Vtbl {
                base: windows_core::IUnknown_Vtbl {
                    QueryInterface: Self::qi_mpm,
                    AddRef: Self::add_ref_mpm,
                    Release: Self::release_mpm,
                },
                GetIids: Self::get_iids,
                GetRuntimeClassName: Self::get_runtime_class_name,
                GetTrustLevel: Self::get_trust_level,
            },
            ServiceRequested: Self::add_event_noop,
            RemoveServiceRequested: Self::remove_event_noop,
            RebootNeeded: Self::add_event_noop,
            RemoveRebootNeeded: Self::remove_event_noop,
            ComponentLoadFailed: Self::add_event_noop,
            RemoveComponentLoadFailed: Self::remove_event_noop,
            Properties: Self::get_properties,
        };

        fn create(
            pmp_server: &IMediaProtectionPMPServer,
        ) -> windows::core::Result<IMFContentProtectionManager> {
            let props = PropertySet::new()?;
            let key = HSTRING::from("Windows.Media.Protection.MediaProtectionPMPServer");
            let server_insp: windows::core::IInspectable = pmp_server.cast()?;
            props.Insert(&key, &server_insp)?;
            let boxed = Box::new(Self {
                cpm_vtbl: &Self::CPM_VTBL,
                mpm_vtbl: &Self::MPM_VTBL,
                ref_count: AtomicU32::new(1),
                props: props.cast()?,
            });
            let raw = Box::into_raw(boxed) as *mut core::ffi::c_void;
            Ok(unsafe { IMFContentProtectionManager::from_raw(raw) })
        }

        unsafe fn from_mpm(this: *mut core::ffi::c_void) -> *mut Self {
            let offset = std::mem::offset_of!(Self, mpm_vtbl);
            unsafe { (this as *mut u8).sub(offset) as *mut Self }
        }

        unsafe fn qi_common(
            self_ptr: *mut Self,
            riid: *const GUID,
            ppv: *mut *mut core::ffi::c_void,
        ) -> HRESULT {
            if riid.is_null() || ppv.is_null() {
                return HRESULT(0x80004003u32 as i32);
            }
            let iid = unsafe { *riid };
            if iid == IUnknown::IID || iid == IMFContentProtectionManager::IID {
                unsafe {
                    (*self_ptr).ref_count.fetch_add(1, Ordering::Relaxed);
                    *ppv = self_ptr as *mut core::ffi::c_void;
                }
                return HRESULT(0);
            }
            if iid == IMediaProtectionManager::IID || iid == windows_core::IInspectable::IID {
                unsafe {
                    (*self_ptr).ref_count.fetch_add(1, Ordering::Relaxed);
                    *ppv = std::ptr::addr_of_mut!((*self_ptr).mpm_vtbl) as *mut core::ffi::c_void;
                }
                return HRESULT(0);
            }
            unsafe { *ppv = std::ptr::null_mut() };
            HRESULT(0x80004002u32 as i32)
        }

        unsafe extern "system" fn qi_cpm(
            this: *mut core::ffi::c_void,
            riid: *const GUID,
            ppv: *mut *mut core::ffi::c_void,
        ) -> HRESULT {
            unsafe { Self::qi_common(this as *mut Self, riid, ppv) }
        }

        unsafe extern "system" fn qi_mpm(
            this: *mut core::ffi::c_void,
            riid: *const GUID,
            ppv: *mut *mut core::ffi::c_void,
        ) -> HRESULT {
            unsafe { Self::qi_common(Self::from_mpm(this), riid, ppv) }
        }

        unsafe extern "system" fn add_ref_cpm(this: *mut core::ffi::c_void) -> u32 {
            unsafe {
                (*(this as *mut Self))
                    .ref_count
                    .fetch_add(1, Ordering::Relaxed)
                    + 1
            }
        }

        unsafe extern "system" fn add_ref_mpm(this: *mut core::ffi::c_void) -> u32 {
            unsafe {
                (*Self::from_mpm(this))
                    .ref_count
                    .fetch_add(1, Ordering::Relaxed)
                    + 1
            }
        }

        unsafe fn release_common(self_ptr: *mut Self) -> u32 {
            let prev = unsafe { (*self_ptr).ref_count.fetch_sub(1, Ordering::Release) };
            if prev == 1 {
                std::sync::atomic::fence(Ordering::Acquire);
                drop(unsafe { Box::from_raw(self_ptr) });
                0
            } else {
                prev - 1
            }
        }

        unsafe extern "system" fn release_cpm(this: *mut core::ffi::c_void) -> u32 {
            unsafe { Self::release_common(this as *mut Self) }
        }

        unsafe extern "system" fn release_mpm(this: *mut core::ffi::c_void) -> u32 {
            unsafe { Self::release_common(Self::from_mpm(this)) }
        }

        unsafe extern "system" fn get_iids(
            _this: *mut core::ffi::c_void,
            count: *mut u32,
            iids: *mut *mut GUID,
        ) -> HRESULT {
            unsafe {
                if !count.is_null() {
                    *count = 0;
                }
                if !iids.is_null() {
                    *iids = std::ptr::null_mut();
                }
            }
            HRESULT(0)
        }

        unsafe extern "system" fn get_runtime_class_name(
            _this: *mut core::ffi::c_void,
            name: *mut *mut core::ffi::c_void,
        ) -> HRESULT {
            unsafe {
                if !name.is_null() {
                    *name = std::ptr::null_mut();
                }
            }
            HRESULT(0)
        }

        unsafe extern "system" fn get_trust_level(
            _this: *mut core::ffi::c_void,
            level: *mut i32,
        ) -> HRESULT {
            unsafe {
                if !level.is_null() {
                    *level = 0;
                }
            }
            HRESULT(0)
        }

        unsafe extern "system" fn add_event_noop(
            _this: *mut core::ffi::c_void,
            _handler: *mut core::ffi::c_void,
            token: *mut i64,
        ) -> HRESULT {
            unsafe {
                if !token.is_null() {
                    *token = 1;
                }
            }
            HRESULT(0)
        }

        unsafe extern "system" fn remove_event_noop(
            _this: *mut core::ffi::c_void,
            _token: i64,
        ) -> HRESULT {
            HRESULT(0)
        }

        unsafe extern "system" fn get_properties(
            this: *mut core::ffi::c_void,
            out: *mut *mut core::ffi::c_void,
        ) -> HRESULT {
            if out.is_null() {
                return HRESULT(0x80004003u32 as i32);
            }
            let me = unsafe { &*Self::from_mpm(this) };
            let cloned = me.props.clone();
            unsafe {
                *out = cloned.into_raw();
            }
            HRESULT(0)
        }

        unsafe extern "system" fn begin_enable_content(
            _this: *mut core::ffi::c_void,
            _enabler: *mut core::ffi::c_void,
            _topo: *mut core::ffi::c_void,
            cb: *mut core::ffi::c_void,
            state: *mut core::ffi::c_void,
        ) -> HRESULT {
            unsafe {
                let cb_ref = IMFAsyncCallback::from_raw_borrowed(&cb);
                let st_ref = IUnknown::from_raw_borrowed(&state);
                match MFCreateAsyncResult(None, cb_ref, st_ref) {
                    Ok(res) => {
                        let _ = MFInvokeCallback(&res);
                        HRESULT(0)
                    }
                    Err(e) => e.code(),
                }
            }
        }

        unsafe extern "system" fn end_enable_content(
            _this: *mut core::ffi::c_void,
            _result: *mut core::ffi::c_void,
        ) -> HRESULT {
            HRESULT(0)
        }
    }

    #[implement(IMFMediaSource, IMFTrustedInput, IMFGetService)]
    struct TrustedMediaSourceProxy {
        inner: IMFMediaSource,
        trusted: IMFTrustedInput,
        itas: Mutex<HashMap<u32, SendCom<IUnknown>>>,
    }

    impl IMFMediaEventGenerator_Impl for TrustedMediaSourceProxy_Impl {
        fn GetEvent(
            &self,
            flags: MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS,
        ) -> windows::core::Result<IMFMediaEvent> {
            unsafe { self.inner.GetEvent(flags) }
        }
        fn BeginGetEvent(
            &self,
            cb: windows::core::Ref<'_, IMFAsyncCallback>,
            state: windows::core::Ref<'_, IUnknown>,
        ) -> windows::core::Result<()> {
            unsafe { self.inner.BeginGetEvent(cb.as_ref(), state.as_ref()) }
        }
        fn EndGetEvent(
            &self,
            res: windows::core::Ref<'_, IMFAsyncResult>,
        ) -> windows::core::Result<IMFMediaEvent> {
            unsafe { self.inner.EndGetEvent(res.as_ref()) }
        }
        fn QueueEvent(
            &self,
            met: u32,
            ext: *const GUID,
            hr: HRESULT,
            val: *const PROPVARIANT,
        ) -> windows::core::Result<()> {
            unsafe { self.inner.QueueEvent(met, ext, hr, val) }
        }
    }

    impl IMFMediaSource_Impl for TrustedMediaSourceProxy_Impl {
        fn GetCharacteristics(&self) -> windows::core::Result<u32> {
            unsafe { self.inner.GetCharacteristics() }
        }
        fn CreatePresentationDescriptor(&self) -> windows::core::Result<IMFPresentationDescriptor> {
            unsafe { self.inner.CreatePresentationDescriptor() }
        }
        fn Start(
            &self,
            pd: windows::core::Ref<'_, IMFPresentationDescriptor>,
            fmt: *const GUID,
            pos: *const PROPVARIANT,
        ) -> windows::core::Result<()> {
            unsafe { self.inner.Start(pd.as_ref(), fmt, pos) }
        }
        fn Stop(&self) -> windows::core::Result<()> {
            unsafe { self.inner.Stop() }
        }
        fn Pause(&self) -> windows::core::Result<()> {
            unsafe { self.inner.Pause() }
        }
        fn Shutdown(&self) -> windows::core::Result<()> {
            unsafe { self.inner.Shutdown() }
        }
    }

    impl IMFTrustedInput_Impl for TrustedMediaSourceProxy_Impl {
        fn GetInputTrustAuthority(
            &self,
            stream_id: u32,
            riid: *const GUID,
        ) -> windows::core::Result<IUnknown> {
            let mut map = self.itas.lock().unwrap();
            if let Some(SendCom(cached)) = map.get(&stream_id) {
                let mut raw = std::ptr::null_mut();
                unsafe {
                    cached.query(riid, &mut raw).ok()?;
                    return Ok(IUnknown::from_raw(raw));
                }
            }
            let unk = unsafe { self.trusted.GetInputTrustAuthority(stream_id, riid)? };
            map.insert(stream_id, SendCom(unk.clone()));
            Ok(unk)
        }
    }

    impl IMFGetService_Impl for TrustedMediaSourceProxy_Impl {
        fn GetService(
            &self,
            guidservice: *const GUID,
            riid: *const GUID,
            ppvobject: *mut *mut core::ffi::c_void,
        ) -> windows::core::Result<()> {
            if let Ok(gs) = self.inner.cast::<IMFGetService>() {
                unsafe {
                    let unk: IUnknown = gs.GetService(guidservice)?;
                    unk.query(riid, ppvobject).ok()
                }
            } else {
                Err(windows::core::Error::from(HRESULT(0x80004002u32 as i32)))
            }
        }
    }

    fn append_bytes(buf: &IMFSourceBuffer, st: &BufState, data: &[u8]) -> Result<(), String> {
        st.updating.store(true, Ordering::SeqCst);
        unsafe {
            buf.Append(data)
                .map_err(|e| format!("IMFSourceBuffer::Append: {e}"))?;
        }
        let deadline = Instant::now() + Duration::from_secs(6);
        let mut guard = st.cvar.0.lock().unwrap();
        while st.updating.load(Ordering::SeqCst) {
            let rem = deadline.saturating_duration_since(Instant::now());
            if rem.is_zero() {
                break;
            }
            let (g, _) = st.cvar.1.wait_timeout(guard, rem).unwrap();
            guard = g;
        }
        Ok(())
    }

    fn extract_playready_soap(challenge_bytes: &[u8]) -> Vec<u8> {
        let utf16: Vec<u16> = challenge_bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| u16::from_le_bytes(*c))
            .collect();
        let text = String::from_utf16_lossy(&utf16);
        if let Some(b64) = extract_tag_body(&text, "Challenge")
            && let Some(decoded) = decode_b64(&b64)
        {
            return decoded;
        }
        challenge_bytes.to_vec()
    }

    fn decode_b64(input: &str) -> Option<Vec<u8>> {
        let mut buf = Vec::with_capacity(input.len() * 3 / 4);
        let mut acc = 0u32;
        let mut bits = 0u8;
        for b in input.bytes() {
            let val = match b {
                b'A'..=b'Z' => b - b'A',
                b'a'..=b'z' => b - b'a' + 26,
                b'0'..=b'9' => b - b'0' + 52,
                b'+' => 62,
                b'/' => 63,
                b'=' => break,
                b' ' | b'\r' | b'\n' | b'\t' => continue,
                _ => return None,
            };
            acc = (acc << 6) | (val as u32);
            bits += 6;
            if bits >= 8 {
                bits -= 8;
                buf.push(((acc >> bits) & 0xFF) as u8);
            }
        }
        Some(buf)
    }

    #[allow(clippy::too_many_arguments)]
    pub(super) fn run_session_thread(
        source: NativeMediaSource,
        hwnd_raw: isize,
        width: i32,
        height: i32,
        muted: bool,
        surface: NativeSurface,
        shared: Arc<Mutex<SharedSessionState>>,
        shutdown: Arc<AtomicBool>,
        waker: Option<Waker>,
        rx: std::sync::mpsc::Receiver<SessionCmd>,
    ) {
        let wake = || {
            if let Some(ref w) = waker {
                w();
            }
        };
        let fail = |msg: String| {
            let mut g = shared.lock().unwrap();
            g.state = NativeMediaState::Failed;
            g.error = Some(msg);
            drop(g);
            wake();
        };

        unsafe {
            let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
            if let Err(e) = MFStartup(MF_VERSION, MFSTARTUP_FULL) {
                fail(format!("MFStartup: {e}"));
                return;
            }
        }

        let (mpd_url, playready_la, requested_sl) = match &source {
            NativeMediaSource::ClearDash { mpd_url } => (mpd_url.clone(), None, 0u32),
            NativeMediaSource::PlayReadyDash {
                mpd_url,
                license_url,
                security_level,
            } => (mpd_url.clone(), Some(license_url.clone()), *security_level),
            _ => {
                fail("unsupported NativeMediaSource for MF DComp".into());
                return;
            }
        };

        let mpd_bytes = match http_get(&mpd_url) {
            Ok(b) => b,
            Err(e) => {
                fail(format!("MPD fetch: {e}"));
                return;
            }
        };
        let mpd_xml = String::from_utf8_lossy(&mpd_bytes);
        let parsed = match parse_dash_mpd(&mpd_url, &mpd_xml, 8) {
            Ok(p) => p,
            Err(e) => {
                fail(format!("MPD parse: {e}"));
                return;
            }
        };

        let result = unsafe {
            run_mf_pipeline(
                &parsed,
                playready_la.as_deref(),
                requested_sl,
                hwnd_raw,
                width,
                height,
                muted,
                &surface,
                &shared,
                &shutdown,
                &waker,
                rx,
            )
        };
        if let Err(e) = result {
            fail(e);
        }
        unsafe {
            let _ = MFShutdown();
            CoUninitialize();
        }
    }

    #[allow(clippy::too_many_arguments)]
    unsafe fn run_mf_pipeline(
        parsed: &ParsedDash,
        playready_la: Option<&str>,
        requested_sl: u32,
        hwnd_raw: isize,
        width: i32,
        height: i32,
        muted: bool,
        surface: &NativeSurface,
        shared: &Arc<Mutex<SharedSessionState>>,
        shutdown: &Arc<AtomicBool>,
        waker: &Option<Waker>,
        rx: std::sync::mpsc::Receiver<SessionCmd>,
    ) -> Result<(), String> {
        let mut device: Option<ID3D11Device> = None;
        let feature_levels = [D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0];
        unsafe {
            D3D11CreateDevice(
                None,
                D3D_DRIVER_TYPE_HARDWARE,
                windows::Win32::Foundation::HMODULE::default(),
                D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                Some(&feature_levels),
                D3D11_SDK_VERSION,
                Some(&mut device),
                None,
                None,
            )
            .map_err(|e| format!("D3D11CreateDevice: {e}"))?;
        }
        let device = device.ok_or("D3D11 device is None")?;
        if let Ok(mt) = device.cast::<ID3D11Multithread>() {
            unsafe {
                let _ = mt.SetMultithreadProtected(true);
            }
        }
        let mut reset_token = 0u32;
        let mut dxgi_mgr: Option<IMFDXGIDeviceManager> = None;
        unsafe {
            MFCreateDXGIDeviceManager(&mut reset_token, &mut dxgi_mgr)
                .map_err(|e| format!("MFCreateDXGIDeviceManager: {e}"))?;
        }
        let dxgi_mgr = dxgi_mgr.ok_or("DXGI manager is None")?;
        unsafe {
            dxgi_mgr
                .ResetDevice(&device, reset_token)
                .map_err(|e| format!("ResetDevice: {e}"))?;
        }

        let factory: IMFMediaEngineClassFactory = unsafe {
            CoCreateInstance(&CLSID_MFMediaEngineClassFactory, None, CLSCTX_INPROC_SERVER)
        }
        .map_err(|e| format!("CoCreateInstance(MFMediaEngineClassFactory): {e}"))?;

        let mse_state = Arc::new(MseState::default());
        let mse_notify: IMFMediaSourceExtensionNotify = MseNotifyImpl {
            state: Arc::clone(&mse_state),
        }
        .into();
        let mut mse_attrs: Option<IMFAttributes> = None;
        unsafe {
            MFCreateAttributes(&mut mse_attrs, 2).map_err(|e| format!("mse attrs: {e}"))?;
        }
        let mse_attrs = mse_attrs.ok_or("mse_attrs is None")?;
        unsafe {
            mse_attrs
                .SetUnknown(&MF_MSE_CALLBACK, &mse_notify)
                .map_err(|e| format!("SetUnknown MF_MSE_CALLBACK: {e}"))?;
        }
        let factory_ex: IMFMediaEngineClassFactoryEx = factory
            .cast()
            .map_err(|e| format!("cast IMFMediaEngineClassFactoryEx: {e}"))?;
        let mse = unsafe {
            factory_ex
                .CreateMediaSourceExtension(0, &mse_attrs)
                .map_err(|e| format!("CreateMediaSourceExtension: {e}"))?
        };

        let mut cdm_keep: Option<IMFContentDecryptionModule> = None;
        let mut session_keep: Option<IMFContentDecryptionModuleSession> = None;
        let mut pmp_keep: Option<IMFPMPHostApp> = None;
        let mut prot_mgr_keep: Option<IMFContentProtectionManager> = None;
        let mut trusted_source_opt: Option<SendCom<IMFMediaSource>> = None;

        if let Some(la_url) = playready_la {
            let factory4: IMFMediaEngineClassFactory4 = factory
                .cast()
                .map_err(|e| format!("cast IMFMediaEngineClassFactory4: {e}"))?;
            let ks_candidates: &[&str] = if requested_sl >= 3000 {
                &[
                    "com.microsoft.playready.recommendation.3000",
                    "com.microsoft.playready.recommendation",
                ]
            } else {
                &["com.microsoft.playready.recommendation"]
            };
            let mut chosen_factory: Option<(String, IMFContentDecryptionModuleFactory)> = None;
            for ks in ks_candidates {
                let ks_h = HSTRING::from(*ks);
                let mut raw = std::ptr::null_mut();
                if unsafe {
                    factory4.CreateContentDecryptionModuleFactory(
                        PCWSTR(ks_h.as_ptr()),
                        &IMFContentDecryptionModuleFactory::IID,
                        &mut raw,
                    )
                }
                .is_ok()
                    && !raw.is_null()
                {
                    chosen_factory = Some(((*ks).to_string(), unsafe {
                        IMFContentDecryptionModuleFactory::from_raw(raw)
                    }));
                    break;
                }
            }
            let (chosen_ks, cdm_factory) =
                chosen_factory.ok_or("PlayReady CDM factory unavailable")?;
            let mut store: Option<IPropertyStore> = None;
            unsafe {
                PSCreateMemoryPropertyStore(&IPropertyStore::IID, &mut store as *mut _ as *mut _)
                    .map_err(|e| format!("PSCreateMemoryPropertyStore: {e}"))?;
            }
            let store = store.ok_or("IPropertyStore is None")?;
            let store_dir = std::env::temp_dir().join("exo-native-playready-store");
            let _ = std::fs::create_dir_all(&store_dir);
            let dir_str = store_dir.to_string_lossy().to_string();
            let pv_store = PROPVARIANT::from(dir_str.as_str());
            let pv_inpriv = PROPVARIANT::from(dir_str.as_str());
            unsafe {
                store
                    .SetValue(&PKEY_CDM_STOREPATH, &pv_store)
                    .map_err(|e| format!("SetValue STOREPATH: {e}"))?;
                store
                    .SetValue(&PKEY_CDM_INPRIVATE_STOREPATH, &pv_inpriv)
                    .map_err(|e| format!("SetValue INPRIVATE: {e}"))?;
            }
            let chosen_ks_h = HSTRING::from(chosen_ks.as_str());
            let cdm = unsafe {
                cdm_factory.CreateContentDecryptionModuleAccess(PCWSTR(chosen_ks_h.as_ptr()), &[])
            }
            .and_then(|acc| unsafe { acc.CreateContentDecryptionModule(&store) })
            .map_err(|e| format!("CreateContentDecryptionModule: {e}"))?;
            let svc: IMFGetService = cdm
                .cast()
                .map_err(|e| format!("cast CDM IMFGetService: {e}"))?;
            let pmp_host: IMFPMPHost = unsafe {
                svc.GetService(&MF_CONTENTDECRYPTIONMODULE_SERVICE)
                    .map_err(|e| format!("GetService(IMFPMPHost): {e}"))?
            };
            let pmp_app: IMFPMPHostApp = PmpHostApp { pmp_host }.into();
            unsafe {
                cdm.SetPMPHostApp(&pmp_app)
                    .map_err(|e| format!("SetPMPHostApp: {e}"))?;
            }
            let pmp_server: IMediaProtectionPMPServer = unsafe {
                svc.GetService(&MF_CONTENTDECRYPTIONMODULE_SERVICE)
                    .map_err(|e| format!("GetService(IMediaProtectionPMPServer): {e}"))?
            };
            let prot_mgr: IMFContentProtectionManager =
                HybridProtectionManager::create(&pmp_server)
                    .map_err(|e| format!("HybridProtectionManager: {e}"))?;

            let cb_state = Arc::new(CdmSessionState::default());
            let cbs: IMFContentDecryptionModuleSessionCallbacks = CdmCallbacks {
                state: Arc::clone(&cb_state),
            }
            .into();
            let cdm_session = unsafe {
                cdm.CreateSession(MF_MEDIAKEYSESSION_TYPE_TEMPORARY, &cbs)
                    .map_err(|e| format!("CreateSession: {e}"))?
            };
            if let Some(pro_b64) = parsed.video.playready_pro_b64.as_deref()
                && let Some(pro_bytes) = decode_b64(pro_b64)
            {
                let pssh = build_playready_pssh(&pro_bytes);
                let init_type = HSTRING::from("cenc");
                unsafe {
                    cdm_session
                        .GenerateRequest(PCWSTR(init_type.as_ptr()), &pssh)
                        .map_err(|e| format!("GenerateRequest: {e}"))?;
                }
                let deadline = Instant::now() + Duration::from_secs(6);
                let challenge = {
                    let mut g = cb_state.cvar.0.lock().unwrap();
                    loop {
                        if let Some(c) = cb_state.challenge.lock().unwrap().take() {
                            break Some(c);
                        }
                        let rem = deadline.saturating_duration_since(Instant::now());
                        if rem.is_zero() {
                            break None;
                        }
                        let (ng, _) = cb_state.cvar.1.wait_timeout(g, rem).unwrap();
                        g = ng;
                    }
                };
                if let Some(raw_challenge) = challenge {
                    let soap = extract_playready_soap(&raw_challenge);
                    let lic_resp = http_post_playready(la_url, &soap)?;
                    unsafe {
                        cdm_session
                            .Update(&lic_resp)
                            .map_err(|e| format!("cdm_session.Update: {e}"))?;
                    }
                }
            }
            let raw_source: IMFMediaSource = mse
                .cast()
                .map_err(|e| format!("cast mse IMFMediaSource: {e}"))?;
            let trusted = unsafe {
                cdm.CreateTrustedInput(&[])
                    .map_err(|e| format!("CreateTrustedInput: {e}"))?
            };
            let proxy: IMFMediaSource = TrustedMediaSourceProxy {
                inner: raw_source,
                trusted,
                itas: Mutex::new(HashMap::new()),
            }
            .into();
            trusted_source_opt = Some(SendCom(proxy));
            cdm_keep = Some(cdm);
            session_keep = Some(cdm_session);
            pmp_keep = Some(pmp_app);
            prot_mgr_keep = Some(prot_mgr);
        }

        let ext: IMFMediaEngineExtension = MseEngineExtension {
            mse: SendCom(mse.clone()),
            trusted_source: Mutex::new(trusted_source_opt),
        }
        .into();

        let mut attrs: Option<IMFAttributes> = None;
        unsafe {
            MFCreateAttributes(&mut attrs, 6).map_err(|e| format!("MFCreateAttributes: {e}"))?;
        }
        let attrs = attrs.ok_or("attrs is None")?;
        let notify_state = Arc::new(EngineNotifyState::default());
        let notify: IMFMediaEngineNotify = NotifyImpl {
            state: Arc::clone(&notify_state),
        }
        .into();
        unsafe {
            attrs
                .SetUnknown(&MF_MEDIA_ENGINE_CALLBACK, &notify)
                .map_err(|e| format!("SetUnknown CALLBACK: {e}"))?;
            attrs
                .SetUnknown(&MF_MEDIA_ENGINE_DXGI_MANAGER, &dxgi_mgr)
                .map_err(|e| format!("SetUnknown DXGI_MANAGER: {e}"))?;
            attrs
                .SetUnknown(&MF_MEDIA_ENGINE_EXTENSION, &ext)
                .map_err(|e| format!("SetUnknown EXTENSION: {e}"))?;
            if hwnd_raw != 0 {
                let _ = attrs.SetUINT64(&MF_MEDIA_ENGINE_OPM_HWND, hwnd_raw as u64);
            }
        }
        let engine = unsafe {
            factory
                .CreateInstance(MF_MEDIA_ENGINE_REAL_TIME_MODE.0 as u32, &attrs)
                .map_err(|e| format!("CreateInstance(IMFMediaEngine): {e}"))?
        };
        let engine_ex: IMFMediaEngineEx = engine
            .cast()
            .map_err(|e| format!("cast IMFMediaEngineEx: {e}"))?;
        unsafe {
            engine_ex
                .EnableWindowlessSwapchainMode(true)
                .map_err(|e| format!("EnableWindowlessSwapchainMode: {e}"))?;
            let _ = engine.SetMuted(muted);
        }

        if let Some(ref prot) = prot_mgr_keep
            && let Ok(prot_content) = engine.cast::<IMFMediaEngineProtectedContent>()
        {
            unsafe {
                prot_content
                    .SetContentProtectionManager(prot)
                    .map_err(|e| format!("SetContentProtectionManager: {e}"))?;
            }
        }

        let src_url = if playready_la.is_some() {
            "trusted://exo"
        } else {
            "mse://exo"
        };
        unsafe {
            engine
                .SetSource(&BSTR::from(src_url))
                .map_err(|e| format!("SetSource({src_url}): {e}"))?;
        }
        if playready_la.is_none() {
            let deadline = Instant::now() + Duration::from_secs(5);
            let mut g = mse_state.cvar.0.lock().unwrap();
            while !mse_state.opened.load(Ordering::SeqCst) {
                let rem = deadline.saturating_duration_since(Instant::now());
                if rem.is_zero() {
                    break;
                }
                let (ng, _) = mse_state.cvar.1.wait_timeout(g, rem).unwrap();
                g = ng;
            }
        }

        let buf_state = Arc::new(BufState::default());
        let buf_notify: IMFSourceBufferNotify = BufNotifyImpl {
            state: Arc::clone(&buf_state),
        }
        .into();
        let v_type = if parsed.video.codecs.is_empty() {
            parsed.video.mime_type.clone()
        } else {
            format!(
                "{};codecs=\"{}\"",
                parsed.video.mime_type, parsed.video.codecs
            )
        };
        let v_bstr = BSTR::from(v_type.as_str());
        let v_buf = unsafe {
            mse.AddSourceBuffer(&v_bstr, &buf_notify)
                .map_err(|e| format!("AddSourceBuffer(video): {e}"))?
        };
        let init_bytes = http_get(&parsed.video.init_url)?;
        append_bytes(&v_buf, &buf_state, &init_bytes)?;
        for seg_url in parsed.video.segment_urls.iter().take(4) {
            if shutdown.load(Ordering::SeqCst) {
                return Ok(());
            }
            let seg_bytes = http_get(seg_url)?;
            append_bytes(&v_buf, &buf_state, &seg_bytes)?;
        }

        let dst = RECT {
            left: 0,
            top: 0,
            right: width.max(16),
            bottom: height.max(16),
        };
        unsafe {
            let _ = engine_ex.UpdateVideoStream(None, Some(&dst), None);
            let _ = engine.Play();
        }

        if let Ok(swapchain_handle) = unsafe { engine_ex.GetVideoSwapchainHandle() }
            && !swapchain_handle.is_invalid()
        {
            surface.set_external_layer(Some(ExternalLayer {
                id: next_layer_id(),
                handle: swapchain_handle.0 as usize,
            }));
        }

        if let Ok(opm) = engine.cast::<IMFMediaEngineOPMInfo>() {
            let mut status = MF_MEDIA_ENGINE_OPM_STATUS::default();
            let mut constricted = windows_core::BOOL(0);
            if unsafe { opm.GetOPMInfo(&mut status, &mut constricted) }.is_ok() {
                shared.lock().unwrap().opm_status = Some(format!("{status:?}"));
            }
        }
        shared.lock().unwrap().state = NativeMediaState::Playing;
        if let Some(w) = waker {
            w();
        }

        loop {
            if shutdown.load(Ordering::SeqCst) {
                break;
            }
            match rx.recv_timeout(Duration::from_millis(60)) {
                Ok(SessionCmd::Play) => unsafe {
                    let _ = engine.Play();
                    shared.lock().unwrap().state = NativeMediaState::Playing;
                },
                Ok(SessionCmd::Pause) => unsafe {
                    let _ = engine.Pause();
                    shared.lock().unwrap().state = NativeMediaState::Paused;
                },
                Ok(SessionCmd::SetMuted(m)) => unsafe {
                    let _ = engine.SetMuted(m);
                },
                Ok(SessionCmd::Seek(t)) => unsafe {
                    let _ = engine.SetCurrentTime(t);
                },
                Ok(SessionCmd::Resize(w, h)) => unsafe {
                    let r = RECT {
                        left: 0,
                        top: 0,
                        right: w.max(16),
                        bottom: h.max(16),
                    };
                    let _ = engine_ex.UpdateVideoStream(None, Some(&r), None);
                },
                Ok(SessionCmd::Stop) | Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                    break;
                }
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                    let t = unsafe { engine.GetCurrentTime() };
                    let d = unsafe { engine.GetDuration() };
                    let mut g = shared.lock().unwrap();
                    if t.is_finite() {
                        g.current_time = t;
                    }
                    if d.is_finite() && d > 0.0 {
                        g.duration = Some(d);
                    }
                }
            }
        }

        surface.set_external_layer(None);
        unsafe {
            let _ = engine.Shutdown();
        }
        drop(session_keep);
        drop(prot_mgr_keep);
        drop(pmp_keep);
        drop(cdm_keep);
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn classifies_clear_hls_dash_and_playready_sources() {
        assert_eq!(
            NativeMediaSource::classify("https://cdn.example/video/master.m3u8", None),
            NativeMediaSource::ClearHls("https://cdn.example/video/master.m3u8".into())
        );
        assert_eq!(
            NativeMediaSource::classify("https://cdn.example/video/stream.mpd", None),
            NativeMediaSource::ClearDash {
                mpd_url: "https://cdn.example/video/stream.mpd".into()
            }
        );
        assert_eq!(
            NativeMediaSource::classify(
                "https://cdn.example/video/stream.mpd",
                Some("https://drm.example/rightsmanager.asmx")
            ),
            NativeMediaSource::PlayReadyDash {
                mpd_url: "https://cdn.example/video/stream.mpd".into(),
                license_url: "https://drm.example/rightsmanager.asmx".into(),
                security_level: 3000,
            }
        );
        assert!(!NativeMediaSource::ClearHls("x.m3u8".into()).uses_mf_dcomp());
        assert!(
            NativeMediaSource::ClearDash {
                mpd_url: "x.mpd".into()
            }
            .uses_mf_dcomp()
        );
    }

    #[test]
    fn parses_dash_segment_template_and_playready_pssh() {
        let xml = r#"<MPD><Period>
            <AdaptationSet mimeType="video/mp4" codecs="avc1.640028">
                <ContentProtection schemeIdUri="urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95"><mspr:pro>AQIDBA==</mspr:pro></ContentProtection>
                <SegmentTemplate initialization="init-$RepresentationID$.mp4" media="seg-$RepresentationID$-$Number%04d$.m4s" startNumber="1" />
                <Representation id="1080p" bandwidth="4500000" width="1920" height="1080" />
            </AdaptationSet>
        </Period></MPD>"#;
        let parsed = parse_dash_mpd("https://cdn.test/v/manifest.mpd", xml, 2).unwrap();
        assert_eq!(parsed.video.width, 1920);
        assert_eq!(parsed.video.init_url, "https://cdn.test/v/init-1080p.mp4");
        assert_eq!(
            parsed.video.segment_urls,
            vec![
                "https://cdn.test/v/seg-1080p-0001.m4s",
                "https://cdn.test/v/seg-1080p-0002.m4s"
            ]
        );
        assert_eq!(parsed.video.playready_pro_b64.as_deref(), Some("AQIDBA=="));

        let pssh = build_playready_pssh(&[1, 2, 3, 4]);
        assert_eq!(&pssh[4..8], b"pssh");
        assert_eq!(pssh.len(), 36);
    }

    #[test]
    fn parses_webvtt_cues_and_strips_tags() {
        let vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.500\n<i>Subtitles:</i> Bip!\n";
        let cues = parse_webvtt(vtt);
        assert_eq!(cues.len(), 1);
        assert_eq!(cues[0].text, "Subtitles: Bip!");
        assert!((cues[0].start_sec - 1.0).abs() < 1e-6);
        assert!((cues[0].end_sec - 3.5).abs() < 1e-6);
    }
}
