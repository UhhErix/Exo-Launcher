use crate::events::HostEventKind;
use crate::pc_menus::{menu_btn, menu_row, menu_switch};
use crate::state::{AppState, HostStatus};
use exo_kit::{
    BadgeTone, FG, RADIUS_PILL, fg_muted, float_line, status_badge, subtle_fill, type_control,
    type_small, warn,
};
use exo_rpc::{
    HdrAutoHdr, HdrCalibrateResult, HdrCalibration, HdrCalibrationProgress, HdrDisplayInfo,
    HdrGame, HdrLaptopCheckItem, HdrLaptopChecks, HdrPeakHealth, HdrState, HostEvent,
    METHOD_HDR_CALIBRATE, METHOD_HDR_STATE,
};
use rune::{El, col, el, row, text};
use serde_json::json;

#[derive(Default)]
pub struct HdrCalibrationUiState {
    pub calibrating: bool,
    pub progress: Option<Box<HdrCalibrationProgress>>,
    pub cal_result: Option<Box<HdrCalibrateResult>>,
    pub cal_error: Option<String>,
}

pub fn verdict_text(verdict: Option<&str>) -> &'static str {
    match verdict.unwrap_or("unknown") {
        "great" => "Bright enough for real HDR",
        "good" => "Fine for HDR",
        "weak" => "Too dim for good HDR",
        _ => "Brightness not reported",
    }
}

pub fn color_mode_label(mode: &str) -> &str {
    match mode {
        "hdr" => "HDR",
        "acm" => "Auto Color Management (ACM)",
        "sdr" => "SDR",
        other => other,
    }
}

pub fn format_peak_summary(edid: Option<i32>, dxgi: Option<i32>) -> Option<String> {
    match (edid.filter(|&v| v > 0), dxgi.filter(|&v| v > 0)) {
        (Some(e), Some(d)) => Some(format!("{e} nits EDID · {d} nits reported")),
        (Some(e), None) => Some(format!("{e} nits EDID")),
        (None, Some(d)) => Some(format!("{d} nits reported")),
        (None, None) => None,
    }
}

pub fn calibration_progress_text(progress: Option<&HdrCalibrationProgress>) -> String {
    match progress {
        Some(p) => {
            if let Some(nits) = p.measured_nits
                && nits > 0
            {
                format!("Step {} of {} · {nits} nits", p.step, p.total)
            } else {
                format!("Step {} of {}", p.step, p.total)
            }
        }
        None => "Calibrating…".into(),
    }
}

pub fn laptop_check_status_label(status: &str, is_warn: bool) -> &'static str {
    match status {
        "active" if is_warn => "Active",
        "active" => "On",
        "off" => "Off",
        _ => "Can't detect",
    }
}

pub fn build_game_hdr_word(
    game: &HdrGame,
    hdr_for_games: bool,
    error: Option<&str>,
) -> Option<(String, bool)> {
    if let Some(err) = error
        && !err.is_empty()
    {
        return Some((err.to_string(), true));
    }
    let auto_hdr_tag = game
        .auto_hdr
        .as_ref()
        .filter(|a| !a.effective.is_empty() && a.effective != "default")
        .map(|a| format!("Windows Auto HDR: {}", a.effective));
    let mut parts: Vec<String> = Vec::new();
    if let Some(ref reason) = game.reason
        && !reason.is_empty()
    {
        parts.push(reason.clone());
    }
    if let Some(tag) = auto_hdr_tag {
        parts.push(tag);
    }
    let why = parts.join(" · ");
    let mode = game.mode.as_deref().unwrap_or("auto");
    if !hdr_for_games && mode != "off" {
        return Some((if why.is_empty() { "Off".into() } else { why }, false));
    }
    if why.is_empty() {
        None
    } else {
        Some((why, false))
    }
}

pub fn show_cursor_fix(screen: &HdrState, game: &HdrGame) -> bool {
    !screen.internal && game.cursor_fix_eligible
}

pub fn cursor_fix_word(game: &HdrGame) -> String {
    if game.cursor_fix {
        "Raises SDR white to 220 nits while playing and restores on exit".into()
    } else {
        let nits = game.sdr_white_nits.unwrap_or(0.0).round() as i32;
        format!("SDR white is {nits} nits (dim hardware cursor in HDR)")
    }
}

