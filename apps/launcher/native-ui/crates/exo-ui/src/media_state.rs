use crate::pc_state::SettingsSection;
use crate::state::{AppState, HostStatus, Room};
use exo_rpc::{
    ActionResult, CaptureItem, CaptureListReply, CaptureLiveState, CaptureSettingsDto,
    CaptureStatusReply, DiscordCallSong, DiscordCallSongsReply, DiscordRich, JamSession,
    LyricsLine, METHOD_APPLEMUSIC_ACT, METHOD_APPLEMUSIC_START, METHOD_APPLEMUSIC_VIEW,
    METHOD_CAPTURE_ACT, METHOD_CAPTURE_LIST, METHOD_CAPTURE_STATUS, METHOD_DISCORD_CALL_SONGS,
    METHOD_DISCORD_FRIENDS, METHOD_MUSIC_CONTROL, METHOD_MUSIC_MATCH, METHOD_MUSIC_PLAYBACK,
    METHOD_MUSIC_SIGN_IN, METHOD_MUSIC_VOLUME, METHOD_MUSIC_WARM, METHOD_SHELL_OPEN_URL,
    METHOD_SPOTIFY_ACT, METHOD_SPOTIFY_LINK, METHOD_SPOTIFY_START, METHOD_SPOTIFY_VIEW,
    METHOD_YTMUSIC_ACT, METHOD_YTMUSIC_START, METHOD_YTMUSIC_VIEW, MusicControlReply,
    MusicMatchReply, MusicMatchSong, MusicPlayback, MusicPlaybackReply, MusicStatusEvent,
    MusicVolumeReply, PlayerActReply, PlayerArtist, PlayerCard, PlayerDevice, PlayerFolderRef,
    PlayerFriend, PlayerOption, PlayerSection, PlayerStartReply, PlayerStateData, PlayerStateReply,
    PlayerTrack, PlayerViewData, PlayerViewReply, SpotifyAudio, SpotifyAudioReply,
    SpotifyLinkReply,
};
use rune::{Image, Player, TextEdit};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::time::{SystemTime, UNIX_EPOCH};

