use super::{
    ClickMode, DpiStage, LodLevel, MouseConfig, MouseProtocolDriver, MouseSnapshot,
    WritePersistence,
};
use crate::registry::DriverFamily;
use crate::transport::{HidError, HidTransport};

pub const REPORT_LEN: usize = 90;
pub const STATUS_NEW: u8 = 0x00;
pub const STATUS_BUSY: u8 = 0x01;
pub const STATUS_OK: u8 = 0x02;
pub const STATUS_FAIL: u8 = 0x03;

pub const CLASS_DEVICE: u8 = 0x00;
pub const CLASS_DPI: u8 = 0x04;
pub const CLASS_BATTERY: u8 = 0x07;
pub const CLASS_SENSOR: u8 = 0x0B;

pub const CMD_GET_FIRMWARE: u8 = 0x81;
pub const CMD_SET_POLLING: u8 = 0x05;
pub const CMD_GET_POLLING: u8 = 0x85;
pub const CMD_SET_POLLING_8K: u8 = 0x40;
pub const CMD_GET_POLLING_8K: u8 = 0xC0;
pub const CMD_SET_DPI_XY: u8 = 0x05;
pub const CMD_GET_DPI_XY: u8 = 0x85;
pub const CMD_GET_BATTERY: u8 = 0x80;
pub const CMD_GET_CHARGING: u8 = 0x84;
pub const CMD_SET_ROTATION: u8 = 0x14;
pub const CMD_GET_ROTATION: u8 = 0x94;
pub const CMD_SET_LOD: u8 = 0x0B;
pub const CMD_BOOTLOADER: u8 = 0x02;

pub struct RazerDriver;

pub fn crc_xor(report: &[u8]) -> u8 {
    let mut crc = 0u8;
    for &b in &report[2..88] {
        crc ^= b;
    }
    crc
}

pub fn encode_report(
    tx_id: u8,
    cmd_class: u8,
    cmd_id: u8,
    args: &[u8],
) -> Result<Vec<u8>, HidError> {
    if cmd_class == CLASS_DEVICE && cmd_id == CMD_BOOTLOADER {
        return Err(HidError::BootloaderBlocked(
            "Razer bootloader command is blocked in Wave 1".into(),
        ));
    }
    if args.len() > 80 {
        return Err(HidError::InvalidReport("Razer args exceed 80 bytes".into()));
    }
    let mut report = vec![0u8; REPORT_LEN];
    report[0] = STATUS_NEW;
    report[1] = tx_id;
    report[5] = args.len() as u8;
    report[6] = cmd_class;
    report[7] = cmd_id;
    report[8..8 + args.len()].copy_from_slice(args);
    report[88] = crc_xor(&report);
    report[89] = 0x00;
    Ok(report)
}

pub fn verify_reply(raw: &[u8], expected_class: u8, expected_cmd: u8) -> Result<&[u8], HidError> {
    let report = if raw.len() == REPORT_LEN + 1 && raw[0] == 0x00 {
        &raw[1..]
    } else if raw.len() >= REPORT_LEN {
        &raw[..REPORT_LEN]
    } else {
        return Err(HidError::InvalidReport(format!(
            "Short Razer reply: {} bytes",
            raw.len()
        )));
    };
    let expected_crc = crc_xor(report);
    if report[88] != expected_crc {
        return Err(HidError::InvalidReport(format!(
            "Razer CRC mismatch: got 0x{:02X}, expected 0x{expected_crc:02X}",
            report[88]
        )));
    }
    if report[6] != expected_class || report[7] != expected_cmd {
        return Err(HidError::InvalidReport(format!(
            "Razer reply command mismatch: 0x{:02X}/0x{:02X}",
            report[6], report[7]
        )));
    }
    match report[0] {
        STATUS_OK => {
            let size = (report[5] as usize).min(80);
            Ok(&report[8..8 + size])
        }
        STATUS_BUSY => Err(HidError::Timeout("Razer device busy".into())),
        STATUS_FAIL => Err(HidError::Io("Razer device rejected command".into())),
        other => Err(HidError::Io(format!("Razer status 0x{other:02X}"))),
    }
}

pub fn encode_polling_8k(hz: u16) -> u8 {
    match hz {
        8000 => 0x01,
        4000 => 0x02,
        2000 => 0x04,
        1000 => 0x08,
        500 => 0x10,
        250 => 0x20,
        125 => 0x40,
        _ => 0x08,
    }
}

pub fn decode_polling_8k(code: u8) -> u16 {
    match code {
        0x01 => 8000,
        0x02 => 4000,
        0x04 => 2000,
        0x08 => 1000,
        0x10 => 500,
        0x20 => 250,
        0x40 => 125,
        _ => 1000,
    }
}

pub fn encode_rotation_angle(deg: i8) -> Result<[u8; 2], HidError> {
    if !(-30..=30).contains(&deg) {
        return Err(HidError::InvalidReport(format!(
            "Sensor rotation {deg} out of -30..=30 range"
        )));
    }
    Ok([0x01, deg as u8])
}

pub fn decode_rotation_angle(byte: u8) -> i8 {
    (byte as i8).clamp(-30, 30)
}

