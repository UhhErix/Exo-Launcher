use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;

pub const BANNED_HOST_IMPORTS: &[&str] = &[
    "BitBlt",
    "PrintWindow",
    "SendInput",
    "mouse_event",
    "keybd_event",
];

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PeImportEntry {
    pub dll: String,
    pub function: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PeAuditReport {
    pub total_imports: usize,
    pub banned_found: Vec<PeImportEntry>,
}

impl PeAuditReport {
    pub fn is_clean(&self) -> bool {
        self.banned_found.is_empty()
    }
}

pub fn audit_pe_file(path: &Path) -> Result<PeAuditReport, String> {
    let bytes = fs::read(path).map_err(|e| format!("failed to read {}: {e}", path.display()))?;
    audit_pe_bytes(&bytes)
}

pub fn audit_pe_bytes(bytes: &[u8]) -> Result<PeAuditReport, String> {
    let imports = parse_pe_imports(bytes)?;
    let banned_found: Vec<PeImportEntry> = imports
        .iter()
        .filter(|entry| {
            BANNED_HOST_IMPORTS
                .iter()
                .any(|&banned| entry.function.eq_ignore_ascii_case(banned))
        })
        .cloned()
        .collect();
    Ok(PeAuditReport {
        total_imports: imports.len(),
        banned_found,
    })
}

pub fn parse_pe_imports(bytes: &[u8]) -> Result<Vec<PeImportEntry>, String> {
    if bytes.len() < 0x40 || &bytes[0..2] != b"MZ" {
        return Err("not a valid PE file (missing MZ header)".into());
    }
    let pe_off = read_u32(bytes, 0x3c)? as usize;
    if pe_off + 24 > bytes.len() || &bytes[pe_off..pe_off + 4] != b"PE\0\0" {
        return Err("not a valid PE file (missing PE signature)".into());
    }
    let num_sections = read_u16(bytes, pe_off + 6)? as usize;
    let opt_size = read_u16(bytes, pe_off + 20)? as usize;
    let opt_off = pe_off + 24;
    if opt_off + opt_size > bytes.len() || opt_size < 2 {
        return Err("truncated PE optional header".into());
    }
    let magic = read_u16(bytes, opt_off)?;
    let (is_pe32_plus, data_dir_off) = match magic {
        0x20b => (true, opt_off + 112),
        0x10b => (false, opt_off + 96),
        other => return Err(format!("unsupported PE optional header magic 0x{other:x}")),
    };
    if data_dir_off + 16 > opt_off + opt_size {
        return Ok(Vec::new());
    }
    let import_rva = read_u32(bytes, data_dir_off + 8)?;
    let import_size = read_u32(bytes, data_dir_off + 12)?;
    if import_rva == 0 || import_size == 0 {
        return Ok(Vec::new());
    }

    let sec_table_off = opt_off + opt_size;
    let mut sections = Vec::with_capacity(num_sections);
    for i in 0..num_sections {
        let off = sec_table_off + i * 40;
        if off + 40 > bytes.len() {
            return Err("truncated section header".into());
        }
        let virt_size = read_u32(bytes, off + 8)?;
        let virt_addr = read_u32(bytes, off + 12)?;
        let raw_size = read_u32(bytes, off + 16)?;
        let raw_ptr = read_u32(bytes, off + 20)?;
        sections.push((virt_addr, virt_size.max(raw_size), raw_ptr, raw_size));
    }

    let rva_to_off = |rva: u32| -> Option<usize> {
        for &(va, vs, rp, rs) in &sections {
            if rva >= va && rva < va.saturating_add(vs) {
                let delta = rva - va;
                if delta < rs {
                    let off = (rp as usize).checked_add(delta as usize)?;
                    if off < bytes.len() {
                        return Some(off);
                    }
                }
            }
        }
        None
    };

    let Some(mut desc_off) = rva_to_off(import_rva) else {
        return Ok(Vec::new());
    };
    let mut out = Vec::new();
    for _ in 0..4096 {
        if desc_off + 20 > bytes.len() {
            break;
        }
        let orig_first_thunk = read_u32(bytes, desc_off)?;
        let name_rva = read_u32(bytes, desc_off + 12)?;
        let first_thunk = read_u32(bytes, desc_off + 16)?;
        if orig_first_thunk == 0 && name_rva == 0 && first_thunk == 0 {
            break;
        }
        let dll = rva_to_off(name_rva)
            .and_then(|o| read_cstr(bytes, o))
            .unwrap_or_default();
        let thunk_rva = if orig_first_thunk != 0 {
            orig_first_thunk
        } else {
            first_thunk
        };
        if let Some(mut thunk_off) = rva_to_off(thunk_rva) {
            let step = if is_pe32_plus { 8 } else { 4 };
            for _ in 0..16384 {
                if thunk_off + step > bytes.len() {
                    break;
                }
                let entry = if is_pe32_plus {
                    read_u64(bytes, thunk_off)?
                } else {
                    read_u32(bytes, thunk_off)? as u64
                };
                if entry == 0 {
                    break;
                }
                let is_ordinal = if is_pe32_plus {
                    (entry & (1u64 << 63)) != 0
                } else {
                    (entry & (1u64 << 31)) != 0
                };
                if !is_ordinal {
                    let hint_name_rva = (entry & 0x7FFF_FFFF) as u32;
                    if let Some(hn_off) = rva_to_off(hint_name_rva)
                        && let Some(func) = read_cstr(bytes, hn_off + 2)
                    {
                        out.push(PeImportEntry {
                            dll: dll.clone(),
                            function: func,
                        });
                    }
                }
                thunk_off += step;
            }
        }
        desc_off += 20;
    }

    Ok(out)
}

fn read_u16(bytes: &[u8], off: usize) -> Result<u16, String> {
    let slice = bytes
        .get(off..off + 2)
        .ok_or_else(|| "unexpected EOF reading u16".to_string())?;
    Ok(u16::from_le_bytes([slice[0], slice[1]]))
}

fn read_u32(bytes: &[u8], off: usize) -> Result<u32, String> {
    let slice = bytes
        .get(off..off + 4)
        .ok_or_else(|| "unexpected EOF reading u32".to_string())?;
    Ok(u32::from_le_bytes([slice[0], slice[1], slice[2], slice[3]]))
}

fn read_u64(bytes: &[u8], off: usize) -> Result<u64, String> {
    let slice = bytes
        .get(off..off + 8)
        .ok_or_else(|| "unexpected EOF reading u64".to_string())?;
    Ok(u64::from_le_bytes([
        slice[0], slice[1], slice[2], slice[3], slice[4], slice[5], slice[6], slice[7],
    ]))
}

fn read_cstr(bytes: &[u8], off: usize) -> Option<String> {
    if off >= bytes.len() {
        return None;
    }
    let tail = &bytes[off..];
    let end = tail.iter().position(|&b| b == 0).unwrap_or(tail.len());
    let capped = end.min(512);
    Some(String::from_utf8_lossy(&tail[..capped]).into_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn build_synthetic_pe64(dll: &str, funcs: &[&str]) -> Vec<u8> {
        let mut buf = vec![0u8; 0x1000];
        buf[0..2].copy_from_slice(b"MZ");
        buf[0x3c..0x40].copy_from_slice(&0x80u32.to_le_bytes());

        let pe = 0x80usize;
        buf[pe..pe + 4].copy_from_slice(b"PE\0\0");
        buf[pe + 6..pe + 8].copy_from_slice(&1u16.to_le_bytes());
        buf[pe + 20..pe + 22].copy_from_slice(&240u16.to_le_bytes());

        let opt = pe + 24;
        buf[opt..opt + 2].copy_from_slice(&0x20bu16.to_le_bytes());
        let import_dir = opt + 112 + 8;
        buf[import_dir..import_dir + 4].copy_from_slice(&0x2000u32.to_le_bytes());
        buf[import_dir + 4..import_dir + 8].copy_from_slice(&40u32.to_le_bytes());

        let sec = opt + 240;
        buf[sec..sec + 6].copy_from_slice(b".idata");
        buf[sec + 8..sec + 12].copy_from_slice(&0x400u32.to_le_bytes());
        buf[sec + 12..sec + 16].copy_from_slice(&0x2000u32.to_le_bytes());
        buf[sec + 16..sec + 20].copy_from_slice(&0x400u32.to_le_bytes());
        buf[sec + 20..sec + 24].copy_from_slice(&0x400u32.to_le_bytes());

        let idata = 0x400usize;
        let thunk_rva = 0x2040u32;
        let dll_rva = 0x20C0u32;
        buf[idata..idata + 4].copy_from_slice(&thunk_rva.to_le_bytes());
        buf[idata + 12..idata + 16].copy_from_slice(&dll_rva.to_le_bytes());
        buf[idata + 16..idata + 20].copy_from_slice(&thunk_rva.to_le_bytes());

        let dll_off = idata + 0xC0;
        buf[dll_off..dll_off + dll.len()].copy_from_slice(dll.as_bytes());

        let mut hn_cursor = 0x100usize;
        for (i, func) in funcs.iter().enumerate() {
            let hn_rva = 0x2000u32 + hn_cursor as u32;
            let t_off = idata + 0x40 + i * 8;
            buf[t_off..t_off + 8].copy_from_slice(&(hn_rva as u64).to_le_bytes());
            let name_off = idata + hn_cursor + 2;
            buf[name_off..name_off + func.len()].copy_from_slice(func.as_bytes());
            hn_cursor += 2 + func.len() + 2;
        }
        buf
    }

    #[test]
    fn pe_audit_flags_banned_imports_and_passes_clean_pe() {
        let clean = build_synthetic_pe64("kernel32.dll", &["CreateFileW", "ReadFile"]);
        let report = audit_pe_bytes(&clean).unwrap();
        assert_eq!(report.total_imports, 2);
        assert!(report.is_clean());

        let dirty = build_synthetic_pe64("user32.dll", &["PostMessageW", "SendInput", "BitBlt"]);
        let dirty_report = audit_pe_bytes(&dirty).unwrap();
        assert_eq!(dirty_report.total_imports, 3);
        assert!(!dirty_report.is_clean());
        assert_eq!(dirty_report.banned_found.len(), 2);
        assert_eq!(dirty_report.banned_found[0].function, "SendInput");
        assert_eq!(dirty_report.banned_found[1].function, "BitBlt");
    }

    #[test]
    fn pe_audit_rejects_corrupt_buffers_cleanly() {
        assert!(audit_pe_bytes(&[]).is_err());
        assert!(audit_pe_bytes(b"NOT_A_PE").is_err());
        let mut bad = vec![0u8; 64];
        bad[0..2].copy_from_slice(b"MZ");
        bad[0x3c..0x40].copy_from_slice(&0xFFFF_FFFFu32.to_le_bytes());
        assert!(audit_pe_bytes(&bad).is_err());
    }
}
