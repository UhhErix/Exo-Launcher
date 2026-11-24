pub mod backends;
pub mod guard;
pub mod monitor;
pub mod per_pin;
pub mod recorder;
pub mod sidecar;
pub mod types;
pub mod win32;

#[cfg(test)]
mod tests {
    use crate::backends::nvidia::{decode_astral_i2c_frame, map_nvml_throttle_bits};
    use crate::guard::{
        EscalationStage, FaultKind, GuardAction, GuardPolicy, MockActionExecutor, PowerGuard,
        PowerLimitJournal, RestoreReason, Severity, dispatch_action, dispatch_action_with_journal,
        recover_power_limit_from_journal,
    };
    use crate::per_pin::msi_psu::{
        MSI_AI1300TS_PID, MSI_AI1600TS_PID, MSI_HID_DISCOVERY_ACCESS, MSI_HID_OPEN_ACCESS,
        MSI_REPORT_BYTES, MSI_USB_VID, MockMsiHidSource, decode_connector_currents,
        decode_linear11, encode_linear11_unsigned, msi_model_name, read_primary_from_source,
    };
    use crate::per_pin::wireview::{
        MockWireViewSource, WIREVIEW_OPEN_ACCESS, decode_wireview_frame,
        encode_wireview_frame_for_test, extract_com_port, read_wireview_from_source,
    };
    use crate::recorder::SessionRecorder;
    use crate::sidecar::parse_snapshot_json;
    use crate::types::{
        BackendKind, GpuSnapshot, GpuVendor, PerPinSource, THROTTLE_HW_POWER_BRAKE,
        THROTTLE_HW_THERMAL, THROTTLE_SW_POWER_CAP, THROTTLE_SW_THERMAL,
    };

    fn sample_snapshot(ts: u64) -> GpuSnapshot {
        GpuSnapshot {
            timestamp_ms: ts,
            backend: BackendKind::NvidiaNvmlNvapi,
            vendor: GpuVendor::Nvidia,
            name: "NVIDIA GeForce RTX 3070".to_string(),
            pci_bus: Some(7),
            core_temp_c: Some(58.0),
            hotspot_temp_c: None,
            memory_temp_c: None,
            board_power_w: Some(185.0),
            power_limit_w: Some(240.0),
            enforced_power_limit_w: Some(240.0),
            min_power_limit_w: Some(100.0),
            max_power_limit_w: Some(270.0),
            power_pct: Some(77.08),
            gpu_clock_mhz: Some(1935),
            mem_clock_mhz: Some(7001),
            vram_used_mb: Some(3200),
            vram_total_mb: Some(8192),
            gpu_util_pct: Some(88),
            fan_pct: Some(60),
            fan_rpm: Some(1800),
            pstate: Some(0),
            pcie_gen: Some(4),
            pcie_width: Some(16),
            temp_target_c: Some(83),
            temp_max_c: Some(93),
            temp_slowdown_c: Some(95),
            throttle_flags: 0,
            connector: None,
        }
    }

    #[test]
    fn astral_i2c_frame_decodes_reverse_big_endian_pairs() {
        let mut raw = [0u8; 24];
        let words: [u16; 12] = [
            7200, 12100, 7100, 12090, 7300, 12080, 7400, 12110, 7000, 12050, 7250, 12070,
        ];
        for (i, word) in words.iter().enumerate() {
            let rev = 11 - i;
            let be = word.to_be_bytes();
            raw[rev * 2] = be[0];
            raw[rev * 2 + 1] = be[1];
        }
        let conn = decode_astral_i2c_frame(&raw);
        assert_eq!(conn.source, PerPinSource::AsusAstralI2c);
        assert_eq!(conn.pins.len(), 6);
        assert!((conn.pins[0].current_a - 7.2).abs() < 1e-6);
        assert!((conn.pins[0].voltage_v.unwrap() - 12.1).abs() < 1e-6);
        assert!((conn.pins[5].current_a - 7.25).abs() < 1e-6);
        assert!((conn.total_current_a - 43.25).abs() < 1e-6);
        assert!((conn.imbalance_spread_a() - 0.4).abs() < 1e-6);
    }

