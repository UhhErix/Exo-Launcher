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
    pub duration_ms: i64,
    #[serde(default)]
    pub explicit: bool,
    #[serde(default)]
    pub liked: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlayerSection {
    #[serde(default)]
    pub title: String,
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
    pub tracks: Vec<PlayerTrack>,
    #[serde(default)]
    pub header: Option<PlayerHeader>,
    #[serde(default)]
    pub total: Option<u32>,
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
#[serde(rename_all = "camelCase")]
pub struct CaptureSettingsDto {
    #[serde(default)]
    pub quality: i32,
    #[serde(default)]
    pub replay_seconds: i32,
    #[serde(default)]
    pub max_fps: i32,
    #[serde(default)]
    pub replay_mode: String,
    #[serde(default)]
    pub cursor: bool,
    #[serde(default)]
    pub save_replay_keys: String,
    #[serde(default)]
    pub record_keys: String,
    #[serde(default)]
    pub snip_keys: String,
    #[serde(default)]
    pub auto_shrink: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptureLiveState {
    #[serde(default)]
    pub replay: bool,
    #[serde(default)]
    pub recording: bool,
    #[serde(default)]
    pub present: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptureStatusReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub present: bool,
    #[serde(default)]
    pub folder: Option<String>,
    #[serde(default)]
    pub state: Option<CaptureLiveState>,
    #[serde(default)]
    pub settings: Option<CaptureSettingsDto>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptureItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub game: String,
    #[serde(default)]
    pub game_id: Option<String>,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub created: String,
    #[serde(default)]
    pub size: u64,
    #[serde(default)]
    pub duration_ms: Option<f64>,
    #[serde(default)]
    pub favourite: bool,
    #[serde(default)]
    pub url: String,
    #[serde(default)]
    pub thumb: Option<String>,
    #[serde(default)]
    pub derived: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CaptureListReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub items: Vec<CaptureItem>,
}

pub fn format_duration_ms(ms: f64) -> String {
    let total_secs = (ms.max(0.0) / 1000.0).round() as u64;
    let mins = total_secs / 60;
    let secs = total_secs % 60;
    format!("{mins}:{secs:02}")
}
