use crate::events::{EventRegistry, HostEventKind};
use crate::pc_state::SettingsSection;
use crate::state::{AppState, HostStatus};
use exo_rpc::{
    AccountsProgressEvent, ActionResult, BackupActionReply, BackupStatus, BackupStoppedEvent,
    ClientsStatus, ComponentsFailedEvent, ExoAccountFileReply, ExoAccountHeldItem,
    ExoAccountOutcome, ExoAccountOverview, ExoAccountPasskeyStatus, ExoAccountProbe,
    ExoAccountSession, ExoAccountSessionsReply, HostEvent, JunkCleanReply, JunkItem, JunkScanReply,
    KeepStatus, LauncherSettings, LeftoverSummary, METHOD_ACCOUNTS_EPIC_REMOVE,
    METHOD_ACCOUNTS_LIST, METHOD_ACHIEVEMENTS_PREVIEW, METHOD_APP_REPAIR_CACHE,
    METHOD_AUTH_NATIVE_PROVIDERS, METHOD_BACKUP_DECLINE, METHOD_BACKUP_EXPORT,
    METHOD_BACKUP_IMPORT, METHOD_BACKUP_NOW, METHOD_BACKUP_RESTORE, METHOD_BACKUP_STATUS,
    METHOD_CLIENTS_FIX, METHOD_CLIENTS_STATUS, METHOD_EXO_ACCOUNT_CHANGE_PASSWORD,
    METHOD_EXO_ACCOUNT_CODE_CONFIRMED, METHOD_EXO_ACCOUNT_CREATE, METHOD_EXO_ACCOUNT_DELETE,
    METHOD_EXO_ACCOUNT_EXPORT, METHOD_EXO_ACCOUNT_NEW_RECOVERY_CODE, METHOD_EXO_ACCOUNT_OVERVIEW,
    METHOD_EXO_ACCOUNT_PASSKEY_DELETE, METHOD_EXO_ACCOUNT_PASSKEY_REGISTER,
    METHOD_EXO_ACCOUNT_PASSKEY_SIGN_IN, METHOD_EXO_ACCOUNT_PASSKEY_STATUS,
    METHOD_EXO_ACCOUNT_PROBE, METHOD_EXO_ACCOUNT_RESET, METHOD_EXO_ACCOUNT_REVOKE,
    METHOD_EXO_ACCOUNT_SAVE_CODE, METHOD_EXO_ACCOUNT_SESSIONS, METHOD_EXO_ACCOUNT_SIGN_IN,
    METHOD_EXO_ACCOUNT_SIGN_OUT, METHOD_EXO_ACCOUNT_SYNC, METHOD_GIFTS_LIST,
    METHOD_GIFTS_STEAM_SIGN_IN, METHOD_GIFTS_STEAM_SIGN_OUT, METHOD_JUNK_CLEAN, METHOD_JUNK_SCAN,
    METHOD_KEEP_STATUS, METHOD_PLAYTIME_IMPORT_RIOT_EXPORT, METHOD_PLAYTIME_RIOT_REFRESH,
    METHOD_PLAYTIME_SET_RIOT_BASELINE, METHOD_REPLACED_REMOVE, METHOD_REPLACED_SCAN,
    METHOD_REPORT_PREVIEW, METHOD_REPORT_SEND, METHOD_SETTINGS_GET, METHOD_SETTINGS_SET,
    METHOD_SHADERS_ADDON, METHOD_SHADERS_CALL, METHOD_SHELL_OPEN_URL, METHOD_SHELL_PICK_FOLDER,
    METHOD_SHELL_SHOW_STORE, METHOD_STEAM_ACCOUNT, METHOD_STEAM_SET_OFFLINE,
    METHOD_STEAM_SIGN_IN_CANCEL, METHOD_STEAM_SIGN_IN_GUARD, METHOD_STEAM_SIGN_IN_PASSWORD,
    METHOD_STEAM_SIGN_IN_POLL, METHOD_STEAM_SIGN_IN_QR, METHOD_STEAM_SIGN_OUT, METHOD_STORES_AUTH,
    METHOD_STORES_CHECK, METHOD_STORES_MATRIX, METHOD_TELEMETRY_DELETE, METHOD_TELEMETRY_SENT,
    METHOD_WEB_PREFERENCES_GET, METHOD_WEB_PREFERENCES_SET, NativeAuthProviderStatus,
    NativeAuthProvidersReply, PickFolderReply, ReplacedApp, ReplacedRemoveReply, ReplacedScanReply,
    ReportPreviewReply, ReportSendReply, RiotBaseline, RiotBaselineReply, RiotExportImport,
    RiotHoursRefresh, ShadersAddonStatus, ShadersEventPayload, SteamAccountReply, SteamSignInStep,
    StoreAccounts, StoreGiftsReply, StoreLocalCheck, StoreStatus, StoresAuthReply,
    TelemetrySentReply, WebClientOptions,
};
use rune::{OtpKind, OtpState, TextEdit};
use serde_json::{Map, Value, json};
use std::collections::{HashMap, HashSet};

pub const OBS_OVERLAY_URL: &str = "http://127.0.0.1:9364/achievements";
pub const RIOT_PRIVACY_URL: &str = "https://privacy.riotgames.com";
pub const PRIVACY_URL: &str = "https://getexo.app/privacy";
pub const CHANGELOG_URL: &str = "https://getexo.app/changelog";
pub const SUPPORT_URL: &str = "https://www.buymeacoffee.com/UhhErix";
pub const RECOVERY_CODE_LENGTH: usize = 24;
pub const MAX_REPORT_MESSAGE: usize = 4000;
pub const MAX_REPORT_CONTACT: usize = 200;
pub const MAX_URL_REPORT_MESSAGE: usize = 1500;
pub const INSTALLER_WAIT_SECS: f64 = 30.0;
pub const ACCOUNT_PROBE_INTERVAL_SECS: f64 = 300.0;
pub const CLIENTS_POLL_INTERVAL_SECS: f64 = 3.0;
pub const JUNK_WORTH_BYTES: u64 = 1_000_000_000;
pub const ACHIEVEMENT_TIERS: [&str; 5] = ["common", "rare", "epic", "legendary", "completion"];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DownloadHoursPreset {
    pub value: &'static str,
    pub label: &'static str,
    pub start: Option<i32>,
    pub end: Option<i32>,
}

pub const DOWNLOAD_HOURS: [DownloadHoursPreset; 4] = [
    DownloadHoursPreset {
        value: "any",
        label: "Any time",
        start: None,
        end: None,
    },
    DownloadHoursPreset {
        value: "0-8",
        label: "12–8 AM",
        start: Some(0),
        end: Some(8),
    },
    DownloadHoursPreset {
        value: "1-7",
        label: "1–7 AM",
        start: Some(1),
        end: Some(7),
    },
    DownloadHoursPreset {
        value: "9-17",
        label: "9 AM–5 PM",
        start: Some(9),
        end: Some(17),
    },
];

pub fn download_hours_label(start: Option<i32>, end: Option<i32>) -> &'static str {
    DOWNLOAD_HOURS
        .iter()
        .find(|p| p.start == start && p.end == end)
        .map_or("Set elsewhere", |p| p.label)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SettingsIndexEntry {
    pub id: &'static str,
    pub label: &'static str,
    pub section: SettingsSection,
    pub page_label: &'static str,
    pub keywords: &'static [&'static str],
}

pub const SETTINGS_INDEX: &[SettingsIndexEntry] = &[
    SettingsIndexEntry {
        id: "general-text-size",
        label: "Text size",
        section: SettingsSection::General,
        page_label: "General",
        keywords: &["small", "normal", "large", "scale", "font"],
    },
    SettingsIndexEntry {
        id: "general-reduce-motion",
        label: "Reduce motion",
        section: SettingsSection::General,
        page_label: "General",
        keywords: &["animation", "animations", "transitions", "motion"],
    },
    SettingsIndexEntry {
        id: "general-startup",
        label: "Open Exo when Windows starts",
        section: SettingsSection::General,
        page_label: "General",
        keywords: &["startup", "windows", "autostart", "boot"],
    },
    SettingsIndexEntry {
        id: "general-last-backup",
        label: "Last backup",
        section: SettingsSection::General,
        page_label: "General",
        keywords: &["backup", "back up now", "daily"],
    },
    SettingsIndexEntry {
        id: "general-backup-file",
        label: "Backup file",
        section: SettingsSection::General,
        page_label: "General",
        keywords: &["export", "import", "restore", "backup"],
    },
    SettingsIndexEntry {
        id: "library-stores",
        label: "Game stores",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &[
            "steam",
            "epic",
            "gog",
            "riot",
            "xbox",
            "ea",
            "ubisoft",
            "battle.net",
            "sign in",
            "check again",
        ],
    },
    SettingsIndexEntry {
        id: "library-steam-offline",
        label: "Offline mode",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &["steam offline", "go offline", "go online"],
    },
    SettingsIndexEntry {
        id: "library-quiet-stores",
        label: "Keep store apps quiet",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &["store clients", "minimize", "close", "background"],
    },
    SettingsIndexEntry {
        id: "library-riot-id",
        label: "Riot ID",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &["valorant", "league", "tft", "riot", "name"],
    },
    SettingsIndexEntry {
        id: "library-riot-hours",
        label: "Riot hours",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &[
            "valorant",
            "league of legends",
            "teamfight tactics",
            "playtime",
            "starting hours",
            "import",
        ],
    },
    SettingsIndexEntry {
        id: "library-get-store",
        label: "Get another store",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &[
            "install store",
            "epic",
            "gog",
            "ea",
            "ubisoft",
            "battle.net",
        ],
    },
    SettingsIndexEntry {
        id: "library-tools",
        label: "Show tools and soundtracks",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &["tools", "soundtracks", "sdks", "servers"],
    },
    SettingsIndexEntry {
        id: "library-shaders",
        label: "Improve shader pre-compiling",
        section: SettingsSection::Account,
        page_label: "Library",
        keywords: &["shaders", "d3d12", "stutter", "cache", "precompile"],
    },
    SettingsIndexEntry {
        id: "downloads-folder",
        label: "Install folder",
        section: SettingsSection::Appearance,
        page_label: "Downloads",
        keywords: &["default", "games folder", "directory", "location", "path"],
    },
    SettingsIndexEntry {
        id: "downloads-hours",
        label: "Download hours",
        section: SettingsSection::Appearance,
        page_label: "Downloads",
        keywords: &["schedule", "window", "overnight", "any time"],
    },
    SettingsIndexEntry {
        id: "downloads-auto-update",
        label: "Update games automatically",
        section: SettingsSection::Appearance,
        page_label: "Downloads",
        keywords: &["automatic updates", "auto update"],
    },
    SettingsIndexEntry {
        id: "downloads-upscalers",
        label: "Update DLSS, FSR and XeSS in anti-cheat games",
        section: SettingsSection::Appearance,
        page_label: "Downloads",
        keywords: &["dlss", "fsr", "xess", "upscalers", "anti-cheat"],
    },
    SettingsIndexEntry {
        id: "ingame-achievements",
        label: "Show achievement pop-ups",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["achievements", "banners", "unlock", "trophy", "popups"],
    },
    SettingsIndexEntry {
        id: "ingame-sound",
        label: "Pop-up sound",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["volume", "achievement sound", "audio"],
    },
    SettingsIndexEntry {
        id: "ingame-test",
        label: "Test pop-up",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["preview", "banner", "achievement"],
    },
    SettingsIndexEntry {
        id: "ingame-obs",
        label: "OBS overlay URL",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["obs", "browser source", "stream"],
    },
    SettingsIndexEntry {
        id: "ingame-fps",
        label: "Show FPS counter",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "frame rate", "frames per second", "overlay"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-position",
        label: "Position",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "top left", "top middle", "top right", "corner"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-low",
        label: "1% low",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "frametime", "metrics"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-frametime",
        label: "Frame time",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "ms", "metrics"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-gpu",
        label: "GPU use",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "graphics", "utilization"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-gputemp",
        label: "GPU temperature",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "temp", "thermals"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-cpu",
        label: "CPU use",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "processor", "utilization"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-vram",
        label: "VRAM",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "video memory"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-ram",
        label: "RAM",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "memory"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-battery",
        label: "Lowest battery",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "mouse", "headset", "wireless"],
    },
    SettingsIndexEntry {
        id: "ingame-fps-clock",
        label: "Time of day",
        section: SettingsSection::Graphics,
        page_label: "In game",
        keywords: &["fps", "clock", "time"],
    },
    SettingsIndexEntry {
        id: "music-service",
        label: "Music service",
        section: SettingsSection::Updates,
        page_label: "Music",
        keywords: &["spotify", "apple music", "youtube music", "player"],
    },
    SettingsIndexEntry {
        id: "music-yt-notifications",
        label: "YouTube Music notifications",
        section: SettingsSection::Updates,
        page_label: "Music",
        keywords: &["youtube music", "permissions", "alerts"],
    },
    SettingsIndexEntry {
        id: "about-updates",
        label: "Exo Launcher",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &[
            "version",
            "check for updates",
            "install update",
            "changelog",
            "what's new",
            "report a problem",
            "licenses",
            "support exo",
        ],
    },
    SettingsIndexEntry {
        id: "about-caches",
        label: "Clear caches",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["repair cache", "sizes", "upscalers", "storage"],
    },
    SettingsIndexEntry {
        id: "about-account-sync",
        label: "Sync",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["exo account", "cloud sync", "sign in", "sign out"],
    },
    SettingsIndexEntry {
        id: "about-account-password",
        label: "Change password",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["security", "password", "exo account"],
    },
    SettingsIndexEntry {
        id: "about-account-recovery",
        label: "Replace recovery code",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["recovery code", "backup code", "security"],
    },
    SettingsIndexEntry {
        id: "about-account-sessions",
        label: "Signed-in PCs",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["sessions", "devices", "exo account"],
    },
    SettingsIndexEntry {
        id: "about-account-export",
        label: "Export my data",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["your data", "download data", "exo account"],
    },
    SettingsIndexEntry {
        id: "about-account-delete",
        label: "Delete account",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["remove account", "your data", "exo account"],
    },
    SettingsIndexEntry {
        id: "about-privacy-sent",
        label: "What's sent",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["telemetry", "crash reports", "app data", "privacy"],
    },
    SettingsIndexEntry {
        id: "about-privacy-delete",
        label: "Delete my data",
        section: SettingsSection::About,
        page_label: "About",
        keywords: &["telemetry", "privacy", "install id"],
    },
];

pub fn search_settings(query: &str) -> Vec<&'static SettingsIndexEntry> {
    let tokens: Vec<String> = query
        .split_whitespace()
        .map(str::to_lowercase)
        .filter(|t| !t.is_empty())
        .collect();
    if tokens.is_empty() {
        return Vec::new();
    }
    let joined = tokens.join(" ");
    let mut matches: Vec<&'static SettingsIndexEntry> = SETTINGS_INDEX
        .iter()
        .filter(|entry| {
            let mut hay = format!("{} {}", entry.label, entry.page_label).to_lowercase();
            for kw in entry.keywords {
                hay.push(' ');
                hay.push_str(&kw.to_lowercase());
            }
            tokens.iter().all(|tok| hay.contains(tok))
        })
        .collect();
    matches.sort_by_key(|entry| {
        let lower = entry.label.to_lowercase();
        if lower.starts_with(&joined) {
            0u8
        } else if lower.contains(&joined) {
            1u8
        } else {
            2u8
        }
    });
    matches
}

pub fn today_utc_ymd() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    unix_days_to_ymd(secs.div_euclid(86_400))
}

fn unix_days_to_ymd(z: i64) -> String {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    format!("{y:04}-{m:02}-{d:02}")
}

pub fn backup_day_label(day: &str, today_ymd: &str) -> String {
    let parts: Vec<&str> = day.split('-').collect();
    if parts.len() != 3 {
        return day.to_string();
    }
    let (Ok(year), Ok(month), Ok(date)) = (
        parts[0].parse::<i32>(),
        parts[1].parse::<u32>(),
        parts[2].parse::<u32>(),
    ) else {
        return day.to_string();
    };
    if !(1..=12).contains(&month) || !(1..=31).contains(&date) {
        return day.to_string();
    }
    if day == today_ymd {
        return "Today".into();
    }
    const MONTHS: [&str; 12] = [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    ];
    format!("{} {}, {}", MONTHS[(month - 1) as usize], date, year)
}

pub fn format_unix_timestamp(seconds: i64) -> String {
    if seconds <= 0 {
        return String::new();
    }
    let ymd = unix_days_to_ymd(seconds.div_euclid(86_400));
    let tod = seconds.rem_euclid(86_400);
    let h = tod / 3600;
    let m = (tod % 3600) / 60;
    let day = backup_day_label(&ymd, "");
    format!("{day}, {h:02}:{m:02} UTC")
}

