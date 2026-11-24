use exo_rpc::anticheat::AntiCheatClass;
use exo_upscale::policy::{ModActionKind, PolicyContext, allows_action, evaluate};
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BridgeBackend {
    #[default]
    Auto,
    Fsr31Dx12,
    XessFgDx12,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BridgeConfig {
    pub schema_version: u32,
    pub game_id: String,
    pub game_title: String,
    pub anti_cheat_class: AntiCheatClass,
    pub has_anti_cheat: bool,
    pub ban_risk_confirmed: bool,
    pub official_offline_mode: bool,
    pub backend: BridgeBackend,
    pub enable_fake_nvapi_shim: bool,
    pub hudless_pass_through: bool,
    pub target_multiplier: u32,
    pub ffx_dll_path: Option<String>,
    pub xess_fg_dll_path: Option<String>,
}

impl Default for BridgeConfig {
    fn default() -> Self {
        Self {
            schema_version: 1,
            game_id: String::new(),
            game_title: String::new(),
            anti_cheat_class: AntiCheatClass::Unknown,
            has_anti_cheat: true,
            ban_risk_confirmed: false,
            official_offline_mode: false,
            backend: BridgeBackend::Auto,
            enable_fake_nvapi_shim: false,
            hudless_pass_through: true,
            target_multiplier: 2,
            ffx_dll_path: None,
            xess_fg_dll_path: None,
        }
    }
}

impl BridgeConfig {
    pub fn policy_context(&self) -> PolicyContext {
        PolicyContext {
            anti_cheat_class: self.anti_cheat_class,
            has_anti_cheat: self.has_anti_cheat,
            ban_risk_confirmed: self.ban_risk_confirmed,
            official_offline_mode: self.official_offline_mode,
        }
    }

    pub fn is_bridge_permitted(&self) -> bool {
        allows_action(self.policy_context(), ModActionKind::FgBridgeProxy)
    }

    pub fn denial_reason(&self) -> Option<String> {
        let decision = evaluate(self.policy_context());
        if decision.fg_bridge_allowed {
            None
        } else {
            Some(decision.reason)
        }
    }

    pub fn from_json_bytes(bytes: &[u8]) -> Result<Self, String> {
        serde_json::from_slice(bytes).map_err(|e| format!("parse bridge config: {e}"))
    }

    pub fn load_from_path(path: &Path) -> Result<Self, String> {
        let raw = std::fs::read(path).map_err(|e| format!("read bridge config: {e}"))?;
        Self::from_json_bytes(&raw)
    }

    pub fn to_json_pretty(&self) -> Result<String, String> {
        serde_json::to_string_pretty(self).map_err(|e| format!("serialize bridge config: {e}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_config_fails_closed_as_unknown() {
        let cfg = BridgeConfig::default();
        assert_eq!(cfg.anti_cheat_class, AntiCheatClass::Unknown);
        assert!(!cfg.is_bridge_permitted());
        assert!(cfg.denial_reason().is_some());
    }

    #[test]
    fn policy_enforces_shared_three_class_rules_across_all_classes() {
        let pvp = BridgeConfig {
            anti_cheat_class: AntiCheatClass::CompetitivePvpOnline,
            has_anti_cheat: true,
            ban_risk_confirmed: true,
            official_offline_mode: true,
            ..Default::default()
        };
        assert!(!pvp.is_bridge_permitted());

        let unknown = BridgeConfig {
            anti_cheat_class: AntiCheatClass::Unknown,
            has_anti_cheat: true,
            ban_risk_confirmed: true,
            official_offline_mode: true,
            ..Default::default()
        };
        assert!(!unknown.is_bridge_permitted());

        let pve_unconfirmed = BridgeConfig {
            anti_cheat_class: AntiCheatClass::OnlineNonCompetitivePve,
            has_anti_cheat: true,
            ban_risk_confirmed: false,
            ..Default::default()
        };
        assert!(!pve_unconfirmed.is_bridge_permitted());

        let pve_confirmed = BridgeConfig {
            ban_risk_confirmed: true,
            ..pve_unconfirmed
        };
        assert!(pve_confirmed.is_bridge_permitted());

        let offline_online = BridgeConfig {
            anti_cheat_class: AntiCheatClass::OfflineAntiCheat,
            has_anti_cheat: true,
            official_offline_mode: false,
            ..Default::default()
        };
        assert!(!offline_online.is_bridge_permitted());

        let offline_ok = BridgeConfig {
            official_offline_mode: true,
            ..offline_online
        };
        assert!(offline_ok.is_bridge_permitted());

        let no_ac = BridgeConfig {
            anti_cheat_class: AntiCheatClass::OfflineAntiCheat,
            has_anti_cheat: false,
            ..Default::default()
        };
        assert!(no_ac.is_bridge_permitted());
    }
}
