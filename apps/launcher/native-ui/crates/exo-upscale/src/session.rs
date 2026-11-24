use crate::catalog::atomic_replace_file;
use crate::policy::{ModActionKind, PolicyContext, allows_action};
use crate::swap::{atomic_backup, restore_from_backup};
use crate::verify::hash_file_blake3;
use serde::{Deserialize, Serialize};
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlacedFileRecord {
    pub path: String,
    pub blake3: String,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplacedFileRecord {
    pub target_path: String,
    pub backup_path: String,
    pub original_blake3: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SessionState {
    Preparing,
    Active,
    Cleaned,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionJournal {
    pub session_id: String,
    pub game_id: String,
    pub owner_pid: u32,
    pub started_unix_ms: u128,
    pub state: SessionState,
    pub placed_files: Vec<PlacedFileRecord>,
    pub replaced_files: Vec<ReplacedFileRecord>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OrphanRecoveryReport {
    pub journals_inspected: usize,
    pub journals_recovered: usize,
    pub active_skipped: usize,
    pub files_removed: usize,
    pub originals_restored: usize,
    pub errors: Vec<String>,
}

pub struct SessionMods {
    state_dir: PathBuf,
    backup_root: PathBuf,
}

impl SessionMods {
    pub fn new(
        state_dir: impl Into<PathBuf>,
        backup_root: impl Into<PathBuf>,
    ) -> Result<Self, String> {
        let state_dir = state_dir.into();
        let backup_root = backup_root.into();
        fs::create_dir_all(&state_dir).map_err(|e| format!("create session state dir: {e}"))?;
        fs::create_dir_all(&backup_root).map_err(|e| format!("create session backup dir: {e}"))?;
        Ok(Self {
            state_dir,
            backup_root,
        })
    }

    fn journal_path(&self, session_id: &str) -> PathBuf {
        self.state_dir.join(format!("session-{session_id}.json"))
    }

    fn write_journal(&self, journal: &SessionJournal) -> Result<(), String> {
        let dst = self.journal_path(&journal.session_id);
        let tmp = self
            .state_dir
            .join(format!("session-{}.json.tmp", journal.session_id));
        let json =
            serde_json::to_string_pretty(journal).map_err(|e| format!("serialize journal: {e}"))?;
        fs::write(&tmp, json.as_bytes()).map_err(|e| format!("write journal tmp: {e}"))?;
        atomic_replace_file(&tmp, &dst)
    }

    pub fn begin_session(
        &self,
        session_id: &str,
        game_id: &str,
        owner_pid: u32,
        policy_ctx: PolicyContext,
        action_kind: ModActionKind,
        placements: &[(PathBuf, PathBuf)],
    ) -> Result<SessionJournal, String> {
        if !allows_action(policy_ctx, action_kind) {
            return Err("anti-cheat policy denied transactional SessionMods deployment".into());
        }

        let started_unix_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map(|d| d.as_millis())
            .unwrap_or(0);
        let mut journal = SessionJournal {
            session_id: session_id.to_string(),
            game_id: game_id.to_string(),
            owner_pid,
            started_unix_ms,
            state: SessionState::Preparing,
            placed_files: Vec::new(),
            replaced_files: Vec::new(),
        };
        self.write_journal(&journal)?;

        for (src, dst) in placements {
            let src_hash = hash_file_blake3(src).map_err(|e| format!("hash staged source: {e}"))?;
            if dst.exists() {
                let (backup_path, orig_hash) = atomic_backup(dst, &self.backup_root)?;
                journal.replaced_files.push(ReplacedFileRecord {
                    target_path: dst.display().to_string(),
                    backup_path: backup_path.display().to_string(),
                    original_blake3: orig_hash,
                });
            } else {
                journal.placed_files.push(PlacedFileRecord {
                    path: dst.display().to_string(),
                    blake3: src_hash.clone(),
                });
            }
            self.write_journal(&journal)?;

            if let Some(parent) = dst.parent() {
                fs::create_dir_all(parent).map_err(|e| format!("create dst parent: {e}"))?;
            }
            let tmp = dst.with_extension("exo-session.tmp");
            if let Err(e) = fs::copy(src, &tmp)
                .map_err(|e| format!("copy session file: {e}"))
                .and_then(|_| atomic_replace_file(&tmp, dst))
            {
                let _ = cleanup_journal(&journal);
                let _ = fs::remove_file(self.journal_path(session_id));
                return Err(e);
            }
        }

        journal.state = SessionState::Active;
        self.write_journal(&journal)?;
        Ok(journal)
    }

    pub fn end_session(&self, session_id: &str) -> Result<OrphanRecoveryReport, String> {
        let path = self.journal_path(session_id);
        if !path.exists() {
            return Ok(OrphanRecoveryReport::default());
        }
        let raw = fs::read_to_string(&path).map_err(|e| format!("read journal: {e}"))?;
        let journal: SessionJournal =
            serde_json::from_str(&raw).map_err(|e| format!("parse journal: {e}"))?;
        let mut report = cleanup_journal(&journal);
        report.journals_inspected = 1;
        if report.errors.is_empty() {
            let _ = fs::remove_file(&path);
            report.journals_recovered = 1;
        }
        Ok(report)
    }

    pub fn recover_orphaned_sessions(&self) -> OrphanRecoveryReport {
        self.recover_orphaned_sessions_with(is_pid_alive)
    }

    pub fn recover_orphaned_sessions_with<F>(&self, pid_alive: F) -> OrphanRecoveryReport
    where
        F: Fn(u32) -> bool,
    {
        let mut total = OrphanRecoveryReport::default();
        let Ok(rd) = fs::read_dir(&self.state_dir) else {
            return total;
        };
        for entry in rd.flatten() {
            let path = entry.path();
            let Some(name) = path.file_name().and_then(|n| n.to_str()) else {
                continue;
            };
            if !name.starts_with("session-") || !name.ends_with(".json") {
                continue;
            }
            total.journals_inspected += 1;
            let Ok(raw) = fs::read_to_string(&path) else {
                total
                    .errors
                    .push(format!("failed to read {}", path.display()));
                continue;
            };
            let Ok(journal) = serde_json::from_str::<SessionJournal>(&raw) else {
                total
                    .errors
                    .push(format!("corrupt journal {}", path.display()));
                continue;
            };
            if journal.owner_pid != 0 && pid_alive(journal.owner_pid) {
                total.active_skipped += 1;
                continue;
            }
            let sub = cleanup_journal(&journal);
            total.files_removed += sub.files_removed;
            total.originals_restored += sub.originals_restored;
            total.errors.extend(sub.errors.clone());
            if sub.errors.is_empty() {
                let _ = fs::remove_file(&path);
                total.journals_recovered += 1;
            }
        }
        total
    }
}

fn cleanup_journal(journal: &SessionJournal) -> OrphanRecoveryReport {
    let mut rep = OrphanRecoveryReport::default();
    for placed in &journal.placed_files {
        let p = Path::new(&placed.path);
        if p.exists() {
            match fs::remove_file(p) {
                Ok(()) => rep.files_removed += 1,
                Err(e) => rep.errors.push(format!("remove {}: {e}", placed.path)),
            }
        }
    }
    for replaced in &journal.replaced_files {
        let target = Path::new(&replaced.target_path);
        let backup = Path::new(&replaced.backup_path);
        match restore_from_backup(target, backup, &replaced.original_blake3) {
            Ok(()) => rep.originals_restored += 1,
            Err(e) => rep
                .errors
                .push(format!("restore {}: {e}", replaced.target_path)),
        }
    }
    rep
}

#[cfg(windows)]
pub fn is_pid_alive(pid: u32) -> bool {
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW,
        TH32CS_SNAPPROCESS,
    };

    if pid == 0 {
        return false;
    }
    let Ok(snap) = (unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }) else {
        return false;
    };
    let mut entry = PROCESSENTRY32W {
        dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    let mut found = false;
    if unsafe { Process32FirstW(snap, &mut entry) }.is_ok() {
        loop {
            if entry.th32ProcessID == pid {
                found = true;
                break;
            }
            if unsafe { Process32NextW(snap, &mut entry) }.is_err() {
                break;
            }
        }
    }
    unsafe {
        let _ = CloseHandle(snap);
    }
    found
}

#[cfg(not(windows))]
pub fn is_pid_alive(pid: u32) -> bool {
    pid == std::process::id()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::Command;

    #[test]
    fn simulated_terminate_process_recovers_placed_and_replaced_files_byte_for_byte() {
        let root = std::env::temp_dir().join(format!("exo-upscale-sess-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let game_dir = root.join("game");
        let staged_dir = root.join("staged");
        fs::create_dir_all(&game_dir).unwrap();
        fs::create_dir_all(&staged_dir).unwrap();

        let existing_dll = game_dir.join("nvngx_dlss.dll");
        let orig_bytes = b"ORIGINAL-GAME-DLL-CONTENT-0001";
        fs::write(&existing_dll, orig_bytes).unwrap();
        let orig_hash = hash_file_blake3(&existing_dll).unwrap();

        let new_dlss = staged_dir.join("nvngx_dlss.dll");
        let new_proxy = staged_dir.join("exo_fg_bridge.dll");
        fs::write(&new_dlss, b"SESSION-SWAPPED-DLSS-310").unwrap();
        fs::write(&new_proxy, b"SESSION-PLACED-BRIDGE-MIT").unwrap();

        let proxy_target = game_dir.join("version.dll");

        let mut child = Command::new("cmd")
            .args(["/C", "ping -n 30 127.0.0.1 > nul"])
            .spawn()
            .expect("spawn dummy child");
        let child_pid = child.id();

        let mods = SessionMods::new(root.join("sessions"), root.join("backups")).unwrap();
        let journal = mods
            .begin_session(
                "crash-test-1",
                "steam:814380",
                child_pid,
                PolicyContext::no_anti_cheat(),
                ModActionKind::FgBridgeProxy,
                &[
                    (new_dlss, existing_dll.clone()),
                    (new_proxy, proxy_target.clone()),
                ],
            )
            .expect("begin session");

        assert_eq!(journal.state, SessionState::Active);
        assert!(proxy_target.exists());
        assert_eq!(
            fs::read(&existing_dll).unwrap(),
            b"SESSION-SWAPPED-DLSS-310"
        );

        let while_alive = mods.recover_orphaned_sessions();
        assert_eq!(while_alive.active_skipped, 1);
        assert_eq!(while_alive.journals_recovered, 0);
        assert!(proxy_target.exists());

        child.kill().expect("TerminateProcess on child");
        let _ = child.wait();

        let recovered = mods.recover_orphaned_sessions();
        assert_eq!(recovered.journals_inspected, 1);
        assert_eq!(recovered.journals_recovered, 1);
        assert_eq!(recovered.files_removed, 1);
        assert_eq!(recovered.originals_restored, 1);
        assert!(
            recovered.errors.is_empty(),
            "errors: {:?}",
            recovered.errors
        );
        assert!(!proxy_target.exists());
        assert_eq!(fs::read(&existing_dll).unwrap(), orig_bytes);
        assert_eq!(hash_file_blake3(&existing_dll).unwrap(), orig_hash);

        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn session_mods_refuses_competitive_pvp_and_unknown_classes() {
        let root = std::env::temp_dir().join(format!("exo-upscale-sdeny-{}", std::process::id()));
        let _ = fs::remove_dir_all(&root);
        let mods = SessionMods::new(root.join("sessions"), root.join("backups")).unwrap();
        let src = root.join("src.dll");
        let dst = root.join("dst.dll");
        fs::write(&src, b"dummy").unwrap();

        for ctx in [PolicyContext::competitive_pvp(), PolicyContext::unknown()] {
            let err = mods
                .begin_session(
                    "blocked",
                    "steam:730",
                    std::process::id(),
                    ctx,
                    ModActionKind::SignedUpscalerSwap,
                    &[(src.clone(), dst.clone())],
                )
                .unwrap_err();
            assert!(err.contains("anti-cheat policy denied"));
            assert!(!dst.exists());
        }
        let _ = fs::remove_dir_all(&root);
    }
}
