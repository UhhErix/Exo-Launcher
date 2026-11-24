use crate::catalog::atomic_replace_file;
use crate::policy::{ModActionKind, PolicyContext, allows_action};
use crate::verify::{Vendor, hash_file_blake3, verify_vendor_dll};
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupRecord {
    pub game_id: String,
    pub target_path: String,
    pub backup_path: String,
    pub original_blake3: String,
    pub replaced_blake3: String,
    pub created_unix_secs: u64,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupLedger {
    pub records: Vec<BackupRecord>,
}

impl BackupLedger {
    pub fn load(backup_root: &Path) -> Result<Self, String> {
        let p = backup_root.join("ledger.json");
        if !p.exists() {
            return Ok(Self::default());
        }
        let raw = fs::read_to_string(&p).map_err(|e| format!("read ledger: {e}"))?;
        serde_json::from_str(&raw).map_err(|e| format!("parse ledger: {e}"))
    }

    pub fn save(&self, backup_root: &Path) -> Result<(), String> {
        fs::create_dir_all(backup_root).map_err(|e| format!("create backup root: {e}"))?;
        let p = backup_root.join("ledger.json");
        let tmp = backup_root.join("ledger.json.tmp");
        let json =
            serde_json::to_string_pretty(self).map_err(|e| format!("serialize ledger: {e}"))?;
        fs::write(&tmp, json.as_bytes()).map_err(|e| format!("write temp ledger: {e}"))?;
        atomic_replace_file(&tmp, &p)
    }
}

pub fn atomic_backup(target_dll: &Path, backup_root: &Path) -> Result<(PathBuf, String), String> {
    let orig_hash = hash_file_blake3(target_dll).map_err(|e| format!("hash original: {e}"))?;
    let file_name = target_dll
        .file_name()
        .and_then(|n| n.to_str())
        .ok_or_else(|| "invalid target dll name".to_string())?;
    let dir = backup_root.join(&orig_hash);
    fs::create_dir_all(&dir).map_err(|e| format!("create backup dir: {e}"))?;
    let backup_path = dir.join(file_name);

    if backup_path.exists() {
        let existing_hash = hash_file_blake3(&backup_path).map_err(|e| e.to_string())?;
        if existing_hash.eq_ignore_ascii_case(&orig_hash) {
            return Ok((backup_path, orig_hash));
        }
    }

    let tmp = dir.join(format!("{file_name}.bak.tmp"));
    fs::copy(target_dll, &tmp).map_err(|e| format!("copy backup: {e}"))?;
    let tmp_hash = hash_file_blake3(&tmp).map_err(|e| format!("verify backup hash: {e}"))?;
    if !tmp_hash.eq_ignore_ascii_case(&orig_hash) {
        let _ = fs::remove_file(&tmp);
        return Err(
            "backup verification hash mismatch; aborting before modifying game file".into(),
        );
    }
    atomic_replace_file(&tmp, &backup_path)?;
    Ok((backup_path, orig_hash))
}

pub fn atomic_swap_verified(
    game_id: &str,
    policy_ctx: PolicyContext,
    target_dll: &Path,
    replacement_dll: &Path,
    vendor: Vendor,
    backup_root: &Path,
) -> Result<BackupRecord, String> {
    if !allows_action(policy_ctx, ModActionKind::SignedUpscalerSwap) {
        return Err("anti-cheat policy denied signed DLL swap for this game class".into());
    }
    let verified = verify_vendor_dll(replacement_dll, vendor, None)
        .map_err(|e| format!("replacement DLL verification failed: {e}"))?;
    atomic_swap_unchecked_bytes(
        game_id,
        target_dll,
        replacement_dll,
        &verified.blake3,
        backup_root,
    )
}

pub(crate) fn atomic_swap_unchecked_bytes(
    game_id: &str,
    target_dll: &Path,
    replacement_dll: &Path,
    expected_replacement_blake3: &str,
    backup_root: &Path,
) -> Result<BackupRecord, String> {
    let (backup_path, original_blake3) = atomic_backup(target_dll, backup_root)?;
    let parent = target_dll
        .parent()
        .ok_or_else(|| "target dll has no parent directory".to_string())?;
    let file_name = target_dll
        .file_name()
        .and_then(|n| n.to_str())
        .ok_or_else(|| "invalid target dll name".to_string())?;
    let staged = parent.join(format!(".{file_name}.exo-stage.tmp"));
    fs::copy(replacement_dll, &staged).map_err(|e| format!("stage replacement: {e}"))?;
    let staged_hash = hash_file_blake3(&staged).map_err(|e| e.to_string())?;
    if !staged_hash.eq_ignore_ascii_case(expected_replacement_blake3) {
        let _ = fs::remove_file(&staged);
        return Err("staged replacement hash mismatch; aborting swap".into());
    }
    atomic_replace_file(&staged, target_dll)?;

    let after_hash = hash_file_blake3(target_dll).map_err(|e| e.to_string())?;
    if !after_hash.eq_ignore_ascii_case(expected_replacement_blake3) {
        let _ = restore_from_backup(target_dll, &backup_path, &original_blake3);
        return Err("post-swap target verification failed; original restored".into());
    }

    let rec = BackupRecord {
        game_id: game_id.to_string(),
        target_path: target_dll.display().to_string(),
        backup_path: backup_path.display().to_string(),
        original_blake3,
        replaced_blake3: after_hash,
        created_unix_secs: SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_secs())
            .unwrap_or(0),
    };
    let mut ledger = BackupLedger::load(backup_root)?;
    ledger.records.retain(|r| r.target_path != rec.target_path);
    ledger.records.push(rec.clone());
    ledger.save(backup_root)?;
    Ok(rec)
}

