use super::{
    DpiStage, LodLevel, MouseConfig, MouseProtocolDriver, MouseSnapshot, WritePersistence,
};
use crate::registry::DriverFamily;
use crate::transport::{HidError, HidTransport};

pub const REPORT_SHORT: u8 = 0x10;
pub const REPORT_LONG: u8 = 0x11;
pub const SHORT_BODY_LEN: usize = 6;
pub const LONG_BODY_LEN: usize = 19;
pub const DEFAULT_DEVICE_INDEX: u8 = 0xFF;
pub const SW_ID: u8 = 0x0A;

pub const FEATURE_ROOT: u16 = 0x0000;
pub const FEATURE_BATTERY_STATUS: u16 = 0x1000;
pub const FEATURE_UNIFIED_BATTERY: u16 = 0x1004;
pub const FEATURE_ADJUSTABLE_DPI: u16 = 0x2201;
pub const FEATURE_REPORT_RATE: u16 = 0x8060;
pub const FEATURE_EXTENDED_REPORT_RATE: u16 = 0x8061;
pub const FEATURE_ONBOARD_PROFILES: u16 = 0x8100;
pub const FEATURE_DFU_CONTROL: u16 = 0x00C2;

pub struct LogitechHidppDriver;

pub fn encode_long_request(
    device_index: u8,
    feature_index: u8,
    function_id: u8,
    params: &[u8],
) -> Vec<u8> {
    let mut body = vec![0u8; LONG_BODY_LEN];
    body[0] = device_index;
    body[1] = feature_index;
    body[2] = ((function_id & 0x0F) << 4) | (SW_ID & 0x0F);
    let take = params.len().min(LONG_BODY_LEN - 3);
    body[3..3 + take].copy_from_slice(&params[..take]);
    body
}

pub fn discover_feature_index(
    transport: &mut dyn HidTransport,
    key: &str,
    feature_id: u16,
) -> Result<Option<u8>, HidError> {
    if feature_id == FEATURE_DFU_CONTROL {
        return Err(HidError::BootloaderBlocked(
            "Logitech DFU control feature 0x00C2 is blocked in Wave 1".into(),
        ));
    }
    let [hi, lo] = feature_id.to_be_bytes();
    let body = encode_long_request(DEFAULT_DEVICE_INDEX, 0x00, 0x00, &[hi, lo, 0x00]);
    let reply = exchange(transport, key, &body)?;
    if reply.len() < 5 {
        return Err(HidError::InvalidReport("Short HID++ root reply".into()));
    }
    let idx = reply[4];
    Ok((idx != 0).then_some(idx))
}

fn exchange(transport: &mut dyn HidTransport, key: &str, body: &[u8]) -> Result<Vec<u8>, HidError> {
    let mut last_err = None;
    for _ in 0..2 {
        transport.write_output(key, REPORT_LONG, body)?;
        match transport.read_input(key, 500) {
            Ok(reply) => {
                if reply.len() < 7 {
                    return Err(HidError::InvalidReport(format!(
                        "Short HID++ reply: {} bytes",
                        reply.len()
                    )));
                }
                if reply[2] == 0xFF || reply[2] == 0x8F {
                    return Err(HidError::Io(format!(
                        "HID++ device error code 0x{:02X}",
                        reply.get(5).copied().unwrap_or(0)
                    )));
                }
                return Ok(reply);
            }
            Err(HidError::Timeout(t)) => last_err = Some(HidError::Timeout(t)),
            Err(e) => return Err(e),
        }
    }
    Err(last_err.unwrap_or_else(|| HidError::Timeout(key.into())))
}

pub fn polling_hz_to_interval_code(hz: u16) -> u8 {
    match hz {
        125 => 8,
        250 => 4,
        500 => 2,
        1000 => 1,
        2000 => 0x01,
        4000 => 0x02,
        8000 => 0x03,
        _ => 1,
    }
}

pub fn interval_code_to_polling_hz(extended: bool, code: u8) -> u16 {
    if extended {
        match code {
            0x01 => 2000,
            0x02 => 4000,
            0x03 => 8000,
            _ => 1000,
        }
    } else {
        match code {
            8 => 125,
            4 => 250,
            2 => 500,
            _ => 1000,
        }
    }
}

impl MouseProtocolDriver for LogitechHidppDriver {
    fn family(&self) -> DriverFamily {
        DriverFamily::LogitechHidpp
    }

