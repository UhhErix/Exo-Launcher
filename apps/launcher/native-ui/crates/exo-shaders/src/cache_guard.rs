use crate::pso_format::{EXOPLIB_MAGIC, PipelineLibraryHeader};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CacheProvenance {
    pub adapter_vendor: u32,
    pub adapter_device: u32,
    pub driver_version: String,
    #[serde(default)]
    pub game_build: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum InvalidationReason {
    AdapterChanged,
    DriverChanged,
    GameBuildChanged,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "status")]
pub enum PruneResult {
    KeptValid,
    Missing,
    BackedUpAndCleared {
        reason: InvalidationReason,
        backup_path: String,
        bytes_backed_up: u64,
    },
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OldDriverPruneSummary {
    pub inspected: usize,
    pub kept_valid: usize,
    pub cleared: usize,
    pub backed_up_bytes: u64,
    pub backup_paths: Vec<String>,
}

pub fn evaluate_cache_invalidation(
    cached: &CacheProvenance,
    current: &CacheProvenance,
) -> Option<InvalidationReason> {
    if cached.adapter_vendor != 0
        && current.adapter_vendor != 0
        && cached.adapter_vendor != current.adapter_vendor
    {
        return Some(InvalidationReason::AdapterChanged);
    }
    if cached.adapter_device != 0
        && current.adapter_device != 0
        && cached.adapter_device != current.adapter_device
    {
        return Some(InvalidationReason::AdapterChanged);
    }
    if !cached.driver_version.is_empty()
        && !current.driver_version.is_empty()
        && cached.driver_version != "unknown"
        && current.driver_version != "unknown"
        && cached.driver_version != current.driver_version
    {
        return Some(InvalidationReason::DriverChanged);
    }
    if let (Some(old_build), Some(new_build)) = (&cached.game_build, &current.game_build)
        && !old_build.is_empty()
        && !new_build.is_empty()
        && old_build != new_build
    {
        return Some(InvalidationReason::GameBuildChanged);
    }
    None
}

fn copy_path_recursive(src: &Path, dst: &Path) -> Result<u64, String> {
    let meta =
        fs::symlink_metadata(src).map_err(|e| format!("stat {} failed: {e}", src.display()))?;
    if meta.file_type().is_symlink() {
        return Err(format!("refusing to follow symlink {}", src.display()));
    }
    if meta.is_file() {
        if let Some(parent) = dst.parent() {
            fs::create_dir_all(parent).map_err(|e| format!("create_dir_all failed: {e}"))?;
        }
        let copied = fs::copy(src, dst).map_err(|e| {
            format!(
                "backup copy {} -> {} failed: {e}",
                src.display(),
                dst.display()
            )
        })?;
        if copied != meta.len() {
            return Err(format!(
                "backup byte count mismatch for {}: expected {}, copied {}",
                src.display(),
                meta.len(),
                copied
            ));
        }
        return Ok(copied);
    }
    if meta.is_dir() {
        fs::create_dir_all(dst)
            .map_err(|e| format!("create_dir_all {} failed: {e}", dst.display()))?;
        let mut total = 0u64;
        for entry in
            fs::read_dir(src).map_err(|e| format!("read_dir {} failed: {e}", src.display()))?
        {
            let entry = entry.map_err(|e| format!("read_dir entry failed: {e}"))?;
            let child_src = entry.path();
            let child_dst = dst.join(entry.file_name());
            total += copy_path_recursive(&child_src, &child_dst)?;
        }
        return Ok(total);
    }
    Err(format!("unsupported cache path type {}", src.display()))
}

fn backup_target_path(backup_root: &Path, cache_path: &Path) -> PathBuf {
    let name = cache_path
        .file_name()
        .map(|n| n.to_string_lossy().into_owned())
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| "cache".to_string());
    let stamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    backup_root.join(format!("{stamp}_{name}"))
}

