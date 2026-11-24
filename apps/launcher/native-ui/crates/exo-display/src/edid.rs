use serde::{Deserialize, Serialize};

pub const EDID_HEADER: [u8; 8] = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
pub const EDID_BLOCK_SIZE: usize = 128;

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Chromaticity {
    pub rx: f32,
    pub ry: f32,
    pub gx: f32,
    pub gy: f32,
    pub bx: f32,
    pub by: f32,
    pub wx: f32,
    pub wy: f32,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct VrrRange {
    pub min_hz: u8,
    pub max_hz: u8,
    pub max_pixel_clock_mhz: u16,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HdrStaticMetadata {
    pub supports_pq: bool,
    pub max_luminance_nits: f32,
    pub max_frame_avg_nits: f32,
    pub min_luminance_nits: f32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ParsedEdid {
    pub manufacturer: String,
    pub product_code: u16,
    pub monitor_name: Option<String>,
    pub width_cm: u8,
    pub height_cm: u8,
    pub edid_version: u8,
    pub edid_revision: u8,
    pub extension_blocks: u8,
    pub chromaticity: Chromaticity,
    pub vrr_range: Option<VrrRange>,
    pub hdr_metadata: Option<HdrStaticMetadata>,
    pub has_cinema_4096p: bool,
    pub likely_oled: bool,
    pub checksums_valid: bool,
}

pub fn verify_block_checksum(block: &[u8]) -> bool {
    block.len() == EDID_BLOCK_SIZE && block.iter().fold(0u8, |acc, &b| acc.wrapping_add(b)) == 0
}

pub fn fix_block_checksum(block: &mut [u8]) {
    if block.len() != EDID_BLOCK_SIZE {
        return;
    }
    let sum = block[..127].iter().fold(0u8, |acc, &b| acc.wrapping_add(b));
    block[127] = 0u8.wrapping_sub(sum);
}

pub fn parse_edid(raw: &[u8]) -> Result<ParsedEdid, String> {
    if raw.len() < EDID_BLOCK_SIZE || !raw.len().is_multiple_of(EDID_BLOCK_SIZE) {
        return Err(format!("invalid EDID length {}", raw.len()));
    }
    if raw[0..8] != EDID_HEADER {
        return Err("missing EDID 1.4 magic header".into());
    }
    let blocks = raw.len() / EDID_BLOCK_SIZE;
    let checksums_valid = (0..blocks)
        .all(|i| verify_block_checksum(&raw[i * EDID_BLOCK_SIZE..(i + 1) * EDID_BLOCK_SIZE]));

    let mfg_raw = u16::from_be_bytes([raw[8], raw[9]]);
    let c1 = (((mfg_raw >> 10) & 0x1F) as u8 + b'@') as char;
    let c2 = (((mfg_raw >> 5) & 0x1F) as u8 + b'@') as char;
    let c3 = ((mfg_raw & 0x1F) as u8 + b'@') as char;
    let manufacturer = format!("{c1}{c2}{c3}");
    let product_code = u16::from_le_bytes([raw[10], raw[11]]);
    let edid_version = raw[18];
    let edid_revision = raw[19];
    let width_cm = raw[21];
    let height_cm = raw[22];
    let extension_blocks = raw[126];

    let chromaticity = parse_chromaticity(raw);
    let mut monitor_name = None;
    let mut vrr_range = None;
    let mut has_cinema_4096p = false;

    for d in 0..4 {
        let off = 54 + d * 18;
        let desc = &raw[off..off + 18];
        if desc[0] == 0 && desc[1] == 0 {
            match desc[3] {
                0xFC => {
                    let s = String::from_utf8_lossy(&desc[5..18])
                        .trim_end_matches(['\n', '\r', ' ', '\0'])
                        .to_string();
                    if !s.is_empty() {
                        monitor_name = Some(s);
                    }
                }
                0xFD => {
                    vrr_range = Some(VrrRange {
                        min_hz: desc[5],
                        max_hz: desc[6],
                        max_pixel_clock_mhz: (desc[9] as u16) * 10,
                    });
                }
                _ => {}
            }
        } else if is_4096p_dtd(desc) {
            has_cinema_4096p = true;
        }
    }

    let mut hdr_metadata = None;
    for b in 1..blocks {
        let ext = &raw[b * EDID_BLOCK_SIZE..(b + 1) * EDID_BLOCK_SIZE];
        if ext[0] == 0x02 {
            parse_cta861(ext, &mut hdr_metadata, &mut has_cinema_4096p);
        }
    }

    let likely_oled = hdr_metadata.is_some_and(|h| h.supports_pq && h.min_luminance_nits <= 0.001)
        || monitor_name
            .as_deref()
            .is_some_and(|n| n.to_ascii_uppercase().contains("OLED"));

    Ok(ParsedEdid {
        manufacturer,
        product_code,
        monitor_name,
        width_cm,
        height_cm,
        edid_version,
        edid_revision,
        extension_blocks,
        chromaticity,
        vrr_range,
        hdr_metadata,
        has_cinema_4096p,
        likely_oled,
        checksums_valid,
    })
}

fn parse_chromaticity(raw: &[u8]) -> Chromaticity {
    let rg_low = raw[25];
    let bw_low = raw[26];
    let decode = |hi: u8, lo_bits: u8| -> f32 {
        let v = ((hi as u16) << 2) | (lo_bits as u16 & 0x03);
        (v as f32) / 1024.0
    };
    Chromaticity {
        rx: decode(raw[27], rg_low >> 6),
        ry: decode(raw[28], rg_low >> 4),
        gx: decode(raw[29], rg_low >> 2),
        gy: decode(raw[30], rg_low),
        bx: decode(raw[31], bw_low >> 6),
        by: decode(raw[32], bw_low >> 4),
        wx: decode(raw[33], bw_low >> 2),
        wy: decode(raw[34], bw_low),
    }
}

fn is_4096p_dtd(desc: &[u8]) -> bool {
    if desc.len() < 18 || (desc[0] == 0 && desc[1] == 0) {
        return false;
    }
    let h_active = (desc[2] as u16) | (((desc[4] as u16) & 0xF0) << 4);
    let v_active = (desc[5] as u16) | (((desc[7] as u16) & 0xF0) << 4);
    h_active == 4096 && v_active == 2160
}

fn parse_cta861(
    ext: &[u8],
    hdr_metadata: &mut Option<HdrStaticMetadata>,
    has_cinema_4096p: &mut bool,
) {
    let dtd_start = (ext[2] as usize).min(127);
    let mut pos = 4usize;
    while pos < dtd_start {
        let header = ext[pos];
        let tag = (header >> 5) & 0x07;
        let len = (header & 0x1F) as usize;
        pos += 1;
        if pos + len > dtd_start {
            break;
        }
        let payload = &ext[pos..pos + len];
        if tag == 2 {
            for &svd in payload {
                let vic = svd & 0x7F;
                if (98..=102).contains(&vic) {
                    *has_cinema_4096p = true;
                }
            }
        } else if tag == 7 && len >= 3 && payload[0] == 0x06 {
            let eotf = payload[1];
            let supports_pq = (eotf & 0x04) != 0;
            let max_l = payload
                .get(3)
                .map(|&v| 50.0 * 2.0f32.powf((v as f32) / 32.0))
                .unwrap_or(0.0);
            let max_fa = payload
                .get(4)
                .map(|&v| 50.0 * 2.0f32.powf((v as f32) / 32.0))
                .unwrap_or(0.0);
            let min_l = payload
                .get(5)
                .map(|&v| max_l * ((v as f32) / 255.0).powi(2) / 100.0)
                .unwrap_or(0.0);
            *hdr_metadata = Some(HdrStaticMetadata {
                supports_pq,
                max_luminance_nits: max_l,
                max_frame_avg_nits: max_fa,
                min_luminance_nits: min_l,
            });
        }
        pos += len;
    }

    let mut d = dtd_start;
    while d >= 4 && d + 18 <= 127 {
        let desc = &ext[d..d + 18];
        if desc[0] == 0 && desc[1] == 0 {
            break;
        }
        if is_4096p_dtd(desc) {
            *has_cinema_4096p = true;
        }
        d += 18;
    }
}

pub fn strip_4096p_modes(raw: &[u8]) -> Result<Vec<u8>, String> {
    if raw.len() < EDID_BLOCK_SIZE || !raw.len().is_multiple_of(EDID_BLOCK_SIZE) {
        return Err("invalid EDID length".into());
    }
    let mut out = raw.to_vec();
    let blocks = out.len() / EDID_BLOCK_SIZE;
    for b in 0..blocks {
        let blk = &mut out[b * EDID_BLOCK_SIZE..(b + 1) * EDID_BLOCK_SIZE];
        if b == 0 {
            for d in 0..4 {
                let off = 54 + d * 18;
                if is_4096p_dtd(&blk[off..off + 18]) {
                    blk[off..off + 18].fill(0);
                    blk[off + 3] = 0x10;
                }
            }
        } else if blk[0] == 0x02 {
            let dtd_start = (blk[2] as usize).min(127);
            let mut pos = 4usize;
            while pos < dtd_start {
                let header = blk[pos];
                let tag = (header >> 5) & 0x07;
                let len = (header & 0x1F) as usize;
                pos += 1;
                if pos + len > dtd_start {
                    break;
                }
                if tag == 2 {
                    for byte in &mut blk[pos..pos + len] {
                        let vic = *byte & 0x7F;
                        if (98..=102).contains(&vic) {
                            *byte = 0;
                        }
                    }
                }
                pos += len;
            }
            let mut d = dtd_start;
            while d >= 4 && d + 18 <= 127 {
                if is_4096p_dtd(&blk[d..d + 18]) {
                    blk[d..d + 18].fill(0);
                }
                d += 18;
            }
        }
        fix_block_checksum(blk);
    }
    Ok(out)
}

pub fn tune_vrr_range(raw: &[u8], min_hz: u8, max_hz: u8) -> Result<Vec<u8>, String> {
    if min_hz < 24 || max_hz <= min_hz {
        return Err("invalid VRR range".into());
    }
    if raw.len() < EDID_BLOCK_SIZE || !raw.len().is_multiple_of(EDID_BLOCK_SIZE) {
        return Err("invalid EDID length".into());
    }
    let mut out = raw.to_vec();
    let base = &mut out[..EDID_BLOCK_SIZE];
    let mut updated = false;
    for d in 0..4 {
        let off = 54 + d * 18;
        if base[off] == 0 && base[off + 1] == 0 && base[off + 3] == 0xFD {
            base[off + 5] = min_hz;
            base[off + 6] = max_hz;
            updated = true;
            break;
        }
    }
    if !updated {
        return Err("no display range limits descriptor (0xFD) found".into());
    }
    fix_block_checksum(base);
    Ok(out)
}

pub fn synthetic_oled_edid_fixture(include_4096p: bool) -> Vec<u8> {
    let mut raw = vec![0u8; 256];
    raw[0..8].copy_from_slice(&EDID_HEADER);
    raw[8] = 0x1E;
    raw[9] = 0x6D;
    raw[10] = 0x34;
    raw[11] = 0x12;
    raw[18] = 1;
    raw[19] = 4;
    raw[21] = 60;
    raw[22] = 34;
    raw[27] = 0xA8;
    raw[28] = 0x54;
    raw[29] = 0x42;
    raw[30] = 0xB0;
    raw[31] = 0x26;
    raw[32] = 0x0F;
    raw[33] = 0x50;
    raw[34] = 0x54;
    raw[126] = 1;

    let name_off = 54;
    raw[name_off + 3] = 0xFC;
    raw[name_off + 5..name_off + 14].copy_from_slice(b"EXO OLED\n");

    let range_off = 72;
    raw[range_off + 3] = 0xFD;
    raw[range_off + 5] = 48;
    raw[range_off + 6] = 240;
    raw[range_off + 9] = 120;

    fix_block_checksum(&mut raw[..128]);

    let ext = &mut raw[128..256];
    ext[0] = 0x02;
    ext[1] = 0x03;
    ext[2] = 18;
    ext[4] = (2 << 5) | 2;
    ext[5] = 97;
    ext[6] = if include_4096p { 102 } else { 95 };
    ext[7] = (7 << 5) | 6;
    ext[8] = 0x06;
    ext[9] = 0x04;
    ext[10] = 0x01;
    ext[11] = 138;
    ext[12] = 100;
    ext[13] = 0;
    fix_block_checksum(ext);
    raw
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_oled_edid_strips_4096p_and_tunes_vrr_with_valid_checksums() {
        let raw = synthetic_oled_edid_fixture(true);
        let parsed = parse_edid(&raw).expect("parse ok");
        assert!(parsed.checksums_valid);
        assert!(parsed.has_cinema_4096p);
        assert!(parsed.likely_oled);
        assert_eq!(parsed.vrr_range.unwrap().min_hz, 48);
        assert_eq!(parsed.vrr_range.unwrap().max_hz, 240);

        let scrubbed = strip_4096p_modes(&raw).expect("strip ok");
        let parsed2 = parse_edid(&scrubbed).expect("re-parse ok");
        assert!(parsed2.checksums_valid);
        assert!(!parsed2.has_cinema_4096p);

        let tuned = tune_vrr_range(&scrubbed, 52, 240).expect("tune ok");
        let parsed3 = parse_edid(&tuned).expect("re-parse vrr");
        assert!(parsed3.checksums_valid);
        assert_eq!(parsed3.vrr_range.unwrap().min_hz, 52);
    }
}
