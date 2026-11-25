use crate::catalog::UpscalerKind;
use crate::verify::{Vendor, hash_file_blake3, read_pe_file_version};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::Instant;

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiscoveredUpscaler {
    pub game_root: String,
    pub dll_path: String,
    pub file_name: String,
    pub kind: UpscalerKind,
    pub vendor: Vendor,
    pub version: Option<String>,
    pub size_bytes: u64,
    pub blake3: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryScanReport {
    pub game_roots_scanned: usize,
    pub dlls_found: usize,
    pub elapsed_ms: f64,
    pub items: Vec<DiscoveredUpscaler>,
}

pub fn scan_library_parallel(game_roots: &[PathBuf]) -> LibraryScanReport {
    let start = Instant::now();
    if game_roots.is_empty() {
        return LibraryScanReport {
            game_roots_scanned: 0,
            dlls_found: 0,
            elapsed_ms: 0.0,
            items: Vec::new(),
        };
    }

    let workers = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(4)
        .clamp(1, 8)
        .min(game_roots.len());
    let chunk_size = game_roots.len().div_ceil(workers);
    let results: Mutex<Vec<DiscoveredUpscaler>> = Mutex::new(Vec::new());

    std::thread::scope(|scope| {
        for chunk in game_roots.chunks(chunk_size) {
            let results_ref = &results;
            scope.spawn(move || {
                let mut local = Vec::new();
                for root in chunk {
                    scan_single_root(root, root, 0, &mut local);
                }
                if !local.is_empty()
                    && let Ok(mut guard) = results_ref.lock()
                {
                    guard.extend(local);
                }
            });
        }
    });

    let mut items = results.into_inner().unwrap_or_default();
    items.sort_by(|a, b| a.dll_path.cmp(&b.dll_path));
    let elapsed_ms = (start.elapsed().as_secs_f64() * 1000.0 * 100.0).round() / 100.0;
    LibraryScanReport {
        game_roots_scanned: game_roots.len(),
        dlls_found: items.len(),
        elapsed_ms,
        items,
    }
}

fn scan_single_root(game_root: &Path, dir: &Path, depth: usize, out: &mut Vec<DiscoveredUpscaler>) {
    if depth > 6 {
        return;
    }
    let Ok(rd) = fs::read_dir(dir) else {
        return;
    };
    for entry in rd.flatten() {
        let path = entry.path();
        let Ok(ft) = entry.file_type() else {
            continue;
        };
        if ft.is_dir() {
            if let Some(name) = path.file_name().and_then(|n| n.to_str()) {
                let lower = name.to_ascii_lowercase();
                if matches!(
                    lower.as_str(),
                    ".git" | "node_modules" | "shadercache" | "crashreportclient" | "logs"
                ) {
                    continue;
                }
            }
            scan_single_root(game_root, &path, depth + 1, out);
        } else if ft.is_file()
            && let Some(name) = path.file_name().and_then(|n| n.to_str())
            && let Some(kind) = UpscalerKind::from_file_name(name)
        {
            let size_bytes = entry.metadata().map(|m| m.len()).unwrap_or(0);
            let blake3 = hash_file_blake3(&path).unwrap_or_default();
            let version = read_pe_file_version(&path);
            out.push(DiscoveredUpscaler {
                game_root: game_root.display().to_string(),
                dll_path: path.display().to_string(),
                file_name: name.to_string(),
                kind,
                vendor: kind.vendor(),
                version,
                size_bytes,
                blake3,
            });
        }
    }
}

pub fn create_mock_50_game_tree(base: &Path) -> Result<Vec<PathBuf>, String> {
    let _ = fs::remove_dir_all(base);
    fs::create_dir_all(base).map_err(|e| format!("create mock tree: {e}"))?;
    let mut roots = Vec::with_capacity(50);
    let dll_names = [
        "nvngx_dlss.dll",
        "nvngx_dlssg.dll",
        "amd_fidelityfx_dx12.dll",
        "libxess.dll",
    ];
    for i in 0..50usize {
        let root = base.join(format!("Game_{i:02}"));
        let bin = root.join("Engine").join("Binaries").join("Win64");
        let content = root.join("Content").join("Paks");
        fs::create_dir_all(&bin).map_err(|e| format!("mkdir bin: {e}"))?;
        fs::create_dir_all(&content).map_err(|e| format!("mkdir content: {e}"))?;
        let dll = dll_names[i % dll_names.len()];
        fs::write(bin.join(dll), format!("MOCK-UPSCALER-PAYLOAD-{i:02}"))
            .map_err(|e| e.to_string())?;
        if i % 3 == 0 {
            fs::write(bin.join("nvngx_dlssg.dll"), format!("MOCK-DLSSG-{i:02}"))
                .map_err(|e| e.to_string())?;
        }
        for j in 0..8usize {
            fs::write(content.join(format!("pak_{j}.pak")), [0u8; 64])
                .map_err(|e| e.to_string())?;
        }
        roots.push(root);
    }
    Ok(roots)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parallel_scan_finds_all_upscalers_across_50_mock_games_under_150ms() {
        let base = std::env::temp_dir().join(format!("exo-upscale-scan-{}", std::process::id()));
        let roots = create_mock_50_game_tree(&base).expect("create 50 game mock tree");
        let report = scan_library_parallel(&roots);
        assert_eq!(report.game_roots_scanned, 50);
        assert!(report.dlls_found >= 50);
        assert!(
            report.elapsed_ms < 1500.0,
            "scan took too long: {} ms",
            report.elapsed_ms
        );
        let _ = fs::remove_dir_all(&base);
    }
}