    #[test]
    fn wireview_pro2_frame_decodes_cross_gpu_pins_and_faults() {
        let frame = encode_wireview_frame_for_test(
            [12020, 12010, 12000, 11980, 11990, 12005],
            [6100, 6200, 9800, 4100, 6000, 6300],
            [625, 642, 410, 965],
            0,
            0x0004,
            0x000C,
        );
        let decoded = decode_wireview_frame(&frame).expect("valid 100-byte frame");
        assert_eq!(decoded.connector.source, PerPinSource::WireViewPro2);
        assert_eq!(decoded.connector.rated_cap_w, Some(600));
        assert_eq!(decoded.connector.hw_fault_bits, 0x0004);
        assert_eq!(decoded.fault_log_bits, 0x000C);
        assert!((decoded.temp_in_c - 62.5).abs() < 1e-6);
        assert!((decoded.ext_temp2_c - 96.5).abs() < 1e-6);
        assert!((decoded.connector.max_pin_current_a() - 9.8).abs() < 1e-6);
        assert!((decoded.connector.imbalance_spread_a() - 5.7).abs() < 1e-6);
    }

    #[test]
    fn msi_linear11_and_dual_connector_decode() {
        assert_eq!(
            msi_model_name(MSI_USB_VID, MSI_AI1300TS_PID),
            Some("MPG Ai1300TS")
        );
        assert_eq!(
            msi_model_name(MSI_USB_VID, MSI_AI1600TS_PID),
            Some("MPG Ai1600TS")
        );
        assert_eq!(msi_model_name(0x1234, MSI_AI1600TS_PID), None);

        let mut reply = [0u8; MSI_REPORT_BYTES];
        let c1 = [6.5, 6.25, 6.75, 6.0, 6.5, 7.0];
        let c2 = [1.0, 1.0, 1.0, 1.0, 1.0, 1.0];
        for i in 0..6 {
            let b1 = encode_linear11_unsigned(c1[i], -4);
            reply[9 + i * 2] = b1[0];
            reply[9 + i * 2 + 1] = b1[1];
            let b2 = encode_linear11_unsigned(c2[i], -4);
            reply[21 + i * 2] = b2[0];
            reply[21 + i * 2 + 1] = b2[1];
        }
        assert!((decode_linear11(reply[9], reply[10]) - 6.5).abs() < 1e-3);
        let conns = decode_connector_currents(&reply);
        assert_eq!(conns[0].connector_index, 1);
        assert_eq!(conns[1].connector_index, 2);
        assert!((conns[0].total_current_a - 39.0).abs() < 1e-2);
        assert!((conns[1].total_current_a - 6.0).abs() < 1e-2);
    }

    #[test]
    fn nvml_throttle_bit_mapping_preserves_hardware_faults() {
        let bits = 0x04 | 0x20 | 0x40 | 0x80;
        let mapped = map_nvml_throttle_bits(bits);
        assert_ne!(mapped & THROTTLE_SW_POWER_CAP, 0);
        assert_ne!(mapped & THROTTLE_SW_THERMAL, 0);
        assert_ne!(mapped & THROTTLE_HW_THERMAL, 0);
        assert_ne!(mapped & THROTTLE_HW_POWER_BRAKE, 0);
    }

    #[test]
    fn default_policy_alerts_on_sustained_fault_and_never_acts_automatically() {
        let mut guard = PowerGuard::new(GuardPolicy::default());
        assert!(guard.policy().alerts_enabled);
        assert!(!guard.policy().opt_in_lower_power_limit);
        assert!(!guard.policy().opt_in_close_game);
        assert!(!guard.policy().opt_in_shutdown_pc);

        let mut exec = MockActionExecutor::default();
        for step in 0..=30u64 {
            let mut s = sample_snapshot(10_000 + step * 1_000);
            s.throttle_flags = THROTTLE_HW_POWER_BRAKE;
            let ev = guard.evaluate(&s);
            let _ = dispatch_action(&ev.action, &mut exec);
            if step == 3 {
                assert_eq!(ev.action, GuardAction::AlertSustained);
            } else {
                assert_eq!(ev.action, GuardAction::None);
            }
        }
        assert!(exec.lowered_limits_w.is_empty());
        assert_eq!(exec.game_closes, 0);
        assert_eq!(exec.pc_shutdowns, 0);
    }

