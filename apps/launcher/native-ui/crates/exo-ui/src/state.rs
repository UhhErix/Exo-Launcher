use exo_rpc::{
    ActionResult, ArtBase, Game, LibraryPayload, METHOD_ART_BASE, METHOD_GAME_LAUNCH,
    METHOD_GAME_STOP, METHOD_LIBRARY_GET, METHOD_LIBRARY_REFRESH,
};
use rune::{Image, ImageCache, TextEdit, Tx, Waker};
use serde_json::json;
use std::cell::RefCell;
use std::collections::HashMap;
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

    pub const fn next_phase_note(self) -> &'static str {
        match self {
            Room::Library => "",
            Room::Store => "Scheduled for Phase 2 (Store browse, deals, and store game pages).",
            Room::Discord => "Scheduled for Phase 5 (DMs, servers, virtualized messages, voice & D3D12 video).",
            Room::Music => "Scheduled for Phase 4 (Native Music room & mini-player over host DRM players).",
            Room::Pc => "Scheduled for Phase 3 (Exo AI, Apps, Storage, and Devices).",
            Room::Settings => "Scheduled for Phase 3 (Settings sidebar, search, Riot sync, and delta updates).",
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
    LaunchGame(String),
    RefreshLibrary,
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
    pub status: HostStatus,
    pub games: Vec<Game>,
    pub recent_ids: Vec<String>,
    pub selected_game_id: Option<String>,
    pub art_base: ArtBase,
    pub notice: Option<String>,
    pub refreshing: bool,
    pub palette_open: bool,
    pub palette_query: TextEdit,
    pub palette_index: usize,
    pub visible_limit: usize,
    pub image_cache: Option<ImageCache>,
    covers: RefCell<HashMap<String, Option<Image>>>,
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
            status: HostStatus::Standalone,
            games,
            recent_ids: vec!["demo:cyberpunk".into(), "demo:cs2".into(), "demo:elden".into()],
            selected_game_id,
            art_base: ArtBase::default(),
            notice: None,
            refreshing: false,
            palette_open: false,
            palette_query: TextEdit::default(),
            palette_index: 0,
            visible_limit: 96,
            image_cache: None,
            covers: RefCell::new(HashMap::new()),
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
            status: HostStatus::Connecting,
            games: Vec::new(),
            recent_ids: Vec::new(),
            selected_game_id: None,
            art_base: ArtBase::default(),
            notice: Some("Connecting to Exo host…".into()),
            refreshing: true,
            palette_open: false,
            palette_query: TextEdit::default(),
            palette_index: 0,
            visible_limit: 96,
            image_cache: None,
            covers: RefCell::new(HashMap::new()),
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
        if self.art_base.covers_dir.is_none() || self.games.is_empty() {
            return;
        }
        let images: Vec<Image> = self
            .filtered_games()
            .into_iter()
            .take(18)
            .filter_map(|g| self.cover_image(g, scale))
            .collect();
        for img in images {
            let _ = img.wait();
        }
    }

    pub fn apply_library(&mut self, payload: LibraryPayload) {
        self.games = payload
            .games
            .into_iter()
            .filter(|g| !g.is_add_portable)
            .collect();
        self.recent_ids = payload.recent;
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
    }

    pub fn apply_art_base(&mut self, base: ArtBase) {
        self.art_base = base;
        self.covers.borrow_mut().clear();
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
            b.is_running
                .cmp(&a.is_running)
                .then_with(|| b.installed.cmp(&a.installed))
                .then_with(|| b.playtime_minutes.cmp(&a.playtime_minutes))
                .then_with(|| a.title.to_lowercase().cmp(&b.title.to_lowercase()))
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

        for game in candidates.into_iter().take(8) {
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
                target: PaletteTarget::LaunchGame(game.id.clone()),
            });
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

        if query.is_empty() || "refresh library".contains(&query) {
            items.push(PaletteItem {
                id: "pal-action-refresh".into(),
                title: "Refresh library".into(),
                subtitle: "Action".into(),
                target: PaletteTarget::RefreshLibrary,
            });
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

    pub fn execute_palette_target(&mut self, target: PaletteTarget) {
        self.close_palette();
        match target {
            PaletteTarget::Room(room) => self.room = room,
            PaletteTarget::LaunchGame(id) => {
                self.room = Room::Library;
                self.selected_game_id = Some(id.clone());
                self.launch_game(&id);
            }
            PaletteTarget::RefreshLibrary => self.refresh_library(),
            PaletteTarget::ToggleFilter(filter) => {
                self.room = Room::Library;
                self.filter = filter;
            }
        }
    }

    pub fn launch_game(&mut self, id: &str) {
        let title = self
            .games
            .iter()
            .find(|g| g.id == id)
            .map(|g| g.title.clone())
            .unwrap_or_else(|| id.to_string());
        self.notice = Some(format!("Launching {title}…"));

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let game_id = id.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_GAME_LAUNCH,
                &json!({ "id": game_id }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => s.notice = Some(format!("Launched {title}")),
                        Ok(r) => s.notice = Some(r.message.unwrap_or_else(|| format!("Could not launch {title}"))),
                        Err(e) => s.notice = Some(format!("Launch failed: {e}")),
                    });
                },
            );
        }
    }

    pub fn stop_game(&mut self, id: &str) {
        let title = self
            .games
            .iter()
            .find(|g| g.id == id)
            .map(|g| g.title.clone())
            .unwrap_or_else(|| id.to_string());
        self.notice = Some(format!("Stopping {title}…"));

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let game_id = id.to_string();
            rpc.call::<_, ActionResult>(
                METHOD_GAME_STOP,
                &json!({ "id": game_id }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => s.notice = Some(format!("Stopped {title}")),
                        Ok(r) => s.notice = Some(r.message.unwrap_or_else(|| format!("Could not stop {title}"))),
                        Err(e) => s.notice = Some(format!("Stop failed: {e}")),
                    });
                },
            );
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
            ..Default::default()
        })
        .collect()
}
