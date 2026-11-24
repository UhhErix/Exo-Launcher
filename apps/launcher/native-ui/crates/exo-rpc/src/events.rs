use crate::discord_types::DiscordStatus;
use crate::media_types::{CaptureItem, MusicPlaybackReply};
use crate::types::{ArtBase, LauncherSettings, LibraryPayload};
use serde::Deserialize;

#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct RequestId(pub String);

impl RequestId {
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShellBootstrap {
    pub ok: bool,
    pub art_base: ArtBase,
    pub settings: LauncherSettings,
    pub library: LibraryPayload,
    pub music_playback: MusicPlaybackReply,
    pub discord_status: DiscordStatus,
    pub binary_media_pipe: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MusicVolumeEvent {
    pub level: f64,
    pub source: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureSavedEvent {
    pub item: CaptureItem,
    pub edit: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureReelEvent {
    pub state: String,
    pub percent: f64,
    pub message: Option<String>,
    pub wait: bool,
    pub item: Option<CaptureItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CaptureShrunkEvent {
    pub id: String,
    pub item: Option<CaptureItem>,
    pub before: u64,
    pub after: u64,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct NotesOpenEvent {
    pub id: Option<String>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PausedDownloadItem {
    pub game_id: String,
    pub title: String,
    pub store: String,
    pub action: String,
    pub can_resume: bool,
    pub reason: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct InstallQueueEvent {
    pub queued_game_ids: Vec<String>,
    pub paused_downloads: Vec<PausedDownloadItem>,
    pub held: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameRunStateEvent {
    pub id: String,
    pub is_running: bool,
    pub can_stop: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct LaunchStatusEvent {
    pub game_id: String,
    pub ok: bool,
    pub message: Option<String>,
    pub phase: String,
    pub process_id: Option<u32>,
    pub backend_started: Option<String>,
    pub handoff_only: bool,
    pub needs_dependencies: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AppUpdateStateEvent {
    pub state: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AutopilotReportEvent {
    pub game: String,
    pub title: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StoreChangedEvent {
    pub what: String,
}