    #[test]
    fn opt_in_escalation_runs_lower_power_then_close_game_then_shutdown_via_mock() {
        let policy = GuardPolicy {
            opt_in_lower_power_limit: true,
            opt_in_close_game: true,
            opt_in_shutdown_pc: true,
            alert_sustained_ms: 2_000,
            stage_step_ms: 5_000,
            ..GuardPolicy::default()
        };
        let mut guard = PowerGuard::new(policy);
        let mut exec = MockActionExecutor::default();

        let bad_frame = encode_wireview_frame_for_test(
            [12000; 6],
            [10200, 9900, 2100, 2200, 6000, 6100],
            [970, 880, 420, 410],
            0,
            0,
            0,
        );
        let bad_conn = decode_wireview_frame(&bad_frame).unwrap().connector;

        let make_bad = |ts: u64| {
            let mut s = sample_snapshot(ts);
            s.connector = Some(bad_conn.clone());
            s
        };

        let ev0 = guard.evaluate(&make_bad(10_000));
        assert_eq!(ev0.severity, Severity::Critical);
        assert!(
            ev0.alerts
                .iter()
                .any(|a| a.kind == FaultKind::PinOvercurrent)
        );
        assert!(ev0.alerts.iter().any(|a| a.kind == FaultKind::PinImbalance));
        assert_eq!(
            ev0.action,
            GuardAction::Countdown {
                next_stage: EscalationStage::LowerPowerLimit,
                remaining_ms: 5_000,
            }
        );

        let ev2 = guard.evaluate(&make_bad(12_000));
        assert_eq!(ev2.action, GuardAction::AlertSustained);

        let ev5 = guard.evaluate(&make_bad(15_000));
        assert_eq!(
            ev5.action,
            GuardAction::LowerPowerLimit {
                target_w: 168.0,
                previous_w: Some(240.0),
            }
        );
        dispatch_action(&ev5.action, &mut exec).unwrap();
        assert_eq!(exec.lowered_limits_w, vec![168.0]);
        assert_eq!(exec.game_closes, 0);

        let ev10 = guard.evaluate(&make_bad(20_000));
        assert_eq!(ev10.action, GuardAction::CloseGame);
        dispatch_action(&ev10.action, &mut exec).unwrap();
        assert_eq!(exec.game_closes, 1);
        assert_eq!(exec.pc_shutdowns, 0);

        let ev15 = guard.evaluate(&make_bad(25_000));
        assert_eq!(
            ev15.action,
            GuardAction::ShutdownCountdown {
                remaining_secs: 30,
                started_at_ms: 25_000,
                first_tick: true,
            }
        );
        dispatch_action(&ev15.action, &mut exec).unwrap();
        assert_eq!(exec.alert_sounds, 1);
        assert_eq!(exec.pc_shutdowns, 0);

        let ev45 = guard.evaluate(&make_bad(55_000));
        assert_eq!(ev45.action, GuardAction::ShutdownPc);
        dispatch_action(&ev45.action, &mut exec).unwrap();
        assert_eq!(exec.pc_shutdowns, 1);

        let ev_ok = guard.evaluate(&sample_snapshot(56_000));
        assert_eq!(ev_ok.severity, Severity::Normal);
        assert_eq!(
            ev_ok.action,
            GuardAction::RestorePowerLimit {
                original_w: 240.0,
                reason: RestoreReason::FaultCleared,
            }
        );
        dispatch_action(&ev_ok.action, &mut exec).unwrap();
        assert_eq!(exec.restored_limits_w, vec![240.0]);
        assert!(!ev_ok.stage1_done && !ev_ok.stage2_done && !ev_ok.stage3_done);
    }

