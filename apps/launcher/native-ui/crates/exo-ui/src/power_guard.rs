use std::process::Command;

use exo_kit::{
    BG, BadgeTone, FG, RADIUS_CARD, RADIUS_PANEL, bad, fg_muted, float_line, status_badge,
    subtle_fill, track_fill, type_control, type_section, type_small, warn,
};
use exo_power_guard::backends::nvidia::try_set_nvidia_power_limit_w;
use exo_power_guard::guard::{GuardAction, GuardPolicy, Severity};
use exo_power_guard::monitor::{LiveGuardState, PowerGuardController};
use exo_power_guard::types::{
    BackendKind, GpuSnapshot, THROTTLE_CURRENT_LIMIT, THROTTLE_HW_POWER_BRAKE, THROTTLE_HW_THERMAL,
    THROTTLE_SW_POWER_CAP, THROTTLE_SW_THERMAL,
};
use rune::{El, col, el, row, text};

use crate::notice::RichNoticeSpec;
use crate::pc_menus::{menu_btn, menu_row, menu_switch};
use crate::state::{AppState, NoticeTone, Room};

pub struct PowerGuardUiState {
    pub live: LiveGuardState,
    pub export_note: Option<String>,
    pub last_poll_at: f64,
    pub dev_mock_actions: bool,
    pub mock_lowered_limits_w: Vec<f64>,
    pub mock_game_closes: usize,
    pub mock_pc_shutdowns: usize,
    controller: PowerGuardController,
}

impl Default for PowerGuardUiState {
    fn default() -> Self {
        Self {
            live: LiveGuardState::default(),
            export_note: None,
            last_poll_at: -10.0,
            dev_mock_actions: false,
            mock_lowered_limits_w: Vec::new(),
            mock_game_closes: 0,
            mock_pc_shutdowns: 0,
            controller: PowerGuardController::new(GuardPolicy::default()),
        }
    }
}

impl PowerGuardUiState {
    pub fn should_poll(room: Room, any_game_running: bool) -> bool {
        room == Room::ExoAi || any_game_running
    }

    #[cfg(test)]
    pub fn is_sidecar_running(&self) -> bool {
        self.controller.is_running()
    }
}

impl AppState {
    pub fn tick_power_guard(&mut self, now: f64) -> bool {
        let any_game_running = self.games.iter().any(|g| g.is_running);
        let active = PowerGuardUiState::should_poll(self.room, any_game_running);

        if !active {
            if self.pc.power_guard_ui.controller.is_running() {
                self.pc.power_guard_ui.controller.stop();
                self.pc.power_guard_ui.live.sidecar_running = false;
                return true;
            }
            return false;
        }

        if now - self.pc.power_guard_ui.last_poll_at < 0.9 {
            return false;
        }
        self.pc.power_guard_ui.last_poll_at = now;

        let started = self.pc.power_guard_ui.controller.ensure_started(1_000);
        self.pc.power_guard_ui.live.sidecar_running = started;

        let maybe_eval = self
            .pc
            .power_guard_ui
            .controller
            .poll_into(&mut self.pc.power_guard_ui.live);

        if let Some(eval) = maybe_eval {
            self.handle_power_guard_action(&eval.action, eval.severity, &eval.alerts);
            return true;
        }
        false
    }

    #[cfg(test)]
    pub fn ingest_power_guard_snapshot_for_test(&mut self, snap: GpuSnapshot) {
        let eval = self
            .pc
            .power_guard_ui
            .controller
            .ingest_snapshot(snap, &mut self.pc.power_guard_ui.live);
        self.handle_power_guard_action(&eval.action, eval.severity, &eval.alerts);
    }

