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
    fn apply_offset(&mut self, clock_offset_mhz: i32, voltage_offset_mv: i32)
    -> Result<(), String>;
    fn restore_stock(&mut self) -> Result<(), String>;
    fn run_stress_step(&mut self) -> StressOutcome;
}

#[derive(Debug, Clone)]
pub struct MockGpuBackend {
    pub clock_offset_mhz: i32,
    pub voltage_offset_mv: i32,
    pub stock_restores: usize,
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
            instability_voltage_mv: -100,
            base_watts: 220.0,
            base_temp_c: 71.0,
            base_clock_mhz: 1935,
        }
    }
}

impl GpuControlBackend for MockGpuBackend {
    fn apply_offset(
        &mut self,
        clock_offset_mhz: i32,
        voltage_offset_mv: i32,
    ) -> Result<(), String> {
        self.clock_offset_mhz = clock_offset_mhz;
        self.voltage_offset_mv = voltage_offset_mv;
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
