use crate::edid_override::DisplayBackend;

pub const VCP_BRIGHTNESS: u8 = 0x10;
pub const VCP_CONTRAST: u8 = 0x12;
pub const VCP_INPUT_SOURCE: u8 = 0x60;
pub const VCP_POWER_MODE: u8 = 0xD6;

pub fn set_brightness_safe(
    backend: &mut dyn DisplayBackend,
    display_id: &str,
    percent: u8,
) -> Result<(), String> {
    let clamped = percent.min(100) as u16;
    backend.ddc_set_vcp(display_id, VCP_BRIGHTNESS, clamped)
}

pub fn get_brightness_safe(backend: &dyn DisplayBackend, display_id: &str) -> Result<u8, String> {
    let val = backend.ddc_get_vcp(display_id, VCP_BRIGHTNESS)?;
    Ok((val.min(100)) as u8)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::edid_override::{DisplayInfo, MockDisplayBackend};

    #[test]
    fn mock_ddc_clamps_and_roundtrips_brightness() {
        let mut mock = MockDisplayBackend::new(vec![DisplayInfo {
            id: "disp:0".into(),
            name: "Mock Monitor".into(),
            is_internal_edp: false,
            max_pixel_clock_mhz: 1200,
        }]);
        set_brightness_safe(&mut mock, "disp:0", 140).unwrap();
        assert_eq!(get_brightness_safe(&mock, "disp:0").unwrap(), 100);
        set_brightness_safe(&mut mock, "disp:0", 65).unwrap();
        assert_eq!(get_brightness_safe(&mock, "disp:0").unwrap(), 65);
    }
}