pub fn restore_from_backup(
    target_dll: &Path,
    backup_path: &Path,
    expected_original_blake3: &str,
) -> Result<(), String> {
    if !backup_path.exists() {
        return Err(format!("backup missing: {}", backup_path.display()));
    }
    let b_hash = hash_file_blake3(backup_path).map_err(|e| format!("hash backup: {e}"))?;
    if !b_hash.eq_ignore_ascii_case(expected_original_blake3) {
        return Err("backup file failed BLAKE3 integrity check before rollback".into());
    }
    let parent = target_dll
        .parent()
        .ok_or_else(|| "target has no parent dir".to_string())?;
    fs::create_dir_all(parent).map_err(|e| format!("create target parent: {e}"))?;
    let staged = parent.join(".exo-restore.tmp");
    fs::copy(backup_path, &staged).map_err(|e| format!("copy restore stage: {e}"))?;
    atomic_replace_file(&staged, target_dll)?;
    let restored_hash = hash_file_blake3(target_dll).map_err(|e| e.to_string())?;
    if !restored_hash.eq_ignore_ascii_case(expected_original_blake3) {
        return Err("restored file hash mismatch".into());
    }
    Ok(())
}

pub fn rollback_target(target_dll: &Path, backup_root: &Path) -> Result<bool, String> {
    let mut ledger = BackupLedger::load(backup_root)?;
    let key = target_dll.display().to_string();
    let Some(pos) = ledger.records.iter().position(|r| r.target_path == key) else {
        return Ok(false);
    };
    let rec = ledger.records[pos].clone();
    restore_from_backup(
        target_dll,
        Path::new(&rec.backup_path),
        &rec.original_blake3,
    )?;
    ledger.records.remove(pos);
    ledger.save(backup_root)?;
    Ok(true)
}

pub const fn should_auto_rollback(exit_code: i32, runtime_secs: f64) -> bool {
    exit_code != 0 && runtime_secs <= 15.0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn atomic_backup_swap_and_rollback_restores_original_bytes_identically() {
        let root = std::env::temp_dir().join(format!("exo-upscale-swap-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let game_dir = root.join("game");
        let backup_root = root.join("backups");
        fs::create_dir_all(&game_dir).unwrap();

        let target = game_dir.join("nvngx_dlss.dll");
        let replacement = root.join("new_nvngx_dlss.dll");
        let orig_bytes = b"ORIGINAL-DLSS-2.4-BYTES-0123456789";
        let new_bytes = b"UPDATED-DLSS-310.2-BYTES-ABCDEFGHIJ";
        fs::write(&target, orig_bytes).unwrap();
        fs::write(&replacement, new_bytes).unwrap();

        let orig_hash = hash_file_blake3(&target).unwrap();
        let new_hash = hash_file_blake3(&replacement).unwrap();

        let rec = atomic_swap_unchecked_bytes(
            "steam:1091500",
            &target,
            &replacement,
            &new_hash,
            &backup_root,
        )
        .expect("atomic swap");
        assert_eq!(rec.original_blake3, orig_hash);
        assert_eq!(fs::read(&target).unwrap(), new_bytes);

        let rolled = rollback_target(&target, &backup_root).expect("rollback");
        assert!(rolled);
        assert_eq!(fs::read(&target).unwrap(), orig_bytes);
        assert_eq!(hash_file_blake3(&target).unwrap(), orig_hash);

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn policy_blocks_swap_for_competitive_pvp_and_unknown_classes() {
        let root = std::env::temp_dir().join(format!("exo-upscale-deny-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        fs::create_dir_all(&root).unwrap();
        let target = root.join("nvngx_dlss.dll");
        let repl = root.join("repl.dll");
        fs::write(&target, b"orig").unwrap();
        fs::write(&repl, b"repl").unwrap();

        for ctx in [
            PolicyContext::competitive_pvp(),
            PolicyContext::unknown(),
            PolicyContext::online_pve(false),
            PolicyContext::offline_anti_cheat(false),
        ] {
            let err = atomic_swap_verified(
                "steam:730",
                ctx,
                &target,
                &repl,
                Vendor::Nvidia,
                &root.join("backups"),
            )
            .unwrap_err();
            assert!(err.contains("anti-cheat policy denied"));
            assert_eq!(fs::read(&target).unwrap(), b"orig");
        }
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn regression_auto_rollback_triggers_on_nonzero_exit_within_15_seconds() {
        assert!(should_auto_rollback(1, 4.2));
        assert!(should_auto_rollback(-1073741819, 14.9));
        assert!(!should_auto_rollback(0, 3.0));
        assert!(!should_auto_rollback(1, 45.0));
    }
}
