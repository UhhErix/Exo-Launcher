use serde::{Deserialize, Serialize};

pub const MHC2_TAG_SIG: &[u8; 4] = b"MHC2";
pub const ICC_MAGIC_SIG: &[u8; 4] = b"acsp";

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Mhc2Spec {
    pub lut_entries: usize,
    pub min_nits: f64,
    pub max_nits: f64,
    pub gamma_22_fix: bool,
    pub matrix_3x4: [f64; 12],
}

impl Mhc2Spec {
    pub fn true_black_hdr_22(min_nits: f64, max_nits: f64) -> Self {
        Self {
            lut_entries: 1024,
            min_nits: min_nits.max(0.0),
            max_nits: max_nits.max(80.0),
            gamma_22_fix: true,
            matrix_3x4: [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0],
        }
    }

    pub fn srgb_clamp_sdr(matrix_3x3: [f64; 9], max_nits: f64) -> Self {
        Self {
            lut_entries: 1024,
            min_nits: 0.0,
            max_nits: max_nits.max(80.0),
            gamma_22_fix: false,
            matrix_3x4: [
                matrix_3x3[0],
                matrix_3x3[1],
                matrix_3x3[2],
                0.0,
                matrix_3x3[3],
                matrix_3x3[4],
                matrix_3x3[5],
                0.0,
                matrix_3x3[6],
                matrix_3x3[7],
                matrix_3x3[8],
                0.0,
            ],
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Mhc2Validation {
    pub profile_bytes: usize,
    pub lut_entries: u32,
    pub min_nits: f64,
    pub max_nits: f64,
    pub lut_monotonic: bool,
}

fn to_s15_fixed16(v: f64) -> i32 {
    (v * 65536.0)
        .round()
        .clamp(i32::MIN as f64, i32::MAX as f64) as i32
}

fn from_s15_fixed16(v: i32) -> f64 {
    (v as f64) / 65536.0
}

fn srgb_eotf(x: f64) -> f64 {
    if x <= 0.04045 {
        x / 12.92
    } else {
        ((x + 0.055) / 1.055).powf(2.4)
    }
}

fn srgb_oetf(l: f64) -> f64 {
    if l <= 0.0031308 {
        l * 12.92
    } else {
        1.055 * l.powf(1.0 / 2.4) - 0.055
    }
}

fn build_mhc2_tag(spec: &Mhc2Spec) -> Vec<u8> {
    let n = spec.lut_entries.clamp(2, 4096);
    let mut tag = Vec::with_capacity(96 + 3 * (8 + n * 4));
    tag.extend_from_slice(MHC2_TAG_SIG);
    tag.extend_from_slice(&0u32.to_be_bytes());
    tag.extend_from_slice(&(n as u32).to_be_bytes());
    tag.extend_from_slice(&to_s15_fixed16(spec.min_nits).to_be_bytes());
    tag.extend_from_slice(&to_s15_fixed16(spec.max_nits).to_be_bytes());

    let matrix_offset = 36u32;
    let regamma_r_offset = matrix_offset + 48;
    let channel_len = (8 + n * 4) as u32;
    let regamma_g_offset = regamma_r_offset + channel_len;
    let regamma_b_offset = regamma_g_offset + channel_len;

    tag.extend_from_slice(&matrix_offset.to_be_bytes());
    tag.extend_from_slice(&regamma_r_offset.to_be_bytes());
    tag.extend_from_slice(&regamma_g_offset.to_be_bytes());
    tag.extend_from_slice(&regamma_b_offset.to_be_bytes());

    for &m in &spec.matrix_3x4 {
        tag.extend_from_slice(&to_s15_fixed16(m).to_be_bytes());
    }

    let mut lut = Vec::with_capacity(n);
    for i in 0..n {
        let x = (i as f64) / ((n - 1) as f64);
        let y = if spec.gamma_22_fix {
            let linear_22 = x.powf(2.2);
            let _ = srgb_eotf(x);
            srgb_oetf(linear_22).clamp(0.0, 1.0)
        } else {
            x
        };
        lut.push(to_s15_fixed16(y));
    }

    for _ in 0..3 {
        tag.extend_from_slice(b"sf32");
        tag.extend_from_slice(&0u32.to_be_bytes());
        for &entry in &lut {
            tag.extend_from_slice(&entry.to_be_bytes());
        }
    }
    tag
}

fn build_mluc_tag(text: &str) -> Vec<u8> {
    let utf16: Vec<u16> = text.encode_utf16().collect();
    let str_len = (utf16.len() * 2) as u32;
    let mut tag = Vec::with_capacity(28 + utf16.len() * 2);
    tag.extend_from_slice(b"mluc");
    tag.extend_from_slice(&0u32.to_be_bytes());
    tag.extend_from_slice(&1u32.to_be_bytes());
    tag.extend_from_slice(&12u32.to_be_bytes());
    tag.extend_from_slice(b"enUS");
    tag.extend_from_slice(&str_len.to_be_bytes());
    tag.extend_from_slice(&28u32.to_be_bytes());
    for u in utf16 {
        tag.extend_from_slice(&u.to_be_bytes());
    }
    tag
}

fn build_xyz_tag(x: f64, y: f64, z: f64) -> Vec<u8> {
    let mut tag = Vec::with_capacity(20);
    tag.extend_from_slice(b"XYZ ");
    tag.extend_from_slice(&0u32.to_be_bytes());
    tag.extend_from_slice(&to_s15_fixed16(x).to_be_bytes());
    tag.extend_from_slice(&to_s15_fixed16(y).to_be_bytes());
    tag.extend_from_slice(&to_s15_fixed16(z).to_be_bytes());
    tag
}

fn build_para_gamma_tag(gamma: f64) -> Vec<u8> {
    let mut tag = Vec::with_capacity(16);
    tag.extend_from_slice(b"para");
    tag.extend_from_slice(&0u32.to_be_bytes());
    tag.extend_from_slice(&0u16.to_be_bytes());
    tag.extend_from_slice(&0u16.to_be_bytes());
    tag.extend_from_slice(&to_s15_fixed16(gamma).to_be_bytes());
    tag
}

pub fn build_mhc2_icc_profile(description: &str, spec: &Mhc2Spec) -> Vec<u8> {
    let tags: Vec<([u8; 4], Vec<u8>)> = vec![
        (*b"desc", build_mluc_tag(description)),
        (*b"cprt", build_mluc_tag("MIT Exo Display")),
        (*b"wtpt", build_xyz_tag(0.9642, 1.0, 0.8249)),
        (*b"rXYZ", build_xyz_tag(0.43607, 0.22250, 0.01393)),
        (*b"gXYZ", build_xyz_tag(0.38506, 0.71687, 0.09710)),
        (*b"bXYZ", build_xyz_tag(0.14307, 0.06061, 0.71417)),
        (*b"rTRC", build_para_gamma_tag(2.2)),
        (*b"gTRC", build_para_gamma_tag(2.2)),
        (*b"bTRC", build_para_gamma_tag(2.2)),
        (*MHC2_TAG_SIG, build_mhc2_tag(spec)),
    ];

    let header_len = 128usize;
    let table_len = 4 + tags.len() * 12;
    let mut data_offset = (header_len + table_len + 3) & !3;
    let mut entries = Vec::with_capacity(tags.len());
    let mut body = Vec::new();

    for (sig, bytes) in tags {
        let len = bytes.len();
        entries.push((sig, data_offset as u32, len as u32));
        body.extend_from_slice(&bytes);
        while !body.len().is_multiple_of(4) {
            body.push(0);
        }
        data_offset = header_len + table_len + body.len();
    }

    let total_size = (header_len + table_len + body.len()) as u32;
    let mut icc = vec![0u8; header_len];
    icc[0..4].copy_from_slice(&total_size.to_be_bytes());
    icc[8..12].copy_from_slice(&[4, 0x30, 0, 0]);
    icc[12..16].copy_from_slice(b"mntr");
    icc[16..20].copy_from_slice(b"RGB ");
    icc[20..24].copy_from_slice(b"XYZ ");
    icc[36..40].copy_from_slice(ICC_MAGIC_SIG);
    icc[40..44].copy_from_slice(b"MSFT");
    icc[68..72].copy_from_slice(&to_s15_fixed16(0.9642).to_be_bytes());
    icc[72..76].copy_from_slice(&to_s15_fixed16(1.0).to_be_bytes());
    icc[76..80].copy_from_slice(&to_s15_fixed16(0.8249).to_be_bytes());

    icc.extend_from_slice(&(entries.len() as u32).to_be_bytes());
    for (sig, off, len) in entries {
        icc.extend_from_slice(&sig);
        icc.extend_from_slice(&off.to_be_bytes());
        icc.extend_from_slice(&len.to_be_bytes());
    }
    icc.extend_from_slice(&body);
    icc
}

pub fn validate_mhc2_icc(bytes: &[u8]) -> Result<Mhc2Validation, String> {
    if bytes.len() < 132 {
        return Err("ICC profile too short".into());
    }
    let declared = u32::from_be_bytes(bytes[0..4].try_into().unwrap()) as usize;
    if declared != bytes.len() {
        return Err(format!(
            "size mismatch: declared {declared}, actual {}",
            bytes.len()
        ));
    }
    if &bytes[36..40] != ICC_MAGIC_SIG {
        return Err("invalid ICC acsp signature".into());
    }
    let count = u32::from_be_bytes(bytes[128..132].try_into().unwrap()) as usize;
    for i in 0..count {
        let base = 132 + i * 12;
        if base + 12 > bytes.len() {
            return Err("truncated tag table".into());
        }
        let sig = &bytes[base..base + 4];
        let off = u32::from_be_bytes(bytes[base + 4..base + 8].try_into().unwrap()) as usize;
        let len = u32::from_be_bytes(bytes[base + 8..base + 12].try_into().unwrap()) as usize;
        if off + len > bytes.len() {
            return Err("tag out of bounds".into());
        }
        if sig == MHC2_TAG_SIG {
            let tag = &bytes[off..off + len];
            if tag.len() < 36 || &tag[0..4] != MHC2_TAG_SIG {
                return Err("invalid MHC2 tag header".into());
            }
            let lut_entries = u32::from_be_bytes(tag[8..12].try_into().unwrap());
            let min_nits = from_s15_fixed16(i32::from_be_bytes(tag[12..16].try_into().unwrap()));
            let max_nits = from_s15_fixed16(i32::from_be_bytes(tag[16..20].try_into().unwrap()));
            let r_off = u32::from_be_bytes(tag[24..28].try_into().unwrap()) as usize;
            let mut monotonic = true;
            let mut prev = f64::NEG_INFINITY;
            for k in 0..lut_entries as usize {
                let p = r_off + 8 + k * 4;
                if p + 4 > tag.len() {
                    return Err("truncated MHC2 LUT".into());
                }
                let val = from_s15_fixed16(i32::from_be_bytes(tag[p..p + 4].try_into().unwrap()));
                if val + 1e-5 < prev {
                    monotonic = false;
                }
                prev = val;
            }
            return Ok(Mhc2Validation {
                profile_bytes: bytes.len(),
                lut_entries,
                min_nits,
                max_nits,
                lut_monotonic: monotonic,
            });
        }
    }
    Err("MHC2 tag not found".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn generates_valid_monotonic_mhc2_icc_profile() {
        let spec = Mhc2Spec::true_black_hdr_22(0.0, 1000.0);
        let bytes = build_mhc2_icc_profile("Exo OLED True Black 2.2", &spec);
        let val = validate_mhc2_icc(&bytes).expect("valid MHC2 ICC");
        assert_eq!(val.lut_entries, 1024);
        assert!(val.lut_monotonic);
        assert!((val.max_nits - 1000.0).abs() < 0.01);
    }
}
