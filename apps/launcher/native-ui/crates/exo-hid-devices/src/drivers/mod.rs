pub mod finalmouse;
pub mod logitech_hidpp;
pub mod oem_nordic;
pub mod razer;
pub mod wooting;

use crate::registry::{DeviceCapabilities, DriverFamily};
use crate::transport::{HidError, HidTransport};
use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum LodLevel {
    Low1Mm,
    High2Mm,
}

impl LodLevel {
    pub fn label(self) -> &'static str {
        match self {
            Self::Low1Mm => "1 mm",
            Self::High2Mm => "2 mm",
        }
    }
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ClickMode {
    Standard,
    IntentTrigger,
    UltraClick,
}

impl ClickMode {
    pub fn label(self) -> &'static str {
        match self {
            Self::Standard => "Standard",
            Self::IntentTrigger => "Intent Trigger",
            Self::UltraClick => "Ultra Click",
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DpiStage {
    pub dpi: u32,
    pub color: String,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MouseConfig {
    pub active_profile: u8,
    pub active_dpi_stage: u8,
    pub dpi_stages: Vec<DpiStage>,
    pub polling_hz: u16,
    pub lod: LodLevel,
    pub motion_sync: bool,
    pub angle_snapping: bool,
    pub ripple_control: bool,
    pub debounce_ms: u8,
    pub click_mode_left: ClickMode,
    pub click_mode_right: ClickMode,
    pub sensor_rotation_deg: i8,
    pub receiver_led_hex: String,
}

impl Default for MouseConfig {
    fn default() -> Self {
        Self {
            active_profile: 0,
            active_dpi_stage: 1,
            dpi_stages: vec![
                DpiStage {
                    dpi: 400,
                    color: "#FF5C5C".into(),
                },
                DpiStage {
                    dpi: 800,
                    color: "#3DD68C".into(),
                },
                DpiStage {
                    dpi: 1600,
                    color: "#3B82F6".into(),
                },
                DpiStage {
                    dpi: 3200,
                    color: "#FFB340".into(),
                },
            ],
            polling_hz: 1000,
            lod: LodLevel::Low1Mm,
            motion_sync: true,
            angle_snapping: false,
            ripple_control: false,
            debounce_ms: 2,
            click_mode_left: ClickMode::Standard,
            click_mode_right: ClickMode::Standard,
            sensor_rotation_deg: 0,
            receiver_led_hex: "#FFFFFF".into(),
        }
    }
}

impl MouseConfig {
    pub fn active_dpi(&self) -> u32 {
        self.dpi_stages
            .get(self.active_dpi_stage as usize)
            .or_else(|| self.dpi_stages.first())
            .map(|s| s.dpi)
            .unwrap_or(800)
    }

    pub fn clamp_to(&mut self, caps: &DeviceCapabilities) {
        if caps.max_dpi > 0 && caps.dpi_step > 0 {
            let max_stages = caps.max_dpi_stages.max(1) as usize;
            if self.dpi_stages.is_empty() {
                self.dpi_stages.push(DpiStage {
                    dpi: 800.clamp(caps.min_dpi, caps.max_dpi),
                    color: "#3DD68C".into(),
                });
            }
            self.dpi_stages.truncate(max_stages);
            for stage in &mut self.dpi_stages {
                let clamped = stage.dpi.clamp(caps.min_dpi, caps.max_dpi);
                let rem = (clamped - caps.min_dpi) % caps.dpi_step;
                stage.dpi = clamped - rem;
            }
            if (self.active_dpi_stage as usize) >= self.dpi_stages.len() {
                self.active_dpi_stage = 0;
            }
        }
        if !caps.polling_rates_hz.is_empty() && !caps.polling_rates_hz.contains(&self.polling_hz) {
            self.polling_hz = *caps
                .polling_rates_hz
                .iter()
                .min_by_key(|&&r| r.abs_diff(self.polling_hz))
                .unwrap_or(&1000);
        }
        if caps.has_debounce_ms {
            self.debounce_ms = self
                .debounce_ms
                .clamp(caps.debounce_min_ms, caps.debounce_max_ms);
        }
        if caps.has_sensor_rotation {
            self.sensor_rotation_deg = self
                .sensor_rotation_deg
                .clamp(caps.rotation_min_deg, caps.rotation_max_deg);
        } else {
            self.sensor_rotation_deg = 0;
        }
        if caps.max_onboard_profiles > 0 && self.active_profile >= caps.max_onboard_profiles {
            self.active_profile = 0;
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MouseSnapshot {
    pub firmware: String,
    pub battery_pct: Option<u8>,
    pub charging: bool,
    pub config: MouseConfig,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WritePersistence {
    VolatileRam,
    CommitFlash,
}

pub trait MouseProtocolDriver {
    fn family(&self) -> DriverFamily;
    fn read_snapshot(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
    ) -> Result<MouseSnapshot, HidError>;
    fn apply_config(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
        current: &MouseConfig,
        target: &MouseConfig,
        persistence: WritePersistence,
    ) -> Result<usize, HidError>;
}

pub fn driver_for_family(family: DriverFamily) -> Option<Box<dyn MouseProtocolDriver>> {
    match family {
        DriverFamily::Finalmouse => Some(Box::new(finalmouse::FinalmouseDriver)),
        DriverFamily::LogitechHidpp => Some(Box::new(logitech_hidpp::LogitechHidppDriver)),
        DriverFamily::Razer => Some(Box::new(razer::RazerDriver)),
        DriverFamily::OemNordic => Some(Box::new(oem_nordic::OemNordicDriver)),
        DriverFamily::Wooting => None,
    }
}
