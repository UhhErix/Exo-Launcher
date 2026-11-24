use std::path::Path;

use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};

use crate::downloader::{
    ChunkCompression, ChunkHash, ChunkSpec, DownloadThrottle, FileChunkSlice, FileLayoutSpec,
    InstallOptions, InstallReport, StoreManifest, VerifyReport, install_or_update,
    uninstall_manifest, verify_and_repair,
};
use crate::hash::to_hex;
use crate::security::StoreGamePolicy;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FuelJsonFile {
    pub path: String,
    pub size: u64,
    pub sha256: String,
    pub url_path: String,
    #[serde(default)]
    pub zlib: bool,
    #[serde(default)]
    pub tags: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FuelJsonManifest {
    pub product_id: String,
    pub build_id: String,
    pub cdn_bases: Vec<String>,
    pub files: Vec<FuelJsonFile>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FuelProtoFileEntry {
    pub path: String,
    pub size: u64,
    pub sha256_hex: String,
}

fn read_varint(buf: &[u8], pos: &mut usize) -> Option<u64> {
    let mut out = 0u64;
    let mut shift = 0u32;
    while *pos < buf.len() && shift < 64 {
        let b = buf[*pos];
        *pos += 1;
        out |= u64::from(b & 0x7F) << shift;
        if b & 0x80 == 0 {
            return Some(out);
        }
        shift += 7;
    }
    None
}

pub fn decode_fuel_proto_file_entries(buf: &[u8]) -> Result<Vec<FuelProtoFileEntry>, String> {
    let mut pos = 0usize;
    let mut entries = Vec::new();
    while pos < buf.len() {
        let tag = read_varint(buf, &mut pos).ok_or_else(|| "Truncated protobuf tag".to_string())?;
        let field = tag >> 3;
        let wire = tag & 0x07;
        match (field, wire) {
            (1, 2) => {
                let len = read_varint(buf, &mut pos)
                    .ok_or_else(|| "Truncated protobuf message length".to_string())?
                    as usize;
                if pos + len > buf.len() {
                    return Err("Protobuf file entry extends past buffer".into());
                }
                let sub = &buf[pos..pos + len];
                pos += len;
                entries.push(decode_single_fuel_proto_entry(sub)?);
            }
            (_, 0) => {
                let _ = read_varint(buf, &mut pos)
                    .ok_or_else(|| "Truncated protobuf varint".to_string())?;
            }
            (_, 2) => {
                let len = read_varint(buf, &mut pos)
                    .ok_or_else(|| "Truncated protobuf bytes length".to_string())?
                    as usize;
                if pos + len > buf.len() {
                    return Err("Protobuf bytes field out of range".into());
                }
                pos += len;
            }
            _ => return Err(format!("Unsupported protobuf wire type {wire}")),
        }
    }
    Ok(entries)
}

fn decode_single_fuel_proto_entry(sub: &[u8]) -> Result<FuelProtoFileEntry, String> {
    let mut pos = 0usize;
    let mut path = String::new();
    let mut size = 0u64;
    let mut sha256_hex = String::new();
    while pos < sub.len() {
        let tag = read_varint(sub, &mut pos).ok_or_else(|| "Truncated entry tag".to_string())?;
        let field = tag >> 3;
        let wire = tag & 0x07;
        match (field, wire) {
            (1, 2) => {
                let len = read_varint(sub, &mut pos).ok_or("Truncated path len")? as usize;
                if pos + len > sub.len() {
                    return Err("Protobuf path out of range".into());
                }
                path = String::from_utf8_lossy(&sub[pos..pos + len]).to_string();
                pos += len;
            }
            (2, 0) => {
                size = read_varint(sub, &mut pos).ok_or("Truncated size varint")?;
            }
            (3, 2) => {
                let len = read_varint(sub, &mut pos).ok_or("Truncated hash len")? as usize;
                if pos + len > sub.len() {
                    return Err("Protobuf hash out of range".into());
                }
                sha256_hex = to_hex(&sub[pos..pos + len]);
                pos += len;
            }
            (_, 0) => {
                let _ = read_varint(sub, &mut pos).ok_or("Truncated unknown varint")?;
            }
            (_, 2) => {
                let len = read_varint(sub, &mut pos).ok_or("Truncated unknown len")? as usize;
                if pos + len > sub.len() {
                    return Err("Protobuf unknown field out of range".into());
                }
                pos += len;
            }
            _ => return Err(format!("Unsupported entry wire type {wire}")),
        }
    }
    if path.is_empty() {
        return Err("Fuel protobuf entry missing path".into());
    }
    Ok(FuelProtoFileEntry {
        path,
        size,
        sha256_hex,
    })
}

pub fn parse_fuel_json_manifest(bytes: &[u8]) -> Result<StoreManifest, String> {
    let raw: FuelJsonManifest =
        serde_json::from_slice(bytes).map_err(|e| format!("Amazon Fuel manifest error: {e}"))?;
    let mut chunks = Vec::with_capacity(raw.files.len());
    let mut files = Vec::with_capacity(raw.files.len());
    for (idx, f) in raw.files.into_iter().enumerate() {
        let id = format!("fuel-{idx}-{}", f.sha256);
        chunks.push(ChunkSpec {
            id: id.clone(),
            url_path: f.url_path,
            hash: ChunkHash::Sha256(f.sha256.clone()),
            compressed_size: f.size,
            uncompressed_size: f.size,
            compression: if f.zlib {
                ChunkCompression::Zlib
            } else {
                ChunkCompression::Raw
            },
        });
        files.push(FileLayoutSpec {
            rel_path: f.path,
            total_size: f.size,
            sha256: Some(f.sha256),
            tags: f.tags,
            slices: vec![FileChunkSlice {
                chunk_id: id,
                chunk_offset: 0,
                file_offset: 0,
                length: f.size,
            }],
        });
    }
    Ok(StoreManifest {
        store: "amazon".into(),
        app_id: raw.product_id,
        build_version: raw.build_id,
        cdn_bases: raw.cdn_bases,
        chunks,
        files,
    })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AmazonGameItem {
    pub product_id: String,
    pub title: String,
    pub executable: String,
    pub anti_cheat: AntiCheatClass,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AmazonLaunchPlan {
    pub executable: String,
    pub args: Vec<String>,
    pub policy: StoreGamePolicy,
}

#[derive(Debug, Default)]
pub struct AmazonClient;

impl AmazonClient {
    pub fn install(
        &self,
        manifest: &StoreManifest,
        install_root: &Path,
        opts: &InstallOptions,
        throttle: Option<&DownloadThrottle>,
    ) -> Result<InstallReport, String> {
        install_or_update(manifest, install_root, opts, throttle)
    }

    pub fn verify_and_repair(
        &self,
        manifest: &StoreManifest,
        install_root: &Path,
        opts: &InstallOptions,
    ) -> Result<VerifyReport, String> {
        verify_and_repair(manifest, install_root, opts)
    }

    pub fn uninstall(
        &self,
        manifest: &StoreManifest,
        install_root: &Path,
    ) -> Result<usize, String> {
        uninstall_manifest(manifest, install_root)
    }

    pub fn plan_launch(
        &self,
        item: &AmazonGameItem,
        extra_args: &[String],
    ) -> Result<AmazonLaunchPlan, String> {
        if item.executable.trim().is_empty() {
            return Err("Amazon launch executable is empty".into());
        }
        let policy = StoreGamePolicy::for_class(item.anti_cheat);
        Ok(AmazonLaunchPlan {
            executable: item.executable.clone(),
            args: extra_args.to_vec(),
            policy,
        })
    }
}
