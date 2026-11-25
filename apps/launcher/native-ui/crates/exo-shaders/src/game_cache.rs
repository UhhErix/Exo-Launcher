use crate::fossilize::{FossilizeArchiveInfo, inspect_fossilize_archive};
use crate::policy::{AntiCheatClass, AntiCheatDetection, resolve_anti_cheat_class};
use crate::ue_archive::{UePakHeader, UeUtocHeader, inspect_pak_header, inspect_utoc_header};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;
use std::time::{Instant, UNIX_EPOCH};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum EngineFamily {
    Unreal4,
    Unreal5,
    SteamFossilize,
    Unity,
    Godot,
    CallOfDuty,
    Frostbite,
    Source2,
    IdTech,
    GenericDx,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PrecacheKind {
    UePipelineCache,
    UeShaderBytecode,
    SteamFossilize,
    UnityShaderCache,
    GodotShaderCache,
    CallOfDutyPsoLib,
    FrostbiteCache,
    Source2Cache,
    IdTechVkCache,
    Dx12PipelineLibrary,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrecacheArtifact {
    pub path: String,
    pub kind: PrecacheKind,
    pub bytes: u64,
    pub modified_unix_secs: Option<u64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PrecompileInvalidationReason {
    DriverUpdated,
    AdapterChanged,
    GameBuildChanged,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrecompileNotice {
    pub game_id: String,
    pub game_name: String,
    pub engine: EngineFamily,
    pub reason: PrecompileInvalidationReason,
    pub prompt: String,
    pub can_auto_prepare: bool,
    pub requires_user_launch: bool,
    pub precache_file_count: usize,
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
    pub precache_artifacts: Vec<PrecacheArtifact>,
    pub fossilize_archives: Vec<FossilizeArchiveInfo>,
    pub sample_pak: Option<UePakHeader>,
    pub sample_utoc: Option<UeUtocHeader>,
    pub estimated_pipelines: usize,
    pub scan_us: u64,
}

pub fn evaluate_precompile_notice(
    profile: &GameShaderProfile,
    reason: Option<PrecompileInvalidationReason>,
) -> Option<PrecompileNotice> {
    let reason = reason?;
    let has_valid_foz = profile.fossilize_archives.iter().any(|a| a.valid);
    let has_precache = !profile.precache_artifacts.is_empty()
        || has_valid_foz
        || matches!(
            profile.engine,
            EngineFamily::Unreal4
                | EngineFamily::Unreal5
                | EngineFamily::CallOfDuty
                | EngineFamily::Frostbite
                | EngineFamily::Source2
                | EngineFamily::IdTech
        );
    if !has_precache {
        return None;
    }
    let can_auto_prepare = has_valid_foz || profile.anti_cheat.class.allows_offline_dll_mod();
    let prompt = if can_auto_prepare {
        format!(
            "Shaders need rebuilding for {}: Exo can do it now while you're away",
            profile.name
        )
    } else {
        format!(
            "Shaders need rebuilding for {}: launch the game to let its built-in precompile finish",
            profile.name
        )
    };
    Some(PrecompileNotice {
        game_id: profile.id.clone(),
        game_name: profile.name.clone(),
        engine: profile.engine,
        reason,
        prompt,
        can_auto_prepare,
        requires_user_launch: !can_auto_prepare,
        precache_file_count: profile.precache_artifacts.len() + profile.fossilize_archives.len(),
    })
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
    let mut scan = WalkAccum::default();

    walk_game(install_dir, 0, &mut scan);

    let mut fossilize_archives = Vec::new();
    if let Some(sc_dir) = steam_shadercache_dir
        && sc_dir.exists()
    {
        collect_fossilize(sc_dir, 0, &mut fossilize_archives);
    }

    let engine = if scan.utoc_count > 0 {
        EngineFamily::Unreal5
    } else if scan.pak_count > 0 {
        if scan.sample_pak.as_ref().is_some_and(|p| p.version >= 11) {
            EngineFamily::Unreal5
        } else {
            EngineFamily::Unreal4
        }
    } else if scan.has_cod {
        EngineFamily::CallOfDuty
    } else if scan.has_frostbite {
        EngineFamily::Frostbite
    } else if scan.has_idtech {
        EngineFamily::IdTech
    } else if scan.has_godot {
        EngineFamily::Godot
    } else if scan.has_unity {
        EngineFamily::Unity
    } else if scan.has_source2 {
        EngineFamily::Source2
    } else if fossilize_archives.iter().any(|a| a.valid) {
        EngineFamily::SteamFossilize
    } else if scan.shader_cache_files > 0 {
        EngineFamily::GenericDx
    } else {
        EngineFamily::Unknown
    };

    let estimated_pipelines = estimate_pipelines(
        scan.pak_count,
        scan.utoc_count,
        scan.shader_cache_files,
        &fossilize_archives,
    );

    GameShaderProfile {
        id: id.into(),
        name: name.into(),
        install_dir: install_dir.display().to_string(),
        engine,
        anti_cheat,
        pak_count: scan.pak_count,
        utoc_count: scan.utoc_count,
        shader_cache_files: scan.shader_cache_files,
        shader_cache_bytes: scan.shader_cache_bytes,
        precache_artifacts: scan.precache_artifacts,
        fossilize_archives,
        sample_pak: scan.sample_pak,
        sample_utoc: scan.sample_utoc,
        estimated_pipelines,
        scan_us: start.elapsed().as_micros() as u64,
    }
}

#[derive(Default)]
struct WalkAccum {
    visited: usize,
    pak_count: usize,
    utoc_count: usize,
    shader_cache_files: usize,
    shader_cache_bytes: u64,
    precache_artifacts: Vec<PrecacheArtifact>,
    sample_pak: Option<UePakHeader>,
    sample_utoc: Option<UeUtocHeader>,
    has_unity: bool,
    has_godot: bool,
    has_cod: bool,
    has_frostbite: bool,
    has_source2: bool,
    has_idtech: bool,
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

fn push_artifact(
    scan: &mut WalkAccum,
    path: &Path,
    kind: PrecacheKind,
    bytes: u64,
    modified: Option<u64>,
) {
    scan.shader_cache_files += 1;
    scan.shader_cache_bytes = scan.shader_cache_bytes.saturating_add(bytes);
    if scan.precache_artifacts.len() < 128 {
        scan.precache_artifacts.push(PrecacheArtifact {
            path: path.display().to_string(),
            kind,
            bytes,
            modified_unix_secs: modified,
        });
    }
}

fn walk_game(dir: &Path, depth: usize, scan: &mut WalkAccum) {
    if depth > 6 || scan.visited > 8192 {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        scan.visited += 1;
        if scan.visited > 8192 {
            return;
        }
        let path = entry.path();
        let Ok(ft) = entry.file_type() else {
            continue;
        };
        if ft.is_symlink() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
        if ft.is_dir() {
            if name == "shaders_pc" {
                scan.has_source2 = true;
            } else if matches!(
                name.as_str(),
                "cod22" | "cod23" | "cod24" | "cod25" | "cod26"
            ) {
                scan.has_cod = true;
            } else if name == "shader_cache"
                && dir.to_string_lossy().to_ascii_lowercase().contains("godot")
            {
                scan.has_godot = true;
            }
            walk_game(&path, depth + 1, scan);
        } else if ft.is_file() {
            let (bytes, modified) = entry
                .metadata()
                .map(|m| {
                    let mod_s = m
                        .modified()
                        .ok()
                        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
                        .map(|d| d.as_secs());
                    (m.len(), mod_s)
                })
                .unwrap_or((0, None));

            if name == "unityplayer.dll" {
                scan.has_unity = true;
            } else if name.ends_with(".pck") && (name.contains("godot") || depth <= 1) {
                if name.contains("godot") {
                    scan.has_godot = true;
                }
            } else if name == "godot.windows.editor.x86_64.exe"
                || name.starts_with("godot") && name.ends_with(".dll")
            {
                scan.has_godot = true;
            } else if name.starts_with("idtech") || name.contains("vkpipelinecache") {
                scan.has_idtech = true;
            } else if name.ends_with(".toc")
                && dir
                    .file_name()
                    .is_some_and(|n| n.eq_ignore_ascii_case("data"))
            {
                scan.has_frostbite = true;
            }

            if name.ends_with(".pak") {
                scan.pak_count += 1;
                if scan.sample_pak.is_none() {
                    scan.sample_pak = inspect_pak_header(&path);
                }
            } else if name.ends_with(".utoc") {
                scan.utoc_count += 1;
                if scan.sample_utoc.is_none() {
                    scan.sample_utoc = inspect_utoc_header(&path);
                }
            } else if name.ends_with(".upipelinecache") || name.ends_with(".ushaderprecache") {
                push_artifact(scan, &path, PrecacheKind::UePipelineCache, bytes, modified);
            } else if name.ends_with(".ushaderbytecode") {
                push_artifact(scan, &path, PrecacheKind::UeShaderBytecode, bytes, modified);
            } else if name.ends_with(".psolib") {
                let kind = if scan.has_cod
                    || path
                        .to_string_lossy()
                        .to_ascii_lowercase()
                        .contains("shadercache")
                {
                    if path.to_string_lossy().to_ascii_lowercase().contains("cod") {
                        scan.has_cod = true;
                        PrecacheKind::CallOfDutyPsoLib
                    } else {
                        PrecacheKind::Dx12PipelineLibrary
                    }
                } else {
                    PrecacheKind::Dx12PipelineLibrary
                };
                push_artifact(scan, &path, kind, bytes, modified);
            } else if name.ends_with(".plib") || name == "shader.cache2" || name == "psocache.bin" {
                push_artifact(
                    scan,
                    &path,
                    PrecacheKind::Dx12PipelineLibrary,
                    bytes,
                    modified,
                );
            } else if name.ends_with(".pcdx12") || name.ends_with(".psocache") {
                scan.has_frostbite = true;
                push_artifact(scan, &path, PrecacheKind::FrostbiteCache, bytes, modified);
            } else if name.contains("vkpipelinecache") || name.ends_with(".vkcache") {
                push_artifact(scan, &path, PrecacheKind::IdTechVkCache, bytes, modified);
            } else if name.ends_with(".scache") {
                scan.has_godot = true;
                push_artifact(scan, &path, PrecacheKind::GodotShaderCache, bytes, modified);
            } else if name.ends_with(".graphicsstate") || name.contains("shadervariant") {
                scan.has_unity = true;
                push_artifact(scan, &path, PrecacheKind::UnityShaderCache, bytes, modified);
            } else if name.ends_with(".cso") || name.ends_with(".dxil") || name.ends_with(".pso") {
                let kind = if scan.has_source2 {
                    PrecacheKind::Source2Cache
                } else {
                    PrecacheKind::Dx12PipelineLibrary
                };
                push_artifact(scan, &path, kind, bytes, modified);
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
    classify_status_for_game(profile, false)
}

pub fn classify_status_for_game(
    profile: &GameShaderProfile,
    has_recorded_psos: bool,
) -> (&'static str, &'static str) {
    let has_valid_foz = profile.fossilize_archives.iter().any(|a| a.valid);
    match profile.anti_cheat.class {
        AntiCheatClass::CompetitivePvpOnline | AntiCheatClass::Unknown => {
            if has_valid_foz {
                ("ready", "Vulkan Fossilize cache available (zero-hook)")
            } else {
                (
                    "managed",
                    "Driver cache managed (no proxy — online anti-cheat)",
                )
            }
        }
        AntiCheatClass::OnlineNonCompetitivePve => {
            if has_valid_foz {
                ("ready", "Vulkan Fossilize cache available (zero-hook)")
            } else {
                ("managed", "Driver cache managed (recorder opt-in)")
            }
        }
        AntiCheatClass::OfflineAntiCheat => {
            if has_recorded_psos {
                ("ready", "Recorded D3D12 shaders ready to compile")
            } else if has_valid_foz {
                ("ready", "Vulkan Fossilize cache available")
            } else if !profile.precache_artifacts.is_empty() {
                ("ready", "Engine shader precache detected")
            } else {
                ("record", "Play once with the recorder to capture shaders")
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::policy::AntiCheatDetection;

    #[test]
    fn detects_engine_precache_artifacts_and_driver_rebuild_notices() {
        let root = std::env::temp_dir().join(format!("exo-precache-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);

        let ue_dir = root.join("Thymesia");
        fs::create_dir_all(ue_dir.join("Saved").join("PipelineCaches")).unwrap();
        fs::write(
            ue_dir
                .join("Saved")
                .join("PipelineCaches")
                .join("Thymesia_PCD3D_SM6.upipelinecache"),
            b"UE_PSO_CACHE",
        )
        .unwrap();

        let cod_dir = root.join("CallOfDuty");
        fs::create_dir_all(cod_dir.join("cod26").join("shadercache")).unwrap();
        fs::write(
            cod_dir
                .join("cod26")
                .join("shadercache")
                .join("techsets_global.psolib"),
            b"COD_PSOLIB",
        )
        .unwrap();

        let ue_prof = scan_game_directory(
            "steam:1343240",
            "Thymesia",
            &ue_dir,
            Some(AntiCheatClass::OfflineAntiCheat),
            None,
        );
        assert_eq!(ue_prof.precache_artifacts.len(), 1);
        assert_eq!(
            ue_prof.precache_artifacts[0].kind,
            PrecacheKind::UePipelineCache
        );
        assert!(evaluate_precompile_notice(&ue_prof, None).is_none());

        let ue_notice =
            evaluate_precompile_notice(&ue_prof, Some(PrecompileInvalidationReason::DriverUpdated))
                .expect("driver update triggers notice");
        assert!(ue_notice.can_auto_prepare);
        assert!(!ue_notice.requires_user_launch);
        assert_eq!(
            ue_notice.prompt,
            "Shaders need rebuilding for Thymesia: Exo can do it now while you're away"
        );

        let cod_prof = scan_game_directory(
            "steam:1938090",
            "Call of Duty",
            &cod_dir,
            Some(AntiCheatClass::CompetitivePvpOnline),
            None,
        );
        assert_eq!(cod_prof.engine, EngineFamily::CallOfDuty);
        assert_eq!(cod_prof.precache_artifacts.len(), 1);
        assert_eq!(
            cod_prof.precache_artifacts[0].kind,
            PrecacheKind::CallOfDutyPsoLib
        );

        let cod_notice = evaluate_precompile_notice(
            &cod_prof,
            Some(PrecompileInvalidationReason::DriverUpdated),
        )
        .expect("CoD notice");
        assert!(!cod_notice.can_auto_prepare);
        assert!(cod_notice.requires_user_launch);
        assert!(cod_notice.prompt.contains("launch the game"));

        let foz_dir = root.join("Dota2Foz").join("fozpipelinesv6");
        fs::create_dir_all(&foz_dir).unwrap();
        let mut foz_hdr = [0u8; 32];
        foz_hdr[0..10].copy_from_slice(b"\x81FOSSILIZE");
        foz_hdr[15] = 6;
        fs::write(foz_dir.join("steamapp_shader_cache.foz"), foz_hdr).unwrap();

        let dota_prof = scan_game_directory(
            "steam:570",
            "Dota 2",
            &root.join("Dota2Empty"),
            Some(AntiCheatClass::CompetitivePvpOnline),
            Some(&root.join("Dota2Foz")),
        );
        assert_eq!(dota_prof.engine, EngineFamily::SteamFossilize);
        let dota_notice = evaluate_precompile_notice(
            &dota_prof,
            Some(PrecompileInvalidationReason::DriverUpdated),
        )
        .expect("Fossilize notice");
        assert!(dota_notice.can_auto_prepare);

        let _ = fs::remove_dir_all(&root);
    }

    fn sample_profile(class: AntiCheatClass, has_foz: bool) -> GameShaderProfile {
        GameShaderProfile {
            id: "steam:1".into(),
            name: "Sample".into(),
            install_dir: "C:\\Games\\Sample".into(),
            engine: EngineFamily::Unreal5,
            anti_cheat: AntiCheatDetection {
                class,
                markers: Vec::new(),
            },
            pak_count: 2,
            utoc_count: 1,
            shader_cache_files: 1,
            shader_cache_bytes: 4096,
            precache_artifacts: Vec::new(),
            fossilize_archives: if has_foz {
                vec![FossilizeArchiveInfo {
                    path: "steamapp_shader_cache.foz".into(),
                    file_bytes: 1024,
                    format_version: 6,
                    valid: true,
                }]
            } else {
                Vec::new()
            },
            sample_pak: None,
            sample_utoc: None,
            estimated_pipelines: 500,
            scan_us: 10,
        }
    }

    #[test]
    fn classifies_honest_status_without_placebo_claims() {
        let pvp = sample_profile(AntiCheatClass::CompetitivePvpOnline, false);
        assert_eq!(
            classify_status_for_game(&pvp, false),
            (
                "managed",
                "Driver cache managed (no proxy — online anti-cheat)"
            )
        );

        let pvp_foz = sample_profile(AntiCheatClass::CompetitivePvpOnline, true);
        assert_eq!(
            classify_status_for_game(&pvp_foz, false),
            ("ready", "Vulkan Fossilize cache available (zero-hook)")
        );

        let offline_unrecorded = sample_profile(AntiCheatClass::OfflineAntiCheat, false);
        assert_eq!(
            classify_status_for_game(&offline_unrecorded, false),
            ("record", "Play once with the recorder to capture shaders")
        );
        assert_eq!(
            classify_status_for_game(&offline_unrecorded, true),
            ("ready", "Recorded D3D12 shaders ready to compile")
        );
    }
}
