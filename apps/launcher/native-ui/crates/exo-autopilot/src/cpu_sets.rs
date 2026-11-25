use crate::blocklist::{ProtectionContext, is_known_background_hog, why_protected};
use crate::os::{AutopilotOs, ProcessEntry};
use crate::topology::TopologyPlan;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedCpuSet {
    pub pid: u32,
    pub name: String,
    pub original_ids: Vec<u32>,
}

#[derive(Debug, Default)]
pub struct CpuSetCorral {
    corralled: HashMap<u32, SavedCpuSet>,
}

impl CpuSetCorral {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn active_count(&self) -> usize {
        self.corralled.len()
    }

    pub fn saved_entries(&self) -> Vec<SavedCpuSet> {
        self.corralled.values().cloned().collect()
    }

    pub fn corral_pid(
        &mut self,
        os: &dyn AutopilotOs,
        proc_entry: &ProcessEntry,
        ctx: &ProtectionContext<'_>,
        plan: &TopologyPlan,
    ) -> bool {
        let pid = proc_entry.pid;
        if plan.background_set_ids.is_empty() || self.corralled.contains_key(&pid) {
            return false;
        }
        if why_protected(pid, &proc_entry.name, proc_entry.session_id, ctx).is_some() {
            return false;
        }
        let original_ids = os.get_default_cpu_sets(pid).unwrap_or_default();
        if os.set_default_cpu_sets(pid, &plan.background_set_ids) {
            self.corralled.insert(
                pid,
                SavedCpuSet {
                    pid,
                    name: proc_entry.name.clone(),
                    original_ids,
                },
            );
            true
        } else {
            false
        }
    }

    pub fn corral_known_hogs(
        &mut self,
        os: &dyn AutopilotOs,
        processes: &[ProcessEntry],
        ctx: &ProtectionContext<'_>,
        learned_offenders: &HashSet<String>,
        plan: &TopologyPlan,
    ) -> usize {
        if plan.background_set_ids.is_empty() {
            return 0;
        }
        let mut applied = 0usize;
        for proc_entry in processes {
            let bare = crate::blocklist::bare_name(&proc_entry.name);
            if !is_known_background_hog(&bare) && !learned_offenders.contains(&bare) {
                continue;
            }
            if self.corral_pid(os, proc_entry, ctx, plan) {
                applied += 1;
            }
        }
        applied
    }

    pub fn release_pid(&mut self, os: &dyn AutopilotOs, pid: u32) -> bool {
        if let Some(saved) = self.corralled.remove(&pid) {
            os.set_default_cpu_sets(saved.pid, &saved.original_ids)
        } else {
            false
        }
    }

    pub fn restore_all(&mut self, os: &dyn AutopilotOs) -> usize {
        let mut restored = 0usize;
        for (_, saved) in self.corralled.drain() {
            if os.set_default_cpu_sets(saved.pid, &saved.original_ids) {
                restored += 1;
            }
        }
        restored
    }
}
