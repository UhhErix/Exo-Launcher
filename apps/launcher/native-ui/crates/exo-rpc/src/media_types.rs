use serde::Deserialize;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MusicPlayback {
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
pub struct MusicVolumeReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub level: Option<f64>,
    #[serde(default)]
    pub muted: bool,
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

#[derive(Debug, Clone, Default, Deserialize)]
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

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerLink {
    #[serde(default)]
    pub uri: String,
    #[serde(default)]
    pub kind: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerOption {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
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
}

#[derive(Debug, Clone, Default, Deserialize)]
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
    pub description: Option<String>,
    #[serde(default)]
    pub saved: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerViewData {
    #[serde(default, deserialize_with = "null_vec")]
    pub sections: Vec<PlayerSection>,
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
    pub tracks: Vec<PlayerTrack>,
    #[serde(default)]
    pub header: Option<PlayerHeader>,
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
    pub filters: Vec<PlayerOption>,
    #[serde(default)]
    pub filter: Option<String>,
    #[serde(default)]
    pub track_uri: Option<String>,
    #[serde(default)]
    pub shuffle: Option<bool>,
    #[serde(default)]
    pub repeat: Option<i32>,
    #[serde(default)]
    pub liked: Option<bool>,
    #[serde(default)]
    pub can_shuffle: Option<bool>,
    #[serde(default)]
    pub can_repeat: Option<bool>,
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
    pub quality: i32,
    pub replay_seconds: i32,
    pub max_fps: i32,
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

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NoteInfo {
    pub id: String,
    pub title: String,
    pub snippet: String,
    pub kind: String,
    pub name: String,
    pub edited: String,
    pub size: u64,
    pub pinned: bool,
    pub external: bool,
    pub unsaved: bool,
    pub game_id: Option<String>,
    pub encoding: String,
    pub eol: String,
    pub spelling: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesListReply {
    pub ok: bool,
    pub notes: Vec<NoteInfo>,
    pub tabs: Vec<String>,
    pub active: Option<String>,
    pub photos: Option<String>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesReadReply {
    pub ok: bool,
    pub text: Option<String>,
    pub note: Option<NoteInfo>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesSaveReply {
    pub ok: bool,
    pub note: Option<NoteInfo>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesPhotoReply {
    pub ok: bool,
    pub src: Option<String>,
    pub cancelled: bool,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesActReply {
    pub ok: bool,
    pub note: Option<NoteInfo>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesOpenReply {
    pub ok: bool,
    pub id: Option<String>,
    pub cancelled: bool,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesSaveAsReply {
    pub ok: bool,
    pub id: Option<String>,
    pub cancelled: bool,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesPendingReply {
    pub id: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesSpellReply {
    pub ok: bool,
    pub wrong: bool,
    pub suggestions: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesTextFilesReply {
    pub ok: bool,
    pub offered: bool,
    pub is_default: bool,
    pub message: Option<String>,
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
