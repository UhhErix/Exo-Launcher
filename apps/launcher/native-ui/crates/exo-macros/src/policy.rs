use crate::model::{MacroAction, MacroDef, MacroDelivery, PlaybackMode};
pub use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

pub const OFFLINE_ONE_TIME_NOTE: &str = "Offline / single-player game: Tier 2 software macros are allowed. Online, competitive anti-cheat, and unknown games allow Tier 1 onboard hardware macros only.";
pub const HARDWARE_ONBOARD_NOTE: &str = "Tier 1 onboard device hardware macro: stored in peripheral memory, zero software hooks or injection.";

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameAntiCheatInput {
    pub game_id: String,
    pub title: String,
    #[serde(default)]
    pub class: AntiCheatClass,
    #[serde(default)]
    pub local_hint: Option<AntiCheatClass>,
    #[serde(default)]
    pub offline_note_acknowledged: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroGamePolicy {
    pub game_id: String,
    pub title: String,
    pub class: AntiCheatClass,
    pub effective_class: AntiCheatClass,
    pub software_macros_allowed: bool,
    pub onboard_hardware_allowed: bool,
    pub exo_internal_ipc_allowed: bool,
    pub requires_offline_note: bool,
    pub offline_note_acknowledged: bool,
    pub summary: String,
    pub hardware_label: String,
}

pub fn apply_stricter_hint(
    base: AntiCheatClass,
    local_hint: Option<AntiCheatClass>,
) -> AntiCheatClass {
    match local_hint {
        Some(hint) => base.strictest(hint),
        None => base,
    }
}

pub fn evaluate_game_policy(input: &GameAntiCheatInput) -> MacroGamePolicy {
    let resolved = apply_stricter_hint(input.class, input.local_hint);
    let effective = resolved.effective();
    let software_allowed = resolved.allows_software_macros();

    let label = match resolved {
        AntiCheatClass::CompetitivePvpOnline => "Competitive PvP / online anti-cheat",
        AntiCheatClass::OnlineNonCompetitivePve => "Online PvE / gacha anti-cheat",
        AntiCheatClass::Unknown => "Unknown anti-cheat class (treated as competitive online)",
        AntiCheatClass::OfflineAntiCheat => "Offline / single-player",
    };

    let summary = if software_allowed {
        OFFLINE_ONE_TIME_NOTE.to_string()
    } else {
        format!(
            "{label}: Tier 2 software macros are hard-disabled (no override). Only Tier 1 onboard hardware device macros and Exo internal IPC hotkeys are allowed."
        )
    };

    MacroGamePolicy {
        game_id: input.game_id.clone(),
        title: input.title.clone(),
        class: resolved,
        effective_class: effective,
        software_macros_allowed: software_allowed,
        onboard_hardware_allowed: true,
        exo_internal_ipc_allowed: true,
        requires_offline_note: software_allowed && !input.offline_note_acknowledged,
        offline_note_acknowledged: software_allowed && input.offline_note_acknowledged,
        summary,
        hardware_label: HARDWARE_ONBOARD_NOTE.to_string(),
    }
}

pub fn can_execute_binding(binding: &MacroDef, foreground_game: Option<&MacroGamePolicy>) -> bool {
    if !binding.enabled {
        return false;
    }
    if is_disallowed_cheat_primitive(binding).is_some() {
        return false;
    }
    match binding.delivery {
        MacroDelivery::OnboardHardware => true,
        MacroDelivery::ExoInternal => true,
        MacroDelivery::SoftwareSidecar => match foreground_game {
            None => true,
            Some(policy) => policy.software_macros_allowed,
        },
    }
}

pub fn is_disallowed_cheat_primitive(binding: &MacroDef) -> Option<&'static str> {
    if binding.delivery != MacroDelivery::SoftwareSidecar {
        return None;
    }
    let has_mouse_move = binding
        .steps
        .iter()
        .any(|s| matches!(s.action, MacroAction::MouseMove { dx, dy } if dx != 0 || dy != 0));
    if has_mouse_move
        && matches!(
            binding.trigger.mode,
            PlaybackMode::Hold | PlaybackMode::Repeat | PlaybackMode::Toggle
        )
    {
        return Some(
            "Repeating or held mouse-movement vectors (recoil compensation) are disallowed.",
        );
    }

    let has_click = binding.steps.iter().any(|s| {
        matches!(
            s.action,
            MacroAction::MouseDown { .. } | MacroAction::MouseUp { .. }
        )
    });
    if has_click
        && matches!(
            binding.trigger.mode,
            PlaybackMode::Hold | PlaybackMode::Repeat | PlaybackMode::Toggle
        )
    {
        let total_ms: f64 = binding.steps.iter().map(|s| s.delay_ms.max(0.0)).sum();
        if total_ms < 25.0 {
            return Some("Inhuman rapid-fire click loops (< 25 ms cycle) are disallowed.");
        }
    }
    None
}

pub struct AntiCheatFocusWatchdog {
    sidecar_paused: AtomicBool,
}

impl Default for AntiCheatFocusWatchdog {
    fn default() -> Self {
        Self::new()
    }
}

impl AntiCheatFocusWatchdog {
    pub fn new() -> Self {
        Self {
            sidecar_paused: AtomicBool::new(false),
        }
    }

    pub fn on_foreground_change(&self, foreground_game: Option<&MacroGamePolicy>) -> (bool, f64) {
        let start = Instant::now();
        let should_pause = foreground_game.is_some_and(|p| !p.software_macros_allowed);
        self.sidecar_paused.store(should_pause, Ordering::Release);
        let elapsed_us = start.elapsed().as_secs_f64() * 1_000_000.0;
        (should_pause, elapsed_us)
    }

    pub fn is_sidecar_paused(&self) -> bool {
        self.sidecar_paused.load(Ordering::Acquire)
    }
}
