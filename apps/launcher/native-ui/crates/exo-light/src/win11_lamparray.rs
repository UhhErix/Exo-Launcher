use crate::transport::Rgb8;
use serde::{Deserialize, Serialize};

pub const HID_USAGE_PAGE_LIGHTING: u16 = 0x0059;
pub const HID_USAGE_LAMP_ARRAY: u16 = 0x0001;
pub const REPORT_ID_LAMP_MULTI_UPDATE: u8 = 0x04;
pub const REPORT_ID_LAMP_RANGE_UPDATE: u8 = 0x05;
pub const REPORT_ID_LAMP_ARRAY_CONTROL: u8 = 0x06;
pub const LAMP_UPDATE_COMPLETE_FLAG: u8 = 0x01;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LampArrayAttributes {
    pub lamp_count: u16,
    pub bounding_box_width_um: u32,
    pub bounding_box_height_um: u32,
    pub bounding_box_depth_um: u32,
    pub min_update_interval_us: u32,
}

pub fn encode_range_update_report(
    start_id: u16,
    end_id: u16,
    color: Rgb8,
    intensity: u8,
    complete: bool,
) -> [u8; 10] {
    let mut buf = [0u8; 10];
    buf[0] = REPORT_ID_LAMP_RANGE_UPDATE;
    buf[1] = if complete {
        LAMP_UPDATE_COMPLETE_FLAG
    } else {
        0
    };
    buf[2..4].copy_from_slice(&start_id.to_le_bytes());
    buf[4..6].copy_from_slice(&end_id.to_le_bytes());
    buf[6] = color.r;
    buf[7] = color.g;
    buf[8] = color.b;
    buf[9] = intensity;
    buf
}

pub fn encode_control_report(autonomous_mode: bool) -> [u8; 2] {
    [REPORT_ID_LAMP_ARRAY_CONTROL, u8::from(autonomous_mode)]
}

pub fn encode_multi_update_chunk(start_lamp_id: u16, colors: &[Rgb8], complete: bool) -> Vec<u8> {
    let count = colors.len().min(8);
    let mut buf = vec![0u8; 3 + 16 + 32];
    buf[0] = REPORT_ID_LAMP_MULTI_UPDATE;
    buf[1] = count as u8;
    buf[2] = if complete {
        LAMP_UPDATE_COMPLETE_FLAG
    } else {
        0
    };
    for (i, color) in colors.iter().enumerate().take(count) {
        let id = start_lamp_id.saturating_add(i as u16);
        let id_off = 3 + i * 2;
        buf[id_off..id_off + 2].copy_from_slice(&id.to_le_bytes());
        let c_off = 19 + i * 4;
        buf[c_off] = color.r;
        buf[c_off + 1] = color.g;
        buf[c_off + 2] = color.b;
        buf[c_off + 3] = 255;
    }
    buf
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_hid_lamparray_range_and_control_reports() {
        let range = encode_range_update_report(0, 63, Rgb8::new(10, 20, 30), 255, true);
        assert_eq!(range[0], REPORT_ID_LAMP_RANGE_UPDATE);
        assert_eq!(range[1], LAMP_UPDATE_COMPLETE_FLAG);
        assert_eq!(u16::from_le_bytes([range[4], range[5]]), 63);
        assert_eq!(&range[6..9], &[10, 20, 30]);
        assert_eq!(
            encode_control_report(false),
            [REPORT_ID_LAMP_ARRAY_CONTROL, 0]
        );
        assert_eq!(
            encode_control_report(true),
            [REPORT_ID_LAMP_ARRAY_CONTROL, 1]
        );
    }
}
