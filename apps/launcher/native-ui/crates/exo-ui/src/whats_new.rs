use crate::events::{EventRegistry, HostEventKind};
use crate::notice::{NoticeActionKind, NoticeActionSpec, RichNoticeSpec};
use crate::settings::set_btn;
use crate::state::{AppState, NoticeTone, Room};
use exo_kit::{
    BG, FG, RADIUS_PILL, RADIUS_SHEET, SPRING_BASE, bad, fg_muted, glass_button, glass_panel_opt,
    icon_app_window, icon_chat, icon_download, icon_folder, icon_gamepad, icon_hard_drive,
    icon_key, icon_library, icon_mic, icon_monitor, icon_mouse, icon_music, icon_notes, icon_play,
    icon_refresh, icon_scissors, icon_search, icon_server, icon_settings, icon_shield,
    icon_sparkles, icon_store, icon_trash, icon_volume, icon_zap, modal_card_transition,
    primary_button, primary_button_dyn, progress_bar, scaled, scrim_fill, selected_fill, type_body,
    type_small, type_title,
};
use exo_rpc::{HostEvent, UpdateProgress};
use rune::{Cursor, El, ScrollSpec, TextStyle, Transition, col, el, row, text};
use serde_json::{Value, json};
use std::sync::OnceLock;

const WHATS_NEW_JSON: &str = include_str!("../../../../ui/src/lib/whatsNew.json");

fn read_gzip_changelog(path: &std::path::Path) -> Option<String> {
    use std::io::Read;
    let file = std::fs::File::open(path).ok()?;
    let mut decoder = flate2::read::GzDecoder::new(file);
    let mut out = String::new();
    decoder.read_to_string(&mut out).ok()?;
    Some(out)
}

fn load_changelog_md() -> &'static str {
    static CACHE: OnceLock<String> = OnceLock::new();
    CACHE.get_or_init(|| {
        if let Ok(exe) = std::env::current_exe()
            && let Some(dir) = exe.parent()
        {
            for rel in ["wwwroot/changelog.gz", "changelog.gz"] {
                if let Some(text) = read_gzip_changelog(&dir.join(rel)) {
                    return text;
                }
            }
            if let Ok(text) = std::fs::read_to_string(dir.join("CHANGELOG.md")) {
                return text;
            }
            let mut ancestor = Some(dir);
            for _ in 0..8 {
                if let Some(a) = ancestor {
                    if let Ok(text) = std::fs::read_to_string(a.join("CHANGELOG.md")) {
                        return text;
                    }
                    if let Some(text) =
                        read_gzip_changelog(&a.join("ExoLauncher/wwwroot/changelog.gz"))
                    {
                        return text;
                    }
                    ancestor = a.parent();
                }
            }
        }
        if let Some(manifest_dir) = option_env!("CARGO_MANIFEST_DIR") {
            let dev_path = std::path::Path::new(manifest_dir).join("../../../../CHANGELOG.md");
            if let Ok(text) = std::fs::read_to_string(dev_path) {
                return text;
            }
        }
        String::new()
    })
}

