use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fs::{self, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

pub const RECORDER_CONFIG_NAME: &str = "exo-shaders-recorder.json";
pub const RECORDER_DLL_NAME: &str = "d3d12.dll";
pub const RECORDER_SOURCE_DLL_NAME: &str = "exo_d3d12_recorder.dll";
pub const RECORDER_MANIFEST_NAME: &str = ".exo-shaders-recorder-manifest.json";
pub const EXOPSO_MAGIC: &[u8; 8] = b"EXOPSO01";
pub const EXOPLIB_MAGIC: &[u8; 8] = b"EXOPLB01";
pub const DEFAULT_MAX_RECORDED_PSOS: usize = 50_000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RecorderMode {
    Record,
    Warm,
    RecordAndWarm,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecorderConfig {
    pub version: u32,
    pub enabled: bool,
    pub game_id: String,
    pub mode: RecorderMode,
    pub record_path: String,
    pub library_path: String,
    pub expected_adapter_vendor: u32,
    pub expected_adapter_device: u32,
    pub expected_driver_version: String,
    pub max_recorded_psos: usize,
    #[serde(default)]
    pub stats_path: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecorderManifest {
    pub version: u32,
    pub game_id: String,
    pub dll_blake3: String,
    pub config_blake3: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecorderStats {
    pub library_loaded: bool,
    pub warm_hits: u64,
    pub warm_misses: u64,
    pub recorded_count: u64,
    pub fallback_count: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RecordedPsoKind {
    Compute,
    Graphics,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordedInputElement {
    pub semantic_name: String,
    pub semantic_index: u32,
    pub format: u32,
    pub input_slot: u32,
    pub aligned_byte_offset: u32,
    pub input_slot_class: u32,
    pub instance_data_step_rate: u32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordedGraphicsState {
    pub blend_state_bytes: Vec<u8>,
    pub sample_mask: u32,
    pub rasterizer_state_bytes: Vec<u8>,
    pub depth_stencil_state_bytes: Vec<u8>,
    pub ib_strip_cut_value: u32,
    pub primitive_topology_type: u32,
    pub num_render_targets: u32,
    pub rtv_formats: [u32; 8],
    pub dsv_format: u32,
    pub sample_count: u32,
    pub sample_quality: u32,
    pub input_elements: Vec<RecordedInputElement>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordedPsoRecord {
    pub key_hex: String,
    pub kind: RecordedPsoKind,
    pub root_sig_blob: Vec<u8>,
    #[serde(default)]
    pub vs: Vec<u8>,
    #[serde(default)]
    pub ps: Vec<u8>,
    #[serde(default)]
    pub ds: Vec<u8>,
    #[serde(default)]
    pub hs: Vec<u8>,
    #[serde(default)]
    pub gs: Vec<u8>,
    #[serde(default)]
    pub cs: Vec<u8>,
    #[serde(default)]
    pub graphics_state: Option<RecordedGraphicsState>,
    pub node_mask: u32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PipelineLibraryHeader {
    pub version: u32,
    pub adapter_vendor: u32,
    pub adapter_device: u32,
    pub driver_version: String,
    pub pso_count: usize,
    pub payload_bytes: usize,
}

pub fn sanitize_game_id(game_id: &str) -> String {
    let mut out = String::with_capacity(game_id.len());
    for ch in game_id.chars() {
        if ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' {
            out.push(ch);
        } else {
            out.push('_');
        }
    }
    if out.is_empty() {
        out.push_str("unknown");
    }
    out
}

pub fn recorded_pso_path(data_dir: &Path, game_id: &str) -> PathBuf {
    data_dir
        .join("recorded")
        .join(format!("{}.exopso", sanitize_game_id(game_id)))
}

pub fn pipeline_library_path(data_dir: &Path, game_id: &str) -> PathBuf {
    data_dir
        .join("libraries")
        .join(format!("{}.exoplib", sanitize_game_id(game_id)))
}

fn hash_bytes_tagged(h: &mut blake3::Hasher, tag: u8, bytes: &[u8]) {
    h.update(&[tag]);
    h.update(&(bytes.len() as u64).to_le_bytes());
    h.update(bytes);
}

pub fn compute_pso_key(root_sig_blob: &[u8], cs: &[u8], node_mask: u32) -> String {
    let mut h = blake3::Hasher::new();
    h.update(b"EXO_CS_PSO_V1");
    hash_bytes_tagged(&mut h, 1, root_sig_blob);
    hash_bytes_tagged(&mut h, 2, cs);
    h.update(&node_mask.to_le_bytes());
    h.finalize().to_hex()[..32].to_string()
}

#[allow(clippy::too_many_arguments)]
pub fn graphics_pso_key(
    root_sig_blob: &[u8],
    vs: &[u8],
    ps: &[u8],
    ds: &[u8],
    hs: &[u8],
    gs: &[u8],
    state: &RecordedGraphicsState,
    node_mask: u32,
) -> String {
    let mut h = blake3::Hasher::new();
    h.update(b"EXO_GFX_PSO_V1");
    hash_bytes_tagged(&mut h, 1, root_sig_blob);
    hash_bytes_tagged(&mut h, 2, vs);
    hash_bytes_tagged(&mut h, 3, ps);
    hash_bytes_tagged(&mut h, 4, ds);
    hash_bytes_tagged(&mut h, 5, hs);
    hash_bytes_tagged(&mut h, 6, gs);
    hash_bytes_tagged(&mut h, 7, &state.blend_state_bytes);
    h.update(&state.sample_mask.to_le_bytes());
    hash_bytes_tagged(&mut h, 8, &state.rasterizer_state_bytes);
    hash_bytes_tagged(&mut h, 9, &state.depth_stencil_state_bytes);
    h.update(&state.ib_strip_cut_value.to_le_bytes());
    h.update(&state.primitive_topology_type.to_le_bytes());
    h.update(&state.num_render_targets.to_le_bytes());
    for fmt in state.rtv_formats {
        h.update(&fmt.to_le_bytes());
    }
    h.update(&state.dsv_format.to_le_bytes());
    h.update(&state.sample_count.to_le_bytes());
    h.update(&state.sample_quality.to_le_bytes());
    h.update(&(state.input_elements.len() as u32).to_le_bytes());
    for el in &state.input_elements {
        hash_bytes_tagged(&mut h, 10, el.semantic_name.as_bytes());
        h.update(&el.semantic_index.to_le_bytes());
        h.update(&el.format.to_le_bytes());
        h.update(&el.input_slot.to_le_bytes());
        h.update(&el.aligned_byte_offset.to_le_bytes());
        h.update(&el.input_slot_class.to_le_bytes());
        h.update(&el.instance_data_step_rate.to_le_bytes());
    }
    h.update(&node_mask.to_le_bytes());
    h.finalize().to_hex()[..32].to_string()
}

pub fn library_entry_name(key_hex: &str) -> Vec<u16> {
    format!("p_{key_hex}")
        .encode_utf16()
        .chain(std::iter::once(0))
        .collect()
}

pub fn append_pso_record(path: &Path, record: &RecordedPsoRecord) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("create_dir_all failed: {e}"))?;
    }
    let payload =
        serde_json::to_vec(record).map_err(|e| format!("serialize record failed: {e}"))?;
    let digest = blake3::hash(&payload);
    let len = u32::try_from(payload.len()).map_err(|_| "PSO record exceeds 4 GiB".to_string())?;
    let needs_header = fs::metadata(path).map(|m| m.len() == 0).unwrap_or(true);
    let mut file = OpenOptions::new()
        .create(true)
        .append(true)
        .open(path)
        .map_err(|e| format!("open {} failed: {e}", path.display()))?;
    if needs_header {
        file.write_all(EXOPSO_MAGIC)
            .map_err(|e| format!("write magic failed: {e}"))?;
    }
    file.write_all(&len.to_le_bytes())
        .map_err(|e| format!("write len failed: {e}"))?;
    file.write_all(&digest.as_bytes()[..8])
        .map_err(|e| format!("write checksum failed: {e}"))?;
    file.write_all(&payload)
        .map_err(|e| format!("write payload failed: {e}"))?;
    Ok(())
}

pub fn read_pso_records(path: &Path) -> Result<Vec<RecordedPsoRecord>, String> {
    if !path.exists() {
        return Ok(Vec::new());
    }
    let mut bytes = Vec::new();
    fs::File::open(path)
        .map_err(|e| format!("open {} failed: {e}", path.display()))?
        .read_to_end(&mut bytes)
        .map_err(|e| format!("read {} failed: {e}", path.display()))?;
    if bytes.len() < EXOPSO_MAGIC.len() || &bytes[..8] != EXOPSO_MAGIC {
        return Err("invalid .exopso magic header".into());
    }
    let mut cursor = 8usize;
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    while cursor + 12 <= bytes.len() {
        let len = u32::from_le_bytes([
            bytes[cursor],
            bytes[cursor + 1],
            bytes[cursor + 2],
            bytes[cursor + 3],
        ]) as usize;
        let expected_tag = &bytes[cursor + 4..cursor + 12];
        cursor += 12;
        if cursor + len > bytes.len() {
            break;
        }
        let payload = &bytes[cursor..cursor + len];
        cursor += len;
        let digest = blake3::hash(payload);
        if &digest.as_bytes()[..8] != expected_tag {
            break;
        }
        if let Ok(rec) = serde_json::from_slice::<RecordedPsoRecord>(payload)
            && seen.insert(rec.key_hex.clone())
        {
            out.push(rec);
        }
    }
    Ok(out)
}

pub fn write_pipeline_library(
    path: &Path,
    adapter_vendor: u32,
    adapter_device: u32,
    driver_version: &str,
    pso_count: usize,
    payload: &[u8],
) -> Result<(), String> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|e| format!("create_dir_all failed: {e}"))?;
    }
    let header = PipelineLibraryHeader {
        version: 1,
        adapter_vendor,
        adapter_device,
        driver_version: driver_version.to_string(),
        pso_count,
        payload_bytes: payload.len(),
    };
    let header_bytes =
        serde_json::to_vec(&header).map_err(|e| format!("serialize library header failed: {e}"))?;
    let header_len =
        u32::try_from(header_bytes.len()).map_err(|_| "library header too large".to_string())?;
    let tmp = path.with_extension("exoplib.tmp");
    let mut file =
        fs::File::create(&tmp).map_err(|e| format!("create {} failed: {e}", tmp.display()))?;
    file.write_all(EXOPLIB_MAGIC)
        .and_then(|_| file.write_all(&header_len.to_le_bytes()))
        .and_then(|_| file.write_all(&header_bytes))
        .and_then(|_| file.write_all(payload))
        .and_then(|_| file.sync_all())
        .map_err(|e| format!("write library failed: {e}"))?;
    drop(file);
    fs::rename(&tmp, path).map_err(|e| format!("rename {} failed: {e}", path.display()))
}

pub fn read_pipeline_library_if_compatible(
    path: &Path,
    adapter_vendor: u32,
    adapter_device: u32,
    driver_version: &str,
) -> Result<Option<(PipelineLibraryHeader, Vec<u8>)>, String> {
    if !path.exists() {
        return Ok(None);
    }
    let bytes = fs::read(path).map_err(|e| format!("read {} failed: {e}", path.display()))?;
    if bytes.len() < 12 || &bytes[..8] != EXOPLIB_MAGIC {
        return Ok(None);
    }
    let header_len = u32::from_le_bytes([bytes[8], bytes[9], bytes[10], bytes[11]]) as usize;
    if 12 + header_len > bytes.len() {
        return Ok(None);
    }
    let Ok(header) = serde_json::from_slice::<PipelineLibraryHeader>(&bytes[12..12 + header_len])
    else {
        return Ok(None);
    };
    if header.version != 1
        || header.adapter_vendor != adapter_vendor
        || header.adapter_device != adapter_device
        || header.driver_version != driver_version
    {
        return Ok(None);
    }
    let payload = &bytes[12 + header_len..];
    if payload.len() != header.payload_bytes {
        return Ok(None);
    }
    Ok(Some((header, payload.to_vec())))
}
