use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicPlayback {
    #[serde(default)]
    pub seq: Option<u64>,
    #[serde(default)]
    pub provider: String,
    #[serde(default)]
    pub source: String,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub artist: String,
    #[serde(default)]
    pub album: String,
    #[serde(default)]
    pub artwork_url: Option<String>,
    #[serde(default)]
    pub artwork_data_url: Option<String>,
    #[serde(default)]
    pub position_ms: Option<i64>,
    #[serde(default)]
    pub duration_ms: Option<i64>,
    #[serde(default)]
    pub position_age_ms: Option<f64>,
    #[serde(default)]
    pub rate: Option<f64>,
    #[serde(default)]
    pub can_play: bool,
    #[serde(default)]
    pub can_pause: bool,
    #[serde(default)]
    pub can_prev: bool,
    #[serde(default)]
    pub can_next: bool,
    #[serde(default)]
    pub can_seek: bool,
}

impl MusicPlayback {
    pub fn is_playing(&self) -> bool {
        self.status.eq_ignore_ascii_case("playing")
    }

    pub fn has_track(&self) -> bool {
        !self.title.trim().is_empty() && !self.status.eq_ignore_ascii_case("none")
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicPlaybackReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub playback: Option<MusicPlayback>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicControlReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub cause: Option<String>,
    #[serde(default)]
    pub device_id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicVolumeReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub level: Option<f64>,
    #[serde(default)]
    pub muted: bool,
    #[serde(default)]
    pub source: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicMatchSong {
    pub title: String,
    pub artists: Vec<String>,
    pub duration_ms: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicMatchReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub index: Option<i32>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicStatusEvent {
    #[serde(default)]
    pub service: String,
    #[serde(default)]
    pub state: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerActReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub uri: Option<String>,
}

fn null_vec<'de, D, T>(d: D) -> Result<Vec<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Deserialize<'de>,
{
    Ok(Option::<Vec<T>>::deserialize(d)?.unwrap_or_default())
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerStartReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerCard {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub subtitle: String,
    #[serde(default)]
    pub image: String,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub pinned: bool,
    #[serde(default)]
    pub owned: bool,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerLink {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub kind: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerOption {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub detail: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerArtist {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerAlbumRef {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerTrack {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub uid: String,
    #[serde(default)]
    pub title: String,
    #[serde(default, deserialize_with = "null_vec")]
    pub artists: Vec<PlayerArtist>,
    #[serde(default)]
    pub album: Option<PlayerAlbumRef>,
    #[serde(default)]
    pub image: String,
    #[serde(default)]
    pub duration_ms: f64,
    #[serde(default)]
    pub explicit: bool,
    #[serde(default)]
    pub playable: bool,
    #[serde(default)]
    pub liked: Option<bool>,
    #[serde(default)]
    pub disliked: Option<bool>,
    #[serde(default)]
    pub in_library: Option<bool>,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub date: Option<String>,
    #[serde(default)]
    pub played: Option<bool>,
    #[serde(default)]
    pub progress_ms: Option<f64>,
}

impl PlayerTrack {
    pub fn artist_line(&self) -> String {
        self.artists
            .iter()
            .map(|a| a.name.as_str())
            .filter(|n| !n.is_empty())
            .collect::<Vec<_>>()
            .join(", ")
    }

    pub fn is_episode(&self) -> bool {
        self.uri.starts_with("spotify:episode:")
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerSection {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub more: Option<PlayerLink>,
    #[serde(default, deserialize_with = "null_vec")]
    pub items: Vec<PlayerCard>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerHeader {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub subtitle: String,
    #[serde(default)]
    pub image: String,
    #[serde(default)]
    pub banner: Option<String>,
    #[serde(default)]
    pub description: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub artists: Vec<PlayerArtist>,
    #[serde(default)]
    pub saved: Option<bool>,
    #[serde(default)]
    pub editable: Option<bool>,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub badges: Vec<String>,
    #[serde(default)]
    pub about: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub facts: Vec<String>,
    #[serde(default)]
    pub station: Option<String>,
    #[serde(default)]
    pub privacy: Option<String>,
    #[serde(default)]
    pub radio: Option<bool>,
    #[serde(default)]
    pub owned: Option<bool>,
    #[serde(default)]
    pub public: Option<bool>,
    #[serde(default)]
    pub collaborative: Option<bool>,
    #[serde(default)]
    pub downloaded: Option<Value>,
    #[serde(default)]
    pub date: Option<String>,
    #[serde(default)]
    pub label: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub copyright: Vec<String>,
    #[serde(default)]
    pub duration_ms: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArtistAboutCity {
    #[serde(default)]
    pub city: String,
    #[serde(default)]
    pub country: String,
    #[serde(default)]
    pub listeners: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArtistAboutLink {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub url: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArtistAbout {
    #[serde(default)]
    pub bio: String,
    #[serde(default)]
    pub followers: u64,
    #[serde(default)]
    pub listeners: u64,
    #[serde(default)]
    pub rank: u64,
    #[serde(default)]
    pub image: String,
    #[serde(default, deserialize_with = "null_vec")]
    pub cities: Vec<ArtistAboutCity>,
    #[serde(default, deserialize_with = "null_vec")]
    pub links: Vec<ArtistAboutLink>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Concert {
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub date: String,
    #[serde(default)]
    pub place: String,
    #[serde(default)]
    pub venue: String,
    #[serde(default)]
    pub uri: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreditPerson {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub roles: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreditGroup {
    #[serde(default)]
    pub role: String,
    #[serde(default, deserialize_with = "null_vec")]
    pub people: Vec<CreditPerson>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerDevice {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub r#type: String,
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub local: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerFriendUser {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub image: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerFriendContext {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerFriendTrack {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub image: String,
    #[serde(default)]
    pub artist: Option<PlayerArtist>,
    #[serde(default)]
    pub context: Option<PlayerFriendContext>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerFriend {
    #[serde(default)]
    pub user: PlayerFriendUser,
    #[serde(default)]
    pub track: PlayerFriendTrack,
    #[serde(default)]
    pub at: i64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JamMember {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub image: String,
    #[serde(default)]
    pub me: bool,
    #[serde(default)]
    pub host: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JamSession {
    #[serde(default)]
    pub owner: bool,
    #[serde(default)]
    pub link: String,
    #[serde(default)]
    pub can_remove: bool,
    #[serde(default, deserialize_with = "null_vec")]
    pub members: Vec<JamMember>,
    #[serde(default)]
    pub guests_play: Option<bool>,
    #[serde(default)]
    pub guest_volume: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LyricsWord {
    #[serde(default)]
    pub start: f64,
    #[serde(default)]
    pub end: f64,
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub bg: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LyricsLine {
    #[serde(default)]
    pub start: f64,
    #[serde(default)]
    pub text: String,
    #[serde(default, deserialize_with = "null_vec")]
    pub words: Vec<LyricsWord>,
    #[serde(default)]
    pub agent: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerRating {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub liked: Option<bool>,
    #[serde(default)]
    pub disliked: Option<bool>,
    #[serde(default)]
    pub in_library: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerFolderRef {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub parent: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyEqFilter {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub frequency: f64,
    #[serde(default)]
    pub gain: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyEq {
    #[serde(default)]
    pub supported: bool,
    #[serde(default)]
    pub enabled: Option<bool>,
    #[serde(default)]
    pub filters: Option<Vec<SpotifyEqFilter>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyAudio {
    #[serde(default)]
    pub quality: Option<i32>,
    #[serde(default)]
    pub lossless: bool,
    #[serde(default)]
    pub crossfade: bool,
    #[serde(default)]
    pub crossfade_ms: i32,
    #[serde(default)]
    pub gapless: bool,
    #[serde(default)]
    pub auto_mix: bool,
    #[serde(default)]
    pub normalize: bool,
    #[serde(default)]
    pub mono: bool,
    #[serde(default)]
    pub exclusive: Option<bool>,
    #[serde(default)]
    pub smart_shuffle: Option<bool>,
    #[serde(default)]
    pub equalizer: SpotifyEq,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SpotifyAudioReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub data: Option<SpotifyAudio>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerStateData {
    #[serde(default)]
    pub track_uri: String,
    #[serde(default)]
    pub context_uri: String,
    #[serde(default)]
    pub shuffle: bool,
    #[serde(default)]
    pub repeat: i32,
    #[serde(default)]
    pub liked: bool,
    #[serde(default)]
    pub can_shuffle: bool,
    #[serde(default)]
    pub can_repeat: bool,
    #[serde(default)]
    pub disliked: Option<bool>,
    #[serde(default)]
    pub can_video: Option<bool>,
    #[serde(default)]
    pub video: Option<bool>,
    #[serde(default)]
    pub speed: Option<f64>,
    #[serde(default)]
    pub image: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub artists: Vec<PlayerArtist>,
    #[serde(default)]
    pub autoplay: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerStateReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub data: Option<PlayerStateData>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerViewData {
    #[serde(default, deserialize_with = "null_vec")]
    pub sections: Vec<PlayerSection>,
    #[serde(default)]
    pub more: Option<PlayerLink>,
    #[serde(default, deserialize_with = "null_vec")]
    pub items: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub top: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub artists: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub albums: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub playlists: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub shows: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub audiobooks: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub episodes: Vec<PlayerTrack>,
    #[serde(default, deserialize_with = "null_vec")]
    pub singles: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub related: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub compilations: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub appears_on: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub featuring: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub discovered_on: Vec<PlayerCard>,
    #[serde(default)]
    pub about: Option<ArtistAbout>,
    #[serde(default, deserialize_with = "null_vec")]
    pub concerts: Vec<Concert>,
    #[serde(default, deserialize_with = "null_vec")]
    pub tags: Vec<PlayerOption>,
    #[serde(default)]
    pub uri: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub tracks: Vec<PlayerTrack>,
    #[serde(default)]
    pub header: Option<PlayerHeader>,
    #[serde(default)]
    pub rating: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub ratings: Vec<PlayerRating>,
    #[serde(default)]
    pub offset: Option<u32>,
    #[serde(default)]
    pub total: Option<u32>,
    #[serde(default)]
    pub context_uri: Option<String>,
    #[serde(default)]
    pub current: Option<PlayerTrack>,
    #[serde(default, deserialize_with = "null_vec")]
    pub queued: Vec<PlayerTrack>,
    #[serde(default, deserialize_with = "null_vec")]
    pub next: Vec<PlayerTrack>,
    #[serde(default, deserialize_with = "null_vec")]
    pub autoplayed: Vec<PlayerTrack>,
    #[serde(default, deserialize_with = "null_vec")]
    pub stations: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub videos: Vec<PlayerCard>,
    #[serde(default, deserialize_with = "null_vec")]
    pub devices: Vec<PlayerDevice>,
    #[serde(default, deserialize_with = "null_vec")]
    pub filters: Vec<PlayerOption>,
    #[serde(default)]
    pub filter: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub sorts: Vec<PlayerOption>,
    #[serde(default)]
    pub sort: Option<String>,
    #[serde(default)]
    pub folder: Option<PlayerFolderRef>,
    #[serde(default, deserialize_with = "null_vec")]
    pub friends: Vec<PlayerFriend>,
    #[serde(default)]
    pub experience: Option<String>,
    #[serde(default)]
    pub session: Option<JamSession>,
    #[serde(default, deserialize_with = "null_vec")]
    pub lines: Vec<LyricsLine>,
    #[serde(default)]
    pub synced: Option<bool>,
    #[serde(default, deserialize_with = "null_vec")]
    pub qualities: Vec<PlayerOption>,
    #[serde(default)]
    pub quality: Option<String>,
    #[serde(default, deserialize_with = "null_vec")]
    pub groups: Vec<CreditGroup>,
    #[serde(default)]
    pub sources: Option<String>,
    #[serde(default)]
    pub track_uri: Option<String>,
    #[serde(default)]
    pub shuffle: Option<bool>,
    #[serde(default)]
    pub repeat: Option<i32>,
    #[serde(default)]
    pub liked: Option<bool>,
    #[serde(default)]
    pub disliked: Option<bool>,
    #[serde(default)]
    pub can_shuffle: Option<bool>,
    #[serde(default)]
    pub can_repeat: Option<bool>,
    #[serde(default)]
    pub can_video: Option<bool>,
    #[serde(default)]
    pub video: Option<bool>,
    #[serde(default)]
    pub speed: Option<f64>,
    #[serde(default)]
    pub autoplay: Option<bool>,
    #[serde(default)]
    pub image: Option<String>,
    #[serde(default)]
    pub title: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerViewReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub data: Option<PlayerViewData>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureHotkeys {
    pub save_replay: bool,
    pub record: bool,
    pub snip: bool,
    pub print_screen: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureSettingsDto {
    pub replay_seconds: i32,
    pub replay_mode: String,
    pub cursor: bool,
    pub save_replay_keys: String,
    pub record_keys: String,
    pub snip_keys: String,
    pub print_screen: bool,
    pub after_snip: String,
    pub folder: String,
    pub keep_gb: i32,
    pub keep_days: i32,
    pub auto_shrink: bool,
    pub call_voices: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureLiveState {
    pub replay: bool,
    pub running: bool,
    pub buffering: bool,
    pub replay_retained_ms: Option<f64>,
    pub replay_limit_seconds: Option<f64>,
    pub recording: bool,
    pub holds_call: bool,
    pub recording_ms: Option<f64>,
    pub codec: Option<String>,
    pub encoder: Option<String>,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub fps: Option<f64>,
    pub hdr: bool,
    pub error: Option<String>,
    pub hotkeys: CaptureHotkeys,
    pub present: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureStatusReply {
    pub ok: bool,
    pub message: Option<String>,
    pub present: bool,
    pub folder: Option<String>,
    pub state: Option<CaptureLiveState>,
    pub settings: Option<CaptureSettingsDto>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureItem {
    pub id: String,
    pub kind: String,
    pub game: String,
    pub game_id: Option<String>,
    pub name: String,
    pub created: String,
    pub size: u64,
    pub duration_ms: Option<f64>,
    pub favourite: bool,
    pub url: String,
    pub thumb: Option<String>,
    pub derived: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureListReply {
    pub ok: bool,
    pub items: Vec<CaptureItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureItemReply {
    pub ok: bool,
    pub message: Option<String>,
    pub item: Option<CaptureItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureEditReply {
    pub ok: bool,
    pub message: Option<String>,
    pub item: Option<CaptureItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureReelReply {
    pub ok: bool,
    pub message: Option<String>,
    pub wait: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureTextReply {
    pub ok: bool,
    pub text: Option<String>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureSendReply {
    pub ok: bool,
    pub message: Option<String>,
    pub sent: Vec<String>,
    pub item: Option<CaptureItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureSaveImageReply {
    pub ok: bool,
    pub message: Option<String>,
    pub item: Option<CaptureItem>,
}

pub fn format_duration_ms(ms: f64) -> String {
    let total_secs = (ms.max(0.0) / 1000.0).floor() as u64;
    let hours = total_secs / 3600;
    let mins = (total_secs % 3600) / 60;
    let secs = total_secs % 60;
    if hours > 0 {
        format!("{hours}:{mins:02}:{secs:02}")
    } else {
        format!("{mins}:{secs:02}")
    }
}

pub const MUSIC_SNAPSHOT_VERSION: u32 = 1;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicServiceSnapshot {
    #[serde(default)]
    pub home_sections: Vec<PlayerSection>,
    #[serde(default)]
    pub recent: Vec<PlayerCard>,
    #[serde(default)]
    pub library: Vec<PlayerCard>,
    #[serde(default)]
    pub library_filters: Vec<PlayerOption>,
    #[serde(default)]
    pub library_top_filters: Vec<PlayerOption>,
    #[serde(default)]
    pub library_sorts: Vec<PlayerOption>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicDiskSnapshot {
    pub version: u32,
    #[serde(default)]
    pub services: std::collections::HashMap<String, MusicServiceSnapshot>,
}
