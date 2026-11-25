use crate::pso_format::{EXOPLIB_MAGIC, PipelineLibraryHeader};
use serde::{Deserialize, Serialize};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Instant, SystemTime, UNIX_EPOCH};
use windows::Win32::Graphics::Dxgi::{
    CreateDXGIFactory1, DXGI_ADAPTER_FLAG_SOFTWARE, IDXGIDevice, IDXGIFactory1,
};
use windows::core::Interface;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GpuAdapterInfo {
    pub name: String,
    pub vendor: String,
    pub vendor_name: String,
    pub vendor_id: u32,
    pub device_id: u32,
    pub dedicated_vram_bytes: u64,
    pub driver_version: String,
    pub driver_raw: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CacheDirReport {
    pub id: String,
    pub vendor: String,
    pub path: String,
    pub exists: bool,
    pub file_count: usize,
    pub total_bytes: u64,
    pub zero_byte_files: usize,
    pub newest_unix_secs: Option<u64>,
    pub oldest_unix_secs: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverCacheInventory {
    pub gpu: GpuAdapterInfo,
    pub dirs: Vec<CacheDirReport>,
    pub total_files: usize,
    pub total_bytes: u64,
    pub scan_us: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriverStamp {
    pub adapter_name: String,
    pub driver_version: String,
    pub driver_raw: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum DriverChange {
    FirstSeen {
        current: DriverStamp,
    },
    Unchanged {
        current: DriverStamp,
    },
    Updated {
        previous: DriverStamp,
        current: DriverStamp,
    },
}

pub fn query_primary_gpu() -> GpuAdapterInfo {
    unsafe {
        let factory: IDXGIFactory1 = match CreateDXGIFactory1() {
            Ok(f) => f,
            Err(_) => return fallback_gpu(),
        };
        let mut best: Option<GpuAdapterInfo> = None;
        let mut idx = 0u32;
        while let Ok(adapter) = factory.EnumAdapters1(idx) {
            idx += 1;
            let Ok(desc) = adapter.GetDesc1() else {
                continue;
            };
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE.0 as u32) != 0 {
                continue;
            }
            let raw_name: Vec<u16> = desc
                .Description
                .iter()
                .copied()
                .take_while(|&c| c != 0)
                .collect();
            let name = String::from_utf16_lossy(&raw_name).trim().to_string();
            let (vendor, vendor_name) = match desc.VendorId {
                0x10DE => ("nvidia", "NVIDIA"),
                0x1002 | 0x1022 => ("amd", "AMD"),
                0x8086 => ("intel", "Intel"),
                0x5143 | 0x4D4F => ("qualcomm", "Qualcomm"),
                _ => ("other", "GPU"),
            };
            let driver_raw = adapter
                .CheckInterfaceSupport(&IDXGIDevice::IID)
                .map(|v| v as u64)
                .unwrap_or(0);
            let driver_version = format_driver_version(vendor, driver_raw);
            let candidate = GpuAdapterInfo {
                name,
                vendor: vendor.to_string(),
                vendor_name: vendor_name.to_string(),
                vendor_id: desc.VendorId,
                device_id: desc.DeviceId,
                dedicated_vram_bytes: desc.DedicatedVideoMemory as u64,
                driver_version,
                driver_raw,
            };
            let replace = match &best {
                None => true,
                Some(prev) => candidate.dedicated_vram_bytes > prev.dedicated_vram_bytes,
            };
            if replace {
                best = Some(candidate);
            }
        }
        best.unwrap_or_else(fallback_gpu)
    }
}

fn fallback_gpu() -> GpuAdapterInfo {
    GpuAdapterInfo {
        name: "Unknown GPU".into(),
        vendor: "other".into(),
        vendor_name: "GPU".into(),
        vendor_id: 0,
        device_id: 0,
        dedicated_vram_bytes: 0,
        driver_version: "0.0.0.0".into(),
        driver_raw: 0,
    }
}

pub fn format_driver_version(vendor: &str, raw: u64) -> String {
    if raw == 0 {
        return "unknown".into();
    }
    let a = (raw >> 48) & 0xFFFF;
    let b = (raw >> 32) & 0xFFFF;
    let c = (raw >> 16) & 0xFFFF;
    let d = raw & 0xFFFF;
    if vendor == "nvidia" {
        let last5 = (c % 10) * 10000 + d;
        format!("{}.{:02}", last5 / 100, last5 % 100)
    } else {
        format!("{a}.{b}.{c}.{d}")
    }
}

pub fn default_driver_cache_roots() -> Vec<(String, String, PathBuf)> {
    let mut roots = Vec::new();
    if let Ok(local) = env::var("LOCALAPPDATA") {
        let base = PathBuf::from(local);
        roots.push((
            "nvidia-dxcache".into(),
            "nvidia".into(),
            base.join("NVIDIA").join("DXCache"),
        ));
        roots.push((
            "nvidia-glcache".into(),
            "nvidia".into(),
            base.join("NVIDIA").join("GLCache"),
        ));
        roots.push((
            "amd-dxcache".into(),
            "amd".into(),
            base.join("AMD").join("DxCache"),
        ));
        roots.push((
            "amd-dxccache".into(),
            "amd".into(),
            base.join("AMD").join("DxcCache"),
        ));
        roots.push((
            "intel-cache".into(),
            "intel".into(),
            base.join("Intel").join("ShaderCache"),
        ));
        roots.push((
            "d3dscache".into(),
            "microsoft".into(),
            base.join("D3DSCache"),
        ));
    }
    if let Ok(program_data) = env::var("PROGRAMDATA") {
        let base = PathBuf::from(program_data);
        roots.push((
            "nvidia-nvcache".into(),
            "nvidia".into(),
            base.join("NVIDIA Corporation").join("NV_Cache"),
        ));
    }
    roots
}

pub fn default_per_driver_roots() -> Vec<PathBuf> {
    let mut out = Vec::new();
    if let Ok(local) = env::var("LOCALAPPDATA")
        && let Some(app_data) = PathBuf::from(local).parent()
    {
        let nv = app_data
            .join("LocalLow")
            .join("NVIDIA")
            .join("PerDriverVersion");
        if nv.is_dir() {
            out.push(nv);
        }
    }
    out
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StaleDriverCacheEntry {
    pub kind: String,
    pub path: String,
    pub cached_driver: String,
    pub current_driver: String,
    pub bytes: u64,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OldDriverCleanupReport {
    pub inspected: usize,
    pub kept_valid: usize,
    pub cleared: usize,
    pub backed_up_bytes: u64,
    pub backup_paths: Vec<String>,
}

fn looks_like_driver_version_dir(name: &str) -> bool {
    if name.len() < 4 || name.len() > 32 {
        return false;
    }
    let parts: Vec<&str> = name.split('.').collect();
    if parts.len() < 2 || parts.len() > 4 {
        return false;
    }
    parts
        .iter()
        .all(|p| !p.is_empty() && p.chars().all(|c| c.is_ascii_digit()))
}

fn normalize_driver_label(ver: &str) -> String {
    ver.trim().to_ascii_lowercase()
}

fn driver_versions_differ(cached: &str, current: &str) -> bool {
    let a = normalize_driver_label(cached);
    let b = normalize_driver_label(current);
    if a.is_empty()
        || b.is_empty()
        || a == "unknown"
        || b == "unknown"
        || a == "0.0.0.0"
        || b == "0.0.0.0"
    {
        return false;
    }
    if a == b {
        return false;
    }
    let a_digits: String = a.chars().filter(|c| c.is_ascii_digit()).collect();
    let b_digits: String = b.chars().filter(|c| c.is_ascii_digit()).collect();
    !(!a_digits.is_empty()
        && !b_digits.is_empty()
        && (a_digits.ends_with(&b_digits) || b_digits.ends_with(&a_digits)))
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

fn dir_byte_size(root: &Path) -> u64 {
    let Ok(meta) = fs::symlink_metadata(root) else {
        return 0;
    };
    if meta.file_type().is_symlink() {
        return 0;
    }
    if meta.is_file() {
        return meta.len();
    }
    if !meta.is_dir() {
        return 0;
    }
    let mut total = 0u64;
    let Ok(entries) = fs::read_dir(root) else {
        return 0;
    };
    for entry in entries.flatten() {
        total = total.saturating_add(dir_byte_size(&entry.path()));
    }
    total
}

fn copy_path_verified(src: &Path, dst: &Path) -> Result<u64, String> {
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
                "backup byte mismatch for {}: expected {}, got {}",
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
            total += copy_path_verified(&entry.path(), &dst.join(entry.file_name()))?;
        }
        return Ok(total);
    }
    Err(format!("unsupported path type {}", src.display()))
}

pub fn find_stale_old_driver_caches(
    data_dir: &Path,
    per_driver_roots: &[PathBuf],
    current_vendor: u32,
    current_device: u32,
    current_driver: &str,
) -> Vec<StaleDriverCacheEntry> {
    let mut stale = Vec::new();
    let lib_dir = data_dir.join("libraries");
    if let Ok(entries) = fs::read_dir(&lib_dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().and_then(|e| e.to_str()) != Some("exoplib") {
                continue;
            }
            let Some(hdr) = read_exoplib_header(&path) else {
                continue;
            };
            let adapter_diff = (hdr.adapter_vendor != 0
                && current_vendor != 0
                && hdr.adapter_vendor != current_vendor)
                || (hdr.adapter_device != 0
                    && current_device != 0
                    && hdr.adapter_device != current_device);
            let driver_diff = driver_versions_differ(&hdr.driver_version, current_driver);
            if adapter_diff || driver_diff {
                stale.push(StaleDriverCacheEntry {
                    kind: "exo-exoplib".into(),
                    path: path.display().to_string(),
                    cached_driver: hdr.driver_version,
                    current_driver: current_driver.to_string(),
                    bytes: dir_byte_size(&path),
                });
            }
        }
    }

    for root in per_driver_roots {
        let Ok(entries) = fs::read_dir(root) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            let Ok(meta) = fs::symlink_metadata(&path) else {
                continue;
            };
            if meta.file_type().is_symlink() || !meta.is_dir() {
                continue;
            }
            let name = entry.file_name().to_string_lossy().into_owned();
            if !looks_like_driver_version_dir(&name) {
                continue;
            }
            if driver_versions_differ(&name, current_driver) {
                stale.push(StaleDriverCacheEntry {
                    kind: "per-driver-dir".into(),
                    path: path.display().to_string(),
                    cached_driver: name,
                    current_driver: current_driver.to_string(),
                    bytes: dir_byte_size(&path),
                });
            }
        }
    }

    stale.sort_by(|a, b| a.path.cmp(&b.path));
    stale
}

pub fn backup_and_clear_old_driver_caches(
    data_dir: &Path,
    per_driver_roots: &[PathBuf],
    current_vendor: u32,
    current_device: u32,
    current_driver: &str,
) -> Result<OldDriverCleanupReport, String> {
    let mut report = OldDriverCleanupReport::default();
    let lib_dir = data_dir.join("libraries");
    if let Ok(entries) = fs::read_dir(&lib_dir) {
        report.inspected += entries
            .flatten()
            .filter(|e| e.path().extension().and_then(|x| x.to_str()) == Some("exoplib"))
            .count();
    }
    for root in per_driver_roots {
        if let Ok(entries) = fs::read_dir(root) {
            report.inspected += entries.flatten().count();
        }
    }

    let stale = find_stale_old_driver_caches(
        data_dir,
        per_driver_roots,
        current_vendor,
        current_device,
        current_driver,
    );
    let backup_root = data_dir.join("backups").join("old-driver");
    for item in &stale {
        let src = Path::new(&item.path);
        if !src.exists() {
            continue;
        }
        let name = src
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_else(|| "cache".into());
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let backup_dst = backup_root.join(format!("{stamp}_{name}"));
        let copied = copy_path_verified(src, &backup_dst)?;
        let meta =
            fs::symlink_metadata(src).map_err(|e| format!("stat {} failed: {e}", src.display()))?;
        if meta.is_dir() {
            fs::remove_dir_all(src)
                .map_err(|e| format!("remove_dir_all {} failed: {e}", src.display()))?;
        } else {
            fs::remove_file(src)
                .map_err(|e| format!("remove_file {} failed: {e}", src.display()))?;
        }
        report.cleared += 1;
        report.backed_up_bytes = report.backed_up_bytes.saturating_add(copied);
        report.backup_paths.push(backup_dst.display().to_string());
    }
    report.kept_valid = report.inspected.saturating_sub(report.cleared);
    Ok(report)
}

pub fn restore_backed_up_cache(backup_path: &Path, restore_target: &Path) -> Result<u64, String> {
    if !backup_path.exists() {
        return Err(format!("backup not found: {}", backup_path.display()));
    }
    if restore_target.exists() {
        return Err(format!(
            "refusing to overwrite existing target: {}",
            restore_target.display()
        ));
    }
    copy_path_verified(backup_path, restore_target)
}

pub fn scan_driver_caches(roots: &[(String, String, PathBuf)]) -> DriverCacheInventory {
    let start = Instant::now();
    let gpu = query_primary_gpu();
    let reports = scan_roots_parallel(roots);
    let total_files = reports.iter().map(|r| r.file_count).sum();
    let total_bytes = reports.iter().map(|r| r.total_bytes).sum();
    DriverCacheInventory {
        gpu,
        dirs: reports,
        total_files,
        total_bytes,
        scan_us: start.elapsed().as_micros() as u64,
    }
}

pub fn scan_roots_parallel(roots: &[(String, String, PathBuf)]) -> Vec<CacheDirReport> {
    if roots.is_empty() {
        return Vec::new();
    }
    let next = AtomicUsize::new(0);
    let workers = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(4)
        .min(roots.len());
    let slots: Vec<std::sync::Mutex<Option<CacheDirReport>>> = (0..roots.len())
        .map(|_| std::sync::Mutex::new(None))
        .collect();
    std::thread::scope(|s| {
        for _ in 0..workers {
            s.spawn(|| {
                loop {
                    let i = next.fetch_add(1, Ordering::Relaxed);
                    if i >= roots.len() {
                        break;
                    }
                    let (id, vendor, path) = &roots[i];
                    let rep = scan_single_root(id, vendor, path);
                    *slots[i].lock().unwrap() = Some(rep);
                }
            });
        }
    });
    slots
        .into_iter()
        .filter_map(|m| m.into_inner().ok().flatten())
        .collect()
}

fn scan_single_root(id: &str, vendor: &str, root: &Path) -> CacheDirReport {
    if !root.exists() {
        return CacheDirReport {
            id: id.into(),
            vendor: vendor.into(),
            path: root.display().to_string(),
            exists: false,
            file_count: 0,
            total_bytes: 0,
            zero_byte_files: 0,
            newest_unix_secs: None,
            oldest_unix_secs: None,
        };
    }
    let mut file_count = 0usize;
    let mut total_bytes = 0u64;
    let mut zero_byte_files = 0usize;
    let mut newest: Option<u64> = None;
    let mut oldest: Option<u64> = None;
    let mut stack = vec![root.to_path_buf()];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            let Ok(ft) = entry.file_type() else {
                continue;
            };
            if ft.is_dir() {
                stack.push(entry.path());
            } else if ft.is_file() {
                file_count += 1;
                if let Ok(meta) = entry.metadata() {
                    let len = meta.len();
                    total_bytes = total_bytes.saturating_add(len);
                    if len == 0 {
                        zero_byte_files += 1;
                    }
                    if let Ok(modified) = meta.modified()
                        && let Ok(dur) = modified.duration_since(UNIX_EPOCH)
                    {
                        let secs = dur.as_secs();
                        newest = Some(newest.map_or(secs, |n| n.max(secs)));
                        oldest = Some(oldest.map_or(secs, |o| o.min(secs)));
                    }
                }
            }
        }
    }
    CacheDirReport {
        id: id.into(),
        vendor: vendor.into(),
        path: root.display().to_string(),
        exists: true,
        file_count,
        total_bytes,
        zero_byte_files,
        newest_unix_secs: newest,
        oldest_unix_secs: oldest,
    }
}