pub fn backup_and_clear_stale_cache(
    cache_path: &Path,
    backup_root: &Path,
    cached: &CacheProvenance,
    current: &CacheProvenance,
) -> Result<PruneResult, String> {
    if !cache_path.exists() {
        return Ok(PruneResult::Missing);
    }
    let Some(reason) = evaluate_cache_invalidation(cached, current) else {
        return Ok(PruneResult::KeptValid);
    };
    let backup_dst = backup_target_path(backup_root, cache_path);
    let bytes_backed_up = copy_path_recursive(cache_path, &backup_dst)?;
    let meta = fs::symlink_metadata(cache_path)
        .map_err(|e| format!("stat {} failed: {e}", cache_path.display()))?;
    if meta.is_dir() {
        fs::remove_dir_all(cache_path)
            .map_err(|e| format!("remove_dir_all {} failed: {e}", cache_path.display()))?;
    } else {
        fs::remove_file(cache_path)
            .map_err(|e| format!("remove_file {} failed: {e}", cache_path.display()))?;
    }
    Ok(PruneResult::BackedUpAndCleared {
        reason,
        backup_path: backup_dst.display().to_string(),
        bytes_backed_up,
    })
}

fn read_exoplib_header(path: &Path) -> Option<PipelineLibraryHeader> {
    let bytes = fs::read(path).ok()?;
    if bytes.len() < 12 || &bytes[..8] != EXOPLIB_MAGIC {
        return None;
    }
    let header_len = u32::from_le_bytes([bytes[8], bytes[9], bytes[10], bytes[11]]) as usize;
    if 12 + header_len > bytes.len() {
        return None;
    }
    serde_json::from_slice::<PipelineLibraryHeader>(&bytes[12..12 + header_len]).ok()
}

