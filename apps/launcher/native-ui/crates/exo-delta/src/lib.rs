use rayon::prelude::*;
use std::collections::{BTreeMap, HashSet};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

pub const DELTA_MAGIC: [u8; 4] = *b"EXD1";
pub const DELTA_VERSION: u8 = 1;
const WINDOW_LOG: u32 = 26;
const MAX_PATCH_RATIO: f64 = 0.85;

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EntryMode {
    Reuse = 0,
    FullZstd = 1,
    ZstdPatchFrom = 2,
}

impl EntryMode {
    fn from_u8(v: u8) -> Result<Self, DeltaError> {
        match v {
            0 => Ok(Self::Reuse),
            1 => Ok(Self::FullZstd),
            2 => Ok(Self::ZstdPatchFrom),
            other => Err(DeltaError::InvalidMode(other)),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeltaEntry {
    pub path: String,
    pub mode: EntryMode,
    pub raw_size: u64,
    pub target_blake3: [u8; 32],
    pub base_blake3: [u8; 32],
    pub payload_offset: u64,
    pub payload_len: u64,
}

#[derive(Debug, Clone)]
pub struct DeltaStats {
    pub total_files: usize,
    pub reused_files: usize,
    pub patched_files: usize,
    pub full_files: usize,
    pub total_raw_bytes: u64,
    pub changed_raw_bytes: u64,
    pub file_level_zstd_bytes: u64,
    pub delta_wire_bytes: u64,
}

#[derive(Debug)]
pub enum DeltaError {
    Io(std::io::Error),
    BadMagic,
    UnsupportedVersion(u8),
    InvalidMode(u8),
    UnsafePath(String),
    DuplicatePath(String),
    Truncated,
    InvalidUtf8,
    Codec(String),
    BaseHashMismatch(String),
    TargetHashMismatch(String),
    SizeMismatch(String),
}

impl fmt::Display for DeltaError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for DeltaError {}

impl From<std::io::Error> for DeltaError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

pub fn is_safe_relative_path(path: &str) -> bool {
    if path.is_empty() || path.starts_with('/') || path.ends_with('/') || path.contains('\\') || path.contains('\0') {
        return false;
    }
    if path.as_bytes().len() >= 2 && path.as_bytes()[1] == b':' {
        return false;
    }
    for seg in path.split('/') {
        if seg.is_empty() || seg == "." || seg == ".." {
            return false;
        }
        let stem = seg.split('.').next().unwrap_or(seg).to_ascii_uppercase();
        let reserved = matches!(
            stem.as_str(),
            "CON"
                | "PRN"
                | "AUX"
                | "NUL"
                | "COM1"
                | "COM2"
                | "COM3"
                | "COM4"
                | "COM5"
                | "COM6"
                | "COM7"
                | "COM8"
                | "COM9"
                | "LPT1"
                | "LPT2"
                | "LPT3"
                | "LPT4"
                | "LPT5"
                | "LPT6"
                | "LPT7"
                | "LPT8"
                | "LPT9"
        );
        if reserved {
            return false;
        }
    }
    true
}

pub fn scan_folder(root: &Path) -> Result<BTreeMap<String, PathBuf>, DeltaError> {
    let mut out = BTreeMap::new();
    let mut seen_lower = HashSet::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for item in fs::read_dir(&dir)? {
            let entry = item?;
            let meta = fs::symlink_metadata(entry.path())?;
            if meta.file_type().is_symlink() {
                return Err(DeltaError::UnsafePath(entry.path().display().to_string()));
            }
            if meta.is_dir() {
                stack.push(entry.path());
            } else if meta.is_file() {
                let rel = entry
                    .path()
                    .strip_prefix(root)
                    .map_err(|_| DeltaError::UnsafePath(entry.path().display().to_string()))?
                    .to_string_lossy()
                    .replace('\\', "/");
                if !is_safe_relative_path(&rel) {
                    return Err(DeltaError::UnsafePath(rel));
                }
                let lower = rel.to_ascii_lowercase();
                if !seen_lower.insert(lower) {
                    return Err(DeltaError::DuplicatePath(rel));
                }
                out.insert(rel, entry.path());
            }
        }
    }
    Ok(out)
}

fn zstd_compress_patch(base: &[u8], target: &[u8], level: i32) -> Result<Vec<u8>, DeltaError> {
    let mut cctx = zstd_safe::CCtx::create();
    cctx.set_parameter(zstd_safe::CParameter::CompressionLevel(level))
        .map_err(|c| DeltaError::Codec(format!("set level {c}")))?;
    cctx.set_parameter(zstd_safe::CParameter::EnableLongDistanceMatching(true))
        .map_err(|c| DeltaError::Codec(format!("set ldm {c}")))?;
    cctx.set_parameter(zstd_safe::CParameter::WindowLog(WINDOW_LOG))
        .map_err(|c| DeltaError::Codec(format!("set wlog {c}")))?;
    cctx.ref_prefix(base)
        .map_err(|c| DeltaError::Codec(format!("ref_prefix {c}")))?;
    let mut buf = vec![0u8; zstd_safe::compress_bound(target.len())];
    let n = cctx
        .compress2(&mut buf, target)
        .map_err(|c| DeltaError::Codec(format!("compress2 {c}")))?;
    buf.truncate(n);
    Ok(buf)
}

fn zstd_decompress_patch(base: &[u8], patch: &[u8], expected_len: usize) -> Result<Vec<u8>, DeltaError> {
    let mut dctx = zstd_safe::DCtx::create();
    dctx.set_parameter(zstd_safe::DParameter::WindowLogMax(WINDOW_LOG))
        .map_err(|c| DeltaError::Codec(format!("set wlogmax {c}")))?;
    dctx.ref_prefix(base)
        .map_err(|c| DeltaError::Codec(format!("ref_prefix {c}")))?;
    let mut out = vec![0u8; expected_len];
    let n = dctx
        .decompress(&mut out, patch)
        .map_err(|c| DeltaError::Codec(format!("decompress {c}")))?;
    if n != expected_len {
        return Err(DeltaError::SizeMismatch(format!("got {n}, expected {expected_len}")));
    }
    Ok(out)
}

struct BuiltItem {
    path: String,
    mode: EntryMode,
    raw_size: u64,
    target_blake3: [u8; 32],
    base_blake3: [u8; 32],
    payload: Vec<u8>,
    standalone_zstd_len: u64,
}

pub fn build_delta_pack(
    old_root: &Path,
    new_root: &Path,
    level: i32,
) -> Result<(Vec<u8>, DeltaStats), DeltaError> {
    let old_map = scan_folder(old_root)?;
    let new_map = scan_folder(new_root)?;
    let new_items: Vec<(String, PathBuf)> = new_map.into_iter().collect();

    let built: Result<Vec<BuiltItem>, DeltaError> = new_items
        .into_par_iter()
        .map(|(rel, new_path)| {
            let new_bytes = fs::read(&new_path)?;
            let target_hash = *blake3::hash(&new_bytes).as_bytes();

            if let Some(old_path) = old_map.get(&rel) {
                let old_bytes = fs::read(old_path)?;
                let base_hash = *blake3::hash(&old_bytes).as_bytes();
                if base_hash == target_hash && old_bytes.len() == new_bytes.len() {
                    return Ok(BuiltItem {
                        path: rel,
                        mode: EntryMode::Reuse,
                        raw_size: new_bytes.len() as u64,
                        target_blake3: target_hash,
                        base_blake3: base_hash,
                        payload: Vec::new(),
                        standalone_zstd_len: 0,
                    });
                }
                let standalone = zstd::bulk::compress(&new_bytes, level)
                    .map_err(|e| DeltaError::Codec(e.to_string()))?;
                let patch = zstd_compress_patch(&old_bytes, &new_bytes, level)?;
                if (patch.len() as f64) < (standalone.len() as f64) * MAX_PATCH_RATIO {
                    return Ok(BuiltItem {
                        path: rel,
                        mode: EntryMode::ZstdPatchFrom,
                        raw_size: new_bytes.len() as u64,
                        target_blake3: target_hash,
                        base_blake3: base_hash,
                        payload: patch,
                        standalone_zstd_len: standalone.len() as u64,
                    });
                }
                return Ok(BuiltItem {
                    path: rel,
                    mode: EntryMode::FullZstd,
                    raw_size: new_bytes.len() as u64,
                    target_blake3: target_hash,
                    base_blake3: [0u8; 32],
                    payload: standalone.clone(),
                    standalone_zstd_len: standalone.len() as u64,
                });
            }

            let standalone = zstd::bulk::compress(&new_bytes, level)
                .map_err(|e| DeltaError::Codec(e.to_string()))?;
            let slen = standalone.len() as u64;
            Ok(BuiltItem {
                path: rel,
                mode: EntryMode::FullZstd,
                raw_size: new_bytes.len() as u64,
                target_blake3: target_hash,
                base_blake3: [0u8; 32],
                payload: standalone,
                standalone_zstd_len: slen,
            })
        })
        .collect();
    let built = built?;

    let mut stats = DeltaStats {
        total_files: built.len(),
        reused_files: 0,
        patched_files: 0,
        full_files: 0,
        total_raw_bytes: 0,
        changed_raw_bytes: 0,
        file_level_zstd_bytes: 0,
        delta_wire_bytes: 0,
    };

    let mut manifest = Vec::with_capacity(built.len() * 128);
    manifest.extend_from_slice(&(built.len() as u32).to_le_bytes());
    let mut payload_offset = 0u64;
    for item in &built {
        stats.total_raw_bytes += item.raw_size;
        match item.mode {
            EntryMode::Reuse => stats.reused_files += 1,
            EntryMode::ZstdPatchFrom => {
                stats.patched_files += 1;
                stats.changed_raw_bytes += item.raw_size;
                stats.file_level_zstd_bytes += item.standalone_zstd_len;
            }
            EntryMode::FullZstd => {
                stats.full_files += 1;
                stats.changed_raw_bytes += item.raw_size;
                stats.file_level_zstd_bytes += item.standalone_zstd_len;
            }
        }
        let pb = item.path.as_bytes();
        manifest.extend_from_slice(&(pb.len() as u16).to_le_bytes());
        manifest.extend_from_slice(pb);
        manifest.push(item.mode as u8);
        manifest.extend_from_slice(&item.raw_size.to_le_bytes());
        manifest.extend_from_slice(&item.target_blake3);
        manifest.extend_from_slice(&item.base_blake3);
        manifest.extend_from_slice(&payload_offset.to_le_bytes());
        let plen = item.payload.len() as u64;
        manifest.extend_from_slice(&plen.to_le_bytes());
        payload_offset += plen;
    }

    let compressed_manifest = zstd::bulk::compress(&manifest, 6)
        .map_err(|e| DeltaError::Codec(e.to_string()))?;

    let mut out = Vec::with_capacity(16 + compressed_manifest.len() + payload_offset as usize);
    out.extend_from_slice(&DELTA_MAGIC);
    out.push(DELTA_VERSION);
    out.extend_from_slice(&[0u8; 3]);
    out.extend_from_slice(&(manifest.len() as u32).to_le_bytes());
    out.extend_from_slice(&(compressed_manifest.len() as u32).to_le_bytes());
    out.extend_from_slice(&compressed_manifest);
    for item in &built {
        out.extend_from_slice(&item.payload);
    }

    let pack_hash = blake3::hash(&out);
    out.extend_from_slice(pack_hash.as_bytes());
    stats.delta_wire_bytes = out.len() as u64;
    Ok((out, stats))
}

pub fn parse_delta_pack(pack: &[u8]) -> Result<(Vec<DeltaEntry>, &[u8]), DeltaError> {
    if pack.len() < 16 + 32 {
        return Err(DeltaError::Truncated);
    }
    let body_end = pack.len() - 32;
    let stored_hash = &pack[body_end..];
    if blake3::hash(&pack[..body_end]).as_bytes() != stored_hash {
        return Err(DeltaError::TargetHashMismatch("pack trailer".into()));
    }
    if pack[0..4] != DELTA_MAGIC {
        return Err(DeltaError::BadMagic);
    }
    if pack[4] != DELTA_VERSION {
        return Err(DeltaError::UnsupportedVersion(pack[4]));
    }
    let raw_manifest_len = u32::from_le_bytes(pack[8..12].try_into().unwrap()) as usize;
    let comp_manifest_len = u32::from_le_bytes(pack[12..16].try_into().unwrap()) as usize;
    let payload_start = 16usize.checked_add(comp_manifest_len).ok_or(DeltaError::Truncated)?;
    if payload_start > body_end {
        return Err(DeltaError::Truncated);
    }
    let manifest = zstd::bulk::decompress(&pack[16..payload_start], raw_manifest_len + 64)
        .map_err(|e| DeltaError::Codec(e.to_string()))?;
    if manifest.len() != raw_manifest_len || manifest.len() < 4 {
        return Err(DeltaError::Truncated);
    }
    let count = u32::from_le_bytes(manifest[0..4].try_into().unwrap()) as usize;
    let mut pos = 4usize;
    let mut entries = Vec::with_capacity(count.min(100_000));
    let mut seen_lower = HashSet::with_capacity(count.min(100_000));
    let payload_slice = &pack[payload_start..body_end];

    for _ in 0..count {
        if pos + 2 > manifest.len() {
            return Err(DeltaError::Truncated);
        }
        let plen = u16::from_le_bytes(manifest[pos..pos + 2].try_into().unwrap()) as usize;
        pos += 2;
        if pos + plen + 1 + 8 + 32 + 32 + 8 + 8 > manifest.len() {
            return Err(DeltaError::Truncated);
        }
        let path = std::str::from_utf8(&manifest[pos..pos + plen])
            .map_err(|_| DeltaError::InvalidUtf8)?
            .to_owned();
        pos += plen;
        if !is_safe_relative_path(&path) {
            return Err(DeltaError::UnsafePath(path));
        }
        if !seen_lower.insert(path.to_ascii_lowercase()) {
            return Err(DeltaError::DuplicatePath(path));
        }
        let mode = EntryMode::from_u8(manifest[pos])?;
        pos += 1;
        let raw_size = u64::from_le_bytes(manifest[pos..pos + 8].try_into().unwrap());
        pos += 8;
        let mut target_blake3 = [0u8; 32];
        target_blake3.copy_from_slice(&manifest[pos..pos + 32]);
        pos += 32;
        let mut base_blake3 = [0u8; 32];
        base_blake3.copy_from_slice(&manifest[pos..pos + 32]);
        pos += 32;
        let payload_offset = u64::from_le_bytes(manifest[pos..pos + 8].try_into().unwrap());
        pos += 8;
        let payload_len = u64::from_le_bytes(manifest[pos..pos + 8].try_into().unwrap());
        pos += 8;
        let end = payload_offset
            .checked_add(payload_len)
            .ok_or(DeltaError::Truncated)?;
        if end as usize > payload_slice.len() {
            return Err(DeltaError::Truncated);
        }
        entries.push(DeltaEntry {
            path,
            mode,
            raw_size,
            target_blake3,
            base_blake3,
            payload_offset,
            payload_len,
        });
    }
    Ok((entries, payload_slice))
}

pub fn apply_delta_pack(
    old_root: &Path,
    pack: &[u8],
    out_root: Option<&Path>,
) -> Result<usize, DeltaError> {
    let (entries, payload_slice) = parse_delta_pack(pack)?;
    entries.par_iter().try_for_each(|e| -> Result<(), DeltaError> {
        let out_bytes: Vec<u8> = match e.mode {
            EntryMode::Reuse => {
                let src = fs::read(old_root.join(&e.path))?;
                if src.len() as u64 != e.raw_size {
                    return Err(DeltaError::SizeMismatch(e.path.clone()));
                }
                if blake3::hash(&src).as_bytes() != &e.target_blake3 {
                    return Err(DeltaError::BaseHashMismatch(e.path.clone()));
                }
                src
            }
            EntryMode::FullZstd => {
                let slice = &payload_slice[e.payload_offset as usize..(e.payload_offset + e.payload_len) as usize];
                let dec = zstd::bulk::decompress(slice, (e.raw_size as usize) + 64)
                    .map_err(|err| DeltaError::Codec(err.to_string()))?;
                if dec.len() as u64 != e.raw_size {
                    return Err(DeltaError::SizeMismatch(e.path.clone()));
                }
                if blake3::hash(&dec).as_bytes() != &e.target_blake3 {
                    return Err(DeltaError::TargetHashMismatch(e.path.clone()));
                }
                dec
            }
            EntryMode::ZstdPatchFrom => {
                let base = fs::read(old_root.join(&e.path))?;
                if blake3::hash(&base).as_bytes() != &e.base_blake3 {
                    return Err(DeltaError::BaseHashMismatch(e.path.clone()));
                }
                let slice = &payload_slice[e.payload_offset as usize..(e.payload_offset + e.payload_len) as usize];
                let dec = zstd_decompress_patch(&base, slice, e.raw_size as usize)?;
                if blake3::hash(&dec).as_bytes() != &e.target_blake3 {
                    return Err(DeltaError::TargetHashMismatch(e.path.clone()));
                }
                dec
            }
        };
        if let Some(root) = out_root {
            let dest = root.join(&e.path);
            if let Some(parent) = dest.parent() {
                fs::create_dir_all(parent)?;
            }
            fs::write(dest, out_bytes)?;
        }
        Ok(())
    })?;
    Ok(entries.len())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn tmp_dir(tag: &str) -> PathBuf {
        let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
        let p = std::env::temp_dir().join(format!("exo-delta-{tag}-{nanos}"));
        fs::create_dir_all(&p).unwrap();
        p
    }

    #[test]
    fn path_security_rejects_traversal_and_reserved() {
        assert!(is_safe_relative_path("ExoLauncher.dll"));
        assert!(is_safe_relative_path("addons/capture/ExoCapture.exe"));
        assert!(!is_safe_relative_path("../evil.dll"));
        assert!(!is_safe_relative_path("a\\b.dll"));
        assert!(!is_safe_relative_path("C:/Windows/win.ini"));
        assert!(!is_safe_relative_path("dir/CON.txt"));
        assert!(!is_safe_relative_path("/abs/path"));
    }

    #[test]
    fn delta_roundtrip_reuse_patch_and_tamper_detection() {
        let old_dir = tmp_dir("old");
        let new_dir = tmp_dir("new");
        let out_dir = tmp_dir("out");

        let mut base_bin = vec![0u8; 256 * 1024];
        let mut s: u64 = 0x9e37_79b9_7f4a_7c15;
        for b in &mut base_bin {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            *b = (s & 0xff) as u8;
        }
        let mut patched_bin = base_bin.clone();
        for b in &mut patched_bin[120_000..121_024] {
            *b ^= 0x5a;
        }

        fs::write(old_dir.join("same.bin"), &base_bin[..8192]).unwrap();
        fs::write(new_dir.join("same.bin"), &base_bin[..8192]).unwrap();
        fs::write(old_dir.join("app.dll"), &base_bin).unwrap();
        fs::write(new_dir.join("app.dll"), &patched_bin).unwrap();
        fs::write(new_dir.join("added.txt"), b"hello exo v4").unwrap();

        let (pack, stats) = build_delta_pack(&old_dir, &new_dir, 9).unwrap();
        assert_eq!(stats.reused_files, 1);
        assert_eq!(stats.patched_files, 1);
        assert_eq!(stats.full_files, 1);
        assert!(stats.delta_wire_bytes < stats.file_level_zstd_bytes / 4);

        let count = apply_delta_pack(&old_dir, &pack, Some(&out_dir)).unwrap();
        assert_eq!(count, 3);
        assert_eq!(fs::read(out_dir.join("app.dll")).unwrap(), patched_bin);
        assert_eq!(fs::read(out_dir.join("same.bin")).unwrap(), &base_bin[..8192]);
        assert_eq!(fs::read(out_dir.join("added.txt")).unwrap(), b"hello exo v4");

        let mut tampered = pack.clone();
        tampered[20] ^= 1;
        assert!(apply_delta_pack(&old_dir, &tampered, None).is_err());

        let _ = fs::remove_dir_all(old_dir);
        let _ = fs::remove_dir_all(new_dir);
        let _ = fs::remove_dir_all(out_dir);
    }
}