pub fn normalize_recovery_code(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len());
    for ch in raw.chars() {
        if ch.is_whitespace() || ch == '-' {
            continue;
        }
        let upper = ch.to_ascii_uppercase();
        match upper {
            'O' => out.push('0'),
            'I' | 'L' => out.push('1'),
            other => out.push(other),
        }
    }
    out
}

pub fn recovery_code_confirmed(typed: &str, code: &str) -> bool {
    let entered = normalize_recovery_code(typed);
    entered.len() == RECOVERY_CODE_LENGTH && entered == normalize_recovery_code(code)
}

pub fn held_sentences(held: &[ExoAccountHeldItem]) -> Vec<String> {
    let mut order: Vec<(String, String)> = Vec::new();
    let mut counts: HashMap<(String, String), usize> = HashMap::new();
    for item in held {
        let kind = item.key.split('/').next().unwrap_or(&item.key).to_string();
        let pair = (kind, item.reason.clone());
        let entry = counts.entry(pair.clone()).or_insert(0);
        if *entry == 0 {
            order.push(pair);
        }
        *entry += 1;
    }
    order
        .into_iter()
        .map(|(kind, reason)| {
            let count = counts[&(kind.clone(), reason.clone())];
            let (one, many) = match kind.as_str() {
                "cover-image" => ("cover image", "cover images"),
                "cover" => ("cover choice", "cover choices"),
                "settings" => ("setting", "settings"),
                "playtime" => ("playtime entry", "playtime entries"),
                "playtime-seed" => ("starting playtime", "starting playtimes"),
                "riot-ledger" => ("Riot hours entry", "Riot hours entries"),
                "collection" => ("collection", "collections"),
                _ => ("item", "items"),
            };
            let noun = if count == 1 { one } else { many };
            if reason == "too-large" {
                if count == 1 {
                    format!("1 {noun} is too large to sync. It stays on this PC.")
                } else {
                    format!("{count} {noun} are too large to sync. They stay on this PC.")
                }
            } else if count == 1 {
                format!("1 {noun} was not accepted by the service. It stays on this PC.")
            } else {
                format!("{count} {noun} were not accepted by the service. They stay on this PC.")
            }
        })
        .collect()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AccountScreen {
    #[default]
    Choose,
    Create,
    SignIn,
    Reset,
    NewCode,
    Code,
    Offline,
    SignedIn,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct AccountFlowState {
    pub screen: AccountScreen,
    pub busy: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccountAnswer {
    Code,
    NoCode,
    SignedIn,
    Offline,
    Refused,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AccountFlowEvent {
    Open(AccountScreen),
    Submit,
    Answer(AccountAnswer),
    Confirm,
    SignedOut,
}

pub fn next_account_flow(state: AccountFlowState, event: AccountFlowEvent) -> AccountFlowState {
    if state.screen == AccountScreen::Code {
        return match event {
            AccountFlowEvent::Confirm => AccountFlowState {
                screen: AccountScreen::SignedIn,
                busy: false,
            },
            _ => state,
        };
    }
    if state.screen == AccountScreen::SignedIn {
        return state;
    }
    if state.busy {
        return match event {
            AccountFlowEvent::Answer(ans) => AccountFlowState {
                screen: match ans {
                    AccountAnswer::Code => AccountScreen::Code,
                    AccountAnswer::NoCode => AccountScreen::NewCode,
                    AccountAnswer::SignedIn => AccountScreen::SignedIn,
                    AccountAnswer::Offline => AccountScreen::Offline,
                    AccountAnswer::Refused => state.screen,
                },
                busy: false,
            },
            _ => state,
        };
    }
    match event {
        AccountFlowEvent::Submit => match state.screen {
            AccountScreen::Create
            | AccountScreen::SignIn
            | AccountScreen::Reset
            | AccountScreen::NewCode => AccountFlowState {
                screen: state.screen,
                busy: true,
            },
            _ => state,
        },
        AccountFlowEvent::SignedOut => match state.screen {
            AccountScreen::NewCode | AccountScreen::Offline => AccountFlowState {
                screen: AccountScreen::Choose,
                busy: false,
            },
            _ => state,
        },
        AccountFlowEvent::Open(to) => {
            if state.screen == AccountScreen::NewCode {
                if to == AccountScreen::Reset {
                    return AccountFlowState {
                        screen: AccountScreen::Reset,
                        busy: false,
                    };
                }
                return state;
            }
            match state.screen {
                AccountScreen::Choose
                | AccountScreen::Create
                | AccountScreen::SignIn
                | AccountScreen::Reset
                | AccountScreen::Offline => AccountFlowState {
                    screen: to,
                    busy: false,
                },
                _ => state,
            }
        }
        AccountFlowEvent::Answer(_) | AccountFlowEvent::Confirm => state,
    }
}

pub fn first_account_screen(code_pending: bool) -> AccountScreen {
    if code_pending {
        AccountScreen::NewCode
    } else {
        AccountScreen::Choose
    }
}

pub fn account_gate_due(
    overview: Option<&ExoAccountOverview>,
    later_this_run: bool,
    code_on_other_screen: bool,
) -> bool {
    let Some(ov) = overview else {
        return false;
    };
    if later_this_run {
        return false;
    }
    if !ov.signed_in {
        return true;
    }
    ov.code_pending && !code_on_other_screen
}

pub fn account_sheet_open(
    was_open: bool,
    overview: Option<&ExoAccountOverview>,
    later_this_run: bool,
    codes_on_screen: usize,
    requests: usize,
) -> bool {
    let busy = codes_on_screen > 0 || requests > 0;
    if was_open && busy {
        return true;
    }
    account_gate_due(overview, later_this_run, busy)
}

pub fn later_ends(probe: &ExoAccountProbe) -> bool {
    probe.reachable && !probe.game_running
}

pub fn clients_heading(status: &ClientsStatus) -> &'static str {
    if status.problem.is_some() && !status.needs_fix {
        return "Exo couldn’t check your game clients";
    }
    let epic = status.epic.layout == "old";
    let steam = status.steam.channel == "stable";
    match (epic, steam) {
        (true, true) => "Exo needs the new Epic launcher and Steam Beta",
        (true, false) => "Exo needs the new Epic launcher",
        (false, true) => "Exo needs Steam Beta",
        (false, false) => "Your game clients are ready",
    }
}

pub fn clients_offered(status: Option<&ClientsStatus>, check_failed: bool) -> bool {
    if check_failed {
        return true;
    }
    status.is_some_and(|s| s.epic.layout != "none" || s.steam.channel != "none")
}

pub fn clients_gate_shows(status: Option<&ClientsStatus>, deferred: bool) -> bool {
    !deferred && status.is_some_and(|s| s.needs_fix)
}

pub fn clients_fix_copy(status: &ClientsStatus) -> String {
    let mut parts = Vec::new();
    if status.steam.channel == "stable" {
        parts.push("closes Steam and restarts it on Steam Beta");
    }
    if status.epic.layout == "old" {
        parts.push("downloads Epic's new launcher and opens its installer");
    }
    if parts.is_empty() {
        "Nothing changes while a game plays.".into()
    } else {
        format!(
            "Fix {}. Nothing changes while a game plays.",
            parts.join(", and ")
        )
    }
}

const ALL_LAUNCHER_STORES: &[(&str, &str)] = &[
    ("steam", "Steam"),
    ("epic", "Epic"),
    ("gog", "GOG"),
    ("riot", "Riot"),
    ("xbox", "Xbox"),
    ("microsoft-store", "Microsoft Store"),
    ("ea", "EA"),
    ("ubisoft", "Ubisoft"),
    ("battlenet", "Battle.net"),
    ("amazon", "Amazon"),
    ("rockstar", "Rockstar"),
    ("itch", "itch.io"),
    ("paradox", "Paradox"),
    ("wargaming", "Wargaming"),
];

fn is_first_class_store(store: &str) -> bool {
    matches!(store, "steam" | "epic" | "gog" | "riot")
}

fn is_agent_backed_store(store: &str) -> bool {
    matches!(store, "epic" | "gog" | "amazon")
}

fn is_session_backed_store(store: &str) -> bool {
    matches!(
        store,
        "riot" | "battlenet" | "ea" | "ubisoft" | "xbox" | "minecraft" | "paradox" | "rockstar"
    )
}

fn is_present_store(store: &StoreStatus) -> bool {
    if store.store == "local" {
        return false;
    }
    if store.signed_in {
        return true;
    }
    if store.client_present == Some(true) {
        return true;
    }
    store.client_present.is_none() && store.agent_present
}

fn placeholder_store(store: &str, display_name: &str) -> StoreStatus {
    StoreStatus {
        store: store.to_string(),
        display_name: display_name.to_string(),
        signed_in: false,
        client_present: Some(false),
        agent_present: false,
        detail: Some("Not installed".into()),
        ..Default::default()
    }
}

pub fn settings_store_rows(stores: &[StoreStatus]) -> Vec<StoreStatus> {
    let by_id: HashMap<&str, &StoreStatus> = stores
        .iter()
        .filter(|s| s.store != "local")
        .map(|s| (s.store.as_str(), s))
        .collect();
    let known: Vec<StoreStatus> = ALL_LAUNCHER_STORES
        .iter()
        .map(|(id, name)| {
            by_id
                .get(*id)
                .map(|s| (*s).clone())
                .unwrap_or_else(|| placeholder_store(id, name))
        })
        .collect();
    let mut out = Vec::new();
    for s in &known {
        if is_first_class_store(&s.store) && is_present_store(s) {
            out.push(s.clone());
        }
    }
    for s in &known {
        if !is_first_class_store(&s.store) && is_present_store(s) {
            out.push(s.clone());
        }
    }
    for s in stores {
        let is_excluded = matches!(s.store.as_str(), "local" | "minecraft" | "roblox");
        let is_known = ALL_LAUNCHER_STORES.iter().any(|(id, _)| *id == s.store);
        if !is_excluded && !is_known && is_present_store(s) {
            out.push(s.clone());
        }
    }
    out
}

pub fn available_store_rows(stores: &[StoreStatus]) -> Vec<StoreStatus> {
    let visible: HashSet<String> = settings_store_rows(stores)
        .into_iter()
        .map(|s| s.store)
        .collect();
    let by_id: HashMap<&str, &StoreStatus> = stores.iter().map(|s| (s.store.as_str(), s)).collect();
    ALL_LAUNCHER_STORES
        .iter()
        .filter(|(id, _)| !visible.contains(*id) && store_client_download_url(id).is_some())
        .map(|(id, name)| {
            by_id
                .get(*id)
                .map(|s| (*s).clone())
                .unwrap_or_else(|| placeholder_store(id, name))
        })
        .collect()
}

pub fn store_client_download_url(store: &str) -> Option<&'static str> {
    match store {
        "microsoft-store" => Some("ms-windows-store://home"),
        "steam" => Some("https://store.steampowered.com/about/"),
        "epic" => Some("https://store.epicgames.com/download"),
        "gog" => Some("https://www.gog.com/galaxy"),
        "riot" => Some("https://www.riotgames.com/en/download"),
        "xbox" => Some("ms-windows-store://pdp/?ProductId=9MV0B5HZVK9Z"),
        "ea" => Some("https://www.ea.com/ea-app"),
        "ubisoft" => Some("https://ubisoftconnect.com/"),
        "battlenet" => Some("https://www.battle.net/download"),
        "amazon" => Some("https://download.amazongames.com/AmazonGamesSetup.exe"),
        "rockstar" => Some("https://socialclub.rockstargames.com/rockstar-games-launcher"),
        "itch" => Some("https://itch.io/app"),
        "minecraft" => Some("https://www.minecraft.net/en-us/download"),
        "roblox" => Some("ms-windows-store://pdp/?ProductId=9PMF91N3LZ3M"),
        "paradox" => Some("https://play.paradoxplaza.com/"),
        "wargaming" => Some("https://wargaming.com/en/game-center/"),
        _ => None,
    }
}

pub fn can_connect_store(store: &StoreStatus) -> bool {
    is_first_class_store(&store.store) && !store.signed_in && is_agent_backed_store(&store.store)
}

pub fn can_open_store_client(store: &StoreStatus) -> bool {
    store.client_present == Some(true) || (store.client_present.is_none() && store.agent_present)
}

pub fn signs_in_in_own_app(store: &StoreStatus) -> bool {
    if !is_first_class_store(&store.store) || store.signed_in || is_agent_backed_store(&store.store)
    {
        return false;
    }
    if store.store == "steam" || is_session_backed_store(&store.store) {
        can_open_store_client(store)
    } else {
        false
    }
}

pub fn store_state(store: &StoreStatus) -> (&'static str, bool) {
    if store.signed_in {
        return ("Connected", true);
    }
    if can_connect_store(store) || signs_in_in_own_app(store) {
        return ("Not signed in", false);
    }
    if store.client_present == Some(true) || store.agent_present {
        return ("Found", false);
    }
    ("Not installed", false)
}

pub fn store_layout_note(store: &StoreStatus) -> Option<&'static str> {
    if store.check_code.as_deref() == Some("layout_unrecognized") {
        Some(
            "Installed, but Exo found no games. The store may have moved its library; Exo logs the details.",
        )
    } else {
        None
    }
}

pub fn account_name(store: &str, accounts: Option<&StoreAccounts>) -> Option<String> {
    let accounts = accounts?;
    match store {
        "steam" => accounts.steam.as_ref().and_then(|s| s.current.clone()),
        "riot" => accounts.riot.as_ref().and_then(|r| r.current.clone()),
        "epic" => accounts.epic.signed_in.then(|| {
            accounts
                .epic
                .name
                .clone()
                .unwrap_or_else(|| "Signed in".into())
        }),
        "gog" => accounts.gog.signed_in.then(|| "Signed in".into()),
        "amazon" => accounts.amazon.signed_in.then(|| {
            accounts
                .amazon
                .name
                .clone()
                .unwrap_or_else(|| "Signed in".into())
        }),
        _ => None,
    }
}

pub fn is_valid_riot_id(raw: &str) -> bool {
    let trimmed = raw.trim();
    let Some((name, tag)) = trimmed.split_once('#') else {
        return false;
    };
    !name.is_empty() && tag.len() >= 2 && !tag.contains('#')
}

pub fn format_baseline_hours(minutes: Option<f64>) -> String {
    let Some(m) = minutes else {
        return String::new();
    };
    if m <= 0.0 || !m.is_finite() {
        return String::new();
    }
    let hours = m / 60.0;
    if (hours - hours.round()).abs() < 1e-6 {
        format!("{}", hours.round() as i64)
    } else {
        format!("{hours:.1}")
    }
}

pub fn parse_hours_input(raw: &str) -> Result<Option<f64>, ()> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return Ok(None);
    }
    let parsed: f64 = trimmed.parse().map_err(|_| ())?;
    if !parsed.is_finite() || parsed <= 0.0 {
        return Err(());
    }
    Ok(Some(parsed))
}

pub fn baseline_summary_parts(baseline: Option<&RiotBaseline>) -> Vec<String> {
    let Some(b) = baseline else {
        return Vec::new();
    };
    let entries = [
        ("VALORANT", b.valorant_minutes),
        ("League", b.league_minutes),
        ("TFT", b.tft_minutes),
        ("LoR", b.lor_minutes),
        ("2XKO", b.two_xko_minutes),
    ];
    entries
        .into_iter()
        .filter_map(|(name, mins)| {
            let h = format_baseline_hours(mins);
            (!h.is_empty()).then(|| format!("{name} {h} h"))
        })
        .collect()
}

pub fn format_bytes_short(bytes: u64) -> String {
    if bytes >= 1_000_000_000 {
        format!("{:.1} GB", bytes as f64 / 1e9)
    } else if bytes >= 1_000_000 {
        format!("{} MB", (bytes as f64 / 1e6).round() as u64)
    } else if bytes > 0 {
        format!("{} KB", ((bytes as f64 / 1e3).round() as u64).max(1))
    } else {
        String::new()
    }
}

