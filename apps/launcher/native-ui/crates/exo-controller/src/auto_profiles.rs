use crate::sony_hid::{DualSenseSettings, MotorStrength};
use serde::{Deserialize, Serialize};

pub const LOW_BATTERY_THRESHOLD_PCT: u8 = 15;
pub const LOW_BATTERY_AMBER_HEX: &str = "#FF9F1C";

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ControllerAntiCheatClass {
    CompetitivePvp,
    OnlinePveGacha,
    OfflineSinglePlayer,
    #[default]
    Unknown,
}

impl ControllerAntiCheatClass {
    pub fn allows_software_remap(self) -> bool {
        matches!(self, Self::OfflineSinglePlayer)
    }
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum TriggerPreset {
    #[default]
    Default,
    FpsHairTrigger,
    RacingProgressiveBrake,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ControllerRemapMode {
    #[default]
    HardwareOnboard,
    OfflineSoftwareGyroOrRemap,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct GameControllerProfile {
    pub id: String,
    pub name: String,
    pub game_ids: Vec<String>,
    pub edge_slot: Option<u8>,
    pub trigger_preset: TriggerPreset,
    pub lightbar: Option<String>,
    pub remap_mode: ControllerRemapMode,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ResolvedControllerAction {
    pub profile_id: String,
    pub edge_slot: Option<u8>,
    pub settings: DualSenseSettings,
    pub software_remap_active: bool,
    pub low_battery_warning: bool,
    pub blocked_reason: Option<String>,
}

pub fn resolve_controller_profile(
    profile: &GameControllerProfile,
    ac_class: ControllerAntiCheatClass,
    battery_pct: Option<u8>,
) -> ResolvedControllerAction {
    let low_battery = battery_pct.is_some_and(|pct| pct < LOW_BATTERY_THRESHOLD_PCT);
    let effective_light = if low_battery {
        Some(LOW_BATTERY_AMBER_HEX.to_string())
    } else {
        profile.lightbar.clone()
    };

    let trigger_effects = match profile.trigger_preset {
        TriggerPreset::Default => MotorStrength::Full,
        TriggerPreset::FpsHairTrigger => MotorStrength::Off,
        TriggerPreset::RacingProgressiveBrake => MotorStrength::Medium,
    };

    let (software_remap_active, blocked_reason) = match profile.remap_mode {
        ControllerRemapMode::HardwareOnboard => (false, None),
        ControllerRemapMode::OfflineSoftwareGyroOrRemap => {
            if ac_class.allows_software_remap() {
                (true, None)
            } else {
                (
                    false,
                    Some("Software virtual-controller / gyro-to-mouse remap is hard-disabled for online, competitive anti-cheat, and unknown games. Hardware onboard profiles remain active.".into()),
                )
            }
        }
    };

    ResolvedControllerAction {
        profile_id: profile.id.clone(),
        edge_slot: profile.edge_slot,
        settings: DualSenseSettings {
            light: effective_light,
            player_light: true,
            mic_off: false,
            speaker_off: false,
            vibration: MotorStrength::Full,
            trigger_effects,
        },
        software_remap_active,
        low_battery_warning: low_battery,
        blocked_reason,
    }
}