    fn handle_power_guard_action(
        &mut self,
        action: &GuardAction,
        severity: Severity,
        alerts: &[exo_power_guard::guard::GuardAlert],
    ) {
        let first_msg = alerts
            .first()
            .map(|a| a.message.clone())
            .unwrap_or_else(|| "Sustained GPU power or thermal fault".into());

        match action {
            GuardAction::AlertSustained => {
                let tone = if severity == Severity::Critical {
                    NoticeTone::Bad
                } else {
                    NoticeTone::Neutral
                };
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some("power-guard-fault".into()),
                    title: Some("Power guard".into()),
                    where_label: Some("PC".into()),
                    text: first_msg,
                    tone,
                    sticky: Some(severity == Severity::Critical),
                    ..Default::default()
                });
            }
            GuardAction::LowerPowerLimit { target_w, .. } => {
                if !self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit {
                    return;
                }
                let msg: String = if self.pc.power_guard_ui.dev_mock_actions {
                    self.pc.power_guard_ui.mock_lowered_limits_w.push(*target_w);
                    format!("Mock lowered GPU power limit to {target_w:.0} W")
                } else {
                    match try_set_nvidia_power_limit_w(*target_w) {
                        Ok(applied) => format!("Lowered GPU power limit to {applied:.0} W"),
                        Err(err) => format!("Could not lower GPU power limit: {err}"),
                    }
                };
                self.pc.power_guard_ui.live.last_action_note = Some(msg.clone());
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some("power-guard-stage1".into()),
                    title: Some("Power guard · Stage 1".into()),
                    where_label: Some("PC".into()),
                    text: msg,
                    tone: NoticeTone::Bad,
                    sticky: Some(true),
                    ..Default::default()
                });
            }
            GuardAction::CloseGame => {
                if !self.pc.power_guard_ui.live.policy.opt_in_close_game {
                    return;
                }
                let running_id = self
                    .games
                    .iter()
                    .find(|g| g.is_running)
                    .map(|g| (g.id.clone(), g.title.clone()));
                let msg: String = if self.pc.power_guard_ui.dev_mock_actions {
                    self.pc.power_guard_ui.mock_game_closes += 1;
                    match running_id {
                        Some((_, name)) => format!("Mock closed {name} after sustained fault"),
                        None => "Mock game close triggered (no running game)".into(),
                    }
                } else if let Some((gid, name)) = running_id {
                    self.stop_game_held(&gid);
                    format!("Closing {name} after sustained GPU fault")
                } else {
                    "No running game to close".into()
                };
                self.pc.power_guard_ui.live.last_action_note = Some(msg.clone());
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some("power-guard-stage2".into()),
                    title: Some("Power guard · Stage 2".into()),
                    where_label: Some("PC".into()),
                    text: msg,
                    tone: NoticeTone::Bad,
                    sticky: Some(true),
                    ..Default::default()
                });
            }
            GuardAction::ShutdownPc => {
                if !self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc {
                    return;
                }
                let msg: String = if self.pc.power_guard_ui.dev_mock_actions {
                    self.pc.power_guard_ui.mock_pc_shutdowns += 1;
                    "Mock graceful Windows shutdown triggered (15 s)".into()
                } else {
                    let ok = Command::new("shutdown.exe")
                        .args([
                            "/s",
                            "/t",
                            "15",
                            "/d",
                            "p:0:0",
                            "/c",
                            "Exo Power Guard: sustained critical GPU power or connector fault",
                        ])
                        .status()
                        .is_ok_and(|s| s.success());
                    if ok {
                        "Shutting down PC in 15 s (run shutdown /a to cancel)".into()
                    } else {
                        "Windows rejected shutdown request".into()
                    }
                };
                self.pc.power_guard_ui.live.last_action_note = Some(msg.clone());
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some("power-guard-stage3".into()),
                    title: Some("Power guard · Stage 3".into()),
                    where_label: Some("PC".into()),
                    text: msg,
                    tone: NoticeTone::Bad,
                    sticky: Some(true),
                    ..Default::default()
                });
            }
            GuardAction::None | GuardAction::Countdown { .. } => {}
        }
    }

    pub fn toggle_power_guard_alerts(&mut self) {
        self.pc.power_guard_ui.live.policy.alerts_enabled =
            !self.pc.power_guard_ui.live.policy.alerts_enabled;
        let pol = self.pc.power_guard_ui.live.policy.clone();
        self.pc.power_guard_ui.controller.set_policy(pol);
    }

    pub fn toggle_power_guard_lower_power(&mut self) {
        self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit =
            !self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit;
        let pol = self.pc.power_guard_ui.live.policy.clone();
        self.pc.power_guard_ui.controller.set_policy(pol);
    }

    pub fn toggle_power_guard_close_game(&mut self) {
        self.pc.power_guard_ui.live.policy.opt_in_close_game =
            !self.pc.power_guard_ui.live.policy.opt_in_close_game;
        let pol = self.pc.power_guard_ui.live.policy.clone();
        self.pc.power_guard_ui.controller.set_policy(pol);
    }

    pub fn toggle_power_guard_shutdown(&mut self) {
        self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc =
            !self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc;
        let pol = self.pc.power_guard_ui.live.policy.clone();
        self.pc.power_guard_ui.controller.set_policy(pol);
    }

    pub fn export_power_guard_html(&mut self) {
        if self.pc.power_guard_ui.live.sample_count == 0 {
            self.pc.power_guard_ui.export_note = Some("No session samples recorded yet".into());
            return;
        }
        let html = self.pc.power_guard_ui.controller.export_html();
        let out_path = std::env::temp_dir().join("exo-power-guard-session.html");
        match std::fs::write(&out_path, html) {
            Ok(()) => {
                self.pc.power_guard_ui.export_note = Some(format!("Saved {}", out_path.display()));
            }
            Err(err) => {
                self.pc.power_guard_ui.export_note = Some(format!("Export failed: {err}"));
            }
        }
    }
}

