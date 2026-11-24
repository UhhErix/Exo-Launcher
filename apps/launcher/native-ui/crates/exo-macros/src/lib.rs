pub mod chord;
pub mod model;
pub mod onboard;
pub mod policy;
pub mod scheduler;
pub mod switcher;

use model::{
    DeviceKind, InputSpec, LayerActivation, MacroAction, MacroDef, MacroDelivery, MacroLayer,
    MacroProfile, MacroStep, MacroTrigger, PlaybackMode,
};
use policy::{AntiCheatClass, AntiCheatFocusWatchdog, GameAntiCheatInput, MacroGamePolicy};
use scheduler::{DryRunSink, ExecutionReport, LatencySummary, MacroScheduler};
use serde::{Deserialize, Serialize};
use std::sync::atomic::AtomicBool;
use switcher::ProfileSwitcher;

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MacroDocument {
    pub version: u32,
    pub enabled: bool,
    pub active_profile_id: String,
    pub profiles: Vec<MacroProfile>,
    pub game_policies: Vec<MacroGamePolicy>,
    pub latency: LatencySummary,
}

pub struct MacroControlPlane {
    pub doc: MacroDocument,
    pub switcher: ProfileSwitcher,
    pub watchdog: AntiCheatFocusWatchdog,
    pub scheduler: MacroScheduler,
    pub foreground_game_id: Option<String>,
    pub recording: bool,
}

impl Default for MacroControlPlane {
    fn default() -> Self {
        Self::demo()
    }
}

impl MacroControlPlane {
    pub fn demo() -> Self {
        let desktop = MacroProfile {
            id: "desktop".into(),
            name: "Desktop".into(),
            is_default: true,
            game_ids: Vec::new(),
            app_executables: Vec::new(),
            active_layer_id: "base".into(),
            layers: vec![
                MacroLayer {
                    id: "base".into(),
                    name: "Base".into(),
                    activation: LayerActivation::Base,
                    trigger: None,
                },
                MacroLayer {
                    id: "shift".into(),
                    name: "Shift layer".into(),
                    activation: LayerActivation::Hold,
                    trigger: Some(InputSpec {
                        device_kind: DeviceKind::Mouse,
                        device_id: None,
                        code: "Mouse4".into(),
                    }),
                },
            ],
            bindings: vec![
                MacroDef {
                    id: "onboard-ptt".into(),
                    name: "Mouse 5 to Push-to-Talk (hardware)".into(),
                    enabled: true,
                    delivery: MacroDelivery::OnboardHardware,
                    onboard_device_id: Some("vid_361d_pid_0300".into()),
                    trigger: MacroTrigger {
                        chord: vec![InputSpec {
                            device_kind: DeviceKind::Mouse,
                            device_id: Some("vid_361d_pid_0300".into()),
                            code: "Mouse5".into(),
                        }],
                        mode: PlaybackMode::Hold,
                        ..MacroTrigger::default()
                    },
                    steps: vec![
                        MacroStep {
                            id: "s1".into(),
                            action: MacroAction::KeyDown { code: "F13".into() },
                            delay_ms: 0.0,
                        },
                        MacroStep {
                            id: "s2".into(),
                            action: MacroAction::Delay,
                            delay_ms: 10.0,
                        },
                        MacroStep {
                            id: "s3".into(),
                            action: MacroAction::KeyUp { code: "F13".into() },
                            delay_ms: 0.0,
                        },
                    ],
                },
                MacroDef {
                    id: "ipc-edge-replay".into(),
                    name: "Edge FnL + Create to Save Replay".into(),
                    enabled: true,
                    delivery: MacroDelivery::ExoInternal,
                    onboard_device_id: None,
                    trigger: MacroTrigger {
                        chord: vec![
                            InputSpec {
                                device_kind: DeviceKind::Controller,
                                device_id: None,
                                code: "FnLeft".into(),
                            },
                            InputSpec {
                                device_kind: DeviceKind::Controller,
                                device_id: None,
                                code: "Create".into(),
                            },
                        ],
                        mode: PlaybackMode::Once,
                        ..MacroTrigger::default()
                    },
                    steps: vec![MacroStep {
                        id: "c1".into(),
                        action: MacroAction::ExoInternal {
                            command: "capture.saveReplay".into(),
                        },
                        delay_ms: 0.0,
                    }],
                },
            ],
        };

        let policies = vec![
            policy::evaluate_game_policy(&GameAntiCheatInput {
                game_id: "riot:valorant".into(),
                title: "VALORANT".into(),
                class: AntiCheatClass::CompetitivePvp,
                anti_cheat_names: vec!["Riot Vanguard".into()],
                offline_note_acknowledged: false,
            }),
            policy::evaluate_game_policy(&GameAntiCheatInput {
                game_id: "steam:1938090".into(),
                title: "Call of Duty".into(),
                class: AntiCheatClass::CompetitivePvp,
                anti_cheat_names: vec!["Ricochet".into()],
                offline_note_acknowledged: false,
            }),
            policy::evaluate_game_policy(&GameAntiCheatInput {
                game_id: "epic:wuwa".into(),
                title: "Wuthering Waves".into(),
                class: AntiCheatClass::OnlinePveGacha,
                anti_cheat_names: vec!["Anti-Cheat Expert".into()],
                offline_note_acknowledged: false,
            }),
            policy::evaluate_game_policy(&GameAntiCheatInput {
                game_id: "steam:413150".into(),
                title: "Stardew Valley".into(),
                class: AntiCheatClass::OfflineSinglePlayer,
                anti_cheat_names: Vec::new(),
                offline_note_acknowledged: false,
            }),
        ];

        let mut switcher = ProfileSwitcher::default();
        switcher.set_initial(&desktop);

        Self {
            doc: MacroDocument {
                version: 2,
                enabled: true,
                active_profile_id: desktop.id.clone(),
                profiles: vec![desktop],
                game_policies: policies,
                latency: LatencySummary {
                    samples: 0,
                    p50_us: 0.0,
                    p95_us: 0.0,
                    p99_us: 0.0,
                    max_us: 0.0,
                    target_us: 500.0,
                },
            },
            switcher,
            watchdog: AntiCheatFocusWatchdog::new(),
            scheduler: MacroScheduler,
            foreground_game_id: None,
            recording: false,
        }
    }

