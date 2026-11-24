use crate::pc_state::PcTab;
use crate::state::{AppState, HostStatus, NoticeAction, NoticeTone, Room};
use exo_rpc::{
    ActionResult, ArtworkMutationResponse, CompressProgressEvent, CompressState,
    CompressStateReply, DequeueReply, DlssNewerItem, DlssNewerReply, DlssRunResult, DlssStatus,
    DlssStatusItem, FrameGenMod, Game, GameAchievementsEvent, GameContent, GameExtras,
    GameGetReply, GameGraphicsFeaturesState, GameOptimizeEvent, GameProperties,
    GamePropertySetReply, GameSettingsState, GpuGame, HdrGame, HdrState, InstallProgress,
    InstallResponse, KeepStatus, LauncherSettings, METHOD_ART_REFETCH, METHOD_ART_REPLACE,
    METHOD_ART_RESET, METHOD_COMPRESS_ACT, METHOD_COMPRESS_STATE, METHOD_DLSS_CONSENT,
    METHOD_DLSS_DOWNLOAD_NEWER, METHOD_DLSS_FRAME_GEN, METHOD_DLSS_NEWER, METHOD_DLSS_RESTORE,
    METHOD_DLSS_STATUS, METHOD_GAME_DEQUEUE, METHOD_GAME_EXTRAS, METHOD_GAME_GET,
    METHOD_GAME_GRAPHICS_FEATURES, METHOD_GAME_GRAPHICS_FEATURES_SET, METHOD_GAME_OPEN_FOLDER,
    METHOD_GAME_PROGRESS, METHOD_GAME_PROPERTIES, METHOD_GAME_PROPERTIES_SET, METHOD_GAME_REPAIR,
    METHOD_GAME_RESUME_DOWNLOAD, METHOD_GAME_SETTINGS, METHOD_GAME_STOP, METHOD_GAME_UNINSTALL,
    METHOD_GPU_GAME, METHOD_HDR_GAME, METHOD_HDR_STATE, METHOD_KEEP_STATUS, METHOD_SETTINGS_SET,
    METHOD_SHADERS_STATUS, METHOD_SHELL_OPEN_URL, OpenUrlReply, PropertyChoice, ShaderStatus,
    UninstallResponse,
};
use rune::{TextEdit, Tx};
use serde_json::json;
use std::collections::{HashMap, HashSet, VecDeque};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const SHEET_CACHE_CAP: usize = 64;
const DLSS_CACHE_CAP: usize = 512;
const DLSS_CACHE_TTL_SECS: f64 = 30.0 * 60.0;
const DLSS_PRELOAD_DELAY_SECS: f64 = 1.0;
const DLSS_PRELOAD_CONCURRENCY: usize = 2;
const DLSS_PRELOAD_BUDGET_SECS: f64 = 2.0;
const DLSS_PRELOAD_MAX_GAMES: usize = 8;
const DLSS_NEWER_DELAY_SECS: f64 = 20.0;
const MOVE_POLL_BASE_SECS: f64 = 2.0;
const MOVE_POLL_MAX_SECS: f64 = 8.0;
pub const SHADERS_RELEASES_URL: &str = "https://getexo.app/source";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PrimaryActionKind {
    Play,
    Install,
    Update,
    None,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ArtAction {
    Replace,
    Reset,
    Refetch,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum PropsSectionId {
    General,
    Files,
    Betas,
    Content,
    Artwork,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RowNote {
    pub text: String,
    pub bad: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct PendingChoice {
    pub row: String,
    pub key: String,
    pub value: String,
    pub question: String,
    pub danger: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LineWord {
    pub text: String,
    pub good: bool,
    pub bad: bool,
    pub working: bool,
}

impl LineWord {
    pub fn plain(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            good: false,
            bad: false,
            working: false,
        }
    }

    pub fn good(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            good: true,
            bad: false,
            working: false,
        }
    }

    pub fn bad(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            good: false,
            bad: true,
            working: false,
        }
    }

    pub fn working(text: impl Into<String>) -> Self {
        Self {
            text: text.into(),
            good: false,
            bad: false,
            working: true,
        }
    }
}

#[derive(Clone, Debug, Default)]
pub struct GamePageState {
    pub generation: u64,
    pub settings: Option<GameSettingsState>,
    pub settings_cache: HashMap<String, GameSettingsState>,
    pub settings_working: bool,
    pub settings_work_on: bool,
    pub settings_work_failed: bool,
    pub graphics_features: Option<GameGraphicsFeaturesState>,
    pub graphics_features_cache: HashMap<String, GameGraphicsFeaturesState>,
    pub graphics_features_busy: Option<String>,
    pub graphics_features_errors: HashMap<String, String>,
    pub keep_status: Option<KeepStatus>,
    pub dlss: Option<DlssStatus>,
    pub dlss_cache: HashMap<String, DlssStatus>,
    pub dlss_cached_at: HashMap<String, f64>,
    pub dlss_inflight: HashSet<String>,
    pub dlss_preloaded: bool,
    pub dlss_preload_pending: bool,
    pub dlss_preload_due: Option<f64>,
    pub dlss_preload_queue: VecDeque<String>,
    pub dlss_preload_active: usize,
    pub dlss_preload_deadline: Option<f64>,
    pub dlss_newer_checked: bool,
    pub dlss_newer_pending: bool,
    pub dlss_newer_due: Option<f64>,
    pub dlss_loading: bool,
    pub frame_gen: Option<FrameGenMod>,
    pub frame_gen_cache: HashMap<String, FrameGenMod>,
    pub gpu_game: Option<GpuGame>,
    pub gpu_game_cache: HashMap<String, GpuGame>,
    pub gpu_busy: bool,
    pub gpu_error: Option<String>,
    pub hdr_game: Option<HdrGame>,
    pub hdr_game_cache: HashMap<String, HdrGame>,
    pub hdr_busy: bool,
    pub hdr_error: Option<String>,
    pub shaders: Option<ShaderStatus>,
    pub shaders_cache: HashMap<String, ShaderStatus>,
    pub compress: Option<CompressState>,
    pub compress_error: Option<String>,
    pub achievements_cache: HashMap<String, (u32, u32)>,
    pub props_cache: HashMap<String, GameProperties>,
    pub props_load_error: Option<String>,
    pub props_busy: Option<String>,
    pub props_notes: HashMap<String, RowNote>,
    pub props_tab_id: Option<PropsSectionId>,
    pub beta_code: TextEdit,
    pub art_action: Option<ArtAction>,
    pub pending_choice: Option<PendingChoice>,
    pub primary_busy: bool,
    pub status_msg: Option<String>,
    pub selected_variant_id: Option<String>,
    pub move_poll_due: Option<f64>,
    pub move_poll_interval: f64,
    pub move_last_done: Option<u64>,
}

impl GamePageState {
    pub fn new() -> Self {
        Self {
            beta_code: TextEdit::default().secure(),
            move_poll_interval: MOVE_POLL_BASE_SECS,
            ..Self::default()
        }
    }

    pub fn demo() -> Self {
        let mut s = Self::new();
        let demo_settings = GameSettingsState {
            ok: true,
            supported: Some(true),
            title: Some("Cyberpunk 2077".into()),
            running: Some(false),
            found: Some(true),
            waiting: Some(false),
            standing: Some("applied".into()),
            again: Some(false),
            window_mode: Some("borderless".into()),
            smooth_motion: None,
            ..Default::default()
        };
        let demo_dlss = DlssStatus {
            ok: Some(true),
            items: vec![
                DlssStatusItem {
                    key: "dlss".into(),
                    file_name: "nvngx_dlss.dll".into(),
                    display_name: Some("DLSS".into()),
                    current_version: Some("310.2.1".into()),
                    latest_version: Some("310.2.1".into()),
                    state: Some("current".into()),
                    ..Default::default()
                },
                DlssStatusItem {
                    key: "xess".into(),
                    file_name: "libxess.dll".into(),
                    display_name: Some("XeSS".into()),
                    current_version: Some("2.0.1.0".into()),
                    latest_version: Some("2.0.1.0".into()),
                    state: Some("current".into()),
                    ..Default::default()
                },
            ],
            ..Default::default()
        };
        let demo_fg = FrameGenMod {
            ok: true,
            state: "native".into(),
            ..Default::default()
        };
        let demo_shaders = ShaderStatus {
            ok: true,
            state: "warmed".into(),
            ..Default::default()
        };
        let demo_hdr = HdrGame {
            ok: true,
            hdr: Some("true".into()),
            reason: Some("Native HDR".into()),
            mode: Some("auto".into()),
            ..Default::default()
        };
        let demo_compress = CompressState {
            state: "none".into(),
            size: Some(68_719_476_736),
            estimate: Some(12_400_000_000),
            sampled: true,
            ..Default::default()
        };
        s.settings = Some(demo_settings);
        s.dlss = Some(demo_dlss);
        s.frame_gen = Some(demo_fg);
        s.shaders = Some(demo_shaders);
        s.hdr_game = Some(demo_hdr);
        s.compress = Some(demo_compress);
        s
    }

    pub fn cache_put<T>(map: &mut HashMap<String, T>, key: String, value: T) {
        if map.len() >= SHEET_CACHE_CAP
            && !map.contains_key(&key)
            && let Some(evict) = map.keys().next().cloned()
        {
            map.remove(&evict);
        }
        map.insert(key, value);
    }

    pub fn set_note(&mut self, row: &str, text: impl Into<String>, bad: bool) {
        let t = text.into();
        if t.is_empty() {
            self.props_notes.remove(row);
        } else {
            self.props_notes
                .insert(row.to_string(), RowNote { text: t, bad });
        }
    }

    pub fn dlss_key(id: &str) -> String {
        id.trim().to_ascii_lowercase()
    }

    pub fn dlss_peek(&self, id: &str) -> Option<DlssStatus> {
        self.dlss_cache.get(&Self::dlss_key(id)).cloned()
    }

    pub fn dlss_get_fresh(&self, id: &str, now: f64) -> Option<DlssStatus> {
        let key = Self::dlss_key(id);
        let status = self.dlss_cache.get(&key)?;
        let ts = self.dlss_cached_at.get(&key).copied().unwrap_or(0.0);
        if (now - ts) < DLSS_CACHE_TTL_SECS {
            Some(status.clone())
        } else {
            None
        }
    }

    pub fn dlss_put(&mut self, id: &str, status: DlssStatus, now: f64) {
        let key = Self::dlss_key(id);
        if self.dlss_cache.len() >= DLSS_CACHE_CAP
            && !self.dlss_cache.contains_key(&key)
            && let Some(oldest) = self
                .dlss_cached_at
                .iter()
                .min_by(|a, b| a.1.total_cmp(b.1))
                .map(|(k, _)| k.clone())
                .or_else(|| self.dlss_cache.keys().next().cloned())
        {
            self.dlss_cache.remove(&oldest);
            self.dlss_cached_at.remove(&oldest);
        }
        self.dlss_cache.insert(key.clone(), status);
        self.dlss_cached_at.insert(key, now);
    }

    pub fn dlss_invalidate(&mut self, id: &str) {
        let key = Self::dlss_key(id);
        self.dlss_cache.remove(&key);
        self.dlss_cached_at.remove(&key);
        self.dlss_inflight.remove(&key);
    }

    pub fn clear_for_game(&mut self, id: &str, store: &str) {
        self.generation = self.generation.wrapping_add(1);
        let key = selection_key_for(id, store);
        self.settings = self.settings_cache.get(id).cloned();
        self.settings_working = false;
        self.settings_work_on = false;
        self.settings_work_failed = false;
        self.graphics_features = self.graphics_features_cache.get(id).cloned();
        self.graphics_features_busy = None;
        self.graphics_features_errors.clear();
        self.dlss = self.dlss_peek(id);
        self.dlss_loading = self.dlss.is_none();
        self.frame_gen = self.frame_gen_cache.get(id).cloned();
        self.gpu_game = self.gpu_game_cache.get(id).cloned();
        self.gpu_busy = false;
        self.gpu_error = None;
        self.hdr_game = self.hdr_game_cache.get(id).cloned();
        self.hdr_busy = false;
        self.hdr_error = None;
        self.shaders = self.shaders_cache.get(id).cloned();
        self.compress = None;
        self.compress_error = None;
        self.props_load_error = None;
        self.props_busy = None;
        self.props_notes.clear();
        self.props_tab_id = None;
        self.beta_code = TextEdit::default().secure();
        self.art_action = None;
        self.pending_choice = None;
        self.primary_busy = false;
        self.status_msg = None;
        self.move_poll_due = None;
        self.move_poll_interval = MOVE_POLL_BASE_SECS;
        self.move_last_done = None;
        let _ = key;
    }
}

pub fn demo_game_properties() -> GameProperties {
    GameProperties {
        ok: true,
        store: "steam".into(),
        launch_options: Some("-skipStartScreen".into()),
        size_bytes: Some(68_719_476_736),
        verify_label: Some("Verify files".into()),
        can_browse: true,
        running: false,
        steam_overlay: Some(false),
        cloud_sync: Some(true),
        language: Some("english".into()),
        language_choices: vec![
            PropertyChoice {
                value: "english".into(),
                label: "English".into(),
                detail: None,
                size_bytes: None,
            },
            PropertyChoice {
                value: "spanish".into(),
                label: "Spanish".into(),
                detail: None,
                size_bytes: None,
            },
            PropertyChoice {
                value: "french".into(),
                label: "French".into(),
                detail: None,
                size_bytes: None,
            },
            PropertyChoice {
                value: "german".into(),
                label: "German".into(),
                detail: None,
                size_bytes: None,
            },
            PropertyChoice {
                value: "japanese".into(),
                label: "Japanese".into(),
                detail: None,
                size_bytes: None,
            },
        ],
        beta: Some("".into()),
        beta_choices: vec![
            PropertyChoice {
                value: "".into(),
                label: "None".into(),
                detail: None,
                size_bytes: None,
            },
            PropertyChoice {
                value: "beta_2_1".into(),
                label: "2.1 legacy".into(),
                detail: None,
                size_bytes: None,
            },
        ],
        beta_code: true,
        content_label: "DLC".into(),
        content: Some(vec![GameContent {
            id: "2138330".into(),
            name: "Phantom Liberty".into(),
            enabled: true,
            size_bytes: Some(24_696_061_952),
            toggle: true,
            state: Some("Owned".into()),
        }]),
        folder: Some("C:\\Program Files (x86)\\Steam\\steamapps".into()),
        folder_choices: vec![
            PropertyChoice {
                value: "C:\\Program Files (x86)\\Steam\\steamapps".into(),
                label: "C: Drive".into(),
                detail: Some("180 GB free".into()),
                size_bytes: None,
            },
            PropertyChoice {
                value: "D:\\SteamLibrary\\steamapps".into(),
                label: "D: Games".into(),
                detail: Some("640 GB free".into()),
                size_bytes: None,
            },
        ],
        ..Default::default()
    }
}

pub fn selection_key_for(id: &str, store: &str) -> String {
    format!("{id}\u{001f}{store}")
}

pub fn materialize_game(game: &Game, selected_variant_id: Option<&str>) -> Game {
    let Some(vid) = selected_variant_id else {
        return game.clone();
    };
    if game.id == vid {
        return game.clone();
    }
    let Some(v) = game.variants.iter().find(|v| v.id == vid) else {
        return game.clone();
    };
    let mut out = game.clone();
    out.id = v.id.clone();
    if !v.store.is_empty() {
        out.store = v.store.clone();
    }
    out.installed = v.installed;
    out.owned = v.owned;
    out.entitlement_state = v.entitlement_state.clone();
    out.update_available = v.update_available;
    out.can_install = v.can_install;
    out.primary_action = v.primary_action.clone();
    out.launch_target = v.launch_target.clone();
    if let Some(m) = v.playtime_minutes {
        out.playtime_minutes = m;
    }
    if v.last_played_utc.is_some() {
        out.last_played_utc = v.last_played_utc.clone();
    }
    if !v.status.is_empty() {
        out.status = Some(v.status.clone());
    }
    out.is_running = v.is_running;
    out.can_stop = v.can_stop;
    out.selected_variant_id = Some(v.id.clone());
    out
}

pub fn resolve_primary_action(game: &Game) -> PrimaryActionKind {
    if game.entitlement_state.as_deref() == Some("notOwned") {
        return PrimaryActionKind::None;
    }
    if game.entitlement_state.as_deref() == Some("unverified") && !game.installed {
        return PrimaryActionKind::None;
    }
    if game.installed && game.update_available {
        return PrimaryActionKind::Update;
    }
    if game.installed
        && game.selected_variant_id.is_none()
        && game.variants.iter().any(|v| v.update_available)
    {
        return PrimaryActionKind::Update;
    }
    if game.installed {
        return PrimaryActionKind::Play;
    }
    if game.can_install && (game.owned || game.primary_action.as_deref() == Some("install")) {
        return PrimaryActionKind::Install;
    }
    match game.primary_action.as_deref() {
        Some("play") => PrimaryActionKind::Play,
        Some("update") => PrimaryActionKind::Update,
        Some("none") => PrimaryActionKind::None,
        _ => PrimaryActionKind::None,
    }
}

pub fn can_expose_buy_url(game: &Game) -> bool {
    if game.entitlement_state.as_deref() == Some("unverified") {
        return false;
    }
    if game.entitlement_state.as_deref() == Some("notOwned") {
        return true;
    }
    !game.installed && !game.owned
}

pub fn hosted_buy_url(game: &Game) -> Option<&str> {
    if !can_expose_buy_url(game) {
        return None;
    }
    game.buy_url
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
}

pub fn buy_label(store: &str) -> &'static str {
    match store.to_ascii_lowercase().as_str() {
        "steam" => "Buy on Steam",
        "gog" => "Buy on GOG",
        _ => "Buy in browser",
    }
}

pub fn blocked_entitlement_label(game: &Game, action: PrimaryActionKind) -> Option<&'static str> {
    if action != PrimaryActionKind::None {
        return None;
    }
    match game.entitlement_state.as_deref() {
        Some("notOwned") => Some("Buy again"),
        Some("unverified") => Some("Unavailable"),
        _ => None,
    }
}

