use super::WritePersistence;
use crate::transport::{HidError, HidTransport};
use serde::{Deserialize, Serialize};

pub const MAGIC_AVR_0: u8 = 0xD0;
pub const MAGIC_ARM_0: u8 = 0xD1;
pub const MAGIC_1: u8 = 0xDA;

pub const CMD_GET_VERSION: u8 = 0x03;
pub const CMD_SAVE_RGB_PROFILE: u8 = 0x08;
pub const CMD_GET_DEVICE_CONFIG: u8 = 0x13;
pub const CMD_ACTIVATE_PROFILE: u8 = 0x17;
pub const CMD_GET_CURRENT_PROFILE_INDEX: u8 = 0x1B;
pub const CMD_GET_PROFILE_NAME: u8 = 0x1E;
pub const CMD_WOOT_DEV_INIT: u8 = 0x21;
pub const CMD_RELOAD_PROFILE: u8 = 0x26;
pub const CMD_GET_KEYBOARD_PROFILE: u8 = 0x27;
pub const CMD_GET_RGB_PROFILE_CORE: u8 = 0x32;
pub const CMD_ENTER_BOOTLOADER: u8 = 0x02;

pub const OUT_SET_KEYBOARD_BASE: u8 = 0x11;
pub const OUT_SET_RGB_PROFILE_CORE: u8 = 0x16;

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum WootingFraming {
    AvrUnnumbered,
    ArmMultiReport,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WootingCapabilities {
    pub framing: WootingFraming,
    pub output_report_len: usize,
    pub min_actuation_mm: f32,
    pub max_actuation_mm: f32,
    pub has_rapid_trigger: bool,
    pub has_tachyon: bool,
    pub has_rgb: bool,
    pub max_scan_hz: u16,
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct WootingSettings {
    pub active_profile: u8,
    pub actuation_mm: f32,
    pub rapid_trigger: bool,
    pub rapid_trigger_mm: f32,
    pub tachyon: bool,
    pub rgb_brightness: u8,
}

pub fn capabilities_for_pid(vid: u16, pid: u16) -> Option<WootingCapabilities> {
    if vid == 0x03EB && matches!(pid, 0xFF01 | 0x2402 | 0xFF02 | 0x2403) {
        return Some(WootingCapabilities {
            framing: WootingFraming::AvrUnnumbered,
            output_report_len: 129,
            min_actuation_mm: 1.5,
            max_actuation_mm: 3.6,
            has_rapid_trigger: false,
            has_tachyon: false,
            has_rgb: true,
            max_scan_hz: 1000,
        });
    }
    if vid != 0x31E3 {
        return None;
    }
    let base = pid & 0xFFF0;
    match base {
        0x1100 | 0x1200 => Some(WootingCapabilities {
            framing: WootingFraming::AvrUnnumbered,
            output_report_len: 257,
            min_actuation_mm: 1.5,
            max_actuation_mm: 3.6,
            has_rapid_trigger: false,
            has_tachyon: false,
            has_rgb: true,
            max_scan_hz: 1000,
        }),
        0x1210 | 0x1220 | 0x1300 => Some(WootingCapabilities {
            framing: WootingFraming::AvrUnnumbered,
            output_report_len: 257,
            min_actuation_mm: 0.1,
            max_actuation_mm: 4.0,
            has_rapid_trigger: true,
            has_tachyon: true,
            has_rgb: true,
            max_scan_hz: 1000,
        }),
        0x1230 | 0x1310 | 0x1320 => Some(WootingCapabilities {
            framing: WootingFraming::ArmMultiReport,
            output_report_len: 255,
            min_actuation_mm: 0.1,
            max_actuation_mm: 4.0,
            has_rapid_trigger: true,
            has_tachyon: true,
            has_rgb: true,
            max_scan_hz: 1000,
        }),
        0x1340 | 0x1400 | 0x1410 => Some(WootingCapabilities {
            framing: WootingFraming::ArmMultiReport,
            output_report_len: 2047,
            min_actuation_mm: 0.1,
            max_actuation_mm: 4.0,
            has_rapid_trigger: true,
            has_tachyon: true,
            has_rgb: true,
            max_scan_hz: 8000,
        }),
        0x1500 => Some(WootingCapabilities {
            framing: WootingFraming::ArmMultiReport,
            output_report_len: 255,
            min_actuation_mm: 0.1,
            max_actuation_mm: 4.0,
            has_rapid_trigger: true,
            has_tachyon: true,
            has_rgb: false,
            max_scan_hz: 1000,
        }),
        0x1510 => Some(WootingCapabilities {
            framing: WootingFraming::ArmMultiReport,
            output_report_len: 255,
            min_actuation_mm: 0.1,
            max_actuation_mm: 4.0,
            has_rapid_trigger: true,
            has_tachyon: true,
            has_rgb: true,
            max_scan_hz: 1000,
        }),
        _ => None,
    }
}

pub fn select_arm_report_id(payload_bytes: usize) -> u8 {
    match payload_bytes {
        0..=32 => 1,
        33..=62 => 2,
        63..=254 => 3,
        255..=510 => 4,
        511..=1022 => 5,
        _ => 6,
    }
}

pub fn encode_feature_command(
    framing: WootingFraming,
    cmd: u8,
    params: &[u8],
) -> Result<(u8, Vec<u8>), HidError> {
    if cmd == CMD_ENTER_BOOTLOADER {
        return Err(HidError::BootloaderBlocked(
            "Wooting bootloader entry command is blocked in Wave 1".into(),
        ));
    }
    let magic0 = match framing {
        WootingFraming::AvrUnnumbered => MAGIC_AVR_0,
        WootingFraming::ArmMultiReport => MAGIC_ARM_0,
    };
    let mut body = vec![0u8; 7];
    body[0] = magic0;
    body[1] = MAGIC_1;
    body[2] = cmd;
    let take = params.len().min(4);
    body[3..3 + take].copy_from_slice(&params[..take]);
    Ok((0x00, body))
}

pub fn encode_output_report(
    caps: &WootingCapabilities,
    opcode: u8,
    payload: &[u8],
) -> Result<(u8, Vec<u8>), HidError> {
    let magic0 = match caps.framing {
        WootingFraming::AvrUnnumbered => MAGIC_AVR_0,
        WootingFraming::ArmMultiReport => MAGIC_ARM_0,
    };
    let mut inner = Vec::with_capacity(payload.len() + 6);
    inner.push(magic0);
    inner.push(MAGIC_1);
    inner.push(opcode);
    if caps.framing == WootingFraming::ArmMultiReport {
        let len = payload.len() as u16;
        inner.extend_from_slice(&len.to_le_bytes());
    }
    inner.extend_from_slice(payload);

    let report_id = match caps.framing {
        WootingFraming::AvrUnnumbered => 0x00,
        WootingFraming::ArmMultiReport => select_arm_report_id(inner.len()),
    };
    let target_body_len = caps.output_report_len.saturating_sub(1).max(inner.len());
    inner.resize(target_body_len, 0);
    Ok((report_id, inner))
}

pub fn decode_version(framing: WootingFraming, reply: &[u8]) -> Result<String, HidError> {
    let offset = match framing {
        WootingFraming::AvrUnnumbered => 4,
        WootingFraming::ArmMultiReport => 6,
    };
    if reply.len() < offset + 3 {
        return Err(HidError::InvalidReport(
            "Short Wooting version reply".into(),
        ));
    }
    Ok(format!(
        "{}.{}.{}",
        reply[offset],
        reply[offset + 1],
        reply[offset + 2]
    ))
}

pub fn decode_device_layout(
    framing: WootingFraming,
    reply: &[u8],
) -> Result<&'static str, HidError> {
    if reply.len() < 11 {
        return Err(HidError::InvalidReport(
            "Short Wooting device config reply".into(),
        ));
    }
    let expected_magic = match framing {
        WootingFraming::AvrUnnumbered => MAGIC_AVR_0,
        WootingFraming::ArmMultiReport => MAGIC_ARM_0,
    };
    if reply[1] != expected_magic || reply[2] != MAGIC_1 || reply[3] != CMD_GET_DEVICE_CONFIG {
        return Err(HidError::InvalidReport(
            "Unsolicited or mismatched Wooting config reply".into(),
        ));
    }
    let code = reply[10];
    Ok(match code {
        0 => "ansi",
        1 => "iso",
        2 => "jis",
        3 => "ansi-split",
        4 => "iso-split",
        _ => "ansi",
    })
}

pub fn apply_settings_diff(
    transport: &mut dyn HidTransport,
    key: &str,
    caps: &WootingCapabilities,
    current: Option<&WootingSettings>,
    settings: &WootingSettings,
    persistence: WritePersistence,
) -> Result<usize, HidError> {
    if current == Some(settings) && persistence == WritePersistence::VolatileRam {
        return Ok(0);
    }
    let mut writes = 0usize;

    let (init_id, init_buf) = encode_feature_command(caps.framing, CMD_WOOT_DEV_INIT, &[])?;
    transport.set_feature(key, init_id, &init_buf)?;
    writes += 1;

    if current != Some(settings) {
        let act_clamped = settings
            .actuation_mm
            .clamp(caps.min_actuation_mm, caps.max_actuation_mm);
        let act_byte = ((act_clamped / 4.0) * 255.0).round().clamp(1.0, 255.0) as u8;
        let rt_enabled = caps.has_rapid_trigger && settings.rapid_trigger;
        let rt_mm = settings.rapid_trigger_mm.clamp(0.10, 2.35);
        let rt_byte = ((rt_mm / 4.0) * 255.0).round().clamp(1.0, 255.0) as u8;
        let tachyon = caps.has_tachyon && settings.tachyon;

        let profile_payload = [
            settings.active_profile,
            act_byte,
            rt_enabled as u8,
            rt_byte,
            tachyon as u8,
        ];
        let (out_id, out_buf) =
            encode_output_report(caps, OUT_SET_KEYBOARD_BASE, &profile_payload)?;
        transport.write_output(key, out_id, &out_buf)?;
        writes += 1;

        if caps.has_rgb {
            let rgb_payload = [settings.active_profile, settings.rgb_brightness.min(100)];
            let (rgb_id, rgb_buf) =
                encode_output_report(caps, OUT_SET_RGB_PROFILE_CORE, &rgb_payload)?;
            transport.write_output(key, rgb_id, &rgb_buf)?;
            writes += 1;
        }
    }

    if caps.has_rgb && persistence == WritePersistence::CommitFlash {
        let (save_id, save_buf) = encode_feature_command(
            caps.framing,
            CMD_SAVE_RGB_PROFILE,
            &[settings.active_profile],
        )?;
        transport.set_feature(key, save_id, &save_buf)?;
        writes += 1;
    }

    let (reload_id, reload_buf) =
        encode_feature_command(caps.framing, CMD_RELOAD_PROFILE, &[settings.active_profile])?;
    transport.set_feature(key, reload_id, &reload_buf)?;
    writes += 1;

    Ok(writes)
}

pub fn apply_settings(
    transport: &mut dyn HidTransport,
    key: &str,
    caps: &WootingCapabilities,
    settings: &WootingSettings,
) -> Result<usize, HidError> {
    apply_settings_diff(
        transport,
        key,
        caps,
        None,
        settings,
        WritePersistence::CommitFlash,
    )
}