fn backend_label(kind: BackendKind) -> &'static str {
    match kind {
        BackendKind::NvidiaNvmlNvapi => "NVML + NvAPI",
        BackendKind::AmdAdlx => "AMD ADLX",
        BackendKind::IntelIgcl => "Intel IGCL",
        BackendKind::WindowsD3dkmt => "Windows D3DKMT",
    }
}

fn format_headline_metrics(snap: &GpuSnapshot) -> String {
    let mut parts = Vec::new();
    match (
        snap.board_power_w,
        snap.enforced_power_limit_w.or(snap.power_limit_w),
    ) {
        (Some(w), Some(lim)) => parts.push(format!("{w:.0} / {lim:.0} W")),
        (Some(w), None) => parts.push(format!("{w:.0} W")),
        (None, _) => {
            if let Some(pct) = snap.power_pct {
                parts.push(format!("{pct:.0}% TDP"));
            }
        }
    }
    if let Some(c) = snap.core_temp_c {
        parts.push(format!("{c:.0} °C"));
    }
    if let Some(hs) = snap.hotspot_temp_c {
        parts.push(format!("hotspot {hs:.0} °C"));
    }
    if let Some(rpm) = snap.fan_rpm {
        parts.push(format!("{rpm} RPM"));
    }
    parts.join(" · ")
}

fn format_throttle_badge(flags: u32) -> (String, BadgeTone) {
    if flags & THROTTLE_HW_POWER_BRAKE != 0 {
        return ("Power brake".into(), BadgeTone::Bad);
    }
    if flags & THROTTLE_HW_THERMAL != 0 {
        return ("Thermal limit".into(), BadgeTone::Bad);
    }
    if flags & THROTTLE_CURRENT_LIMIT != 0 {
        return ("Current limit".into(), BadgeTone::Warn);
    }
    if flags & THROTTLE_SW_THERMAL != 0 {
        return ("Warm".into(), BadgeTone::Warn);
    }
    if flags & THROTTLE_SW_POWER_CAP != 0 {
        return ("Power cap".into(), BadgeTone::Neutral);
    }
    ("None".into(), BadgeTone::Good)
}

