use crate::discord_state::DiscordState;
use crate::gamepad::GamepadAction;
use crate::media_state::MediaState;
use crate::pc_state::SettingsAndPcState;
use crate::tour;
use crate::video_decoder::VideoFrameRouter;
use exo_kit::SPRING_MOVE;
use exo_rpc::types::COVER_CACHE_ORIGIN;
use exo_rpc::{
    ActionResult, AppUpdateStateEvent, ArtBase, AutopilotLive, AutopilotReportEvent,
    CaptureLiveState, CaptureReelEvent, CaptureSavedEvent, CaptureStatusReply, ExoAccountState,
    Game, GameExtras, GameGetReply, GameProperties, GamePropertySetReply, GameRunStateEvent,
    InstallProgress, InstallQueueEvent, KeyPreview, LaunchStatusEvent, LauncherSettings,
    LibraryPayload, METHOD_EXO_ACCOUNT_STATE, METHOD_GAME_CANCEL_INSTALL, METHOD_GAME_EXTRAS,
    METHOD_GAME_GET, METHOD_GAME_INSTALL, METHOD_GAME_LAUNCH, METHOD_GAME_OPEN_FOLDER,
    METHOD_GAME_PAUSE_INSTALL, METHOD_GAME_PROPERTIES, METHOD_GAME_PROPERTIES_SET,
    METHOD_GAME_REPAIR, METHOD_GAME_STOP, METHOD_GAME_UNINSTALL, METHOD_GAME_UPDATE,
    METHOD_KEYS_ACTIVATE, METHOD_KEYS_PREVIEW, METHOD_LIBRARY_REFRESH, METHOD_SETTINGS_SET,
    METHOD_SHELL_BOOTSTRAP, METHOD_STORE_GAME, METHOD_STORE_GAME_OFFERS, METHOD_STORE_HOME,
    METHOD_STORE_OPEN, METHOD_STORE_SEARCH, MusicVolumeEvent, ShellBootstrap, StoreArt,
    StoreChangedEvent, StoreGame, StoreHome, StoreOffer, StoreOtherOffers, StoreSearchReply,
    StoreShelf, StoreTile, remote_art,
};
use rune::{Image, ImageCache, ScrollPos, TextEdit, Tx, Waker};
use serde_json::json;
use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet, VecDeque};
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::Arc;
use std::time::Duration;