pub const BOOT_HOLD_SECS: f64 = 0.8;

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct WhatsNewItemWire {
    pub icon: String,
    pub title: String,
    pub detail: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct WhatsNewRelease {
    pub version: String,
    pub items: Vec<WhatsNewItemWire>,
}

impl WhatsNewRelease {
    pub fn from_json(v: &Value) -> Option<Self> {
        let version = v.get("version").and_then(|s| s.as_str())?.to_string();
        let items = v
            .get("items")
            .and_then(|a| a.as_array())
            .map(|arr| {
                arr.iter()
                    .map(|it| WhatsNewItemWire {
                        icon: it
                            .get("icon")
                            .and_then(|s| s.as_str())
                            .unwrap_or("New")
                            .to_string(),
                        title: it
                            .get("title")
                            .and_then(|s| s.as_str())
                            .unwrap_or("")
                            .to_string(),
                        detail: it
                            .get("detail")
                            .and_then(|s| s.as_str())
                            .unwrap_or("")
                            .to_string(),
                    })
                    .collect()
            })
            .unwrap_or_default();
        Some(Self { version, items })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UpdatePhase {
    Checking,
    Idle,
    Available,
    Installing,
    Restarting,
    Failed,
    Waiting,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WhatsNewMode {
    Missed,
    All,
}

#[derive(Debug, Clone, Default)]
pub struct AppUpdateReply {
    pub ok: bool,
    pub update_available: bool,
    pub already_latest: bool,
    pub latest: Option<String>,
    pub state: Option<String>,
    pub message: Option<String>,
    pub problem: Option<String>,
    pub offer_on_open: bool,
    pub not_now: bool,
    pub staged: bool,
    pub installed: bool,
    pub should_exit: bool,
    pub upcoming: Option<WhatsNewRelease>,
}

impl AppUpdateReply {
    pub fn from_json(v: &Value) -> Self {
        Self {
            ok: v.get("ok").and_then(|b| b.as_bool()).unwrap_or(false),
            update_available: v
                .get("updateAvailable")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
            already_latest: v
                .get("alreadyLatest")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
            latest: v
                .get("latest")
                .and_then(|s| s.as_str())
                .filter(|s| !s.is_empty())
                .map(|s| s.to_string()),
            state: v
                .get("state")
                .and_then(|s| s.as_str())
                .map(|s| s.to_string()),
            message: v
                .get("message")
                .and_then(|s| s.as_str())
                .map(|s| s.to_string()),
            problem: v
                .get("problem")
                .and_then(|s| s.as_str())
                .filter(|s| !s.is_empty())
                .map(|s| s.to_string()),
            offer_on_open: v
                .get("offerOnOpen")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
            not_now: v.get("notNow").and_then(|b| b.as_bool()).unwrap_or(false),
            staged: v.get("staged").and_then(|b| b.as_bool()).unwrap_or(false),
            installed: v
                .get("installed")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
            should_exit: v
                .get("shouldExit")
                .and_then(|b| b.as_bool())
                .unwrap_or(false),
            upcoming: v.get("upcoming").and_then(WhatsNewRelease::from_json),
        }
    }
}

#[derive(Debug, Clone)]
pub struct WhatsNewState {
    pub mode: Option<WhatsNewMode>,
    pub auto_shown: bool,
    pub current_version: Option<String>,
    pub seen_version: Option<String>,
    pub host_state: String,
    pub answered: bool,
    pub asking: bool,
    pub installing: bool,
    pub boot_hold_active: bool,
    pub latest: Option<String>,
    pub upcoming: Option<WhatsNewRelease>,
    pub sheet_open: bool,
    pub not_now: bool,
    pub message: Option<String>,
    pub problem: Option<String>,
    pub dismissed_notice: bool,
    pub percent: Option<f64>,
    pub status: String,
}

impl Default for WhatsNewState {
    fn default() -> Self {
        Self {
            mode: None,
            auto_shown: false,
            current_version: None,
            seen_version: None,
            host_state: "idle".into(),
            answered: true,
            asking: false,
            installing: false,
            boot_hold_active: false,
            latest: None,
            upcoming: None,
            sheet_open: false,
            not_now: false,
            message: None,
            problem: None,
            dismissed_notice: false,
            percent: None,
            status: "Preparing…".into(),
        }
    }
}

impl WhatsNewState {
    pub fn for_host_pipe() -> Self {
        Self {
            host_state: "checking".into(),
            answered: false,
            boot_hold_active: true,
            ..Self::default()
        }
    }

    pub fn phase(&self) -> UpdatePhase {
        if self.answered || self.host_state != "checking" {
            update_phase(&self.host_state, self.latest.is_some(), self.asking)
        } else {
            UpdatePhase::Checking
        }
    }

    pub fn busy(&self) -> bool {
        matches!(
            self.phase(),
            UpdatePhase::Installing | UpdatePhase::Restarting
        )
    }

    pub fn available(&self) -> bool {
        matches!(
            self.phase(),
            UpdatePhase::Available | UpdatePhase::Failed | UpdatePhase::Waiting
        )
    }

    pub fn error_text(&self) -> Option<String> {
        if self.phase() == UpdatePhase::Failed && !self.dismissed_notice {
            Some(
                self.message
                    .clone()
                    .unwrap_or_else(|| "The update didn’t install. Try again.".into()),
            )
        } else {
            None
        }
    }

    pub fn waiting_text(&self) -> Option<String> {
        if self.phase() == UpdatePhase::Waiting && !self.dismissed_notice {
            Some(self.message.clone().unwrap_or_else(|| {
                "Your update is ready and goes in the next time Exo opens.".into()
            }))
        } else {
            None
        }
    }

    pub fn holds_boot(&self) -> bool {
        self.boot_hold_active && self.phase() == UpdatePhase::Checking
    }
}

pub fn is_valid_update_state(state: &str) -> bool {
    matches!(
        state,
        "idle"
            | "checking"
            | "downloading"
            | "waitingForGame"
            | "ready"
            | "applying"
            | "restarting"
            | "failed"
    )
}

pub fn update_phase(state: &str, offered: bool, asking: bool) -> UpdatePhase {
    match state {
        "checking" => UpdatePhase::Checking,
        "downloading" | "applying" => UpdatePhase::Installing,
        "restarting" => UpdatePhase::Restarting,
        "waitingForGame" => UpdatePhase::Waiting,
        "ready" => {
            if asking {
                UpdatePhase::Installing
            } else {
                UpdatePhase::Waiting
            }
        }
        "failed" => UpdatePhase::Failed,
        _ => {
            if offered {
                UpdatePhase::Available
            } else {
                UpdatePhase::Idle
            }
        }
    }
}

pub fn sheet_after_check(sheet: bool, ok: bool, update_available: bool) -> bool {
    sheet && ok && update_available
}

pub fn update_words(status: &str, percent: Option<f64>) -> (String, Option<f32>) {
    let downloading = status.starts_with("Preparing") || status.starts_with("Downloading");
    if !downloading {
        return ("Installing".into(), None);
    }
    if let Some(p) = percent
        && p.is_finite()
    {
        let clamped = p.clamp(0.0, 100.0);
        return (
            format!("Downloading · {}%", clamped.round() as i32),
            Some((clamped / 100.0) as f32),
        );
    }
    if let Some(mb_pos) = status.find(" MB") {
        let prefix = &status[..mb_pos];
        let num_start = prefix
            .rfind(|c: char| !c.is_ascii_digit() && c != '.')
            .map(|i| i + 1)
            .unwrap_or(0);
        let num = &prefix[num_start..];
        if !num.is_empty() {
            return (format!("Downloading · {num} MB"), None);
        }
    }
    ("Downloading".into(), None)
}

pub fn compare_versions(a: &str, b: &str) -> std::cmp::Ordering {
    let parse = |v: &str| -> Vec<u32> {
        let head = v
            .split(|c: char| !c.is_ascii_digit() && c != '.')
            .next()
            .unwrap_or("");
        head.split('.')
            .map(|p| p.parse::<u32>().unwrap_or(0))
            .collect()
    };
    let x = parse(a);
    let y = parse(b);
    let len = x.len().max(y.len());
    for i in 0..len {
        let xi = x.get(i).copied().unwrap_or(0);
        let yi = y.get(i).copied().unwrap_or(0);
        if xi != yi {
            return xi.cmp(&yi);
        }
    }
    std::cmp::Ordering::Equal
}

pub fn bundled_releases() -> &'static [WhatsNewRelease] {
    static CACHE: OnceLock<Vec<WhatsNewRelease>> = OnceLock::new();
    CACHE.get_or_init(|| {
        serde_json::from_str::<Value>(WHATS_NEW_JSON)
            .ok()
            .and_then(|v| v.as_array().cloned())
            .map(|arr| arr.iter().filter_map(WhatsNewRelease::from_json).collect())
            .unwrap_or_default()
    })
}

pub fn parse_changelog_md(src: &str) -> Vec<WhatsNewRelease> {
    let mut releases: Vec<WhatsNewRelease> = Vec::new();
    for line in src.lines() {
        if let Some(rest) = line.strip_prefix("## ") {
            let ver = rest.split_whitespace().next().unwrap_or("");
            if ver.split('.').count() == 3
                && ver
                    .split('.')
                    .all(|p| !p.is_empty() && p.chars().all(|c| c.is_ascii_digit()))
            {
                releases.push(WhatsNewRelease {
                    version: ver.to_string(),
                    items: Vec::new(),
                });
            }
        } else if let Some(entry_raw) = line.strip_prefix("- ")
            && let Some(cur) = releases.last_mut()
        {
            let entry = strip_verification_suffix(entry_raw).trim();
            let (title, detail) = if let Some(after) = entry.strip_prefix("**")
                && let Some(end) = after.find("**")
            {
                let t = strip_md_inline(&after[..end]);
                let d = strip_md_inline(after[end + 2..].trim());
                (t.trim_end_matches('.').to_string(), d)
            } else {
                (
                    strip_md_inline(entry).trim_end_matches('.').to_string(),
                    String::new(),
                )
            };
            if !title.is_empty() {
                cur.items.push(WhatsNewItemWire {
                    icon: "New".into(),
                    title,
                    detail,
                });
            }
        }
    }
    releases
        .into_iter()
        .filter(|r| !r.items.is_empty())
        .collect()
}

fn strip_verification_suffix(s: &str) -> &str {
    let trimmed = s.trim_end();
    if let Some(body) = trimmed.strip_suffix('_')
        && let Some(start) = body.rfind('_')
    {
        let tag = &body[start + 1..];
        if tag.starts_with("Verified") || tag.starts_with("Checked") {
            return body[..start].trim_end();
        }
    }
    trimmed
}

fn strip_md_inline(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    let chars: Vec<char> = s.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if chars[i] == '['
            && let Some(close_b) = chars[i + 1..].iter().position(|&c| c == ']')
        {
            let label_end = i + 1 + close_b;
            if label_end + 1 < chars.len()
                && chars[label_end + 1] == '('
                && let Some(close_p) = chars[label_end + 2..].iter().position(|&c| c == ')')
            {
                for &ch in &chars[i + 1..label_end] {
                    if ch != '`' {
                        out.push(ch);
                    }
                }
                i = label_end + 2 + close_p + 1;
                continue;
            }
        }
        if chars[i] == '*' && i + 1 < chars.len() && chars[i + 1] == '*' {
            i += 2;
            continue;
        }
        if chars[i] == '`' {
            i += 1;
            continue;
        }
        out.push(chars[i]);
        i += 1;
    }
    out
}

pub fn missed_releases(version: &str, seen: Option<&str>) -> Vec<WhatsNewRelease> {
    if seen == Some(version) {
        return Vec::new();
    }
    let all = bundled_releases();
    if let Some(prev) = seen {
        all.iter()
            .filter(|r| {
                compare_versions(&r.version, prev) == std::cmp::Ordering::Greater
                    && compare_versions(&r.version, version) != std::cmp::Ordering::Greater
            })
            .cloned()
            .collect()
    } else {
        all.iter()
            .filter(|r| r.version == version)
            .cloned()
            .collect()
    }
}

pub fn full_changelog(version: &str) -> Vec<WhatsNewRelease> {
    let mut told: Vec<WhatsNewRelease> = bundled_releases()
        .iter()
        .filter(|r| compare_versions(&r.version, version) != std::cmp::Ordering::Greater)
        .cloned()
        .collect();
    if told.is_empty() {
        told = bundled_releases().to_vec();
    }
    let known: std::collections::HashSet<String> = told.iter().map(|r| r.version.clone()).collect();
    let older = parse_changelog_md(load_changelog_md())
        .into_iter()
        .filter(|r| {
            !known.contains(&r.version)
                && compare_versions(&r.version, version) != std::cmp::Ordering::Greater
        });
    told.extend(older);
    told
}

pub fn register_events(reg: &mut EventRegistry) {
    reg.on(HostEventKind::AppUpdateState, |state, ev| {
        let HostEvent::AppUpdateState(s) = ev else {
            return false;
        };
        state.accept_update_host_state(&s.state);
        state.sync_update_notices();
        true
    });

    reg.on(HostEventKind::UpdateProgress, |state, ev| {
        let HostEvent::UpdateProgress(p) = ev else {
            return false;
        };
        state.accept_update_progress(p);
        state.sync_update_notices();
        true
    });

    reg.on_raw("versions.new", |state, data| {
        let id = data.get("id").and_then(|v| v.as_str()).unwrap_or("");
        let name = data.get("name").and_then(|v| v.as_str()).unwrap_or("");
        let version = data.get("version").and_then(|v| v.as_str()).unwrap_or("");
        if id.is_empty() || name.is_empty() || version.is_empty() {
            return false;
        }
        let installed = data.get("installed").and_then(|v| v.as_str());
        let page = data
            .get("page")
            .and_then(|v| v.as_str())
            .unwrap_or("drivers")
            .to_string();
        let body = match installed {
            Some(cur) if !cur.is_empty() => format!("Out now. You have {cur}."),
            _ => "Out now.".into(),
        };
        let notice_id = format!("versions-{id}");
        state.push_rich_notice(RichNoticeSpec {
            key_id: Some(notice_id.clone()),
            title: Some(format!("{name} {version}")),
            text: body,
            sticky: Some(true),
            action: Some(NoticeActionSpec {
                label: "Open".into(),
                primary: true,
                danger: false,
                busy: false,
                confirm: None,
                kind: NoticeActionKind::OpenPcPage {
                    notice_key: notice_id,
                    page,
                },
            }),
            ..RichNoticeSpec::default()
        });
        true
    });
}

impl AppState {
    pub fn accept_update_host_state(&mut self, next: &str) {
        if !is_valid_update_state(next) {
            return;
        }
        self.whats_new.host_state = next.to_string();
        self.whats_new.dismissed_notice = false;
        self.app_update_state = next.to_string();
    }

    pub fn accept_update_progress(&mut self, p: UpdateProgress) {
        if !self.whats_new.installing && !self.whats_new.busy() {
            return;
        }
        self.whats_new.percent = p.percent.filter(|v| v.is_finite() && *v >= 0.0);
        if !p.status.trim().is_empty() {
            self.whats_new.status = p.status;
        }
    }

    pub fn apply_update_check_json(&mut self, val: &Value, allow_sheet: bool) {
        let r = AppUpdateReply::from_json(val);
        if let Some(st) = r.state.as_deref() {
            self.accept_update_host_state(st);
        }
        self.whats_new.problem = r.problem;
        if r.ok && r.update_available {
            self.whats_new.latest = r.latest;
            self.whats_new.upcoming = r.upcoming;
            self.whats_new.not_now = r.not_now;
            if allow_sheet {
                self.whats_new.sheet_open = r.offer_on_open;
            }
        }
    }

    pub fn startup_check_update(&mut self) {
        self.whats_new.host_state = "checking".into();
        self.whats_new.answered = false;
        self.whats_new.boot_hold_active = true;
        #[cfg(windows)]
        self.call_host_async(
            "app.checkUpdate",
            json!({}),
            |s, res: Result<Value, String>| {
                s.whats_new.boot_hold_active = false;
                s.whats_new.answered = true;
                match res {
                    Ok(val) => {
                        let r = AppUpdateReply::from_json(&val);
                        if let Some(st) = r.state.as_deref() {
                            s.accept_update_host_state(st);
                        }
                        s.whats_new.problem = r.problem;
                        if r.ok && r.update_available {
                            s.whats_new.latest = r.latest;
                            s.whats_new.upcoming = r.upcoming;
                            s.whats_new.not_now = r.not_now;
                            s.whats_new.sheet_open = r.offer_on_open;
                        }
                    }
                    Err(_) => {
                        if s.whats_new.host_state == "checking" {
                            s.whats_new.host_state = "idle".into();
                        }
                    }
                }
                s.sync_update_notices();
                s.maybe_auto_open_whats_new();
            },
        );
    }

    pub fn manual_check_update(&mut self) {
        if self.whats_new.installing || self.whats_new.busy() {
            self.push_notice("An update is already being installed.", NoticeTone::Neutral);
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            "app.checkUpdate",
            json!({ "manual": true }),
            |s, res: Result<Value, String>| match res {
                Ok(val) => {
                    let r = AppUpdateReply::from_json(&val);
                    if let Some(st) = r.state.as_deref() {
                        s.accept_update_host_state(st);
                    }
                    s.whats_new.sheet_open =
                        sheet_after_check(s.whats_new.sheet_open, r.ok, r.update_available);
                    if !r.ok || (!r.update_available && !r.already_latest) {
                        let msg = r
                            .message
                            .unwrap_or_else(|| "Couldn’t check for updates. Try again.".into());
                        s.push_notice(msg, NoticeTone::Bad);
                        return;
                    }
                    if r.update_available {
                        let latest = r.latest.clone();
                        let msg = r.message.unwrap_or_else(|| {
                            format!(
                                "Version {} is available.",
                                latest.as_deref().unwrap_or("new")
                            )
                        });
                        s.whats_new.message = Some(msg);
                        s.whats_new.latest = latest;
                        s.whats_new.upcoming = r.upcoming;
                        s.sync_update_notices();
                    } else {
                        s.whats_new.message = None;
                        s.whats_new.latest = None;
                        let msg = r.message.unwrap_or_else(|| "You're up to date.".into());
                        s.push_notice(msg, NoticeTone::Good);
                    }
                }
                Err(_) => {
                    s.push_notice("Couldn’t check for updates. Try again.", NoticeTone::Bad);
                }
            },
        );
    }

    pub fn start_app_update_install(&mut self) {
        let phase = self.whats_new.phase();
        if self.whats_new.installing
            || self.whats_new.busy()
            || (self.whats_new.latest.is_none()
                && phase != UpdatePhase::Available
                && phase != UpdatePhase::Failed
                && phase != UpdatePhase::Waiting)
        {
            return;
        }
        self.whats_new.installing = true;
        self.whats_new.asking = true;
        self.whats_new.percent = None;
        self.whats_new.status = "Preparing…".into();
        self.sync_update_notices();
        #[cfg(windows)]
        self.call_host_async(
            "app.installUpdate",
            json!({}),
            |s, res: Result<Value, String>| {
                s.whats_new.asking = false;
                s.whats_new.installing = false;
                match res {
                    Ok(val) => {
                        let r = AppUpdateReply::from_json(&val);
                        if r.not_now {
                            s.whats_new.not_now = true;
                        }
                        if let Some(st) = r.state.as_deref() {
                            s.accept_update_host_state(st);
                        }
                        if r.ok && r.staged {
                            s.whats_new.sheet_open = false;
                            s.whats_new.message = Some(r.message.unwrap_or_else(|| {
                                "Your update is ready and goes in the next time Exo opens.".into()
                            }));
                        } else if r.ok && (r.should_exit || r.installed) {
                            s.whats_new.percent = None;
                            s.whats_new.status = "Restarting…".into();
                            s.whats_new.message =
                                Some(r.message.unwrap_or_else(|| "Restarting…".into()));
                        } else if r.already_latest {
                            s.whats_new.message = None;
                            s.whats_new.latest = None;
                            s.whats_new.sheet_open = false;
                        } else {
                            s.whats_new.message =
                                Some(r.message.unwrap_or_else(|| {
                                    "The update didn’t install. Try again.".into()
                                }));
                        }
                    }
                    Err(e) => {
                        s.whats_new.not_now = true;
                        s.whats_new.host_state = "failed".into();
                        s.whats_new.message = Some(if e.trim().is_empty() {
                            "The update didn’t install. Try again.".into()
                        } else {
                            e
                        });
                    }
                }
                s.sync_update_notices();
            },
        );
    }

    pub fn set_aside_update_sheet(&mut self) {
        self.whats_new.sheet_open = false;
        self.sync_update_notices();
    }

    pub fn sync_update_notices(&mut self) {
        if let Some(problem) = self.whats_new.problem.clone() {
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some("app-update-problem".into()),
                title: Some("Update didn’t install".into()),
                text: problem,
                tone: NoticeTone::Bad,
                sticky: Some(true),
                ..RichNoticeSpec::default()
            });
        } else {
            self.dismiss_notice_by_key("app-update-problem");
        }

        if self.whats_new.sheet_open {
            self.dismiss_notice_by_key("app-update");
            return;
        }

        if self.whats_new.busy() {
            let (words, share) = update_words(&self.whats_new.status, self.whats_new.percent);
            let title = self
                .whats_new
                .latest
                .as_deref()
                .map(|v| format!("Exo {v}"))
                .unwrap_or_else(|| "Updating Exo".into());
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some("app-update".into()),
                title: Some(title),
                text: words,
                progress: Some(share),
                sticky: Some(true),
                lasting: true,
                ..RichNoticeSpec::default()
            });
        } else if let Some(err) = self.whats_new.error_text() {
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some("app-update".into()),
                title: Some("Update failed".into()),
                text: err,
                tone: NoticeTone::Bad,
                sticky: Some(true),
                action: Some(NoticeActionSpec {
                    label: "Try again".into(),
                    primary: true,
                    danger: false,
                    busy: false,
                    confirm: None,
                    kind: NoticeActionKind::InstallAppUpdate,
                }),
                ..RichNoticeSpec::default()
            });
        } else if let Some(wait) = self.whats_new.waiting_text() {
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some("app-update".into()),
                title: Some("Update ready".into()),
                text: wait,
                tone: NoticeTone::Good,
                sticky: Some(true),
                ..RichNoticeSpec::default()
            });
        } else if self.whats_new.available()
            && let Some(ref latest) = self.whats_new.latest
        {
            self.push_rich_notice(RichNoticeSpec {
                key_id: Some("app-update".into()),
                title: Some(format!("Exo {latest}")),
                text: "Update available.".into(),
                sticky: Some(true),
                action: Some(NoticeActionSpec {
                    label: "Install".into(),
                    primary: true,
                    danger: false,
                    busy: false,
                    confirm: None,
                    kind: NoticeActionKind::InstallAppUpdate,
                }),
                ..RichNoticeSpec::default()
            });
        } else {
            self.dismiss_notice_by_key("app-update");
        }
    }

    pub fn open_whats_new_all(&mut self) {
        self.whats_new.auto_shown = true;
        self.whats_new.mode = Some(WhatsNewMode::All);
        self.sync_mouse_panel_visibility();
    }

    pub fn maybe_auto_open_whats_new(&mut self) {
        if self.whats_new.auto_shown
            || self.whats_new.sheet_open
            || !self.whats_new.answered
            || self.setup.open
            || self.palette_open
            || self.key_modal_open
            || self.downloads.open
            || self.room != Room::Library
            || self.open_game_id.is_some()
        {
            return;
        }
        let Some(ver) = self.whats_new.current_version.clone() else {
            return;
        };
        let seen = self.whats_new.seen_version.as_deref();
        if !missed_releases(&ver, seen).is_empty() {
            self.whats_new.auto_shown = true;
            self.whats_new.mode = Some(WhatsNewMode::Missed);
            self.sync_mouse_panel_visibility();
        }
    }

    pub fn close_whats_new(&mut self) {
        self.whats_new.mode = None;
        self.sync_mouse_panel_visibility();
        if let Some(ver) = self.whats_new.current_version.clone()
            && self.whats_new.seen_version.as_deref() != Some(ver.as_str())
        {
            self.whats_new.seen_version = Some(ver.clone());
            #[cfg(windows)]
            self.call_host_async(
                "settings.set",
                json!({ "whatsNewSeen": ver }),
                |_, _: Result<Value, String>| {},
            );
        }
    }

    pub fn whats_new_releases(&self) -> Vec<WhatsNewRelease> {
        let Some(mode) = self.whats_new.mode else {
            return Vec::new();
        };
        let ver = self
            .whats_new
            .current_version
            .as_deref()
            .or_else(|| bundled_releases().first().map(|r| r.version.as_str()))
            .unwrap_or("3.29.0");
        match mode {
            WhatsNewMode::Missed => missed_releases(ver, self.whats_new.seen_version.as_deref()),
            WhatsNewMode::All => full_changelog(ver),
        }
    }

    pub fn hydrate_version_from_settings_json(&mut self) {
        #[cfg(windows)]
        self.call_host_async(
            "settings.get",
            json!({}),
            |s, res: Result<Value, String>| {
                if let Ok(val) = res {
                    if let Some(v) = val.get("appVersion").and_then(|v| v.as_str()) {
                        s.whats_new.current_version = Some(v.to_string());
                    }
                    if let Some(seen) = val.get("whatsNewSeen").and_then(|v| v.as_str()) {
                        s.whats_new.seen_version = Some(seen.to_string());
                    }
                    s.maybe_auto_open_whats_new();
                }
            },
        );
    }
}

