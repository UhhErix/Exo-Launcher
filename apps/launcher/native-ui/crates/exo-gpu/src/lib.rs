pub mod nvidia_drs;
pub mod undervolt;

use exo_sensors::gpu::{GpuSnapshot, GpuVendor, discover_primary_gpu};
use exo_sensors::whea::query_boot_crash_window;

use crate::nvidia_drs::{NvidiaDrsSnapshot, read_global_drs_readonly};
use crate::undervolt::{CrashGuardDecision, UndervoltJournal, evaluate_boot_crash_window};

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
    let unverified_journal = UndervoltJournal::new(UndervoltJournal::default_path())
        .read()
        .is_some_and(|entry| !entry.verified);
    let boot_crash_guard = evaluate_boot_crash_window(&recent_10m, unverified_journal);

    GpuControlReadonlyOverview {
        gpu,
        nvidia_drs,
        boot_crash_guard,
    }
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use exo_sensors::whea::StabilityEventSummary;

    use crate::nvidia_drs::{
        DRS_LOW_LATENCY_MODE, DRS_POWER_MANAGEMENT, DRS_SHADER_CACHE_SIZE, format_drs_value,
    };
    use crate::undervolt::{
        CrashGuardDecision, MockGpuBackend, SafeUndervoltSession, TuneLimits, UndervoltJournal,
        UndervoltJournalEntry, UndervoltRestoreReason, evaluate_boot_crash_window,
        recover_undervolt_from_journal, run_opt_in_undervolt_search,
    };

    fn temp_journal(tag: &str) -> UndervoltJournal {
        let p: PathBuf =
            std::env::temp_dir().join(format!("exo-gpu-uv-{tag}-{}.journal", std::process::id()));
        let j = UndervoltJournal::new(p);
        let _ = j.clear();
        j
    }

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
    fn undervolt_journal_writes_before_apply_and_restores_original_on_game_exit_and_stop() {
        let journal = temp_journal("lifecycle");
        let clean = StabilityEventSummary::default();
        let mock = MockGpuBackend {
            clock_offset_mhz: 15,
            voltage_offset_mv: -10,
            instability_voltage_mv: -100,
            ..Default::default()
        };
        let mut session = SafeUndervoltSession::new(
            mock,
            journal.clone(),
            TuneLimits::default(),
            "RTX 3070",
            15_000,
        );

        let res = session
            .apply_opt_in_undervolt(true, 15, 10_000, &clean)
            .unwrap();
        assert_eq!(res.applied_voltage_offset_mv, -60);
        assert!(session.is_tuned());
        let saved = journal.read().expect("journal persisted");
        assert_eq!(saved.original_clock_offset_mhz, 15);
        assert_eq!(saved.original_voltage_offset_mv, -10);
        assert_eq!(saved.applied_voltage_offset_mv, -60);
        assert!(saved.verified);

        assert!(session.on_game_exited().unwrap());
        assert!(!session.is_tuned());
        assert_eq!(session.backend().clock_offset_mhz, 15);
        assert_eq!(session.backend().voltage_offset_mv, -10);
        assert_eq!(
            session.last_restore_reason(),
            Some(UndervoltRestoreReason::GameExited)
        );
        assert!(journal.read().is_none());

        let _ = session
            .apply_opt_in_undervolt(true, 15, 20_000, &clean)
            .unwrap();
        assert!(session.stop_and_restore().unwrap());
        assert_eq!(
            session.last_restore_reason(),
            Some(UndervoltRestoreReason::SessionStopped)
        );
        assert_eq!(session.backend().clock_offset_mhz, 15);
        assert_eq!(session.backend().voltage_offset_mv, -10);
        assert!(journal.read().is_none());
    }

    #[test]
    fn undervolt_crash_recovery_journal_restores_original_and_pauses_reapply_on_next_start() {
        let journal = temp_journal("crash");
        journal
            .write(&UndervoltJournalEntry {
                gpu_name: "RTX 3070".into(),
                original_clock_offset_mhz: 0,
                original_voltage_offset_mv: 0,
                applied_clock_offset_mhz: 0,
                applied_voltage_offset_mv: -90,
                verified: false,
                written_at_ms: 5_000,
            })
            .unwrap();

        let mut crashed_gpu = MockGpuBackend {
            clock_offset_mhz: 0,
            voltage_offset_mv: -90,
            ..Default::default()
        };
        let clean = StabilityEventSummary::default();
        let outcome = recover_undervolt_from_journal(&journal, &mut crashed_gpu, &clean).unwrap();
        assert!(outcome.recovered_entry.is_some());
        assert_eq!(crashed_gpu.clock_offset_mhz, 0);
        assert_eq!(crashed_gpu.voltage_offset_mv, 0);
        assert_eq!(crashed_gpu.restored_history, vec![(0, 0)]);
        assert!(journal.read().is_none());
        assert!(matches!(
            outcome.decision,
            CrashGuardDecision::PauseAndKeepStock { .. }
        ));
    }

    #[test]
    fn undervolt_runtime_watchdog_restores_original_on_timeout_and_whea_tdr() {
        let journal = temp_journal("watchdog");
        let clean = StabilityEventSummary::default();
        let mut session = SafeUndervoltSession::new(
            MockGpuBackend::default(),
            journal.clone(),
            TuneLimits::default(),
            "RTX 3070",
            10_000,
        );

        let _ = session
            .apply_opt_in_undervolt(true, 15, 1_000, &clean)
            .unwrap();
        session.heartbeat(5_000);
        assert_eq!(session.tick_watchdog(10_000, &clean).unwrap(), None);
        assert_eq!(
            session.tick_watchdog(15_000, &clean).unwrap(),
            Some(UndervoltRestoreReason::WatchdogTimeout)
        );
        assert!(!session.is_tuned());
        assert_eq!(session.backend().voltage_offset_mv, 0);
        assert!(journal.read().is_none());

        let _ = session
            .apply_opt_in_undervolt(true, 15, 20_000, &clean)
            .unwrap();
        let tdr = StabilityEventSummary {
            gpu_tdr_resets: 1,
            ..Default::default()
        };
        assert_eq!(
            session.tick_watchdog(21_000, &tdr).unwrap(),
            Some(UndervoltRestoreReason::WatchdogFault)
        );
        assert!(!session.is_tuned());
        assert_eq!(session.backend().voltage_offset_mv, 0);
        assert!(journal.read().is_none());

        assert!(
            session
                .apply_opt_in_undervolt(true, 15, 22_000, &tdr)
                .is_err()
        );
        assert!(!session.is_tuned());
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
