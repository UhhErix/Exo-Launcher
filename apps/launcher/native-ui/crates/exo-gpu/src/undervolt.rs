use std::path::{Path, PathBuf};

use exo_sensors::whea::StabilityEventSummary;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TuneLimits {
    pub min_clock_offset_mhz: i32,
    pub max_clock_offset_mhz: i32,
    pub min_voltage_offset_mv: i32,
    pub max_voltage_offset_mv: i32,
    pub max_temp_c: u32,
    pub backoff_steps: u32,
}

impl Default for TuneLimits {
    fn default() -> Self {
        Self {
            min_clock_offset_mhz: -200,
            max_clock_offset_mhz: 250,
            min_voltage_offset_mv: -150,
            max_voltage_offset_mv: 0,
            max_temp_c: 82,
            backoff_steps: 2,
        }
    }
}

impl TuneLimits {
    pub fn validate(&self, clock_offset_mhz: i32, voltage_offset_mv: i32) -> Result<(), String> {
        if clock_offset_mhz < self.min_clock_offset_mhz
            || clock_offset_mhz > self.max_clock_offset_mhz
        {
            return Err(format!(
                "Clock offset {clock_offset_mhz} MHz outside safe bounds [{}..{}]",
                self.min_clock_offset_mhz, self.max_clock_offset_mhz
            ));
        }
        if voltage_offset_mv < self.min_voltage_offset_mv
            || voltage_offset_mv > self.max_voltage_offset_mv
        {
            return Err(format!(
                "Voltage offset {voltage_offset_mv} mV outside safe bounds [{}..{}]",
                self.min_voltage_offset_mv, self.max_voltage_offset_mv
            ));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UndervoltRestoreReason {
    InstabilityBackoff,
    GameExited,
    SessionStopped,
    WatchdogTimeout,
    WatchdogFault,
    CrashRecovery,
}

impl UndervoltRestoreReason {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::InstabilityBackoff => "instability-backoff",
            Self::GameExited => "game-exited",
            Self::SessionStopped => "session-stopped",
            Self::WatchdogTimeout => "watchdog-timeout",
            Self::WatchdogFault => "watchdog-fault",
            Self::CrashRecovery => "crash-recovery",
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UndervoltJournalEntry {
    pub gpu_name: String,
    pub original_clock_offset_mhz: i32,
    pub original_voltage_offset_mv: i32,
    pub applied_clock_offset_mhz: i32,
    pub applied_voltage_offset_mv: i32,
    pub verified: bool,
    pub written_at_ms: u64,
}

#[derive(Debug, Clone)]
pub struct UndervoltJournal {
    path: PathBuf,
}

impl UndervoltJournal {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn default_path() -> PathBuf {
        let base = std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .unwrap_or_else(std::env::temp_dir);
        base.join("ExoLauncher").join("exo-gpu-tune.journal")
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn write(&self, entry: &UndervoltJournalEntry) -> Result<(), String> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        let body = format!(
            "gpu={}\norig_clk={}\norig_mv={}\napplied_clk={}\napplied_mv={}\nverified={}\nwritten_at_ms={}\n",
            entry.gpu_name.replace(['\r', '\n'], " "),
            entry.original_clock_offset_mhz,
            entry.original_voltage_offset_mv,
            entry.applied_clock_offset_mhz,
            entry.applied_voltage_offset_mv,
            if entry.verified { 1 } else { 0 },
            entry.written_at_ms
        );
        let tmp = self.path.with_extension("journal.tmp");
        std::fs::write(&tmp, body).map_err(|e| e.to_string())?;
        if self.path.exists() {
            let _ = std::fs::remove_file(&self.path);
        }
        std::fs::rename(&tmp, &self.path).map_err(|e| e.to_string())
    }

    pub fn read(&self) -> Option<UndervoltJournalEntry> {
        let raw = std::fs::read_to_string(&self.path).ok()?;
        let mut gpu = None;
        let mut orig_clk = None;
        let mut orig_mv = None;
        let mut applied_clk = None;
        let mut applied_mv = None;
        let mut verified = None;
        let mut written_at_ms = None;
        for line in raw.lines() {
            if let Some(v) = line.strip_prefix("gpu=") {
                gpu = Some(v.trim().to_string());
            } else if let Some(v) = line.strip_prefix("orig_clk=") {
                orig_clk = v.trim().parse::<i32>().ok();
            } else if let Some(v) = line.strip_prefix("orig_mv=") {
                orig_mv = v.trim().parse::<i32>().ok();
            } else if let Some(v) = line.strip_prefix("applied_clk=") {
                applied_clk = v.trim().parse::<i32>().ok();
            } else if let Some(v) = line.strip_prefix("applied_mv=") {
                applied_mv = v.trim().parse::<i32>().ok();
            } else if let Some(v) = line.strip_prefix("verified=") {
                verified = Some(v.trim() == "1");
            } else if let Some(v) = line.strip_prefix("written_at_ms=") {
                written_at_ms = v.trim().parse::<u64>().ok();
            }
        }
        Some(UndervoltJournalEntry {
            gpu_name: gpu?,
            original_clock_offset_mhz: orig_clk?,
            original_voltage_offset_mv: orig_mv?,
            applied_clock_offset_mhz: applied_clk?,
            applied_voltage_offset_mv: applied_mv?,
            verified: verified?,
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

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct StressOutcome {
    pub temp_c: f64,
    pub watts: f64,
    pub clock_mhz: u32,
    pub crc_ok: bool,
    pub tdr_detected: bool,
    pub whea_detected: bool,
}

pub trait GpuControlBackend {
    fn current_offsets(&self) -> (i32, i32);
    fn apply_offset(&mut self, clock_offset_mhz: i32, voltage_offset_mv: i32)
    -> Result<(), String>;
    fn restore_offsets(
        &mut self,
        original_clock_mhz: i32,
        original_voltage_mv: i32,
    ) -> Result<(), String>;
    fn restore_stock(&mut self) -> Result<(), String>;
    fn run_stress_step(&mut self) -> StressOutcome;
}

#[derive(Debug, Clone)]
pub struct MockGpuBackend {
    pub clock_offset_mhz: i32,
    pub voltage_offset_mv: i32,
    pub stock_restores: usize,
    pub restored_history: Vec<(i32, i32)>,
    pub instability_voltage_mv: i32,
    pub base_watts: f64,
    pub base_temp_c: f64,
    pub base_clock_mhz: u32,
}

impl Default for MockGpuBackend {
    fn default() -> Self {
        Self {
            clock_offset_mhz: 0,
            voltage_offset_mv: 0,
            stock_restores: 0,
            restored_history: Vec::new(),
            instability_voltage_mv: -100,
            base_watts: 220.0,
            base_temp_c: 71.0,
            base_clock_mhz: 1935,
        }
    }
}

impl GpuControlBackend for MockGpuBackend {
    fn current_offsets(&self) -> (i32, i32) {
        (self.clock_offset_mhz, self.voltage_offset_mv)
    }

    fn apply_offset(
        &mut self,
        clock_offset_mhz: i32,
        voltage_offset_mv: i32,
    ) -> Result<(), String> {
        self.clock_offset_mhz = clock_offset_mhz;
        self.voltage_offset_mv = voltage_offset_mv;
        Ok(())
    }

    fn restore_offsets(
        &mut self,
        original_clock_mhz: i32,
        original_voltage_mv: i32,
    ) -> Result<(), String> {
        self.clock_offset_mhz = original_clock_mhz;
        self.voltage_offset_mv = original_voltage_mv;
        self.restored_history
            .push((original_clock_mhz, original_voltage_mv));
        Ok(())
    }

    fn restore_stock(&mut self) -> Result<(), String> {
        self.clock_offset_mhz = 0;
        self.voltage_offset_mv = 0;
        self.stock_restores += 1;
        Ok(())
    }

    fn run_stress_step(&mut self) -> StressOutcome {
        let reduction = (-self.voltage_offset_mv).max(0) as f64;
        let watts = (self.base_watts - reduction * 0.42).max(110.0);
        let temp_c = (self.base_temp_c - reduction * 0.08).max(45.0);
        let clock_mhz = (self.base_clock_mhz as i32 + self.clock_offset_mhz).max(1200) as u32;
        let unstable = self.voltage_offset_mv <= self.instability_voltage_mv;
        StressOutcome {
            temp_c,
            watts,
            clock_mhz,
            crc_ok: !unstable,
            tdr_detected: false,
            whea_detected: false,
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct UndervoltResult {
    pub opted_in: bool,
    pub applied_voltage_offset_mv: i32,
    pub applied_clock_offset_mhz: i32,
    pub before_watts: f64,
    pub after_watts: f64,
    pub before_temp_c: f64,
    pub after_temp_c: f64,
    pub clock_mhz: u32,
    pub aborted_reason: Option<String>,
}

pub fn run_opt_in_undervolt_search<B: GpuControlBackend>(
    backend: &mut B,
    limits: TuneLimits,
    user_opted_in: bool,
    step_mv: i32,
) -> Result<UndervoltResult, String> {
    if !user_opted_in {
        return Err("Undervolt write refused: explicit user opt-in required".into());
    }
    let step = step_mv.abs().clamp(5, 25);
    let baseline = backend.run_stress_step();
    let mut last_stable_mv = 0i32;
    let mut candidate_mv = -step;
    let mut abort_reason = None;

    while candidate_mv >= limits.min_voltage_offset_mv {
        limits.validate(0, candidate_mv)?;
        backend.apply_offset(0, candidate_mv)?;
        let out = backend.run_stress_step();
        if !out.crc_ok
            || out.tdr_detected
            || out.whea_detected
            || out.temp_c >= limits.max_temp_c as f64
        {
            abort_reason = Some(if !out.crc_ok {
                format!("CRC mismatch at {candidate_mv} mV; backed off")
            } else if out.tdr_detected {
                format!("TDR reset at {candidate_mv} mV; backed off")
            } else if out.whea_detected {
                format!("WHEA error at {candidate_mv} mV; backed off")
            } else {
                format!(
                    "Thermal ceiling {} C hit at {candidate_mv} mV",
                    limits.max_temp_c
                )
            });
            backend.restore_stock()?;
            break;
        }
        last_stable_mv = candidate_mv;
        candidate_mv -= step;
    }

    let safe_mv = (last_stable_mv + (limits.backoff_steps as i32) * step).min(0);
    if safe_mv < 0 {
        limits.validate(0, safe_mv)?;
        backend.apply_offset(0, safe_mv)?;
    } else {
        backend.restore_stock()?;
    }
    let final_out = backend.run_stress_step();

    Ok(UndervoltResult {
        opted_in: true,
        applied_voltage_offset_mv: safe_mv,
        applied_clock_offset_mhz: 0,
        before_watts: baseline.watts,
        after_watts: final_out.watts,
        before_temp_c: baseline.temp_c,
        after_temp_c: final_out.temp_c,
        clock_mhz: final_out.clock_mhz,
        aborted_reason: abort_reason,
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CrashGuardDecision {
    AllowReapply,
    PauseAndKeepStock { reason: String },
}

pub fn evaluate_boot_crash_window(
    recent_10m: &StabilityEventSummary,
    previous_step_unverified: bool,
) -> CrashGuardDecision {
    if previous_step_unverified {
        return CrashGuardDecision::PauseAndKeepStock {
            reason: "Previous tuning step did not finish verification before shutdown; kept stock."
                .into(),
        };
    }
    if recent_10m.kernel_power_41 > 0 || recent_10m.bugcheck_1001 > 0 {
        return CrashGuardDecision::PauseAndKeepStock {
            reason: format!(
                "Unexpected restart detected in 10 min window (KP41={}, BugCheck={}); auto-reapply paused.",
                recent_10m.kernel_power_41, recent_10m.bugcheck_1001
            ),
        };
    }
    if recent_10m.whea_errors > 0 || recent_10m.gpu_tdr_resets > 0 {
        return CrashGuardDecision::PauseAndKeepStock {
            reason: format!(
                "Hardware/driver instability in 10 min window (WHEA={}, TDR={}); auto-reapply paused.",
                recent_10m.whea_errors, recent_10m.gpu_tdr_resets
            ),
        };
    }
    CrashGuardDecision::AllowReapply
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CrashRecoveryOutcome {
    pub recovered_entry: Option<UndervoltJournalEntry>,
    pub decision: CrashGuardDecision,
}

pub fn recover_undervolt_from_journal<B: GpuControlBackend>(
    journal: &UndervoltJournal,
    backend: &mut B,
    recent_10m: &StabilityEventSummary,
) -> Result<CrashRecoveryOutcome, String> {
    let Some(entry) = journal.read() else {
        return Ok(CrashRecoveryOutcome {
            recovered_entry: None,
            decision: evaluate_boot_crash_window(recent_10m, false),
        });
    };
    backend.restore_offsets(
        entry.original_clock_offset_mhz,
        entry.original_voltage_offset_mv,
    )?;
    journal.clear()?;
    let decision = evaluate_boot_crash_window(recent_10m, !entry.verified);
    Ok(CrashRecoveryOutcome {
        recovered_entry: Some(entry),
        decision,
    })
}

pub struct SafeUndervoltSession<B: GpuControlBackend> {
    backend: B,
    journal: UndervoltJournal,
    limits: TuneLimits,
    gpu_name: String,
    watchdog_timeout_ms: u64,
    active_original: Option<(i32, i32)>,
    last_heartbeat_ms: u64,
    last_restore_reason: Option<UndervoltRestoreReason>,
}

impl<B: GpuControlBackend> SafeUndervoltSession<B> {
    pub fn new(
        backend: B,
        journal: UndervoltJournal,
        limits: TuneLimits,
        gpu_name: impl Into<String>,
        watchdog_timeout_ms: u64,
    ) -> Self {
        Self {
            backend,
            journal,
            limits,
            gpu_name: gpu_name.into(),
            watchdog_timeout_ms: watchdog_timeout_ms.max(1_000),
            active_original: None,
            last_heartbeat_ms: 0,
            last_restore_reason: None,
        }
    }

    pub fn backend(&self) -> &B {
        &self.backend
    }

    pub fn journal(&self) -> &UndervoltJournal {
        &self.journal
    }

    pub fn is_tuned(&self) -> bool {
        self.active_original.is_some()
    }

    pub fn last_restore_reason(&self) -> Option<UndervoltRestoreReason> {
        self.last_restore_reason
    }

    pub fn recover_on_startup(
        &mut self,
        recent_10m: &StabilityEventSummary,
    ) -> Result<CrashRecoveryOutcome, String> {
        let out = recover_undervolt_from_journal(&self.journal, &mut self.backend, recent_10m)?;
        if out.recovered_entry.is_some() {
            self.last_restore_reason = Some(UndervoltRestoreReason::CrashRecovery);
        }
        Ok(out)
    }

    pub fn apply_opt_in_undervolt(
        &mut self,
        user_opted_in: bool,
        step_mv: i32,
        now_ms: u64,
        recent_10m: &StabilityEventSummary,
    ) -> Result<UndervoltResult, String> {
        if !user_opted_in {
            return Err("Undervolt write refused: explicit user opt-in required".into());
        }
        let unverified_prior = self.journal.read().is_some_and(|e| !e.verified);
        if let CrashGuardDecision::PauseAndKeepStock { reason } =
            evaluate_boot_crash_window(recent_10m, unverified_prior)
        {
            return Err(reason);
        }

        let (orig_clk, orig_mv) = self
            .active_original
            .unwrap_or_else(|| self.backend.current_offsets());

        self.journal.write(&UndervoltJournalEntry {
            gpu_name: self.gpu_name.clone(),
            original_clock_offset_mhz: orig_clk,
            original_voltage_offset_mv: orig_mv,
            applied_clock_offset_mhz: orig_clk,
            applied_voltage_offset_mv: orig_mv,
            verified: false,
            written_at_ms: now_ms,
        })?;

        let result = match run_opt_in_undervolt_search(
            &mut self.backend,
            self.limits,
            user_opted_in,
            step_mv,
        ) {
            Ok(res) => res,
            Err(err) => {
                let _ = self.backend.restore_offsets(orig_clk, orig_mv);
                let _ = self.journal.clear();
                return Err(err);
            }
        };

        if result.applied_voltage_offset_mv < 0 || result.applied_clock_offset_mhz != 0 {
            self.journal.write(&UndervoltJournalEntry {
                gpu_name: self.gpu_name.clone(),
                original_clock_offset_mhz: orig_clk,
                original_voltage_offset_mv: orig_mv,
                applied_clock_offset_mhz: result.applied_clock_offset_mhz,
                applied_voltage_offset_mv: result.applied_voltage_offset_mv,
                verified: true,
                written_at_ms: now_ms,
            })?;
            self.active_original = Some((orig_clk, orig_mv));
            self.last_heartbeat_ms = now_ms;
        } else {
            let _ = self.backend.restore_offsets(orig_clk, orig_mv);
            let _ = self.journal.clear();
            self.active_original = None;
            self.last_restore_reason = Some(UndervoltRestoreReason::InstabilityBackoff);
        }
        Ok(result)
    }

    pub fn heartbeat(&mut self, now_ms: u64) {
        self.last_heartbeat_ms = now_ms;
    }

    pub fn tick_watchdog(
        &mut self,
        now_ms: u64,
        recent_stability: &StabilityEventSummary,
    ) -> Result<Option<UndervoltRestoreReason>, String> {
        if self.active_original.is_none() {
            return Ok(None);
        }
        if recent_stability.whea_errors > 0
            || recent_stability.gpu_tdr_resets > 0
            || recent_stability.kernel_power_41 > 0
            || recent_stability.bugcheck_1001 > 0
        {
            self.restore_internal(UndervoltRestoreReason::WatchdogFault)?;
            return Ok(Some(UndervoltRestoreReason::WatchdogFault));
        }
        if now_ms.saturating_sub(self.last_heartbeat_ms) >= self.watchdog_timeout_ms {
            self.restore_internal(UndervoltRestoreReason::WatchdogTimeout)?;
            return Ok(Some(UndervoltRestoreReason::WatchdogTimeout));
        }
        Ok(None)
    }

    pub fn on_game_exited(&mut self) -> Result<bool, String> {
        if self.active_original.is_none() {
            return Ok(false);
        }
        self.restore_internal(UndervoltRestoreReason::GameExited)?;
        Ok(true)
    }

    pub fn stop_and_restore(&mut self) -> Result<bool, String> {
        if self.active_original.is_none() {
            return Ok(false);
        }
        self.restore_internal(UndervoltRestoreReason::SessionStopped)?;
        Ok(true)
    }

    fn restore_internal(&mut self, reason: UndervoltRestoreReason) -> Result<(), String> {
        if let Some((orig_clk, orig_mv)) = self.active_original.take() {
            self.backend.restore_offsets(orig_clk, orig_mv)?;
            self.journal.clear()?;
            self.last_restore_reason = Some(reason);
        }
        Ok(())
    }
}

impl<B: GpuControlBackend> Drop for SafeUndervoltSession<B> {
    fn drop(&mut self) {
        let _ = self.restore_internal(UndervoltRestoreReason::SessionStopped);
    }
}
