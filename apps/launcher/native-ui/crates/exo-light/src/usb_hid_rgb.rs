use crate::transport::Rgb8;
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum HidRgbProtocol {
    FinalmouseReceiver,
    WootingDirect,
    LogitechHidPlusPlus,
    RazerChromaHid,
    CorsairDirectHid,
}

pub fn encode_single_zone_hid_packet(proto: HidRgbProtocol, color: Rgb8) -> Vec<u8> {
    match proto {
        HidRgbProtocol::FinalmouseReceiver => vec![0x00, 0x04, 0x01, color.r, color.g, color.b],
        HidRgbProtocol::WootingDirect => {
            let mut pkt = vec![0u8; 8];
            pkt[0] = 0xD0;
            pkt[1] = 0xDA;
            pkt[2] = 0x1E;
            pkt[3] = color.r;
            pkt[4] = color.g;
            pkt[5] = color.b;
            pkt
        }
        HidRgbProtocol::LogitechHidPlusPlus => {
            vec![
                0x11, 0xFF, 0x0E, 0x3A, 0x00, 0x01, color.r, color.g, color.b,
            ]
        }
        HidRgbProtocol::RazerChromaHid => {
            let mut pkt = vec![0u8; 16];
            pkt[0] = 0x00;
            pkt[1] = 0x1F;
            pkt[5] = 0x0F;
            pkt[6] = 0x02;
            pkt[7] = color.r;
            pkt[8] = color.g;
            pkt[9] = color.b;
            pkt
        }
        HidRgbProtocol::CorsairDirectHid => {
            vec![0x08, 0x06, 0x01, 0x00, color.r, color.g, color.b]
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encodes_vendor_hid_rgb_packets_cleanly() {
        let color = Rgb8::new(12, 34, 56);
        for proto in [
            HidRgbProtocol::FinalmouseReceiver,
            HidRgbProtocol::WootingDirect,
            HidRgbProtocol::LogitechHidPlusPlus,
            HidRgbProtocol::RazerChromaHid,
            HidRgbProtocol::CorsairDirectHid,
        ] {
            let pkt = encode_single_zone_hid_packet(proto, color);
            assert!(pkt.windows(3).any(|w| w == [12, 34, 56]));
        }
    }
}