#[cfg(windows)]
use exo_rpc::{RequestId, RpcClient};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum InputMode {
    #[default]
    Pointer,
    Gamepad,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Room {
    #[default]
    Library,
    Store,
    Discord,
    Music,
    Devices,
    ExoAi,
    Apps,
    Settings,
}

impl Room {
    pub const NAV_TABS: [Room; 7] = [
        Room::Library,
        Room::Store,
        Room::Discord,
        Room::Music,
        Room::Devices,
        Room::ExoAi,
        Room::Apps,
    ];

    pub const fn label(self) -> &'static str {
        match self {
            Room::Library => "Library",
            Room::Store => "Store",
            Room::Discord => "Discord",
            Room::Music => "Music",
            Room::Devices => "Devices",
            Room::ExoAi => "Exo AI",
            Room::Apps => "Apps",
            Room::Settings => "Settings",
        }
    }

    pub const fn key(self) -> &'static str {
        match self {
            Room::Library => "nav-library",
            Room::Store => "nav-store",
            Room::Discord => "nav-discord",
            Room::Music => "nav-music",
            Room::Devices => "nav-devices",
            Room::ExoAi => "nav-exo-ai",
            Room::Apps => "nav-apps",
            Room::Settings => "nav-settings",
        }
    }

    pub const fn tab_index(self) -> usize {
        match self {
            Room::Library => 0,
            Room::Store => 1,
            Room::Discord => 2,
            Room::Music => 3,
            Room::Devices => 4,
            Room::ExoAi => 5,
            Room::Apps => 6,
            Room::Settings => 7,
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LibraryFilter {
    #[default]
    All,
    Installed,
    Recent,
    Running,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LibrarySort {
    #[default]
    LastLaunched,
    Playtime,
    Title,
    Size,
}

impl LibrarySort {
    pub const fn label(self) -> &'static str {
        match self {
            Self::LastLaunched => "Last launched",
            Self::Playtime => "Playtime",
            Self::Title => "A–Z",
            Self::Size => "Size",
        }
    }

    pub const fn next(self) -> Self {
        match self {
            Self::LastLaunched => Self::Playtime,
            Self::Playtime => Self::Title,
            Self::Title => Self::Size,
            Self::Size => Self::LastLaunched,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HostStatus {
    Standalone,
    Connecting,
    Connected,
    Error(String),
}

#[derive(Clone, Debug)]
pub enum PaletteTarget {
    Room(Room),
    OpenGame(String),
    RefreshLibrary,
    ActivateKey,
    StartTour,
}

#[derive(Clone, Debug)]
pub struct PaletteItem {
    pub id: String,
    pub group: &'static str,
    pub title: String,
    pub target: PaletteTarget,
}

pub const NOTICE_DURATION_SECS: f64 = 4.0;
pub const MAX_NOTICES: usize = 3;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NoticeTone {
    Neutral,
    Good,
    Bad,
}

#[derive(Clone, Debug)]
pub struct NoticeItem {
    pub id: u64,
    pub text: String,
    pub tone: NoticeTone,
    pub remaining_secs: f64,
    pub last_tick: Option<f64>,
    pub hovered: bool,
    pub dragging: bool,
    pub drag_x: f32,
    pub drag_prev_x: f32,
    pub drag_prev_t: f64,
    pub drag_vx: f32,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum MiniBarSlot {
    #[default]
    Inline,
    Medium,
    Compact,
    Hidden,
}

impl MiniBarSlot {
    pub fn from_width(w: f32) -> Self {
        if w >= 1880.0 {
            Self::Inline
        } else if w >= 1480.0 {
            Self::Medium
        } else if w >= 1180.0 {
            Self::Compact
        } else {
            Self::Hidden
        }
    }
}

pub struct AppState {
    pub room: Room,
    pub prev_room: Room,
    pub room_dir: f32,
    pub mini_bar_slot: MiniBarSlot,
    pub library_all_games: bool,
    pub library_search_open: bool,
    pub library_search: TextEdit,
    pub library_more_open: bool,
    pub capture_menu_open: bool,
    pub deck_scroll: Rc<Cell<ScrollPos>>,
    pub deck_scroll_x: f32,
    pub store_shelf_scrolls: [Rc<Cell<ScrollPos>>; 4],
    pub store_shelf_x: [f32; 4],
    pub filter: LibraryFilter,
    pub sort: LibrarySort,
    pub status: HostStatus,
    pub games: Vec<Game>,
    pub recent_ids: Vec<String>,
    pub selected_game_id: Option<String>,
    pub open_game_id: Option<String>,
    pub cover_flight_id: Option<String>,
    pub hovered_cover_key: Option<String>,
    pub game_extras: Option<GameExtras>,
    pub game_props: Option<GameProperties>,
    pub props_open: bool,
    pub game_props_tab: usize,
    pub props_launch_opts: TextEdit,
    pub uninstall_confirm: bool,
    pub armed_confirm: Option<(String, f64)>,
    pub install_progress: Option<InstallProgress>,
    pub install_queue: Option<InstallQueueEvent>,
    pub app_update_state: String,
    pub key_modal_open: bool,
    pub key_input: TextEdit,
    pub key_preview: Option<KeyPreview>,
    pub key_submitting: bool,
    pub store_home: Option<StoreHome>,
    pub store_tab: usize,
    pub store_loading: bool,
    pub store_hero_index: usize,
    pub store_query: TextEdit,
    pub store_search_results: Option<Vec<StoreTile>>,
    pub store_game: Option<StoreGame>,
    pub store_offers: Option<StoreOtherOffers>,
    pub tour_step: Option<usize>,
    pub tour_seen: bool,
    pub gamepad_connected: bool,
    pub input_mode: InputMode,
    pub settings: LauncherSettings,
    pub pc: SettingsAndPcState,
    pub media: MediaState,
    pub discord: DiscordState,
    pub art_base: ArtBase,
    pub notice: Option<String>,
    pub notices: VecDeque<NoticeItem>,
    next_notice_id: u64,
    notice_last_synced: Option<String>,
    clock_secs: f64,
    pub refreshing: bool,
    pub palette_open: bool,
    pub palette_query: TextEdit,
    pub palette_index: usize,
    pub visible_limit: usize,
    pub image_cache: Option<ImageCache>,
    pub remote_cache_dir: Option<PathBuf>,
    covers: RefCell<HashMap<String, Option<Image>>>,
    remote_covers: RefCell<HashMap<String, Option<Image>>>,
    remote_inflight: RefCell<HashSet<String>>,
    pub video_router: VideoFrameRouter,
    #[cfg(windows)]
    pub rpc: Option<Arc<RpcClient>>,
    #[cfg(windows)]
    pub handle: Option<rune::AppHandle<AppState>>,
    #[cfg(windows)]
    inflight_requests: HashMap<&'static str, RequestId>,
    #[cfg(windows)]
    pub inflight_discord_channel: Option<String>,
}

impl Default for AppState {
    fn default() -> Self {
        Self::with_demo_games()
    }
}

impl AppState {
    pub fn with_demo_games() -> Self {
        let games = demo_games();
        let selected_game_id = games.first().map(|g| g.id.clone());
        Self {
            room: Room::Library,
            prev_room: Room::Library,
            room_dir: 1.0,
            mini_bar_slot: MiniBarSlot::Inline,
            library_all_games: false,
            library_search_open: false,
            library_search: TextEdit::default(),
            library_more_open: false,
            capture_menu_open: false,
            deck_scroll: Rc::new(Cell::new(ScrollPos::default())),
            deck_scroll_x: 0.0,
            store_shelf_scrolls: std::array::from_fn(|_| Rc::new(Cell::new(ScrollPos::default()))),
            store_shelf_x: [0.0; 4],
            filter: LibraryFilter::All,
            sort: LibrarySort::LastLaunched,
            status: HostStatus::Standalone,
            games,
            recent_ids: vec![
                "demo:cyberpunk".into(),
                "demo:cs2".into(),
                "demo:elden".into(),
            ],
            selected_game_id,
            open_game_id: None,
            cover_flight_id: None,
            hovered_cover_key: None,
            game_extras: Some(GameExtras {
                ok: true,
                achievements_unlocked: Some(34),
                achievements_total: Some(57),
                message: None,
            }),
            game_props: Some(GameProperties {
                ok: true,
                launch_options: Some("-skipStartScreen".into()),
                steam_overlay: Some(false),
                cloud_sync: Some(true),
                ..Default::default()
            }),
            props_open: false,
            game_props_tab: 0,
            props_launch_opts: TextEdit::default(),
            uninstall_confirm: false,
            armed_confirm: None,
            install_progress: None,
            install_queue: None,
            app_update_state: "idle".into(),
            key_modal_open: false,
            key_input: TextEdit::default(),
            key_preview: None,
            key_submitting: false,
            store_home: Some(demo_store_home()),
            store_tab: 0,
            store_loading: false,
            store_hero_index: 0,
            store_query: TextEdit::default(),
            store_search_results: None,
            store_game: None,
            store_offers: None,
            tour_step: None,
            tour_seen: true,
            gamepad_connected: false,
            input_mode: InputMode::Pointer,
            settings: LauncherSettings {
                start_with_windows: true,
                show_library_tools: true,
                pause_downloads_while_playing: true,
                keep_awake_downloads: true,
                store_clients_quiet: true,
                achievement_banners: true,
                fps_counter: true,
                fps_counter_position: Some("top-right".into()),
                hdr_for_games: true,
                auto_update_upscalers: true,
                ..LauncherSettings::default()
            },
            pc: SettingsAndPcState::demo(),
            media: MediaState::demo(),
            discord: DiscordState::demo(),
            art_base: ArtBase::default(),
            notice: None,
            notices: VecDeque::new(),
            next_notice_id: 1,
            notice_last_synced: None,
            clock_secs: 0.0,
            refreshing: false,
            palette_open: false,
            palette_query: TextEdit::default(),
            palette_index: 0,
            visible_limit: 96,
            image_cache: None,
            remote_cache_dir: remote_art::default_cache_dir(),
            covers: RefCell::new(HashMap::new()),
            remote_covers: RefCell::new(HashMap::new()),
            remote_inflight: RefCell::new(HashSet::new()),
            video_router: VideoFrameRouter::new(),
            #[cfg(windows)]
            rpc: None,
            #[cfg(windows)]
            handle: None,
            #[cfg(windows)]
            inflight_requests: HashMap::new(),
            #[cfg(windows)]
            inflight_discord_channel: None,
        }
    }

    pub fn for_host_pipe() -> Self {
        Self {
            room: Room::Library,
            prev_room: Room::Library,
            room_dir: 1.0,
            mini_bar_slot: MiniBarSlot::Inline,
            library_all_games: false,
            library_search_open: false,
            library_search: TextEdit::default(),
            library_more_open: false,
            capture_menu_open: false,
            deck_scroll: Rc::new(Cell::new(ScrollPos::default())),
            deck_scroll_x: 0.0,
            store_shelf_scrolls: std::array::from_fn(|_| Rc::new(Cell::new(ScrollPos::default()))),
            store_shelf_x: [0.0; 4],
            filter: LibraryFilter::All,
            sort: LibrarySort::LastLaunched,
            status: HostStatus::Connecting,
            games: Vec::new(),
            recent_ids: Vec::new(),
            selected_game_id: None,
            open_game_id: None,
            cover_flight_id: None,
            hovered_cover_key: None,
            game_extras: None,
            game_props: None,
            props_open: false,
            game_props_tab: 0,
            props_launch_opts: TextEdit::default(),
            uninstall_confirm: false,
            armed_confirm: None,
            install_progress: None,
            install_queue: None,
            app_update_state: "idle".into(),
            key_modal_open: false,
            key_input: TextEdit::default(),
            key_preview: None,
            key_submitting: false,
            store_home: None,
            store_tab: 0,
            store_loading: false,
            store_hero_index: 0,
            store_query: TextEdit::default(),
            store_search_results: None,
            store_game: None,
            store_offers: None,
            tour_step: None,
            tour_seen: true,
            gamepad_connected: false,
            input_mode: InputMode::Pointer,
            settings: LauncherSettings::default(),
            pc: SettingsAndPcState::empty_for_host(),
            media: MediaState::empty_for_host(),
            discord: DiscordState::empty(),
            art_base: ArtBase::default(),
            notice: Some("Connecting to Exo host…".into()),
            notices: VecDeque::new(),
            next_notice_id: 1,
            notice_last_synced: Some("Connecting to Exo host…".into()),
            clock_secs: 0.0,
            refreshing: true,
            palette_open: false,
            palette_query: TextEdit::default(),
            palette_index: 0,
            visible_limit: 96,
            image_cache: None,
            remote_cache_dir: remote_art::default_cache_dir(),
            covers: RefCell::new(HashMap::new()),
            remote_covers: RefCell::new(HashMap::new()),
            remote_inflight: RefCell::new(HashSet::new()),
            video_router: VideoFrameRouter::new(),
            #[cfg(windows)]
            rpc: None,
            #[cfg(windows)]
            handle: None,
            #[cfg(windows)]
            inflight_requests: HashMap::new(),
            #[cfg(windows)]
            inflight_discord_channel: None,
        }
    }

    pub fn install_waker(&mut self, waker: Waker) {
        self.image_cache = Some(ImageCache::new(waker));
    }

    pub fn preload_visible_covers_sync(&self, scale: f32) {
        let mut images: Vec<Image> = Vec::new();
        if self.art_base.covers_dir.is_some() && !self.games.is_empty() {
            if let Some(sel) = self.open_game().or_else(|| self.selected_game()) {
                if let Some(img) = self.hero_image(sel, scale) {
                    images.push(img);
                }
                if let Some(img) = self.logo_image(sel, scale) {
                    images.push(img);
                }
            }
            images.extend(
                self.filtered_games()
                    .into_iter()
                    .take(48)
                    .filter_map(|g| self.cover_image(g, scale)),
            );
            for sg in &self.pc.shader_snap_games {
                if let Some(g) = self
                    .games
                    .iter()
                    .find(|g| g.id == sg.id || g.title.eq_ignore_ascii_case(&sg.name))
                    && let Some(img) = self.cover_image(g, scale)
                {
                    images.push(img);
                }
            }
        }
        if let Some(ref home) = self.store_home {
            for (i, h) in home.hero.iter().take(6).enumerate() {
                if let Some(a) = h.art.as_ref() {
                    if i == 0
                        && let Some(u) = a.best_hero()
                        && let Some(img) = self.remote_image_sized(u, 1600.0, scale)
                    {
                        images.push(img);
                    }
                    if let Some(u) = a
                        .capsule
                        .as_deref()
                        .or(a.header.as_deref())
                        .or_else(|| a.best_hero())
                        && let Some(img) = self.remote_image_sized(u, 220.0, scale)
                    {
                        images.push(img);
                    }
                }
            }
            for shelf in home.shelves.iter().take(2) {
                for tile in shelf.items.iter().take(12) {
                    if let Some(u) = tile.art.as_ref().and_then(|a| a.best_poster())
                        && let Some(img) = self.remote_image_sized(u, 320.0, scale)
                    {
                        images.push(img);
                    }
                }
            }
        }
        for url in self.pc.device_images.values() {
            if let Some(img) = self.device_image(url, scale) {
                images.push(img);
            }
        }
        for app_id in self.pc.app_icons.keys().take(100) {
            if let Some(img) = self.app_icon_image(app_id) {
                images.push(img);
            }
        }
        for img in images {
            let _ = img.wait();
        }
    }

    #[cfg(windows)]
    pub fn call_host_async<R, F>(&self, method: &str, params: serde_json::Value, on_done: F)
    where
        R: exo_rpc::DeserializeOwned + Send + 'static,
        F: FnOnce(&mut AppState, Result<R, String>) + Send + 'static,
    {
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, R>(method, &params, move |res| {
                let mapped = res.map_err(|e| e.to_string());
                handle.update(move |s, _| on_done(s, mapped));
            });
        }
    }

    #[cfg(windows)]
    pub fn cancel_inflight(&mut self, slot: &'static str) {
        if let Some(prev) = self.inflight_requests.remove(slot)
            && let Some(ref rpc) = self.rpc
        {
            rpc.cancel(&prev);
        }
    }

    #[cfg(windows)]
    pub fn call_host_replaceable<R, F>(
        &mut self,
        slot: &'static str,
        method: &str,
        params: serde_json::Value,
        on_done: F,
    ) where
        R: exo_rpc::DeserializeOwned + Send + 'static,
        F: FnOnce(&mut AppState, Result<R, String>) + Send + 'static,
    {
        self.cancel_inflight(slot);
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let slot_copy = slot;
            let req_id_cell = Arc::new(std::sync::Mutex::new(None::<RequestId>));
            let cell_for_cb = Arc::clone(&req_id_cell);
            let req_id = rpc.call::<_, R>(method, &params, move |res| {
                let expected = cell_for_cb.lock().ok().and_then(|g| g.clone());
                let mapped = res.map_err(|e| e.to_string());
                handle.update(move |s, _| {
                    if let Some(ref exp) = expected
                        && s.inflight_requests.get(slot_copy) != Some(exp)
                    {
                        return;
                    }
                    s.inflight_requests.remove(slot_copy);
                    on_done(s, mapped);
                });
            });
            if let Ok(mut g) = req_id_cell.lock() {
                *g = Some(req_id.clone());
            }
            self.inflight_requests.insert(slot, req_id);
        }
    }

    pub fn switch_room(&mut self, room: Room) {
        if room != self.room {
            self.room_dir = if room.tab_index() >= self.room.tab_index() {
                1.0
            } else {
                -1.0
            };
        }
        if self.room == Room::Devices && room != Room::Devices && self.pc.mouse_panel_open {
            self.close_mouse_panel();
        }
        if self.room != Room::Settings && room == Room::Settings {
            self.prev_room = self.room;
        }
        self.room = room;
        self.open_game_id = None;
        self.props_open = false;
        self.uninstall_confirm = false;
        self.capture_menu_open = false;
        self.library_more_open = false;
        if self.media.captures_open {
            self.close_captures();
        }
        match room {
            Room::Store if self.store_home.is_none() && !self.store_loading => {
                self.load_store_home(false);
            }
            Room::Discord => self.load_discord_room(),
            Room::Music => self.load_music_room(),
            Room::Settings => self.load_settings_room(),
            Room::Devices => self.load_pc_tab(crate::pc_state::PcTab::Devices),
            Room::ExoAi => self.load_pc_tab(crate::pc_state::PcTab::ExoAi),
            Room::Apps => self.load_pc_tab(crate::pc_state::PcTab::Apps),
            _ => {}
        }
    }

    pub fn toggle_settings(&mut self) {
        if self.room == Room::Settings {
            let back = if self.prev_room == Room::Settings {
                Room::Library
            } else {
                self.prev_room
            };
            self.switch_room(back);
        } else {
            self.switch_room(Room::Settings);
        }
    }

    pub fn apply_library(&mut self, payload: LibraryPayload) {
        self.games = payload
            .games
            .into_iter()
            .filter(|g| !g.is_add_portable)
            .collect();
        self.recent_ids = payload.recent;
        if let Some(p) = payload.progress {
            self.install_progress = p.is_active.then_some(p);
        }
        self.refreshing = false;
        self.status = HostStatus::Connected;
        if self
            .games
            .iter()
            .all(|g| Some(&g.id) != self.selected_game_id.as_ref())
        {
            self.selected_game_id = self
                .filtered_games()
                .into_iter()
                .next()
                .or_else(|| self.games.first())
                .map(|g| g.id.clone());
        }
        if self.notice.as_deref() == Some("Connecting to Exo host…") {
            self.notice = None;
        }
        if self.room == Room::Store && self.store_home.is_none() && !self.store_loading {
            self.load_store_home(false);
        }
    }

    pub fn apply_art_base(&mut self, base: ArtBase) {
        self.art_base = base;
        self.covers.borrow_mut().clear();
    }

    pub fn apply_settings(&mut self, settings: LauncherSettings) {
        let seen = settings
            .ui_prefs
            .get("tourSeen")
            .and_then(|v| v.as_i64())
            .unwrap_or(0)
            >= 1;
        self.tour_seen = seen;
        self.settings = settings;
        if !seen && self.tour_step.is_none() && self.status == HostStatus::Connected {
            self.start_tour();
        }
    }

    pub fn text_scale(&self) -> f32 {
        match self.settings.text_size.as_deref() {
            Some("small") => 0.9,
            Some("large") => 1.14,
            _ => 1.0,
        }
    }

    pub fn filtered_games(&self) -> Vec<&Game> {
        let query = self.library_search.text.trim().to_lowercase();
        let show_tools = self.settings.show_library_tools;
        let transfer_id = self
            .install_progress
            .as_ref()
            .filter(|p| p.is_active)
            .and_then(|p| p.game_id.as_deref());
        let recent: &[String] = if !self.recent_ids.is_empty() {
            &self.recent_ids
        } else {
            &self.settings.recent
        };
        let mut out: Vec<&Game> = self
            .games
            .iter()
            .filter(|g| {
                is_shelf_game(g, transfer_id, show_tools)
                    && !self.settings.hidden_games.iter().any(|h| h == &g.id)
            })
            .filter(|g| match self.filter {
                LibraryFilter::All => true,
                LibraryFilter::Installed => g.installed,
                LibraryFilter::Running => g.is_running,
                LibraryFilter::Recent => {
                    g.last_played_utc
                        .as_deref()
                        .and_then(parse_iso_utc_ms)
                        .is_some()
                        || recent.iter().any(|r| r.eq_ignore_ascii_case(&g.id))
                        || g.playtime_minutes > 0
                }
            })
            .collect();
        if !query.is_empty() {
            out.retain(|g| g.title.to_lowercase().contains(&query));
        }
        let recent_rank = |id: &str| -> usize {
            recent
                .iter()
                .position(|r| r.eq_ignore_ascii_case(id))
                .unwrap_or(9999)
        };
        out.sort_by(|a, b| match self.sort {
            LibrarySort::LastLaunched => {
                let pa = a
                    .last_played_utc
                    .as_deref()
                    .and_then(parse_iso_utc_ms)
                    .unwrap_or(0);
                let pb = b
                    .last_played_utc
                    .as_deref()
                    .and_then(parse_iso_utc_ms)
                    .unwrap_or(0);
                pb.cmp(&pa)
                    .then_with(|| recent_rank(&a.id).cmp(&recent_rank(&b.id)))
                    .then_with(|| cmp_title_base(&a.title, &b.title))
            }
            LibrarySort::Playtime => b
                .playtime_minutes
                .cmp(&a.playtime_minutes)
                .then_with(|| cmp_title_base(&a.title, &b.title)),
            LibrarySort::Title => cmp_title_base(&a.title, &b.title),
            LibrarySort::Size => b
                .size_bytes
                .unwrap_or(0)
                .cmp(&a.size_bytes.unwrap_or(0))
                .then_with(|| cmp_title_base(&a.title, &b.title)),
        });
        out
    }

    pub fn selected_game(&self) -> Option<&Game> {
        if let Some(id) = self.selected_game_id.as_deref()
            && let Some(found) = self.games.iter().find(|g| g.id == id)
        {
            return Some(found);
        }
        self.filtered_games().into_iter().next()
    }

    pub fn open_game(&self) -> Option<&Game> {
        let id = self.open_game_id.as_deref()?;
        self.games.iter().find(|g| g.id == id)
    }

    pub fn cover_image(&self, game: &Game, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let mut waiting_remote = false;
        for cand in game.portrait_art_candidates() {
            if cand.url.starts_with(COVER_CACHE_ORIGIN) {
                let key = format!("cov-url:{}", cand.url);
                let cached = self.covers.borrow().get(&key).cloned();
                let img = match cached {
                    Some(existing) => existing,
                    None => {
                        let loaded = self
                            .art_base
                            .resolve_local_cover(&cand.url)
                            .map(|path| cache.file(path, 320.0, scale));
                        self.covers.borrow_mut().insert(key, loaded.clone());
                        loaded
                    }
                };
                if img.is_some() {
                    return img;
                }
            } else if cand.url.starts_with("https://") && !waiting_remote {
                let remote_key = format!("{}@320", cand.url);
                let failed = matches!(self.remote_covers.borrow().get(&remote_key), Some(None));
                if failed {
                    continue;
                }
                if let Some(img) = self.remote_image_sized(&cand.url, 320.0, scale) {
                    return Some(img);
                }
                waiting_remote = true;
            }
        }
        let san_key = format!("cov-san:{}#{}", game.id, game.art_revision.unwrap_or(0));
        if let Some(existing) = self.covers.borrow().get(&san_key) {
            return existing.clone();
        }
        let loaded = self
            .art_base
            .resolve_local_sanitized_portrait(game)
            .map(|path| cache.file(path, 320.0, scale));
        self.covers.borrow_mut().insert(san_key, loaded.clone());
        loaded
    }

    pub fn hero_image(&self, game: &Game, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let key = format!("hero:{}#{}", game.id, game.art_revision.unwrap_or(0));
        if let Some(existing) = self.covers.borrow().get(&key) {
            if existing.is_some() {
                return existing.clone();
            }
        } else {
            let loaded = self
                .art_base
                .resolve_local_hero(game)
                .map(|path| cache.file(path, 1440.0, scale));
            self.covers.borrow_mut().insert(key, loaded.clone());
            if loaded.is_some() {
                return loaded;
            }
        }
        if let Some(url) = game.hero_url.as_deref()
            && url.starts_with("https://")
            && !url.contains(".exo-launcher.local")
            && let Some(img) = self.remote_image_sized(url, 1440.0, scale)
        {
            return Some(img);
        }
        if let Some(url) = game.steam_hero_url()
            && let Some(img) = self.remote_image_sized(&url, 1440.0, scale)
        {
            return Some(img);
        }
        self.cover_image(game, scale)
    }

    pub fn logo_image(&self, game: &Game, scale: f32) -> Option<Image> {
        let url = game.steam_logo_url()?;
        self.remote_image_sized(&url, 460.0, scale)
    }

    pub fn device_image(&self, url: &str, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let key = format!("dev:{url}");
        if let Some(existing) = self.covers.borrow().get(&key) {
            return existing.clone();
        }
        let loaded = self
            .art_base
            .resolve_device_image(url)
            .map(|path| cache.file(path, 640.0, scale));
        self.covers.borrow_mut().insert(key, loaded.clone());
        loaded
    }

    pub fn app_icon_image(&self, app_id: &str) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let key = format!("appicon:{app_id}");
        if let Some(existing) = self.covers.borrow().get(&key) {
            return existing.clone();
        }
        let data_url = self.pc.app_icons.get(app_id)?;
        let b64 = data_url.split_once("base64,").map(|(_, b)| b)?;
        let bytes = exo_rpc::base64::decode(b64)?;
        let loaded = Some(cache.load(bytes));
        self.covers.borrow_mut().insert(key, loaded.clone());
        loaded
    }

    pub fn capture_thumb_image(&self, url: &str, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let key = format!("cap:{url}");
        if let Some(existing) = self.covers.borrow().get(&key) {
            return existing.clone();
        }
        let loaded = self
            .art_base
            .resolve_capture_thumb(url)
            .map(|path| cache.file(path, 320.0, scale));
        self.covers.borrow_mut().insert(key, loaded.clone());
        loaded
    }

    pub fn remote_image(&self, url: &str, scale: f32) -> Option<Image> {
        self.remote_image_sized(url, 360.0, scale)
    }

    pub fn remote_image_sized(&self, url: &str, max_dim: f32, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let cache_key = format!("{url}@{max_dim:.0}");
        if let Some(existing) = self.remote_covers.borrow().get(&cache_key) {
            return existing.clone();
        }
        let dir = self.remote_cache_dir.as_ref()?;
        remote_art::parse_allowed_https(url)?;
        let path = remote_art::cache_path_for(dir, url);
        if path.is_file() {
            let img = cache.file(path, max_dim, scale);
            self.remote_covers
                .borrow_mut()
                .insert(cache_key, Some(img.clone()));
            return Some(img);
        }
        #[cfg(windows)]
        if let Some(handle) = self.handle.clone() {
            let mut inflight = self.remote_inflight.borrow_mut();
            if inflight.len() < 16 && inflight.insert(url.to_string()) {
                let url_owned = url.to_string();
                let key_owned = cache_key;
                let dir_owned = dir.clone();
                std::thread::Builder::new()
                    .name("exo-art-fetch".into())
                    .spawn(move || {
                        let fetched = remote_art::fetch_cached_image(&url_owned, &dir_owned);
                        handle.update(move |s, _| {
                            s.remote_inflight.borrow_mut().remove(&url_owned);
                            if let (Some(p), Some(ic)) = (fetched, s.image_cache.as_ref()) {
                                let img = ic.file(p, max_dim, scale);
                                s.remote_covers.borrow_mut().insert(key_owned, Some(img));
                            } else {
                                s.remote_covers.borrow_mut().insert(key_owned, None);
                            }
                        });
                    })
                    .ok();
            }
        }
        None
    }

    pub fn open_game_page_from(&mut self, id: &str, flight_id: Option<String>, tx: &mut Tx) {
        self.cover_flight_id = flight_id;
        tx.unclipped_flights();
        self.open_game_page(id);
    }

    pub fn open_game_page(&mut self, id: &str) {
        self.room = Room::Library;
        self.selected_game_id = Some(id.to_string());
        self.open_game_id = Some(id.to_string());
        self.props_open = false;
        self.game_props_tab = 0;
        self.uninstall_confirm = false;
        self.armed_confirm = None;
        if self.status != HostStatus::Standalone {
            self.game_extras = None;
            self.game_props = None;
            self.props_launch_opts.text.clear();
            self.props_launch_opts.sync_invariants();
        } else if let Some(opts) = self
            .game_props
            .as_ref()
            .and_then(|p| p.launch_options.as_deref())
        {
            self.props_launch_opts.text = opts.to_string();
            self.props_launch_opts.sync_invariants();
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid_props = id.to_string();
            let h_props = handle.clone();
            rpc.call::<_, GameProperties>(
                METHOD_GAME_PROPERTIES,
                &json!({ "id": gid_props }),
                move |res| {
                    if let Ok(props) = res {
                        h_props.update(move |s, _| {
                            if s.open_game_id.as_deref() == Some(gid_props.as_str()) {
                                s.props_launch_opts.text =
                                    props.launch_options.clone().unwrap_or_default();
                                s.props_launch_opts.sync_invariants();
                                s.game_props = Some(props);
                            }
                        });
                    }
                },
            );
            let gid = id.to_string();
            let h1 = handle.clone();
            rpc.call::<_, GameGetReply>(METHOD_GAME_GET, &json!({ "id": gid }), move |res| {
                if let Ok(reply) = res
                    && let Some(fresh) = reply.game
                {
                    h1.update(move |s, _| {
                        if let Some(slot) = s.games.iter_mut().find(|g| g.id == fresh.id) {
                            *slot = fresh;
                        }
                    });
                }
            });

            let gid2 = id.to_string();
            let h2 = handle.clone();
            rpc.call::<_, GameExtras>(METHOD_GAME_EXTRAS, &json!({ "id": gid2 }), move |res| {
                if let Ok(ex) = res {
                    h2.update(move |s, _| {
                        if s.open_game_id.as_deref() == Some(gid2.as_str()) {
                            s.game_extras = Some(ex);
                        }
                    });
                }
            });
        }
    }

    pub fn close_game_page_tx(&mut self, tx: &mut Tx) {
        tx.unclipped_flights();
        self.close_game_page();
    }

    pub fn close_game_page(&mut self) {
        self.open_game_id = None;
        self.props_open = false;
        self.uninstall_confirm = false;
        self.armed_confirm = None;
    }

    pub fn open_store_game_from(&mut self, app_id: &str, flight_id: Option<String>, tx: &mut Tx) {
        self.cover_flight_id = flight_id;
        tx.unclipped_flights();
        self.open_store_game(app_id);
    }

    pub fn close_store_game_tx(&mut self, tx: &mut Tx) {
        tx.unclipped_flights();
        self.store_game = None;
        self.store_offers = None;
    }

    pub fn is_confirm_armed(&self, key: &str) -> bool {
        self.armed_confirm.as_ref().is_some_and(|(k, _)| k == key)
    }

    pub fn disarm_confirm(&mut self) -> bool {
        let was_armed = self.armed_confirm.take().is_some() || self.uninstall_confirm;
        self.uninstall_confirm = false;
        was_armed
    }

    pub fn arm_or_confirm_game_uninstall(&mut self, id: &str, tx: &mut Tx) {
        const KEY: &str = "gp-uninstall";
        const ARMED_SECS: f64 = 4.0;
        if self.is_confirm_armed(KEY) {
            self.disarm_confirm();
            self.uninstall_game(id);
        } else {
            let deadline = self.clock_secs + ARMED_SECS;
            self.armed_confirm = Some((KEY.to_string(), deadline));
            self.uninstall_confirm = true;
            tx.wake_after(Duration::from_secs_f64(ARMED_SECS));
        }
    }

    pub fn deck_can_page(&self, total: usize) -> (bool, bool) {
        let pos = self.deck_scroll.get();
        let current = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.deck_scroll_x
        };
        let can_prev = current > 2.0;
        let can_next = if pos.max[0] > 0.0 {
            current + 2.0 < pos.max[0]
        } else {
            let approx_track = (total as f32 * 238.0 - 14.0).max(0.0);
            let approx_view = (1920.0_f32 - 64.0).max(320.0);
            approx_track > approx_view + 2.0 && current + 2.0 < approx_track - approx_view
        };
        (can_prev, can_next)
    }

    pub fn page_deck(&mut self, dir: f32, total: usize, tx: &mut Tx) {
        let pos = self.deck_scroll.get();
        let viewport = if pos.viewport[0] > 1.0 {
            pos.viewport[0]
        } else {
            (1920.0_f32 - 64.0).max(320.0)
        };
        let max_x = if pos.max[0] > 0.0 {
            pos.max[0]
        } else {
            ((total as f32 * 238.0 - 14.0) - viewport).max(0.0)
        };
        let current = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.deck_scroll_x
        };
        let step = (viewport * 0.8).max(320.0);
        let target = (current + dir * step).clamp(0.0, max_x);
        self.deck_scroll_x = target;
        if self.settings.reduce_motion {
            tx.scroll_to_instant("home-deck-track", target, 0.0);
        } else {
            tx.scroll_to_with("home-deck-track", target, 0.0, SPRING_MOVE);
        }
    }

    pub fn store_shelf_can_page(&self, idx: usize, count: usize) -> (bool, bool) {
        let slot = idx.min(3);
        let pos = self.store_shelf_scrolls[slot].get();
        let current = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.store_shelf_x[slot]
        };
        let can_prev = current > 2.0;
        let can_next = if pos.max[0] > 0.0 {
            current + 2.0 < pos.max[0]
        } else {
            let approx_track = (count as f32 * 194.0 - 14.0).max(0.0);
            let approx_view = (1920.0_f32 - 104.0).max(360.0);
            approx_track > approx_view + 2.0 && current + 2.0 < approx_track - approx_view
        };
        (can_prev, can_next)
    }

    pub fn page_store_shelf(&mut self, idx: usize, dir: f32, count: usize, tx: &mut Tx) {
        let slot = idx.min(3);
        let pos = self.store_shelf_scrolls[slot].get();
        let viewport = if pos.viewport[0] > 1.0 {
            pos.viewport[0]
        } else {
            (1920.0_f32 - 104.0).max(360.0)
        };
        let max_x = if pos.max[0] > 0.0 {
            pos.max[0]
        } else {
            ((count as f32 * 194.0 - 14.0) - viewport).max(0.0)
        };
        let current = if pos.max[0] > 0.0 {
            pos.offset[0]
        } else {
            self.store_shelf_x[slot]
        };
        let step = (viewport * 0.8).max(360.0);
        let target = (current + dir * step).clamp(0.0, max_x);
        self.store_shelf_x[slot] = target;
        let key = format!("store-shelf-track-{idx}");
        if self.settings.reduce_motion {
            tx.scroll_to_instant(key, target, 0.0);
        } else {
            tx.scroll_to_with(key, target, 0.0, SPRING_MOVE);
        }
    }

    pub fn set_game_property(&mut self, id: &str, key: &str, value: &str) {
        if self.status == HostStatus::Standalone {
            if let Some(ref mut p) = self.game_props {
                if key == "launchOptions" {
                    p.launch_options = Some(value.to_string());
                } else if key == "steamOverlay" {
                    p.steam_overlay = Some(value == "true");
                }
            }
            self.notice = Some("Saved game property".into());
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            let k = key.to_string();
            let v = value.to_string();
            rpc.call::<_, GamePropertySetReply>(
                METHOD_GAME_PROPERTIES_SET,
                &json!({ "id": gid, "key": k, "value": v }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => {
                            if let Some(p) = r.properties {
                                s.game_props = Some(p);
                            }
                            s.notice = Some(r.message.unwrap_or_else(|| "Saved properties".into()));
                        }
                        Ok(r) => {
                            s.notice = Some(
                                r.message
                                    .unwrap_or_else(|| "Could not save property".into()),
                            )
                        }
                        Err(e) => s.notice = Some(format!("Save failed: {e}")),
                    });
                },
            );
        }
    }

    pub fn open_key_modal(&mut self, tx: &mut Tx) {
        self.key_modal_open = true;
        self.key_submitting = false;
        self.key_input.text.clear();
        self.key_input.sync_invariants();
        self.key_preview = None;
        tx.focus("activate-key-input", false);

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, KeyPreview>(METHOD_KEYS_PREVIEW, &json!({}), move |res| {
                if let Ok(prev) = res {
                    handle.update(move |s, _| {
                        if prev.looks_like_key
                            && let Some(ref k) = prev.key
                            && s.key_input.text.is_empty()
                        {
                            s.key_input.text = k.clone();
                            s.key_input.sync_invariants();
                        }
                        s.key_preview = Some(prev);
                    });
                }
            });
        }
    }

    pub fn close_key_modal(&mut self) {
        self.key_modal_open = false;
        self.key_submitting = false;
    }

    pub fn submit_key_activation(&mut self) {
        let key = self.key_input.text.trim().to_string();
        if key.is_empty() || self.key_submitting {
            return;
        }
        if self.status == HostStatus::Standalone {
            self.close_key_modal();
            self.notice = Some("Standalone preview: key redemption requires Exo host".into());
            return;
        }
        self.key_submitting = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_KEYS_ACTIVATE, &json!({ "key": key }), move |res| {
                handle.update(move |s, _| {
                    s.key_submitting = false;
                    match res {
                        Ok(r) if r.ok => {
                            s.close_key_modal();
                            s.notice = Some(
                                r.message
                                    .unwrap_or_else(|| "Opened store key redemption".into()),
                            );
                        }
                        Ok(r) => {
                            s.notice = Some(
                                r.message
                                    .unwrap_or_else(|| "Key could not be redeemed".into()),
                            )
                        }
                        Err(e) => s.notice = Some(format!("Key activation failed: {e}")),
                    }
                });
            });
        }
    }

    pub fn load_store_home(&mut self, refresh: bool) {
        if self.status == HostStatus::Standalone {
            self.store_home = Some(demo_store_home());
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.store_loading = true;
            let params = if refresh {
                json!({ "refresh": true })
            } else {
                json!({})
            };
            rpc.call::<_, StoreHome>(METHOD_STORE_HOME, &params, move |res| {
                handle.update(move |s, _| {
                    s.store_loading = false;
                    match res {
                        Ok(home) if home.ok => s.store_home = Some(home),
                        Ok(home) => {
                            s.notice = home.message.or_else(|| Some("Store unavailable".into()))
                        }
                        Err(e) => s.notice = Some(format!("Store error: {e}")),
                    }
                });
            });
        }
    }

    pub fn search_store(&mut self) {
        let q = self.store_query.text.trim().to_string();
        if q.is_empty() {
            self.store_search_results = None;
            return;
        }
        if self.status == HostStatus::Standalone {
            let lower = q.to_lowercase();
            let hits: Vec<StoreTile> = demo_store_home()
                .shelves
                .into_iter()
                .flat_map(|sh| sh.items)
                .filter(|t| t.name.to_lowercase().contains(&lower))
                .collect();
            self.store_search_results = Some(hits);
            return;
        }
        self.store_loading = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreSearchReply>(
                METHOD_STORE_SEARCH,
                &json!({ "query": q, "typed": true }),
                move |res| {
                    handle.update(move |s, _| {
                        s.store_loading = false;
                        if let Ok(reply) = res {
                            s.store_search_results = Some(reply.items);
                        }
                    });
                },
            );
        }
    }

    pub fn open_store_game(&mut self, app_id: &str) {
        self.store_offers = None;
        if self.status == HostStatus::Standalone {
            let tile = demo_store_home()
                .hero
                .into_iter()
                .chain(demo_store_home().shelves.into_iter().flat_map(|s| s.items))
                .find(|t| t.app_id == app_id);
            let name = tile
                .as_ref()
                .map(|t| t.name.clone())
                .unwrap_or_else(|| "Store Game".into());
            self.store_game = Some(StoreGame {
                ok: true,
                app_id: app_id.into(),
                name,
                description: Some("Official storefront details, Steam review scores, and cross-store deal comparison rendered natively in Rune.".into()),
                developers: vec!["Studio".into()],
                genres: vec!["Action".into(), "RPG".into()],
                metacritic: Some(89),
                review_word: Some("Very Positive".into()),
                review_percent: Some(92),
                price_text: tile.map(|t| t.display_price()),
                discount: 40,
                ..Default::default()
            });
            self.store_offers = Some(StoreOtherOffers {
                ok: true,
                offers: vec![
                    StoreOffer {
                        store: "steam".into(),
                        name: "Steam".into(),
                        final_cents: Some(3599),
                        discount: 40,
                        currency: Some("USD".into()),
                        price_text: Some("$35.99".into()),
                        url: format!("https://store.steampowered.com/app/{app_id}/"),
                    },
                    StoreOffer {
                        store: "gog".into(),
                        name: "GOG".into(),
                        final_cents: Some(3299),
                        discount: 45,
                        currency: Some("USD".into()),
                        price_text: Some("$32.99".into()),
                        url: "https://www.gog.com/".into(),
                    },
                ],
                lowest: Some(exo_rpc::StoreLowest {
                    final_cents: 2999,
                    at: "Oct 2026".into(),
                }),
            });
            return;
        }

        #[cfg(windows)]
        {
            self.call_host_replaceable(
                "store.game",
                METHOD_STORE_GAME,
                json!({ "appId": app_id }),
                |s, res: Result<StoreGame, String>| {
                    if let Ok(g) = res
                        && g.ok
                    {
                        s.store_game = Some(g);
                    }
                },
            );

            self.call_host_replaceable(
                "store.gameOffers",
                METHOD_STORE_GAME_OFFERS,
                json!({ "appId": app_id }),
                |s, res: Result<StoreOtherOffers, String>| {
                    if let Ok(offers) = res
                        && offers.ok
                    {
                        s.store_offers = Some(offers);
                    }
                },
            );
        }
    }

    pub fn close_store_game(&mut self) {
        #[cfg(windows)]
        {
            self.cancel_inflight("store.game");
            self.cancel_inflight("store.gameOffers");
        }
        self.store_game = None;
        self.store_offers = None;
    }

    pub fn open_store_url(&mut self, url: &str) {
        if self.status == HostStatus::Standalone {
            self.notice = Some(format!("Open: {url}"));
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let u = url.to_string();
            rpc.call::<_, ActionResult>(METHOD_STORE_OPEN, &json!({ "url": u }), move |res| {
                if let Ok(r) = res
                    && !r.ok
                    && let Some(msg) = r.message
                {
                    handle.update(move |s, _| s.notice = Some(msg));
                }
            });
        }
    }

    pub fn start_tour(&mut self) {
        self.palette_open = false;
        self.key_modal_open = false;
        self.open_game_id = None;
        self.store_game = None;
        self.jump_tour(0);
    }

    pub fn jump_tour(&mut self, idx: usize) {
        let clamped = idx.min(tour::STEPS.len() - 1);
        self.tour_step = Some(clamped);
        let target_room = tour::STEPS[clamped].room;
        self.room = target_room;
        if target_room == Room::Store && self.store_home.is_none() {
            self.load_store_home(false);
        }
    }

    pub fn finish_tour(&mut self) {
        self.tour_step = None;
        self.tour_seen = true;
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(
                METHOD_SETTINGS_SET,
                &json!({ "uiPrefs": { "tourSeen": 1 } }),
                |_| {},
            );
        }
    }

    pub fn apply_gamepad_action(&mut self, action: GamepadAction, tx: &mut Tx) {
        if !matches!(action, GamepadAction::Connected(_)) {
            self.input_mode = InputMode::Gamepad;
        }
        match action {
            GamepadAction::Connected(on) => {
                self.gamepad_connected = on;
                if !on {
                    self.input_mode = InputMode::Pointer;
                }
            }
            GamepadAction::PrevRoom => {
                let len = Room::NAV_TABS.len();
                let cur = Room::NAV_TABS
                    .iter()
                    .position(|&r| r == self.room)
                    .unwrap_or(0);
                let target = Room::NAV_TABS[(cur + len - 1) % len];
                self.switch_room(target);
                tx.focus(target.key(), true);
            }
            GamepadAction::NextRoom => {
                let len = Room::NAV_TABS.len();
                let cur = Room::NAV_TABS
                    .iter()
                    .position(|&r| r == self.room)
                    .unwrap_or(0);
                let target = Room::NAV_TABS[(cur + 1) % len];
                self.switch_room(target);
                tx.focus(target.key(), true);
            }
            GamepadAction::Search => {
                if self.palette_open {
                    self.close_palette();
                } else {
                    self.open_palette(tx);
                }
            }
            GamepadAction::Back => {
                if self.open_game_id.is_some() || self.store_game.is_some() {
                    tx.unclipped_flights();
                }
                self.dismiss_top_layer();
            }
            GamepadAction::QuickPlay => {
                if let Some(id) = self.selected_game().map(|g| g.id.clone()) {
                    self.launch_game(&id);
                }
            }
            GamepadAction::Confirm => {
                if let Some(step) = self.tour_step {
                    if step + 1 >= tour::STEPS.len() {
                        self.finish_tour();
                    } else {
                        self.jump_tour(step + 1);
                    }
                } else if self.palette_open {
                    if let Some(item) = self.palette_results().get(self.palette_index) {
                        self.execute_palette_target(item.target.clone(), tx);
                    }
                } else if self.room == Room::Library && self.open_game_id.is_none() {
                    if let Some(id) = self.selected_game().map(|g| g.id.clone()) {
                        let flight_id = if self.library_all_games {
                            format!("grid:{id}")
                        } else {
                            format!("deck:{id}")
                        };
                        self.open_game_page_from(&id, Some(flight_id), tx);
                        tx.focus("gp-play", true);
                    }
                } else if let Some(g) = self.open_game().cloned()
                    && g.installed
                {
                    self.launch_game(&g.id);
                }
            }
            GamepadAction::Left
            | GamepadAction::Right
            | GamepadAction::Up
            | GamepadAction::Down => {
                if self.palette_open {
                    let len = self.palette_results().len();
                    if len > 0 {
                        if matches!(action, GamepadAction::Down | GamepadAction::Right) {
                            self.palette_index = (self.palette_index + 1) % len;
                        } else {
                            self.palette_index = (self.palette_index + len - 1) % len;
                        }
                    }
                    return;
                }
                if self.room == Room::Library && self.open_game_id.is_none() {
                    let ids: Vec<String> = self
                        .filtered_games()
                        .into_iter()
                        .take(self.visible_limit)
                        .map(|g| g.id.clone())
                        .collect();
                    if ids.is_empty() {
                        return;
                    }
                    let cur = self
                        .selected_game_id
                        .as_deref()
                        .and_then(|id| ids.iter().position(|x| x == id))
                        .unwrap_or(0);
                    let delta: isize = match action {
                        GamepadAction::Left => -1,
                        GamepadAction::Right => 1,
                        GamepadAction::Up => -6,
                        GamepadAction::Down => 6,
                        _ => 0,
                    };
                    let next = (cur as isize + delta).clamp(0, (ids.len() - 1) as isize) as usize;
                    let next_id = ids[next].clone();
                    let focus_key = format!("tile-{next_id}");
                    self.selected_game_id = Some(next_id);
                    tx.focus(focus_key.clone(), true);
                    tx.scroll_into_view(focus_key);
                }
            }
        }
    }

    pub fn dismiss_top_layer(&mut self) -> bool {
        if self.disarm_confirm() {
            return true;
        }
        if self.capture_menu_open {
            self.capture_menu_open = false;
            return true;
        }
        if self.library_more_open {
            self.library_more_open = false;
            return true;
        }
        if self.palette_open {
            self.close_palette();
            return true;
        }
        if self.key_modal_open {
            self.close_key_modal();
            return true;
        }
        if self.tour_step.is_some() {
            self.finish_tour();
            return true;
        }
        if self.props_open {
            self.props_open = false;
            return true;
        }
        if self.room == Room::Discord && self.discord.status_menu_open {
            self.discord.status_menu_open = false;
            return true;
        }
        if self.room == Room::Discord && self.discord.add_friend_open {
            self.discord.add_friend_open = false;
            return true;
        }
        if self.room == Room::Discord && self.discord.chat.editing.is_some() {
            self.discord.chat.editing = None;
            return true;
        }
        if self.room == Room::Discord
            && self.discord.chat.drawer != crate::discord_chat_state::ChatDrawer::None
        {
            self.discord.chat.drawer = crate::discord_chat_state::ChatDrawer::None;
            return true;
        }
        if self.room == Room::Discord && self.discord.chat.reply_to.is_some() {
            self.discord.chat.reply_to = None;
            return true;
        }
        if self.room == Room::Discord && self.discord.voice_ui.fullscreen_stream {
            self.discord.voice_ui.fullscreen_stream = false;
            return true;
        }
        if self.room == Room::Discord
            && self.discord.voice_ui.panel != crate::discord_voice_state::VoicePanel::None
        {
            self.discord.voice_ui.panel = crate::discord_voice_state::VoicePanel::None;
            return true;
        }
        if self.media.open_capture_id.is_some() {
            self.close_capture_item();
            return true;
        }
        if self.media.captures_open {
            self.close_captures();
            return true;
        }
        if self.open_game_id.is_some() {
            self.close_game_page();
            return true;
        }
        if self.store_game.is_some() {
            self.close_store_game();
            return true;
        }
        if self.room == Room::Devices && self.pc.mouse_panel_open {
            self.close_mouse_panel();
            return true;
        }
        if !self.notices.is_empty() {
            return self.dismiss_newest_notice();
        }
        if self.notice.is_some() {
            self.notice = None;
            self.notice_last_synced = None;
            return true;
        }
        false
    }

    pub fn infer_notice_tone(text: &str) -> NoticeTone {
        let lower = text.to_lowercase();
        if lower.contains("failed")
            || lower.contains("error")
            || lower.contains("unable")
            || lower.contains("could not")
            || lower.contains("couldn't")
            || lower.contains("invalid")
            || lower.contains("denied")
            || lower.contains("disconnected")
            || lower.contains("not connected")
        {
            NoticeTone::Bad
        } else if lower.contains("saved")
            || lower.contains("activated")
            || lower.contains("copied")
            || lower.contains("installed")
            || lower.contains("connected")
            || lower.contains("updated")
            || lower.contains("captured")
            || lower.contains("added")
        {
            NoticeTone::Good
        } else {
            NoticeTone::Neutral
        }
    }

    pub fn push_notice(&mut self, text: impl Into<String>, tone: NoticeTone) -> u64 {
        let text = text.into();
        let id = self.next_notice_id;
        self.next_notice_id = self.next_notice_id.saturating_add(1);
        self.notices.push_back(NoticeItem {
            id,
            text: text.clone(),
            tone,
            remaining_secs: NOTICE_DURATION_SECS,
            last_tick: None,
            hovered: false,
            dragging: false,
            drag_x: 0.0,
            drag_prev_x: 0.0,
            drag_prev_t: self.clock_secs,
            drag_vx: 0.0,
        });
        while self.notices.len() > MAX_NOTICES {
            self.notices.pop_front();
        }
        self.notice = Some(text.clone());
        self.notice_last_synced = Some(text);
        id
    }

    pub fn dismiss_notice(&mut self, id: u64) -> bool {
        if let Some(pos) = self.notices.iter().position(|n| n.id == id) {
            self.notices.remove(pos);
            if self.notices.is_empty() {
                self.notice = None;
                self.notice_last_synced = None;
            }
            return true;
        }
        false
    }

    pub fn dismiss_newest_notice(&mut self) -> bool {
        if self.notices.pop_back().is_some() {
            if self.notices.is_empty() {
                self.notice = None;
                self.notice_last_synced = None;
            }
            return true;
        }
        false
    }

    fn schedule_notice_wake(&self, now: f64, tx: &mut Tx) {
        let min_rem = self
            .notices
            .iter()
            .filter(|n| !n.hovered && !n.dragging)
            .map(|n| n.remaining_secs)
            .reduce(f64::min);
        if let Some(rem) = min_rem {
            tx.wake_at(now + rem.max(0.001));
        }
    }

    pub fn set_notice_hovered(&mut self, id: u64, hovering: bool, tx: &mut Tx) {
        let now = tx.clock();
        self.clock_secs = now;
        if let Some(item) = self.notices.iter_mut().find(|n| n.id == id) {
            if hovering {
                if !item.hovered
                    && !item.dragging
                    && let Some(prev) = item.last_tick
                {
                    item.remaining_secs = (item.remaining_secs - (now - prev).max(0.0)).max(0.0);
                }
                item.hovered = true;
                item.last_tick = Some(now);
            } else {
                item.hovered = false;
                item.last_tick = Some(now);
            }
        }
        self.schedule_notice_wake(now, tx);
    }

    pub fn begin_notice_drag(&mut self, id: u64, now: f64) {
        self.clock_secs = now;
        if let Some(item) = self.notices.iter_mut().find(|n| n.id == id) {
            if !item.hovered
                && !item.dragging
                && let Some(prev) = item.last_tick
            {
                item.remaining_secs = (item.remaining_secs - (now - prev).max(0.0)).max(0.0);
            }
            item.dragging = true;
            item.last_tick = Some(now);
            item.drag_prev_x = item.drag_x;
            item.drag_prev_t = now;
            item.drag_vx = 0.0;
        }
    }

    pub fn update_notice_drag(&mut self, id: u64, x: f32, now: f64) {
        self.clock_secs = now;
        if let Some(item) = self.notices.iter_mut().find(|n| n.id == id) {
            let dt = (now - item.drag_prev_t) as f32;
            if dt > 0.0005 {
                let instant_vx = (x - item.drag_prev_x) / dt;
                item.drag_vx = item.drag_vx * 0.4 + instant_vx * 0.6;
                item.drag_prev_x = x;
                item.drag_prev_t = now;
            }
            item.drag_x = x;
            item.last_tick = Some(now);
        }
    }

    pub fn end_notice_drag(&mut self, id: u64, cancelled: bool, tx: &mut Tx) {
        let now = tx.clock();
        self.clock_secs = now;
        let should_dismiss = self
            .notices
            .iter()
            .find(|n| n.id == id)
            .is_some_and(|item| {
                !cancelled
                    && (item.drag_x > crate::notice::SWIPE_DISMISS_DX
                        || item.drag_vx > crate::notice::SWIPE_DISMISS_VX)
            });
        if should_dismiss {
            self.dismiss_notice(id);
        } else if let Some(item) = self.notices.iter_mut().find(|n| n.id == id) {
            item.dragging = false;
            item.drag_x = 0.0;
            item.drag_vx = 0.0;
            item.last_tick = Some(now);
        }
        self.schedule_notice_wake(now, tx);
    }

    pub fn sync_and_tick_notices(&mut self, now: f64, tx: &mut Tx) -> bool {
        self.clock_secs = now;
        self.sync_mouse_panel_visibility();
        let mut changed = false;

        if let Some((_, deadline)) = self.armed_confirm {
            if now >= deadline {
                self.disarm_confirm();
                tx.animated();
                changed = true;
            } else {
                tx.wake_at(deadline);
            }
        }

        if self.notice != self.notice_last_synced {
            self.notice_last_synced = self.notice.clone();
            if let Some(text) = self.notice.clone()
                && !text.trim().is_empty()
            {
                let tone = Self::infer_notice_tone(&text);
                self.push_notice(text, tone);
                tx.animated();
                changed = true;
            }
        }

        if self.notices.is_empty() {
            return changed;
        }

        for item in &mut self.notices {
            if item.hovered || item.dragging {
                item.last_tick = Some(now);
            } else {
                if let Some(prev) = item.last_tick {
                    item.remaining_secs -= (now - prev).max(0.0);
                }
                item.last_tick = Some(now);
            }
        }

        let before = self.notices.len();
        self.notices
            .retain(|item| item.hovered || item.dragging || item.remaining_secs > 0.0);
        if self.notices.len() != before {
            if self.notices.is_empty() {
                self.notice = None;
                self.notice_last_synced = None;
            }
            tx.animated();
            changed = true;
        }

        self.schedule_notice_wake(now, tx);
        changed
    }

    pub fn open_palette(&mut self, tx: &mut Tx) {
        self.palette_open = true;
        self.palette_index = 0;
        self.palette_query.text.clear();
        self.palette_query.sync_invariants();
        tx.focus("palette-input", false);
    }

    pub fn close_palette(&mut self) {
        self.palette_open = false;
        self.palette_index = 0;
    }

    pub fn palette_results(&self) -> Vec<PaletteItem> {
        let query = self.palette_query.text.trim().to_lowercase();
        let mut items = Vec::new();

        const PALETTE_ROOMS: [Room; 8] = [
            Room::Library,
            Room::Store,
            Room::Discord,
            Room::Music,
            Room::ExoAi,
            Room::Apps,
            Room::Devices,
            Room::Settings,
        ];

        for room in PALETTE_ROOMS {
            let label = room.label();
            if query.is_empty() || label.to_lowercase().contains(&query) {
                items.push(PaletteItem {
                    id: format!("pal-room-{}", room.key()),
                    group: "Go to",
                    title: label.into(),
                    target: PaletteTarget::Room(room),
                });
            }
        }

        let mut candidates: Vec<&Game> = self
            .games
            .iter()
            .filter(|g| query.is_empty() || g.title.to_lowercase().contains(&query))
            .collect();
        candidates.sort_by_key(|a| a.title.to_lowercase());

        for game in candidates.into_iter().take(50) {
            items.push(PaletteItem {
                id: format!("pal-game-{}", game.id),
                group: "Library",
                title: game.title.clone(),
                target: PaletteTarget::OpenGame(game.id.clone()),
            });
        }

        if !query.is_empty() {
            let actions = [
                (
                    "pal-action-tour",
                    "Take the guided tour",
                    PaletteTarget::StartTour,
                ),
                (
                    "pal-action-key",
                    "Activate a game key",
                    PaletteTarget::ActivateKey,
                ),
                (
                    "pal-action-refresh",
                    "Refresh library",
                    PaletteTarget::RefreshLibrary,
                ),
            ];
            for (id, title, target) in actions {
                if title.to_lowercase().contains(&query) {
                    items.push(PaletteItem {
                        id: id.into(),
                        group: "Actions",
                        title: title.into(),
                        target,
                    });
                }
            }
        }

        items
    }

    pub fn execute_palette_target(&mut self, target: PaletteTarget, tx: &mut Tx) {
        self.close_palette();
        match target {
            PaletteTarget::Room(room) => self.switch_room(room),
            PaletteTarget::OpenGame(id) => self.open_game_page(&id),
            PaletteTarget::RefreshLibrary => self.refresh_library(),
            PaletteTarget::ActivateKey => self.open_key_modal(tx),
            PaletteTarget::StartTour => self.start_tour(),
        }
    }

    pub fn launch_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_LAUNCH, id, "Launching", "Launched");
    }

    pub fn stop_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_STOP, id, "Stopping", "Stopped");
    }

    pub fn install_game(&mut self, id: &str) {
        self.game_action(
            METHOD_GAME_INSTALL,
            id,
            "Starting install for",
            "Install started for",
        );
    }

    pub fn update_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_UPDATE, id, "Updating", "Update started for");
    }

    pub fn repair_game(&mut self, id: &str) {
        self.game_action(
            METHOD_GAME_REPAIR,
            id,
            "Verifying",
            "Verification started for",
        );
    }

    pub fn open_game_folder(&mut self, id: &str) {
        self.game_action(
            METHOD_GAME_OPEN_FOLDER,
            id,
            "Opening folder for",
            "Opened folder for",
        );
    }

    pub fn uninstall_game(&mut self, id: &str) {
        self.disarm_confirm();
        self.game_action(
            METHOD_GAME_UNINSTALL,
            id,
            "Uninstalling",
            "Uninstall started for",
        );
    }

    pub fn cancel_install(&mut self, id: &str) {
        self.game_action(
            METHOD_GAME_CANCEL_INSTALL,
            id,
            "Canceling install for",
            "Canceled install for",
        );
    }

    pub fn pause_install(&mut self, id: &str) {
        self.game_action(
            METHOD_GAME_PAUSE_INSTALL,
            id,
            "Pausing download for",
            "Paused download for",
        );
    }

    fn game_action(
        &mut self,
        method: &'static str,
        id: &str,
        pending_verb: &str,
        done_verb: &'static str,
    ) {
        let title = self
            .games
            .iter()
            .find(|g| g.id == id)
            .map(|g| g.title.clone())
            .unwrap_or_else(|| id.to_string());
        self.notice = Some(format!("{pending_verb} {title}…"));

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let game_id = id.to_string();
            rpc.call::<_, ActionResult>(method, &json!({ "id": game_id }), move |res| {
                handle.update(move |s, _| match res {
                    Ok(r) if r.ok => {
                        s.notice = Some(r.message.unwrap_or_else(|| format!("{done_verb} {title}")))
                    }
                    Ok(r) => {
                        s.notice = Some(
                            r.message
                                .unwrap_or_else(|| format!("Action failed for {title}")),
                        )
                    }
                    Err(e) => s.notice = Some(format!("Action failed: {e}")),
                });
            });
        }
    }

    pub fn refresh_library(&mut self) {
        self.refreshing = true;
        self.notice = Some("Refreshing library…".into());

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, LibraryPayload>(
                METHOD_LIBRARY_REFRESH,
                &json!({ "force": true }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(lib) => {
                            let count = lib.games.len();
                            s.apply_library(lib);
                            s.notice = Some(format!("Library refreshed ({count} games)"));
                        }
                        Err(e) => {
                            s.refreshing = false;
                            s.notice = Some(format!("Refresh failed: {e}"));
                        }
                    });
                },
            );
        } else {
            self.refreshing = false;
            self.notice = Some("Standalone preview mode".into());
        }
    }

    pub fn apply_bootstrap(&mut self, boot: ShellBootstrap) {
        self.apply_art_base(boot.art_base);
        self.apply_settings(boot.settings);
        self.apply_library(boot.library);
        self.media.playback = boot.music_playback.playback;
        self.discord.status = boot.discord_status;
    }

    pub fn apply_music_volume(&mut self, ev: MusicVolumeEvent) {
        let clamped = ev.level.clamp(0.0, 1.0);
        self.media.music_volume = clamped;
        self.media.music_muted = clamped <= 0.0001;
    }

    pub fn apply_capture_state(&mut self, live: CaptureLiveState) {
        if let Some(ref mut st) = self.media.capture_status {
            st.state = Some(live);
        } else {
            self.media.capture_status = Some(CaptureStatusReply {
                ok: true,
                state: Some(live),
                ..Default::default()
            });
        }
    }

    pub fn apply_capture_saved(&mut self, ev: CaptureSavedEvent) {
        let name = ev.item.name.clone();
        self.media.capture_items.retain(|it| it.id != ev.item.id);
        self.media.capture_items.insert(0, ev.item);
        if !name.is_empty() {
            self.notice = Some(format!("Saved {name}."));
        }
    }

    pub fn apply_capture_reel(&mut self, ev: CaptureReelEvent) {
        match ev.state.as_str() {
            "done" => {
                if let Some(item) = ev.item {
                    let name = item.name.clone();
                    self.media.capture_items.retain(|it| it.id != item.id);
                    self.media.capture_items.insert(0, item);
                    if !name.is_empty() {
                        self.notice = Some(format!("{name} is ready."));
                    }
                }
            }
            "failed" => {
                self.notice = Some(
                    ev.message
                        .filter(|m| !m.trim().is_empty())
                        .unwrap_or_else(|| "Could not make reel.".into()),
                );
            }
            "working" => {
                self.notice = Some(format!("Making reel… {:.0}%", ev.percent.clamp(0.0, 100.0)));
            }
            "waiting" => {
                self.notice = Some("Reel queued until game ends.".into());
            }
            _ => {}
        }
    }

    pub fn apply_install_queue(&mut self, ev: InstallQueueEvent) {
        self.install_queue = Some(ev);
    }

    pub fn apply_game_run_state(&mut self, ev: GameRunStateEvent) {
        if let Some(game) = self.games.iter_mut().find(|g| g.id == ev.id) {
            game.is_running = ev.is_running;
            game.can_stop = ev.can_stop;
        }
    }

    pub fn apply_launch_status(&mut self, ev: LaunchStatusEvent) {
        if let Some(game) = self.games.iter_mut().find(|g| g.id == ev.game_id) {
            match ev.phase.as_str() {
                "running" => {
                    game.is_running = true;
                    game.can_stop = !ev.handoff_only;
                }
                "stopped" | "failed" => {
                    game.is_running = false;
                    game.can_stop = false;
                }
                _ => {}
            }
        }
        if (!ev.ok || matches!(ev.phase.as_str(), "failed" | "stopFailed"))
            && let Some(msg) = ev.message.filter(|m| !m.trim().is_empty())
        {
            self.notice = Some(msg);
        }
    }

    pub fn apply_app_update_state(&mut self, ev: AppUpdateStateEvent) {
        self.pc.checking_update = ev.state == "checking";
        self.pc.installing_update =
            matches!(ev.state.as_str(), "downloading" | "applying" | "restarting");
        if matches!(ev.state.as_str(), "idle" | "failed") {
            self.pc.update_progress = None;
        }
        self.app_update_state = ev.state;
    }

    pub fn apply_exo_account_changed(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        self.call_host_async(
            METHOD_EXO_ACCOUNT_STATE,
            json!({}),
            |s, res: Result<ExoAccountState, String>| {
                if let Ok(acct) = res {
                    s.pc.account = acct;
                }
            },
        );
    }

    pub fn apply_autopilot_live(&mut self, live: Option<AutopilotLive>) {
        if let Some(ref mut ap) = self.pc.autopilot {
            ap.live = live;
        }
    }

    pub fn apply_autopilot_report(&mut self, ev: AutopilotReportEvent) {
        if !ev.title.is_empty() {
            self.notice = Some(format!("Autopilot report saved for {}.", ev.title));
        }
        if self.room == Room::ExoAi {
            self.load_pc_exo_ai();
        }
    }

    pub fn apply_shaders_changed(&mut self) {
        if self.room == Room::ExoAi {
            self.load_pc_exo_ai();
        }
    }

    pub fn apply_store_changed(&mut self, ev: StoreChangedEvent) {
        if matches!(ev.what.as_str(), "home" | "wishlist")
            && (self.room == Room::Store || self.store_home.is_some())
            && !self.store_loading
        {
            self.load_store_home(false);
        }
    }

    #[cfg(windows)]
    pub fn bootstrap_rpc(rpc: Arc<RpcClient>, handle: rune::AppHandle<AppState>) {
        let rpc_for_media = Arc::clone(&rpc);
        rpc.call::<_, ShellBootstrap>(METHOD_SHELL_BOOTSTRAP, &json!({}), move |res| match res {
            Ok(boot) => {
                if let Some(media_pipe) = boot.binary_media_pipe.clone() {
                    let rpc_media = Arc::clone(&rpc_for_media);
                    std::thread::Builder::new()
                        .name("exo-media-connect".into())
                        .spawn(move || {
                            let _ = rpc_media
                                .connect_media_pipe(&media_pipe, std::time::Duration::from_secs(3));
                        })
                        .ok();
                }
                handle.update(move |s, _| s.apply_bootstrap(boot));
            }
            Err(e) => {
                let msg = e.to_string();
                handle.update(move |s, _| {
                    s.refreshing = false;
                    s.status = HostStatus::Error(msg.clone());
                    s.notice = Some(format!("Failed to bootstrap host: {msg}"));
                });
            }
        });
    }
}

