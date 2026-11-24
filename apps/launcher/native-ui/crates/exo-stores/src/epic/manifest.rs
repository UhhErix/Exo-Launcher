use std::io::Read;

use flate2::read::ZlibDecoder;
use serde::{Deserialize, Serialize};

use crate::downloader::{
    ChunkCompression, ChunkHash, ChunkSpec, FileChunkSlice, FileLayoutSpec, StoreManifest,
};

pub const EPIC_BINARY_MANIFEST_MAGIC: u32 = 0x44BE_C00C;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EpicJsonChunk {
    pub guid: String,
    pub url_path: String,
    pub sha256: Option<String>,
    pub sha1: Option<String>,
    pub compressed_size: u64,
    pub uncompressed_size: u64,
    #[serde(default)]
    pub zlib: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EpicJsonFilePart {
    pub guid: String,
    pub chunk_offset: u64,
    pub file_offset: u64,
    pub size: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EpicJsonFile {
    pub filename: String,
    pub file_size: u64,
    pub sha256: Option<String>,
    #[serde(default)]
    pub install_tags: Vec<String>,
    pub parts: Vec<EpicJsonFilePart>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EpicJsonManifest {
    pub app_name: String,
    pub build_version: String,
    pub launch_exe: String,
    pub cdn_bases: Vec<String>,
    pub chunks: Vec<EpicJsonChunk>,
    pub files: Vec<EpicJsonFile>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EpicBinaryHeader {
    pub header_size: u32,
    pub size_uncompressed: u32,
    pub size_compressed: u32,
    pub sha1: [u8; 20],
    pub stored_as: u8,
    pub version: u32,
}

pub fn parse_epic_binary_header(bytes: &[u8]) -> Result<EpicBinaryHeader, String> {
    if bytes.len() < 41 {
        return Err("Epic binary manifest truncated (<41 bytes)".into());
    }
    let magic = u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
    if magic != EPIC_BINARY_MANIFEST_MAGIC {
        return Err(format!("Bad Epic binary manifest magic 0x{magic:08X}"));
    }
    let header_size = u32::from_le_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]);
    if (header_size as usize) < 41 || (header_size as usize) > bytes.len() {
        return Err(format!("Invalid Epic binary header size {header_size}"));
    }
    let size_uncompressed = u32::from_le_bytes([bytes[8], bytes[9], bytes[10], bytes[11]]);
    let size_compressed = u32::from_le_bytes([bytes[12], bytes[13], bytes[14], bytes[15]]);
    let mut sha1 = [0u8; 20];
    sha1.copy_from_slice(&bytes[16..36]);
    let stored_as = bytes[36];
    let version = u32::from_le_bytes([bytes[37], bytes[38], bytes[39], bytes[40]]);
    Ok(EpicBinaryHeader {
        header_size,
        size_uncompressed,
        size_compressed,
        sha1,
        stored_as,
        version,
    })
}

pub fn parse_epic_manifest(bytes: &[u8]) -> Result<StoreManifest, String> {
    if bytes.len() >= 4
        && u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])
            == EPIC_BINARY_MANIFEST_MAGIC
    {
        let hdr = parse_epic_binary_header(bytes)?;
        let payload = &bytes[hdr.header_size as usize..];
        let decoded = if hdr.stored_as & 0x01 != 0 {
            let mut dec = ZlibDecoder::new(payload);
            let mut out = Vec::with_capacity(hdr.size_uncompressed as usize);
            dec.read_to_end(&mut out)
                .map_err(|e| format!("Epic manifest zlib decode failed: {e}"))?;
            out
        } else {
            payload.to_vec()
        };
        let actual_sha1 = crate::hash::sha1(&decoded);
        if !crate::hash::constant_time_eq(&actual_sha1, &hdr.sha1) {
            return Err("Epic binary manifest payload SHA-1 mismatch".into());
        }
        return parse_epic_json_manifest(&decoded);
    }
    parse_epic_json_manifest(bytes)
}

pub fn parse_epic_json_manifest(bytes: &[u8]) -> Result<StoreManifest, String> {
    let raw: EpicJsonManifest =
        serde_json::from_slice(bytes).map_err(|e| format!("Epic manifest JSON error: {e}"))?;
    let mut chunks = Vec::with_capacity(raw.chunks.len());
    for c in raw.chunks {
        let hash = if let Some(s256) = c.sha256 {
            ChunkHash::Sha256(s256)
        } else if let Some(s1) = c.sha1 {
            ChunkHash::Sha1(s1)
        } else {
            return Err(format!(
                "Epic chunk '{}' missing SHA-256/SHA-1 hash",
                c.guid
            ));
        };
        chunks.push(ChunkSpec {
            id: c.guid,
            url_path: c.url_path,
            hash,
            compressed_size: c.compressed_size,
            uncompressed_size: c.uncompressed_size,
            compression: if c.zlib {
                ChunkCompression::Zlib
            } else {
                ChunkCompression::Raw
            },
        });
    }
    let files = raw
        .files
        .into_iter()
        .map(|f| FileLayoutSpec {
            rel_path: f.filename,
            total_size: f.file_size,
            sha256: f.sha256,
            tags: f.install_tags,
            slices: f
                .parts
                .into_iter()
                .map(|p| FileChunkSlice {
                    chunk_id: p.guid,
                    chunk_offset: p.chunk_offset,
                    file_offset: p.file_offset,
                    length: p.size,
                })
                .collect(),
        })
        .collect();
    Ok(StoreManifest {
        store: "epic".into(),
        app_id: raw.app_name,
        build_version: raw.build_version,
        cdn_bases: raw.cdn_bases,
        chunks,
        files,
    })
}
