use std::io::Read;

use flate2::read::ZlibDecoder;
use serde::{Deserialize, Serialize};

use crate::downloader::{
    ChunkCompression, ChunkHash, ChunkSpec, FileChunkSlice, FileLayoutSpec, StoreManifest,
};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GogDepotChunk {
    pub compressed_md5: String,
    pub md5: String,
    pub sha256: Option<String>,
    pub compressed_size: u64,
    pub size: u64,
    pub url_path: String,
    #[serde(default = "default_zlib")]
    pub zlib: bool,
}

fn default_zlib() -> bool {
    true
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GogDepotFile {
    pub path: String,
    pub sha256: Option<String>,
    #[serde(default)]
    pub tags: Vec<String>,
    pub chunks: Vec<GogDepotChunk>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GogDepotManifestV2 {
    pub product_id: String,
    pub build_id: String,
    pub cdn_bases: Vec<String>,
    pub files: Vec<GogDepotFile>,
}

pub fn parse_gog_depot_manifest(bytes: &[u8]) -> Result<StoreManifest, String> {
    let json_bytes = if bytes.first().copied() == Some(0x78) {
        let mut dec = ZlibDecoder::new(bytes);
        let mut out = Vec::new();
        dec.read_to_end(&mut out)
            .map_err(|e| format!("GOG depot zlib decode failed: {e}"))?;
        out
    } else {
        bytes.to_vec()
    };

    let raw: GogDepotManifestV2 =
        serde_json::from_slice(&json_bytes).map_err(|e| format!("GOG depot JSON error: {e}"))?;

    let mut chunks = Vec::new();
    let mut files = Vec::new();
    for f in raw.files {
        let mut file_offset = 0u64;
        let mut slices = Vec::with_capacity(f.chunks.len());
        for c in f.chunks {
            let chunk_id = if !c.compressed_md5.is_empty() {
                c.compressed_md5.clone()
            } else {
                c.md5.clone()
            };
            let hash = if let Some(s256) = c.sha256 {
                ChunkHash::Sha256(s256)
            } else {
                ChunkHash::Md5(c.md5)
            };
            chunks.push(ChunkSpec {
                id: chunk_id.clone(),
                url_path: c.url_path,
                hash,
                compressed_size: c.compressed_size,
                uncompressed_size: c.size,
                compression: if c.zlib {
                    ChunkCompression::Zlib
                } else {
                    ChunkCompression::Raw
                },
            });
            slices.push(FileChunkSlice {
                chunk_id,
                chunk_offset: 0,
                file_offset,
                length: c.size,
            });
            file_offset = file_offset.saturating_add(c.size);
        }
        files.push(FileLayoutSpec {
            rel_path: f.path,
            total_size: file_offset,
            sha256: f.sha256,
            tags: f.tags,
            slices,
        });
    }

    Ok(StoreManifest {
        store: "gog".into(),
        app_id: raw.product_id,
        build_version: raw.build_id,
        cdn_bases: raw.cdn_bases,
        chunks,
        files,
    })
}
