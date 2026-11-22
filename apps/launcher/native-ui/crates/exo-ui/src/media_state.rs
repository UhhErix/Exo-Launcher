use crate::state::{AppState, HostStatus};
use exo_rpc::{
    ActionResult, CaptureItem, CaptureListReply, CaptureLiveState, CaptureSettingsDto,
    CaptureStatusReply, METHOD_APPLEMUSIC_ACT, METHOD_APPLEMUSIC_START, METHOD_APPLEMUSIC_VIEW,
    METHOD_CAPTURE_ACT, METHOD_CAPTURE_EDIT, METHOD_CAPTURE_ITEM, METHOD_CAPTURE_LIST,
    METHOD_CAPTURE_SETTINGS, METHOD_CAPTURE_STATUS, METHOD_MUSIC_CONTROL, METHOD_MUSIC_PLAYBACK,
    METHOD_MUSIC_SIGN_IN, METHOD_MUSIC_VOLUME, METHOD_SPOTIFY_ACT, METHOD_SPOTIFY_START,
    METHOD_SPOTIFY_VIEW, METHOD_YTMUSIC_ACT, METHOD_YTMUSIC_START, METHOD_YTMUSIC_VIEW,
    MusicPlayback, MusicPlaybackReply, MusicVolumeReply, PlayerCard, PlayerOption, PlayerSection,
    PlayerStartReply, PlayerViewData, PlayerViewReply,
};
use rune::{Player, TextEdit};
use serde_json::json;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MusicService {
    Spotify,
    Apple,
    YouTube,
}

impl MusicService {
    pub fn from_pref(pref: Option<&str>) -> Self {
        match pref.map(|s| s.trim().to_ascii_lowercase()).as_deref() {
            Some("apple") | Some("cider") => Self::Apple,
            Some("youtube") | Some("ytmusic") => Self::YouTube,
            _ => Self::Spotify,
        }
    }