fn session_graph_view(s: &AppState) -> El {
    let pg = &s.pc.power_guard_ui;
    let hist = &pg.live.history;
    let max_limit = pg
        .live
        .snapshot
        .as_ref()
        .and_then(|sn| sn.enforced_power_limit_w.or(sn.power_limit_w))
        .unwrap_or(250.0);
    let max_seen = hist
        .iter()
        .map(|p| {
            if p.power_w > 0.0 {
                p.power_w
            } else {
                p.power_pct
            }
        })
        .fold(0.0_f64, f64::max);
    let scale_max = max_seen.max(max_limit).max(100.0);

    let use_pct = pg
        .live
        .snapshot
        .as_ref()
        .is_some_and(|sn| sn.board_power_w.is_none() && sn.power_pct.is_some());

    let mut bars = row()
        .key("pg-session-graph")
        .w_full()
        .h(42.0)
        .px(8.0)
        .py(5.0)
        .gap(2.0)
        .items_end()
        .radius(RADIUS_CARD)
        .continuous_corners()
        .fill(subtle_fill())
        .border(1.0, float_line());

    let slots = 48usize;
    let empty_slots = slots.saturating_sub(hist.len());
    for i in 0..empty_slots {
        bars = bars.child(
            el().key(format!("pg-bar-empty-{i}"))
                .grow(1.0)
                .h(2.0)
                .fill(track_fill()),
        );
    }
    for (idx, pt) in hist.iter().enumerate() {
        let val = if use_pct { pt.power_pct } else { pt.power_w };
        let norm = (val / scale_max).clamp(0.06, 1.0);
        let h_px = (norm * 32.0).round() as f32;
        let fill = if pt.max_pin_a.is_some_and(|a| a >= 9.2)
            || pt.core_temp_c >= 90.0
            || pt.hotspot_temp_c.is_some_and(|h| h >= 108.0)
        {
            bad()
        } else if pt.max_pin_a.is_some_and(|a| a >= 8.5)
            || pt.core_temp_c >= 83.0
            || pt.hotspot_temp_c.is_some_and(|h| h >= 100.0)
        {
            warn()
        } else {
            FG.alpha(0.72)
        };
        bars = bars.child(
            el().key(format!("pg-bar-{idx}"))
                .grow(1.0)
                .h(h_px.max(2.0))
                .radius(1.5)
                .fill(fill),
        );
    }

    let caption = if let Some(last) = hist.back() {
        let min_v = hist
            .iter()
            .map(|p| if use_pct { p.power_pct } else { p.power_w })
            .fold(f64::INFINITY, f64::min);
        let max_v = hist
            .iter()
            .map(|p| if use_pct { p.power_pct } else { p.power_w })
            .fold(0.0_f64, f64::max);
        let unit = if use_pct { "% TDP" } else { "W" };
        format!(
            "Session ({} s) · min {min_v:.0} · now {:.0} · max {max_v:.0} {unit} · {:.0} °C",
            hist.len(),
            if use_pct {
                last.power_pct
            } else {
                last.power_w
            },
            last.core_temp_c
        )
    } else {
        "Session graph · polls at 1 Hz only while PC is open or a game runs".into()
    };

    col()
        .w_full()
        .gap(5.0)
        .child(bars)
        .child(text(caption).font(type_small()).color(fg_muted()))
}

