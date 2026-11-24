pub mod auto_profiles;
pub mod diag;
pub mod gameinput;
pub mod sony_calibrate;
pub mod sony_hid;
pub mod steam_conflict;
pub mod switch_hid;
pub mod xbox_hid;

use gameinput::{ControllerFamily, GameInputProbe, probe_gameinput_runtime};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ConnectedController {
    pub id: String,
    pub name: String,
    pub family: ControllerFamily,
    pub usb_wired: bool,
    pub battery_pct: Option<u8>,
    pub active_onboard_slot: Option<u8>,
}

pub trait ControllerTransport {
    fn write_output(&mut self, device_id: &str, report: &[u8]) -> Result<(), String>;
    fn set_feature(&mut self, device_id: &str, report_id: u8, payload: &[u8])
    -> Result<(), String>;
    fn get_feature(&mut self, device_id: &str, report_id: u8) -> Result<Vec<u8>, String>;
}

#[derive(Default)]
pub struct MockControllerTransport {
    pub written_outputs: Vec<(String, Vec<u8>)>,
    pub written_features: Vec<(String, u8, Vec<u8>)>,
    pub feature_replies: std::collections::HashMap<u8, Vec<u8>>,
}

impl ControllerTransport for MockControllerTransport {
    fn write_output(&mut self, device_id: &str, report: &[u8]) -> Result<(), String> {
        self.written_outputs
            .push((device_id.to_string(), report.to_vec()));
        Ok(())
    }

    fn set_feature(
        &mut self,
        device_id: &str,
        report_id: u8,
        payload: &[u8],
    ) -> Result<(), String> {
        self.written_features
            .push((device_id.to_string(), report_id, payload.to_vec()));
        Ok(())
    }

    fn get_feature(&mut self, _device_id: &str, report_id: u8) -> Result<Vec<u8>, String> {
        self.feature_replies
            .get(&report_id)
            .cloned()
            .ok_or_else(|| format!("No mock feature reply for 0x{report_id:02X}"))
    }
}

pub struct ControllerHub {
    pub probe: GameInputProbe,
    pub controllers: Vec<ConnectedController>,
}

impl Default for ControllerHub {
    fn default() -> Self {
        Self::new()
    }
}

impl ControllerHub {
    pub fn new() -> Self {
        Self {
            probe: probe_gameinput_runtime(),
            controllers: Vec::new(),
        }
    }