    pub fn id(self) -> &'static str {
        match self {
            Self::Spotify => "spotify",
            Self::Apple => "apple",
            Self::YouTube => "youtube",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Spotify => "Spotify",
            Self::Apple => "Apple Music",
            Self::YouTube => "YouTube Music",
        }
    }

    pub fn liked_name(self) -> &'static str {
        match self {
            Self::Apple => "Favorite Songs",
            Self::YouTube => "Liked Music",
            Self::Spotify => "Liked Songs",
        }
    }

    pub fn has_browse(self) -> bool {
        self == Self::Apple
    }

    pub fn has_radio(self) -> bool {
        self == Self::Apple
    }

    pub fn has_explore(self) -> bool {
        self == Self::YouTube
    }

    pub fn has_recent(self) -> bool {
        matches!(self, Self::Apple | Self::Spotify)
    }

    pub fn start_method(self) -> Option<&'static str> {
        match self {
            Self::Spotify => Some(METHOD_SPOTIFY_START),
            Self::Apple => Some(METHOD_APPLEMUSIC_START),
            Self::YouTube => Some(METHOD_YTMUSIC_START),
        }
    }

    pub fn view_method(self) -> Option<&'static str> {
        match self {
            Self::Spotify => Some(METHOD_SPOTIFY_VIEW),
            Self::Apple => Some(METHOD_APPLEMUSIC_VIEW),
            Self::YouTube => Some(METHOD_YTMUSIC_VIEW),
        }
    }

    pub fn act_method(self) -> Option<&'static str> {
        match self {
            Self::Spotify => Some(METHOD_SPOTIFY_ACT),
            Self::Apple => Some(METHOD_APPLEMUSIC_ACT),
            Self::YouTube => Some(METHOD_YTMUSIC_ACT),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MusicNav {
    Home,
    Songs,
    Browse,
    Radio,
    Explore,
    Search,
    Collection {
        uri: String,
        kind: String,
        title: String,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MusicPanel {
    NowPlaying,
    Queue,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MusicRoomState {
    Loading,
    Running,
    SignedOut,
    NotPremium,
    Error(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureFilter {
    All,
    Videos,
    Screenshots,
    Favorites,
}

pub struct MediaState {
    pub music_service: MusicService,
    pub music_room_state: MusicRoomState,
    pub music_nav: MusicNav,
    pub music_history: Vec<MusicNav>,
    pub music_panel: Option<MusicPanel>,
    pub music_immersion: bool,
    pub playback: Option<MusicPlayback>,
    pub music_volume: f64,
    pub music_muted: bool,
    pub music_shuffle: bool,
    pub music_repeat: i32,
    pub music_can_shuffle: bool,
    pub music_can_repeat: bool,
    pub music_liked: bool,
    pub music_track_uri: String,
    pub music_library: Vec<PlayerCard>,
    pub music_library_filters: Vec<PlayerOption>,
    pub music_library_filter: String,
    pub music_recent: Vec<PlayerCard>,
    pub music_home_sections: Vec<PlayerSection>,
    pub music_page_data: Option<PlayerViewData>,
    pub music_queue_data: Option<PlayerViewData>,
    pub music_search_edit: TextEdit,

    pub captures_open: bool,
    pub captures_settings_open: bool,
    pub capture_popover_open: bool,
    pub capture_filter: CaptureFilter,
    pub capture_game_filter: Option<String>,
    pub capture_status: Option<CaptureStatusReply>,
    pub capture_items: Vec<CaptureItem>,
    pub captures_loading: bool,
    pub open_capture_id: Option<String>,
    pub clip_player: Option<Player>,
    pub clip_playing: bool,
    pub clip_muted: bool,
    pub trim_start_pct: f32,
    pub trim_end_pct: f32,
}

impl MediaState {
    pub fn demo() -> Self {
        Self {
            music_service: MusicService::Apple,
            music_room_state: MusicRoomState::Running,
            music_nav: MusicNav::Home,
            music_history: Vec::new(),
            music_panel: None,
            music_immersion: false,
            playback: Some(MusicPlayback {
                provider: "apple".into(),
                source: "session".into(),
                status: "playing".into(),
                title: "Midnight City".into(),
                artist: "M83".into(),
                album: "Hurry Up, We're Dreaming".into(),
                artwork_url: None,
                artwork_data_url: None,
                position_ms: Some(114_000),
                duration_ms: Some(243_000),
                can_play: true,
                can_pause: true,
                can_prev: true,
                can_next: true,
                can_seek: true,
            }),
            music_volume: 0.78,
            music_muted: false,
            music_shuffle: false,
            music_repeat: 0,
            music_can_shuffle: true,
            music_can_repeat: true,
            music_liked: false,
            music_track_uri: String::new(),
            music_library: vec![
                PlayerCard {
                    uri: "am:liked".into(),
                    kind: "collection".into(),
                    title: "Favorite Songs".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    url: None,
                    pinned: false,
                    owned: true,
                },
                PlayerCard {
                    uri: "am:playlist:demo1".into(),
                    kind: "playlist".into(),
                    title: "peak".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    url: None,
                    pinned: false,
                    owned: true,
                },
                PlayerCard {
                    uri: "am:playlist:demo2".into(),
                    kind: "playlist".into(),
                    title: "Peak".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    url: None,
                    pinned: false,
                    owned: true,
                },
            ],
            music_library_filters: vec![
                PlayerOption {
                    id: "all".into(),
                    name: "All".into(),
                },
                PlayerOption {
                    id: "playlists".into(),
                    name: "Playlists".into(),
                },
                PlayerOption {
                    id: "albums".into(),
                    name: "Albums".into(),
                },
                PlayerOption {
                    id: "artists".into(),
                    name: "Artists".into(),
                },
            ],
            music_library_filter: "all".into(),
            music_recent: vec![
                PlayerCard {
                    uri: "am:album:r1".into(),
                    kind: "album".into(),
                    title: "Peak".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r2".into(),
                    kind: "album".into(),
                    title: "The Way I Are".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r3".into(),
                    kind: "album".into(),
                    title: "Favorite Songs".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r4".into(),
                    kind: "album".into(),
                    title: "The Eminem Show".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r5".into(),
                    kind: "album".into(),
                    title: "Hurry Up, We're Dreaming".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r6".into(),
                    kind: "album".into(),
                    title: "Currents".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r7".into(),
                    kind: "album".into(),
                    title: "Random Access Memories".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:r8".into(),
                    kind: "album".into(),
                    title: "Discovery".into(),
                    subtitle: String::new(),
                    image: String::new(),
                    ..Default::default()
                },
            ],
            music_home_sections: vec![PlayerSection {
                title: "Best New Songs".into(),
                more: None,
                items: vec![
                    PlayerCard {
                        uri: "am:album:s1".into(),
                        kind: "album".into(),
                        title: "Midnight City".into(),
                        subtitle: "M83".into(),
                        image: String::new(),
                        ..Default::default()
                    },
                    PlayerCard {
                        uri: "am:album:s2".into(),
                        kind: "album".into(),
                        title: "Let It Happen".into(),
                        subtitle: "Tame Impala".into(),
                        image: String::new(),
                        ..Default::default()
                    },
                    PlayerCard {
                        uri: "am:album:s3".into(),
                        kind: "album".into(),
                        title: "Instant Crush".into(),
                        subtitle: "Daft Punk".into(),
                        image: String::new(),
                        ..Default::default()
                    },
                    PlayerCard {
                        uri: "am:album:s4".into(),
                        kind: "album".into(),
                        title: "Resonance".into(),
                        subtitle: "HOME".into(),
                        image: String::new(),
                        ..Default::default()
                    },
                ],
            }],
            music_page_data: None,
            music_queue_data: None,
            music_search_edit: TextEdit::new(""),
            captures_open: false,
            captures_settings_open: false,
            capture_popover_open: false,
            capture_filter: CaptureFilter::All,
            capture_game_filter: None,
            capture_status: Some(CaptureStatusReply {
                ok: true,
                present: true,
                folder: Some("Videos\\Exo Captures".into()),
                state: Some(CaptureLiveState {
                    replay: true,
                    buffering: true,
                    recording: false,
                    recording_ms: None,
                    present: true,
                }),
                settings: Some(CaptureSettingsDto {
                    quality: 60,
                    replay_seconds: 60,
                    max_fps: 120,
                    replay_mode: "games".into(),
                    cursor: true,
                    save_replay_keys: "Ctrl+Alt+F10".into(),
                    record_keys: "Ctrl+Alt+F9".into(),
                    snip_keys: "Ctrl+Alt+F8".into(),
                    auto_shrink: true,
                }),
            }),
            capture_items: vec![
                CaptureItem {
                    id: "cap0000000000001".into(),
                    kind: "video".into(),
                    game: "Counter-Strike 2".into(),
                    game_id: Some("demo:cs2".into()),
                    name: "Mirage 1v4 Clutch Replay".into(),
                    created: "2026-11-22T02:14:00Z".into(),
                    size: 64 * 1024 * 1024,
                    duration_ms: Some(42_000.0),
                    favourite: true,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
                CaptureItem {
                    id: "cap0000000000002".into(),
                    kind: "video".into(),
                    game: "Cyberpunk 2077".into(),
                    game_id: Some("demo:cyberpunk".into()),
                    name: "Night City Ray-Traced Drift".into(),
                    created: "2026-11-21T23:40:00Z".into(),
                    size: 118 * 1024 * 1024,
                    duration_ms: Some(60_000.0),
                    favourite: false,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
                CaptureItem {
                    id: "cap0000000000003".into(),
                    kind: "image".into(),
                    game: "Elden Ring".into(),
                    game_id: Some("demo:elden".into()),
                    name: "Leyndell Golden Vista".into(),
                    created: "2026-11-21T20:10:00Z".into(),
                    size: 4_800_000,
                    duration_ms: None,
                    favourite: true,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
                CaptureItem {
                    id: "cap0000000000004".into(),
                    kind: "video".into(),
                    game: "Valorant".into(),
                    game_id: Some("demo:valorant".into()),
                    name: "Ascent Operator Ace".into(),
                    created: "2026-11-20T19:05:00Z".into(),
                    size: 51 * 1024 * 1024,
                    duration_ms: Some(28_000.0),
                    favourite: false,
                    url: String::new(),
                    thumb: None,
                    derived: false,
                },
            ],
            captures_loading: false,
            open_capture_id: None,
            clip_player: None,
            clip_playing: false,
            clip_muted: false,
            trim_start_pct: 0.0,
            trim_end_pct: 1.0,
        }
    }

    pub fn empty_for_host() -> Self {
        Self {
            music_service: MusicService::Spotify,
            music_room_state: MusicRoomState::Loading,
            music_nav: MusicNav::Home,
            music_history: Vec::new(),
            music_panel: None,
            music_immersion: false,
            playback: None,
            music_volume: 0.75,
            music_muted: false,
            music_shuffle: false,
            music_repeat: 0,
            music_can_shuffle: false,
            music_can_repeat: false,
            music_liked: false,
            music_track_uri: String::new(),
            music_library: Vec::new(),
            music_library_filters: Vec::new(),
            music_library_filter: "all".into(),
            music_recent: Vec::new(),
            music_home_sections: Vec::new(),
            music_page_data: None,
            music_queue_data: None,
            music_search_edit: TextEdit::new(""),
            captures_open: false,
            captures_settings_open: false,
            capture_popover_open: false,
            capture_filter: CaptureFilter::All,
            capture_game_filter: None,
            capture_status: None,
            capture_items: Vec::new(),
            captures_loading: false,
            open_capture_id: None,
            clip_player: None,
            clip_playing: false,
            clip_muted: false,
            trim_start_pct: 0.0,
            trim_end_pct: 1.0,
        }
    }

    pub fn apply_player_state(&mut self, data: &PlayerViewData) {
        if let Some(ref uri) = data.track_uri {
            self.music_track_uri = uri.clone();
        }
        if let Some(sh) = data.shuffle {
            self.music_shuffle = sh;
        }
        if let Some(rep) = data.repeat {
            self.music_repeat = rep;
        }
        if let Some(cs) = data.can_shuffle {
            self.music_can_shuffle = cs;
        }
        if let Some(cr) = data.can_repeat {
            self.music_can_repeat = cr;
        }
        if let Some(lk) = data.liked {
            self.music_liked = lk;
        }
    }
}

impl AppState {
    pub fn sync_music_service_from_settings(&mut self) {
        let pref = self
            .settings
            .ui_prefs
            .get("exo.musicWebService")
            .and_then(|v| v.as_str());
        self.media.music_service = MusicService::from_pref(pref);
    }

    pub fn load_music_room(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        self.sync_music_service_from_settings();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, MusicPlaybackReply>(METHOD_MUSIC_PLAYBACK, &json!({}), move |res| {
                if let Ok(reply) = res {
                    h1.update(move |s, _| s.media.playback = reply.playback);
                }
            });
            let h2 = handle.clone();
            rpc.call::<_, MusicVolumeReply>(METHOD_MUSIC_VOLUME, &json!({}), move |res| {
                if let Ok(v) = res {
                    h2.update(move |s, _| {
                        if let Some(lvl) = v.level {
                            s.media.music_volume = lvl;
                        }
                        s.media.music_muted = v.muted;
                    });
                }
            });
            if let Some(start_method) = self.media.music_service.start_method() {
                let h3 = handle.clone();
                rpc.call::<_, PlayerStartReply>(start_method, &json!({}), move |res| {
                    h3.update(move |s, _| match res {
                        Ok(reply) if reply.ok && reply.state == "running" => {
                            s.media.music_room_state = MusicRoomState::Running;
                            s.load_music_views();
                        }
                        Ok(reply) if reply.state == "signedOut" => {
                            s.media.music_room_state = MusicRoomState::SignedOut;
                        }
                        Ok(reply) if reply.state == "notPremium" => {
                            s.media.music_room_state = MusicRoomState::NotPremium;
                        }
                        Ok(reply) if reply.state == "loading" => {
                            s.media.music_room_state = MusicRoomState::Loading;
                        }
                        Ok(reply) => {
                            s.media.music_room_state = MusicRoomState::Error(
                                reply
                                    .message
                                    .unwrap_or_else(|| "Unable to start music engine.".into()),
                            );
                        }
                        Err(err) => {
                            s.media.music_room_state = MusicRoomState::Error(err.to_string());
                        }
                    });
                });
            }
        }
    }

    pub fn load_music_views(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(view_method) = self.media.music_service.view_method() else {
            return;
        };
        let has_recent = self.media.music_service.has_recent();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h_lib = handle.clone();
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "library" }),
                move |res| {
                    if let Ok(reply) = res
                        && reply.ok
                        && let Some(data) = reply.data
                    {
                        h_lib.update(move |s, _| {
                            s.media.music_library = data.items;
                            if !data.filters.is_empty() {
                                s.media.music_library_filters = data.filters;
                            }
                            if let Some(f) = data.filter {
                                s.media.music_library_filter = f;
                            }
                        });
                    }
                },
            );
            let h_home = handle.clone();
            rpc.call::<_, PlayerViewReply>(view_method, &json!({ "view": "home" }), move |res| {
                if let Ok(reply) = res
                    && reply.ok
                    && let Some(data) = reply.data
                {
                    h_home.update(move |s, _| {
                        s.media.music_home_sections = data.sections;
                    });
                }
            });
            if has_recent {
                let h_rec = handle.clone();
                rpc.call::<_, PlayerViewReply>(
                    view_method,
                    &json!({ "view": "recent" }),
                    move |res| {
                        if let Ok(reply) = res
                            && reply.ok
                            && let Some(data) = reply.data
                        {
                            h_rec.update(move |s, _| {
                                s.media.music_recent = data.items;
                            });
                        }
                    },
                );
            }
            let h_pl = handle.clone();
            rpc.call::<_, PlayerViewReply>(view_method, &json!({ "view": "player" }), move |res| {
                if let Ok(reply) = res
                    && reply.ok
                    && let Some(data) = reply.data
                {
                    h_pl.update(move |s, _| {
                        s.media.apply_player_state(&data);
                    });
                }
            });
        }
    }

    pub fn music_open_nav(&mut self, nav: MusicNav) {
        if self.media.music_nav != nav {
            let prev = self.media.music_nav.clone();
            self.media.music_history.push(prev);
            self.media.music_nav = nav.clone();
            self.media.music_page_data = None;
        }
        self.load_music_current_nav();
    }

    pub fn music_go_back(&mut self) {
        if let Some(prev) = self.media.music_history.pop() {
            self.media.music_nav = prev;
            self.media.music_page_data = None;
            self.load_music_current_nav();
        }
    }

    fn load_music_current_nav(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(view_method) = self.media.music_service.view_method() else {
            return;
        };
        let params = match &self.media.music_nav {
            MusicNav::Home => {
                self.load_music_views();
                return;
            }
            MusicNav::Songs => json!({ "view": "songs" }),
            MusicNav::Browse => json!({ "view": "browse" }),
            MusicNav::Radio => json!({ "view": "radio" }),
            MusicNav::Explore => json!({ "view": "explore" }),
            MusicNav::Search => {
                let q = self.media.music_search_edit.text.trim().to_string();
                if q.is_empty() {
                    return;
                }
                json!({ "view": "search", "query": q })
            }
            MusicNav::Collection { uri, kind, .. } => {
                let v = match kind.as_str() {
                    "album" => "album",
                    "artist" => "artist",
                    "collection" => "songs",
                    _ => "playlist",
                };
                if v == "songs" {
                    json!({ "view": "songs" })
                } else {
                    json!({ "view": v, "uri": uri })
                }
            }
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(view_method, &params, move |res| {
                if let Ok(reply) = res
                    && reply.ok
                    && let Some(data) = reply.data
                {
                    handle.update(move |s, _| {
                        s.media.music_page_data = Some(data);
                    });
                }
            });
        }
    }

    pub fn music_open_card(&mut self, uri: String, kind: String, title: String) {
        if kind == "track" {
            self.music_play_uri(&uri, None, None);
        } else {
            self.music_open_nav(MusicNav::Collection { uri, kind, title });
        }
    }

    pub fn music_play_uri(&mut self, uri: &str, context: Option<&str>, index: Option<usize>) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(act_method) = self.media.music_service.act_method() else {
            return;
        };
        let mut body = json!({ "action": "play", "uri": uri });
        if let Some(ctx) = context {
            body["context"] = json!(ctx);
        }
        if let Some(idx) = index {
            body["index"] = json!(idx);
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(act_method, &body, move |_| {
                handle.update(|s, _| s.load_music_room());
            });
        }
    }

    pub fn music_toggle_shuffle(&mut self) {
        let next = !self.media.music_shuffle;
        self.media.music_shuffle = next;
        if let Some(act_method) = self.media.music_service.act_method() {
            #[cfg(windows)]
            if let Some(rpc) = self.rpc.clone() {
                rpc.call_raw(
                    act_method,
                    &json!({ "action": "shuffle", "on": next }),
                    |_| {},
                );
            }
        }
    }

    pub fn music_cycle_repeat(&mut self) {
        let next = (self.media.music_repeat + 1) % 3;
        self.media.music_repeat = next;
        if let Some(act_method) = self.media.music_service.act_method() {
            #[cfg(windows)]
            if let Some(rpc) = self.rpc.clone() {
                rpc.call_raw(
                    act_method,
                    &json!({ "action": "repeat", "index": next }),
                    |_| {},
                );
            }
        }
    }

    pub fn music_toggle_panel(&mut self, panel: MusicPanel) {
        if self.media.music_panel == Some(panel) {
            self.media.music_panel = None;
            return;
        }
        self.media.music_panel = Some(panel);
        if panel == MusicPanel::Queue {
            self.load_music_queue();
        }
    }

    pub fn load_music_queue(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(view_method) = self.media.music_service.view_method() else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(view_method, &json!({ "view": "queue" }), move |res| {
                if let Ok(reply) = res
                    && reply.ok
                    && let Some(data) = reply.data
                {
                    handle.update(move |s, _| {
                        s.media.music_queue_data = Some(data);
                    });
                }
            });
        }
    }

    pub fn music_submit_search(&mut self) {
        if !self.media.music_search_edit.text.trim().is_empty() {
            self.music_open_nav(MusicNav::Search);
        }
    }

    pub fn refresh_music_playback(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, MusicPlaybackReply>(METHOD_MUSIC_PLAYBACK, &json!({}), move |res| {
                if let Ok(reply) = res {
                    handle.update(move |s, _| s.media.playback = reply.playback);
                }
            });
        }
    }

    pub fn music_control(&mut self, action: &str) {
        if let Some(ref mut pb) = self.media.playback {
            match action {
                "toggle" => {
                    pb.status = if pb.is_playing() {
                        "paused".into()
                    } else {
                        "playing".into()
                    };
                }
                "play" => pb.status = "playing".into(),
                "pause" => pb.status = "paused".into(),
                _ => {}
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_MUSIC_CONTROL,
                &json!({ "action": action }),
                move |res| {
                    handle.update(move |s, _| {
                        let control_ok = match res {
                            Ok(r) if r.ok => true,
                            Ok(r) => {
                                if let Some(msg) = r.message {
                                    s.notice = Some(msg);
                                }
                                false
                            }
                            Err(_) => false,
                        };
                        if needs_music_playback_refresh(control_ok, s.media.playback.as_ref()) {
                            s.refresh_music_playback();
                        }
                    });
                },
            );
        }
    }

    pub fn music_set_volume(&mut self, level: f64, muted: Option<bool>) {
        let clamped = level.clamp(0.0, 1.0);
        self.media.music_volume = clamped;
        if let Some(m) = muted {
            self.media.music_muted = m;
        } else {
            self.media.music_muted = clamped <= 0.0;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            let params = match muted {
                Some(m) => json!({ "muted": m }),
                None => json!({ "level": clamped }),
            };
            rpc.call_raw(METHOD_MUSIC_VOLUME, &params, |_| {});
        }
    }

    pub fn music_sign_in(&mut self) {
        let svc = self.media.music_service.id();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_MUSIC_SIGN_IN,
                &json!({ "service": svc }),
                move |res| {
                    if let Ok(r) = res
                        && let Some(msg) = r.message
                    {
                        handle.update(move |s, _| s.notice = Some(msg));
                    }
                },
            );
        }
    }

    pub fn open_captures(&mut self, game_id: Option<String>) {
        self.media.captures_open = true;
        self.media.captures_settings_open = false;
        self.media.capture_popover_open = false;
        self.media.capture_game_filter = game_id;
        self.media.open_capture_id = None;
        self.media.clip_player = None;
        self.load_captures();
    }

    pub fn close_captures(&mut self) {
        self.media.captures_open = false;
        self.media.captures_settings_open = false;
        self.media.open_capture_id = None;
        self.media.clip_player = None;
    }

    pub fn load_captures(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.media.captures_loading = true;
            let h1 = handle.clone();
            rpc.call::<_, CaptureStatusReply>(METHOD_CAPTURE_STATUS, &json!({}), move |res| {
                if let Ok(st) = res {
                    h1.update(move |s, _| s.media.capture_status = Some(st));
                }
            });
            let params = match &self.media.capture_game_filter {
                Some(gid) => json!({ "gameId": gid, "limit": 60 }),
                None => json!({ "limit": 60 }),
            };
            rpc.call::<_, CaptureListReply>(METHOD_CAPTURE_LIST, &params, move |res| {
                handle.update(move |s, _| {
                    s.media.captures_loading = false;
                    if let Ok(list) = res {
                        s.media.capture_items = list.items;
                    }
                });
            });
        }
    }

    pub fn open_capture_item(&mut self, id: &str) {
        self.media.open_capture_id = Some(id.to_string());
        self.media.trim_start_pct = 0.0;
        self.media.trim_end_pct = 1.0;
        self.media.clip_player = None;
        self.media.clip_playing = false;

        if let Some(item) = self.media.capture_items.iter().find(|c| c.id == id)
            && item.kind == "video"
            && let Some(path) = self.art_base.resolve_capture_file(&item.url)
        {
            let player = Player::file(path);
            player.set_muted(self.media.clip_muted);
            player.set_looping(true);
            player.play();
            self.media.clip_player = Some(player);
            self.media.clip_playing = true;
        }
    }

    pub fn close_capture_item(&mut self) {
        if let Some(ref p) = self.media.clip_player {
            p.pause();
        }
        self.media.clip_player = None;
        self.media.clip_playing = false;
        self.media.open_capture_id = None;
    }

    pub fn toggle_clip_play(&mut self) {
        if let Some(ref p) = self.media.clip_player {
            if self.media.clip_playing {
                p.pause();
                self.media.clip_playing = false;
            } else {
                p.play();
                self.media.clip_playing = true;
            }
        }
    }

    pub fn toggle_clip_mute(&mut self) {
        self.media.clip_muted = !self.media.clip_muted;
        if let Some(ref p) = self.media.clip_player {
            p.set_muted(self.media.clip_muted);
        }
    }

    pub fn capture_act(&mut self, action: &str) {
        self.media.capture_popover_open = false;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_CAPTURE_ACT,
                &json!({ "action": action }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(r) = res
                            && let Some(msg) = r.message
                        {
                            s.notice = Some(msg);
                        }
                        s.load_captures();
                    });
                },
            );
        }
    }

    pub fn capture_set_replay_mode(&mut self, mode: &str) {
        if let Some(ref mut st) = self.media.capture_status
            && let Some(ref mut cfg) = st.settings
        {
            cfg.replay_mode = mode.to_string();
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureStatusReply>(
                METHOD_CAPTURE_SETTINGS,
                &json!({ "replayMode": mode }),
                move |res| {
                    if let Ok(st) = res {
                        handle.update(move |s, _| s.media.capture_status = Some(st));
                    }
                },
            );
        }
    }

    pub fn capture_item_action(&mut self, id: &str, action: &str) {
        if action == "favourite" || action == "unfavourite" {
            let fav = action == "favourite";
            if let Some(item) = self.media.capture_items.iter_mut().find(|c| c.id == id) {
                item.favourite = fav;
            }
        } else if action == "delete" {
            self.media.capture_items.retain(|c| c.id != id);
            if self.media.open_capture_id.as_deref() == Some(id) {
                self.close_capture_item();
            }
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_CAPTURE_ITEM,
                &json!({ "id": id, "action": action }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(r) = res
                            && let Some(msg) = r.message
                        {
                            s.notice = Some(msg);
                        }
                    });
                },
            );
        }
    }

    pub fn capture_export_trim(&mut self, id: &str, preset: &str) {
        let dur_ms = self
            .media
            .capture_items
            .iter()
            .find(|c| c.id == id)
            .and_then(|c| c.duration_ms)
            .unwrap_or(30_000.0);
        let start_ms = (dur_ms * self.media.trim_start_pct as f64).round();
        let end_ms = (dur_ms * self.media.trim_end_pct as f64).round();
        self.notice = Some(format!("Exporting {preset} clip…"));
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_CAPTURE_EDIT,
                &json!({ "id": id, "preset": preset, "startMs": start_ms, "endMs": end_ms }),
                move |res| {
                    handle.update(move |s, _| {
                        s.notice = Some(match res {
                            Ok(r) if r.ok => "Clip exported.".into(),
                            Ok(r) => r.message.unwrap_or_else(|| "Export failed.".into()),
                            Err(e) => format!("Export failed: {e}"),
                        });
                        s.load_captures();
                    });
                },
            );
        }
    }
}

