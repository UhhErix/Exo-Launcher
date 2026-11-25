use crate::game_cache::GameShaderProfile;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameAsdFiles {
    pub game_id: String,
    pub game_name: String,
    pub sodb_files: Vec<String>,
    pub psdb_files: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AsdStatus {
    pub os_supported: bool,
    pub d3dscache_present: bool,
    pub d3dconfig_present: bool,
    pub d3dscr_api_public: bool,
    pub third_party_psdb_download_available: bool,
    pub games_with_sodb: Vec<GameAsdFiles>,
    pub games_with_psdb: Vec<GameAsdFiles>,
    pub summary: String,
}

fn system32_dir() -> PathBuf {
    std::env::var("SystemRoot")
        .map(|r| PathBuf::from(r).join("System32"))
        .unwrap_or_else(|_| PathBuf::from(r"C:\Windows\System32"))
}

pub fn scan_dir_for_asd_databases(root: &Path) -> (Vec<String>, Vec<String>) {
    let mut sodb = Vec::new();
    let mut psdb = Vec::new();
    if !root.is_dir() {
        return (sodb, psdb);
    }
    let mut visited = 0usize;
    walk_asd(root, 0, &mut visited, &mut sodb, &mut psdb);
    sodb.sort();
    psdb.sort();
    (sodb, psdb)
}

fn walk_asd(
    dir: &Path,
    depth: usize,
    visited: &mut usize,
    sodb: &mut Vec<String>,
    psdb: &mut Vec<String>,
) {
    if depth > 5 || *visited > 4096 {
        return;
    }
    let Ok(entries) = fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        *visited += 1;
        if *visited > 4096 {
            return;
        }
        let Ok(ft) = entry.file_type() else {
            continue;
        };
        if ft.is_symlink() {
            continue;
        }
        let path = entry.path();
        if ft.is_dir() {
            walk_asd(&path, depth + 1, visited, sodb, psdb);
        } else if ft.is_file()
            && let Some(ext) = path.extension().and_then(|e| e.to_str())
        {
            if ext.eq_ignore_ascii_case("sodb") {
                sodb.push(path.display().to_string());
            } else if ext.eq_ignore_ascii_case("psdb") {
                psdb.push(path.display().to_string());
            }
        }
    }
}

pub fn probe_asd_status(games: &[GameShaderProfile]) -> AsdStatus {
    let sys32 = system32_dir();
    let d3dscache_present = sys32.join("D3DSCache.dll").is_file();
    let d3dconfig_present = sys32.join("d3dconfig.exe").is_file();
    let os_supported = d3dscache_present && d3dconfig_present;

    let mut games_with_sodb = Vec::new();
    let mut games_with_psdb = Vec::new();
    for game in games {
        let dir = Path::new(&game.install_dir);
        let (sodb_files, psdb_files) = scan_dir_for_asd_databases(dir);
        if !sodb_files.is_empty() {
            games_with_sodb.push(GameAsdFiles {
                game_id: game.id.clone(),
                game_name: game.name.clone(),
                sodb_files: sodb_files.clone(),
                psdb_files: psdb_files.clone(),
            });
        }
        if !psdb_files.is_empty() {
            games_with_psdb.push(GameAsdFiles {
                game_id: game.id.clone(),
                game_name: game.name.clone(),
                sodb_files,
                psdb_files,
            });
        }
    }

    let summary = if !os_supported {
        "Windows 11 Advanced Shader Delivery runtime components not present on this OS.".into()
    } else if !games_with_psdb.is_empty() {
        format!(
            "Windows 11 ASD runtime ready ({} game(s) with local .psdb, {} with .sodb); third-party cloud PSDB download is not publicly exposed.",
            games_with_psdb.len(),
            games_with_sodb.len()
        )
    } else if !games_with_sodb.is_empty() {
        format!(
            "Windows 11 ASD runtime ready ({} game(s) ship .sodb); third-party cloud PSDB delivery requires storefront/IHV compiler integration.",
            games_with_sodb.len()
        )
    } else {
        "Windows 11 ASD runtime detected (D3DSCache.dll + d3dconfig.exe); no scanned games ship .sodb/.psdb databases and no public third-party PSDB CDN exists.".into()
    };

    AsdStatus {
        os_supported,
        d3dscache_present,
        d3dconfig_present,
        d3dscr_api_public: true,
        third_party_psdb_download_available: false,
        games_with_sodb,
        games_with_psdb,
        summary,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::game_cache::EngineFamily;
    use crate::policy::{AntiCheatClass, AntiCheatDetection};

    #[test]
    fn discovers_sodb_and_psdb_files_and_reports_honest_third_party_status() {
        let root = std::env::temp_dir().join(format!("exo-asd-test-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let shaders_dir = root.join("Content").join("Shaders");
        fs::create_dir_all(&shaders_dir).unwrap();
        fs::write(shaders_dir.join("game.sodb"), b"SODB").unwrap();
        fs::write(shaders_dir.join("game_nv.psdb"), b"PSDB").unwrap();

        let profile = GameShaderProfile {
            id: "steam:999".into(),
            name: "AsdSample".into(),
            install_dir: root.display().to_string(),
            engine: EngineFamily::Unreal5,
            anti_cheat: AntiCheatDetection {
                class: AntiCheatClass::CompetitivePvpOnline,
                markers: Vec::new(),
            },
            pak_count: 0,
            utoc_count: 0,
            shader_cache_files: 0,
            shader_cache_bytes: 0,
            precache_artifacts: Vec::new(),
            fossilize_archives: Vec::new(),
            sample_pak: None,
            sample_utoc: None,
            estimated_pipelines: 0,
            scan_us: 0,
        };

        let status = probe_asd_status(&[profile]);
        assert!(status.d3dscr_api_public);
        assert!(!status.third_party_psdb_download_available);
        assert_eq!(status.games_with_sodb.len(), 1);
        assert_eq!(status.games_with_psdb.len(), 1);
        assert!(status.os_supported);
        assert!(status.d3dscache_present);
        assert!(status.d3dconfig_present);

        let _ = fs::remove_dir_all(&root);
    }
}