const MUSIC_START_FAST_ATTEMPTS: u32 = 20;
const MUSIC_START_FAST_POLL_MS: u64 = 500;
const MUSIC_START_SLOW_POLL_MS: u64 = 1000;
const MUSIC_START_MAX_ATTEMPTS: u32 = 120;
const MUSIC_SIGN_IN_POLL_MS: u64 = 2000;
const MUSIC_SIGN_IN_MAX_ATTEMPTS: u32 = 300;
#[cfg(windows)]
const MUSIC_HOME_RETRY_MS: u64 = 1000;
const WARM_COOLDOWN_S: f64 = 5.0;
const FRIENDS_POLL_S: f64 = 30.0;
const JAM_ACTIVE_POLL_S: f64 = 5.0;
const JAM_IDLE_POLL_S: f64 = 30.0;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MusicStartAction {
    LoadViews,
    RetryAfter { delay_ms: u64, next_attempt: u32 },
    Stop,
}

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

    pub fn logo_id(self) -> &'static str {
        match self {
            Self::Spotify => "spotify",
            Self::Apple => "apple-music",
            Self::YouTube => "youtube",
        }
    }

    pub fn liked_name(self) -> &'static str {
        match self {
            Self::Apple => "Favorites",
            Self::YouTube => "Liked Music",
            Self::Spotify => "Liked Songs",
        }
    }

    pub fn premium_url(self) -> Option<&'static str> {
        match self {
            Self::Spotify => Some("https://www.spotify.com/premium/"),
            _ => None,
        }
    }

    pub fn new_playlist_name(self, count: usize) -> Option<String> {
        match self {
            Self::Spotify => Some(format!("My Playlist #{}", count + 1)),
            _ => None,
        }
    }

    pub fn track_uri_prefix(self) -> &'static str {
        match self {
            Self::Spotify => "spotify:track:",
            Self::YouTube => "ytm:track:",
            Self::Apple => "am:songs:",
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

    pub fn has_devices(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_jam(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_friends(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_add_to_playlist(self) -> bool {
        true
    }

    pub fn has_enqueue(self) -> bool {
        true
    }

    pub fn has_play_next(self) -> bool {
        true
    }

    pub fn has_shows(self) -> bool {
        matches!(self, Self::Spotify | Self::YouTube)
    }

    pub fn has_audio(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_quality(self) -> bool {
        matches!(self, Self::YouTube | Self::Apple)
    }

    pub fn has_edit_queue(self) -> bool {
        matches!(self, Self::Spotify | Self::YouTube)
    }

    pub fn has_dequeue(self) -> bool {
        matches!(self, Self::Spotify | Self::YouTube | Self::Apple)
    }

    pub fn has_history(self) -> bool {
        self == Self::Apple
    }

    pub fn has_autoplay(self) -> bool {
        matches!(self, Self::YouTube | Self::Apple)
    }

    pub fn has_station(self) -> bool {
        self == Self::Apple
    }

    pub fn has_mix(self) -> bool {
        self == Self::YouTube
    }

    pub fn has_song_radio(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_library(self) -> bool {
        self == Self::Apple
    }

    pub fn has_suggest_less(self) -> bool {
        self == Self::Apple
    }

    pub fn has_dislike(self) -> bool {
        self == Self::YouTube
    }

    pub fn has_playlists(self) -> bool {
        true
    }

    pub fn has_cover(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_folders(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_credits(self) -> bool {
        matches!(self, Self::Spotify | Self::Apple)
    }

    pub fn has_discography(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_speed(self) -> bool {
        self == Self::Spotify
    }

    pub fn has_video(self) -> bool {
        self == Self::YouTube
    }

    pub fn has_related(self) -> bool {
        self == Self::YouTube
    }

    pub fn has_queue_lists(self) -> bool {
        matches!(self, Self::YouTube | Self::Apple)
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

    pub fn share_url(self, uri: &str, explicit_url: Option<&str>) -> Option<String> {
        if let Some(u) = explicit_url.filter(|u| !u.is_empty()) {
            return Some(u.to_string());
        }
        match self {
            Self::Spotify => spotify_share_url(uri),
            Self::YouTube => youtube_share_url(uri),
            Self::Apple => None,
        }
    }
}

pub fn spotify_share_url(uri: &str) -> Option<String> {
    let parts: Vec<&str> = uri.split(':').collect();
    let (kind, id) = if parts.get(1) == Some(&"user") && parts.get(3) == Some(&"playlist") {
        ("playlist", *parts.get(4)?)
    } else {
        (*parts.get(1)?, *parts.get(2)?)
    };
    if !id.is_empty()
        && id.bytes().all(|b| b.is_ascii_alphanumeric())
        && matches!(
            kind,
            "track" | "album" | "artist" | "playlist" | "show" | "episode"
        )
    {
        Some(format!("https://open.spotify.com/{kind}/{id}"))
    } else {
        None
    }
}

pub fn youtube_share_url(uri: &str) -> Option<String> {
    let rest = uri.strip_prefix("ytm:")?;
    let (kind, id) = rest.split_once(':')?;
    let id = id.split(':').next().unwrap_or(id);
    if id.is_empty()
        || !id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
    {
        return None;
    }
    match kind {
        "track" => Some(format!("https://music.youtube.com/watch?v={id}")),
        "list" => Some(format!("https://music.youtube.com/playlist?list={id}")),
        "playlist" if id.starts_with("VL") => Some(format!(
            "https://music.youtube.com/playlist?list={}",
            &id[2..]
        )),
        "artist" if id.starts_with("UC") => Some(format!("https://music.youtube.com/channel/{id}")),
        "album" | "show" | "playlist" => Some(format!("https://music.youtube.com/browse/{id}")),
        _ => None,
    }
}

pub fn encode_act_params(service: MusicService, action: &str, mut body: Value) -> Value {
    if service == MusicService::Spotify && action == "deletePlaylist" {
        let uri = body
            .get("uri")
            .and_then(|v| v.as_str())
            .unwrap_or_default()
            .to_string();
        return json!({ "action": "like", "uri": uri, "on": false });
    }
    if matches!(service, MusicService::YouTube | MusicService::Apple) {
        let name = body.get("name").and_then(|v| v.as_str()).map(String::from);
        let desc = body
            .get("description")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        let privacy = body
            .get("privacy")
            .and_then(|v| v.as_str())
            .map(String::from);
        let uri = body.get("uri").and_then(|v| v.as_str()).map(String::from);
        if action == "createPlaylist" {
            let text = json!({ "name": name, "description": desc, "privacy": privacy }).to_string();
            let mut out = json!({ "action": action, "text": text });
            if let Some(u) = uri {
                out["uri"] = json!(u);
            }
            return out;
        }
        if action == "editPlaylist" {
            let text = json!({ "name": name, "description": desc, "privacy": privacy }).to_string();
            let mut out = json!({ "action": action, "text": text });
            if let Some(u) = uri {
                out["context"] = json!(u);
            }
            return out;
        }
        if action == "deletePlaylist" {
            let mut out = json!({ "action": action });
            if let Some(u) = uri {
                out["context"] = json!(u);
            }
            return out;
        }
    }
    body["action"] = json!(action);
    body
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MusicNav {
    Home,
    Songs,
    Browse {
        uri: Option<String>,
        title: Option<String>,
    },
    Radio,
    Explore,
    Search,
    Collection {
        uri: String,
        kind: String,
        title: String,
        edit: bool,
    },
    Discography {
        uri: String,
        title: String,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MusicPanel {
    NowPlaying,
    Queue,
    Devices,
    Friends,
    Jam,
    Sound,
    Credits,
}

impl MusicPanel {
    pub fn label(self) -> &'static str {
        match self {
            Self::NowPlaying => "Now playing",
            Self::Queue => "Queue",
            Self::Devices => "Devices",
            Self::Friends => "Friend activity",
            Self::Jam => "Jam",
            Self::Sound => "Sound",
            Self::Credits => "Credits",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum NowPlayingTab {
    Lyrics,
    Related,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MusicRoomState {
    Loading,
    Running,
    SignedOut,
    NotPremium,
    Error(String),
}

#[derive(Debug, Clone)]
pub enum MusicMenu {
    LibraryShowSort,
    LibraryCreate,
    LibraryRow {
        card: PlayerCard,
        in_folder: bool,
        anchor_key: String,
    },
    PageMore {
        anchor_key: String,
    },
    TrackMore {
        track: PlayerTrack,
        index: usize,
        total: usize,
        playlist: Option<String>,
        show_album: bool,
        liked_page: bool,
        can_move: bool,
        whole: bool,
        anchor_key: String,
        point: Option<(f32, f32)>,
        open_submenu: Option<usize>,
    },
    MorePanels,
    SongsSort,
    SoundQuality,
    SoundSpeed,
    SoundSleep,
}

#[derive(Clone)]
pub struct PlaylistSheetState {
    pub uri: Option<String>,
    pub orig_title: String,
    pub orig_desc: String,
    pub orig_privacy: Option<String>,
    pub name_edit: TextEdit,
    pub desc_edit: TextEdit,
    pub privacy: Option<String>,
    pub image_url: String,
    pub cover_data_url: Option<String>,
    pub cover_preview: Option<Image>,
    pub track_uri: Option<String>,
    pub folder_uri: Option<String>,
    pub busy: bool,
    pub error: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ListenAlongFriend {
    pub id: String,
    pub name: String,
    pub avatar: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ListenAlongStatus {
    Off,
    Finding,
    Playing,
    Paused,
    Missing,
    Failed,
}

#[derive(Debug, Clone)]
pub struct ListenAlongState {
    pub status: ListenAlongStatus,
    pub friend: Option<ListenAlongFriend>,
    pub service_name: String,
    pub key: String,
    pub start_ms: i64,
    pub end_ms: Option<i64>,
    pub uri: Option<String>,
    pub title: String,
    pub artist: String,
    pub paused: bool,
    pub moved_at_ms: i64,
    pub from_call: bool,
    pub seek_latency_ms: Option<f64>,
    pub turn: u64,
    pub away_deadline_ms: Option<i64>,
    pub end_deadline_ms: Option<i64>,
}

impl Default for ListenAlongState {
    fn default() -> Self {
        Self {
            status: ListenAlongStatus::Off,
            friend: None,
            service_name: String::new(),
            key: String::new(),
            start_ms: 0,
            end_ms: None,
            uri: None,
            title: String::new(),
            artist: String::new(),
            paused: false,
            moved_at_ms: 0,
            from_call: false,
            seek_latency_ms: None,
            turn: 0,
            away_deadline_ms: None,
            end_deadline_ms: None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CaptureFilter {
    All,
    Videos,
    Screenshots,
    Favorites,
}

pub struct MediaState {
    pub notes: crate::notes::NotesState,
    inner: Box<MediaStateInner>,
}

impl std::ops::Deref for MediaState {
    type Target = MediaStateInner;

    fn deref(&self) -> &Self::Target {
        &self.inner
    }
}

impl std::ops::DerefMut for MediaState {
    fn deref_mut(&mut self) -> &mut Self::Target {
        &mut self.inner
    }
}

pub struct MediaStateInner {
    pub music_service: MusicService,
    pub music_room_state: MusicRoomState,
    pub music_room_message: Option<String>,
    pub music_nav: MusicNav,
    pub music_history: Vec<MusicNav>,
    pub music_panel: Option<MusicPanel>,
    pub music_immersion: bool,
    pub playback: Option<MusicPlayback>,
    pub music_playback_anchor_s: f64,
    pub music_clock_s: f64,
    pub current_art_key: Option<String>,
    pub current_art: Option<Image>,
    pub music_volume: f64,
    pub music_last_audible_volume: f64,
    pub music_volume_known: bool,
    pub music_volume_source: Option<String>,
    pub music_volume_dragging: bool,
    pub music_muted: bool,
    pub music_seek_dragging: bool,
    pub music_seek_preview_ms: Option<f64>,
    pub music_shuffle: bool,
    pub music_repeat: i32,
    pub music_can_shuffle: bool,
    pub music_can_repeat: bool,
    pub music_liked: bool,
    pub music_disliked: bool,
    pub music_can_video: bool,
    pub music_video: bool,
    pub music_speed: f64,
    pub music_track_uri: String,
    pub music_player_title: Option<String>,
    pub music_player_image: Option<String>,
    pub music_player_artists: Vec<PlayerArtist>,
    pub music_elsewhere_device_id: Option<String>,
    pub music_bar_failed: Option<(String, f64)>,

    pub music_library: Vec<PlayerCard>,
    pub music_library_filters: Vec<PlayerOption>,
    pub music_library_top_filters: Vec<PlayerOption>,
    pub music_library_filter: String,
    pub music_library_sorts: Vec<PlayerOption>,
    pub music_library_sort: String,
    pub music_library_loaded: bool,
    pub music_library_error: Option<String>,
    pub music_library_failed: Option<String>,
    pub music_library_said: Option<(String, f64)>,
    pub music_folder_stack: Vec<PlayerCard>,
    pub music_folder_ref: Option<PlayerFolderRef>,
    pub music_folder_naming: Option<String>,
    pub music_folder_name_edit: TextEdit,

    pub music_recent: Vec<PlayerCard>,
    pub music_home_sections: Vec<PlayerSection>,
    pub music_page_data: Option<PlayerViewData>,
    pub music_page_loading: bool,
    pub music_page_error: Option<String>,
    pub music_page_more_loading: bool,
    pub music_page_removed: Vec<String>,
    pub music_page_saved: Option<bool>,
    pub music_page_status: Option<(String, f64)>,
    pub music_page_failed: Option<String>,
    pub music_about_expanded: bool,
    pub music_track_feedback: Option<(String, String, bool, f64)>,
    pub music_track_likes: HashMap<String, bool>,
    pub music_track_less: HashMap<String, bool>,
    pub music_track_in_lib: HashMap<String, bool>,

    pub music_songs_sort: String,
    pub music_songs_sorts: Vec<PlayerOption>,
    pub music_songs_filter: TextEdit,
    pub music_songs_applied_filter: String,
    pub music_songs_tracks: Vec<PlayerTrack>,
    pub music_songs_total: u32,
    pub music_songs_context_uri: String,
    pub music_songs_liked: bool,
    pub music_songs_loaded: bool,
    pub music_songs_loading_more: bool,
    pub music_songs_error: Option<String>,
    pub music_songs_removed: Vec<String>,

    pub music_search_edit: TextEdit,
    pub music_search_filter: String,
    pub music_search_only: String,
    pub music_search_query_loaded: String,

    pub music_queue_data: Option<PlayerViewData>,
    pub music_queue_history: Vec<PlayerTrack>,
    pub music_queue_error: Option<String>,
    pub music_queue_failed: bool,

    pub music_now_tab: NowPlayingTab,
    pub music_lyrics_uri: String,
    pub music_lyrics_lines: Vec<LyricsLine>,
    pub music_lyrics_synced: bool,
    pub music_lyrics_loading: bool,
    pub music_lyrics_error: Option<String>,
    pub music_related_uri: String,
    pub music_related_sections: Vec<PlayerSection>,
    pub music_related_loading: bool,
    pub music_related_error: Option<String>,

    pub music_devices: Vec<PlayerDevice>,
    pub music_devices_loaded: bool,
    pub music_devices_error: Option<String>,

    pub music_friends: Vec<PlayerFriend>,
    pub music_friends_loaded: bool,
    pub music_friends_error: Option<String>,
    pub music_friends_last_poll_s: f64,

    pub music_jam_session: Option<JamSession>,
    pub music_jam_experience: Option<String>,
    pub music_jam_loaded: bool,
    pub music_jam_busy: bool,
    pub music_jam_error: Option<String>,
    pub music_jam_join_edit: TextEdit,
    pub music_jam_join_wrong: bool,
    pub music_jam_last_poll_s: f64,
    pub music_jam_copied_until_s: f64,
    pub music_jam_end_armed: bool,

    pub music_credits_uri: String,
    pub music_credits_data: Option<PlayerViewData>,
    pub music_credits_loading: bool,
    pub music_credits_error: Option<String>,

    pub music_sound_spotify: Option<SpotifyAudio>,
    pub music_sound_qualities: Vec<PlayerOption>,
    pub music_sound_quality: String,
    pub music_sound_loading: bool,
    pub music_sound_error: Option<String>,
    pub music_sound_failed: Option<String>,
    pub music_sleep_deadline_ms: Option<i64>,

    pub music_user_playlists: Vec<PlayerCard>,
    pub music_user_playlists_rev: u64,
    pub music_playlist_sheet: Option<PlaylistSheetState>,
    pub music_menu: Option<MusicMenu>,
    pub music_delete_confirm_uri: Option<String>,

    pub music_call_songs: Vec<DiscordCallSong>,
    pub music_listen_along: ListenAlongState,
    pub music_match_cache: HashMap<String, Option<(String, String)>>,

    pub music_start_gen: u64,
    pub music_revision: u64,
    pub music_sign_in_pending: bool,
    pub music_sign_in_busy: bool,
    pub music_sign_in_message: Option<String>,
    pub music_sign_in_status: Option<String>,
    pub music_home_loading: bool,
    pub music_warmed_at_s: f64,
    pub music_restarts_s: Vec<f64>,
    pub music_signed_in_services: [bool; 3],
    pub music_start_time_s: f64,

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
    pub clip_editor_open: bool,
    pub viewer_note: Option<(String, bool)>,
    pub captures_ui: crate::captures::CapturesUiState,
    pub snip_editor: Option<crate::snip_editor::SnipEditorState>,
    pub lightbox: rune::LightboxState,
    pub lightbox_urls: Vec<String>,
}

impl MediaState {
    #[inline(never)]
    fn default_extra_music() -> Self {
        Self {
            notes: crate::notes::NotesState::default(),
            inner: Box::new(MediaStateInner {
                music_service: MusicService::Spotify,
                music_room_state: MusicRoomState::Loading,
                music_room_message: None,
                music_nav: MusicNav::Home,
                music_history: Vec::new(),
                music_panel: None,
                music_immersion: false,
                playback: None,
                music_playback_anchor_s: 0.0,
                music_clock_s: 0.0,
                current_art_key: None,
                current_art: None,
                music_volume: 0.75,
                music_last_audible_volume: 0.75,
                music_volume_known: false,
                music_volume_source: None,
                music_volume_dragging: false,
                music_muted: false,
                music_seek_dragging: false,
                music_seek_preview_ms: None,
                music_shuffle: false,
                music_repeat: 0,
                music_can_shuffle: false,
                music_can_repeat: false,
                music_liked: false,
                music_disliked: false,
                music_can_video: false,
                music_video: false,
                music_speed: 1.0,
                music_track_uri: String::new(),
                music_player_title: None,
                music_player_image: None,
                music_player_artists: Vec::new(),
                music_elsewhere_device_id: None,
                music_bar_failed: None,
                music_library: Vec::new(),
                music_library_filters: Vec::new(),
                music_library_top_filters: Vec::new(),
                music_library_filter: String::new(),
                music_library_sorts: Vec::new(),
                music_library_sort: String::new(),
                music_library_loaded: false,
                music_library_error: None,
                music_library_failed: None,
                music_library_said: None,
                music_folder_stack: Vec::new(),
                music_folder_ref: None,
                music_folder_naming: None,
                music_folder_name_edit: TextEdit::new(""),
                music_recent: Vec::new(),
                music_home_sections: Vec::new(),
                music_page_data: None,
                music_page_loading: false,
                music_page_error: None,
                music_page_more_loading: false,
                music_page_removed: Vec::new(),
                music_page_saved: None,
                music_page_status: None,
                music_page_failed: None,
                music_about_expanded: false,
                music_track_feedback: None,
                music_track_likes: HashMap::new(),
                music_track_less: HashMap::new(),
                music_track_in_lib: HashMap::new(),
                music_songs_sort: String::new(),
                music_songs_sorts: Vec::new(),
                music_songs_filter: TextEdit::new(""),
                music_songs_applied_filter: String::new(),
                music_songs_tracks: Vec::new(),
                music_songs_total: 0,
                music_songs_context_uri: String::new(),
                music_songs_liked: false,
                music_songs_loaded: false,
                music_songs_loading_more: false,
                music_songs_error: None,
                music_songs_removed: Vec::new(),
                music_search_edit: TextEdit::new(""),
                music_search_filter: String::new(),
                music_search_only: String::new(),
                music_search_query_loaded: String::new(),
                music_queue_data: None,
                music_queue_history: Vec::new(),
                music_queue_error: None,
                music_queue_failed: false,
                music_now_tab: NowPlayingTab::Lyrics,
                music_lyrics_uri: String::new(),
                music_lyrics_lines: Vec::new(),
                music_lyrics_synced: false,
                music_lyrics_loading: false,
                music_lyrics_error: None,
                music_related_uri: String::new(),
                music_related_sections: Vec::new(),
                music_related_loading: false,
                music_related_error: None,
                music_devices: Vec::new(),
                music_devices_loaded: false,
                music_devices_error: None,
                music_friends: Vec::new(),
                music_friends_loaded: false,
                music_friends_error: None,
                music_friends_last_poll_s: 0.0,
                music_jam_session: None,
                music_jam_experience: None,
                music_jam_loaded: false,
                music_jam_busy: false,
                music_jam_error: None,
                music_jam_join_edit: TextEdit::new(""),
                music_jam_join_wrong: false,
                music_jam_last_poll_s: 0.0,
                music_jam_copied_until_s: 0.0,
                music_jam_end_armed: false,
                music_credits_uri: String::new(),
                music_credits_data: None,
                music_credits_loading: false,
                music_credits_error: None,
                music_sound_spotify: None,
                music_sound_qualities: Vec::new(),
                music_sound_quality: String::new(),
                music_sound_loading: false,
                music_sound_error: None,
                music_sound_failed: None,
                music_sleep_deadline_ms: None,
                music_user_playlists: Vec::new(),
                music_user_playlists_rev: 0,
                music_playlist_sheet: None,
                music_menu: None,
                music_delete_confirm_uri: None,
                music_call_songs: Vec::new(),
                music_listen_along: ListenAlongState::default(),
                music_match_cache: HashMap::new(),
                music_start_gen: 0,
                music_revision: 0,
                music_sign_in_pending: false,
                music_sign_in_busy: false,
                music_sign_in_message: None,
                music_sign_in_status: None,
                music_home_loading: false,
                music_warmed_at_s: -10.0,
                music_restarts_s: Vec::new(),
                music_signed_in_services: [false; 3],
                music_start_time_s: 0.0,
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
                clip_editor_open: false,
                viewer_note: None,
                captures_ui: crate::captures::CapturesUiState::default(),
                snip_editor: None,
                lightbox: rune::LightboxState::new(),
                lightbox_urls: Vec::new(),
            }),
        }
    }

    pub fn demo() -> Self {
        let mut s = Self::default_extra_music();
        s.music_service = MusicService::Apple;
        s.music_room_state = MusicRoomState::Running;
        s.playback = Some(MusicPlayback {
            provider: "apple".into(),
            source: "session".into(),
            status: "playing".into(),
            title: "Midnight City".into(),
            artist: "M83".into(),
            album: "Hurry Up, We're Dreaming".into(),
            position_ms: Some(114_000),
            duration_ms: Some(243_000),
            rate: Some(1.0),
            can_play: true,
            can_pause: true,
            can_prev: true,
            can_next: true,
            can_seek: true,
            ..Default::default()
        });
        s.music_volume = 0.78;
        s.music_last_audible_volume = 0.78;
        s.music_volume_known = true;
        s.music_can_shuffle = true;
        s.music_can_repeat = true;
        s.music_library_loaded = true;
        s.music_library = vec![
            PlayerCard {
                uri: "am:liked".into(),
                kind: "collection".into(),
                title: "Favorites".into(),
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
        ];
        s.music_library_filters = vec![
            PlayerOption {
                id: "playlists".into(),
                name: "Playlists".into(),
                detail: None,
            },
            PlayerOption {
                id: "albums".into(),
                name: "Albums".into(),
                detail: None,
            },
            PlayerOption {
                id: "artists".into(),
                name: "Artists".into(),
                detail: None,
            },
        ];
        s.music_library_top_filters = s.music_library_filters.clone();
        s.music_recent = vec![
            PlayerCard {
                uri: "am:album:r1".into(),
                kind: "album".into(),
                title: "Peak".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r2".into(),
                kind: "album".into(),
                title: "The Way I Are".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r3".into(),
                kind: "album".into(),
                title: "Favorites".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r4".into(),
                kind: "album".into(),
                title: "The Eminem Show".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r5".into(),
                kind: "album".into(),
                title: "Hurry Up, We're Dreaming".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r6".into(),
                kind: "album".into(),
                title: "Currents".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r7".into(),
                kind: "album".into(),
                title: "Random Access Memories".into(),
                ..Default::default()
            },
            PlayerCard {
                uri: "am:album:r8".into(),
                kind: "album".into(),
                title: "Discovery".into(),
                ..Default::default()
            },
        ];
        s.music_home_sections = vec![PlayerSection {
            title: "Best New Songs".into(),
            more: None,
            items: vec![
                PlayerCard {
                    uri: "am:album:s1".into(),
                    kind: "album".into(),
                    title: "Midnight City".into(),
                    subtitle: "M83".into(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:s2".into(),
                    kind: "album".into(),
                    title: "Let It Happen".into(),
                    subtitle: "Tame Impala".into(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:s3".into(),
                    kind: "album".into(),
                    title: "Instant Crush".into(),
                    subtitle: "Daft Punk".into(),
                    ..Default::default()
                },
                PlayerCard {
                    uri: "am:album:s4".into(),
                    kind: "album".into(),
                    title: "Resonance".into(),
                    subtitle: "HOME".into(),
                    ..Default::default()
                },
            ],
        }];
        s.capture_status = Some(CaptureStatusReply {
            ok: true,
            present: true,
            folder: Some("Videos\\Exo Captures".into()),
            message: None,
            state: Some(CaptureLiveState {
                replay: true,
                running: true,
                buffering: true,
                recording: false,
                recording_ms: None,
                present: true,
                hotkeys: exo_rpc::CaptureHotkeys {
                    save_replay: true,
                    record: true,
                    snip: true,
                    print_screen: true,
                },
                ..Default::default()
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
                print_screen: false,
                after_snip: "edit".into(),
                folder: "Videos\\Exo Captures".into(),
                keep_gb: 50,
                keep_days: 30,
                auto_shrink: true,
                call_voices: false,
            }),
        });
        s.notes = crate::notes::NotesState::demo();
        s.capture_items = vec![
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
        ];
        s
    }

    pub fn empty_for_host() -> Self {
        Self::default_extra_music()
    }

    pub fn apply_player_state_data(&mut self, data: &PlayerStateData) {
        self.music_track_uri = data.track_uri.clone();
        self.music_shuffle = data.shuffle;
        self.music_repeat = data.repeat;
        self.music_can_shuffle = data.can_shuffle;
        self.music_can_repeat = data.can_repeat;
        self.music_liked = data.liked;
        self.music_disliked = data.disliked.unwrap_or(false);
        self.music_can_video = data.can_video.unwrap_or(false);
        self.music_video = data.video.unwrap_or(false);
        if let Some(sp) = data.speed
            && sp > 0.0
        {
            self.music_speed = sp;
        }
        self.music_player_image = data.image.clone().filter(|s| !s.is_empty());
        self.music_player_title = data.title.clone().filter(|s| !s.is_empty());
        self.music_player_artists = data.artists.clone();
    }

    pub fn service_idx(service: MusicService) -> usize {
        match service {
            MusicService::Spotify => 0,
            MusicService::Apple => 1,
            MusicService::YouTube => 2,
        }
    }

    pub fn current_position_ms(&self) -> Option<f64> {
        if let Some(preview) = self.music_seek_preview_ms {
            return Some(preview);
        }
        let pb = self.playback.as_ref()?;
        music_position_ms(pb, self.music_playback_anchor_s, self.music_clock_s)
    }

    pub fn sleep_minutes_left(&self) -> Option<i64> {
        let deadline = self.music_sleep_deadline_ms?;
        let now = now_epoch_ms();
        if deadline <= now {
            return None;
        }
        Some(((deadline - now) as f64 / 60_000.0).round().max(1.0) as i64)
    }
}

pub fn now_epoch_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

pub fn music_position_ms(pb: &MusicPlayback, anchor_s: f64, now_s: f64) -> Option<f64> {
    let pos = pb.position_ms? as f64;
    if !pos.is_finite() || pos < 0.0 {
        return None;
    }
    let rate = if pb.is_playing() {
        pb.rate.unwrap_or(1.0).max(0.0)
    } else {
        0.0
    };
    let elapsed_ms = ((now_s - anchor_s).max(0.0)) * 1000.0;
    let raw = pos + elapsed_ms * rate;
    match pb.duration_ms {
        Some(dur) if dur > 0 => Some(raw.min(dur as f64)),
        _ => Some(raw),
    }
}

pub fn same_song(a: &str, b: &str) -> bool {
    let fold = |s: &str| -> String {
        s.chars()
            .filter(|c| c.is_alphanumeric())
            .flat_map(|c| c.to_lowercase())
            .collect()
    };
    let x = fold(a);
    let y = fold(b);
    !x.is_empty() && !y.is_empty() && (x.contains(&y) || y.contains(&x))
}

pub fn can_listen_along(rich: Option<&DiscordRich>) -> bool {
    let Some(r) = rich else {
        return false;
    };
    r.activity_type == 2 && r.details.as_deref().is_some_and(|d| !d.is_empty()) && r.start.is_some()
}

pub fn expected_position_ms(start_ms: i64, wall_now_ms: i64, latency_ms: f64) -> f64 {
    ((wall_now_ms - start_ms) as f64 + latency_ms).max(0.0)
}

pub fn next_seek_latency_ms(prev: Option<f64>, sample: f64) -> Option<f64> {
    if !sample.is_finite() || sample < 0.0 {
        return prev;
    }
    let clamped = sample.min(1500.0);
    Some(match prev {
        None => clamped,
        Some(p) => (p + (clamped - p) / 3.0).round(),
    })
}

pub fn should_correct_listen_drift(
    from_call: bool,
    actual_ms: Option<f64>,
    expected_ms: f64,
    playing: bool,
    since_last_seek_ms: i64,
    ends_in_ms: Option<i64>,
) -> bool {
    let (tolerance, cooldown) = if from_call {
        (1500.0, 8000)
    } else {
        (5000.0, 20000)
    };
    let Some(actual) = actual_ms else {
        return false;
    };
    if !playing || !actual.is_finite() || since_last_seek_ms < cooldown {
        return false;
    }
    if let Some(ends) = ends_in_ms
        && ends < 3000
    {
        return false;
    }
    (actual - expected_ms).abs() > tolerance
}

pub fn parse_direct_jam_token(input: &str) -> Option<String> {
    let v = input.trim();
    if v.len() >= 4
        && v.len() <= 80
        && v.bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
    {
        return Some(v.to_string());
    }
    if let Some(rest) = v.strip_prefix("spotify:socialsession:") {
        let tok = rest.split(['?', '#', '/']).next().unwrap_or(rest).trim();
        if !tok.is_empty() {
            return Some(tok.to_string());
        }
    }
    None
}

pub fn validate_cover_image(bytes: &[u8]) -> Result<String, &'static str> {
    if bytes.len() < 24 {
        return Err("Choose a JPEG or PNG picture.");
    }
    if bytes[0] == 0xFF && bytes[1] == 0xD8 {
        let (w, h) = jpeg_dimensions(bytes).ok_or("Choose a JPEG or PNG picture.")?;
        if w.min(h) < 300 {
            return Err("Choose a picture at least 300 by 300 pixels.");
        }
        return Ok(format!(
            "data:image/jpeg;base64,{}",
            exo_rpc::base64::encode(bytes)
        ));
    }
    if bytes.starts_with(&[0x89, b'P', b'N', b'G', 0x0D, 0x0A, 0x1A, 0x0A]) {
        let w = u32::from_be_bytes([bytes[16], bytes[17], bytes[18], bytes[19]]);
        let h = u32::from_be_bytes([bytes[20], bytes[21], bytes[22], bytes[23]]);
        if w.min(h) < 300 {
            return Err("Choose a picture at least 300 by 300 pixels.");
        }
        return Err("Spotify requires a JPEG cover picture.");
    }
    Err("Choose a JPEG or PNG picture.")
}

fn jpeg_dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    let mut i = 2usize;
    while i + 9 < bytes.len() {
        if bytes[i] != 0xFF {
            i += 1;
            continue;
        }
        while i < bytes.len() && bytes[i] == 0xFF {
            i += 1;
        }
        if i >= bytes.len() {
            return None;
        }
        let marker = bytes[i];
        i += 1;
        if marker == 0xD8 || marker == 0xD9 || marker == 0x01 || (0xD0..=0xD7).contains(&marker) {
            continue;
        }
        if i + 2 > bytes.len() {
            return None;
        }
        let seg_len = u16::from_be_bytes([bytes[i], bytes[i + 1]]) as usize;
        if seg_len < 2 || i + seg_len > bytes.len() {
            return None;
        }
        if matches!(
            marker,
            0xC0 | 0xC1
                | 0xC2
                | 0xC3
                | 0xC5
                | 0xC6
                | 0xC7
                | 0xC9
                | 0xCA
                | 0xCB
                | 0xCD
                | 0xCE
                | 0xCF
        ) && seg_len >= 7
        {
            let h = u16::from_be_bytes([bytes[i + 3], bytes[i + 4]]) as u32;
            let w = u16::from_be_bytes([bytes[i + 5], bytes[i + 6]]) as u32;
            return Some((w, h));
        }
        i += seg_len;
    }
    None
}

pub fn lyrics_active_index(
    lines: &[LyricsLine],
    synced: bool,
    pos_ms: Option<f64>,
) -> Option<usize> {
    if !synced {
        return None;
    }
    let pos = pos_ms?;
    let mut active = None;
    for (i, line) in lines.iter().enumerate() {
        if line.start <= pos {
            active = Some(i);
        } else {
            break;
        }
    }
    active
}

pub fn lyrics_gaps(lines: &[LyricsLine]) -> Vec<Option<(f64, f64)>> {
    lines
        .iter()
        .enumerate()
        .map(|(idx, line)| {
            let before = if idx == 0 {
                Some(0.0)
            } else {
                lines[idx - 1].words.iter().map(|w| w.end).reduce(f64::max)
            }?;
            if line.start - before >= 4000.0 {
                Some((before, line.start))
            } else {
                None
            }
        })
        .collect()
}

pub fn format_time_clock(ms: Option<f64>) -> String {
    let Some(v) = ms.filter(|v| v.is_finite() && *v >= 0.0) else {
        return "–:––".into();
    };
    let secs = (v / 1000.0).floor() as u64;
    format!("{}:{:02}", secs / 60, secs % 60)
}

pub fn format_ago_ms(at_ms: i64, now_ms: i64) -> String {
    let minutes = ((now_ms - at_ms) as f64 / 60_000.0).round() as i64;
    if minutes < 60 {
        return format!("{} min", minutes.max(1));
    }
    let hours = (minutes as f64 / 60.0).round() as i64;
    if hours < 24 {
        format!("{hours} hr")
    } else {
        format!("{} d", (hours as f64 / 24.0).round() as i64)
    }
}

pub fn format_commas(n: u64) -> String {
    let s = n.to_string();
    let mut out = String::with_capacity(s.len() + s.len() / 3);
    for (i, ch) in s.chars().enumerate() {
        let rem = s.len() - i;
        if i > 0 && rem.is_multiple_of(3) {
            out.push(',');
        }
        out.push(ch);
    }
    out
}

pub fn page_for_card(card: &PlayerCard) -> Option<MusicNav> {
    match card.kind.as_str() {
        "collection" => Some(MusicNav::Collection {
            uri: card.uri.clone(),
            kind: "collection".into(),
            title: card.title.clone(),
            edit: false,
        }),
        "playlist" | "album" | "artist" | "show" => Some(MusicNav::Collection {
            uri: card.uri.clone(),
            kind: card.kind.clone(),
            title: card.title.clone(),
            edit: false,
        }),
        "episodes" => Some(MusicNav::Collection {
            uri: card.uri.clone(),
            kind: "playlist".into(),
            title: card.title.clone(),
            edit: false,
        }),
        "browse" | "mood" => Some(MusicNav::Browse {
            uri: (!card.uri.is_empty()).then(|| card.uri.clone()),
            title: (!card.title.is_empty()).then(|| card.title.clone()),
        }),
        "discography" => Some(MusicNav::Discography {
            uri: card.uri.clone(),
            title: card.title.clone(),
        }),
        _ => None,
    }
}

impl AppState {
    pub fn open_music_settings(&mut self) {
        self.pc.settings_section = SettingsSection::Updates;
        self.switch_room(Room::Settings);
    }

    pub fn sync_music_service_from_settings(&mut self) {
        let pref = self
            .settings
            .ui_prefs
            .get("exo.musicWebService")
            .or_else(|| self.settings.ui_prefs.get("musicService"))
            .and_then(|v| v.as_str());
        let next = MusicService::from_pref(pref);
        if self.media.music_service != next {
            self.media.music_service = next;
            self.media.music_room_state = MusicRoomState::Loading;
            self.media.music_room_message = None;
            self.media.music_nav = MusicNav::Home;
            self.media.music_history.clear();
            self.media.music_panel = None;
            self.media.music_menu = None;
            self.media.music_playlist_sheet = None;
            self.media.music_library.clear();
            self.media.music_library_filters.clear();
            self.media.music_library_top_filters.clear();
            self.media.music_library_filter.clear();
            self.media.music_library_sorts.clear();
            self.media.music_library_sort.clear();
            self.media.music_library_loaded = false;
            self.media.music_library_error = None;
            self.media.music_folder_stack.clear();
            self.media.music_folder_ref = None;
            self.media.music_folder_naming = None;
            self.media.music_recent.clear();
            self.media.music_home_sections.clear();
            self.media.music_page_data = None;
            self.media.music_page_error = None;
            self.media.music_queue_data = None;
            self.media.music_queue_history.clear();
            self.media.music_lyrics_uri.clear();
            self.media.music_lyrics_lines.clear();
            self.media.music_devices.clear();
            self.media.music_devices_loaded = false;
            self.media.music_friends.clear();
            self.media.music_friends_loaded = false;
            self.media.music_jam_session = None;
            self.media.music_jam_loaded = false;
            self.media.music_sound_spotify = None;
            self.media.music_sound_qualities.clear();
            self.media.music_songs_tracks.clear();
            self.media.music_songs_loaded = false;
            self.media.music_user_playlists.clear();
            self.media.music_sign_in_pending = false;
            self.media.music_sign_in_busy = false;
            self.media.music_sign_in_message = None;
            self.media.music_sign_in_status = None;
            self.media.music_home_loading = false;
            self.media.music_start_gen = self.media.music_start_gen.wrapping_add(1);
            self.media.music_start_time_s = self.media.music_clock_s;
            self.stop_listen_along(None);
        }
    }

    pub fn warm_music(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        if self.media.music_clock_s - self.media.music_warmed_at_s < WARM_COOLDOWN_S {
            return;
        }
        self.media.music_warmed_at_s = self.media.music_clock_s;
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_MUSIC_WARM, &json!({}), |_| {});
        }
    }

    fn music_open_raw_external_url(&mut self, url: &str) {
        if self.status == HostStatus::Standalone || url.is_empty() {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_SHELL_OPEN_URL, &json!({ "url": url }), |_| {});
        }
        #[cfg(not(windows))]
        let _ = url;
    }

    pub fn music_open_external_url(&mut self, url: &str) {
        if self.media.music_service == MusicService::Spotify
            && (url.starts_with("https://open.spotify.com/")
                || url.starts_with("https://spotify.link/"))
        {
            self.music_open_spotify_link(url);
            return;
        }
        self.music_open_raw_external_url(url);
    }

    pub fn music_open_spotify_link(&mut self, url: &str) {
        if self.status == HostStatus::Standalone || url.is_empty() {
            return;
        }
        if self.media.music_service != MusicService::Spotify {
            self.music_open_raw_external_url(url);
            return;
        }
        let fallback = url.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SpotifyLinkReply>(
                METHOD_SPOTIFY_LINK,
                &json!({ "url": url }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(reply) = res
                            && reply.ok
                            && let Some(uri) = reply.uri.filter(|u| !u.is_empty())
                        {
                            s.music_handle_resolved_spotify_uri(&uri);
                        } else {
                            s.music_open_raw_external_url(&fallback);
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = fallback;
    }

    pub fn music_handle_resolved_spotify_uri(&mut self, uri: &str) {
        let parts: Vec<&str> = uri.split(':').collect();
        let (kind, id) = match (parts.get(1), parts.get(2)) {
            (Some(&k), Some(&i)) if !i.is_empty() => (k, i),
            _ => return,
        };
        self.switch_room(Room::Music);
        match kind {
            "socialsession" => {
                self.media.music_panel = Some(MusicPanel::Jam);
                self.music_jam_action("jamJoin", json!({ "name": id }));
            }
            "track" | "episode" => {
                self.music_play_uri(uri, None, None);
                self.media.music_panel = Some(MusicPanel::NowPlaying);
            }
            "playlist" | "album" | "artist" | "show" => {
                self.music_open_nav(MusicNav::Collection {
                    uri: uri.to_string(),
                    kind: kind.to_string(),
                    title: String::new(),
                    edit: false,
                });
            }
            _ => {}
        }
    }

    pub fn load_music_room(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        self.sync_music_service_from_settings();
        if matches!(self.media.music_room_state, MusicRoomState::Error(_)) {
            self.media.music_room_state = MusicRoomState::Loading;
            self.media.music_room_message = None;
        }
        self.media.music_start_gen = self.media.music_start_gen.wrapping_add(1);
        self.media.music_start_time_s = self.media.music_clock_s;
        let generation = self.media.music_start_gen;
        let service = self.media.music_service;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, MusicPlaybackReply>(METHOD_MUSIC_PLAYBACK, &json!({}), move |res| {
                if let Ok(reply) = res {
                    h1.update_if(move |s, _| {
                        let changed = s.apply_music_playback(reply.playback);
                        s.on_music_playback_changed();
                        changed
                    });
                }
            });
            let h2 = handle.clone();
            rpc.call::<_, MusicVolumeReply>(METHOD_MUSIC_VOLUME, &json!({}), move |res| {
                if let Ok(v) = res {
                    h2.update(move |s, _| {
                        if let Some(lvl) = v.level {
                            s.media.music_volume = lvl;
                            s.media.music_volume_known = true;
                            if lvl > 0.0 {
                                s.media.music_last_audible_volume = lvl;
                            }
                        }
                        s.media.music_muted = v.muted;
                        s.media.music_volume_source = v.source;
                    });
                }
            });
            self.poll_music_start(service, generation, 0);
        }
        #[cfg(not(windows))]
        let _ = (generation, service);
    }

    pub fn on_music_status(&mut self, ev: MusicStatusEvent) -> bool {
        if self.room != Room::Music
            || self.media.music_room_state != MusicRoomState::Running
            || ev.service != self.media.music_service.id()
            || ev.state != "error"
        {
            return false;
        }
        let now = self.media.music_clock_s;
        self.media.music_restarts_s.retain(|&t| now - t < 60.0);
        if self.media.music_restarts_s.len() >= 2 {
            let msg = format!(
                "{} stopped. Open it again.",
                self.media.music_service.label()
            );
            self.media.music_room_message = Some(msg.clone());
            self.media.music_room_state = MusicRoomState::Error(msg);
            return true;
        }
        self.media.music_restarts_s.push(now);
        self.media.music_room_state = MusicRoomState::Loading;
        self.load_music_room();
        true
    }

    pub fn on_music_playback_changed(&mut self) {
        let now = self.media.music_clock_s;
        let age_s = self
            .media
            .playback
            .as_ref()
            .and_then(|p| p.position_age_ms)
            .filter(|a| a.is_finite() && *a > 0.0)
            .unwrap_or(0.0)
            / 1000.0;
        self.media.music_playback_anchor_s = (now - age_s).max(0.0);

        if self.media.music_listen_along.status != ListenAlongStatus::Off {
            self.listen_along_on_playback();
        }

        if self.room == Room::Music && self.media.music_room_state == MusicRoomState::Running {
            self.load_music_player_state();
            if self.media.music_panel == Some(MusicPanel::Queue) {
                self.load_music_queue();
            }
        }
    }

    pub fn music_tick(&mut self, now_s: f64) -> bool {
        let prev_sec = self.media.music_clock_s.floor() as i64;
        self.media.music_clock_s = now_s;
        let mut redraw = false;

        if let Some(deadline) = self.media.music_sleep_deadline_ms
            && now_epoch_ms() >= deadline
        {
            self.media.music_sleep_deadline_ms = None;
            self.music_control("pause");
            redraw = true;
        }

        if let Some((_, until)) = self.media.music_bar_failed
            && now_s >= until
        {
            self.media.music_bar_failed = None;
            redraw = true;
        }
        if let Some((_, _, _, until)) = self.media.music_track_feedback
            && now_s >= until
        {
            self.media.music_track_feedback = None;
            redraw = true;
        }
        if let Some((_, until)) = self.media.music_library_said
            && now_s >= until
        {
            self.media.music_library_said = None;
            redraw = true;
        }
        if let Some((_, until)) = self.media.music_page_status
            && now_s >= until
        {
            self.media.music_page_status = None;
            redraw = true;
        }
        if self.media.music_jam_copied_until_s > 0.0 && now_s >= self.media.music_jam_copied_until_s
        {
            self.media.music_jam_copied_until_s = 0.0;
            redraw = true;
        }

        if self.media.music_listen_along.status != ListenAlongStatus::Off {
            let wall = now_epoch_ms();
            if let Some(away) = self.media.music_listen_along.away_deadline_ms
                && wall >= away
            {
                self.media.music_listen_along.away_deadline_ms = None;
                self.media.music_listen_along.paused = true;
                self.media.music_listen_along.status = ListenAlongStatus::Paused;
                self.music_control("pause");
                redraw = true;
            }
            if let Some(end) = self.media.music_listen_along.end_deadline_ms
                && wall >= end
            {
                let name = self
                    .media
                    .music_listen_along
                    .friend
                    .as_ref()
                    .map(|f| f.name.clone())
                    .unwrap_or_default();
                self.stop_listen_along(Some(format!("{name} stopped listening.")));
                redraw = true;
            }
        }

        if self.room == Room::Music && self.media.music_room_state == MusicRoomState::Running {
            let cur_sec = now_s.floor() as i64;
            let playing = self.media.playback.as_ref().is_some_and(|p| p.is_playing());
            if playing
                && (self.media.music_immersion
                    || self.media.music_panel == Some(MusicPanel::NowPlaying))
                && (self.media.music_lyrics_synced || cur_sec != prev_sec)
            {
                redraw = true;
            }
            if self.media.music_panel == Some(MusicPanel::Friends)
                && now_s - self.media.music_friends_last_poll_s >= FRIENDS_POLL_S
            {
                self.media.music_friends_last_poll_s = now_s;
                self.load_music_friends();
            }
            if self.media.music_panel == Some(MusicPanel::Jam) && self.media.music_service.has_jam()
            {
                let interval = if self.media.music_jam_session.is_some() {
                    JAM_ACTIVE_POLL_S
                } else {
                    JAM_IDLE_POLL_S
                };
                if now_s - self.media.music_jam_last_poll_s >= interval {
                    self.media.music_jam_last_poll_s = now_s;
                    self.load_music_jam();
                }
            }
        }
        redraw
    }

    #[cfg(windows)]
    fn poll_music_start(&mut self, service: MusicService, generation: u64, attempt: u32) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(start_method) = service.start_method() else {
            return;
        };
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h = handle.clone();
            rpc.call::<_, PlayerStartReply>(start_method, &json!({}), move |res| {
                let mapped = res.map_err(|e| e.to_string());
                h.update(move |s, _| {
                    if s.media.music_start_gen != generation || s.media.music_service != service {
                        return;
                    }
                    let in_music_room = s.room == Room::Music;
                    let idx = MediaState::service_idx(service);
                    let mapped = match mapped {
                        Ok(mut reply)
                            if reply.state == "signedOut"
                                && s.media.music_signed_in_services[idx]
                                && (s.media.music_clock_s - s.media.music_start_time_s) < 15.0 =>
                        {
                            reply.state = "loading".into();
                            Ok(reply)
                        }
                        other => other,
                    };
                    match apply_music_start_reply(&mut s.media, in_music_room, mapped, attempt) {
                        MusicStartAction::LoadViews => {
                            s.media.music_signed_in_services[idx] = true;
                            s.load_music_views();
                            if s.media.music_nav != MusicNav::Home {
                                s.load_music_current_nav();
                            }
                        }
                        MusicStartAction::RetryAfter {
                            delay_ms,
                            next_attempt,
                        } => {
                            if s.media.music_room_state == MusicRoomState::SignedOut {
                                s.media.music_signed_in_services[idx] = false;
                            }
                            s.schedule_music_start_retry(
                                service,
                                generation,
                                next_attempt,
                                delay_ms,
                            );
                        }
                        MusicStartAction::Stop => {
                            if s.media.music_room_state == MusicRoomState::SignedOut {
                                s.media.music_signed_in_services[idx] = false;
                            }
                        }
                    }
                });
            });
        }
    }

    #[cfg(windows)]
    fn schedule_music_start_retry(
        &self,
        service: MusicService,
        generation: u64,
        next_attempt: u32,
        delay_ms: u64,
    ) {
        if let Some(handle) = self.handle.clone() {
            let _ = std::thread::Builder::new()
                .name("exo-music-retry".into())
                .spawn(move || {
                    std::thread::sleep(std::time::Duration::from_millis(delay_ms));
                    handle.update(move |s, _| {
                        if s.room == Room::Music
                            && s.media.music_start_gen == generation
                            && s.media.music_service == service
                        {
                            s.poll_music_start(service, generation, next_attempt);
                        }
                    });
                });
        }
    }

    pub fn load_music_views(&mut self) {
        self.load_music_views_attempt(self.media.music_start_gen, 0);
    }

    fn load_music_views_attempt(&mut self, generation: u64, retry: u32) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        let has_recent = service.has_recent();
        if self.media.music_home_sections.is_empty() && self.media.music_recent.is_empty() {
            self.media.music_home_loading = true;
        }
        self.load_music_library();
        self.load_music_player_state();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h_home = handle.clone();
            rpc.call::<_, PlayerViewReply>(view_method, &json!({ "view": "home" }), move |res| {
                let sections = res
                    .ok()
                    .filter(|reply| reply.ok)
                    .and_then(|reply| reply.data)
                    .map(|data| data.sections)
                    .filter(|sections| !sections.is_empty());
                h_home.update(move |s, _| {
                    if s.media.music_start_gen != generation || s.media.music_service != service {
                        return;
                    }
                    if let Some(sections) = sections {
                        s.media.music_home_sections = sections;
                        s.media.music_home_loading = false;
                        return;
                    }
                    if retry < 2
                        && s.room == Room::Music
                        && s.media.music_room_state == MusicRoomState::Running
                        && s.media.music_home_sections.is_empty()
                        && let Some(retry_handle) = s.handle.clone()
                    {
                        let next_retry = retry + 1;
                        let _ = std::thread::Builder::new()
                            .name("exo-music-home-retry".into())
                            .spawn(move || {
                                std::thread::sleep(std::time::Duration::from_millis(
                                    MUSIC_HOME_RETRY_MS,
                                ));
                                retry_handle.update(move |s, _| {
                                    if s.room == Room::Music
                                        && s.media.music_start_gen == generation
                                        && s.media.music_service == service
                                        && s.media.music_room_state == MusicRoomState::Running
                                        && s.media.music_home_sections.is_empty()
                                    {
                                        s.load_music_views_attempt(generation, next_retry);
                                    }
                                });
                            });
                        return;
                    }
                    s.media.music_home_loading = false;
                });
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
                                if s.media.music_service != service {
                                    return;
                                }
                                if !data.items.is_empty() {
                                    s.media.music_home_loading = false;
                                }
                                s.media.music_recent = data.items;
                            });
                        }
                    },
                );
            }
        }
        #[cfg(not(windows))]
        let _ = (generation, retry, has_recent);
    }

    pub fn load_music_library(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        let folder_uri = self.media.music_folder_stack.last().map(|f| f.uri.clone());
        let filter = self.media.music_library_filter.clone();
        let sort = self.media.music_library_sort.clone();
        let query = format!("{filter}|{sort}");
        let mut params = json!({ "view": "library", "query": query });
        if let Some(ref u) = folder_uri {
            params["uri"] = json!(u);
        }
        let unfiltered = filter.is_empty() && folder_uri.is_none();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(view_method, &params, move |res| {
                handle.update(move |s, _| {
                    if s.media.music_service != service {
                        return;
                    }
                    match res {
                        Ok(reply) if reply.ok => {
                            if let Some(data) = reply.data {
                                s.media.music_library = data.items;
                                s.media.music_library_loaded = true;
                                s.media.music_library_error = None;
                                if unfiltered && !data.filters.is_empty() {
                                    s.media.music_library_top_filters = data.filters.clone();
                                }
                                if !data.filters.is_empty() {
                                    s.media.music_library_filters = data.filters;
                                }
                                if !data.sorts.is_empty() {
                                    s.media.music_library_sorts = data.sorts;
                                }
                                if let Some(f) = data.filter
                                    && s.media.music_library_filter.is_empty()
                                {
                                    s.media.music_library_filter = f;
                                }
                                if let Some(so) = data.sort
                                    && s.media.music_library_sort.is_empty()
                                {
                                    s.media.music_library_sort = so;
                                }
                                s.media.music_folder_ref = data.folder;
                            }
                        }
                        Ok(reply) => {
                            s.media.music_library_error =
                                Some(reply.message.unwrap_or_else(|| {
                                    format!("{} could not load your library.", service.label())
                                }));
                        }
                        Err(e) => {
                            s.media.music_library_error = Some(e.to_string());
                        }
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (params, unfiltered);
    }

    pub fn load_music_player_state(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerStateReply>(
                view_method,
                &json!({ "view": "player" }),
                move |res| {
                    if let Ok(reply) = res
                        && reply.ok
                        && let Some(data) = reply.data
                    {
                        handle.update(move |s, _| {
                            if s.media.music_service != service {
                                return;
                            }
                            let prev_uri = s.media.music_track_uri.clone();
                            s.media.apply_player_state_data(&data);
                            if s.media.music_track_uri != prev_uri
                                && !s.media.music_track_uri.is_empty()
                                && (s.media.music_immersion
                                    || s.media.music_panel == Some(MusicPanel::NowPlaying))
                            {
                                let uri = s.media.music_track_uri.clone();
                                s.load_music_lyrics(&uri);
                                if s.media.music_now_tab == NowPlayingTab::Related
                                    && s.media.music_service.has_related()
                                {
                                    s.load_music_related(&uri);
                                }
                            }
                        });
                    }
                },
            );
        }
    }

    pub fn load_music_user_playlists(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        if !self.media.music_user_playlists.is_empty()
            && self.media.music_user_playlists_rev == self.media.music_revision
        {
            return;
        }
        let service = self.media.music_service;
        let rev = self.media.music_revision;
        let Some(view_method) = service.view_method() else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "playlists" }),
                move |res| {
                    if let Ok(reply) = res
                        && reply.ok
                        && let Some(data) = reply.data
                    {
                        handle.update(move |s, _| {
                            if s.media.music_service == service {
                                s.media.music_user_playlists = data.items;
                                s.media.music_user_playlists_rev = rev;
                            }
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = rev;
    }

    pub fn music_bump_revision(&mut self) {
        self.media.music_revision = self.media.music_revision.wrapping_add(1);
        self.load_music_library();
        self.load_music_player_state();
        if self.media.music_nav != MusicNav::Home && self.media.music_nav != MusicNav::Songs {
            self.load_music_current_nav();
        }
    }

    pub fn music_set_library_filter(&mut self, filter_id: String) {
        self.media.music_folder_stack.clear();
        self.media.music_folder_ref = None;
        self.media.music_library_filter = filter_id;
        self.media.music_library_sort.clear();
        self.media.music_menu = None;
        self.load_music_library();
    }

    pub fn music_set_library_sort(&mut self, sort_id: String) {
        self.media.music_library_sort = sort_id;
        self.media.music_menu = None;
        self.load_music_library();
    }

    pub fn music_open_folder(&mut self, card: PlayerCard) {
        self.media.music_folder_stack.push(card);
        self.media.music_folder_naming = None;
        self.load_music_library();
    }

    pub fn music_folder_back(&mut self) {
        if self.media.music_folder_stack.pop().is_some() {
            self.media.music_folder_naming = None;
            self.load_music_library();
        }
    }

    pub fn music_start_create_folder(&mut self) {
        self.media.music_menu = None;
        self.media.music_folder_naming = Some("new".into());
        self.media.music_folder_name_edit = TextEdit::new("New Folder");
    }

    pub fn music_start_rename_folder(&mut self, uri: String, current_name: String) {
        self.media.music_menu = None;
        self.media.music_folder_naming = Some(uri);
        self.media.music_folder_name_edit = TextEdit::new(&current_name);
    }

    pub fn music_commit_folder_name(&mut self) {
        let Some(target) = self.media.music_folder_naming.take() else {
            return;
        };
        let name = self.media.music_folder_name_edit.text.trim().to_string();
        if name.is_empty() {
            return;
        }
        if target == "new" {
            let folder = self.media.music_folder_stack.last().map(|f| f.uri.clone());
            let mut body = json!({ "name": name });
            if let Some(ctx) = folder {
                body["context"] = json!(ctx);
            }
            self.music_run_library_action("createFolder", body);
        } else {
            self.music_run_library_action("renameFolder", json!({ "uri": target, "name": name }));
        }
    }

    pub fn music_run_library_action(&mut self, action: &str, body: Value) {
        self.media.music_library_failed = None;
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        let params = encode_act_params(service, action, body);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(act_method, &params, move |res| {
                handle.update(move |s, _| {
                    if let Ok(r) = res
                        && r.ok
                    {
                        s.music_bump_revision();
                    } else {
                        s.media.music_library_failed =
                            Some(format!("{} did not take that. Try again.", service.label()));
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, params);
    }

    pub fn music_start_new_playlist(&mut self, track_uri: Option<String>) {
        self.media.music_menu = None;
        let folder = self.media.music_folder_stack.last().map(|f| f.uri.clone());
        let owned_count = self
            .media
            .music_library
            .iter()
            .filter(|c| c.owned && c.kind == "playlist")
            .count();
        if let Some(auto_name) = self.media.music_service.new_playlist_name(owned_count) {
            let service = self.media.music_service;
            let Some(act_method) = service.act_method() else {
                return;
            };
            let mut body = json!({ "name": auto_name });
            if let Some(ref f) = folder {
                body["context"] = json!(f);
            }
            if let Some(ref t) = track_uri {
                body["uri"] = json!(t);
            }
            let params = encode_act_params(service, "createPlaylist", body);
            let open_after = track_uri.is_none();
            #[cfg(windows)]
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                rpc.call::<_, PlayerActReply>(act_method, &params, move |res| {
                    handle.update(move |s, _| {
                        if let Ok(r) = res
                            && r.ok
                        {
                            s.music_bump_revision();
                            if open_after && let Some(uri) = r.uri.filter(|u| !u.is_empty()) {
                                s.music_open_nav(MusicNav::Collection {
                                    uri,
                                    kind: "playlist".into(),
                                    title: auto_name,
                                    edit: true,
                                });
                            }
                        } else {
                            s.media.music_library_failed = Some(format!(
                                "{} could not make a playlist. Try again.",
                                service.label()
                            ));
                        }
                    });
                });
            }
            #[cfg(not(windows))]
            let _ = (act_method, params, open_after);
        } else {
            let privacy =
                (self.media.music_service == MusicService::YouTube).then(|| "PRIVATE".to_string());
            self.media.music_playlist_sheet = Some(PlaylistSheetState {
                uri: None,
                orig_title: String::new(),
                orig_desc: String::new(),
                orig_privacy: privacy.clone(),
                name_edit: TextEdit::new(""),
                desc_edit: TextEdit::new(""),
                privacy,
                image_url: String::new(),
                cover_data_url: None,
                cover_preview: None,
                track_uri,
                folder_uri: folder,
                busy: false,
                error: None,
            });
        }
    }

    pub fn music_open_edit_playlist_sheet(
        &mut self,
        uri: String,
        title: String,
        description: String,
        privacy: Option<String>,
        image_url: String,
    ) {
        self.media.music_menu = None;
        self.media.music_playlist_sheet = Some(PlaylistSheetState {
            uri: Some(uri),
            orig_title: title.clone(),
            orig_desc: description.clone(),
            orig_privacy: privacy.clone(),
            name_edit: TextEdit::new(&title),
            desc_edit: TextEdit::new(&description),
            privacy,
            image_url,
            cover_data_url: None,
            cover_preview: None,
            track_uri: None,
            folder_uri: None,
            busy: false,
            error: None,
        });
    }

    pub fn music_playlist_sheet_pick_file(&mut self, bytes: Vec<u8>) {
        let Some(ref mut sheet) = self.media.music_playlist_sheet else {
            return;
        };
        sheet.error = None;
        match validate_cover_image(&bytes) {
            Ok(data_url) => {
                sheet.cover_data_url = Some(data_url);
                if let Some(cache) = self.image_cache.as_ref() {
                    sheet.cover_preview = Some(cache.load_capped(bytes, 176.0, 1.5));
                }
            }
            Err(msg) => {
                sheet.error = Some(msg.into());
            }
        }
    }

    pub fn music_playlist_sheet_save(&mut self) {
        let Some(sheet) = self.media.music_playlist_sheet.as_mut() else {
            return;
        };
        let name = sheet.name_edit.text.trim().to_string();
        if name.is_empty() || sheet.busy {
            return;
        }
        let desc = sheet.desc_edit.text.trim().to_string();
        let privacy = sheet.privacy.clone();
        let cover = sheet.cover_data_url.clone();
        let creating = sheet.uri.is_none();
        let existing_uri = sheet.uri.clone();
        let track_uri = sheet.track_uri.clone();
        let folder_uri = sheet.folder_uri.clone();
        let changed = creating
            || name != sheet.orig_title
            || desc != sheet.orig_desc
            || privacy != sheet.orig_privacy;
        sheet.busy = true;
        sheet.error = None;

        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };

        if !changed && cover.is_none() {
            self.media.music_playlist_sheet = None;
            return;
        }

        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let finish_cover = move |rpc_c: std::sync::Arc<exo_rpc::RpcClient>,
                                     handle_c: rune::AppHandle<AppState>,
                                     target_uri: Option<String>,
                                     cover_opt: Option<String>| {
                if let (Some(u), Some(img)) = (target_uri, cover_opt) {
                    let params = encode_act_params(
                        service,
                        "playlistImage",
                        json!({ "uri": u, "image": img }),
                    );
                    rpc_c.call::<_, PlayerActReply>(act_method, &params, move |res| {
                        handle_c.update(move |s, _| {
                            if let Ok(ref r) = res
                                && r.ok
                            {
                                s.media.music_playlist_sheet = None;
                                s.music_bump_revision();
                            } else if let Some(ref mut sh) = s.media.music_playlist_sheet {
                                sh.busy = false;
                                sh.error =
                                    Some(res.ok().and_then(|r| r.message).unwrap_or_else(|| {
                                        format!("{} did not take that. Try again.", service.label())
                                    }));
                            }
                        });
                    });
                } else {
                    handle_c.update(move |s, _| {
                        s.media.music_playlist_sheet = None;
                        s.music_bump_revision();
                    });
                }
            };

            if !changed {
                finish_cover(rpc, handle, existing_uri, cover);
                return;
            }

            let action = if creating {
                "createPlaylist"
            } else {
                "editPlaylist"
            };
            let mut body = json!({ "name": name, "description": desc });
            if let Some(p) = privacy {
                body["privacy"] = json!(p);
            }
            if creating {
                if let Some(t) = track_uri {
                    body["uri"] = json!(t);
                }
                if let Some(f) = folder_uri {
                    body["context"] = json!(f);
                }
            } else if let Some(ref u) = existing_uri {
                body["uri"] = json!(u);
            }
            let params = encode_act_params(service, action, body);
            let rpc2 = rpc.clone();
            rpc.call::<_, PlayerActReply>(act_method, &params, move |res| match res {
                Ok(r) if r.ok => {
                    let target_uri = if creating { r.uri } else { existing_uri };
                    finish_cover(rpc2, handle, target_uri, cover);
                }
                other => {
                    let msg = other.ok().and_then(|r| r.message).unwrap_or_else(|| {
                        format!("{} did not take that. Try again.", service.label())
                    });
                    handle.update(move |s, _| {
                        if let Some(ref mut sh) = s.media.music_playlist_sheet {
                            sh.busy = false;
                            sh.error = Some(msg);
                        }
                    });
                }
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, existing_uri, track_uri, folder_uri, cover);
    }

    pub fn music_open_nav(&mut self, nav: MusicNav) {
        self.media.music_menu = None;
        self.media.music_delete_confirm_uri = None;
        if self.media.music_nav != nav {
            let prev = self.media.music_nav.clone();
            self.media.music_history.push(prev);
            self.media.music_nav = nav.clone();
            self.media.music_page_data = None;
            self.media.music_page_error = None;
            self.media.music_page_removed.clear();
            self.media.music_page_saved = None;
            self.media.music_page_status = None;
            self.media.music_page_failed = None;
            self.media.music_about_expanded = false;
            self.media.music_track_likes.clear();
            self.media.music_track_less.clear();
            self.media.music_track_in_lib.clear();
        }
        self.load_music_current_nav();
    }

    pub fn music_reset_nav(&mut self, nav: MusicNav) {
        self.media.music_menu = None;
        self.media.music_delete_confirm_uri = None;
        self.media.music_history.clear();
        self.media.music_search_edit = TextEdit::new("");
        self.media.music_search_filter.clear();
        self.media.music_search_only.clear();
        self.media.music_nav = nav;
        self.media.music_page_data = None;
        self.media.music_page_error = None;
        self.media.music_page_removed.clear();
        self.media.music_page_saved = None;
        self.media.music_about_expanded = false;
        self.load_music_current_nav();
    }

    pub fn music_go_back(&mut self) {
        self.media.music_menu = None;
        if let Some(prev) = self.media.music_history.pop() {
            self.media.music_nav = prev;
            self.media.music_page_data = None;
            self.media.music_page_error = None;
            self.media.music_page_removed.clear();
            self.media.music_page_saved = None;
            self.media.music_about_expanded = false;
            self.load_music_current_nav();
        }
    }

    pub fn music_playlist_deleted(&mut self, uri: &str) {
        let on_deleted = matches!(&self.media.music_nav, MusicNav::Collection { uri: u, .. } if u == uri)
            || self
                .media
                .music_history
                .iter()
                .any(|n| matches!(n, MusicNav::Collection { uri: u, .. } if u == uri));
        if on_deleted {
            self.music_reset_nav(MusicNav::Home);
        }
        self.music_bump_revision();
    }

    pub fn load_music_songs(&mut self, append: bool) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        let sort = self.media.music_songs_sort.clone();
        let filter = self.media.music_songs_filter.text.trim().to_string();
        let query = format!("{sort}|{filter}");
        let offset = if append {
            self.media.music_songs_tracks.len()
        } else {
            self.media.music_songs_removed.clear();
            self.media.music_songs_applied_filter = filter;
            0
        };
        if append {
            if self.media.music_songs_loading_more {
                return;
            }
            self.media.music_songs_loading_more = true;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let q_sent = query.clone();
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "songs", "query": query, "offset": offset }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.music_songs_loading_more = false;
                        if s.media.music_service != service {
                            return;
                        }
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(data) = reply.data {
                                    s.media.music_songs_error = None;
                                    s.media.music_songs_loaded = true;
                                    if append {
                                        let added = data.tracks.len();
                                        s.media.music_songs_tracks.extend(data.tracks);
                                        let len = s.media.music_songs_tracks.len() as u32;
                                        s.media.music_songs_total = if added > 0 {
                                            data.total.unwrap_or(0).max(len)
                                        } else {
                                            len
                                        };
                                    } else {
                                        let len = data.tracks.len() as u32;
                                        s.media.music_songs_tracks = data.tracks;
                                        s.media.music_songs_total =
                                            data.total.unwrap_or(0).max(len);
                                        s.media.music_songs_context_uri =
                                            data.context_uri.unwrap_or_default();
                                        s.media.music_songs_sorts = data.sorts;
                                        if let Some(so) = data.sort {
                                            s.media.music_songs_sort = so;
                                        }
                                        s.media.music_songs_liked = data.liked.unwrap_or(false);
                                    }
                                }
                            }
                            Ok(reply) => {
                                s.media.music_songs_error =
                                    Some(reply.message.unwrap_or_else(|| {
                                        format!("{} could not load your songs.", service.label())
                                    }));
                            }
                            Err(e) => {
                                s.media.music_songs_error = Some(e.to_string());
                            }
                        }
                        let _ = q_sent;
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (query, offset);
    }

    pub fn load_music_current_nav(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        let (params, ratings_uri, auto_edit) = match &self.media.music_nav {
            MusicNav::Home => {
                self.load_music_views();
                return;
            }
            MusicNav::Songs => {
                self.load_music_songs(false);
                return;
            }
            MusicNav::Browse { uri, .. } => {
                let mut p = json!({ "view": "browse" });
                if let Some(u) = uri {
                    p["uri"] = json!(u);
                }
                (p, uri.clone(), false)
            }
            MusicNav::Radio => (json!({ "view": "radio" }), None, false),
            MusicNav::Explore => (json!({ "view": "explore" }), None, false),
            MusicNav::Search => {
                let q = self.media.music_search_edit.text.trim().to_string();
                if q.is_empty() {
                    return;
                }
                if self.media.music_search_query_loaded != q {
                    self.media.music_search_only.clear();
                    self.media.music_search_query_loaded = q.clone();
                }
                let mut p = json!({ "view": "search", "query": q });
                if !self.media.music_search_filter.is_empty() {
                    p["uri"] = json!(self.media.music_search_filter.clone());
                }
                (p, None, false)
            }
            MusicNav::Discography { uri, .. } => (
                json!({ "view": "discography", "uri": uri, "offset": 0 }),
                None,
                false,
            ),
            MusicNav::Collection {
                uri, kind, edit, ..
            } => {
                let v = match kind.as_str() {
                    "album" => "album",
                    "artist" => "artist",
                    "show" => "show",
                    "collection" => "collection",
                    _ => "playlist",
                };
                (json!({ "view": v, "uri": uri }), Some(uri.clone()), *edit)
            }
        };
        self.media.music_page_loading = true;
        self.media.music_page_error = None;
        #[cfg(windows)]
        self.call_host_replaceable(
            "music.view",
            view_method,
            params,
            move |s, res: Result<PlayerViewReply, String>| {
                s.media.music_page_loading = false;
                if s.media.music_service != service {
                    return;
                }
                match res {
                    Ok(reply) if reply.ok => {
                        if let Some(data) = reply.data {
                            let pending_ratings = data.rating.as_deref() == Some("pending");
                            s.media.music_page_saved = data.header.as_ref().and_then(|h| h.saved);
                            if auto_edit
                                && let Some(ref hdr) = data.header
                                && hdr.owned.unwrap_or(false)
                            {
                                s.music_open_edit_playlist_sheet(
                                    hdr.uri.clone(),
                                    hdr.title.clone(),
                                    hdr.description.clone().unwrap_or_default(),
                                    hdr.privacy.clone(),
                                    hdr.image.clone(),
                                );
                                if let MusicNav::Collection { ref mut edit, .. } = s.media.music_nav
                                {
                                    *edit = false;
                                }
                            }
                            s.media.music_page_data = Some(data);
                            if pending_ratings && let Some(uri) = ratings_uri {
                                s.load_music_page_ratings(&uri);
                            }
                        }
                    }
                    Ok(reply) => {
                        s.media.music_page_error = Some(reply.message.unwrap_or_else(|| {
                            format!("{} could not load this page.", service.label())
                        }));
                    }
                    Err(e) => {
                        s.media.music_page_error = Some(e);
                    }
                }
            },
        );
        #[cfg(not(windows))]
        let _ = (params, ratings_uri, auto_edit);
    }

    #[cfg(windows)]
    fn load_music_page_ratings(&mut self, uri: &str) {
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "ratings", "uri": uri }),
                move |res| {
                    if let Ok(reply) = res
                        && reply.ok
                        && let Some(data) = reply.data
                    {
                        handle.update(move |s, _| {
                            if s.media.music_service != service {
                                return;
                            }
                            if let Some(ref mut page) = s.media.music_page_data {
                                page.rating = None;
                                for r in data.ratings {
                                    if let Some(t) = page.tracks.iter_mut().find(|t| t.uri == r.uri)
                                    {
                                        t.liked = r.liked;
                                        t.disliked = r.disliked;
                                        t.in_library = r.in_library;
                                    }
                                }
                            }
                        });
                    }
                },
            );
        }
    }

    pub fn load_music_page_more(&mut self) {
        if self.status == HostStatus::Standalone || self.media.music_page_more_loading {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        let (view_name, uri, offset, is_discog) = match &self.media.music_nav {
            MusicNav::Discography { uri, .. } => {
                let offset = self
                    .media
                    .music_page_data
                    .as_ref()
                    .map(|d| d.items.len())
                    .unwrap_or(0);
                ("discography", uri.clone(), offset, true)
            }
            MusicNav::Collection { uri, kind, .. } => {
                let v = match kind.as_str() {
                    "album" => "album",
                    "show" => "show",
                    "collection" => "collection",
                    _ => "playlist",
                };
                let offset = self
                    .media
                    .music_page_data
                    .as_ref()
                    .map(|d| d.tracks.len())
                    .unwrap_or(0);
                (v, uri.clone(), offset, false)
            }
            _ => return,
        };
        self.media.music_page_more_loading = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": view_name, "uri": uri, "offset": offset }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.music_page_more_loading = false;
                        if s.media.music_service != service {
                            return;
                        }
                        if let Ok(reply) = res
                            && reply.ok
                            && let Some(more) = reply.data
                        {
                            if let Some(ref mut page) = s.media.music_page_data {
                                if is_discog {
                                    page.items.extend(more.items);
                                } else {
                                    page.tracks.extend(more.tracks);
                                }
                            }
                        } else {
                            s.media.music_page_failed = Some("Didn't work. Try again.".into());
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (view_name, uri, offset, is_discog);
    }

    pub fn music_open_card(&mut self, uri: String, kind: String, title: String) {
        let card = PlayerCard {
            uri: uri.clone(),
            kind: kind.clone(),
            title: title.clone(),
            ..Default::default()
        };
        if kind == "folder" {
            self.music_open_folder(card);
        } else if let Some(nav) = page_for_card(&card) {
            self.music_open_nav(nav);
        } else {
            self.music_play_card(&card);
        }
    }

    pub fn music_play_card(&mut self, card: &PlayerCard) {
        if card.kind == "track" {
            self.music_play_uri(&card.uri, None, None);
        } else if card.kind == "station" && self.media.music_service.has_station() {
            self.music_act_simple("station", json!({ "uri": card.uri }));
        } else {
            self.music_play_context(&card.uri);
        }
    }

    pub fn music_play_context(&mut self, context: &str) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(act_method) = self.media.music_service.act_method() else {
            return;
        };
        let body = json!({ "action": "play", "context": context });
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(act_method, &body, move |_| {
                handle.update(|s, _| s.load_music_player_state());
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, body);
    }

    pub fn music_play_uri(&mut self, uri: &str, context: Option<&str>, index: Option<usize>) {
        self.music_play_uri_ordered(uri, context, index, None);
    }

    pub fn music_play_uri_ordered(
        &mut self,
        uri: &str,
        context: Option<&str>,
        index: Option<usize>,
        query: Option<&str>,
    ) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let Some(act_method) = self.media.music_service.act_method() else {
            return;
        };
        let mut body = json!({ "action": "play", "uri": uri });
        if let Some(ctx) = context
            && !ctx.is_empty()
        {
            body["context"] = json!(ctx);
        }
        if let Some(idx) = index {
            body["index"] = json!(idx);
        }
        if let Some(q) = query
            && !q.is_empty()
        {
            body["query"] = json!(q);
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(act_method, &body, move |_| {
                handle.update(|s, _| s.load_music_player_state());
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, body);
    }

    pub fn music_act_simple(&mut self, action: &str, body: Value) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        let params = encode_act_params(service, action, body);
        let refreshes_lib = matches!(
            action,
            "like"
                | "dislike"
                | "addToPlaylist"
                | "removeFromPlaylist"
                | "library"
                | "suggestLess"
                | "privacy"
                | "collaborative"
                | "download"
                | "played"
                | "pin"
                | "deletePlaylist"
        );
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(act_method, &params, move |res| {
                handle.update(move |s, _| {
                    if let Ok(r) = res
                        && r.ok
                    {
                        if refreshes_lib {
                            s.music_bump_revision();
                        } else {
                            s.load_music_player_state();
                        }
                    } else {
                        s.media.music_bar_failed = Some((
                            format!("{} did not take that. Try again.", service.label()),
                            s.media.music_clock_s + 4.5,
                        ));
                    }
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, params, refreshes_lib);
    }

    pub fn music_open_radio(&mut self, uri: &str, title: String) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "radio", "uri": uri }),
                move |res| {
                    handle.update(move |s, _| {
                        if let Ok(reply) = res
                            && reply.ok
                            && let Some(data) = reply.data
                            && let Some(radio_uri) = data.uri.filter(|u| !u.is_empty())
                        {
                            s.music_open_nav(MusicNav::Collection {
                                uri: radio_uri,
                                kind: "playlist".into(),
                                title,
                                edit: false,
                            });
                        } else {
                            s.media.music_page_failed = Some("Didn't work. Try again.".into());
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (view_method, uri, title);
    }

    pub fn music_move_playlist_track(&mut self, playlist_uri: &str, from: usize, by: isize) {
        let Some(ref mut page) = self.media.music_page_data else {
            return;
        };
        let to_isize = from as isize + by;
        if to_isize < 0 || (to_isize as usize) >= page.tracks.len() || from >= page.tracks.len() {
            return;
        }
        let to = to_isize as usize;
        let track = page.tracks[from].clone();
        let other = page.tracks[to].clone();
        let before_idx = if by < 0 { to } else { to + 1 };
        let before_uid = page
            .tracks
            .get(before_idx)
            .map(|t| t.uid.clone())
            .unwrap_or_default();
        let dir = if by < 0 { "before" } else { "after" };
        let name = format!("{dir}|{}|{}", other.uri, other.uid);
        let text = json!({ "from": from, "to": to, "before": before_uid }).to_string();

        let item = page.tracks.remove(from);
        page.tracks.insert(to, item);

        self.music_act_simple(
            "movePlaylistTrack",
            json!({
                "uri": track.uri,
                "uid": track.uid,
                "context": playlist_uri,
                "name": name,
                "text": text
            }),
        );
    }

    pub fn music_toggle_shuffle(&mut self) {
        if !self.media.music_can_shuffle && self.media.playback.is_none() {
            return;
        }
        let next = !self.media.music_shuffle;
        self.media.music_shuffle = next;
        self.music_act_simple("shuffle", json!({ "on": next }));
    }

    pub fn music_cycle_repeat(&mut self) {
        if !self.media.music_can_repeat && self.media.playback.is_none() {
            return;
        }
        let next = (self.media.music_repeat + 1) % 3;
        self.media.music_repeat = next;
        self.music_act_simple("repeat", json!({ "index": next }));
    }

    pub fn music_toggle_like(&mut self) {
        if self.media.music_track_uri.is_empty() {
            return;
        }
        let uri = self.media.music_track_uri.clone();
        let next = !self.media.music_liked;
        self.media.music_liked = next;
        if next {
            self.media.music_disliked = false;
        }
        self.music_act_simple("like", json!({ "uri": uri, "on": next }));
    }

    pub fn music_toggle_dislike(&mut self) {
        if self.media.music_track_uri.is_empty() || !self.media.music_service.has_dislike() {
            return;
        }
        let uri = self.media.music_track_uri.clone();
        let next = !self.media.music_disliked;
        self.media.music_disliked = next;
        if next {
            self.media.music_liked = false;
        }
        self.music_act_simple("dislike", json!({ "uri": uri, "on": next }));
    }

    pub fn music_toggle_video(&mut self) {
        if !self.media.music_can_video || !self.media.music_service.has_video() {
            return;
        }
        let next = !self.media.music_video;
        self.media.music_video = next;
        self.music_act_simple("video", json!({ "on": next }));
    }

    pub fn music_toggle_panel(&mut self, panel: MusicPanel) {
        self.media.music_menu = None;
        if self.media.music_panel == Some(panel) {
            self.media.music_panel = None;
            return;
        }
        self.music_open_panel(panel);
    }

    pub fn music_open_panel(&mut self, panel: MusicPanel) {
        self.media.music_menu = None;
        self.media.music_panel = Some(panel);
        match panel {
            MusicPanel::Queue => {
                self.load_music_queue();
            }
            MusicPanel::NowPlaying => {
                let uri = self.media.music_track_uri.clone();
                if !uri.is_empty() {
                    self.load_music_lyrics(&uri);
                    if self.media.music_now_tab == NowPlayingTab::Related
                        && self.media.music_service.has_related()
                    {
                        self.load_music_related(&uri);
                    }
                }
            }
            MusicPanel::Devices => {
                self.load_music_devices();
            }
            MusicPanel::Friends => {
                self.media.music_friends_last_poll_s = self.media.music_clock_s;
                self.load_music_friends();
            }
            MusicPanel::Jam => {
                self.media.music_jam_last_poll_s = self.media.music_clock_s;
                if self.media.music_service.has_jam() {
                    self.load_music_jam();
                }
                self.load_listen_along_sources();
            }
            MusicPanel::Sound => {
                self.load_music_sound();
            }
            MusicPanel::Credits => {
                let uri = self.media.music_credits_uri.clone();
                if !uri.is_empty() {
                    self.load_music_credits(&uri);
                }
            }
        }
    }

    pub fn music_show_credits(&mut self, uri: String) {
        self.media.music_credits_uri = uri.clone();
        self.media.music_panel = Some(MusicPanel::Credits);
        self.load_music_credits(&uri);
    }

    pub fn load_music_queue(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_queue_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, PlayerViewReply>(view_method, &json!({ "view": "queue" }), move |res| {
                h1.update(move |s, _| {
                    if s.media.music_service != service {
                        return;
                    }
                    match res {
                        Ok(reply) if reply.ok => {
                            s.media.music_queue_data = reply.data;
                            s.media.music_queue_error = None;
                        }
                        Ok(reply) => {
                            s.media.music_queue_error = Some(reply.message.unwrap_or_else(|| {
                                format!("{} could not load the queue.", service.label())
                            }));
                        }
                        Err(e) => {
                            s.media.music_queue_error = Some(e.to_string());
                        }
                    }
                });
            });
            if service.has_history() {
                let h2 = handle;
                rpc.call::<_, PlayerViewReply>(
                    view_method,
                    &json!({ "view": "history" }),
                    move |res| {
                        if let Ok(reply) = res
                            && reply.ok
                            && let Some(data) = reply.data
                        {
                            h2.update(move |s, _| {
                                if s.media.music_service == service {
                                    s.media.music_queue_history = data.tracks;
                                }
                            });
                        }
                    },
                );
            }
        }
    }

    pub fn music_queue_edit(&mut self, action: &str, body: Value) {
        if self.status == HostStatus::Standalone {
            return;
        }
        self.media.music_queue_failed = false;
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        let params = encode_act_params(service, action, body);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(act_method, &params, move |res| {
                handle.update(move |s, _| {
                    let ok = res.ok().is_some_and(|r| r.ok);
                    s.media.music_queue_failed = !ok;
                    s.load_music_queue();
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, params);
    }

    pub fn load_music_lyrics(&mut self, uri: &str) {
        if self.status == HostStatus::Standalone || uri.is_empty() {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        if self.media.music_lyrics_uri == uri && !self.media.music_lyrics_loading {
            return;
        }
        self.media.music_lyrics_uri = uri.to_string();
        self.media.music_lyrics_loading = true;
        self.media.music_lyrics_error = None;
        self.media.music_lyrics_lines.clear();
        let target_uri = uri.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "lyrics", "uri": target_uri }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.media.music_service != service
                            || s.media.music_lyrics_uri != target_uri
                        {
                            return;
                        }
                        s.media.music_lyrics_loading = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(data) = reply.data {
                                    s.media.music_lyrics_lines = data.lines;
                                    s.media.music_lyrics_synced = data.synced.unwrap_or(false);
                                }
                            }
                            Ok(reply) => {
                                s.media.music_lyrics_error = reply.message;
                            }
                            Err(e) => {
                                s.media.music_lyrics_error = Some(e.to_string());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (view_method, target_uri);
    }

    pub fn load_music_related(&mut self, uri: &str) {
        if self.status == HostStatus::Standalone || uri.is_empty() {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_related_uri = uri.to_string();
        self.media.music_related_loading = true;
        self.media.music_related_error = None;
        let target_uri = uri.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "related", "uri": target_uri }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.media.music_service != service
                            || s.media.music_related_uri != target_uri
                        {
                            return;
                        }
                        s.media.music_related_loading = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(data) = reply.data {
                                    s.media.music_related_sections = data
                                        .sections
                                        .into_iter()
                                        .filter(|sec| !sec.items.is_empty())
                                        .collect();
                                }
                            }
                            Ok(reply) => {
                                s.media.music_related_error = reply.message;
                            }
                            Err(e) => {
                                s.media.music_related_error = Some(e.to_string());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (view_method, target_uri);
    }

    pub fn load_music_devices(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_devices_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "devices" }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.media.music_service != service {
                            return;
                        }
                        s.media.music_devices_loaded = true;
                        match res {
                            Ok(reply) if reply.ok => {
                                s.media.music_devices =
                                    reply.data.map(|d| d.devices).unwrap_or_default();
                            }
                            Ok(reply) => {
                                s.media.music_devices_error = reply.message;
                            }
                            Err(e) => {
                                s.media.music_devices_error = Some(e.to_string());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn music_transfer_device(&mut self, device_id: &str) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        self.media.music_devices_error = None;
        self.media.music_elsewhere_device_id = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(
                act_method,
                &json!({ "action": "transfer", "context": device_id }),
                move |res| {
                    handle.update(move |s, _| {
                        if !res.ok().is_some_and(|r| r.ok) {
                            s.media.music_devices_error = Some("Didn't work. Try again.".into());
                        }
                        s.load_music_devices();
                        s.load_music_player_state();
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (act_method, device_id);
    }

    pub fn load_music_friends(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_friends_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "friends" }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.media.music_service != service {
                            return;
                        }
                        s.media.music_friends_loaded = true;
                        match res {
                            Ok(reply) if reply.ok => {
                                let mut list = reply.data.map(|d| d.friends).unwrap_or_default();
                                list.sort_by_key(|a| std::cmp::Reverse(a.at));
                                s.media.music_friends = list;
                            }
                            Ok(reply) => {
                                s.media.music_friends_error = reply.message;
                            }
                            Err(e) => {
                                s.media.music_friends_error = Some(e.to_string());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn load_music_jam(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_jam_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(view_method, &json!({ "view": "jam" }), move |res| {
                handle.update(move |s, _| {
                    if s.media.music_service != service {
                        return;
                    }
                    s.media.music_jam_loaded = true;
                    match res {
                        Ok(reply) if reply.ok => {
                            if let Some(data) = reply.data {
                                s.media.music_jam_session = data.session;
                                s.media.music_jam_experience = data.experience;
                            }
                        }
                        Ok(reply) => {
                            s.media.music_jam_error = reply.message;
                        }
                        Err(e) => {
                            s.media.music_jam_error = Some(e.to_string());
                        }
                    }
                });
            });
        }
    }

    pub fn music_jam_action(&mut self, action: &str, body: Value) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        self.media.music_jam_busy = true;
        self.media.music_jam_error = None;
        self.media.music_jam_end_armed = false;
        let params = encode_act_params(service, action, body);
        let action_owned = action.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(act_method, &params, move |res| {
                handle.update(move |s, _| {
                    s.media.music_jam_busy = false;
                    if !res.ok().is_some_and(|r| r.ok) {
                        s.media.music_jam_error = Some(match action_owned.as_str() {
                            "jamStart" => "Spotify could not start a Jam. Try again.".into(),
                            "jamJoin" => "Spotify could not join that Jam. Check the link.".into(),
                            _ => "Spotify did not take that. Try again.".into(),
                        });
                    }
                    s.load_music_jam();
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, params, action_owned);
    }

    pub fn music_jam_submit_join(&mut self) {
        let raw = self.media.music_jam_join_edit.text.trim().to_string();
        if raw.is_empty() || self.media.music_jam_busy {
            return;
        }
        self.media.music_jam_join_wrong = false;
        if let Some(tok) = parse_direct_jam_token(&raw) {
            self.music_jam_action("jamJoin", json!({ "name": tok }));
            return;
        }
        self.media.music_jam_busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SpotifyLinkReply>(
                METHOD_SPOTIFY_LINK,
                &json!({ "url": raw }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.music_jam_busy = false;
                        if let Ok(reply) = res
                            && reply.ok
                            && let Some(uri) = reply.uri
                            && let Some(tok) = uri.strip_prefix("spotify:socialsession:")
                        {
                            s.music_jam_action("jamJoin", json!({ "name": tok }));
                        } else {
                            s.media.music_jam_join_wrong = true;
                        }
                    });
                },
            );
        }
    }

    pub fn load_music_credits(&mut self, uri: &str) {
        if self.status == HostStatus::Standalone || uri.is_empty() {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_credits_loading = true;
        self.media.music_credits_error = None;
        self.media.music_credits_data = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "credits", "uri": uri }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.media.music_service != service {
                            return;
                        }
                        s.media.music_credits_loading = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                s.media.music_credits_data = reply.data;
                            }
                            Ok(reply) => {
                                s.media.music_credits_error = reply.message;
                            }
                            Err(e) => {
                                s.media.music_credits_error = Some(e.to_string());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (view_method, uri);
    }

    pub fn load_music_sound(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        self.media.music_sound_loading = true;
        self.media.music_sound_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            if service.has_audio() {
                rpc.call::<_, SpotifyAudioReply>(
                    view_method,
                    &json!({ "view": "audio" }),
                    move |res| {
                        handle.update(move |s, _| {
                            if s.media.music_service != service {
                                return;
                            }
                            s.media.music_sound_loading = false;
                            match res {
                                Ok(reply) if reply.ok => {
                                    s.media.music_sound_spotify = reply.data;
                                }
                                Ok(reply) => {
                                    s.media.music_sound_error = reply.message;
                                }
                                Err(e) => {
                                    s.media.music_sound_error = Some(e.to_string());
                                }
                            }
                        });
                    },
                );
            } else if service.has_quality() {
                rpc.call::<_, PlayerViewReply>(
                    view_method,
                    &json!({ "view": "audio" }),
                    move |res| {
                        handle.update(move |s, _| {
                            if s.media.music_service != service {
                                return;
                            }
                            s.media.music_sound_loading = false;
                            match res {
                                Ok(reply) if reply.ok => {
                                    if let Some(data) = reply.data {
                                        s.media.music_sound_qualities = data.qualities;
                                        s.media.music_sound_quality =
                                            data.quality.unwrap_or_default();
                                    }
                                }
                                Ok(reply) => {
                                    s.media.music_sound_error = reply.message;
                                }
                                Err(e) => {
                                    s.media.music_sound_error = Some(e.to_string());
                                }
                            }
                        });
                    },
                );
            } else {
                self.media.music_sound_loading = false;
            }
        }
        #[cfg(not(windows))]
        {
            let _ = view_method;
            self.media.music_sound_loading = false;
        }
    }

    pub fn music_set_spotify_audio(&mut self, setting: &str, body: Value) {
        self.media.music_sound_failed = None;
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        let mut params = body;
        params["action"] = json!("audio");
        params["context"] = json!(setting);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(act_method, &params, move |res| {
                handle.update(move |s, _| {
                    if !res.ok().is_some_and(|r| r.ok) {
                        s.media.music_sound_failed =
                            Some("Spotify did not take that. Try again.".into());
                    }
                    s.load_music_sound();
                });
            });
        }
        #[cfg(not(windows))]
        let _ = (act_method, params);
    }

    pub fn music_set_service_quality(&mut self, quality_id: String) {
        self.media.music_sound_quality = quality_id.clone();
        self.media.music_sound_failed = None;
        self.media.music_menu = None;
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        let idx: i64 = quality_id.parse().unwrap_or(0);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(
                act_method,
                &json!({ "action": "audio", "index": idx }),
                move |res| {
                    handle.update(move |s, _| {
                        if !res.ok().is_some_and(|r| r.ok) {
                            s.media.music_sound_failed =
                                Some(format!("{} did not take that. Try again.", service.label()));
                        }
                        s.load_music_sound();
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (act_method, idx);
    }

    pub fn music_set_speed(&mut self, speed_tenths: i32) {
        self.media.music_speed = speed_tenths as f64 / 10.0;
        self.media.music_sound_failed = None;
        self.media.music_menu = None;
        let service = self.media.music_service;
        let Some(act_method) = service.act_method() else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PlayerActReply>(
                act_method,
                &json!({ "action": "speed", "index": speed_tenths }),
                move |res| {
                    handle.update(move |s, _| {
                        if !res.ok().is_some_and(|r| r.ok) {
                            s.media.music_sound_failed =
                                Some(format!("{} did not take that. Try again.", service.label()));
                            s.load_music_player_state();
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (act_method, speed_tenths);
    }

    pub fn music_set_sleep_minutes(&mut self, minutes: Option<i64>) {
        self.media.music_menu = None;
        self.media.music_sleep_deadline_ms = minutes.map(|m| now_epoch_ms() + m * 60_000);
    }

    pub fn load_listen_along_sources(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, DiscordCallSongsReply>(
                METHOD_DISCORD_CALL_SONGS,
                &json!({}),
                move |res| {
                    if let Ok(reply) = res {
                        h1.update(move |s, _| {
                            for song in reply.songs {
                                s.on_discord_call_song(song);
                            }
                        });
                    }
                },
            );
            if self.discord.status.signed_in && self.discord.friends.is_empty() {
                rpc.call::<_, exo_rpc::DiscordFriendsReply>(
                    METHOD_DISCORD_FRIENDS,
                    &json!({}),
                    move |res| {
                        if let Ok(reply) = res {
                            handle.update(move |s, _| {
                                s.discord.friends = reply.friends;
                            });
                        }
                    },
                );
            }
        }
    }

    pub fn on_discord_call_song(&mut self, song: DiscordCallSong) -> bool {
        let user_id = song.user_id.clone();
        if song.rich.is_some() || song.paused {
            if let Some(existing) = self
                .media
                .music_call_songs
                .iter_mut()
                .find(|s| s.user_id == user_id)
            {
                *existing = song.clone();
            } else {
                self.media.music_call_songs.push(song.clone());
            }
        } else {
            self.media.music_call_songs.retain(|s| s.user_id != user_id);
        }

        if self.media.music_listen_along.status != ListenAlongStatus::Off
            && self
                .media
                .music_listen_along
                .friend
                .as_ref()
                .is_some_and(|f| f.id == user_id)
        {
            self.media.music_listen_along.from_call = song.rich.is_some() || song.paused;
            let rich = if song.rich.is_some() || song.paused {
                song.rich
            } else {
                self.discord
                    .friends
                    .iter()
                    .find(|f| f.id == user_id)
                    .and_then(|f| f.rich.clone())
            };
            self.listen_along_apply(rich.as_ref(), song.paused);
        }
        true
    }

    pub fn on_listen_along_presence(&mut self, user_id: &str, rich: Option<&DiscordRich>) {
        if self.media.music_listen_along.status == ListenAlongStatus::Off {
            return;
        }
        if !self
            .media
            .music_listen_along
            .friend
            .as_ref()
            .is_some_and(|f| f.id == user_id)
        {
            return;
        }
        if !self.media.music_listen_along.from_call {
            self.listen_along_apply(rich, false);
        }
    }

    pub fn start_listen_along(&mut self, friend: ListenAlongFriend, rich: DiscordRich) {
        self.stop_listen_along(None);
        let service = self.media.music_service;
        let in_call = self
            .media
            .music_call_songs
            .iter()
            .any(|c| c.user_id == friend.id);
        let turn = self.media.music_listen_along.turn.wrapping_add(1);
        self.media.music_listen_along = ListenAlongState {
            status: ListenAlongStatus::Finding,
            friend: Some(friend.clone()),
            service_name: service.label().into(),
            key: String::new(),
            start_ms: rich.start.unwrap_or(0),
            end_ms: rich.end,
            uri: None,
            title: rich.details.clone().unwrap_or_default(),
            artist: rich.state.clone().unwrap_or_default(),
            paused: false,
            moved_at_ms: now_epoch_ms(),
            from_call: in_call,
            seek_latency_ms: self.media.music_listen_along.seek_latency_ms,
            turn,
            away_deadline_ms: None,
            end_deadline_ms: None,
        };
        self.notice = Some(format!(
            "Listening along with {} on {}.",
            friend.name,
            service.label()
        ));
        if self.media.music_service == MusicService::Spotify
            && self.media.music_jam_session.is_some()
        {
            self.stop_listen_along(Some(
                "You are in a Spotify Jam. Leave it to listen along.".into(),
            ));
            return;
        }
        self.listen_along_apply(Some(&rich), false);
    }

    pub fn stop_listen_along(&mut self, why: Option<String>) {
        if self.media.music_listen_along.status == ListenAlongStatus::Off {
            return;
        }
        let next_turn = self.media.music_listen_along.turn.wrapping_add(1);
        let lat = self.media.music_listen_along.seek_latency_ms;
        self.media.music_listen_along = ListenAlongState {
            turn: next_turn,
            seek_latency_ms: lat,
            ..Default::default()
        };
        if let Some(msg) = why {
            self.notice = Some(msg);
        }
    }

    fn listen_along_apply(&mut self, rich: Option<&DiscordRich>, paused: bool) {
        self.media.music_listen_along.away_deadline_ms = None;
        self.media.music_listen_along.end_deadline_ms = None;
        if !can_listen_along(rich) {
            let now = now_epoch_ms();
            let wait = if paused { 0 } else { 4000 };
            if wait == 0 {
                self.media.music_listen_along.paused = true;
                self.media.music_listen_along.status = ListenAlongStatus::Paused;
                self.music_control("pause");
            } else {
                self.media.music_listen_along.away_deadline_ms = Some(now + wait);
            }
            self.media.music_listen_along.end_deadline_ms = Some(now + 15 * 60_000);
            return;
        }
        let r = rich.expect("checked");
        let details = r.details.clone().unwrap_or_default();
        let state_str = r.state.clone().unwrap_or_default();
        let start = r.start.unwrap_or(0);
        let key = r
            .sync_id
            .clone()
            .unwrap_or_else(|| format!("{details}|{state_str}"));

        if key == self.media.music_listen_along.key {
            let jumped = (start - self.media.music_listen_along.start_ms).abs() > 2500;
            self.media.music_listen_along.start_ms = start;
            if self.media.music_listen_along.uri.is_some()
                && (self.media.music_listen_along.paused || jumped)
            {
                if self.media.music_listen_along.paused {
                    self.music_control("play");
                }
                self.media.music_listen_along.paused = false;
                self.media.music_listen_along.status = ListenAlongStatus::Playing;
                self.listen_along_seek();
            }
            return;
        }

        let turn = self.media.music_listen_along.turn.wrapping_add(1);
        self.media.music_listen_along.turn = turn;
        self.media.music_listen_along.key = key;
        self.media.music_listen_along.start_ms = start;
        self.media.music_listen_along.end_ms = r.end.filter(|&e| e > start);
        self.media.music_listen_along.uri = None;
        self.media.music_listen_along.title = details.clone();
        self.media.music_listen_along.artist = state_str.clone();
        self.media.music_listen_along.paused = false;
        self.media.music_listen_along.moved_at_ms = now_epoch_ms();
        self.media.music_listen_along.status = ListenAlongStatus::Finding;

        let service = self.media.music_service;
        if let Some(direct_uri) = r
            .uri
            .as_deref()
            .filter(|u| u.starts_with(service.track_uri_prefix()))
            .map(String::from)
            .or_else(|| {
                if service == MusicService::Spotify
                    && r.name == "Spotify"
                    && let Some(sid) = r.sync_id.as_deref()
                    && sid.len() == 22
                    && sid.bytes().all(|b| b.is_ascii_alphanumeric())
                {
                    Some(format!("spotify:track:{sid}"))
                } else {
                    None
                }
            })
        {
            self.listen_along_play_found(turn, direct_uri, details);
            return;
        }

        let cache_key = format!("{}|{}|{}", service.id(), details, state_str);
        if let Some(cached) = self.media.music_match_cache.get(&cache_key).cloned() {
            match cached {
                Some((u, t)) => self.listen_along_play_found(turn, u, t),
                None => {
                    self.media.music_listen_along.status = ListenAlongStatus::Missing;
                    self.music_control("pause");
                }
            }
            return;
        }

        self.listen_along_search_and_match(turn, r.clone(), cache_key);
    }

    fn listen_along_search_and_match(&mut self, turn: u64, rich: DiscordRich, cache_key: String) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let service = self.media.music_service;
        let Some(view_method) = service.view_method() else {
            return;
        };
        let title = rich.details.clone().unwrap_or_default();
        let artists: Vec<String> = rich
            .state
            .as_deref()
            .unwrap_or("")
            .split(';')
            .map(|a| a.trim().to_string())
            .filter(|a| !a.is_empty())
            .collect();
        let base_title = title.split(['(', '[']).next().unwrap_or(&title).trim();
        let query = format!(
            "{} {}",
            base_title,
            artists.first().map(String::as_str).unwrap_or("")
        )
        .trim()
        .to_string();
        let duration_ms = match (rich.start, rich.end) {
            (Some(s), Some(e)) if e > s => Some(e - s),
            _ => None,
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc2 = rpc.clone();
            rpc.call::<_, PlayerViewReply>(
                view_method,
                &json!({ "view": "search", "query": query }),
                move |res| {
                    let tracks: Vec<PlayerTrack> = match res {
                        Ok(reply) if reply.ok => reply
                            .data
                            .map(|d| d.tracks)
                            .unwrap_or_default()
                            .into_iter()
                            .filter(|t| t.playable && !t.uri.is_empty())
                            .take(20)
                            .collect(),
                        _ => {
                            handle.update(move |s, _| {
                                if s.media.music_listen_along.turn == turn {
                                    s.media.music_listen_along.status = ListenAlongStatus::Failed;
                                    s.music_control("pause");
                                }
                            });
                            return;
                        }
                    };
                    if tracks.is_empty() {
                        handle.update(move |s, _| {
                            if s.media.music_match_cache.len() > 200 {
                                s.media.music_match_cache.clear();
                            }
                            s.media.music_match_cache.insert(cache_key, None);
                            if s.media.music_listen_along.turn == turn {
                                s.media.music_listen_along.status = ListenAlongStatus::Missing;
                                s.music_control("pause");
                            }
                        });
                        return;
                    }
                    let song = MusicMatchSong {
                        title,
                        artists,
                        duration_ms,
                    };
                    let candidates: Vec<MusicMatchSong> = tracks
                        .iter()
                        .map(|t| MusicMatchSong {
                            title: t.title.clone(),
                            artists: t.artists.iter().map(|a| a.name.clone()).collect(),
                            duration_ms: (t.duration_ms > 0.0)
                                .then(|| t.duration_ms.round() as i64),
                        })
                        .collect();
                    rpc2.call::<_, MusicMatchReply>(
                        METHOD_MUSIC_MATCH,
                        &json!({ "song": song, "candidates": candidates }),
                        move |mres| {
                            let picked = mres
                                .ok()
                                .filter(|r| r.ok)
                                .and_then(|r| r.index)
                                .filter(|&i| i >= 0)
                                .and_then(|i| tracks.get(i as usize))
                                .map(|t| (t.uri.clone(), t.title.clone()));
                            handle.update(move |s, _| {
                                if s.media.music_match_cache.len() > 200 {
                                    s.media.music_match_cache.clear();
                                }
                                s.media.music_match_cache.insert(cache_key, picked.clone());
                                if s.media.music_listen_along.turn != turn {
                                    return;
                                }
                                match picked {
                                    Some((u, t)) => s.listen_along_play_found(turn, u, t),
                                    None => {
                                        s.media.music_listen_along.status =
                                            ListenAlongStatus::Missing;
                                        s.music_control("pause");
                                    }
                                }
                            });
                        },
                    );
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (view_method, query, duration_ms, cache_key);
    }

    fn listen_along_play_found(&mut self, turn: u64, uri: String, title: String) {
        if self.media.music_listen_along.turn != turn {
            return;
        }
        let Some(act_method) = self.media.music_service.act_method() else {
            return;
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let uri_c = uri.clone();
            let title_c = title.clone();
            rpc.call::<_, PlayerActReply>(
                act_method,
                &json!({ "action": "play", "uri": uri }),
                move |res| {
                    handle.update(move |s, _| {
                        if s.media.music_listen_along.turn != turn {
                            return;
                        }
                        if res.ok().is_some_and(|r| r.ok) {
                            s.media.music_listen_along.uri = Some(uri_c);
                            s.media.music_listen_along.title = title_c;
                            s.media.music_listen_along.status = ListenAlongStatus::Playing;
                            s.listen_along_seek();
                        } else {
                            s.media.music_listen_along.status = ListenAlongStatus::Failed;
                            s.music_control("pause");
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (act_method, uri, title);
    }

    fn listen_along_seek(&mut self) {
        let wall = now_epoch_ms();
        let at = expected_position_ms(
            self.media.music_listen_along.start_ms,
            wall,
            self.media.music_listen_along.seek_latency_ms.unwrap_or(0.0),
        );
        self.media.music_listen_along.moved_at_ms = wall;
        let near_end = self
            .media
            .music_listen_along
            .end_ms
            .is_some_and(|e| wall >= e - 3000);
        if at > 3000.0 && !near_end {
            self.music_seek_ms(at);
        }
    }

    fn listen_along_on_playback(&mut self) {
        let Some(pb) = self.media.playback.clone() else {
            return;
        };
        if self.media.music_listen_along.uri.is_none() || !pb.is_playing() {
            return;
        }
        if same_song(&pb.title, &self.media.music_listen_along.title) {
            if pb.can_seek && !self.media.music_listen_along.paused {
                let wall = now_epoch_ms();
                let actual = self.media.current_position_ms();
                let expected =
                    expected_position_ms(self.media.music_listen_along.start_ms, wall, 0.0);
                let ends_in = self.media.music_listen_along.end_ms.map(|e| e - wall);
                if should_correct_listen_drift(
                    self.media.music_listen_along.from_call,
                    actual,
                    expected,
                    true,
                    wall - self.media.music_listen_along.moved_at_ms,
                    ends_in,
                ) {
                    self.listen_along_seek();
                }
            }
            return;
        }
        if let Some(end) = self.media.music_listen_along.end_ms
            && now_epoch_ms() < end - 5000
        {
            self.stop_listen_along(Some(
                "You played another song, so listening along stopped.".into(),
            ));
        }
    }

    pub fn music_submit_search(&mut self) {
        if !self.media.music_search_edit.text.trim().is_empty() {
            self.music_open_nav(MusicNav::Search);
        }
    }

    pub fn music_set_search_filter(&mut self, filter_id: String) {
        self.media.music_search_filter = filter_id;
        self.load_music_current_nav();
    }

    pub fn refresh_music_playback(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, MusicPlaybackReply>(METHOD_MUSIC_PLAYBACK, &json!({}), move |res| {
                if let Ok(reply) = res {
                    handle.update_if(move |s, _| {
                        let changed = s.apply_music_playback(reply.playback);
                        s.on_music_playback_changed();
                        changed
                    });
                }
            });
        }
    }

    pub fn music_seek_ms(&mut self, position_ms: f64) {
        let clamped = position_ms.max(0.0);
        if let Some(ref mut pb) = self.media.playback {
            pb.position_ms = Some(clamped.round() as i64);
            self.media.music_playback_anchor_s = self.media.music_clock_s;
        }
        self.media.music_seek_preview_ms = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let sent_ms = now_epoch_ms();
            rpc.call::<_, MusicControlReply>(
                METHOD_MUSIC_CONTROL,
                &json!({ "action": "seek", "positionMs": clamped }),
                move |res| {
                    let elapsed = (now_epoch_ms() - sent_ms).max(0) as f64;
                    handle.update(move |s, _| {
                        if let Ok(r) = res {
                            if r.ok {
                                s.media.music_listen_along.seek_latency_ms = next_seek_latency_ms(
                                    s.media.music_listen_along.seek_latency_ms,
                                    elapsed,
                                );
                            } else if let Some(msg) = r.message {
                                s.media.music_bar_failed = Some((msg, s.media.music_clock_s + 4.5));
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn music_seek_by_ms(&mut self, delta_ms: f64) -> bool {
        let can_seek = self.media.playback.as_ref().is_some_and(|p| p.can_seek);
        if !can_seek {
            return false;
        }
        let Some(pos) = self.media.current_position_ms() else {
            return false;
        };
        self.music_seek_ms((pos + delta_ms).max(0.0));
        true
    }

    pub fn music_control(&mut self, action: &str) {
        let wire_action = if action == "prev" { "previous" } else { action };
        self.media.music_bar_failed = None;
        self.media.music_elsewhere_device_id = None;
        if let Some(ref mut pb) = self.media.playback {
            match wire_action {
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
            rpc.call::<_, MusicControlReply>(
                METHOD_MUSIC_CONTROL,
                &json!({ "action": wire_action }),
                move |res| {
                    handle.update(move |s, _| {
                        let control_ok = match res {
                            Ok(r) if r.ok => true,
                            Ok(r) => {
                                if r.cause.as_deref() == Some("another-device") {
                                    s.media.music_elsewhere_device_id = r.device_id;
                                }
                                if let Some(msg) = r.message {
                                    s.media.music_bar_failed =
                                        Some((msg.clone(), s.media.music_clock_s + 4.5));
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
        self.media.music_volume_known = true;
        if clamped > 0.0 {
            self.media.music_last_audible_volume = clamped;
        }
        if let Some(m) = muted {
            self.media.music_muted = m;
        } else {
            self.media.music_muted = clamped <= 0.0;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            let wire_level = if self.media.music_muted { 0.0 } else { clamped };
            let params = match muted {
                Some(true) => json!({ "level": 0.0 }),
                Some(false) => json!({ "level": wire_level }),
                None => json!({ "level": clamped }),
            };
            rpc.call_raw(METHOD_MUSIC_VOLUME, &params, |_| {});
        }
    }

    pub fn music_step_volume_pct(&mut self, step_pct: i32) {
        let cur = if self.media.music_muted {
            0.0
        } else {
            self.media.music_volume * 100.0
        };
        let next = ((cur.round() as i32 + step_pct).clamp(0, 100) as f64) / 100.0;
        self.music_set_volume(next, None);
    }

    pub fn music_toggle_mute(&mut self) {
        if self.media.music_muted || self.media.music_volume <= 0.001 {
            let restore = self.media.music_last_audible_volume.clamp(0.05, 1.0);
            self.music_set_volume(restore, Some(false));
        } else {
            self.media.music_last_audible_volume = self.media.music_volume;
            self.music_set_volume(0.0, Some(true));
        }
    }

    pub fn music_sign_in(&mut self) {
        let service = self.media.music_service;
        let svc = service.id();
        self.media.music_sign_in_pending = true;
        self.media.music_sign_in_busy = true;
        self.media.music_sign_in_message = None;
        self.media.music_sign_in_status = None;
        self.media.music_start_gen = self.media.music_start_gen.wrapping_add(1);
        let generation = self.media.music_start_gen;
        match service {
            MusicService::Spotify => {
                self.open_web_surface(crate::web_host::WebSurfaceKind::SpotifyPlayer);
            }
            MusicService::Apple => {
                self.open_web_surface(crate::web_host::WebSurfaceKind::AppleMusicSignIn);
            }
            MusicService::YouTube => {}
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_MUSIC_SIGN_IN,
                &json!({ "service": svc }),
                move |res| {
                    handle.update(move |s, _| {
                        s.media.music_sign_in_busy = false;
                        match res {
                            Ok(r) if r.ok => {
                                s.media.music_sign_in_status =
                                    Some(r.message.unwrap_or_else(|| {
                                        "Finish signing in in its window.".into()
                                    }));
                                if s.room == Room::Music
                                    && s.media.music_start_gen == generation
                                    && s.media.music_service == service
                                {
                                    s.schedule_music_start_retry(
                                        service,
                                        generation,
                                        0,
                                        MUSIC_SIGN_IN_POLL_MS,
                                    );
                                }
                            }
                            Ok(r) => {
                                s.media.music_sign_in_pending = false;
                                s.media.music_sign_in_message =
                                    Some(r.message.unwrap_or_else(|| {
                                        format!(
                                            "{} could not start sign-in. Try again.",
                                            service.label()
                                        )
                                    }));
                            }
                            Err(_) => {
                                s.media.music_sign_in_pending = false;
                                s.media.music_sign_in_message = Some(format!(
                                    "{} could not start sign-in. Try again.",
                                    service.label()
                                ));
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (svc, generation);
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
        self.media.clip_editor_open = false;
        self.media.snip_editor = None;
        self.media.captures_ui.send_dialog.open = false;
        self.media.captures_ui.selection = None;
        self.media.notes.open = false;
        self.media.lightbox.close();
        self.media.lightbox_urls.clear();
        self.media.clip_player = None;
    }

    pub fn load_captures(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            self.media.captures_loading = true;
            self.media.captures_ui.list_failed = false;
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
                    match res {
                        Ok(list) => {
                            s.media.captures_ui.list_failed = false;
                            s.media.capture_items = list.items;
                        }
                        Err(_) => {
                            s.media.captures_ui.list_failed = true;
                        }
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

    pub fn load_capture_status(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, CaptureStatusReply>(METHOD_CAPTURE_STATUS, &json!({}), move |res| {
                if let Ok(st) = res {
                    handle.update(move |s, _| s.media.capture_status = Some(st));
                }
            });
        }
    }
}

pub fn apply_music_start_reply(
    media: &mut MediaState,
    in_music_room: bool,
    res: Result<PlayerStartReply, String>,
    attempt: u32,
) -> MusicStartAction {
    match res {
        Ok(reply) if reply.ok && reply.state == "running" => {
            media.music_room_state = MusicRoomState::Running;
            media.music_room_message = None;
            media.music_sign_in_pending = false;
            MusicStartAction::LoadViews
        }
        Ok(reply) if reply.state == "loading" => {
            if media.music_room_state != MusicRoomState::Running {
                media.music_room_state = MusicRoomState::Loading;
            }
            media.music_room_message = reply.message;
            if !in_music_room {
                return MusicStartAction::Stop;
            }
            if attempt >= MUSIC_START_MAX_ATTEMPTS {
                if media.music_room_state != MusicRoomState::Running {
                    let msg = format!("{} did not open. Try again.", media.music_service.label());
                    media.music_room_message = Some(msg.clone());
                    media.music_room_state = MusicRoomState::Error(msg);
                }
                return MusicStartAction::Stop;
            }
            let delay_ms = if attempt < MUSIC_START_FAST_ATTEMPTS {
                MUSIC_START_FAST_POLL_MS
            } else {
                MUSIC_START_SLOW_POLL_MS
            };
            MusicStartAction::RetryAfter {
                delay_ms,
                next_attempt: attempt + 1,
            }
        }
        Ok(reply) if reply.state == "signedOut" => {
            media.music_room_state = MusicRoomState::SignedOut;
            media.music_room_message = reply.message;
            if in_music_room && media.music_sign_in_pending && attempt < MUSIC_SIGN_IN_MAX_ATTEMPTS
            {
                MusicStartAction::RetryAfter {
                    delay_ms: MUSIC_SIGN_IN_POLL_MS,
                    next_attempt: attempt + 1,
                }
            } else {
                MusicStartAction::Stop
            }
        }
        Ok(reply) if reply.state == "notPremium" => {
            media.music_room_state = MusicRoomState::NotPremium;
            media.music_room_message = reply.message;
            if in_music_room && media.music_sign_in_pending && attempt < MUSIC_SIGN_IN_MAX_ATTEMPTS
            {
                MusicStartAction::RetryAfter {
                    delay_ms: MUSIC_SIGN_IN_POLL_MS,
                    next_attempt: attempt + 1,
                }
            } else {
                MusicStartAction::Stop
            }
        }
        Ok(reply) => {
            media.music_sign_in_pending = false;
            let msg = reply
                .message
                .unwrap_or_else(|| "Unable to start music engine.".into());
            media.music_room_message = Some(msg.clone());
            media.music_room_state = MusicRoomState::Error(msg);
            MusicStartAction::Stop
        }
        Err(err) => {
            media.music_sign_in_pending = false;
            media.music_room_message = Some(err.clone());
            media.music_room_state = MusicRoomState::Error(err);
            MusicStartAction::Stop
        }
    }
}

pub fn needs_music_playback_refresh(control_ok: bool, playback: Option<&MusicPlayback>) -> bool {
    if !control_ok {
        return true;
    }
    playback.is_some_and(|p| !p.source.is_empty() && !p.source.eq_ignore_ascii_case("page"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn start_reply(ok: bool, state: &str, message: Option<&str>) -> PlayerStartReply {
        PlayerStartReply {
            ok,
            state: state.into(),
            message: message.map(Into::into),
        }
    }

    #[test]
    fn music_start_loading_retries_fast_then_loads_views_when_ready() {
        let mut media = MediaState::empty_for_host();
        media.music_service = MusicService::Apple;

        let first =
            apply_music_start_reply(&mut media, true, Ok(start_reply(true, "loading", None)), 0);
        assert_eq!(media.music_room_state, MusicRoomState::Loading);
        assert_eq!(
            first,
            MusicStartAction::RetryAfter {
                delay_ms: MUSIC_START_FAST_POLL_MS,
                next_attempt: 1,
            }
        );

        let slow = apply_music_start_reply(
            &mut media,
            true,
            Ok(start_reply(true, "loading", None)),
            MUSIC_START_FAST_ATTEMPTS,
        );
        assert_eq!(
            slow,
            MusicStartAction::RetryAfter {
                delay_ms: MUSIC_START_SLOW_POLL_MS,
                next_attempt: MUSIC_START_FAST_ATTEMPTS + 1,
            }
        );

        let ready =
            apply_music_start_reply(&mut media, true, Ok(start_reply(true, "running", None)), 2);
        assert_eq!(media.music_room_state, MusicRoomState::Running);
        assert_eq!(ready, MusicStartAction::LoadViews);
    }

    #[test]
    fn music_start_stops_when_left_room_or_attempt_limit_reached() {
        let mut media = MediaState::empty_for_host();
        media.music_service = MusicService::Apple;

        let left =
            apply_music_start_reply(&mut media, false, Ok(start_reply(true, "loading", None)), 0);
        assert_eq!(left, MusicStartAction::Stop);
        assert_eq!(media.music_room_state, MusicRoomState::Loading);

        let timed_out = apply_music_start_reply(
            &mut media,
            true,
            Ok(start_reply(true, "loading", None)),
            MUSIC_START_MAX_ATTEMPTS,
        );
        assert_eq!(timed_out, MusicStartAction::Stop);
        assert!(
            matches!(media.music_room_state, MusicRoomState::Error(ref msg) if msg.contains("Apple Music"))
        );
    }

    #[test]
    fn music_start_keeps_running_on_transient_loading_and_polls_pending_sign_in() {
        let mut media = MediaState::demo();
        let retry =
            apply_music_start_reply(&mut media, true, Ok(start_reply(true, "loading", None)), 0);
        assert_eq!(media.music_room_state, MusicRoomState::Running);
        assert_eq!(
            retry,
            MusicStartAction::RetryAfter {
                delay_ms: MUSIC_START_FAST_POLL_MS,
                next_attempt: 1,
            }
        );

        let idle_signed_out = apply_music_start_reply(
            &mut media,
            true,
            Ok(start_reply(true, "signedOut", None)),
            0,
        );
        assert_eq!(media.music_room_state, MusicRoomState::SignedOut);
        assert_eq!(idle_signed_out, MusicStartAction::Stop);

        media.music_sign_in_pending = true;
        let polling_signed_out = apply_music_start_reply(
            &mut media,
            true,
            Ok(start_reply(true, "signedOut", None)),
            0,
        );
        assert_eq!(
            polling_signed_out,
            MusicStartAction::RetryAfter {
                delay_ms: MUSIC_SIGN_IN_POLL_MS,
                next_attempt: 1,
            }
        );

        let completed =
            apply_music_start_reply(&mut media, true, Ok(start_reply(true, "running", None)), 1);
        assert_eq!(completed, MusicStartAction::LoadViews);
        assert!(!media.music_sign_in_pending);
    }

    #[test]
    fn sync_music_service_clears_views_and_bumps_generation_on_change() {
        let mut s = AppState::default();
        assert_eq!(s.media.music_service, MusicService::Apple);
        assert!(!s.media.music_home_sections.is_empty());
        let before_gen = s.media.music_start_gen;

        s.settings
            .ui_prefs
            .insert("exo.musicWebService".into(), json!("spotify"));
        s.sync_music_service_from_settings();

        assert_eq!(s.media.music_service, MusicService::Spotify);
        assert_eq!(s.media.music_room_state, MusicRoomState::Loading);
        assert!(s.media.music_home_sections.is_empty());
        assert!(s.media.music_recent.is_empty());
        assert!(s.media.music_library.is_empty());
        assert_ne!(s.media.music_start_gen, before_gen);
    }

    #[test]
    fn page_control_ok_skips_follow_up_rpcs() {
        let engine_pb = MusicPlayback {
            provider: "apple".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        let page_pb = MusicPlayback {
            source: "page".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        assert!(!needs_music_playback_refresh(true, Some(&engine_pb)));
        assert!(!needs_music_playback_refresh(true, Some(&page_pb)));
        assert!(!needs_music_playback_refresh(true, None));
    }

    #[test]
    fn failed_or_headless_control_requests_single_playback_refresh() {
        let engine_pb = MusicPlayback {
            provider: "apple".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        let gsmtc_pb = MusicPlayback {
            source: "session".into(),
            status: "playing".into(),
            title: "Song".into(),
            ..Default::default()
        };
        assert!(needs_music_playback_refresh(false, Some(&engine_pb)));
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

    #[test]
    fn share_urls_and_act_encoding_match_react_adapters() {
        assert_eq!(
            spotify_share_url("spotify:track:4uLU6hMCjMI75M1A2tKUQC").as_deref(),
            Some("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC")
        );
        assert_eq!(
            spotify_share_url("spotify:user:erix:playlist:37i9dQZF1DXcBWIGoYBM5M").as_deref(),
            Some("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")
        );
        assert_eq!(spotify_share_url("spotify:folder:abc"), None);

        assert_eq!(
            youtube_share_url("ytm:track:dQw4w9WgXcQ").as_deref(),
            Some("https://music.youtube.com/watch?v=dQw4w9WgXcQ")
        );
        assert_eq!(
            youtube_share_url("ytm:playlist:VLPL12345").as_deref(),
            Some("https://music.youtube.com/playlist?list=PL12345")
        );
        assert_eq!(
            youtube_share_url("ytm:artist:UC12345").as_deref(),
            Some("https://music.youtube.com/channel/UC12345")
        );

        let sp_del = encode_act_params(
            MusicService::Spotify,
            "deletePlaylist",
            json!({ "uri": "spotify:playlist:123" }),
        );
        assert_eq!(sp_del["action"], "like");
        assert_eq!(sp_del["on"], false);

        let yt_create = encode_act_params(
            MusicService::YouTube,
            "createPlaylist",
            json!({ "name": "Chill", "description": "Late night", "privacy": "PRIVATE" }),
        );
        assert_eq!(yt_create["action"], "createPlaylist");
        let parsed: Value = serde_json::from_str(yt_create["text"].as_str().unwrap()).unwrap();
        assert_eq!(parsed["name"], "Chill");
        assert_eq!(parsed["privacy"], "PRIVATE");
    }

    #[test]
    fn listen_along_sync_and_matching_rules() {
        assert!(same_song("Midnight City", "midnight city (remastered)"));
        assert!(!same_song("Midnight City", "Instant Crush"));
        assert_eq!(expected_position_ms(10_000, 25_000, 200.0), 15_200.0);
        assert_eq!(next_seek_latency_ms(None, 300.0), Some(300.0));
        assert_eq!(next_seek_latency_ms(Some(300.0), 600.0), Some(400.0));
        assert!(!should_correct_listen_drift(
            true,
            Some(10_000.0),
            11_000.0,
            true,
            10_000,
            Some(60_000)
        ));
        assert!(should_correct_listen_drift(
            true,
            Some(10_000.0),
            12_000.0,
            true,
            10_000,
            Some(60_000)
        ));
        assert!(!should_correct_listen_drift(
            false,
            Some(10_000.0),
            14_000.0,
            true,
            25_000,
            Some(60_000)
        ));
        assert!(should_correct_listen_drift(
            false,
            Some(10_000.0),
            16_000.0,
            true,
            25_000,
            Some(60_000)
        ));
    }

    #[test]
    fn lyrics_clock_and_jam_token_and_crash_leash() {
        let lines = vec![
            LyricsLine {
                start: 5000.0,
                text: "First".into(),
                ..Default::default()
            },
            LyricsLine {
                start: 12000.0,
                text: "Second".into(),
                ..Default::default()
            },
        ];
        assert_eq!(lyrics_active_index(&lines, true, Some(4000.0)), None);
        assert_eq!(lyrics_active_index(&lines, true, Some(5500.0)), Some(0));
        assert_eq!(lyrics_active_index(&lines, true, Some(15000.0)), Some(1));
        assert_eq!(lyrics_active_index(&lines, false, Some(15000.0)), None);
        let gaps = lyrics_gaps(&lines);
        assert_eq!(gaps[0], Some((0.0, 5000.0)));

        assert_eq!(
            parse_direct_jam_token("abcDEF_123"),
            Some("abcDEF_123".into())
        );
        assert_eq!(
            parse_direct_jam_token("spotify:socialsession:tok99"),
            Some("tok99".into())
        );
        assert_eq!(parse_direct_jam_token("https://spotify.link/abc"), None);

        let mut s = AppState::default();
        s.switch_room(Room::Music);
        s.media.music_service = MusicService::Spotify;
        s.media.music_room_state = MusicRoomState::Running;
        s.media.music_clock_s = 100.0;
        let ev = MusicStatusEvent {
            service: "spotify".into(),
            state: "error".into(),
        };
        assert!(s.on_music_status(ev.clone()));
        s.media.music_room_state = MusicRoomState::Running;
        s.media.music_clock_s = 110.0;
        assert!(s.on_music_status(ev.clone()));
        s.media.music_room_state = MusicRoomState::Running;
        s.media.music_clock_s = 120.0;
        assert!(s.on_music_status(ev));
        assert!(matches!(s.media.music_room_state, MusicRoomState::Error(_)));
    }

    #[test]
    fn sleep_timer_and_mute_restore_behave_like_react() {
        let mut s = AppState::default();
        s.media.music_volume = 0.62;
        s.media.music_last_audible_volume = 0.62;
        s.music_toggle_mute();
        assert!(s.media.music_muted);
        assert_eq!(s.media.music_volume, 0.0);
        s.music_toggle_mute();
        assert!(!s.media.music_muted);
        assert!((s.media.music_volume - 0.62).abs() < 1e-6);

        s.music_set_sleep_minutes(Some(30));
        assert!(s.media.sleep_minutes_left().is_some());
        s.media.music_sleep_deadline_ms = Some(now_epoch_ms() - 1000);
        s.media.playback = Some(MusicPlayback {
            status: "playing".into(),
            title: "Track".into(),
            ..Default::default()
        });
        assert!(s.music_tick(1.0));
        assert!(s.media.music_sleep_deadline_ms.is_none());
        assert_eq!(s.media.playback.as_ref().unwrap().status, "paused");
    }
}