pub(crate) fn card_view(s: &AppState) -> El {
    let pg = &s.pc.power_guard_ui;
    let pol = &pg.live.policy;

    let (status_word, status_tone) = match pg.live.evaluation.as_ref().map(|e| e.severity) {
        Some(Severity::Normal) => ("Healthy", BadgeTone::Good),
        Some(Severity::Warning) => ("Warning", BadgeTone::Warn),
        Some(Severity::Critical) => ("Critical", BadgeTone::Bad),
        None => (
            if pg.live.sidecar_running {
                "Reading"
            } else {
                "Standby"
            },
            BadgeTone::Neutral,
        ),
    };

    let headline_metrics = pg
        .live
        .snapshot
        .as_ref()
        .map(format_headline_metrics)
        .unwrap_or_else(|| "0% CPU while hidden".into());

    let header = row()
        .w_full()
        .justify_between()
        .center_y()
        .gap(10.0)
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    text("Power guard")
                        .key("exo-ai-power-guard-title")
                        .font(type_section())
                        .color(FG),
                )
                .child(status_badge(status_word.into(), status_tone))
                .child(text(headline_metrics).font(type_small()).color(fg_muted())),
        )
        .child(
            row()
                .gap(8.0)
                .center_y()
                .child(
                    text(
                        pg.export_note
                            .clone()
                            .unwrap_or_else(|| format!("{} samples", pg.live.sample_count)),
                    )
                    .font(type_small())
                    .color(fg_muted()),
                )
                .child(
                    menu_btn(
                        "pg-card-export-html".into(),
                        "Export HTML",
                        false,
                        false,
                        pg.live.sample_count == 0,
                    )
                    .on_press(|s: &mut AppState| s.export_power_guard_html()),
                ),
        );

    let mut left_info = col()
        .grow(1.25)
        .min_w(0.0)
        .gap(6.0)
        .child(session_graph_view(s));

    if let Some(ref snap) = pg.live.snapshot {
        let caps = snap.capabilities();
        let backend_txt = format!("{} · {}", snap.name, backend_label(snap.backend));
        let (throttle_word, throttle_tone) = format_throttle_badge(snap.throttle_flags);

        left_info = left_info.child(
            row()
                .w_full()
                .justify_between()
                .center_y()
                .gap(8.0)
                .child(
                    text(format!("{backend_txt} · {}", caps.summary))
                        .font(type_small())
                        .color(FG)
                        .truncate(),
                )
                .child(status_badge(throttle_word, throttle_tone)),
        );

        if let Some(ref conn) = snap.connector {
            let max_a = conn.max_pin_current_a();
            let spread = conn.imbalance_spread_a();
            let temp_txt = conn
                .max_connector_temp_c()
                .map(|t| format!(" · {t:.1} °C"))
                .unwrap_or_default();
            let pins_line: String = conn
                .pins
                .iter()
                .map(|p| format!("P{}:{:.1}A", p.pin, p.current_a))
                .collect::<Vec<_>>()
                .join("  ");
            left_info = left_info.child(
                text(format!(
                    "12V-2x6 ({}) · max {max_a:.2} A · spread {spread:.2} A{temp_txt} · {pins_line}",
                    conn.source.as_str()
                ))
                .font(type_small())
                .color(fg_muted())
                .wrap(),
            );
        } else if let Some(note) = caps.limitation_note {
            left_info = left_info.child(text(note).font(type_small()).color(fg_muted()).wrap());
        }
    } else {
        left_info = left_info.child(
            text("Board power, temps, and HW throttle flags across NVML/NvAPI, ADLX, IGCL, and D3DKMT. Per-pin 12V-2x6 current requires shunt hardware (Astral, WireView Pro II, or MSI PSU).")
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        );
    }

    if let Some(ref ev) = pg.live.evaluation
        && let Some(first) = ev.alerts.first()
    {
        let alert_color = if ev.severity == Severity::Critical {
            bad()
        } else {
            warn()
        };
        let extra = match ev.action {
            GuardAction::Countdown {
                next_stage,
                remaining_ms,
            } => format!(
                " · {} in {:.0} s",
                next_stage.label(),
                (remaining_ms as f64 / 1000.0).ceil()
            ),
            _ => String::new(),
        };
        left_info = left_info.child(
            text(format!("{}{extra}", first.message))
                .font(type_small())
                .color(alert_color)
                .wrap(),
        );
    }

    if let Some(ref note) = pg.live.last_action_note {
        left_info = left_info.child(text(note.clone()).font(type_small()).color(FG).wrap());
    }

    let opt_in_btn = |key: &str, label: &str, active: bool, danger: bool| {
        let txt = format!("{label}: {}", if active { "On" } else { "Off" });
        menu_btn(key.into(), txt, active && !danger, active && danger, false)
    };

    let right_controls = col()
        .grow(1.0)
        .min_w(0.0)
        .gap(6.0)
        .child(
            text("Sustained fault actions (opt-in, off by default)")
                .font(type_control())
                .color(FG),
        )
        .child(
            row()
                .w_full()
                .gap(6.0)
                .child(
                    opt_in_btn("pg-card-alerts", "Alerts", pol.alerts_enabled, false)
                        .on_press(|s: &mut AppState| s.toggle_power_guard_alerts()),
                )
                .child(
                    opt_in_btn(
                        "pg-card-stage1",
                        "1. Power limit",
                        pol.opt_in_lower_power_limit,
                        false,
                    )
                    .on_press(|s: &mut AppState| s.toggle_power_guard_lower_power()),
                ),
        )
        .child(
            row()
                .w_full()
                .gap(6.0)
                .child(
                    opt_in_btn(
                        "pg-card-stage2",
                        "2. Close game",
                        pol.opt_in_close_game,
                        false,
                    )
                    .on_press(|s: &mut AppState| s.toggle_power_guard_close_game()),
                )
                .child(
                    opt_in_btn(
                        "pg-card-stage3",
                        "3. Shut down",
                        pol.opt_in_shutdown_pc,
                        true,
                    )
                    .on_press(|s: &mut AppState| s.toggle_power_guard_shutdown()),
                ),
        );

    col()
        .key("exo-ai-power-guard-card")
        .w_full()
        .pt(14.0)
        .px(24.0)
        .pb(14.0)
        .gap(10.0)
        .radius(RADIUS_PANEL)
        .continuous_corners()
        .fill(BG)
        .border(1.0, float_line())
        .child(header)
        .child(
            row()
                .w_full()
                .items_start()
                .gap(20.0)
                .child(left_info)
                .child(right_controls),
        )
}