    pub fn apply_dualsense_settings<T: ControllerTransport>(
        transport: &mut T,
        pad: &ConnectedController,
        settings: &sony_hid::DualSenseSettings,
    ) -> Result<(), String> {
        if pad.usb_wired {
            let len = if pad.family == ControllerFamily::DualSenseEdge {
                64
            } else {
                48
            };
            let report = sony_hid::build_usb_settings_report(settings, len)?;
            if !sony_hid::is_only_settings_report(&report) {
                return Err("Output report failed settings-only safety validator.".into());
            }
            transport.write_output(&pad.id, &report)
        } else {
            let report = sony_hid::build_bt_settings_report(settings)?;
            if !sony_hid::verify_bt_output_crc(&report) {
                return Err("Bluetooth CRC-32 validation failed.".into());
            }
            transport.write_output(&pad.id, &report)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use auto_profiles::{
        ControllerAntiCheatClass, ControllerRemapMode, GameControllerProfile,
        LOW_BATTERY_AMBER_HEX, TriggerPreset, resolve_controller_profile,
    };
    use sony_calibrate::{
        CalibrationSession, CalibrationStep, EdgeOnboardProfile, parse_edge_slot,
    };
    use sony_hid::{
        BT_OUTPUT_CRC_SEED, DualSenseSettings, MotorStrength, build_bt_settings_report,
        build_usb_settings_report, crc32_sony_bt, is_feature_allowed, is_only_settings_report,
        parse_dualsense_input, verify_bt_output_crc,
    };
    use std::collections::BTreeMap;
    use steam_conflict::{
        ControllerSupportLevel, PLAYERS_OVERRIDE_MARKER, STEAM_DESKTOP_LAYOUT_APP_ID,
        SteamAppControllerInfo, apply_vdf_writes, desired_steam_input, plan_steam_input_fixes,
    };

    #[test]
    fn sony_crc32_a2_seed_and_usb_settings_report_match_controller_lab_parity() {
        let settings = DualSenseSettings {
            light: Some("#1E64FF".into()),
            player_light: true,
            mic_off: true,
            speaker_off: false,
            vibration: MotorStrength::Medium,
            trigger_effects: MotorStrength::Low,
        };
        let usb = build_usb_settings_report(&settings, 64).expect("usb report");
        assert_eq!(usb[0], 0x02);
        assert_eq!(usb[2], 0x02 | 0x04 | 0x10 | 0x40);
        assert_eq!(usb[10], 0x10);
        assert_eq!(usb[37], 3 | (5 << 4));
        assert_eq!(usb[39], 0x02);
        assert_eq!(usb[42], 0x02);
        assert_eq!(usb[44], 0x04);
        assert_eq!(&usb[45..48], &[0x1E, 0x64, 0xFF]);
        assert!(is_only_settings_report(&usb));

        let bt = build_bt_settings_report(&settings).expect("bt report");
        assert_eq!(bt[0], 0x31);
        assert_eq!(bt[1], 0x02);
        assert!(verify_bt_output_crc(&bt));
        let manual = crc32_sony_bt(BT_OUTPUT_CRC_SEED, &bt[..74]);
        assert_eq!(u32::from_le_bytes([bt[74], bt[75], bt[76], bt[77]]), manual);

        assert!(!is_feature_allowed(0x08, &[0x01], true, true));
        assert!(!is_feature_allowed(0x60, &[0x00], true, true));
        assert!(is_feature_allowed(0x70, &[], false, true));
        assert!(!is_feature_allowed(0x70, &[], false, false));
    }

    #[test]
    fn dualsense_edge_onboard_profile_round_trips_losslessly() {
        let profile = EdgeOnboardProfile {
            slot_number: 2,
            name: "FPS Hair-Trigger".into(),
            left_stick_curve: 2,
            right_stick_curve: 3,
            left_deadzone_pct: 4,
            right_deadzone_pct: 5,
            back_left_remap: 0x01,
            back_right_remap: 0x02,
        };
        let encoded = profile.to_slot_report().expect("encode slot");
        assert_eq!(encoded[0], 0x73);
        let decoded = EdgeOnboardProfile::from_slot_report(2, &encoded).expect("decode slot");
        assert_eq!(decoded, profile);

        let mut empty = [0u8; 64];
        empty[0] = 0x76;
        empty[1] = 0x10;
        let summary = parse_edge_slot(3, &empty).expect("empty slot");
        assert!(summary.empty);
        assert!(summary.name.is_none());
    }

    #[test]
    fn calibration_requires_usb_and_explicit_confirmation_and_uses_mock_transport() {
        let mut bt_session = CalibrationSession::new(false, false);
        assert!(bt_session.begin_center().is_err());

        let mut usb_session = CalibrationSession::new(true, false);
        usb_session.begin_center().unwrap();
        usb_session.sample_center().unwrap();
        usb_session.end_center_and_begin_range().unwrap();
        usb_session.end_range().unwrap();
        assert_eq!(usb_session.step, CalibrationStep::ReadyToCommit);
        assert!(usb_session.commit_permanent_flash().is_err());

        usb_session.user_confirmed_flash = true;
        usb_session.commit_permanent_flash().unwrap();
        assert_eq!(usb_session.step, CalibrationStep::Committed);

        let mut mock = MockControllerTransport::default();
        let pad = ConnectedController {
            id: "mock-edge".into(),
            name: "DualSense Edge".into(),
            family: ControllerFamily::DualSenseEdge,
            usb_wired: true,
            battery_pct: Some(12),
            active_onboard_slot: Some(1),
        };
        ControllerHub::apply_dualsense_settings(&mut mock, &pad, &DualSenseSettings::default())
            .unwrap();
        assert_eq!(mock.written_outputs.len(), 1);
    }

    #[test]
    fn steam_input_conflict_resolver_fixes_native_ps_games_and_respects_player_override() {
        let native_ps = SteamAppControllerInfo {
            app_id: "1091500".into(),
            is_game: true,
            support: ControllerSupportLevel::Full,
            reads_playstation: true,
        };
        let partial = SteamAppControllerInfo {
            app_id: "413150".into(),
            is_game: true,
            support: ControllerSupportLevel::Partial,
            reads_playstation: false,
        };
        assert_eq!(desired_steam_input(&native_ps), Some("0"));
        assert_eq!(desired_steam_input(&partial), Some("2"));

        let mut vdf = BTreeMap::from([("1091500".into(), "2".into())]);
        let rules = BTreeMap::from([
            ("1091500".into(), desired_steam_input(&native_ps)),
            ("413150".into(), desired_steam_input(&partial)),
        ]);
        let plan = plan_steam_input_fixes(&vdf, &rules, &BTreeMap::new());
        assert_eq!(plan.writes.get("1091500").map(String::as_str), Some("0"));
        assert_eq!(plan.writes.get("413150").map(String::as_str), Some("2"));
        assert_eq!(
            plan.writes
                .get(STEAM_DESKTOP_LAYOUT_APP_ID)
                .map(String::as_str),
            Some("0")
        );
        assert_eq!(plan.conflicts_fixed, 1);

        apply_vdf_writes(&mut vdf, &plan.writes);
        vdf.insert("1091500".into(), "2".into());
        let plan_after_player_edit = plan_steam_input_fixes(&vdf, &rules, &plan.next_record);
        assert_eq!(
            plan_after_player_edit
                .next_record
                .get("1091500")
                .map(String::as_str),
            Some(PLAYERS_OVERRIDE_MARKER)
        );
        assert!(!plan_after_player_edit.writes.contains_key("1091500"));
    }

    #[test]
    fn diagnostics_detect_drift_over_4_percent_and_ac_blocks_software_remap() {
        let drifted: Vec<(u8, u8)> = vec![(140, 128); 30];
        let rest = diag::analyze_rest(&drifted).expect("rest diag");
        assert!(rest.drift_warning);
        assert!(rest.radial_offset_pct > 4.0);

        let circle: Vec<(u8, u8)> = (0..72)
            .map(|i| {
                let a = (i as f64) * std::f64::consts::TAU / 72.0;
                let x = (127.5 + a.cos() * 127.0).clamp(0.0, 255.0) as u8;
                let y = (127.5 + a.sin() * 127.0).clamp(0.0, 255.0) as u8;
                (x, y)
            })
            .collect();
        let circ = diag::analyze_circularity(&circle).expect("circularity");
        assert_eq!(circ.covered_bins, 36);
        assert!(circ.avg_error_pct < 2.0);

        let profile = GameControllerProfile {
            id: "gyro-test".into(),
            name: "Gyro Aim".into(),
            game_ids: vec!["riot:valorant".into()],
            edge_slot: Some(1),
            trigger_preset: TriggerPreset::FpsHairTrigger,
            lightbar: Some("#00C8FF".into()),
            remap_mode: ControllerRemapMode::OfflineSoftwareGyroOrRemap,
        };
        for cls in [
            ControllerAntiCheatClass::CompetitivePvp,
            ControllerAntiCheatClass::OnlinePveGacha,
            ControllerAntiCheatClass::Unknown,
        ] {
            let res = resolve_controller_profile(&profile, cls, Some(10));
            assert!(!res.software_remap_active);
            assert!(res.blocked_reason.is_some());
            assert!(res.low_battery_warning);
            assert_eq!(res.settings.light.as_deref(), Some(LOW_BATTERY_AMBER_HEX));
        }

        let offline = resolve_controller_profile(
            &profile,
            ControllerAntiCheatClass::OfflineSinglePlayer,
            Some(80),
        );
        assert!(offline.software_remap_active);
        assert!(!offline.low_battery_warning);
        assert!(offline.blocked_reason.is_none());

        let mut usb_in = [0u8; 64];
        usb_in[0] = 0x01;
        usb_in[1] = 128;
        usb_in[2] = 128;
        usb_in[10] = sony_hid::EDGE_FN_LEFT | sony_hid::EDGE_BACK_RIGHT;
        usb_in[49] = 2 << 4;
        usb_in[53] = 0x08;
        let parsed = parse_dualsense_input(&usb_in, true, true).expect("edge input");
        assert_eq!(parsed.edge_profile, Some(2));
        assert_eq!(
            parsed.extras,
            sony_hid::EDGE_FN_LEFT | sony_hid::EDGE_BACK_RIGHT
        );
        assert_eq!(parsed.battery_pct, Some(85));
    }
}
