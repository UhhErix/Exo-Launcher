use crate::fossilize::{FossilizeArchiveInfo, inspect_fossilize_archive};
use crate::policy::{AntiCheatClass, AntiCheatDetection, resolve_anti_cheat_class};
use crate::ue_archive::{UePakHeader, UeUtocHeader, inspect_pak_header, inspect_utoc_header};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;
use std::time::Instant;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum EngineFamily {
    Unreal4,
    Unreal5,
    Unity,
    Source2,
    GenericDx,
    Unknown,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameShaderProfile {
    pub id: String,
    pub name: String,
    pub install_dir: String,
    pub engine: EngineFamily,
    pub anti_cheat: AntiCheatDetection,
    pub pak_count: usize,
    pub utoc_count: usize,
    pub shader_cache_files: usize,
    pub shader_cache_bytes: u64,
    pub fossilize_archives: Vec<FossilizeArchiveInfo>,
    pub sample_pak: Option<UePakHeader>,
    pub sample_utoc: Option<UeUtocHeader>,
    pub estimated_pipelines: usize,
    pub scan_us: u64,
}

pub fn scan_game_directory(
    id: &str,
    name: &str,
    install_dir: &Path,
    host_class: Option<AntiCheatClass>,
    steam_shadercache_dir: Option<&Path>,
) -> GameShaderProfile {
    let start = Instant::now();
    let anti_cheat = resolve_anti_cheat_class(host_class, install_dir);
    let mut pak_count = 0usize;
    let mut utoc_count = 0usize;
    let mut shader_cache_files = 0usize;
    let mut shader_cache_bytes = 0u64;
    let mut sample_pak: Option<UePakHeader> = None;
    let mut sample_utoc: Option<UeUtocHeader> = None;
    let mut has_unity = false;
    let mut has_source2 = false;
    let mut visited = 0usize;

    walk_game(
        install_dir,
        0,
        &mut visited,
        &mut pak_count,
        &mut utoc_count,
        &mut shader_cache_files,
        &mut shader_cache_bytes,
        &mut sample_pak,
        &mut sample_utoc,
        &mut has_unity,
        &mut has_source2,
    );

    let mut fossilize_archives = Vec::new();
    if let Some(sc_dir) = steam_shadercache_dir
        && sc_dir.exists()
    {
        collect_fossilize(sc_dir, 0, &mut fossilize_archives);
    }

    let engine = if utoc_count > 0 {
        EngineFamily::Unreal5
    } else if pak_count > 0 {
        if sample_pak.as_ref().is_some_and(|p| p.version >= 11) {
            EngineFamily::Unreal5
        } else {
            EngineFamily::Unreal4
        }
    } else if has_unity {
        EngineFamily::Unity
    } else if has_source2 {
        EngineFamily::Source2
    } else if shader_cache_files > 0 || !fossilize_archives.is_empty() {
        EngineFamily::GenericDx
    } else {
        EngineFamily::Unknown
    };

    let estimated_pipelines = estimate_pipelines(
        pak_count,
        utoc_count,
        shader_cache_files,
        &fossilize_archives,
    );

    GameShaderProfile {
        id: id.into(),
        name: name.into(),
        install_dir: install_dir.display().to_string(),
        engine,
        anti_cheat,
        pak_count,
        utoc_count,
        shader_cache_files,
        shader_cache_bytes,
        fossilize_archives,
        sample_pak,
        sample_utoc,
        estimated_pipelines,
        scan_us: start.elapsed().as_micros() as u64,
    }
}

fn estimate_pipelines(
    pak_count: usize,
    utoc_count: usize,
    shader_cache_files: usize,
    foz: &[FossilizeArchiveInfo],
) -> usize {
    let foz_est: usize = foz
        .iter()
        .filter(|a| a.valid)
        .map(|a| (a.file_bytes / 4096).max(1) as usize)
        .sum();
    (pak_count * 120) + (utoc_count * 250) + (shader_cache_files * 80) + foz_est
}

#[allow(clippy::too_many_arguments)]
fn walk_game(
    dir: &Path,
    depth: usize,
    visited: &mut usize,
    pak_count: &mut usize,
    utoc_count: &mut usize,
    shader_cache_files: &mut usize,
    shader_cache_bytes: &mut u64,
    sample_pak: &mut Option<UePakHeader>,
    sample_utoc: &mut Option<UeUtocHeader>,
    has_unity: &mut bool,
    has_source2: &mut bool,
) {
    if depth > 6 || *visited > 8192 {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        *visited += 1;
        if *visited > 8192 {
            return;
        }
        let path = entry.path();
        let Ok(ft) = entry.file_type() else {
            continue;
        };
        if ft.is_dir() {
            let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
            if name == "shaders_pc" {
                *has_source2 = true;
            }
            walk_game(
                &path,
                depth + 1,
                visited,
                pak_count,
                utoc_count,
                shader_cache_files,
                shader_cache_bytes,
                sample_pak,
                sample_utoc,
                has_unity,
                has_source2,
            );
        } else if ft.is_file() {
            let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
            if name == "unityplayer.dll" {
                *has_unity = true;
            }
            if name.ends_with(".pak") {
                *pak_count += 1;
                if sample_pak.is_none() {
                    *sample_pak = inspect_pak_header(&path);
                }
            } else if name.ends_with(".utoc") {
                *utoc_count += 1;
                if sample_utoc.is_none() {
                    *sample_utoc = inspect_utoc_header(&path);
                }
            } else if name.ends_with(".ushaderprecache")
                || name.ends_with(".upipelinecache")
                || name.ends_with(".ushaderbytecode")
                || name.ends_with(".cso")
                || name.ends_with(".dxil")
                || name.ends_with(".pso")
            {
                *shader_cache_files += 1;
                if let Ok(m) = entry.metadata() {
                    *shader_cache_bytes = shader_cache_bytes.saturating_add(m.len());
                }
            }
        }
    }
}

fn collect_fossilize(dir: &Path, depth: usize, out: &mut Vec<FossilizeArchiveInfo>) {
    if depth > 4 || out.len() >= 64 {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
            collect_fossilize(&path, depth + 1, out);
        } else if path
            .extension()
            .and_then(|e| e.to_str())
            .is_some_and(|e| e.eq_ignore_ascii_case("foz"))
            && let Some(info) = inspect_fossilize_archive(&path)
        {
            out.push(info);
        }
    }
}

pub fn classify_status_label(profile: &GameShaderProfile) -> (&'static str, &'static str) {
    match profile.anti_cheat.class {
        AntiCheatClass::CompetitivePvP | AntiCheatClass::Unknown => {
            ("ready", "Driver-cache warm only (anti-cheat protected)")
        }
        AntiCheatClass::OnlinePvE => ("ready", "File routes ready (DLL opt-in)"),
        AntiCheatClass::Offline => {
            if profile.estimated_pipelines > 0 {
                ("ready", "Ready to compile")
            } else {
                ("ready", "No extra shader archives found")
            }
        }
    }
}
