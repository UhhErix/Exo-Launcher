use serde::{Deserialize, Deserializer, Serialize};
use std::collections::HashMap;

fn null_as_default<'de, D, T>(d: D) -> Result<T, D::Error>
where
    D: Deserializer<'de>,
    T: Default + Deserialize<'de>,
{
    Ok(Option::<T>::deserialize(d)?.unwrap_or_default())
}

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
pub struct WindowsReleaseInfo {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub build: String,
    #[serde(default)]
    pub newest: Option<String>,
    #[serde(default)]
    pub newest_build: Option<i64>,
    #[serde(default)]
    pub up_to_date: Option<bool>,
    #[serde(default)]
    pub insider: bool,
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
    pub bios: Option<String>,
    #[serde(default)]
    pub bios_date: Option<String>,
    #[serde(default)]
    pub windows: Option<String>,
    #[serde(default)]
    pub windows_version: Option<String>,
    #[serde(default)]
    pub build: Option<String>,
    #[serde(default)]
    pub os: Option<String>,
    #[serde(default)]
    pub secure_boot: Option<bool>,
    #[serde(default)]
    pub tpm: Option<String>,
    #[serde(default)]
    pub memory_integrity: Option<bool>,
    #[serde(default)]
    pub network: Option<String>,
    #[serde(default)]
    pub network_mbps: Option<f64>,
    #[serde(default)]
    pub network_adapter: Option<String>,
    #[serde(default)]
    pub windows_release: Option<WindowsReleaseInfo>,
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
    #[serde(default)]
    pub gpu_answer: Option<GpuUsageAnswer>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotMemoryIntegrity {
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub reason: String,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub needs_restart: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotGameMode {
    #[serde(default)]
    pub off: bool,
    #[serde(default)]
    pub kept_on: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotMouseAccel {
    #[serde(default)]
    pub off: bool,
    #[serde(default)]
    pub managed: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotNow {
    #[serde(default)]
    pub game: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub did: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotFinding {
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub stutters: i32,
    #[serde(default)]
    pub app: Option<String>,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub suggestion: String,
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
    pub clock: Option<f64>,
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
    pub standby_gb: Option<f64>,
    #[serde(default)]
    pub free_gb: Option<f64>,
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
    pub started: String,
    #[serde(default)]
    pub minutes: f64,
    #[serde(default)]
    pub fps: f64,
    #[serde(default)]
    pub low1: f64,
    #[serde(default)]
    pub low01: f64,
    #[serde(default)]
    pub stutters: i32,
    #[serde(default, deserialize_with = "null_as_default")]
    pub findings: Vec<AutopilotFinding>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub did: Vec<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub learned: Vec<String>,
    #[serde(default)]
    pub islc_seconds: f64,
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
pub struct AutopilotTimerGlobal {
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub needed_for_win11: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotDownloadsPaused {
    #[serde(default)]
    pub active: bool,
    #[serde(default)]
    pub jobs: u32,
    #[serde(default, deserialize_with = "null_as_default")]
    pub services: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotState {
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub game_priority: String,
    #[serde(default)]
    pub keep_game_mode: bool,
    #[serde(default)]
    pub core_parking: bool,
    #[serde(default)]
    pub cpu: String,
    #[serde(default)]
    pub cpu_sets: Option<String>,
    #[serde(default)]
    pub timer_global: Option<AutopilotTimerGlobal>,
    #[serde(default)]
    pub downloads_paused: Option<AutopilotDownloadsPaused>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub always_demote: Vec<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub suspend: Vec<String>,
    #[serde(default)]
    pub game_mode: AutopilotGameMode,
    #[serde(default)]
    pub mouse_accel: Option<AutopilotMouseAccel>,
    #[serde(default)]
    pub now: Option<AutopilotNow>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub memory_integrity: AutopilotMemoryIntegrity,
    #[serde(default)]
    pub standby_purge: Option<AutopilotStandbyPurge>,
    #[serde(default)]
    pub live: Option<AutopilotLive>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub reports: Vec<AutopilotReport>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotSetReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub state: AutopilotState,
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
    pub off: bool,
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
pub struct TuningMigration {
    #[serde(default)]
    pub pending: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub keys: Vec<String>,
    #[serde(default)]
    pub summary: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub reverted: Vec<String>,
    #[serde(default)]
    pub restart: bool,
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
    pub migration: Option<TuningMigration>,
    #[serde(default)]
    pub items: Vec<TuningItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QosGame {
    #[serde(default)]
    pub game_id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub exe_name: String,
    #[serde(default)]
    pub enabled: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QosState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub nla_bypassed: bool,
    #[serde(default)]
    pub note: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub games: Vec<QosGame>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlayAdvisorItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub cost: String,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub can_toggle: bool,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub manual_steps: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OverlayAdvisorState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<OverlayAdvisorItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuAdapterUsage {
    #[serde(default)]
    pub luid: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub vendor_id: u32,
    #[serde(default)]
    pub dedicated_bytes: u64,
    #[serde(default)]
    pub shared_bytes: u64,
    #[serde(default)]
    pub software: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuEngineSlice {
    #[serde(default, rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub percent: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuProcessUsage {
    #[serde(default)]
    pub pid: u32,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub adapter_luid: String,
    #[serde(default)]
    pub adapter_name: String,
    #[serde(default)]
    pub total_percent: f64,
    #[serde(default, deserialize_with = "null_as_default")]
    pub engines: Vec<GpuEngineSlice>,
    #[serde(default)]
    pub local_bytes: u64,
    #[serde(default)]
    pub non_local_bytes: u64,
    #[serde(default)]
    pub protected_reason: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuUsageAnswer {
    #[serde(default)]
    pub summary: String,
    #[serde(default)]
    pub top_gpu: Option<GpuProcessUsage>,
    #[serde(default)]
    pub top_vram: Option<GpuProcessUsage>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub hogs: Vec<GpuProcessUsage>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuProcessesState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub adapters: Vec<GpuAdapterUsage>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub processes: Vec<GpuProcessUsage>,
    #[serde(default)]
    pub answer: GpuUsageAnswer,
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
    #[serde(default)]
    pub installed_on: Option<String>,
    #[serde(default)]
    pub quiet_since: Option<String>,
    #[serde(default)]
    pub uninstaller: bool,
    #[serde(default)]
    pub caution: Option<String>,
    #[serde(default)]
    pub replaces: Option<String>,
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
    pub app: Option<String>,
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
    pub old_caches: u64,
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
pub struct DriveVendor {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub tool: String,
    #[serde(default)]
    pub url: String,
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
    pub vendor: Option<DriveVendor>,
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

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TuneMeasure {
    #[serde(default)]
    pub score: f64,
    #[serde(default)]
    pub peak_c: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TunePart {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub modes: bool,
    #[serde(default)]
    pub mode: String,
    #[serde(default)]
    pub testing: Option<String>,
    #[serde(default)]
    pub minutes_left: Option<f64>,
    #[serde(default)]
    pub applied: Option<String>,
    #[serde(default)]
    pub before: Option<TuneMeasure>,
    #[serde(default)]
    pub after: Option<TuneMeasure>,
    #[serde(default)]
    pub note: Option<String>,
    #[serde(default)]
    pub warning: Option<String>,
    #[serde(default)]
    pub minutes: f64,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub blocked: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoTuneState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub parts: Vec<TunePart>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NetSample {
    #[serde(default)]
    pub ping: Option<f64>,
    #[serde(default)]
    pub jitter: Option<f64>,
    #[serde(default)]
    pub router: Option<f64>,
    #[serde(default)]
    pub lookup: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NetCheck {
    #[serde(default)]
    pub before: Option<NetSample>,
    #[serde(default)]
    pub after: NetSample,
    #[serde(default)]
    pub said: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PcHealthItem {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub url: Option<String>,
    #[serde(default)]
    pub applied: bool,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub busy: bool,
    #[serde(default)]
    pub cpu: Option<String>,
    #[serde(default)]
    pub current: bool,
    #[serde(default)]
    pub held: bool,
    #[serde(default)]
    pub off: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PcHealth {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<PcHealthItem>,
    #[serde(default)]
    pub check: Option<NetCheck>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterruptDevice {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub mode: String,
    #[serde(default)]
    pub plan: String,
    #[serde(default)]
    pub reason: String,
    #[serde(default)]
    pub changed: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InterruptsState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub restart: bool,
    #[serde(default)]
    pub result: Option<String>,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub reset: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub devices: Vec<InterruptDevice>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuProfileScreen {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub hz: f64,
    #[serde(default)]
    pub vrr: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuProfileItem {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub value: String,
    #[serde(default)]
    pub why: String,
    #[serde(default)]
    pub current: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuFeature {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub state: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuProfile {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub vendor: Option<String>,
    #[serde(default)]
    pub gpu: Option<String>,
    #[serde(default)]
    pub series: i32,
    #[serde(default)]
    pub screen: GpuProfileScreen,
    #[serde(default)]
    pub status: String,
    #[serde(default)]
    pub can_undo: bool,
    #[serde(default)]
    pub driver_updated: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<GpuProfileItem>,
    #[serde(default)]
    pub low_latency: Option<String>,
    #[serde(default)]
    pub vrr_enabled: Option<bool>,
    #[serde(default)]
    pub rebar: Option<bool>,
    #[serde(default)]
    pub summary: Option<String>,
    #[serde(default)]
    pub skipped: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub features: Vec<GpuFeature>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct OutputColor {
    #[serde(default)]
    pub format: String,
    #[serde(default)]
    pub bpc: i32,
    #[serde(default)]
    pub full: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayBest {
    #[serde(default)]
    pub refresh: i32,
    #[serde(default)]
    pub color: Option<OutputColor>,
    #[serde(default)]
    pub why: Option<String>,
    #[serde(default)]
    pub scaling: bool,
    #[serde(default)]
    pub over: bool,
    #[serde(default)]
    pub video_hdr: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ColorControl {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub value: f64,
    #[serde(default)]
    pub min: f64,
    #[serde(default)]
    pub max: f64,
    #[serde(default)]
    pub step: f64,
    #[serde(default)]
    pub normal: f64,
    #[serde(default)]
    pub unit: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayAdjust {
    #[serde(default)]
    pub nvidia: Option<bool>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub controls: Vec<ColorControl>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayDdc {
    #[serde(default)]
    pub brightness: Option<i32>,
    #[serde(default)]
    pub brightness_min: i32,
    #[serde(default)]
    pub brightness_max: i32,
    #[serde(default)]
    pub contrast: Option<i32>,
    #[serde(default)]
    pub contrast_min: i32,
    #[serde(default)]
    pub contrast_max: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayScreen {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub main: bool,
    #[serde(default)]
    pub hdr: bool,
    #[serde(default)]
    pub hdr_supported: bool,
    #[serde(default)]
    pub vendor: Option<String>,
    #[serde(default)]
    pub refresh: i32,
    #[serde(default)]
    pub refresh_label: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub refreshes: Vec<i32>,
    #[serde(default)]
    pub color: Option<OutputColor>,
    #[serde(default)]
    pub auto: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub supported: Vec<OutputColor>,
    #[serde(default)]
    pub best: DisplayBest,
    #[serde(default)]
    pub is_best: bool,
    #[serde(default)]
    pub adjust: Option<DisplayAdjust>,
    #[serde(default)]
    pub ddc: Option<DisplayDdc>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DisplayState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub displays: Vec<DisplayScreen>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverProgress {
    #[serde(default)]
    pub stage: String,
    #[serde(default)]
    pub percent: f64,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverLatest {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub size: u64,
    #[serde(default)]
    pub released: Option<String>,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub page: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverSkipped {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub why: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverAdvised {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub why: String,
    #[serde(default)]
    pub back: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub vendor: Option<String>,
    #[serde(default)]
    pub card: Option<String>,
    #[serde(default)]
    pub installed: Option<String>,
    #[serde(default)]
    pub workstation: bool,
    #[serde(default)]
    pub latest: Option<DriverLatest>,
    #[serde(default)]
    pub update: bool,
    #[serde(default)]
    pub skipped: Option<DriverSkipped>,
    #[serde(default)]
    pub advised: Option<DriverAdvised>,
    #[serde(default)]
    pub software: bool,
    #[serde(default)]
    pub blocked: Option<String>,
    #[serde(default)]
    pub progress: Option<DriverProgress>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverEntryAdvised {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub why: String,
    #[serde(default)]
    pub action: String,
    #[serde(default)]
    pub to: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverEntry {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub maker: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub date: Option<String>,
    #[serde(default)]
    pub latest: Option<String>,
    #[serde(default)]
    pub latest_date: Option<String>,
    #[serde(default)]
    pub how: String,
    #[serde(default)]
    pub page: Option<String>,
    #[serde(default)]
    pub source: Option<String>,
    #[serde(default)]
    pub app: bool,
    #[serde(default)]
    pub skipped: Option<DriverSkipped>,
    #[serde(default)]
    pub advised: Option<DriverEntryAdvised>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriversState {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub busy: Option<String>,
    #[serde(default)]
    pub running: bool,
    #[serde(default)]
    pub restart: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<DriverEntry>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverStep {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub working: Option<String>,
    #[serde(default)]
    pub ok: Option<bool>,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub restart: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BiosStaged {
    #[serde(default)]
    pub version: String,
    #[serde(default)]
    pub file: String,
    #[serde(default)]
    pub bitlocker: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BiosUpdateReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub staged: Option<BiosStaged>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Afmf3State {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub offer: bool,
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub note: Option<String>,
}

pub use crate::game_types::{CompressState, KeepStatus};

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CompressSettings {
    #[serde(default)]
    pub while_idle: bool,
    #[serde(default)]
    pub after_updates: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompressListGame {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub compress: CompressState,
    #[serde(default)]
    pub eligible: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompressList {
    #[serde(default)]
    pub ok: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub games: Vec<CompressListGame>,
    #[serde(default)]
    pub saved: u64,
    #[serde(default)]
    pub potential: u64,
    #[serde(default)]
    pub settings: CompressSettings,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompressActReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub compress: Option<CompressState>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompressAllReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub queued: i32,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CompressSettingsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub settings: CompressSettings,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub group: String,
    #[serde(default)]
    pub installed: Option<String>,
    #[serde(default)]
    pub latest: String,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub detail: Option<String>,
    #[serde(default)]
    pub size: u64,
    #[serde(default)]
    pub end_of_support: bool,
    #[serde(default)]
    pub bulk: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeResult {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub outcome: String,
    #[serde(default)]
    pub message: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimesListReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub busy: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<RuntimeItem>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimesInstallReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub declined: bool,
    #[serde(default)]
    pub canceled: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub results: Vec<RuntimeResult>,
    #[serde(default)]
    pub items: Option<Vec<RuntimeItem>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimesProgress {
    #[serde(default)]
    pub phase: String,
    #[serde(default)]
    pub id: Option<String>,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub index: usize,
    #[serde(default)]
    pub count: usize,
    #[serde(default)]
    pub bytes: u64,
    #[serde(default)]
    pub total: Option<u64>,
    #[serde(default)]
    pub done_bytes: u64,
    #[serde(default)]
    pub all_bytes: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShaderRebuild {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub ready: i32,
    #[serde(default)]
    pub total: i32,
}

pub use crate::settings_types::LeftoverSummary;

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppLeftover {
    #[serde(default)]
    pub id: i64,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub path: String,
    #[serde(default)]
    pub bytes: u64,
    #[serde(default)]
    pub selected: bool,
    #[serde(default)]
    pub note: Option<String>,
    #[serde(default)]
    pub certainty: String,
    #[serde(default)]
    pub because: String,
    #[serde(default)]
    pub store: bool,
    #[serde(default)]
    pub machine: bool,
    #[serde(default)]
    pub from: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppAutoCleared {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub cleared: LeftoverSummary,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppFind {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub token: String,
    #[serde(default)]
    pub name: String,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<AppLeftover>,
    #[serde(default)]
    pub auto: bool,
    #[serde(default)]
    pub cleared: Option<LeftoverSummary>,
    #[serde(default)]
    pub undo: Option<String>,
    #[serde(default)]
    pub declined: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub auto_cleared: Vec<AppAutoCleared>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub defaults: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppCleanReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub cleared: Option<LeftoverSummary>,
    #[serde(default)]
    pub undo: Option<String>,
    #[serde(default)]
    pub left: i32,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppSkip {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub reason: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppConfirmReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub ids: Vec<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub skipped: Vec<AppSkip>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RestorePointReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub made: bool,
    #[serde(default)]
    pub declined: bool,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppUndoReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub back: i32,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallerCacheReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub files: i32,
    #[serde(default)]
    pub bytes: u64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallerCacheCleanReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub files: i32,
    #[serde(default)]
    pub bytes: u64,
    #[serde(default)]
    pub undo: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowsApp {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub publisher: Option<String>,
    #[serde(default)]
    pub for_me: bool,
    #[serde(default)]
    pub others: i32,
    #[serde(default)]
    pub provisioned: bool,
    #[serde(default)]
    pub bloat: Option<String>,
    #[serde(default)]
    pub add_back: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppsPackagesReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub hidden: i32,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<WindowsApp>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowsAppLine {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub does: String,
    #[serde(default)]
    pub back: String,
    #[serde(default)]
    pub undoable: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppsPackagesPlanReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub token: Option<String>,
    #[serde(default)]
    pub admin: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub lines: Vec<WindowsAppLine>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppsPackagesRemoveReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub removed: i32,
    #[serde(default)]
    pub left: i32,
    #[serde(default)]
    pub message: Option<String>,
}

pub use crate::settings_types::{JunkCleanReply, JunkItem, JunkScanReply};

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FpsCounterReply {
    #[serde(default)]
    pub on: bool,
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub message: Option<String>,
}

pub use crate::settings_types::WebClientOptions;

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
