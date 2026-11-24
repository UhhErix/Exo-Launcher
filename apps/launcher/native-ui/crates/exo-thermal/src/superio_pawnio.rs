use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SuperIoChip {
    Nct6775,
    Nct6798,
    It8688,
}

impl SuperIoChip {
    pub const fn expected_chip_id(self) -> u16 {
        match self {
            Self::Nct6775 => 0xB470,
            Self::Nct6798 => 0xD428,
            Self::It8688 => 0x8688,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SuperIoError {
    OptInRequired,
    LaptopEcLocked,
    BlockedByCompetitiveAntiCheat,
    ChipIdMismatch { expected: u16, actual: u16 },
    ChannelOutOfRange(u8),
}

pub trait SuperIoBus {
    fn read_chip_id(&self) -> u16;
    fn write_pwm_reg(&mut self, channel: u8, duty: u8);
    fn restore_bios_control(&mut self, channel: u8);
}

#[derive(Debug, Clone, Copy)]
pub struct SuperIoGuard {
    pub opt_in: bool,
    pub is_laptop: bool,
    pub competitive_ac_active: bool,
}

pub fn write_verified_superio_pwm(
    bus: &mut dyn SuperIoBus,
    chip: SuperIoChip,
    guard: SuperIoGuard,
    channel: u8,
    pwm_pct: u8,
) -> Result<(), SuperIoError> {
    if !guard.opt_in {
        return Err(SuperIoError::OptInRequired);
    }
    if guard.is_laptop {
        return Err(SuperIoError::LaptopEcLocked);
    }
    if guard.competitive_ac_active {
        return Err(SuperIoError::BlockedByCompetitiveAntiCheat);
    }
    if channel >= 7 {
        return Err(SuperIoError::ChannelOutOfRange(channel));
    }
    let actual = bus.read_chip_id();
    let expected = chip.expected_chip_id();
    if actual != expected {
        return Err(SuperIoError::ChipIdMismatch { expected, actual });
    }
    let duty = ((pwm_pct.min(100) as u16 * 255) / 100) as u8;
    bus.write_pwm_reg(channel, duty);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct MockSuperIo {
        chip_id: u16,
        writes: Vec<(u8, u8)>,
        restores: Vec<u8>,
    }

    impl SuperIoBus for MockSuperIo {
        fn read_chip_id(&self) -> u16 {
            self.chip_id
        }
        fn write_pwm_reg(&mut self, channel: u8, duty: u8) {
            self.writes.push((channel, duty));
        }
        fn restore_bios_control(&mut self, channel: u8) {
            self.restores.push(channel);
        }
    }

    #[test]
    fn refuses_write_on_chip_mismatch_laptop_or_competitive_ac() {
        let mut bus = MockSuperIo {
            chip_id: 0x0000,
            writes: Vec::new(),
            restores: Vec::new(),
        };
        let ok_guard = SuperIoGuard {
            opt_in: true,
            is_laptop: false,
            competitive_ac_active: false,
        };
        assert!(matches!(
            write_verified_superio_pwm(&mut bus, SuperIoChip::Nct6798, ok_guard, 0, 50),
            Err(SuperIoError::ChipIdMismatch { .. })
        ));
        bus.chip_id = SuperIoChip::Nct6798.expected_chip_id();
        assert_eq!(
            write_verified_superio_pwm(
                &mut bus,
                SuperIoChip::Nct6798,
                SuperIoGuard {
                    is_laptop: true,
                    ..ok_guard
                },
                0,
                50
            ),
            Err(SuperIoError::LaptopEcLocked)
        );
        assert_eq!(
            write_verified_superio_pwm(
                &mut bus,
                SuperIoChip::Nct6798,
                SuperIoGuard {
                    competitive_ac_active: true,
                    ..ok_guard
                },
                0,
                50
            ),
            Err(SuperIoError::BlockedByCompetitiveAntiCheat)
        );
        assert!(bus.writes.is_empty());
    }
}
