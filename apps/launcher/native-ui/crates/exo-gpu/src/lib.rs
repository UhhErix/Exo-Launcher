pub mod nvidia_drs;
pub mod undervolt;

use exo_sensors::gpu::{GpuSnapshot, GpuVendor, discover_primary_gpu};
use exo_sensors::whea::query_boot_crash_window;

use crate::nvidia_drs::{NvidiaDrsSnapshot, read_global_drs_readonly};
use crate::undervolt::{CrashGuardDecision, evaluate_boot_crash_window};

#[derive(Debug, Clone)]
pub struct GpuControlReadonlyOverview {
    pub gpu: Option<GpuSnapshot>,
    pub nvidia_drs: Option<NvidiaDrsSnapshot>,
    pub boot_crash_guard: CrashGuardDecision,
}

pub fn read_gpu_control_overview() -> GpuControlReadonlyOverview {
    let gpu = discover_primary_gpu().and_then(|mut b| b.read());
    let nvidia_drs = if gpu.as_ref().is_some_and(|g| g.vendor == GpuVendor::Nvidia) {
        read_global_drs_readonly()
    } else {
        None
    };
    let recent_10m = query_boot_crash_window(10);
    let boot_crash_guard = evaluate_boot_crash_window(&recent_10m, false);

    GpuControlReadonlyOverview {
        gpu,
        nvidia_drs,
        boot_crash_guard,
    }
}

#[cfg(test)]
mod tests {
    use exo_sensors::whea::StabilityEventSummary;

    use crate::nvidia_drs::{
        DRS_LOW_LATENCY_MODE, DRS_POWER_MANAGEMENT, DRS_SHADER_CACHE_SIZE, format_drs_value,
    };
    use crate::undervolt::{
        CrashGuardDecision, MockGpuBackend, TuneLimits, evaluate_boot_crash_window,
        run_opt_in_undervolt_search,
    };

    #[test]
    fn tune_limits_reject_out_of_range_clocks_and_voltages() {
        let lim = TuneLimits::default();
        assert!(lim.validate(0, -75).is_ok());
        assert!(lim.validate(500, 0).is_err());
        assert!(lim.validate(0, -250).is_err());
        assert!(lim.validate(0, 25).is_err());
    }

    #[test]
    fn undervolt_requires_explicit_opt_in_and_backs_off_two_steps_on_mock() {
        let mut mock = MockGpuBackend {
            instability_voltage_mv: -100,
            ..Default::default()
        };
        assert!(run_opt_in_undervolt_search(&mut mock, TuneLimits::default(), false, 15).is_err());
        assert_eq!(mock.voltage_offset_mv, 0);

        let res = run_opt_in_undervolt_search(&mut mock, TuneLimits::default(), true, 15).unwrap();
        assert!(res.opted_in);
        assert_eq!(res.applied_voltage_offset_mv, -60);
        assert!(res.after_watts < res.before_watts);
        assert!(res.after_temp_c < res.before_temp_c);
        assert!(res.aborted_reason.is_some());
        assert!(mock.stock_restores >= 1);
    }

    #[test]
    fn crash_window_watchdog_pauses_on_kp41_whea_tdr_or_unverified_step() {
        let clean = StabilityEventSummary::default();
        assert_eq!(
            evaluate_boot_crash_window(&clean, false),
            CrashGuardDecision::AllowReapply
        );
        assert!(matches!(
            evaluate_boot_crash_window(&clean, true),
            CrashGuardDecision::PauseAndKeepStock { .. }
        ));
        let kp41 = StabilityEventSummary {
            kernel_power_41: 1,
            ..Default::default()
        };
        assert!(matches!(
            evaluate_boot_crash_window(&kp41, false),
            CrashGuardDecision::PauseAndKeepStock { .. }
        ));
        let whea = StabilityEventSummary {
            whea_errors: 1,
            ..Default::default()
        };
        assert!(matches!(
            evaluate_boot_crash_window(&whea, false),
            CrashGuardDecision::PauseAndKeepStock { .. }
        ));
    }

    #[test]
    fn drs_value_formatter_covers_key_global_settings() {
        assert_eq!(
            format_drs_value(DRS_SHADER_CACHE_SIZE, Some(0xFFFF_FFFF)),
            "Unlimited"
        );
        assert_eq!(format_drs_value(DRS_LOW_LATENCY_MODE, Some(2)), "Ultra");
        assert_eq!(
            format_drs_value(DRS_POWER_MANAGEMENT, Some(1)),
            "Prefer maximum performance"
        );
        assert_eq!(
            format_drs_value(DRS_LOW_LATENCY_MODE, None),
            "Driver default"
        );
    }
}