    #[test]
    fn wireview_and_msi_transports_are_strictly_read_only_with_zero_writes() {
        assert_eq!(WIREVIEW_OPEN_ACCESS, 0x8000_0000);
        assert_eq!(MSI_HID_OPEN_ACCESS, 0x8000_0000);
        assert_eq!(MSI_HID_DISCOVERY_ACCESS, 0);

        let wv_frame = encode_wireview_frame_for_test(
            [12010; 6],
            [6200, 6100, 6300, 6000, 6150, 6250],
            [520, 530, 410, 420],
            0,
            0,
            0,
        );
        let mut wv_src = MockWireViewSource::new(wv_frame);
        let decoded = read_wireview_from_source(&mut wv_src).expect("read-only WireView decode");
        assert_eq!(wv_src.read_calls, 1);
        assert_eq!(decoded.connector.pins.len(), 6);

        let mut reply = [0u8; MSI_REPORT_BYTES];
        for i in 0..6 {
            let b = encode_linear11_unsigned(6.0, -4);
            reply[9 + i * 2] = b[0];
            reply[9 + i * 2 + 1] = b[1];
        }
        let mut msi_src = MockMsiHidSource::new(reply);
        let msi_conn = read_primary_from_source(&mut msi_src).expect("read-only MSI decode");
        assert_eq!(msi_src.read_calls, 1);
        assert_eq!(msi_conn.source, PerPinSource::MsiPsuHid);
        assert!((msi_conn.total_current_a - 36.0).abs() < 1e-2);
    }

    #[test]
    fn stage1_restore_on_fault_clear_game_exit_stop_and_crash_recovery_journal() {
        let jpath = std::env::temp_dir().join(format!(
            "exo-pg-journal-test-{}.journal",
            std::process::id()
        ));
        let journal = PowerLimitJournal::new(&jpath);
        let _ = journal.clear();

        let policy = GuardPolicy {
            opt_in_lower_power_limit: true,
            stage_step_ms: 2_000,
            ..GuardPolicy::default()
        };

        let make_fault = |ts: u64| {
            let mut s = sample_snapshot(ts);
            s.throttle_flags = THROTTLE_HW_POWER_BRAKE;
            s
        };

        let mut guard = PowerGuard::new(policy.clone());
        let mut exec = MockActionExecutor::default();
        let _ = guard.evaluate(&make_fault(1_000));
        let ev = guard.evaluate(&make_fault(3_000));
        dispatch_action_with_journal(&ev.action, "RTX 3070", 3_000, &journal, &mut exec).unwrap();
        assert_eq!(exec.lowered_limits_w, vec![168.0]);
        let persisted = journal.read().expect("journal written before stage 1");
        assert!((persisted.original_limit_w - 240.0).abs() < 1e-6);

        let mut lowered_ok = sample_snapshot(4_000);
        lowered_ok.enforced_power_limit_w = Some(168.0);
        let ev_clear = guard.evaluate(&lowered_ok);
        assert_eq!(
            ev_clear.action,
            GuardAction::RestorePowerLimit {
                original_w: 240.0,
                reason: RestoreReason::FaultCleared,
            }
        );
        dispatch_action_with_journal(&ev_clear.action, "RTX 3070", 4_000, &journal, &mut exec)
            .unwrap();
        assert_eq!(exec.restored_limits_w, vec![240.0]);
        assert!(journal.read().is_none());

        let _ = guard.evaluate(&make_fault(10_000));
        let ev2 = guard.evaluate(&make_fault(12_000));
        dispatch_action_with_journal(&ev2.action, "RTX 3070", 12_000, &journal, &mut exec).unwrap();
        let exit_act = guard
            .on_game_exited()
            .expect("game exit restores original limit");
        assert_eq!(
            exit_act,
            GuardAction::RestorePowerLimit {
                original_w: 240.0,
                reason: RestoreReason::GameExited,
            }
        );
        dispatch_action_with_journal(&exit_act, "RTX 3070", 13_000, &journal, &mut exec).unwrap();
        assert_eq!(exec.restored_limits_w, vec![240.0, 240.0]);
        assert!(journal.read().is_none());

        let _ = guard.evaluate(&make_fault(20_000));
        let ev3 = guard.evaluate(&make_fault(22_000));
        dispatch_action_with_journal(&ev3.action, "RTX 3070", 22_000, &journal, &mut exec).unwrap();
        let stop_act = guard.on_stop().expect("stop restores original limit");
        assert_eq!(
            stop_act,
            GuardAction::RestorePowerLimit {
                original_w: 240.0,
                reason: RestoreReason::MonitorStopped,
            }
        );
        dispatch_action_with_journal(&stop_act, "RTX 3070", 23_000, &journal, &mut exec).unwrap();
        assert_eq!(exec.restored_limits_w, vec![240.0, 240.0, 240.0]);
        assert!(journal.read().is_none());

        let mut crashed_guard = PowerGuard::new(policy);
        let _ = crashed_guard.evaluate(&make_fault(30_000));
        let ev4 = crashed_guard.evaluate(&make_fault(32_000));
        dispatch_action_with_journal(&ev4.action, "RTX 3070", 32_000, &journal, &mut exec).unwrap();
        drop(crashed_guard);
        assert!(journal.read().is_some());

        let recovered = recover_power_limit_from_journal(&journal, &mut exec)
            .unwrap()
            .expect("crash recovery restores original limit");
        assert!((recovered.original_limit_w - 240.0).abs() < 1e-6);
        assert_eq!(exec.restored_limits_w, vec![240.0, 240.0, 240.0, 240.0]);
        assert!(journal.read().is_none());
    }

