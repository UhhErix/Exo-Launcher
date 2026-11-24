use serde::{Deserialize, Serialize};

pub const SONY_VENDOR: u16 = 0x054C;
pub const DUALSENSE_PID: u16 = 0x0CE6;
pub const DUALSENSE_EDGE_PID: u16 = 0x0DF2;
pub const DUALSHOCK4_V1_PID: u16 = 0x05C4;
pub const DUALSHOCK4_V2_PID: u16 = 0x09CC;

pub const EDGE_FN_LEFT: u8 = 0x10;
pub const EDGE_FN_RIGHT: u8 = 0x20;
pub const EDGE_BACK_LEFT: u8 = 0x40;
pub const EDGE_BACK_RIGHT: u8 = 0x80;

pub const READ_SERIAL: [u8; 2] = [0x01, 0x13];
pub const NV_STATUS: [u8; 2] = [0x03, 0x03];
pub const NV_LOCK: [u8; 2] = [0x03, 0x01];
pub const NV_UNLOCK: [u8; 6] = [0x03, 0x02, 0x65, 0x32, 0x40, 0x0C];
pub const FINETUNE_WRITE: [u8; 2] = [0x0C, 0x01];
pub const CENTER_BEGIN: [u8; 3] = [1, 1, 1];
pub const CENTER_SAMPLE: [u8; 3] = [3, 1, 1];
pub const CENTER_END: [u8; 3] = [2, 1, 1];
pub const RANGE_BEGIN: [u8; 3] = [1, 1, 2];
pub const RANGE_END: [u8; 3] = [2, 1, 2];
pub const FIRST_SLOT_REPORT: u8 = 0x70;
pub const LAST_SLOT_REPORT: u8 = 0x7B;

pub const BT_OUTPUT_CRC_SEED: u8 = 0xA2;

const VALID_FLAG_1: usize = 2;
const POWER_SAVE: usize = 10;
const REDUCE_MOTOR_POWER: usize = 37;
const VALID_FLAG_2: usize = 39;
const LIGHTBAR_SETUP: usize = 42;
const PLAYER_LEDS: usize = 44;
const RED: usize = 45;

