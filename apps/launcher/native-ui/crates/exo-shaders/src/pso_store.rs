use crate::policy::{AntiCheatClass, RouteKind, evaluate_route};
pub use crate::pso_format::*;
use std::fs;
use std::path::Path;

fn file_blake3_hex(path: &Path) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|e| format!("read {} failed: {e}", path.display()))?;
    Ok(blake3::hash(&bytes).to_hex().to_string())
}

pub fn is_recorder_deployed(game_dir: &Path) -> bool {
    game_dir.join(RECORDER_CONFIG_NAME).exists() || game_dir.join(RECORDER_MANIFEST_NAME).exists()
}

pub fn deploy_recorder(
    game_dir: &Path,
    source_dll: &Path,
    config: &RecorderConfig,
    class: AntiCheatClass,
    game_running: bool,
) -> Result<RecorderManifest, String> {
    let decision = evaluate_route(class, RouteKind::RecorderProxy, game_running, true);
    if !decision.is_permitted() {
        return Err(match decision {
            crate::policy::RouteDecision::Denied { reason } => reason,
            _ => "Recorder deployment denied by policy.".into(),
        });
    }
    if !game_dir.is_dir() {
        return Err("Game directory does not exist.".into());
    }
    if !source_dll.is_file() {
        return Err("Recorder DLL binary is missing.".into());
    }

    let dst_dll = game_dir.join(RECORDER_DLL_NAME);
    let dst_cfg = game_dir.join(RECORDER_CONFIG_NAME);
    let dst_manifest = game_dir.join(RECORDER_MANIFEST_NAME);

    if dst_dll.exists() {
        let owned = fs::read_to_string(&dst_manifest)
            .ok()
            .and_then(|s| serde_json::from_str::<RecorderManifest>(&s).ok())
            .and_then(|m| {
                file_blake3_hex(&dst_dll)
                    .ok()
                    .map(|actual| actual == m.dll_blake3)
            })
            .unwrap_or(false);
        if !owned {
            return Err(
                "A non-Exo d3d12.dll already exists beside this game; refusing to overwrite."
                    .into(),
            );
        }
    }

    let dll_bytes = fs::read(source_dll).map_err(|e| format!("read recorder DLL failed: {e}"))?;
    let dll_hash = blake3::hash(&dll_bytes).to_hex().to_string();
    let cfg_bytes =
        serde_json::to_vec_pretty(config).map_err(|e| format!("serialize config failed: {e}"))?;
    let cfg_hash = blake3::hash(&cfg_bytes).to_hex().to_string();

    let manifest = RecorderManifest {
        version: 1,
        game_id: config.game_id.clone(),
        dll_blake3: dll_hash,
        config_blake3: cfg_hash,
    };
    let manifest_bytes = serde_json::to_vec_pretty(&manifest)
        .map_err(|e| format!("serialize manifest failed: {e}"))?;

    fs::write(&dst_manifest, &manifest_bytes)
        .map_err(|e| format!("write recorder manifest failed: {e}"))?;
    if let Err(e) = fs::write(&dst_dll, &dll_bytes) {
        let _ = fs::remove_file(&dst_manifest);
        return Err(format!("write recorder DLL failed: {e}"));
    }
    if let Err(e) = fs::write(&dst_cfg, &cfg_bytes) {
        let _ = fs::remove_file(&dst_dll);
        let _ = fs::remove_file(&dst_manifest);
        return Err(format!("write recorder config failed: {e}"));
    }
    Ok(manifest)
}

