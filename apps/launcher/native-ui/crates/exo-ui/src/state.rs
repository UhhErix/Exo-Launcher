use crate::discord_state::DiscordState;
use crate::gamepad::GamepadAction;
use crate::media_state::MediaState;
use crate::pc_state::SettingsAndPcState;
use crate::tour;
use exo_rpc::{
    ActionResult, ArtBase, Game, GameExtras, GameGetReply, GameProperties, GamePropertySetReply,
    InstallProgress, KeyPreview, LauncherSettings, LibraryPayload, METHOD_ART_BASE,
    METHOD_GAME_CANCEL_INSTALL, METHOD_GAME_EXTRAS, METHOD_GAME_GET, METHOD_GAME_INSTALL,
    METHOD_GAME_LAUNCH, METHOD_GAME_OPEN_FOLDER, METHOD_GAME_PAUSE_INSTALL, METHOD_GAME_PROPERTIES,
    METHOD_GAME_PROPERTIES_SET, METHOD_GAME_REPAIR, METHOD_GAME_STOP, METHOD_GAME_UNINSTALL,
    METHOD_GAME_UPDATE, METHOD_KEYS_ACTIVATE, METHOD_KEYS_PREVIEW, METHOD_LIBRARY_GET,
    METHOD_LIBRARY_REFRESH, METHOD_SETTINGS_GET, METHOD_SETTINGS_SET, METHOD_STORE_GAME,
    METHOD_STORE_GAME_OFFERS, METHOD_STORE_HOME, METHOD_STORE_OPEN, METHOD_STORE_SEARCH, StoreArt,
    StoreGame, StoreHome, StoreOffer, StoreOtherOffers, StoreSearchReply, StoreShelf, StoreTile,
    remote_art,
};
use rune::{Image, ImageCache, TextEdit, Tx, Waker};
use serde_json::json;
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::Arc;

#[cfg(windows)]
use exo_rpc::RpcClient;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Room {
    #[default]
    Library,
    Store,
    Discord,
    Music,
    Pc,
    Settings,
}

impl Room {
    pub const ALL: [Room; 6] = [
        Room::Library,
        Room::Store,
        Room::Discord,
        Room::Music,
        Room::Pc,
        Room::Settings,
    ];

