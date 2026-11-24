use crate::transport::Rgb8;
use serde::{Deserialize, Serialize};

pub const SPD_EEPROM_ADDR_START: u8 = 0x50;
pub const SPD_EEPROM_ADDR_END: u8 = 0x57;
pub const DDR4_PAGE_SELECT_ADDRS: [u8; 2] = [0x36, 0x37];
pub const TSOD_HUB_ADDR_START: u8 = 0x18;
pub const TSOD_HUB_ADDR_END: u8 = 0x1F;
pub const SMBUS_OPT_IN_NOTE: &str = "SMBus RAM RGB is opt-in only and is automatically disabled while any competitive anti-cheat game is running or launching.";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum JedecDramType {
    Ddr4,
    Ddr5,
    Unknown,
}

impl JedecDramType {
    pub const fn from_spd_byte(byte: u8) -> Self {
        match byte {
            0x0C => Self::Ddr4,
            0x12 => Self::Ddr5,
            _ => Self::Unknown,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SmbusAntiCheatClass {
    NoAntiCheat,
    OfflineAntiCheat,
    OnlineNonCompetitivePve,
    CompetitivePvpOnline,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SmbusGuardContext {
    pub opt_in: bool,
    pub active_or_launching_class: Option<SmbusAntiCheatClass>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SmbusError {
    SpdEepromForbidden(u8),
    Ddr4PageSelectForbidden(u8),
    Ddr4TsodForbidden(u8),
    AddressInvalidForController(u8),
    ControllerIdMismatch,
    OptInRequired,
    BlockedByAntiCheat,
    BusReadFailed,
    BusWriteFailed,
}

pub const fn is_hard_blocked_address(addr: u8) -> Option<SmbusError> {
    if addr >= SPD_EEPROM_ADDR_START && addr <= SPD_EEPROM_ADDR_END {
        return Some(SmbusError::SpdEepromForbidden(addr));
    }
    if addr == DDR4_PAGE_SELECT_ADDRS[0] || addr == DDR4_PAGE_SELECT_ADDRS[1] {
        return Some(SmbusError::Ddr4PageSelectForbidden(addr));
    }
    None
}

pub const fn is_tsod_range(addr: u8) -> bool {
    addr >= TSOD_HUB_ADDR_START && addr <= TSOD_HUB_ADDR_END
}

pub trait SmbusBus {
    fn read_byte(&self, addr: u8, reg: u8) -> Result<u8, SmbusError>;
    fn read_spd_dram_type(&self, slot_index: u8) -> Result<JedecDramType, SmbusError>;
    fn write_byte(&mut self, addr: u8, reg: u8, val: u8) -> Result<(), SmbusError>;
}

fn check_guard_and_address(
    bus: &dyn SmbusBus,
    guard: SmbusGuardContext,
    addr: u8,
    slot_index: u8,
) -> Result<(), SmbusError> {
    if !guard.opt_in {
        return Err(SmbusError::OptInRequired);
    }
    if matches!(
        guard.active_or_launching_class,
        Some(SmbusAntiCheatClass::CompetitivePvpOnline | SmbusAntiCheatClass::Unknown)
    ) {
        return Err(SmbusError::BlockedByAntiCheat);
    }
    if let Some(err) = is_hard_blocked_address(addr) {
        return Err(err);
    }
    if is_tsod_range(addr) {
        let dram = bus.read_spd_dram_type(slot_index)?;
        if dram != JedecDramType::Ddr5 {
            return Err(SmbusError::Ddr4TsodForbidden(addr));
        }
    }
    Ok(())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RamController {
    EneAura,
    CorsairDram,
    CrucialBallistix,
    KingstonFury,
    GskillEne,
}

impl RamController {
    fn allowed_addr_range(self, addr: u8) -> bool {
        match self {
            Self::EneAura | Self::GskillEne => (0x70..=0x77).contains(&addr),
            Self::CorsairDram => (0x58..=0x5F).contains(&addr) || (0x18..=0x1F).contains(&addr),
            Self::CrucialBallistix => (0x20..=0x27).contains(&addr),
            Self::KingstonFury => (0x60..=0x67).contains(&addr),
        }
    }

    fn verify_signature(self, bus: &dyn SmbusBus, addr: u8) -> Result<(), SmbusError> {
        let matched = match self {
            Self::EneAura => {
                bus.read_byte(addr, 0xA0)? == b'A'
                    && bus.read_byte(addr, 0xA1)? == b'U'
                    && bus.read_byte(addr, 0xA2)? == b'R'
                    && bus.read_byte(addr, 0xA3)? == b'A'
            }
            Self::GskillEne => {
                bus.read_byte(addr, 0xA0)? == b'E'
                    && bus.read_byte(addr, 0xA1)? == b'N'
                    && bus.read_byte(addr, 0xA2)? == b'E'
                    && bus.read_byte(addr, 0xA3)? == 0x01
            }
            Self::CorsairDram => {
                bus.read_byte(addr, 0x43)? == 0x1B && bus.read_byte(addr, 0x44)? == 0x0C
            }
            Self::CrucialBallistix => {
                bus.read_byte(addr, 0x00)? == 0x2C && bus.read_byte(addr, 0x01)? == 0xB1
            }
            Self::KingstonFury => {
                bus.read_byte(addr, 0x08)? == b'F'
                    && bus.read_byte(addr, 0x09)? == b'U'
                    && bus.read_byte(addr, 0x0A)? == b'R'
                    && bus.read_byte(addr, 0x0B)? == b'Y'
            }
        };
        if matched {
            Ok(())
        } else {
            Err(SmbusError::ControllerIdMismatch)
        }
    }

    fn documented_registers(self) -> (u8, u8, u8, u8, u8) {
        match self {
            Self::EneAura | Self::GskillEne => (0x21, 0x10, 0x11, 0x12, 0xA0),
            Self::CorsairDram => (0x31, 0x32, 0x33, 0x34, 0x35),
            Self::CrucialBallistix => (0x04, 0x05, 0x06, 0x07, 0x08),
            Self::KingstonFury => (0x09, 0x31, 0x32, 0x33, 0x08),
        }
    }

    pub fn apply_static_color(
        self,
        bus: &mut dyn SmbusBus,
        guard: SmbusGuardContext,
        addr: u8,
        slot_index: u8,
        color: Rgb8,
    ) -> Result<(), SmbusError> {
        check_guard_and_address(bus, guard, addr, slot_index)?;
        if !self.allowed_addr_range(addr) {
            return Err(SmbusError::AddressInvalidForController(addr));
        }
        self.verify_signature(bus, addr)?;
        let (mode_reg, r_reg, g_reg, b_reg, apply_reg) = self.documented_registers();
        bus.write_byte(addr, mode_reg, 0x01)?;
        bus.write_byte(addr, r_reg, color.r)?;
        bus.write_byte(addr, g_reg, color.g)?;
        bus.write_byte(addr, b_reg, color.b)?;
        bus.write_byte(addr, apply_reg, 0x01)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    struct MockSmbus {
        regs: HashMap<(u8, u8), u8>,
        spd_type: JedecDramType,
        writes: Vec<(u8, u8, u8)>,
    }

    impl MockSmbus {
        fn new(spd_type: JedecDramType) -> Self {
            Self {
                regs: HashMap::new(),
                spd_type,
                writes: Vec::new(),
            }
        }
    }

    impl SmbusBus for MockSmbus {
        fn read_byte(&self, addr: u8, reg: u8) -> Result<u8, SmbusError> {
            Ok(*self.regs.get(&(addr, reg)).unwrap_or(&0xFF))
        }

        fn read_spd_dram_type(&self, _slot_index: u8) -> Result<JedecDramType, SmbusError> {
            Ok(self.spd_type)
        }

        fn write_byte(&mut self, addr: u8, reg: u8, val: u8) -> Result<(), SmbusError> {
            self.writes.push((addr, reg, val));
            Ok(())
        }
    }

    fn opt_in_safe() -> SmbusGuardContext {
        SmbusGuardContext {
            opt_in: true,
            active_or_launching_class: None,
        }
    }

    #[test]
    fn id_mismatch_performs_zero_writes() {
        let mut bus = MockSmbus::new(JedecDramType::Ddr5);
        let res = RamController::EneAura.apply_static_color(
            &mut bus,
            opt_in_safe(),
            0x70,
            0,
            Rgb8::EXO_ACCENT,
        );
        assert_eq!(res, Err(SmbusError::ControllerIdMismatch));
        assert!(bus.writes.is_empty());
    }

    #[test]
    fn ddr4_at_0x18_is_refused_with_zero_writes_even_if_id_matches() {
        let mut bus = MockSmbus::new(JedecDramType::Ddr4);
        bus.regs.insert((0x18, 0x43), 0x1B);
        bus.regs.insert((0x18, 0x44), 0x0C);
        let res = RamController::CorsairDram.apply_static_color(
            &mut bus,
            opt_in_safe(),
            0x18,
            0,
            Rgb8::EXO_ACCENT,
        );
        assert_eq!(res, Err(SmbusError::Ddr4TsodForbidden(0x18)));
        assert!(bus.writes.is_empty());
    }

    #[test]
    fn ddr5_at_0x18_with_matching_id_writes_only_documented_regs() {
        let mut bus = MockSmbus::new(JedecDramType::Ddr5);
        bus.regs.insert((0x18, 0x43), 0x1B);
        bus.regs.insert((0x18, 0x44), 0x0C);
        RamController::CorsairDram
            .apply_static_color(&mut bus, opt_in_safe(), 0x18, 0, Rgb8::new(10, 20, 30))
            .expect("DDR5 + valid Corsair ID should succeed");
        assert_eq!(
            bus.writes,
            vec![
                (0x18, 0x31, 0x01),
                (0x18, 0x32, 10),
                (0x18, 0x33, 20),
                (0x18, 0x34, 30),
                (0x18, 0x35, 0x01),
            ]
        );
    }

    #[test]
    fn spd_and_ddr4_page_select_are_hard_blocked() {
        let mut bus = MockSmbus::new(JedecDramType::Ddr5);
        for addr in SPD_EEPROM_ADDR_START..=SPD_EEPROM_ADDR_END {
            assert_eq!(
                RamController::EneAura.apply_static_color(
                    &mut bus,
                    opt_in_safe(),
                    addr,
                    0,
                    Rgb8::WHITE
                ),
                Err(SmbusError::SpdEepromForbidden(addr))
            );
        }
        for addr in DDR4_PAGE_SELECT_ADDRS {
            assert_eq!(
                RamController::EneAura.apply_static_color(
                    &mut bus,
                    opt_in_safe(),
                    addr,
                    0,
                    Rgb8::WHITE
                ),
                Err(SmbusError::Ddr4PageSelectForbidden(addr))
            );
        }
        assert!(bus.writes.is_empty());
    }

    #[test]
    fn competitive_anti_cheat_and_missing_opt_in_block_bus_access() {
        let mut bus = MockSmbus::new(JedecDramType::Ddr5);
        bus.regs.insert((0x60, 0x08), b'F');
        bus.regs.insert((0x60, 0x09), b'U');
        bus.regs.insert((0x60, 0x0A), b'R');
        bus.regs.insert((0x60, 0x0B), b'Y');
        let no_opt = SmbusGuardContext {
            opt_in: false,
            active_or_launching_class: None,
        };
        assert_eq!(
            RamController::KingstonFury.apply_static_color(&mut bus, no_opt, 0x60, 0, Rgb8::WHITE),
            Err(SmbusError::OptInRequired)
        );
        let pvp = SmbusGuardContext {
            opt_in: true,
            active_or_launching_class: Some(SmbusAntiCheatClass::CompetitivePvpOnline),
        };
        assert_eq!(
            RamController::KingstonFury.apply_static_color(&mut bus, pvp, 0x60, 0, Rgb8::WHITE),
            Err(SmbusError::BlockedByAntiCheat)
        );
        assert!(bus.writes.is_empty());
    }
}
