use crate::drivers::{MouseConfig, WritePersistence};
pub use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::time::{Duration, Instant};

pub const DEFAULT_DEBOUNCE_MS: u64 = 500;
pub const DEFAULT_MIN_SWITCH_INTERVAL_MS: u64 = 2_000;

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum MouseRemapMode {
    #[default]
    HardwareOnboard,
    OfflineSoftwareMacroOrRemap,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MouseRemapDecision {
    pub software_macros_allowed: bool,
    pub effective_mode: MouseRemapMode,
    pub blocked_reason: Option<String>,
}

pub fn resolve_mouse_remap_policy(
    requested: MouseRemapMode,
    ac_class: AntiCheatClass,
) -> MouseRemapDecision {
    match requested {
        MouseRemapMode::HardwareOnboard => MouseRemapDecision {
            software_macros_allowed: false,
            effective_mode: MouseRemapMode::HardwareOnboard,
            blocked_reason: None,
        },
        MouseRemapMode::OfflineSoftwareMacroOrRemap => {
            if ac_class.allows_software_macros() {
                MouseRemapDecision {
                    software_macros_allowed: true,
                    effective_mode: MouseRemapMode::OfflineSoftwareMacroOrRemap,
                    blocked_reason: None,
                }
            } else {
                MouseRemapDecision {
                    software_macros_allowed: false,
                    effective_mode: MouseRemapMode::HardwareOnboard,
                    blocked_reason: Some(
                        "Software macros and remaps are disabled for online and unknown games; hardware onboard bindings remain active."
                            .into(),
                    ),
                }
            }
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameProfileRule {
    pub id: String,
    pub game_id: String,
    pub game_name: String,
    pub profile_name: String,
    pub config: MouseConfig,
    #[serde(default)]
    pub anti_cheat_class: AntiCheatClass,
    #[serde(default)]
    pub remap_mode: MouseRemapMode,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum AutoSwitchDecision {
    PendingDebounce {
        remaining_ms: u64,
    },
    RateLimited {
        retry_after_ms: u64,
    },
    SkippedUnchanged,
    Apply {
        profile_name: String,
        game_id: Option<String>,
        config: MouseConfig,
        persistence: WritePersistence,
        remap_decision: MouseRemapDecision,
    },
}

#[derive(Clone, Debug)]
pub struct AutoProfileEngine {
    debounce: Duration,
    min_switch_interval: Duration,
    desktop_config: MouseConfig,
    rules: HashMap<String, GameProfileRule>,
    active_applied_config: Option<MouseConfig>,
    last_switch_at: Option<Instant>,
    pending_game_id: Option<String>,
    pending_since: Option<Instant>,
    pending_committed: bool,
}

impl AutoProfileEngine {
    pub fn new(desktop_config: MouseConfig, debounce_ms: u64) -> Self {
        Self {
            debounce: Duration::from_millis(debounce_ms),
            min_switch_interval: Duration::from_millis(DEFAULT_MIN_SWITCH_INTERVAL_MS),
            desktop_config,
            rules: HashMap::new(),
            active_applied_config: None,
            last_switch_at: None,
            pending_game_id: None,
            pending_since: None,
            pending_committed: true,
        }
    }

    pub fn with_min_switch_interval_ms(mut self, interval_ms: u64) -> Self {
        self.min_switch_interval = Duration::from_millis(interval_ms);
        self
    }

    pub fn debounce_ms(&self) -> u64 {
        self.debounce.as_millis() as u64
    }

    pub fn min_switch_interval_ms(&self) -> u64 {
        self.min_switch_interval.as_millis() as u64
    }

    pub fn set_desktop_config(&mut self, config: MouseConfig) {
        self.desktop_config = config;
    }

    pub fn desktop_config(&self) -> &MouseConfig {
        &self.desktop_config
    }

    pub fn set_applied_state(&mut self, config: MouseConfig) {
        self.active_applied_config = Some(config);
    }

    pub fn upsert_rule(&mut self, rule: GameProfileRule) {
        self.rules.insert(rule.game_id.clone(), rule);
    }

    pub fn remove_rule(&mut self, game_id: &str) -> bool {
        self.rules.remove(game_id).is_some()
    }

    pub fn rules(&self) -> Vec<GameProfileRule> {
        let mut out: Vec<GameProfileRule> = self.rules.values().cloned().collect();
        out.sort_by(|a, b| a.game_name.cmp(&b.game_name));
        out
    }

    pub fn on_foreground_changed(&mut self, game_id: Option<&str>, now: Instant) {
        let next = game_id.map(str::to_string);
        if self.pending_game_id != next || self.pending_since.is_none() {
            self.pending_game_id = next;
            self.pending_since = Some(now);
            self.pending_committed = false;
        }
    }

    pub fn poll(&mut self, now: Instant) -> AutoSwitchDecision {
        if self.pending_committed {
            return AutoSwitchDecision::SkippedUnchanged;
        }
        let Some(since) = self.pending_since else {
            return AutoSwitchDecision::SkippedUnchanged;
        };
        let elapsed = now.saturating_duration_since(since);
        if elapsed < self.debounce {
            return AutoSwitchDecision::PendingDebounce {
                remaining_ms: self.debounce.saturating_sub(elapsed).as_millis() as u64,
            };
        }

        let (profile_name, target_config, remap_decision) = match self
            .pending_game_id
            .as_deref()
            .and_then(|id| self.rules.get(id))
        {
            Some(rule) => (
                rule.profile_name.clone(),
                rule.config.clone(),
                resolve_mouse_remap_policy(rule.remap_mode, rule.anti_cheat_class),
            ),
            None => (
                "Desktop".into(),
                self.desktop_config.clone(),
                resolve_mouse_remap_policy(
                    MouseRemapMode::HardwareOnboard,
                    AntiCheatClass::OfflineAntiCheat,
                ),
            ),
        };

        if self.active_applied_config.as_ref() == Some(&target_config) {
            self.pending_committed = true;
            return AutoSwitchDecision::SkippedUnchanged;
        }

        if let Some(last) = self.last_switch_at {
            let since_switch = now.saturating_duration_since(last);
            if since_switch < self.min_switch_interval {
                return AutoSwitchDecision::RateLimited {
                    retry_after_ms: self
                        .min_switch_interval
                        .saturating_sub(since_switch)
                        .as_millis() as u64,
                };
            }
        }

        self.pending_committed = true;
        self.last_switch_at = Some(now);
        self.active_applied_config = Some(target_config.clone());
        AutoSwitchDecision::Apply {
            profile_name,
            game_id: self.pending_game_id.clone(),
            config: target_config,
            persistence: WritePersistence::VolatileRam,
            remap_decision,
        }
    }
}
