use crate::devices_audio::{
    DevicePageHeader, device_btn, device_page_shell, device_panel, device_row, device_switch,
    port_words,
};
use crate::state::{AppState, HostStatus, Room};
use exo_kit::{
    BadgeTone, FG, GLASS_PANEL_SIGMA, RADIUS_PILL, RADIUS_SHEET, SPRING_BASE, bad, fg_faint,
    fg_muted, float_line, glass_panel_fill, glass_rim, good, hairline, icon_gamepad,
    modal_card_transition, modal_scrim_transition, primary_button, progress_bar, scaled,
    scrim_fill, status_badge, type_body, type_small, type_title,
};
use exo_rpc::{
    ButtonCheck, ControllerCalibrateReply, ControllerGameProfile, ControllerGameProfilesReply,
    ControllerMeasureReply, ControllerPolling, ControllerQualityReply, ControllerReadReply,
    ControllerSettingsReply, ControllerSoundReply, ControllerStateEvent, ControllerTestEdgeReply,
    ControllerTestRestReply, HidInputEvent, METHOD_CONTROLLER_CALIBRATE,
    METHOD_CONTROLLER_GAME_PROFILES, METHOD_CONTROLLER_GAME_PROFILES_SAVE,
    METHOD_CONTROLLER_MEASURE, METHOD_CONTROLLER_QUALITY, METHOD_CONTROLLER_READ,
    METHOD_CONTROLLER_SETTINGS, METHOD_CONTROLLER_SOUND, METHOD_CONTROLLER_TEST,
    METHOD_CONTROLLER_WATCH, METHOD_SHELL_OPEN_URL, METHOD_STEAM_INPUT_STATE, PadOverrides,
    PadSettings, PeripheralCard, SteamInputStateReply, StickEdge, StickRest, TriggerSweep,
};
use rune::{
    El, ScrollSpec, Shape, SurfaceFit, TextEdit, TextStyle, canvas, col, edit_access, el, row,
    text, text_input,
};
use serde_json::json;
use std::collections::HashSet;

const CALIBRATE_BINS: usize = 36;
const SONY_UPDATE_URL: &str = "https://www.playstation.com/support/hardware/ps5-controller-update/";

const LIGHT_CHOICES: &[(&str, &str)] = &[
    ("games", "Games set it"),
    ("off", "Off"),
    ("#FFFFFF", "White"),
    ("#1E64FF", "Blue"),
    ("#00C8FF", "Cyan"),
    ("#8A2BE2", "Purple"),
    ("#FF4FA0", "Pink"),
    ("#FF2020", "Red"),
    ("#FF8C00", "Orange"),
    ("#20D060", "Green"),
];

const PROFILE_LIGHT_CHOICES: &[(&str, &str)] = &[
    ("same", "As usual"),
    ("games", "The game sets it"),
    ("off", "Off"),
    ("#FFFFFF", "White"),
    ("#1E64FF", "Blue"),
    ("#00C8FF", "Cyan"),
    ("#8A2BE2", "Purple"),
    ("#FF4FA0", "Pink"),
    ("#FF2020", "Red"),
    ("#FF8C00", "Orange"),
    ("#20D060", "Green"),
];

const STRENGTH_CHOICES: &[(&str, &str)] = &[
    ("full", "Full"),
    ("medium", "Medium"),
    ("low", "Low"),
    ("off", "Off"),
];

const TRIGGER_STRENGTH_CHOICES: &[(&str, &str)] = &[
    ("full", "Full"),
    ("medium", "Medium"),
    ("low", "Low"),
    ("off", "Lightest"),
];

const PROFILE_ONOFF_CHOICES: &[(&str, &str)] =
    &[("same", "As usual"), ("on", "On"), ("off", "Off")];

const PROFILE_STRENGTH_CHOICES: &[(&str, &str)] = &[
    ("same", "As usual"),
    ("full", "Full"),
    ("medium", "Medium"),
    ("low", "Low"),
    ("off", "Off"),
];

const PROFILE_TRIGGER_CHOICES: &[(&str, &str)] = &[
    ("same", "As usual"),
    ("full", "Full"),
    ("medium", "Medium"),
    ("low", "Low"),
    ("off", "Lightest"),
];