pub fn prune_old_driver_libraries(
    data_dir: &Path,
    current: &CacheProvenance,
) -> Result<OldDriverPruneSummary, String> {
    let lib_dir = data_dir.join("libraries");
    if !lib_dir.is_dir() {
        return Ok(OldDriverPruneSummary::default());
    }
    let backup_root = data_dir.join("backups").join("old-driver");
    let mut summary = OldDriverPruneSummary::default();
    let entries = fs::read_dir(&lib_dir).map_err(|e| format!("read_dir libraries failed: {e}"))?;
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|e| e.to_str()) != Some("exoplib") {
            continue;
        }
        summary.inspected += 1;
        let Some(header) = read_exoplib_header(&path) else {
            summary.kept_valid += 1;
            continue;
        };
        let cached = CacheProvenance {
            adapter_vendor: header.adapter_vendor,
            adapter_device: header.adapter_device,
            driver_version: header.driver_version,
            game_build: None,
        };
        match backup_and_clear_stale_cache(&path, &backup_root, &cached, current)? {
            PruneResult::KeptValid | PruneResult::Missing => {
                summary.kept_valid += 1;
            }
            PruneResult::BackedUpAndCleared {
                backup_path,
                bytes_backed_up,
                ..
            } => {
                summary.cleared += 1;
                summary.backed_up_bytes += bytes_backed_up;
                summary.backup_paths.push(backup_path);
            }
        }
    }
    Ok(summary)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::pso_format::write_pipeline_library;

    fn temp_root(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!(
            "exo-cache-guard-{tag}-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let _ = fs::create_dir_all(&p);
        p
    }

    #[test]
    fn valid_or_unknown_cache_is_never_deleted() {
        let root = temp_root("valid");
        let cache = root.join("game_cache.psolib");
        let backups = root.join("backups");
        fs::write(&cache, b"valid-pso-data").unwrap();

        let current = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "595.79".into(),
            game_build: Some("12345".into()),
        };
        let unknown = CacheProvenance {
            adapter_vendor: 0,
            adapter_device: 0,
            driver_version: String::new(),
            game_build: None,
        };

        assert_eq!(
            backup_and_clear_stale_cache(&cache, &backups, &current, &current).unwrap(),
            PruneResult::KeptValid
        );
        assert!(cache.exists());

        assert_eq!(
            backup_and_clear_stale_cache(&cache, &backups, &unknown, &current).unwrap(),
            PruneResult::KeptValid
        );
        assert!(cache.exists());
        assert!(!backups.exists());

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn wrong_driver_or_build_backs_up_before_clearing() {
        let root = temp_root("stale");
        let cache_dir = root.join("shadercache");
        fs::create_dir_all(cache_dir.join("nested")).unwrap();
        fs::write(
            cache_dir.join("nested").join("pso.bin"),
            b"stale-payload-bytes",
        )
        .unwrap();
        let backups = root.join("backups");

        let cached = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "590.10".into(),
            game_build: Some("100".into()),
        };
        let current = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "595.79".into(),
            game_build: Some("100".into()),
        };

        let res = backup_and_clear_stale_cache(&cache_dir, &backups, &cached, &current).unwrap();
        match res {
            PruneResult::BackedUpAndCleared {
                reason,
                backup_path,
                bytes_backed_up,
            } => {
                assert_eq!(reason, InvalidationReason::DriverChanged);
                assert_eq!(bytes_backed_up, 19);
                assert!(!cache_dir.exists());
                let backed_file = PathBuf::from(backup_path).join("nested").join("pso.bin");
                assert_eq!(fs::read(backed_file).unwrap(), b"stale-payload-bytes");
            }
            other => panic!("expected BackedUpAndCleared, got {other:?}"),
        }

        let build_cache = root.join("build.bin");
        fs::write(&build_cache, b"old-build-cache").unwrap();
        let old_build = CacheProvenance {
            game_build: Some("100".into()),
            ..current.clone()
        };
        let new_build = CacheProvenance {
            game_build: Some("200".into()),
            ..current.clone()
        };
        let res2 =
            backup_and_clear_stale_cache(&build_cache, &backups, &old_build, &new_build).unwrap();
        assert!(matches!(
            res2,
            PruneResult::BackedUpAndCleared {
                reason: InvalidationReason::GameBuildChanged,
                ..
            }
        ));
        assert!(!build_cache.exists());

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn prune_old_driver_libraries_keeps_current_and_backs_up_stale() {
        let root = temp_root("old-libs");
        let libs = root.join("libraries");
        let valid_lib = libs.join("steam_1.exoplib");
        let stale_lib = libs.join("steam_2.exoplib");

        write_pipeline_library(&valid_lib, 0x10DE, 0x2484, "595.79", 4, b"valid-blob").unwrap();
        write_pipeline_library(&stale_lib, 0x10DE, 0x2484, "580.00", 4, b"stale-blob").unwrap();

        let current = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "595.79".into(),
            game_build: None,
        };

        let summary = prune_old_driver_libraries(&root, &current).unwrap();
        assert_eq!(summary.inspected, 2);
        assert_eq!(summary.kept_valid, 1);
        assert_eq!(summary.cleared, 1);
        assert_eq!(summary.backup_paths.len(), 1);
        assert!(valid_lib.exists());
        assert!(!stale_lib.exists());
        assert!(Path::new(&summary.backup_paths[0]).exists());

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn backup_failure_preserves_original_cache() {
        let root = temp_root("backup-fail");
        let cache = root.join("game.exoplib");
        fs::write(&cache, b"important-cache-bytes").unwrap();
        let blocking_file = root.join("not-a-dir");
        fs::write(&blocking_file, b"blocker").unwrap();

        let cached = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "590.10".into(),
            game_build: None,
        };
        let current = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "595.79".into(),
            game_build: None,
        };

        let err = backup_and_clear_stale_cache(&cache, &blocking_file, &cached, &current);
        assert!(err.is_err());
        assert!(cache.exists());
        assert_eq!(fs::read(&cache).unwrap(), b"important-cache-bytes");

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn prune_keeps_corrupt_or_unknown_exoplib_header() {
        let root = temp_root("corrupt-lib");
        let libs = root.join("libraries");
        fs::create_dir_all(&libs).unwrap();
        let unknown = libs.join("steam_unknown.exoplib");
        fs::write(&unknown, b"not-a-valid-header").unwrap();

        let current = CacheProvenance {
            adapter_vendor: 0x10DE,
            adapter_device: 0x2484,
            driver_version: "595.79".into(),
            game_build: None,
        };

        let summary = prune_old_driver_libraries(&root, &current).unwrap();
        assert_eq!(summary.inspected, 1);
        assert_eq!(summary.kept_valid, 1);
        assert_eq!(summary.cleared, 0);
        assert!(unknown.exists());

        let _ = fs::remove_dir_all(&root);
    }
}
