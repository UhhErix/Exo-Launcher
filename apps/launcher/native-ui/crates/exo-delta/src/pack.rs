use crate::{
    DELTA_MAGIC, DELTA_VERSION, DeltaEntry, DeltaError, DeltaStats, EntryMode, MAX_PATCH_RATIO,
    WINDOW_LOG, compute_tree_blake3_from_entries, scan_folder,
};
use rayon::prelude::*;
use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

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

struct BuiltItem {
    path: String,
    mode: EntryMode,
    raw_size: u64,
    target_blake3: [u8; 32],
    base_blake3: [u8; 32],
    payload: Vec<u8>,
    standalone_zstd_len: u64,
}

fn serialize_built(built: Vec<BuiltItem>) -> Result<(Vec<u8>, DeltaStats), DeltaError> {
    let mut stats = DeltaStats {
        total_files: built.len(),
        reused_files: 0,
        patched_files: 0,
        full_files: 0,
        total_raw_bytes: 0,
        changed_raw_bytes: 0,
        file_level_zstd_bytes: 0,
        delta_wire_bytes: 0,
        tree_blake3: [0u8; 32],
        pack_blake3: [0u8; 32],
    };

    let mut manifest = Vec::with_capacity(built.len() * 128);
    manifest.extend_from_slice(&(built.len() as u32).to_le_bytes());
    let mut entries = Vec::with_capacity(built.len());
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
        entries.push(DeltaEntry {
            path: item.path.clone(),
            mode: item.mode,
            raw_size: item.raw_size,
            target_blake3: item.target_blake3,
            base_blake3: item.base_blake3,
            payload_offset,
            payload_len: plen,
        });
        payload_offset += plen;
    }

    stats.tree_blake3 = compute_tree_blake3_from_entries(&entries);

    let compressed_manifest =
        zstd::bulk::compress(&manifest, 6).map_err(|e| DeltaError::Codec(e.to_string()))?;

    let mut out = Vec::with_capacity(16 + compressed_manifest.len() + payload_offset as usize + 32);
    out.extend_from_slice(&DELTA_MAGIC);
    out.push(DELTA_VERSION);
    out.extend_from_slice(&[0u8; 3]);
    out.extend_from_slice(&(manifest.len() as u32).to_le_bytes());
    out.extend_from_slice(&(compressed_manifest.len() as u32).to_le_bytes());
    out.extend_from_slice(&compressed_manifest);
    for item in &built {
        out.extend_from_slice(&item.payload);
    }

    let pack_hash = *blake3::hash(&out).as_bytes();
    out.extend_from_slice(&pack_hash);
    stats.pack_blake3 = pack_hash;
    stats.delta_wire_bytes = out.len() as u64;
    Ok((out, stats))
}

fn build_with_optional_old(
    old_map: &BTreeMap<String, PathBuf>,
    new_root: &Path,
    level: i32,
) -> Result<(Vec<u8>, DeltaStats), DeltaError> {
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
                let slen = standalone.len() as u64;
                return Ok(BuiltItem {
                    path: rel,
                    mode: EntryMode::FullZstd,
                    raw_size: new_bytes.len() as u64,
                    target_blake3: target_hash,
                    base_blake3: [0u8; 32],
                    payload: standalone,
                    standalone_zstd_len: slen,
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
    serialize_built(built?)
}

pub fn build_delta_pack(
    old_root: &Path,
    new_root: &Path,
    level: i32,
) -> Result<(Vec<u8>, DeltaStats), DeltaError> {
    let old_map = scan_folder(old_root)?;
    build_with_optional_old(&old_map, new_root, level)
}

pub fn build_full_pack(new_root: &Path, level: i32) -> Result<(Vec<u8>, DeltaStats), DeltaError> {
    let empty = BTreeMap::new();
    build_with_optional_old(&empty, new_root, level)
}
