use std::collections::{HashMap, HashSet};
use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use flate2::read::ZlibDecoder;
use serde::{Deserialize, Serialize};

use crate::hash::{constant_time_eq, md5, sha1, sha256, to_hex};
use crate::http::PooledHttpClient;
use crate::security::validate_manifest_rel_path;

pub const LEDGER_FILE_NAME: &str = ".exo-install-ledger.json";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum ChunkCompression {
    Raw,
    Zlib,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ChunkHash {
    Sha256(String),
    Sha1(String),
    Md5(String),
}

impl ChunkHash {
    pub fn verify(&self, data: &[u8]) -> bool {
        match self {
            Self::Sha256(expected) => {
                let actual = to_hex(&sha256(data));
                constant_time_eq(actual.as_bytes(), expected.to_ascii_lowercase().as_bytes())
            }
            Self::Sha1(expected) => {
                let actual = to_hex(&sha1(data));
                constant_time_eq(actual.as_bytes(), expected.to_ascii_lowercase().as_bytes())
            }
            Self::Md5(expected) => {
                let actual = to_hex(&md5(data));
                constant_time_eq(actual.as_bytes(), expected.to_ascii_lowercase().as_bytes())
            }
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChunkSpec {
    pub id: String,
    pub url_path: String,
    pub hash: ChunkHash,
    pub compressed_size: u64,
    pub uncompressed_size: u64,
    pub compression: ChunkCompression,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileChunkSlice {
    pub chunk_id: String,
    pub chunk_offset: u64,
    pub file_offset: u64,
    pub length: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FileLayoutSpec {
    pub rel_path: String,
    pub total_size: u64,
    pub sha256: Option<String>,
    pub tags: Vec<String>,
    pub slices: Vec<FileChunkSlice>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StoreManifest {
    pub store: String,
    pub app_id: String,
    pub build_version: String,
    pub cdn_bases: Vec<String>,
    pub chunks: Vec<ChunkSpec>,
    pub files: Vec<FileLayoutSpec>,
}

#[derive(Debug, Clone)]
pub struct CdnProbeResult {
    pub base_url: String,
    pub probe_ms: f64,
    pub bytes_read: usize,
    pub throughput_mb_s: f64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct InstallLedger {
    pub app_id: String,
    pub build_version: String,
    pub completed_chunks: HashSet<String>,
}

impl InstallLedger {
    pub fn load(install_root: &Path) -> Option<Self> {
        let raw = std::fs::read_to_string(install_root.join(LEDGER_FILE_NAME)).ok()?;
        serde_json::from_str(&raw).ok()
    }

    pub fn save(&self, install_root: &Path) -> Result<(), String> {
        std::fs::create_dir_all(install_root).map_err(|e| e.to_string())?;
        let path = install_root.join(LEDGER_FILE_NAME);
        let tmp = install_root.join(".exo-install-ledger.json.tmp");
        let json = serde_json::to_string(self).map_err(|e| e.to_string())?;
        std::fs::write(&tmp, json).map_err(|e| e.to_string())?;
        if path.exists() {
            let _ = std::fs::remove_file(&path);
        }
        std::fs::rename(&tmp, &path).map_err(|e| e.to_string())
    }

    pub fn clear(install_root: &Path) -> Result<(), String> {
        let path = install_root.join(LEDGER_FILE_NAME);
        match std::fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e.to_string()),
        }
    }
}

#[derive(Debug, Default)]
pub struct DownloadThrottle {
    pub paused_for_game: AtomicBool,
    pub max_bytes_per_sec: AtomicU64,
    pub cancel_requested: AtomicBool,
    pub stop_after_chunks: AtomicUsize,
}

impl DownloadThrottle {
    pub fn set_game_running(&self, running: bool, throttle_bps: u64) {
        self.paused_for_game
            .store(running && throttle_bps == 0, Ordering::SeqCst);
        self.max_bytes_per_sec
            .store(if running { throttle_bps } else { 0 }, Ordering::SeqCst);
    }

    pub fn request_cancel(&self) {
        self.cancel_requested.store(true, Ordering::SeqCst);
    }
}

#[derive(Debug, Clone)]
pub struct DeltaPlan {
    pub active_files: Vec<FileLayoutSpec>,
    pub skipped_optional_files: usize,
    pub chunks_to_download: Vec<String>,
    pub chunks_reused: usize,
    pub bytes_to_download: u64,
    pub bytes_reused: u64,
}

#[derive(Debug, Clone)]
pub struct InstallReport {
    pub downloaded_chunks: usize,
    pub reused_chunks: usize,
    pub bytes_downloaded: u64,
    pub bytes_written: u64,
    pub selected_cdn: String,
    pub cdn_failovers: usize,
    pub elapsed_ms: f64,
    pub throughput_mb_s: f64,
    pub paused_or_interrupted: bool,
}

#[derive(Debug, Clone)]
pub struct VerifyReport {
    pub files_checked: usize,
    pub corrupted_files: Vec<String>,
    pub repaired_chunks: usize,
    pub all_valid_after_repair: bool,
}

#[derive(Debug, Clone)]
pub struct InstallOptions {
    pub workers: usize,
    pub allow_loopback: bool,
    pub excluded_tags: HashSet<String>,
    pub bearer_token: Option<String>,
    pub timeout_ms: u64,
}

impl Default for InstallOptions {
    fn default() -> Self {
        Self {
            workers: 4,
            allow_loopback: false,
            excluded_tags: HashSet::new(),
            bearer_token: None,
            timeout_ms: 15_000,
        }
    }
}

pub fn race_cdns(
    cdn_bases: &[String],
    probe_path: &str,
    allow_loopback: bool,
    timeout_ms: u64,
) -> Vec<CdnProbeResult> {
    let probe_clean = probe_path.trim_start_matches('/');
    let mut results = Vec::new();
    std::thread::scope(|s| {
        let mut handles = Vec::new();
        for base in cdn_bases {
            let base_s = base.trim_end_matches('/').to_string();
            let url = format!("{base_s}/{probe_clean}");
            handles.push(s.spawn(move || {
                let mut client = PooledHttpClient::new(
                    allow_loopback,
                    Duration::from_millis(timeout_ms.min(4_000)),
                );
                let t0 = Instant::now();
                let resp = client.get(&url, Some((0, 262_143)), None).ok()?;
                if !(200..300).contains(&resp.status) {
                    return None;
                }
                let elapsed = t0.elapsed().as_secs_f64().max(1e-6);
                let mb_s = (resp.body.len() as f64 / (1024.0 * 1024.0)) / elapsed;
                Some(CdnProbeResult {
                    base_url: base_s,
                    probe_ms: elapsed * 1000.0,
                    bytes_read: resp.body.len(),
                    throughput_mb_s: mb_s,
                })
            }));
        }
        for h in handles {
            if let Ok(Some(r)) = h.join() {
                results.push(r);
            }
        }
    });
    results.sort_by(|a, b| {
        b.throughput_mb_s
            .partial_cmp(&a.throughput_mb_s)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| {
                a.probe_ms
                    .partial_cmp(&b.probe_ms)
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
    });
    results
}

pub fn plan_delta(
    manifest: &StoreManifest,
    install_root: &Path,
    excluded_tags: &HashSet<String>,
) -> Result<DeltaPlan, String> {
    let chunk_map: HashMap<&str, &ChunkSpec> =
        manifest.chunks.iter().map(|c| (c.id.as_str(), c)).collect();
    let ledger = InstallLedger::load(install_root);

    let mut active_files = Vec::new();
    let mut skipped_optional_files = 0usize;
    let mut needed_chunks: HashSet<String> = HashSet::new();
    let mut reused_chunks: HashSet<String> = HashSet::new();

    for file in &manifest.files {
        if !excluded_tags.is_empty() && file.tags.iter().any(|t| excluded_tags.contains(t)) {
            skipped_optional_files += 1;
            continue;
        }
        let target = validate_manifest_rel_path(install_root, &file.rel_path)?;
        active_files.push(file.clone());

        let file_matches_on_disk = if let Ok(meta) = std::fs::metadata(&target)
            && meta.len() == file.total_size
        {
            if let Some(expected_sha) = &file.sha256 {
                file_sha256_matches(&target, expected_sha)
            } else {
                false
            }
        } else {
            false
        };

        if file_matches_on_disk {
            for sl in &file.slices {
                reused_chunks.insert(sl.chunk_id.clone());
            }
            continue;
        }

        let mut existing = OpenOptions::new().read(true).open(&target).ok();
        for sl in &file.slices {
            let chunk = chunk_map
                .get(sl.chunk_id.as_str())
                .ok_or_else(|| format!("Manifest references unknown chunk '{}'", sl.chunk_id))?;

            let in_ledger = ledger.as_ref().is_some_and(|l| {
                l.app_id == manifest.app_id
                    && l.build_version == manifest.build_version
                    && l.completed_chunks.contains(&sl.chunk_id)
            });

            let slice_valid_on_disk = if in_ledger
                && sl.chunk_offset == 0
                && sl.length == chunk.uncompressed_size
                && let Some(ref mut f) = existing
            {
                let mut buf = vec![0u8; sl.length as usize];
                f.seek(SeekFrom::Start(sl.file_offset)).is_ok()
                    && f.read_exact(&mut buf).is_ok()
                    && chunk.hash.verify(&buf)
            } else if sl.chunk_offset == 0
                && sl.length == chunk.uncompressed_size
                && let Some(ref mut f) = existing
            {
                let mut buf = vec![0u8; sl.length as usize];
                f.seek(SeekFrom::Start(sl.file_offset)).is_ok()
                    && f.read_exact(&mut buf).is_ok()
                    && chunk.hash.verify(&buf)
            } else {
                false
            };

            if slice_valid_on_disk {
                reused_chunks.insert(sl.chunk_id.clone());
            } else {
                needed_chunks.insert(sl.chunk_id.clone());
            }
        }
    }

    for id in &needed_chunks {
        reused_chunks.remove(id);
    }

    let mut chunks_to_download: Vec<String> = needed_chunks.into_iter().collect();
    chunks_to_download.sort();

    let bytes_to_download = chunks_to_download
        .iter()
        .filter_map(|id| chunk_map.get(id.as_str()))
        .map(|c| c.compressed_size)
        .sum();
    let bytes_reused = reused_chunks
        .iter()
        .filter_map(|id| chunk_map.get(id.as_str()))
        .map(|c| c.uncompressed_size)
        .sum();

    Ok(DeltaPlan {
        active_files,
        skipped_optional_files,
        chunks_to_download,
        chunks_reused: reused_chunks.len(),
        bytes_to_download,
        bytes_reused,
    })
}

pub fn install_or_update(
    manifest: &StoreManifest,
    install_root: &Path,
    opts: &InstallOptions,
    throttle: Option<&DownloadThrottle>,
) -> Result<InstallReport, String> {
    if manifest.cdn_bases.is_empty() {
        return Err("Manifest has no CDN bases".into());
    }
    std::fs::create_dir_all(install_root).map_err(|e| e.to_string())?;
    let plan = plan_delta(manifest, install_root, &opts.excluded_tags)?;
    let t0 = Instant::now();

    let mut validated_paths: HashMap<String, PathBuf> = HashMap::new();
    let mut open_files: HashMap<PathBuf, File> = HashMap::new();
    for file in &plan.active_files {
        let path = validate_manifest_rel_path(install_root, &file.rel_path)?;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let f = OpenOptions::new()
            .create(true)
            .read(true)
            .write(true)
            .truncate(false)
            .open(&path)
            .map_err(|e| format!("Pre-alloc open {} failed: {e}", file.rel_path))?;
        let cur_len = f.metadata().map(|m| m.len()).unwrap_or(0);
        if cur_len > file.total_size {
            f.set_len(file.total_size)
                .map_err(|e| format!("Truncate {} failed: {e}", file.rel_path))?;
        }
        open_files.insert(path.clone(), f);
        validated_paths.insert(file.rel_path.clone(), path);
    }

    let ordered_cdns: Vec<String> = if let Some(first_chunk) = manifest.chunks.first() {
        let raced = race_cdns(
            &manifest.cdn_bases,
            &first_chunk.url_path,
            opts.allow_loopback,
            opts.timeout_ms,
        );
        if raced.is_empty() {
            manifest
                .cdn_bases
                .iter()
                .map(|b| b.trim_end_matches('/').to_string())
                .collect()
        } else {
            let mut list: Vec<String> = raced.into_iter().map(|r| r.base_url).collect();
            for base in &manifest.cdn_bases {
                let norm = base.trim_end_matches('/').to_string();
                if !list.contains(&norm) {
                    list.push(norm);
                }
            }
            list
        }
    } else {
        manifest
            .cdn_bases
            .iter()
            .map(|b| b.trim_end_matches('/').to_string())
            .collect()
    };
    let selected_cdn = ordered_cdns.first().cloned().unwrap_or_default();

    if plan.chunks_to_download.is_empty() {
        InstallLedger::clear(install_root)?;
        return Ok(InstallReport {
            downloaded_chunks: 0,
            reused_chunks: plan.chunks_reused,
            bytes_downloaded: 0,
            bytes_written: 0,
            selected_cdn,
            cdn_failovers: 0,
            elapsed_ms: t0.elapsed().as_secs_f64() * 1000.0,
            throughput_mb_s: 0.0,
            paused_or_interrupted: false,
        });
    }

    let chunk_map: HashMap<String, ChunkSpec> = manifest
        .chunks
        .iter()
        .map(|c| (c.id.clone(), c.clone()))
        .collect();
    let mut chunk_targets: HashMap<String, Vec<(PathBuf, u64, u64, u64)>> = HashMap::new();
    for file in &plan.active_files {
        let path = validated_paths[&file.rel_path].clone();
        for sl in &file.slices {
            chunk_targets.entry(sl.chunk_id.clone()).or_default().push((
                path.clone(),
                sl.chunk_offset,
                sl.file_offset,
                sl.length,
            ));
        }
    }

    let existing_ledger = InstallLedger::load(install_root)
        .filter(|l| l.app_id == manifest.app_id && l.build_version == manifest.build_version)
        .unwrap_or_else(|| InstallLedger {
            app_id: manifest.app_id.clone(),
            build_version: manifest.build_version.clone(),
            completed_chunks: HashSet::new(),
        });
    let ledger = Arc::new(Mutex::new(existing_ledger));

    let next_idx = AtomicUsize::new(0);
    let downloaded_chunks = AtomicUsize::new(0);
    let bytes_downloaded = AtomicU64::new(0);
    let bytes_written = AtomicU64::new(0);
    let cdn_failovers = AtomicUsize::new(0);
    let interrupted = AtomicBool::new(false);
    let first_err: Mutex<Option<String>> = Mutex::new(None);
    let file_write_lock: Mutex<()> = Mutex::new(());

    let worker_count = opts.workers.clamp(1, 16).min(plan.chunks_to_download.len());
    let stop_limit = throttle
        .map(|t| t.stop_after_chunks.load(Ordering::SeqCst))
        .unwrap_or(0);

    std::thread::scope(|s| {
        for _ in 0..worker_count {
            let chunk_ids = &plan.chunks_to_download;
            let chunk_map = &chunk_map;
            let chunk_targets = &chunk_targets;
            let ordered_cdns = &ordered_cdns;
            let ledger = Arc::clone(&ledger);
            let next_idx = &next_idx;
            let downloaded_chunks = &downloaded_chunks;
            let bytes_downloaded = &bytes_downloaded;
            let bytes_written = &bytes_written;
            let cdn_failovers = &cdn_failovers;
            let interrupted = &interrupted;
            let first_err = &first_err;
            let file_write_lock = &file_write_lock;
            let open_files = &open_files;
            let bearer = opts.bearer_token.as_deref();

            s.spawn(move || {
                let mut client = PooledHttpClient::new(
                    opts.allow_loopback,
                    Duration::from_millis(opts.timeout_ms),
                );
                let mut cdn_idx = 0usize;

                loop {
                    if first_err.lock().unwrap().is_some() {
                        break;
                    }
                    if let Some(th) = throttle
                        && (th.cancel_requested.load(Ordering::SeqCst)
                            || th.paused_for_game.load(Ordering::SeqCst))
                    {
                        interrupted.store(true, Ordering::SeqCst);
                        break;
                    }
                    let idx = next_idx.fetch_add(1, Ordering::SeqCst);
                    if idx >= chunk_ids.len() {
                        break;
                    }
                    if stop_limit > 0 && idx >= stop_limit {
                        interrupted.store(true, Ordering::SeqCst);
                        break;
                    }
                    let chunk_id = &chunk_ids[idx];
                    let Some(spec) = chunk_map.get(chunk_id) else {
                        continue;
                    };

                    let mut fetched: Option<Vec<u8>> = None;
                    let mut last_err = String::new();
                    for retry in 0..ordered_cdns.len() {
                        let base = &ordered_cdns[(cdn_idx + retry) % ordered_cdns.len()];
                        let url = format!("{}/{}", base, spec.url_path.trim_start_matches('/'));
                        match client.get(&url, None, bearer) {
                            Ok(resp) if (200..300).contains(&resp.status) => {
                                if retry > 0 {
                                    cdn_idx = (cdn_idx + retry) % ordered_cdns.len();
                                    cdn_failovers.fetch_add(1, Ordering::SeqCst);
                                }
                                fetched = Some(resp.body);
                                break;
                            }
                            Ok(resp) => {
                                last_err = format!("CDN {base} returned HTTP {}", resp.status);
                            }
                            Err(e) => {
                                last_err = e;
                            }
                        }
                    }

                    let Some(wire_bytes) = fetched else {
                        *first_err.lock().unwrap() = Some(format!(
                            "Failed to download chunk '{}': {last_err}",
                            spec.id
                        ));
                        break;
                    };
                    let wire_len = wire_bytes.len() as u64;
                    let raw_owned;
                    let raw: &[u8] = match spec.compression {
                        ChunkCompression::Raw => &wire_bytes,
                        ChunkCompression::Zlib => {
                            match decompress_chunk(&wire_bytes, spec.compression) {
                                Ok(d) => {
                                    raw_owned = d;
                                    &raw_owned
                                }
                                Err(e) => {
                                    *first_err.lock().unwrap() =
                                        Some(format!("Decompress '{}' failed: {e}", spec.id));
                                    break;
                                }
                            }
                        }
                    };
                    if !spec.hash.verify(raw) {
                        *first_err.lock().unwrap() =
                            Some(format!("Integrity hash mismatch on chunk '{}'", spec.id));
                        break;
                    }

                    if let Some(targets) = chunk_targets.get(&spec.id) {
                        for (path, chunk_off, file_off, len) in targets {
                            let start = *chunk_off as usize;
                            let end = start.saturating_add(*len as usize);
                            if end > raw.len() {
                                *first_err.lock().unwrap() = Some(format!(
                                    "Chunk '{}' slice out of bounds ({end} > {})",
                                    spec.id,
                                    raw.len()
                                ));
                                return;
                            }
                            let res = write_slice_at_offset(
                                open_files.get(path),
                                path,
                                *file_off,
                                &raw[start..end],
                                file_write_lock,
                            );
                            if let Err(e) = res {
                                *first_err.lock().unwrap() =
                                    Some(format!("Write {} failed: {e}", path.display()));
                                return;
                            }
                            bytes_written.fetch_add(*len, Ordering::Relaxed);
                        }
                    }

                    bytes_downloaded.fetch_add(wire_len, Ordering::Relaxed);
                    downloaded_chunks.fetch_add(1, Ordering::Relaxed);
                    if let Ok(mut lg) = ledger.lock() {
                        lg.completed_chunks.insert(spec.id.clone());
                    }

                    if let Some(th) = throttle {
                        let cap = th.max_bytes_per_sec.load(Ordering::Relaxed);
                        if cap > 0 {
                            let sleep_ms =
                                ((wire_len as f64 / cap as f64) * 1000.0).clamp(1.0, 250.0) as u64;
                            std::thread::sleep(Duration::from_millis(sleep_ms));
                        }
                    }
                }
            });
        }
    });

    if let Some(err) = first_err.into_inner().unwrap() {
        let _ = ledger.lock().unwrap().save(install_root);
        return Err(err);
    }
    drop(open_files);

    let was_interrupted = interrupted.load(Ordering::SeqCst);
    if was_interrupted {
        ledger.lock().unwrap().save(install_root)?;
    } else {
        InstallLedger::clear(install_root)?;
    }

    let elapsed_s = t0.elapsed().as_secs_f64().max(1e-6);
    let written = bytes_written.load(Ordering::Relaxed);
    Ok(InstallReport {
        downloaded_chunks: downloaded_chunks.load(Ordering::Relaxed),
        reused_chunks: plan.chunks_reused,
        bytes_downloaded: bytes_downloaded.load(Ordering::Relaxed),
        bytes_written: written,
        selected_cdn,
        cdn_failovers: cdn_failovers.load(Ordering::Relaxed),
        elapsed_ms: elapsed_s * 1000.0,
        throughput_mb_s: (written as f64 / (1024.0 * 1024.0)) / elapsed_s,
        paused_or_interrupted: was_interrupted,
    })
}

pub fn verify_and_repair(
    manifest: &StoreManifest,
    install_root: &Path,
    opts: &InstallOptions,
) -> Result<VerifyReport, String> {
    let mut corrupted = Vec::new();
    let mut checked = 0usize;
    for file in &manifest.files {
        if !opts.excluded_tags.is_empty()
            && file.tags.iter().any(|t| opts.excluded_tags.contains(t))
        {
            continue;
        }
        let path = validate_manifest_rel_path(install_root, &file.rel_path)?;
        checked += 1;
        let ok = match &file.sha256 {
            Some(expected) => file_sha256_matches(&path, expected),
            None => std::fs::metadata(&path).is_ok_and(|m| m.len() == file.total_size),
        };
        if !ok {
            corrupted.push(file.rel_path.clone());
        }
    }

    let repaired_chunks = if corrupted.is_empty() {
        0
    } else {
        let rep = install_or_update(manifest, install_root, opts, None)?;
        rep.downloaded_chunks
    };

    let mut all_valid = true;
    for file in &manifest.files {
        if !opts.excluded_tags.is_empty()
            && file.tags.iter().any(|t| opts.excluded_tags.contains(t))
        {
            continue;
        }
        let path = validate_manifest_rel_path(install_root, &file.rel_path)?;
        if let Some(expected) = &file.sha256
            && !file_sha256_matches(&path, expected)
        {
            all_valid = false;
        }
    }

    Ok(VerifyReport {
        files_checked: checked,
        corrupted_files: corrupted,
        repaired_chunks,
        all_valid_after_repair: all_valid,
    })
}

pub fn uninstall_manifest(manifest: &StoreManifest, install_root: &Path) -> Result<usize, String> {
    let mut removed = 0usize;
    for file in &manifest.files {
        let path = validate_manifest_rel_path(install_root, &file.rel_path)?;
        match std::fs::remove_file(&path) {
            Ok(()) => removed += 1,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(format!("Uninstall remove {} failed: {e}", file.rel_path)),
        }
    }
    InstallLedger::clear(install_root)?;
    Ok(removed)
}

pub fn decompress_chunk(data: &[u8], compression: ChunkCompression) -> Result<Vec<u8>, String> {
    match compression {
        ChunkCompression::Raw => Ok(data.to_vec()),
        ChunkCompression::Zlib => {
            let mut dec = ZlibDecoder::new(data);
            let mut out = Vec::new();
            dec.read_to_end(&mut out)
                .map_err(|e| format!("zlib decode error: {e}"))?;
            Ok(out)
        }
    }
}

fn write_slice_at_offset(
    shared_file: Option<&File>,
    path: &Path,
    offset: u64,
    data: &[u8],
    fallback_lock: &Mutex<()>,
) -> std::io::Result<()> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::FileExt;
        let _ = (path, fallback_lock);
        if let Some(f) = shared_file {
            let mut written = 0usize;
            while written < data.len() {
                let n = f.seek_write(&data[written..], offset + written as u64)?;
                if n == 0 {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::WriteZero,
                        "short seek_write",
                    ));
                }
                written += n;
            }
            return Ok(());
        }
    }
    let _ = shared_file;
    let _g = fallback_lock.lock().unwrap();
    let mut f = OpenOptions::new().write(true).open(path)?;
    f.seek(SeekFrom::Start(offset))?;
    f.write_all(data)
}

pub fn file_sha256_matches(path: &Path, expected_hex: &str) -> bool {
    let Ok(mut f) = File::open(path) else {
        return false;
    };
    let mut buf = Vec::new();
    if f.read_to_end(&mut buf).is_err() {
        return false;
    }
    let actual = to_hex(&sha256(&buf));
    constant_time_eq(
        actual.as_bytes(),
        expected_hex.to_ascii_lowercase().as_bytes(),
    )
}
