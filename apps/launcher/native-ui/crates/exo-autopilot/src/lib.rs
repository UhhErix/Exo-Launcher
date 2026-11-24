pub mod blocklist;
pub mod cpu_sets;
pub mod os;
pub mod probalance;
pub mod session_pause;
pub mod standby;
pub mod timer_res;
pub mod topology;

use crate::blocklist::{ProtectionContext, bare_name, expand_pid_tree};
use crate::cpu_sets::CpuSetCorral;
use crate::os::AutopilotOs;
use crate::probalance::{ProBalanceConfig, ProBalanceDamper, RestrainedProcess};
use crate::session_pause::{
    AutopilotJournal, SessionDownloadPause, SessionPauseSnapshot, recover_from_journal,
};
use crate::standby::{SmartStandbyShield, StandbyConfig, StandbyStats};
use crate::timer_res::{TimerResolutionGuard, TimerResolutionState};
use crate::topology::{TopologyPlan, classify_topology};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::path::PathBuf;
use std::sync::Arc;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotConfig {
    pub journal_path: PathBuf,
    pub enable_cpu_sets: bool,
    pub enable_probalance: bool,
    pub enable_timer_resolution: bool,
    pub enable_standby_shield: bool,
    pub enable_download_pause: bool,
    pub enable_mouse_accel_guard: bool,
    pub gaming_power_scheme_guid: Option<String>,
    pub probalance: ProBalanceConfig,
    pub standby: StandbyConfig,
}

