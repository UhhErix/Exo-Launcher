use crate::{
    DeltaError, EntryMode, WINDOW_LOG, assert_no_reparse_ancestors, is_reparse_point,
    parse_delta_pack, tree_blake3, verify_pack_signature,
};
use rayon::prelude::*;
use std::fs;
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ApplySummary {
    pub files_applied: usize,
    pub hardlinked_files: usize,
    pub patched_files: usize,
    pub full_files: usize,
    pub tree_blake3: [u8; 32],
    pub pack_blake3: [u8; 32],
}

fn zstd_decompress_patch(
    base: &[u8],
    patch: &[u8],
    expected_len: usize,
) -> Result<Vec<u8>, DeltaError> {
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
        return Err(DeltaError::SizeMismatch(format!(
            "got {n}, expected {expected_len}"
        )));
    }
    Ok(out)
}

fn ensure_safe_parent(root: &Path, dest: &Path) -> Result<(), DeltaError> {
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent)?;
        assert_no_reparse_ancestors(root, parent)?;
    }
    Ok(())
}

pub fn apply_delta_pack(
    old_root: Option<&Path>,
    pack: &[u8],
    out_root: Option<&Path>,
    verify_sig: Option<(&[u8; 64], &[u8; 32])>,
) -> Result<ApplySummary, DeltaError> {
    if let Some((sig, pubkey)) = verify_sig {
        verify_pack_signature(pack, sig, pubkey)?;
    }
    let (manifest, payload_slice) = parse_delta_pack(pack)?;

    if let Some(root) = old_root {
        let meta = fs::symlink_metadata(root)?;
        if is_reparse_point(&meta) {
            return Err(DeltaError::UnsafePath(root.display().to_string()));
        }
    }
    if let Some(root) = out_root {
        fs::create_dir_all(root)?;
        let meta = fs::symlink_metadata(root)?;
        if is_reparse_point(&meta) {
            return Err(DeltaError::UnsafePath(root.display().to_string()));
        }
    }

    let hardlinked = AtomicUsize::new(0);
    let patched = AtomicUsize::new(0);
    let full = AtomicUsize::new(0);

    manifest
        .entries
        .par_iter()
        .try_for_each(|e| -> Result<(), DeltaError> {
            match e.mode {
                EntryMode::Reuse => {
                    let base_dir =
                        old_root.ok_or_else(|| DeltaError::MissingBase(e.path.clone()))?;
                    let src_path = base_dir.join(&e.path);
                    assert_no_reparse_ancestors(base_dir, &src_path)?;
                    let src = fs::read(&src_path)?;
                    if src.len() as u64 != e.raw_size {
                        return Err(DeltaError::SizeMismatch(e.path.clone()));
                    }
                    if blake3::hash(&src).as_bytes() != &e.target_blake3 {
                        return Err(DeltaError::BaseHashMismatch(e.path.clone()));
                    }
                    if let Some(root) = out_root {
                        let dest = root.join(&e.path);
                        ensure_safe_parent(root, &dest)?;
                        if dest.exists() {
                            fs::remove_file(&dest)?;
                        }
                        if fs::hard_link(&src_path, &dest).is_ok() {
                            hardlinked.fetch_add(1, Ordering::Relaxed);
                        } else {
                            fs::write(&dest, src)?;
                        }
                    }
                }
                EntryMode::FullZstd => {
                    let slice = &payload_slice
                        [e.payload_offset as usize..(e.payload_offset + e.payload_len) as usize];
                    let dec = zstd::bulk::decompress(slice, (e.raw_size as usize) + 64)
                        .map_err(|err| DeltaError::Codec(err.to_string()))?;
                    if dec.len() as u64 != e.raw_size {
                        return Err(DeltaError::SizeMismatch(e.path.clone()));
                    }
                    if blake3::hash(&dec).as_bytes() != &e.target_blake3 {
                        return Err(DeltaError::TargetHashMismatch(e.path.clone()));
                    }
                    full.fetch_add(1, Ordering::Relaxed);
                    if let Some(root) = out_root {
                        let dest = root.join(&e.path);
                        ensure_safe_parent(root, &dest)?;
                        fs::write(&dest, dec)?;
                    }
                }
                EntryMode::ZstdPatchFrom => {
                    let base_dir =
                        old_root.ok_or_else(|| DeltaError::MissingBase(e.path.clone()))?;
                    let src_path = base_dir.join(&e.path);
                    assert_no_reparse_ancestors(base_dir, &src_path)?;
                    let base = fs::read(&src_path)?;
                    if blake3::hash(&base).as_bytes() != &e.base_blake3 {
                        return Err(DeltaError::BaseHashMismatch(e.path.clone()));
                    }
                    let slice = &payload_slice
                        [e.payload_offset as usize..(e.payload_offset + e.payload_len) as usize];
                    let dec = zstd_decompress_patch(&base, slice, e.raw_size as usize)?;
                    if blake3::hash(&dec).as_bytes() != &e.target_blake3 {
                        return Err(DeltaError::TargetHashMismatch(e.path.clone()));
                    }
                    patched.fetch_add(1, Ordering::Relaxed);
                    if let Some(root) = out_root {
                        let dest = root.join(&e.path);
                        ensure_safe_parent(root, &dest)?;
                        fs::write(&dest, dec)?;
                    }
                }
            }
            Ok(())
        })?;

    if let Some(root) = out_root {
        let actual_tree = tree_blake3(root)?;
        if actual_tree != manifest.tree_blake3 {
            return Err(DeltaError::TargetHashMismatch(
                "post-apply tree blake3 mismatch".into(),
            ));
        }
    }

    Ok(ApplySummary {
        files_applied: manifest.entries.len(),
        hardlinked_files: hardlinked.load(Ordering::Relaxed),
        patched_files: patched.load(Ordering::Relaxed),
        full_files: full.load(Ordering::Relaxed),
        tree_blake3: manifest.tree_blake3,
        pack_blake3: manifest.pack_blake3,
    })
}

pub fn verify_directory_against_pack(
    dir: &Path,
    pack: &[u8],
    verify_sig: Option<(&[u8; 64], &[u8; 32])>,
) -> Result<[u8; 32], DeltaError> {
    if let Some((sig, pubkey)) = verify_sig {
        verify_pack_signature(pack, sig, pubkey)?;
    }
    let (manifest, _) = parse_delta_pack(pack)?;
    let actual = tree_blake3(dir)?;
    if actual != manifest.tree_blake3 {
        return Err(DeltaError::TargetHashMismatch(
            "directory tree blake3 mismatch".into(),
        ));
    }
    Ok(actual)
}