pub fn register_events(state: &mut AppState) {
    state.register_event(HostEventKind::HdrCalibrationProgress, |s, ev| {
        let HostEvent::HdrCalibrationProgress(progress) = ev else {
            return false;
        };
        s.pc.hdr_ui.calibrating = true;
        s.pc.hdr_ui.progress = Some(Box::new(progress));
        true
    });

    state.register_event(HostEventKind::HdrWarning, |s, ev| {
        let HostEvent::HdrWarning(warning) = ev else {
            return false;
        };
        if let Some(ref mut hdr) = s.pc.hdr {
            hdr.brightness_warning = Some(warning.message);
        }
        if s.status == HostStatus::Connected {
            s.refresh_hdr_state();
        }
        true
    });
}

impl AppState {
    pub fn refresh_hdr_state(&mut self) {
        if self.status != HostStatus::Connected {
            return;
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, HdrState>(METHOD_HDR_STATE, &json!({}), move |res| {
                if let Ok(next) = res {
                    handle.update(move |s, _| {
                        s.pc.hdr = Some(next);
                    });
                }
            });
        }
    }

    pub fn run_hdr_calibrate(&mut self, action: &'static str, enabled: Option<bool>) {
        let device_path = self.pc.hdr.as_ref().and_then(|h| h.device_path.clone());
        let is_long = action != "toggle";
        if is_long {
            self.pc.hdr_ui.calibrating = true;
            self.pc.hdr_ui.progress = None;
            self.pc.hdr_ui.cal_result = None;
        }
        self.pc.hdr_ui.cal_error = None;

        if self.status != HostStatus::Connected {
            self.pc.hdr_ui.calibrating = false;
            match action {
                "undo" => {
                    if let Some(ref mut hdr) = self.pc.hdr {
                        hdr.calibration = None;
                    }
                    self.pc.hdr_ui.cal_result = Some(Box::new(HdrCalibrateResult {
                        ok: true,
                        status: "undone".into(),
                        message: "Neutral-pair calibration removed and display restored.".into(),
                        manual_steps: None,
                        entry: None,
                    }));
                }
                "toggle" => {
                    if let Some(ref mut hdr) = self.pc.hdr
                        && let Some(ref mut cal) = hdr.calibration
                    {
                        cal.enabled = enabled.unwrap_or(!cal.enabled);
                    }
                }
                _ => {
                    let entry = HdrCalibration {
                        device_path: device_path.unwrap_or_else(|| "DISPLAY#INTERNAL#1".into()),
                        edid_peak_nits: 600,
                        measured_peak_nits: 598,
                        global_brightness: 80,
                        sdr_white_nits: 240.0,
                        enabled: true,
                        calibrated_at_utc: "2026-11-23T00:00:00Z".into(),
                    };
                    if let Some(ref mut hdr) = self.pc.hdr {
                        hdr.calibration = Some(entry.clone());
                    }
                    self.pc.hdr_ui.cal_result = Some(Box::new(HdrCalibrateResult {
                        ok: true,
                        status: "ok".into(),
                        message: "Neutral pair found.".into(),
                        manual_steps: None,
                        entry: Some(entry),
                    }));
                }
            }
            return;
        }

        let fallback = match action {
            "undo" => "Could not undo calibration. Try again.",
            "toggle" => "Could not update calibration setting. Try again.",
            _ => "Calibration failed. Try again.",
        };
        let mut body = serde_json::Map::new();
        body.insert("action".into(), json!(action));
        if let Some(dp) = device_path {
            body.insert("devicePath".into(), json!(dp));
        }
        if let Some(en) = enabled {
            body.insert("enabled".into(), json!(en));
        }
        #[cfg(windows)]
        if let (Some(rpc), Some(handle)) = (self.rpc.clone(), self.handle.clone()) {
            rpc.call::<_, HdrCalibrateResult>(
                METHOD_HDR_CALIBRATE,
                &serde_json::Value::Object(body),
                move |res| {
                    handle.update(move |s, _| {
                        if is_long {
                            s.pc.hdr_ui.calibrating = false;
                            s.pc.hdr_ui.progress = None;
                        }
                        match res {
                            Ok(reply) => {
                                if !reply.ok && reply.manual_steps.is_none() {
                                    s.pc.hdr_ui.cal_error = Some(reply.message.clone());
                                }
                                if is_long {
                                    s.pc.hdr_ui.cal_result = Some(Box::new(reply));
                                }
                                s.refresh_hdr_state();
                            }
                            Err(_) => {
                                s.pc.hdr_ui.cal_error = Some(fallback.into());
                            }
                        }
                    });
                },
            );
        }
    }
}

