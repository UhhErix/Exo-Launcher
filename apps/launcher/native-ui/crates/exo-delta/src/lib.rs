pub mod apply;
pub mod pack;

pub use apply::{ApplySummary, apply_delta_pack, verify_directory_against_pack};
pub use pack::{build_delta_pack, build_full_pack};

use ed25519_dalek::{Signature, Signer, SigningKey, Verifier, VerifyingKey};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashSet};
use std::fmt;
use std::fs;
use std::path::{Path, PathBuf};

pub const DELTA_MAGIC: [u8; 4] = *b"EXD1";
pub const DELTA_VERSION: u8 = 1;
pub const SIGNATURE_DOMAIN: &[u8] = b"exo-delta-v1\0";
pub(crate) const WINDOW_LOG: u32 = 26;
pub(crate) const MAX_PATCH_RATIO: f64 = 0.85;

#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum EntryMode {
    Reuse = 0,
    FullZstd = 1,
    ZstdPatchFrom = 2,
}

impl EntryMode {
    pub fn from_u8(v: u8) -> Result<Self, DeltaError> {
        match v {
            0 => Ok(Self::Reuse),
            1 => Ok(Self::FullZstd),
            2 => Ok(Self::ZstdPatchFrom),
            other => Err(DeltaError::InvalidMode(other)),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DeltaEntry {
    pub path: String,
    pub mode: EntryMode,
    pub raw_size: u64,
    pub target_blake3: [u8; 32],
    pub base_blake3: [u8; 32],
    pub payload_offset: u64,
    pub payload_len: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DeltaManifest {
    pub version: u8,
    pub entries: Vec<DeltaEntry>,
    pub tree_blake3: [u8; 32],
    pub pack_blake3: [u8; 32],
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DeltaStats {
    pub total_files: usize,
    pub reused_files: usize,
    pub patched_files: usize,
    pub full_files: usize,
    pub total_raw_bytes: u64,
    pub changed_raw_bytes: u64,
    pub file_level_zstd_bytes: u64,
    pub delta_wire_bytes: u64,
    pub tree_blake3: [u8; 32],
    pub pack_blake3: [u8; 32],
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
    SignatureInvalid(String),
    MissingBase(String),
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
    if path.is_empty()
        || path.starts_with('/')
        || path.ends_with('/')
        || path.contains('\\')
        || path.contains('\0')
    {
        return false;
    }
    if path.len() >= 2 && path.as_bytes()[1] == b':' {
        return false;
    }
    for seg in path.split('/') {
        if seg.is_empty() || seg == "." || seg == ".." {
            return false;
        }
        if seg.ends_with(' ') || seg.ends_with('.') {
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

#[cfg(windows)]
pub fn is_reparse_point(meta: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0400;
    (meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || meta.file_type().is_symlink()
}

#[cfg(not(windows))]
pub fn is_reparse_point(meta: &fs::Metadata) -> bool {
    meta.file_type().is_symlink()
}

pub fn assert_no_reparse_ancestors(root: &Path, target: &Path) -> Result<(), DeltaError> {
    let mut current = root.to_path_buf();
    if current.exists() {
        let meta = fs::symlink_metadata(&current)?;
        if is_reparse_point(&meta) {
            return Err(DeltaError::UnsafePath(current.display().to_string()));
        }
    }
    let rel = target
        .strip_prefix(root)
        .map_err(|_| DeltaError::UnsafePath(target.display().to_string()))?;
    for comp in rel.components() {
        current.push(comp);
        if current.exists() {
            let meta = fs::symlink_metadata(&current)?;
            if is_reparse_point(&meta) {
                return Err(DeltaError::UnsafePath(current.display().to_string()));
            }
        }
    }
    Ok(())
}

pub fn scan_folder(root: &Path) -> Result<BTreeMap<String, PathBuf>, DeltaError> {
    let root_meta = fs::symlink_metadata(root)?;
    if is_reparse_point(&root_meta) {
        return Err(DeltaError::UnsafePath(root.display().to_string()));
    }
    let mut out = BTreeMap::new();
    let mut seen_lower = HashSet::new();
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        for item in fs::read_dir(&dir)? {
            let entry = item?;
            let path = entry.path();
            let meta = fs::symlink_metadata(&path)?;
            if is_reparse_point(&meta) {
                return Err(DeltaError::UnsafePath(path.display().to_string()));
            }
            if meta.is_dir() {
                stack.push(path);
            } else if meta.is_file() {
                let rel = path
                    .strip_prefix(root)
                    .map_err(|_| DeltaError::UnsafePath(path.display().to_string()))?
                    .to_string_lossy()
                    .replace('\\', "/");
                if !is_safe_relative_path(&rel) {
                    return Err(DeltaError::UnsafePath(rel));
                }
                let lower = rel.to_ascii_lowercase();
                if !seen_lower.insert(lower) {
                    return Err(DeltaError::DuplicatePath(rel));
                }
                out.insert(rel, path);
            }
        }
    }
    Ok(out)
}

pub fn compute_tree_blake3_from_entries(entries: &[DeltaEntry]) -> [u8; 32] {
    let mut sorted: Vec<&DeltaEntry> = entries.iter().collect();
    sorted.sort_by(|a, b| a.path.cmp(&b.path));
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"exo-tree-v1\0");
    for e in sorted {
        hasher.update(&(e.path.len() as u32).to_le_bytes());
        hasher.update(e.path.as_bytes());
        hasher.update(&e.raw_size.to_le_bytes());
        hasher.update(&e.target_blake3);
    }
    *hasher.finalize().as_bytes()
}

pub fn hash_file_streaming(path: &Path) -> Result<([u8; 32], u64), DeltaError> {
    let mut file = fs::File::open(path)?;
    let mut hasher = blake3::Hasher::new();
    let mut buf = [0u8; 64 * 1024];
    let mut total = 0u64;
    loop {
        let n = std::io::Read::read(&mut file, &mut buf)?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        total += n as u64;
    }
    Ok((*hasher.finalize().as_bytes(), total))
}

pub fn tree_blake3(root: &Path) -> Result<[u8; 32], DeltaError> {
    let files = scan_folder(root)?;
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"exo-tree-v1\0");
    for (rel, path) in files {
        let (digest, size) = hash_file_streaming(&path)?;
        hasher.update(&(rel.len() as u32).to_le_bytes());
        hasher.update(rel.as_bytes());
        hasher.update(&size.to_le_bytes());
        hasher.update(&digest);
    }
    Ok(*hasher.finalize().as_bytes())
}

pub fn pack_trailer_hash(pack: &[u8]) -> Result<[u8; 32], DeltaError> {
    if pack.len() < 16 + 32 {
        return Err(DeltaError::Truncated);
    }
    let body_end = pack.len() - 32;
    let computed = *blake3::hash(&pack[..body_end]).as_bytes();
    if computed.as_slice() != &pack[body_end..] {
        return Err(DeltaError::TargetHashMismatch("pack trailer".into()));
    }
    Ok(computed)
}

fn signing_message(pack_hash: &[u8; 32]) -> [u8; 45] {
    let mut msg = [0u8; 45];
    msg[..13].copy_from_slice(SIGNATURE_DOMAIN);
    msg[13..].copy_from_slice(pack_hash);
    msg
}

pub fn sign_pack(pack: &[u8], signing_seed: &[u8; 32]) -> Result<([u8; 64], [u8; 32]), DeltaError> {
    let pack_hash = pack_trailer_hash(pack)?;
    let key = SigningKey::from_bytes(signing_seed);
    let msg = signing_message(&pack_hash);
    let sig = key.sign(&msg);
    Ok((sig.to_bytes(), key.verifying_key().to_bytes()))
}

pub fn verify_pack_signature(
    pack: &[u8],
    signature: &[u8; 64],
    public_key: &[u8; 32],
) -> Result<[u8; 32], DeltaError> {
    let pack_hash = pack_trailer_hash(pack)?;
    let vk = VerifyingKey::from_bytes(public_key)
        .map_err(|e| DeltaError::SignatureInvalid(e.to_string()))?;
    let sig = Signature::from_bytes(signature);
    let msg = signing_message(&pack_hash);
    vk.verify(&msg, &sig)
        .map_err(|e| DeltaError::SignatureInvalid(e.to_string()))?;
    Ok(pack_hash)
}

pub fn parse_delta_pack(pack: &[u8]) -> Result<(DeltaManifest, &[u8]), DeltaError> {
    let pack_blake3 = pack_trailer_hash(pack)?;
    let body_end = pack.len() - 32;
    if pack[0..4] != DELTA_MAGIC {
        return Err(DeltaError::BadMagic);
    }
    if pack[4] != DELTA_VERSION {
        return Err(DeltaError::UnsupportedVersion(pack[4]));
    }
    let raw_manifest_len = u32::from_le_bytes(pack[8..12].try_into().unwrap()) as usize;
    let comp_manifest_len = u32::from_le_bytes(pack[12..16].try_into().unwrap()) as usize;
    let payload_start = 16usize
        .checked_add(comp_manifest_len)
        .ok_or(DeltaError::Truncated)?;
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
    let tree = compute_tree_blake3_from_entries(&entries);
    Ok((
        DeltaManifest {
            version: DELTA_VERSION,
            entries,
            tree_blake3: tree,
            pack_blake3,
        },
        payload_slice,
    ))
}

pub fn encode_hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        use std::fmt::Write;
        let _ = write!(&mut s, "{b:02x}");
    }
    s
}

pub fn decode_hex<const N: usize>(hex: &str) -> Result<[u8; N], DeltaError> {
    let trimmed = hex.trim();
    if trimmed.len() != N * 2 {
        return Err(DeltaError::SignatureInvalid(format!(
            "expected {} hex chars, got {}",
            N * 2,
            trimmed.len()
        )));
    }
    let mut out = [0u8; N];
    for (i, slot) in out.iter_mut().enumerate() {
        let byte = u8::from_str_radix(&trimmed[i * 2..i * 2 + 2], 16)
            .map_err(|_| DeltaError::SignatureInvalid("invalid hex".into()))?;
        *slot = byte;
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn tmp_dir(tag: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let p =
            std::env::temp_dir().join(format!("exo-delta-{tag}-{}-{nanos}", std::process::id()));
        fs::create_dir_all(&p).unwrap();
        p
    }

    fn pseudo_bytes(len: usize, seed: u64) -> Vec<u8> {
        let mut out = vec![0u8; len];
        let mut s = seed;
        for b in &mut out {
            s ^= s << 13;
            s ^= s >> 7;
            s ^= s << 17;
            *b = (s & 0xff) as u8;
        }
        out
    }

    #[test]
    fn path_security_rejects_traversal_and_reserved() {
        assert!(is_safe_relative_path("ExoLauncher.dll"));
        assert!(is_safe_relative_path("addons/capture/ExoCapture.exe"));
        assert!(!is_safe_relative_path("../evil.dll"));
        assert!(!is_safe_relative_path("sub/../../evil.dll"));
        assert!(!is_safe_relative_path("a\\b.dll"));
        assert!(!is_safe_relative_path("C:/Windows/win.ini"));
        assert!(!is_safe_relative_path("dir/CON.txt"));
        assert!(!is_safe_relative_path("dir/NUL"));
        assert!(!is_safe_relative_path("/abs/path"));
        assert!(!is_safe_relative_path("trailing."));
        assert!(!is_safe_relative_path("trailing "));
    }

    #[test]
    fn delta_roundtrip_ed25519_and_tamper_gates() {
        let old_dir = tmp_dir("old");
        let new_dir = tmp_dir("new");
        let out_dir = tmp_dir("out");

        let base_bin = pseudo_bytes(256 * 1024, 0x9e37_79b9_7f4a_7c15);
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

        let seed = [0x42u8; 32];
        let (sig, pubkey) = sign_pack(&pack, &seed).unwrap();

        let summary =
            apply_delta_pack(Some(&old_dir), &pack, Some(&out_dir), Some((&sig, &pubkey))).unwrap();
        assert_eq!(summary.files_applied, 3);
        assert_eq!(summary.tree_blake3, stats.tree_blake3);
        assert_eq!(tree_blake3(&out_dir).unwrap(), stats.tree_blake3);
        assert_eq!(fs::read(out_dir.join("app.dll")).unwrap(), patched_bin);
        assert_eq!(
            fs::read(out_dir.join("same.bin")).unwrap(),
            &base_bin[..8192]
        );
        assert_eq!(
            fs::read(out_dir.join("added.txt")).unwrap(),
            b"hello exo v4"
        );

        let mut bad_sig = sig;
        bad_sig[0] ^= 1;
        let bad_sig_out = tmp_dir("bad-sig");
        assert!(matches!(
            apply_delta_pack(
                Some(&old_dir),
                &pack,
                Some(&bad_sig_out),
                Some((&bad_sig, &pubkey))
            ),
            Err(DeltaError::SignatureInvalid(_))
        ));
        assert!(fs::read_dir(&bad_sig_out).unwrap().next().is_none());

        let mut tampered_pack = pack.clone();
        tampered_pack[20] ^= 1;
        let bad_pack_out = tmp_dir("bad-pack");
        assert!(
            apply_delta_pack(
                Some(&old_dir),
                &tampered_pack,
                Some(&bad_pack_out),
                Some((&sig, &pubkey)),
            )
            .is_err()
        );
        assert!(fs::read_dir(&bad_pack_out).unwrap().next().is_none());

        let tampered_base = tmp_dir("bad-base");
        let mut flipped_base = base_bin.clone();
        flipped_base[100] ^= 1;
        fs::write(tampered_base.join("same.bin"), &base_bin[..8192]).unwrap();
        fs::write(tampered_base.join("app.dll"), &flipped_base).unwrap();
        let bad_base_out = tmp_dir("bad-base-out");
        assert!(matches!(
            apply_delta_pack(
                Some(&tampered_base),
                &pack,
                Some(&bad_base_out),
                Some((&sig, &pubkey)),
            ),
            Err(DeltaError::BaseHashMismatch(_))
        ));

        let _ = fs::remove_dir_all(old_dir);
        let _ = fs::remove_dir_all(new_dir);
        let _ = fs::remove_dir_all(out_dir);
        let _ = fs::remove_dir_all(bad_sig_out);
        let _ = fs::remove_dir_all(bad_pack_out);
        let _ = fs::remove_dir_all(tampered_base);
        let _ = fs::remove_dir_all(bad_base_out);
    }

    #[test]
    fn full_pack_fresh_install_roundtrip() {
        let src = tmp_dir("full-src");
        let dst = tmp_dir("full-dst");
        fs::create_dir_all(src.join("sub")).unwrap();
        fs::write(src.join("ExoLauncher.exe"), pseudo_bytes(64 * 1024, 17)).unwrap();
        fs::write(src.join("sub/data.bin"), pseudo_bytes(32 * 1024, 99)).unwrap();

        let (pack, stats) = build_full_pack(&src, 6).unwrap();
        assert_eq!(stats.reused_files, 0);
        assert_eq!(stats.patched_files, 0);
        assert_eq!(stats.full_files, 2);

        let seed = [0x11u8; 32];
        let (sig, pubkey) = sign_pack(&pack, &seed).unwrap();
        let summary = apply_delta_pack(None, &pack, Some(&dst), Some((&sig, &pubkey))).unwrap();
        assert_eq!(summary.files_applied, 2);
        assert_eq!(
            verify_directory_against_pack(&dst, &pack, Some((&sig, &pubkey))).unwrap(),
            stats.tree_blake3
        );

        let _ = fs::remove_dir_all(src);
        let _ = fs::remove_dir_all(dst);
    }

    #[cfg(windows)]
    #[test]
    fn reparse_point_junction_escape_is_rejected() {
        use std::process::Command;
        let root = tmp_dir("reparse-root");
        let outside = tmp_dir("reparse-outside");
        fs::write(outside.join("secret.txt"), b"outside").unwrap();
        let junction = root.join("escape");
        let status = Command::new("cmd")
            .args([
                "/C",
                "mklink",
                "/J",
                &junction.to_string_lossy(),
                &outside.to_string_lossy(),
            ])
            .status()
            .unwrap();
        assert!(status.success());
        assert!(matches!(scan_folder(&root), Err(DeltaError::UnsafePath(_))));
        let _ = fs::remove_dir(&junction);
        let _ = fs::remove_dir_all(root);
        let _ = fs::remove_dir_all(outside);
    }
}