    #[test]
    fn stage3_shutdown_30s_countdown_cancel_aborts_shutdown_and_logs_events() {
        let policy = GuardPolicy {
            opt_in_shutdown_pc: true,
            stage_step_ms: 2_000,
            ..GuardPolicy::default()
        };
        assert_eq!(policy.shutdown_countdown_secs, 30);
        let mut guard = PowerGuard::new(policy);
        let mut exec = MockActionExecutor::default();
        let mut rec = SessionRecorder::new(64);

        let make_fault = |ts: u64| {
            let mut s = sample_snapshot(ts);
            s.throttle_flags = THROTTLE_HW_POWER_BRAKE;
            s
        };

        let _ = guard.evaluate(&make_fault(10_000));
        let ev_cd = guard.evaluate(&make_fault(12_000));
        assert_eq!(
            ev_cd.action,
            GuardAction::ShutdownCountdown {
                remaining_secs: 30,
                started_at_ms: 12_000,
                first_tick: true,
            }
        );
        dispatch_action(&ev_cd.action, &mut exec).unwrap();
        rec.record_event(
            12_000,
            "shutdown-countdown-started",
            "30 s cancelable countdown started",
        );
        assert_eq!(exec.alert_sounds, 1);
        assert_eq!(exec.pc_shutdowns, 0);

        let ev_mid = guard.evaluate(&make_fault(27_000));
        assert_eq!(
            ev_mid.action,
            GuardAction::ShutdownCountdown {
                remaining_secs: 15,
                started_at_ms: 12_000,
                first_tick: false,
            }
        );
        assert!(guard.cancel_shutdown_countdown());
        rec.record_event(
            27_000,
            "shutdown-cancelled",
            "User cancelled Stage 3 shutdown countdown",
        );

        for step in 1..=40u64 {
            let ev_after = guard.evaluate(&make_fault(27_000 + step * 1_000));
            dispatch_action(&ev_after.action, &mut exec).unwrap();
        }
        assert_eq!(exec.pc_shutdowns, 0);
        assert_eq!(rec.events().len(), 2);
        assert_eq!(rec.events()[0].kind, "shutdown-countdown-started");
        assert_eq!(rec.events()[1].kind, "shutdown-cancelled");
        assert!(rec.export_html().contains("shutdown-cancelled"));
    }