fn internal_panel_badge() -> El {
    el().key("hdr-internal-badge")
        .h(22.0)
        .px(8.0)
        .radius(RADIUS_PILL)
        .center()
        .fill(subtle_fill())
        .border(1.0, float_line())
        .child(text("Internal panel").font(type_small()).color(fg_muted()))
}

fn warn_row(label: impl Into<String>, detail: Option<String>, right: El) -> El {
    col()
        .w_full()
        .child(el().w_full().h(1.0).fill(float_line()))
        .child(
            row()
                .w_full()
                .py(10.0)
                .gap(12.0)
                .justify_between()
                .center_y()
                .child(
                    col()
                        .grow(1.0)
                        .min_w(0.0)
                        .gap(2.0)
                        .child(text(label.into()).font(type_control()).color(FG))
                        .children(detail.map(|d| text(d).font(type_small()).color(warn()).wrap())),
                )
                .child(right),
        )
}

pub(crate) fn hdr_rows_view(s: &AppState) -> Option<El> {
    let hdr = s.pc.hdr.as_ref()?;
    if !hdr.supported {
        return None;
    }

    let mut body = col().key("pc-hdr-rows").w_full();

    let good_hdr = hdr.verdict.as_deref() != Some("weak");
    let mut hdr_right = row().gap(8.0).center_y();
    if hdr.internal {
        hdr_right = hdr_right.child(internal_panel_badge());
    }
    hdr_right = hdr_right.child(status_badge(
        verdict_text(hdr.verdict.as_deref()).into(),
        if good_hdr {
            BadgeTone::Good
        } else {
            BadgeTone::Neutral
        },
    ));
    body = body.child(menu_row("HDR", None, None, hdr_right));

    if let Some(ref health) = hdr.peak_health
        && let Some(summary) = format_peak_summary(health.edid_nits, health.dxgi_nits)
    {
        let tone = match health.status.as_str() {
            "ok" => BadgeTone::Good,
            "mismatch" => BadgeTone::Warn,
            _ => BadgeTone::Neutral,
        };
        let badge = status_badge(summary, tone);
        body = body.child(if let Some(ref w) = health.warning {
            warn_row("Peak luminance", Some(w.clone()), badge)
        } else {
            menu_row("Peak luminance", None, None, badge)
        });
    }

    let hdr_games = s.settings.hdr_for_games;
    body = body.child(menu_row(
        "Auto-enable HDR for HDR games",
        None,
        None,
        menu_switch(
            "set-hdr-games".into(),
            "Auto-enable HDR for HDR games",
            hdr_games,
            false,
        )
        .on_press(move |s: &mut AppState| {
            s.set_bool_setting("hdrForGames", !hdr_games);
        }),
    ));

    if hdr.internal {
        if hdr.edid_peak_nits.is_none() {
            body = body.child(menu_row(
                "Laptop HDR calibration",
                Some(
                    "Panel EDID peak is unavailable, so neutral-pair calibration is skipped."
                        .into(),
                ),
                None,
                status_badge("Skipped".into(), BadgeTone::Neutral),
            ));
        } else {
            let ui = &s.pc.hdr_ui;
            let cal = hdr.calibration.as_ref();
            let detail_text = if let Some(ref res) = ui.cal_result
                && let Some(ref steps) = res.manual_steps
                && !steps.is_empty()
            {
                let mut lines = vec![res.message.clone()];
                for (i, step) in steps.iter().enumerate() {
                    lines.push(format!("{}. {step}", i + 1));
                }
                lines.join("\n")
            } else if let Some(c) = cal {
                format!(
                    "Brightness {}% · HDR content brightness {} nits · reports {} nits (EDID {} nits)",
                    c.global_brightness,
                    c.sdr_white_nits.round() as i32,
                    c.measured_peak_nits,
                    c.edid_peak_nits
                )
            } else if let Some(ref res) = ui.cal_result
                && res.ok
            {
                res.message.clone()
            } else {
                "Finds the brightness pair where games see this panel’s true EDID peak on launch and restores yours on exit.".into()
            };

            let right = if ui.calibrating {
                status_badge(
                    calibration_progress_text(ui.progress.as_deref()),
                    BadgeTone::Neutral,
                )
            } else if let Some(c) = cal {
                let next_en = !c.enabled;
                row()
                    .gap(6.0)
                    .center_y()
                    .child(
                        menu_switch(
                            "hdr-laptop-cal-toggle".into(),
                            "Apply calibrated laptop HDR brightness pair on game launch",
                            c.enabled,
                            false,
                        )
                        .on_press(move |s: &mut AppState| {
                            s.run_hdr_calibrate("toggle", Some(next_en));
                        }),
                    )
                    .child(
                        menu_btn(
                            "hdr-laptop-recal".into(),
                            "Recalibrate",
                            false,
                            false,
                            false,
                        )
                        .on_press(|s: &mut AppState| s.run_hdr_calibrate("run", None)),
                    )
                    .child(
                        menu_btn("hdr-laptop-undo".into(), "Undo", false, false, false)
                            .on_press(|s: &mut AppState| s.run_hdr_calibrate("undo", None)),
                    )
            } else {
                let is_mismatch = hdr
                    .peak_health
                    .as_ref()
                    .is_some_and(|h| h.status == "mismatch");
                menu_btn(
                    "hdr-laptop-cal-run".into(),
                    "Calibrate",
                    is_mismatch,
                    false,
                    false,
                )
                .on_press(|s: &mut AppState| s.run_hdr_calibrate("run", None))
            };

            body = body.child(menu_row(
                "Laptop HDR calibration",
                Some(detail_text),
                ui.cal_error.clone(),
                right,
            ));
        }
    }

    body = body.child(menu_row(
        "HDR calibration",
        None,
        None,
        if hdr.calibrated {
            status_badge("Calibrated".into(), BadgeTone::Good)
        } else {
            menu_btn("disp-hdr-cal".into(), "Calibrate", false, false, false).on_press(
                |s: &mut AppState| {
                    s.open_external_url("ms-windows-store://pdp/?productid=9N7F2SM5D1LR");
                },
            )
        },
    ));

    if let Some(ref bw) = hdr.brightness_warning
        && !bw.is_empty()
    {
        body = body.child(warn_row(
            "Brightness changed",
            Some(bw.clone()),
            status_badge("Notice".into(), BadgeTone::Warn),
        ));
    }

    let color_mode_txt = hdr.color_mode.as_deref().map(color_mode_label);
    let auto_hdr_detail = match hdr.auto_hdr_global {
        Some(true) => Some("Windows Auto HDR is on globally (read-only).".to_string()),
        Some(false) => Some("Windows Auto HDR is off globally (read-only).".to_string()),
        None => None,
    };
    if color_mode_txt.is_some() || auto_hdr_detail.is_some() {
        body = body.child(menu_row(
            "Color mode",
            auto_hdr_detail,
            None,
            status_badge(
                color_mode_txt.unwrap_or("Unavailable").into(),
                if hdr.color_mode.as_deref() == Some("hdr") {
                    BadgeTone::Good
                } else {
                    BadgeTone::Neutral
                },
            ),
        ));
    }

    if let Some(ref checks) = hdr.laptop_checks {
        for item in &checks.items {
            let status_lbl = laptop_check_status_label(&item.status, item.warn);
            let tone = if item.status == "off" {
                BadgeTone::Good
            } else if item.warn {
                BadgeTone::Warn
            } else {
                BadgeTone::Neutral
            };
            let badge = status_badge(status_lbl.into(), tone);
            body = body.child(if item.warn {
                warn_row(item.label.clone(), Some(item.detail.clone()), badge)
            } else {
                menu_row(item.label.clone(), Some(item.detail.clone()), None, badge)
            });
        }
    }

    Some(body)
}