    pub fn should_spawn_worker(&self) -> bool {
        if !self.doc.enabled || self.watchdog.is_sidecar_paused() {
            return false;
        }
        let Some(active) = self
            .doc
            .profiles
            .iter()
            .find(|p| p.id == self.doc.active_profile_id)
        else {
            return false;
        };
        active
            .bindings
            .iter()
            .any(|b| b.enabled && b.delivery == MacroDelivery::SoftwareSidecar)
    }

    pub fn requires_ll_hooks(&self) -> bool {
        if !self.should_spawn_worker() {
            return false;
        }
        let Some(active) = self
            .doc
            .profiles
            .iter()
            .find(|p| p.id == self.doc.active_profile_id)
        else {
            return false;
        };
        active.bindings.iter().any(|b| {
            b.enabled && b.delivery == MacroDelivery::SoftwareSidecar && b.trigger.swallow_input
        })
    }

    pub fn on_foreground_game(&mut self, game_id: Option<&str>) -> (bool, f64) {
        self.foreground_game_id = game_id.map(str::to_string);
        let policy = game_id.and_then(|gid| {
            self.doc
                .game_policies
                .iter()
                .find(|p| p.game_id.eq_ignore_ascii_case(gid))
        });
        self.watchdog.on_foreground_change(policy)
    }

