pub use exo_rpc::anticheat::AntiCheatClass;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RouteKind {
    DriverCacheScan,
    OfflinePsoWarm,
    FileRoute,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "decision")]
pub enum RouteDecision {
    Allowed,
    AllowedWithNote { note: String },
    Denied { reason: String },
}

impl RouteDecision {
    pub fn is_permitted(&self) -> bool {
        matches!(self, Self::Allowed | Self::AllowedWithNote { .. })
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AntiCheatDetection {
    pub class: AntiCheatClass,
    pub markers: Vec<String>,
}

pub fn evaluate_route(
    class: AntiCheatClass,
    route: RouteKind,
    game_running: bool,
    has_restore_record: bool,
) -> RouteDecision {
    match route {
        RouteKind::DriverCacheScan | RouteKind::OfflinePsoWarm => RouteDecision::Allowed,
        RouteKind::FileRoute if game_running => RouteDecision::Denied {
            reason: "Game is currently running; never modify game shader folders while active."
                .into(),
        },
        RouteKind::FileRoute if !has_restore_record => RouteDecision::Denied {
            reason: "File route requires a tracked restore record before writing.".into(),
        },
        RouteKind::FileRoute => match class {
            AntiCheatClass::CompetitivePvpOnline | AntiCheatClass::Unknown => {
                RouteDecision::Denied {
                    reason: "Competitive PvP and unknown games disallow game-folder file routes."
                        .into(),
                }
            }
            AntiCheatClass::OnlineNonCompetitivePve => RouteDecision::AllowedWithNote {
                note: "Online PvE: game-folder shader cache file route allowed while closed with restore record.".into(),
            },
            AntiCheatClass::OfflineAntiCheat => RouteDecision::AllowedWithNote {
                note: "Offline game: game-folder shader cache file route allowed while closed with restore record.".into(),
            },
        },
    }
}

const PVP_MARKERS: &[(&str, &str)] = &[
    ("easyanticheat", "EasyAntiCheat"),
    ("battleye", "BattlEye"),
    ("vgk.sys", "Vanguard"),
    ("vgc.exe", "Vanguard"),
    ("faceit", "FACEIT"),
    ("punkbuster", "PunkBuster"),
    ("xigncode", "XIGNCODE"),
    ("gameguard", "GameGuard"),
    ("anticheatexpert", "ACE"),
    ("randgrid.sys", "Ricochet"),
    ("eaanticheat", "EA AntiCheat"),
];

pub fn resolve_anti_cheat_class(
    host_class: Option<AntiCheatClass>,
    install_dir: &Path,
) -> AntiCheatDetection {
    let base = host_class.unwrap_or(AntiCheatClass::Unknown);
    if !install_dir.exists() {
        return AntiCheatDetection {
            class: base.stricter_of(AntiCheatClass::Unknown),
            markers: Vec::new(),
        };
    }
    let mut found = Vec::new();
    let mut visited = 0usize;
    let mut unreadable = false;
    walk_markers(install_dir, 0, &mut visited, &mut unreadable, &mut found);
    found.sort();
    found.dedup();
    let local_floor = if !found.is_empty() {
        AntiCheatClass::CompetitivePvpOnline
    } else if unreadable {
        AntiCheatClass::Unknown
    } else {
        base
    };
    AntiCheatDetection {
        class: base.stricter_of(local_floor),
        markers: found,
    }
}

fn walk_markers(
    dir: &Path,
    depth: usize,
    visited: &mut usize,
    unreadable: &mut bool,
    found: &mut Vec<String>,
) {
    if depth > 4 || *visited > 4096 {
        return;
    }
    let entries = match fs::read_dir(dir) {
        Ok(e) => e,
        Err(_) => {
            *unreadable = true;
            return;
        }
    };
    for entry in entries.flatten() {
        *visited += 1;
        if *visited > 4096 {
            return;
        }
        let name = entry.file_name().to_string_lossy().to_ascii_lowercase();
        for (needle, label) in PVP_MARKERS {
            if name.contains(needle) {
                found.push((*label).to_string());
            }
        }
        if entry.file_type().map(|t| t.is_dir()).unwrap_or(false) {
            walk_markers(&entry.path(), depth + 1, visited, unreadable, found);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cs2_like_folder_without_markers_is_unknown_and_denies_file_route() {
        let root = std::env::temp_dir().join(format!("exo-shaders-cs2-{}", std::process::id()));
        let bin = root.join("game").join("bin").join("win64");
        let _ = fs::create_dir_all(&bin);
        let _ = fs::write(bin.join("cs2.exe"), b"stub");
        let det = resolve_anti_cheat_class(None, &root);
        assert_eq!(det.class, AntiCheatClass::Unknown);
        assert!(det.markers.is_empty());
        assert!(!evaluate_route(det.class, RouteKind::FileRoute, false, true).is_permitted());
        assert!(evaluate_route(det.class, RouteKind::DriverCacheScan, false, false).is_permitted());
        assert!(evaluate_route(det.class, RouteKind::OfflinePsoWarm, false, false).is_permitted());
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn host_offline_plus_eac_marker_escalates_to_competitive_pvp() {
        let root = std::env::temp_dir().join(format!("exo-shaders-eac-{}", std::process::id()));
        let eac = root.join("EasyAntiCheat");
        let _ = fs::create_dir_all(&eac);
        let det = resolve_anti_cheat_class(Some(AntiCheatClass::OfflineAntiCheat), &root);
        assert_eq!(det.class, AntiCheatClass::CompetitivePvpOnline);
        assert!(det.markers.iter().any(|m| m == "EasyAntiCheat"));
        assert!(!evaluate_route(det.class, RouteKind::FileRoute, false, true).is_permitted());
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn file_route_requires_closed_game_and_restore_record() {
        for class in [
            AntiCheatClass::OfflineAntiCheat,
            AntiCheatClass::OnlineNonCompetitivePve,
        ] {
            assert!(evaluate_route(class, RouteKind::FileRoute, false, true).is_permitted());
            assert!(!evaluate_route(class, RouteKind::FileRoute, true, true).is_permitted());
            assert!(!evaluate_route(class, RouteKind::FileRoute, false, false).is_permitted());
        }
        for class in [
            AntiCheatClass::CompetitivePvpOnline,
            AntiCheatClass::Unknown,
        ] {
            assert!(!evaluate_route(class, RouteKind::FileRoute, false, true).is_permitted());
            assert!(evaluate_route(class, RouteKind::DriverCacheScan, false, false).is_permitted());
            assert!(evaluate_route(class, RouteKind::OfflinePsoWarm, false, false).is_permitted());
        }
    }
}