pub fn demo_hdr_internal_state() -> HdrState {
    HdrState {
        supported: true,
        on: true,
        name: Some("Built-in OLED 240Hz".into()),
        device_path: Some("DISPLAY#INTERNAL#1".into()),
        peak_nits: Some(600),
        edid_peak_nits: Some(600),
        dxgi_peak_nits: Some(420),
        sdr_white_nits: Some(200.0),
        internal: true,
        color_mode: Some("hdr".into()),
        auto_hdr_global: Some(false),
        calibrated: false,
        verdict: Some("great".into()),
        peak_health: Some(HdrPeakHealth {
            status: "mismatch".into(),
            edid_nits: Some(600),
            dxgi_nits: Some(420),
            gap_percent: Some(30),
            warning: Some(
                "Windows reports 420 nits instead of this panel’s 600-nit EDID peak. Run calibration to find the neutral brightness pair.".into(),
            ),
        }),
        calibration: None,
        laptop_checks: Some(HdrLaptopChecks {
            is_laptop: true,
            plugged_in: Some(true),
            hdr_on: true,
            items: vec![
                HdrLaptopCheckItem {
                    id: "cabc".into(),
                    label: "Content adaptive brightness".into(),
                    status: "active".into(),
                    warn: true,
                    detail: "Windows CABC dims HDR highlights on laptops; set to Off in Settings > Display.".into(),
                },
                HdrLaptopCheckItem {
                    id: "varibright".into(),
                    label: "AMD Vari-Bright".into(),
                    status: "off".into(),
                    warn: false,
                    detail: "Off on AC power.".into(),
                },
            ],
        }),
        brightness_warning: Some(
            "Brightness moved while HDR was active; games may clip above 420 nits until restored."
                .into(),
        ),
        displays: vec![HdrDisplayInfo {
            name: Some("Built-in OLED 240Hz".into()),
            gdi_name: "\\\\.\\DISPLAY1".into(),
            device_path: "DISPLAY#INTERNAL#1".into(),
            main: true,
            supported: true,
            on: true,
            internal: true,
            connector: "edp".into(),
            color_mode: Some("hdr".into()),
            edid_peak_nits: Some(600),
            dxgi_peak_nits: Some(420),
            sdr_white_nits: Some(200.0),
            peak_health: None,
        }],
    }
}