const POWER_SAVE_CONTROL: u8 = 0x02;
const LIGHTBAR_CONTROL: u8 = 0x04;
const PLAYER_INDICATOR_CONTROL: u8 = 0x10;
const VIBRATION_ATTENUATION: u8 = 0x40;
const LIGHTBAR_SETUP_CONTROL: u8 = 0x02;
const LIGHT_ON: u8 = 0x01;
const LIGHT_OUT: u8 = 0x02;
const MIC_MUTE: u8 = 0x10;
const SPEAKER_MUTE: u8 = 0x20;
const HAPTICS_MUTE: u8 = 0x80;
const PLAYER_ONE: u8 = 0x04;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SonyInputState {
    pub lx: u8,
    pub ly: u8,
    pub rx: u8,
    pub ry: u8,
    pub l2: u8,
    pub r2: u8,
    pub sensor_clock: Option<u32>,
    pub edge_profile: Option<u8>,
    pub stop_l: Option<u8>,
    pub stop_r: Option<u8>,
    pub battery_pct: Option<u8>,
    pub charging: bool,
    pub extras: u8,
    pub buttons: u32,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum MotorStrength {
    #[default]
    Full,
    Medium,
    Low,
    Off,
}

impl MotorStrength {
    pub fn attenuation(self) -> u8 {
        match self {
            Self::Full => 0,
            Self::Medium => 3,
            Self::Low => 5,
            Self::Off => 7,
        }
    }
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DualSenseSettings {
    pub light: Option<String>,
    pub player_light: bool,
    pub mic_off: bool,
    pub speaker_off: bool,
    pub vibration: MotorStrength,
    pub trigger_effects: MotorStrength,
}

pub fn finetune_read_cmd(edge: bool) -> [u8; 2] {
    [0x0C, if edge { 0x04 } else { 0x02 }]
}

pub fn is_feature_allowed(id: u8, payload: &[u8], set: bool, edge: bool) -> bool {
    if !set {
        return matches!(id, 0x20 | 0x81 | 0x83)
            || (edge && (FIRST_SLOT_REPORT..=LAST_SLOT_REPORT).contains(&id));
    }
    match id {
        0x80 => {
            payload == READ_SERIAL
                || payload == NV_STATUS
                || payload == NV_LOCK
                || payload == NV_UNLOCK
                || payload == finetune_read_cmd(edge)
                || (payload.len() == 26 && payload.starts_with(&FINETUNE_WRITE))
        }
        0x82 => {
            payload == CENTER_BEGIN
                || payload == CENTER_SAMPLE
                || payload == CENTER_END
                || payload == RANGE_BEGIN
                || payload == RANGE_END
        }
        _ => false,
    }
}

pub fn parse_dualsense_input(report: &[u8], usb: bool, edge: bool) -> Option<SonyInputState> {
    if report.is_empty() {
        return None;
    }
    if !usb && report[0] == 0x01 {
        if report.len() < 10 {
            return None;
        }
        return Some(SonyInputState {
            lx: report[1],
            ly: report[2],
            rx: report[3],
            ry: report[4],
            l2: report[8],
            r2: report[9],
            sensor_clock: None,
            edge_profile: None,
            stop_l: None,
            stop_r: None,
            battery_pct: None,
            charging: false,
            extras: 0,
            buttons: sony_buttons(report[5], report[6], report[7] & 0x03),
        });
    }

    let start = if usb && report[0] == 0x01 {
        1
    } else if !usb && report[0] == 0x31 {
        2
    } else {
        return None;
    };
    if report.len() < start + 53 {
        return None;
    }
    let data = &report[start..];
    let (extras, edge_profile, stop_l, stop_r) = if edge {
        let ex = data[9] & (EDGE_FN_LEFT | EDGE_FN_RIGHT | EDGE_BACK_LEFT | EDGE_BACK_RIGHT);
        let p = data[48] >> 4;
        let prof = (1..=4).contains(&p).then_some(p);
        (
            ex,
            prof,
            Some((data[49] >> 4) & 0x03),
            Some((data[49] >> 6) & 0x03),
        )
    } else {
        (0, None, None, None)
    };
    let level = data[52] & 0x0F;
    let charge = data[52] >> 4;
    let sensor_clock = u32::from_le_bytes([data[27], data[28], data[29], data[30]]);
    Some(SonyInputState {
        lx: data[0],
        ly: data[1],
        rx: data[2],
        ry: data[3],
        l2: data[4],
        r2: data[5],
        sensor_clock: Some(sensor_clock),
        edge_profile,
        stop_l,
        stop_r,
        battery_pct: (level <= 10).then_some((level * 10 + 5).min(100)),
        charging: matches!(charge, 1 | 2),
        extras,
        buttons: sony_buttons(data[7], data[8], data[9]),
    })
}

pub fn sony_buttons(first: u8, second: u8, third: u8) -> u32 {
    let hat: u32 = match first & 0x0F {
        0 => 0b0001,
        1 => 0b0011,
        2 => 0b0010,
        3 => 0b0110,
        4 => 0b0100,
        5 => 0b1100,
        6 => 0b1000,
        7 => 0b1001,
        _ => 0,
    };
    hat | u32::from(first & 0xF0) | (u32::from(second) << 8) | (u32::from(third) << 16)
}

pub fn parse_hex_color(text: &str) -> Option<(u8, u8, u8)> {
    let hex = text.strip_prefix('#')?;
    if hex.len() != 6 {
        return None;
    }
    let val = u32::from_str_radix(hex, 16).ok()?;
    Some((
        ((val >> 16) & 0xFF) as u8,
        ((val >> 8) & 0xFF) as u8,
        (val & 0xFF) as u8,
    ))
}

pub fn build_usb_settings_report(
    s: &DualSenseSettings,
    length: usize,
) -> Result<Vec<u8>, &'static str> {
    if !(48..=64).contains(&length) {
        return Err("USB settings report length must be 48..=64 bytes.");
    }
    let mut report = vec![0u8; length];
    report[0] = 0x02;
    report[VALID_FLAG_1] = POWER_SAVE_CONTROL | PLAYER_INDICATOR_CONTROL | VIBRATION_ATTENUATION;
    report[POWER_SAVE] = (if s.mic_off { MIC_MUTE } else { 0 })
        | (if s.speaker_off { SPEAKER_MUTE } else { 0 })
        | (if s.vibration == MotorStrength::Off {
            HAPTICS_MUTE
        } else {
            0
        });
    report[REDUCE_MOTOR_POWER] = s.vibration.attenuation() | (s.trigger_effects.attenuation() << 4);
    report[PLAYER_LEDS] = if s.player_light { PLAYER_ONE } else { 0 };
    if let Some(ref light) = s.light {
        report[VALID_FLAG_2] = LIGHTBAR_SETUP_CONTROL;
        if light == "on" {
            report[LIGHTBAR_SETUP] = LIGHT_ON;
        } else {
            report[LIGHTBAR_SETUP] = LIGHT_OUT;
            report[VALID_FLAG_1] |= LIGHTBAR_CONTROL;
            let (r, g, b) = if light == "off" {
                (0, 0, 0)
            } else {
                parse_hex_color(light).ok_or("Invalid lightbar color.")?
            };
            report[RED] = r;
            report[RED + 1] = g;
            report[RED + 2] = b;
        }
    }
    Ok(report)
}

pub fn is_only_settings_report(report: &[u8]) -> bool {
    if !(48..=64).contains(&report.len()) || report[0] != 0x02 {
        return false;
    }
    let allowed_f1 =
        POWER_SAVE_CONTROL | LIGHTBAR_CONTROL | PLAYER_INDICATOR_CONTROL | VIBRATION_ATTENUATION;
    if (report[VALID_FLAG_1] & !allowed_f1) != 0 {
        return false;
    }
    if (report[VALID_FLAG_2] & !LIGHTBAR_SETUP_CONTROL) != 0 || report[LIGHTBAR_SETUP] > LIGHT_OUT {
        return false;
    }
    if (report[POWER_SAVE] & !(MIC_MUTE | SPEAKER_MUTE | HAPTICS_MUTE)) != 0
        || (report[REDUCE_MOTOR_POWER] & 0x88) != 0
    {
        return false;
    }
    for (i, &b) in report.iter().enumerate().skip(1) {
        let allowed_idx = matches!(
            i,
            VALID_FLAG_1
                | POWER_SAVE
                | REDUCE_MOTOR_POWER
                | VALID_FLAG_2
                | LIGHTBAR_SETUP
                | PLAYER_LEDS
        ) || (RED..=RED + 2).contains(&i);
        if b != 0 && !allowed_idx {
            return false;
        }
    }
    true
}

pub fn crc32_sony_bt(seed: u8, payload: &[u8]) -> u32 {
    let mut crc: u32 = 0xFFFF_FFFF;
    crc = crc32_step(crc, seed);
    for &b in payload {
        crc = crc32_step(crc, b);
    }
    !crc
}

fn crc32_step(mut crc: u32, byte: u8) -> u32 {
    crc ^= u32::from(byte);
    for _ in 0..8 {
        if (crc & 1) != 0 {
            crc = (crc >> 1) ^ 0xEDB8_8320;
        } else {
            crc >>= 1;
        }
    }
    crc
}

pub fn build_bt_settings_report(s: &DualSenseSettings) -> Result<[u8; 78], &'static str> {
    let usb = build_usb_settings_report(s, 48)?;
    let mut bt = [0u8; 78];
    bt[0] = 0x31;
    bt[1] = 0x02;
    bt[2..49].copy_from_slice(&usb[1..48]);
    let crc = crc32_sony_bt(BT_OUTPUT_CRC_SEED, &bt[..74]);
    bt[74..78].copy_from_slice(&crc.to_le_bytes());
    Ok(bt)
}

pub fn verify_bt_output_crc(report: &[u8; 78]) -> bool {
    let expected = crc32_sony_bt(BT_OUTPUT_CRC_SEED, &report[..74]);
    u32::from_le_bytes([report[74], report[75], report[76], report[77]]) == expected
}