    pub const fn label(self) -> &'static str {
        match self {
            Room::Library => "Library",
            Room::Store => "Store",
            Room::Discord => "Discord",
            Room::Music => "Music",
            Room::Pc => "This PC",
            Room::Settings => "Settings",
        }
    }

    pub const fn key(self) -> &'static str {
        match self {
            Room::Library => "nav-library",
            Room::Store => "nav-store",
            Room::Discord => "nav-discord",
            Room::Music => "nav-music",
            Room::Pc => "nav-pc",
            Room::Settings => "nav-settings",
        }
    }

    pub const fn index(self) -> usize {
        match self {
            Room::Library => 0,
            Room::Store => 1,
            Room::Discord => 2,
            Room::Music => 3,
            Room::Pc => 4,
            Room::Settings => 5,
        }
    }

    pub const fn from_index(i: usize) -> Self {
        Self::ALL[i % Self::ALL.len()]
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

impl LibraryFilter {
    pub const ALL: [LibraryFilter; 4] = [
        LibraryFilter::All,
        LibraryFilter::Installed,
        LibraryFilter::Recent,
        LibraryFilter::Running,
    ];

    pub const fn label(self) -> &'static str {
        match self {
            LibraryFilter::All => "All",
            LibraryFilter::Installed => "Installed",
            LibraryFilter::Recent => "Recent",
            LibraryFilter::Running => "Running",
        }
    }

    pub const fn key(self) -> &'static str {
        match self {
            LibraryFilter::All => "filter-all",
            LibraryFilter::Installed => "filter-installed",
            LibraryFilter::Recent => "filter-recent",
            LibraryFilter::Running => "filter-running",
        }
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LibrarySort {
    #[default]
    Playtime,
    Title,
    Size,
}

impl LibrarySort {
    pub const fn label(self) -> &'static str {
        match self {
            Self::Playtime => "Sort: Playtime",
            Self::Title => "Sort: A–Z",
            Self::Size => "Sort: Size",
        }
    }

    pub const fn next(self) -> Self {
        match self {
            Self::Playtime => Self::Title,
            Self::Title => Self::Size,
            Self::Size => Self::Playtime,
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
    LaunchGame(String),
    RefreshLibrary,
    ActivateKey,
    StartTour,
    ToggleFilter(LibraryFilter),
}

#[derive(Clone, Debug)]
pub struct PaletteItem {
    pub id: String,
    pub title: String,
    pub subtitle: String,
    pub target: PaletteTarget,
}

pub struct AppState {
    pub room: Room,
    pub filter: LibraryFilter,
    pub sort: LibrarySort,
    pub status: HostStatus,
    pub games: Vec<Game>,
    pub recent_ids: Vec<String>,
    pub selected_game_id: Option<String>,
    pub open_game_id: Option<String>,
    pub game_extras: Option<GameExtras>,
    pub game_props: Option<GameProperties>,
    pub props_open: bool,
    pub props_launch_opts: TextEdit,
    pub uninstall_confirm: bool,
    pub install_progress: Option<InstallProgress>,
    pub key_modal_open: bool,
    pub key_input: TextEdit,
    pub key_preview: Option<KeyPreview>,
    pub key_submitting: bool,
    pub store_home: Option<StoreHome>,
    pub store_loading: bool,
    pub store_hero_index: usize,
    pub store_query: TextEdit,
    pub store_search_results: Option<Vec<StoreTile>>,
    pub store_game: Option<StoreGame>,
    pub store_offers: Option<StoreOtherOffers>,
    pub tour_step: Option<usize>,
    pub tour_seen: bool,
    pub gamepad_connected: bool,
    pub settings: LauncherSettings,
    pub pc: SettingsAndPcState,
    pub media: MediaState,
    pub discord: DiscordState,
    pub art_base: ArtBase,
    pub notice: Option<String>,
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
    #[cfg(windows)]
    pub rpc: Option<Arc<RpcClient>>,
    #[cfg(windows)]
    pub handle: Option<rune::AppHandle<AppState>>,
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
            filter: LibraryFilter::All,
            sort: LibrarySort::Playtime,
            status: HostStatus::Standalone,
            games,
            recent_ids: vec!["demo:cyberpunk".into(), "demo:cs2".into(), "demo:elden".into()],
            selected_game_id,
            open_game_id: None,
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
            props_launch_opts: TextEdit::default(),
            uninstall_confirm: false,
            install_progress: None,
            key_modal_open: false,
            key_input: TextEdit::default(),
            key_preview: None,
            key_submitting: false,
            store_home: Some(demo_store_home()),
            store_loading: false,
            store_hero_index: 0,
            store_query: TextEdit::default(),
            store_search_results: None,
            store_game: None,
            store_offers: None,
            tour_step: None,
            tour_seen: true,
            gamepad_connected: false,
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
            #[cfg(windows)]
            rpc: None,
            #[cfg(windows)]
            handle: None,
        }
    }

    pub fn for_host_pipe() -> Self {
        Self {
            room: Room::Library,
            filter: LibraryFilter::Installed,
            sort: LibrarySort::Playtime,
            status: HostStatus::Connecting,
            games: Vec::new(),
            recent_ids: Vec::new(),
            selected_game_id: None,
            open_game_id: None,
            game_extras: None,
            game_props: None,
            props_open: false,
            props_launch_opts: TextEdit::default(),
            uninstall_confirm: false,
            install_progress: None,
            key_modal_open: false,
            key_input: TextEdit::default(),
            key_preview: None,
            key_submitting: false,
            store_home: None,
            store_loading: false,
            store_hero_index: 0,
            store_query: TextEdit::default(),
            store_search_results: None,
            store_game: None,
            store_offers: None,
            tour_step: None,
            tour_seen: true,
            gamepad_connected: false,
            settings: LauncherSettings::default(),
            pc: SettingsAndPcState::empty_for_host(),
            media: MediaState::empty_for_host(),
            discord: DiscordState::empty(),
            art_base: ArtBase::default(),
            notice: Some("Connecting to Exo host…".into()),
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
            #[cfg(windows)]
            rpc: None,
            #[cfg(windows)]
            handle: None,
        }
    }

    pub fn install_waker(&mut self, waker: Waker) {
        self.image_cache = Some(ImageCache::new(waker));
    }

    pub fn preload_visible_covers_sync(&self, scale: f32) {
        let mut images: Vec<Image> = Vec::new();
        if self.art_base.covers_dir.is_some() && !self.games.is_empty() {
            images.extend(
                self.filtered_games()
                    .into_iter()
                    .take(18)
                    .filter_map(|g| self.cover_image(g, scale)),
            );
        }
        if let Some(ref home) = self.store_home {
            if let Some(u) = home.hero.first().and_then(|t| t.art.as_ref()).and_then(|a| a.best_hero())
                && let Some(img) = self.remote_image(u, scale)
            {
                images.push(img);
            }
            for shelf in home.shelves.iter().take(2) {
                for tile in shelf.items.iter().take(6) {
                    if let Some(u) = tile.art.as_ref().and_then(|a| a.best_poster())
                        && let Some(img) = self.remote_image(u, scale)
                    {
                        images.push(img);
                    }
                }
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

    pub fn switch_room(&mut self, room: Room) {
        self.room = room;
        self.open_game_id = None;
        self.props_open = false;
        self.uninstall_confirm = false;
        if self.media.captures_open {
            self.close_captures();
        }
        if room == Room::Store && self.store_home.is_none() && !self.store_loading {
            self.load_store_home(false);
        } else if room == Room::Discord {
            self.load_discord_room();
        } else if room == Room::Music {
            self.load_music_room();
        } else if room == Room::Settings {
            self.load_settings_room();
        } else if room == Room::Pc {
            self.load_pc_tab(self.pc.pc_tab);
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
        if self.games.iter().all(|g| Some(&g.id) != self.selected_game_id.as_ref()) {
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

    pub fn installed_count(&self) -> usize {
        self.games.iter().filter(|g| g.installed).count()
    }

    pub fn running_count(&self) -> usize {
        self.games.iter().filter(|g| g.is_running).count()
    }

    pub fn filtered_games(&self) -> Vec<&Game> {
        let mut out: Vec<&Game> = match self.filter {
            LibraryFilter::All => self.games.iter().collect(),
            LibraryFilter::Installed => self.games.iter().filter(|g| g.installed).collect(),
            LibraryFilter::Running => self.games.iter().filter(|g| g.is_running).collect(),
            LibraryFilter::Recent => {
                let mut list: Vec<&Game> = self
                    .games
                    .iter()
                    .filter(|g| {
                        g.last_played_utc.is_some()
                            || self.recent_ids.iter().any(|r| r == &g.id)
                            || g.playtime_minutes > 0
                    })
                    .collect();
                list.sort_by(|a, b| {
                    b.last_played_utc
                        .cmp(&a.last_played_utc)
                        .then_with(|| b.playtime_minutes.cmp(&a.playtime_minutes))
                });
                return list;
            }
        };
        out.sort_by(|a, b| {
            let primary = b.is_running.cmp(&a.is_running).then_with(|| b.installed.cmp(&a.installed));
            match self.sort {
                LibrarySort::Playtime => primary
                    .then_with(|| b.playtime_minutes.cmp(&a.playtime_minutes))
                    .then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase())),
                LibrarySort::Title => primary.then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase())),
                LibrarySort::Size => primary
                    .then_with(|| b.size_bytes.unwrap_or(0).cmp(&a.size_bytes.unwrap_or(0)))
                    .then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase())),
            }
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
        let url = game.cover_url.as_deref()?;
        let key = format!("{url}#{}", game.art_revision.unwrap_or(0));
        if let Some(existing) = self.covers.borrow().get(&key) {
            return existing.clone();
        }
        let loaded = self
            .art_base
            .resolve_local_cover(url)
            .map(|path| cache.file(path, 250.0, scale));
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
        let cache = self.image_cache.as_ref()?;
        if let Some(existing) = self.remote_covers.borrow().get(url) {
            return existing.clone();
        }
        let dir = self.remote_cache_dir.as_ref()?;
        if remote_art::parse_allowed_https(url).is_none() {
            return None;
        }
        let path = remote_art::cache_path_for(dir, url);
        if path.is_file() {
            let img = cache.file(path, 360.0, scale);
            self.remote_covers
                .borrow_mut()
                .insert(url.to_string(), Some(img.clone()));
            return Some(img);
        }
        #[cfg(windows)]
        if let Some(handle) = self.handle.clone() {
            let mut inflight = self.remote_inflight.borrow_mut();
            if inflight.len() < 16 && inflight.insert(url.to_string()) {
                let url_owned = url.to_string();
                let dir_owned = dir.clone();
                std::thread::Builder::new()
                    .name("exo-art-fetch".into())
                    .spawn(move || {
                        let fetched = remote_art::fetch_cached_image(&url_owned, &dir_owned);
                        handle.update(move |s, _| {
                            s.remote_inflight.borrow_mut().remove(&url_owned);
                            if let (Some(p), Some(ic)) = (fetched, s.image_cache.as_ref()) {
                                let img = ic.file(p, 360.0, scale);
                                s.remote_covers.borrow_mut().insert(url_owned, Some(img));
                            } else {
                                s.remote_covers.borrow_mut().insert(url_owned, None);
                            }
                        });
                    })
                    .ok();
            }
        }
        None
    }

    pub fn open_game_page(&mut self, id: &str) {
        self.room = Room::Library;
        self.selected_game_id = Some(id.to_string());
        self.open_game_id = Some(id.to_string());
        self.props_open = false;
        self.uninstall_confirm = false;
        if self.status != HostStatus::Standalone {
            self.game_extras = None;
            self.game_props = None;
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
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

    pub fn close_game_page(&mut self) {
        self.open_game_id = None;
        self.props_open = false;
        self.uninstall_confirm = false;
    }

    pub fn open_game_properties(&mut self, id: &str) {
        self.props_open = true;
        if let Some(ref p) = self.game_props {
            self.props_launch_opts.text = p.launch_options.clone().unwrap_or_default();
            self.props_launch_opts.sync_invariants();
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid = id.to_string();
            rpc.call::<_, GameProperties>(METHOD_GAME_PROPERTIES, &json!({ "id": gid }), move |res| {
                if let Ok(props) = res {
                    handle.update(move |s, _| {
                        s.props_launch_opts.text = props.launch_options.clone().unwrap_or_default();
                        s.props_launch_opts.sync_invariants();
                        s.game_props = Some(props);
                    });
                }
            });
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
                        Ok(r) => s.notice = Some(r.message.unwrap_or_else(|| "Could not save property".into())),
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
                            s.notice = Some(r.message.unwrap_or_else(|| "Opened store key redemption".into()));
                        }
                        Ok(r) => s.notice = Some(r.message.unwrap_or_else(|| "Key could not be redeemed".into())),
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
            let params = if refresh { json!({ "refresh": true }) } else { json!({}) };
            rpc.call::<_, StoreHome>(METHOD_STORE_HOME, &params, move |res| {
                handle.update(move |s, _| {
                    s.store_loading = false;
                    match res {
                        Ok(home) if home.ok => s.store_home = Some(home),
                        Ok(home) => s.notice = home.message.or_else(|| Some("Store unavailable".into())),
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
            let name = tile.as_ref().map(|t| t.name.clone()).unwrap_or_else(|| "Store Game".into());
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
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let aid = app_id.to_string();
            let h1 = handle.clone();
            rpc.call::<_, StoreGame>(METHOD_STORE_GAME, &json!({ "appId": aid }), move |res| {
                h1.update(move |s, _| {
                    if let Ok(g) = res
                        && g.ok
                    {
                        s.store_game = Some(g);
                    }
                });
            });

            let aid2 = app_id.to_string();
            let h2 = handle.clone();
            rpc.call::<_, StoreOtherOffers>(
                METHOD_STORE_GAME_OFFERS,
                &json!({ "appId": aid2 }),
                move |res| {
                    if let Ok(offers) = res
                        && offers.ok
                    {
                        h2.update(move |s, _| s.store_offers = Some(offers));
                    }
                },
            );
        }
    }

    pub fn close_store_game(&mut self) {
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
        match action {
            GamepadAction::Connected(on) => self.gamepad_connected = on,
            GamepadAction::PrevRoom => {
                let prev = (self.room.index() + Room::ALL.len() - 1) % Room::ALL.len();
                self.switch_room(Room::from_index(prev));
            }
            GamepadAction::NextRoom => {
                let next = (self.room.index() + 1) % Room::ALL.len();
                self.switch_room(Room::from_index(next));
            }
            GamepadAction::Search => {
                if self.palette_open {
                    self.close_palette();
                } else {
                    self.open_palette(tx);
                }
            }
            GamepadAction::Back => {
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
                        self.open_game_page(&id);
                    }
                } else if let Some(g) = self.open_game().cloned() {
                    if g.installed {
                        self.launch_game(&g.id);
                    }
                }
            }
            GamepadAction::Left | GamepadAction::Right | GamepadAction::Up | GamepadAction::Down => {
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
                    self.selected_game_id = Some(ids[next].clone());
                }
            }
        }
    }

    pub fn dismiss_top_layer(&mut self) -> bool {
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
        if self.notice.is_some() {
            self.notice = None;
            return true;
        }
        false
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
        let mut candidates: Vec<&Game> = self
            .games
            .iter()
            .filter(|g| {
                if !query.is_empty() {
                    g.title.to_lowercase().contains(&query)
                } else {
                    g.installed
                }
            })
            .collect();
        candidates.sort_by(|a, b| {
            b.is_running
                .cmp(&a.is_running)
                .then_with(|| b.installed.cmp(&a.installed))
                .then_with(|| b.playtime_minutes.cmp(&a.playtime_minutes))
                .then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase()))
        });

        for game in candidates.into_iter().take(6) {
            let sub = if game.is_running {
                format!("Running · {}", game.store_display())
            } else if game.installed {
                let hrs = game.hours_label();
                if hrs.is_empty() {
                    game.store_display().to_string()
                } else {
                    format!("{} · {hrs}", game.store_display())
                }
            } else {
                format!("Not installed · {}", game.store_display())
            };
            items.push(PaletteItem {
                id: format!("pal-game-{}", game.id),
                title: game.title.clone(),
                subtitle: sub,
                target: if game.installed {
                    PaletteTarget::LaunchGame(game.id.clone())
                } else {
                    PaletteTarget::OpenGame(game.id.clone())
                },
            });
        }

        let actions = [
            ("pal-action-tour", "Take the guided tour", "Help", PaletteTarget::StartTour),
            ("pal-action-key", "Activate a game key", "Library", PaletteTarget::ActivateKey),
            ("pal-action-refresh", "Refresh library", "Action", PaletteTarget::RefreshLibrary),
        ];
        for (id, title, sub, target) in actions {
            if query.is_empty() || title.to_lowercase().contains(&query) {
                items.push(PaletteItem {
                    id: id.into(),
                    title: title.into(),
                    subtitle: sub.into(),
                    target,
                });
            }
        }

        for room in Room::ALL {
            let label = format!("Go to {}", room.label());
            if query.is_empty() || label.to_lowercase().contains(&query) {
                items.push(PaletteItem {
                    id: format!("pal-room-{}", room.key()),
                    title: label,
                    subtitle: "Room".into(),
                    target: PaletteTarget::Room(room),
                });
            }
        }

        for filter in [LibraryFilter::Installed, LibraryFilter::All, LibraryFilter::Recent] {
            let title = format!("Filter: {}", filter.label());
            if !query.is_empty() && title.to_lowercase().contains(&query) {
                items.push(PaletteItem {
                    id: format!("pal-filter-{}", filter.key()),
                    title,
                    subtitle: "Library".into(),
                    target: PaletteTarget::ToggleFilter(filter),
                });
            }
        }

        items.truncate(10);
        items
    }

    pub fn execute_palette_target(&mut self, target: PaletteTarget, tx: &mut Tx) {
        self.close_palette();
        match target {
            PaletteTarget::Room(room) => self.switch_room(room),
            PaletteTarget::OpenGame(id) => self.open_game_page(&id),
            PaletteTarget::LaunchGame(id) => {
                self.room = Room::Library;
                self.selected_game_id = Some(id.clone());
                self.launch_game(&id);
            }
            PaletteTarget::RefreshLibrary => self.refresh_library(),
            PaletteTarget::ActivateKey => self.open_key_modal(tx),
            PaletteTarget::StartTour => self.start_tour(),
            PaletteTarget::ToggleFilter(filter) => {
                self.room = Room::Library;
                self.open_game_id = None;
                self.filter = filter;
            }
        }
    }

    pub fn launch_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_LAUNCH, id, "Launching", "Launched");
    }

    pub fn stop_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_STOP, id, "Stopping", "Stopped");
    }

    pub fn install_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_INSTALL, id, "Starting install for", "Install started for");
    }

    pub fn update_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_UPDATE, id, "Updating", "Update started for");
    }

    pub fn repair_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_REPAIR, id, "Verifying", "Verification started for");
    }

    pub fn open_game_folder(&mut self, id: &str) {
        self.game_action(METHOD_GAME_OPEN_FOLDER, id, "Opening folder for", "Opened folder for");
    }

    pub fn uninstall_game(&mut self, id: &str) {
        self.uninstall_confirm = false;
        self.game_action(METHOD_GAME_UNINSTALL, id, "Uninstalling", "Uninstall started for");
    }

    pub fn cancel_install(&mut self, id: &str) {
        self.game_action(METHOD_GAME_CANCEL_INSTALL, id, "Canceling install for", "Canceled install for");
    }

    pub fn pause_install(&mut self, id: &str) {
        self.game_action(METHOD_GAME_PAUSE_INSTALL, id, "Pausing download for", "Paused download for");
    }

    fn game_action(&mut self, method: &'static str, id: &str, pending_verb: &str, done_verb: &'static str) {
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
                    Ok(r) if r.ok => s.notice = Some(r.message.unwrap_or_else(|| format!("{done_verb} {title}"))),
                    Ok(r) => s.notice = Some(r.message.unwrap_or_else(|| format!("Action failed for {title}"))),
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

    #[cfg(windows)]
    pub fn bootstrap_rpc(rpc: Arc<RpcClient>, handle: rune::AppHandle<AppState>) {
        let art_handle = handle.clone();
        rpc.call::<_, ArtBase>(METHOD_ART_BASE, &json!({}), move |res| {
            if let Ok(base) = res {
                art_handle.update(move |s, _| s.apply_art_base(base));
            }
        });

        let lib_handle = handle.clone();
        rpc.call::<_, LibraryPayload>(METHOD_LIBRARY_GET, &json!({}), move |res| {
            lib_handle.update(move |s, _| match res {
                Ok(lib) => s.apply_library(lib),
                Err(e) => {
                    s.refreshing = false;
                    s.status = HostStatus::Error(e.to_string());
                    s.notice = Some(format!("Failed to load library: {e}"));
                }
            });
        });

        let set_handle = handle.clone();
        rpc.call::<_, LauncherSettings>(METHOD_SETTINGS_GET, &json!({}), move |res| {
            if let Ok(settings) = res {
                set_handle.update(move |s, _| s.apply_settings(settings));
            }
        });
    }
}