pub fn title_hue(title: &str) -> f32 {
    let hash = title.bytes().fold(2166136261u32, |acc, b| {
        acc.wrapping_mul(16777619) ^ b as u32
    });
    (hash % 360) as f32
}

fn demo_games() -> Vec<Game> {
    let raw = [
        (
            "demo:cyberpunk",
            "Cyberpunk 2077",
            "steam",
            true,
            8520,
            Some(68_000_000_000),
        ),
        (
            "demo:cs2",
            "Counter-Strike 2",
            "steam",
            true,
            37080,
            Some(42_500_000_000),
        ),
        (
            "demo:elden",
            "Elden Ring",
            "steam",
            true,
            5640,
            Some(66_200_000_000),
        ),
        (
            "demo:valorant",
            "Valorant",
            "riot",
            true,
            18720,
            Some(34_800_000_000),
        ),
        (
            "demo:hades2",
            "Hades II",
            "steam",
            true,
            2760,
            Some(10_400_000_000),
        ),
        (
            "demo:bg3",
            "Baldur's Gate 3",
            "steam",
            true,
            11280,
            Some(144_000_000_000),
        ),
        (
            "demo:apex",
            "Apex Legends",
            "steam",
            true,
            12600,
            Some(74_000_000_000),
        ),
        (
            "demo:hollow",
            "Hollow Knight",
            "steam",
            true,
            3480,
            Some(9_100_000_000),
        ),
        ("demo:aw2", "Alan Wake 2", "epic", false, 1740, None),
        (
            "demo:hd2",
            "Helldivers 2",
            "steam",
            true,
            4440,
            Some(82_000_000_000),
        ),
        ("demo:fh5", "Forza Horizon 5", "xbox", false, 3780, None),
        ("demo:doom", "Doom Eternal", "steam", false, 2460, None),
    ];
    raw.into_iter()
        .map(
            |(id, title, store, installed, playtime_minutes, size_bytes)| Game {
                id: id.into(),
                title: title.into(),
                store: store.into(),
                stores: vec![store.into()],
                installed,
                owned: true,
                playtime_minutes,
                size_bytes,
                tags: vec!["Action".into(), "Singleplayer".into()],
                ..Default::default()
            },
        )
        .collect()
}

