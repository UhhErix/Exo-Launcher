use super::{
    ClickMode, DpiStage, LodLevel, MouseConfig, MouseProtocolDriver, MouseSnapshot,
    WritePersistence,
};
use crate::registry::DriverFamily;
use crate::transport::{HidError, HidTransport};

pub const REPORT_ID: u8 = 0x04;
pub const PACKET_LEN: usize = 64;
pub const MAGIC_HOST: u8 = 0x5A;
pub const MAGIC_DEVICE: u8 = 0xA5;

pub const CMD_GET_STATUS: u8 = 0x01;
pub const CMD_SET_DPI: u8 = 0x10;
pub const CMD_SET_POLLING: u8 = 0x11;
pub const CMD_SET_SENSOR: u8 = 0x12;
pub const CMD_SET_CLICK_MODE: u8 = 0x1F;
pub const CMD_SET_RECEIVER_LED: u8 = 0x22;
pub const CMD_COMMIT_FLASH: u8 = 0x2F;
pub const CMD_BOOTLOADER_DFU: u8 = 0xFE;
pub const CMD_FACTORY_RESET: u8 = 0xFF;

pub struct FinalmouseDriver;

pub fn checksum(packet: &[u8]) -> u8 {
    packet.iter().fold(0u8, |acc, &b| acc ^ b)
}

pub fn encode_packet(cmd: u8, payload: &[u8]) -> Result<Vec<u8>, HidError> {
    if cmd == CMD_BOOTLOADER_DFU || cmd == CMD_FACTORY_RESET {
        return Err(HidError::BootloaderBlocked(format!(
            "Finalmouse opcode 0x{cmd:02X} is blocked in Wave 1"
        )));
    }
    if payload.len() > PACKET_LEN - 4 {
        return Err(HidError::InvalidReport(
            "Finalmouse payload overflow".into(),
        ));
    }
    let mut pkt = vec![0u8; PACKET_LEN];
    pkt[0] = MAGIC_HOST;
    pkt[1] = cmd;
    pkt[2] = payload.len() as u8;
    pkt[3..3 + payload.len()].copy_from_slice(payload);
    pkt[PACKET_LEN - 1] = checksum(&pkt[..PACKET_LEN - 1]);
    Ok(pkt)
}

pub fn verify_reply(reply: &[u8], expected_cmd: u8) -> Result<&[u8], HidError> {
    if reply.len() < PACKET_LEN {
        return Err(HidError::InvalidReport(format!(
            "Short Finalmouse reply: {} bytes",
            reply.len()
        )));
    }
    let frame = &reply[..PACKET_LEN];
    if frame[0] != MAGIC_DEVICE {
        return Err(HidError::InvalidReport(format!(
            "Bad Finalmouse header 0x{:02X}",
            frame[0]
        )));
    }
    if frame[1] != expected_cmd {
        return Err(HidError::InvalidReport(format!(
            "Unexpected Finalmouse command 0x{:02X} (expected 0x{expected_cmd:02X})",
            frame[1]
        )));
    }
    let expected_crc = checksum(&frame[..PACKET_LEN - 1]);
    if frame[PACKET_LEN - 1] != expected_crc {
        return Err(HidError::InvalidReport(format!(
            "Finalmouse checksum mismatch: got 0x{:02X}, expected 0x{expected_crc:02X}",
            frame[PACKET_LEN - 1]
        )));
    }
    let len = (frame[2] as usize).min(PACKET_LEN - 4);
    Ok(&frame[3..3 + len])
}

pub fn encode_click_mode_payload(right: ClickMode, left: ClickMode, debounce_ms: u8) -> [u8; 6] {
    fn mode_bits(mode: ClickMode) -> u8 {
        match mode {
            ClickMode::Standard => 0x00,
            ClickMode::IntentTrigger => 0x01,
            ClickMode::UltraClick => 0x03,
        }
    }
    let r = mode_bits(right);
    let l = mode_bits(left);
    [r, l, debounce_ms, debounce_ms, 0x01, 0x00]
}

pub fn decode_click_mode_byte(b: u8) -> ClickMode {
    if (b & 0x02) != 0 {
        ClickMode::UltraClick
    } else if (b & 0x01) != 0 {
        ClickMode::IntentTrigger
    } else {
        ClickMode::Standard
    }
}

pub fn parse_hex_rgb(hex: &str) -> (u8, u8, u8) {
    let clean = hex.trim().trim_start_matches('#');
    if clean.len() == 6
        && let (Ok(r), Ok(g), Ok(b)) = (
            u8::from_str_radix(&clean[0..2], 16),
            u8::from_str_radix(&clean[2..4], 16),
            u8::from_str_radix(&clean[4..6], 16),
        )
    {
        return (r, g, b);
    }
    (255, 255, 255)
}

