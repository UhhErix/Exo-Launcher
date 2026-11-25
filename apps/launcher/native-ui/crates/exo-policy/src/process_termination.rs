pub use exo_rpc::anticheat::{
    ANTI_CHEAT_EXACT_NAMES, ANTI_CHEAT_PREFIXES, ANTI_CHEAT_SUBSTRINGS, ANTI_CHEAT_SUFFIXES,
    is_anti_cheat_process,
};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessNode {
    pub pid: u32,
    pub name: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TreeKillPlan {
    pub kill: Vec<u32>,
    pub refused: Vec<u32>,
}

pub fn is_anti_cheat(process_name: Option<&str>) -> bool {
    process_name.is_some_and(is_anti_cheat_process)
}

pub fn plan_tree(root: &ProcessNode, descendants_child_first: &[ProcessNode]) -> TreeKillPlan {
    if is_anti_cheat(root.name.as_deref()) {
        let mut refused = Vec::with_capacity(1 + descendants_child_first.len());
        refused.push(root.pid);
        refused.extend(descendants_child_first.iter().map(|d| d.pid));
        return TreeKillPlan {
            kill: Vec::new(),
            refused,
        };
    }

    let mut kill = Vec::with_capacity(1 + descendants_child_first.len());
    let mut refused = Vec::new();
    for child in descendants_child_first {
        if is_anti_cheat(child.name.as_deref()) {
            refused.push(child.pid);
        } else {
            kill.push(child.pid);
        }
    }
    kill.push(root.pid);
    TreeKillPlan { kill, refused }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn detects_all_anti_cheat_names_prefixes_and_launcher_suffixes() {
        for &name in &[
            "vgc",
            "VGK.EXE",
            "EasyAntiCheat_EOS.exe",
            "BEService_x64",
            "EAAntiCheat.GameService.exe",
            "FortniteClient-Win64-Shipping_BE.exe",
            "Game_EAC",
            "Game_EAC_EOS.exe",
            "SGuard64.exe",
            "GameMon64.des",
        ] {
            assert!(is_anti_cheat(Some(name)), "expected anti-cheat for {name}");
        }
        for name in [
            None,
            Some(""),
            Some("   "),
            Some("notepad.exe"),
            Some("sekiro.exe"),
        ] {
            assert!(!is_anti_cheat(name), "unexpected anti-cheat for {name:?}");
        }
    }

    #[test]
    fn anti_cheat_root_refuses_entire_tree() {
        let root = ProcessNode {
            pid: 100,
            name: Some("EasyAntiCheat_EOS.exe".into()),
        };
        let children = [
            ProcessNode {
                pid: 101,
                name: Some("child.exe".into()),
            },
            ProcessNode {
                pid: 102,
                name: Some("helper.exe".into()),
            },
        ];
        let plan = plan_tree(&root, &children);
        assert!(plan.kill.is_empty());
        assert_eq!(plan.refused, vec![100, 101, 102]);
    }

    #[test]
    fn non_anti_cheat_root_skips_anti_cheat_children_and_kills_root_last() {
        let root = ProcessNode {
            pid: 200,
            name: Some("game.exe".into()),
        };
        let children = [
            ProcessNode {
                pid: 201,
                name: Some("worker.exe".into()),
            },
            ProcessNode {
                pid: 202,
                name: Some("BEService.exe".into()),
            },
            ProcessNode {
                pid: 203,
                name: Some("audio.exe".into()),
            },
        ];
        let plan = plan_tree(&root, &children);
        assert_eq!(plan.kill, vec![201, 203, 200]);
        assert_eq!(plan.refused, vec![202]);
    }
}