pub fn leftover_summary_words(left: &LeftoverSummary) -> String {
    let mut parts: Vec<String> = Vec::new();
    if left.folders > 0 {
        parts.push(format!(
            "{} {}",
            left.folders,
            if left.folders == 1 {
                "folder"
            } else {
                "folders"
            }
        ));
    }
    if left.files > 0 {
        parts.push(format!(
            "{} {}",
            left.files,
            if left.files == 1 { "file" } else { "files" }
        ));
    }
    let amount = format_bytes_short(left.bytes);
    if !amount.is_empty()
        && let Some(last) = parts.last_mut()
    {
        last.push_str(&format!(" ({amount})"));
    }
    if left.registry > 0 {
        parts.push(format!(
            "{} {}",
            left.registry,
            if left.registry == 1 {
                "registry entry"
            } else {
                "registry entries"
            }
        ));
    }
    if left.shortcuts > 0 {
        parts.push(format!(
            "{} {}",
            left.shortcuts,
            if left.shortcuts == 1 {
                "shortcut"
            } else {
                "shortcuts"
            }
        ));
    }
    if parts.len() < 2 {
        parts.join("")
    } else {
        let last = parts.pop().unwrap_or_default();
        format!("{} and {last}", parts.join(", "))
    }
}

pub fn junk_total_bytes(items: &[JunkItem]) -> u64 {
    items.iter().map(|i| i.bytes).sum()
}

pub fn junk_worth(items: &[JunkItem]) -> bool {
    junk_total_bytes(items) >= JUNK_WORTH_BYTES
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReportPayload {
    pub message: String,
    pub excerpt: Option<String>,
    pub contact: Option<String>,
}

pub fn build_report_payload(
    message: &str,
    attach_logs: bool,
    contact: &str,
    excerpt: Option<&str>,
) -> Option<ReportPayload> {
    let normalized = message.replace("\r\n", "\n").replace('\r', "\n");
    let trimmed = normalized.trim();
    if trimmed.is_empty() {
        return None;
    }
    let msg: String = trimmed.chars().take(MAX_REPORT_MESSAGE).collect();
    let contact_clean: String = contact
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .chars()
        .take(MAX_REPORT_CONTACT)
        .collect();
    let excerpt_opt = if attach_logs {
        excerpt.filter(|e| !e.is_empty()).map(str::to_string)
    } else {
        None
    };
    Some(ReportPayload {
        message: msg,
        excerpt: excerpt_opt,
        contact: (!contact_clean.is_empty()).then_some(contact_clean),
    })
}

pub fn report_sent_words(reply: &ReportSendReply) -> String {
    if let Some(issue) = reply.issue
        && issue > 0
    {
        return format!("Sent, #{issue}");
    }
    if let Some(ref reference) = reply.r#ref
        && !reference.is_empty()
    {
        return format!("Sent, ref {reference}");
    }
    "Sent".into()
}

pub fn scrub_for_url(input: &str) -> String {
    let mut out = String::with_capacity(input.len());
    for line in input.split('\n') {
        if !out.is_empty() {
            out.push('\n');
        }
        let mut first_word = true;
        for token in line.split(' ') {
            if !first_word {
                out.push(' ');
            }
            first_word = false;
            out.push_str(&scrub_token(token));
        }
    }
    out
}

fn scrub_token(tok: &str) -> String {
    if tok.is_empty() {
        return String::new();
    }
    let lower = tok.to_ascii_lowercase();
    if lower.contains("://")
        && lower
            .split("://")
            .next()
            .is_some_and(|s| !s.is_empty() && s.len() <= 16)
    {
        return "<url>".into();
    }
    if tok.starts_with("\\\\") && tok.len() > 2 {
        return "<path>".into();
    }
    let bytes = tok.as_bytes();
    if bytes.len() >= 3
        && bytes[0].is_ascii_alphabetic()
        && bytes[1] == b':'
        && (bytes[2] == b'\\' || bytes[2] == b'/')
    {
        return "<path>".into();
    }
    if tok.starts_with("/home/") || tok.starts_with("/Users/") {
        return "<path>".into();
    }
    if let Some((local, domain)) = tok.split_once('@')
        && !local.is_empty()
        && domain.contains('.')
        && domain.len() >= 4
    {
        return "<email>".into();
    }
    if tok.len() >= 9 && tok.bytes().all(|b| b.is_ascii_digit()) {
        return "<id>".into();
    }
    tok.to_string()
}

fn percent_encode_query(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(b as char);
            }
            b' ' => out.push('+'),
            other => out.push_str(&format!("%{other:02X}")),
        }
    }
    out
}