fn send_and_read(
    transport: &mut dyn HidTransport,
    key: &str,
    cmd: u8,
    payload: &[u8],
) -> Result<Vec<u8>, HidError> {
    let pkt = encode_packet(cmd, payload)?;
    let mut last_err = None;
    for _ in 0..2 {
        transport.write_output(key, REPORT_ID, &pkt)?;
        match transport.read_input(key, 500) {
            Ok(reply) => {
                let _ = verify_reply(&reply, cmd)?;
                return Ok(reply);
            }
            Err(HidError::Timeout(t)) => last_err = Some(HidError::Timeout(t)),
            Err(e) => return Err(e),
        }
    }
    Err(last_err.unwrap_or_else(|| HidError::Timeout(key.into())))
}

impl MouseProtocolDriver for FinalmouseDriver {
    fn family(&self) -> DriverFamily {
        DriverFamily::Finalmouse
    }

    fn read_snapshot(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
    ) -> Result<MouseSnapshot, HidError> {
        let raw = send_and_read(transport, key, CMD_GET_STATUS, &[])?;
        let data = verify_reply(&raw, CMD_GET_STATUS)?;
        if data.len() < 16 {
            return Err(HidError::InvalidReport(
                "Finalmouse status payload too short".into(),
            ));
        }
        let firmware = format!("{}.{}.{}", data[0], data[1], data[2]);
        let battery_pct = Some(data[3].min(100));
        let charging = data[4] != 0;
        let active_stage = data[5].min(3);
        let dpi_active = u16::from_le_bytes([data[6], data[7]]) as u32;
        let polling_hz = u16::from_le_bytes([data[8], data[9]]);
        let lod = if data[10] >= 2 {
            LodLevel::High2Mm
        } else {
            LodLevel::Low1Mm
        };
        let motion_sync = data[11] != 0;
        let click_mode_right = decode_click_mode_byte(data[12]);
        let click_mode_left = decode_click_mode_byte(data[13]);
        let debounce_ms = data[14];
        let active_profile = data[15].min(3);
        let led_hex = if data.len() >= 19 {
            format!("#{:02X}{:02X}{:02X}", data[16], data[17], data[18])
        } else {
            "#FFFFFF".into()
        };

        let mut stages = vec![
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
        ];
        if let Some(slot) = stages.get_mut(active_stage as usize)
            && dpi_active >= 100
        {
            slot.dpi = dpi_active;
        }

        Ok(MouseSnapshot {
            firmware,
            battery_pct,
            charging,
            config: MouseConfig {
                active_profile,
                active_dpi_stage: active_stage,
                dpi_stages: stages,
                polling_hz: if polling_hz == 0 { 1000 } else { polling_hz },
                lod,
                motion_sync,
                angle_snapping: false,
                ripple_control: false,
                debounce_ms,
                click_mode_left,
                click_mode_right,
                sensor_rotation_deg: 0,
                receiver_led_hex: led_hex,
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

        if current.active_dpi_stage != target.active_dpi_stage
            || current.dpi_stages != target.dpi_stages
        {
            let dpi = target.active_dpi().min(u16::MAX as u32) as u16;
            let [lo, hi] = dpi.to_le_bytes();
            let _ = send_and_read(
                transport,
                key,
                CMD_SET_DPI,
                &[target.active_dpi_stage, lo, hi],
            )?;
            writes += 1;
        }

        if current.polling_hz != target.polling_hz {
            let [lo, hi] = target.polling_hz.to_le_bytes();
            let _ = send_and_read(transport, key, CMD_SET_POLLING, &[lo, hi])?;
            writes += 1;
        }

        if current.lod != target.lod || current.motion_sync != target.motion_sync {
            let lod_byte = match target.lod {
                LodLevel::Low1Mm => 1u8,
                LodLevel::High2Mm => 2u8,
            };
            let _ = send_and_read(
                transport,
                key,
                CMD_SET_SENSOR,
                &[lod_byte, target.motion_sync as u8],
            )?;
            writes += 1;
        }

        if current.click_mode_left != target.click_mode_left
            || current.click_mode_right != target.click_mode_right
            || current.debounce_ms != target.debounce_ms
        {
            let payload = encode_click_mode_payload(
                target.click_mode_right,
                target.click_mode_left,
                target.debounce_ms,
            );
            let _ = send_and_read(transport, key, CMD_SET_CLICK_MODE, &payload)?;
            writes += 1;
        }

        if current.receiver_led_hex != target.receiver_led_hex {
            let (r, g, b) = parse_hex_rgb(&target.receiver_led_hex);
            let _ = send_and_read(transport, key, CMD_SET_RECEIVER_LED, &[r, g, b])?;
            writes += 1;
        }

        if persistence == WritePersistence::CommitFlash {
            let _ = send_and_read(transport, key, CMD_COMMIT_FLASH, &[target.active_profile])?;
            writes += 1;
        }

        Ok(writes)
    }
}