pub fn primary_cta_label(game: &Game, action: PrimaryActionKind, buy_url: Option<&str>) -> String {
    if action == PrimaryActionKind::None && buy_url.is_some() {
        return if game.entitlement_state.as_deref() == Some("notOwned") {
            "Buy again".into()
        } else {
            buy_label(&game.store).into()
        };
    }
    if game.can_stop {
        return "Stop".into();
    }
    if let Some(blocked) = blocked_entitlement_label(game, action) {
        return blocked.into();
    }
    if game.store.eq_ignore_ascii_case("ea")
        && matches!(
            action,
            PrimaryActionKind::Install | PrimaryActionKind::Update
        )
    {
        return "Open EA app".into();
    }
    match action {
        PrimaryActionKind::Update => "Update".into(),
        PrimaryActionKind::Play => "Play".into(),
        PrimaryActionKind::Install => "Install".into(),
        PrimaryActionKind::None => {
            if game.installed {
                "Unavailable".into()
            } else if game.is_preloaded() {
                "Preloaded".into()
            } else {
                "Not installed".into()
            }
        }
    }
}

pub fn unavailable_reason(
    game: &Game,
    action: PrimaryActionKind,
    buy_url: Option<&str>,
) -> Option<&'static str> {
    if game.can_stop || action != PrimaryActionKind::None || buy_url.is_some() {
        return None;
    }
    if game.entitlement_state.as_deref() == Some("unverified") {
        return Some("Exo could not confirm you own this game. Open it from its store app.");
    }
    if game.installed {
        return Some("Exo cannot launch this copy. Open it from its store app.");
    }
    Some("Exo cannot install this game yet. Install it from its store app.")
}

pub fn is_installing_game(game: &Game, target_id: Option<&str>) -> bool {
    let Some(tid) = target_id else {
        return false;
    };
    if game.id == tid {
        return true;
    }
    game.variants.iter().any(|v| v.id == tid)
}

pub fn transfer_for_game<'a>(
    progress: Option<&'a InstallProgress>,
    game: &Game,
) -> Option<&'a InstallProgress> {
    let p = progress?;
    if !p.is_active {
        return None;
    }
    is_installing_game(game, p.game_id.as_deref()).then_some(p)
}

pub fn transfer_percent(progress: &InstallProgress) -> Option<f64> {
    if progress.percent.is_finite() && progress.percent > 0.0 && progress.percent <= 100.0 {
        return Some(progress.percent);
    }
    let phase = progress.phase.to_ascii_lowercase();
    if !phase.is_empty() && phase != "downloading" {
        return None;
    }
    if progress.bytes_to_download > 0 && progress.bytes_downloaded <= progress.bytes_to_download {
        let pct = (100.0 * progress.bytes_downloaded as f64) / progress.bytes_to_download as f64;
        if pct.is_finite() && (0.0..=100.0).contains(&pct) {
            return Some(pct);
        }
    }
    if progress.percent.is_finite() && progress.percent == 0.0 && phase == "downloading" {
        return Some(0.0);
    }
    None
}

pub fn transfer_share(progress: &InstallProgress) -> Option<f64> {
    transfer_percent(progress).map(|p| (p / 100.0).clamp(0.0, 1.0))
}

pub fn install_verb(progress: &InstallProgress, action: PrimaryActionKind) -> &'static str {
    let phase = progress.phase.to_ascii_lowercase();
    let status = progress
        .status
        .as_deref()
        .unwrap_or("")
        .to_ascii_lowercase();
    if phase == "finalizing" {
        return "Finalizing";
    }
    if phase == "installing" {
        return if action == PrimaryActionKind::Update {
            "Applying"
        } else {
            "Installing"
        };
    }
    if progress.bytes_to_download == 0 {
        if status.contains("starting")
            || status.contains("waiting")
            || status.contains("requesting")
            || status.contains("queued")
            || status.contains("preparing")
        {
            return "Waiting";
        }
        if phase == "preparing" {
            return "Starting";
        }
    }
    if action == PrimaryActionKind::Update {
        "Updating"
    } else {
        "Downloading"
    }
}

pub fn format_size(bytes: u64) -> String {
    if bytes == 0 {
        return "—".into();
    }
    let gb = bytes as f64 / (1024.0 * 1024.0 * 1024.0);
    if gb >= 1.0 {
        if gb < 10.0 {
            return format!("{gb:.1} GB");
        }
        return format!("{} GB", gb.round() as u64);
    }
    let mb = bytes as f64 / (1024.0 * 1024.0);
    format!("{} MB", mb.round().max(1.0) as u64)
}

pub fn format_speed(bps: u64) -> Option<String> {
    if bps == 0 {
        return None;
    }
    let mb = bps as f64 / (1024.0 * 1024.0);
    if mb >= 1.0 {
        return Some(format!("{mb:.1} MB/s"));
    }
    let kb = bps as f64 / 1024.0;
    Some(format!("{} KB/s", kb.round() as u64))
}

pub fn format_eta(progress: &InstallProgress) -> Option<String> {
    if progress.bytes_per_second == 0 || progress.bytes_to_download <= progress.bytes_downloaded {
        return None;
    }
    let rem = progress.bytes_to_download - progress.bytes_downloaded;
    let secs = rem / progress.bytes_per_second;
    if secs < 60 {
        return Some(format!("{}s left", secs.max(1)));
    }
    let mins = (secs + 30) / 60;
    if mins < 60 {
        return Some(format!("{mins}m left"));
    }
    let hrs = mins / 60;
    let rem_m = mins % 60;
    if rem_m == 0 {
        Some(format!("{hrs}h left"))
    } else {
        Some(format!("{hrs}h {rem_m}m left"))
    }
}

pub fn transfer_meta_line(progress: &InstallProgress, include_bytes: bool) -> String {
    let mut parts: Vec<String> = Vec::new();
    if include_bytes && progress.bytes_to_download > 0 {
        if progress.bytes_downloaded > 0 {
            parts.push(format!(
                "{} / {}",
                format_size(progress.bytes_downloaded),
                format_size(progress.bytes_to_download)
            ));
        } else {
            parts.push(format_size(progress.bytes_to_download));
        }
    } else if progress.bytes_to_download == 0
        && let Some(st) = progress.status.as_deref()
    {
        let low = st.to_ascii_lowercase();
        if !st.is_empty()
            && !low.contains("waiting")
            && !low.contains("starting")
            && !low.contains("requesting")
        {
            parts.push(st.to_string());
        }
    }
    if let Some(speed) = format_speed(progress.bytes_per_second) {
        parts.push(speed);
    }
    if let Some(eta) = format_eta(progress) {
        parts.push(eta);
    }
    parts.join(" · ")
}

pub fn format_played_opt(minutes: u64) -> Option<String> {
    if minutes == 0 {
        return None;
    }
    let hrs = minutes / 60;
    let mins = minutes % 60;
    if hrs == 0 {
        Some(format!("{mins} min"))
    } else if mins == 0 {
        Some(format!("{hrs} hr"))
    } else {
        Some(format!("{hrs} hr {mins} min"))
    }
}

