use serde::{Deserialize, Serialize};

pub const XBOX_VENDOR: u16 = 0x045E;
pub const XBOX_ELITE_2_USB_PID: u16 = 0x0B00;
pub const XBOX_ELITE_2_BT_PID: u16 = 0x0B05;
pub const XBOX_SERIES_USB_PID: u16 = 0x0B12;
pub const XBOX_SERIES_BT_PID: u16 = 0x0B13;

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum XboxBatteryLevel {
    Empty,
    Low,
    Medium,
    Full,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct XboxEliteSlot {
    pub slot_index: u8,
    pub name: String,
    pub paddles_enabled: bool,
    pub left_trigger_hair: bool,
    pub right_trigger_hair: bool,
}

pub fn decode_xbox_battery_byte(byte: u8) -> (XboxBatteryLevel, u8) {
    match byte & 0x03 {
        0 => (XboxBatteryLevel::Empty, 5),
        1 => (XboxBatteryLevel::Low, 30),
        2 => (XboxBatteryLevel::Medium, 65),
        _ => (XboxBatteryLevel::Full, 100),
    }
}

pub fn build_xbox_rumble_report(
    left_trigger: u8,
    right_trigger: u8,
    left_motor: u8,
    right_motor: u8,
    duration_10ms: u8,
) -> [u8; 9] {
    [
        0x03,
        0x0F,
        left_trigger,
        right_trigger,
        left_motor,
        right_motor,
        duration_10ms,
        0x00,
        0x00,
    ]
}

pub fn default_elite_slots() -> [XboxEliteSlot; 3] {
    [
        XboxEliteSlot {
            slot_index: 1,
            name: "FPS Hair-Trigger".into(),
            paddles_enabled: true,
            left_trigger_hair: true,
            right_trigger_hair: true,
        },
        XboxEliteSlot {
            slot_index: 2,
            name: "Racing".into(),
            paddles_enabled: true,
            left_trigger_hair: false,
            right_trigger_hair: false,
        },
        XboxEliteSlot {
            slot_index: 3,
            name: "Desktop".into(),
            paddles_enabled: false,
            left_trigger_hair: false,
            right_trigger_hair: false,
        },
    ]
}