fn demo_store_home() -> StoreHome {
    let tile = |app_id: &str, name: &str, price: &str, discount: i32| StoreTile {
        app_id: app_id.into(),
        name: name.into(),
        price_text: Some(price.into()),
        discount,
        art: Some(StoreArt::default()),
        ..Default::default()
    };
    StoreHome {
        ok: true,
        message: None,
        hero: vec![
            tile("1091500", "Cyberpunk 2077: Ultimate Edition", "$35.99", 40),
            tile("1245620", "Elden Ring: Shadow of the Erdtree", "$47.99", 20),
        ],
        shelves: vec![StoreShelf {
            id: "deals".into(),
            title: "Top deals".into(),
            items: vec![
                tile("1145350", "Hades II", "$23.99", 20),
                tile("1086940", "Baldur's Gate 3", "$47.99", 20),
                tile("553850", "Helldivers 2", "$31.99", 20),
                tile("367520", "Hollow Knight", "$7.49", 50),
                tile("782330", "DOOM Eternal", "$9.99", 75),
                tile("1817070", "Marvel's Spider-Man Remastered", "$29.99", 50),
            ],
        }],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::{CaptureItem, DiscordStatus, MusicPlayback, MusicPlaybackReply};

    #[test]
    fn apply_bootstrap_populates_library_music_and_discord() {
        let mut s = AppState::for_host_pipe();
        let boot = ShellBootstrap {
            ok: true,
            art_base: ArtBase {
                covers: "https://covers.exo-launcher.local/".into(),
                ..Default::default()
            },
            settings: LauncherSettings {
                fps_counter: true,
                ..Default::default()
            },
            library: LibraryPayload {
                games: vec![Game {
                    id: "steam:10".into(),
                    title: "Half-Life".into(),
                    ..Default::default()
                }],
                count: 1,
                recent: vec!["steam:10".into()],
                ..Default::default()
            },
            music_playback: MusicPlaybackReply {
                ok: true,
                playback: Some(MusicPlayback {
                    status: "playing".into(),
                    title: "Still Alive".into(),
                    ..Default::default()
                }),
            },
            discord_status: DiscordStatus {
                state: "ready".into(),
                ..Default::default()
            },
            binary_media_pipe: Some("exo-pipe-media".into()),
        };

        s.apply_bootstrap(boot);
        assert_eq!(s.games.len(), 1);
        assert!(s.settings.fps_counter);
        assert_eq!(
            s.media.playback.as_ref().map(|p| p.title.as_str()),
            Some("Still Alive")
        );
        assert_eq!(s.discord.status.state, "ready");
    }

    #[test]
    fn push_event_handlers_update_state_without_roundtrip() {
        let mut s = AppState::with_demo_games();
        s.apply_music_volume(MusicVolumeEvent {
            level: 0.35,
            source: "app".into(),
        });
        assert!((s.media.music_volume - 0.35).abs() < 1e-6);
        assert!(!s.media.music_muted);

        s.apply_capture_state(CaptureLiveState {
            present: true,
            buffering: true,
            recording: true,
            recording_ms: Some(4200.0),
            replay: true,
        });
        assert!(
            s.media
                .capture_status
                .as_ref()
                .and_then(|c| c.state.as_ref())
                .is_some_and(|st| st.recording)
        );

        s.apply_capture_saved(CaptureSavedEvent {
            item: CaptureItem {
                id: "cap-new".into(),
                name: "Clutch Clip".into(),
                ..Default::default()
            },
            edit: false,
        });
        assert_eq!(s.media.capture_items[0].id, "cap-new");

        s.apply_game_run_state(GameRunStateEvent {
            id: "demo:cs2".into(),
            is_running: true,
            can_stop: true,
        });
        let cs2 = s.games.iter().find(|g| g.id == "demo:cs2").unwrap();
        assert!(cs2.is_running && cs2.can_stop);

        s.apply_launch_status(LaunchStatusEvent {
            game_id: "demo:cs2".into(),
            ok: false,
            message: Some("Launch failed".into()),
            phase: "failed".into(),
            ..Default::default()
        });
        let cs2 = s.games.iter().find(|g| g.id == "demo:cs2").unwrap();
        assert!(!cs2.is_running);
        assert_eq!(s.notice.as_deref(), Some("Launch failed"));

        s.apply_app_update_state(AppUpdateStateEvent {
            state: "downloading".into(),
        });
        assert!(s.pc.installing_update);
        assert_eq!(s.app_update_state, "downloading");
    }
}

const MARKETPLACE_TITLES: &[&str] = &[
    "actor locker",
    "actorlocker",
    "auto footstep",
    "autofootstep",
    "async loading",
    "ai for npc",
    "metahuman",
    "unreal engine",
    "c++",
    "sequence manager",
    "montage sequence",
    "electric dreams",
    "code flow",
    "quixel",
    "megascan",
    "twinmotion",
    "lyra starter",
    "city sample",
    "content example",
    "starter content",
    "vehicle variety",
    "fab plugin",
    "unreal marketplace",
];

const EXACT_DROP_TITLES: &[&str] = &["discord", "epiconlineservices", "eosoverlay"];

const TOOL_TITLES: &[&str] = &["soundtrack", "dedicated server", "software development kit"];

fn compact_title(title: &str) -> String {
    title
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .map(|c| c.to_ascii_lowercase())
        .collect()
}

fn is_marketplace_or_engine_title(title: &str) -> bool {
    let lower = title.to_lowercase();
    let packed = compact_title(title);
    if EXACT_DROP_TITLES.contains(&packed.as_str()) {
        return true;
    }
    MARKETPLACE_TITLES.iter().any(|needle| {
        let needle_packed: String = needle.chars().filter(|c| !c.is_whitespace()).collect();
        lower.contains(needle) || packed.contains(&needle_packed)
    })
}

fn has_word_demo(title: &str) -> bool {
    let lower = title.to_ascii_lowercase();
    if lower.contains("(demo)") {
        return true;
    }
    let bytes = lower.as_bytes();
    let word = b"demo";
    if bytes.len() < word.len() {
        return false;
    }
    for i in 0..=bytes.len() - word.len() {
        if &bytes[i..i + word.len()] == word {
            let left_ok = i == 0 || !(bytes[i - 1].is_ascii_alphanumeric() || bytes[i - 1] == b'_');
            let end = i + word.len();
            let right_ok =
                end == bytes.len() || !(bytes[end].is_ascii_alphanumeric() || bytes[end] == b'_');
            if left_ok && right_ok {
                return true;
            }
        }
    }
    false
}

fn is_tool_or_extra_title(title: &str) -> bool {
    let padded = format!(" {} ", title.to_lowercase());
    TOOL_TITLES.iter().any(|needle| padded.contains(needle)) || has_word_demo(title)
}

pub fn is_playable_library_game(game: &Game, show_tools: bool) -> bool {
    if game.is_add_portable || game.id == "local:add" {
        return false;
    }
    let id_lower = game.id.to_ascii_lowercase();
    if !id_lower.starts_with("steam:") && is_marketplace_or_engine_title(&game.title) {
        return false;
    }
    if !show_tools && is_tool_or_extra_title(&game.title) {
        return false;
    }
    if compact_title(&game.title) == "emergence" && id_lower.starts_with("epic:") {
        return false;
    }
    true
}

fn is_installing_game(game: &Game, transfer_id: Option<&str>) -> bool {
    let Some(tid) = transfer_id else {
        return false;
    };
    if game.id == tid || game.variants.iter().any(|v| v.id == tid) {
        return true;
    }
    if let Some(rest) = tid
        .strip_prefix("steam:")
        .or_else(|| tid.strip_prefix("STEAM:"))
    {
        let digits: String = rest.chars().take_while(|c| c.is_ascii_digit()).collect();
        if !digits.is_empty() && game.art_steam_app_id().as_deref() == Some(digits.as_str()) {
            return true;
        }
    }
    false
}

pub fn is_shelf_game(game: &Game, transfer_id: Option<&str>, show_tools: bool) -> bool {
    if !is_playable_library_game(game, show_tools) {
        return false;
    }
    if is_installing_game(game, transfer_id) {
        return true;
    }
    if game.installed || game.owned {
        return true;
    }
    game.store == "local"
}

fn cmp_title_base(a: &str, b: &str) -> std::cmp::Ordering {
    a.to_lowercase().cmp(&b.to_lowercase())
}

fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = y - i64::from(m <= 2);
    let era = y.div_euclid(400);
    let yoe = (y - era * 400) as u64;
    let m = u64::from(m);
    let d = u64::from(d);
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe as i64 - 719_468
}