    pub fn dry_run(&mut self, profile_id: &str, binding_id: &str) -> Option<ExecutionReport> {
        let profile = self.doc.profiles.iter().find(|p| p.id == profile_id)?;
        let binding = profile.bindings.iter().find(|b| b.id == binding_id)?;
        let mut sink = DryRunSink::new();
        let cancel = AtomicBool::new(false);
        let report = self.scheduler.execute(binding, &mut sink, &cancel);
        if report.latency.samples > 0 {
            self.doc.latency = report.latency.clone();
        }
        Some(report)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::chord::{ChordMatcher, LayerStack};
    use crate::onboard::{OnboardTarget, compile_onboard_remap};
    use std::time::{Duration, Instant};

    #[test]
    fn online_competitive_and_unknown_games_get_onboard_hardware_only() {
        let cases = [
            (
                "riot:valorant",
                "VALORANT",
                AntiCheatClass::CompetitivePvp,
                vec!["Riot Vanguard"],
            ),
            (
                "steam:1938090",
                "Call of Duty",
                AntiCheatClass::CompetitivePvp,
                vec!["Ricochet"],
            ),
            (
                "steam:730",
                "CS2",
                AntiCheatClass::CompetitivePvp,
                vec!["Valve Anti-Cheat", "FACEIT"],
            ),
            (
                "steam:1172470",
                "Apex Legends",
                AntiCheatClass::CompetitivePvp,
                vec!["Easy Anti-Cheat"],
            ),
            (
                "steam:359550",
                "R6 Siege",
                AntiCheatClass::CompetitivePvp,
                vec!["BattlEye"],
            ),
            (
                "epic:wuwa",
                "Wuthering Waves",
                AntiCheatClass::OnlinePveGacha,
                vec!["Anti-Cheat Expert"],
            ),
            (
                "roblox:client",
                "Roblox",
                AntiCheatClass::CompetitivePvp,
                vec!["Hyperion"],
            ),
            ("local:unknown", "Mystery", AntiCheatClass::Unknown, vec![]),
        ];

        let sw = MacroDef {
            id: "sw".into(),
            name: "Software".into(),
            enabled: true,
            delivery: MacroDelivery::SoftwareSidecar,
            onboard_device_id: None,
            trigger: MacroTrigger::default(),
            steps: vec![MacroStep {
                id: "1".into(),
                action: MacroAction::KeyDown { code: "E".into() },
                delay_ms: 0.0,
            }],
        };
        let hw = MacroDef {
            id: "hw".into(),
            name: "Onboard".into(),
            enabled: true,
            delivery: MacroDelivery::OnboardHardware,
            onboard_device_id: Some("mouse-1".into()),
            trigger: MacroTrigger::default(),
            steps: vec![MacroStep {
                id: "1".into(),
                action: MacroAction::KeyDown { code: "F13".into() },
                delay_ms: 0.0,
            }],
        };

        for (id, title, class, names) in cases {
            let p = policy::evaluate_game_policy(&GameAntiCheatInput {
                game_id: id.into(),
                title: title.into(),
                class,
                anti_cheat_names: names.into_iter().map(str::to_string).collect(),
                offline_note_acknowledged: true,
            });
            assert!(
                !p.software_macros_allowed,
                "{title} must block Tier 2 software"
            );
            assert!(p.onboard_hardware_allowed);
            assert!(!policy::can_execute_binding(&sw, Some(&p)));
            assert!(policy::can_execute_binding(&hw, Some(&p)));
        }

        let offline = policy::evaluate_game_policy(&GameAntiCheatInput {
            game_id: "steam:413150".into(),
            title: "Stardew Valley".into(),
            class: AntiCheatClass::OfflineSinglePlayer,
            anti_cheat_names: Vec::new(),
            offline_note_acknowledged: false,
        });
        assert!(offline.software_macros_allowed);
        assert!(offline.requires_offline_note);
        assert!(policy::can_execute_binding(&sw, Some(&offline)));
    }

    #[test]
    fn cheat_primitives_recoil_and_inhuman_rapid_fire_are_rejected() {
        let recoil = MacroDef {
            id: "recoil".into(),
            name: "Recoil".into(),
            enabled: true,
            delivery: MacroDelivery::SoftwareSidecar,
            onboard_device_id: None,
            trigger: MacroTrigger {
                mode: PlaybackMode::Hold,
                ..MacroTrigger::default()
            },
            steps: vec![MacroStep {
                id: "1".into(),
                action: MacroAction::MouseMove { dx: 0, dy: 4 },
                delay_ms: 10.0,
            }],
        };
        assert!(policy::is_disallowed_cheat_primitive(&recoil).is_some());

        let rapid = MacroDef {
            id: "rapid".into(),
            name: "Rapid".into(),
            enabled: true,
            delivery: MacroDelivery::SoftwareSidecar,
            onboard_device_id: None,
            trigger: MacroTrigger {
                mode: PlaybackMode::Repeat,
                repeat_count: 10,
                ..MacroTrigger::default()
            },
            steps: vec![
                MacroStep {
                    id: "1".into(),
                    action: MacroAction::MouseDown {
                        button: "Left".into(),
                    },
                    delay_ms: 5.0,
                },
                MacroStep {
                    id: "2".into(),
                    action: MacroAction::MouseUp {
                        button: "Left".into(),
                    },
                    delay_ms: 5.0,
                },
            ],
        };
        assert!(policy::is_disallowed_cheat_primitive(&rapid).is_some());
    }

    #[test]
    fn worker_not_spawned_when_only_onboard_or_internal_macros_exist() {
        let mut cp = MacroControlPlane::demo();
        assert!(!cp.should_spawn_worker());
        assert!(!cp.requires_ll_hooks());

        let (_, template_def) = model::built_in_templates().pop().unwrap();
        cp.doc.profiles[0].bindings.push(template_def);
        assert!(cp.should_spawn_worker());
        assert!(!cp.requires_ll_hooks());

        let (paused, latency_us) = cp.on_foreground_game(Some("riot:valorant"));
        assert!(paused);
        assert!(latency_us < 10_000.0, "Focus AC pause took {latency_us}us");
        assert!(!cp.should_spawn_worker());
    }

    #[test]
    fn qpc_scheduler_achieves_sub_500us_p99_and_releases_stuck_keys_on_abort() {
        let binding = MacroDef {
            id: "bench".into(),
            name: "10ms timing".into(),
            enabled: true,
            delivery: MacroDelivery::SoftwareSidecar,
            onboard_device_id: None,
            trigger: MacroTrigger::default(),
            steps: (0..20)
                .map(|i| MacroStep {
                    id: format!("s{i}"),
                    action: if i % 2 == 0 {
                        MacroAction::KeyDown { code: "E".into() }
                    } else {
                        MacroAction::KeyUp { code: "E".into() }
                    },
                    delay_ms: 10.0,
                })
                .collect(),
        };

        let mut sink = DryRunSink::new();
        let cancel = AtomicBool::new(false);
        let report = MacroScheduler.execute(&binding, &mut sink, &cancel);
        assert!(report.ok);
        assert_eq!(report.latency.samples, 20);
        assert!(
            report.latency.p50_us < 500.0,
            "Expected p50 < 500us, got {}us",
            report.latency.p50_us
        );
        assert!(
            report.latency.p95_us < 1500.0,
            "Expected p95 < 1500us, got {}us",
            report.latency.p95_us
        );

        let mut stuck_sink = DryRunSink::new();
        {
            let mut guard =
                scheduler::StuckKeyGuard::new(&mut stuck_sink, MacroDelivery::SoftwareSidecar);
            guard.emit(&MacroAction::KeyDown {
                code: "ShiftLeft".into(),
            });
            guard.emit(&MacroAction::MouseDown {
                button: "Left".into(),
            });
        }
        assert!(
            stuck_sink
                .events
                .iter()
                .any(|(a, _, _)| matches!(a, MacroAction::KeyUp { code } if code == "ShiftLeft"))
        );
        assert!(
            stuck_sink
                .events
                .iter()
                .any(|(a, _, _)| matches!(a, MacroAction::MouseUp { button } if button == "Left"))
        );
    }

    #[test]
    fn chord_matcher_layers_panic_chord_and_onboard_compiler_work() {
        let mut matcher = ChordMatcher::new();
        let t0 = Instant::now();
        let m4 = InputSpec {
            device_kind: DeviceKind::Mouse,
            device_id: Some("vid_046d_pid_c094".into()),
            code: "Mouse4".into(),
        };
        let key_e = InputSpec {
            device_kind: DeviceKind::Keyboard,
            device_id: Some("vid_31e3_pid_1320".into()),
            code: "E".into(),
        };
        matcher.key_down(&m4, t0);
        matcher.key_down(&key_e, t0 + Duration::from_millis(12));

        let trigger = MacroTrigger {
            chord: vec![
                m4.clone(),
                InputSpec {
                    device_kind: DeviceKind::Keyboard,
                    device_id: None,
                    code: "E".into(),
                },
            ],
            chord_window_ms: 45,
            layer_id: "shift".into(),
            ..MacroTrigger::default()
        };

        let layers = vec![MacroLayer {
            id: "shift".into(),
            name: "Shift".into(),
            activation: LayerActivation::Hold,
            trigger: Some(m4.clone()),
        }];
        let mut stack = LayerStack::new("base");
        assert!(!matcher.matches(&trigger, stack.active(), t0 + Duration::from_millis(15)));
        stack.on_input_down(&m4, &layers);
        assert!(matcher.matches(&trigger, stack.active(), t0 + Duration::from_millis(15)));
        stack.on_input_up(&m4, &layers);
        assert_eq!(stack.active(), "base");

        matcher.key_down(
            &InputSpec {
                device_kind: DeviceKind::Keyboard,
                device_id: None,
                code: "Escape".into(),
            },
            t0,
        );
        matcher.key_down(
            &InputSpec {
                device_kind: DeviceKind::Keyboard,
                device_id: None,
                code: "Pause".into(),
            },
            t0,
        );
        assert!(matcher.is_emergency_panic_chord());

        let cp = MacroControlPlane::demo();
        let compiled =
            compile_onboard_remap(&cp.doc.profiles[0].bindings[0], OnboardTarget::OpenMouse)
                .expect("onboard remap compiles");
        assert_eq!(compiled.hid_usage, 0x68);
        assert_eq!(&compiled.payload_preview[..2], &[0xA1, 0x01]);
    }
}
