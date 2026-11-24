use crate::config::BridgeConfig;
use exo_upscale::policy::ModActionKind;
use exo_upscale::session::{OrphanRecoveryReport, SessionJournal, SessionMods};
use std::fs;
use std::path::{Path, PathBuf};

pub const BRIDGE_CONFIG_FILENAME: &str = "exo-fg-bridge.json";
pub const BRIDGE_DLL_FILENAME: &str = "exo_fg_bridge.dll";

pub fn deploy_bridge_session(
    session_mods: &SessionMods,
    session_id: &str,
    owner_pid: u32,
    config: &BridgeConfig,
    bridge_dll_source: &Path,
    game_bin_dir: &Path,
    stage_dir: &Path,
) -> Result<SessionJournal, String> {
    if let Some(reason) = config.denial_reason() {
        return Err(format!(
            "exo-fg-bridge blocked by AntiCheatClass policy: {reason}"
        ));
    }
    if !bridge_dll_source.exists() {
        return Err(format!(
            "bridge binary missing: {}",
            bridge_dll_source.display()
        ));
    }

    fs::create_dir_all(stage_dir).map_err(|e| format!("create bridge stage dir: {e}"))?;
    let staged_cfg = stage_dir.join(format!("{session_id}-{BRIDGE_CONFIG_FILENAME}"));
    let json = config.to_json_pretty()?;
    fs::write(&staged_cfg, json.as_bytes()).map_err(|e| format!("write staged config: {e}"))?;

    let placements: Vec<(PathBuf, PathBuf)> = vec![
        (
            bridge_dll_source.to_path_buf(),
            game_bin_dir.join(BRIDGE_DLL_FILENAME),
        ),
        (staged_cfg, game_bin_dir.join(BRIDGE_CONFIG_FILENAME)),
    ];

    session_mods.begin_session(
        session_id,
        &config.game_id,
        owner_pid,
        config.policy_context(),
        ModActionKind::FgBridgeProxy,
        &placements,
    )
}

pub fn end_bridge_session(
    session_mods: &SessionMods,
    session_id: &str,
) -> Result<OrphanRecoveryReport, String> {
    session_mods.end_session(session_id)
}

pub fn recover_orphaned_bridge_sessions(session_mods: &SessionMods) -> OrphanRecoveryReport {
    session_mods.recover_orphaned_sessions()
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::anticheat::AntiCheatClass;
    use std::process::Command;

    #[test]
    fn transactional_bridge_deploy_cleans_up_100pct_after_terminate_process() {
        let root = std::env::temp_dir().join(format!("exo-fgb-journal-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let game_dir = root.join("sekiro");
        let stage_dir = root.join("stage");
        fs::create_dir_all(&game_dir).unwrap();
        fs::create_dir_all(&stage_dir).unwrap();

        let fake_bridge = stage_dir.join("exo_fg_bridge.dll");
        fs::write(&fake_bridge, b"EXO-FG-BRIDGE-MIT-DLL").unwrap();

        let cfg = BridgeConfig {
            game_id: "steam:814380".into(),
            game_title: "Sekiro: Shadows Die Twice".into(),
            anti_cheat_class: AntiCheatClass::OfflineAntiCheat,
            has_anti_cheat: false,
            ..Default::default()
        };

        let mut child = Command::new("cmd")
            .args(["/C", "ping -n 30 127.0.0.1 > nul"])
            .spawn()
            .expect("spawn dummy child");

        let mods = SessionMods::new(root.join("sessions"), root.join("backups")).unwrap();
        let j = deploy_bridge_session(
            &mods,
            "fg-crash-1",
            child.id(),
            &cfg,
            &fake_bridge,
            &game_dir,
            &stage_dir,
        )
        .expect("deploy bridge session");
        assert_eq!(j.placed_files.len(), 2);
        assert!(game_dir.join(BRIDGE_DLL_FILENAME).exists());
        assert!(game_dir.join(BRIDGE_CONFIG_FILENAME).exists());

        child.kill().expect("kill child");
        let _ = child.wait();

        let rec = recover_orphaned_bridge_sessions(&mods);
        assert_eq!(rec.journals_recovered, 1);
        assert_eq!(rec.files_removed, 2);
        assert!(rec.errors.is_empty());
        assert!(!game_dir.join(BRIDGE_DLL_FILENAME).exists());
        assert!(!game_dir.join(BRIDGE_CONFIG_FILENAME).exists());

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn transactional_bridge_deploy_refuses_competitive_and_unknown_games() {
        let root = std::env::temp_dir().join(format!("exo-fgb-deny-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let game_dir = root.join("game");
        let stage_dir = root.join("stage");
        fs::create_dir_all(&game_dir).unwrap();
        fs::create_dir_all(&stage_dir).unwrap();
        let fake_bridge = stage_dir.join("exo_fg_bridge.dll");
        fs::write(&fake_bridge, b"EXO-FG-BRIDGE-MIT-DLL").unwrap();
        let mods = SessionMods::new(root.join("sessions"), root.join("backups")).unwrap();

        for ac in [
            AntiCheatClass::CompetitivePvpOnline,
            AntiCheatClass::Unknown,
        ] {
            let cfg = BridgeConfig {
                game_id: "steam:730".into(),
                game_title: "Blocked Game".into(),
                anti_cheat_class: ac,
                has_anti_cheat: true,
                ban_risk_confirmed: true,
                official_offline_mode: true,
                ..Default::default()
            };
            let err = deploy_bridge_session(
                &mods,
                "blocked",
                std::process::id(),
                &cfg,
                &fake_bridge,
                &game_dir,
                &stage_dir,
            )
            .unwrap_err();
            assert!(err.contains("blocked by AntiCheatClass policy"));
            assert!(!game_dir.join(BRIDGE_DLL_FILENAME).exists());
        }
        let _ = fs::remove_dir_all(&root);
    }
}