impl Default for AutopilotConfig {
    fn default() -> Self {
        Self {
            journal_path: PathBuf::from("autopilot-journal.json"),
            enable_cpu_sets: true,
            enable_probalance: true,
            enable_timer_resolution: true,
            enable_standby_shield: true,
            enable_download_pause: true,
            enable_mouse_accel_guard: true,
            gaming_power_scheme_guid: None,
            probalance: ProBalanceConfig::default(),
            standby: StandbyConfig::default(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ActiveGameSession {
    pub pid: u32,
    pub name: String,
    pub is_anti_cheat: bool,
    pub foreground: bool,
    pub started_ms: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutopilotStatus {
    pub active_game: Option<ActiveGameSession>,
    pub topology: TopologyPlan,
    pub timer: TimerResolutionState,
    pub corralled_count: usize,
    pub restrained: Vec<RestrainedProcess>,
    pub total_restraints: u64,
    pub standby: StandbyStats,
    pub downloads: SessionPauseSnapshot,
    pub on_battery: bool,
    pub recovered_from_crash_count: usize,
    pub learned_offenders: Vec<String>,
}

pub struct AutopilotEngine<O: AutopilotOs> {
    os: Arc<O>,
    config: AutopilotConfig,
    topology: TopologyPlan,
    active_game: Option<ActiveGameSession>,
    corral: CpuSetCorral,
    probalance: ProBalanceDamper,
    timer: TimerResolutionGuard,
    standby: SmartStandbyShield,
    downloads: SessionDownloadPause,
    previous_power_scheme: Option<String>,
    previous_mouse_accel: Option<[i32; 3]>,
    recovered_from_crash_count: usize,
}

impl<O: AutopilotOs> AutopilotEngine<O> {
    pub fn new(os: Arc<O>, config: AutopilotConfig, vcache_mask: Option<u64>) -> Self {
        let (journal, recovered_count) = recover_from_journal(os.as_ref(), &config.journal_path);
        let brand = os.read_cpu_brand();
        let cpu_sets = os.read_system_cpu_sets();
        let topology = classify_topology(&cpu_sets, &brand, vcache_mask);
        let learned = journal.learned_set();

        Self {
            os,
            probalance: ProBalanceDamper::new(config.probalance, learned),
            standby: SmartStandbyShield::new(config.standby),
            config,
            topology,
            active_game: None,
            corral: CpuSetCorral::new(),
            timer: TimerResolutionGuard::new(),
            downloads: SessionDownloadPause::new(),
            previous_power_scheme: None,
            previous_mouse_accel: None,
            recovered_from_crash_count: recovered_count,
        }
    }

    pub fn topology(&self) -> &TopologyPlan {
        &self.topology
    }

    pub fn status(&self) -> AutopilotStatus {
        let mut learned: Vec<String> = self
            .probalance
            .learned_offenders()
            .iter()
            .cloned()
            .collect();
        learned.sort();
        AutopilotStatus {
            active_game: self.active_game.clone(),
            topology: self.topology.clone(),
            timer: self.timer.state(),
            corralled_count: self.corral.active_count(),
            restrained: self.probalance.active_restraints(),
            total_restraints: self.probalance.total_restraints(),
            standby: self.standby.stats(),
            downloads: self.downloads.snapshot(),
            on_battery: self.os.is_on_battery(),
            recovered_from_crash_count: self.recovered_from_crash_count,
            learned_offenders: learned,
        }
    }

    fn protected_context(
        &self,
        processes: &[crate::os::ProcessEntry],
    ) -> (HashSet<u32>, HashSet<String>) {
        let mut pids = HashSet::new();
        let mut names = HashSet::new();
        pids.insert(self.os.current_pid());
        if let Some(ref game) = self.active_game {
            let pairs: Vec<(u32, u32)> = processes.iter().map(|p| (p.pid, p.parent_pid)).collect();
            for p in expand_pid_tree(game.pid, &pairs) {
                pids.insert(p);
            }
            let b = bare_name(&game.name);
            if !b.is_empty() {
                names.insert(b);
            }
            for proc_entry in processes {
                if pids.contains(&proc_entry.pid) {
                    let nb = bare_name(&proc_entry.name);
                    if !nb.is_empty() {
                        names.insert(nb);
                    }
                }
            }
        }
        (pids, names)
    }

    fn persist_journal(&self, active_session: bool) {
        let mut learned: Vec<String> = self
            .probalance
            .learned_offenders()
            .iter()
            .cloned()
            .collect();
        learned.sort();
        let journal = AutopilotJournal {
            active_session,
            started_ms: self.active_game.as_ref().map(|g| g.started_ms).unwrap_or(0),
            paused_bits_jobs: self.downloads.paused_bits_jobs().to_vec(),
            paused_services: self.downloads.paused_services().to_vec(),
            previous_power_scheme: self.previous_power_scheme.clone(),
            previous_mouse_accel: self.previous_mouse_accel,
            restrained_processes: self.probalance.active_restraints(),
            corralled_cpu_sets: self.corral.saved_entries(),
            learned_offenders: learned,
        };
        let _ = journal.save_atomic(&self.config.journal_path);
    }

    fn engage_foreground_optimizations(&mut self, now_ms: u64) {
        let on_battery = self.os.is_on_battery();

        if self.config.enable_timer_resolution && !on_battery {
            self.timer.acquire(self.os.as_ref());
        }

        if let Some(ref desired_scheme) = self.config.gaming_power_scheme_guid
            && !on_battery
            && self.previous_power_scheme.is_none()
            && let Some(prev) = self.os.active_power_scheme()
            && prev != *desired_scheme
            && self.os.set_power_scheme(desired_scheme)
        {
            self.previous_power_scheme = Some(prev);
        }

        if self.config.enable_mouse_accel_guard
            && self.previous_mouse_accel.is_none()
            && let Some(cur) = self.os.read_mouse_accel()
            && cur[2] != 0
            && self.os.write_mouse_accel([0, 0, 0])
        {
            self.previous_mouse_accel = Some(cur);
        }

        if self.config.enable_download_pause {
            self.downloads.pause(self.os.as_ref(), now_ms);
        }

        if self.config.enable_cpu_sets {
            let procs = self.os.enumerate_processes();
            let (protected_pids, game_names) = self.protected_context(&procs);
            let ctx = ProtectionContext {
                current_session_id: self.os.current_session_id(),
                protected_pids: &protected_pids,
                game_names: &game_names,
            };
            self.corral.corral_known_hogs(
                self.os.as_ref(),
                &procs,
                &ctx,
                self.probalance.learned_offenders(),
                &self.topology,
            );
        }

        self.persist_journal(true);
    }

    fn disengage_foreground_optimizations(&mut self) {
        self.timer.release(self.os.as_ref());
        self.probalance.restore_all(self.os.as_ref());
        self.corral.restore_all(self.os.as_ref());
        if let Some(prev) = self.previous_power_scheme.take() {
            let _ = self.os.set_power_scheme(&prev);
        }
        if let Some(mouse) = self.previous_mouse_accel.take() {
            let _ = self.os.write_mouse_accel(mouse);
        }
    }

    pub fn on_game_start(
        &mut self,
        game_pid: u32,
        game_name: impl Into<String>,
        is_anti_cheat: bool,
        now_ms: u64,
    ) {
        if self.active_game.is_some() {
            self.on_game_exit();
        }
        self.active_game = Some(ActiveGameSession {
            pid: game_pid,
            name: game_name.into(),
            is_anti_cheat,
            foreground: true,
            started_ms: now_ms,
        });
        self.engage_foreground_optimizations(now_ms);
    }

    pub fn on_focus_change(&mut self, is_game_foreground: bool, now_ms: u64) {
        let Some(ref mut game) = self.active_game else {
            return;
        };
        if game.foreground == is_game_foreground {
            return;
        }
        game.foreground = is_game_foreground;
        if is_game_foreground {
            self.engage_foreground_optimizations(now_ms);
        } else {
            self.disengage_foreground_optimizations();
            self.downloads.resume(self.os.as_ref());
            self.persist_journal(false);
        }
    }

    pub fn sync_focus_from_os(&mut self, now_ms: u64) {
        let Some(game_pid) = self.active_game.as_ref().map(|g| g.pid) else {
            return;
        };
        let procs = self.os.enumerate_processes();
        let pairs: Vec<(u32, u32)> = procs.iter().map(|p| (p.pid, p.parent_pid)).collect();
        let tree = expand_pid_tree(game_pid, &pairs);
        let is_fg = self
            .os
            .foreground_pid()
            .is_some_and(|fg| tree.contains(&fg));
        self.on_focus_change(is_fg, now_ms);
    }

    pub fn tick(&mut self, now_ms: u64, paging_pressure: bool) {
        let is_fg = self.active_game.as_ref().is_some_and(|g| g.foreground);
        if !is_fg {
            return;
        }

        if self.downloads.check_watchdog(self.os.as_ref(), now_ms) {
            self.persist_journal(true);
        }

        if self.config.enable_probalance {
            let procs = self.os.enumerate_processes();
            let (protected_pids, game_names) = self.protected_context(&procs);
            let ctx = ProtectionContext {
                current_session_id: self.os.current_session_id(),
                protected_pids: &protected_pids,
                game_names: &game_names,
            };
            let newly = self.probalance.tick(
                self.os.as_ref(),
                now_ms,
                &procs,
                &ctx,
                &self.topology,
                &mut self.corral,
            );
            if newly > 0 {
                self.persist_journal(true);
            }
        }

        if self.config.enable_standby_shield {
            self.standby
                .maybe_purge(self.os.as_ref(), now_ms, paging_pressure);
        }
    }

    pub fn on_game_exit(&mut self) {
        self.disengage_foreground_optimizations();
        self.downloads.resume(self.os.as_ref());
        self.active_game = None;
        self.persist_journal(false);
    }
}

impl<O: AutopilotOs> Drop for AutopilotEngine<O> {
    fn drop(&mut self) {
        if self.active_game.is_some()
            || self.timer.state().active
            || self.corral.active_count() > 0
            || !self.probalance.active_restraints().is_empty()
            || self.downloads.snapshot().active
            || self.previous_power_scheme.is_some()
            || self.previous_mouse_accel.is_some()
        {
            self.on_game_exit();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::os::{
        MemorySnapshot, PRIORITY_BELOW_NORMAL, PRIORITY_NORMAL, ProcessEntry, ServiceSnapshot,
        TimerResolutionInfo,
    };
    use crate::topology::{CpuSetInfo, CpuTopologyKind};
    use std::collections::HashMap;
    use std::sync::Mutex;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[derive(Default)]
    struct MockState {
        brand: String,
        cpu_sets: Vec<CpuSetInfo>,
        processes: Vec<ProcessEntry>,
        cpu_times: HashMap<u32, u64>,
        priorities: HashMap<u32, u32>,
        default_cpu_sets: HashMap<u32, Vec<u32>>,
        eco_qos: HashMap<u32, bool>,
        touched_pids: HashSet<u32>,
        timer_set_calls: Vec<(u32, bool)>,
        memory: MemorySnapshot,
        purge_count: u64,
        bits_jobs: Vec<String>,
        suspended_bits: HashSet<String>,
        services: HashMap<String, ServiceSnapshot>,
        on_battery: bool,
        power_scheme: Option<String>,
        mouse_accel: Option<[i32; 3]>,
        foreground_pid: Option<u32>,
    }

    struct MockOs {
        inner: Mutex<MockState>,
    }

    impl MockOs {
        fn new(state: MockState) -> Arc<Self> {
            Arc::new(Self {
                inner: Mutex::new(state),
            })
        }
    }

    impl AutopilotOs for MockOs {
        fn current_pid(&self) -> u32 {
            9999
        }
        fn current_session_id(&self) -> u32 {
            1
        }
        fn read_cpu_brand(&self) -> String {
            self.inner.lock().unwrap().brand.clone()
        }
        fn read_system_cpu_sets(&self) -> Vec<CpuSetInfo> {
            self.inner.lock().unwrap().cpu_sets.clone()
        }
        fn enumerate_processes(&self) -> Vec<ProcessEntry> {
            self.inner.lock().unwrap().processes.clone()
        }
        fn foreground_pid(&self) -> Option<u32> {
            self.inner.lock().unwrap().foreground_pid
        }
        fn process_cpu_time_100ns(&self, pid: u32) -> Option<u64> {
            let mut g = self.inner.lock().unwrap();
            g.touched_pids.insert(pid);
            g.cpu_times.get(&pid).copied()
        }
        fn get_priority_class(&self, pid: u32) -> Option<u32> {
            let mut g = self.inner.lock().unwrap();
            g.touched_pids.insert(pid);
            g.priorities.get(&pid).copied()
        }
        fn set_priority_class(&self, pid: u32, priority_class: u32) -> bool {
            let mut g = self.inner.lock().unwrap();
            g.touched_pids.insert(pid);
            g.priorities.insert(pid, priority_class);
            true
        }
        fn get_default_cpu_sets(&self, pid: u32) -> Option<Vec<u32>> {
            let mut g = self.inner.lock().unwrap();
            g.touched_pids.insert(pid);
            Some(g.default_cpu_sets.get(&pid).cloned().unwrap_or_default())
        }
        fn set_default_cpu_sets(&self, pid: u32, ids: &[u32]) -> bool {
            let mut g = self.inner.lock().unwrap();
            g.touched_pids.insert(pid);
            g.default_cpu_sets.insert(pid, ids.to_vec());
            true
        }
        fn set_eco_qos(&self, pid: u32, enabled: bool) -> bool {
            let mut g = self.inner.lock().unwrap();
            g.touched_pids.insert(pid);
            g.eco_qos.insert(pid, enabled);
            true
        }
        fn query_timer_resolution(&self) -> Option<TimerResolutionInfo> {
            Some(TimerResolutionInfo {
                min_100ns: 156_250,
                max_100ns: 5_000,
                current_100ns: 156_250,
            })
        }
        fn set_timer_resolution(&self, desired_100ns: u32, set: bool) -> Option<u32> {
            let mut g = self.inner.lock().unwrap();
            g.timer_set_calls.push((desired_100ns, set));
            Some(if set { desired_100ns } else { 156_250 })
        }
        fn memory_snapshot(&self) -> MemorySnapshot {
            self.inner.lock().unwrap().memory
        }
        fn purge_standby_list(&self) -> Result<(), String> {
            self.inner.lock().unwrap().purge_count += 1;
            Ok(())
        }
        fn list_active_bits_jobs(&self) -> Vec<String> {
            self.inner.lock().unwrap().bits_jobs.clone()
        }
        fn suspend_bits_job(&self, job_id: &str) -> bool {
            self.inner
                .lock()
                .unwrap()
                .suspended_bits
                .insert(job_id.to_string())
        }
        fn resume_bits_job(&self, job_id: &str) -> bool {
            self.inner.lock().unwrap().suspended_bits.remove(job_id)
        }
        fn query_service(&self, name: &str) -> Option<ServiceSnapshot> {
            self.inner.lock().unwrap().services.get(name).cloned()
        }
        fn pause_service(&self, name: &str) -> bool {
            if let Some(s) = self.inner.lock().unwrap().services.get_mut(name) {
                s.running = false;
                s.paused = true;
                return true;
            }
            false
        }
        fn stop_service(&self, name: &str) -> bool {
            if let Some(s) = self.inner.lock().unwrap().services.get_mut(name) {
                s.running = false;
                s.paused = false;
                return true;
            }
            false
        }
        fn continue_service(&self, name: &str) -> bool {
            if let Some(s) = self.inner.lock().unwrap().services.get_mut(name) {
                s.running = true;
                s.paused = false;
                return true;
            }
            false
        }
        fn start_service(&self, name: &str) -> bool {
            if let Some(s) = self.inner.lock().unwrap().services.get_mut(name) {
                s.running = true;
                s.paused = false;
                return true;
            }
            false
        }
        fn is_on_battery(&self) -> bool {
            self.inner.lock().unwrap().on_battery
        }
        fn active_power_scheme(&self) -> Option<String> {
            self.inner.lock().unwrap().power_scheme.clone()
        }
        fn set_power_scheme(&self, guid_str: &str) -> bool {
            self.inner.lock().unwrap().power_scheme = Some(guid_str.to_string());
            true
        }
        fn read_mouse_accel(&self) -> Option<[i32; 3]> {
            self.inner.lock().unwrap().mouse_accel
        }
        fn write_mouse_accel(&self, values: [i32; 3]) -> bool {
            self.inner.lock().unwrap().mouse_accel = Some(values);
            true
        }
    }

    fn temp_journal(tag: &str) -> PathBuf {
        let ns = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("exo-autopilot-{tag}-{ns}.json"))
    }

    fn make_monolithic_5600x() -> Vec<CpuSetInfo> {
        (0..12u8)
            .map(|i| CpuSetInfo {
                id: 256 + i as u32,
                group: 0,
                core_index: i / 2,
                last_level_cache_index: 0,
                efficiency_class: 0,
                logical_processor_index: i,
            })
            .collect()
    }

    #[test]
    fn topology_classifier_golden_cases() {
        let mut intel_14900k = Vec::new();
        for i in 0..16u8 {
            intel_14900k.push(CpuSetInfo {
                id: 100 + i as u32,
                group: 0,
                core_index: i / 2,
                last_level_cache_index: 0,
                efficiency_class: 1,
                logical_processor_index: i,
            });
        }
        for i in 16..32u8 {
            intel_14900k.push(CpuSetInfo {
                id: 100 + i as u32,
                group: 0,
                core_index: i,
                last_level_cache_index: 0,
                efficiency_class: 0,
                logical_processor_index: i,
            });
        }
        let p_intel = classify_topology(&intel_14900k, "Intel(R) Core(TM) i9-14900K", None);
        assert_eq!(p_intel.kind, CpuTopologyKind::IntelHybrid);
        assert_eq!(p_intel.background_set_ids.len(), 16);
        assert!(p_intel.background_set_ids.iter().all(|&id| id >= 116));

        let mut amd_7950x3d = Vec::new();
        for i in 0..32u8 {
            amd_7950x3d.push(CpuSetInfo {
                id: 200 + i as u32,
                group: 0,
                core_index: i / 2,
                last_level_cache_index: if i < 16 { 0 } else { 1 },
                efficiency_class: 0,
                logical_processor_index: i,
            });
        }
        let vcache_ccd0_mask = 0x0000_FFFFu64;
        let p_x3d = classify_topology(
            &amd_7950x3d,
            "AMD Ryzen 9 7950X3D 16-Core Processor",
            Some(vcache_ccd0_mask),
        );
        assert_eq!(p_x3d.kind, CpuTopologyKind::AmdDualCcdX3d);
        assert_eq!(p_x3d.background_set_ids.len(), 16);
        assert!(p_x3d.background_set_ids.iter().all(|&id| id >= 216));

        let p_5600x = classify_topology(
            &make_monolithic_5600x(),
            "AMD Ryzen 5 5600X 6-Core Processor",
            None,
        );
        assert_eq!(p_5600x.kind, CpuTopologyKind::Monolithic);
        assert_eq!(p_5600x.background_set_ids, vec![265, 266, 267]);

        let quad_core: Vec<CpuSetInfo> = (0..4u8)
            .map(|i| CpuSetInfo {
                id: 300 + i as u32,
                group: 0,
                core_index: i,
                last_level_cache_index: 0,
                efficiency_class: 0,
                logical_processor_index: i,
            })
            .collect();
        let p_quad = classify_topology(&quad_core, "Quad Core CPU", None);
        assert_eq!(p_quad.kind, CpuTopologyKind::LowCoreNoOp);
        assert!(p_quad.background_set_ids.is_empty());
    }

    #[test]
    fn zero_game_touch_and_blocklist_invariants() {
        let mut st = MockState {
            brand: "AMD Ryzen 5 5600X 6-Core Processor".into(),
            cpu_sets: make_monolithic_5600x(),
            processes: vec![
                ProcessEntry {
                    pid: 1000,
                    parent_pid: 1,
                    name: "VALORANT-Win64-Shipping.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 1001,
                    parent_pid: 1000,
                    name: "UnrealCEFSubProcess.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 2001,
                    parent_pid: 4,
                    name: "vgc.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 2002,
                    parent_pid: 4,
                    name: "vgtray.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 2003,
                    parent_pid: 4,
                    name: "EasyAntiCheat_EOS.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 2004,
                    parent_pid: 4,
                    name: "BEService_x64.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 2005,
                    parent_pid: 4,
                    name: "faceitservice.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 2006,
                    parent_pid: 4,
                    name: "FortniteClient-Win64-Shipping_BE.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 3001,
                    parent_pid: 4,
                    name: "audiodg.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 3002,
                    parent_pid: 4,
                    name: "dwm.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 3003,
                    parent_pid: 4,
                    name: "Discord.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 3004,
                    parent_pid: 4,
                    name: "ExoLauncher.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 3005,
                    parent_pid: 4,
                    name: "exo-ui.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 4001,
                    parent_pid: 1,
                    name: "burn-cpu.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 4002,
                    parent_pid: 1,
                    name: "chrome.exe".into(),
                    session_id: Some(1),
                },
            ],
            ..Default::default()
        };
        for p in &st.processes {
            st.cpu_times.insert(p.pid, 1_000_000);
            st.priorities.insert(p.pid, PRIORITY_NORMAL);
        }

        let os = MockOs::new(st);
        let jpath = temp_journal("blocklist");
        let cfg = AutopilotConfig {
            journal_path: jpath.clone(),
            ..Default::default()
        };
        let mut engine = AutopilotEngine::new(os.clone(), cfg, None);
        engine.on_game_start(1000, "VALORANT-Win64-Shipping.exe", true, 1000);

        engine.tick(2000, false);
        {
            let mut g = os.inner.lock().unwrap();
            for &pid in &[
                1000u32, 1001, 2001, 2002, 2003, 2004, 2005, 2006, 3001, 3002, 3003, 3004, 3005,
                4001,
            ] {
                *g.cpu_times.get_mut(&pid).unwrap() += 40_000_000;
            }
        }
        engine.tick(3000, false);
        {
            let mut g = os.inner.lock().unwrap();
            for &pid in &[
                1000u32, 1001, 2001, 2002, 2003, 2004, 2005, 2006, 3001, 3002, 3003, 3004, 3005,
                4001,
            ] {
                *g.cpu_times.get_mut(&pid).unwrap() += 40_000_000;
            }
        }
        engine.tick(4000, false);

        {
            let g = os.inner.lock().unwrap();
            for &protected_pid in &[
                1000u32, 1001, 2001, 2002, 2003, 2004, 2005, 2006, 3001, 3002, 3003, 3004, 3005,
            ] {
                assert!(
                    !g.touched_pids.contains(&protected_pid),
                    "protected PID {protected_pid} must NEVER be touched"
                );
            }
            assert_eq!(g.priorities.get(&4001), Some(&PRIORITY_BELOW_NORMAL));
            assert_eq!(g.eco_qos.get(&4001), Some(&true));
            assert_eq!(g.default_cpu_sets.get(&4001), Some(&vec![265, 266, 267]));
            assert_eq!(g.default_cpu_sets.get(&4002), Some(&vec![265, 266, 267]));
        }

        engine.on_game_exit();
        {
            let g = os.inner.lock().unwrap();
            assert_eq!(g.priorities.get(&4001), Some(&PRIORITY_NORMAL));
            assert_eq!(g.eco_qos.get(&4001), Some(&false));
            assert_eq!(g.default_cpu_sets.get(&4001), Some(&vec![]));
            assert_eq!(g.default_cpu_sets.get(&4002), Some(&vec![]));
            assert_eq!(
                g.timer_set_calls,
                vec![
                    (timer_res::TARGET_TIMER_100NS, true),
                    (timer_res::TARGET_TIMER_100NS, false)
                ]
            );
        }
        let _ = std::fs::remove_file(&jpath);
    }

    #[test]
    fn crash_journal_recovers_all_state_on_startup() {
        let jpath = temp_journal("crash");
        let mut services = HashMap::new();
        services.insert(
            "BITS".into(),
            ServiceSnapshot {
                name: "BITS".into(),
                running: true,
                paused: false,
                can_pause: false,
                can_stop: true,
            },
        );
        services.insert(
            "WSearch".into(),
            ServiceSnapshot {
                name: "WSearch".into(),
                running: true,
                paused: false,
                can_pause: true,
                can_stop: true,
            },
        );

        let mut st = MockState {
            brand: "AMD Ryzen 5 5600X 6-Core Processor".into(),
            cpu_sets: make_monolithic_5600x(),
            processes: vec![
                ProcessEntry {
                    pid: 1000,
                    parent_pid: 1,
                    name: "Sekiro.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 5001,
                    parent_pid: 1,
                    name: "chrome.exe".into(),
                    session_id: Some(1),
                },
            ],
            bits_jobs: vec!["11111111-2222-3333-4444-555555555555".into()],
            services,
            power_scheme: Some("381b4222-f694-41f0-9685-ff5bb260df2e".into()),
            mouse_accel: Some([6, 10, 1]),
            ..Default::default()
        };
        st.priorities.insert(5001, PRIORITY_NORMAL);
        st.cpu_times.insert(5001, 0);

        let os = MockOs::new(st);
        let cfg = AutopilotConfig {
            journal_path: jpath.clone(),
            gaming_power_scheme_guid: Some("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c".into()),
            ..Default::default()
        };

        let mut engine = AutopilotEngine::new(os.clone(), cfg.clone(), None);
        engine.on_game_start(1000, "Sekiro.exe", false, 500);
        std::mem::forget(engine);

        {
            let g = os.inner.lock().unwrap();
            assert!(!g.services["BITS"].running);
            assert!(g.services["WSearch"].paused);
            assert!(
                g.suspended_bits
                    .contains("11111111-2222-3333-4444-555555555555")
            );
            assert_eq!(g.mouse_accel, Some([0, 0, 0]));
            assert_eq!(
                g.power_scheme.as_deref(),
                Some("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")
            );
        }

        let recovered_engine = AutopilotEngine::new(os.clone(), cfg, None);
        assert!(recovered_engine.status().recovered_from_crash_count >= 5);
        {
            let g = os.inner.lock().unwrap();
            assert!(g.services["BITS"].running);
            assert!(g.services["WSearch"].running);
            assert!(!g.services["WSearch"].paused);
            assert!(g.suspended_bits.is_empty());
            assert_eq!(g.mouse_accel, Some([6, 10, 1]));
            assert_eq!(
                g.power_scheme.as_deref(),
                Some("381b4222-f694-41f0-9685-ff5bb260df2e")
            );
            assert_eq!(g.default_cpu_sets.get(&5001), Some(&vec![]));
        }
        let _ = std::fs::remove_file(&jpath);
    }

    #[test]
    fn smart_standby_purges_only_on_pressure_and_respects_battery() {
        let jpath = temp_journal("standby");
        let st = MockState {
            brand: "AMD Ryzen 5 5600X 6-Core Processor".into(),
            cpu_sets: make_monolithic_5600x(),
            memory: MemorySnapshot {
                total_phys_bytes: 16 * 1024 * 1024 * 1024,
                avail_phys_bytes: 1024 * 1024 * 1024,
                standby_bytes: Some(3 * 1024 * 1024 * 1024),
            },
            on_battery: true,
            power_scheme: Some("381b4222-f694-41f0-9685-ff5bb260df2e".into()),
            ..Default::default()
        };
        let os = MockOs::new(st);
        let cfg = AutopilotConfig {
            journal_path: jpath.clone(),
            gaming_power_scheme_guid: Some("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c".into()),
            ..Default::default()
        };
        let mut engine = AutopilotEngine::new(os.clone(), cfg, None);
        engine.on_game_start(1000, "Game.exe", false, 1000);

        assert!(!engine.status().timer.active);
        assert_eq!(
            os.inner.lock().unwrap().power_scheme.as_deref(),
            Some("381b4222-f694-41f0-9685-ff5bb260df2e")
        );

        engine.tick(2000, false);
        assert_eq!(os.inner.lock().unwrap().purge_count, 0);

        engine.tick(3000, true);
        assert_eq!(os.inner.lock().unwrap().purge_count, 1);

        engine.tick(10_000, true);
        assert_eq!(os.inner.lock().unwrap().purge_count, 1);

        engine.tick(35_000, true);
        assert_eq!(os.inner.lock().unwrap().purge_count, 2);

        engine.on_game_exit();
        let _ = std::fs::remove_file(&jpath);
    }

    #[test]
    fn alt_tab_releases_and_reengages_and_learns_repeat_offender() {
        let jpath = temp_journal("alttab_learn");
        let mut st = MockState {
            brand: "AMD Ryzen 5 5600X 6-Core Processor".into(),
            cpu_sets: make_monolithic_5600x(),
            processes: vec![
                ProcessEntry {
                    pid: 1000,
                    parent_pid: 1,
                    name: "Sekiro.exe".into(),
                    session_id: Some(1),
                },
                ProcessEntry {
                    pid: 7777,
                    parent_pid: 1,
                    name: "custom_transcoder.exe".into(),
                    session_id: Some(1),
                },
            ],
            mouse_accel: Some([6, 10, 1]),
            ..Default::default()
        };
        st.priorities.insert(7777, PRIORITY_NORMAL);
        st.cpu_times.insert(7777, 0);

        let os = MockOs::new(st);
        let cfg = AutopilotConfig {
            journal_path: jpath.clone(),
            ..Default::default()
        };

        let mut engine = AutopilotEngine::new(os.clone(), cfg.clone(), None);
        engine.on_game_start(1000, "Sekiro.exe", false, 1000);
        assert!(engine.status().timer.active);
        assert_eq!(engine.status().corralled_count, 0);

        engine.tick(1500, false);
        os.inner.lock().unwrap().cpu_times.insert(7777, 15_000_000);
        engine.tick(2000, false);
        os.inner.lock().unwrap().cpu_times.insert(7777, 30_000_000);
        engine.tick(2500, false);

        assert_eq!(engine.status().restrained.len(), 1);
        assert_eq!(
            os.inner.lock().unwrap().priorities.get(&7777),
            Some(&PRIORITY_BELOW_NORMAL)
        );
        assert_eq!(os.inner.lock().unwrap().eco_qos.get(&7777), Some(&true));
        assert!(
            engine
                .status()
                .learned_offenders
                .contains(&"custom_transcoder".to_string())
        );

        engine.on_focus_change(false, 3000);
        assert!(!engine.status().timer.active);
        assert_eq!(
            os.inner.lock().unwrap().priorities.get(&7777),
            Some(&PRIORITY_NORMAL)
        );
        assert_eq!(os.inner.lock().unwrap().eco_qos.get(&7777), Some(&false));
        assert_eq!(
            os.inner.lock().unwrap().default_cpu_sets.get(&7777),
            Some(&vec![])
        );
        assert_eq!(os.inner.lock().unwrap().mouse_accel, Some([6, 10, 1]));

        engine.on_focus_change(true, 3500);
        assert!(engine.status().timer.active);
        assert_eq!(engine.status().corralled_count, 1);
        assert_eq!(os.inner.lock().unwrap().mouse_accel, Some([0, 0, 0]));

        engine.on_game_exit();
        drop(engine);

        let mut engine2 = AutopilotEngine::new(os.clone(), cfg, None);
        assert!(
            engine2
                .status()
                .learned_offenders
                .contains(&"custom_transcoder".to_string())
        );
        engine2.on_game_start(1000, "Sekiro.exe", false, 5000);
        assert_eq!(engine2.status().corralled_count, 1);
        assert!(!os.inner.lock().unwrap().default_cpu_sets[&7777].is_empty());

        engine2.on_game_exit();
        assert_eq!(
            os.inner.lock().unwrap().default_cpu_sets.get(&7777),
            Some(&vec![])
        );
        let _ = std::fs::remove_file(&jpath);
    }
}