pub fn bug_report_url(version: Option<&str>, message: &str) -> String {
    let mut params = vec![("template", "bug_report.yml".to_string())];
    let normalized = message.replace("\r\n", "\n").replace('\r', "\n");
    let scrubbed: String = scrub_for_url(normalized.trim())
        .chars()
        .take(MAX_URL_REPORT_MESSAGE)
        .collect();
    if !scrubbed.is_empty() {
        let first_line = scrubbed.split('\n').next().unwrap_or("");
        let title = if first_line.chars().count() > 80 {
            let prefix: String = first_line.chars().take(79).collect();
            format!("{prefix}…")
        } else {
            first_line.to_string()
        };
        params.push(("title", title));
        params.push(("what-happened", scrubbed));
    }
    if let Some(v) = version.map(str::trim).filter(|v| !v.is_empty()) {
        params.push(("version", v.to_string()));
    }
    let query = params
        .into_iter()
        .map(|(k, v)| format!("{k}={}", percent_encode_query(&v)))
        .collect::<Vec<_>>()
        .join("&");
    format!("https://github.com/UhhErix/Exo-Launcher/issues/new?{query}")
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum StoreActionKind {
    Connect,
    Open,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoreNote {
    pub text: String,
    pub error: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BackupOp {
    Now,
    Export,
    Import,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BackupOutcome {
    pub kind: BackupOp,
    pub ok: bool,
    pub text: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SteamSignInMode {
    #[default]
    Qr,
    Password,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AccountSecurityForm {
    #[default]
    None,
    Password,
    Recovery,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AccountSignedOutMode {
    #[default]
    SignIn,
    Create,
    Reset,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub enum ReportPhase {
    #[default]
    Editing,
    Sending,
    Sent(String),
}

pub struct SettingsUiState {
    pub highlight_row: Option<(&'static str, f64)>,

    pub picking_folder: bool,
    pub folder_error: Option<String>,
    pub download_hours_open: bool,

    pub achievement_volume_error: Option<String>,
    pub achievement_preview_error: Option<String>,
    pub achievement_preview_index: usize,
    pub achievement_slider_dragging: bool,
    pub obs_copied_until: Option<f64>,

    pub backup_busy: Option<BackupOp>,
    pub backup_outcome: Option<BackupOutcome>,
    pub backup_held: Option<String>,
    pub backup_note: Option<String>,
    pub backup_confirm_import: bool,

    pub stores: Vec<StoreStatus>,
    pub store_accounts: Option<StoreAccounts>,
    pub store_check_busy: bool,
    pub store_check_copy: Option<String>,
    pub store_busy: HashMap<String, StoreActionKind>,
    pub store_notes: HashMap<String, StoreNote>,
    pub store_get_error: Option<String>,
    pub account_menu_store: Option<String>,
    pub account_menu_confirm_id: Option<String>,
    pub account_busy_store: Option<String>,
    pub account_line: HashMap<String, String>,
    pub account_error: HashMap<String, String>,
    pub epic_removing_key: Option<String>,
    pub epic_confirm_remove: Option<String>,
    pub epic_remove_error: Option<(String, String)>,

    pub steam_account: Option<SteamAccountReply>,
    pub steam_panel_open: bool,
    pub steam_mode: SteamSignInMode,
    pub steam_step: Option<SteamSignInStep>,
    pub steam_busy: bool,
    pub steam_poll_at: Option<f64>,
    pub steam_user: TextEdit,
    pub steam_pass: TextEdit,
    pub steam_guard: OtpState,
    pub steam_offline_pending: Option<bool>,
    pub steam_offline_line: Option<String>,
    pub steam_offline_error: Option<String>,
    pub gifts_steam_signed_in: Option<bool>,

    pub riot_editing: bool,
    pub riot_draft: TextEdit,
    pub riot_busy: bool,
    pub riot_note: Option<StoreNote>,
    pub riot_hours_editing: bool,
    pub riot_val_input: TextEdit,
    pub riot_lol_input: TextEdit,
    pub riot_tft_input: TextEdit,
    pub riot_hours_busy: bool,
    pub riot_hours_note: Option<StoreNote>,

    pub shaders_present: bool,
    pub shaders_auto: bool,
    pub shaders_error: Option<String>,

    pub caches_busy: bool,
    pub caches_done: bool,
    pub caches_error: Option<String>,
    pub update_note: Option<StoreNote>,

    pub account_overview: Option<ExoAccountOverview>,
    pub account_passkeys: Option<ExoAccountPasskeyStatus>,
    pub account_passkey_busy: bool,
    pub account_passkey_note: Option<String>,
    pub account_passkey_error: Option<String>,
    pub account_passkey_reauth_needed: bool,
    pub account_passkey_reauth_pw: TextEdit,
    pub native_auth_providers: Option<Vec<NativeAuthProviderStatus>>,
    pub account_recovery_code: Option<String>,
    pub account_recovery_typed: TextEdit,
    pub account_recovery_note: Option<String>,
    pub account_recovery_finishing: bool,
    pub account_recovery_failed: bool,
    pub account_sync_error: Option<String>,
    pub account_sign_out_confirm: bool,
    pub account_security_form: AccountSecurityForm,
    pub account_current_pass: TextEdit,
    pub account_new_pass: TextEdit,
    pub account_code_pass: TextEdit,
    pub account_security_error: Option<String>,
    pub account_security_note: Option<String>,
    pub account_security_busy: bool,
    pub account_sessions_open: bool,
    pub account_sessions: Option<Vec<ExoAccountSession>>,
    pub account_sessions_note: Option<String>,
    pub account_export_note: Option<String>,
    pub account_delete_open: bool,
    pub account_delete_pass: TextEdit,
    pub account_delete_error: Option<String>,
    pub account_delete_busy: bool,
    pub account_signed_out_mode: AccountSignedOutMode,
    pub account_email: TextEdit,
    pub account_password: TextEdit,
    pub account_reset_code: TextEdit,
    pub account_show_pass: bool,
    pub account_form_error: Option<String>,
    pub account_form_offline: bool,
    pub account_form_busy: bool,

    pub privacy_viewing: bool,
    pub privacy_payloads: Option<Vec<String>>,
    pub privacy_failed: bool,
    pub privacy_confirming: bool,
    pub privacy_message: Option<String>,

    pub report_open: bool,
    pub report_message: TextEdit,
    pub report_attach_logs: bool,
    pub report_contact: TextEdit,
    pub report_excerpt: Option<String>,
    pub report_showing_excerpt: bool,
    pub report_phase: ReportPhase,
    pub report_error: Option<String>,

    pub account_sheet_was_open: bool,
    pub account_later_this_run: bool,
    pub account_sheet_requests: usize,
    pub account_probe_next_at: Option<f64>,
    pub flow: AccountFlowState,
    pub flow_email: TextEdit,
    pub flow_password: TextEdit,
    pub flow_reset_code: TextEdit,
    pub flow_show_pass: bool,
    pub flow_email_error: Option<String>,
    pub flow_password_error: Option<String>,
    pub flow_form_error: Option<String>,
    pub flow_taken_sign_in: bool,
    pub flow_locked_until: Option<f64>,
    pub flow_code: Option<String>,
    pub flow_offline_from: AccountScreen,
    pub flow_reset_from: AccountScreen,

    pub web_prefs: Option<WebClientOptions>,
    pub web_prefs_saving: bool,
    pub web_prefs_error: Option<String>,

    pub replaced_apps: Vec<ReplacedApp>,
    pub replaced_listed: HashSet<String>,
    pub replaced_busy: Option<String>,
    pub replaced_confirm: Option<String>,
    pub replaced_errors: HashMap<String, String>,
    pub junk_items: Vec<JunkItem>,
    pub junk_scanning: bool,
    pub junk_confirm: bool,
    pub junk_busy: bool,
    pub junk_done: Option<String>,
    pub junk_error: Option<String>,

    pub restore_busy: bool,
    pub restore_held: bool,
    pub restore_message: Option<StoreNote>,

    pub clients_failed: bool,
    pub clients_failures: u32,
    pub clients_checking: bool,
    pub clients_busy: bool,
    pub clients_deferred: bool,
    pub clients_note: Option<StoreNote>,
    pub clients_installer_started_at: Option<f64>,
    pub clients_installer_long: bool,
    pub clients_next_poll_at: Option<f64>,

    pub keep_status: Option<KeepStatus>,
}

impl Default for SettingsUiState {
    fn default() -> Self {
        Self {
            highlight_row: None,
            picking_folder: false,
            folder_error: None,
            download_hours_open: false,
            achievement_volume_error: None,
            achievement_preview_error: None,
            achievement_preview_index: 0,
            achievement_slider_dragging: false,
            obs_copied_until: None,
            backup_busy: None,
            backup_outcome: None,
            backup_held: None,
            backup_note: None,
            backup_confirm_import: false,
            stores: Vec::new(),
            store_accounts: None,
            store_check_busy: false,
            store_check_copy: None,
            store_busy: HashMap::new(),
            store_notes: HashMap::new(),
            store_get_error: None,
            account_menu_store: None,
            account_menu_confirm_id: None,
            account_busy_store: None,
            account_line: HashMap::new(),
            account_error: HashMap::new(),
            epic_removing_key: None,
            epic_confirm_remove: None,
            epic_remove_error: None,
            steam_account: None,
            steam_panel_open: false,
            steam_mode: SteamSignInMode::Qr,
            steam_step: None,
            steam_busy: false,
            steam_poll_at: None,
            steam_user: TextEdit::new(""),
            steam_pass: TextEdit::new("").secure(),
            steam_guard: OtpState::new(5).kind(OtpKind::AlphanumericUpper),
            steam_offline_pending: None,
            steam_offline_line: None,
            steam_offline_error: None,
            gifts_steam_signed_in: None,
            riot_editing: false,
            riot_draft: TextEdit::new(""),
            riot_busy: false,
            riot_note: None,
            riot_hours_editing: false,
            riot_val_input: TextEdit::new(""),
            riot_lol_input: TextEdit::new(""),
            riot_tft_input: TextEdit::new(""),
            riot_hours_busy: false,
            riot_hours_note: None,
            shaders_present: true,
            shaders_auto: false,
            shaders_error: None,
            caches_busy: false,
            caches_done: false,
            caches_error: None,
            update_note: None,
            account_overview: None,
            account_passkeys: None,
            account_passkey_busy: false,
            account_passkey_note: None,
            account_passkey_error: None,
            account_passkey_reauth_needed: false,
            account_passkey_reauth_pw: TextEdit::new(""),
            native_auth_providers: None,
            account_recovery_code: None,
            account_recovery_typed: TextEdit::new(""),
            account_recovery_note: None,
            account_recovery_finishing: false,
            account_recovery_failed: false,
            account_sync_error: None,
            account_sign_out_confirm: false,
            account_security_form: AccountSecurityForm::None,
            account_current_pass: TextEdit::new("").secure(),
            account_new_pass: TextEdit::new("").secure(),
            account_code_pass: TextEdit::new("").secure(),
            account_security_error: None,
            account_security_note: None,
            account_security_busy: false,
            account_sessions_open: false,
            account_sessions: None,
            account_sessions_note: None,
            account_export_note: None,
            account_delete_open: false,
            account_delete_pass: TextEdit::new("").secure(),
            account_delete_error: None,
            account_delete_busy: false,
            account_signed_out_mode: AccountSignedOutMode::SignIn,
            account_email: TextEdit::new(""),
            account_password: TextEdit::new("").secure(),
            account_reset_code: TextEdit::new(""),
            account_show_pass: false,
            account_form_error: None,
            account_form_offline: false,
            account_form_busy: false,
            privacy_viewing: false,
            privacy_payloads: None,
            privacy_failed: false,
            privacy_confirming: false,
            privacy_message: None,
            report_open: false,
            report_message: TextEdit::new(""),
            report_attach_logs: true,
            report_contact: TextEdit::new(""),
            report_excerpt: None,
            report_showing_excerpt: false,
            report_phase: ReportPhase::Editing,
            report_error: None,
            account_sheet_was_open: false,
            account_later_this_run: false,
            account_sheet_requests: 0,
            account_probe_next_at: None,
            flow: AccountFlowState::default(),
            flow_email: TextEdit::new(""),
            flow_password: TextEdit::new("").secure(),
            flow_reset_code: TextEdit::new(""),
            flow_show_pass: false,
            flow_email_error: None,
            flow_password_error: None,
            flow_form_error: None,
            flow_taken_sign_in: false,
            flow_locked_until: None,
            flow_code: None,
            flow_offline_from: AccountScreen::Create,
            flow_reset_from: AccountScreen::SignIn,
            web_prefs: None,
            web_prefs_saving: false,
            web_prefs_error: None,
            replaced_apps: Vec::new(),
            replaced_listed: HashSet::new(),
            replaced_busy: None,
            replaced_confirm: None,
            replaced_errors: HashMap::new(),
            junk_items: Vec::new(),
            junk_scanning: false,
            junk_confirm: false,
            junk_busy: false,
            junk_done: None,
            junk_error: None,
            restore_busy: false,
            restore_held: false,
            restore_message: None,
            clients_failed: false,
            clients_failures: 0,
            clients_checking: false,
            clients_busy: false,
            clients_deferred: false,
            clients_note: None,
            clients_installer_started_at: None,
            clients_installer_long: false,
            clients_next_poll_at: None,
            keep_status: None,
        }
    }
}

pub fn register_wp4_events(reg: &mut EventRegistry) {
    reg.on(HostEventKind::AccountsProgress, on_accounts_progress);
    reg.on(HostEventKind::BackupStopped, on_backup_stopped);
    reg.on(HostEventKind::ComponentsFailed, on_components_failed);
    reg.on(HostEventKind::GiftsChanged, on_gifts_changed);
    reg.on(HostEventKind::ShadersEvent, on_shaders_event);
    reg.on(HostEventKind::ExoAccountChanged, on_exo_account_changed);
}

fn on_accounts_progress(s: &mut AppState, ev: HostEvent) -> bool {
    let HostEvent::AccountsProgress(AccountsProgressEvent { store, line }) = ev else {
        return false;
    };
    if store == "steam" && s.wp4.steam_offline_pending.is_some() {
        s.wp4.steam_offline_line = Some(line);
        return true;
    }
    if s.wp4.account_busy_store.as_deref() == Some(store.as_str()) {
        s.wp4.account_line.insert(store, line);
        return true;
    }
    false
}

fn on_backup_stopped(s: &mut AppState, ev: HostEvent) -> bool {
    let HostEvent::BackupStopped(BackupStoppedEvent { sentence, .. }) = ev else {
        return false;
    };
    if !sentence.is_empty() {
        s.wp4.backup_note = Some(sentence);
        return true;
    }
    false
}

fn on_components_failed(s: &mut AppState, ev: HostEvent) -> bool {
    let HostEvent::ComponentsFailed(ComponentsFailedEvent { feature }) = ev else {
        return false;
    };
    let msg = if feature.is_empty() {
        "A required Exo component failed to start. Try checking your game clients.".to_string()
    } else {
        format!("{feature} could not start. Check your game clients and try again.")
    };
    s.push_notice(msg, crate::state::NoticeTone::Bad);
    true
}

fn on_gifts_changed(s: &mut AppState, ev: HostEvent) -> bool {
    let HostEvent::GiftsChanged(StoreGiftsReply {
        steam_signed_in, ..
    }) = ev
    else {
        return false;
    };
    s.wp4.gifts_steam_signed_in = Some(steam_signed_in);
    true
}

fn on_shaders_event(s: &mut AppState, ev: HostEvent) -> bool {
    let HostEvent::ShadersEvent(ShadersEventPayload { name, data }) = ev else {
        return false;
    };
    if name == "state" {
        if let Some(auto) = data
            .get("recorder")
            .and_then(|r| r.get("auto"))
            .and_then(Value::as_bool)
        {
            s.wp4.shaders_auto = auto;
        }
        s.wp4.shaders_present = true;
        return true;
    }
    if name == "addon" || name == "ready" {
        s.wp4_load_shaders();
        return true;
    }
    false
}

fn on_exo_account_changed(s: &mut AppState, _ev: HostEvent) -> bool {
    s.wp4_load_account_overview();
    true
}

impl AppState {
    pub fn wp4_now_secs(&self) -> f64 {
        self.notice_clock()
    }

    pub fn wp4_is_account_sheet_open(&self) -> bool {
        if self.setup.open || self.status == HostStatus::Standalone {
            return false;
        }
        let codes = usize::from(self.wp4.account_recovery_code.is_some())
            + usize::from(self.wp4.flow_code.is_some());
        account_sheet_open(
            self.wp4.account_sheet_was_open,
            self.wp4.account_overview.as_ref(),
            self.wp4.account_later_this_run,
            codes,
            self.wp4.account_sheet_requests,
        )
    }

    pub fn wp4_is_clients_gate_open(&self) -> bool {
        if self.setup.open || self.status == HostStatus::Standalone {
            return false;
        }
        let done = self.settings.onboarding_complete == Some(true);
        done && clients_gate_shows(self.pc.clients.as_ref(), self.wp4.clients_deferred)
    }

    pub fn wp4_tick(&mut self, now: f64) -> bool {
        let mut redraw = false;

        if let Some((_, expires)) = self.wp4.highlight_row
            && now >= expires
        {
            self.wp4.highlight_row = None;
            redraw = true;
        }

        if let Some(until) = self.wp4.obs_copied_until
            && now >= until
        {
            self.wp4.obs_copied_until = None;
            redraw = true;
        }

        if let Some(at) = self.wp4.steam_poll_at
            && self.wp4.steam_panel_open
            && now >= at
        {
            self.wp4.steam_poll_at = None;
            self.wp4_steam_poll();
            redraw = true;
        }

        if let Some(until) = self.wp4.flow_locked_until {
            if now >= until {
                self.wp4.flow_locked_until = None;
            }
            redraw = true;
        }

        let installing = self.pc.clients.as_ref().is_some_and(|c| c.epic.installing);
        if installing {
            let started = *self.wp4.clients_installer_started_at.get_or_insert(now);
            if !self.wp4.clients_installer_long && now - started >= INSTALLER_WAIT_SECS {
                self.wp4.clients_installer_long = true;
                redraw = true;
            }
            if self.wp4.clients_next_poll_at.is_none_or(|t| now >= t) {
                self.wp4.clients_next_poll_at = Some(now + CLIENTS_POLL_INTERVAL_SECS);
                self.wp4_load_clients_status(false);
            }
        } else if self.wp4.clients_installer_started_at.is_some() {
            self.wp4.clients_installer_started_at = None;
            self.wp4.clients_installer_long = false;
            self.wp4.clients_next_poll_at = None;
            redraw = true;
        }

        if self.wp4.account_later_this_run {
            let next = *self
                .wp4
                .account_probe_next_at
                .get_or_insert(now + ACCOUNT_PROBE_INTERVAL_SECS);
            if now >= next {
                self.wp4.account_probe_next_at = Some(now + ACCOUNT_PROBE_INTERVAL_SECS);
                self.wp4_probe_account_service();
            }
        } else {
            self.wp4.account_probe_next_at = None;
        }

        let open_now = self.wp4_is_account_sheet_open();
        if open_now != self.wp4.account_sheet_was_open {
            self.wp4.account_sheet_was_open = open_now;
            if open_now {
                let code_pending = self
                    .wp4
                    .account_overview
                    .as_ref()
                    .is_some_and(|o| o.code_pending);
                self.wp4.flow = AccountFlowState {
                    screen: first_account_screen(code_pending),
                    busy: false,
                };
            }
            redraw = true;
        }

        redraw
    }

    pub fn wp4_warm_startup(&mut self) {
        self.wp4_reload_settings();
        self.wp4_load_account_overview();
        self.wp4_load_clients_status(false);
        self.wp4_load_backup_status();
        self.wp4_load_stores_matrix();
        self.wp4_load_store_accounts();
        self.wp4_load_steam_account();
        self.wp4_load_gifts_steam_status();
        self.wp4_load_shaders();
        self.wp4_load_web_prefs();
        self.wp4_load_keep_status();
    }

    pub fn wp4_load_keep_status(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, KeepStatus>(METHOD_KEEP_STATUS, &json!({}), move |res| {
                if let Ok(status) = res {
                    handle.update(move |s, _| s.wp4.keep_status = Some(status));
                }
            });
        }
    }

    pub fn wp4_load_settings_all(&mut self) {
        self.wp4_load_backup_status();
        self.wp4_load_stores_matrix();
        self.wp4_load_store_accounts();
        self.wp4_load_steam_account();
        self.wp4_load_gifts_steam_status();
        self.wp4_load_shaders();
        self.wp4_load_account_overview();
        self.wp4_load_native_auth_providers();
        self.wp4_load_web_prefs();
        if self.wp4.riot_draft.text.is_empty()
            && let Some(ref id) = self.settings.riot_id
            && !id.trim().is_empty()
        {
            self.wp4.riot_draft = TextEdit::new(id.trim());
            self.wp4.riot_editing = false;
        } else if self
            .settings
            .riot_id
            .as_deref()
            .unwrap_or("")
            .trim()
            .is_empty()
        {
            self.wp4.riot_editing = true;
        }
    }

    pub fn wp4_jump_to_setting(&mut self, entry: &'static SettingsIndexEntry) {
        self.pc.settings_section = entry.section;
        self.pc.settings_query.text.clear();
        self.pc.settings_query.sync_invariants();
        self.wp4.highlight_row = Some((entry.id, self.wp4_now_secs() + 1.8));
    }

    pub fn wp4_open_url(&self, url: &str) {
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_SHELL_OPEN_URL, &json!({ "url": url }), |_| {});
        }
        #[cfg(not(windows))]
        let _ = url;
    }

    pub fn wp4_save_settings_patch(
        &mut self,
        patch: Map<String, Value>,
        on_done: impl FnOnce(&mut AppState, Result<(), String>) + Send + 'static,
    ) {
        #[cfg(windows)]
        {
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                rpc.call::<_, LauncherSettings>(
                    METHOD_SETTINGS_SET,
                    &Value::Object(patch),
                    move |res| {
                        handle.update(move |s, _| match res {
                            Ok(set) => {
                                s.settings = set;
                                on_done(s, Ok(()));
                            }
                            Err(e) => on_done(s, Err(e.to_string())),
                        });
                    },
                );
                return;
            }
        }
        let _ = patch;
        on_done(self, Ok(()));
    }

    pub fn wp4_reload_settings(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, LauncherSettings>(METHOD_SETTINGS_GET, &json!({}), move |res| {
                if let Ok(set) = res {
                    handle.update(move |s, _| s.settings = set);
                }
            });
        }
    }

    pub fn wp4_pick_install_folder(&mut self) {
        if self.wp4.picking_folder {
            return;
        }
        self.wp4.picking_folder = true;
        self.wp4.folder_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, PickFolderReply>(
                METHOD_SHELL_PICK_FOLDER,
                &json!({ "title": "Choose where new games install" }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.picking_folder = false;
                        match res {
                            Ok(reply) if reply.ok => {
                                if let Some(path) = reply.path.filter(|p| !p.trim().is_empty()) {
                                    s.settings.default_install_root = Some(path.clone());
                                    let mut map = Map::new();
                                    map.insert("defaultInstallRoot".into(), Value::String(path));
                                    s.wp4_save_settings_patch(map, |s, r| {
                                        if let Err(err) = r {
                                            s.wp4.folder_error = Some(err);
                                        }
                                    });
                                }
                            }
                            Ok(reply) if reply.cancelled => {}
                            Ok(reply) => {
                                s.wp4.folder_error = Some(reply.message.unwrap_or_else(|| {
                                    "Could not open the folder picker. Try again.".into()
                                }));
                            }
                            Err(err) => s.wp4.folder_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        self.wp4.picking_folder = false;
    }

    pub fn wp4_reset_install_folder(&mut self) {
        self.wp4.folder_error = None;
        self.settings.default_install_root = None;
        let mut map = Map::new();
        map.insert("defaultInstallRoot".into(), Value::Null);
        self.wp4_save_settings_patch(map, |s, r| {
            if let Err(err) = r {
                s.wp4.folder_error = Some(err);
            }
        });
    }

    pub fn wp4_set_download_hours(&mut self, start: Option<i32>, end: Option<i32>) {
        self.wp4.download_hours_open = false;
        self.settings.download_hours_start = start;
        self.settings.download_hours_end = end;
        let mut map = Map::new();
        map.insert(
            "downloadHoursStart".into(),
            start.map_or(Value::Null, Value::from),
        );
        map.insert(
            "downloadHoursEnd".into(),
            end.map_or(Value::Null, Value::from),
        );
        self.wp4_save_settings_patch(map, |_, _| {});
    }

    pub fn wp4_set_upscaler_anticheat(&mut self, on: bool) {
        self.settings.upscaler_anti_cheat_everywhere = on;
        let mut map = Map::new();
        map.insert("upscalerAntiCheatEverywhere".into(), Value::Bool(on));
        self.wp4_save_settings_patch(map, |_, _| {});
    }

    pub fn wp4_set_achievement_volume(&mut self, volume: i32, commit: bool) {
        let clamped = (volume.clamp(0, 100) / 5) * 5;
        self.settings.achievement_volume = Some(clamped);
        if !commit {
            return;
        }
        self.wp4.achievement_volume_error = None;
        let mut map = Map::new();
        map.insert("achievementVolume".into(), Value::from(clamped));
        self.wp4_save_settings_patch(map, |s, r| {
            if let Err(err) = r {
                s.wp4.achievement_volume_error = Some(err);
            }
        });
    }

    pub fn wp4_preview_achievement(&mut self) {
        self.wp4.achievement_preview_error = None;
        let tier = ACHIEVEMENT_TIERS[self.wp4.achievement_preview_index % ACHIEVEMENT_TIERS.len()];
        self.wp4.achievement_preview_index = self.wp4.achievement_preview_index.wrapping_add(1);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_ACHIEVEMENTS_PREVIEW,
                &json!({ "tier": tier }),
                move |res| {
                    if let Err(err) = res {
                        let msg = err.to_string();
                        handle.update(move |s, _| {
                            s.wp4.achievement_preview_error = Some(msg);
                        });
                    }
                },
            );
        }
    }

    pub fn wp4_load_backup_status(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, BackupStatus>(METHOD_BACKUP_STATUS, &json!({}), move |res| {
                if let Ok(status) = res {
                    handle.update(move |s, _| {
                        if let Some(ref note) = status.note
                            && !note.sentence.is_empty()
                        {
                            s.wp4.backup_note = Some(note.sentence.clone());
                        }
                        s.pc.backup = Some(status);
                    });
                }
            });
        }
    }

    pub fn wp4_run_backup(&mut self, kind: BackupOp) {
        if self.wp4.backup_busy.is_some() || self.wp4.backup_held.is_some() {
            return;
        }
        self.wp4.backup_confirm_import = false;
        self.wp4.backup_busy = Some(kind);
        self.wp4.backup_outcome = None;
        let method = match kind {
            BackupOp::Now => METHOD_BACKUP_NOW,
            BackupOp::Export => METHOD_BACKUP_EXPORT,
            BackupOp::Import => METHOD_BACKUP_IMPORT,
        };
        let fallback = match kind {
            BackupOp::Now => "The backup was not made. Try again.",
            BackupOp::Export => "The backup file was not saved. Try again.",
            BackupOp::Import => "The backup was not restored. Try again.",
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, BackupActionReply>(method, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.backup_busy = None;
                    match res {
                        Ok(done) if done.cancelled => {}
                        Ok(done) if done.restored => {
                            s.wp4.backup_held = Some(done.message.unwrap_or_else(|| {
                                "Restored. Close and reopen Exo to finish.".into()
                            }));
                        }
                        Ok(done) if done.ok => {
                            let text = if done.restarting {
                                "Restored. Exo is restarting to load your data.".into()
                            } else {
                                done.message.unwrap_or_else(|| "Done.".into())
                            };
                            s.wp4.backup_outcome = Some(BackupOutcome {
                                kind,
                                ok: true,
                                text,
                            });
                            if kind == BackupOp::Now {
                                s.wp4_load_backup_status();
                            }
                        }
                        Ok(done) => {
                            s.wp4.backup_outcome = Some(BackupOutcome {
                                kind,
                                ok: false,
                                text: done.message.unwrap_or_else(|| fallback.into()),
                            });
                        }
                        Err(err) => {
                            let err = err.to_string();
                            s.wp4.backup_outcome = Some(BackupOutcome {
                                kind,
                                ok: false,
                                text: if err.is_empty() { fallback.into() } else { err },
                            });
                        }
                    }
                });
            });
            return;
        }
        let _ = (method, fallback);
        self.wp4.backup_busy = None;
    }

    pub fn wp4_load_stores_matrix(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, Vec<StoreStatus>>(METHOD_STORES_MATRIX, &json!({}), move |res| {
                if let Ok(list) = res {
                    handle.update(move |s, _| s.wp4.stores = list);
                }
            });
        }
    }

    pub fn wp4_check_stores(&mut self) {
        if self.wp4.store_check_busy {
            return;
        }
        self.wp4.store_check_busy = true;
        self.wp4.store_check_copy = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreLocalCheck>(METHOD_STORES_CHECK, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.store_check_busy = false;
                    match res {
                        Ok(check) if check.state == "failed" => {
                            s.wp4.store_check_copy =
                                Some("Could not check the stores. Try again.".into());
                        }
                        Ok(_) => {
                            s.wp4_load_stores_matrix();
                            s.wp4_load_store_accounts();
                        }
                        Err(_) => {
                            s.wp4.store_check_copy =
                                Some("Could not check the stores. Try again.".into());
                        }
                    }
                });
            });
            return;
        }
        self.wp4.store_check_busy = false;
    }

    pub fn wp4_connect_store(&mut self, store: String, display_name: String) {
        if self.wp4.store_busy.contains_key(&store) {
            return;
        }
        self.wp4
            .store_busy
            .insert(store.clone(), StoreActionKind::Connect);
        self.wp4.store_notes.remove(&store);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let st = store.clone();
            rpc.call::<_, StoresAuthReply>(
                METHOD_STORES_AUTH,
                &json!({ "store": store }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.store_busy.remove(&st);
                        match res {
                            Ok(reply) => {
                                if reply.message.is_some() || !reply.ok {
                                    let text = reply.message.unwrap_or_else(|| {
                                        format!("Could not sign in to {display_name}. Try again.")
                                    });
                                    s.wp4.store_notes.insert(
                                        st.clone(),
                                        StoreNote {
                                            text,
                                            error: !reply.ok,
                                        },
                                    );
                                }
                                if reply.ok {
                                    s.wp4_load_stores_matrix();
                                    s.wp4_load_store_accounts();
                                }
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.store_notes.insert(
                                    st,
                                    StoreNote {
                                        text: if err.is_empty() {
                                            format!(
                                                "Exo couldn’t sign in to {display_name}. Try again."
                                            )
                                        } else {
                                            err
                                        },
                                        error: true,
                                    },
                                );
                            }
                        }
                    });
                },
            );
            return;
        }
        self.wp4.store_busy.remove(&store);
    }

    pub fn wp4_open_store(&mut self, store: String, display_name: String) {
        if self.wp4.store_busy.contains_key(&store) {
            return;
        }
        if store == "microsoft-store" {
            self.wp4_open_url("ms-windows-store://home");
            return;
        }
        self.wp4
            .store_busy
            .insert(store.clone(), StoreActionKind::Open);
        self.wp4.store_notes.remove(&store);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let st = store.clone();
            rpc.call::<_, ActionResult>(
                METHOD_SHELL_SHOW_STORE,
                &json!({ "store": store }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.store_busy.remove(&st);
                        match res {
                            Ok(reply) if !reply.ok => {
                                s.wp4.store_notes.insert(
                                    st,
                                    StoreNote {
                                        text: reply.message.unwrap_or_else(|| {
                                            format!("Exo couldn’t open {display_name}. Try again.")
                                        }),
                                        error: true,
                                    },
                                );
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.store_notes.insert(
                                    st,
                                    StoreNote {
                                        text: if err.is_empty() {
                                            format!("Exo couldn’t open {display_name}. Try again.")
                                        } else {
                                            err
                                        },
                                        error: true,
                                    },
                                );
                            }
                            Ok(_) => {}
                        }
                    });
                },
            );
            return;
        }
        self.wp4.store_busy.remove(&store);
    }

    pub fn wp4_load_store_accounts(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreAccounts>(METHOD_ACCOUNTS_LIST, &json!({}), move |res| {
                if let Ok(accounts) = res {
                    handle.update(move |s, _| s.wp4.store_accounts = Some(accounts));
                }
            });
        }
    }

    pub fn wp4_run_store_account_action(
        &mut self,
        store: String,
        method: &'static str,
        params: Value,
        progress_line: Option<String>,
    ) {
        if self.wp4.account_busy_store.is_some() {
            return;
        }
        self.wp4.account_menu_store = None;
        self.wp4.account_menu_confirm_id = None;
        self.wp4.account_busy_store = Some(store.clone());
        self.wp4.account_error.remove(&store);
        if let Some(line) = progress_line {
            self.wp4.account_line.insert(store.clone(), line);
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let st = store.clone();
            rpc.call::<_, ActionResult>(method, &params, move |res| {
                handle.update(move |s, _| {
                    s.wp4.account_busy_store = None;
                    match res {
                        Ok(reply) if reply.ok => {
                            if let Some(msg) = reply.message {
                                s.wp4.account_line.insert(st.clone(), msg);
                            } else {
                                s.wp4.account_line.remove(&st);
                            }
                        }
                        Ok(reply) => {
                            s.wp4.account_line.remove(&st);
                            s.wp4.account_error.insert(
                                st.clone(),
                                reply.message.unwrap_or_else(|| {
                                    format!("Could not change the {st} account. Try again.")
                                }),
                            );
                        }
                        Err(err) => {
                            let err = err.to_string();
                            s.wp4.account_line.remove(&st);
                            s.wp4.account_error.insert(st.clone(), err.to_string());
                        }
                    }
                    s.wp4_load_store_accounts();
                    s.wp4_load_stores_matrix();
                });
            });
            return;
        }
        let _ = (method, params);
        self.wp4.account_busy_store = None;
    }

    pub fn wp4_epic_remove_added(&mut self, key: String) {
        self.wp4.epic_confirm_remove = None;
        self.wp4.epic_removing_key = Some(key.clone());
        self.wp4.epic_remove_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let k = key.clone();
            rpc.call::<_, ActionResult>(
                METHOD_ACCOUNTS_EPIC_REMOVE,
                &json!({ "key": key }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.epic_removing_key = None;
                        match res {
                            Ok(r) if !r.ok => {
                                s.wp4.epic_remove_error = Some((
                                    k,
                                    r.message.unwrap_or_else(|| {
                                        "Could not remove that account. Try again.".into()
                                    }),
                                ));
                            }
                            Err(err) => s.wp4.epic_remove_error = Some((k, err.to_string())),
                            Ok(_) => {}
                        }
                        s.wp4_load_store_accounts();
                    });
                },
            );
            return;
        }
        self.wp4.epic_removing_key = None;
    }

    pub fn wp4_load_steam_account(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SteamAccountReply>(METHOD_STEAM_ACCOUNT, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.steam_account = Some(res.unwrap_or_default());
                });
            });
        }
    }

    pub fn wp4_steam_begin_sign_in(&mut self) {
        self.wp4.account_menu_store = None;
        self.wp4.account_menu_confirm_id = None;
        self.wp4.steam_panel_open = true;
        self.wp4.steam_mode = SteamSignInMode::Qr;
        self.wp4.steam_step = None;
        self.wp4_steam_start_qr();
    }

    pub fn wp4_steam_close_panel(&mut self) {
        self.wp4.steam_panel_open = false;
        self.wp4.steam_step = None;
        self.wp4.steam_poll_at = None;
        self.wp4.steam_busy = false;
        self.wp4.steam_pass = TextEdit::new("").secure();
        self.wp4.steam_guard.clear();
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_STEAM_SIGN_IN_CANCEL, &json!({}), |_| {});
        }
    }

    pub fn wp4_steam_switch_mode(&mut self, mode: SteamSignInMode) {
        self.wp4.steam_mode = mode;
        self.wp4.steam_step = None;
        self.wp4.steam_poll_at = None;
        match mode {
            SteamSignInMode::Password =>
            {
                #[cfg(windows)]
                if let Some(rpc) = self.rpc.clone() {
                    rpc.call_raw(METHOD_STEAM_SIGN_IN_CANCEL, &json!({}), |_| {});
                }
            }
            SteamSignInMode::Qr => self.wp4_steam_start_qr(),
        }
    }

    pub fn wp4_steam_start_qr(&mut self) {
        self.wp4.steam_busy = true;
        self.wp4.steam_step = None;
        self.wp4.steam_poll_at = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SteamSignInStep>(METHOD_STEAM_SIGN_IN_QR, &json!({}), move |res| {
                handle.update(move |s, _| s.wp4_steam_settle(res));
            });
            return;
        }
        self.wp4.steam_busy = false;
    }

    pub fn wp4_steam_poll(&mut self) {
        if !self.wp4.steam_panel_open {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SteamSignInStep>(METHOD_STEAM_SIGN_IN_POLL, &json!({}), move |res| {
                handle.update(move |s, _| s.wp4_steam_settle(res));
            });
        }
    }

    pub fn wp4_steam_submit_password(&mut self) {
        let account = self.wp4.steam_user.text.trim().to_string();
        let password = std::mem::take(&mut self.wp4.steam_pass.text);
        self.wp4.steam_pass.sync_invariants();
        if account.is_empty() || password.is_empty() || self.wp4.steam_busy {
            return;
        }
        self.wp4.steam_busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SteamSignInStep>(
                METHOD_STEAM_SIGN_IN_PASSWORD,
                &json!({ "account": account, "password": password }),
                move |res| {
                    handle.update(move |s, _| s.wp4_steam_settle(res));
                },
            );
            return;
        }
        self.wp4.steam_busy = false;
    }

    pub fn wp4_steam_submit_guard(&mut self) {
        let code = self.wp4.steam_guard.value();
        self.wp4.steam_guard.clear();
        if code.is_empty() || self.wp4.steam_busy {
            return;
        }
        self.wp4.steam_busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SteamSignInStep>(
                METHOD_STEAM_SIGN_IN_GUARD,
                &json!({ "code": code }),
                move |res| {
                    handle.update(move |s, _| s.wp4_steam_settle(res));
                },
            );
            return;
        }
        self.wp4.steam_busy = false;
    }

    pub fn wp4_steam_sign_out(&mut self) {
        self.wp4.account_menu_store = None;
        self.wp4.account_menu_confirm_id = None;
        self.wp4.account_busy_store = Some("steam".into());
        self.wp4
            .account_line
            .insert("steam".into(), "Exo signs out of the Steam library".into());
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_STEAM_SIGN_OUT, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.account_busy_store = None;
                    s.wp4.account_line.remove("steam");
                    if let Err(err) = res {
                        s.wp4.account_error.insert("steam".into(), err.to_string());
                    }
                    s.wp4_load_steam_account();
                });
            });
            return;
        }
        self.wp4.account_busy_store = None;
    }

    fn wp4_steam_settle(&mut self, res: Result<SteamSignInStep, exo_rpc::RpcError>) {
        self.wp4.steam_busy = false;
        match res {
            Ok(step) if step.state == "done" => {
                self.wp4.steam_panel_open = false;
                self.wp4.steam_step = None;
                self.wp4.steam_poll_at = None;
                self.wp4_load_steam_account();
                self.wp4_load_store_accounts();
                self.wp4_load_stores_matrix();
            }
            Ok(step) => {
                if step.state == "waiting" || step.state == "approve" {
                    let interval = step.interval_seconds.unwrap_or(5).max(1) as f64;
                    self.wp4.steam_poll_at = Some(self.wp4_now_secs() + interval);
                } else {
                    self.wp4.steam_poll_at = None;
                }
                self.wp4.steam_step = Some(step);
            }
            Err(_) => {
                self.wp4.steam_poll_at = None;
                self.wp4.steam_step = Some(SteamSignInStep {
                    state: "failed".into(),
                    message: Some("Could not start the sign-in. Try again.".into()),
                    ..Default::default()
                });
            }
        }
    }

    pub fn wp4_steam_set_offline(&mut self, offline: bool) {
        if self.wp4.steam_offline_pending.is_some() {
            return;
        }
        self.wp4.steam_offline_pending = Some(offline);
        self.wp4.steam_offline_line = None;
        self.wp4.steam_offline_error = None;
        let fallback = if offline {
            "Could not turn on offline mode. Try again."
        } else {
            "Could not turn off offline mode. Try again."
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_STEAM_SET_OFFLINE,
                &json!({ "offline": offline }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.steam_offline_pending = None;
                        match res {
                            Ok(reply) if reply.ok => {
                                s.wp4.steam_offline_line = reply.message;
                            }
                            Ok(reply) => {
                                s.wp4.steam_offline_line = None;
                                s.wp4.steam_offline_error =
                                    Some(reply.message.unwrap_or_else(|| fallback.into()));
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.steam_offline_line = None;
                                s.wp4.steam_offline_error =
                                    Some(if err.is_empty() { fallback.into() } else { err });
                            }
                        }
                        s.wp4_load_store_accounts();
                        s.wp4_load_stores_matrix();
                    });
                },
            );
            return;
        }
        self.wp4.steam_offline_pending = None;
    }

    pub fn wp4_load_gifts_steam_status(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, StoreGiftsReply>(
                METHOD_GIFTS_LIST,
                &json!({ "force": false }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.gifts_steam_signed_in =
                            Some(res.map(|r| r.steam_signed_in).unwrap_or(false));
                    });
                },
            );
        }
    }

    pub fn wp4_gifts_steam_toggle(&mut self, sign_in: bool) {
        self.wp4.account_menu_store = None;
        self.wp4.account_menu_confirm_id = None;
        self.wp4.account_busy_store = Some("steam".into());
        self.wp4.account_error.remove("steam");
        let method = if sign_in {
            METHOD_GIFTS_STEAM_SIGN_IN
        } else {
            METHOD_GIFTS_STEAM_SIGN_OUT
        };
        let fallback = if sign_in {
            "Could not sign in to Steam. Try again."
        } else {
            "Could not sign out of Steam. Try again."
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(method, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.account_busy_store = None;
                    match res {
                        Ok(reply) if reply.ok => {
                            s.wp4.gifts_steam_signed_in = Some(sign_in);
                            if let Some(msg) = reply.message {
                                s.wp4.account_line.insert("steam".into(), msg);
                            }
                        }
                        Ok(reply) => {
                            s.wp4.account_error.insert(
                                "steam".into(),
                                reply.message.unwrap_or_else(|| fallback.into()),
                            );
                        }
                        Err(err) => {
                            let err = err.to_string();
                            s.wp4.account_error.insert(
                                "steam".into(),
                                if err.is_empty() { fallback.into() } else { err },
                            );
                        }
                    }
                });
            });
            return;
        }
        let _ = (method, fallback);
        self.wp4.account_busy_store = None;
    }

    pub fn wp4_save_riot_id(&mut self, next: String) {
        if self.wp4.riot_busy {
            return;
        }
        self.wp4.riot_busy = true;
        self.wp4.riot_note = None;
        let mut map = Map::new();
        map.insert("riotId".into(), Value::String(next.clone()));
        self.wp4_save_settings_patch(map, move |s, res| {
            s.wp4.riot_busy = false;
            match res {
                Ok(()) => {
                    s.wp4.riot_editing = next.is_empty();
                    s.wp4.riot_draft = TextEdit::new(&next);
                    s.wp4_reload_settings();
                }
                Err(err) => {
                    let err = err.to_string();
                    s.wp4.riot_note = Some(StoreNote {
                        text: if err.is_empty() {
                            "Riot ID could not be saved. Try again.".into()
                        } else {
                            err
                        },
                        error: true,
                    });
                }
            }
        });
    }

    pub fn wp4_sync_riot_hours(&mut self) {
        if self.wp4.riot_hours_busy {
            return;
        }
        let riot_id = self.settings.riot_id.clone().unwrap_or_default();
        self.wp4.riot_hours_busy = true;
        self.wp4.riot_hours_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, RiotHoursRefresh>(
                METHOD_PLAYTIME_RIOT_REFRESH,
                &json!({ "riotId": riot_id }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.riot_hours_busy = false;
                        s.wp4_reload_settings();
                        match res {
                            Ok(reply) => {
                                let neutral = reply.unconfigured || reply.client_needed;
                                let text = reply.message.unwrap_or_else(|| {
                                    if reply.ok {
                                        "Riot hours synced.".into()
                                    } else {
                                        "Riot hours could not be synced.".into()
                                    }
                                });
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text,
                                    error: !reply.ok && !neutral,
                                });
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text: if err.is_empty() {
                                        "Riot hours could not be synced.".into()
                                    } else {
                                        err
                                    },
                                    error: true,
                                });
                            }
                        }
                    });
                },
            );
            return;
        }
        let _ = riot_id;
        self.wp4.riot_hours_busy = false;
    }

    pub fn wp4_import_riot_export(&mut self) {
        if self.wp4.riot_hours_busy {
            return;
        }
        let riot_id = self.settings.riot_id.clone().unwrap_or_default();
        self.wp4.riot_hours_busy = true;
        self.wp4.riot_hours_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, RiotExportImport>(
                METHOD_PLAYTIME_IMPORT_RIOT_EXPORT,
                &json!({ "riotId": riot_id }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.riot_hours_busy = false;
                        match res {
                            Ok(reply) if reply.cancelled => {}
                            Ok(reply) => {
                                s.wp4_reload_settings();
                                let text = reply.message.unwrap_or_else(|| {
                                    if reply.ok {
                                        "Riot data export imported.".into()
                                    } else {
                                        "Riot data export could not be imported.".into()
                                    }
                                });
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text,
                                    error: !reply.ok,
                                });
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text: if err.is_empty() {
                                        "Riot data export could not be imported.".into()
                                    } else {
                                        err
                                    },
                                    error: true,
                                });
                            }
                        }
                    });
                },
            );
            return;
        }
        let _ = riot_id;
        self.wp4.riot_hours_busy = false;
    }

    pub fn wp4_begin_riot_hours_edit(&mut self) {
        let base = self.settings.riot_baseline.as_ref();
        self.wp4.riot_val_input =
            TextEdit::new(format_baseline_hours(base.and_then(|b| b.valorant_minutes)));
        self.wp4.riot_lol_input =
            TextEdit::new(format_baseline_hours(base.and_then(|b| b.league_minutes)));
        self.wp4.riot_tft_input =
            TextEdit::new(format_baseline_hours(base.and_then(|b| b.tft_minutes)));
        self.wp4.riot_hours_note = None;
        self.wp4.riot_hours_editing = true;
    }

    pub fn wp4_save_riot_baseline(&mut self) {
        if self.wp4.riot_hours_busy {
            return;
        }
        let val = parse_hours_input(&self.wp4.riot_val_input.text);
        let lol = parse_hours_input(&self.wp4.riot_lol_input.text);
        let tft = parse_hours_input(&self.wp4.riot_tft_input.text);
        let (Ok(v), Ok(l), Ok(t)) = (val, lol, tft) else {
            self.wp4.riot_hours_note = Some(StoreNote {
                text: "Enter starting hours greater than zero.".into(),
                error: true,
            });
            return;
        };
        if v.is_none() && l.is_none() && t.is_none() {
            self.wp4.riot_hours_note = Some(StoreNote {
                text: "Enter starting hours greater than zero.".into(),
                error: true,
            });
            return;
        }
        let mut payload = Map::new();
        if let Some(ref id) = self.settings.riot_id
            && !id.trim().is_empty()
        {
            payload.insert("riotId".into(), Value::String(id.trim().to_string()));
        }
        if let Some(h) = v {
            payload.insert("valorantHours".into(), Value::from(h));
        }
        if let Some(h) = l {
            payload.insert("leagueHours".into(), Value::from(h));
        }
        if let Some(h) = t {
            payload.insert("tftHours".into(), Value::from(h));
        }
        self.wp4.riot_hours_busy = true;
        self.wp4.riot_hours_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, RiotBaselineReply>(
                METHOD_PLAYTIME_SET_RIOT_BASELINE,
                &Value::Object(payload),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.riot_hours_busy = false;
                        s.wp4_reload_settings();
                        match res {
                            Ok(reply) if reply.ok => {
                                s.wp4.riot_hours_editing = false;
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text: reply
                                        .message
                                        .unwrap_or_else(|| "Starting hours saved.".into()),
                                    error: false,
                                });
                            }
                            Ok(reply) => {
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text: reply.message.unwrap_or_else(|| {
                                        "Starting hours could not be saved. Try again.".into()
                                    }),
                                    error: true,
                                });
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.riot_hours_note = Some(StoreNote {
                                    text: if err.is_empty() {
                                        "Starting hours could not be saved. Try again.".into()
                                    } else {
                                        err
                                    },
                                    error: true,
                                });
                            }
                        }
                    });
                },
            );
            return;
        }
        self.wp4.riot_hours_busy = false;
    }

    pub fn wp4_load_shaders(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, ShadersAddonStatus>(METHOD_SHADERS_ADDON, &json!({}), move |res| {
                if let Ok(st) = res {
                    h1.update(move |s, _| {
                        s.wp4.shaders_present = st.present;
                    });
                }
            });
            rpc.call::<_, Value>(
                METHOD_SHADERS_CALL,
                &json!({ "method": "snapshot", "params": {} }),
                move |res| {
                    if let Ok(v) = res
                        && let Some(auto) = v
                            .get("result")
                            .and_then(|r| r.get("recorder"))
                            .and_then(|r| r.get("auto"))
                            .and_then(Value::as_bool)
                    {
                        handle.update(move |s, _| s.wp4.shaders_auto = auto);
                    }
                },
            );
        }
    }

    pub fn wp4_set_shaders_auto(&mut self, on: bool) {
        self.wp4.shaders_error = None;
        self.wp4.shaders_auto = on;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, Value>(
                METHOD_SHADERS_CALL,
                &json!({ "method": "recorder.auto", "params": { "on": on } }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(v) if v.get("ok").and_then(Value::as_bool) == Some(false) => {
                            s.wp4.shaders_auto = !on;
                            s.wp4.shaders_error = Some(
                                v.get("message")
                                    .and_then(Value::as_str)
                                    .unwrap_or("The shader engine did not answer.")
                                    .to_string(),
                            );
                        }
                        Ok(_) => s.wp4_load_shaders(),
                        Err(err) => {
                            let err = err.to_string();
                            s.wp4.shaders_auto = !on;
                            s.wp4.shaders_error = Some(if err.is_empty() {
                                "The shader engine did not answer.".into()
                            } else {
                                err
                            });
                        }
                    });
                },
            );
        }
    }

    pub fn wp4_clear_caches(&mut self) {
        if self.wp4.caches_busy {
            return;
        }
        self.wp4.caches_busy = true;
        self.wp4.caches_done = false;
        self.wp4.caches_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc2 = rpc.clone();
            let h2 = handle.clone();
            rpc.call::<_, ActionResult>(
                METHOD_APP_REPAIR_CACHE,
                &json!({ "scope": "sizes" }),
                move |res| match res {
                    Ok(r) if r.ok => {
                        rpc2.call::<_, ActionResult>(
                            METHOD_APP_REPAIR_CACHE,
                            &json!({ "scope": "upscalers" }),
                            move |res2| {
                                h2.update(move |s, _| {
                                    s.wp4.caches_busy = false;
                                    match res2 {
                                        Ok(r2) if r2.ok => s.wp4.caches_done = true,
                                        Ok(r2) => {
                                            s.wp4.caches_error =
                                                Some(r2.message.unwrap_or_else(|| {
                                                    "Caches could not be cleared. Try again.".into()
                                                }));
                                        }
                                        Err(err) => s.wp4.caches_error = Some(err.to_string()),
                                    }
                                });
                            },
                        );
                    }
                    Ok(r) => {
                        handle.update(move |s, _| {
                            s.wp4.caches_busy = false;
                            s.wp4.caches_error = Some(r.message.unwrap_or_else(|| {
                                "Caches could not be cleared. Try again.".into()
                            }));
                        });
                    }
                    Err(err) => {
                        let err = err.to_string();
                        let msg = err.to_string();
                        handle.update(move |s, _| {
                            s.wp4.caches_busy = false;
                            s.wp4.caches_error = Some(msg);
                        });
                    }
                },
            );
            return;
        }
        self.wp4.caches_busy = false;
        self.wp4.caches_done = true;
    }

    pub fn wp4_load_account_overview(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOverview>(
                METHOD_EXO_ACCOUNT_OVERVIEW,
                &json!({}),
                move |res| {
                    if let Ok(ov) = res {
                        handle.update(move |s, _| {
                            s.pc.account.signed_in = ov.signed_in;
                            s.pc.account.email = ov.email.clone();
                            s.pc.account.account_asked = ov.account_asked;
                            s.wp4.account_overview = Some(ov);
                            s.wp4_load_account_passkeys();
                        });
                    }
                },
            );
        }
    }

    pub fn wp4_load_native_auth_providers(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, NativeAuthProvidersReply>(
                METHOD_AUTH_NATIVE_PROVIDERS,
                &json!({}),
                move |res| {
                    if let Ok(reply) = res {
                        handle.update(move |s, _| {
                            s.wp4.native_auth_providers = Some(reply.providers);
                        });
                    }
                },
            );
        }
    }

    pub fn wp4_load_account_passkeys(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountPasskeyStatus>(
                METHOD_EXO_ACCOUNT_PASSKEY_STATUS,
                &json!({}),
                move |res| {
                    if let Ok(st) = res {
                        handle.update(move |s, _| {
                            s.wp4.account_passkeys = Some(st);
                        });
                    }
                },
            );
        }
    }

    pub fn wp4_account_passkey_register(&mut self) {
        if self.wp4.account_passkey_busy {
            return;
        }
        self.wp4.account_passkey_busy = true;
        self.wp4.account_passkey_error = None;
        self.wp4.account_passkey_note = None;
        let owner_hwnd = self.pc.owner_hwnd;
        let password = if self.wp4.account_passkey_reauth_needed {
            let pw = self.wp4.account_passkey_reauth_pw.text.clone();
            if pw.is_empty() {
                self.wp4.account_passkey_busy = false;
                self.wp4.account_passkey_error =
                    Some("Enter your account password to confirm adding a passkey.".into());
                return;
            }
            Some(pw)
        } else {
            None
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let payload = match password {
                Some(pw) => json!({ "ownerHwnd": owner_hwnd, "password": pw }),
                None => json!({ "ownerHwnd": owner_hwnd }),
            };
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_PASSKEY_REGISTER,
                &payload,
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_passkey_busy = false;
                        match res {
                            Ok(r) if r.ok => {
                                s.wp4.account_passkey_reauth_needed = false;
                                s.wp4.account_passkey_reauth_pw = TextEdit::new("");
                                s.wp4.account_passkey_note = Some(
                                    "Passkey added. Password and recovery code remain available."
                                        .into(),
                                );
                                s.wp4_load_account_passkeys();
                            }
                            Ok(r) if r.error.as_deref() == Some("reauth-required") => {
                                s.wp4.account_passkey_reauth_needed = true;
                                s.wp4.account_passkey_error = Some(r.message);
                            }
                            Ok(r) => s.wp4.account_passkey_error = Some(r.message),
                            Err(err) => s.wp4.account_passkey_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        let _ = (owner_hwnd, password);
        self.wp4.account_passkey_busy = false;
    }

    pub fn wp4_account_passkey_delete(&mut self, id: String) {
        if self.wp4.account_passkey_busy {
            return;
        }
        self.wp4.account_passkey_busy = true;
        self.wp4.account_passkey_error = None;
        self.wp4.account_passkey_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_PASSKEY_DELETE,
                &json!({ "id": id }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_passkey_busy = false;
                        match res {
                            Ok(r) if r.ok => {
                                s.wp4.account_passkey_note = Some("Passkey removed.".into());
                                s.wp4_load_account_passkeys();
                            }
                            Ok(r) => s.wp4.account_passkey_error = Some(r.message),
                            Err(err) => s.wp4.account_passkey_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        let _ = id;
        self.wp4.account_passkey_busy = false;
    }

    pub fn wp4_account_passkey_sign_in(&mut self) {
        if self.wp4.account_form_busy {
            return;
        }
        self.wp4.account_form_error = None;
        self.wp4.account_form_offline = false;
        self.wp4.account_form_busy = true;
        let email = self.wp4.account_email.text.trim().to_string();
        let owner_hwnd = self.pc.owner_hwnd;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_PASSKEY_SIGN_IN,
                &json!({ "email": email, "ownerHwnd": owner_hwnd }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_form_busy = false;
                        match res {
                            Ok(r) if r.ok => s.wp4_load_account_overview(),
                            Ok(r) => {
                                s.wp4.account_form_offline = r.offline;
                                s.wp4.account_form_error = Some(if r.message.is_empty() {
                                    "Passkey sign-in didn't finish. You can use your password instead.".into()
                                } else {
                                    r.message
                                });
                            }
                            Err(err) => s.wp4.account_form_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        let _ = (email, owner_hwnd);
        self.wp4.account_form_busy = false;
    }

    pub fn wp4_account_sync_now(&mut self) {
        self.wp4.account_sync_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOverview>(METHOD_EXO_ACCOUNT_SYNC, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(ov) => s.wp4.account_overview = Some(ov),
                    Err(err) => {
                        let err = err.to_string();
                        s.wp4.account_sync_error = Some(if err.is_empty() {
                            "Exo couldn’t sync. Try again.".into()
                        } else {
                            err
                        });
                    }
                });
            });
        }
    }

    pub fn wp4_account_sign_out(&mut self) {
        self.wp4.account_sign_out_confirm = false;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(METHOD_EXO_ACCOUNT_SIGN_OUT, &json!({}), move |res| {
                handle.update(move |s, _| {
                    if let Err(err) = res {
                        s.push_notice(err.to_string(), crate::state::NoticeTone::Bad);
                    }
                    s.wp4_load_account_overview();
                });
            });
        }
    }

    pub fn wp4_account_change_password(&mut self) {
        if self.wp4.account_security_busy {
            return;
        }
        let current = self.wp4.account_current_pass.text.clone();
        let next = self.wp4.account_new_pass.text.clone();
        self.wp4.account_security_error = None;
        self.wp4.account_security_note = None;
        if next.chars().count() < 12 {
            self.wp4.account_security_error = Some("Use at least 12 characters.".into());
            return;
        }
        self.wp4.account_security_busy = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_CHANGE_PASSWORD,
                &json!({ "current": current, "password": next }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_security_busy = false;
                        match res {
                            Ok(r) if r.ok => {
                                s.wp4.account_current_pass = TextEdit::new("").secure();
                                s.wp4.account_new_pass = TextEdit::new("").secure();
                                s.wp4.account_security_form = AccountSecurityForm::None;
                                s.wp4.account_security_note =
                                    Some("Password changed. Other PCs were signed out.".into());
                            }
                            Ok(r) => {
                                s.wp4.account_security_error = Some(if r.message.is_empty() {
                                    "The password was not changed. Try again.".into()
                                } else {
                                    r.message
                                });
                            }
                            Err(err) => s.wp4.account_security_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        self.wp4.account_security_busy = false;
    }

    pub fn wp4_account_new_recovery_code(&mut self) {
        if self.wp4.account_security_busy {
            return;
        }
        let password = self.wp4.account_code_pass.text.clone();
        if password.is_empty() {
            return;
        }
        self.wp4.account_security_error = None;
        self.wp4.account_security_busy = true;
        self.wp4.account_sheet_requests += 1;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_NEW_RECOVERY_CODE,
                &json!({ "password": password }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_security_busy = false;
                        s.wp4.account_sheet_requests =
                            s.wp4.account_sheet_requests.saturating_sub(1);
                        match res {
                            Ok(r) if r.ok => {
                                if let Some(code) = r.recovery_code.filter(|c| !c.is_empty()) {
                                    s.wp4.account_code_pass = TextEdit::new("").secure();
                                    s.wp4.account_security_form = AccountSecurityForm::None;
                                    s.wp4.account_recovery_code = Some(code);
                                    s.wp4.account_recovery_typed = TextEdit::new("");
                                    s.wp4.account_recovery_note = None;
                                    s.wp4.account_recovery_failed = false;
                                } else {
                                    s.wp4.account_security_error =
                                        Some("The code was not made. Try again.".into());
                                }
                            }
                            Ok(r) => {
                                s.wp4.account_security_error = Some(if r.message.is_empty() {
                                    "The code was not made. Try again.".into()
                                } else {
                                    r.message
                                });
                            }
                            Err(err) => s.wp4.account_security_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        self.wp4.account_security_busy = false;
        self.wp4.account_sheet_requests = self.wp4.account_sheet_requests.saturating_sub(1);
    }

    pub fn wp4_load_account_sessions(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountSessionsReply>(
                METHOD_EXO_ACCOUNT_SESSIONS,
                &json!({}),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) => {
                            if !reply.ok {
                                s.wp4.account_sessions_note = reply.message;
                                s.wp4.account_sessions = Some(Vec::new());
                            } else {
                                s.wp4.account_sessions = Some(reply.sessions);
                            }
                        }
                        Err(err) => {
                            let err = err.to_string();
                            s.wp4.account_sessions = Some(Vec::new());
                            s.wp4.account_sessions_note = Some(err.to_string());
                        }
                    });
                },
            );
        }
    }

    pub fn wp4_revoke_account_session(&mut self, device: String) {
        self.wp4.account_sessions_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_REVOKE,
                &json!({ "device": device }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.ok => {
                            s.wp4.account_sessions_note = Some("That PC was signed out.".into());
                            s.wp4_load_account_sessions();
                            s.wp4_load_account_overview();
                        }
                        Ok(r) => {
                            s.wp4.account_sessions_note = Some(if r.message.is_empty() {
                                "That PC was not signed out. Try again.".into()
                            } else {
                                r.message
                            });
                        }
                        Err(err) => s.wp4.account_sessions_note = Some(err.to_string()),
                    });
                },
            );
        }
    }

    pub fn wp4_export_account_data(&mut self) {
        self.wp4.account_export_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountFileReply>(METHOD_EXO_ACCOUNT_EXPORT, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(r) if r.cancelled => {}
                    Ok(r) if r.ok => s.wp4.account_export_note = Some("Saved.".into()),
                    Ok(r) => {
                        s.wp4.account_export_note = Some(
                            r.message
                                .unwrap_or_else(|| "The file was not saved. Try again.".into()),
                        );
                    }
                    Err(err) => s.wp4.account_export_note = Some(err.to_string()),
                });
            });
        }
    }

    pub fn wp4_delete_account(&mut self) {
        if self.wp4.account_delete_busy {
            return;
        }
        let password = self.wp4.account_delete_pass.text.clone();
        if password.is_empty() {
            return;
        }
        self.wp4.account_delete_busy = true;
        self.wp4.account_delete_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(
                METHOD_EXO_ACCOUNT_DELETE,
                &json!({ "password": password }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_delete_busy = false;
                        match res {
                            Ok(r) if r.ok => {
                                s.wp4.account_delete_pass = TextEdit::new("").secure();
                                s.wp4.account_delete_open = false;
                                s.wp4_load_account_overview();
                            }
                            Ok(r) => {
                                s.wp4.account_delete_error = Some(if r.message.is_empty() {
                                    "The account was not deleted. Try again.".into()
                                } else {
                                    r.message
                                });
                            }
                            Err(err) => s.wp4.account_delete_error = Some(err.to_string()),
                        }
                    });
                },
            );
            return;
        }
        self.wp4.account_delete_busy = false;
    }

    pub fn wp4_account_signed_out_submit(&mut self) {
        if self.wp4.account_form_busy {
            return;
        }
        let email = self.wp4.account_email.text.trim().to_string();
        let password = self.wp4.account_password.text.clone();
        let code = self.wp4.account_reset_code.text.trim().to_string();
        if email.is_empty() || password.is_empty() {
            return;
        }
        let mode = self.wp4.account_signed_out_mode;
        if (mode == AccountSignedOutMode::Create || mode == AccountSignedOutMode::Reset)
            && password.chars().count() < 12
        {
            self.wp4.account_form_error = Some("Use at least 12 characters.".into());
            return;
        }
        self.wp4.account_form_error = None;
        self.wp4.account_form_offline = false;
        self.wp4.account_form_busy = true;
        self.wp4.account_sheet_requests += 1;
        let (method, params) = match mode {
            AccountSignedOutMode::Create => (
                METHOD_EXO_ACCOUNT_CREATE,
                json!({ "email": email, "password": password }),
            ),
            AccountSignedOutMode::SignIn => (
                METHOD_EXO_ACCOUNT_SIGN_IN,
                json!({ "email": email, "password": password }),
            ),
            AccountSignedOutMode::Reset => (
                METHOD_EXO_ACCOUNT_RESET,
                json!({ "email": email, "code": code, "password": password }),
            ),
        };
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(method, &params, move |res| {
                handle.update(move |s, _| {
                    s.wp4.account_form_busy = false;
                    s.wp4.account_sheet_requests = s.wp4.account_sheet_requests.saturating_sub(1);
                    match res {
                        Ok(r) if r.ok => {
                            s.wp4.account_password = TextEdit::new("").secure();
                            if let Some(issued) = r.recovery_code.filter(|c| !c.is_empty()) {
                                s.wp4.account_recovery_code = Some(issued);
                                s.wp4.account_recovery_typed = TextEdit::new("");
                                s.wp4.account_recovery_note = None;
                                s.wp4.account_recovery_failed = false;
                            } else {
                                s.wp4_load_account_overview();
                            }
                        }
                        Ok(r) => {
                            s.wp4.account_form_offline = r.offline;
                            s.wp4.account_form_error = Some(if r.message.is_empty() {
                                "Exo couldn’t reach your account. Try again.".into()
                            } else {
                                r.message
                            });
                        }
                        Err(err) => s.wp4.account_form_error = Some(err.to_string()),
                    }
                });
            });
            return;
        }
        let _ = (method, params);
        self.wp4.account_form_busy = false;
        self.wp4.account_sheet_requests = self.wp4.account_sheet_requests.saturating_sub(1);
    }

    pub fn wp4_save_recovery_code_file(&mut self, code: String) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountFileReply>(
                METHOD_EXO_ACCOUNT_SAVE_CODE,
                &json!({ "code": code }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(r) if r.cancelled => {}
                        Ok(r) if r.ok => s.wp4.account_recovery_note = Some("Saved.".into()),
                        Ok(r) => {
                            s.wp4.account_recovery_note = Some(
                                r.message
                                    .unwrap_or_else(|| "The file wasn't saved. Try again.".into()),
                            );
                        }
                        Err(_) => {
                            s.wp4.account_recovery_note =
                                Some("The file wasn't saved. Try again.".into());
                        }
                    });
                },
            );
            return;
        }
        let _ = code;
        self.wp4.account_recovery_note = Some("Saved.".into());
    }

    pub fn wp4_confirm_recovery_code(&mut self, for_settings: bool) {
        let code = if for_settings {
            self.wp4.account_recovery_code.as_deref()
        } else {
            self.wp4.flow_code.as_deref()
        };
        let Some(code) = code else {
            return;
        };
        if !recovery_code_confirmed(&self.wp4.account_recovery_typed.text, code)
            || self.wp4.account_recovery_finishing
        {
            return;
        }
        self.wp4.account_recovery_finishing = true;
        self.wp4.account_recovery_failed = false;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(
                METHOD_EXO_ACCOUNT_CODE_CONFIRMED,
                &json!({}),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.account_recovery_finishing = false;
                        match res {
                            Ok(r) if r.ok => {
                                s.wp4.account_recovery_typed = TextEdit::new("");
                                s.wp4.account_recovery_note = None;
                                if for_settings {
                                    s.wp4.account_recovery_code = None;
                                } else {
                                    s.wp4.flow_code = None;
                                    s.wp4.flow =
                                        next_account_flow(s.wp4.flow, AccountFlowEvent::Confirm);
                                }
                                s.wp4_load_account_overview();
                            }
                            _ => s.wp4.account_recovery_failed = true,
                        }
                    });
                },
            );
            return;
        }
        self.wp4.account_recovery_finishing = false;
        if for_settings {
            self.wp4.account_recovery_code = None;
        } else {
            self.wp4.flow_code = None;
            self.wp4.flow = next_account_flow(self.wp4.flow, AccountFlowEvent::Confirm);
        }
    }

    pub fn wp4_flow_open(&mut self, to: AccountScreen) {
        self.wp4.flow_email_error = None;
        self.wp4.flow_password_error = None;
        self.wp4.flow_form_error = None;
        self.wp4.flow_locked_until = None;
        if to == AccountScreen::Reset && self.wp4.flow.screen == AccountScreen::NewCode {
            self.wp4.flow_reset_from = AccountScreen::NewCode;
            if self.wp4.flow_email.text.trim().is_empty()
                && let Some(ref em) = self
                    .wp4
                    .account_overview
                    .as_ref()
                    .and_then(|o| o.email.clone())
            {
                self.wp4.flow_email = TextEdit::new(em.trim());
            }
        } else if to == AccountScreen::Reset {
            self.wp4.flow_reset_from = AccountScreen::SignIn;
        }
        self.wp4.flow = next_account_flow(self.wp4.flow, AccountFlowEvent::Open(to));
    }

    pub fn wp4_flow_submit(&mut self) {
        if self.wp4.flow.busy {
            return;
        }
        if let Some(until) = self.wp4.flow_locked_until
            && self.wp4_now_secs() < until
        {
            return;
        }
        let screen = self.wp4.flow.screen;
        let email = self.wp4.flow_email.text.trim().to_string();
        let password = self.wp4.flow_password.text.clone();
        let code = self.wp4.flow_reset_code.text.trim().to_string();
        self.wp4.flow_email_error = None;
        self.wp4.flow_password_error = None;
        self.wp4.flow_form_error = None;

        if (screen == AccountScreen::Create || screen == AccountScreen::Reset)
            && password.chars().count() < 12
        {
            self.wp4.flow_password_error = Some("Use at least 12 characters.".into());
            return;
        }
        let (method, params, expects_code) = match screen {
            AccountScreen::Create => (
                METHOD_EXO_ACCOUNT_CREATE,
                json!({ "email": email, "password": password }),
                true,
            ),
            AccountScreen::SignIn => (
                METHOD_EXO_ACCOUNT_SIGN_IN,
                json!({ "email": email, "password": password }),
                false,
            ),
            AccountScreen::Reset => (
                METHOD_EXO_ACCOUNT_RESET,
                json!({ "email": email, "code": code, "password": password }),
                true,
            ),
            AccountScreen::NewCode => (
                METHOD_EXO_ACCOUNT_NEW_RECOVERY_CODE,
                json!({ "password": password }),
                true,
            ),
            _ => return,
        };
        self.wp4.flow = next_account_flow(self.wp4.flow, AccountFlowEvent::Submit);
        self.wp4.account_sheet_requests += 1;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(method, &params, move |res| {
                handle.update(move |s, _| {
                    s.wp4.account_sheet_requests = s.wp4.account_sheet_requests.saturating_sub(1);
                    match res {
                        Ok(outcome) => {
                            if !outcome.ok {
                                if outcome.offline {
                                    s.wp4.flow_offline_from = screen;
                                    s.wp4.flow = next_account_flow(
                                        s.wp4.flow,
                                        AccountFlowEvent::Answer(AccountAnswer::Offline),
                                    );
                                    return;
                                }
                                match outcome.error.as_deref() {
                                    Some("locked") => {
                                        let wait = outcome.retry_after_seconds.unwrap_or(60) as f64;
                                        s.wp4.flow_locked_until = Some(s.wp4_now_secs() + wait);
                                    }
                                    Some("bad-email") => {
                                        s.wp4.flow_email_error = Some(outcome.message);
                                    }
                                    Some("bad-password") | Some("common-password") => {
                                        s.wp4.flow_password_error = Some(outcome.message);
                                    }
                                    Some("email-taken") => {
                                        s.wp4.flow_taken_sign_in = true;
                                        s.wp4.flow_email_error = Some(outcome.message);
                                    }
                                    _ => s.wp4.flow_form_error = Some(outcome.message),
                                }
                                s.wp4.flow = next_account_flow(
                                    s.wp4.flow,
                                    AccountFlowEvent::Answer(AccountAnswer::Refused),
                                );
                            } else {
                                s.wp4.flow_password = TextEdit::new("").secure();
                                if expects_code {
                                    if let Some(rc) =
                                        outcome.recovery_code.filter(|c| !c.is_empty())
                                    {
                                        s.wp4.flow_code = Some(rc);
                                        s.wp4.account_recovery_typed = TextEdit::new("");
                                        s.wp4.account_recovery_note = None;
                                        s.wp4.account_recovery_failed = false;
                                        s.wp4.flow = next_account_flow(
                                            s.wp4.flow,
                                            AccountFlowEvent::Answer(AccountAnswer::Code),
                                        );
                                    } else {
                                        s.wp4.flow = next_account_flow(
                                            s.wp4.flow,
                                            AccountFlowEvent::Answer(AccountAnswer::NoCode),
                                        );
                                    }
                                } else {
                                    s.wp4.flow = next_account_flow(
                                        s.wp4.flow,
                                        AccountFlowEvent::Answer(AccountAnswer::SignedIn),
                                    );
                                    s.wp4_load_account_overview();
                                }
                            }
                        }
                        Err(_) => {
                            s.wp4.flow_form_error = Some(
                                "Exo's account service didn't answer. Try again in a moment."
                                    .into(),
                            );
                            s.wp4.flow = next_account_flow(
                                s.wp4.flow,
                                AccountFlowEvent::Answer(AccountAnswer::Refused),
                            );
                        }
                    }
                });
            });
            return;
        }
        let _ = (method, params, expects_code);
        self.wp4.account_sheet_requests = self.wp4.account_sheet_requests.saturating_sub(1);
        self.wp4.flow = next_account_flow(
            self.wp4.flow,
            AccountFlowEvent::Answer(AccountAnswer::Offline),
        );
    }

    pub fn wp4_flow_sign_out(&mut self) {
        if self.wp4.flow.busy {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountOutcome>(METHOD_EXO_ACCOUNT_SIGN_OUT, &json!({}), move |res| {
                handle.update(move |s, _| {
                    if res.is_ok() {
                        s.wp4.flow = next_account_flow(s.wp4.flow, AccountFlowEvent::SignedOut);
                        s.wp4_load_account_overview();
                    } else {
                        s.wp4.flow_form_error = Some(
                            "Exo's account service didn't answer. Try again in a moment.".into(),
                        );
                    }
                });
            });
            return;
        }
        self.wp4.flow = next_account_flow(self.wp4.flow, AccountFlowEvent::SignedOut);
    }

    pub fn wp4_continue_without_account(&mut self) {
        self.wp4.account_later_this_run = true;
        self.wp4.account_probe_next_at = Some(self.wp4_now_secs() + ACCOUNT_PROBE_INTERVAL_SECS);
    }

    pub fn wp4_probe_account_service(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ExoAccountProbe>(METHOD_EXO_ACCOUNT_PROBE, &json!({}), move |res| {
                if let Ok(probe) = res
                    && later_ends(&probe)
                {
                    handle.update(move |s, _| {
                        s.wp4.account_later_this_run = false;
                        s.wp4_load_account_overview();
                    });
                }
            });
        }
    }

    pub fn wp4_toggle_privacy_sent(&mut self) {
        self.wp4.privacy_viewing = !self.wp4.privacy_viewing;
        if self.wp4.privacy_viewing {
            self.wp4.privacy_payloads = None;
            self.wp4.privacy_failed = false;
            #[cfg(windows)]
            if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
                rpc.call::<_, TelemetrySentReply>(METHOD_TELEMETRY_SENT, &json!({}), move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) => s.wp4.privacy_payloads = Some(reply.payloads),
                        Err(_) => s.wp4.privacy_failed = true,
                    });
                });
                return;
            }
            self.wp4.privacy_payloads = Some(Vec::new());
        }
    }

    pub fn wp4_delete_telemetry_data(&mut self) {
        self.wp4.privacy_confirming = false;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_TELEMETRY_DELETE, &json!({}), move |res| {
                handle.update(move |s, _| match res {
                    Ok(done) if done.ok => {
                        s.wp4.privacy_viewing = false;
                        s.wp4.privacy_payloads = None;
                        s.wp4.privacy_message = Some(
                            "Your data was deleted. Exo made a new install ID for this PC.".into(),
                        );
                    }
                    Ok(done) => {
                        s.wp4.privacy_message = Some(
                            done.message
                                .unwrap_or_else(|| "Your data was not deleted. Try again.".into()),
                        );
                    }
                    Err(_) => {
                        s.wp4.privacy_message =
                            Some("Your data was not deleted. Try again.".into());
                    }
                });
            });
            return;
        }
        self.wp4.privacy_message =
            Some("Your data was deleted. Exo made a new install ID for this PC.".into());
    }

    pub fn wp4_open_report(&mut self) {
        self.palette_open = false;
        self.wp4.report_open = true;
        self.wp4.report_message = TextEdit::new("");
        self.wp4.report_attach_logs = true;
        self.wp4.report_contact = TextEdit::new("");
        self.wp4.report_excerpt = None;
        self.wp4.report_showing_excerpt = false;
        self.wp4.report_phase = ReportPhase::Editing;
        self.wp4.report_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ReportPreviewReply>(METHOD_REPORT_PREVIEW, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.report_excerpt = Some(match res {
                        Ok(r) if r.ok => r.excerpt.unwrap_or_default(),
                        _ => String::new(),
                    });
                });
            });
            return;
        }
        self.wp4.report_excerpt = Some(String::new());
    }

    pub fn wp4_close_report(&mut self) {
        self.wp4.report_open = false;
    }

    pub fn wp4_send_report(&mut self) {
        if self.wp4.report_phase != ReportPhase::Editing {
            return;
        }
        if self.wp4.report_attach_logs && self.wp4.report_excerpt.is_none() {
            return;
        }
        let Some(payload) = build_report_payload(
            &self.wp4.report_message.text,
            self.wp4.report_attach_logs,
            &self.wp4.report_contact.text,
            self.wp4.report_excerpt.as_deref(),
        ) else {
            return;
        };
        self.wp4.report_phase = ReportPhase::Sending;
        self.wp4.report_error = None;
        let mut body = Map::new();
        body.insert("message".into(), Value::String(payload.message));
        if let Some(ex) = payload.excerpt {
            body.insert("excerpt".into(), Value::String(ex));
        }
        if let Some(c) = payload.contact {
            body.insert("contact".into(), Value::String(c));
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ReportSendReply>(METHOD_REPORT_SEND, &Value::Object(body), move |res| {
                handle.update(move |s, _| match res {
                    Ok(reply) if reply.ok => {
                        s.wp4.report_phase = ReportPhase::Sent(report_sent_words(&reply));
                    }
                    Ok(reply) => {
                        s.wp4.report_phase = ReportPhase::Editing;
                        s.wp4.report_error = Some(reply.message.unwrap_or_else(|| {
                            "Couldn’t send the report. Try again, or use Open on GitHub.".into()
                        }));
                    }
                    Err(err) => {
                        let err = err.to_string();
                        s.wp4.report_phase = ReportPhase::Editing;
                        s.wp4.report_error = Some(if err.is_empty() {
                            "Couldn’t send the report. Try again, or use Open on GitHub.".into()
                        } else {
                            err
                        });
                    }
                });
            });
            return;
        }
        self.wp4.report_phase = ReportPhase::Sent("Sent".into());
    }

    pub fn wp4_load_web_prefs(&mut self) {
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, WebClientOptions>(METHOD_WEB_PREFERENCES_GET, &json!({}), move |res| {
                if let Ok(opts) = res {
                    handle.update(move |s, _| {
                        if let Some(ref svc) = opts.music_service {
                            s.media.music_service =
                                crate::media_state::MusicService::from_pref(Some(svc));
                        }
                        s.wp4.web_prefs = Some(opts);
                    });
                }
            });
        }
    }

    pub fn wp4_set_web_music_service(&mut self, service: &'static str) {
        if self.wp4.web_prefs_saving {
            return;
        }
        let previous = self.media.music_service;
        self.media.music_service = crate::media_state::MusicService::from_pref(Some(service));
        self.wp4.web_prefs_saving = true;
        self.wp4.web_prefs_error = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, WebClientOptions>(
                METHOD_WEB_PREFERENCES_SET,
                &json!({ "musicService": service }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.web_prefs_saving = false;
                        match res {
                            Ok(saved) => {
                                if let Some(ref svc) = saved.music_service {
                                    s.media.music_service =
                                        crate::media_state::MusicService::from_pref(Some(svc));
                                }
                                s.wp4.web_prefs = Some(saved);
                            }
                            Err(_) => {
                                s.media.music_service = previous;
                                s.wp4.web_prefs_error =
                                    Some("Exo couldn’t save the music service. Try again.".into());
                            }
                        }
                    });
                },
            );
            return;
        }
        self.wp4.web_prefs_saving = false;
    }

    pub fn wp4_set_web_notifications(&mut self, notifications: &'static str) {
        if self.wp4.web_prefs_saving {
            return;
        }
        self.wp4.web_prefs_saving = true;
        self.wp4.web_prefs_error = None;
        if let Some(ref mut prefs) = self.wp4.web_prefs {
            prefs.notifications = notifications.to_string();
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, WebClientOptions>(
                METHOD_WEB_PREFERENCES_SET,
                &json!({ "notifications": notifications }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.web_prefs_saving = false;
                        match res {
                            Ok(saved) => s.wp4.web_prefs = Some(saved),
                            Err(_) => {
                                s.wp4.web_prefs_error =
                                    Some("The change could not be saved. Try again.".into());
                            }
                        }
                    });
                },
            );
            return;
        }
        self.wp4.web_prefs_saving = false;
    }

    pub fn wp4_load_cleanup(&mut self) {
        self.wp4.junk_scanning = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let h1 = handle.clone();
            rpc.call::<_, ReplacedScanReply>(
                METHOD_REPLACED_SCAN,
                &json!({ "clean": true }),
                move |res| {
                    if let Ok(reply) = res {
                        h1.update(move |s, _| {
                            for app in &reply.apps {
                                if app.installed || app.leftovers > 0 || app.cleared.is_some() {
                                    s.wp4.replaced_listed.insert(app.id.clone());
                                }
                            }
                            s.wp4.replaced_apps = reply.apps;
                        });
                    }
                },
            );
            rpc.call::<_, JunkScanReply>(METHOD_JUNK_SCAN, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.junk_scanning = false;
                    if let Ok(reply) = res {
                        s.wp4.junk_items = reply.items;
                    }
                });
            });
            return;
        }
        self.wp4.junk_scanning = false;
    }

    pub fn wp4_remove_replaced_app(&mut self, id: String, name: String) {
        if self.wp4.replaced_busy.is_some() {
            return;
        }
        self.wp4.replaced_confirm = None;
        self.wp4.replaced_busy = Some(id.clone());
        self.wp4.replaced_errors.remove(&id);
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let app_id = id.clone();
            rpc.call::<_, ReplacedRemoveReply>(
                METHOD_REPLACED_REMOVE,
                &json!({ "id": id }),
                move |res| {
                    handle.update(move |s, _| {
                        s.wp4.replaced_busy = None;
                        match res {
                            Ok(reply) => {
                                s.wp4.replaced_apps = reply.apps;
                                if !reply.ok {
                                    s.wp4.replaced_errors.insert(
                                        app_id,
                                        reply.message.unwrap_or_else(|| {
                                            format!("{name} wasn't removed. Try again.")
                                        }),
                                    );
                                }
                            }
                            Err(err) => {
                                let err = err.to_string();
                                s.wp4.replaced_errors.insert(
                                    app_id,
                                    if err.is_empty() {
                                        format!("{name} wasn't removed. Try again.")
                                    } else {
                                        err
                                    },
                                );
                            }
                        }
                    });
                },
            );
            return;
        }
        self.wp4.replaced_busy = None;
    }

    pub fn wp4_clean_setup_junk(&mut self) {
        if self.wp4.junk_busy {
            return;
        }
        self.wp4.junk_confirm = false;
        self.wp4.junk_busy = true;
        self.wp4.junk_error = None;
        let ids: Vec<String> = self.wp4.junk_items.iter().map(|i| i.id.clone()).collect();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, JunkCleanReply>(METHOD_JUNK_CLEAN, &json!({ "ids": ids }), move |res| {
                handle.update(move |s, _| {
                    s.wp4.junk_busy = false;
                    match res {
                        Ok(reply) if reply.ok => {
                            let bytes = reply.bytes.unwrap_or(0);
                            if bytes > 0 {
                                s.wp4.junk_done =
                                    Some(format!("Cleared {}", format_bytes_short(bytes)));
                                s.wp4.junk_items.clear();
                            } else {
                                s.wp4.junk_error =
                                    Some(reply.message.unwrap_or_else(|| {
                                        "Nothing was deleted. Try again.".into()
                                    }));
                            }
                        }
                        Ok(reply) => {
                            s.wp4.junk_error = Some(
                                reply
                                    .message
                                    .unwrap_or_else(|| "Nothing was deleted. Try again.".into()),
                            );
                        }
                        Err(err) => s.wp4.junk_error = Some(err.to_string()),
                    }
                });
            });
            return;
        }
        let _ = ids;
        self.wp4.junk_busy = false;
    }

    pub fn wp4_restore_backup_offer(&mut self) {
        if self.wp4.restore_busy {
            return;
        }
        let Some(offer) = self.pc.backup.as_ref().and_then(|b| b.offer.clone()) else {
            return;
        };
        self.wp4.restore_busy = true;
        self.wp4.restore_message = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, BackupActionReply>(
                METHOD_BACKUP_RESTORE,
                &json!({ "day": offer.day, "device": offer.device }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(done) if done.ok => {
                            s.wp4.restore_message = Some(StoreNote {
                                text: "Restored. Exo is restarting to load your data.".into(),
                                error: false,
                            });
                        }
                        Ok(done) if done.restored => {
                            s.wp4.restore_held = true;
                            s.wp4.restore_message = Some(StoreNote {
                                text: done.message.unwrap_or_else(|| {
                                    "Restored. Close and reopen Exo to finish.".into()
                                }),
                                error: false,
                            });
                        }
                        Ok(done) => {
                            s.wp4.restore_busy = false;
                            s.wp4.restore_message = Some(StoreNote {
                                text: done.message.unwrap_or_else(|| {
                                    "Your data could not be restored. Start fresh, or try again."
                                        .into()
                                }),
                                error: true,
                            });
                        }
                        Err(_) => {
                            s.wp4.restore_busy = false;
                            s.wp4.restore_message = Some(StoreNote {
                                text: "Your data could not be restored. Start fresh, or try again."
                                    .into(),
                                error: true,
                            });
                        }
                    });
                },
            );
            return;
        }
        self.wp4.restore_busy = false;
    }

    pub fn wp4_decline_backup_offer(&mut self) {
        if let Some(ref mut b) = self.pc.backup {
            b.offer = None;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_BACKUP_DECLINE, &json!({}), |_| {});
        }
    }

    pub fn wp4_load_clients_status(&mut self, again: bool) {
        self.wp4.clients_checking = true;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ClientsStatus>(METHOD_CLIENTS_STATUS, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.clients_checking = false;
                    match res {
                        Ok(next) => {
                            s.pc.clients = Some(next);
                            s.wp4.clients_failed = false;
                            s.wp4.clients_failures = 0;
                        }
                        Err(_) => {
                            s.wp4.clients_failed = true;
                            s.wp4.clients_failures = if again {
                                s.wp4.clients_failures + 1
                            } else {
                                s.wp4.clients_failures.max(1)
                            };
                        }
                    }
                });
            });
            return;
        }
        let _ = again;
        self.wp4.clients_checking = false;
    }

    pub fn wp4_fix_clients(&mut self) {
        if self.wp4.clients_busy {
            return;
        }
        self.wp4.clients_busy = true;
        self.wp4.clients_note = None;
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ActionResult>(METHOD_CLIENTS_FIX, &json!({}), move |res| {
                handle.update(move |s, _| {
                    s.wp4.clients_busy = false;
                    match res {
                        Ok(reply) => {
                            if let Some(msg) = reply.message {
                                s.wp4.clients_note = Some(StoreNote {
                                    text: if reply.ok {
                                        msg
                                    } else if msg.is_empty() {
                                        "Exo couldn’t update your clients. Try again.".into()
                                    } else {
                                        msg
                                    },
                                    error: !reply.ok,
                                });
                            } else if !reply.ok {
                                s.wp4.clients_note = Some(StoreNote {
                                    text: "Exo couldn’t update your clients. Try again.".into(),
                                    error: true,
                                });
                            }
                            s.wp4_load_clients_status(false);
                        }
                        Err(err) => {
                            let err = err.to_string();
                            s.wp4.clients_note = Some(StoreNote {
                                text: if err.is_empty() {
                                    "Exo couldn’t update your clients. Try again.".into()
                                } else {
                                    err
                                },
                                error: true,
                            });
                        }
                    }
                });
            });
            return;
        }
        self.wp4.clients_busy = false;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_index_covers_all_sections_and_ranks_exact_prefixes() {
        assert!(SETTINGS_INDEX.len() >= 38);
        let hits = search_settings("obs");
        assert_eq!(hits[0].id, "ingame-obs");
        assert_eq!(hits[0].section, SettingsSection::Graphics);
        let steam = search_settings("steam offline");
        assert_eq!(steam[0].id, "library-steam-offline");
        let empty = search_settings("   ");
        assert!(empty.is_empty());
    }

    #[test]
    fn backup_day_label_formats_today_and_past_dates() {
        assert_eq!(backup_day_label("2026-11-23", "2026-11-23"), "Today");
        assert_eq!(backup_day_label("2026-11-08", "2026-11-23"), "Nov 8, 2026");
        assert_eq!(backup_day_label("invalid", "2026-11-23"), "invalid");
    }

    #[test]
    fn recovery_code_normalizes_ambiguous_chars_and_separators() {
        let code = "ABCD-EFGH-2345-6789-0101-WXYZ";
        assert!(recovery_code_confirmed(
            "abcd efgh 2345 6789 oioi wxyz",
            code
        ));
        assert!(!recovery_code_confirmed("short", code));
    }

    #[test]
    fn account_flow_locks_code_screen_until_confirm() {
        let s = AccountFlowState {
            screen: AccountScreen::Code,
            busy: false,
        };
        assert_eq!(
            next_account_flow(s, AccountFlowEvent::Open(AccountScreen::Choose)).screen,
            AccountScreen::Code
        );
        assert_eq!(
            next_account_flow(s, AccountFlowEvent::SignedOut).screen,
            AccountScreen::Code
        );
        assert_eq!(
            next_account_flow(s, AccountFlowEvent::Confirm).screen,
            AccountScreen::SignedIn
        );
    }

    #[test]
    fn account_sheet_gate_holds_while_code_pending_or_signed_out() {
        let out = ExoAccountOverview {
            signed_in: false,
            ..Default::default()
        };
        assert!(account_sheet_open(false, Some(&out), false, 0, 0));
        assert!(!account_sheet_open(false, Some(&out), true, 0, 0));

        let pending = ExoAccountOverview {
            signed_in: true,
            code_pending: true,
            ..Default::default()
        };
        assert!(account_sheet_open(false, Some(&pending), false, 0, 0));
        assert!(!account_sheet_open(false, Some(&pending), false, 1, 0));
        assert!(account_sheet_open(true, Some(&pending), false, 1, 0));
    }

    #[test]
    fn held_sentences_groups_by_type_and_reason() {
        let items = vec![
            ExoAccountHeldItem {
                key: "cover-image/1".into(),
                reason: "too-large".into(),
            },
            ExoAccountHeldItem {
                key: "cover-image/2".into(),
                reason: "too-large".into(),
            },
            ExoAccountHeldItem {
                key: "settings/main".into(),
                reason: "refused".into(),
            },
        ];
        let lines = held_sentences(&items);
        assert_eq!(
            lines,
            vec![
                "2 cover images are too large to sync. They stay on this PC.",
                "1 setting was not accepted by the service. It stays on this PC.",
            ]
        );
    }

    #[test]
    fn bug_report_scrubs_pii_and_builds_github_url() {
        let scrubbed = scrub_for_url(
            "Contact me at user@example.com about C:\\Users\\Alice\\file.txt and https://foo.bar/x id 1234567890",
        );
        assert!(scrubbed.contains("<email>"));
        assert!(scrubbed.contains("<path>"));
        assert!(scrubbed.contains("<url>"));
        assert!(scrubbed.contains("<id>"));
        let url = bug_report_url(Some("3.29.0"), "Crash on launch\nMore info");
        assert!(url.contains("template=bug_report.yml"));
        assert!(url.contains("version=3.29.0"));
        assert!(url.contains("title=Crash+on+launch"));
    }

    #[test]
    fn clients_copy_and_heading_match_react() {
        let mut st = ClientsStatus {
            ok: true,
            needs_fix: true,
            ..Default::default()
        };
        st.steam.channel = "stable".into();
        st.epic.layout = "old".into();
        assert_eq!(
            clients_heading(&st),
            "Exo needs the new Epic launcher and Steam Beta"
        );
        assert!(clients_fix_copy(&st).contains("Steam Beta"));
        assert!(clients_fix_copy(&st).contains("Epic's new launcher"));
    }

    #[test]
    fn leftover_summary_formats_english_list() {
        let sum = LeftoverSummary {
            folders: 2,
            files: 1,
            registry: 3,
            shortcuts: 0,
            bytes: 150_000_000,
        };
        assert_eq!(
            leftover_summary_words(&sum),
            "2 folders, 1 file (150 MB) and 3 registry entries"
        );
    }

    #[test]
    fn riot_id_and_baseline_helpers_validate_properly() {
        assert!(is_valid_riot_id("TenZ#NA1"));
        assert!(!is_valid_riot_id("TenZ"));
        assert!(!is_valid_riot_id("TenZ#1"));
        assert_eq!(format_baseline_hours(Some(120.0)), "2");
        assert_eq!(format_baseline_hours(Some(90.0)), "1.5");
        assert!(parse_hours_input("0").is_err());
        assert_eq!(parse_hours_input(""), Ok(None));
        assert_eq!(parse_hours_input("12.5"), Ok(Some(12.5)));
    }

    #[test]
    fn steam_guard_otp_state_normalizes_upper_alphanumeric_five_boxes() {
        let mut wp4 = SettingsUiState::default();
        assert_eq!(wp4.steam_guard.len(), 5);
        assert!(wp4.steam_guard.is_empty());
        wp4.steam_guard.paste_at(0, "a2-b9z");
        assert!(wp4.steam_guard.is_complete());
        assert_eq!(wp4.steam_guard.value(), "A2B9Z");
        wp4.steam_guard.clear();
        assert!(wp4.steam_guard.is_empty());
    }
}