pub fn needs_music_playback_refresh(control_ok: bool, playback: Option<&MusicPlayback>) -> bool {
    if !control_ok {
        return true;
    }
    playback.is_some_and(|p| !p.source.eq_ignore_ascii_case("page"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn page_control_ok_skips_follow_up_rpcs() {
        let page_pb = MusicPlayback {
            source: "page".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        assert!(!needs_music_playback_refresh(true, Some(&page_pb)));
        assert!(!needs_music_playback_refresh(true, None));
    }

    #[test]
    fn failed_or_headless_control_requests_single_playback_refresh() {
        let page_pb = MusicPlayback {
            source: "page".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        let gsmtc_pb = MusicPlayback {
            source: "system".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        assert!(needs_music_playback_refresh(false, Some(&page_pb)));
        assert!(needs_music_playback_refresh(false, None));
        assert!(needs_music_playback_refresh(true, Some(&gsmtc_pb)));
    }

    #[test]
    fn music_control_flips_playback_optimistically() {
        let mut s = AppState::default();
        s.media.playback = Some(MusicPlayback {
            source: "page".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        });
        s.music_control("toggle");
        assert_eq!(s.media.playback.as_ref().unwrap().status, "paused");
        s.music_control("toggle");
        assert_eq!(s.media.playback.as_ref().unwrap().status, "playing");
        s.music_control("pause");
        assert_eq!(s.media.playback.as_ref().unwrap().status, "paused");
        s.music_control("play");
        assert_eq!(s.media.playback.as_ref().unwrap().status, "playing");
    }
}
