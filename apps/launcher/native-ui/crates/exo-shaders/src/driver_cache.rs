use serde::{Deserialize, Serialize};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Instant, UNIX_EPOCH};
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
}