fn parse_iso_unix_secs(raw: &str) -> Option<i64> {
    if raw.len() < 10 {
        return None;
    }
    let y: i64 = raw.get(0..4)?.parse().ok()?;
    let m: i64 = raw.get(5..7)?.parse().ok()?;
    let d: i64 = raw.get(8..10)?.parse().ok()?;
    let y_adj = y - if m <= 2 { 1 } else { 0 };
    let era = y_adj.div_euclid(400);
    let yoe = y_adj - era * 400;
    let doy = (153 * (m + if m > 2 { -3 } else { 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era * 146097 + doe - 719468;
    let mut secs = days * 86_400;
    if raw.len() >= 19
        && matches!(raw.as_bytes()[10], b'T' | b' ')
        && let (Ok(hh), Ok(mm), Ok(ss)) = (
            raw[11..13].parse::<i64>(),
            raw[14..16].parse::<i64>(),
            raw[17..19].parse::<i64>(),
        )
    {
        secs += hh * 3600 + mm * 60 + ss;
    }
    Some(secs)
}

pub fn format_relative_last_played(raw: Option<&str>) -> Option<String> {
    let s = raw.filter(|v| !v.trim().is_empty())?;
    let ts = parse_iso_unix_secs(s)?;
    let now = SystemTime::now().duration_since(UNIX_EPOCH).ok()?.as_secs() as i64;
    let diff = (now - ts).max(0);
    let mins = (diff + 30) / 60;
    if mins < 1 {
        return Some("Just now".into());
    }
    if mins < 60 {
        return Some(format!("{mins} min ago"));
    }
    let hours = (mins + 30) / 60;
    if hours < 24 {
        return Some(format!("{hours} hr ago"));
    }
    let days = (hours + 12) / 24;
    if days < 14 {
        return Some(if days == 1 {
            "Yesterday".into()
        } else {
            format!("{days} days ago")
        });
    }
    let month = match s.get(5..7) {
        Some("01") => "Jan",
        Some("02") => "Feb",
        Some("03") => "Mar",
        Some("04") => "Apr",
        Some("05") => "May",
        Some("06") => "Jun",
        Some("07") => "Jul",
        Some("08") => "Aug",
        Some("09") => "Sep",
        Some("10") => "Oct",
        Some("11") => "Nov",
        Some("12") => "Dec",
        _ => return Some(s[..10.min(s.len())].to_string()),
    };
    let day = s
        .get(8..10)
        .and_then(|d| d.parse::<u32>().ok())
        .unwrap_or(1);
    Some(format!("{month} {day}"))
}

pub fn is_noise_status(message: &str) -> bool {
    let t = message.trim().to_ascii_lowercase();
    let base = t.trim_end_matches('.');
    matches!(
        base,
        "canceled" | "uninstalled" | "removed" | "installed" | "up to date" | "running"
    ) || ((t.starts_with("removing ") || t.starts_with("uninstalling ")) && t.ends_with('…'))
}

pub fn trim_stop(text: Option<&str>) -> String {
    text.unwrap_or("").trim().trim_end_matches('.').to_string()
}

pub fn curly_quotes(text: &str) -> String {
    text.replace('\'', "’")
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct UpscalerRow {
    pub key: String,
    pub label: String,
    pub state: String,
    pub version: Option<String>,
    pub mod_name: Option<String>,
    pub keys: Vec<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct UpscalerView {
    pub rows: Vec<UpscalerRow>,
    pub anti_cheat_name: Option<String>,
    pub consent: Option<String>,
    pub locked: bool,
    pub updatable: usize,
    pub restorable: usize,
    pub mods: Vec<String>,
}

fn state_severity(state: &str) -> usize {
    match state {
        "outdated" => 0,
        "unknown" => 1,
        "newer" => 2,
        "current" => 3,
        "mod" => 4,
        _ => 1,
    }
}

fn row_version(item: &DlssStatusItem, state: &str) -> Option<String> {
    let latest = item.latest_version.as_deref().filter(|s| !s.is_empty());
    if state == "current"
        && let Some(l) = latest
    {
        return Some(l.to_string());
    }
    let file = item
        .current_version
        .as_deref()
        .filter(|s| !s.is_empty())
        .or_else(|| item.file_version.as_deref().filter(|s| !s.is_empty()))?;
    let parts: Vec<&str> = file.split('.').collect();
    let keep = latest
        .map(|l| l.split('.').count())
        .unwrap_or(parts.len())
        .max(1);
    Some(parts.into_iter().take(keep).collect::<Vec<_>>().join("."))
}

fn compare_versions(a: &str, b: &str) -> std::cmp::Ordering {
    let left: Vec<u64> = a.split('.').map(|p| p.parse().unwrap_or(0)).collect();
    let right: Vec<u64> = b.split('.').map(|p| p.parse().unwrap_or(0)).collect();
    let n = left.len().max(right.len());
    for i in 0..n {
        let l = left.get(i).copied().unwrap_or(0);
        let r = right.get(i).copied().unwrap_or(0);
        if l != r {
            return l.cmp(&r);
        }
    }
    std::cmp::Ordering::Equal
}

pub fn upscaler_view(status: Option<&DlssStatus>) -> UpscalerView {
    let Some(st) = status else {
        return UpscalerView::default();
    };
    let mut rows: Vec<UpscalerRow> = Vec::new();
    let mut row_idx: HashMap<String, usize> = HashMap::new();
    let mut mods_seen: HashSet<String> = HashSet::new();
    let mut mods: Vec<String> = Vec::new();

    for item in &st.items {
        let label = item
            .display_name
            .as_deref()
            .filter(|s| !s.is_empty())
            .unwrap_or(&item.file_name)
            .to_string();
        let state = item.state.as_deref().unwrap_or("unknown").to_string();
        let version = row_version(item, &state);
        let mod_name = if state == "mod" {
            let m = item
                .mod_name
                .as_deref()
                .filter(|s| !s.is_empty())
                .unwrap_or("Mod")
                .to_string();
            if mods_seen.insert(m.clone()) {
                mods.push(m.clone());
            }
            Some(m)
        } else {
            None
        };
        let key = label.to_ascii_lowercase();
        if let Some(&idx) = row_idx.get(&key) {
            rows[idx].keys.push(item.key.clone());
            if state_severity(&state) < state_severity(&rows[idx].state) {
                rows[idx].state = state;
                rows[idx].version = version;
                rows[idx].mod_name = mod_name;
            }
        } else {
            row_idx.insert(key.clone(), rows.len());
            rows.push(UpscalerRow {
                key,
                label,
                state,
                version,
                mod_name,
                keys: vec![item.key.clone()],
            });
        }
    }

    let anti_cheat_name = st
        .anti_cheat
        .as_deref()
        .filter(|s| !s.is_empty())
        .map(str::to_string);
    let consent = anti_cheat_name
        .as_ref()
        .map(|_| st.consent.clone().unwrap_or_else(|| "never".into()));
    let locked = anti_cheat_name.is_some() && consent.as_deref() != Some("given");
    let updatable = st
        .items
        .iter()
        .filter(|i| i.can_update == Some(true))
        .count();
    let restorable = st
        .items
        .iter()
        .filter(|i| i.can_restore == Some(true))
        .count();

    UpscalerView {
        rows,
        anti_cheat_name,
        consent,
        locked,
        updatable,
        restorable,
        mods,
    }
}

fn upscaler_families(rows: &[&UpscalerRow]) -> Vec<UpscalerRow> {
    const FAMILIES: [&str; 3] = ["DLSS", "FSR", "XeSS"];
    let mut families: Vec<UpscalerRow> = Vec::new();
    let mut idx_map: HashMap<String, usize> = HashMap::new();
    for row in rows {
        let name = FAMILIES
            .iter()
            .find(|&&f| row.label.starts_with(f))
            .map(|&f| f.to_string())
            .unwrap_or_else(|| row.label.clone());
        if let Some(&idx) = idx_map.get(&name) {
            families[idx].keys.extend(row.keys.iter().cloned());
            let worse = state_severity(&row.state) < state_severity(&families[idx].state);
            let same = row.state == families[idx].state
                && row.version.is_some()
                && families[idx].version.is_some();
            if worse {
                families[idx].state = row.state.clone();
                families[idx].version = row.version.clone();
            } else if same {
                let ord = compare_versions(
                    row.version.as_deref().unwrap_or(""),
                    families[idx].version.as_deref().unwrap_or(""),
                );
                let take = if row.state == "outdated" {
                    ord.is_lt()
                } else {
                    ord.is_gt()
                };
                if take {
                    families[idx].version = row.version.clone();
                }
            }
        } else {
            idx_map.insert(name.clone(), families.len());
            families.push(UpscalerRow {
                key: name.to_ascii_lowercase(),
                label: name,
                state: row.state.clone(),
                version: row.version.clone(),
                mod_name: None,
                keys: row.keys.clone(),
            });
        }
    }
    families
}

pub fn upscaler_line_word(view: &UpscalerView, scan_error: Option<&str>) -> LineWord {
    if let Some(err) = scan_error.filter(|s| !s.is_empty()) {
        return LineWord::bad(err);
    }
    if view.rows.is_empty() {
        return LineWord::plain("Not supported in this game");
    }
    if view.locked || view.consent.as_deref() == Some("never") {
        let ac = view.anti_cheat_name.as_deref().unwrap_or("anti-cheat");
        return LineWord::plain(format!("Uses {ac}"));
    }
    let own: Vec<&UpscalerRow> = view.rows.iter().filter(|r| r.state != "mod").collect();
    let families = upscaler_families(&own);
    if families.is_empty() {
        return LineWord::plain("Not updated: a mod owns these files");
    }
    let outdated = families.iter().filter(|r| r.state == "outdated").count();
    let outdated = if outdated > 0 {
        outdated
    } else if view.updatable > 0 {
        1
    } else {
        0
    };
    if outdated > 0 {
        return LineWord::plain(if outdated == 1 {
            "1 update".to_string()
        } else {
            format!("{outdated} updates")
        });
    }
    let known: Vec<&UpscalerRow> = families
        .iter()
        .filter(|r| matches!(r.state.as_str(), "current" | "newer"))
        .collect();
    if known.is_empty() {
        return LineWord::plain("Version unknown");
    }
    let unread: Vec<&UpscalerRow> = families
        .iter()
        .filter(|r| !matches!(r.state.as_str(), "current" | "newer"))
        .collect();
    if !unread.is_empty() {
        let labels: Vec<&str> = unread.iter().map(|r| r.label.as_str()).collect();
        return LineWord::plain(format!("Version unknown: {}", labels.join(", ")));
    }
    let list: Vec<String> = known
        .iter()
        .map(|r| match r.version.as_deref() {
            Some(v) if !v.is_empty() => format!("{} {v}", r.label),
            _ => r.label.clone(),
        })
        .collect();
    LineWord::good(format!("Up to date: {}", list.join(", ")))
}

pub fn gate_reason(game_running: bool, installing: bool) -> Option<&'static str> {
    if game_running {
        return Some("Close the game first");
    }
    if installing {
        return Some("Wait for the download to finish");
    }
    None
}

pub fn ban_question(anti_cheat: &str) -> String {
    let trimmed = anti_cheat.strip_prefix("possibly ").unwrap_or(anti_cheat);
    let name = trimmed.strip_prefix("an ").unwrap_or(trimmed);
    let article = match name.chars().next().map(|c| c.to_ascii_lowercase()) {
        Some('a' | 'e' | 'i' | 'o' | 'u') => "an",
        _ => "a",
    };
    format!("Risk {article} {name} ban?")
}

pub fn frame_gen_word(state: &FrameGenMod) -> Option<LineWord> {
    match state.state.as_str() {
        "hidden" => Some(LineWord::plain(
            if state.reason.as_deref() == Some("unread") {
                "Could not check"
            } else {
                "Not supported in this game"
            },
        )),
        "native" => Some(LineWord::plain("Built into the game")),
        "mod" => Some(LineWord::plain(curly_quotes(
            state.mod_name.as_deref().unwrap_or("A mod"),
        ))),
        "note" => Some(LineWord::plain(curly_quotes(
            state.note.as_deref().unwrap_or(""),
        ))),
        "broken" => {
            let text = match state.reason.as_deref() {
                Some("replaced") => format!(
                    "{} replaced Exo’s loader",
                    curly_quotes(state.mod_name.as_deref().unwrap_or("Another mod"))
                ),
                Some("changed") => "Exo’s files were changed".into(),
                Some("gone") => "This game no longer has DLSS Frame Generation".into(),
                _ => "Exo’s files were removed".into(),
            };
            Some(LineWord::bad(text))
        }
        _ => {
            if let Some(note) = state.note.as_deref().filter(|s| !s.is_empty()) {
                return Some(LineWord::plain(curly_quotes(note)));
            }
            if state.state == "off" {
                return Some(LineWord::plain("Off"));
            }
            if state.state == "on" {
                return state
                    .version
                    .as_deref()
                    .filter(|s| !s.is_empty())
                    .map(|v| LineWord::plain(curly_quotes(v)));
            }
            None
        }
    }
}

pub fn optimize_word(
    state: &GameSettingsState,
    working: bool,
    work_on: bool,
    work_failed: bool,
    kept: Option<&str>,
) -> LineWord {
    if working {
        return LineWord::working(if !work_on {
            "Putting back"
        } else {
            "Optimizing"
        });
    }
    if work_failed {
        return LineWord::bad(if work_on {
            "Could not optimize"
        } else {
            "Could not put back"
        });
    }
    if state.running == Some(true) {
        let title = state.title.as_deref().unwrap_or("the game");
        return LineWord::plain(format!("Close {title} first"));
    }
    if state.found != Some(true) {
        return LineWord::plain(if state.waiting == Some(true) {
            "Applies after you play once"
        } else {
            "Not optimized"
        });
    }
    match state.standing.as_deref() {
        Some("unfound") => LineWord::plain("Settings not found"),
        Some("partly") => LineWord::plain("Partly optimized"),
        Some("applied") => {
            if kept == Some("wontKeepSome") {
                LineWord::plain("The game won’t keep some settings")
            } else if kept == Some("yourChangesKept") {
                LineWord::good("Applied, your changes kept")
            } else if state.again == Some(true) || kept == Some("putBackAfterUpdate") {
                LineWord::good("Applied again after an update")
            } else {
                LineWord::good("Applied")
            }
        }
        _ => LineWord::plain("Not optimized"),
    }
}

pub fn shader_word(status: &ShaderStatus) -> Option<LineWord> {
    match status.state.as_str() {
        "warmed" => Some(LineWord::plain("Compiled")),
        "stale" => Some(LineWord::plain(
            status.reason.as_deref().unwrap_or("Compile again"),
        )),
        "ready" | "record" => Some(LineWord::plain("Not compiled")),
        "unsupported" => Some(LineWord::plain("Not supported in this game")),
        "noApp" => Some(LineWord::plain("Get Exo Shaders")),
        "error" => Some(LineWord::bad("Could not check")),
        _ => None,
    }
}

pub fn compress_shown(c: &CompressState) -> bool {
    c.state != "refused"
}

pub fn compress_words(c: &CompressState) -> String {
    let pct = c
        .percent
        .map(|p| format!(" {}%", (p * 100.0).round() as i64))
        .unwrap_or_default();
    match c.state.as_str() {
        "compressing" => c
            .held
            .clone()
            .unwrap_or_else(|| format!("Compressing{pct}")),
        "uncompressing" => c
            .held
            .clone()
            .unwrap_or_else(|| format!("Putting back{pct}")),
        "queued" => "Waiting its turn".into(),
        "paused" => "Paused".into(),
        "compressed" => format!("Saved {}", format_size(c.saved)),
        "changed" => format!(
            "Saved {} · an update added {}",
            format_size(c.saved),
            format_size(c.changed)
        ),
        "needsConsent" => {
            if let Some(ac) = c.anti_cheat.as_deref().filter(|s| !s.is_empty()) {
                format!("Uses {ac}")
            } else {
                c.message.clone().unwrap_or_else(|| "Not compressed".into())
            }
        }
        "refused" => c.message.clone().unwrap_or_else(|| "Not compressed".into()),
        _ => {
            if let Some(est) = c.estimate.filter(|&e| e > 0) {
                let verb = if c.sampled { "Saves" } else { "Could save" };
                format!("{verb} about {}", format_size(est))
            } else {
                "Not compressed".into()
            }
        }
    }
}

pub fn compress_stopped(c: &CompressState) -> Option<&str> {
    if c.state == "refused" || c.state == "needsConsent" {
        return None;
    }
    c.message.as_deref().filter(|s| !s.is_empty())
}

pub fn sections_for(props: &GameProperties) -> Vec<(PropsSectionId, String)> {
    let mut out = Vec::new();
    let has_general = props.launch_options.is_some()
        || props
            .launch_options_note
            .as_deref()
            .is_some_and(|s| !s.is_empty())
        || props.language.is_some()
        || props.cloud_sync == Some(true)
        || props
            .store_properties
            .as_deref()
            .is_some_and(|s| !s.is_empty())
        || props.through_steam.is_some()
        || props.steam_overlay.is_some()
        || props.anti_cheat_launch.is_some();
    if has_general {
        out.push((PropsSectionId::General, "General".into()));
    }
    out.push((PropsSectionId::Files, "Installed files".into()));
    if props.beta.is_some() && !props.beta_choices.is_empty() {
        out.push((PropsSectionId::Betas, "Betas".into()));
    }
    if props.content.as_ref().is_some_and(|c| !c.is_empty()) {
        let label = if props.content_label.is_empty() {
            "DLC".to_string()
        } else {
            props.content_label.clone()
        };
        out.push((PropsSectionId::Content, label));
    }
    out.push((PropsSectionId::Artwork, "Artwork".into()));
    out
}

pub fn default_section_id(sections: &[(PropsSectionId, String)]) -> PropsSectionId {
    if sections
        .iter()
        .any(|(id, _)| *id == PropsSectionId::General)
    {
        return PropsSectionId::General;
    }
    if sections.iter().any(|(id, _)| *id == PropsSectionId::Files) {
        return PropsSectionId::Files;
    }
    sections
        .first()
        .map(|(id, _)| *id)
        .unwrap_or(PropsSectionId::Files)
}

pub fn download_question(bytes: Option<u64>) -> String {
    match bytes.filter(|&b| b > 0) {
        Some(b) => format!("Download {}?", format_size(b)),
        None => "Download it?".into(),
    }
}

pub fn moved_share(done: u64, total: u64) -> Option<f64> {
    if total > 0 {
        Some((done as f64 / total as f64).clamp(0.0, 1.0))
    } else {
        None
    }
}

impl AppState {
    pub fn active_game_view(&self, base: &Game) -> Game {
        materialize_game(base, self.game_page.selected_variant_id.as_deref())
    }

    pub fn load_game_page_data(&mut self, id: &str) {
        let base_game = self.games.iter().find(|g| g.id == id).cloned();
        let active_game = base_game.as_ref().map(|g| self.active_game_view(g));
        let effective_id = active_game
            .as_ref()
            .map(|g| g.id.clone())
            .unwrap_or_else(|| id.to_string());
        let store = active_game
            .as_ref()
            .map(|g| g.store.clone())
            .unwrap_or_default();
        let installed = active_game.as_ref().is_some_and(|g| g.installed);
        let portable = active_game.as_ref().is_some_and(|g| g.is_add_portable);

        if self.status == HostStatus::Standalone {
            if self.game_props.is_none() {
                self.game_props = Some(demo_game_properties());
            }
            if let Some(opts) = self
                .game_props
                .as_ref()
                .and_then(|p| p.launch_options.as_deref())
            {
                self.props_launch_opts.text = opts.to_string();
                self.props_launch_opts.sync_invariants();
            }
            return;
        }

        self.game_page.clear_for_game(&effective_id, &store);
        let epoch = self.game_page.generation;
        let sel_key = selection_key_for(&effective_id, &store);
        if let Some(&(unlocked, total)) = self.game_page.achievements_cache.get(&sel_key) {
            self.game_extras = Some(GameExtras {
                ok: true,
                achievements_unlocked: Some(unlocked),
                achievements_total: Some(total),
                drive_heat: None,
                message: None,
            });
        } else {
            self.game_extras = None;
        }
        if let Some(cached_props) = self.game_page.props_cache.get(&effective_id).cloned() {
            self.props_launch_opts.text = cached_props.launch_options.clone().unwrap_or_default();
            self.props_launch_opts.sync_invariants();
            self.game_props = Some(cached_props);
        } else {
            self.game_props = None;
            self.props_launch_opts.text.clear();
            self.props_launch_opts.sync_invariants();
        }

        #[cfg(windows)]
        self.fetch_game_page_rpcs(&effective_id, &sel_key, installed && !portable, epoch);
    }

    #[cfg(windows)]
    fn fetch_game_page_rpcs(
        &mut self,
        game_id: &str,
        sel_key: &str,
        installed_actions: bool,
        epoch: u64,
    ) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };

        {
            let gid = game_id.to_string();
            let h = handle.clone();
            rpc.call::<_, GameGetReply>(METHOD_GAME_GET, &json!({ "id": gid }), move |res| {
                if let Ok(reply) = res
                    && let Some(fresh) = reply.game
                {
                    h.update(move |s, _| {
                        if let Some(slot) = s.games.iter_mut().find(|g| g.id == fresh.id) {
                            *slot = fresh;
                        }
                    });
                }
            });
        }

        {
            let gid = game_id.to_string();
            let sk = sel_key.to_string();
            let h = handle.clone();
            rpc.call::<_, GameExtras>(METHOD_GAME_EXTRAS, &json!({ "id": gid }), move |res| {
                if let Ok(ex) = res {
                    h.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        if let (Some(u), Some(t)) =
                            (ex.achievements_unlocked, ex.achievements_total)
                            && t > 0
                        {
                            GamePageState::cache_put(
                                &mut s.game_page.achievements_cache,
                                sk,
                                (u, t),
                            );
                        }
                        s.game_extras = Some(ex);
                    });
                }
            });
        }

        {
            let gid = game_id.to_string();
            let h = handle.clone();
            rpc.call::<_, InstallProgress>(
                METHOD_GAME_PROGRESS,
                &json!({ "id": gid }),
                move |res| {
                    if let Ok(prog) = res {
                        h.update(move |s, _| {
                            if prog.is_active {
                                s.install_progress = Some(prog);
                            }
                        });
                    }
                },
            );
        }

        if !installed_actions {
            return;
        }

        self.fetch_game_properties_rpc(game_id, epoch);
        self.fetch_game_perf_rpcs(game_id, epoch);
        self.fetch_game_compress_rpc(game_id, false, epoch);
    }

    #[cfg(windows)]
    pub fn fetch_game_properties_rpc(&self, game_id: &str, epoch: u64) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };
        let gid = game_id.to_string();
        rpc.call::<_, GameProperties>(METHOD_GAME_PROPERTIES, &json!({ "id": gid }), move |res| {
            handle.update(move |s, _| {
                if s.game_page.generation != epoch {
                    return;
                }
                match res {
                    Ok(props) if props.ok => {
                        s.game_page.props_load_error = None;
                        s.props_launch_opts.text = props.launch_options.clone().unwrap_or_default();
                        s.props_launch_opts.sync_invariants();
                        let moving_done = props.moving.as_ref().map(|m| m.done);
                        if props.moving.is_some() {
                            if s.game_page.move_last_done != moving_done {
                                s.game_page.move_poll_interval = MOVE_POLL_BASE_SECS;
                            } else {
                                s.game_page.move_poll_interval =
                                    (s.game_page.move_poll_interval * 2.0).min(MOVE_POLL_MAX_SECS);
                            }
                            s.game_page.move_last_done = moving_done;
                            s.game_page.move_poll_due =
                                Some(s.now_secs() + s.game_page.move_poll_interval);
                        } else {
                            s.game_page.move_poll_due = None;
                            s.game_page.move_last_done = None;
                            s.game_page.move_poll_interval = MOVE_POLL_BASE_SECS;
                        }
                        GamePageState::cache_put(
                            &mut s.game_page.props_cache,
                            gid.clone(),
                            props.clone(),
                        );
                        s.game_props = Some(props);
                    }
                    Ok(props) => {
                        let msg = props
                            .message
                            .filter(|m| !m.trim().is_empty())
                            .unwrap_or_else(|| {
                                "Exo couldn’t read this game's properties. Try again.".into()
                            });
                        s.game_page.props_load_error = Some(msg);
                    }
                    Err(e) => {
                        s.game_page.props_load_error = Some(format!(
                            "Exo couldn’t read this game's properties. Try again. ({e})"
                        ));
                    }
                }
            });
        });
    }

    #[cfg(windows)]
    fn fetch_game_perf_rpcs(&mut self, game_id: &str, epoch: u64) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };

        self.fetch_game_dlss_rpc(game_id, false, epoch);
        self.fetch_game_settings_rpc(game_id, epoch);
        self.fetch_game_graphics_features_rpc(game_id, epoch);
        self.fetch_game_shaders_rpc(game_id, epoch);

        {
            let gid = game_id.to_string();
            let h = handle.clone();
            rpc.call::<_, FrameGenMod>(METHOD_DLSS_FRAME_GEN, &json!({ "id": gid }), move |res| {
                h.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    let fg = res.unwrap_or_else(|_| FrameGenMod {
                        ok: false,
                        state: "hidden".into(),
                        ..Default::default()
                    });
                    GamePageState::cache_put(&mut s.game_page.frame_gen_cache, gid, fg.clone());
                    s.game_page.frame_gen = Some(fg);
                });
            });
        }

        {
            let gid = game_id.to_string();
            let h = handle.clone();
            rpc.call::<_, GpuGame>(METHOD_GPU_GAME, &json!({ "id": gid }), move |res| {
                if let Ok(g) = res {
                    h.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        if g.ok {
                            GamePageState::cache_put(
                                &mut s.game_page.gpu_game_cache,
                                gid,
                                g.clone(),
                            );
                        }
                        s.game_page.gpu_game = Some(g);
                    });
                }
            });
        }

        if self.pc.hdr.is_none() {
            let h = handle.clone();
            rpc.call::<_, HdrState>(METHOD_HDR_STATE, &json!({}), move |res| {
                if let Ok(hdr) = res {
                    h.update(move |s, _| {
                        s.pc.hdr = Some(hdr);
                    });
                }
            });
        }

        {
            let gid = game_id.to_string();
            let h = handle.clone();
            rpc.call::<_, HdrGame>(METHOD_HDR_GAME, &json!({ "id": gid }), move |res| {
                h.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    let next = res.unwrap_or_else(|_| HdrGame {
                        ok: false,
                        ..Default::default()
                    });
                    if next.ok {
                        GamePageState::cache_put(
                            &mut s.game_page.hdr_game_cache,
                            gid,
                            next.clone(),
                        );
                    }
                    s.game_page.hdr_game = Some(next);
                });
            });
        }
    }

    #[cfg(windows)]
    pub fn fetch_game_dlss_rpc(&mut self, game_id: &str, refresh: bool, epoch: u64) {
        let now = self.now_secs();
        let key = GamePageState::dlss_key(game_id);
        if !refresh {
            if let Some(cached) = self.game_page.dlss_get_fresh(game_id, now) {
                self.game_page.dlss = Some(cached);
                self.game_page.dlss_loading = false;
                return;
            }
            if self.game_page.dlss_inflight.contains(&key) {
                self.game_page.dlss_loading = self.game_page.dlss.is_none();
                return;
            }
        }
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };
        self.game_page.dlss_inflight.insert(key.clone());
        let gid = game_id.to_string();
        let params = if refresh {
            json!({ "id": gid, "refresh": true })
        } else {
            json!({ "id": gid })
        };
        rpc.call::<_, DlssStatus>(METHOD_DLSS_STATUS, &params, move |res| {
            handle.update(move |s, _| {
                s.game_page.dlss_inflight.remove(&key);
                let st = res.unwrap_or_else(|_| DlssStatus {
                    ok: Some(false),
                    message: Some("Could not check the files.".into()),
                    items: Vec::new(),
                    ..Default::default()
                });
                let now = s.now_secs();
                if st.ok != Some(false) {
                    s.game_page.dlss_put(&gid, st.clone(), now);
                }
                let matches_open = s
                    .current_open_game_id()
                    .map(|cur| GamePageState::dlss_key(&cur) == key)
                    .unwrap_or(false);
                if s.game_page.generation == epoch || matches_open {
                    s.game_page.dlss_loading = false;
                    s.game_page.dlss = Some(st);
                }
            });
        });
    }

    #[cfg(windows)]
    pub fn fetch_game_settings_rpc(&self, game_id: &str, epoch: u64) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };
        let gid = game_id.to_string();
        let h1 = handle.clone();
        rpc.call::<_, GameSettingsState>(METHOD_GAME_SETTINGS, &json!({ "id": gid }), move |res| {
            h1.update(move |s, _| {
                if s.game_page.generation != epoch {
                    return;
                }
                let next = res.unwrap_or_else(|_| GameSettingsState {
                    ok: false,
                    ..Default::default()
                });
                if next.ok {
                    if let Some(ref wm) = next.window_mode {
                        s.game_display_mode = wm.clone();
                    }
                    GamePageState::cache_put(&mut s.game_page.settings_cache, gid, next.clone());
                }
                s.game_page.settings = Some(next);
            });
        });
        rpc.call::<_, KeepStatus>(METHOD_KEEP_STATUS, &json!({}), move |res| {
            if let Ok(ks) = res {
                handle.update(move |s, _| {
                    s.game_page.keep_status = Some(ks);
                });
            }
        });
    }

    #[cfg(windows)]
    pub fn fetch_game_graphics_features_rpc(&self, game_id: &str, epoch: u64) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };
        let gid = game_id.to_string();
        rpc.call::<_, GameGraphicsFeaturesState>(
            METHOD_GAME_GRAPHICS_FEATURES,
            &json!({ "id": gid }),
            move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    let next = res.unwrap_or_else(|_| GameGraphicsFeaturesState {
                        ok: false,
                        message: Some("Could not check graphics features.".into()),
                        ..Default::default()
                    });
                    if next.ok {
                        GamePageState::cache_put(
                            &mut s.game_page.graphics_features_cache,
                            gid,
                            next.clone(),
                        );
                    }
                    s.game_page.graphics_features = Some(next);
                });
            },
        );
    }

    pub fn toggle_game_graphics_feature(
        &mut self,
        game_id: &str,
        feature: &str,
        on: bool,
        accept_risk: bool,
    ) {
        self.game_page.graphics_features_busy = Some(feature.to_string());
        self.game_page.graphics_features_errors.remove(feature);
        #[cfg(windows)]
        {
            let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
                self.game_page.graphics_features_busy = None;
                return;
            };
            let gid = game_id.to_string();
            let feat = feature.to_string();
            let epoch = self.game_page.generation;
            rpc.call::<_, GameGraphicsFeaturesState>(
                METHOD_GAME_GRAPHICS_FEATURES_SET,
                &json!({
                    "id": gid,
                    "feature": feat,
                    "on": on,
                    "acceptAntiCheatRisk": accept_risk,
                }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.graphics_features_busy = None;
                        match res {
                            Ok(next) if next.ok => {
                                GamePageState::cache_put(
                                    &mut s.game_page.graphics_features_cache,
                                    gid,
                                    next.clone(),
                                );
                                s.game_page.graphics_features = Some(next);
                            }
                            Ok(next) => {
                                let msg = next
                                    .message
                                    .clone()
                                    .unwrap_or_else(|| "Could not save. Try again.".into());
                                s.game_page.graphics_features_errors.insert(feat, msg);
                                if next.features.as_ref().is_some_and(|v| !v.is_empty()) {
                                    s.game_page.graphics_features = Some(next);
                                }
                            }
                            Err(_) => {
                                s.game_page
                                    .graphics_features_errors
                                    .insert(feat, "Could not save. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        {
            let _ = (game_id, on, accept_risk);
            self.game_page.graphics_features_busy = None;
        }
    }

    #[cfg(windows)]
    pub fn fetch_game_shaders_rpc(&self, game_id: &str, epoch: u64) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };
        let gid = game_id.to_string();
        rpc.call::<_, ShaderStatus>(METHOD_SHADERS_STATUS, &json!({ "id": gid }), move |res| {
            handle.update(move |s, _| {
                if s.game_page.generation != epoch {
                    return;
                }
                let next = res.unwrap_or_else(|_| ShaderStatus {
                    ok: false,
                    state: "error".into(),
                    ..Default::default()
                });
                if next.ok {
                    GamePageState::cache_put(&mut s.game_page.shaders_cache, gid, next.clone());
                }
                s.game_page.shaders = Some(next);
            });
        });
    }

    #[cfg(windows)]
    pub fn fetch_game_compress_rpc(&self, game_id: &str, estimate: bool, epoch: u64) {
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            return;
        };
        let gid = game_id.to_string();
        rpc.call::<_, CompressStateReply>(
            METHOD_COMPRESS_STATE,
            &json!({ "id": gid, "estimate": estimate }),
            move |res| {
                if let Ok(reply) = res
                    && let Some(comp) = reply.compress
                {
                    let should_sample = !estimate
                        && ((comp.state == "none" && !comp.sampled) || comp.state == "compressed");
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.compress = Some(comp);
                        if should_sample && !s.game_props.as_ref().is_some_and(|p| p.running) {
                            s.fetch_game_compress_rpc(&gid, true, epoch);
                        }
                    });
                }
            },
        );
    }

    pub fn reload_game_properties(&mut self) {
        let Some(gid) = self.current_open_game_id() else {
            return;
        };
        self.game_page.props_load_error = None;
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        self.fetch_game_properties_rpc(&gid, epoch);
        #[cfg(not(windows))]
        let _ = (gid, epoch);
    }

    pub fn current_open_game_id(&self) -> Option<String> {
        let base_id = self.open_game_id.as_deref()?;
        if let Some(base) = self.games.iter().find(|g| g.id == base_id) {
            Some(self.active_game_view(base).id)
        } else {
            Some(base_id.to_string())
        }
    }

    pub fn open_game_system_page(&mut self, _page: &str) {
        self.close_game_page();
        self.switch_room(Room::ExoAi);
        self.switch_pc_tab(PcTab::ExoAi);
    }

    pub fn set_game_window_mode(&mut self, mode: &str) {
        let normalized = match mode {
            "fullscreen" => "fullscreen",
            _ => "borderless",
        };
        self.game_display_mode = normalized.to_string();
        if self.status == HostStatus::Standalone {
            if let Some(ref mut gs) = self.game_page.settings {
                gs.window_mode = Some(normalized.to_string());
                gs.standing = Some("applied".into());
            }
            return;
        }
        let Some(gid) = self.current_open_game_id() else {
            return;
        };
        self.apply_game_settings_payload(&gid, json!({ "id": gid, "mode": normalized }), true);
    }

    pub fn toggle_game_optimize(&mut self, id: &str, on: bool) {
        if self.status == HostStatus::Standalone {
            if let Some(ref mut gs) = self.game_page.settings {
                gs.standing = Some(if on { "applied" } else { "off" }.into());
            }
            return;
        }
        self.apply_game_settings_payload(id, json!({ "id": id, "all": true, "on": on }), on);
    }

    pub fn toggle_game_smooth_motion(&mut self, id: &str, smooth_motion: bool) {
        let applied = self
            .game_page
            .settings
            .as_ref()
            .is_some_and(|s| matches!(s.standing.as_deref(), Some("applied" | "partly")));
        if self.status == HostStatus::Standalone {
            if let Some(ref mut gs) = self.game_page.settings {
                gs.smooth_motion = Some(smooth_motion);
                if smooth_motion {
                    gs.standing = Some("applied".into());
                }
            }
            return;
        }
        let payload = if smooth_motion {
            json!({ "id": id, "smoothMotion": true, "all": true, "on": true })
        } else if applied {
            json!({ "id": id, "smoothMotion": false, "all": true, "on": true })
        } else {
            json!({ "id": id, "smoothMotion": false })
        };
        self.apply_game_settings_payload(id, payload, smooth_motion || applied);
    }

    fn apply_game_settings_payload(&mut self, id: &str, payload: serde_json::Value, next_on: bool) {
        self.game_page.settings_working = true;
        self.game_page.settings_work_on = next_on;
        self.game_page.settings_work_failed = false;
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, GameSettingsState>(METHOD_GAME_SETTINGS, &payload, move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    s.game_page.settings_working = false;
                    match res {
                        Ok(answer) if answer.ok => {
                            if let Some(ref wm) = answer.window_mode {
                                s.game_display_mode = wm.clone();
                            }
                            GamePageState::cache_put(
                                &mut s.game_page.settings_cache,
                                gid,
                                answer.clone(),
                            );
                            s.game_page.settings = Some(answer);
                        }
                        _ => {
                            s.game_page.settings_work_failed = true;
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, payload, epoch);
    }

    pub fn set_game_hdr_mode(&mut self, id: &str, mode: &str) {
        if self.status == HostStatus::Standalone {
            if let Some(ref mut h) = self.game_page.hdr_game {
                h.mode = Some(mode.to_string());
            }
            return;
        }
        self.game_page.hdr_busy = true;
        self.game_page.hdr_error = None;
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let m = mode.to_string();
            rpc.call::<_, HdrGame>(
                METHOD_HDR_GAME,
                &json!({ "id": gid, "mode": m }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.hdr_busy = false;
                        match res {
                            Ok(answer) if answer.ok => {
                                GamePageState::cache_put(
                                    &mut s.game_page.hdr_game_cache,
                                    gid,
                                    answer.clone(),
                                );
                                s.game_page.hdr_game = Some(answer);
                            }
                            _ => {
                                s.game_page.hdr_error = Some("Could not save. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, mode, epoch);
    }

    pub fn set_game_hdr_cursor_fix(&mut self, id: &str, cursor_fix: bool) {
        if self.status == HostStatus::Standalone {
            if let Some(ref mut h) = self.game_page.hdr_game {
                h.cursor_fix = cursor_fix;
            }
            return;
        }
        self.game_page.hdr_busy = true;
        self.game_page.hdr_error = None;
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, HdrGame>(
                METHOD_HDR_GAME,
                &json!({ "id": gid, "cursorFix": cursor_fix }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.hdr_busy = false;
                        match res {
                            Ok(answer) if answer.ok => {
                                GamePageState::cache_put(
                                    &mut s.game_page.hdr_game_cache,
                                    gid,
                                    answer.clone(),
                                );
                                s.game_page.hdr_game = Some(answer);
                            }
                            _ => {
                                s.game_page.hdr_error = Some("Could not save. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, cursor_fix, epoch);
    }

    pub fn turn_on_hdr_for_games(&mut self) {
        self.game_page.hdr_error = None;
        if self.status == HostStatus::Standalone {
            self.settings.hdr_for_games = true;
            return;
        }
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "hdrForGames": true }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(set) => {
                            s.settings = set;
                        }
                        Err(_) => {
                            if s.game_page.generation == epoch {
                                s.game_page.hdr_error = Some("Could not save. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = epoch;
    }

    pub fn set_game_intel_frame_gen(&mut self, id: &str, frame_gen: &str) {
        if self.status == HostStatus::Standalone {
            if let Some(ref mut g) = self.game_page.gpu_game {
                g.frame_gen = Some(frame_gen.to_string());
            }
            return;
        }
        self.game_page.gpu_busy = true;
        self.game_page.gpu_error = None;
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let fg = frame_gen.to_string();
            rpc.call::<_, GpuGame>(
                METHOD_GPU_GAME,
                &json!({ "id": gid, "frameGen": fg }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.gpu_busy = false;
                        match res {
                            Ok(answer) if answer.ok => {
                                GamePageState::cache_put(
                                    &mut s.game_page.gpu_game_cache,
                                    gid,
                                    answer.clone(),
                                );
                                s.game_page.gpu_game = Some(answer);
                            }
                            _ => {
                                s.game_page.gpu_error = Some("Could not save. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, frame_gen, epoch);
    }

    pub fn compress_game_act(&mut self, id: &str, action: &str, anti_cheat: Option<&str>) {
        self.game_page.compress_error = None;
        if self.status == HostStatus::Standalone {
            if let Some(ref mut c) = self.game_page.compress {
                match action {
                    "compress" => {
                        c.state = "compressed".into();
                        c.saved = c.estimate.unwrap_or(10_000_000_000);
                    }
                    "uncompress" => {
                        c.state = "none".into();
                        c.saved = 0;
                    }
                    "pause" => c.state = "paused".into(),
                    "resume" => c.state = "compressing".into(),
                    "cancel" => c.state = "none".into(),
                    "allow" => c.state = "none".into(),
                    _ => {}
                }
            }
            return;
        }
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let params = match anti_cheat {
                Some(ac) if action == "allow" => {
                    json!({ "id": gid, "action": action, "antiCheat": ac })
                }
                _ => json!({ "id": gid, "action": action }),
            };
            rpc.call::<_, CompressStateReply>(METHOD_COMPRESS_ACT, &params, move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    match res {
                        Ok(answer) => {
                            if let Some(comp) = answer.compress {
                                s.game_page.compress = Some(comp);
                            }
                            if !answer.ok {
                                s.game_page.compress_error = Some(
                                    answer
                                        .message
                                        .filter(|m| !m.trim().is_empty())
                                        .unwrap_or_else(|| {
                                            "Exo couldn’t do that. Try again.".into()
                                        }),
                                );
                            }
                        }
                        Err(_) => {
                            s.game_page.compress_error =
                                Some("Exo couldn’t do that. Try again.".into());
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, action, anti_cheat, epoch);
    }

    pub fn set_game_property_ex(
        &mut self,
        id: &str,
        key: &str,
        value: &str,
        secret: Option<&str>,
        row: &str,
    ) {
        self.game_page.pending_choice = None;
        self.game_page.set_note(row, "", false);
        if self.status == HostStatus::Standalone {
            if let Some(ref mut p) = self.game_props {
                match key {
                    "launchOptions" => p.launch_options = Some(value.to_string()),
                    "steamOverlay" => p.steam_overlay = Some(value == "on" || value == "true"),
                    "throughSteam" => p.through_steam = Some(value == "on" || value == "true"),
                    "language" => p.language = Some(value.to_string()),
                    "beta" => p.beta = Some(value.to_string()),
                    "folder" => p.folder = Some(value.to_string()),
                    "antiCheatLaunch" => {
                        if let Some(ref mut ac) = p.anti_cheat_launch {
                            ac.value = value.to_string();
                        }
                    }
                    "content" => {
                        if let Some((cid, state)) = value.rsplit_once(':')
                            && let Some(ref mut list) = p.content
                            && let Some(item) = list.iter_mut().find(|i| i.id == cid)
                        {
                            item.enabled = state == "on";
                        }
                    }
                    "shaders" => p.shader_cache_bytes = Some(0),
                    "betaCode" => {
                        self.game_page.beta_code = TextEdit::default().secure();
                    }
                    _ => {}
                }
            }
            self.game_page.set_note(row, "Saved.", false);
            return;
        }
        self.game_page.props_busy = Some(row.to_string());
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let k = key.to_string();
            let row_key = row.to_string();
            let params = match secret {
                Some(sec) if !sec.is_empty() => {
                    json!({ "id": gid, "key": k, "value": value, "secret": sec })
                }
                _ => json!({ "id": gid, "key": k, "value": value }),
            };
            rpc.call::<_, GamePropertySetReply>(METHOD_GAME_PROPERTIES_SET, &params, move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    s.game_page.props_busy = None;
                    match res {
                        Ok(r) => {
                            if let Some(props) = r.properties.filter(|p| p.ok) {
                                s.props_launch_opts.text =
                                    props.launch_options.clone().unwrap_or_default();
                                s.props_launch_opts.sync_invariants();
                                if props.moving.is_some() {
                                    s.game_page.move_poll_interval = MOVE_POLL_BASE_SECS;
                                    s.game_page.move_poll_due =
                                        Some(s.now_secs() + MOVE_POLL_BASE_SECS);
                                }
                                GamePageState::cache_put(
                                    &mut s.game_page.props_cache,
                                    gid.clone(),
                                    props.clone(),
                                );
                                s.game_props = Some(props);
                            }
                            if r.ok && k == "betaCode" {
                                s.game_page.beta_code = TextEdit::default().secure();
                            }
                            if r.ok {
                                if let Some(msg) = r.message.filter(|m| !m.trim().is_empty()) {
                                    s.game_page.set_note(&row_key, msg, false);
                                }
                            } else {
                                let msg =
                                    r.message.filter(|m| !m.trim().is_empty()).unwrap_or_else(
                                        || "Exo couldn’t make that change. Try again.".into(),
                                    );
                                s.game_page.set_note(&row_key, msg, true);
                            }
                        }
                        Err(_) => {
                            s.game_page.set_note(
                                &row_key,
                                "Exo couldn’t make that change. Try again.",
                                true,
                            );
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, key, value, secret, row, epoch);
    }

    pub fn repair_game_props(&mut self, id: &str) {
        self.game_page.set_note("verify", "", false);
        if self.status == HostStatus::Standalone {
            self.game_page.set_note("verify", "Files verified.", false);
            return;
        }
        self.game_page.props_busy = Some("verify".into());
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, InstallResponse>(METHOD_GAME_REPAIR, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    s.game_page.props_busy = None;
                    match res {
                        Ok(r) if r.ok => {
                            let msg = if r.message.trim().is_empty() {
                                "Files verified.".into()
                            } else {
                                r.message
                            };
                            s.game_page.set_note("verify", msg, false);
                        }
                        Ok(r) => {
                            let msg = if r.message.trim().is_empty() {
                                "Exo couldn’t check the files. Try again.".into()
                            } else {
                                r.message
                            };
                            s.game_page.set_note("verify", msg, true);
                        }
                        Err(_) => {
                            s.game_page.set_note(
                                "verify",
                                "Exo couldn’t check the files. Try again.",
                                true,
                            );
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, epoch);
    }

    pub fn open_game_folder_props(&mut self, id: &str) {
        self.game_page.set_note("openFolder", "", false);
        if self.status == HostStatus::Standalone {
            return;
        }
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_GAME_OPEN_FOLDER,
                &json!({ "id": gid }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        match res {
                            Ok(r) if r.ok => {}
                            Ok(r) => {
                                let msg =
                                    r.message.filter(|m| !m.trim().is_empty()).unwrap_or_else(
                                        || "Exo couldn’t open the folder. Try again.".into(),
                                    );
                                s.game_page.set_note("openFolder", msg, true);
                            }
                            Err(_) => {
                                s.game_page.set_note(
                                    "openFolder",
                                    "Exo couldn’t open the folder. Try again.",
                                    true,
                                );
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, epoch);
    }

    pub fn uninstall_game_page(&mut self, id: &str) {
        self.disarm_confirm();
        if self.status == HostStatus::Standalone {
            self.notice = Some("Uninstall started.".into());
            return;
        }
        let added_here = self
            .games
            .iter()
            .find(|g| g.id == id)
            .is_some_and(|g| g.store == "local");
        let fallback = if added_here {
            "Exo couldn’t remove the game. Try again."
        } else {
            "Exo couldn’t uninstall the game. Try again."
        };
        self.game_page.props_busy = Some("uninstall".into());
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, UninstallResponse>(
                METHOD_GAME_UNINSTALL,
                &json!({ "id": gid }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.game_page.generation == epoch {
                            s.game_page.props_busy = None;
                        }
                        match res {
                            Ok(r) if r.ok || r.handoff_only => {
                                if r.handoff_only || r.queued {
                                    let msg =
                                        r.message.filter(|m| !m.trim().is_empty()).unwrap_or_else(
                                            || "Finish uninstalling it in the store app.".into(),
                                        );
                                    s.game_page.status_msg = Some(msg.clone());
                                    s.notice = Some(msg);
                                } else {
                                    s.close_game_page();
                                    s.refresh_library();
                                }
                            }
                            Ok(r) => {
                                let msg = r
                                    .message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| fallback.into());
                                s.game_page.status_msg = Some(msg.clone());
                                s.notice = Some(msg);
                            }
                            Err(_) => {
                                s.game_page.status_msg = Some(fallback.into());
                                s.notice = Some(fallback.into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (id, fallback, epoch);
    }

    pub fn dlss_restore_game(&mut self, id: &str) {
        self.game_page.set_note("restore", "", false);
        if self.status == HostStatus::Standalone {
            self.game_page
                .set_note("restore", "The game's own files are back.", false);
            return;
        }
        self.game_page.props_busy = Some("restore".into());
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, DlssRunResult>(METHOD_DLSS_RESTORE, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation != epoch {
                        return;
                    }
                    s.game_page.props_busy = None;
                    s.game_page.dlss_invalidate(&gid);
                    s.fetch_game_dlss_rpc(&gid, false, epoch);
                    match res {
                        Ok(r) => {
                            let failed = r
                                .files
                                .iter()
                                .find(|f| f.state.as_deref() == Some("failed"));
                            let bad = !r.ok || failed.is_some();
                            let msg = failed.and_then(|f| f.message.clone()).unwrap_or_else(|| {
                                if r.ok {
                                    "The game's own files are back.".into()
                                } else {
                                    r.message.unwrap_or_else(|| {
                                        "Exo couldn’t make that change. Try again.".into()
                                    })
                                }
                            });
                            s.game_page.set_note("restore", msg, bad);
                        }
                        Err(_) => {
                            s.game_page.set_note(
                                "restore",
                                "Exo couldn’t make that change. Try again.",
                                true,
                            );
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, epoch);
    }

    pub fn dlss_consent_game(&mut self, id: &str, allow: bool, restore_after: bool) {
        self.game_page.set_note("consent", "", false);
        let anti_cheat = self
            .game_page
            .dlss
            .as_ref()
            .and_then(|d| d.anti_cheat.clone());
        let done_msg = if allow {
            "Exo may update this game's upscalers."
        } else if restore_after {
            "Stopped. The game's own files are back."
        } else {
            "Stopped."
        };
        if self.status == HostStatus::Standalone {
            if let Some(ref mut d) = self.game_page.dlss {
                d.consent = Some(if allow { "given" } else { "available" }.into());
            }
            self.game_page.set_note("consent", done_msg, false);
            return;
        }
        self.game_page.props_busy = Some("consent".into());
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let params = if allow {
                if let Some(ac) = anti_cheat {
                    json!({ "id": gid, "allow": true, "antiCheat": ac })
                } else {
                    json!({ "id": gid, "allow": true })
                }
            } else {
                json!({ "id": gid, "allow": false })
            };
            let rpc_restore = rpc.clone();
            rpc.call::<_, ActionResult>(METHOD_DLSS_CONSENT, &params, move |res| match res {
                Ok(ans) if ans.ok && restore_after => {
                    let gid2 = gid.clone();
                    rpc_restore.call::<_, DlssRunResult>(
                        METHOD_DLSS_RESTORE,
                        &json!({ "id": gid2 }),
                        move |r2| {
                            handle.update(move |s, _| {
                                if s.game_page.generation != epoch {
                                    return;
                                }
                                s.game_page.props_busy = None;
                                s.game_page.dlss_invalidate(&gid2);
                                s.fetch_game_dlss_rpc(&gid2, false, epoch);
                                match r2 {
                                    Ok(rr) => {
                                        let failed = rr
                                            .files
                                            .iter()
                                            .find(|f| f.state.as_deref() == Some("failed"));
                                        let bad = !rr.ok || failed.is_some();
                                        let msg = failed
                                            .and_then(|f| f.message.clone())
                                            .unwrap_or_else(|| {
                                                if rr.ok {
                                                    done_msg.into()
                                                } else {
                                                    rr.message.unwrap_or_else(|| {
                                                        "Exo couldn’t make that change. Try again."
                                                            .into()
                                                    })
                                                }
                                            });
                                        s.game_page.set_note("consent", msg, bad);
                                    }
                                    Err(_) => {
                                        s.game_page.set_note(
                                            "consent",
                                            "Exo couldn’t make that change. Try again.",
                                            true,
                                        );
                                    }
                                }
                            });
                        },
                    );
                }
                Ok(ans) => {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.props_busy = None;
                        s.game_page.dlss_invalidate(&gid);
                        s.fetch_game_dlss_rpc(&gid, false, epoch);
                        if ans.ok {
                            s.game_page.set_note("consent", done_msg, false);
                        } else {
                            let msg = ans.message.unwrap_or_else(|| {
                                "Exo couldn’t make that change. Try again.".into()
                            });
                            s.game_page.set_note("consent", msg, true);
                        }
                    });
                }
                Err(_) => {
                    handle.update(move |s, _| {
                        if s.game_page.generation != epoch {
                            return;
                        }
                        s.game_page.props_busy = None;
                        s.game_page.set_note(
                            "consent",
                            "Exo couldn’t make that change. Try again.",
                            true,
                        );
                    });
                }
            });
        }
        #[cfg(not(windows))]
        let _ = (id, allow, restore_after, anti_cheat, done_msg, epoch);
    }

    pub fn art_action_game(&mut self, id: &str, action: ArtAction) {
        if self.game_page.art_action.is_some() {
            return;
        }
        if self.status == HostStatus::Standalone {
            if let Some(slot) = self.games.iter_mut().find(|g| g.id == id) {
                slot.art_revision = Some(slot.art_revision.unwrap_or(0) + 1);
                match action {
                    ArtAction::Replace => slot.cover_source = Some("custom".into()),
                    ArtAction::Reset => slot.cover_source = Some("store".into()),
                    ArtAction::Refetch => {}
                }
            }
            return;
        }
        self.game_page.art_action = Some(action);
        let method = match action {
            ArtAction::Replace => METHOD_ART_REPLACE,
            ArtAction::Reset => METHOD_ART_RESET,
            ArtAction::Refetch => METHOD_ART_REFETCH,
        };
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, ArtworkMutationResponse>(method, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation == epoch {
                        s.game_page.art_action = None;
                    }
                    match res {
                        Ok(r) if r.cancelled => {
                            if s.game_page.generation == epoch {
                                s.game_page.status_msg = None;
                            }
                        }
                        Ok(r) if r.ok => {
                            if let Some(updated) = r.game {
                                if let Some(slot) = s.games.iter_mut().find(|g| g.id == updated.id)
                                {
                                    slot.cover_url = updated.cover_url;
                                    slot.cover_source = updated.cover_source;
                                    slot.art_revision = updated.art_revision;
                                }
                            } else if let Some(rev) = r.art_revision
                                && let Some(slot) = s.games.iter_mut().find(|g| g.id == gid)
                            {
                                slot.art_revision = Some(rev);
                            }
                            if s.game_page.generation == epoch {
                                s.game_page.status_msg = None;
                            }
                        }
                        Ok(r) => {
                            let msg =
                                r.message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| {
                                        "Exo couldn’t update the cover. Try again.".into()
                                    });
                            if s.game_page.generation == epoch {
                                s.game_page.status_msg = Some(msg.clone());
                            }
                            s.notice = Some(msg);
                        }
                        Err(_) => {
                            let msg = "Exo couldn’t update the cover. Try again.";
                            if s.game_page.generation == epoch {
                                s.game_page.status_msg = Some(msg.into());
                            }
                            s.notice = Some(msg.into());
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, method, epoch);
    }

    pub fn dequeue_game(&mut self, id: &str) {
        self.disarm_confirm();
        if self.status == HostStatus::Standalone {
            if let Some(ref mut q) = self.install_queue {
                q.queued_game_ids.retain(|x| x != id);
            }
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, DequeueReply>(METHOD_GAME_DEQUEUE, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| match res {
                    Ok(r) => {
                        if let Some(ref mut q) = s.install_queue {
                            q.queued_game_ids = r.queued_game_ids;
                            q.paused_downloads = r.paused_downloads;
                        }
                        if !r.ok {
                            let msg =
                                r.message
                                    .filter(|m| !m.trim().is_empty())
                                    .unwrap_or_else(|| {
                                        "Could not remove this game from the queue.".into()
                                    });
                            s.game_page.status_msg = Some(msg.clone());
                            s.notice = Some(msg);
                        }
                    }
                    Err(_) => {
                        let msg = "Exo couldn’t take it off the queue. Try again.";
                        s.game_page.status_msg = Some(msg.into());
                        s.notice = Some(msg.into());
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn resume_download_game(&mut self, id: &str) {
        if self.status == HostStatus::Standalone {
            self.notice = Some("Resuming download…".into());
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, InstallResponse>(
                METHOD_GAME_RESUME_DOWNLOAD,
                &json!({ "id": gid }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) => {
                            if let Some(prog) = r.progress.filter(|p| p.is_active) {
                                s.install_progress = Some(prog);
                            }
                            if let Some(ids) = r.queued_game_ids
                                && let Some(ref mut q) = s.install_queue
                            {
                                q.queued_game_ids = ids;
                            }
                            if !r.ok && !r.message.trim().is_empty() {
                                s.game_page.status_msg = Some(r.message.clone());
                                s.notice = Some(r.message);
                            }
                        }
                        Err(e) => {
                            s.notice = Some(format!("Could not resume download: {e}"));
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn stop_game_page(&mut self, id: &str) {
        self.game_page.primary_busy = true;
        let epoch = self.game_page.generation;
        if self.status == HostStatus::Standalone {
            self.game_page.primary_busy = false;
            if let Some(g) = self.games.iter_mut().find(|g| g.id == id) {
                g.is_running = false;
                g.can_stop = false;
            }
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let rpc2 = rpc.clone();
            rpc.call::<_, ActionResult>(METHOD_GAME_STOP, &json!({ "id": gid }), move |res| {
                handle.update(move |s, _| {
                    if s.game_page.generation == epoch {
                        s.game_page.primary_busy = false;
                    }
                    match res {
                        Ok(r) if r.ok => {
                            if let Some(g) = s.games.iter_mut().find(|g| g.id == gid) {
                                g.is_running = false;
                                g.can_stop = false;
                            }
                            if let Some(msg) = r.message.filter(|m| !m.trim().is_empty()) {
                                s.game_page.status_msg = Some(msg);
                            }
                            let h2 = s.handle.clone();
                            let gid2 = gid.clone();
                            rpc2.call::<_, GameGetReply>(
                                METHOD_GAME_GET,
                                &json!({ "id": gid2 }),
                                move |gr| {
                                    if let Ok(reply) = gr
                                        && let Some(fresh) = reply.game
                                        && let Some(h) = h2
                                    {
                                        h.update(move |st, _| {
                                            if let Some(slot) =
                                                st.games.iter_mut().find(|g| g.id == fresh.id)
                                            {
                                                *slot = fresh;
                                            }
                                        });
                                    }
                                },
                            );
                        }
                        Ok(r) => {
                            let msg = r
                                .message
                                .filter(|m| !m.trim().is_empty())
                                .unwrap_or_else(|| "Could not close the game.".into());
                            s.game_page.status_msg = Some(msg.clone());
                            s.notice = Some(msg);
                        }
                        Err(_) => {
                            let msg = "Exo couldn’t close the game. Try again.";
                            s.game_page.status_msg = Some(msg.into());
                            s.notice = Some(msg.into());
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (id, epoch);
    }

    pub fn open_external_url(&mut self, url: &str) {
        if self.status == HostStatus::Standalone {
            self.notice = Some(format!("Open {url}"));
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let target = url.to_string();
            rpc.call::<_, OpenUrlReply>(
                METHOD_SHELL_OPEN_URL,
                &json!({ "url": target }),
                move |res| {
                    if let Err(e) = res {
                        handle.update(move |s, _| {
                            s.notice = Some(format!("Could not open link: {e}"));
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = url;
    }

    pub fn select_game_source(&mut self, variant_id: &str) {
        let Some(base_id) = self.open_game_id.clone() else {
            return;
        };
        let Some(card) = self.games.iter().find(|g| g.id == base_id).cloned() else {
            return;
        };
        if !card.variants.iter().any(|v| v.id == variant_id) {
            return;
        }
        self.game_page.selected_variant_id = Some(variant_id.to_string());
        self.game_page.status_msg = None;
        if let Some(canon) = card.canonical_title_key.clone() {
            self.settings
                .preferred_game_sources
                .insert(canon.clone(), variant_id.to_string());
            #[cfg(windows)]
            if self.status == HostStatus::Connected
                && let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone())
            {
                let vid = variant_id.to_string();
                rpc.call::<_, LauncherSettings>(
                    METHOD_SETTINGS_SET,
                    &json!({ "preferredGameSources": { canon: vid } }),
                    move |res| {
                        if let Ok(saved) = res {
                            handle.update(move |s, _| {
                                s.settings = saved;
                            });
                        }
                    },
                );
            }
        }
        self.load_game_page_data(&base_id);
    }

    pub fn arm_game_confirm(&mut self, key: &str, tx: &mut Tx) -> bool {
        const ARMED_SECS: f64 = 4.0;
        if self.is_confirm_armed(key) {
            self.disarm_confirm();
            true
        } else {
            let deadline = self.now_secs() + ARMED_SECS;
            self.armed_confirm = Some((key.to_string(), deadline));
            tx.wake_after(Duration::from_secs_f64(ARMED_SECS));
            false
        }
    }

    pub fn handle_game_achievements(&mut self, ev: GameAchievementsEvent) -> bool {
        if ev.total == 0 {
            return false;
        }
        let store = self
            .games
            .iter()
            .find(|g| g.id == ev.id)
            .map(|g| g.store.as_str())
            .unwrap_or("");
        let sk = selection_key_for(&ev.id, store);
        GamePageState::cache_put(
            &mut self.game_page.achievements_cache,
            sk,
            (ev.unlocked, ev.total),
        );
        let is_open = self.current_open_game_id().as_deref() == Some(ev.id.as_str());
        if is_open {
            let prev_heat = self.game_extras.as_ref().and_then(|e| e.drive_heat.clone());
            self.game_extras = Some(GameExtras {
                ok: true,
                achievements_unlocked: Some(ev.unlocked),
                achievements_total: Some(ev.total),
                drive_heat: prev_heat,
                message: None,
            });
        }
        is_open
    }

    pub fn handle_game_optimized_again(&mut self, _ev: GameOptimizeEvent) -> bool {
        let Some(gid) = self.current_open_game_id() else {
            return false;
        };
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        self.fetch_game_settings_rpc(&gid, epoch);
        #[cfg(not(windows))]
        let _ = (gid, epoch);
        true
    }

    pub fn handle_game_optimize_held(&mut self, _ev: GameOptimizeEvent) -> bool {
        let Some(gid) = self.current_open_game_id() else {
            return false;
        };
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        self.fetch_game_settings_rpc(&gid, epoch);
        #[cfg(not(windows))]
        let _ = (gid, epoch);
        true
    }

    pub fn handle_compress_progress(&mut self, ev: CompressProgressEvent) -> bool {
        let is_open = self.current_open_game_id().as_deref() == Some(ev.id.as_str());
        if is_open {
            self.game_page.compress = Some(ev.compress);
        }
        is_open
    }

    pub fn on_game_run_state(&mut self, game_id: &str) {
        if self.current_open_game_id().as_deref() != Some(game_id) {
            return;
        }
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        {
            self.fetch_game_settings_rpc(game_id, epoch);
            self.fetch_game_properties_rpc(game_id, epoch);
        }
        #[cfg(not(windows))]
        let _ = epoch;
    }

    pub fn on_game_shaders_changed(&mut self) {
        let Some(gid) = self.current_open_game_id() else {
            return;
        };
        let epoch = self.game_page.generation;
        #[cfg(windows)]
        self.fetch_game_shaders_rpc(&gid, epoch);
        #[cfg(not(windows))]
        let _ = (gid, epoch);
    }

    pub fn poll_game_move_if_due(&mut self, now: f64) {
        let Some(due) = self.game_page.move_poll_due else {
            return;
        };
        if now < due {
            return;
        }
        self.game_page.move_poll_due = None;
        if let Some(gid) = self.current_open_game_id() {
            let epoch = self.game_page.generation;
            #[cfg(windows)]
            self.fetch_game_properties_rpc(&gid, epoch);
            #[cfg(not(windows))]
            let _ = (gid, epoch);
        }
    }

    pub fn schedule_upscaler_startup_work(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        if !self.game_page.dlss_preloaded
            && !self.game_page.dlss_preload_pending
            && self.game_page.dlss_preload_due.is_none()
            && !self.games.is_empty()
        {
            self.game_page.dlss_preload_pending = true;
        }
        if !self.game_page.dlss_newer_checked
            && !self.game_page.dlss_newer_pending
            && self.game_page.dlss_newer_due.is_none()
        {
            self.game_page.dlss_newer_pending = true;
        }
    }

    pub fn tick_game_page_timers(&mut self, now: f64, tx: &mut Tx) {
        self.poll_game_move_if_due(now);
        if let Some(due) = self.game_page.move_poll_due {
            tx.wake_at(due);
        }

        if self.game_page.dlss_preload_pending {
            self.game_page.dlss_preload_pending = false;
            self.game_page.dlss_preload_due = Some(now + DLSS_PRELOAD_DELAY_SECS);
        }
        if let Some(due) = self.game_page.dlss_preload_due {
            if now >= due {
                self.game_page.dlss_preload_due = None;
                self.game_page.dlss_preloaded = true;
                self.preload_upscaler_statuses(now);
            } else {
                tx.wake_at(due);
            }
        }

        if self.game_page.dlss_newer_pending {
            self.game_page.dlss_newer_pending = false;
            self.game_page.dlss_newer_due = Some(now + DLSS_NEWER_DELAY_SECS);
        }
        if let Some(due) = self.game_page.dlss_newer_due {
            if now >= due {
                self.game_page.dlss_newer_due = None;
                self.game_page.dlss_newer_checked = true;
                self.check_dlss_newer();
            } else {
                tx.wake_at(due);
            }
        }
    }

    pub fn preload_upscaler_statuses(&mut self, now: f64) {
        if self.status != HostStatus::Connected {
            return;
        }
        let visible: Vec<Game> = self
            .filtered_games()
            .into_iter()
            .take(DLSS_PRELOAD_MAX_GAMES)
            .cloned()
            .collect();
        let ids = collect_upscaler_preload_ids(&visible, DLSS_PRELOAD_MAX_GAMES);
        if ids.is_empty() {
            return;
        }
        self.game_page.dlss_preload_queue = ids.into_iter().collect();
        self.game_page.dlss_preload_deadline = Some(now + DLSS_PRELOAD_BUDGET_SECS);
        #[cfg(windows)]
        self.pump_dlss_preload(now);
        #[cfg(not(windows))]
        let _ = now;
    }

    #[cfg(windows)]
    fn pump_dlss_preload(&mut self, now: f64) {
        let Some(deadline) = self.game_page.dlss_preload_deadline else {
            return;
        };
        if now >= deadline {
            self.game_page.dlss_preload_queue.clear();
            self.game_page.dlss_preload_deadline = None;
            return;
        }
        let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) else {
            self.game_page.dlss_preload_queue.clear();
            self.game_page.dlss_preload_deadline = None;
            return;
        };
        while self.game_page.dlss_preload_active < DLSS_PRELOAD_CONCURRENCY {
            let Some(gid) = self.game_page.dlss_preload_queue.pop_front() else {
                if self.game_page.dlss_preload_active == 0 {
                    self.game_page.dlss_preload_deadline = None;
                }
                break;
            };
            let key = GamePageState::dlss_key(&gid);
            if self.game_page.dlss_get_fresh(&gid, now).is_some()
                || self.game_page.dlss_inflight.contains(&key)
            {
                continue;
            }
            self.game_page.dlss_inflight.insert(key.clone());
            self.game_page.dlss_preload_active += 1;
            let h = handle.clone();
            rpc.call::<_, DlssStatus>(METHOD_DLSS_STATUS, &json!({ "id": gid }), move |res| {
                h.update(move |s, _| {
                    s.game_page.dlss_inflight.remove(&key);
                    s.game_page.dlss_preload_active =
                        s.game_page.dlss_preload_active.saturating_sub(1);
                    let now = s.now_secs();
                    if let Ok(st) = res {
                        if st.ok != Some(false) {
                            s.game_page.dlss_put(&gid, st.clone(), now);
                        }
                        let matches_open = s
                            .current_open_game_id()
                            .map(|cur| GamePageState::dlss_key(&cur) == key)
                            .unwrap_or(false);
                        if matches_open && s.game_page.dlss.is_none() {
                            s.game_page.dlss_loading = false;
                            s.game_page.dlss = Some(st);
                        }
                    }
                    s.pump_dlss_preload(now);
                });
            });
        }
    }

    pub fn check_dlss_newer(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, DlssNewerReply>(METHOD_DLSS_NEWER, &json!({}), move |res| {
                if let Ok(reply) = res
                    && let Some(text) = format_dlss_newer_notice(&reply.items)
                {
                    handle.update(move |s, _| {
                        s.push_notice_with_action(
                            text,
                            NoticeTone::Neutral,
                            "Download",
                            NoticeAction::DownloadNewerUpscalers,
                        );
                    });
                }
            });
        }
    }

    pub fn download_newer_upscalers(&mut self) {
        if self.status == HostStatus::Standalone {
            self.push_notice("Downloaded and checked.", NoticeTone::Good);
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_DLSS_DOWNLOAD_NEWER, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(done) if done.ok => {
                        s.push_notice("Downloaded and checked.", NoticeTone::Good);
                    }
                    Ok(done) => {
                        let msg = done
                            .message
                            .filter(|m| !m.trim().is_empty())
                            .unwrap_or_else(|| "Exo couldn't download it.".into());
                        s.push_notice(msg, NoticeTone::Bad);
                    }
                    Err(_) => {
                        s.push_notice("Exo couldn't download it.", NoticeTone::Bad);
                    }
                });
            });
        }
    }

    pub fn run_notice_action(&mut self, action: NoticeAction) {
        match action {
            NoticeAction::DownloadNewerUpscalers => self.download_newer_upscalers(),
        }
    }

    pub fn select_props_section(&mut self, section: PropsSectionId, idx: usize) {
        self.disarm_confirm();
        self.game_page.pending_choice = None;
        self.game_page.props_tab_id = Some(section);
        self.game_props_tab = idx;
        self.props_open = false;
    }
}

pub fn collect_upscaler_preload_ids(games: &[Game], max_games: usize) -> Vec<String> {
    let mut seen = HashSet::new();
    let mut ids = Vec::new();
    for game in games.iter().take(max_games) {
        if game.installed {
            let key = GamePageState::dlss_key(&game.id);
            if seen.insert(key) {
                ids.push(game.id.clone());
            }
        }
        for variant in &game.variants {
            if variant.installed {
                let key = GamePageState::dlss_key(&variant.id);
                if seen.insert(key) {
                    ids.push(variant.id.clone());
                }
            }
        }
    }
    ids
}

pub fn format_dlss_newer_notice(items: &[DlssNewerItem]) -> Option<String> {
    let mut seen = HashSet::new();
    let mut families = Vec::new();
    for item in items {
        if !item.family.is_empty() && seen.insert(item.family.clone()) {
            families.push(item.family.clone());
        }
    }
    match families.len() {
        0 => None,
        1 => Some(format!("A newer {} is available", families[0])),
        _ => Some(format!("Newer {} are available", families.join(", "))),
    }
}

#[cfg(test)]
mod upscaler_cache_tests {
    use super::*;
    use exo_rpc::GameVariant;

    #[test]
    fn collect_upscaler_preload_ids_dedupes_installed_games_and_variants() {
        let g1 = Game {
            id: "steam:10".into(),
            installed: true,
            variants: vec![
                GameVariant {
                    id: "STEAM:10".into(),
                    installed: true,
                    ..Default::default()
                },
                GameVariant {
                    id: "epic:10".into(),
                    installed: true,
                    ..Default::default()
                },
                GameVariant {
                    id: "gog:10".into(),
                    installed: false,
                    ..Default::default()
                },
            ],
            ..Default::default()
        };
        let g2 = Game {
            id: "steam:20".into(),
            installed: false,
            variants: vec![GameVariant {
                id: "xbox:20".into(),
                installed: true,
                ..Default::default()
            }],
            ..Default::default()
        };
        let g3 = Game {
            id: "steam:30".into(),
            installed: true,
            ..Default::default()
        };

        let ids = collect_upscaler_preload_ids(&[g1, g2, g3], 2);
        assert_eq!(ids, vec!["steam:10", "epic:10", "xbox:20"]);
    }

    #[test]
    fn dlss_cache_ttl_case_insensitive_and_invalidation() {
        let mut state = GamePageState::new();
        let st = DlssStatus {
            ok: Some(true),
            ..Default::default()
        };
        state.dlss_put("Steam:1091500", st, 100.0);
        assert!(
            state
                .dlss_get_fresh("steam:1091500", 100.0 + 60.0)
                .is_some()
        );
        assert!(
            state
                .dlss_get_fresh("steam:1091500", 100.0 + DLSS_CACHE_TTL_SECS)
                .is_none()
        );
        state.dlss_invalidate("STEAM:1091500");
        assert!(state.dlss_peek("steam:1091500").is_none());
    }

    #[test]
    fn format_dlss_newer_notice_matches_react_copy() {
        assert_eq!(format_dlss_newer_notice(&[]), None);
        assert_eq!(
            format_dlss_newer_notice(&[
                DlssNewerItem {
                    family: "DLSS".into(),
                    version: "310.3.0".into(),
                },
                DlssNewerItem {
                    family: "DLSS".into(),
                    version: "310.3.1".into(),
                },
            ]),
            Some("A newer DLSS is available".into())
        );
        assert_eq!(
            format_dlss_newer_notice(&[
                DlssNewerItem {
                    family: "DLSS".into(),
                    version: "310.3.0".into(),
                },
                DlssNewerItem {
                    family: "XeSS".into(),
                    version: "2.1.0".into(),
                },
            ]),
            Some("Newer DLSS, XeSS are available".into())
        );
    }

    #[test]
    fn apply_library_schedules_upscaler_preload_and_newer_check() {
        let mut app = AppState::with_demo_games();
        app.status = HostStatus::Connected;
        app.game_page.dlss_preloaded = false;
        app.game_page.dlss_newer_checked = false;
        app.schedule_upscaler_startup_work();
        assert!(app.game_page.dlss_preload_pending);
        assert!(app.game_page.dlss_newer_pending);
    }
}
