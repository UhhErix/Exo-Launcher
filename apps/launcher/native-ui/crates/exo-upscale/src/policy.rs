use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ModActionKind {
    DriverDrsOverride,
    SignedUpscalerSwap,
    FgBridgeProxy,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PolicyContext {
    pub anti_cheat_class: AntiCheatClass,
    pub has_anti_cheat: bool,
    pub ban_risk_confirmed: bool,
    pub official_offline_mode: bool,
}

impl PolicyContext {
    pub const fn no_anti_cheat() -> Self {
        Self {
            anti_cheat_class: AntiCheatClass::OfflineAntiCheat,
            has_anti_cheat: false,
            ban_risk_confirmed: false,
            official_offline_mode: false,
        }
    }

    pub const fn offline_anti_cheat(official_offline_mode: bool) -> Self {
        Self {
            anti_cheat_class: AntiCheatClass::OfflineAntiCheat,
            has_anti_cheat: true,
            ban_risk_confirmed: false,
            official_offline_mode,
        }
    }

    pub const fn online_pve(ban_risk_confirmed: bool) -> Self {
        Self {
            anti_cheat_class: AntiCheatClass::OnlineNonCompetitivePve,
            has_anti_cheat: true,
            ban_risk_confirmed,
            official_offline_mode: false,
        }
    }

    pub const fn competitive_pvp() -> Self {
        Self {
            anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
            has_anti_cheat: true,
            ban_risk_confirmed: false,
            official_offline_mode: false,
        }
    }

    pub const fn unknown() -> Self {
        Self {
            anti_cheat_class: AntiCheatClass::Unknown,
            has_anti_cheat: true,
            ban_risk_confirmed: false,
            official_offline_mode: false,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpscalePolicyDecision {
    pub class: AntiCheatClass,
    pub effective_class: AntiCheatClass,
    pub drs_override_allowed: bool,
    pub signed_dll_swap_allowed: bool,
    pub fg_bridge_allowed: bool,
    pub requires_ban_risk_confirmation: bool,
    pub requires_offline_note: bool,
    pub reason: String,
}

pub fn evaluate(ctx: PolicyContext) -> UpscalePolicyDecision {
    let effective = ctx.anti_cheat_class.effective();
    match effective {
        AntiCheatClass::CompetitivePvpOnline | AntiCheatClass::Unknown => UpscalePolicyDecision {
            class: ctx.anti_cheat_class,
            effective_class: AntiCheatClass::CompetitivePvpOnline,
            drs_override_allowed: true,
            signed_dll_swap_allowed: false,
            fg_bridge_allowed: false,
            requires_ban_risk_confirmation: false,
            requires_offline_note: false,
            reason: if ctx.anti_cheat_class == AntiCheatClass::Unknown {
                "Unknown anti-cheat class fails closed: no DLL swap or proxy allowed; driver-level DRS override remains safe.".into()
            } else {
                "Competitive PvP online game: no DLL swap or proxy allowed; driver-level DRS override remains safe.".into()
            },
        },
        AntiCheatClass::OnlineNonCompetitivePve => UpscalePolicyDecision {
            class: ctx.anti_cheat_class,
            effective_class: effective,
            drs_override_allowed: true,
            signed_dll_swap_allowed: ctx.ban_risk_confirmed,
            fg_bridge_allowed: ctx.ban_risk_confirmed,
            requires_ban_risk_confirmation: true,
            requires_offline_note: false,
            reason: if ctx.ban_risk_confirmed {
                "Online PvE game: config/DRS active and opt-in signed DLL/bridge enabled after explicit ban-risk confirmation.".into()
            } else {
                "Online PvE game: config/DRS active by default; signed DLL swap and FG bridge require explicit BanRiskConfirmation.".into()
            },
        },
        AntiCheatClass::OfflineAntiCheat => {
            if !ctx.has_anti_cheat {
                UpscalePolicyDecision {
                    class: ctx.anti_cheat_class,
                    effective_class: effective,
                    drs_override_allowed: true,
                    signed_dll_swap_allowed: true,
                    fg_bridge_allowed: true,
                    requires_ban_risk_confirmation: false,
                    requires_offline_note: false,
                    reason:
                        "No anti-cheat: DRS override, signed DLL swap, and FG bridge are allowed."
                            .into(),
                }
            } else {
                UpscalePolicyDecision {
                    class: ctx.anti_cheat_class,
                    effective_class: effective,
                    drs_override_allowed: true,
                    signed_dll_swap_allowed: ctx.official_offline_mode,
                    fg_bridge_allowed: ctx.official_offline_mode,
                    requires_ban_risk_confirmation: false,
                    requires_offline_note: true,
                    reason: if ctx.official_offline_mode {
                        "Offline anti-cheat game in official offline mode: allowed with OfflineOneTimeNote.".into()
                    } else {
                        "Offline anti-cheat game requires official offline mode before placing DLLs; DRS override remains safe.".into()
                    },
                }
            }
        }
    }
}

pub fn allows_action(ctx: PolicyContext, action: ModActionKind) -> bool {
    let d = evaluate(ctx);
    match action {
        ModActionKind::DriverDrsOverride => d.drs_override_allowed,
        ModActionKind::SignedUpscalerSwap => d.signed_dll_swap_allowed,
        ModActionKind::FgBridgeProxy => d.fg_bridge_allowed,
    }
}

pub fn ban_risk_confirmation(game_title: &str, anti_cheat_name: Option<&str>) -> String {
    let title = if game_title.trim().is_empty() {
        "This game"
    } else {
        game_title.trim()
    };
    let ac = anti_cheat_name
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or("anti-cheat");
    format!("{title} runs {ac}. A mod loading into it can get the account banned.")
}

pub fn offline_one_time_note(game_title: &str) -> String {
    let title = if game_title.trim().is_empty() {
        "This game"
    } else {
        game_title.trim()
    };
    format!("{title} runs in offline mode with anti-cheat. Mods load only in offline play.")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn competitive_pvp_and_unknown_block_all_dll_swaps_and_proxies_but_allow_drs() {
        for ctx in [
            PolicyContext::competitive_pvp(),
            PolicyContext {
                ban_risk_confirmed: true,
                official_offline_mode: true,
                ..PolicyContext::competitive_pvp()
            },
            PolicyContext::unknown(),
            PolicyContext {
                ban_risk_confirmed: true,
                official_offline_mode: true,
                ..PolicyContext::unknown()
            },
        ] {
            let d = evaluate(ctx);
            assert_eq!(d.effective_class, AntiCheatClass::CompetitivePvpOnline);
            assert!(d.drs_override_allowed);
            assert!(!d.signed_dll_swap_allowed);
            assert!(!d.fg_bridge_allowed);
            assert!(!allows_action(ctx, ModActionKind::SignedUpscalerSwap));
            assert!(!allows_action(ctx, ModActionKind::FgBridgeProxy));
            assert!(allows_action(ctx, ModActionKind::DriverDrsOverride));
        }
    }

    #[test]
    fn online_pve_allows_drs_by_default_and_dll_or_bridge_only_with_ban_risk_confirmation() {
        let unconfirmed = PolicyContext::online_pve(false);
        let d0 = evaluate(unconfirmed);
        assert!(d0.drs_override_allowed);
        assert!(d0.requires_ban_risk_confirmation);
        assert!(!d0.signed_dll_swap_allowed);
        assert!(!d0.fg_bridge_allowed);

        let confirmed = PolicyContext::online_pve(true);
        let d1 = evaluate(confirmed);
        assert!(d1.drs_override_allowed);
        assert!(d1.requires_ban_risk_confirmation);
        assert!(d1.signed_dll_swap_allowed);
        assert!(d1.fg_bridge_allowed);
    }

    #[test]
    fn offline_anti_cheat_requires_official_offline_mode_and_carries_note() {
        let online_mode = PolicyContext::offline_anti_cheat(false);
        let d0 = evaluate(online_mode);
        assert!(d0.drs_override_allowed);
        assert!(d0.requires_offline_note);
        assert!(!d0.signed_dll_swap_allowed);
        assert!(!d0.fg_bridge_allowed);

        let offline_mode = PolicyContext::offline_anti_cheat(true);
        let d1 = evaluate(offline_mode);
        assert!(d1.drs_override_allowed);
        assert!(d1.requires_offline_note);
        assert!(d1.signed_dll_swap_allowed);
        assert!(d1.fg_bridge_allowed);
        assert!(!d1.requires_ban_risk_confirmation);
    }

    #[test]
    fn no_anti_cheat_allows_all_modes_without_warnings() {
        let clean = PolicyContext::no_anti_cheat();
        let d = evaluate(clean);
        assert!(d.drs_override_allowed);
        assert!(d.signed_dll_swap_allowed);
        assert!(d.fg_bridge_allowed);
        assert!(!d.requires_ban_risk_confirmation);
        assert!(!d.requires_offline_note);
    }

    #[test]
    fn confirmation_and_offline_notes_match_host_copy() {
        assert_eq!(
            ban_risk_confirmation("Wuthering Waves", Some("Anti-Cheat Expert")),
            "Wuthering Waves runs Anti-Cheat Expert. A mod loading into it can get the account banned."
        );
        assert_eq!(
            offline_one_time_note("ELDEN RING"),
            "ELDEN RING runs in offline mode with anti-cheat. Mods load only in offline play."
        );
    }
}