const PAD_KEYS: &[(u32, &str)] = &[
    (0x40, "Left back"),
    (0x80, "Right back"),
    (0x10, "Left Fn"),
    (0x20, "Right Fn"),
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StickTestStep {
    #[default]
    Idle,
    Rest,
    Edge,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CalibrationPhase {
    #[default]
    Intro,
    Before,
    Center,
    Range,
    Ending,
    After,
    Done,
}

#[derive(Debug, Clone, Default)]
pub struct CalibrationDialogState {
    pub open: bool,
    pub phase: CalibrationPhase,
    pub in_range: bool,
    pub error: Option<String>,
    pub before: Vec<StickRest>,
    pub after: Vec<StickRest>,
    pub swept_left: HashSet<usize>,
    pub swept_right: HashSet<usize>,
}

#[derive(Debug, Clone, Default)]
pub struct ProfileEditorState {
    pub draft: ControllerGameProfile,
    pub is_new: bool,
    pub name_edit: TextEdit,
    pub game_picker_open: bool,
    pub game_query: TextEdit,
    pub busy: bool,
    pub error: Option<String>,
    pub open_choice: Option<String>,
}

#[derive(Debug, Clone, Default)]
pub struct HidRelayTracker {
    pub change_count: u64,
    pub input_count: u64,
    pub last_device: Option<String>,
    pub last_report_id: Option<i32>,
    pub last_data_len: usize,
}

#[derive(Debug, Clone, Default)]
pub struct ControllerLabState {
    pub watching_id: Option<String>,
    pub last_watch_renew: f64,
    pub read_id: Option<String>,
    pub read: Option<ControllerReadReply>,
    pub live: Option<ControllerStateEvent>,
    pub polling_id: Option<String>,
    pub polling: Option<ControllerPolling>,
    pub polling_busy: bool,
    pub polling_error: Option<String>,
    pub stick_step: StickTestStep,
    pub stick_ran_rest: Option<(Option<StickRest>, Option<StickRest>)>,
    pub stick_ran_edge: Option<(Option<StickEdge>, Option<StickEdge>)>,
    pub stick_error: Option<String>,
    pub trigger_most_l2: u8,
    pub trigger_most_r2: u8,
    pub trigger_swept: Option<(Option<TriggerSweep>, Option<TriggerSweep>)>,
    pub trigger_testing: bool,
    pub trigger_error: Option<String>,
    pub button_check: Option<ButtonCheck>,
    pub button_testing: bool,
    pub button_error: Option<String>,
    pub pad_settings: PadSettings,
    pub pad_settings_busy: bool,
    pub pad_settings_error: Option<String>,
    pub sound_on: bool,
    pub sound_busy: bool,
    pub sound_error: Option<String>,
    pub open_choice: Option<String>,
    pub calibration_error: Option<String>,
    pub calibration_busy: bool,
    pub keep_armed: bool,
    pub calibrate_dialog: CalibrationDialogState,
    pub steam_input: Option<SteamInputStateReply>,
    pub game_profiles: Option<Vec<ControllerGameProfile>>,
    pub game_profiles_error: Option<String>,
    pub profile_editor: Option<ProfileEditorState>,
    pub hid: HidRelayTracker,
}

impl ControllerLabState {
    pub fn demo() -> Self {
        Self {
            read_id: Some("dev:pad".into()),
            read: Some(ControllerReadReply {
                ok: true,
                link: Some("usb".into()),
                edge: Some(true),
                firmware: Some(exo_rpc::ControllerFirmware {
                    version: "0520".into(),
                    built: "2026-06-10".into(),
                    latest: Some("0520".into()),
                    current: Some(true),
                }),
                layers: Some(exo_rpc::ControllerLayers {
                    steam_play_station: Some("off".into()),
                    tools: Vec::new(),
                    polling_driver: false,
                    memory_integrity: Some(true),
                }),
                calibration: Some(exo_rpc::ControllerCalibration {
                    calibrated: false,
                    kept: false,
                    can_undo: false,
                    holds: None,
                    put_back: None,
                }),
                settings: Some(PadSettings::default()),
                sound: Some(false),
                remembered: Some(true),
                slots: Some(vec![
                    exo_rpc::ControllerSlot {
                        number: 1,
                        empty: false,
                        name: Some("Default".into()),
                    },
                    exo_rpc::ControllerSlot {
                        number: 2,
                        empty: false,
                        name: Some("FPS".into()),
                    },
                    exo_rpc::ControllerSlot {
                        number: 3,
                        empty: true,
                        name: None,
                    },
                    exo_rpc::ControllerSlot {
                        number: 4,
                        empty: true,
                        name: None,
                    },
                ]),
                tested: None,
                message: None,
            }),
            live: Some(ControllerStateEvent {
                id: "dev:pad".into(),
                lx: 128,
                ly: 128,
                rx: 128,
                ry: 128,
                l2: 0,
                r2: 0,
                profile: Some(1),
                stop_l: Some(0),
                stop_r: Some(0),
                battery: Some(90),
                charging: true,
                extras: 0,
            }),
            polling_id: Some("dev:pad".into()),
            polling: Some(ControllerPolling {
                rate: 1000.0,
                fresh: Some(1000.0),
                p99: 1.1,
                jitter: 0.1,
                repeats: Some(0.0),
                reports: 3000,
                batched: 0.0,
                changes: false,
            }),
            steam_input: Some(SteamInputStateReply {
                ok: true,
                steam: true,
                native: 12,
                translated: 2,
            }),
            game_profiles: Some(Vec::new()),
            ..Self::default()
        }
    }
}

pub fn pad_settable(connection: Option<&str>) -> bool {
    connection == Some("usb")
}

pub fn axis_percent(val: Option<u8>) -> String {
    match val {
        None => "–".into(),
        Some(v) => {
            let pct = ((v as f64 - 127.5) / 127.5) * 100.0;
            let sign = if pct >= 0.0 { "+" } else { "−" };
            format!("{sign}{:.1}", pct.abs())
        }
    }
}

pub fn deadzone_for(rest: &StickRest) -> i32 {
    let off = rest.offset_x.abs().max(rest.offset_y.abs());
    (off + (rest.noise as f64 / 127.5) * 100.0).ceil() as i32
}

pub fn trigger_share(val: u8) -> i32 {
    ((val as f64 / 255.0) * 100.0).round() as i32
}

pub fn sweep_bin(x: u8, y: u8) -> Option<usize> {
    let dx = (x as f64 - 127.5) / 127.5;
    let dy = (y as f64 - 127.5) / 127.5;
    if dx.hypot(dy) < 0.5 {
        return None;
    }
    let frac = (dy.atan2(dx) + std::f64::consts::PI) / std::f64::consts::TAU;
    Some(((frac * CALIBRATE_BINS as f64).floor() as usize) % CALIBRATE_BINS)
}

pub fn sweep_percent(bins: &HashSet<usize>) -> i32 {
    ((bins.len() as f64 / CALIBRATE_BINS as f64) * 100.0).round() as i32
}

pub fn stick_verdict(
    rest: Option<&StickRest>,
    edge: Option<&StickEdge>,
    edge_attempted: bool,
) -> (String, Option<bool>) {
    let mut parts = Vec::new();
    let mut good_stick = true;
    if let Some(r) = rest {
        let off = r.offset_x.abs().max(r.offset_y.abs());
        let step_w = if r.noise == 1 { "step" } else { "steps" };
        parts.push(format!("center {off:.1}% off"));
        parts.push(format!("wanders {} {step_w}", r.noise));
        parts.push(format!("a {}% deadzone hides it", deadzone_for(r)));
        if let Some(creep) = r.creep
            && creep >= 1.0
        {
            parts.push(format!("crept {creep:.0}% while left alone"));
        }
        good_stick &= off <= 2.0 && r.noise <= 2 && r.creep.unwrap_or(0.0) < 1.0;
    }
    let mut has_full_edge = false;
    if let Some(e) = edge {
        if e.covered < 0.75 {
            parts.push("roll it all the way around to measure its edge".into());
        } else {
            has_full_edge = true;
            parts.push(format!("reaches {:.0}%", e.reach));
            parts.push(format!("{:.0}% out of round", e.circularity));
            good_stick &= e.reach >= 97.0 && e.circularity <= 6.0;
        }
    } else if edge_attempted {
        parts.push("roll it all the way around to measure its edge".into());
    }
    let joined = parts.join(" · ");
    let mut chars = joined.chars();
    let sentence = match chars.next() {
        Some(c) => c.to_uppercase().collect::<String>() + chars.as_str(),
        None => String::new(),
    };
    (sentence, has_full_edge.then_some(good_stick))
}

pub fn pad_settings_changed(s: &PadSettings) -> bool {
    s.light.is_some()
        || s.player_light
        || s.mic_off
        || s.speaker_off
        || s.vibration != "full"
        || s.trigger_effects != "full"
}

pub fn save_profile_list(
    existing: &[ControllerGameProfile],
    draft: ControllerGameProfile,
) -> Vec<ControllerGameProfile> {
    let stripped: Vec<ControllerGameProfile> = existing
        .iter()
        .map(|p| {
            if p.id == draft.id {
                draft.clone()
            } else {
                let mut copy = p.clone();
                copy.games.retain(|g| !draft.games.contains(g));
                copy
            }
        })
        .collect();
    if existing.iter().any(|p| p.id == draft.id) {
        stripped
    } else {
        let mut out = stripped;
        out.push(draft);
        out
    }
}

fn choice_label(choices: &[(&str, &str)], value: &str) -> String {
    choices
        .iter()
        .find(|(v, _)| *v == value)
        .map(|(_, l)| (*l).to_string())
        .unwrap_or_else(|| value.to_string())
}

fn stick_offset_summary(rest: Option<&StickRest>) -> String {
    match rest {
        Some(r) => format!("{:.1}%", r.offset_x.abs().max(r.offset_y.abs())),
        None => "–".into(),
    }
}

impl AppState {
    pub fn open_controller_page(&mut self, id: String) {
        let prev = self.pc.controller_ui.watching_id.clone();
        if prev.as_deref() != Some(id.as_str()) {
            self.stop_controller_watch();
            self.pc.controller_ui.live = None;
            self.pc.controller_ui.trigger_most_l2 = 0;
            self.pc.controller_ui.trigger_most_r2 = 0;
            self.pc.controller_ui.trigger_swept = None;
            self.pc.controller_ui.button_check = None;
            self.pc.controller_ui.stick_ran_rest = None;
            self.pc.controller_ui.stick_ran_edge = None;
            self.pc.controller_ui.stick_error = None;
            self.pc.controller_ui.open_choice = None;
            self.pc.controller_ui.keep_armed = false;
            self.pc.controller_ui.calibrate_dialog = CalibrationDialogState::default();
        }
        self.pc.controller_ui.watching_id = Some(id.clone());
        self.controller_read(&id);
        self.controller_watch(&id, true);
        if self.pc.controller_ui.polling_id.as_deref() != Some(id.as_str()) {
            self.pc.controller_ui.polling_id = Some(id.clone());
            self.controller_measure(&id);
        }
        self.load_steam_input_state();
        self.load_controller_game_profiles();
    }

    pub fn close_controller_page(&mut self) {
        if self.pc.controller_ui.calibrate_dialog.in_range
            && let Some(id) = self.pc.controller_ui.watching_id.clone()
        {
            self.controller_calibrate_cancel(&id);
        }
        self.pc.controller_ui.calibrate_dialog = CalibrationDialogState::default();
        self.pc.controller_ui.profile_editor = None;
        self.pc.controller_ui.open_choice = None;
        self.stop_controller_watch();
        self.pc.selected_device_id = None;
    }

    pub fn stop_controller_watch(&mut self) {
        if let Some(id) = self.pc.controller_ui.watching_id.take() {
            self.controller_watch(&id, false);
        }
    }

    pub fn controller_read(&mut self, id: &str) {
        if self.status == HostStatus::Standalone {
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_copy = target_id.clone();
            rpc.call::<_, ControllerReadReply>(
                METHOD_CONTROLLER_READ,
                &json!({ "id": target_id }),
                move |res| {
                    if let Ok(reply) = res {
                        handle.update(move |s, _| {
                            s.pc.controller_ui.read_id = Some(id_copy);
                            if let Some(ref settings) = reply.settings {
                                s.pc.controller_ui.pad_settings = settings.clone();
                            } else {
                                s.pc.controller_ui.pad_settings = PadSettings::default();
                            }
                            s.pc.controller_ui.sound_on = reply.sound.unwrap_or(false);
                            s.pc.controller_ui.read = Some(reply);
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn controller_watch(&mut self, id: &str, on: bool) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(
                METHOD_CONTROLLER_WATCH,
                &json!({ "id": id, "on": on }),
                |_| {},
            );
        }
        #[cfg(not(windows))]
        let _ = (id, on);
    }

    pub fn controller_measure(&mut self, id: &str) {
        if self.pc.controller_ui.polling_busy {
            return;
        }
        self.pc.controller_ui.polling_busy = true;
        self.pc.controller_ui.polling_error = None;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.polling_busy = false;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerMeasureReply>(
                METHOD_CONTROLLER_MEASURE,
                &json!({ "id": target_id, "seconds": 3 }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.controller_ui.polling_busy = false;
                        match res {
                            Ok(reply) if reply.ok && reply.polling.is_some() => {
                                s.pc.controller_ui.polling = reply.polling;
                            }
                            Ok(reply) => {
                                s.pc.controller_ui.polling_error =
                                    Some(reply.message.unwrap_or_else(|| {
                                        "Exo couldn’t measure it. Try again.".into()
                                    }));
                            }
                            Err(_) => {
                                s.pc.controller_ui.polling_error =
                                    Some("Exo couldn’t measure it. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn controller_run_stick_test(&mut self, id: &str) {
        if self.pc.controller_ui.stick_step != StickTestStep::Idle {
            return;
        }
        self.pc.controller_ui.stick_step = StickTestStep::Rest;
        self.pc.controller_ui.stick_error = None;
        self.pc.controller_ui.stick_ran_rest = None;
        self.pc.controller_ui.stick_ran_edge = None;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.stick_step = StickTestStep::Idle;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc_edge = rpc.clone();
            let id_edge = target_id.clone();
            rpc.call::<_, ControllerTestRestReply>(
                METHOD_CONTROLLER_TEST,
                &json!({ "id": target_id, "kind": "rest", "seconds": 5 }),
                move |res| match res {
                    Ok(rest) if rest.ok => {
                        let h2 = handle.clone();
                        handle.update(move |s, _| {
                            s.pc.controller_ui.stick_ran_rest = Some((rest.left, rest.right));
                            s.pc.controller_ui.stick_step = StickTestStep::Edge;
                        });
                        rpc_edge.call::<_, ControllerTestEdgeReply>(
                            METHOD_CONTROLLER_TEST,
                            &json!({ "id": id_edge, "kind": "edge", "seconds": 6 }),
                            move |edge_res| {
                                h2.update(move |s, _| {
                                    s.pc.controller_ui.stick_step = StickTestStep::Idle;
                                    match edge_res {
                                        Ok(edge) if edge.ok => {
                                            s.pc.controller_ui.stick_ran_edge =
                                                Some((edge.left, edge.right));
                                        }
                                        Ok(edge) => {
                                            s.pc.controller_ui.stick_error =
                                                Some(edge.message.unwrap_or_else(|| {
                                                    "The test stopped. Try again.".into()
                                                }));
                                        }
                                        Err(_) => {
                                            s.pc.controller_ui.stick_error =
                                                Some("The test stopped. Try again.".into());
                                        }
                                    }
                                });
                            },
                        );
                    }
                    Ok(rest) => {
                        handle.update(move |s, _| {
                            s.pc.controller_ui.stick_step = StickTestStep::Idle;
                            s.pc.controller_ui.stick_error = Some(
                                rest.message
                                    .unwrap_or_else(|| "The test stopped. Try again.".into()),
                            );
                        });
                    }
                    Err(_) => {
                        handle.update(|s, _| {
                            s.pc.controller_ui.stick_step = StickTestStep::Idle;
                            s.pc.controller_ui.stick_error =
                                Some("The test stopped. Try again.".into());
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn controller_test_triggers(&mut self, id: &str) {
        if self.pc.controller_ui.trigger_testing {
            return;
        }
        self.pc.controller_ui.trigger_testing = true;
        self.pc.controller_ui.trigger_error = None;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.trigger_testing = false;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerQualityReply>(
                METHOD_CONTROLLER_QUALITY,
                &json!({ "id": target_id, "kind": "triggers", "seconds": 6 }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.controller_ui.trigger_testing = false;
                        match res {
                            Ok(ans) if ans.ok => {
                                s.pc.controller_ui.trigger_swept = Some((ans.left, ans.right));
                            }
                            Ok(ans) => {
                                s.pc.controller_ui.trigger_error = Some(
                                    ans.message
                                        .unwrap_or_else(|| "The test stopped. Try again.".into()),
                                );
                            }
                            Err(_) => {
                                s.pc.controller_ui.trigger_error =
                                    Some("The test stopped. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn controller_test_buttons(&mut self, id: &str) {
        if self.pc.controller_ui.button_testing {
            return;
        }
        self.pc.controller_ui.button_testing = true;
        self.pc.controller_ui.button_error = None;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.button_testing = false;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerQualityReply>(
                METHOD_CONTROLLER_QUALITY,
                &json!({ "id": target_id, "kind": "buttons", "seconds": 8 }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.controller_ui.button_testing = false;
                        match res {
                            Ok(ans) if ans.ok && ans.buttons.is_some() => {
                                s.pc.controller_ui.button_check = ans.buttons;
                            }
                            Ok(ans) => {
                                s.pc.controller_ui.button_error = Some(
                                    ans.message
                                        .unwrap_or_else(|| "The test stopped. Try again.".into()),
                                );
                            }
                            Err(_) => {
                                s.pc.controller_ui.button_error =
                                    Some("The test stopped. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn controller_set_pad_settings(&mut self, id: &str, next: PadSettings) {
        let before = self.pc.controller_ui.pad_settings.clone();
        self.pc.controller_ui.pad_settings = next.clone();
        self.pc.controller_ui.pad_settings_busy = true;
        self.pc.controller_ui.pad_settings_error = None;
        self.pc.controller_ui.open_choice = None;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.pad_settings_busy = false;
            if let Some(ref mut r) = self.pc.controller_ui.read {
                r.settings = Some(next);
            }
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerSettingsReply>(
                METHOD_CONTROLLER_SETTINGS,
                &json!({ "id": target_id, "settings": next }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.controller_ui.pad_settings_busy = false;
                        match res {
                            Ok(r) if r.ok => {
                                if let Some(saved) = r.settings {
                                    s.pc.controller_ui.pad_settings = saved.clone();
                                    if let Some(ref mut read) = s.pc.controller_ui.read {
                                        read.settings = Some(saved);
                                    }
                                }
                            }
                            Ok(r) => {
                                s.pc.controller_ui.pad_settings = before;
                                s.pc.controller_ui.pad_settings_error =
                                    Some(r.message.unwrap_or_else(|| {
                                        "The controller didn’t take it. Try again.".into()
                                    }));
                            }
                            Err(_) => {
                                s.pc.controller_ui.pad_settings = before;
                                s.pc.controller_ui.pad_settings_error =
                                    Some("The controller didn’t take it. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (target_id, before);
    }

    pub fn controller_set_sound(&mut self, on: bool) {
        self.pc.controller_ui.sound_busy = true;
        self.pc.controller_ui.sound_error = None;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.sound_on = on;
            self.pc.controller_ui.sound_busy = false;
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerSoundReply>(
                METHOD_CONTROLLER_SOUND,
                &json!({ "on": on }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.controller_ui.sound_busy = false;
                        match res {
                            Ok(r) if r.ok => s.pc.controller_ui.sound_on = r.on,
                            _ => {
                                s.pc.controller_ui.sound_error =
                                    Some("Windows didn’t take it. Try again.".into());
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn controller_calibrate_step_inline(&mut self, id: &str, step: &'static str) {
        self.pc.controller_ui.calibration_error = None;
        self.pc.controller_ui.calibration_busy = true;
        self.pc.controller_ui.keep_armed = false;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.calibration_busy = false;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let id_after = target_id.clone();
            rpc.call::<_, ControllerCalibrateReply>(
                METHOD_CONTROLLER_CALIBRATE,
                &json!({ "id": target_id, "step": step }),
                move |res| {
                    handle.update(move |s, _| {
                        s.pc.controller_ui.calibration_busy = false;
                        match res {
                            Ok(r) if r.ok => {}
                            Ok(r) => {
                                s.pc.controller_ui.calibration_error =
                                    Some(r.message.unwrap_or_else(|| {
                                        "The controller didn’t take it. Try again.".into()
                                    }));
                            }
                            Err(_) => {
                                s.pc.controller_ui.calibration_error =
                                    Some("The controller didn’t take it. Try again.".into());
                            }
                        }
                        s.controller_read(&id_after);
                    });
                },
            );
        }
        #[cfg(not(windows))]
        let _ = (target_id, step);
    }

    pub fn open_calibrate_dialog(&mut self) {
        self.pc.controller_ui.calibrate_dialog = CalibrationDialogState {
            open: true,
            phase: CalibrationPhase::Intro,
            ..Default::default()
        };
    }

    pub fn close_calibrate_dialog(&mut self) {
        if self.pc.controller_ui.calibrate_dialog.in_range
            && let Some(id) = self.pc.controller_ui.watching_id.clone()
        {
            self.controller_calibrate_cancel(&id);
        }
        self.pc.controller_ui.calibrate_dialog = CalibrationDialogState::default();
        if let Some(id) = self.pc.controller_ui.watching_id.clone() {
            self.controller_read(&id);
        }
    }

    fn controller_calibrate_cancel(&mut self, id: &str) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(
                METHOD_CONTROLLER_CALIBRATE,
                &json!({ "id": id, "step": "range-cancel" }),
                |_| {},
            );
        }
        #[cfg(not(windows))]
        let _ = id;
    }

    pub fn start_calibration_flow(&mut self, id: &str) {
        self.pc.controller_ui.calibrate_dialog.error = None;
        self.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Before;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Range;
            self.pc.controller_ui.calibrate_dialog.in_range = true;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc2 = rpc.clone();
            let rpc3 = rpc.clone();
            let id2 = target_id.clone();
            let id3 = target_id.clone();
            rpc.call::<_, ControllerTestRestReply>(
                METHOD_CONTROLLER_TEST,
                &json!({ "id": target_id, "kind": "rest", "seconds": 3 }),
                move |res| match res {
                    Ok(rest) if rest.ok => {
                        let before: Vec<StickRest> = [rest.left, rest.right]
                            .into_iter()
                            .flatten()
                            .collect();
                        let h2 = handle.clone();
                        handle.update(move |s, _| {
                            s.pc.controller_ui.calibrate_dialog.before = before;
                            s.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Center;
                        });
                        rpc2.call::<_, ControllerCalibrateReply>(
                            METHOD_CONTROLLER_CALIBRATE,
                            &json!({ "id": id2, "step": "center" }),
                            move |c_res| match c_res {
                                Ok(c) if c.ok => {
                                    let h3 = h2.clone();
                                    rpc3.call::<_, ControllerCalibrateReply>(
                                        METHOD_CONTROLLER_CALIBRATE,
                                        &json!({ "id": id3, "step": "range-begin" }),
                                        move |b_res| {
                                            h3.update(move |s, _| match b_res {
                                                Ok(b) if b.ok => {
                                                    s.pc.controller_ui.calibrate_dialog.in_range =
                                                        true;
                                                    s.pc.controller_ui
                                                        .calibrate_dialog
                                                        .swept_left
                                                        .clear();
                                                    s.pc.controller_ui
                                                        .calibrate_dialog
                                                        .swept_right
                                                        .clear();
                                                    s.pc.controller_ui.calibrate_dialog.phase =
                                                        CalibrationPhase::Range;
                                                }
                                                Ok(b) => {
                                                    s.pc.controller_ui.calibrate_dialog.error =
                                                        Some(b.message.unwrap_or_else(|| {
                                                            "The controller didn’t start. Try again.".into()
                                                        }));
                                                    s.pc.controller_ui.calibrate_dialog.phase =
                                                        CalibrationPhase::Intro;
                                                }
                                                Err(_) => {
                                                    s.pc.controller_ui.calibrate_dialog.error =
                                                        Some("The calibration stopped. Try again.".into());
                                                    s.pc.controller_ui.calibrate_dialog.phase =
                                                        CalibrationPhase::Intro;
                                                }
                                            });
                                        },
                                    );
                                }
                                Ok(c) => {
                                    h2.update(move |s, _| {
                                        s.pc.controller_ui.calibrate_dialog.error = Some(
                                            c.message.unwrap_or_else(|| {
                                                "The controller didn’t take it. Try again.".into()
                                            }),
                                        );
                                        s.pc.controller_ui.calibrate_dialog.phase =
                                            CalibrationPhase::Intro;
                                    });
                                }
                                Err(_) => {
                                    h2.update(|s, _| {
                                        s.pc.controller_ui.calibrate_dialog.error =
                                            Some("The calibration stopped. Try again.".into());
                                        s.pc.controller_ui.calibrate_dialog.phase =
                                            CalibrationPhase::Intro;
                                    });
                                }
                            },
                        );
                    }
                    Ok(rest) => {
                        handle.update(move |s, _| {
                            s.pc.controller_ui.calibrate_dialog.error = Some(
                                rest.message.unwrap_or_else(|| {
                                    "The controller stopped answering. Try again.".into()
                                }),
                            );
                            s.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Intro;
                        });
                    }
                    Err(_) => {
                        handle.update(|s, _| {
                            s.pc.controller_ui.calibrate_dialog.error =
                                Some("The calibration stopped. Try again.".into());
                            s.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Intro;
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn finish_calibration_flow(&mut self, id: &str) {
        self.pc.controller_ui.calibrate_dialog.error = None;
        self.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Ending;
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.calibrate_dialog.in_range = false;
            self.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Done;
            return;
        }
        let target_id = id.to_string();
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            let rpc_after = rpc.clone();
            let id_after = target_id.clone();
            rpc.call::<_, ControllerCalibrateReply>(
                METHOD_CONTROLLER_CALIBRATE,
                &json!({ "id": target_id, "step": "range-end" }),
                move |res| match res {
                    Ok(end) if !end.ok => {
                        let more = end.more.unwrap_or(false);
                        let msg = end
                            .message
                            .unwrap_or_else(|| "The controller didn’t take it. Try again.".into());
                        handle.update(move |s, _| {
                            s.pc.controller_ui.calibrate_dialog.error = Some(msg);
                            s.pc.controller_ui.calibrate_dialog.phase = if more {
                                CalibrationPhase::Range
                            } else {
                                CalibrationPhase::Intro
                            };
                            if !more {
                                s.pc.controller_ui.calibrate_dialog.in_range = false;
                            }
                        });
                    }
                    Ok(_) => {
                        let h2 = handle.clone();
                        handle.update(|s, _| {
                            s.pc.controller_ui.calibrate_dialog.in_range = false;
                            s.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::After;
                        });
                        rpc_after.call::<_, ControllerTestRestReply>(
                            METHOD_CONTROLLER_TEST,
                            &json!({ "id": id_after, "kind": "rest", "seconds": 3 }),
                            move |after_res| {
                                h2.update(move |s, _| {
                                    if let Ok(rest) = after_res
                                        && rest.ok
                                    {
                                        s.pc.controller_ui.calibrate_dialog.after =
                                            [rest.left, rest.right].into_iter().flatten().collect();
                                    } else {
                                        s.pc.controller_ui.calibrate_dialog.error =
                                            Some("The calibration stopped. Try again.".into());
                                    }
                                    s.pc.controller_ui.calibrate_dialog.phase =
                                        CalibrationPhase::Done;
                                });
                            },
                        );
                    }
                    Err(_) => {
                        handle.update(|s, _| {
                            s.pc.controller_ui.calibrate_dialog.in_range = false;
                            s.pc.controller_ui.calibrate_dialog.error =
                                Some("The calibration stopped. Try again.".into());
                            s.pc.controller_ui.calibrate_dialog.phase = CalibrationPhase::Done;
                        });
                    }
                },
            );
        }
        #[cfg(not(windows))]
        let _ = target_id;
    }

    pub fn load_steam_input_state(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, SteamInputStateReply>(METHOD_STEAM_INPUT_STATE, &json!({}), move |res| {
                if let Ok(st) = res {
                    handle.update(move |s, _| s.pc.controller_ui.steam_input = Some(st));
                }
            });
        }
    }

    pub fn load_controller_game_profiles(&mut self) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerGameProfilesReply>(
                METHOD_CONTROLLER_GAME_PROFILES,
                &json!({}),
                move |res| {
                    if let Ok(reply) = res {
                        handle.update(move |s, _| {
                            s.pc.controller_ui.game_profiles =
                                Some(reply.profiles.unwrap_or_default());
                        });
                    }
                },
            );
        }
    }

    pub fn open_profile_editor(&mut self, existing: Option<ControllerGameProfile>) {
        let count = self
            .pc
            .controller_ui
            .game_profiles
            .as_ref()
            .map(|p| p.len())
            .unwrap_or(0);
        let (draft, is_new) = match existing {
            Some(p) => (p, false),
            None => {
                let ts = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map(|d| d.as_nanos())
                    .unwrap_or(0);
                (
                    ControllerGameProfile {
                        id: format!("gp-{ts}"),
                        name: format!("Profile {}", count + 1),
                        settings: PadOverrides::default(),
                        games: Vec::new(),
                    },
                    true,
                )
            }
        };
        let mut name_edit = TextEdit::default();
        name_edit.text = draft.name.clone();
        name_edit.sync_invariants();
        self.pc.controller_ui.profile_editor = Some(ProfileEditorState {
            draft,
            is_new,
            name_edit,
            ..Default::default()
        });
    }

    pub fn save_controller_game_profiles(&mut self, next: Vec<ControllerGameProfile>) {
        if let Some(ref mut ed) = self.pc.controller_ui.profile_editor {
            ed.busy = true;
            ed.error = None;
        }
        if self.status == HostStatus::Standalone {
            self.pc.controller_ui.game_profiles = Some(next);
            self.pc.controller_ui.profile_editor = None;
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, ControllerGameProfilesReply>(
                METHOD_CONTROLLER_GAME_PROFILES_SAVE,
                &json!({ "profiles": next }),
                move |res| {
                    handle.update(move |s, _| match res {
                        Ok(reply) if reply.ok && reply.profiles.is_some() => {
                            s.pc.controller_ui.game_profiles = reply.profiles;
                            s.pc.controller_ui.profile_editor = None;
                        }
                        Ok(reply) => {
                            let msg = reply
                                .message
                                .unwrap_or_else(|| "Exo couldn’t save them. Try again.".into());
                            if let Some(ref mut ed) = s.pc.controller_ui.profile_editor {
                                ed.busy = false;
                                ed.error = Some(msg);
                            } else {
                                s.pc.controller_ui.game_profiles_error = Some(msg);
                            }
                        }
                        Err(_) => {
                            let msg = "Exo couldn’t save them. Try again.".to_string();
                            if let Some(ref mut ed) = s.pc.controller_ui.profile_editor {
                                ed.busy = false;
                                ed.error = Some(msg);
                            } else {
                                s.pc.controller_ui.game_profiles_error = Some(msg);
                            }
                        }
                    });
                },
            );
        }
    }

    pub fn open_external_url(&mut self, url: &str) {
        if self.status == HostStatus::Standalone {
            return;
        }
        #[cfg(windows)]
        if let Some(rpc) = self.rpc.clone() {
            rpc.call_raw(METHOD_SHELL_OPEN_URL, &json!({ "url": url }), |_| {});
        }
        #[cfg(not(windows))]
        let _ = url;
    }

    pub fn apply_controller_state(&mut self, ev: ControllerStateEvent) -> bool {
        if self.pc.controller_ui.watching_id.as_deref() != Some(ev.id.as_str()) {
            return false;
        }
        if self.pc.controller_ui.live.as_ref() == Some(&ev) {
            return false;
        }
        if ev.l2 > self.pc.controller_ui.trigger_most_l2 {
            self.pc.controller_ui.trigger_most_l2 = ev.l2;
        }
        if ev.r2 > self.pc.controller_ui.trigger_most_r2 {
            self.pc.controller_ui.trigger_most_r2 = ev.r2;
        }
        if self.pc.controller_ui.calibrate_dialog.open
            && self.pc.controller_ui.calibrate_dialog.phase == CalibrationPhase::Range
        {
            if let Some(bin) = sweep_bin(ev.lx, ev.ly) {
                self.pc
                    .controller_ui
                    .calibrate_dialog
                    .swept_left
                    .insert(bin);
            }
            if let Some(bin) = sweep_bin(ev.rx, ev.ry) {
                self.pc
                    .controller_ui
                    .calibrate_dialog
                    .swept_right
                    .insert(bin);
            }
        }
        self.pc.controller_ui.live = Some(ev);
        self.room == Room::Devices
    }

    pub fn apply_hid_changed(&mut self) -> bool {
        self.pc.controller_ui.hid.change_count =
            self.pc.controller_ui.hid.change_count.saturating_add(1);
        if self.room == Room::Devices {
            self.load_pc_devices();
            true
        } else {
            false
        }
    }

    pub fn apply_hid_input(&mut self, ev: HidInputEvent) -> bool {
        self.pc.controller_ui.hid.input_count =
            self.pc.controller_ui.hid.input_count.saturating_add(1);
        self.pc.controller_ui.hid.last_device = Some(ev.device);
        self.pc.controller_ui.hid.last_report_id = Some(ev.report_id);
        self.pc.controller_ui.hid.last_data_len = ev.data.len();
        false
    }

    pub fn tick_controller_watch(&mut self, now: f64) {
        if self.room != Room::Devices {
            if self.pc.controller_ui.watching_id.is_some() {
                self.stop_controller_watch();
            }
            return;
        }
        if let Some(id) = self.pc.controller_ui.watching_id.clone()
            && now - self.pc.controller_ui.last_watch_renew >= 540.0
        {
            self.pc.controller_ui.last_watch_renew = now;
            self.controller_watch(&id, true);
        }
    }
}

fn circle_pts(cx: f32, cy: f32, r: f32, steps: usize) -> Vec<[f32; 2]> {
    (0..=steps)
        .map(|i| {
            let a = (i as f32 / steps as f32) * std::f32::consts::TAU;
            [cx + r * a.cos(), cy + r * a.sin()]
        })
        .collect()
}

fn stick_visualizer(x: Option<u8>, y: Option<u8>, label: &'static str) -> El {
    let cx = match x {
        Some(v) => 8.0 + (v as f32 / 255.0) * 84.0,
        None => 50.0,
    };
    let cy = match y {
        Some(v) => 8.0 + (v as f32 / 255.0) * 84.0,
        None => 50.0,
    };
    let shape = Shape::new()
        .view_box(100.0, 100.0)
        .stroke(circle_pts(50.0, 50.0, 42.0, 40), 1.5, float_line())
        .stroke(vec![[8.0, 50.0], [92.0, 50.0]], 1.0, hairline())
        .stroke(vec![[50.0, 8.0], [50.0, 92.0]], 1.0, hairline())
        .fill(circle_pts(cx, cy, 5.5, 16), FG);

    col()
        .items_center()
        .gap(6.0)
        .child(canvas(shape).size(92.0, 92.0))
        .child(text(label).font(type_small()).color(fg_muted()))
        .child(
            text(format!("X {}% · Y {}%", axis_percent(x), axis_percent(y)))
                .font(TextStyle::new(scaled(11.0), 500.0))
                .color(FG),
        )
}

fn sticks_panel(
    s: &AppState,
    card: &PeripheralCard,
    read: Option<&ControllerReadReply>,
    sony: bool,
    usb: bool,
    edge: bool,
) -> El {
    let ui = &s.pc.controller_ui;
    let live = ui.live.as_ref();
    let tested = read.and_then(|r| r.tested.as_ref());
    let rest_left = ui
        .stick_ran_rest
        .as_ref()
        .and_then(|(l, _)| l.as_ref())
        .or_else(|| tested.and_then(|t| t.rest_left.as_ref()));
    let rest_right = ui
        .stick_ran_rest
        .as_ref()
        .and_then(|(_, r)| r.as_ref())
        .or_else(|| tested.and_then(|t| t.rest_right.as_ref()));
    let edge_left = ui
        .stick_ran_edge
        .as_ref()
        .and_then(|(l, _)| l.as_ref())
        .or_else(|| tested.and_then(|t| t.edge_left.as_ref()));
    let edge_right = ui
        .stick_ran_edge
        .as_ref()
        .and_then(|(_, r)| r.as_ref())
        .or_else(|| tested.and_then(|t| t.edge_right.as_ref()));
    let edge_attempted = ui.stick_ran_edge.is_some()
        || tested.is_some_and(|t| t.edge_left.is_some() || t.edge_right.is_some());

    let asked = match ui.stick_step {
        StickTestStep::Rest => Some("Leave both sticks alone"),
        StickTestStep::Edge => Some("Roll both sticks slowly around their edge, a few times"),
        StickTestStep::Idle => None,
    };
    let test_detail = if asked.is_none() && ui.stick_ran_rest.is_none() {
        tested.map(|t| format!("Last run {}", t.at.get(..10).unwrap_or(&t.at)))
    } else {
        None
    };

    let id_test = card.id.clone();
    let mut rows = vec![
        row()
            .w_full()
            .py(8.0)
            .justify_around()
            .center_y()
            .child(stick_visualizer(
                live.map(|l| l.lx),
                live.map(|l| l.ly),
                "Left stick",
            ))
            .child(stick_visualizer(
                live.map(|l| l.rx),
                live.map(|l| l.ry),
                "Right stick",
            )),
        device_row(
            asked.unwrap_or("Stick test"),
            test_detail,
            ui.stick_error.clone(),
            Some(
                device_btn(
                    "ctl-stick-test",
                    if ui.stick_step != StickTestStep::Idle {
                        "Testing…"
                    } else if edge_attempted {
                        "Test again"
                    } else {
                        "Test"
                    },
                    ui.stick_step != StickTestStep::Idle,
                )
                .on_press(move |s: &mut AppState| s.controller_run_stick_test(&id_test)),
            ),
        ),
    ];

    if rest_left.is_some() || rest_right.is_some() || edge_attempted {
        for (lbl, r, e) in [
            ("Left stick", rest_left, edge_left),
            ("Right stick", rest_right, edge_right),
        ] {
            let (desc, verdict) = stick_verdict(r, e, edge_attempted);
            let badge = verdict.map(|ok| {
                status_badge(
                    if ok {
                        "Good".into()
                    } else {
                        "Needs calibration".into()
                    },
                    if ok { BadgeTone::Good } else { BadgeTone::Warn },
                )
            });
            rows.push(device_row(lbl, Some(desc), None, badge));
        }
    }

    if sony {
        let cal = read.and_then(|r| r.calibration.as_ref());
        let calibrated = cal.is_some_and(|c| c.calibrated);
        let detail = if !usb {
            Some("Connect it with a cable to calibrate it".to_string())
        } else if !calibrated {
            None
        } else if edge {
            Some(if cal.and_then(|c| c.holds) == Some(false) {
                "Not on the controller now: it goes back when it reconnects".into()
            } else {
                "Calibrated by Exo, sent back each time it connects".into()
            })
        } else if cal.is_some_and(|c| c.kept) {
            Some("Calibrated by Exo and kept by the controller".into())
        } else {
            Some("Calibrated by Exo until it’s unplugged".into())
        };

        let mut actions = row().gap(8.0).center_y();
        if calibrated && cal.is_some_and(|c| c.can_undo) {
            let id_undo = card.id.clone();
            actions = actions.child(
                device_btn("ctl-cal-undo", "Undo", ui.calibration_busy).on_press(
                    move |s: &mut AppState| s.controller_calibrate_step_inline(&id_undo, "undo"),
                ),
            );
        }
        if usb {
            actions = actions.child(
                device_btn(
                    "ctl-cal-open",
                    if calibrated { "Again" } else { "Calibrate" },
                    ui.calibration_busy,
                )
                .on_press(|s: &mut AppState| s.open_calibrate_dialog()),
            );
        }

        rows.push(device_row(
            "Calibration",
            detail,
            ui.calibration_error.clone(),
            Some(actions),
        ));

        if calibrated && !edge && cal.is_some_and(|c| !c.kept) {
            let armed = ui.keep_armed;
            let id_keep = card.id.clone();
            rows.push(device_row(
                "Keep it after unplugging",
                None,
                None,
                Some(
                    device_btn(
                        "ctl-cal-keep",
                        if armed {
                            "Keep? Leave the cable in"
                        } else {
                            "Keep"
                        },
                        ui.calibration_busy,
                    )
                    .on_press(move |s: &mut AppState| {
                        if s.pc.controller_ui.keep_armed {
                            s.controller_calibrate_step_inline(&id_keep, "keep");
                        } else {
                            s.pc.controller_ui.keep_armed = true;
                        }
                    }),
                ),
            ));
        }
    }

    device_panel("Sticks", rows)
}

fn triggers_panel(s: &AppState, card: &PeripheralCard, edge: bool) -> El {
    let ui = &s.pc.controller_ui;
    let l2 = ui.live.as_ref().map(|l| l.l2).unwrap_or(0);
    let r2 = ui.live.as_ref().map(|l| l.r2).unwrap_or(0);
    let swept = ui.trigger_swept.as_ref();

    let make_row = |label: &'static str, val: u8, most: u8, sw: Option<&TriggerSweep>| {
        let detail = if swept.is_some() {
            match sw {
                Some(sw) => {
                    let reach = if sw.full {
                        "full travel".to_string()
                    } else {
                        format!("{:.0}% to {:.0}%, short of full travel", sw.low, sw.high)
                    };
                    format!("At least {} of 256 steps · {reach}", sw.steps)
                }
                None => "Pull it all the way to measure it".into(),
            }
        } else if most > 0 {
            format!("Pulled to {}%", trigger_share(most))
        } else {
            "Pull it to see its range".into()
        };
        device_row(
            label,
            Some(detail),
            None,
            Some(
                row()
                    .gap(10.0)
                    .center_y()
                    .child(
                        text(format!("{}%", trigger_share(val)))
                            .font(type_small())
                            .color(fg_muted()),
                    )
                    .child(progress_bar(100.0, val as f32 / 255.0)),
            ),
        )
    };

    let id_trig = card.id.clone();
    let mut rows = vec![
        make_row(
            "L2",
            l2,
            ui.trigger_most_l2,
            swept.and_then(|(l, _)| l.as_ref()),
        ),
        make_row(
            "R2",
            r2,
            ui.trigger_most_r2,
            swept.and_then(|(_, r)| r.as_ref()),
        ),
        device_row(
            if ui.trigger_testing {
                "Pull both triggers slowly all the way, a few times"
            } else {
                "Trigger test"
            },
            None,
            ui.trigger_error.clone(),
            Some(
                device_btn(
                    "ctl-trigger-test",
                    if ui.trigger_testing {
                        "Testing…"
                    } else if swept.is_some() {
                        "Test again"
                    } else {
                        "Test"
                    },
                    ui.trigger_testing,
                )
                .on_press(move |s: &mut AppState| s.controller_test_triggers(&id_trig)),
            ),
        ),
    ];

    if edge
        && let Some(live) = ui.live.as_ref()
        && let (Some(sl), Some(sr)) = (live.stop_l, live.stop_r)
    {
        rows.push(device_row(
            "Stop sliders",
            None,
            None,
            Some(status_badge(
                format!("L2 {} · R2 {} of 3", sl + 1, sr + 1),
                BadgeTone::Neutral,
            )),
        ));
    }

    device_panel("Triggers", rows)
}

fn buttons_panel(s: &AppState, card: &PeripheralCard) -> El {
    let ui = &s.pc.controller_ui;
    let id_btn = card.id.clone();
    let mut rows = vec![device_row(
        if ui.button_testing {
            "Press every button a few times, firmly"
        } else {
            "Button test"
        },
        None,
        ui.button_error.clone(),
        Some(
            device_btn(
                "ctl-btn-test",
                if ui.button_testing {
                    "Testing…"
                } else if ui.button_check.is_some() {
                    "Test again"
                } else {
                    "Test"
                },
                ui.button_testing,
            )
            .on_press(move |s: &mut AppState| s.controller_test_buttons(&id_btn)),
        ),
    )];

    if let Some(ref check) = ui.button_check {
        let presses: i32 = check.buttons.iter().map(|b| b.presses).sum();
        if presses == 0 {
            rows.push(device_row(
                "No presses read",
                Some("Press its buttons during the test".into()),
                None,
                None,
            ));
        } else {
            let bouncing: Vec<_> = check.buttons.iter().filter(|b| b.bounces > 0).collect();
            let btn_word = if check.buttons.len() == 1 {
                "button"
            } else {
                "buttons"
            };
            let b_word = if bouncing.len() == 1 {
                "button"
            } else {
                "buttons"
            };
            rows.push(device_row(
                if bouncing.is_empty() {
                    "No chatter seen"
                } else {
                    "Chatter"
                },
                Some(format!(
                    "{presses} presses on {} {btn_word} · Quicker than a report ({:.1} ms) can't be seen",
                    check.buttons.len(),
                    check.report_ms
                )),
                None,
                Some(status_badge(
                    if bouncing.is_empty() {
                        "Clean".into()
                    } else {
                        format!("{} {b_word}", bouncing.len())
                    },
                    if bouncing.is_empty() {
                        BadgeTone::Good
                    } else {
                        BadgeTone::Bad
                    },
                )),
            ));
            for b in bouncing {
                let t_word = if b.bounces == 1 { "time" } else { "times" };
                let shortest = b
                    .shortest
                    .map(|ms| format!("{ms:.1} ms"))
                    .unwrap_or_else(|| "–".into());
                rows.push(device_row(
                    b.name.clone(),
                    Some(format!(
                        "Bounced {} {t_word} in {} presses, as quick as {shortest}",
                        b.bounces, b.presses
                    )),
                    None,
                    None,
                ));
            }
        }
    }

    device_panel("Buttons", rows)
}

fn edge_panels(s: &AppState, read: Option<&ControllerReadReply>) -> Vec<El> {
    let extras =
        s.pc.controller_ui
            .live
            .as_ref()
            .map(|l| l.extras)
            .unwrap_or(0);
    let active_profile = s.pc.controller_ui.live.as_ref().and_then(|l| l.profile);
    let keys_row = row()
        .w_full()
        .flex_wrap()
        .gap(8.0)
        .py(6.0)
        .children(PAD_KEYS.iter().map(|(bit, lbl)| {
            let held = (extras & *bit) != 0;
            status_badge(
                (*lbl).into(),
                if held {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            )
        }));

    let mut out = vec![device_panel("Back and Fn buttons", vec![keys_row])];
    if let Some(slots) = read.and_then(|r| r.slots.as_ref()) {
        let slots_row = row()
            .w_full()
            .flex_wrap()
            .gap(8.0)
            .py(6.0)
            .children(slots.iter().map(|slot| {
                let on = active_profile == Some(slot.number);
                let label = if slot.empty {
                    format!("Slot {} empty", slot.number)
                } else {
                    slot.name
                        .clone()
                        .unwrap_or_else(|| format!("Slot {}", slot.number))
                };
                status_badge(
                    label,
                    if on {
                        BadgeTone::Good
                    } else {
                        BadgeTone::Neutral
                    },
                )
            }));
        out.push(device_panel("Profiles on the controller", vec![slots_row]));
    }
    out
}

fn polling_panel(s: &AppState, card: &PeripheralCard) -> El {
    let ui = &s.pc.controller_ui;
    let id_m = card.id.clone();
    let mut ctl = row().gap(8.0).center_y();
    if let Some(ref p) = ui.polling {
        let prefix = if p.changes { "At least " } else { "" };
        ctl = ctl.child(status_badge(
            format!("{prefix}{:.0} Hz", p.rate),
            BadgeTone::Good,
        ));
    }
    ctl = ctl.child(
        device_btn(
            "ctl-measure",
            if ui.polling_busy {
                "Measuring…"
            } else if ui.polling.is_some() {
                "Again"
            } else {
                "Measure"
            },
            ui.polling_busy,
        )
        .on_press(move |s: &mut AppState| s.controller_measure(&id_m)),
    );

    let mut rows = vec![device_row(
        "Polling rate",
        ui.polling
            .as_ref()
            .filter(|p| p.changes)
            .map(|_| "Keep a stick moving while it measures".into()),
        ui.polling_error.clone(),
        Some(ctl),
    )];

    if let Some(ref p) = ui.polling {
        if !p.changes {
            rows.push(device_row(
                "Slowest 1%",
                None,
                None,
                Some(status_badge(format!("{:.1} ms", p.p99), BadgeTone::Neutral)),
            ));
        }
        if let Some(rep) = p.repeats {
            rows.push(device_row(
                "Repeats",
                None,
                None,
                Some(status_badge(
                    format!("{rep:.0}%"),
                    if rep < 5.0 {
                        BadgeTone::Good
                    } else {
                        BadgeTone::Warn
                    },
                )),
            ));
        }
    }

    device_panel("Polling", rows)
}

struct PadChoiceSpec<'a> {
    card_id: &'a str,
    row_key: &'static str,
    label: &'static str,
    current_val: &'a str,
    choices: &'static [(&'static str, &'static str)],
    error: Option<String>,
}

fn pad_settings_choice_rows(
    s: &AppState,
    spec: PadChoiceSpec<'_>,
    on_pick: impl Fn(&mut PadSettings, &str) + Clone + 'static,
) -> Vec<El> {
    let row_key = spec.row_key;
    let busy = s.pc.controller_ui.pad_settings_busy;
    let is_open = s.pc.controller_ui.open_choice.as_deref() == Some(row_key);
    let mut out = vec![device_row(
        spec.label,
        None,
        spec.error,
        Some(
            device_btn(
                format!("ctl-ch-{row_key}"),
                choice_label(spec.choices, spec.current_val),
                busy,
            )
            .on_press(move |s: &mut AppState| {
                s.pc.controller_ui.open_choice = if is_open { None } else { Some(row_key.into()) };
            }),
        ),
    )];
    if is_open {
        let mut list = row().w_full().flex_wrap().gap(6.0).py(4.0);
        for (val, lbl) in spec.choices {
            let selected = *val == spec.current_val;
            let cid = spec.card_id.to_string();
            let v = *val;
            let f = on_pick.clone();
            list = list.child(
                exo_kit::filter_pill(format!("ctl-opt-{row_key}-{val}"), *lbl, selected).on_press(
                    move |s: &mut AppState| {
                        let mut next = s.pc.controller_ui.pad_settings.clone();
                        f(&mut next, v);
                        s.controller_set_pad_settings(&cid, next);
                    },
                ),
            );
        }
        out.push(list);
    }
    out
}

fn lights_and_sound_panel(s: &AppState, card: &PeripheralCard, sony: bool, usb: bool) -> El {
    let ui = &s.pc.controller_ui;
    let cur = &ui.pad_settings;
    let busy = ui.pad_settings_busy;
    let mut rows = Vec::new();

    if sony {
        if !usb {
            rows.push(device_row(
                "Connect it with a cable to change these",
                None,
                None,
                None,
            ));
        } else {
            let light_val = cur.light.as_deref().unwrap_or("games");
            rows.extend(pad_settings_choice_rows(
                s,
                PadChoiceSpec {
                    card_id: &card.id,
                    row_key: "light",
                    label: "Light bar",
                    current_val: light_val,
                    choices: LIGHT_CHOICES,
                    error: ui.pad_settings_error.clone(),
                },
                |st, v| st.light = if v == "games" { None } else { Some(v.into()) },
            ));

            let id_pl = card.id.clone();
            let pl = cur.player_light;
            rows.push(device_row(
                "Player light",
                None,
                None,
                Some(
                    device_switch("ctl-player-light", "Player light", pl, busy).on_press(
                        move |s: &mut AppState| {
                            let mut next = s.pc.controller_ui.pad_settings.clone();
                            next.player_light = !pl;
                            s.controller_set_pad_settings(&id_pl, next);
                        },
                    ),
                ),
            ));

            let id_mic = card.id.clone();
            let mic_on = !cur.mic_off;
            rows.push(device_row(
                "Microphone",
                None,
                None,
                Some(
                    device_switch("ctl-mic", "Microphone", mic_on, busy).on_press(
                        move |s: &mut AppState| {
                            let mut next = s.pc.controller_ui.pad_settings.clone();
                            next.mic_off = mic_on;
                            s.controller_set_pad_settings(&id_mic, next);
                        },
                    ),
                ),
            ));

            let id_spk = card.id.clone();
            let spk_on = !cur.speaker_off;
            rows.push(device_row(
                "Speaker",
                None,
                None,
                Some(
                    device_switch("ctl-speaker", "Speaker", spk_on, busy).on_press(
                        move |s: &mut AppState| {
                            let mut next = s.pc.controller_ui.pad_settings.clone();
                            next.speaker_off = spk_on;
                            s.controller_set_pad_settings(&id_spk, next);
                        },
                    ),
                ),
            ));

            rows.extend(pad_settings_choice_rows(
                s,
                PadChoiceSpec {
                    card_id: &card.id,
                    row_key: "vibration",
                    label: "Vibration",
                    current_val: &cur.vibration,
                    choices: STRENGTH_CHOICES,
                    error: None,
                },
                |st, v| st.vibration = v.into(),
            ));

            rows.extend(pad_settings_choice_rows(
                s,
                PadChoiceSpec {
                    card_id: &card.id,
                    row_key: "trigger-effects",
                    label: "Trigger effects",
                    current_val: &cur.trigger_effects,
                    choices: TRIGGER_STRENGTH_CHOICES,
                    error: None,
                },
                |st, v| st.trigger_effects = v.into(),
            ));
        }
    }

    let sound_on = ui.sound_on;
    rows.push(device_row(
        "Sound in Windows",
        None,
        ui.sound_error.clone(),
        Some(
            device_switch("ctl-sound", "Sound in Windows", sound_on, ui.sound_busy)
                .on_press(move |s: &mut AppState| s.controller_set_sound(!sound_on)),
        ),
    ));

    device_panel("Lights and sound", rows)
}

fn game_profiles_panel(s: &AppState) -> El {
    let ui = &s.pc.controller_ui;
    let Some(profiles) = ui.game_profiles.as_ref() else {
        return device_panel(
            "Game profiles",
            vec![device_row(
                "Game profiles",
                Some("Reading game profiles…".into()),
                None,
                None,
            )],
        );
    };

    let mut rows = Vec::new();
    for profile in profiles {
        let linked = if profile.games.is_empty() {
            "No games linked".to_string()
        } else {
            let names: Vec<String> = profile
                .games
                .iter()
                .map(|gid| {
                    s.games
                        .iter()
                        .find(|g| g.id == *gid)
                        .map(|g| g.title.clone())
                        .unwrap_or_else(|| "A removed game".into())
                })
                .collect();
            if names.len() <= 2 {
                names.join(" and ")
            } else {
                format!("{}, {} and {} more", names[0], names[1], names.len() - 2)
            }
        };
        let p_clone = profile.clone();
        rows.push(device_row(
            profile.name.clone(),
            Some(linked),
            None,
            Some(
                device_btn(format!("ctl-gp-edit-{}", profile.id), "Edit", false)
                    .on_press(move |s: &mut AppState| s.open_profile_editor(Some(p_clone.clone()))),
            ),
        ));
    }

    rows.push(device_row(
        if profiles.is_empty() {
            "A game’s own light, sound and vibration"
        } else {
            "Another game profile"
        },
        None,
        ui.game_profiles_error.clone(),
        Some(
            device_btn("ctl-gp-new", "New", false)
                .on_press(|s: &mut AppState| s.open_profile_editor(None)),
        ),
    ));

    device_panel("Game profiles", rows)
}

fn memory_panel(read: &ControllerReadReply, usb: bool) -> El {
    if !read.remembered.unwrap_or(false) {
        return device_panel(
            "What Exo remembers",
            vec![device_row(
                if usb {
                    "Reading which controller this is"
                } else {
                    "Connect it with a cable"
                },
                None,
                None,
                None,
            )],
        );
    }
    let has_saved = read.settings.as_ref().is_some_and(pad_settings_changed);
    let cal = read.calibration.as_ref();
    let (cal_text, cal_tone) = if !cal.is_some_and(|c| c.calibrated) {
        ("Not calibrated", BadgeTone::Neutral)
    } else if cal.and_then(|c| c.holds) == Some(true) {
        ("On the controller", BadgeTone::Good)
    } else if cal.and_then(|c| c.holds) == Some(false) {
        ("Goes back when it reconnects", BadgeTone::Neutral)
    } else {
        ("Saved", BadgeTone::Good)
    };
    let mut rows = vec![
        device_row(
            "Light, sound and vibration",
            None,
            None,
            Some(status_badge(
                if has_saved {
                    "Saved".into()
                } else {
                    "The controller’s own".into()
                },
                if has_saved {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            )),
        ),
        device_row(
            "Calibration",
            None,
            None,
            Some(status_badge(cal_text.into(), cal_tone)),
        ),
    ];
    if let Some(put_back) = cal.and_then(|c| c.put_back.as_ref()) {
        rows.push(device_row(
            "Last put back",
            None,
            None,
            Some(status_badge(
                put_back.get(..16).unwrap_or(put_back).replace('T', " "),
                BadgeTone::Neutral,
            )),
        ));
    }
    device_panel("What Exo remembers", rows)
}

fn port_panel(port: &exo_rpc::PeripheralPort) -> El {
    let good_port = port.r#where.as_deref() == Some("cpu") && port.hubs == 0;
    let mut rows = vec![device_row(
        "Port",
        None,
        None,
        Some(status_badge(
            port_words(port),
            if good_port {
                BadgeTone::Good
            } else {
                BadgeTone::Neutral
            },
        )),
    )];
    if let Some(ref intr) = port.interrupts {
        rows.push(device_row(
            "Interrupts",
            None,
            None,
            Some(status_badge(
                if intr == "msi" {
                    "MSI".into()
                } else {
                    "Shared line".into()
                },
                if intr == "msi" {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            )),
        ));
    }
    if port.better.as_deref() == Some("free") {
        rows.push(device_row(
            "Use a free port on the processor’s own USB",
            None,
            None,
            None,
        ));
    } else if port.better.as_deref() == Some("hub") {
        rows.push(device_row(
            "Plug it straight into the PC, not into a hub",
            None,
            None,
            None,
        ));
    }
    device_panel("USB port", rows)
}

fn layers_panel(s: &AppState, layers: &exo_rpc::ControllerLayers) -> El {
    let mut rows = Vec::new();
    if let Some(ref si) = s.pc.controller_ui.steam_input
        && si.steam
    {
        let g_word = if si.native == 1 { "game" } else { "games" };
        rows.push(device_row(
            "Games read it themselves",
            (si.translated > 0).then(|| format!("{} keep Steam Input", si.translated)),
            None,
            Some(status_badge(
                format!("{} {g_word}", si.native),
                BadgeTone::Good,
            )),
        ));
    }
    for tool in &layers.tools {
        rows.push(device_row(
            tool.name.clone(),
            None,
            None,
            Some(status_badge(
                if tool.running {
                    "Running".into()
                } else {
                    "Not running".into()
                },
                if tool.running {
                    BadgeTone::Warn
                } else {
                    BadgeTone::Good
                },
            )),
        ));
    }
    if layers.polling_driver {
        rows.push(device_row(
            "Polling driver",
            None,
            None,
            Some(status_badge(
                if layers.memory_integrity == Some(true) {
                    "Blocked by Memory Integrity".into()
                } else {
                    "Installed".into()
                },
                BadgeTone::Neutral,
            )),
        ));
    }
    if rows.is_empty() && layers.steam_play_station.is_none() {
        rows.push(device_row("Nothing in the way", None, None, None));
    }
    device_panel("Between it and your games", rows)
}

fn calibrate_dialog_overlay(s: &AppState, card: &PeripheralCard, edge: bool) -> Option<El> {
    let dlg = &s.pc.controller_ui.calibrate_dialog;
    if !dlg.open {
        return None;
    }
    let title = match dlg.phase {
        CalibrationPhase::Intro => "Calibrate the sticks",
        CalibrationPhase::Before | CalibrationPhase::Center | CalibrationPhase::After => {
            "Leave both sticks alone"
        }
        CalibrationPhase::Range | CalibrationPhase::Ending => "Roll both sticks",
        CalibrationPhase::Done => "Calibrated",
    };

    let mut content = col().w_full().gap(12.0);
    match dlg.phase {
        CalibrationPhase::Intro => {
            let copy = if edge {
                "First the centers, with the sticks left alone: your Edge takes its own, then Exo measures them and corrects what’s left. Then roll both sticks firmly around their edge: Exo measures how far each one goes in every direction and sets each so a normal push gives full output, the same all the way round. An Edge’s sticks lock their own memory, so it lasts until you unplug it; Exo keeps it and gives it back each time it connects."
            } else {
                "First the centers, with the sticks left alone: your DualSense takes its own, then Exo measures them and corrects what’s left. Then its range, while you roll both sticks around their edge. It lasts until you unplug it, unless you keep it."
            };
            content = content.child(text(copy).font(type_body()).color(fg_muted()));
        }
        CalibrationPhase::Before | CalibrationPhase::Center | CalibrationPhase::After => {
            content = content.child(
                text("Put the controller down and don’t touch the sticks.")
                    .font(type_body())
                    .color(fg_muted()),
            );
        }
        CalibrationPhase::Range | CalibrationPhase::Ending => {
            let lp = sweep_percent(&dlg.swept_left);
            let rp = sweep_percent(&dlg.swept_right);
            content = content
                .child(
                    text("Slowly, all the way around, both directions, a few times. Then press Done.")
                        .font(type_body())
                        .color(fg_muted()),
                )
                .child(
                    row()
                        .w_full()
                        .gap(16.0)
                        .child(
                            col()
                                .grow(1.0)
                                .gap(6.0)
                                .child(text(format!("Left stick · {lp}%")).font(type_small()).color(FG))
                                .child(progress_bar(180.0, lp as f32 / 100.0)),
                        )
                        .child(
                            col()
                                .grow(1.0)
                                .gap(6.0)
                                .child(text(format!("Right stick · {rp}%")).font(type_small()).color(FG))
                                .child(progress_bar(180.0, rp as f32 / 100.0)),
                        ),
                );
        }
        CalibrationPhase::Done => {
            for (idx, stick) in dlg.after.iter().enumerate() {
                let lbl = if idx == 0 {
                    "Left stick center"
                } else {
                    "Right stick center"
                };
                content = content.child(device_row(
                    lbl,
                    None,
                    None,
                    Some(
                        text(format!(
                            "{} → {}",
                            stick_offset_summary(dlg.before.get(idx)),
                            stick_offset_summary(Some(stick))
                        ))
                        .font(type_body())
                        .color(good()),
                    ),
                ));
            }
        }
    }

    if let Some(ref err) = dlg.error {
        content = content.child(text(err.clone()).font(type_small()).color(bad()));
    }

    let id_act = card.id.clone();
    let actions = match dlg.phase {
        CalibrationPhase::Intro => row()
            .gap(8.0)
            .child(
                device_btn("cal-cancel", "Cancel", false)
                    .on_press(|s: &mut AppState| s.close_calibrate_dialog()),
            )
            .child(
                primary_button("cal-start", "Start", None)
                    .on_press(move |s: &mut AppState| s.start_calibration_flow(&id_act)),
            ),
        CalibrationPhase::Range => row()
            .gap(8.0)
            .child(
                device_btn("cal-cancel", "Cancel", false)
                    .on_press(|s: &mut AppState| s.close_calibrate_dialog()),
            )
            .child(
                primary_button("cal-finish", "Done", None)
                    .on_press(move |s: &mut AppState| s.finish_calibration_flow(&id_act)),
            ),
        CalibrationPhase::Done => row().child(
            primary_button("cal-close", "Close", None)
                .on_press(|s: &mut AppState| s.close_calibrate_dialog()),
        ),
        _ => row().child(device_btn("cal-working", "Working…", true)),
    };

    let card_el = col()
        .key("ctl-calibrate-modal")
        .w(480.0)
        .padding(24.0)
        .gap(16.0)
        .radius(RADIUS_SHEET)
        .fill(glass_panel_fill())
        .backdrop_blur(GLASS_PANEL_SIGMA)
        .border(1.0, glass_rim())
        .motion(SPRING_BASE)
        .transition(modal_card_transition())
        .child(text(title).font(type_title()).color(FG))
        .child(content)
        .child(row().w_full().justify_end().child(actions));

    Some(
        el().key("ctl-calibrate-scrim")
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .fill(scrim_fill())
            .center()
            .transition(modal_scrim_transition())
            .child(card_el),
    )
}

fn profile_choice_rows(
    ed: &ProfileEditorState,
    row_key: &'static str,
    label: &'static str,
    current_val: &str,
    choices: &'static [(&'static str, &'static str)],
    on_pick: impl Fn(&mut PadOverrides, &str) + Clone + 'static,
) -> Vec<El> {
    let is_open = ed.open_choice.as_deref() == Some(row_key);
    let mut out = vec![device_row(
        label,
        None,
        None,
        Some(
            device_btn(
                format!("gp-ch-{row_key}"),
                choice_label(choices, current_val),
                ed.busy,
            )
            .on_press(move |s: &mut AppState| {
                if let Some(ref mut e) = s.pc.controller_ui.profile_editor {
                    e.open_choice = if is_open { None } else { Some(row_key.into()) };
                }
            }),
        ),
    )];
    if is_open {
        let mut list = row().w_full().flex_wrap().gap(6.0).py(4.0);
        for (val, lbl) in choices {
            let selected = *val == current_val;
            let v = *val;
            let f = on_pick.clone();
            list = list.child(
                exo_kit::filter_pill(format!("gp-opt-{row_key}-{val}"), *lbl, selected).on_press(
                    move |s: &mut AppState| {
                        if let Some(ref mut e) = s.pc.controller_ui.profile_editor {
                            f(&mut e.draft.settings, v);
                            e.open_choice = None;
                        }
                    },
                ),
            );
        }
        out.push(list);
    }
    out
}

fn profile_editor_overlay(s: &AppState) -> Option<El> {
    let ed = s.pc.controller_ui.profile_editor.as_ref()?;
    let profiles = s.pc.controller_ui.game_profiles.as_deref().unwrap_or(&[]);

    if ed.game_picker_open {
        let q = ed.game_query.text.trim().to_lowercase();
        let mut installed: Vec<_> = s
            .games
            .iter()
            .filter(|g| g.installed && (q.is_empty() || g.title.to_lowercase().contains(&q)))
            .collect();
        installed.sort_by_key(|a| a.title.to_lowercase());

        let mut list = col()
            .key("gp-game-pick-scroll")
            .w_full()
            .max_h(340.0)
            .scroll(ScrollSpec {
                stick_bottom: false,
            });
        for game in installed {
            let on = ed.draft.games.contains(&game.id);
            let elsewhere = if on {
                None
            } else {
                profiles
                    .iter()
                    .find(|p| p.id != ed.draft.id && p.games.contains(&game.id))
                    .map(|p| format!("On {}", p.name))
            };
            let gid = game.id.clone();
            list = list.child(device_row(
                game.title.clone(),
                elsewhere,
                None,
                Some(
                    device_switch(
                        format!("gp-game-{}", game.id),
                        game.title.clone(),
                        on,
                        false,
                    )
                    .on_press(move |s: &mut AppState| {
                        if let Some(ref mut e) = s.pc.controller_ui.profile_editor {
                            if let Some(pos) = e.draft.games.iter().position(|x| x == &gid) {
                                e.draft.games.remove(pos);
                            } else {
                                e.draft.games.push(gid.clone());
                            }
                        }
                    }),
                ),
            ));
        }

        let picker_card = col()
            .key("gp-game-picker-modal")
            .w(500.0)
            .padding(24.0)
            .gap(14.0)
            .radius(RADIUS_SHEET)
            .fill(glass_panel_fill())
            .backdrop_blur(GLASS_PANEL_SIGMA)
            .border(1.0, glass_rim())
            .motion(SPRING_BASE)
            .child(
                text(format!(
                    "Games for {}",
                    if ed.draft.name.trim().is_empty() {
                        "this profile"
                    } else {
                        &ed.draft.name
                    }
                ))
                .font(type_title())
                .color(FG),
            )
            .child(
                row()
                    .w_full()
                    .h(36.0)
                    .px(12.0)
                    .center_y()
                    .radius(RADIUS_PILL)
                    .fill(exo_kit::glass_control_fill())
                    .border(1.0, glass_rim())
                    .child(
                        text_input(&ed.game_query, type_body())
                            .key("gp-game-search")
                            .label("Find a game")
                            .single_line()
                            .grow(1.0)
                            .color(FG)
                            .placeholder("Find a game", fg_faint())
                            .on_edit(edit_access(|s: &mut AppState| {
                                &mut s
                                    .pc
                                    .controller_ui
                                    .profile_editor
                                    .as_mut()
                                    .unwrap()
                                    .game_query
                            })),
                    ),
            )
            .child(list)
            .child(row().w_full().justify_end().child(
                primary_button("gp-picker-done", "Done", None).on_press(|s: &mut AppState| {
                    if let Some(ref mut e) = s.pc.controller_ui.profile_editor {
                        e.game_picker_open = false;
                    }
                }),
            ));

        return Some(
            el().key("gp-game-picker-scrim")
                .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
                .fill(scrim_fill())
                .center()
                .child(picker_card),
        );
    }

    let ov = &ed.draft.settings;
    let onoff_str = |v: Option<bool>, inv: bool| match v {
        None => "same",
        Some(b) => {
            if b != inv {
                "on"
            } else {
                "off"
            }
        }
    };
    let mut rows = vec![device_row(
        "Games",
        Some(format!("{} linked", ed.draft.games.len())),
        None,
        Some(
            device_btn("gp-choose-games", "Choose", ed.busy).on_press(|s: &mut AppState| {
                if let Some(ref mut e) = s.pc.controller_ui.profile_editor {
                    e.game_picker_open = true;
                }
            }),
        ),
    )];
    rows.extend(profile_choice_rows(
        ed,
        "light",
        "Light bar",
        ov.light.as_deref().unwrap_or("same"),
        PROFILE_LIGHT_CHOICES,
        |o, v| o.light = (v != "same").then(|| v.into()),
    ));
    rows.extend(profile_choice_rows(
        ed,
        "playerLight",
        "Player light",
        onoff_str(ov.player_light, false),
        PROFILE_ONOFF_CHOICES,
        |o, v| o.player_light = (v != "same").then(|| v == "on"),
    ));
    rows.extend(profile_choice_rows(
        ed,
        "micOff",
        "Microphone",
        onoff_str(ov.mic_off, true),
        PROFILE_ONOFF_CHOICES,
        |o, v| o.mic_off = (v != "same").then(|| v != "on"),
    ));
    rows.extend(profile_choice_rows(
        ed,
        "speakerOff",
        "Speaker",
        onoff_str(ov.speaker_off, true),
        PROFILE_ONOFF_CHOICES,
        |o, v| o.speaker_off = (v != "same").then(|| v != "on"),
    ));
    rows.extend(profile_choice_rows(
        ed,
        "vibration",
        "Vibration",
        ov.vibration.as_deref().unwrap_or("same"),
        PROFILE_STRENGTH_CHOICES,
        |o, v| o.vibration = (v != "same").then(|| v.into()),
    ));
    rows.extend(profile_choice_rows(
        ed,
        "triggerEffects",
        "Trigger effects",
        ov.trigger_effects.as_deref().unwrap_or("same"),
        PROFILE_TRIGGER_CHOICES,
        |o, v| o.trigger_effects = (v != "same").then(|| v.into()),
    ));

    let name_empty = ed.name_edit.text.trim().is_empty();
    let mut foot = row().w_full().justify_end().gap(8.0).child(
        device_btn("gp-cancel", "Cancel", ed.busy).on_press(|s: &mut AppState| {
            s.pc.controller_ui.profile_editor = None;
        }),
    );
    if !ed.is_new {
        let del_id = ed.draft.id.clone();
        foot = foot.child(
            exo_kit::danger_button("gp-delete", "Delete", None).on_press(
                move |s: &mut AppState| {
                    let next: Vec<_> =
                        s.pc.controller_ui
                            .game_profiles
                            .clone()
                            .unwrap_or_default()
                            .into_iter()
                            .filter(|p| p.id != del_id)
                            .collect();
                    s.save_controller_game_profiles(next);
                },
            ),
        );
    }
    foot = foot.child(if name_empty || ed.busy {
        device_btn("gp-save", "Save", true)
    } else {
        primary_button("gp-save", "Save", None).on_press(|s: &mut AppState| {
            if let Some(ref ed) = s.pc.controller_ui.profile_editor {
                let mut draft = ed.draft.clone();
                draft.name = ed.name_edit.text.trim().chars().take(40).collect();
                let current = s.pc.controller_ui.game_profiles.clone().unwrap_or_default();
                let next = save_profile_list(&current, draft);
                s.save_controller_game_profiles(next);
            }
        })
    });

    let modal = col()
        .key("gp-editor-modal")
        .w(500.0)
        .max_h(760.0)
        .padding(24.0)
        .gap(14.0)
        .radius(RADIUS_SHEET)
        .fill(glass_panel_fill())
        .backdrop_blur(GLASS_PANEL_SIGMA)
        .border(1.0, glass_rim())
        .motion(SPRING_BASE)
        .child(
            text(if ed.is_new {
                "New game profile".to_string()
            } else {
                ed.draft.name.clone()
            })
            .font(type_title())
            .color(FG),
        )
        .child(
            row()
                .w_full()
                .h(36.0)
                .px(12.0)
                .center_y()
                .radius(RADIUS_PILL)
                .fill(exo_kit::glass_control_fill())
                .border(1.0, glass_rim())
                .child(
                    text_input(&ed.name_edit, type_body())
                        .key("gp-name-input")
                        .label("Profile name")
                        .single_line()
                        .grow(1.0)
                        .color(FG)
                        .placeholder("Profile name", fg_faint())
                        .on_edit(edit_access(|s: &mut AppState| {
                            &mut s
                                .pc
                                .controller_ui
                                .profile_editor
                                .as_mut()
                                .unwrap()
                                .name_edit
                        }))
                        .on_input_change(|s: &mut AppState, _| {
                            if let Some(ref mut e) = s.pc.controller_ui.profile_editor {
                                e.draft.name = e.name_edit.text.chars().take(40).collect();
                            }
                        }),
                ),
        )
        .child(
            col()
                .w_full()
                .grow(1.0)
                .min_h(0.0)
                .scroll(ScrollSpec {
                    stick_bottom: false,
                })
                .children(rows),
        )
        .children(
            ed.error
                .as_ref()
                .map(|err| text(err.clone()).font(type_small()).color(bad())),
        )
        .child(foot);

    Some(
        el().key("gp-editor-scrim")
            .pin(Some(0.0), Some(0.0), Some(0.0), Some(0.0))
            .fill(scrim_fill())
            .center()
            .child(modal),
    )
}

pub fn controller_page(
    s: &AppState,
    card: &PeripheralCard,
    display_name: String,
    status: String,
    scale: f32,
) -> El {
    let ui = &s.pc.controller_ui;
    let read = ui
        .read
        .as_ref()
        .filter(|_| ui.read_id.as_deref() == Some(card.id.as_str()));
    let edge = card.model.as_deref() == Some("dualsense-edge")
        || read.and_then(|r| r.edge).unwrap_or(false);
    let sony = edge || card.model.as_deref() == Some("dualsense");
    let usb = pad_settable(card.connection.as_deref());
    let firmware = read.and_then(|r| r.firmware.as_ref());

    let media = if let Some(photo) =
        s.pc.device_images
            .get(&card.id)
            .and_then(|u| s.device_image(u, scale))
    {
        el().size(300.0, 200.0)
            .pixels_fit(photo.pixels(), SurfaceFit::Fit)
    } else {
        icon_gamepad(64.0, fg_muted())
    };

    let charge = card
        .battery
        .map(|b| (b * 100.0).round() as i32)
        .or_else(|| ui.live.as_ref().and_then(|l| l.battery));
    let charging = ui.live.as_ref().is_some_and(|l| l.charging);

    let mut stats = Vec::new();
    if let Some(pct) = charge {
        let label = if charging && pct < 100 {
            format!("{pct}% charging")
        } else {
            format!("{pct}%")
        };
        stats.push(("Battery", label));
    }
    if let Some(ref p) = ui.polling {
        let prefix = if p.changes { "At least " } else { "" };
        stats.push(("Polling", format!("{prefix}{:.0} Hz", p.rate)));
    }
    if let Some(ref color) = card.color {
        stats.push(("Color", color.clone()));
    }
    if let Some(fw) = firmware {
        stats.push(("Firmware", fw.version.clone()));
    }

    let mut panels = vec![
        sticks_panel(s, card, read, sony, usb, edge),
        triggers_panel(s, card, edge),
        buttons_panel(s, card),
    ];
    if edge {
        panels.extend(edge_panels(s, read));
    }
    panels.push(polling_panel(s, card));
    if sony {
        panels.push(game_profiles_panel(s));
    }
    if let Some(r) = read {
        panels.push(lights_and_sound_panel(s, card, sony, usb));
        if sony {
            panels.push(memory_panel(r, usb));
        }
    }
    if let Some(ref port) = card.port {
        panels.push(port_panel(port));
    }
    if let Some(fw) = firmware
        && fw.current == Some(false)
    {
        let latest = fw.latest.as_deref().unwrap_or("");
        panels.push(device_panel(
            "Firmware",
            vec![device_row(
                format!("Firmware {latest} is out"),
                None,
                None,
                Some(
                    device_btn("ctl-fw-update", "How to update", false)
                        .on_press(|s: &mut AppState| s.open_external_url(SONY_UPDATE_URL)),
                ),
            )],
        ));
    }
    if let Some(layers) = read.and_then(|r| r.layers.as_ref()) {
        panels.push(layers_panel(s, layers));
    }

    let id_rescan = card.id.clone();
    let page = device_page_shell(
        DevicePageHeader {
            key: "room-devices-controller",
            name: display_name,
            status,
            media,
            stats,
            credit: None,
        },
        panels,
        |s| s.close_controller_page(),
        move |s| {
            s.load_pc_devices();
            s.open_controller_page(id_rescan.clone());
        },
    );

    let mut overlay = Vec::new();
    if let Some(cal_ov) = calibrate_dialog_overlay(s, card, edge) {
        overlay.push(cal_ov);
    }
    if let Some(gp_ov) = profile_editor_overlay(s) {
        overlay.push(gp_ov);
    }

    if overlay.is_empty() {
        page
    } else {
        el().w_full().h_full().child(page).children(overlay)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn axis_percent_and_deadzone_match_react_formulas() {
        assert_eq!(axis_percent(None), "–");
        assert_eq!(axis_percent(Some(255)), "+100.0");
        assert_eq!(axis_percent(Some(0)), "−100.0");
        let rest = StickRest {
            offset_x: 1.5,
            offset_y: -0.4,
            noise: 2,
            creep: None,
        };
        assert_eq!(deadzone_for(&rest), 4);
    }

    #[test]
    fn sweep_bin_ignores_center_and_tracks_perimeter() {
        assert_eq!(sweep_bin(128, 128), None);
        assert!(sweep_bin(255, 128).is_some());
        assert!(sweep_bin(0, 128).is_some());
        let mut set = HashSet::new();
        for b in 0..18 {
            set.insert(b);
        }
        assert_eq!(sweep_percent(&set), 50);
    }

    #[test]
    fn stick_verdict_distinguishes_good_and_needs_calibration() {
        let good_rest = StickRest {
            offset_x: 0.8,
            offset_y: -0.5,
            noise: 1,
            creep: Some(0.0),
        };
        let good_edge = StickEdge {
            reach: 99.0,
            circularity: 3.5,
            covered: 0.95,
        };
        let (msg, verdict) = stick_verdict(Some(&good_rest), Some(&good_edge), true);
        assert_eq!(verdict, Some(true));
        assert!(msg.contains("reaches 99%"));

        let bad_rest = StickRest {
            offset_x: 4.2,
            offset_y: 0.0,
            noise: 3,
            creep: Some(1.5),
        };
        let (_, bad_v) = stick_verdict(Some(&bad_rest), Some(&good_edge), true);
        assert_eq!(bad_v, Some(false));
    }

    #[test]
    fn save_profile_list_moves_games_from_other_profiles() {
        let p1 = ControllerGameProfile {
            id: "p1".into(),
            name: "FPS".into(),
            settings: PadOverrides::default(),
            games: vec!["g1".into(), "g2".into()],
        };
        let p2 = ControllerGameProfile {
            id: "p2".into(),
            name: "Story".into(),
            settings: PadOverrides::default(),
            games: vec!["g2".into()],
        };
        let out = save_profile_list(&[p1], p2);
        assert_eq!(out.len(), 2);
        assert_eq!(out[0].games, vec!["g1".to_string()]);
        assert_eq!(out[1].games, vec!["g2".to_string()]);
    }
}