fn whats_new_icon(name: &str) -> El {
    let sz = 20.0;
    match name {
        "Library" => icon_library(sz, FG),
        "StoreBag" => icon_store(sz, FG),
        "Chats" | "Forum" | "Members" => icon_chat(sz, FG),
        "MusicNotes" | "Volume" => icon_music(sz, FG),
        "Mouse" => icon_mouse(sz, FG),
        "Settings" | "Calibrate" => icon_settings(sz, FG),
        "Controller" => icon_gamepad(sz, FG),
        "Performance" | "FrameGen" => icon_zap(sz, FG),
        "Download" | "AppUpdate" => icon_download(sz, FG),
        "Drive" => icon_hard_drive(sz, FG),
        "Folder" | "FolderOpen" => icon_folder(sz, FG),
        "Monitor" => icon_monitor(sz, FG),
        "AntiCheat" | "Encrypted" => icon_shield(sz, FG),
        "Mic" => icon_mic(sz, FG),
        "Key" => icon_key(sz, FG),
        "Notes" => icon_notes(sz, FG),
        "Scissors" => icon_scissors(sz, FG),
        "Search" => icon_search(sz, FG),
        "Server" => icon_server(sz, FG),
        "Trash" | "CleanUp" => icon_trash(sz, FG),
        "Refresh" => icon_refresh(sz, FG),
        "Live" | "Record" => icon_play(sz, FG),
        "ThisPc" => icon_app_window(sz, FG),
        "VolumeLow" => icon_volume(sz, FG),
        "Home" => icon_library(sz, FG),
        _ => icon_sparkles(sz, FG),
    }
}

