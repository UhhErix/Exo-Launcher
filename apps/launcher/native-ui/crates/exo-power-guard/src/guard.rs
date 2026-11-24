use std::path::{Path, PathBuf};

use crate::types::{
    GpuSnapshot, THROTTLE_CURRENT_LIMIT, THROTTLE_HW_POWER_BRAKE, THROTTLE_HW_THERMAL,
};

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum Severity {
    Normal,
    Warning,
    Critical,
}

impl Severity {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Normal => "normal",
            Self::Warning => "warning",
            Self::Critical => "critical",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EscalationStage {
    LowerPowerLimit,
    CloseGame,
    ShutdownPc,
}

impl EscalationStage {
    pub fn label(self) -> &'static str {
        match self {
            Self::LowerPowerLimit => "Lower GPU power limit",
            Self::CloseGame => "Close running game",
            Self::ShutdownPc => "Shut down PC",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FaultKind {
    PinOvercurrent,
    PinImbalance,
    ConnectorOvertemp,
    ConnectorHwFault,
    GpuCoreOvertemp,
    GpuHotspotOvertemp,
    GpuMemoryOvertemp,
    BoardOverpower,
    HwPowerBrake,
    HwThermalSlowdown,
    HwCurrentLimit,
}

impl FaultKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::PinOvercurrent => "pin-overcurrent",
            Self::PinImbalance => "pin-imbalance",
            Self::ConnectorOvertemp => "connector-overtemp",
            Self::ConnectorHwFault => "connector-hw-fault",
            Self::GpuCoreOvertemp => "gpu-core-overtemp",
            Self::GpuHotspotOvertemp => "gpu-hotspot-overtemp",
            Self::GpuMemoryOvertemp => "gpu-memory-overtemp",
            Self::BoardOverpower => "board-overpower",
            Self::HwPowerBrake => "hw-power-brake",
            Self::HwThermalSlowdown => "hw-thermal-slowdown",
            Self::HwCurrentLimit => "hw-current-limit",
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct GuardAlert {
    pub kind: FaultKind,
    pub severity: Severity,
    pub message: String,
    pub measured: f64,
    pub threshold: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RestoreReason {
    FaultCleared,
    GameExited,
    MonitorStopped,
    CrashRecovery,
}

impl RestoreReason {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::FaultCleared => "fault-cleared",
            Self::GameExited => "game-exited",
            Self::MonitorStopped => "monitor-stopped",
            Self::CrashRecovery => "crash-recovery",
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct PowerLimitJournalEntry {
    pub gpu_name: String,
    pub original_limit_w: f64,
    pub lowered_limit_w: f64,
    pub written_at_ms: u64,
}

#[derive(Debug, Clone)]
pub struct PowerLimitJournal {
    path: PathBuf,
}

impl PowerLimitJournal {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn default_path() -> PathBuf {
        let base = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir);
        base.join("ExoLauncher").join("power-guard-limit.journal")
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn write(&self, entry: &PowerLimitJournalEntry) -> Result<(), String> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let body = format!(
            "gpu={}\noriginal_w={:.3}\nlowered_w={:.3}\nwritten_at_ms={}\n",
            entry.gpu_name.replace(['\r', '\n'], " "),
            entry.original_limit_w,
            entry.lowered_limit_w,
            entry.written_at_ms
        );
        let tmp = self.path.with_extension("journal.tmp");
        std::fs::write(&tmp, body).map_err(|e| e.to_string())?;
        if self.path.exists() {
            let _ = std::fs::remove_file(&self.path);
        }
        std::fs::rename(&tmp, &self.path).map_err(|e| e.to_string())
    }

    pub fn read(&self) -> Option<PowerLimitJournalEntry> {
        let raw = std::fs::read_to_string(&self.path).ok()?;
        let mut gpu = None;
        let mut original_w = None;
        let mut lowered_w = None;
        let mut written_at_ms = None;
        for line in raw.lines() {
            if let Some(v) = line.strip_prefix("gpu=") {
                gpu = Some(v.trim().to_string());
            } else if let Some(v) = line.strip_prefix("original_w=") {
                original_w = v
                    .trim()
                    .parse::<f64>()
                    .ok()
                    .filter(|x| x.is_finite() && *x > 0.0);
            } else if let Some(v) = line.strip_prefix("lowered_w=") {
                lowered_w = v
                    .trim()
                    .parse::<f64>()
                    .ok()
                    .filter(|x| x.is_finite() && *x > 0.0);
            } else if let Some(v) = line.strip_prefix("written_at_ms=") {
                written_at_ms = v.trim().parse::<u64>().ok();
            }
        }
        Some(PowerLimitJournalEntry {
            gpu_name: gpu?,
            original_limit_w: original_w?,
            lowered_limit_w: lowered_w?,
            written_at_ms: written_at_ms?,
        })
    }

    pub fn clear(&self) -> Result<(), String> {
        match std::fs::remove_file(&self.path) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e.to_string()),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct GuardPolicy {
    pub alerts_enabled: bool,
    pub opt_in_lower_power_limit: bool,
    pub opt_in_close_game: bool,
    pub opt_in_shutdown_pc: bool,
    pub power_limit_scale: f64,
    pub pin_warn_a: f64,
    pub pin_critical_a: f64,
    pub imbalance_min_total_a: f64,
    pub imbalance_warn_spread_a: f64,
    pub imbalance_critical_spread_a: f64,
    pub connector_warn_c: f64,
    pub connector_critical_c: f64,
    pub default_core_warn_c: f64,
    pub default_core_critical_c: f64,
    pub default_hotspot_warn_c: f64,
    pub default_hotspot_critical_c: f64,
    pub memory_warn_c: f64,
    pub memory_critical_c: f64,
    pub overpower_warn_pct: f64,
    pub overpower_critical_pct: f64,
    pub alert_sustained_ms: u64,
    pub stage_step_ms: u64,
    pub shutdown_countdown_secs: u32,
}

impl Default for GuardPolicy {
    fn default() -> Self {
        Self {
            alerts_enabled: true,
            opt_in_lower_power_limit: false,
            opt_in_close_game: false,
            opt_in_shutdown_pc: false,
            power_limit_scale: 0.70,
            pin_warn_a: 8.5,
            pin_critical_a: 9.2,
            imbalance_min_total_a: 12.0,
            imbalance_warn_spread_a: 2.5,
            imbalance_critical_spread_a: 4.0,
            connector_warn_c: 80.0,
            connector_critical_c: 95.0,
            default_core_warn_c: 83.0,
            default_core_critical_c: 92.0,
            default_hotspot_warn_c: 100.0,
            default_hotspot_critical_c: 110.0,
            memory_warn_c: 98.0,
            memory_critical_c: 108.0,
            overpower_warn_pct: 105.0,
            overpower_critical_pct: 115.0,
            alert_sustained_ms: 3_000,
            stage_step_ms: 10_000,
            shutdown_countdown_secs: 30,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum GuardAction {
    None,
    AlertSustained,
    Countdown {
        next_stage: EscalationStage,
        remaining_ms: u64,
    },
    LowerPowerLimit {
        target_w: f64,
        previous_w: Option<f64>,
    },
    RestorePowerLimit {
        original_w: f64,
        reason: RestoreReason,
    },
    CloseGame,
    ShutdownCountdown {
        remaining_secs: u32,
        started_at_ms: u64,
        first_tick: bool,
    },
    ShutdownPc,
}

#[derive(Debug, Clone)]
pub struct GuardEvaluation {
    pub severity: Severity,
    pub alerts: Vec<GuardAlert>,
    pub fault_sustained_ms: u64,
    pub critical_sustained_ms: u64,
    pub action: GuardAction,
    pub stage1_done: bool,
    pub stage2_done: bool,
    pub stage3_done: bool,
}

pub trait ActionExecutor {
    fn lower_power_limit(&mut self, target_w: f64) -> Result<f64, String>;
    fn restore_power_limit(&mut self, original_w: f64) -> Result<f64, String>;
    fn close_game(&mut self) -> Result<(), String>;
    fn play_alert_sound(&mut self);
    fn shutdown_pc(&mut self) -> Result<(), String>;
}

#[derive(Debug, Default)]
pub struct MockActionExecutor {
    pub lowered_limits_w: Vec<f64>,
    pub restored_limits_w: Vec<f64>,
    pub game_closes: usize,
    pub alert_sounds: usize,
    pub pc_shutdowns: usize,
}

impl ActionExecutor for MockActionExecutor {
    fn lower_power_limit(&mut self, target_w: f64) -> Result<f64, String> {
        self.lowered_limits_w.push(target_w);
        Ok(target_w)
    }

    fn restore_power_limit(&mut self, original_w: f64) -> Result<f64, String> {
        self.restored_limits_w.push(original_w);
        Ok(original_w)
    }

    fn close_game(&mut self) -> Result<(), String> {
        self.game_closes += 1;
        Ok(())
    }

    fn play_alert_sound(&mut self) {
        self.alert_sounds += 1;
    }

    fn shutdown_pc(&mut self) -> Result<(), String> {
        self.pc_shutdowns += 1;
        Ok(())
    }
}

pub fn dispatch_action<E: ActionExecutor>(
    action: &GuardAction,
    exec: &mut E,
) -> Result<(), String> {
    match action {
        GuardAction::LowerPowerLimit { target_w, .. } => {
            exec.lower_power_limit(*target_w).map(|_| ())
        }
        GuardAction::RestorePowerLimit { original_w, .. } => {
            exec.restore_power_limit(*original_w).map(|_| ())
        }
        GuardAction::CloseGame => exec.close_game(),
        GuardAction::ShutdownCountdown { first_tick, .. } => {
            if *first_tick {
                exec.play_alert_sound();
            }
            Ok(())
        }
        GuardAction::ShutdownPc => exec.shutdown_pc(),
        _ => Ok(()),
    }
}

pub fn dispatch_action_with_journal<E: ActionExecutor>(
    action: &GuardAction,
    gpu_name: &str,
    now_ms: u64,
    journal: &PowerLimitJournal,
    exec: &mut E,
) -> Result<(), String> {
    match action {
        GuardAction::LowerPowerLimit {
            target_w,
            previous_w,
        } => {
            let original_w = previous_w.unwrap_or(*target_w);
            journal.write(&PowerLimitJournalEntry {
                gpu_name: gpu_name.to_string(),
                original_limit_w: original_w,
                lowered_limit_w: *target_w,
                written_at_ms: now_ms,
            })?;
            match exec.lower_power_limit(*target_w) {
                Ok(_) => Ok(()),
                Err(err) => {
                    let _ = journal.clear();
                    Err(err)
                }
            }
        }
        GuardAction::RestorePowerLimit { original_w, .. } => {
            exec.restore_power_limit(*original_w)?;
            journal.clear()?;
            Ok(())
        }
        other => dispatch_action(other, exec),
    }
}

pub fn recover_power_limit_from_journal<E: ActionExecutor>(
    journal: &PowerLimitJournal,
    exec: &mut E,
) -> Result<Option<PowerLimitJournalEntry>, String> {
    let Some(entry) = journal.read() else {
        return Ok(None);
    };
    exec.restore_power_limit(entry.original_limit_w)?;
    journal.clear()?;
    Ok(Some(entry))
}

pub struct PowerGuard {
    policy: GuardPolicy,
    fault_since_ms: Option<u64>,
    critical_since_ms: Option<u64>,
    stage_clock_ms: Option<u64>,
    alert_emitted: bool,
    stage1_done: bool,
    stage2_done: bool,
    stage3_done: bool,
    original_power_limit_w: Option<f64>,
    power_limit_lowered: bool,
    shutdown_countdown_started_ms: Option<u64>,
    shutdown_cancelled: bool,
}

impl PowerGuard {
    pub fn new(policy: GuardPolicy) -> Self {
        Self {
            policy,
            fault_since_ms: None,
            critical_since_ms: None,
            stage_clock_ms: None,
            alert_emitted: false,
            stage1_done: false,
            stage2_done: false,
            stage3_done: false,
            original_power_limit_w: None,
            power_limit_lowered: false,
            shutdown_countdown_started_ms: None,
            shutdown_cancelled: false,
        }
    }

    pub fn policy(&self) -> &GuardPolicy {
        &self.policy
    }

    pub fn set_policy(&mut self, policy: GuardPolicy) {
        self.policy = policy;
    }

    pub fn original_power_limit_w(&self) -> Option<f64> {
        self.original_power_limit_w
    }

    pub fn is_power_limit_lowered(&self) -> bool {
        self.power_limit_lowered
    }

    pub fn on_game_exited(&mut self) -> Option<GuardAction> {
        if !self.power_limit_lowered {
            return None;
        }
        self.power_limit_lowered = false;
        self.stage1_done = false;
        self.original_power_limit_w
            .take()
            .map(|original_w| GuardAction::RestorePowerLimit {
                original_w,
                reason: RestoreReason::GameExited,
            })
    }

    pub fn on_stop(&mut self) -> Option<GuardAction> {
        self.shutdown_countdown_started_ms = None;
        if !self.power_limit_lowered {
            return None;
        }
        self.power_limit_lowered = false;
        self.stage1_done = false;
        self.original_power_limit_w
            .take()
            .map(|original_w| GuardAction::RestorePowerLimit {
                original_w,
                reason: RestoreReason::MonitorStopped,
            })
    }

    pub fn cancel_shutdown_countdown(&mut self) -> bool {
        if self.shutdown_countdown_started_ms.take().is_some() {
            self.stage3_done = true;
            self.shutdown_cancelled = true;
            return true;
        }
        false
    }

    pub fn active_shutdown_remaining_secs(&self, now_ms: u64) -> Option<u32> {
        let start = self.shutdown_countdown_started_ms?;
        let total_ms = u64::from(self.policy.shutdown_countdown_secs) * 1_000;
        let elapsed = now_ms.saturating_sub(start);
        let rem_ms = total_ms.saturating_sub(elapsed);
        Some(rem_ms.div_ceil(1_000) as u32)
    }

    pub fn compute_reduced_power_limit_w(&self, snap: &GpuSnapshot) -> Option<f64> {
        let current = self
            .original_power_limit_w
            .or(snap.enforced_power_limit_w)
            .or(snap.power_limit_w)?;
        let scaled = (current * self.policy.power_limit_scale).round();
        let floor = snap.min_power_limit_w.unwrap_or((current * 0.50).round());
        Some(scaled.max(floor).min(current))
    }

    fn next_pending_stage(&self) -> Option<EscalationStage> {
        if self.policy.opt_in_lower_power_limit && !self.stage1_done {
            return Some(EscalationStage::LowerPowerLimit);
        }
        if self.policy.opt_in_close_game && !self.stage2_done {
            return Some(EscalationStage::CloseGame);
        }
        if self.policy.opt_in_shutdown_pc && !self.stage3_done && !self.shutdown_cancelled {
            return Some(EscalationStage::ShutdownPc);
        }
        None
    }

    pub fn evaluate(&mut self, snap: &GpuSnapshot) -> GuardEvaluation {
        let mut alerts = Vec::new();

        if let Some(conn) = &snap.connector {
            let max_a = conn.max_pin_current_a();
            if max_a >= self.policy.pin_critical_a {
                alerts.push(GuardAlert {
                    kind: FaultKind::PinOvercurrent,
                    severity: Severity::Critical,
                    message: format!(
                        "Connector pin current {max_a:.2} A reached critical limit {:.1} A",
                        self.policy.pin_critical_a
                    ),
                    measured: max_a,
                    threshold: self.policy.pin_critical_a,
                });
            } else if max_a >= self.policy.pin_warn_a {
                alerts.push(GuardAlert {
                    kind: FaultKind::PinOvercurrent,
                    severity: Severity::Warning,
                    message: format!(
                        "Connector pin current {max_a:.2} A above warning {:.1} A",
                        self.policy.pin_warn_a
                    ),
                    measured: max_a,
                    threshold: self.policy.pin_warn_a,
                });
            }

            if conn.total_current_a >= self.policy.imbalance_min_total_a {
                let spread = conn.imbalance_spread_a();
                if spread >= self.policy.imbalance_critical_spread_a {
                    alerts.push(GuardAlert {
                        kind: FaultKind::PinImbalance,
                        severity: Severity::Critical,
                        message: format!(
                            "Connector pin spread {spread:.2} A at {:.1} A total load (critical {:.1} A)",
                            conn.total_current_a, self.policy.imbalance_critical_spread_a
                        ),
                        measured: spread,
                        threshold: self.policy.imbalance_critical_spread_a,
                    });
                } else if spread >= self.policy.imbalance_warn_spread_a {
                    alerts.push(GuardAlert {
                        kind: FaultKind::PinImbalance,
                        severity: Severity::Warning,
                        message: format!(
                            "Connector pin spread {spread:.2} A at {:.1} A total load (warn {:.1} A)",
                            conn.total_current_a, self.policy.imbalance_warn_spread_a
                        ),
                        measured: spread,
                        threshold: self.policy.imbalance_warn_spread_a,
                    });
                }
            }

            if let Some(ct) = conn.max_connector_temp_c() {
                if ct >= self.policy.connector_critical_c {
                    alerts.push(GuardAlert {
                        kind: FaultKind::ConnectorOvertemp,
                        severity: Severity::Critical,
                        message: format!(
                            "Connector temperature {ct:.1} C reached critical {:.0} C",
                            self.policy.connector_critical_c
                        ),
                        measured: ct,
                        threshold: self.policy.connector_critical_c,
                    });
                } else if ct >= self.policy.connector_warn_c {
                    alerts.push(GuardAlert {
                        kind: FaultKind::ConnectorOvertemp,
                        severity: Severity::Warning,
                        message: format!(
                            "Connector temperature {ct:.1} C above warning {:.0} C",
                            self.policy.connector_warn_c
                        ),
                        measured: ct,
                        threshold: self.policy.connector_warn_c,
                    });
                }
            }

            if conn.hw_fault_bits != 0 {
                alerts.push(GuardAlert {
                    kind: FaultKind::ConnectorHwFault,
                    severity: Severity::Critical,
                    message: format!(
                        "Inline monitor hardware fault bitmask 0x{:04X}",
                        conn.hw_fault_bits
                    ),
                    measured: conn.hw_fault_bits as f64,
                    threshold: 0.0,
                });
            }
        }

        let core_warn = snap
            .temp_target_c
            .map(|t| t as f64)
            .unwrap_or(self.policy.default_core_warn_c);
        let core_crit = snap
            .temp_max_c
            .or(snap.temp_slowdown_c)
            .map(|t| t as f64)
            .unwrap_or(self.policy.default_core_critical_c);

        if let Some(c) = snap.core_temp_c {
            if c >= core_crit {
                alerts.push(GuardAlert {
                    kind: FaultKind::GpuCoreOvertemp,
                    severity: Severity::Critical,
                    message: format!("GPU core {c:.1} C reached limit {core_crit:.0} C"),
                    measured: c,
                    threshold: core_crit,
                });
            } else if c >= core_warn {
                alerts.push(GuardAlert {
                    kind: FaultKind::GpuCoreOvertemp,
                    severity: Severity::Warning,
                    message: format!("GPU core {c:.1} C above target {core_warn:.0} C"),
                    measured: c,
                    threshold: core_warn,
                });
            }
        }

        if let Some(hs) = snap.hotspot_temp_c {
            if hs >= self.policy.default_hotspot_critical_c {
                alerts.push(GuardAlert {
                    kind: FaultKind::GpuHotspotOvertemp,
                    severity: Severity::Critical,
                    message: format!(
                        "GPU hotspot {hs:.1} C reached limit {:.0} C",
                        self.policy.default_hotspot_critical_c
                    ),
                    measured: hs,
                    threshold: self.policy.default_hotspot_critical_c,
                });
            } else if hs >= self.policy.default_hotspot_warn_c {
                alerts.push(GuardAlert {
                    kind: FaultKind::GpuHotspotOvertemp,
                    severity: Severity::Warning,
                    message: format!(
                        "GPU hotspot {hs:.1} C above warning {:.0} C",
                        self.policy.default_hotspot_warn_c
                    ),
                    measured: hs,
                    threshold: self.policy.default_hotspot_warn_c,
                });
            }
        }

        if let Some(mem) = snap.memory_temp_c {
            if mem >= self.policy.memory_critical_c {
                alerts.push(GuardAlert {
                    kind: FaultKind::GpuMemoryOvertemp,
                    severity: Severity::Critical,
                    message: format!(
                        "VRAM junction {mem:.1} C reached critical {:.0} C",
                        self.policy.memory_critical_c
                    ),
                    measured: mem,
                    threshold: self.policy.memory_critical_c,
                });
            } else if mem >= self.policy.memory_warn_c {
                alerts.push(GuardAlert {
                    kind: FaultKind::GpuMemoryOvertemp,
                    severity: Severity::Warning,
                    message: format!(
                        "VRAM junction {mem:.1} C above warning {:.0} C",
                        self.policy.memory_warn_c
                    ),
                    measured: mem,
                    threshold: self.policy.memory_warn_c,
                });
            }
        }

        if let Some(pct) = snap.power_pct {
            if pct >= self.policy.overpower_critical_pct {
                alerts.push(GuardAlert {
                    kind: FaultKind::BoardOverpower,
                    severity: Severity::Critical,
                    message: format!(
                        "Board power at {pct:.1}% of enforced limit (critical {:.0}%)",
                        self.policy.overpower_critical_pct
                    ),
                    measured: pct,
                    threshold: self.policy.overpower_critical_pct,
                });
            } else if pct >= self.policy.overpower_warn_pct {
                alerts.push(GuardAlert {
                    kind: FaultKind::BoardOverpower,
                    severity: Severity::Warning,
                    message: format!(
                        "Board power at {pct:.1}% of enforced limit (warn {:.0}%)",
                        self.policy.overpower_warn_pct
                    ),
                    measured: pct,
                    threshold: self.policy.overpower_warn_pct,
                });
            }
        }

        if snap.throttle_flags & THROTTLE_HW_POWER_BRAKE != 0 {
            alerts.push(GuardAlert {
                kind: FaultKind::HwPowerBrake,
                severity: Severity::Critical,
                message: "Hardware power brake active (external power delivery fault or PSU drop)"
                    .to_string(),
                measured: 1.0,
                threshold: 0.0,
            });
        }
        if snap.throttle_flags & THROTTLE_HW_THERMAL != 0 {
            alerts.push(GuardAlert {
                kind: FaultKind::HwThermalSlowdown,
                severity: Severity::Critical,
                message: "Hardware thermal slowdown active".to_string(),
                measured: 1.0,
                threshold: 0.0,
            });
        }
        if snap.throttle_flags & THROTTLE_CURRENT_LIMIT != 0 {
            alerts.push(GuardAlert {
                kind: FaultKind::HwCurrentLimit,
                severity: Severity::Warning,
                message: "Hardware current limit active".to_string(),
                measured: 1.0,
                threshold: 0.0,
            });
        }

        let severity = alerts
            .iter()
            .map(|a| a.severity)
            .max()
            .unwrap_or(Severity::Normal);

        let fault_sustained_ms = if severity >= Severity::Warning {
            let start = *self.fault_since_ms.get_or_insert(snap.timestamp_ms);
            snap.timestamp_ms.saturating_sub(start)
        } else {
            self.fault_since_ms = None;
            self.alert_emitted = false;
            0
        };

        let (critical_sustained_ms, action) = if severity == Severity::Critical {
            let crit_start = *self.critical_since_ms.get_or_insert(snap.timestamp_ms);
            let crit_elapsed = snap.timestamp_ms.saturating_sub(crit_start);
            let stage_start = *self.stage_clock_ms.get_or_insert(snap.timestamp_ms);
            let stage_elapsed = snap.timestamp_ms.saturating_sub(stage_start);

            let act = if let Some(start_ms) = self.shutdown_countdown_started_ms {
                let total_ms = u64::from(self.policy.shutdown_countdown_secs) * 1_000;
                let cd_elapsed = snap.timestamp_ms.saturating_sub(start_ms);
                if cd_elapsed >= total_ms {
                    self.shutdown_countdown_started_ms = None;
                    self.stage3_done = true;
                    GuardAction::ShutdownPc
                } else {
                    let rem_secs = (total_ms - cd_elapsed).div_ceil(1_000) as u32;
                    GuardAction::ShutdownCountdown {
                        remaining_secs: rem_secs,
                        started_at_ms: start_ms,
                        first_tick: false,
                    }
                }
            } else if let Some(next_stage) = self.next_pending_stage() {
                if stage_elapsed >= self.policy.stage_step_ms {
                    self.stage_clock_ms = Some(snap.timestamp_ms);
                    match next_stage {
                        EscalationStage::LowerPowerLimit => {
                            self.stage1_done = true;
                            let previous_w = self
                                .original_power_limit_w
                                .or(snap.enforced_power_limit_w)
                                .or(snap.power_limit_w);
                            if !self.power_limit_lowered {
                                self.original_power_limit_w = previous_w;
                                self.power_limit_lowered = true;
                            }
                            let target_w =
                                self.compute_reduced_power_limit_w(snap).unwrap_or(150.0);
                            GuardAction::LowerPowerLimit {
                                target_w,
                                previous_w,
                            }
                        }
                        EscalationStage::CloseGame => {
                            self.stage2_done = true;
                            GuardAction::CloseGame
                        }
                        EscalationStage::ShutdownPc => {
                            if self.policy.shutdown_countdown_secs == 0 {
                                self.stage3_done = true;
                                GuardAction::ShutdownPc
                            } else {
                                self.shutdown_countdown_started_ms = Some(snap.timestamp_ms);
                                GuardAction::ShutdownCountdown {
                                    remaining_secs: self.policy.shutdown_countdown_secs,
                                    started_at_ms: snap.timestamp_ms,
                                    first_tick: true,
                                }
                            }
                        }
                    }
                } else if self.policy.alerts_enabled
                    && !self.alert_emitted
                    && fault_sustained_ms >= self.policy.alert_sustained_ms
                {
                    self.alert_emitted = true;
                    GuardAction::AlertSustained
                } else {
                    GuardAction::Countdown {
                        next_stage,
                        remaining_ms: self.policy.stage_step_ms - stage_elapsed,
                    }
                }
            } else if self.policy.alerts_enabled
                && !self.alert_emitted
                && fault_sustained_ms >= self.policy.alert_sustained_ms
            {
                self.alert_emitted = true;
                GuardAction::AlertSustained
            } else {
                GuardAction::None
            };
            (crit_elapsed, act)
        } else {
            self.critical_since_ms = None;
            self.stage_clock_ms = None;
            self.shutdown_countdown_started_ms = None;
            self.shutdown_cancelled = false;
            self.stage1_done = false;
            self.stage2_done = false;
            self.stage3_done = false;
            let act = if self.power_limit_lowered {
                self.power_limit_lowered = false;
                if let Some(original_w) = self.original_power_limit_w.take() {
                    GuardAction::RestorePowerLimit {
                        original_w,
                        reason: RestoreReason::FaultCleared,
                    }
                } else {
                    GuardAction::None
                }
            } else if severity == Severity::Warning
                && self.policy.alerts_enabled
                && !self.alert_emitted
                && fault_sustained_ms >= self.policy.alert_sustained_ms
            {
                self.alert_emitted = true;
                GuardAction::AlertSustained
            } else {
                GuardAction::None
            };
            (0, act)
        };

        GuardEvaluation {
            severity,
            alerts,
            fault_sustained_ms,
            critical_sustained_ms,
            action,
            stage1_done: self.stage1_done,
            stage2_done: self.stage2_done,
            stage3_done: self.stage3_done,
        }
    }
}
