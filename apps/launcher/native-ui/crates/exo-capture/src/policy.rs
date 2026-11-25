pub use exo_rpc::anticheat::{AntiCheatClass, is_anti_cheat_process};
use windows::Win32::System::Diagnostics::Debug::{
    SEM_FAILCRITICALERRORS, SEM_NOGPFAULTERRORBOX, SetErrorMode,
};
use windows::Win32::System::ErrorReporting::{
    WER_FAULT_REPORTING, WER_FAULT_REPORTING_NO_UI, WerSetFlags,
};
use windows::Win32::System::SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX};
use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};

pub const CAPTURE_GPU_UNAVAILABLE_EXIT_CODE: i32 = 78;
pub const MIN_REPLAY_BUDGET_BYTES: u64 = 16 * 1024 * 1024;
pub const MAX_RAM_FRACTION_NUMERATOR: u64 = 15;
pub const MAX_RAM_FRACTION_DENOMINATOR: u64 = 100;

pub fn parse_anti_cheat_class(value: &str) -> AntiCheatClass {
    match value.trim().to_ascii_lowercase().as_str() {
        "competitivepvponline"
        | "competitive_pvp_online"
        | "competitivepvp"
        | "competitive"
        | "pvp" => AntiCheatClass::CompetitivePvpOnline,
        "onlinenoncompetitivepve"
        | "online_non_competitive_pve"
        | "onlinepve"
        | "pve"
        | "online" => AntiCheatClass::OnlineNonCompetitivePve,
        "offlineanticheat" | "offline_anti_cheat" | "offline" | "noanticheat" | "no_anti_cheat"
        | "none" => AntiCheatClass::OfflineAntiCheat,
        _ => AntiCheatClass::Unknown,
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HookDecision {
    pub allowed: bool,
    pub reason: &'static str,
}

pub fn evaluate_legacy_hook_policy(class: AntiCheatClass, user_opt_in: bool) -> HookDecision {
    if !user_opt_in {
        return HookDecision {
            allowed: false,
            reason: "legacy_hook_not_opted_in",
        };
    }
    if !class.allows_exo_flow() {
        return match class {
            AntiCheatClass::CompetitivePvpOnline => HookDecision {
                allowed: false,
                reason: "blocked_competitive_pvp_anticheat",
            },
            _ => HookDecision {
                allowed: false,
                reason: "blocked_unknown_anticheat_class",
            },
        };
    }
    match class.effective() {
        AntiCheatClass::OfflineAntiCheat => HookDecision {
            allowed: true,
            reason: "permitted_offline_or_no_anticheat",
        },
        AntiCheatClass::OnlineNonCompetitivePve => HookDecision {
            allowed: false,
            reason: "blocked_online_pve_anticheat",
        },
        AntiCheatClass::CompetitivePvpOnline | AntiCheatClass::Unknown => HookDecision {
            allowed: false,
            reason: "blocked_competitive_pvp_anticheat",
        },
    }
}

pub fn evaluate_legacy_hook_policy_for_process(
    class: AntiCheatClass,
    process_name: &str,
    user_opt_in: bool,
) -> HookDecision {
    if is_anti_cheat_process(process_name) {
        return HookDecision {
            allowed: false,
            reason: "blocked_known_anticheat_process",
        };
    }
    evaluate_legacy_hook_policy(class, user_opt_in)
}

pub fn effective_replay_budget_bytes(requested_bytes: u64, total_system_ram_bytes: u64) -> u64 {
    let ram_cap = total_system_ram_bytes.saturating_mul(MAX_RAM_FRACTION_NUMERATOR)
        / MAX_RAM_FRACTION_DENOMINATOR;
    if ram_cap == 0 {
        return 0;
    }
    let clamped_req = requested_bytes.max(MIN_REPLAY_BUDGET_BYTES);
    clamped_req.min(ram_cap)
}

pub fn system_total_ram_bytes() -> u64 {
    let mut mem = MEMORYSTATUSEX {
        dwLength: std::mem::size_of::<MEMORYSTATUSEX>() as u32,
        ..Default::default()
    };
    let ok = unsafe { GlobalMemoryStatusEx(&mut mem) }.is_ok();
    if ok {
        mem.ullTotalPhys
    } else {
        8 * 1024 * 1024 * 1024
    }
}

const SENSITIVE_PROCESS_NAMES: &[&str] = &[
    "consent.exe",
    "credentialuibroker.exe",
    "logonui.exe",
    "1password.exe",
    "bitwarden.exe",
    "keepass.exe",
    "keepassxc.exe",
    "dashlane.exe",
    "lastpass.exe",
];

const SENSITIVE_TITLE_SUBSTRINGS: &[&str] = &[
    "windows security",
    "user account control",
    "enter network credentials",
    "1password",
    "bitwarden",
    "keepass",
];

pub fn is_sensitive_window(process_name: &str, window_title: &str) -> bool {
    let proc_lower = process_name.trim().to_ascii_lowercase();
    if SENSITIVE_PROCESS_NAMES.iter().any(|p| proc_lower == *p) {
        return true;
    }
    let title_lower = window_title.trim().to_ascii_lowercase();
    SENSITIVE_TITLE_SUBSTRINGS
        .iter()
        .any(|needle| title_lower.contains(needle))
}

pub fn suppress_os_error_dialogs() {
    unsafe {
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
        let _ = WerSetFlags(WER_FAULT_REPORTING(WER_FAULT_REPORTING_NO_UI));
    }
}

pub fn enable_per_monitor_dpi_v2() {
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn legacy_hook_strictly_denies_online_unknown_and_shared_anticheat_processes() {
        assert!(!evaluate_legacy_hook_policy(AntiCheatClass::CompetitivePvpOnline, true).allowed);
        assert!(
            !evaluate_legacy_hook_policy(AntiCheatClass::OnlineNonCompetitivePve, true).allowed
        );
        assert!(!evaluate_legacy_hook_policy(AntiCheatClass::Unknown, true).allowed);
        assert!(!evaluate_legacy_hook_policy(AntiCheatClass::OfflineAntiCheat, false).allowed);
        assert!(evaluate_legacy_hook_policy(AntiCheatClass::OfflineAntiCheat, true).allowed);
        assert!(
            !evaluate_legacy_hook_policy_for_process(
                AntiCheatClass::OfflineAntiCheat,
                "vgc.exe",
                true
            )
            .allowed
        );
        assert!(
            !evaluate_legacy_hook_policy_for_process(
                AntiCheatClass::OfflineAntiCheat,
                "EasyAntiCheat_EOS.exe",
                true
            )
            .allowed
        );
        assert!(
            evaluate_legacy_hook_policy_for_process(
                AntiCheatClass::OfflineAntiCheat,
                "hades.exe",
                true
            )
            .allowed
        );
    }

    #[test]
    fn replay_budget_enforces_fifteen_percent_ram_cap() {
        let ram_16gb = 16 * 1024 * 1024 * 1024u64;
        let cap_15pct = (ram_16gb * 15) / 100;
        assert_eq!(
            effective_replay_budget_bytes(8 * 1024 * 1024 * 1024, ram_16gb),
            cap_15pct
        );
        assert_eq!(
            effective_replay_budget_bytes(512 * 1024 * 1024, ram_16gb),
            512 * 1024 * 1024
        );
        assert_eq!(
            effective_replay_budget_bytes(1024, ram_16gb),
            MIN_REPLAY_BUDGET_BYTES
        );
        assert_eq!(effective_replay_budget_bytes(512 * 1024 * 1024, 0), 0);
    }

    #[test]
    fn sensitive_window_detector_flags_uac_and_password_managers() {
        assert!(is_sensitive_window("consent.exe", ""));
        assert!(is_sensitive_window("1Password.exe", "Vault"));
        assert!(is_sensitive_window("explorer.exe", "Windows Security"));
        assert!(!is_sensitive_window("eldenring.exe", "ELDEN RING"));
    }

    #[test]
    fn anticheat_class_parser_defaults_unknown_safely() {
        assert_eq!(
            parse_anti_cheat_class("CompetitivePvpOnline"),
            AntiCheatClass::CompetitivePvpOnline
        );
        assert_eq!(
            parse_anti_cheat_class("NoAntiCheat"),
            AntiCheatClass::OfflineAntiCheat
        );
        assert_eq!(
            parse_anti_cheat_class("OfflineAntiCheat"),
            AntiCheatClass::OfflineAntiCheat
        );
        assert_eq!(parse_anti_cheat_class("garbage"), AntiCheatClass::Unknown);
    }
}
