use super::{
    ClickMode, DpiStage, LodLevel, MouseConfig, MouseProtocolDriver, MouseSnapshot,
    WritePersistence,
};
use crate::registry::DriverFamily;
use crate::transport::{HidError, HidTransport};

pub const REPORT_ID: u8 = 0x08;
pub const PACKET_LEN: usize = 16;

pub const OP_GET_BATTERY: u8 = 0x04;
pub const OP_READ_CONFIG: u8 = 0x08;
pub const OP_WRITE_CONFIG: u8 = 0x07;
pub const OP_COMMIT_EEPROM: u8 = 0x09;
pub const OP_BOOTLOADER: u8 = 0xFA;

pub struct OemNordicDriver;

pub fn checksum(packet: &[u8]) -> u8 {
    let sum: u8 = packet[..PACKET_LEN - 1]
        .iter()
        .fold(0u8, |acc, &b| acc.wrapping_add(b));
    0x55u8.wrapping_sub(sum)
}

pub fn encode_packet(opcode: u8, addr: u16, payload: &[u8]) -> Result<Vec<u8>, HidError> {
    if opcode == OP_BOOTLOADER {
        return Err(HidError::BootloaderBlocked(
            "OEM Nordic bootloader opcode 0xFA is blocked in Wave 1".into(),
        ));
    }
    if payload.len() > 10 {
        return Err(HidError::InvalidReport(
            "OEM Nordic payload exceeds 10 bytes".into(),
        ));
    }
    let mut pkt = vec![0u8; PACKET_LEN];
    pkt[0] = opcode;
    let [hi, lo] = addr.to_be_bytes();
    pkt[2] = hi;
    pkt[3] = lo;
    pkt[4] = payload.len() as u8;
    pkt[5..5 + payload.len()].copy_from_slice(payload);
    pkt[PACKET_LEN - 1] = checksum(&pkt);
    Ok(pkt)
}

pub fn verify_reply(raw: &[u8], expected_opcode: u8) -> Result<&[u8], HidError> {
    let pkt = if raw.len() == PACKET_LEN + 1 && raw[0] == REPORT_ID {
        &raw[1..]
    } else if raw.len() >= PACKET_LEN {
        &raw[..PACKET_LEN]
    } else {
        return Err(HidError::InvalidReport(format!(
            "Short OEM Nordic reply: {} bytes",
            raw.len()
        )));
    };
    if pkt[0] != expected_opcode {
        return Err(HidError::InvalidReport(format!(
            "OEM Nordic opcode mismatch: 0x{:02X}",
            pkt[0]
        )));
    }
    let expected = checksum(pkt);
    if pkt[PACKET_LEN - 1] != expected {
        return Err(HidError::InvalidReport(format!(
            "OEM Nordic checksum mismatch: got 0x{:02X}, expected 0x{expected:02X}",
            pkt[PACKET_LEN - 1]
        )));
    }
    let len = (pkt[4] as usize).min(10);
    Ok(&pkt[5..5 + len])
}

pub fn encode_polling_code(hz: u16) -> u8 {
    match hz {
        125 => 0x08,
        250 => 0x04,
        500 => 0x02,
        1000 => 0x01,
        2000 => 0x10,
        4000 => 0x20,
        8000 => 0x40,
        _ => 0x01,
    }
}

pub fn decode_polling_code(code: u8) -> u16 {
    match code {
        0x08 => 125,
        0x04 => 250,
        0x02 => 500,
        0x01 => 1000,
        0x10 => 2000,
        0x20 => 4000,
        0x40 => 8000,
        _ => 1000,
    }
}

fn exchange(
    transport: &mut dyn HidTransport,
    key: &str,
    opcode: u8,
    addr: u16,
    payload: &[u8],
) -> Result<Vec<u8>, HidError> {
    let pkt = encode_packet(opcode, addr, payload)?;
    let mut last_err = None;
    for _ in 0..2 {
        transport.write_output(key, REPORT_ID, &pkt)?;
        match transport.read_input(key, 500) {
            Ok(rep) => return Ok(verify_reply(&rep, opcode)?.to_vec()),
            Err(HidError::Timeout(t)) => last_err = Some(HidError::Timeout(t)),
            Err(e) => return Err(e),
        }
    }
    Err(last_err.unwrap_or_else(|| HidError::Timeout(key.into())))
}

impl MouseProtocolDriver for OemNordicDriver {
    fn family(&self) -> DriverFamily {
        DriverFamily::OemNordic
    }

    fn read_snapshot(
        &self,
        transport: &mut dyn HidTransport,
        key: &str,
    ) -> Result<MouseSnapshot, HidError> {
        let batt = exchange(transport, key, OP_GET_BATTERY, 0x0000, &[])?;
        let battery_pct = batt.first().copied().map(|b| b.min(100));
        let charging = batt.get(1).copied().unwrap_or(0) != 0;

        let cfg = exchange(transport, key, OP_READ_CONFIG, 0x0000, &[])?;
        if cfg.len() < 8 {
            return Err(HidError::InvalidReport(
                "OEM Nordic config reply too short".into(),
            ));
        }
        let polling_hz = decode_polling_code(cfg[0]);
        let active_stage = cfg[1].min(5);
        let dpi = u16::from_le_bytes([cfg[2], cfg[3]]) as u32;
        let lod = if cfg[4] >= 2 {
            LodLevel::High2Mm
        } else {
            LodLevel::Low1Mm
        };
        let flags = cfg[5];
        let motion_sync = (flags & 0x01) != 0;
        let angle_snapping = (flags & 0x02) != 0;
        let ripple_control = (flags & 0x04) != 0;
        let debounce_ms = cfg[6].min(20);
        let active_profile = cfg[7].min(3);

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
            && dpi >= 50
        {
            slot.dpi = dpi;
        }

        Ok(MouseSnapshot {
            firmware: "Nordic 1.0".into(),
            battery_pct,
            charging,
            config: MouseConfig {
                active_profile,
                active_dpi_stage: active_stage,
                dpi_stages: stages,
                polling_hz,
                lod,
                motion_sync,
                angle_snapping,
                ripple_control,
                debounce_ms,
                click_mode_left: ClickMode::Standard,
                click_mode_right: ClickMode::Standard,
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
        if current == target && persistence == WritePersistence::VolatileRam {
            return Ok(0);
        }
        let mut writes = 0usize;
        if current != target {
            let dpi = target.active_dpi().min(u16::MAX as u32) as u16;
            let [lo, hi] = dpi.to_le_bytes();
            let lod = match target.lod {
                LodLevel::Low1Mm => 1u8,
                LodLevel::High2Mm => 2u8,
            };
            let flags = (target.motion_sync as u8)
                | ((target.angle_snapping as u8) << 1)
                | ((target.ripple_control as u8) << 2);
            let payload = [
                encode_polling_code(target.polling_hz),
                target.active_dpi_stage,
                lo,
                hi,
                lod,
                flags,
                target.debounce_ms,
                target.active_profile,
            ];
            let _ = exchange(transport, key, OP_WRITE_CONFIG, 0x0000, &payload)?;
            writes += 1;
        }
        if persistence == WritePersistence::CommitFlash {
            let _ = exchange(
                transport,
                key,
                OP_COMMIT_EEPROM,
                0x0000,
                &[target.active_profile],
            )?;
            writes += 1;
        }
        Ok(writes)
    }
}
