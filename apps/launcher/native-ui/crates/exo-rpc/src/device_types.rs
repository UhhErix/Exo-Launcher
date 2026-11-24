use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AudioFormatInfo {
    pub rate: u32,
    pub bits: u32,
    pub channels: u32,
}

impl AudioFormatInfo {
    pub fn display_words(&self) -> String {
        let ch = match self.channels {
            1 => "Mono".to_string(),
            2 => "Stereo".to_string(),
            6 => "5.1".to_string(),
            8 => "7.1".to_string(),
            n => format!("{n} channels"),
        };
        let khz = self.rate as f64 / 1000.0;
        let khz_str = if (khz.fract()).abs() < 1e-6 {
            format!("{}", khz as u32)
        } else {
            format!("{khz:.1}")
        };
        format!("{ch} · {khz_str} kHz · {}-bit", self.bits)
    }

    pub fn rate_khz(&self) -> String {
        let khz = self.rate as f64 / 1000.0;
        if (khz.fract()).abs() < 1e-6 {
            format!("{} kHz", khz as u32)
        } else {
            format!("{khz:.1} kHz")
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AudioFormatOption {
    pub key: String,
    pub rate: u32,
    pub bits: u32,
    pub container: u32,
    pub channels: u32,
}

impl AudioFormatOption {
    pub fn as_info(&self) -> AudioFormatInfo {
        AudioFormatInfo {
            rate: self.rate,
            bits: self.bits,
            channels: self.channels,
        }
    }

    pub fn display_words(&self) -> String {
        self.as_info().display_words()
    }
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AudioFormatsReply {
    pub ok: bool,
    pub current: Option<String>,
    pub formats: Vec<AudioFormatOption>,
    pub message: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AudioEndpoint {
    pub volume: Option<i32>,
    pub muted: Option<bool>,
    pub format: Option<AudioFormatInfo>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AudioVoiceInfo {
    pub custom: bool,
    pub noise: String,
    pub push_to_talk: bool,
    pub sensitivity: Option<f64>,
    pub focus: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AudioReadReply {
    pub ok: bool,
    pub output: Option<AudioEndpoint>,
    pub mic: Option<AudioEndpoint>,
    pub voice: AudioVoiceInfo,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqHeadphone {
    pub id: String,
    pub name: String,
    pub source: String,
    pub form: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqApoState {
    pub installed: bool,
    pub installing: bool,
    pub version: Option<String>,
    pub on_output: bool,
    pub restart: bool,
    pub effects_off: bool,
    pub outputs: i32,
    pub installer_version: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqOutputInfo {
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqCustomBand {
    pub hz: f64,
    pub gain: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqPlayingInfo {
    pub id: String,
    pub title: String,
    pub sound: Option<String>,
    pub label: Option<String>,
    pub reason: Option<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqGameSound {
    pub id: String,
    pub title: String,
    pub sound: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqLibraryGame {
    pub id: String,
    pub title: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqNightState {
    pub on: bool,
    pub start: String,
    pub end: String,
    pub sync: bool,
    pub now: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqOtherOutput {
    pub name: String,
    pub headphone: Option<String>,
    pub sound: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqStateReply {
    pub ok: bool,
    pub message: Option<String>,
    pub apo: EqApoState,
    pub on: bool,
    pub comparing: bool,
    pub output: Option<EqOutputInfo>,
    pub headphone: Option<EqHeadphone>,
    pub sound: String,
    pub custom: Vec<EqCustomBand>,
    pub preamp: f64,
    pub playing: Option<EqPlayingInfo>,
    pub games: Vec<EqGameSound>,
    pub library: Vec<EqLibraryGame>,
    pub night: EqNightState,
    pub others: Vec<EqOtherOutput>,
    pub guess: Option<EqHeadphone>,
    pub lucid_plays: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EqHeadphonesReply {
    pub ok: bool,
    pub headphones: Vec<EqHeadphone>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerLayerTool {
    pub name: String,
    pub running: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerLayers {
    pub steam_play_station: Option<String>,
    pub tools: Vec<ControllerLayerTool>,
    pub polling_driver: bool,
    pub memory_integrity: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerFirmware {
    pub version: String,
    pub built: String,
    pub latest: Option<String>,
    pub current: Option<bool>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerCalibration {
    pub calibrated: bool,
    pub kept: bool,
    pub can_undo: bool,
    pub holds: Option<bool>,
    pub put_back: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PadSettings {
    pub light: Option<String>,
    pub player_light: bool,
    pub mic_off: bool,
    pub speaker_off: bool,
    pub vibration: String,
    pub trigger_effects: String,
}

impl Default for PadSettings {
    fn default() -> Self {
        Self {
            light: None,
            player_light: false,
            mic_off: false,
            speaker_off: false,
            vibration: "full".into(),
            trigger_effects: "full".into(),
        }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PadOverrides {
    pub light: Option<String>,
    pub player_light: Option<bool>,
    pub mic_off: Option<bool>,
    pub speaker_off: Option<bool>,
    pub vibration: Option<String>,
    pub trigger_effects: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerGameProfile {
    pub id: String,
    pub name: String,
    pub settings: PadOverrides,
    pub games: Vec<String>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerSlot {
    pub number: i32,
    pub empty: bool,
    pub name: Option<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StickRest {
    pub offset_x: f64,
    pub offset_y: f64,
    pub noise: i32,
    pub creep: Option<f64>,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct StickEdge {
    pub reach: f64,
    pub circularity: f64,
    pub covered: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerTested {
    pub at: String,
    pub rest_left: Option<StickRest>,
    pub rest_right: Option<StickRest>,
    pub edge_left: Option<StickEdge>,
    pub edge_right: Option<StickEdge>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerReadReply {
    pub ok: bool,
    pub message: Option<String>,
    pub link: Option<String>,
    pub edge: Option<bool>,
    pub firmware: Option<ControllerFirmware>,
    pub layers: Option<ControllerLayers>,
    pub calibration: Option<ControllerCalibration>,
    pub settings: Option<PadSettings>,
    pub sound: Option<bool>,
    pub remembered: Option<bool>,
    pub slots: Option<Vec<ControllerSlot>>,
    pub tested: Option<ControllerTested>,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerPolling {
    pub rate: f64,
    pub fresh: Option<f64>,
    pub p99: f64,
    pub jitter: f64,
    pub repeats: Option<f64>,
    pub reports: u32,
    pub batched: f64,
    pub changes: bool,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerMeasureReply {
    pub ok: bool,
    pub message: Option<String>,
    pub polling: Option<ControllerPolling>,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct TriggerSweep {
    pub low: f64,
    pub high: f64,
    pub steps: i32,
    pub full: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ButtonBounce {
    pub name: String,
    pub presses: i32,
    pub bounces: i32,
    pub shortest: Option<f64>,
}

#[derive(Debug, Clone, Default, PartialEq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ButtonCheck {
    pub buttons: Vec<ButtonBounce>,
    pub report_ms: f64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerQualityReply {
    pub ok: bool,
    pub message: Option<String>,
    pub left: Option<TriggerSweep>,
    pub right: Option<TriggerSweep>,
    pub buttons: Option<ButtonCheck>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerTestRestReply {
    pub ok: bool,
    pub message: Option<String>,
    pub left: Option<StickRest>,
    pub right: Option<StickRest>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerTestEdgeReply {
    pub ok: bool,
    pub message: Option<String>,
    pub left: Option<StickEdge>,
    pub right: Option<StickEdge>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerCalibrateReply {
    pub ok: bool,
    pub message: Option<String>,
    pub more: Option<bool>,
    pub left: Option<f64>,
    pub right: Option<f64>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerSettingsReply {
    pub ok: bool,
    pub message: Option<String>,
    pub settings: Option<PadSettings>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerSoundReply {
    pub ok: bool,
    pub on: bool,
    pub changed: i32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerGameProfilesReply {
    pub ok: bool,
    pub message: Option<String>,
    pub profiles: Option<Vec<ControllerGameProfile>>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ControllerStateEvent {
    pub id: String,
    pub lx: u8,
    pub ly: u8,
    pub rx: u8,
    pub ry: u8,
    pub l2: u8,
    pub r2: u8,
    pub profile: Option<i32>,
    pub stop_l: Option<i32>,
    pub stop_r: Option<i32>,
    pub battery: Option<i32>,
    pub charging: bool,
    pub extras: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SteamInputStateReply {
    pub ok: bool,
    pub steam: bool,
    pub native: i32,
    pub translated: i32,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HidInputEvent {
    pub device: String,
    pub report_id: i32,
    pub data: String,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HidCollectionInfo {
    pub usage_page: u32,
    pub usage: u32,
    pub input_ids: Vec<u32>,
    pub output_ids: Vec<u32>,
    pub feature_ids: Vec<u32>,
    pub input_length: u32,
    pub output_length: u32,
    pub feature_length: u32,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HidDeviceInfo {
    pub key: String,
    pub vendor_id: u32,
    pub product_id: u32,
    pub product_name: String,
    pub collections: Vec<HidCollectionInfo>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HidReply {
    pub ok: bool,
    pub message: Option<String>,
    pub data: Option<String>,
    pub devices: Option<Vec<HidDeviceInfo>>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MouseDataReply {
    pub ok: bool,
    pub message: Option<String>,
    pub data: Option<Value>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct PickFolderReply {
    pub ok: bool,
    pub cancelled: bool,
    pub path: Option<String>,
    pub message: Option<String>,
}