pub fn remove_recorder(game_dir: &Path, game_running: bool) -> Result<bool, String> {
    if game_running {
        return Err("Game is currently running; remove the recorder after exit.".into());
    }
    let dst_dll = game_dir.join(RECORDER_DLL_NAME);
    let dst_cfg = game_dir.join(RECORDER_CONFIG_NAME);
    let dst_manifest = game_dir.join(RECORDER_MANIFEST_NAME);

    let mut removed_any = false;
    let manifest = fs::read_to_string(&dst_manifest)
        .ok()
        .and_then(|s| serde_json::from_str::<RecorderManifest>(&s).ok());

    if dst_dll.exists()
        && let Some(ref m) = manifest
        && let Ok(actual) = file_blake3_hex(&dst_dll)
        && actual == m.dll_blake3
    {
        fs::remove_file(&dst_dll).map_err(|e| format!("remove recorder DLL failed: {e}"))?;
        removed_any = true;
    }
    if dst_cfg.exists() {
        fs::remove_file(&dst_cfg).map_err(|e| format!("remove recorder config failed: {e}"))?;
        removed_any = true;
    }
    if dst_manifest.exists() {
        fs::remove_file(&dst_manifest)
            .map_err(|e| format!("remove recorder manifest failed: {e}"))?;
        removed_any = true;
    }
    Ok(removed_any)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs::OpenOptions;
    use std::io::Write;
    use std::path::PathBuf;

    fn temp_dir(tag: &str) -> PathBuf {
        let p = std::env::temp_dir().join(format!(
            "exo-pso-store-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        let _ = fs::create_dir_all(&p);
        p
    }

    #[test]
    fn exopso_round_trip_dedups_and_tolerates_truncated_tail() {
        let dir = temp_dir("exopso");
        let path = dir.join("game.exopso");
        let rs = vec![1u8, 2, 3, 4];
        let cs = vec![9u8; 64];
        let key = compute_pso_key(&rs, &cs, 0);
        let rec = RecordedPsoRecord {
            key_hex: key.clone(),
            kind: RecordedPsoKind::Compute,
            root_sig_blob: rs,
            vs: Vec::new(),
            ps: Vec::new(),
            ds: Vec::new(),
            hs: Vec::new(),
            gs: Vec::new(),
            cs,
            graphics_state: None,
            node_mask: 0,
        };
        append_pso_record(&path, &rec).unwrap();
        append_pso_record(&path, &rec).unwrap();
        let mut f = OpenOptions::new().append(true).open(&path).unwrap();
        f.write_all(&[0xFF, 0x00, 0x00, 0x00, 1, 2, 3]).unwrap();
        drop(f);

        let loaded = read_pso_records(&path).unwrap();
        assert_eq!(loaded.len(), 1);
        assert_eq!(loaded[0].key_hex, key);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn exoplib_checks_vendor_device_and_driver_version() {
        let dir = temp_dir("exoplib");
        let path = dir.join("game.exoplib");
        let payload = vec![0xABu8; 1024];
        write_pipeline_library(&path, 0x10DE, 0x2484, "32.0.16.2135", 12, &payload).unwrap();

        let hit = read_pipeline_library_if_compatible(&path, 0x10DE, 0x2484, "32.0.16.2135")
            .unwrap()
            .expect("matching driver hits");
        assert_eq!(hit.0.pso_count, 12);
        assert_eq!(hit.1, payload);

        assert!(
            read_pipeline_library_if_compatible(&path, 0x10DE, 0x2484, "32.0.16.9999")
                .unwrap()
                .is_none()
        );
        assert!(
            read_pipeline_library_if_compatible(&path, 0x1002, 0x2484, "32.0.16.2135")
                .unwrap()
                .is_none()
        );
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn deploy_and_remove_enforce_anticheat_running_and_foreign_dll_safety() {
        let dir = temp_dir("deploy");
        let game_dir = dir.join("game");
        let _ = fs::create_dir_all(&game_dir);
        let src_dll = dir.join(RECORDER_SOURCE_DLL_NAME);
        fs::write(&src_dll, b"EXO_FAKE_D3D12_RECORDER_BYTES").unwrap();

        let cfg = RecorderConfig {
            version: 1,
            enabled: true,
            game_id: "steam:1343240".into(),
            mode: RecorderMode::RecordAndWarm,
            record_path: dir.join("rec.exopso").display().to_string(),
            library_path: dir.join("lib.exoplib").display().to_string(),
            expected_adapter_vendor: 0x10DE,
            expected_adapter_device: 0x2484,
            expected_driver_version: "32.0.16.2135".into(),
            max_recorded_psos: DEFAULT_MAX_RECORDED_PSOS,
            stats_path: None,
        };

        for denied in [
            AntiCheatClass::CompetitivePvpOnline,
            AntiCheatClass::OnlineNonCompetitivePve,
            AntiCheatClass::Unknown,
        ] {
            assert!(deploy_recorder(&game_dir, &src_dll, &cfg, denied, false).is_err());
        }
        assert!(
            deploy_recorder(
                &game_dir,
                &src_dll,
                &cfg,
                AntiCheatClass::OfflineAntiCheat,
                true
            )
            .is_err()
        );

        let foreign = game_dir.join(RECORDER_DLL_NAME);
        fs::write(&foreign, b"OPTISCALER_OR_GAME_OWN_D3D12").unwrap();
        assert!(
            deploy_recorder(
                &game_dir,
                &src_dll,
                &cfg,
                AntiCheatClass::OfflineAntiCheat,
                false
            )
            .is_err()
        );
        fs::remove_file(&foreign).unwrap();

        let manifest = deploy_recorder(
            &game_dir,
            &src_dll,
            &cfg,
            AntiCheatClass::OfflineAntiCheat,
            false,
        )
        .expect("offline closed game deploys cleanly");
        assert!(is_recorder_deployed(&game_dir));
        assert!(game_dir.join(RECORDER_DLL_NAME).exists());
        assert!(game_dir.join(RECORDER_CONFIG_NAME).exists());
        assert_eq!(manifest.game_id, "steam:1343240");

        assert!(remove_recorder(&game_dir, true).is_err());
        assert!(remove_recorder(&game_dir, false).unwrap());
        assert!(!is_recorder_deployed(&game_dir));
        assert!(!game_dir.join(RECORDER_DLL_NAME).exists());
        assert!(!game_dir.join(RECORDER_CONFIG_NAME).exists());
        assert!(!game_dir.join(RECORDER_MANIFEST_NAME).exists());
        let _ = fs::remove_dir_all(&dir);
    }
}