    fn read_snapshot(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
    ) -> Result<MouseSnapshot, HidError> {
        let mut battery_pct = None;
        let mut charging = false;
        if let Some(idx) = discover_feature_index(transport, key, FEATURE_UNIFIED_BATTERY)? {
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x01, &[]);
            let rep = exchange(transport, key, &req)?;
            battery_pct = Some(rep[4].min(100));
            charging = rep.get(6).copied().unwrap_or(0) == 1;
        } else if let Some(idx) = discover_feature_index(transport, key, FEATURE_BATTERY_STATUS)? {
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x00, &[]);
            let rep = exchange(transport, key, &req)?;
            battery_pct = Some(rep[4].min(100));
            charging = rep.get(6).copied().unwrap_or(0) == 1;
        }

        let mut dpi = 800u32;
        if let Some(idx) = discover_feature_index(transport, key, FEATURE_ADJUSTABLE_DPI)? {
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x02, &[0x00]);
            let rep = exchange(transport, key, &req)?;
            let val = u16::from_be_bytes([rep[5], rep[6]]) as u32;
            if val >= 100 {
                dpi = val;
            }
        }

        let mut polling_hz = 1000u16;
        if let Some(idx) = discover_feature_index(transport, key, FEATURE_EXTENDED_REPORT_RATE)? {
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x01, &[]);
            let rep = exchange(transport, key, &req)?;
            polling_hz = interval_code_to_polling_hz(true, rep[4]);
        } else if let Some(idx) = discover_feature_index(transport, key, FEATURE_REPORT_RATE)? {
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x01, &[]);
            let rep = exchange(transport, key, &req)?;
            polling_hz = interval_code_to_polling_hz(false, rep[4]);
        }

        let mut active_profile = 0u8;
        if let Some(idx) = discover_feature_index(transport, key, FEATURE_ONBOARD_PROFILES)? {
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x04, &[]);
            if let Ok(rep) = exchange(transport, key, &req) {
                active_profile = rep[5].min(4);
            }
        }

        let stages = vec![
            DpiStage {
                dpi: 400,
                color: "#FF5C5C".into(),
            },
            DpiStage {
                dpi,
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
        ];

        Ok(MouseSnapshot {
            firmware: "HID++ 2.0".into(),
            battery_pct,
            charging,
            config: MouseConfig {
                active_profile,
                active_dpi_stage: 1,
                dpi_stages: stages,
                polling_hz,
                lod: LodLevel::Low1Mm,
                motion_sync: false,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms: 0,
                click_mode_left: super::ClickMode::Standard,
                click_mode_right: super::ClickMode::Standard,
                sensor_rotation_deg: 0,
                receiver_led_hex: "#FFFFFF".into(),
            },
        })
    }

    fn apply_config(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
        current: &MouseConfig,
        target: &MouseConfig,
        persistence: WritePersistence,
    ) -> Result<usize, HidError> {
        let mut writes = 0usize;

        if (current.active_dpi() != target.active_dpi()
            || current.active_dpi_stage != target.active_dpi_stage)
            && let Some(idx) = discover_feature_index(transport, key, FEATURE_ADJUSTABLE_DPI)?
        {
            let dpi = target.active_dpi().min(u16::MAX as u32) as u16;
            let [hi, lo] = dpi.to_be_bytes();
            let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x03, &[0x00, hi, lo]);
            let _ = exchange(transport, key, &req)?;
            writes += 1;
        }

        if current.polling_hz != target.polling_hz {
            let feat = if target.polling_hz > 1000 {
                FEATURE_EXTENDED_REPORT_RATE
            } else {
                FEATURE_REPORT_RATE
            };
            if let Some(idx) = discover_feature_index(transport, key, feat)? {
                let code = polling_hz_to_interval_code(target.polling_hz);
                let req = encode_long_request(DEFAULT_DEVICE_INDEX, idx, 0x02, &[code]);
                let _ = exchange(transport, key, &req)?;
                writes += 1;
            }
        }

        if persistence == WritePersistence::CommitFlash
            && let Some(idx) = discover_feature_index(transport, key, FEATURE_ONBOARD_PROFILES)?
        {
            let req = encode_long_request(
                DEFAULT_DEVICE_INDEX,
                idx,
                0x03,
                &[0x00, target.active_profile],
            );
            let _ = exchange(transport, key, &req)?;
            writes += 1;
        }

        Ok(writes)
    }
}