pub fn title_hue(title: &str) -> f32 {
    let hash = title
        .bytes()
        .fold(2166136261u32, |acc, b| acc.wrapping_mul(16777619) ^ b as u32);
    (hash % 360) as f32
}

fn demo_games() -> Vec<Game> {
    let raw = [
        ("demo:cyberpunk", "Cyberpunk 2077", "steam", true, 8520, Some(68_000_000_000)),
        ("demo:cs2", "Counter-Strike 2", "steam", true, 37080, Some(42_500_000_000)),
        ("demo:elden", "Elden Ring", "steam", true, 5640, Some(66_200_000_000)),
        ("demo:valorant", "Valorant", "riot", true, 18720, Some(34_800_000_000)),
        ("demo:hades2", "Hades II", "steam", true, 2760, Some(10_400_000_000)),
        ("demo:bg3", "Baldur's Gate 3", "steam", true, 11280, Some(144_000_000_000)),
        ("demo:apex", "Apex Legends", "steam", true, 12600, Some(74_000_000_000)),
        ("demo:hollow", "Hollow Knight", "steam", true, 3480, Some(9_100_000_000)),
        ("demo:aw2", "Alan Wake 2", "epic", false, 1740, None),
        ("demo:hd2", "Helldivers 2", "steam", true, 4440, Some(82_000_000_000)),
        ("demo:fh5", "Forza Horizon 5", "xbox", false, 3780, None),
        ("demo:doom", "Doom Eternal", "steam", false, 2460, None),
    ];
    raw.into_iter()
        .map(|(id, title, store, installed, playtime_minutes, size_bytes)| Game {
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
        })
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