pub fn whats_new_layers(s: &AppState, blur: bool) -> Vec<El> {
    if s.whats_new.mode.is_none() {
        return Vec::new();
    }
    let releases = s.whats_new_releases();
    let upcoming = if s.whats_new.available() || s.whats_new.busy() {
        s.whats_new.upcoming.clone()
    } else {
        None
    };
    if releases.is_empty() && upcoming.is_none() {
        return Vec::new();
    }

    let heading = match upcoming.as_ref() {
        Some(up) => format!("Coming in Exo {}", up.version),
        None => "Changelog".into(),
    };

    let mut sections: Vec<WhatsNewRelease> = Vec::new();
    if let Some(ref up) = upcoming {
        sections.push(up.clone());
    }
    sections.extend(releases);

    let mut list_col = col().w_full().gap(28.0).px(4.0);
    for (idx, release) in sections.iter().enumerate() {
        let line_h = scaled(13.0) * 1.35;
        let mut sec = col().w_full().gap(14.0);
        if idx > 0 || upcoming.is_none() {
            sec = sec.child(
                text(release.version.clone())
                    .font(TextStyle::new(scaled(13.0), 600.0).lh(line_h))
                    .color(fg_muted()),
            );
        }
        let mut items_col = col().w_full().gap(18.0);
        for item in &release.items {
            let icon_circle = el()
                .size(40.0, 40.0)
                .radius(RADIUS_PILL)
                .fill(selected_fill())
                .center()
                .child(whats_new_icon(&item.icon));
            let mut words = col().grow(1.0).min_w(0.0).gap(2.0).child(
                text(item.title.clone())
                    .font(TextStyle::new(scaled(13.0), 600.0).lh(line_h))
                    .color(FG)
                    .wrap(),
            );
            if !item.detail.is_empty() {
                words = words.child(
                    text(item.detail.clone())
                        .font(TextStyle::new(scaled(13.0), 400.0).lh(line_h))
                        .color(FG)
                        .wrap(),
                );
            }
            items_col = items_col.child(
                row()
                    .w_full()
                    .gap(14.0)
                    .center_y()
                    .child(icon_circle)
                    .child(words),
            );
        }
        sec = sec.child(items_col);
        list_col = list_col.child(sec);
    }

    let mut foot = row().w_full().gap(10.0);
    if upcoming.is_some() {
        let busy = s.whats_new.busy();
        let cont = set_btn("whats-new-continue", "Continue", None)
            .grow(1.0)
            .justify_center()
            .on_press(|s: &mut AppState| s.close_whats_new());
        let install_label = if busy {
            s.whats_new.status.clone()
        } else {
            "Install update".into()
        };
        let mut install = primary_button_dyn("whats-new-install", install_label, None)
            .grow(1.0)
            .justify_center()
            .disabled(busy)
            .opacity(if busy { 0.55 } else { 1.0 });
        if !busy {
            install = install.on_press(|s: &mut AppState| s.start_app_update_install());
        }
        foot = foot.child(cont).child(install);
    } else {
        foot = foot.child(
            primary_button("whats-new-continue", "Continue", None)
                .grow(1.0)
                .justify_center()
                .on_press(|s: &mut AppState| s.close_whats_new()),
        );
    }

    let mut card_inner = col()
        .w_full()
        .px(36.0)
        .pt(36.0)
        .pb(28.0)
        .gap(24.0)
        .child(
            row()
                .w_full()
                .justify_center()
                .child(text(heading).font(type_title()).color(FG)),
        )
        .child(
            col()
                .key("whats-new-list")
                .w_full()
                .max_h(scaled(588.0))
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .clip_fade_linear(0.0, 0.0, 0.0, 24.0)
                .child(list_col),
        );

    if upcoming.is_some()
        && let Some(err) = s.whats_new.error_text()
    {
        card_inner = card_inner.child(
            row()
                .w_full()
                .justify_center()
                .child(text(err).font(type_body()).color(bad()).wrap()),
        );
    }

    card_inner = card_inner.child(foot);

    let _ = blur;
    let card = col()
        .key("whats-new-sheet")
        .w(scaled(540.0))
        .role("dialog")
        .label("Changelog")
        .max_h(scaled(760.0))
        .radius(RADIUS_SHEET)
        .continuous_corners()
        .clip_rounded(RADIUS_SHEET)
        .hit()
        .cursor(Cursor::Default)
        .liquid_glass(exo_kit::exo_glass(exo_kit::ExoGlass::Sheet, false))
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .modal(false, |s: &mut AppState| s.close_whats_new())
        .modal_scrim(scrim_fill())
        .child(card_inner);

    let host = col()
        .key("whats-new-host")
        .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
        .pt(52.0)
        .center()
        .transition(Transition::none())
        .child(card);

    vec![host]
}

