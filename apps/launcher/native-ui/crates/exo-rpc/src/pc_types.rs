use serde::Deserialize;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateCheckReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub already_latest: bool,
    #[serde(default)]
    pub update_available: bool,
    #[serde(default)]
    pub latest: Option<String>,
    #[serde(default)]
    pub current: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub state: Option<String>,
    #[serde(default)]
    pub staged: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateProgress {
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub percent: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemAbout {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub cpu: Option<String>,
    #[serde(default)]
    pub gpu: Option<String>,
    #[serde(default)]
    pub ram_gb: Option<f64>,
    #[serde(default)]
    pub os: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAiGaming {
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub game_id: Option<String>,
    #[serde(default)]
    pub game_title: Option<String>,
    #[serde(default)]
    pub demoted_count: i32,
    #[serde(default)]
    pub timer_res_active: bool,
    #[serde(default)]
    pub alt_tabbed: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAiFans {
    #[serde(default)]
    pub supported: bool,
    #[serde(default)]
    pub preset: String,
    #[serde(default)]
    pub temp_c: Option<i32>,
    #[serde(default)]
    pub fan_percent: Option<i32>,
    #[serde(default)]
    pub target_percent: Option<i32>,
    #[serde(default)]
    pub fan_count: i32,
    #[serde(default)]
    pub note: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAiState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub auto_apply_best_settings: bool,
    #[serde(default)]
    pub background_drift_check: bool,
    #[serde(default)]
    pub last_check_utc: Option<String>,
    #[serde(default)]
    pub auto_applied: Vec<String>,
    #[serde(default)]
    pub needs_attention: Vec<String>,
    #[serde(default)]
    pub gaming: ExoAiGaming,
    #[serde(default)]
    pub fans: ExoAiFans,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotMemoryIntegrity {
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub reason: String,
    #[serde(default)]
    pub needs_restart: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotLive {
    #[serde(default)]
    pub game: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub seconds: f64,
    #[serde(default)]
    pub fps: f64,
    #[serde(default)]
    pub low1: f64,
    #[serde(default)]
    pub low01: f64,
    #[serde(default)]
    pub stutters: i32,
    #[serde(default)]
    pub cpu: Option<f64>,
    #[serde(default)]
    pub gpu: Option<f64>,
    #[serde(default)]
    pub gpu_temp: Option<f64>,
    #[serde(default)]
    pub vram_gb: Option<f64>,
    #[serde(default)]
    pub vram_total_gb: Option<f64>,
    #[serde(default)]
    pub ram_gb: Option<f64>,
    #[serde(default)]
    pub ram_total_gb: Option<f64>,
    #[serde(default)]
    pub dpc: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotReport {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub game: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub minutes: f64,
    #[serde(default)]
    pub fps: f64,
    #[serde(default)]
    pub low1: f64,
    #[serde(default)]
    pub stutters: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotState {
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub cpu: String,
    #[serde(default)]
    pub memory_integrity: AutopilotMemoryIntegrity,
    #[serde(default)]
    pub live: Option<AutopilotLive>,
    #[serde(default)]
    pub reports: Vec<AutopilotReport>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LatencySample {
    #[serde(default)]
    pub dpc_us: f64,
    #[serde(default)]
    pub dpc_driver: Option<String>,
    #[serde(default)]
    pub isr_us: f64,
    #[serde(default)]
    pub isr_driver: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LatencyReport {
    #[serde(default)]
    pub before: Option<LatencySample>,
    #[serde(default)]
    pub after: Option<LatencySample>,
    #[serde(default)]
    pub said: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TuningItem {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub opt_in: bool,
    #[serde(default)]
    pub restart: bool,
    #[serde(default)]
    pub needs_restart: bool,
    #[serde(default)]
    pub blocked: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemTuningState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub applied: bool,
    #[serde(default)]
    pub restart: bool,
    #[serde(default)]
    pub latency: Option<LatencyReport>,
    #[serde(default)]
    pub items: Vec<TuningItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HdrState {
    #[serde(default)]
    pub supported: bool,
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub peak_nits: Option<i32>,
    #[serde(default)]
    pub calibrated: bool,
    #[serde(default)]
    pub verdict: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadersAddonStatus {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub present: bool,
    #[serde(default)]
    pub state: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstalledAppItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub publisher: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub bytes: Option<u64>,
    #[serde(default)]
    pub store: bool,
    #[serde(default)]
    pub removable: bool,
    #[serde(default)]
    pub kept: Option<String>,
    #[serde(default)]
    pub bloat: Option<String>,
    #[serde(default)]
    pub last_opened: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppsListReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub programs: Vec<InstalledAppItem>,
    #[serde(default)]
    pub edge_removed: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StartupItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub source: String,
    #[serde(default)]
    pub machine: bool,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub publisher: Option<String>,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub does: Option<String>,
    #[serde(default)]
    pub verdict: Option<String>,
    #[serde(default)]
    pub unneeded: bool,
    #[serde(default)]
    pub missing: bool,
    #[serde(default)]
    pub locked: bool,
    #[serde(default)]
    pub read_only: bool,
    #[serde(default)]
    pub status: Option<String>,
    #[serde(default)]
    pub memory: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StartupRemovedItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub at: String,
    #[serde(default)]
    pub locked: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StartupListReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub items: Vec<StartupItem>,
    #[serde(default)]
    pub removed: Vec<StartupRemovedItem>,
    #[serde(default)]
    pub extras: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveFix {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub applied: bool,
    #[serde(default)]
    pub restart: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveLink {
    #[serde(rename = "gen", default)]
    pub pcie_gen: i32,
    #[serde(default)]
    pub width: i32,
    #[serde(default)]
    pub max_gen: i32,
    #[serde(default)]
    pub max_width: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveTemp {
    #[serde(default)]
    pub now: i32,
    #[serde(default)]
    pub warn: Option<i32>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveHealth {
    #[serde(default)]
    pub life_used: Option<i32>,
    #[serde(default)]
    pub spare: Option<i32>,
    #[serde(default)]
    pub written_tb: Option<f64>,
    #[serde(default)]
    pub hours: Option<i64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveVolume {
    #[serde(default)]
    pub letter: String,
    #[serde(default)]
    pub total_gb: f64,
    #[serde(default)]
    pub free_gb: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveSpaceBreakdown {
    #[serde(default)]
    pub total: u64,
    #[serde(default)]
    pub free: u64,
    #[serde(default)]
    pub games: u64,
    #[serde(default)]
    pub caches: u64,
    #[serde(default)]
    pub exo: u64,
    #[serde(default)]
    pub leftovers: u64,
    #[serde(default)]
    pub other: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveGameEntry {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub bytes: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveVerdict {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub text: String,
    #[serde(default)]
    pub bad: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveInfo {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub link: Option<DriveLink>,
    #[serde(default)]
    pub temperature: Option<DriveTemp>,
    #[serde(default)]
    pub health: Option<DriveHealth>,
    #[serde(default)]
    pub volumes: Vec<DriveVolume>,
    #[serde(default)]
    pub write_cache: Option<bool>,
    #[serde(default)]
    pub build: Option<String>,
    #[serde(default)]
    pub space: Option<DriveSpaceBreakdown>,
    #[serde(default)]
    pub games: Vec<DriveGameEntry>,
    #[serde(default)]
    pub verdicts: Vec<DriveVerdict>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DrivesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub fixes: Vec<DriveFix>,
    #[serde(default)]
    pub drives: Vec<DriveInfo>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PeripheralCard {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub brand: Option<String>,
    #[serde(default)]
    pub role: Option<String>,
    #[serde(default)]
    pub connection: Option<String>,
    #[serde(default)]
    pub battery: Option<i32>,
    #[serde(default)]
    pub polling_hz: Option<i32>,
    #[serde(default)]
    pub wooting: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DevicesListReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub devices: Vec<PeripheralCard>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioEndpoint {
    #[serde(default)]
    pub volume: Option<i32>,
    #[serde(default)]
    pub muted: bool,
    #[serde(default)]
    pub format: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioVoiceInfo {
    #[serde(default)]
    pub noise: String,
    #[serde(default)]
    pub push_to_talk: bool,
    #[serde(default)]
    pub focus: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AudioReadReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub output: Option<AudioEndpoint>,
    #[serde(default)]
    pub mic: Option<AudioEndpoint>,
    #[serde(default)]
    pub voice: AudioVoiceInfo,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardProfileSlot {
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardReadReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub model: Option<String>,
    #[serde(default)]
    pub hall_effect: bool,
    #[serde(default)]
    pub max_scan_hz: Option<i32>,
    #[serde(default)]
    pub firmware: Option<String>,
    #[serde(default)]
    pub layout: Option<String>,
    #[serde(default)]
    pub active_profile: i32,
    #[serde(default)]
    pub profiles: Vec<KeyboardProfileSlot>,
    #[serde(default)]
    pub actuation_mm: Option<f64>,
    #[serde(default)]
    pub rapid_trigger: bool,
    #[serde(default)]
    pub rapid_trigger_mm: Option<f64>,
    #[serde(default)]
    pub tachyon: bool,
    #[serde(default)]
    pub can_write: bool,
    #[serde(default)]
    pub wootility_installed: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardBackupSummary {
    #[serde(default)]
    pub file_name: String,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub created_utc: String,
    #[serde(default)]
    pub profile_count: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardBackupsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub backups: Vec<KeyboardBackupSummary>,
}
