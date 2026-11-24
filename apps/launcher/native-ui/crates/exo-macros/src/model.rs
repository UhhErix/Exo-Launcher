use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
pub enum DeviceKind {
    #[default]
    Keyboard,
    Mouse,
    Controller,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum MacroDelivery {
    #[default]
    OnboardHardware,
    SoftwareSidecar,
    ExoInternal,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum PlaybackMode {
    #[default]
    Once,
    Repeat,
    Hold,
    Toggle,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum LayerActivation {
    #[default]
    Base,
    Hold,
    Toggle,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "camelCase")]
pub struct InputSpec {
    pub device_kind: DeviceKind,
    #[serde(default)]
    pub device_id: Option<String>,
    pub code: String,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroTrigger {
    #[serde(default)]
    pub chord: Vec<InputSpec>,
    #[serde(default = "default_chord_window_ms")]
    pub chord_window_ms: u32,
    #[serde(default = "default_layer_id")]
    pub layer_id: String,
    #[serde(default)]
    pub mode: PlaybackMode,
    #[serde(default = "default_repeat_count")]
    pub repeat_count: u32,
    #[serde(default)]
    pub jitter_ms: u32,
    #[serde(default)]
    pub swallow_input: bool,
}

impl Default for MacroTrigger {
    fn default() -> Self {
        Self {
            chord: Vec::new(),
            chord_window_ms: default_chord_window_ms(),
            layer_id: default_layer_id(),
            mode: PlaybackMode::Once,
            repeat_count: 1,
            jitter_ms: 0,
            swallow_input: false,
        }
    }
}

fn default_chord_window_ms() -> u32 {
    45
}

fn default_layer_id() -> String {
    "base".to_string()
}

fn default_repeat_count() -> u32 {
    1
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum MacroAction {
    KeyDown { code: String },
    KeyUp { code: String },
    MouseDown { button: String },
    MouseUp { button: String },
    MouseMove { dx: i32, dy: i32 },
    MouseWheel { delta: i32 },
    PadButtonDown { button: String },
    PadButtonUp { button: String },
    PadAxis { axis: String, value: f32 },
    Delay,
    Text { text: String },
    LaunchApp { path: String },
    LayerSwitch { layer_id: String },
    ExoInternal { command: String },
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroStep {
    pub id: String,
    pub action: MacroAction,
    #[serde(default)]
    pub delay_ms: f64,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroDef {
    pub id: String,
    pub name: String,
    #[serde(default = "default_true")]
    pub enabled: bool,
    #[serde(default)]
    pub delivery: MacroDelivery,
    #[serde(default)]
    pub onboard_device_id: Option<String>,
    #[serde(default)]
    pub trigger: MacroTrigger,
    #[serde(default)]
    pub steps: Vec<MacroStep>,
}

fn default_true() -> bool {
    true
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MacroLayer {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub activation: LayerActivation,
    #[serde(default)]
    pub trigger: Option<InputSpec>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroProfile {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub is_default: bool,
    #[serde(default)]
    pub game_ids: Vec<String>,
    #[serde(default)]
    pub app_executables: Vec<String>,
    #[serde(default = "default_layer_id")]
    pub active_layer_id: String,
    #[serde(default)]
    pub layers: Vec<MacroLayer>,
    #[serde(default)]
    pub bindings: Vec<MacroDef>,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SmartTemplate {
    pub id: String,
    pub name: String,
    pub summary: String,
    pub delivery: MacroDelivery,
}

pub fn built_in_templates() -> Vec<(SmartTemplate, MacroDef)> {
    vec![
        (
            SmartTemplate {
                id: "tpl-mute-discord".into(),
                name: "Mute Discord on side button".into(),
                summary: "Internal Exo IPC toggle; zero synthetic input, safe in every game."
                    .into(),
                delivery: MacroDelivery::ExoInternal,
            },
            MacroDef {
                id: "tpl-mute-discord".into(),
                name: "Mute Discord on side button".into(),
                enabled: true,
                delivery: MacroDelivery::ExoInternal,
                onboard_device_id: None,
                trigger: MacroTrigger {
                    chord: vec![InputSpec {
                        device_kind: DeviceKind::Mouse,
                        device_id: None,
                        code: "Mouse5".into(),
                    }],
                    mode: PlaybackMode::Once,
                    swallow_input: false,
                    ..MacroTrigger::default()
                },
                steps: vec![MacroStep {
                    id: "s1".into(),
                    action: MacroAction::ExoInternal {
                        command: "discord.toggleMute".into(),
                    },
                    delay_ms: 0.0,
                }],
            },
        ),
        (
            SmartTemplate {
                id: "tpl-edge-replay".into(),
                name: "Save Instant Replay on Edge FnL + Create".into(),
                summary: "Controller chord routed via Exo Capture IPC; works during any match."
                    .into(),
                delivery: MacroDelivery::ExoInternal,
            },
            MacroDef {
                id: "tpl-edge-replay".into(),
                name: "Save Instant Replay on Edge FnL + Create".into(),
                enabled: true,
                delivery: MacroDelivery::ExoInternal,
                onboard_device_id: None,
                trigger: MacroTrigger {
                    chord: vec![
                        InputSpec {
                            device_kind: DeviceKind::Controller,
                            device_id: None,
                            code: "FnLeft".into(),
                        },
                        InputSpec {
                            device_kind: DeviceKind::Controller,
                            device_id: None,
                            code: "Create".into(),
                        },
                    ],
                    chord_window_ms: 50,
                    mode: PlaybackMode::Once,
                    swallow_input: false,
                    ..MacroTrigger::default()
                },
                steps: vec![MacroStep {
                    id: "s1".into(),
                    action: MacroAction::ExoInternal {
                        command: "capture.saveReplay".into(),
                    },
                    delay_ms: 0.0,
                }],
            },
        ),
        (
            SmartTemplate {
                id: "tpl-media-skip".into(),
                name: "Skip Music track on F13".into(),
                summary: "Routes to Exo Music IPC on F13 without global hooks.".into(),
                delivery: MacroDelivery::ExoInternal,
            },
            MacroDef {
                id: "tpl-media-skip".into(),
                name: "Skip Music track on F13".into(),
                enabled: true,
                delivery: MacroDelivery::ExoInternal,
                onboard_device_id: None,
                trigger: MacroTrigger {
                    chord: vec![InputSpec {
                        device_kind: DeviceKind::Keyboard,
                        device_id: None,
                        code: "F13".into(),
                    }],
                    mode: PlaybackMode::Once,
                    swallow_input: false,
                    ..MacroTrigger::default()
                },
                steps: vec![MacroStep {
                    id: "s1".into(),
                    action: MacroAction::ExoInternal {
                        command: "music.next".into(),
                    },
                    delay_ms: 0.0,
                }],
            },
        ),
        (
            SmartTemplate {
                id: "tpl-sp-autosprint".into(),
                name: "Single-player accessibility auto-sprint toggle".into(),
                summary: "Tier 2 software toggle; active strictly in offline single-player games."
                    .into(),
                delivery: MacroDelivery::SoftwareSidecar,
            },
            MacroDef {
                id: "tpl-sp-autosprint".into(),
                name: "Single-player accessibility auto-sprint toggle".into(),
                enabled: true,
                delivery: MacroDelivery::SoftwareSidecar,
                onboard_device_id: None,
                trigger: MacroTrigger {
                    chord: vec![InputSpec {
                        device_kind: DeviceKind::Keyboard,
                        device_id: None,
                        code: "CapsLock".into(),
                    }],
                    mode: PlaybackMode::Toggle,
                    swallow_input: false,
                    ..MacroTrigger::default()
                },
                steps: vec![MacroStep {
                    id: "s1".into(),
                    action: MacroAction::KeyDown {
                        code: "ShiftLeft".into(),
                    },
                    delay_ms: 0.0,
                }],
            },
        ),
    ]
}