pub fn update_sheet_layers(s: &AppState, blur: bool) -> Vec<El> {
    if !s.whats_new.sheet_open {
        return Vec::new();
    }
    let version = s
        .whats_new
        .latest
        .as_deref()
        .map(|v| format!("Exo Launcher {v}"))
        .unwrap_or_else(|| "Exo Launcher".into());
    let busy = s.whats_new.busy();
    let failed_err = (!busy).then(|| s.whats_new.error_text()).flatten();

    let head = col()
        .w_full()
        .items_center()
        .gap(6.0)
        .child(text("Update available").font(type_title()).color(FG))
        .child(text(version).font(type_body()).color(fg_muted()));

    let mut inner = col()
        .w_full()
        .px(36.0)
        .pt(32.0)
        .pb(28.0)
        .gap(20.0)
        .child(head);

    if let Some(ref err) = failed_err {
        inner = inner.child(
            row()
                .w_full()
                .justify_center()
                .child(text(err.clone()).font(type_body()).color(bad()).wrap()),
        );
    }

    if busy {
        let (words, share) = update_words(&s.whats_new.status, s.whats_new.percent);
        let bar_frac = share.unwrap_or(0.35);
        inner = inner.child(
            col()
                .w_full()
                .gap(8.0)
                .items_center()
                .child(text(words).font(type_small()).color(fg_muted()))
                .child(progress_bar(scaled(348.0), bar_frac)),
        );
    } else {
        let mut foot = row().w_full().gap(10.0);
        if s.whats_new.not_now {
            foot = foot.child(
                glass_button("update-not-now", "Not now", None)
                    .grow(1.0)
                    .justify_center()
                    .on_press(|s: &mut AppState| s.set_aside_update_sheet()),
            );
        }
        foot = foot.child(
            primary_button(
                "update-now",
                if failed_err.is_some() {
                    "Try again"
                } else {
                    "Update now"
                },
                None,
            )
            .grow(1.0)
            .justify_center()
            .on_press(|s: &mut AppState| s.start_app_update_install()),
        );
        inner = inner.child(foot);
    }

    let card = glass_panel_opt("update-sheet", scaled(420.0), blur)
        .role("alertdialog")
        .label("Update available")
        .radius(RADIUS_SHEET)
        .clip_rounded(RADIUS_SHEET)
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .modal(false, |s: &mut AppState| s.set_aside_update_sheet())
        .modal_scrim(BG.alpha(0.56))
        .child(inner);

    let host = col()
        .key("update-sheet-host")
        .pin(Some(0.0), Some(52.0), Some(0.0), Some(0.0))
        .center()
        .transition(Transition::none())
        .child(card);

    vec![host]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compare_versions_orders_semver_and_ignores_suffixes() {
        assert_eq!(
            compare_versions("3.29.0", "3.28.9"),
            std::cmp::Ordering::Greater
        );
        assert_eq!(
            compare_versions("3.29.0-dev", "3.29.0"),
            std::cmp::Ordering::Equal
        );
        assert_eq!(
            compare_versions("3.10.0", "3.9.9"),
            std::cmp::Ordering::Greater
        );
    }

    #[test]
    fn missed_releases_filters_between_seen_and_current() {
        assert!(!bundled_releases().is_empty());
        assert!(missed_releases("3.29.0", Some("3.29.0")).is_empty());
        let first = missed_releases("3.29.0", None);
        assert_eq!(first.len(), 1);
        assert_eq!(first[0].version, "3.29.0");
        let range = missed_releases("3.29.0", Some("3.27.0"));
        assert!(range.iter().any(|r| r.version == "3.29.0"));
        assert!(range.iter().any(|r| r.version == "3.28.0"));
        assert!(!range.iter().any(|r| r.version == "3.27.0"));
    }

    #[test]
    fn update_phase_and_words_match_react_contract() {
        assert_eq!(
            update_phase("checking", false, false),
            UpdatePhase::Checking
        );
        assert_eq!(
            update_phase("downloading", true, false),
            UpdatePhase::Installing
        );
        assert_eq!(update_phase("ready", true, true), UpdatePhase::Installing);
        assert_eq!(update_phase("ready", true, false), UpdatePhase::Waiting);
        assert_eq!(update_phase("idle", true, false), UpdatePhase::Available);
        assert_eq!(update_phase("idle", false, false), UpdatePhase::Idle);

        let (w, share) = update_words("Downloading update", Some(54.2));
        assert_eq!(w, "Downloading · 54%");
        assert!((share.unwrap() - 0.542).abs() < 0.001);

        let (w2, share2) = update_words("Downloading 18.5 MB", None);
        assert_eq!(w2, "Downloading · 18.5 MB");
        assert!(share2.is_none());

        let (w3, share3) = update_words("Applying update", Some(100.0));
        assert_eq!(w3, "Installing");
        assert!(share3.is_none());
    }

    #[test]
    fn versions_new_event_pushes_actionable_notice() {
        let mut s = AppState::default();
        assert!(s.apply_host_event(HostEvent::Raw {
            event: "versions.new".into(),
            data: json!({
                "id": "nvidia",
                "name": "NVIDIA Driver",
                "version": "572.16",
                "installed": "566.36",
                "page": "drivers"
            }),
        }));
        let n = s.notices.back().expect("versions notice");
        assert_eq!(n.title.as_deref(), Some("NVIDIA Driver 572.16"));
        assert_eq!(n.text, "Out now. You have 566.36.");
        assert!(n.action.is_some());
    }

    #[test]
    fn lazy_changelog_loads_from_repo_and_gzip() {
        let releases = full_changelog("3.30.2");
        assert!(releases.len() >= 10);
        assert!(releases.iter().any(|r| r.version == "1.0.0"));
        use std::io::Write;
        let tmp =
            std::env::temp_dir().join(format!("exo-changelog-test-{}.gz", std::process::id()));
        {
            let f = std::fs::File::create(&tmp).expect("create temp gz");
            let mut enc = flate2::write::GzEncoder::new(f, flate2::Compression::default());
            enc.write_all(
                "# Changelog\n\n## 2.0.0 — 2026-01-01\n\n### Added\n- Fast path\n".as_bytes(),
            )
            .expect("write gz");
            enc.finish().expect("finish gz");
        }
        let decoded = read_gzip_changelog(&tmp).expect("decode gz");
        let _ = std::fs::remove_file(&tmp);
        let parsed = parse_changelog_md(&decoded);
        assert_eq!(parsed.len(), 1);
        assert_eq!(parsed[0].version, "2.0.0");
    }
}