pub fn parse_iso_utc_ms(raw: &str) -> Option<i64> {
    let s = raw.trim();
    if s.len() < 10 {
        return None;
    }
    let bytes = s.as_bytes();
    if bytes[4] != b'-' || bytes[7] != b'-' {
        return None;
    }
    let year: i64 = s[0..4].parse().ok()?;
    let month: u32 = s[5..7].parse().ok()?;
    let day: u32 = s[8..10].parse().ok()?;
    if !(1..=12).contains(&month) || !(1..=31).contains(&day) {
        return None;
    }
    let days = days_from_civil(year, month, day);
    if s.len() == 10 {
        return Some(days * 86_400_000);
    }
    if !matches!(bytes[10], b'T' | b't' | b' ') || s.len() < 19 {
        return None;
    }
    if bytes[13] != b':' || bytes[16] != b':' {
        return None;
    }
    let hour: i64 = s[11..13].parse().ok()?;
    let min: i64 = s[14..16].parse().ok()?;
    let sec: i64 = s[17..19].parse().ok()?;
    if !(0..=23).contains(&hour) || !(0..=59).contains(&min) || !(0..=60).contains(&sec) {
        return None;
    }
    let mut rest = &s[19..];
    let mut ms: i64 = 0;
    if let Some(after_dot) = rest.strip_prefix('.') {
        let frac_len = after_dot.bytes().take_while(|b| b.is_ascii_digit()).count();
        let frac = &after_dot[..frac_len];
        if !frac.is_empty() {
            let mut scaled = 0i64;
            for idx in 0..3 {
                let digit = frac
                    .as_bytes()
                    .get(idx)
                    .map(|b| i64::from(b - b'0'))
                    .unwrap_or(0);
                scaled = scaled * 10 + digit;
            }
            ms = scaled;
        }
        rest = &after_dot[frac_len..];
    }
    let tz_offset_ms: i64 = if rest.is_empty() || rest == "Z" || rest == "z" {
        0
    } else if let Some(sign_byte) = rest.as_bytes().first().copied()
        && (sign_byte == b'+' || sign_byte == b'-')
    {
        let sign = if sign_byte == b'+' { 1i64 } else { -1i64 };
        let tz = &rest[1..];
        let (th, tm) = if let Some((h, m)) = tz.split_once(':') {
            (h.parse::<i64>().ok()?, m.parse::<i64>().ok()?)
        } else if tz.len() == 4 {
            (tz[0..2].parse::<i64>().ok()?, tz[2..4].parse::<i64>().ok()?)
        } else {
            return None;
        };
        sign * (th * 3600 + tm * 60) * 1000
    } else {
        return None;
    };
    let utc_ms = days * 86_400_000 + (hour * 3600 + min * 60 + sec) * 1000 + ms - tz_offset_ms;
    Some(utc_ms)
}