pub(crate) fn view(s: &AppState) -> El {
    let pg = &s.pc.power_guard_ui;
    let pol = &pg.live.policy;
    let (status_word, status_tone) = match pg.live.evaluation.as_ref().map(|e| e.severity) {
        Some(Severity::Normal) => ("Healthy", BadgeTone::Good),
        Some(Severity::Warning) => ("Warning", BadgeTone::Warn),
        Some(Severity::Critical) => ("Critical", BadgeTone::Bad),
        None => ("Standby", BadgeTone::Neutral),
    };
    let detail = pg
        .live
        .snapshot
        .as_ref()
        .map(format_headline_metrics)
        .unwrap_or_else(|| "Polls at 1 Hz while PC is open or a game runs".into());

    col()
        .w_full()
        .child(menu_row(
            "GPU power & thermal guard",
            Some(detail),
            pg.live
                .snapshot
                .as_ref()
                .and_then(|sn| sn.capabilities().limitation_note),
            status_badge(status_word.into(), status_tone),
        ))
        .child(menu_row(
            "Alerts on sustained fault",
            Some("Default on".into()),
            None,
            menu_switch(
                "pg-menu-alerts".into(),
                "Alerts on sustained fault",
                pol.alerts_enabled,
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_power_guard_alerts()),
        ))
        .child(menu_row(
            "1. Lower GPU power limit",
            Some("Opt-in · first protective step".into()),
            None,
            menu_switch(
                "pg-menu-stage1".into(),
                "Lower GPU power limit",
                pol.opt_in_lower_power_limit,
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_power_guard_lower_power()),
        ))
        .child(menu_row(
            "2. Close running game",
            Some("Opt-in · second protective step".into()),
            None,
            menu_switch(
                "pg-menu-stage2".into(),
                "Close running game",
                pol.opt_in_close_game,
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_power_guard_close_game()),
        ))
        .child(menu_row(
            "3. Shut down PC",
            Some("Opt-in · last resort (15 s countdown)".into()),
            None,
            menu_switch(
                "pg-menu-stage3".into(),
                "Shut down PC",
                pol.opt_in_shutdown_pc,
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_power_guard_shutdown()),
        ))
}

#[cfg(test)]
mod tests {
    use super::*;
    use exo_power_guard::per_pin::wireview::{
        decode_wireview_frame, encode_wireview_frame_for_test,
    };
    use exo_power_guard::types::GpuVendor;

