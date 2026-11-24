use serde::{Deserialize, Serialize};
use std::fs;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum AntiCheatClass {
    CompetitivePvP,
    OnlinePvE,
    Offline,
    Unknown,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum RouteKind {
    DriverCacheScan,
    OfflinePsoWarm,
    FileRoute,
    DllRoute,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "decision")]
pub enum RouteDecision {
    Allowed,
    AllowedWithNote { note: String },
    RequiresOptIn { reason: String },
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
    dll_opt_in: bool,
) -> RouteDecision {
    match route {
        RouteKind::DriverCacheScan | RouteKind::OfflinePsoWarm => RouteDecision::Allowed,
        RouteKind::FileRoute | RouteKind::DllRoute if game_running => RouteDecision::Denied {
            reason: "Game is currently running; never modify files or inject while active.".into(),
        },
        RouteKind::FileRoute => match class {
            AntiCheatClass::CompetitivePvP | AntiCheatClass::Unknown => RouteDecision::Denied {
                reason: "Competitive PvP and unknown games disallow file routes.".into(),
            },
            AntiCheatClass::OnlinePvE => RouteDecision::Allowed,
            AntiCheatClass::Offline => RouteDecision::AllowedWithNote {
                note: "Offline game: file route allowed and tracked for clean restore.".into(),
            },
        },
        RouteKind::DllRoute => match class {
            AntiCheatClass::CompetitivePvP | AntiCheatClass::Unknown => RouteDecision::Denied {
                reason:
                    "Competitive PvP and unknown games forbid DLL routes, hooks, and injection."
                        .into(),
            },
            AntiCheatClass::OnlinePvE => {
                if dll_opt_in {
                    RouteDecision::AllowedWithNote {
                        note: "Online PvE: opt-in DLL route enabled; session-cleaned on exit."
                            .into(),
                    }
                } else {
                    RouteDecision::RequiresOptIn {
                        reason: "Online PvE requires explicit user opt-in for DLL routes.".into(),
                    }
                }
            }
            AntiCheatClass::Offline => RouteDecision::AllowedWithNote {
                note: "Offline game: session-cleaned DLL route allowed; removed on exit.".into(),
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

pub fn detect_anti_cheat_markers(install_dir: &Path) -> AntiCheatDetection {
    if !install_dir.exists() {
        return AntiCheatDetection {
            class: AntiCheatClass::Unknown,
            markers: Vec::new(),
        };
    }
    let mut found = Vec::new();
    let mut visited = 0usize;
    let mut unreadable = false;
    walk_markers(install_dir, 0, &mut visited, &mut unreadable, &mut found);
    found.sort();
    found.dedup();
    let class = if !found.is_empty() {
        AntiCheatClass::CompetitivePvP
    } else if unreadable {
        AntiCheatClass::Unknown
    } else {
        AntiCheatClass::Offline
    };
    AntiCheatDetection {
        class,
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
    fn competitive_and_unknown_forbid_file_and_dll_routes() {
        for class in [AntiCheatClass::CompetitivePvP, AntiCheatClass::Unknown] {
            assert!(evaluate_route(class, RouteKind::DriverCacheScan, false, false).is_permitted());
            assert!(evaluate_route(class, RouteKind::OfflinePsoWarm, false, false).is_permitted());
            assert!(!evaluate_route(class, RouteKind::FileRoute, false, true).is_permitted());
            assert!(!evaluate_route(class, RouteKind::DllRoute, false, true).is_permitted());
        }
    }

    #[test]
    fn online_pve_allows_file_and_requires_opt_in_for_dll() {
        assert!(
            evaluate_route(
                AntiCheatClass::OnlinePvE,
                RouteKind::FileRoute,
                false,
                false
            )
            .is_permitted()
        );
        assert!(matches!(
            evaluate_route(AntiCheatClass::OnlinePvE, RouteKind::DllRoute, false, false),
            RouteDecision::RequiresOptIn { .. }
        ));
        assert!(
            evaluate_route(AntiCheatClass::OnlinePvE, RouteKind::DllRoute, false, true)
                .is_permitted()
        );
        assert!(
            !evaluate_route(AntiCheatClass::OnlinePvE, RouteKind::DllRoute, true, true)
                .is_permitted()
        );
    }

    #[test]
    fn offline_allows_routes_with_note_when_not_running() {
        assert!(matches!(
            evaluate_route(AntiCheatClass::Offline, RouteKind::DllRoute, false, false),
            RouteDecision::AllowedWithNote { .. }
        ));
        assert!(
            !evaluate_route(AntiCheatClass::Offline, RouteKind::FileRoute, true, false)
                .is_permitted()
        );
    }

    #[test]
    fn detects_eac_marker_in_temp_dir() {
        let root = std::env::temp_dir().join(format!("exo-shaders-ac-test-{}", std::process::id()));
        let eac = root.join("EasyAntiCheat");
        let _ = fs::create_dir_all(&eac);
        let det = detect_anti_cheat_markers(&root);
        assert_eq!(det.class, AntiCheatClass::CompetitivePvP);
        assert!(det.markers.iter().any(|m| m == "EasyAntiCheat"));
        let _ = fs::remove_dir_all(&root);
    }
}
