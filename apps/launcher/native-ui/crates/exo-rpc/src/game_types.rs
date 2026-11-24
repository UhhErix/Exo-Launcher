use crate::types::{Game, null_as_default};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DriveHeat {
    pub peak: f64,
    pub warn: Option<f64>,
    #[serde(deserialize_with = "null_as_default")]
    pub slowed_seconds: f64,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct PropertyChoice {
    #[serde(deserialize_with = "null_as_default")]
    pub value: String,
    #[serde(deserialize_with = "null_as_default")]
    pub label: String,
    pub detail: Option<String>,
    pub size_bytes: Option<u64>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct GameContent {
    #[serde(deserialize_with = "null_as_default")]
    pub id: String,
    #[serde(deserialize_with = "null_as_default")]
    pub name: String,
    #[serde(deserialize_with = "null_as_default")]
    pub enabled: bool,
    pub size_bytes: Option<u64>,
    #[serde(deserialize_with = "null_as_default")]
    pub toggle: bool,
    pub state: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct MovingProgress {
    #[serde(deserialize_with = "null_as_default")]
    pub done: u64,
    #[serde(deserialize_with = "null_as_default")]
    pub total: u64,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct AntiCheatLaunch {
    #[serde(deserialize_with = "null_as_default")]
    pub value: String,
    #[serde(deserialize_with = "null_as_default")]
    pub detail: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameSettingsGroup {
    #[serde(deserialize_with = "null_as_default")]
    pub id: String,
    #[serde(deserialize_with = "null_as_default")]
    pub label: String,
    #[serde(deserialize_with = "null_as_default")]
    pub on: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub exo: bool,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameSettingsState {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    pub supported: Option<bool>,
    pub note: Option<String>,
    pub title: Option<String>,
    pub running: Option<bool>,
    pub found: Option<bool>,
    pub waiting: Option<bool>,
    pub groups: Option<Vec<GameSettingsGroup>>,
    pub standing: Option<String>,
    pub again: Option<bool>,
    pub window_mode: Option<String>,
    pub smooth_motion: Option<bool>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GraphicsFeatureRow {
    #[serde(deserialize_with = "null_as_default")]
    pub id: String,
    #[serde(deserialize_with = "null_as_default")]
    pub name: String,
    #[serde(deserialize_with = "null_as_default")]
    pub state: String,
    #[serde(deserialize_with = "null_as_default")]
    pub route: String,
    #[serde(deserialize_with = "null_as_default")]
    pub route_kind: String,
    #[serde(deserialize_with = "null_as_default")]
    pub can_toggle: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub needs_anti_cheat_confirm: bool,
    pub anti_cheat_warning: Option<String>,
    pub closest_option: Option<String>,
    pub details: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameGraphicsFeaturesState {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    pub engine: Option<String>,
    pub running: Option<bool>,
    pub anti_cheat: Option<String>,
    pub features: Option<Vec<GraphicsFeatureRow>>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct KeepStatus {
    #[serde(deserialize_with = "null_as_default")]
    pub pc: String,
    #[serde(deserialize_with = "null_as_default")]
    pub games: HashMap<String, String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DlssStatusItem {
    #[serde(deserialize_with = "null_as_default")]
    pub key: String,
    #[serde(deserialize_with = "null_as_default")]
    pub file_name: String,
    pub display_name: Option<String>,
    pub api: Option<String>,
    pub current_version: Option<String>,
    pub file_version: Option<String>,
    pub current_release: Option<String>,
    pub latest_version: Option<String>,
    pub latest_release: Option<String>,
    pub state: Option<String>,
    pub origin: Option<String>,
    pub original_version: Option<String>,
    pub original_state: Option<String>,
    pub can_update: Option<bool>,
    pub can_restore: Option<bool>,
    pub skip_reason: Option<String>,
    #[serde(rename = "mod")]
    pub mod_name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DlssStatus {
    pub ok: Option<bool>,
    pub message: Option<String>,
    pub anti_cheat: Option<String>,
    pub consent: Option<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub items: Vec<DlssStatusItem>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DlssFileOutcome {
    pub file_name: Option<String>,
    pub folder: Option<String>,
    pub state: Option<String>,
    pub before: Option<String>,
    pub after: Option<String>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DlssRunResult {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub updated: Option<u32>,
    pub skipped: Option<u32>,
    pub failed: Option<u32>,
    pub message: Option<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub files: Vec<DlssFileOutcome>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct FrameGenMod {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub state: String,
    pub path: Option<String>,
    pub version: Option<String>,
    pub session: Option<String>,
    #[serde(rename = "mod")]
    pub mod_name: Option<String>,
    pub anti_cheat: Option<String>,
    pub note: Option<String>,
    pub reason: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GpuGame {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    pub vendor: Option<String>,
    pub supported: Option<bool>,
    pub afmf: Option<bool>,
    pub amd_software: Option<bool>,
    pub frame_gen: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HdrAutoHdr {
    pub global: Option<bool>,
    pub game: Option<bool>,
    #[serde(deserialize_with = "null_as_default")]
    pub effective: String,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HdrGame {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    pub hdr: Option<String>,
    pub reason: Option<String>,
    pub mode: Option<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub cursor_fix: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub cursor_fix_eligible: bool,
    pub sdr_white_nits: Option<f64>,
    pub auto_hdr: Option<HdrAutoHdr>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CompressState {
    #[serde(deserialize_with = "null_as_default")]
    pub state: String,
    pub percent: Option<f64>,
    #[serde(deserialize_with = "null_as_default")]
    pub saved: u64,
    pub size: Option<u64>,
    pub estimate: Option<u64>,
    #[serde(deserialize_with = "null_as_default")]
    pub sampled: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub changed: u64,
    pub algorithm: Option<String>,
    pub message: Option<String>,
    pub anti_cheat: Option<String>,
    pub held: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CompressStateReply {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    pub compress: Option<CompressState>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShaderWarmed {
    #[serde(deserialize_with = "null_as_default")]
    pub at: String,
    pub driver: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ShaderStatus {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub state: String,
    pub reason: Option<String>,
    pub engine_id: Option<String>,
    pub warmed: Option<ShaderWarmed>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ArtworkMutationResponse {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub cancelled: bool,
    pub message: Option<String>,
    pub game: Option<Game>,
    pub art_revision: Option<u64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DequeueReply {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub queued_game_ids: Vec<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub paused_downloads: Vec<crate::events::PausedDownloadItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct InstallResponse {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub message: String,
    pub path: Option<String>,
    pub progress: Option<crate::types::InstallProgress>,
    #[serde(deserialize_with = "null_as_default")]
    pub queued: bool,
    pub queued_game_ids: Option<Vec<String>>,
    pub paused_downloads: Option<Vec<crate::events::PausedDownloadItem>>,
    #[serde(deserialize_with = "null_as_default")]
    pub handoff_only: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub needs_dependencies: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct UninstallResponse {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub message: Option<String>,
    #[serde(deserialize_with = "null_as_default")]
    pub queued: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub handoff_only: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct OpenUrlReply {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    pub lookalike: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameAchievementsEvent {
    #[serde(deserialize_with = "null_as_default")]
    pub id: String,
    #[serde(deserialize_with = "null_as_default")]
    pub unlocked: u32,
    #[serde(deserialize_with = "null_as_default")]
    pub total: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameOptimizeEvent {
    #[serde(deserialize_with = "null_as_default")]
    pub title: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct CompressProgressEvent {
    #[serde(deserialize_with = "null_as_default")]
    pub id: String,
    pub compress: CompressState,
}

#[derive(Debug, Clone, Default, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", default)]
pub struct DlssNewerItem {
    #[serde(deserialize_with = "null_as_default")]
    pub family: String,
    #[serde(deserialize_with = "null_as_default")]
    pub version: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct DlssNewerReply {
    #[serde(deserialize_with = "null_as_default")]
    pub ok: bool,
    #[serde(deserialize_with = "null_as_default")]
    pub items: Vec<DlssNewerItem>,
}
