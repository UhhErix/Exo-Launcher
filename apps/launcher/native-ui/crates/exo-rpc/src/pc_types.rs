use serde::Deserialize;
use std::collections::HashMap;

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
    pub name: Option<String>,
    #[serde(default)]
    pub processor: Option<String>,
    #[serde(default)]
    pub cpu: Option<String>,
    #[serde(default)]
    pub graphics: Option<String>,
    #[serde(default)]
    pub gpu: Option<String>,
    #[serde(default)]
    pub memory_gb: Option<f64>,
    #[serde(default)]
    pub memory_type: Option<String>,
    #[serde(default)]
    pub ram_gb: Option<f64>,
    #[serde(default)]
    pub motherboard: Option<String>,
    #[serde(default)]
    pub windows: Option<String>,
    #[serde(default)]
    pub windows_version: Option<String>,
    #[serde(default)]
    pub os: Option<String>,
    #[serde(default)]
    pub network: Option<String>,
    #[serde(default)]
    pub network_mbps: Option<f64>,
    #[serde(default)]
    pub display: Option<String>,
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
pub struct AutopilotStandbyPurge {
    #[serde(default)]
    pub available: bool,
    #[serde(default)]
    pub reason: Option<String>,
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
    pub standby_purge: Option<AutopilotStandbyPurge>,
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
pub struct ShaderEstimate {
    #[serde(default)]
    pub seconds: f64,
    #[serde(default)]
    pub low_seconds: Option<f64>,
    #[serde(default)]
    pub learned: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShaderSnapGame {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub install_dir: Option<String>,
    #[serde(default)]
    pub status_text: Option<String>,
    #[serde(default)]
    pub tone: Option<String>,
    #[serde(default)]
    pub queued: bool,
    #[serde(default)]
    pub compiling: bool,
    #[serde(default)]
    pub percent: Option<f64>,
    #[serde(default)]
    pub estimate: Option<ShaderEstimate>,
    #[serde(default)]
    pub in_add_all_ready: bool,
    #[serde(default)]
    pub error: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadersSnapshot {
    #[serde(default)]
    pub rev: u64,
    #[serde(default)]
    pub games: Vec<ShaderSnapGame>,
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
    #[serde(default)]
    pub snapshot: Option<ShadersSnapshot>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadersEstimatesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub seconds: HashMap<String, f64>,
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
    pub care: Option<String>,
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
pub struct PeripheralPort {
    #[serde(default)]
    pub r#where: Option<String>,
    #[serde(default)]
    pub chips: Option<i32>,
    #[serde(default)]
    pub better: Option<String>,
    #[serde(default)]
    pub hubs: i32,
    #[serde(default)]
    pub interrupts: Option<String>,
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
    pub maker: Option<String>,
    #[serde(default)]
    pub model: Option<String>,
    #[serde(default)]
    pub brand: Option<String>,
    #[serde(default)]
    pub role: Option<String>,
    #[serde(default)]
    pub connection: Option<String>,
    #[serde(default)]
    pub battery: Option<f64>,
    #[serde(default)]
    pub polling_hz: Option<i32>,
    #[serde(default, alias = "wooting")]
    pub lab: bool,
    #[serde(default)]
    pub color: Option<String>,
    #[serde(default)]
    pub layout: Option<String>,
    #[serde(default)]
    pub port: Option<PeripheralPort>,
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
pub struct DevicesImageReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub url: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppsIconsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub icons: HashMap<String, Option<String>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShaderPrepareGame {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadersPrepareGamesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub games: Vec<ShaderPrepareGame>,
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
    pub form_factor: Option<String>,
    #[serde(default)]
    pub hall_effect: bool,
    #[serde(default)]
    pub has_rapid_trigger: bool,
    #[serde(default)]
    pub has_rgb: bool,
    #[serde(default)]
    pub max_scan_hz: Option<i32>,
    #[serde(default)]
    pub min_actuation_mm: Option<f64>,
    #[serde(default)]
    pub max_actuation_mm: Option<f64>,
    #[serde(default)]
    pub firmware: Option<String>,
    #[serde(default)]
    pub layout: Option<String>,
    #[serde(default)]
    pub layout_code: Option<String>,
    #[serde(default)]
    pub active_profile: i32,
    #[serde(default)]
    pub profiles: Vec<KeyboardProfileSlot>,
    #[serde(default)]
    pub actuation_mm: Option<f64>,
    #[serde(default)]
    pub rapid_trigger: Option<bool>,
    #[serde(default)]
    pub rapid_trigger_mm: Option<f64>,
    #[serde(default)]
    pub tachyon: Option<bool>,
    #[serde(default)]
    pub rgb_brightness: Option<i32>,
    #[serde(default)]
    pub can_write: bool,
    #[serde(default)]
    pub wootility_installed: bool,
    #[serde(default)]
    pub background_service_running: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardBackupSummary {
    #[serde(default)]
    pub file_name: String,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub product_id: Option<i32>,
    #[serde(default)]
    pub firmware: Option<String>,
    #[serde(default)]
    pub created_utc: String,
    #[serde(default)]
    pub profile_count: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardBackupResult {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub created: Option<KeyboardBackupSummary>,
    #[serde(default)]
    pub backups: Vec<KeyboardBackupSummary>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeyboardBackupsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub backups: Vec<KeyboardBackupSummary>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupDay {
    #[serde(default)]
    pub day: String,
    #[serde(default)]
    pub at: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupOffer {
    #[serde(default)]
    pub day: String,
    #[serde(default)]
    pub device: String,
    #[serde(default)]
    pub files: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupNote {
    #[serde(default)]
    pub sentence: String,
    #[serde(default)]
    pub stopped: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupStatus {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub last: Option<BackupDay>,
    #[serde(default)]
    pub offer: Option<BackupOffer>,
    #[serde(default)]
    pub note: Option<BackupNote>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientsEpic {
    #[serde(default)]
    pub layout: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub old_still_there: bool,
    #[serde(default)]
    pub installing: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientsSteam {
    #[serde(default)]
    pub channel: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ClientsStatus {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub needs_fix: bool,
    #[serde(default)]
    pub problem: Option<String>,
    #[serde(default)]
    pub epic: ClientsEpic,
    #[serde(default)]
    pub steam: ClientsSteam,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn peripheral_card_accepts_lab_and_wooting_alias() {
        let from_lab: PeripheralCard = serde_json::from_str(
            r#"{"id":"kb-1","kind":"keyboard","name":"Wooting 60HE v2","lab":true}"#,
        )
        .expect("lab field");
        assert!(from_lab.lab);

        let from_wooting: PeripheralCard = serde_json::from_str(
            r#"{"id":"kb-2","kind":"keyboard","name":"Wooting 60HE","wooting":true}"#,
        )
        .expect("wooting alias");
        assert!(from_wooting.lab);
    }

    #[test]
    fn keyboard_read_reply_deserializes_60he_v2_and_optical_models() {
        let v2: KeyboardReadReply = serde_json::from_str(
            r#"{
                "ok": true,
                "model": "Wooting 60HE v2",
                "formFactor": "60",
                "hallEffect": true,
                "hasRapidTrigger": true,
                "hasRgb": true,
                "maxScanHz": 8000,
                "minActuationMm": 0.1,
                "maxActuationMm": 4.0,
                "firmware": "2.15.0-beta.0",
                "layout": "ANSI · Split spacebar",
                "layoutCode": "ansi-split",
                "activeProfile": 0,
                "profiles": [{"index": 0, "name": "General"}],
                "actuationMm": 3.0,
                "rapidTrigger": true,
                "rapidTriggerMm": 0.15,
                "tachyon": true,
                "rgbBrightness": 70,
                "canWrite": true,
                "wootilityInstalled": false,
                "backgroundServiceRunning": false
            }"#,
        )
        .expect("60HE v2 payload");
        assert!(v2.ok);
        assert_eq!(v2.form_factor.as_deref(), Some("60"));
        assert_eq!(v2.layout_code.as_deref(), Some("ansi-split"));
        assert_eq!(v2.rapid_trigger, Some(true));
        assert_eq!(v2.tachyon, Some(true));
        assert_eq!(v2.rgb_brightness, Some(70));
        assert_eq!(v2.min_actuation_mm, Some(0.1));
        assert_eq!(v2.max_actuation_mm, Some(4.0));
        assert!(v2.has_rapid_trigger);
        assert!(v2.has_rgb);

        let optical: KeyboardReadReply = serde_json::from_str(
            r#"{
                "ok": true,
                "model": "Wooting One",
                "formFactor": "tkl",
                "hallEffect": false,
                "hasRapidTrigger": false,
                "hasRgb": true,
                "minActuationMm": 1.5,
                "maxActuationMm": 3.6,
                "rapidTrigger": null,
                "tachyon": null,
                "canWrite": false,
                "backgroundServiceRunning": true
            }"#,
        )
        .expect("Wooting One optical payload");
        assert_eq!(optical.form_factor.as_deref(), Some("tkl"));
        assert!(!optical.has_rapid_trigger);
        assert_eq!(optical.rapid_trigger, None);
        assert_eq!(optical.tachyon, None);
        assert!(optical.background_service_running);
        assert_eq!(optical.min_actuation_mm, Some(1.5));
        assert_eq!(optical.max_actuation_mm, Some(3.6));
    }

    #[test]
    fn keyboard_backup_result_deserializes_product_id_and_firmware() {
        let res: KeyboardBackupResult = serde_json::from_str(
            r#"{
                "ok": true,
                "created": {
                    "fileName": "wooting-1342-20261123.json",
                    "model": "Wooting 60HE v2",
                    "productId": 4930,
                    "firmware": "2.15.0-beta.0",
                    "createdUtc": "2026-11-23T07:45:26Z",
                    "profileCount": 1
                },
                "backups": [{
                    "fileName": "wooting-1342-20261123.json",
                    "model": "Wooting 60HE v2",
                    "productId": 4930,
                    "firmware": "2.15.0-beta.0",
                    "createdUtc": "2026-11-23T07:45:26Z",
                    "profileCount": 1
                }]
            }"#,
        )
        .expect("backup result");
        assert!(res.ok);
        let created = res.created.expect("created summary");
        assert_eq!(created.product_id, Some(4930));
        assert_eq!(created.firmware.as_deref(), Some("2.15.0-beta.0"));
        assert_eq!(res.backups.len(), 1);
    }
}
