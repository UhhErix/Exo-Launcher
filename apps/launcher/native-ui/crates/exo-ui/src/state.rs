use crate::boot::BootState;
use crate::discord_state::DiscordState;
use crate::downloads::DownloadsState;
use crate::events::EventRegistry;
use crate::gamepad::GamepadAction;
use crate::image_cache::{CacheRoom, ImageLruCache, bucket_dim, scale_bucket};
use crate::island::IslandState;
use crate::key_modal::KeyModalState;
use crate::media_state::MediaState;
use crate::notice::{NoticeActionSpec, NoticeOpenKind, NoticeRuntimeState};
use crate::palette::PaletteExtrasState;
use crate::pc_state::SettingsAndPcState;
use crate::setup::SetupFlow;
use crate::shell::ShellChromeState;
use crate::video_decoder::VideoFrameRouter;
use crate::whats_new::WhatsNewState;
use exo_kit::SPRING_MOVE;
use exo_rpc::types::COVER_CACHE_ORIGIN;
use exo_rpc::{
    ActionResult, AppUpdateStateEvent, ArtBase, AutopilotLive, AutopilotReportEvent,
    CaptureLiveState, CaptureSavedEvent, CaptureStatusReply, DiscordFriend,
    ExoAccountState, FrameGenRoute, Game, GameExtras, GameFrameGen, GameProperties,
    GameRunStateEvent, HostEvent, InstallProgress, InstallQueueEvent, KeyPreview,
    LaunchStatusEvent, LauncherSettings, LibraryPayload, METHOD_EXO_ACCOUNT_STATE,
    METHOD_GAME_CANCEL_INSTALL, METHOD_GAME_FRAME_GEN, METHOD_GAME_INSTALL, METHOD_GAME_LAUNCH,
    METHOD_GAME_OPEN_FOLDER, METHOD_GAME_PAUSE_INSTALL, METHOD_GAME_UPDATE,
    METHOD_LIBRARY_REFRESH, METHOD_SETTINGS_SET, METHOD_SHELL_BOOTSTRAP, METHOD_STORE_HOME,
    MusicPlayback, MusicVolumeEvent, ShellBootstrap, StoreArt, StoreChangedEvent, StoreGame,
    StoreHome, StoreOtherOffers, StoreShelf, StoreTile, remote_art,
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

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum LibraryChip {
    Installed,
    NotInstalled,
    Update,
    PlayedLately,
    NotPlayed,
    FamilyShared,
    Store(String),
    Friends,
    Tag(String),
}

const PLAYED_LATELY_MS: i64 = 14 * 86_400_000;

pub fn now_unix_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

fn has_played(g: &Game) -> bool {
    g.playtime_minutes > 0 || g.last_played_utc.is_some()
}

impl LibraryChip {
    pub fn section(&self) -> &'static str {
        match self {
            Self::Store(_) => "Store",
            Self::Tag(tag) if is_genre(tag) => "Genre",
            Self::Tag(_) => "Mode",
            _ => "Show",
        }
    }

    /// Chips in one group stand in for each other; every group must match. Each genre or mode is its own group.
    fn group(&self) -> String {
        match self {
            Self::Installed | Self::NotInstalled => "install".into(),
            Self::PlayedLately => "recent".into(),
            Self::NotPlayed => "played".into(),
            Self::Update => "update".into(),
            Self::FamilyShared => "family".into(),
            Self::Store(_) => "store".into(),
            Self::Friends => "friends".into(),
            Self::Tag(tag) => format!("tag:{tag}"),
        }
    }

    pub fn key(&self) -> String {
        match self {
            Self::Installed => "installed".into(),
            Self::NotInstalled => "not-installed".into(),
            Self::Update => "update".into(),
            Self::PlayedLately => "played-lately".into(),
            Self::NotPlayed => "not-played".into(),
            Self::FamilyShared => "family-shared".into(),
            Self::Store(store) => format!("store-{store}"),
            Self::Friends => "friends".into(),
            Self::Tag(tag) => format!("tag-{tag}"),
        }
    }

    pub fn label(&self) -> String {
        match self {
            Self::Installed => "Installed".into(),
            Self::NotInstalled => "Not installed".into(),
            Self::Update => "Update ready".into(),
            Self::PlayedLately => "Played lately".into(),
            Self::NotPlayed => "Not played".into(),
            Self::FamilyShared => "Family shared".into(),
            Self::Store(store) => store_chip_label(store),
            Self::Friends => "Friends playing".into(),
            Self::Tag(tag) => tag_label(tag),
        }
    }

    fn matches(&self, g: &Game, now_ms: i64, playing: &HashSet<String>) -> bool {
        match self {
            Self::Friends => playing.contains(&g.id),
            Self::Tag(tag) => g.tags.iter().any(|t| t == tag),
            Self::Installed => g.installed,
            Self::NotInstalled => !g.installed,
            Self::Update => g.update_available,
            Self::PlayedLately => g
                .last_played_utc
                .as_deref()
                .and_then(parse_iso_utc_ms)
                .is_some_and(|t| now_ms - t < PLAYED_LATELY_MS),
            Self::NotPlayed => !has_played(g),
            Self::FamilyShared => g.family_shared,
            Self::Store(store) => g.store == *store || g.stores.iter().any(|s| s == store),
        }
    }
}

fn store_chip_label(store: &str) -> String {
    match store {
        "steam" => "Steam".into(),
        "epic" => "Epic".into(),
        "gog" => "GOG".into(),
        "riot" => "Riot".into(),
        "xbox" => "Xbox".into(),
        "ea" => "EA".into(),
        "ubisoft" => "Ubisoft".into(),
        "battlenet" => "Battle.net".into(),
        "local" => "Local".into(),
        other => {
            let mut chars = other.chars();
            chars
                .next()
                .map(|c| c.to_uppercase().chain(chars).collect())
                .unwrap_or_default()
        }
    }
}

pub fn chips_match(
    chips: &[LibraryChip],
    g: &Game,
    now_ms: i64,
    playing: &HashSet<String>,
) -> bool {
    chips.iter().all(|c| {
        chips
            .iter()
            .any(|o| o.group() == c.group() && o.matches(g, now_ms, playing))
    })
}

/// The genre and mode words Steam's tags reach the page as (ui/src/lib/librarySearch.ts LIBRARY_TAGS), in the Filter menu's order.
const LIBRARY_TAGS: [&str; 17] = [
    "action",
    "adventure",
    "rpg",
    "strategy",
    "simulation",
    "racing",
    "sports",
    "casual",
    "indie",
    "mmo",
    "free to play",
    "early access",
    "single-player",
    "multiplayer",
    "co-op",
    "local co-op",
    "controller",
];

/// Genres come before the first mode word; the Filter menu splits its Genre and Mode sections there.
fn is_genre(tag: &str) -> bool {
    LIBRARY_TAGS
        .iter()
        .take_while(|t| **t != "single-player")
        .any(|t| *t == tag)
}

fn tag_label(tag: &str) -> String {
    match tag {
        "rpg" => "RPG".into(),
        "mmo" => "MMO".into(),
        "co-op" => "Co-op".into(),
        "local co-op" => "Local co-op".into(),
        "single-player" => "Single-player".into(),
        "free to play" => "Free to play".into(),
        "early access" => "Early access".into(),
        other => {
            let mut chars = other.chars();
            chars
                .next()
                .map(|c| c.to_uppercase().chain(chars).collect())
                .unwrap_or_default()
        }
    }
}

/// The key a title folds on (ui/src/lib/utils.ts titleIdentity): lower case, letters and digits only.
fn title_identity(value: &str) -> String {
    value
        .to_lowercase()
        .chars()
        .filter(|c| c.is_alphanumeric())
        .collect()
}

/// A game a library search can name (ui/src/lib/library.ts isSearchableLibraryGame).
fn is_searchable(g: &Game) -> bool {
    !(g.is_add_portable || g.id == "local:add") && (g.installed || g.owned || g.can_install)
}

/// The library games a Discord friend is playing now (ui/src/lib/friendsPlaying.ts gamesFriendsPlay): an online
/// friend whose activity is Playing or Competing, matched to a game by its title or a part's.
pub fn friends_playing(games: &[Game], friends: &[DiscordFriend]) -> HashSet<String> {
    let mut ids = HashSet::new();
    for friend in friends {
        if friend.kind != "friend" || friend.status == "offline" {
            continue;
        }
        let (kind, name) = match &friend.rich {
            Some(rich) => (Some(rich.activity_type), rich.name.as_str()),
            None => (
                friend.activity_type,
                friend.activity.as_deref().unwrap_or(""),
            ),
        };
        if !matches!(kind, Some(0 | 5)) {
            continue;
        }
        let key = title_identity(name);
        if key.is_empty() {
            continue;
        }
        let found = games.iter().find(|g| {
            is_searchable(g)
                && (title_identity(&g.title) == key
                    || g.includes.iter().any(|p| {
                        p.title.as_deref().map(title_identity).as_deref() == Some(key.as_str())
                    }))
        });
        if let Some(game) = found {
            ids.insert(game.id.clone());
        }
    }
    ids
}

