use crate::blocklist::{ProtectionContext, bare_name, why_protected};
use crate::cpu_sets::CpuSetCorral;
use crate::os::{AutopilotOs, PRIORITY_BELOW_NORMAL, PRIORITY_NORMAL, ProcessEntry};
use crate::topology::TopologyPlan;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProBalanceConfig {
    pub cpu_threshold_pct: f64,
    pub release_threshold_pct: f64,
    pub trigger_consecutive_ticks: u32,
    pub release_consecutive_ticks: u32,
}

impl Default for ProBalanceConfig {
    fn default() -> Self {
        Self {
            cpu_threshold_pct: 15.0,
            release_threshold_pct: 5.0,
            trigger_consecutive_ticks: 2,
            release_consecutive_ticks: 3,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RestrainedProcess {
    pub pid: u32,
    pub name: String,
    pub original_priority: u32,
    pub eco_qos_applied: bool,
    pub peak_cpu_pct: f64,
}

#[derive(Debug, Clone, Default)]
struct PidSample {
    last_cpu_100ns: u64,
    last_wall_ms: u64,
    high_streak: u32,
    low_streak: u32,
}

#[derive(Debug)]
pub struct ProBalanceDamper {
    config: ProBalanceConfig,
    samples: HashMap<u32, PidSample>,
    restrained: HashMap<u32, RestrainedProcess>,
    learned_offenders: HashSet<String>,
    total_restraints: u64,
}

impl ProBalanceDamper {
    pub fn new(config: ProBalanceConfig, learned_offenders: HashSet<String>) -> Self {
        Self {
            config,
            samples: HashMap::new(),
            restrained: HashMap::new(),
            learned_offenders,
            total_restraints: 0,
        }
    }

    pub fn active_restraints(&self) -> Vec<RestrainedProcess> {
        self.restrained.values().cloned().collect()
    }

    pub fn total_restraints(&self) -> u64 {
        self.total_restraints
    }

    pub fn learned_offenders(&self) -> &HashSet<String> {
        &self.learned_offenders
    }

    pub fn tick(
        &mut self,
        os: &dyn AutopilotOs,
        now_ms: u64,
        processes: &[ProcessEntry],
        ctx: &ProtectionContext<'_>,
        plan: &TopologyPlan,
        corral: &mut CpuSetCorral,
    ) -> usize {
        let logical_cores = plan.total_threads.max(1) as f64;
        let mut alive = HashSet::with_capacity(processes.len());
        let mut newly_restrained = 0usize;

        for proc_entry in processes {
            let pid = proc_entry.pid;
            alive.insert(pid);

            if why_protected(pid, &proc_entry.name, proc_entry.session_id, ctx).is_some() {
                self.samples.remove(&pid);
                continue;
            }

            let Some(cpu_100ns) = os.process_cpu_time_100ns(pid) else {
                continue;
            };

            let entry = self.samples.entry(pid).or_insert(PidSample {
                last_cpu_100ns: cpu_100ns,
                last_wall_ms: now_ms,
                high_streak: 0,
                low_streak: 0,
            });

            let wall_ms = now_ms.saturating_sub(entry.last_wall_ms);
            if wall_ms == 0 {
                continue;
            }

            let delta_cpu_100ns = cpu_100ns.saturating_sub(entry.last_cpu_100ns);
            entry.last_cpu_100ns = cpu_100ns;
            entry.last_wall_ms = now_ms;

            let wall_100ns = (wall_ms as f64) * 10_000.0;
            let cpu_pct = ((delta_cpu_100ns as f64) / (wall_100ns * logical_cores)) * 100.0;

            if cpu_pct >= self.config.cpu_threshold_pct {
                entry.high_streak = entry.high_streak.saturating_add(1);
                entry.low_streak = 0;
            } else if cpu_pct <= self.config.release_threshold_pct {
                entry.low_streak = entry.low_streak.saturating_add(1);
                entry.high_streak = 0;
            } else {
                entry.high_streak = 0;
                entry.low_streak = 0;
            }

            if let Some(existing) = self.restrained.get_mut(&pid) {
                if cpu_pct > existing.peak_cpu_pct {
                    existing.peak_cpu_pct = cpu_pct;
                }
                if entry.low_streak >= self.config.release_consecutive_ticks
                    && let Some(saved) = self.restrained.remove(&pid)
                {
                    let _ = os.set_eco_qos(saved.pid, false);
                    let _ = os.set_priority_class(saved.pid, saved.original_priority);
                    corral.release_pid(os, saved.pid);
                }
            } else {
                let bare = bare_name(&proc_entry.name);
                let required_ticks = if self.learned_offenders.contains(&bare) {
                    1
                } else {
                    self.config.trigger_consecutive_ticks
                };
                if entry.high_streak >= required_ticks {
                    let original_priority = os.get_priority_class(pid).unwrap_or(PRIORITY_NORMAL);
                    let prio_ok = if original_priority != PRIORITY_BELOW_NORMAL {
                        os.set_priority_class(pid, PRIORITY_BELOW_NORMAL)
                    } else {
                        true
                    };
                    let eco_ok = os.set_eco_qos(pid, true);
                    let _ = corral.corral_pid(os, proc_entry, ctx, plan);
                    if prio_ok || eco_ok {
                        if !bare.is_empty() {
                            self.learned_offenders.insert(bare);
                        }
                        self.restrained.insert(
                            pid,
                            RestrainedProcess {
                                pid,
                                name: proc_entry.name.clone(),
                                original_priority,
                                eco_qos_applied: eco_ok,
                                peak_cpu_pct: cpu_pct,
                            },
                        );
                        self.total_restraints += 1;
                        newly_restrained += 1;
                    }
                }
            }
        }

        self.samples.retain(|pid, _| alive.contains(pid));
        self.restrained.retain(|pid, _| alive.contains(pid));
        newly_restrained
    }

    pub fn restore_all(&mut self, os: &dyn AutopilotOs) -> usize {
        let mut restored = 0usize;
        for (_, saved) in self.restrained.drain() {
            if saved.eco_qos_applied {
                let _ = os.set_eco_qos(saved.pid, false);
            }
            if os.set_priority_class(saved.pid, saved.original_priority) {
                restored += 1;
            }
        }
        self.samples.clear();
        restored
    }
}