pub fn demo_hdr_external_state() -> HdrState {
    HdrState {
        supported: true,
        on: true,
        name: Some("ASUS ROG Swift OLED PG27AQDM".into()),
        device_path: Some("DISPLAY#EXT#1".into()),
        peak_nits: Some(1000),
        edid_peak_nits: Some(1000),
        dxgi_peak_nits: Some(1000),
        sdr_white_nits: Some(120.0),
        internal: false,
        color_mode: Some("hdr".into()),
        auto_hdr_global: Some(true),
        calibrated: true,
        verdict: Some("great".into()),
        peak_health: Some(HdrPeakHealth {
            status: "ok".into(),
            edid_nits: Some(1000),
            dxgi_nits: Some(1000),
            gap_percent: Some(0),
            warning: None,
        }),
        calibration: None,
        laptop_checks: Some(HdrLaptopChecks {
            is_laptop: false,
            plugged_in: Some(true),
            hdr_on: true,
            items: Vec::new(),
        }),
        brightness_warning: None,
        displays: Vec::new(),
    }
}

pub fn demo_hdr_game_cursor_fix() -> HdrGame {
    HdrGame {
        ok: true,
        message: None,
        hdr: Some("true".into()),
        reason: Some("Native HDR".into()),
        mode: Some("auto".into()),
        cursor_fix: false,
        cursor_fix_eligible: true,
        sdr_white_nits: Some(120.0),
        auto_hdr: Some(HdrAutoHdr {
            global: Some(true),
            game: Some(true),
            effective: "on".into(),
        }),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_rpc::HdrWarningEvent;

    #[test]
    fn peak_health_and_progress_formatting_match_react() {
        assert_eq!(
            format_peak_summary(Some(600), Some(420)).as_deref(),
            Some("600 nits EDID · 420 nits reported")
        );
        assert_eq!(
            format_peak_summary(Some(1000), None).as_deref(),
            Some("1000 nits EDID")
        );
        assert_eq!(format_peak_summary(None, None), None);
        assert_eq!(
            calibration_progress_text(Some(&HdrCalibrationProgress {
                device_path: "DISPLAY#1".into(),
                step: 4,
                total: 10,
                slider: 70,
                measured_nits: Some(590),
                target_nits: 600,
            })),
            "Step 4 of 10 · 590 nits"
        );
        assert_eq!(calibration_progress_text(None), "Calibrating…");
    }

    #[test]
    fn game_hdr_word_and_cursor_fix_gating_match_react() {
        let ext = demo_hdr_external_state();
        let internal = demo_hdr_internal_state();
        let mut game = demo_hdr_game_cursor_fix();
        assert!(show_cursor_fix(&ext, &game));
        assert!(!show_cursor_fix(&internal, &game));
        assert_eq!(
            cursor_fix_word(&game),
            "SDR white is 120 nits (dim hardware cursor in HDR)"
        );
        game.cursor_fix = true;
        assert_eq!(
            cursor_fix_word(&game),
            "Raises SDR white to 220 nits while playing and restores on exit"
        );
        assert_eq!(
            build_game_hdr_word(&game, true, None),
            Some(("Native HDR · Windows Auto HDR: on".into(), false))
        );
        assert_eq!(
            build_game_hdr_word(&game, true, Some("Could not save. Try again.")),
            Some(("Could not save. Try again.".into(), true))
        );
    }

    #[test]
    fn register_events_handles_calibration_progress_and_warning() {
        let mut s = AppState::with_demo_games();
        s.pc.hdr = Some(demo_hdr_internal_state());
        assert!(
            s.apply_host_event(HostEvent::HdrCalibrationProgress(HdrCalibrationProgress {
                device_path: "DISPLAY#INTERNAL#1".into(),
                step: 2,
                total: 8,
                slider: 50,
                measured_nits: Some(540),
                target_nits: 600,
            }))
        );
        assert!(s.pc.hdr_ui.calibrating);
        assert_eq!(s.pc.hdr_ui.progress.as_ref().map(|p| p.step), Some(2));

        assert!(s.apply_host_event(HostEvent::HdrWarning(HdrWarningEvent {
            device_path: "DISPLAY#INTERNAL#1".into(),
            kind: "brightness".into(),
            message: "Slider moved externally".into(),
        })));
        assert_eq!(
            s.pc.hdr
                .as_ref()
                .and_then(|h| h.brightness_warning.as_deref()),
            Some("Slider moved externally")
        );
    }

    #[test]
    fn hdr_display_and_game_cursor_fix_gridlines_and_motion_gate() {
        use crate::pc_state::PcPartMenu;
        use crate::shell;
        use crate::state::Room;
        use rune::motion_gate::Run;
        use rune::{Env, Runtime};

        let mut internal_state = AppState::with_demo_games();
        internal_state.switch_room(Room::ExoAi);
        internal_state.pc.hdr = Some(demo_hdr_internal_state());
        internal_state.pc.part_menu = Some(PcPartMenu::Display);
        let mut rt = Runtime::new(
            internal_state,
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt.frame(0.0);
        rt.settle_all();
        rt.frame(1.0 / 60.0);
        let (panel, _) = rt.presented("pc-part-menu").expect("pc-part-menu");
        let (hdr_rows, _) = rt.presented("pc-hdr-rows").expect("pc-hdr-rows");
        assert!(hdr_rows[0] + hdr_rows[2] <= panel[0] + panel[2] + 1.0);
        assert!(rt.presented("hdr-internal-badge").is_some());
        assert!(rt.presented("hdr-laptop-cal-run").is_some());

        let mut ext_state = AppState::with_demo_games();
        ext_state.switch_room(Room::ExoAi);
        ext_state.pc.hdr = Some(demo_hdr_external_state());
        ext_state.pc.part_menu = Some(PcPartMenu::Display);
        let mut rt_ext = Runtime::new(
            ext_state,
            shell::view,
            (1920.0, 1080.0),
            Env::new(true, 1.0),
        );
        rt_ext.frame(0.0);
        rt_ext.settle_all();
        rt_ext.frame(1.0 / 60.0);
        assert!(rt_ext.presented("hdr-internal-badge").is_none());

        for reduce_motion in [false, true] {
            let mut state = AppState::with_demo_games();
            state.settings.reduce_motion = reduce_motion;
            state.pc.hdr = Some(demo_hdr_external_state());
            state.open_game_page("demo:cyberpunk");
            state.game_page.hdr_game = Some(demo_hdr_game_cursor_fix());
            let mut rt_gp = Runtime::new(state, shell::view, (1920.0, 1080.0), Env::new(true, 1.0));
            rt_gp.set_system_reduced_motion(reduce_motion);
            let mut run = Run::new(rt_gp, 120.0);
            run.idle(0.4);
            run.step();
            run.act("toggle match HDR cursor brightness", |rt| {
                Run::tap(rt, "gp-hdr-cursor-fix")
            });
            run.play(0.35);
            let findings: Vec<_> = run
                .finish()
                .into_iter()
                .filter(|f| !(reduce_motion && matches!(f.kind, "jump" | "text-swap")))
                .collect();
            assert!(
                findings.is_empty(),
                "hdr cursor fix motion_gate findings (reduce_motion={reduce_motion}): {findings:?}"
            );
        }
    }
}