pub fn check_driver_change(data_dir: &Path, gpu: &GpuAdapterInfo) -> DriverChange {
    let stamp_path = data_dir.join("exo-shaders-driver.json");
    let current = DriverStamp {
        adapter_name: gpu.name.clone(),
        driver_version: gpu.driver_version.clone(),
        driver_raw: gpu.driver_raw,
    };
    let previous = fs::read_to_string(&stamp_path)
        .ok()
        .and_then(|s| serde_json::from_str::<DriverStamp>(&s).ok());
    if let Ok(bytes) = serde_json::to_vec_pretty(&current) {
        let _ = fs::create_dir_all(data_dir);
        let _ = fs::write(&stamp_path, bytes);
    }
    match previous {
        None => DriverChange::FirstSeen { current },
        Some(prev)
            if prev.driver_raw == current.driver_raw
                && prev.driver_version == current.driver_version =>
        {
            DriverChange::Unchanged { current }
        }
        Some(previous) => DriverChange::Updated { previous, current },
    }
}

pub fn format_bytes(bytes: u64) -> String {
    const KB: f64 = 1024.0;
    const MB: f64 = KB * 1024.0;
    const GB: f64 = MB * 1024.0;
    let b = bytes as f64;
    if b >= GB {
        format!("{:.2} GB", b / GB)
    } else if b >= MB {
        format!("{:.1} MB", b / MB)
    } else if b >= KB {
        format!("{:.0} KB", b / KB)
    } else {
        format!("{bytes} B")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formats_nvidia_driver_version() {
        let raw = (32u64 << 48) | (15u64 << 16) | 6636u64;
        assert_eq!(format_driver_version("nvidia", raw), "566.36");
    }

    #[test]
    fn detects_driver_update_in_temp_dir() {
        let dir = env::temp_dir().join(format!("exo-shaders-drv-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        let gpu1 = GpuAdapterInfo {
            name: "RTX 3070".into(),
            vendor: "nvidia".into(),
            vendor_name: "NVIDIA".into(),
            vendor_id: 0x10DE,
            device_id: 0x2484,
            dedicated_vram_bytes: 8 * 1024 * 1024 * 1024,
            driver_version: "566.14".into(),
            driver_raw: 100,
        };
        let gpu2 = GpuAdapterInfo {
            driver_version: "566.36".into(),
            driver_raw: 200,
            ..gpu1.clone()
        };
        assert!(matches!(
            check_driver_change(&dir, &gpu1),
            DriverChange::FirstSeen { .. }
        ));
        assert!(matches!(
            check_driver_change(&dir, &gpu1),
            DriverChange::Unchanged { .. }
        ));
        assert!(matches!(
            check_driver_change(&dir, &gpu2),
            DriverChange::Updated { .. }
        ));
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn old_driver_cleanup_only_touches_provably_stale_and_backs_up_first() {
        let root = env::temp_dir().join(format!(
            "exo-old-drv-clean-{}-{}",
            std::process::id(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let _ = fs::remove_dir_all(&root);
        let data_dir = root.join("data");
        let libs = data_dir.join("libraries");
        let per_drv = root.join("PerDriverVersion");
        fs::create_dir_all(&libs).unwrap();
        fs::create_dir_all(per_drv.join("566.36")).unwrap();
        fs::create_dir_all(per_drv.join("621.35")).unwrap();
        fs::create_dir_all(per_drv.join("DXCache")).unwrap();
        fs::write(per_drv.join("566.36").join("old.bin"), b"old-driver-blob").unwrap();
        fs::write(per_drv.join("621.35").join("cur.bin"), b"current-blob").unwrap();
        fs::write(per_drv.join("DXCache").join("active.nvph"), b"active").unwrap();

        let valid_lib = libs.join("steam_cur.exoplib");
        let stale_lib = libs.join("steam_old.exoplib");
        crate::pso_format::write_pipeline_library(
            &valid_lib, 0x10DE, 0x2484, "621.35", 4, b"valid",
        )
        .unwrap();
        crate::pso_format::write_pipeline_library(
            &stale_lib, 0x10DE, 0x2484, "566.36", 4, b"stale",
        )
        .unwrap();

        let stale_found = find_stale_old_driver_caches(
            &data_dir,
            std::slice::from_ref(&per_drv),
            0x10DE,
            0x2484,
            "621.35",
        );
        assert_eq!(stale_found.len(), 2);

        let report = backup_and_clear_old_driver_caches(
            &data_dir,
            std::slice::from_ref(&per_drv),
            0x10DE,
            0x2484,
            "621.35",
        )
        .unwrap();
        assert_eq!(report.cleared, 2);
        assert_eq!(report.kept_valid, 3);
        assert_eq!(report.backup_paths.len(), 2);
        assert!(valid_lib.exists());
        assert!(!stale_lib.exists());
        assert!(per_drv.join("621.35").join("cur.bin").exists());
        assert!(per_drv.join("DXCache").join("active.nvph").exists());
        assert!(!per_drv.join("566.36").exists());
        for bp in &report.backup_paths {
            assert!(Path::new(bp).exists());
        }

        let dir_backup = report
            .backup_paths
            .iter()
            .find(|p| p.ends_with("_566.36"))
            .unwrap();
        let restored_dir = per_drv.join("566.36");
        let restored_dir_bytes =
            restore_backed_up_cache(Path::new(dir_backup), &restored_dir).unwrap();
        assert_eq!(restored_dir_bytes, b"old-driver-blob".len() as u64);
        assert_eq!(
            fs::read(restored_dir.join("old.bin")).unwrap(),
            b"old-driver-blob"
        );
        assert!(restore_backed_up_cache(Path::new(dir_backup), &restored_dir).is_err());

        let lib_backup = report
            .backup_paths
            .iter()
            .find(|p| p.ends_with("_steam_old.exoplib"))
            .unwrap();
        let restored_lib_bytes =
            restore_backed_up_cache(Path::new(lib_backup), &stale_lib).unwrap();
        assert!(restored_lib_bytes > 0);
        assert!(stale_lib.is_file());

        let _ = fs::remove_dir_all(&root);
    }
}
