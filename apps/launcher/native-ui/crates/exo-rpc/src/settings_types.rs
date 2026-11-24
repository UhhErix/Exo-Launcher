use crate::types::null_as_default;
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Default, PartialEq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RiotBaseline {
    #[serde(default)]
    pub valorant_minutes: Option<f64>,
    #[serde(default)]
    pub league_minutes: Option<f64>,
    #[serde(default)]
    pub tft_minutes: Option<f64>,
    #[serde(default)]
    pub lor_minutes: Option<f64>,
    #[serde(default)]
    pub two_xko_minutes: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RiotHoursRefresh {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub profile_private: bool,
    #[serde(default)]
    pub skipped: bool,
    #[serde(default)]
    pub partial: bool,
    #[serde(default)]
    pub unconfigured: bool,
    #[serde(default)]
    pub source: Option<String>,
    #[serde(default)]
    pub client_needed: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub valorant_minutes: Option<f64>,
    #[serde(default)]
    pub league_minutes: Option<f64>,
    #[serde(default)]
    pub tft_minutes: Option<f64>,
    #[serde(default)]
    pub baseline: Option<RiotBaseline>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RiotExportImport {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub cancelled: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub baseline: Option<RiotBaseline>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RiotBaselineReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub baseline: Option<RiotBaseline>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreStatus {
    #[serde(default)]
    pub store: String,
    #[serde(default)]
    pub display_name: String,
    #[serde(default)]
    pub agent_present: bool,
    #[serde(default)]
    pub client_present: Option<bool>,
    #[serde(default)]
    pub backend_present: Option<bool>,
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub cache_present: bool,
    #[serde(default)]
    pub detail: Option<String>,
    #[serde(default)]
    pub check_code: Option<String>,
    #[serde(default)]
    pub checked_at_utc: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreLocalCheck {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub checked_at_utc: String,
    #[serde(default)]
    pub code: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoresAuthReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub requires_user_action: bool,
    #[serde(default)]
    pub handoff: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamAccountEntry {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub current: bool,
    #[serde(default)]
    pub remembered: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamAccountsInfo {
    #[serde(default)]
    pub current: Option<String>,
    #[serde(default)]
    pub offline: bool,
    #[serde(default)]
    pub can_offline: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub accounts: Vec<SteamAccountEntry>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RiotAccountEntry {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub current: bool,
    #[serde(default)]
    pub saved: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub last_in: Vec<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RiotAccountsInfo {
    #[serde(default)]
    pub current: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub accounts: Vec<RiotAccountEntry>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EpicAddedAccount {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub name: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EpicAccountsInfo {
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub launcher_differs: bool,
    #[serde(default)]
    pub can_import: bool,
    #[serde(default)]
    pub can_switch_launcher: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub added: Vec<EpicAddedAccount>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GogAccountInfo {
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub can_sign_out: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AmazonAccountInfo {
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoreAccounts {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub steam: Option<SteamAccountsInfo>,
    #[serde(default)]
    pub riot: Option<RiotAccountsInfo>,
    #[serde(default)]
    pub epic: EpicAccountsInfo,
    #[serde(default)]
    pub gog: GogAccountInfo,
    #[serde(default)]
    pub amazon: AmazonAccountInfo,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamAccountReply {
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub account_name: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamSignInStep {
    #[serde(default)]
    pub state: String,
    #[serde(default)]
    pub challenge_url: Option<String>,
    #[serde(default)]
    pub guard: Option<String>,
    #[serde(default)]
    pub account_name: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub interval_seconds: Option<u64>,
}

pub type StoreGiftsReply = crate::types::StoreGifts;

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountHeldItem {
    #[serde(default)]
    pub key: String,
    #[serde(default)]
    pub reason: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountSyncState {
    #[serde(default)]
    pub last_sync_at: Option<String>,
    #[serde(default)]
    pub pending: u32,
    #[serde(default, deserialize_with = "null_as_default")]
    pub held: Vec<ExoAccountHeldItem>,
    #[serde(default)]
    pub signed_out: bool,
    #[serde(default)]
    pub push_stopped: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountOverview {
    #[serde(default)]
    pub signed_in: bool,
    #[serde(default)]
    pub code_pending: bool,
    #[serde(default)]
    pub session_ended: bool,
    #[serde(default)]
    pub email: Option<String>,
    #[serde(default)]
    pub account_id: Option<String>,
    #[serde(default)]
    pub account_asked: bool,
    #[serde(default)]
    pub sync: ExoAccountSyncState,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountOutcome {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: String,
    #[serde(default)]
    pub error: Option<String>,
    #[serde(default)]
    pub retry_after_seconds: Option<u64>,
    #[serde(default)]
    pub offline: bool,
    #[serde(default)]
    pub recovery_code: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountSession {
    #[serde(default)]
    pub device: String,
    #[serde(default)]
    pub created: i64,
    #[serde(default)]
    pub last_used: i64,
    #[serde(default)]
    pub current: bool,
    #[serde(default)]
    pub this_pc: bool,
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountSessionsReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub error: Option<String>,
    #[serde(default, deserialize_with = "null_as_default")]
    pub sessions: Vec<ExoAccountSession>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountProbe {
    #[serde(default)]
    pub reachable: bool,
    #[serde(default)]
    pub game_running: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExoAccountFileReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub cancelled: bool,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupActionReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub files: Option<u32>,
    #[serde(default)]
    pub cancelled: bool,
    #[serde(default)]
    pub restarting: bool,
    #[serde(default)]
    pub restored: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TelemetrySentReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub payloads: Vec<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportPreviewReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub excerpt: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReportSendReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub issue: Option<u64>,
    #[serde(default)]
    pub r#ref: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WebClientOptions {
    #[serde(default)]
    pub music_service: Option<String>,
    #[serde(default)]
    pub microphone: String,
    #[serde(default)]
    pub camera: String,
    #[serde(default)]
    pub clipboard: String,
    #[serde(default)]
    pub notifications: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LeftoverSummary {
    #[serde(default)]
    pub folders: u32,
    #[serde(default)]
    pub files: u32,
    #[serde(default)]
    pub registry: u32,
    #[serde(default)]
    pub shortcuts: u32,
    #[serde(default)]
    pub bytes: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplacedApp {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub installed: bool,
    #[serde(default)]
    pub leftovers: u32,
    #[serde(default)]
    pub bytes: u64,
    #[serde(default)]
    pub needs_admin: bool,
    #[serde(default)]
    pub found: LeftoverSummary,
    #[serde(default)]
    pub cleared: Option<LeftoverSummary>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplacedScanReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub cleared: u32,
    #[serde(default, deserialize_with = "null_as_default")]
    pub apps: Vec<ReplacedApp>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplacedRemoveReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub message: Option<String>,
    #[serde(default)]
    pub removed: u32,
    #[serde(default)]
    pub left: u32,
    #[serde(default, deserialize_with = "null_as_default")]
    pub apps: Vec<ReplacedApp>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JunkItem {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub detail: String,
    #[serde(default)]
    pub bytes: u64,
    #[serde(default)]
    pub files: u32,
    #[serde(default)]
    pub auto: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JunkScanReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default, deserialize_with = "null_as_default")]
    pub items: Vec<JunkItem>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JunkCleanReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub bytes: Option<u64>,
    #[serde(default)]
    pub files: Option<u32>,
    #[serde(default)]
    pub skipped: u32,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PickFolderReply {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub cancelled: bool,
    #[serde(default)]
    pub path: Option<String>,
    #[serde(default)]
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AccountsProgressEvent {
    #[serde(default)]
    pub store: String,
    #[serde(default)]
    pub line: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupStoppedEvent {
    #[serde(default)]
    pub sentence: String,
    #[serde(default)]
    pub since: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ComponentsFailedEvent {
    #[serde(default)]
    pub feature: String,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ShadersEventPayload {
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub data: Value,
}

pub use crate::game_types::KeepStatus;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn deserializes_store_accounts_and_steam_step() {
        let json = r#"{
            "ok": true,
            "steam": {
                "current": "alice",
                "offline": false,
                "canOffline": true,
                "accounts": [{"key": "76561198", "name": "alice", "current": true, "remembered": true}]
            },
            "riot": null,
            "epic": {
                "signedIn": true,
                "name": "AliceEpic",
                "launcherDiffers": false,
                "canImport": true,
                "canSwitchLauncher": true,
                "added": [{"key": "e1", "name": "Alt"}]
            },
            "gog": {"signedIn": true, "canSignOut": true},
            "amazon": {"signedIn": false, "name": null}
        }"#;
        let parsed: StoreAccounts = serde_json::from_str(json).expect("store accounts");
        assert!(parsed.ok);
        assert_eq!(
            parsed.steam.as_ref().unwrap().current.as_deref(),
            Some("alice")
        );
        assert!(parsed.steam.as_ref().unwrap().can_offline);
        assert_eq!(parsed.epic.added.len(), 1);
        assert!(parsed.gog.can_sign_out);

        let step_json =
            r#"{"state":"waiting","challengeUrl":"https://s.team/q/1/999","intervalSeconds":5}"#;
        let step: SteamSignInStep = serde_json::from_str(step_json).expect("steam step");
        assert_eq!(step.state, "waiting");
        assert_eq!(
            step.challenge_url.as_deref(),
            Some("https://s.team/q/1/999")
        );
        assert_eq!(step.interval_seconds, Some(5));
    }

    #[test]
    fn deserializes_exo_account_overview_and_replaced_apps() {
        let json = r#"{
            "signedIn": true,
            "codePending": false,
            "sessionEnded": false,
            "email": "player@example.com",
            "accountAsked": true,
            "sync": {
                "lastSyncAt": "2026-11-23T00:00:00Z",
                "pending": 0,
                "held": [{"key": "cover-image/abc", "reason": "too-large"}],
                "signedOut": false,
                "pushStopped": false
            }
        }"#;
        let overview: ExoAccountOverview = serde_json::from_str(json).expect("overview");
        assert!(overview.signed_in);
        assert_eq!(overview.sync.held.len(), 1);
        assert_eq!(overview.sync.held[0].reason, "too-large");

        let rep_json = r#"{
            "ok": true,
            "cleared": 0,
            "apps": [{
                "id": "discord",
                "name": "Discord",
                "detail": "Replaced by Exo",
                "installed": true,
                "leftovers": 2,
                "bytes": 250000000,
                "needsAdmin": false,
                "found": {"folders": 1, "files": 1, "registry": 0, "shortcuts": 0, "bytes": 250000000},
                "cleared": null
            }]
        }"#;
        let rep: ReplacedScanReply = serde_json::from_str(rep_json).expect("replaced");
        assert_eq!(rep.apps.len(), 1);
        assert_eq!(rep.apps[0].found.folders, 1);
    }

    #[test]
    fn deserializes_keep_status() {
        let json =
            r#"{"pc":"optimized","games":{"steam:730":"optimized","epic:fn":"waitsForFirstRun"}}"#;
        let parsed: KeepStatus = serde_json::from_str(json).expect("keep status");
        assert_eq!(parsed.pc, "optimized");
        assert_eq!(
            parsed.games.get("steam:730").map(String::as_str),
            Some("optimized")
        );
        assert_eq!(
            parsed.games.get("epic:fn").map(String::as_str),
            Some("waitsForFirstRun")
        );
    }
}
