use crate::os::{AutopilotOs, MemorySnapshot};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StandbyConfig {
    pub min_free_bytes: u64,
    pub min_free_ratio: f64,
    pub min_standby_bytes: u64,
    pub cooldown_ms: u64,
    pub require_paging_pressure: bool,
}

impl Default for StandbyConfig {
    fn default() -> Self {
        Self {
            min_free_bytes: 1536 * 1024 * 1024,
            min_free_ratio: 0.15,
            min_standby_bytes: 512 * 1024 * 1024,
            cooldown_ms: 30_000,
            require_paging_pressure: true,
        }
    }
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StandbyStats {
    pub purges_executed: u64,
    pub last_purge_ms: Option<u64>,
    pub last_error: Option<String>,
    pub last_snapshot: MemorySnapshot,
}

#[derive(Debug)]
pub struct SmartStandbyShield {
    config: StandbyConfig,
    stats: StandbyStats,
}

impl SmartStandbyShield {
    pub fn new(config: StandbyConfig) -> Self {
        Self {
            config,
            stats: StandbyStats::default(),
        }
    }

    pub fn stats(&self) -> StandbyStats {
        self.stats.clone()
    }

    pub fn should_purge(&self, now_ms: u64, snap: MemorySnapshot, paging_pressure: bool) -> bool {
        if snap.total_phys_bytes == 0 {
            return false;
        }
        if let Some(last) = self.stats.last_purge_ms
            && now_ms.saturating_sub(last) < self.config.cooldown_ms
        {
            return false;
        }
        if self.config.require_paging_pressure && !paging_pressure {
            return false;
        }
        let ratio = (snap.avail_phys_bytes as f64) / (snap.total_phys_bytes as f64);
        let low_free = snap.avail_phys_bytes < self.config.min_free_bytes
            || ratio < self.config.min_free_ratio;
        if !low_free {
            return false;
        }
        if let Some(standby) = snap.standby_bytes
            && standby < self.config.min_standby_bytes
        {
            return false;
        }
        true
    }

    pub fn maybe_purge(
        &mut self,
        os: &dyn AutopilotOs,
        now_ms: u64,
        paging_pressure: bool,
    ) -> bool {
        let snap = os.memory_snapshot();
        self.stats.last_snapshot = snap;
        if !self.should_purge(now_ms, snap, paging_pressure) {
            return false;
        }
        match os.purge_standby_list() {
            Ok(()) => {
                self.stats.purges_executed += 1;
                self.stats.last_purge_ms = Some(now_ms);
                self.stats.last_error = None;
                true
            }
            Err(err) => {
                self.stats.last_purge_ms = Some(now_ms);
                self.stats.last_error = Some(err);
                false
            }
        }
    }
}
