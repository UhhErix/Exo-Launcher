use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroInputSpec {
    #[serde(default)]
    pub device_kind: String,
    #[serde(default)]
    pub device_id: Option<String>,
    #[serde(default)]
    pub code: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroTrigger {
    #[serde(default)]
    pub chord: Vec<MacroInputSpec>,
    #[serde(default)]
    pub chord_window_ms: i32,
    #[serde(default)]
    pub layer_id: String,
    #[serde(default)]
    pub mode: String,
    #[serde(default)]
    pub repeat_count: i32,
    #[serde(default)]
    pub jitter_ms: i32,
    #[serde(default)]
    pub swallow_input: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroStep {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub code: Option<String>,
    #[serde(default)]
    pub dx: Option<i32>,
    #[serde(default)]
    pub dy: Option<i32>,
    #[serde(default)]
    pub value: Option<f64>,
    #[serde(default)]
    pub delay_ms: f64,
    #[serde(default)]
    pub text: Option<String>,
    #[serde(default)]
    pub app_path: Option<String>,
    #[serde(default)]
    pub layer_id: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroBinding {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub delivery: String,
    #[serde(default)]
    pub onboard_device_id: Option<String>,
    #[serde(default)]
    pub trigger: MacroTrigger,
    #[serde(default)]
    pub steps: Vec<MacroStep>,
    #[serde(default)]
    pub updated_at: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroLayer {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub activation: String,
    #[serde(default)]
    pub trigger: Option<MacroInputSpec>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroProfile {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub is_default: bool,
    #[serde(default)]
    pub game_ids: Vec<String>,
    #[serde(default)]
    pub app_executables: Vec<String>,
    #[serde(default)]
    pub active_layer_id: String,
    #[serde(default)]
    pub layers: Vec<MacroLayer>,
    #[serde(default)]
    pub bindings: Vec<MacroBinding>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroGamePolicy {
    #[serde(default)]
    pub game_id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub anti_cheat_kind: String,
    #[serde(default)]
    pub anti_cheat_names: Vec<String>,
    #[serde(default)]
    pub anti_cheat_label: Option<String>,
    #[serde(default)]
    pub software_allowed: bool,
    #[serde(default)]
    pub allowed_tier: String,
    #[serde(default)]
    pub requires_confirmation: bool,
    #[serde(default)]
    pub confirmed: bool,
    #[serde(default)]
    pub auto_enable_allowed: bool,
    #[serde(default)]
    pub software_warning: Option<String>,
    #[serde(default)]
    pub onboard_note: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroDeviceCapability {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    #[serde(default)]
    pub kind: String,
    #[serde(default)]
    pub supports_onboard: bool,
    #[serde(default)]
    pub protocol: Option<String>,
    #[serde(default)]
    pub connection: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroLatencySummary {
    #[serde(default)]
    pub samples: i32,
    #[serde(default)]
    pub p50_us: f64,
    #[serde(default)]
    pub p95_us: f64,
    #[serde(default)]
    pub p99_us: f64,
    #[serde(default)]
    pub max_us: f64,
    #[serde(default)]
    pub target_us: f64,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroStateSnapshot {
    #[serde(default)]
    pub ok: bool,
    #[serde(default)]
    pub enabled: bool,
    #[serde(default)]
    pub active_profile_id: String,
    #[serde(default)]
    pub active_game_id: Option<String>,
    #[serde(default)]
    pub software_paused_for_anti_cheat: bool,
    #[serde(default)]
    pub pause_reason: Option<String>,
    #[serde(default)]
    pub recording: bool,
    #[serde(default)]
    pub recording_macro_id: Option<String>,
    #[serde(default)]
    pub debounce_ms: i32,
    #[serde(default)]
    pub exit_grace_ms: i32,
    #[serde(default)]
    pub hardware_write_cooldown_ms: i32,
    #[serde(default)]
    pub profiles: Vec<MacroProfile>,
    #[serde(default)]
    pub devices: Vec<MacroDeviceCapability>,
    #[serde(default)]
    pub game_policies: Vec<MacroGamePolicy>,
    #[serde(default)]
    pub latency: MacroLatencySummary,
}