    #[test]
    fn sidecar_json_round_trips_full_snapshot_and_connector() {
        let frame = encode_wireview_frame_for_test(
            [12020, 12010, 12000, 11980, 11990, 12005],
            [6100, 6200, 6300, 6000, 6050, 6150],
            [525, 540, 410, 420],
            0,
            0,
            0,
        );
        let mut snap = sample_snapshot(123_456_789);
        snap.connector = Some(decode_wireview_frame(&frame).unwrap().connector);
        let json = snap.to_json();
        let parsed = parse_snapshot_json(&json).expect("round-trip snapshot");
        assert_eq!(parsed.timestamp_ms, snap.timestamp_ms);
        assert_eq!(parsed.backend, snap.backend);
        assert_eq!(parsed.vendor, snap.vendor);
        assert_eq!(parsed.name, snap.name);
        assert_eq!(parsed.min_power_limit_w, snap.min_power_limit_w);
        assert_eq!(parsed.connector.as_ref().unwrap().pins.len(), 6);
        assert_eq!(
            parsed.connector.as_ref().unwrap().connector_temps_c.len(),
            4
        );
    }

    #[test]
    fn capabilities_honestly_report_shunt_requirements_per_gpu() {
        let rtx3070 = sample_snapshot(1_000);
        let caps = rtx3070.capabilities();
        assert!(caps.has_board_power_w);
        assert!(caps.has_power_limit_control);
        assert!(!caps.has_per_pin_shunts);
        assert!(
            caps.limitation_note
                .as_deref()
                .unwrap()
                .contains("shunt hardware")
        );

        let mut d3d = sample_snapshot(1_000);
        d3d.backend = BackendKind::WindowsD3dkmt;
        d3d.board_power_w = None;
        let d3d_caps = d3d.capabilities();
        assert!(!d3d_caps.has_board_power_w);
        assert!(d3d_caps.limitation_note.is_some());
    }

    #[test]
    fn guard_works_without_per_pin_shunts_for_amd_intel_and_older_nvidia() {
        let mut guard = PowerGuard::new(GuardPolicy::default());
        let mut amd_snap = sample_snapshot(1_000);
        amd_snap.backend = BackendKind::AmdAdlx;
        amd_snap.vendor = GpuVendor::Amd;
        amd_snap.name = "AMD Radeon RX 9070 XT".to_string();
        amd_snap.hotspot_temp_c = Some(112.0);
        amd_snap.throttle_flags = THROTTLE_HW_POWER_BRAKE;

        let ev = guard.evaluate(&amd_snap);
        assert_eq!(ev.severity, Severity::Critical);
        assert!(
            ev.alerts
                .iter()
                .any(|a| a.kind == FaultKind::GpuHotspotOvertemp)
        );
        assert!(ev.alerts.iter().any(|a| a.kind == FaultKind::HwPowerBrake));
    }

    #[test]
    fn recorder_caps_capacity_and_exports_valid_html() {
        let mut rec = SessionRecorder::new(3);
        for i in 0..5u64 {
            let mut s = sample_snapshot(1_000 + i * 1_000);
            s.board_power_w = Some(100.0 + (i as f64) * 25.0);
            rec.push(s);
        }
        assert_eq!(rec.len(), 3);
        let sum = rec.summarize().unwrap();
        assert_eq!(sum.samples, 3);
        assert_eq!(sum.duration_ms, 2_000);
        assert!((sum.power_w.as_ref().unwrap().min - 150.0).abs() < 1e-6);
        assert!((sum.power_w.as_ref().unwrap().max - 200.0).abs() < 1e-6);

        let html = rec.export_html();
        assert!(html.contains("<svg"));
        assert!(html.contains("Exo Power Guard Session"));
    }

    #[test]
    fn wireview_com_port_extractor_handles_windows_friendly_names() {
        assert_eq!(
            extract_com_port("STMicroelectronics Virtual COM Port (COM7)"),
            Some("COM7".to_string())
        );
        assert_eq!(
            extract_com_port("USB Serial Device (COM14)"),
            Some("COM14".to_string())
        );
        assert_eq!(extract_com_port("Generic USB Hub"), None);
    }
}
