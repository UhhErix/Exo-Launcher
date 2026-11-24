use serde::{Deserialize, Serialize};
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

const UE_PAK_MAGIC: u32 = 0x5A6F_12E1;
const UE_UTOC_MAGIC: &[u8; 16] = b"-==--==--==--==-";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UePakHeader {
    pub version: u32,
    pub index_offset: u64,
    pub index_size: u64,
    pub encrypted_index: bool,
    pub file_bytes: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UeUtocHeader {
    pub version: u32,
    pub header_size: u32,
    pub entry_count: u32,
    pub compressed_block_count: u32,
    pub file_bytes: u64,
}

pub fn inspect_pak_header(path: &Path) -> Option<UePakHeader> {
    let mut file = File::open(path).ok()?;
    let file_bytes = file.metadata().ok()?.len();
    for footer_size in [221u64, 204, 172, 168, 45, 44] {
        if file_bytes < footer_size {
            continue;
        }
        if file.seek(SeekFrom::End(-(footer_size as i64))).is_err() {
            continue;
        }
        let mut buf = vec![0u8; footer_size as usize];
        if file.read_exact(&mut buf).is_err() {
            continue;
        }
        for offset in [0usize, 16, 17] {
            if offset + 24 > buf.len() {
                continue;
            }
            let magic = u32::from_le_bytes(buf[offset..offset + 4].try_into().ok()?);
            if magic == UE_PAK_MAGIC {
                let version = u32::from_le_bytes(buf[offset + 4..offset + 8].try_into().ok()?);
                let index_offset =
                    u64::from_le_bytes(buf[offset + 8..offset + 16].try_into().ok()?);
                let index_size = u64::from_le_bytes(buf[offset + 16..offset + 24].try_into().ok()?);
                let encrypted_index = offset > 0 && buf[offset - 1] != 0;
                if version <= 12 && index_offset <= file_bytes {
                    return Some(UePakHeader {
                        version,
                        index_offset,
                        index_size,
                        encrypted_index,
                        file_bytes,
                    });
                }
            }
        }
    }
    None
}

pub fn inspect_utoc_header(path: &Path) -> Option<UeUtocHeader> {
    let mut file = File::open(path).ok()?;
    let file_bytes = file.metadata().ok()?.len();
    if file_bytes < 48 {
        return None;
    }
    let mut buf = [0u8; 48];
    file.read_exact(&mut buf).ok()?;
    if &buf[0..16] != UE_UTOC_MAGIC {
        return None;
    }
    let version = u32::from_le_bytes(buf[16..20].try_into().ok()?);
    let header_size = u32::from_le_bytes(buf[24..28].try_into().ok()?);
    let entry_count = u32::from_le_bytes(buf[28..32].try_into().ok()?);
    let compressed_block_count = u32::from_le_bytes(buf[32..36].try_into().ok()?);
    Some(UeUtocHeader {
        version,
        header_size,
        entry_count,
        compressed_block_count,
        file_bytes,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    #[test]
    fn parses_synthetic_ue_pak_footer() {
        let p = std::env::temp_dir().join(format!("exo-test-{}.pak", std::process::id()));
        let mut data = vec![0u8; 1024];
        let mut footer = vec![0u8; 204];
        footer[16] = 0;
        footer[17..21].copy_from_slice(&UE_PAK_MAGIC.to_le_bytes());
        footer[21..25].copy_from_slice(&11u32.to_le_bytes());
        footer[25..33].copy_from_slice(&512u64.to_le_bytes());
        footer[33..41].copy_from_slice(&256u64.to_le_bytes());
        data.extend_from_slice(&footer);
        let mut f = File::create(&p).unwrap();
        f.write_all(&data).unwrap();
        let hdr = inspect_pak_header(&p).expect("should parse UE pak footer");
        assert_eq!(hdr.version, 11);
        assert_eq!(hdr.index_offset, 512);
        assert_eq!(hdr.index_size, 256);
        let _ = std::fs::remove_file(&p);
    }

    #[test]
    fn parses_synthetic_utoc_header() {
        let p = std::env::temp_dir().join(format!("exo-test-{}.utoc", std::process::id()));
        let mut hdr = [0u8; 64];
        hdr[0..16].copy_from_slice(UE_UTOC_MAGIC);
        hdr[16..20].copy_from_slice(&7u32.to_le_bytes());
        hdr[24..28].copy_from_slice(&144u32.to_le_bytes());
        hdr[28..32].copy_from_slice(&1200u32.to_le_bytes());
        hdr[32..36].copy_from_slice(&400u32.to_le_bytes());
        std::fs::write(&p, hdr).unwrap();
        let parsed = inspect_utoc_header(&p).expect("should parse utoc");
        assert_eq!(parsed.version, 7);
        assert_eq!(parsed.entry_count, 1200);
        let _ = std::fs::remove_file(&p);
    }
}
