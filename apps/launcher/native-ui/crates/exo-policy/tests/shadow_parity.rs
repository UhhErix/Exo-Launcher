use exo_policy::anti_cheat::{
    AntiCheatClass, Change, GameDescriptor, SteamWord, StoreKind, allows, by_title, classify,
    decide, dll_policy_for, for_store, known_game, refused_argument, wire_class,
};
use exo_policy::deletion_gate::{DeletionPolicy, refusal};
use exo_policy::network_destinations::is_listed;
use exo_policy::player_data::{lookup, ui_pref_syncs};
use exo_policy::process_termination::{ProcessNode, is_anti_cheat, plan_tree};
use serde_json::Value;
use std::path::PathBuf;

fn load_golden() -> Value {
    let manifest_dir = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let fixture = manifest_dir
        .join("tests")
        .join("fixtures")
        .join("exo-policy-golden.json");
    let raw = std::fs::read_to_string(&fixture)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", fixture.display()));
    serde_json::from_str(&raw).expect("valid golden json")
}

#[test]
fn rust_exo_policy_matches_shared_csharp_golden_vectors_100_percent() {
    let golden = load_golden();

    for row in golden["processTermination"].as_array().unwrap() {
        let name = row["name"].as_str();
        let expected = row["isAntiCheat"].as_bool().unwrap();
        assert_eq!(is_anti_cheat(name), expected, "processTermination {name:?}");
    }

    for tree in golden["processTrees"].as_array().unwrap() {
        let root = ProcessNode {
            pid: tree["root"]["pid"].as_u64().unwrap() as u32,
            name: tree["root"]["name"].as_str().map(str::to_owned),
        };
        let desc: Vec<ProcessNode> = tree["descendants"]
            .as_array()
            .unwrap()
            .iter()
            .map(|d| ProcessNode {
                pid: d["pid"].as_u64().unwrap() as u32,
                name: d["name"].as_str().map(str::to_owned),
            })
            .collect();
        let plan = plan_tree(&root, &desc);
        let expected_kill: Vec<u32> = tree["expectedKill"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| v.as_u64().unwrap() as u32)
            .collect();
        let expected_refused: Vec<u32> = tree["expectedRefused"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| v.as_u64().unwrap() as u32)
            .collect();
        assert_eq!(plan.kill, expected_kill);
        assert_eq!(plan.refused, expected_refused);
    }

    for row in golden["networkHosts"].as_array().unwrap() {
        let host = row["host"].as_str().unwrap();
        let expected = row["listed"].as_bool().unwrap();
        assert_eq!(is_listed(host), expected, "networkHost {host}");
    }

    for row in golden["playerData"].as_array().unwrap() {
        let path = row["path"].as_str().unwrap();
        let expected_matched = row["matched"].as_bool().unwrap();
        let expected_local = row["ofThisMachine"].as_bool().unwrap();
        let actual = lookup(path);
        assert_eq!(actual.is_some(), expected_matched, "playerData {path}");
        if let Some(file) = actual {
            assert_eq!(file.of_this_machine, expected_local, "ofThisMachine {path}");
        }
    }

    for row in golden["uiPrefs"].as_array().unwrap() {
        let key = row["key"].as_str().unwrap();
        let expected = row["syncs"].as_bool().unwrap();
        assert_eq!(ui_pref_syncs(key), expected, "uiPref {key}");
    }

    let policy = DeletionPolicy {
        kept_roots: vec![PathBuf::from(r"C:\Users\Test\Documents\Exo\Backups")],
        exo_data: Some(PathBuf::from(r"C:\Users\Test\AppData\Local\ExoLauncher")),
        shader_roots: vec![PathBuf::from(r"C:\Users\Test\AppData\Local\NVIDIA")],
        running_processes: Some(vec!["steam".into(), "discord".into()]),
        shader_busy: false,
    };

    for row in golden["deletionGate"].as_array().unwrap() {
        let path = row["path"].as_str().unwrap();
        let clean = row["clean"].as_bool().unwrap();
        let owners: Vec<&str> = row["owners"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| v.as_str().unwrap())
            .collect();
        let allow_browser = row["allowBrowser"].as_bool().unwrap();
        let expected_allowed = row["allowed"].as_bool().unwrap();
        let actual_allowed = refusal(path, clean, &owners, &policy, allow_browser).is_none();
        assert_eq!(actual_allowed, expected_allowed, "deletionGate {path}");
    }

    for row in golden["antiCheat"].as_array().unwrap() {
        let id = row["id"].as_str().unwrap().to_owned();
        let title = row["title"].as_str().unwrap().to_owned();
        let store = match row["store"].as_str().unwrap() {
            "riot" => StoreKind::Riot,
            "steam" => StoreKind::Steam,
            _ => StoreKind::Local,
        };
        let install_found = row["installFound"].as_bool().unwrap();
        let disk = row["disk"].as_str();
        let steam = match row["steam"].as_str().unwrap() {
            "none" => SteamWord::None,
            "vac" => SteamWord::Vac,
            _ => SteamWord::NotSteam,
        };
        let game = GameDescriptor {
            id: id.clone(),
            title: title.clone(),
            store,
            path: None,
            launch_target: None,
        };
        let answer = decide(
            for_store(store),
            known_game(&id),
            install_found,
            disk,
            None,
            steam,
            by_title(&title),
            false,
        );
        assert_eq!(
            serde_json::to_value(answer.kind).unwrap(),
            row["expectedKind"],
            "kind for {id}"
        );
        let cls = classify(&game, &answer);
        let expected_effective: AntiCheatClass =
            serde_json::from_value(row["expectedClass"].clone()).unwrap();
        assert_eq!(cls.effective(), expected_effective, "class for {id}");
        assert_eq!(
            cls.allows_exo_flow(),
            row["allowsExoFlow"].as_bool().unwrap(),
            "allowsExoFlow for {id}"
        );
        assert_eq!(
            wire_class(&game, Some(&answer)),
            row["expectedWireClass"].as_str().unwrap(),
            "wireClass for {id}"
        );
        assert_eq!(
            serde_json::to_value(dll_policy_for(cls, &answer)).unwrap(),
            row["expectedDllPolicy"],
            "dllPolicy for {id}"
        );
        assert_eq!(
            allows(&game, &answer, Change::GameSettingsFile, None),
            row["allowsSettingsFile"].as_bool().unwrap(),
            "allowsSettingsFile for {id}"
        );
        assert_eq!(
            allows(&game, &answer, Change::FrameGenMod, None),
            row["allowsFrameGenMod"].as_bool().unwrap(),
            "allowsFrameGenMod for {id}"
        );
    }

    for row in golden["refusedArguments"].as_array().unwrap() {
        let args = row["args"].as_str().unwrap();
        let expected = row["refused"].as_str().map(str::to_owned);
        assert_eq!(refused_argument(args), expected, "refusedArg {args}");
    }
}