fn send_feature_exchange(
    transport: &mut dyn HidTransport,
    key: &str,
    tx_id: u8,
    cmd_class: u8,
    cmd_id: u8,
    args: &[u8],
) -> Result<Vec<u8>, HidError> {
    let report = encode_report(tx_id, cmd_class, cmd_id, args)?;
    let mut last_err = None;
    for _ in 0..3 {
        transport.set_feature(key, 0x00, &report)?;
        let reply = transport.get_feature(key, 0x00, REPORT_LEN + 1)?;
        match verify_reply(&reply, cmd_class, cmd_id) {
            Ok(data) => return Ok(data.to_vec()),
            Err(HidError::Timeout(t)) => last_err = Some(HidError::Timeout(t)),
            Err(e) => return Err(e),
        }
    }
    Err(last_err.unwrap_or_else(|| HidError::Timeout(key.into())))
}

impl MouseProtocolDriver for RazerDriver {
    fn family(&self) -> DriverFamily {
        DriverFamily::Razer
    }

    fn read_snapshot(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
    ) -> Result<MouseSnapshot, HidError> {
        let fw_data = send_feature_exchange(
            transport,
            key,
            0x1F,
            CLASS_DEVICE,
            CMD_GET_FIRMWARE,
            &[0, 0],
        )?;
        let firmware = if fw_data.len() >= 2 {
            format!("v{}.{}", fw_data[0], fw_data[1])
        } else {
            "v1.0".into()
        };

        let dpi_data = send_feature_exchange(
            transport,
            key,
            0x1F,
            CLASS_DPI,
            CMD_GET_DPI_XY,
            &[0x01, 0, 0, 0, 0, 0, 0],
        )?;
        let dpi = if dpi_data.len() >= 3 {
            u16::from_be_bytes([dpi_data[1], dpi_data[2]]) as u32
        } else {
            800
        };

        let poll_data = send_feature_exchange(
            transport,
            key,
            0x1F,
            CLASS_DEVICE,
            CMD_GET_POLLING_8K,
            &[0x00, 0x00],
        )?;
        let polling_hz = poll_data
            .get(1)
            .map(|&b| decode_polling_8k(b))
            .unwrap_or(1000);

        let battery_pct = send_feature_exchange(
            transport,
            key,
            0x1F,
            CLASS_BATTERY,
            CMD_GET_BATTERY,
            &[0, 0],
        )
        .ok()
        .and_then(|b| b.get(1).copied())
        .map(|raw| ((raw as u16 * 100) / 255).min(100) as u8);

        let charging = send_feature_exchange(
            transport,
            key,
            0x1F,
            CLASS_BATTERY,
            CMD_GET_CHARGING,
            &[0, 0],
        )
        .ok()
        .and_then(|b| b.get(1).copied())
        .map(|v| v != 0)
        .unwrap_or(false);

        let rotation = send_feature_exchange(
            transport,
            key,
            0x1F,
            CLASS_SENSOR,
            CMD_GET_ROTATION,
            &[0x01, 0x00],
        )
        .ok()
        .and_then(|b| b.get(1).copied())
        .map(decode_rotation_angle)
        .unwrap_or(0);

        let stages = vec![
            DpiStage {
                dpi: 400,
                color: "#FF5C5C".into(),
            },
            DpiStage {
                dpi: dpi.max(100),
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
            firmware,
            battery_pct,
            charging,
            config: MouseConfig {
                active_profile: 0,
                active_dpi_stage: 1,
                dpi_stages: stages,
                polling_hz,
                lod: LodLevel::Low1Mm,
                motion_sync: true,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms: 0,
                click_mode_left: ClickMode::Standard,
                click_mode_right: ClickMode::Standard,
                sensor_rotation_deg: rotation,
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
        let storage = match persistence {
            WritePersistence::VolatileRam => 0x00u8,
            WritePersistence::CommitFlash => 0x01u8,
        };

        if current.active_dpi() != target.active_dpi()
            || current.active_dpi_stage != target.active_dpi_stage
        {
            let dpi = target.active_dpi().min(u16::MAX as u32) as u16;
            let [hi, lo] = dpi.to_be_bytes();
            let _ = send_feature_exchange(
                transport,
                key,
                0x1F,
                CLASS_DPI,
                CMD_SET_DPI_XY,
                &[storage, hi, lo, hi, lo, 0, 0],
            )?;
            writes += 1;
        }

        if current.polling_hz != target.polling_hz {
            let code = encode_polling_8k(target.polling_hz);
            let _ = send_feature_exchange(
                transport,
                key,
                0x1F,
                CLASS_DEVICE,
                CMD_SET_POLLING_8K,
                &[storage, code],
            )?;
            writes += 1;
        }

        if current.lod != target.lod {
            let lod_val = match target.lod {
                LodLevel::Low1Mm => 0x01u8,
                LodLevel::High2Mm => 0x02u8,
            };
            let _ = send_feature_exchange(
                transport,
                key,
                0x1F,
                CLASS_SENSOR,
                CMD_SET_LOD,
                &[storage, 0x04, lod_val],
            )?;
            writes += 1;
        }

        if current.sensor_rotation_deg != target.sensor_rotation_deg {
            let args = encode_rotation_angle(target.sensor_rotation_deg)?;
            let _ = send_feature_exchange(
                transport,
                key,
                0x1F,
                CLASS_SENSOR,
                CMD_SET_ROTATION,
                &[storage, args[1]],
            )?;
            writes += 1;
        }

        Ok(writes)
    }
}