pub fn library_chip_options(games: &[Game], now_ms: i64) -> Vec<LibraryChip> {
    let show = [
        LibraryChip::Installed,
        LibraryChip::NotInstalled,
        LibraryChip::Update,
        LibraryChip::PlayedLately,
        LibraryChip::NotPlayed,
        LibraryChip::FamilyShared,
    ];
    let mut out: Vec<LibraryChip> = show
        .into_iter()
        .filter(|chip| {
            games
                .iter()
                .any(|g| chip.matches(g, now_ms, &HashSet::new()))
        })
        .collect();
    out.push(LibraryChip::Friends);
    let mut stores: Vec<String> = games
        .iter()
        .flat_map(|g| {
            if g.stores.is_empty() {
                vec![g.store.clone()]
            } else {
                g.stores.clone()
            }
        })
        .filter(|s| !s.is_empty())
        .collect();
    stores.sort();
    stores.dedup();
    if stores.len() > 1 {
        out.extend(stores.into_iter().map(LibraryChip::Store));
    }
    out.extend(
        LIBRARY_TAGS
            .iter()
            .filter(|tag| games.iter().any(|g| g.tags.iter().any(|t| t == *tag)))
            .map(|tag| LibraryChip::Tag(tag.to_string())),
    );
    out
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum LibrarySort {
    #[default]
    LastLaunched,
    Installed,
    Playtime,
    Title,
    Size,
    Store,
}

impl LibrarySort {
    pub const ALL: [Self; 6] = [
        Self::Title,
        Self::Installed,
        Self::LastLaunched,
        Self::Playtime,
        Self::Size,
        Self::Store,
    ];

    pub const fn id(self) -> &'static str {
        match self {
            Self::Title => "title",
            Self::Installed => "installed",
            Self::LastLaunched => "last-launched",
            Self::Playtime => "playtime",
            Self::Size => "size",
            Self::Store => "store",
        }
    }

    pub fn from_id(id: &str) -> Self {
        match id {
            "installed" => Self::Installed,
            "playtime" => Self::Playtime,
            "title" | "name" => Self::Title,
            "size" => Self::Size,
            "store" => Self::Store,
            _ => Self::LastLaunched,
        }
    }

    pub const fn label(self) -> &'static str {
        match self {
            Self::Title => "Name",
            Self::Installed => "Installed first",
            Self::LastLaunched => "Last launched",
            Self::Playtime => "Time played",
            Self::Size => "Size",
            Self::Store => "Store",
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum ContextMenuTarget {
    SearchCapsule,
    NavTab(Room),
    CaptureButton,
    SettingsButton,
    GameCard(String),
    FolderCard(String),
    DiscordStatusMenu,
    DiscordAudioQuick {
        input: bool,
    },
    DiscordDm {
        dm_id: String,
        user_id: Option<String>,
        name: String,
        username: Option<String>,
        unread: bool,
        muted: bool,
        group: bool,
    },
    DiscordChannel {
        guild_id: String,
        channel_id: String,
        name: String,
        kind: String,
        unread: bool,
        muted: bool,
        can_edit: bool,
        can_manage: bool,
    },
    DiscordCategory {
        guild_id: String,
        category_id: String,
        name: String,
    },
    DiscordServer {
        guild_id: String,
        name: String,
        muted: bool,
        owner: bool,
        icon: Option<String>,
    },
    DiscordMessage {
        channel_id: String,
        guild_id: Option<String>,
        message_id: String,
        author_id: String,
        author_name: String,
        content: String,
        mine: bool,
        pinned: bool,
    },
    DiscordMember {
        guild_id: Option<String>,
        user_id: String,
        name: String,
        username: Option<String>,
        in_voice: bool,
    },
    DiscordGoLiveMenu,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ContextMenuState {
    pub target: ContextMenuTarget,
    pub x: f32,
    pub y: f32,
    pub open_submenu: Option<usize>,
    pub anchor_key: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum OpenDropdown {
    TextSize,
    MusicService,
    LibrarySort,
    GameDisplayMode,
    GameLanguage,
    GameHdrMode,
    GameIntelFrameGen,
    GameBeta,
    GameFolder,
    GameAntiCheat,
    GameUpscalerStop,
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
    SettingsSection(crate::pc_state::SettingsSection),
    PcSection(&'static str),
    OpenGame(String),
    AddGame,
    RefreshLibrary,
    ActivateKey,
    StartTour,
    CheckUpdates,
    MinimizeWindow,
    CloseWindow,
    ResumeSetup,
    ReportProblem,
    PlaySelected(String),
    InstallSelected(String),
    OpenDownloads,
    OpenWhatsNew,
    NoteNew,
    NoteOpen,
    NoteItem(String),
    DiscordDm(String),
    DiscordServer(String),
    DiscordChannel {
        guild_id: String,
        channel_id: String,
    },
    MusicToggle,
    MusicNext,
    MusicPrevious,
    MusicOpen,
}

#[derive(Clone, Debug)]
pub struct PaletteItem {
    pub id: String,
    pub group: &'static str,
    pub title: String,
    pub detail: Option<String>,
    pub target: PaletteTarget,
}

pub const NOTICE_DURATION_SECS: f64 = 4.0;
pub const MAX_NOTICES: usize = 3;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum NoticeTone {
    #[default]
    Neutral,
    Good,
    Bad,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NoticeAction {
    DownloadNewerUpscalers,
}

#[derive(Clone, Debug)]
pub struct NoticeItem {
    pub id: u64,
    pub key_id: Option<String>,
    pub title: Option<String>,
    pub where_label: Option<String>,
    pub text: String,
    pub tone: NoticeTone,
    pub sticky: bool,
    pub lasting: bool,
    pub count: u32,
    pub progress: Option<Option<f32>>,
    pub action: Option<NoticeActionSpec>,
    pub on_open: Option<NoticeOpenKind>,
    pub avatar_url: Option<String>,
    pub avatar_initial: Option<String>,
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
    Inline,
    Medium,
    #[default]
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
    pub library_add_open: bool,
    pub library_show_hidden: bool,
    pub capture_menu_open: bool,
    pub context_menu: Option<ContextMenuState>,
    pub open_dropdown: Option<OpenDropdown>,
    pub game_display_mode: String,
    pub deck_scroll: Rc<Cell<ScrollPos>>,
    pub deck_scroll_x: f32,
    pub store_shelf_scrolls: [Rc<Cell<ScrollPos>>; 4],
    pub store_shelf_x: [f32; 4],
    pub filters: Vec<LibraryChip>,
    pub library_filter_open: bool,
    pub lib: crate::library_state::LibraryExtraState,
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
    pub game_page: crate::game_actions::GamePageState,
    pub game_frame_gen: Option<GameFrameGen>,
    pub frame_gen_menu_open: bool,
    pub frame_gen_busy: bool,
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
    pub store_ext: crate::store_page::StoreExtraState,
    pub setup: SetupFlow,
    pub gamepad_connected: bool,
    pub input_mode: InputMode,
    pub settings: LauncherSettings,
    pub pc: SettingsAndPcState,
    pub wp4: crate::settings_state::SettingsUiState,
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
    pub palette_needs_focus: bool,
    pub palette_query: TextEdit,
    pub palette_index: usize,
    pub visible_limit: usize,
    pub image_cache: Option<ImageCache>,
    pub remote_cache_dir: Option<PathBuf>,
    pub images: RefCell<ImageLruCache>,
    last_scale_bucket: Cell<u8>,
    pub room_left_at: HashMap<CacheRoom, f64>,
    pub discord_dirty_while_closed: bool,
    remote_inflight: RefCell<HashSet<String>>,
    pub video_router: VideoFrameRouter,
    pub events: Rc<EventRegistry>,
    pub chrome: ShellChromeState,
    pub downloads: DownloadsState,
    pub island: IslandState,
    pub whats_new: WhatsNewState,
    pub boot: BootState,
    pub key_modal: KeyModalState,
    pub notice_runtime: NoticeRuntimeState,
    pub palette_extras: PaletteExtrasState,
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
        let mut s = Self {
            room: Room::Library,
            prev_room: Room::Library,
            room_dir: 1.0,
            mini_bar_slot: MiniBarSlot::Compact,
            library_all_games: false,
            library_search_open: false,
            library_search: TextEdit::default(),
            library_more_open: false,
            library_add_open: false,
            library_show_hidden: false,
            capture_menu_open: false,
            context_menu: None,
            open_dropdown: None,
            game_display_mode: "borderless".into(),
            deck_scroll: Rc::new(Cell::new(ScrollPos::default())),
            deck_scroll_x: 0.0,
            store_shelf_scrolls: std::array::from_fn(|_| Rc::new(Cell::new(ScrollPos::default()))),
            store_shelf_x: [0.0; 4],
            filters: Vec::new(),
            library_filter_open: false,
            lib: crate::library_state::LibraryExtraState::default(),
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
                drive_heat: None,
                message: None,
            }),
            game_props: Some(crate::game_actions::demo_game_properties()),
            game_page: crate::game_actions::GamePageState::demo(),
            game_frame_gen: Some(GameFrameGen {
                ok: true,
                best_route_id: "native_fsr".into(),
                selected_route_id: "native_fsr".into(),
                state: "native".into(),
                word: "Built into the game · FSR 3".into(),
                routes: vec![FrameGenRoute {
                    id: "native_fsr".into(),
                    tier: "native".into(),
                    kind: "native_fsr".into(),
                    label: "Built into the game (FSR 3)".into(),
                    status: "active".into(),
                    summary: "Built into the game · FSR 3".into(),
                    gpu_supported: true,
                    gpu_note: "Supported on this GPU.".into(),
                    anti_cheat_verdict: "safe".into(),
                    anti_cheat_note: "Safe: built into the game.".into(),
                    auto_enable: false,
                    latency_note: "Lowest latency: uses game motion vectors.".into(),
                    selectable: true,
                    active: true,
                    ..Default::default()
                }],
                ..Default::default()
            }),
            frame_gen_menu_open: false,
            frame_gen_busy: false,
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
            store_ext: crate::store_page::StoreExtraState::default(),
            setup: SetupFlow::default(),
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
            wp4: crate::settings_state::SettingsUiState::default(),
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
            palette_needs_focus: false,
            palette_query: TextEdit::default(),
            palette_index: 0,
            visible_limit: 96,
            image_cache: None,
            remote_cache_dir: remote_art::default_cache_dir(),
            images: RefCell::new(ImageLruCache::default()),
            last_scale_bucket: Cell::new(0),
            room_left_at: HashMap::new(),
            discord_dirty_while_closed: false,
            remote_inflight: RefCell::new(HashSet::new()),
            video_router: VideoFrameRouter::new(),
            events: Rc::new(EventRegistry::with_defaults()),
            chrome: ShellChromeState::default(),
            downloads: DownloadsState::default(),
            island: IslandState::default(),
            whats_new: WhatsNewState::default(),
            boot: BootState::default(),
            key_modal: KeyModalState::default(),
            notice_runtime: NoticeRuntimeState::default(),
            palette_extras: PaletteExtrasState::default(),
            #[cfg(windows)]
            rpc: None,
            #[cfg(windows)]
            handle: None,
            #[cfg(windows)]
            inflight_requests: HashMap::new(),
            #[cfg(windows)]
            inflight_discord_channel: None,
        };
        crate::library_state::register_library_wp2_events(&mut s);
        s.selected_game_id = s.home_deck_games().first().map(|g| g.id.clone());
        s
    }

    pub fn for_host_pipe() -> Self {
        let mut s = Self {
            room: Room::Library,
            prev_room: Room::Library,
            room_dir: 1.0,
            mini_bar_slot: MiniBarSlot::Compact,
            library_all_games: false,
            library_search_open: false,
            library_search: TextEdit::default(),
            library_more_open: false,
            library_add_open: false,
            library_show_hidden: false,
            capture_menu_open: false,
            context_menu: None,
            open_dropdown: None,
            game_display_mode: "borderless".into(),
            deck_scroll: Rc::new(Cell::new(ScrollPos::default())),
            deck_scroll_x: 0.0,
            store_shelf_scrolls: std::array::from_fn(|_| Rc::new(Cell::new(ScrollPos::default()))),
            store_shelf_x: [0.0; 4],
            filters: Vec::new(),
            library_filter_open: false,
            lib: crate::library_state::LibraryExtraState::default(),
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
            game_page: crate::game_actions::GamePageState::new(),
            game_frame_gen: None,
            frame_gen_menu_open: false,
            frame_gen_busy: false,
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
            store_ext: crate::store_page::StoreExtraState::default(),
            setup: SetupFlow::default(),
            gamepad_connected: false,
            input_mode: InputMode::Pointer,
            settings: LauncherSettings::default(),
            pc: SettingsAndPcState::empty_for_host(),
            wp4: crate::settings_state::SettingsUiState::default(),
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
            palette_needs_focus: false,
            palette_query: TextEdit::default(),
            palette_index: 0,
            visible_limit: 96,
            image_cache: None,
            remote_cache_dir: remote_art::default_cache_dir(),
            images: RefCell::new(ImageLruCache::default()),
            last_scale_bucket: Cell::new(0),
            room_left_at: HashMap::new(),
            discord_dirty_while_closed: false,
            remote_inflight: RefCell::new(HashSet::new()),
            video_router: VideoFrameRouter::new(),
            events: Rc::new(EventRegistry::with_defaults()),
            chrome: ShellChromeState::default(),
            downloads: DownloadsState::default(),
            island: IslandState::default(),
            whats_new: WhatsNewState::for_host_pipe(),
            boot: BootState::default(),
            key_modal: KeyModalState::default(),
            notice_runtime: NoticeRuntimeState::default(),
            palette_extras: PaletteExtrasState::default(),
            #[cfg(windows)]
            rpc: None,
            #[cfg(windows)]
            handle: None,
            #[cfg(windows)]
            inflight_requests: HashMap::new(),
            #[cfg(windows)]
            inflight_discord_channel: None,
        };
        crate::library_state::register_library_wp2_events(&mut s);
        s
    }

    pub fn clock_secs_now(&self) -> f64 {
        self.clock_secs
    }

    pub fn arm_confirm_key(&mut self, key: &str, tx: &mut Tx) {
        const ARMED_SECS: f64 = 4.0;
        let deadline = self.clock_secs + ARMED_SECS;
        self.armed_confirm = Some((key.to_string(), deadline));
        tx.wake_after(Duration::from_secs_f64(ARMED_SECS));
    }

    pub fn install_waker(&mut self, waker: Waker) {
        self.store_ext.waker = Some(waker.clone());
        let budget = self.images.borrow().max_bytes();
        self.image_cache = Some(ImageCache::with_budget(waker, budget));
    }

    pub fn clock_now(&self) -> f64 {
        self.clock_secs
    }

    pub fn sync_budget_for_scale(&self, scale: f32) {
        let bucket = scale_bucket(scale);
        if self.last_scale_bucket.get() != bucket {
            self.last_scale_bucket.set(bucket);
            let max_bytes = self.images.borrow_mut().set_budget_for_scale(scale);
            if let Some(ref ic) = self.image_cache {
                ic.set_budget(max_bytes);
            }
        }
    }

    pub fn cache_room_for(room: Room) -> CacheRoom {
        match room {
            Room::Library => CacheRoom::Library,
            Room::Store => CacheRoom::Store,
            Room::Discord => CacheRoom::Discord,
            Room::Music => CacheRoom::Music,
            Room::Devices | Room::ExoAi | Room::Apps => CacheRoom::Pc,
            Room::Settings => CacheRoom::Global,
        }
    }

    pub fn current_cache_room(&self) -> CacheRoom {
        Self::cache_room_for(self.room)
    }

    #[cfg(test)]
    pub fn cache_stats(&self) -> (usize, usize, usize, usize) {
        let mut images = self.images.borrow_mut();
        images.refresh_sizes_and_evict();
        let (retained_count, retained_bytes) = self
            .image_cache
            .as_ref()
            .map(|ic| (ic.retained_count(), ic.retained_bytes()))
            .unwrap_or((0, 0));
        (
            images.len(),
            images.total_bytes(),
            retained_count,
            retained_bytes,
        )
    }

    pub fn preload_visible_covers_sync(&self, scale: f32) {
        let mut images: Vec<Image> = Vec::new();
        if self.room == Room::Library
            && self.art_base.covers_dir.is_some()
            && !self.games.is_empty()
        {
            if let Some(sel) = self.open_game().or_else(|| self.selected_game()) {
                if let Some(img) = self.hero_image(sel, scale) {
                    images.push(img);
                }
                if let Some(img) = self.logo_image(sel, scale) {
                    images.push(img);
                }
            }
            let mut preload_ids: Vec<&Game> = self.filtered_games().into_iter().take(60).collect();
            for folder in &self.settings.library_folders {
                for gid in folder.games.iter().take(4) {
                    if let Some(g) = self.games.iter().find(|g| g.id == *gid)
                        && !preload_ids.iter().any(|existing| existing.id == g.id)
                    {
                        preload_ids.push(g);
                    }
                }
            }
            images.extend(
                preload_ids
                    .iter()
                    .filter_map(|g| self.cover_image(g, scale)),
            );
            for img in &images {
                let _ = img.wait();
            }
            let retry: Vec<Image> = preload_ids
                .iter()
                .filter_map(|g| self.cover_image(g, scale))
                .collect();
            images.extend(retry);
        }
        if self.room == Room::Store {
            if let Some(ref home) = self.store_home {
                for (i, h) in home.hero.iter().take(6).enumerate() {
                    if i == 0 {
                        let logo = StoreArt::steam_url(&h.app_id, "logo");
                        if let Some(img) =
                            self.remote_image_in_room(&logo, 640.0, scale, CacheRoom::Store)
                        {
                            images.push(img);
                        }
                    }
                    if let Some(a) = h.art.as_ref() {
                        if i == 0
                            && let Some(u) = a.best_hero()
                            && let Some(img) =
                                self.remote_image_in_room(u, 1440.0, scale, CacheRoom::Store)
                        {
                            images.push(img);
                        }
                        if let Some(u) = a
                            .capsule
                            .as_deref()
                            .or(a.header.as_deref())
                            .or_else(|| a.best_hero())
                            && let Some(img) =
                                self.remote_image_in_room(u, 360.0, scale, CacheRoom::Store)
                        {
                            images.push(img);
                        }
                    }
                }
                for shelf in home.shelves.iter().take(2) {
                    for tile in shelf.items.iter().take(12) {
                        let u = tile
                            .art
                            .as_ref()
                            .and_then(|a| a.best_poster())
                            .map(ToString::to_string)
                            .unwrap_or_else(|| StoreArt::steam_url(&tile.app_id, "poster"));
                        if let Some(img) =
                            self.remote_image_in_room(&u, 360.0, scale, CacheRoom::Store)
                        {
                            images.push(img);
                        }
                    }
                }
            }
            for tile in self.store_ext.wishlist.iter().take(12) {
                let u = tile
                    .art
                    .as_ref()
                    .and_then(|a| a.best_poster())
                    .map(ToString::to_string)
                    .unwrap_or_else(|| StoreArt::steam_url(&tile.app_id, "poster"));
                if let Some(img) = self.remote_image_in_room(&u, 360.0, scale, CacheRoom::Store) {
                    images.push(img);
                }
            }
            if let Some(ref hits) = self.store_search_results {
                for tile in hits.iter().take(12) {
                    let u = tile
                        .art
                        .as_ref()
                        .and_then(|a| a.best_poster())
                        .map(ToString::to_string)
                        .unwrap_or_else(|| StoreArt::steam_url(&tile.app_id, "poster"));
                    if let Some(img) = self.remote_image_in_room(&u, 360.0, scale, CacheRoom::Store)
                    {
                        images.push(img);
                    }
                }
            }
            if let Some(ref g) = self.store_game {
                let logo = StoreArt::steam_url(&g.app_id, "logo");
                if let Some(img) = self.remote_image_in_room(&logo, 640.0, scale, CacheRoom::Store)
                {
                    images.push(img);
                }
                for m in g.media.iter().take(4) {
                    if let Some(img) =
                        self.remote_image_in_room(&m.thumb, 1440.0, scale, CacheRoom::Store)
                    {
                        images.push(img);
                    }
                    if let Some(img) =
                        self.remote_image_in_room(&m.thumb, 320.0, scale, CacheRoom::Store)
                    {
                        images.push(img);
                    }
                }
                for (i, shot) in g.screenshots.iter().take(8).enumerate() {
                    if i == 0
                        && let Some(img) =
                            self.remote_image_in_room(shot, 1440.0, scale, CacheRoom::Store)
                    {
                        images.push(img);
                    }
                    let small = crate::store_media::thumb_of_screenshot(shot);
                    if let Some(img) =
                        self.remote_image_in_room(&small, 320.0, scale, CacheRoom::Store)
                    {
                        images.push(img);
                    }
                }
            }
        }
        if self.room == Room::Devices {
            for url in self.pc.device_images.values() {
                if let Some(img) = self.device_image(url, scale) {
                    images.push(img);
                }
            }
        }
        if self.room == Room::Apps {
            for app_id in self.pc.app_icons.keys().take(48) {
                if let Some(img) = self.app_icon_image(app_id) {
                    images.push(img);
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

    pub fn notice_clock(&self) -> f64 {
        self.clock_secs
    }

    pub fn clock_secs(&self) -> f64 {
        self.clock_secs
    }

    pub fn alloc_notice_id(&mut self) -> u64 {
        let id = self.next_notice_id;
        self.next_notice_id = self.next_notice_id.saturating_add(1);
        id
    }
    pub fn switch_room(&mut self, room: Room) {
        self.record_room_history(room);
        self.switch_room_without_history(room);
    }

    pub fn switch_room_without_history(&mut self, room: Room) {
        let prev_room = self.room;
        let prev_cache_room = Self::cache_room_for(self.room);
        let next_cache_room = Self::cache_room_for(room);
        if prev_cache_room != next_cache_room {
            if prev_cache_room != CacheRoom::Global {
                self.room_left_at.insert(prev_cache_room, self.clock_secs);
            }
            self.room_left_at.remove(&next_cache_room);
        }
        if room != self.room {
            self.room_dir = if room.tab_index() >= self.room.tab_index() {
                1.0
            } else {
                -1.0
            };
        }
        if self.room == Room::Devices && room != Room::Devices {
            if self.pc.mouse_panel_open {
                self.close_mouse_panel();
            }
            self.stop_controller_watch();
        }
        if self.room == Room::Discord && room != Room::Discord && self.discord.signin.is_some() {
            self.cancel_discord_signin();
        }
        if self.room == Room::Store && room != Room::Store {
            self.store_on_room_leave();
        }
        if self.room == Room::ExoAi && room != Room::ExoAi {
            self.notify_shaders_visibility(false);
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
        self.library_add_open = false;
        self.lib.gifts_open = false;
        if room != Room::Library {
            self.exit_select_mode();
            self.close_folder_panel();
            self.lib.folder_sheet = None;
            self.lib.folder_cropper = None;
        }
        self.context_menu = None;
        self.open_dropdown = None;
        self.chrome.mini_volume_open = false;
        self.island.person_menu = None;
        if self.media.captures_open {
            self.close_captures();
        }
        match room {
            Room::Library => self.on_library_room_enter(),
            Room::Store => {
                if !self.store_loading {
                    self.load_store_home(false);
                }
                self.load_store_wishlist();
                self.store_on_room_enter();
            }
            Room::Discord => {
                if self.discord_dirty_while_closed {
                    self.discord_dirty_while_closed = false;
                    self.discord.loaded = false;
                }
                self.load_discord_room();
            }
            Room::Music => self.load_music_room(),
            Room::Settings => {
                self.load_settings_room();
                self.wp4_load_settings_all();
            }
            Room::Devices => self.load_pc_tab(crate::pc_state::PcTab::Devices),
            Room::ExoAi => self.load_pc_tab(crate::pc_state::PcTab::ExoAi),
            Room::Apps => self.load_pc_tab(crate::pc_state::PcTab::Apps),
        }
        self.on_room_switched(prev_room, room);
    }

    pub fn open_context_menu(&mut self, target: ContextMenuTarget, x: f32, y: f32) {
        self.open_context_menu_anchored(target, x, y, None);
    }

    pub fn open_context_menu_at_element(&mut self, target: ContextMenuTarget, key: &str) {
        self.open_context_menu_anchored(target, 0.0, 0.0, Some(key.to_string()));
    }

    fn open_context_menu_anchored(
        &mut self,
        target: ContextMenuTarget,
        x: f32,
        y: f32,
        anchor_key: Option<String>,
    ) {
        self.capture_menu_open = false;
        self.library_more_open = false;
        self.library_add_open = false;
        self.open_dropdown = None;
        self.discord.status_menu_open = matches!(target, ContextMenuTarget::DiscordStatusMenu);
        if let ContextMenuTarget::GameCard(ref gid) = target
            && self
                .games
                .iter()
                .any(|g| g.id == *gid && g.store == "riot" && g.installed && !g.is_running)
        {
            self.load_riot_accounts();
        }
        if let ContextMenuTarget::DiscordMember {
            ref user_id,
            guild_id: Some(ref gid),
            ..
        } = target
        {
            let uid = user_id.clone();
            let gid = gid.clone();
            self.fetch_discord_person(&gid, &uid);
        }
        self.context_menu = Some(ContextMenuState {
            target,
            x,
            y,
            open_submenu: None,
            anchor_key,
        });
    }

    pub fn close_context_menu(&mut self) {
        self.context_menu = None;
        self.discord.status_menu_open = false;
    }

    pub fn toggle_library_add(&mut self) {
        let next = !self.library_add_open;
        self.capture_menu_open = false;
        self.library_more_open = false;
        self.context_menu = None;
        self.open_dropdown = None;
        self.chrome.mini_volume_open = false;
        self.island.person_menu = None;
        self.library_add_open = next;
    }

    pub fn toggle_hide_game(&mut self, id: &str) {
        if let Some(pos) = self
            .settings
            .hidden_games
            .iter()
            .position(|g| g.eq_ignore_ascii_case(id))
        {
            self.settings.hidden_games.remove(pos);
            self.push_notice("Game unhidden", NoticeTone::Neutral);
        } else {
            self.settings.hidden_games.push(id.to_string());
            self.push_notice("Game hidden", NoticeTone::Neutral);
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let hidden = self.settings.hidden_games.clone();
            rpc.call::<_, LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "hiddenGames": hidden }),
                move |res| {
                    if let Ok(set) = res {
                        handle.update(move |s, _| s.settings = set);
                    }
                },
            );
        }
    }

    pub fn toggle_dropdown(&mut self, which: OpenDropdown) {
        self.capture_menu_open = false;
        self.library_more_open = false;
        self.library_add_open = false;
        self.context_menu = None;
        if self.open_dropdown == Some(which) {
            self.open_dropdown = None;
        } else {
            self.open_dropdown = Some(which);
        }
    }

    pub fn close_dropdown(&mut self) {
        self.open_dropdown = None;
    }

    pub fn set_text_size(&mut self, size: &str) {
        let normalized = match size {
            "small" | "large" => size,
            _ => "normal",
        };
        self.settings.text_size = Some(normalized.to_string());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "textSize": normalized }),
                move |res| {
                    if let Ok(set) = res {
                        handle.update(move |s, _| s.settings = set);
                    }
                },
            );
        }
    }

    pub fn set_music_service(&mut self, id: &str) {
        let svc = crate::media_state::MusicService::from_pref(Some(id));
        self.media.music_service = svc;
        let svc_id = svc.id();
        self.settings
            .ui_prefs
            .insert("musicService".into(), serde_json::json!(svc_id));
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, LauncherSettings>(
                METHOD_SETTINGS_SET,
                &json!({ "uiPrefs": { "musicService": svc_id } }),
                move |res| {
                    if let Ok(set) = res {
                        handle.update(move |s, _| {
                            s.settings = set;
                            s.sync_music_service_from_settings();
                        });
                    }
                },
            );
        }
    }

    pub fn now_secs(&self) -> f64 {
        self.clock_secs
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
                .home_deck_games()
                .first()
                .map(|g| g.id.clone())
                .or_else(|| self.games.first().map(|g| g.id.clone()));
        }
        if self.notice.as_deref() == Some("Connecting to Exo host…") {
            self.notice = None;
        }
        if self.room == Room::Store && self.store_home.is_none() && !self.store_loading {
            self.load_store_home(false);
        }
        self.schedule_upscaler_startup_work();
    }

    pub fn apply_art_base(&mut self, base: ArtBase) {
        self.art_base = base;
        self.images.borrow_mut().clear();
    }

    pub fn apply_settings(&mut self, settings: LauncherSettings) {
        let tour_seen = settings.onboarding_complete.unwrap_or(false)
            || settings
                .ui_prefs
                .get("tourSeen")
                .is_some_and(|v| v.as_bool().unwrap_or(false) || v.as_i64().unwrap_or(0) >= 1);
        let raw = settings
            .ui_prefs
            .get("setupProgress")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        self.settings = settings;
        if let Some(mode) = self.settings.sort_mode.as_deref() {
            self.sort = LibrarySort::from_id(mode);
        }
        if !self.setup.loaded && self.status == HostStatus::Connected {
            self.setup.loaded = true;
            if raw.is_empty() && !tour_seen {
                self.setup = SetupFlow::first_run();
                self.setup_enter_stage();
            } else {
                self.setup.apply_pref(&raw);
            }
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
        let playing = if self.filters.contains(&LibraryChip::Friends) {
            friends_playing(&self.games, &self.discord.friends)
        } else {
            HashSet::new()
        };
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
        let now_ms = now_unix_ms();
        let mut out: Vec<&Game> = self
            .games
            .iter()
            .filter(|g| is_shelf_game(g, transfer_id, show_tools))
            .filter(|g| chips_match(&self.filters, g, now_ms, &playing))
            .collect();
        let hidden = &self.settings.hidden_games;
        let show_hidden = self.library_show_hidden;
        out.retain(|g| {
            let is_hidden = hidden.iter().any(|h| h.eq_ignore_ascii_case(&g.id));
            if show_hidden { is_hidden } else { !is_hidden }
        });
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
            LibrarySort::Installed => b
                .installed
                .cmp(&a.installed)
                .then_with(|| cmp_title_base(&a.title, &b.title)),
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
            LibrarySort::Store => a
                .store
                .to_lowercase()
                .cmp(&b.store.to_lowercase())
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
        self.sync_budget_for_scale(scale);
        let dim = bucket_dim(320.0);
        let mut waiting_remote = false;
        for cand in game.portrait_art_candidates() {
            if cand.url.starts_with(COVER_CACHE_ORIGIN) {
                let key = format!("cov-url:{}", cand.url);
                let cached = self.images.borrow_mut().get(&key, Some(cache));
                let img = match cached {
                    Some(existing) => existing,
                    None => {
                        let loaded = self
                            .art_base
                            .resolve_local_cover(&cand.url)
                            .map(|path| cache.file_capped(path, dim, scale));
                        self.images.borrow_mut().insert(
                            key,
                            loaded.clone(),
                            CacheRoom::Library,
                            dim,
                            scale,
                        );
                        loaded
                    }
                };
                if let Some(ready) = img.filter(|i| i.error().is_none()) {
                    return Some(ready);
                }
            } else if cand.url.starts_with("https://") && !waiting_remote {
                let canonical = remote_art::canonical_cache_key(&cand.url);
                let remote_key = format!("rem:{canonical}@{dim:.0}");
                if self.images.borrow().is_failed(&remote_key) {
                    continue;
                }
                if let Some(img) =
                    self.remote_image_in_room(&cand.url, dim, scale, CacheRoom::Library)
                    && img.error().is_none()
                {
                    return Some(img);
                }
                waiting_remote = true;
            }
        }
        let san_key = format!("cov-san:{}#{}", game.id, game.art_revision.unwrap_or(0));
        let sanitized = if let Some(existing) = self.images.borrow_mut().get(&san_key, Some(cache))
        {
            existing
        } else {
            let loaded = self
                .art_base
                .resolve_local_sanitized_portrait(game)
                .map(|path| cache.file_capped(path, dim, scale));
            self.images.borrow_mut().insert(
                san_key,
                loaded.clone(),
                CacheRoom::Library,
                dim,
                scale,
            );
            loaded
        };
        if let Some(ok_img) = sanitized.filter(|i| i.error().is_none()) {
            return Some(ok_img);
        }
        self.hero_image_direct(game, scale)
            .filter(|i| i.error().is_none())
    }

    fn hero_image_direct(&self, game: &Game, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        self.sync_budget_for_scale(scale);
        let dim = bucket_dim(1440.0);
        let key = format!("hero:{}#{}", game.id, game.art_revision.unwrap_or(0));
        if let Some(existing) = self.images.borrow_mut().get(&key, Some(cache)) {
            if let Some(ready) = existing.filter(|i| i.error().is_none()) {
                return Some(ready);
            }
        } else {
            let loaded = self
                .art_base
                .resolve_local_hero(game)
                .map(|path| cache.file_capped(path, dim, scale));
            self.images
                .borrow_mut()
                .insert(key, loaded.clone(), CacheRoom::Library, dim, scale);
            if let Some(ready) = loaded.filter(|i| i.error().is_none()) {
                return Some(ready);
            }
        }
        if let Some(url) = game.hero_url.as_deref()
            && url.starts_with("https://")
            && !url.contains(".exo-launcher.local")
            && let Some(img) = self.remote_image_in_room(url, dim, scale, CacheRoom::Library)
            && img.error().is_none()
        {
            return Some(img);
        }
        if let Some(url) = game.steam_hero_url()
            && let Some(img) = self.remote_image_in_room(&url, dim, scale, CacheRoom::Library)
            && img.error().is_none()
        {
            return Some(img);
        }
        None
    }

    pub fn hero_image(&self, game: &Game, scale: f32) -> Option<Image> {
        self.hero_image_direct(game, scale)
            .or_else(|| self.cover_image(game, scale))
    }

    pub fn logo_image(&self, game: &Game, scale: f32) -> Option<Image> {
        let url = game.steam_logo_url()?;
        self.remote_image_in_room(&url, 640.0, scale, CacheRoom::Library)
    }

    pub fn device_image(&self, url: &str, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        self.sync_budget_for_scale(scale);
        let dim = bucket_dim(640.0);
        let key = format!("dev:{url}");
        if let Some(existing) = self.images.borrow_mut().get(&key, Some(cache)) {
            return existing;
        }
        let loaded = self
            .art_base
            .resolve_device_image(url)
            .map(|path| cache.file_capped(path, dim, scale));
        self.images
            .borrow_mut()
            .insert(key, loaded.clone(), CacheRoom::Pc, dim, scale);
        loaded
    }

    pub fn app_icon_image(&self, app_id: &str) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        let dim = bucket_dim(128.0);
        let key = format!("appicon:{app_id}");
        if let Some(existing) = self.images.borrow_mut().get(&key, Some(cache)) {
            return existing;
        }
        let data_url = self.pc.app_icons.get(app_id)?;
        let b64 = data_url.split_once("base64,").map(|(_, b)| b)?;
        let bytes = exo_rpc::base64::decode(b64)?;
        let loaded = Some(cache.load_capped(bytes, dim, 1.0));
        self.images
            .borrow_mut()
            .insert(key, loaded.clone(), CacheRoom::Pc, dim, 1.0);
        loaded
    }

    pub fn capture_thumb_image(&self, url: &str, scale: f32) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        self.sync_budget_for_scale(scale);
        let dim = bucket_dim(320.0);
        let key = format!("cap:{url}");
        if let Some(existing) = self.images.borrow_mut().get(&key, Some(cache)) {
            return existing;
        }
        let loaded = self
            .art_base
            .resolve_capture_thumb(url)
            .map(|path| cache.file_capped(path, dim, scale));
        self.images
            .borrow_mut()
            .insert(key, loaded.clone(), CacheRoom::Library, dim, scale);
        loaded
    }

    pub fn remote_image(&self, url: &str, scale: f32) -> Option<Image> {
        self.remote_image_sized(url, 360.0, scale)
    }

    pub fn remote_image_sized(&self, url: &str, max_dim: f32, scale: f32) -> Option<Image> {
        self.remote_image_in_room(url, max_dim, scale, self.current_cache_room())
    }

    pub fn remote_image_in_room(
        &self,
        url: &str,
        max_dim: f32,
        scale: f32,
        room: CacheRoom,
    ) -> Option<Image> {
        let cache = self.image_cache.as_ref()?;
        self.sync_budget_for_scale(scale);
        let dim = bucket_dim(max_dim);
        let canonical = remote_art::canonical_cache_key(url);
        let cache_key = format!("rem:{canonical}@{dim:.0}");
        if let Some(existing) = self.images.borrow_mut().get(&cache_key, Some(cache)) {
            return existing;
        }
        let dir = self.remote_cache_dir.as_ref()?;
        remote_art::parse_allowed_https(url)?;
        let path = remote_art::cache_path_for(dir, url);
        if path.is_file() {
            let img = cache.file_capped(path, dim, scale);
            self.images
                .borrow_mut()
                .insert(cache_key, Some(img.clone()), room, dim, scale);
            return Some(img);
        }
        #[cfg(windows)]
        if let Some(handle) = self.handle.clone() {
            let mut inflight = self.remote_inflight.borrow_mut();
            if inflight.len() < 16 && inflight.insert(canonical.clone()) {
                let url_owned = url.to_string();
                let inflight_key = canonical;
                let key_owned = cache_key;
                let dir_owned = dir.clone();
                std::thread::Builder::new()
                    .name("exo-art-fetch".into())
                    .spawn(move || {
                        let fetched = remote_art::fetch_cached_image(&url_owned, &dir_owned);
                        handle.update_if(move |s, _| {
                            s.remote_inflight.borrow_mut().remove(&inflight_key);
                            let room_active =
                                room == CacheRoom::Global || s.current_cache_room() == room;
                            if let (Some(p), Some(ic)) = (fetched, s.image_cache.as_ref()) {
                                if room_active {
                                    let img = ic.file_capped(p, dim, scale);
                                    s.images.borrow_mut().insert(
                                        key_owned,
                                        Some(img),
                                        room,
                                        dim,
                                        scale,
                                    );
                                    return true;
                                }
                                return false;
                            }
                            s.images
                                .borrow_mut()
                                .insert(key_owned, None, room, dim, scale);
                            false
                        });
                    })
                    .ok();
            }
        }
        None
    }

    pub fn current_playback_art(&self, scale: f32) -> Option<Image> {
        if let Some(ref img) = self.media.current_art {
            return Some(img.clone());
        }
        let url = self
            .media
            .playback
            .as_ref()
            .and_then(|p| p.artwork_url.as_deref())
            .filter(|u| !u.is_empty())?;
        self.remote_image_in_room(url, 360.0, scale, CacheRoom::Global)
    }

    pub fn apply_music_playback(&mut self, pb: Option<MusicPlayback>) -> bool {
        let Some(mut next) = pb else {
            let had = self.media.playback.take().is_some();
            self.media.current_art_key = None;
            self.media.current_art = None;
            return had;
        };
        let inline_data = next.artwork_data_url.take();
        let allowed_url = next
            .artwork_url
            .as_deref()
            .filter(|u| !u.is_empty() && remote_art::parse_allowed_https(u).is_some())
            .map(remote_art::canonical_cache_key);
        let art_key = if let Some(ref u) = allowed_url {
            format!("url:{u}")
        } else if inline_data.as_deref().is_some_and(|d| !d.is_empty()) {
            format!("inline:{}|{}|{}", next.title, next.artist, next.album)
        } else {
            String::new()
        };
        let track_art_changed = self.media.current_art_key.as_deref() != Some(art_key.as_str());
        if track_art_changed {
            self.media.current_art_key = (!art_key.is_empty()).then_some(art_key);
            self.media.current_art = None;
            if allowed_url.is_none()
                && let Some(ref data_url) = inline_data
                && let Some(cache) = self.image_cache.as_ref()
                && let Some(b64) = data_url.split_once("base64,").map(|(_, b)| b)
                && let Some(bytes) = exo_rpc::base64::decode(b64)
            {
                self.media.current_art = Some(cache.load_capped(bytes, 360.0, 1.5));
            }
        }
        let redraw = match self.media.playback.as_ref() {
            None => true,
            Some(prev) => {
                let meta_changed = prev.provider != next.provider
                    || prev.source != next.source
                    || prev.status != next.status
                    || prev.title != next.title
                    || prev.artist != next.artist
                    || prev.album != next.album
                    || prev.duration_ms != next.duration_ms
                    || prev.can_play != next.can_play
                    || prev.can_pause != next.can_pause
                    || prev.can_next != next.can_next
                    || prev.can_prev != next.can_prev;
                let progress = |p: &MusicPlayback| match (p.position_ms, p.duration_ms) {
                    (Some(pos), Some(dur)) if dur > 0 => (pos as f32 / dur as f32).clamp(0.0, 1.0),
                    _ => 0.0,
                };
                if meta_changed || track_art_changed {
                    true
                } else if !next.is_playing() {
                    false
                } else if self.room == Room::Music {
                    let prev_sec = prev.position_ms.unwrap_or(0) / 1000;
                    let next_sec = next.position_ms.unwrap_or(0) / 1000;
                    let prev_bar = (progress(prev) * 300.0).round() as i32;
                    let next_bar = (progress(&next) * 300.0).round() as i32;
                    prev_sec != next_sec || prev_bar != next_bar
                } else {
                    let prev_bar = (progress(prev) * 164.0).round() as i32;
                    let next_bar = (progress(&next) * 164.0).round() as i32;
                    prev_bar != next_bar
                }
            }
        };
        self.media.playback = Some(next);
        redraw
    }

    pub fn apply_host_event(&mut self, ev: HostEvent) -> bool {
        let registry = Rc::clone(&self.events);
        registry.dispatch(self, ev)
    }

    pub fn prune_inactive_rooms(&mut self, now: f64, rest: bool) -> bool {
        let ttl = if rest { 10.0 } else { 30.0 };
        let active = self.current_cache_room();
        let due: Vec<CacheRoom> = self
            .room_left_at
            .iter()
            .filter(|&(&room, &left_at)| room != active && now - left_at >= ttl)
            .map(|(&room, _)| room)
            .collect();
        if due.is_empty() {
            return false;
        }
        let mut images = self.images.borrow_mut();
        for room in due {
            self.room_left_at.remove(&room);
            images.evict_room(room);
            match room {
                CacheRoom::Discord => {
                    self.discord.chat.messages_by_channel.clear();
                    self.discord.chat.pins.clear();
                    self.discord.chat.typing_by_channel.clear();
                    self.discord.chat.search_results.clear();
                    self.discord.members_by_guild.clear();
                    self.discord.channels_by_guild.clear();
                    self.discord.loaded = false;
                    self.discord_dirty_while_closed = false;
                }
                CacheRoom::Music => {
                    self.media.music_page_data = None;
                    self.media.music_queue_data = None;
                    self.media.music_home_sections.clear();
                    self.media.music_recent.clear();
                    self.media.music_library.clear();
                }
                CacheRoom::Store => {
                    self.store_game = None;
                    self.store_offers = None;
                    self.store_search_results = None;
                }
                CacheRoom::Pc => {
                    self.pc.app_icons.clear();
                }
                CacheRoom::Library | CacheRoom::Global => {}
            }
        }
        drop(images);
        if let Some(ref ic) = self.image_cache {
            ic.evict_all();
        }
        true
    }

    pub fn on_rest(&mut self, _tx: &mut Tx) -> bool {
        self.prune_inactive_rooms(self.clock_secs, true);
        self.discord
            .chat
            .typing_by_channel
            .retain(|_, (_, at)| at.elapsed() < Duration::from_secs(10));
        self.images.borrow_mut().refresh_sizes_and_evict();
        if let Some(ref ic) = self.image_cache {
            ic.evict_all();
        }
        false
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
        self.frame_gen_menu_open = false;
        self.frame_gen_busy = false;
        self.game_props_tab = 0;
        self.uninstall_confirm = false;
        self.armed_confirm = None;
        self.exit_select_mode();
        self.game_page.selected_variant_id = None;
        if self.status != HostStatus::Standalone {
            self.game_frame_gen = None;
        }
        self.load_game_page_data(id);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let gid3 = id.to_string();
            let h3 = handle.clone();
            rpc.call::<_, GameFrameGen>(
                METHOD_GAME_FRAME_GEN,
                &json!({ "id": gid3 }),
                move |res| {
                    let fg = res.unwrap_or_else(|_| GameFrameGen {
                        ok: false,
                        best_route_id: "unavailable".into(),
                        selected_route_id: "unavailable".into(),
                        state: "unread".into(),
                        word: "Could not check".into(),
                        bad: true,
                        ..Default::default()
                    });
                    h3.update(move |s, _| {
                        if s.open_game_id.as_deref() == Some(gid3.as_str()) {
                            s.game_frame_gen = Some(fg);
                        }
                    });
                },
            );
        }
    }

    pub fn set_game_frame_gen(
        &mut self,
        id: &str,
        route: Option<&str>,
        xess_override: Option<&str>,
    ) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.frame_gen_busy = true;
            let gid = id.to_string();
            let mut body = serde_json::Map::new();
            body.insert("id".into(), json!(gid));
            if let Some(r) = route {
                body.insert("route".into(), json!(r));
            }
            if let Some(x) = xess_override {
                body.insert("xessOverride".into(), json!(x));
            }
            rpc.call::<_, GameFrameGen>(
                METHOD_GAME_FRAME_GEN,
                &serde_json::Value::Object(body),
                move |res| {
                    handle.update(move |s, _| {
                        s.frame_gen_busy = false;
                        if s.open_game_id.as_deref() == Some(gid.as_str()) {
                            if let Ok(fg) = res {
                                if !fg.routes.is_empty() {
                                    s.game_frame_gen = Some(fg);
                                } else if let Some(msg) = fg.message {
                                    s.notice = Some(msg);
                                }
                            } else {
                                s.notice = Some("Could not save frame generation route.".into());
                            }
                        }
                    });
                },
            );
            return;
        }
        if let Some(ref mut fg) = self.game_frame_gen {
            if let Some(r) = route
                && let Some(found) = fg.routes.iter().find(|rt| rt.id == r)
            {
                fg.selected_route_id = found.id.clone();
                fg.word = found.summary.clone();
            }
            if let Some(x) = xess_override
                && let Some(rt) = fg
                    .routes
                    .iter_mut()
                    .find(|rt| rt.kind == "driver_xess_override")
            {
                rt.choice_value = Some(x.to_string());
                rt.active = x != "game";
            }
        }
    }

    pub fn close_game_page_tx(&mut self, tx: &mut Tx) {
        tx.unclipped_flights();
        self.close_game_page();
    }

    pub fn close_game_page(&mut self) {
        self.open_game_id = None;
        self.props_open = false;
        self.frame_gen_menu_open = false;
        self.frame_gen_busy = false;
        self.uninstall_confirm = false;
        self.armed_confirm = None;
        self.game_page.pending_choice = None;
        self.game_page.selected_variant_id = None;
        self.game_page.move_poll_due = None;
    }

    pub fn close_store_game_tx(&mut self, tx: &mut Tx) {
        tx.unclipped_flights();
        self.store_close_game_full();
    }

    pub fn is_confirm_armed(&self, key: &str) -> bool {
        self.armed_confirm.as_ref().is_some_and(|(k, _)| k == key)
    }

    pub fn disarm_confirm(&mut self) -> bool {
        let was_armed = self.armed_confirm.take().is_some() || self.uninstall_confirm;
        self.uninstall_confirm = false;
        was_armed
    }

    #[cfg(test)]
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

    pub fn open_key_modal(&mut self, tx: &mut Tx) {
        self.key_modal_open = true;
        self.key_submitting = false;
        self.key_input.text.clear();
        self.key_input.sync_invariants();
        self.key_preview = None;
        self.reset_key_modal_fields();
        tx.focus("activate-key-input", false);
        self.preview_clipboard_key();
    }

    pub fn close_key_modal(&mut self) {
        self.key_modal_open = false;
        self.key_submitting = false;
    }

    pub fn load_store_home(&mut self, refresh: bool) {
        if self.status == HostStatus::Standalone {
            let home = demo_store_home();
            self.store_ensure_shelf_slots(home.shelves.len());
            self.store_home = Some(home);
            self.store_ext.home_error = None;
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.store_loading = true;
            if refresh {
                self.store_ext.home_refreshing = true;
            }
            let params = if refresh {
                json!({ "refresh": true })
            } else {
                json!({})
            };
            rpc.call::<_, StoreHome>(METHOD_STORE_HOME, &params, move |res| {
                handle.update(move |s, _| {
                    s.store_loading = false;
                    if refresh {
                        s.store_ext.home_refreshing = false;
                    }
                    match res {
                        Ok(home) if home.ok => {
                            let same_at = home.at.is_some()
                                && s.store_home.as_ref().and_then(|h| h.at.as_deref())
                                    == home.at.as_deref();
                            if !same_at {
                                s.store_ensure_shelf_slots(home.shelves.len());
                                s.store_home = Some(home);
                            }
                            s.store_ext.home_error = None;
                            if s.room == Room::Store && !s.store_has_page_open() {
                                if s.store_ext.room_visible_since.is_none() {
                                    s.store_ext.room_visible_since = Some(s.clock_now());
                                }
                                s.store_wake();
                            }
                        }
                        Ok(home) => {
                            s.store_ext.home_error = Some(
                                home.message
                                    .filter(|m| !m.is_empty())
                                    .unwrap_or_else(|| {
                                        "Exo couldn't reach the store. Try again.".into()
                                    }),
                            );
                        }
                        Err(_) => {
                            s.store_ext.home_error = Some(
                                "The store could not be reached. Check your connection and try again."
                                    .into(),
                            );
                        }
                    }
                });
            });
        }
    }

    pub fn open_store_game(&mut self, app_id: &str) {
        self.store_load_game_full(app_id);
    }

    pub fn close_store_game(&mut self) {
        self.store_close_game_full();
    }

    pub fn apply_gamepad_action(&mut self, action: GamepadAction, tx: &mut Tx) {
        if !matches!(action, GamepadAction::Connected(_)) {
            if self.chrome.gamepad_paused {
                return;
            }
            self.input_mode = InputMode::Gamepad;
        }
        match action {
            GamepadAction::Connected(on) => {
                self.gamepad_connected = on;
                if !on {
                    self.input_mode = InputMode::Pointer;
                }
            }
            GamepadAction::PrevTab => crate::gamepad::cycle_room_subtab(self, -1),
            GamepadAction::NextTab => crate::gamepad::cycle_room_subtab(self, 1),
            GamepadAction::Settings => self.toggle_settings(),
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
                if self.setup.beat.is_some() {
                    self.setup_beat_skip();
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
        if let Some(ref driver) = self.boot.driver_install
            && driver.visible()
        {
            if driver.working() {
                return true;
            }
            self.dismiss_driver_install();
            return true;
        }
        if self.whats_new.sheet_open {
            if self.whats_new.not_now && !self.whats_new.busy() {
                self.set_aside_update_sheet();
            }
            return true;
        }
        if self.whats_new.mode.is_some() {
            self.close_whats_new();
            return true;
        }
        if self.chrome.gamepad_paused {
            self.chrome.gamepad_paused = false;
            return true;
        }
        if self.disarm_confirm() {
            return true;
        }
        if self.island.person_menu.is_some() {
            self.island_close_person_menu();
            return true;
        }
        if self.chrome.mini_volume_open {
            self.chrome.mini_volume_open = false;
            return true;
        }
        if self.context_menu.is_some() {
            self.context_menu = None;
            return true;
        }
        if self.open_dropdown.is_some() {
            self.open_dropdown = None;
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
        if self.library_add_open {
            self.library_add_open = false;
            return true;
        }
        if self.library_filter_open {
            self.library_filter_open = false;
            return true;
        }
        if self.lib.gifts_open {
            self.lib.gifts_open = false;
            return true;
        }
        if self.lib.folder_cropper.is_some() {
            self.lib.folder_cropper = None;
            return true;
        }
        if self.lib.folder_sheet.is_some() {
            self.lib.folder_sheet = None;
            return true;
        }
        if self.lib.open_folder_id.is_some() {
            self.close_folder_panel();
            return true;
        }
        if self.lib.selecting {
            self.exit_select_mode();
            return true;
        }
        if self.pc.part_menu.is_some() {
            self.pc.part_menu = None;
            return true;
        }
        if self.downloads.open {
            self.close_downloads();
            return true;
        }
        if self.palette_open {
            self.close_palette();
            return true;
        }
        if self.wp4.report_open {
            self.wp4_close_report();
            return true;
        }
        if self.key_modal_open {
            self.close_key_modal();
            return true;
        }
        if self.setup.beat.is_some() {
            self.finish_tour();
            return true;
        }
        if self.props_open {
            self.props_open = false;
            return true;
        }
        if self.room == Room::Discord && self.discord.admin.dismiss_top() {
            return true;
        }
        if self.room == Room::Discord && self.discord.custom_status_open {
            self.discord.custom_status_open = false;
            return true;
        }
        if self.room == Room::Discord && self.discord.settings_open {
            self.close_discord_settings_clean();
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
        if self.pc.controller_ui.calibrate_dialog.open {
            self.close_calibrate_dialog();
            return true;
        }
        if self.pc.controller_ui.profile_editor.is_some() {
            if self
                .pc
                .controller_ui
                .profile_editor
                .as_ref()
                .is_some_and(|e| e.game_picker_open)
            {
                if let Some(ref mut ed) = self.pc.controller_ui.profile_editor {
                    ed.game_picker_open = false;
                }
            } else {
                self.pc.controller_ui.profile_editor = None;
            }
            return true;
        }
        if self.media.lightbox.is_open() {
            self.media.lightbox.close();
            return true;
        }
        if self.media.captures_ui.send_dialog.open {
            self.media.captures_ui.send_dialog.open = false;
            return true;
        }
        if self.media.snip_editor.is_some() {
            self.close_snip_editor();
            return true;
        }
        if self.media.clip_editor_open {
            self.media.clip_editor_open = false;
            return true;
        }
        if self.media.notes.open {
            self.close_notes_to_captures();
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
        if self.store_ext.lightbox_index.is_some() {
            self.store_close_lightbox();
            return true;
        }
        if self.store_has_page_open() {
            self.close_store_game();
            return true;
        }
        if self.room == Room::Devices && self.pc.mouse_panel_open {
            self.close_mouse_panel();
            return true;
        }
        if self.room == Room::Settings {
            self.toggle_settings();
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
        self.push_notice_inner(text.into(), tone, None, None)
    }

    pub fn push_notice_with_action(
        &mut self,
        text: impl Into<String>,
        tone: NoticeTone,
        action_label: impl Into<String>,
        action: NoticeAction,
    ) -> u64 {
        self.push_notice_inner(text.into(), tone, Some(action_label.into()), Some(action))
    }

    fn push_notice_inner(
        &mut self,
        text: String,
        tone: NoticeTone,
        action_label: Option<String>,
        action: Option<NoticeAction>,
    ) -> u64 {
        let id = self.next_notice_id;
        self.next_notice_id = self.next_notice_id.saturating_add(1);
        self.notices.push_back(NoticeItem {
            id,
            key_id: None,
            title: None,
            where_label: None,
            text: text.clone(),
            tone,
            sticky: false,
            lasting: false,
            count: 0,
            progress: None,
            action: match (action_label, action) {
                (Some(label), Some(NoticeAction::DownloadNewerUpscalers)) => {
                    Some(NoticeActionSpec {
                        label,
                        primary: false,
                        danger: false,
                        busy: false,
                        confirm: None,
                        kind: crate::notice::NoticeActionKind::DownloadNewerUpscalers {
                            notice_id: id,
                        },
                    })
                }
                _ => None,
            },
            on_open: None,
            avatar_url: None,
            avatar_initial: None,
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
            self.promote_waiting_notices();
            if self.notices.is_empty() {
                self.notice = None;
                self.notice_last_synced = None;
            }
            return true;
        }
        false
    }

    pub fn dismiss_newest_notice(&mut self) -> bool {
        if let Some(pos) = self.notices.iter().rposition(|n| !n.lasting) {
            self.notices.remove(pos);
            self.promote_waiting_notices();
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
            .filter(|n| !n.hovered && !n.dragging && !n.sticky && !n.lasting)
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

        if !self.room_left_at.is_empty() {
            self.prune_inactive_rooms(now, false);
            if let Some(&earliest) = self.room_left_at.values().min_by(|a, b| a.total_cmp(b)) {
                tx.wake_at(earliest + 30.0);
            }
        }

        if let Some((_, deadline)) = self.armed_confirm {
            if now >= deadline {
                self.disarm_confirm();
                tx.animated();
                changed = true;
            } else {
                tx.wake_at(deadline);
            }
        }

        self.tick_game_page_timers(now, tx);

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

        if self.whats_new.boot_hold_active {
            if now >= crate::whats_new::BOOT_HOLD_SECS {
                self.whats_new.boot_hold_active = false;
                changed = true;
            } else {
                tx.wake_at(crate::whats_new::BOOT_HOLD_SECS);
            }
        }

        if let Some(until) = self.key_modal.copied_until
            && now >= until
        {
            self.key_modal.copied_until = None;
            changed = true;
        }

        if self.notices.is_empty() {
            return changed;
        }

        for item in &mut self.notices {
            if item.hovered || item.dragging || item.sticky || item.lasting {
                item.last_tick = Some(now);
            } else {
                if let Some(prev) = item.last_tick {
                    item.remaining_secs -= (now - prev).max(0.0);
                }
                item.last_tick = Some(now);
            }
        }

        let before = self.notices.len();
        self.notices.retain(|item| {
            item.hovered
                || item.dragging
                || item.sticky
                || item.lasting
                || item.remaining_secs > 0.0
        });
        if self.notices.len() != before {
            self.promote_waiting_notices();
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
        self.palette_needs_focus = false;
        self.palette_index = 0;
        self.palette_query.text.clear();
        self.palette_query.sync_invariants();
        self.load_palette_extras();
        self.load_notes_list(None);
        self.sync_chrome_layout();
        tx.focus("palette-input", false);
    }

    pub fn close_palette(&mut self) {
        self.palette_open = false;
        self.palette_needs_focus = false;
        self.palette_index = 0;
        self.sync_chrome_layout();
    }

    pub fn palette_results(&self) -> Vec<PaletteItem> {
        let query = self.palette_query.text.trim().to_lowercase();
        let mut items = Vec::new();

        const PALETTE_ROOMS: [(Room, &[&str]); 8] = [
            (Room::Library, &[]),
            (Room::Store, &[]),
            (
                Room::Discord,
                &[
                    "home", "friends", "messages", "dms", "servers", "playing", "voice",
                ],
            ),
            (Room::Music, &[]),
            (
                Room::ExoAi,
                &[
                    "auto tune",
                    "specs",
                    "overclock",
                    "undervolt",
                    "autopilot",
                    "system tuning",
                    "shaders",
                    "updates",
                    "drivers",
                    "storage",
                ],
            ),
            (Room::Apps, &["uninstall", "programs", "bloat", "startup"]),
            (Room::Devices, &[]),
            (Room::Settings, &[]),
        ];

        for (room, keywords) in PALETTE_ROOMS {
            let label = room.label();
            let matched = query.is_empty()
                || label.to_lowercase().contains(&query)
                || keywords.iter().any(|k| k.contains(&query));
            if matched {
                items.push(PaletteItem {
                    id: format!("pal-room-{}", room.key()),
                    group: "Go to",
                    title: label.into(),
                    detail: None,
                    target: PaletteTarget::Room(room),
                });
            }
        }

        if !query.is_empty() {
            const PC_SECTIONS: [(&str, &str, &str, &[&str]); 13] = [
                (
                    "overview",
                    "This PC",
                    "Exo AI",
                    &[
                        "this pc",
                        "about",
                        "specs",
                        "processor",
                        "cpu",
                        "cores",
                        "graphics card",
                        "gpu",
                        "video memory",
                        "vram",
                        "memory",
                        "ram",
                        "windows version",
                        "windows update",
                        "build",
                        "best settings",
                    ],
                ),
                (
                    "drivers",
                    "Updates",
                    "Exo AI",
                    &[
                        "updates",
                        "update",
                        "check for updates",
                        "windows update",
                        "drivers",
                        "driver",
                        "chipset",
                        "bios",
                        "firmware",
                        "runtimes",
                        "redistributables",
                        "directx",
                        "vulkan",
                    ],
                ),
                (
                    "graphics",
                    "Graphics",
                    "Exo AI",
                    &[
                        "nvidia",
                        "radeon",
                        "intel",
                        "driver profile",
                        "g-sync",
                        "upscalers",
                        "dlss",
                        "fsr",
                        "xess",
                    ],
                ),
                (
                    "shaders",
                    "Shaders",
                    "Exo AI",
                    &["shaders", "compile", "shader cache"],
                ),
                (
                    "control",
                    "Control Panel",
                    "Exo AI",
                    &[
                        "control panel",
                        "nvidia control panel",
                        "nvcp",
                        "3d settings",
                        "adrenalin",
                        "graphics command center",
                    ],
                ),
                (
                    "display",
                    "Display",
                    "Exo AI",
                    &[
                        "screen",
                        "monitor",
                        "refresh rate",
                        "hz",
                        "hdr",
                        "color",
                        "scaling",
                        "super resolution",
                        "digital vibrance",
                    ],
                ),
                (
                    "board",
                    "Motherboard",
                    "Exo AI",
                    &[
                        "motherboard",
                        "bios",
                        "uefi",
                        "recovery",
                        "advanced startup",
                        "secure boot",
                        "tpm",
                        "memory integrity",
                    ],
                ),
                (
                    "network",
                    "Network",
                    "Exo AI",
                    &[
                        "network",
                        "internet",
                        "ethernet",
                        "wi-fi",
                        "wifi",
                        "wireless",
                        "network adapter",
                        "ping",
                        "latency",
                        "packet loss",
                        "background scan",
                    ],
                ),
                (
                    "autopilot",
                    "Autopilot",
                    "Exo AI",
                    &[
                        "autopilot",
                        "game optimizer",
                        "game booster",
                        "stutter",
                        "frame times",
                        "game mode",
                        "background apps",
                        "core parking",
                        "standby list",
                        "islc",
                        "dpc latency",
                        "vram",
                    ],
                ),
                (
                    "performance",
                    "Performance",
                    "Exo AI",
                    &[
                        "performance",
                        "power plan",
                        "gpu scheduling",
                        "resizable bar",
                        "rebar",
                        "msi mode",
                        "system tuning",
                        "hpet",
                        "page file",
                        "virtual memory",
                        "hvci",
                        "restore point",
                    ],
                ),
                (
                    "drives",
                    "Drives",
                    "Exo AI",
                    &[
                        "drives",
                        "storage",
                        "ssd",
                        "nvme",
                        "hard drive",
                        "disk",
                        "disk space",
                        "smart",
                        "trim",
                        "defrag",
                        "write cache",
                        "directstorage fast path",
                    ],
                ),
                (
                    "compress",
                    "Compress games",
                    "Exo AI",
                    &[
                        "compress",
                        "compress games",
                        "compression",
                        "compactgui",
                        "compact os",
                        "free up space",
                        "save space",
                        "shrink games",
                    ],
                ),
                (
                    "apps",
                    "Apps",
                    "Apps",
                    &[
                        "apps",
                        "uninstall",
                        "programs",
                        "leftovers",
                        "clean up",
                        "junk",
                        "bloatware",
                        "startup",
                        "startup apps",
                        "autostart",
                        "temp files",
                    ],
                ),
            ];
            for (sec_id, title, detail, keywords) in PC_SECTIONS {
                if title.to_lowercase().contains(&query)
                    || detail.to_lowercase().contains(&query)
                    || keywords.iter().any(|k| k.contains(&query))
                {
                    items.push(PaletteItem {
                        id: format!("pal-pc-{sec_id}"),
                        group: "Go to",
                        title: title.into(),
                        detail: Some(detail.into()),
                        target: PaletteTarget::PcSection(sec_id),
                    });
                }
            }

            use crate::pc_state::SettingsSection;
            const SETTINGS_PAGES: [(SettingsSection, &str, &[&str]); 6] = [
                (
                    SettingsSection::General,
                    "General",
                    &[
                        "dark mode",
                        "amoled",
                        "pure black",
                        "text size",
                        "reduce motion",
                        "animation",
                        "startup",
                        "start with windows",
                        "window",
                    ],
                ),
                (
                    SettingsSection::Account,
                    "Library",
                    &[
                        "game stores",
                        "sign in",
                        "account",
                        "steam",
                        "epic",
                        "gog",
                        "amazon",
                        "riot",
                        "ea",
                        "ubisoft",
                        "battle.net",
                        "xbox",
                        "offline mode",
                        "soundtracks",
                        "shaders",
                    ],
                ),
                (
                    SettingsSection::Appearance,
                    "Downloads",
                    &[
                        "downloads",
                        "install folder",
                        "download hours",
                        "automatic updates",
                        "anti-cheat",
                        "dlss",
                        "fsr",
                        "xess",
                    ],
                ),
                (
                    SettingsSection::Graphics,
                    "In game",
                    &[
                        "in game",
                        "achievements",
                        "pop-ups",
                        "obs overlay url",
                        "fps counter",
                        "frame rate",
                        "overlay",
                        "gpu use",
                        "vram",
                        "ram",
                        "battery",
                    ],
                ),
                (
                    SettingsSection::Updates,
                    "Music",
                    &[
                        "music",
                        "music service",
                        "spotify",
                        "apple music",
                        "youtube music",
                        "lossless",
                        "jam",
                    ],
                ),
                (
                    SettingsSection::About,
                    "About",
                    &[
                        "about",
                        "version",
                        "updates",
                        "caches",
                        "what's new",
                        "changelog",
                        "account",
                        "privacy",
                        "app data",
                        "telemetry",
                        "crash reports",
                    ],
                ),
            ];
            for (sec, title, keywords) in SETTINGS_PAGES {
                if title.to_lowercase().contains(&query)
                    || "settings".contains(&query)
                    || keywords.iter().any(|k| k.contains(&query))
                {
                    items.push(PaletteItem {
                        id: format!("pal-set-{}", sec.key()),
                        group: "Go to",
                        title: title.into(),
                        detail: Some("Settings".into()),
                        target: PaletteTarget::SettingsSection(sec),
                    });
                }
            }
        }

        let mut candidates: Vec<&Game> = self
            .games
            .iter()
            .filter(|g| {
                g.id != "local:add" && (query.is_empty() || g.title.to_lowercase().contains(&query))
            })
            .collect();
        candidates.sort_by_key(|a| a.title.to_lowercase());

        let mut seen = std::collections::HashSet::new();
        for game in candidates.into_iter().take(50) {
            let norm = game.title.trim().to_lowercase();
            if !seen.insert(norm) {
                continue;
            }
            items.push(PaletteItem {
                id: format!("pal-game-{}", game.id),
                group: "Library",
                title: game.title.clone(),
                detail: None,
                target: PaletteTarget::OpenGame(game.id.clone()),
            });
        }

        let actions: [(&str, &str, &[&str], PaletteTarget); 9] = [
            (
                "pal-action-add-game",
                "Add a game",
                &["exe", "portable"],
                PaletteTarget::AddGame,
            ),
            (
                "pal-action-key",
                "Activate a key",
                &["redeem", "code"],
                PaletteTarget::ActivateKey,
            ),
            (
                "pal-action-refresh",
                "Refresh library",
                &["scan", "rescan"],
                PaletteTarget::RefreshLibrary,
            ),
            (
                "pal-action-tour",
                "Take the tour",
                &["guide", "walkthrough", "onboarding", "help", "intro"],
                PaletteTarget::StartTour,
            ),
            (
                "pal-action-setup",
                "Resume setup",
                &["onboarding", "welcome", "setup"],
                PaletteTarget::ResumeSetup,
            ),
            (
                "pal-action-update",
                "Check for updates",
                &["upgrade"],
                PaletteTarget::CheckUpdates,
            ),
            (
                "pal-action-report",
                "Report a problem",
                &["bug", "issue", "feedback", "support", "logs"],
                PaletteTarget::ReportProblem,
            ),
            (
                "pal-action-minimize",
                "Minimize window",
                &[],
                PaletteTarget::MinimizeWindow,
            ),
            (
                "pal-action-close",
                "Close window",
                &["quit", "exit"],
                PaletteTarget::CloseWindow,
            ),
        ];
        for (id, title, keywords, target) in actions {
            if query.is_empty()
                || title.to_lowercase().contains(&query)
                || keywords.iter().any(|k| k.contains(&query))
            {
                items.push(PaletteItem {
                    id: id.into(),
                    group: "Actions",
                    title: title.into(),
                    detail: None,
                    target,
                });
            }
        }

        self.append_palette_extras(&query, &mut items);

        items
    }

    pub fn execute_palette_target(&mut self, target: PaletteTarget, tx: &mut Tx) {
        self.close_palette();
        match target {
            PaletteTarget::Room(room) => self.switch_room(room),
            PaletteTarget::SettingsSection(sec) => {
                self.pc.settings_section = sec;
                self.switch_room(Room::Settings);
            }
            PaletteTarget::PcSection(sec) => {
                if sec == "apps" {
                    self.switch_room(Room::Apps);
                } else {
                    self.switch_room(Room::ExoAi);
                    match sec {
                        "storage" | "drives" | "compress" => {
                            self.pc.exo_ai_storage_open = true;
                            self.load_pc_drives();
                        }
                        "graphics" => {
                            self.pc.part_menu = Some(crate::pc_state::PcPartMenu::Graphics)
                        }
                        "display" => self.pc.part_menu = Some(crate::pc_state::PcPartMenu::Display),
                        _ => self.pc.exo_ai_storage_open = false,
                    }
                }
            }
            PaletteTarget::OpenGame(id) => self.open_game_page(&id),
            PaletteTarget::AddGame => {
                self.switch_room(Room::Library);
                self.library_all_games = true;
                self.open_key_modal(tx);
            }
            PaletteTarget::RefreshLibrary => {
                self.switch_room(Room::Library);
                self.refresh_library();
            }
            PaletteTarget::ActivateKey => self.open_key_modal(tx),
            PaletteTarget::StartTour => self.start_tour(),
            PaletteTarget::CheckUpdates => self.manual_check_update(),
            PaletteTarget::MinimizeWindow => tx.minimize(),
            PaletteTarget::CloseWindow => tx.close_window(),
            PaletteTarget::ResumeSetup => self.setup_resume(),
            PaletteTarget::ReportProblem => self.wp4_open_report(),
            other => self.execute_palette_extra(other),
        }
    }

    pub fn launch_game(&mut self, id: &str) {
        self.game_action(METHOD_GAME_LAUNCH, id, "Launching", "Launched");
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

    pub fn open_game_folder(&mut self, id: &str) {
        self.game_action(
            METHOD_GAME_OPEN_FOLDER,
            id,
            "Opening folder for",
            "Opened folder for",
        );
    }

    #[cfg(test)]
    pub fn uninstall_game(&mut self, id: &str) {
        self.disarm_confirm();
        self.game_action(
            exo_rpc::METHOD_GAME_UNINSTALL,
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
                            s.lib.library_error = None;
                            s.lib.refresh_error = None;
                            s.apply_library(lib);
                            s.notice = Some(format!("Library refreshed ({count} games)"));
                        }
                        Err(e) => {
                            let msg = e.to_string();
                            s.refreshing = false;
                            if s.games.is_empty() {
                                s.lib.library_error = Some(msg.clone());
                            } else {
                                s.lib.refresh_error = Some(msg.clone());
                            }
                            s.notice = Some(format!("Refresh failed: {msg}"));
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
        self.apply_music_playback(boot.music_playback.playback);
        self.discord.status = boot.discord_status;
        self.load_web_preferences();
        self.warm_library_on_startup();
        match self.room {
            Room::Discord if !self.discord.loaded => self.load_discord_room(),
            Room::Music => self.load_music_room(),
            _ => {}
        }
        self.load_shell_chrome_state();
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
        let id = ev.item.id.clone();
        let name = ev.item.name.clone();
        let edit = ev.edit;
        self.media.capture_items.retain(|it| it.id != id);
        self.media.capture_items.insert(0, ev.item);
        if edit {
            self.open_captures(None);
            self.open_capture_item(&id);
            self.open_snip_editor(&id);
        } else if !name.is_empty() {
            self.notice = Some(format!("Saved {name}."));
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
        self.on_game_run_state(&ev.id);
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
        self.on_game_shaders_changed();
    }

    pub fn apply_store_changed(&mut self, ev: StoreChangedEvent) {
        if ev.what == "wishlist" {
            self.load_store_wishlist();
        } else if ev.what == "home"
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
                handle.update(move |s, _| {
                    s.apply_bootstrap(boot);
                    s.warm_discord();
                    s.load_capture_status();
                    s.check_pending_notes_on_startup();
                    s.warm_store_on_startup();
                    s.wp4_warm_startup();
                });
            }
            Err(e) => {
                let msg = e.to_string();
                handle.update(move |s, _| {
                    s.refreshing = false;
                    s.lib.library_error = Some(msg.clone());
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
        at: None,
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
            ..Default::default()
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
        s.filters.clear();
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

    #[test]
    fn memory_leak_regression_10k_music_and_discord_pushes_stay_bounded() {
        use exo_rpc::{
            DiscordMessage, DiscordMessageEvent, DiscordPresenceEvent, DiscordTypingEvent,
            DiscordUser,
        };

        const TINY_PNG_B64: &str = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+X2dIAAAAASUVORK5CYII=";
        let tiny_png = exo_rpc::base64::decode(TINY_PNG_B64).expect("valid png");

        let cache_dir =
            std::env::temp_dir().join(format!("exo-ui-leak-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&cache_dir);
        std::fs::create_dir_all(&cache_dir).expect("cache dir");

        let mut s = AppState::with_demo_games();
        s.remote_cache_dir = Some(cache_dir.clone());
        s.images.borrow_mut().set_budget(2 << 20, 48);
        s.install_waker(Arc::new(|| {}));
        s.room = Room::Library;

        let padded_data_url = format!("data:image/png;base64,{TINY_PNG_B64}");
        let mut music_redraws = 0usize;
        let mut paused_redraws = 0usize;

        for i in 0..10_000usize {
            let track_idx = i / 100;
            let tick = (i % 100) as i64;
            let playing = tick < 80;
            let pos_ms = if playing { tick * 200 } else { 16_000 };
            let url = format!(
                "https://cdn.discordapp.com/avatars/100/track_{track_idx}.png?ex={i:x}&is=1&hm={:x}",
                i.wrapping_mul(31)
            );
            let cached_path = remote_art::cache_path_for(&cache_dir, &url);
            if !cached_path.is_file() {
                std::fs::write(&cached_path, &tiny_png).expect("write cached art");
            }
            let pb = MusicPlayback {
                provider: "apple".into(),
                source: "apple".into(),
                status: if playing { "playing" } else { "paused" }.into(),
                title: format!("Track {track_idx}"),
                artist: "Artist".into(),
                album: "Album".into(),
                artwork_url: Some(url),
                artwork_data_url: Some(padded_data_url.clone()),
                position_ms: Some(pos_ms),
                duration_ms: Some(180_000),
                can_play: true,
                can_pause: true,
                can_prev: true,
                can_next: true,
                can_seek: true,
            };
            let redrew = s.apply_host_event(HostEvent::MusicPlayback(Some(pb)));
            if playing {
                if redrew {
                    music_redraws += 1;
                }
            } else if tick > 80 && redrew {
                paused_redraws += 1;
            }
            assert!(
                s.media
                    .playback
                    .as_ref()
                    .and_then(|p| p.artwork_data_url.as_ref())
                    .is_none()
            );
            if tick == 0 || tick == 10 {
                let _ = s.current_playback_art(1.0);
            }
        }

        assert_eq!(paused_redraws, 0);
        assert!(music_redraws < 2500, "unexpected redraws: {music_redraws}");

        let mut library_discord_redraws = 0usize;
        for i in 0..10_000usize {
            let ev = HostEvent::DiscordPresence(DiscordPresenceEvent {
                user_id: format!("user-{}", i % 250),
                status: if i % 2 == 0 { "online" } else { "idle" }.into(),
                activity: Some(format!("Game {}", i % 10)),
                activity_type: Some(0),
                rich: None,
                prep: None,
            });
            if s.apply_host_event(ev) {
                library_discord_redraws += 1;
            }
        }
        assert_eq!(library_discord_redraws, 0);

        s.clock_secs = 10.0;
        s.switch_room(Room::Discord);
        for i in 0..120usize {
            let base = format!("https://cdn.discordapp.com/avatars/{i}/hash.png");
            let url_a = format!("{base}?ex=100&is=1&hm=aaa");
            let url_b = format!("{base}?ex=999&is=2&hm=zzz");
            let p = remote_art::cache_path_for(&cache_dir, &url_a);
            if !p.is_file() {
                std::fs::write(&p, &tiny_png).expect("write avatar");
            }
            let img_a = s.remote_image_sized(&url_a, 128.0, 1.0);
            let img_b = s.remote_image_sized(&url_b, 128.0, 1.0);
            assert!(img_a.is_some());
            assert!(img_b.is_some());
            if let Some(ref ic) = s.image_cache {
                let raw = ic.rgba(64, 64, vec![42; 64 * 64 * 4]);
                s.images.borrow_mut().insert(
                    format!("synthetic-discord:{i}"),
                    Some(raw),
                    CacheRoom::Discord,
                    128.0,
                    1.0,
                );
            }
        }

        for i in 0..1_000usize {
            let cid = format!("chan-{}", i % 40);
            let msg = DiscordMessage {
                id: format!("msg-{i}"),
                channel_id: cid.clone(),
                author: DiscordUser {
                    id: "u1".into(),
                    username: "Alice".into(),
                    ..Default::default()
                },
                content: format!("hello {i}"),
                ..Default::default()
            };
            s.apply_host_event(HostEvent::DiscordMessage(Box::new(DiscordMessageEvent {
                kind: "create".into(),
                channel_id: cid.clone(),
                id: Some(format!("msg-{i}")),
                message: Some(msg),
            })));
            if i < 200 {
                s.apply_host_event(HostEvent::DiscordTyping(DiscordTypingEvent {
                    channel_id: cid,
                    user_id: format!("typer-{i}"),
                    name: "Bob".into(),
                }));
            }
        }

        let (entries, bytes, retained_count, retained_bytes) = s.cache_stats();
        let max_entries = s.images.borrow().max_entries();
        let max_bytes = s.images.borrow().max_bytes();
        assert!(entries <= max_entries, "{entries} > {max_entries}");
        assert!(bytes <= max_bytes, "{bytes} > {max_bytes}");
        assert!(
            retained_bytes <= max_bytes,
            "rune retained {retained_bytes} > {max_bytes} ({retained_count} entries)"
        );
        assert!(s.discord.chat.messages_by_channel.len() <= 12);
        for msgs in s.discord.chat.messages_by_channel.values() {
            assert!(msgs.len() <= 200);
        }
        assert!(s.discord.chat.typing_by_channel.len() <= 32);

        s.clock_secs = 20.0;
        s.switch_room(Room::Library);
        assert!(s.images.borrow().room_count(CacheRoom::Discord) > 0);
        let mut tx = Tx::default();
        s.sync_and_tick_notices(51.0, &mut tx);
        assert_eq!(s.images.borrow().room_count(CacheRoom::Discord), 0);
        assert!(s.discord.chat.messages_by_channel.is_empty());
        assert!(s.discord.members_by_guild.is_empty());
        assert!(s.discord.channels_by_guild.is_empty());

        s.on_rest(&mut tx);
        let (_, _, after_rest_retained, _) = s.cache_stats();
        assert_eq!(after_rest_retained, 0);

        let _ = std::fs::remove_dir_all(&cache_dir);
    }
}