#[cfg(test)]
mod library_sort_and_confirm_tests {
    use super::*;

    fn mk_game(
        id: &str,
        title: &str,
        installed: bool,
        owned: bool,
        last_played_utc: Option<&str>,
    ) -> Game {
        Game {
            id: id.into(),
            title: title.into(),
            store: "steam".into(),
            stores: vec!["steam".into()],
            installed,
            owned,
            last_played_utc: last_played_utc.map(str::to_string),
            ..Default::default()
        }
    }

    #[test]
    fn last_launched_sort_matches_react_timestamps_tiebreaks_and_hygiene() {
        let mut s = AppState::with_demo_games();
        s.settings.show_library_tools = false;
        s.settings.hidden_games = vec!["steam:hidden".into()];
        s.recent_ids = vec!["steam:tie-first".into(), "steam:tie-second".into()];
        s.sort = LibrarySort::LastLaunched;
        s.filter = LibraryFilter::All;
        s.games = vec![
            mk_game(
                "steam:older-z",
                "Older Z",
                true,
                true,
                Some("2026-04-10T10:00:00Z"),
            ),
            mk_game(
                "steam:newer-frac",
                "Newer Fractional",
                false,
                true,
                Some("2026-04-10T10:00:00.5000000Z"),
            ),
            mk_game(
                "steam:offset",
                "Offset Newer",
                false,
                true,
                Some("2026-04-10T12:00:01+02:00"),
            ),
            mk_game("steam:never-b", "Beta Never", true, true, None),
            mk_game("steam:never-a", "alpha never", false, true, None),
            mk_game("steam:tie-second", "Tie Alpha", true, true, None),
            mk_game("steam:tie-first", "Tie Zulu", false, true, None),
            mk_game(
                "steam:hidden",
                "Hidden Game",
                true,
                true,
                Some("2026-05-01T00:00:00Z"),
            ),
            mk_game(
                "steam:demo",
                "Cool Game Demo",
                true,
                true,
                Some("2026-05-02T00:00:00Z"),
            ),
            mk_game(
                "steam:unowned",
                "Not Owned",
                false,
                false,
                Some("2026-05-03T00:00:00Z"),
            ),
        ];

        let ids: Vec<&str> = s
            .filtered_games()
            .into_iter()
            .map(|g| g.id.as_str())
            .collect();
        assert_eq!(
            ids,
            vec![
                "steam:offset",
                "steam:newer-frac",
                "steam:older-z",
                "steam:tie-first",
                "steam:tie-second",
                "steam:never-a",
                "steam:never-b",
            ]
        );
    }

