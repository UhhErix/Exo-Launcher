use crate::transport::Rgb8;
use serde::{Deserialize, Serialize};

pub const PAWNIO_DEVICE_PATH: &str = r"\\.\PawnIO";

pub const SPD_EEPROM_ADDR_START: u8 = 0x50;
pub const SPD_EEPROM_ADDR_END: u8 = 0x57;

pub const ALLOWED_RGB_SMBUS_ADDRS: &[u8] = &[
    0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
    0x4E, 0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76,
    0x77,
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SmbusError {
    SpdEepromForbidden(u8),
    AddressNotAllowlisted(u8),
    DriverUnavailable,
}

pub const fn is_spd_eeprom_address(addr: u8) -> bool {
    addr >= SPD_EEPROM_ADDR_START && addr <= SPD_EEPROM_ADDR_END
}

pub fn validate_smbus_write_address(addr: u8) -> Result<(), SmbusError> {
    if is_spd_eeprom_address(addr) {
        return Err(SmbusError::SpdEepromForbidden(addr));
    }
    if !ALLOWED_RGB_SMBUS_ADDRS.contains(&addr) {
        return Err(SmbusError::AddressNotAllowlisted(addr));
    }
    Ok(())
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SmbusWriteCommand {
    pub address: u8,
    pub register: u8,
    pub payload: [u8; 3],
}

pub fn build_safe_rgb_smbus_write(
    addr: u8,
    register: u8,
    color: Rgb8,
) -> Result<SmbusWriteCommand, SmbusError> {
    validate_smbus_write_address(addr)?;
    Ok(SmbusWriteCommand {
        address: addr,
        register,
        payload: [color.r, color.g, color.b],
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn spd_eeprom_addresses_are_unconditionally_blocked() {
        for addr in SPD_EEPROM_ADDR_START..=SPD_EEPROM_ADDR_END {
            assert!(is_spd_eeprom_address(addr));
            assert_eq!(
                build_safe_rgb_smbus_write(addr, 0x00, Rgb8::WHITE),
                Err(SmbusError::SpdEepromForbidden(addr))
            );
        }
    }

    #[test]
    fn allowlist_permits_known_rgb_controllers_and_blocks_unknown() {
        assert!(build_safe_rgb_smbus_write(0x18, 0x01, Rgb8::EXO_ACCENT).is_ok());
        assert_eq!(
            build_safe_rgb_smbus_write(0x44, 0x01, Rgb8::EXO_ACCENT),
            Err(SmbusError::AddressNotAllowlisted(0x44))
        );
    }

    #[test]
    fn allowlist_contains_no_spd_addresses() {
        for &addr in ALLOWED_RGB_SMBUS_ADDRS {
            assert!(!is_spd_eeprom_address(addr));
        }
    }
}
