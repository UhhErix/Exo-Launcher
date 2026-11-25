use crate::cpu_sets::SavedCpuSet;
use crate::os::AutopilotOs;
use crate::probalance::RestrainedProcess;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

pub const SESSION_SERVICES: &[&str] = &["BITS", "DoSvc", "wuauserv", "WSearch"];
pub const MAX_PAUSE_DURATION_MS: u64 = 6 * 60 * 60 * 1000;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum ServicePauseAction {
    Paused,
    Stopped,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PausedServiceRecord {
    pub name: String,
    pub action: ServicePauseAction,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotJournal {
    pub active_session: bool,
    pub started_ms: u64,
    pub paused_bits_jobs: Vec<String>,
    pub paused_services: Vec<PausedServiceRecord>,
    pub previous_power_scheme: Option<String>,
    pub previous_mouse_accel: Option<[i32; 3]>,
    pub restrained_processes: Vec<RestrainedProcess>,
    pub corralled_cpu_sets: Vec<SavedCpuSet>,
    pub learned_offenders: Vec<String>,
}

impl AutopilotJournal {
    pub fn load(path: &Path) -> Self {
        let Ok(raw) = fs::read_to_string(path) else {
            return Self::default();
        };
        serde_json::from_str(&raw).unwrap_or_default()
    }

    pub fn save_atomic(&self, path: &Path) -> Result<(), String> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).map_err(|e| format!("create_dir_all: {e}"))?;
        }
        let tmp = PathBuf::from(format!("{}.tmp", path.display()));
        let json = serde_json::to_string_pretty(self).map_err(|e| format!("serialize: {e}"))?;
        fs::write(&tmp, json).map_err(|e| format!("write tmp: {e}"))?;
        if path.exists() {
            let _ = fs::remove_file(path);
        }
        fs::rename(&tmp, path).map_err(|e| format!("rename tmp: {e}"))?;
        Ok(())
    }

    pub fn learned_set(&self) -> HashSet<String> {
        self.learned_offenders.iter().cloned().collect()
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionPauseSnapshot {
    pub active: bool,
    pub paused_bits_jobs: usize,
    pub paused_services: Vec<String>,
}

#[derive(Debug, Default)]
pub struct SessionDownloadPause {
    active: bool,
    started_ms: u64,
    paused_bits_jobs: Vec<String>,
    paused_services: Vec<PausedServiceRecord>,
}

impl SessionDownloadPause {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn snapshot(&self) -> SessionPauseSnapshot {
        SessionPauseSnapshot {
            active: self.active,
            paused_bits_jobs: self.paused_bits_jobs.len(),
            paused_services: self
                .paused_services
                .iter()
                .map(|s| format!("{}:{:?}", s.name, s.action))
                .collect(),
        }
    }

    pub fn paused_bits_jobs(&self) -> &[String] {
        &self.paused_bits_jobs
    }

    pub fn paused_services(&self) -> &[PausedServiceRecord] {
        &self.paused_services
    }

    pub fn pause(&mut self, os: &dyn AutopilotOs, now_ms: u64) {
        if self.active {
            return;
        }
        self.active = true;
        self.started_ms = now_ms;
        self.paused_bits_jobs.clear();
        self.paused_services.clear();

        for job_id in os.list_active_bits_jobs() {
            if os.suspend_bits_job(&job_id) {
                self.paused_bits_jobs.push(job_id);
            }
        }

        for &svc in SESSION_SERVICES {
            let Some(snap) = os.query_service(svc) else {
                continue;
            };
            if !snap.running {
                continue;
            }
            if snap.can_pause && os.pause_service(svc) {
                self.paused_services.push(PausedServiceRecord {
                    name: svc.to_string(),
                    action: ServicePauseAction::Paused,
                });
            } else if snap.can_stop && os.stop_service(svc) {
                self.paused_services.push(PausedServiceRecord {
                    name: svc.to_string(),
                    action: ServicePauseAction::Stopped,
                });
            }
        }
    }

    pub fn check_watchdog(&mut self, os: &dyn AutopilotOs, now_ms: u64) -> bool {
        if self.active && now_ms.saturating_sub(self.started_ms) >= MAX_PAUSE_DURATION_MS {
            self.resume(os);
            true
        } else {
            false
        }
    }

    pub fn resume(&mut self, os: &dyn AutopilotOs) {
        for svc in self.paused_services.drain(..).rev() {
            match svc.action {
                ServicePauseAction::Paused => {
                    let _ = os.continue_service(&svc.name);
                }
                ServicePauseAction::Stopped => {
                    let _ = os.start_service(&svc.name);
                }
            }
        }
        for job_id in self.paused_bits_jobs.drain(..) {
            let _ = os.resume_bits_job(&job_id);
        }
        self.active = false;
        self.started_ms = 0;
    }
}

pub fn recover_from_journal(
    os: &dyn AutopilotOs,
    journal_path: &Path,
) -> (AutopilotJournal, usize) {
    let mut journal = AutopilotJournal::load(journal_path);
    if !journal.active_session {
        return (journal, 0);
    }

    let mut restored_items = 0usize;

    for saved in journal.corralled_cpu_sets.drain(..) {
        if os.set_default_cpu_sets(saved.pid, &saved.original_ids) {
            restored_items += 1;
        }
    }

    for proc_rec in journal.restrained_processes.drain(..) {
        if proc_rec.eco_qos_applied {
            let _ = os.set_eco_qos(proc_rec.pid, false);
        }
        if os.set_priority_class(proc_rec.pid, proc_rec.original_priority) {
            restored_items += 1;
        }
    }

    for svc in journal.paused_services.drain(..).rev() {
        let ok = match svc.action {
            ServicePauseAction::Paused => os.continue_service(&svc.name),
            ServicePauseAction::Stopped => os.start_service(&svc.name),
        };
        if ok {
            restored_items += 1;
        }
    }

    for job_id in journal.paused_bits_jobs.drain(..) {
        if os.resume_bits_job(&job_id) {
            restored_items += 1;
        }
    }

    if let Some(scheme) = journal.previous_power_scheme.take()
        && os.set_power_scheme(&scheme)
    {
        restored_items += 1;
    }

    if let Some(mouse) = journal.previous_mouse_accel.take()
        && os.write_mouse_accel(mouse)
    {
        restored_items += 1;
    }

    journal.active_session = false;
    journal.started_ms = 0;
    let _ = journal.save_atomic(journal_path);
    (journal, restored_items)
}