    #[test]
    fn confirm_button_arms_disarms_on_escape_timeout_and_fires_on_second_press() {
        let mut s = AppState::with_demo_games();
        let mut tx = Tx::default();
        assert!(!s.is_confirm_armed("gp-uninstall"));

        s.arm_or_confirm_game_uninstall("demo:cyberpunk", &mut tx);
        assert!(s.is_confirm_armed("gp-uninstall"));
        assert!(s.notice.is_none());

        assert!(s.dismiss_top_layer());
        assert!(!s.is_confirm_armed("gp-uninstall"));

        s.clock_secs = 10.0;
        s.arm_or_confirm_game_uninstall("demo:cyberpunk", &mut tx);
        assert!(s.is_confirm_armed("gp-uninstall"));
        s.sync_and_tick_notices(13.5, &mut tx);
        assert!(s.is_confirm_armed("gp-uninstall"));
        s.sync_and_tick_notices(14.0, &mut tx);
        assert!(!s.is_confirm_armed("gp-uninstall"));

        s.arm_or_confirm_game_uninstall("demo:cyberpunk", &mut tx);
        assert!(s.is_confirm_armed("gp-uninstall"));
        s.arm_or_confirm_game_uninstall("demo:cyberpunk", &mut tx);
        assert!(!s.is_confirm_armed("gp-uninstall"));
        assert!(
            s.notice
                .as_deref()
                .is_some_and(|n| n.contains("Uninstalling"))
        );
    }

    #[test]
    fn store_shelf_paging_respects_bounds_and_scroll_targets() {
        let mut s = AppState::with_demo_games();
        let mut tx = Tx::default();
        let mut pos = ScrollPos::default();
        pos.viewport = [1000.0, 260.0];
        pos.max = [1400.0, 0.0];
        s.store_shelf_scrolls[0].set(pos);

        assert_eq!(s.store_shelf_can_page(0, 12), (false, true));
        s.page_store_shelf(0, 1.0, 12, &mut tx);
        assert_eq!(s.store_shelf_x[0], 800.0);

        pos.offset = [800.0, 0.0];
        s.store_shelf_scrolls[0].set(pos);
        assert_eq!(s.store_shelf_can_page(0, 12), (true, true));
        s.page_store_shelf(0, 1.0, 12, &mut tx);
        assert_eq!(s.store_shelf_x[0], 1400.0);

        pos.offset = [1400.0, 0.0];
        s.store_shelf_scrolls[0].set(pos);
        assert_eq!(s.store_shelf_can_page(0, 12), (true, false));
        s.page_store_shelf(0, -1.0, 12, &mut tx);
        assert_eq!(s.store_shelf_x[0], 600.0);
    }
}