    fn make_snap(ts: u64, vendor: GpuVendor, backend: BackendKind, name: &str) -> GpuSnapshot {
        GpuSnapshot {
            timestamp_ms: ts,
            backend,
            vendor,
            name: name.into(),
            pci_bus: Some(7),
            core_temp_c: Some(52.0),
            hotspot_temp_c: None,
            memory_temp_c: None,
            board_power_w: Some(89.5),
            power_limit_w: Some(240.0),
            enforced_power_limit_w: Some(240.0),
            min_power_limit_w: Some(100.0),
            max_power_limit_w: Some(270.0),
            power_pct: Some(37.3),
            gpu_clock_mhz: Some(1965),
            mem_clock_mhz: Some(6801),
            vram_used_mb: Some(2500),
            vram_total_mb: Some(8192),
            gpu_util_pct: Some(30),
            fan_pct: Some(53),
            fan_rpm: Some(1002),
            pstate: Some(2),
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
    fn zero_cost_while_hidden_gate_checks_room_and_running_games() {
        assert!(!PowerGuardUiState::should_poll(Room::Library, false));
        assert!(PowerGuardUiState::should_poll(Room::ExoAi, false));
        assert!(PowerGuardUiState::should_poll(Room::Library, true));

        let mut s = AppState::with_demo_games();
        s.room = Room::Library;
        for g in &mut s.games {
            g.is_running = false;
        }
        assert!(!s.tick_power_guard(10.0));
        assert!(!s.pc.power_guard_ui.is_sidecar_running());
    }

    #[test]
    fn default_opt_ins_are_off_and_dev_mock_escalates_power_game_shutdown_in_order() {
        let mut s = AppState::with_demo_games();
        s.pc.power_guard_ui.dev_mock_actions = true;

        let pol = &s.pc.power_guard_ui.live.policy;
        assert!(pol.alerts_enabled);
        assert!(!pol.opt_in_lower_power_limit);
        assert!(!pol.opt_in_close_game);
        assert!(!pol.opt_in_shutdown_pc);

        s.toggle_power_guard_lower_power();
        s.toggle_power_guard_close_game();
        s.toggle_power_guard_shutdown();
        s.pc.power_guard_ui.live.policy.alert_sustained_ms = 2_000;
        s.pc.power_guard_ui.live.policy.stage_step_ms = 5_000;
        let updated = s.pc.power_guard_ui.live.policy.clone();
        s.pc.power_guard_ui.controller.set_policy(updated);

        let make_fault = |ts: u64| {
            let mut sn = make_snap(
                ts,
                GpuVendor::Nvidia,
                BackendKind::NvidiaNvmlNvapi,
                "NVIDIA GeForce RTX 3070",
            );
            sn.throttle_flags = THROTTLE_HW_POWER_BRAKE;
            sn
        };

        s.ingest_power_guard_snapshot_for_test(make_fault(10_000));
        s.ingest_power_guard_snapshot_for_test(make_fault(12_000));
        assert!(s.pc.power_guard_ui.mock_lowered_limits_w.is_empty());

        s.ingest_power_guard_snapshot_for_test(make_fault(15_000));
        assert_eq!(s.pc.power_guard_ui.mock_lowered_limits_w, vec![168.0]);
        assert_eq!(s.pc.power_guard_ui.mock_game_closes, 0);
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 0);

        s.ingest_power_guard_snapshot_for_test(make_fault(20_000));
        assert_eq!(s.pc.power_guard_ui.mock_game_closes, 1);
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 0);

        s.ingest_power_guard_snapshot_for_test(make_fault(25_000));
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 1);
    }

    #[test]
    fn card_view_renders_live_graph_connector_pins_and_honest_shunt_disclosure() {
        let mut s = AppState::with_demo_games();
        s.pc.power_guard_ui.dev_mock_actions = true;
        for i in 0..8u64 {
            s.ingest_power_guard_snapshot_for_test(make_snap(
                10_000 + i * 1_000,
                GpuVendor::Nvidia,
                BackendKind::NvidiaNvmlNvapi,
                "NVIDIA GeForce RTX 3070",
            ));
        }
        let _ = card_view(&s);
        let _ = view(&s);

        let mut amd_wireview = make_snap(
            20_000,
            GpuVendor::Amd,
            BackendKind::AmdAdlx,
            "AMD Radeon RX 9070 XT",
        );
        amd_wireview.hotspot_temp_c = Some(112.0);
        let raw = encode_wireview_frame_for_test(
            [12000; 6],
            [10100, 9900, 1900, 2000, 6100, 6000],
            [970, 910, 420, 410],
            0,
            0x0002,
            0x0002,
        );
        amd_wireview.connector = Some(decode_wireview_frame(&raw).unwrap().connector);
        s.ingest_power_guard_snapshot_for_test(amd_wireview);
        let _ = card_view(&s);
    }
}
