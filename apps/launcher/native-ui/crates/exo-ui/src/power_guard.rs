#[cfg(test)]
use std::path::PathBuf;
use std::process::Command;

use exo_kit::{
    BG, BadgeTone, FG, RADIUS_CARD, RADIUS_PANEL, bad, fg_muted, float_line, status_badge,
    subtle_fill, track_fill, type_control, type_small, type_title, warn,
};
use exo_power_guard::backends::nvidia::try_set_nvidia_power_limit_w;
use exo_power_guard::guard::{
    GuardAction, GuardPolicy, PowerLimitJournal, PowerLimitJournalEntry, RestoreReason, Severity,
};
use exo_power_guard::monitor::{LiveGuardState, PowerGuardController};
use exo_power_guard::types::{
    BackendKind, GpuSnapshot, THROTTLE_CURRENT_LIMIT, THROTTLE_HW_POWER_BRAKE, THROTTLE_HW_THERMAL,
    THROTTLE_SW_POWER_CAP, THROTTLE_SW_THERMAL,
};
use rune::{El, col, el, row, text};

use crate::notice::{NoticeActionKind, NoticeActionSpec, RichNoticeSpec};
use crate::pc_menus::{menu_btn, menu_row, menu_switch};
use crate::state::{AppState, NoticeTone, Room};

#[cfg(windows)]
#[link(name = "user32")]
unsafe extern "system" {
    fn MessageBeep(u_type: u32) -> i32;
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PendingOptInConfirm {
    LowerPowerLimit,
    CloseGame,
    ShutdownPc,
}

impl PendingOptInConfirm {
    pub fn title(self) -> &'static str {
        match self {
            Self::LowerPowerLimit => "Enable Stage 1: Lower GPU power limit?",
            Self::CloseGame => "Enable Stage 2: Close running game?",
            Self::ShutdownPc => "Enable Stage 3: Emergency PC shutdown?",
        }
    }

    pub fn detail(self) -> &'static str {
        match self {
            Self::LowerPowerLimit => {
                "On a sustained critical GPU or connector fault, Exo lowers the GPU power limit by 30% (within hardware bounds). The original limit is journaled to disk and restored automatically when the fault clears, the game exits, Exo closes, or on next start after a crash."
            }
            Self::CloseGame => {
                "If a sustained critical GPU or connector fault persists after Stage 1, Exo stops the active game immediately to drop GPU power load. Unsaved game progress may be lost."
            }
            Self::ShutdownPc => {
                "If a sustained critical GPU or connector fault persists after Stage 2, Exo starts a 30-second cancelable shutdown countdown with a sticky toast and alert sound before shutting down Windows."
            }
        }
    }

    pub fn confirm_label(self) -> &'static str {
        match self {
            Self::LowerPowerLimit => "Enable power limit drop",
            Self::CloseGame => "Enable game close",
            Self::ShutdownPc => "Enable emergency shutdown",
        }
    }
}

pub struct PowerGuardUiState {
    pub live: LiveGuardState,
    pub export_note: Option<String>,
    pub last_poll_at: f64,
    pub last_any_game_running: bool,
    pub pending_confirm: Option<PendingOptInConfirm>,
    pub dev_mock_actions: bool,
    pub mock_lowered_limits_w: Vec<f64>,
    pub mock_restored_limits_w: Vec<f64>,
    pub mock_game_closes: usize,
    pub mock_alert_sounds: usize,
    pub mock_pc_shutdowns: usize,
    journal: PowerLimitJournal,
    journal_recovered: bool,
    controller: PowerGuardController,
}

impl Default for PowerGuardUiState {
    fn default() -> Self {
        Self {
            live: LiveGuardState::default(),
            export_note: None,
            last_poll_at: -10.0,
            last_any_game_running: false,
            pending_confirm: None,
            dev_mock_actions: false,
            mock_lowered_limits_w: Vec::new(),
            mock_restored_limits_w: Vec::new(),
            mock_game_closes: 0,
            mock_alert_sounds: 0,
            mock_pc_shutdowns: 0,
            journal: PowerLimitJournal::new(PowerLimitJournal::default_path()),
            journal_recovered: false,
            controller: PowerGuardController::new(GuardPolicy::default()),
        }
    }
}

impl PowerGuardUiState {
    pub fn should_poll(room: Room, any_game_running: bool) -> bool {
        room == Room::ExoAi || any_game_running
    }

    #[cfg(test)]
    pub fn set_journal_path(&mut self, path: PathBuf) {
        self.journal = PowerLimitJournal::new(path);
        self.journal_recovered = false;
    }

    #[cfg(test)]
    pub fn journal_entry(&self) -> Option<PowerLimitJournalEntry> {
        self.journal.read()
    }

    #[cfg(test)]
    pub fn is_sidecar_running(&self) -> bool {
        self.controller.is_running()
    }
}

impl AppState {
    pub fn recover_power_guard_journal_if_needed(&mut self) -> bool {
        if self.pc.power_guard_ui.journal_recovered {
            return false;
        }
        self.pc.power_guard_ui.journal_recovered = true;
        let Some(entry) = self.pc.power_guard_ui.journal.read() else {
            return false;
        };
        let restore_act = GuardAction::RestorePowerLimit {
            original_w: entry.original_limit_w,
            reason: RestoreReason::CrashRecovery,
        };
        self.handle_power_guard_action(&restore_act, Severity::Normal, &[]);
        true
    }

    pub fn tick_power_guard(&mut self, now: f64) -> bool {
        let mut changed = self.recover_power_guard_journal_if_needed();
        let any_game_running = self.games.iter().any(|g| g.is_running);

        if self.pc.power_guard_ui.last_any_game_running
            && !any_game_running
            && let Some(restore_act) = self.pc.power_guard_ui.controller.on_game_exited()
        {
            self.handle_power_guard_action(&restore_act, Severity::Normal, &[]);
            changed = true;
        }
        self.pc.power_guard_ui.last_any_game_running = any_game_running;

        let active = PowerGuardUiState::should_poll(self.room, any_game_running);

        if !active {
            if self.pc.power_guard_ui.controller.is_running() {
                let restore_act = self.pc.power_guard_ui.controller.stop();
                self.pc.power_guard_ui.live.sidecar_running = false;
                self.pc.power_guard_ui.live.shutdown_countdown_secs = None;
                if let Some(act) = restore_act {
                    self.handle_power_guard_action(&act, Severity::Normal, &[]);
                }
                return true;
            }
            return changed;
        }

        if now - self.pc.power_guard_ui.last_poll_at < 0.9 {
            return changed;
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
        changed
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

    #[cfg(test)]
    pub fn trigger_power_guard_stop_for_test(&mut self) {
        if let Some(act) = self.pc.power_guard_ui.controller.stop() {
            self.handle_power_guard_action(&act, Severity::Normal, &[]);
        }
    }

    #[cfg(test)]
    pub fn trigger_power_guard_game_exit_for_test(&mut self) {
        if let Some(act) = self.pc.power_guard_ui.controller.on_game_exited() {
            self.handle_power_guard_action(&act, Severity::Normal, &[]);
        }
    }

    fn current_power_guard_ts_ms(&self) -> u64 {
        self.pc
            .power_guard_ui
            .live
            .snapshot
            .as_ref()
            .map(|s| s.timestamp_ms)
            .unwrap_or(0)
    }

    fn record_power_guard_event(&mut self, kind: &str, detail: String) {
        let ts = self.current_power_guard_ts_ms();
        let pg = &mut self.pc.power_guard_ui;
        pg.controller.record_event(ts, kind, detail, &mut pg.live);
    }

    fn play_power_guard_alert_sound(&mut self) {
        if self.pc.power_guard_ui.dev_mock_actions {
            self.pc.power_guard_ui.mock_alert_sounds += 1;
            return;
        }
        #[cfg(windows)]
        unsafe {
            MessageBeep(0x0000_0010);
        }
    }

    pub fn cancel_power_guard_shutdown(&mut self) {
        if !self
            .pc
            .power_guard_ui
            .controller
            .cancel_shutdown_countdown()
        {
            return;
        }
        self.pc.power_guard_ui.live.shutdown_countdown_secs = None;
        let msg = "Stage 3 shutdown countdown cancelled by user".to_string();
        self.pc.power_guard_ui.live.last_action_note = Some(msg.clone());
        self.record_power_guard_event("shutdown-cancelled", msg.clone());
        self.dismiss_notice_by_key("power-guard-stage3");
        self.push_rich_notice(RichNoticeSpec {
            key_id: Some("power-guard-stage3-cancelled".into()),
            title: Some("Power guard".into()),
            where_label: Some("PC".into()),
            text: msg,
            tone: NoticeTone::Neutral,
            sticky: Some(false),
            ..Default::default()
        });
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
                self.record_power_guard_event("fault-alert", first_msg.clone());
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
            GuardAction::LowerPowerLimit {
                target_w,
                previous_w,
            } => {
                if !self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit {
                    return;
                }
                let gpu_name = self
                    .pc
                    .power_guard_ui
                    .live
                    .snapshot
                    .as_ref()
                    .map(|s| s.name.clone())
                    .unwrap_or_else(|| "GPU".into());
                let original_w = previous_w.unwrap_or(*target_w);
                let ts = self.current_power_guard_ts_ms();
                let _ = self
                    .pc
                    .power_guard_ui
                    .journal
                    .write(&PowerLimitJournalEntry {
                        gpu_name,
                        original_limit_w: original_w,
                        lowered_limit_w: *target_w,
                        written_at_ms: ts,
                    });

                let (ok, msg) = if self.pc.power_guard_ui.dev_mock_actions {
                    self.pc.power_guard_ui.mock_lowered_limits_w.push(*target_w);
                    (
                        true,
                        format!(
                            "Mock lowered GPU power limit to {target_w:.0} W (original {original_w:.0} W journaled)"
                        ),
                    )
                } else {
                    match try_set_nvidia_power_limit_w(*target_w) {
                        Ok(applied) => (
                            true,
                            format!(
                                "Lowered GPU power limit to {applied:.0} W (original {original_w:.0} W journaled)"
                            ),
                        ),
                        Err(err) => {
                            let _ = self.pc.power_guard_ui.journal.clear();
                            (false, format!("Could not lower GPU power limit: {err}"))
                        }
                    }
                };
                if ok {
                    self.record_power_guard_event("power-limit-lowered", msg.clone());
                }
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
            GuardAction::RestorePowerLimit { original_w, reason } => {
                let (ok, msg) = if self.pc.power_guard_ui.dev_mock_actions {
                    self.pc
                        .power_guard_ui
                        .mock_restored_limits_w
                        .push(*original_w);
                    (
                        true,
                        format!(
                            "Mock restored original GPU power limit to {original_w:.0} W ({})",
                            reason.as_str()
                        ),
                    )
                } else {
                    match try_set_nvidia_power_limit_w(*original_w) {
                        Ok(applied) => (
                            true,
                            format!(
                                "Restored original GPU power limit to {applied:.0} W ({})",
                                reason.as_str()
                            ),
                        ),
                        Err(err) => (false, format!("Could not restore GPU power limit: {err}")),
                    }
                };
                if ok {
                    let _ = self.pc.power_guard_ui.journal.clear();
                    self.record_power_guard_event("power-limit-restored", msg.clone());
                }
                self.pc.power_guard_ui.live.last_action_note = Some(msg.clone());
                self.dismiss_notice_by_key("power-guard-stage1");
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some("power-guard-restore".into()),
                    title: Some("Power guard · Restored".into()),
                    where_label: Some("PC".into()),
                    text: msg,
                    tone: if ok {
                        NoticeTone::Good
                    } else {
                        NoticeTone::Bad
                    },
                    sticky: Some(!ok),
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
                self.record_power_guard_event("game-closed", msg.clone());
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
            GuardAction::ShutdownCountdown {
                remaining_secs,
                first_tick,
                ..
            } => {
                if !self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc {
                    return;
                }
                self.pc.power_guard_ui.live.shutdown_countdown_secs = Some(*remaining_secs);
                let msg = format!(
                    "Shutting down PC in {remaining_secs} s due to sustained critical GPU fault."
                );
                if *first_tick {
                    self.play_power_guard_alert_sound();
                    self.record_power_guard_event(
                        "shutdown-countdown-started",
                        format!("30 s cancelable shutdown countdown started ({first_msg})"),
                    );
                }
                self.pc.power_guard_ui.live.last_action_note = Some(msg.clone());
                self.push_rich_notice(RichNoticeSpec {
                    key_id: Some("power-guard-stage3".into()),
                    title: Some(format!("Power guard · Shutdown in {remaining_secs} s")),
                    where_label: Some("PC".into()),
                    text: msg,
                    tone: NoticeTone::Bad,
                    sticky: Some(true),
                    action: Some(NoticeActionSpec {
                        label: "Cancel shutdown".into(),
                        primary: false,
                        danger: true,
                        busy: false,
                        confirm: None,
                        kind: NoticeActionKind::CancelPowerGuardShutdown,
                    }),
                    ..Default::default()
                });
            }
            GuardAction::ShutdownPc => {
                if !self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc {
                    return;
                }
                self.pc.power_guard_ui.live.shutdown_countdown_secs = None;
                let msg: String = if self.pc.power_guard_ui.dev_mock_actions {
                    self.pc.power_guard_ui.mock_pc_shutdowns += 1;
                    "Mock graceful Windows shutdown triggered after 30 s countdown".into()
                } else {
                    let ok = Command::new("shutdown.exe")
                        .args([
                            "/s",
                            "/t",
                            "0",
                            "/d",
                            "p:0:0",
                            "/c",
                            "Exo Power Guard: sustained critical GPU power or connector fault",
                        ])
                        .status()
                        .is_ok_and(|s| s.success());
                    if ok {
                        "Initiated graceful Windows shutdown after 30 s countdown".into()
                    } else {
                        "Windows rejected shutdown request".into()
                    }
                };
                self.record_power_guard_event("shutdown-executed", msg.clone());
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
        if self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit {
            self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit = false;
            self.pc.power_guard_ui.pending_confirm = None;
            let pol = self.pc.power_guard_ui.live.policy.clone();
            self.pc.power_guard_ui.controller.set_policy(pol);
        } else {
            self.pc.power_guard_ui.pending_confirm = Some(PendingOptInConfirm::LowerPowerLimit);
        }
    }

    pub fn toggle_power_guard_close_game(&mut self) {
        if self.pc.power_guard_ui.live.policy.opt_in_close_game {
            self.pc.power_guard_ui.live.policy.opt_in_close_game = false;
            self.pc.power_guard_ui.pending_confirm = None;
            let pol = self.pc.power_guard_ui.live.policy.clone();
            self.pc.power_guard_ui.controller.set_policy(pol);
        } else {
            self.pc.power_guard_ui.pending_confirm = Some(PendingOptInConfirm::CloseGame);
        }
    }

    pub fn toggle_power_guard_shutdown(&mut self) {
        if self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc {
            self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc = false;
            self.pc.power_guard_ui.pending_confirm = None;
            let pol = self.pc.power_guard_ui.live.policy.clone();
            self.pc.power_guard_ui.controller.set_policy(pol);
        } else {
            self.pc.power_guard_ui.pending_confirm = Some(PendingOptInConfirm::ShutdownPc);
        }
    }

    pub fn confirm_power_guard_opt_in(&mut self) {
        let Some(stage) = self.pc.power_guard_ui.pending_confirm.take() else {
            return;
        };
        match stage {
            PendingOptInConfirm::LowerPowerLimit => {
                self.pc.power_guard_ui.live.policy.opt_in_lower_power_limit = true;
            }
            PendingOptInConfirm::CloseGame => {
                self.pc.power_guard_ui.live.policy.opt_in_close_game = true;
            }
            PendingOptInConfirm::ShutdownPc => {
                self.pc.power_guard_ui.live.policy.opt_in_shutdown_pc = true;
            }
        }
        let pol = self.pc.power_guard_ui.live.policy.clone();
        self.pc.power_guard_ui.controller.set_policy(pol);
        self.record_power_guard_event("opt-in-enabled", stage.title().to_string());
    }

    pub fn cancel_power_guard_opt_in(&mut self) {
        self.pc.power_guard_ui.pending_confirm = None;
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

fn confirm_sheet_view(stage: PendingOptInConfirm, key_prefix: &str) -> El {
    let is_danger = stage == PendingOptInConfirm::ShutdownPc;
    col()
        .key(format!("{key_prefix}-confirm-sheet"))
        .w_full()
        .px(12.0)
        .py(10.0)
        .gap(8.0)
        .radius(RADIUS_CARD)
        .continuous_corners()
        .fill(subtle_fill())
        .border(1.0, if is_danger { bad() } else { float_line() })
        .child(text(stage.title()).font(type_control()).color(FG))
        .child(
            text(stage.detail())
                .font(type_small())
                .color(fg_muted())
                .wrap(),
        )
        .child(
            row()
                .w_full()
                .gap(8.0)
                .child(
                    menu_btn(
                        format!("{key_prefix}-confirm-enable"),
                        stage.confirm_label(),
                        !is_danger,
                        is_danger,
                        false,
                    )
                    .on_press(|s: &mut AppState| s.confirm_power_guard_opt_in()),
                )
                .child(
                    menu_btn(
                        format!("{key_prefix}-confirm-cancel"),
                        "Cancel",
                        false,
                        false,
                        false,
                    )
                    .on_press(|s: &mut AppState| s.cancel_power_guard_opt_in()),
                ),
        )
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
                        .font(type_title())
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
            GuardAction::ShutdownCountdown { remaining_secs, .. } => {
                format!(" · Shutdown in {remaining_secs} s")
            }
            _ => String::new(),
        };
        left_info = left_info.child(
            text(format!("{}{extra}", first.message))
                .font(type_small())
                .color(alert_color)
                .wrap(),
        );
    }

    if let Some(rem) = pg.live.shutdown_countdown_secs {
        left_info = left_info.child(
            row().w_full().gap(8.0).center_y().child(
                menu_btn(
                    "pg-card-cancel-shutdown".into(),
                    format!("Cancel shutdown ({rem} s)"),
                    false,
                    true,
                    false,
                )
                .on_press(|s: &mut AppState| s.cancel_power_guard_shutdown()),
            ),
        );
    }

    if let Some(ref note) = pg.live.last_action_note {
        left_info = left_info.child(text(note.clone()).font(type_small()).color(FG).wrap());
    }

    let opt_in_btn = |key: &str, label: &str, active: bool, danger: bool| {
        let txt = format!("{label}: {}", if active { "On" } else { "Off" });
        menu_btn(key.into(), txt, active && !danger, active && danger, false)
    };

    let mut right_controls = col()
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

    if let Some(stage) = pg.pending_confirm {
        right_controls = right_controls.child(confirm_sheet_view(stage, "pg-card"));
    }

    col()
        .key("exo-ai-power-guard-card")
        .w_full()
        .h_full()
        .pt(16.0)
        .px(24.0)
        .pb(16.0)
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
                .gap(16.0)
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

    let mut out = col()
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
            Some("Opt-in · restores original on clear, exit, or crash".into()),
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
            Some("Opt-in · last resort (30 s cancelable countdown)".into()),
            None,
            menu_switch(
                "pg-menu-stage3".into(),
                "Shut down PC",
                pol.opt_in_shutdown_pc,
                false,
            )
            .on_press(|s: &mut AppState| s.toggle_power_guard_shutdown()),
        ));

    if let Some(stage) = pg.pending_confirm {
        out = out.child(confirm_sheet_view(stage, "pg-menu"));
    }
    out
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

    fn temp_journal_path(tag: &str) -> PathBuf {
        std::env::temp_dir().join(format!("exo-ui-pg-{tag}-{}.journal", std::process::id()))
    }

    #[test]
    fn zero_cost_while_hidden_gate_checks_room_and_running_games() {
        assert!(!PowerGuardUiState::should_poll(Room::Library, false));
        assert!(PowerGuardUiState::should_poll(Room::ExoAi, false));
        assert!(PowerGuardUiState::should_poll(Room::Library, true));

        let mut s = AppState::with_demo_games();
        s.pc.power_guard_ui
            .set_journal_path(temp_journal_path("hidden"));
        s.pc.power_guard_ui.dev_mock_actions = true;
        s.room = Room::Library;
        for g in &mut s.games {
            g.is_running = false;
        }
        assert!(!s.tick_power_guard(10.0));
        assert!(!s.pc.power_guard_ui.is_sidecar_running());
    }

    #[test]
    fn opt_in_toggles_require_plain_confirm_sheet_before_enabling() {
        let mut s = AppState::with_demo_games();
        s.pc.power_guard_ui.dev_mock_actions = true;

        s.toggle_power_guard_lower_power();
        assert!(!s.pc.power_guard_ui.live.policy.opt_in_lower_power_limit);
        assert_eq!(
            s.pc.power_guard_ui.pending_confirm,
            Some(PendingOptInConfirm::LowerPowerLimit)
        );
        let _ = card_view(&s);
        let _ = view(&s);

        s.cancel_power_guard_opt_in();
        assert!(!s.pc.power_guard_ui.live.policy.opt_in_lower_power_limit);
        assert_eq!(s.pc.power_guard_ui.pending_confirm, None);

        s.toggle_power_guard_lower_power();
        s.confirm_power_guard_opt_in();
        assert!(s.pc.power_guard_ui.live.policy.opt_in_lower_power_limit);
        assert_eq!(s.pc.power_guard_ui.pending_confirm, None);

        s.toggle_power_guard_close_game();
        assert!(!s.pc.power_guard_ui.live.policy.opt_in_close_game);
        assert_eq!(
            s.pc.power_guard_ui.pending_confirm,
            Some(PendingOptInConfirm::CloseGame)
        );
        s.confirm_power_guard_opt_in();
        assert!(s.pc.power_guard_ui.live.policy.opt_in_close_game);

        s.toggle_power_guard_shutdown();
        assert!(!s.pc.power_guard_ui.live.policy.opt_in_shutdown_pc);
        assert_eq!(
            s.pc.power_guard_ui.pending_confirm,
            Some(PendingOptInConfirm::ShutdownPc)
        );
        s.confirm_power_guard_opt_in();
        assert!(s.pc.power_guard_ui.live.policy.opt_in_shutdown_pc);

        s.toggle_power_guard_shutdown();
        assert!(!s.pc.power_guard_ui.live.policy.opt_in_shutdown_pc);
    }

    #[test]
    fn default_opt_ins_are_off_and_dev_mock_escalates_restores_and_cancels_shutdown() {
        let jpath = temp_journal_path("escalate");
        let _ = std::fs::remove_file(&jpath);

        let mut s = AppState::with_demo_games();
        s.pc.power_guard_ui.dev_mock_actions = true;
        s.pc.power_guard_ui.set_journal_path(jpath.clone());

        let pol = &s.pc.power_guard_ui.live.policy;
        assert!(pol.alerts_enabled);
        assert!(!pol.opt_in_lower_power_limit);
        assert!(!pol.opt_in_close_game);
        assert!(!pol.opt_in_shutdown_pc);

        s.toggle_power_guard_lower_power();
        s.confirm_power_guard_opt_in();
        s.toggle_power_guard_close_game();
        s.confirm_power_guard_opt_in();
        s.toggle_power_guard_shutdown();
        s.confirm_power_guard_opt_in();

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
        assert!(s.pc.power_guard_ui.journal_entry().is_some());
        assert_eq!(s.pc.power_guard_ui.mock_game_closes, 0);
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 0);

        s.ingest_power_guard_snapshot_for_test(make_fault(20_000));
        assert_eq!(s.pc.power_guard_ui.mock_game_closes, 1);
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 0);

        s.ingest_power_guard_snapshot_for_test(make_fault(25_000));
        assert_eq!(s.pc.power_guard_ui.live.shutdown_countdown_secs, Some(30));
        assert_eq!(s.pc.power_guard_ui.mock_alert_sounds, 1);
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 0);
        let _ = card_view(&s);

        s.cancel_power_guard_shutdown();
        assert_eq!(s.pc.power_guard_ui.live.shutdown_countdown_secs, None);
        s.ingest_power_guard_snapshot_for_test(make_fault(60_000));
        assert_eq!(s.pc.power_guard_ui.mock_pc_shutdowns, 0);
        assert!(
            s.pc.power_guard_ui
                .live
                .session_log
                .iter()
                .any(|e| e.kind == "shutdown-cancelled")
        );

        let mut cleared = make_snap(
            61_000,
            GpuVendor::Nvidia,
            BackendKind::NvidiaNvmlNvapi,
            "NVIDIA GeForce RTX 3070",
        );
        cleared.enforced_power_limit_w = Some(168.0);
        s.ingest_power_guard_snapshot_for_test(cleared);
        assert_eq!(s.pc.power_guard_ui.mock_restored_limits_w, vec![240.0]);
        assert!(s.pc.power_guard_ui.journal_entry().is_none());

        let _ = std::fs::remove_file(&jpath);
    }

    #[test]
    fn stage1_restores_on_game_exit_stop_and_startup_crash_journal_via_mock() {
        let jpath = temp_journal_path("restore");
        let _ = std::fs::remove_file(&jpath);

        let mut s = AppState::with_demo_games();
        s.pc.power_guard_ui.dev_mock_actions = true;
        s.pc.power_guard_ui.set_journal_path(jpath.clone());
        s.toggle_power_guard_lower_power();
        s.confirm_power_guard_opt_in();
        s.pc.power_guard_ui.live.policy.stage_step_ms = 2_000;
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

        s.ingest_power_guard_snapshot_for_test(make_fault(1_000));
        s.ingest_power_guard_snapshot_for_test(make_fault(3_000));
        assert_eq!(s.pc.power_guard_ui.mock_lowered_limits_w, vec![168.0]);
        s.trigger_power_guard_game_exit_for_test();
        assert_eq!(s.pc.power_guard_ui.mock_restored_limits_w, vec![240.0]);
        assert!(s.pc.power_guard_ui.journal_entry().is_none());

        s.ingest_power_guard_snapshot_for_test(make_fault(10_000));
        s.ingest_power_guard_snapshot_for_test(make_fault(12_000));
        s.trigger_power_guard_stop_for_test();
        assert_eq!(
            s.pc.power_guard_ui.mock_restored_limits_w,
            vec![240.0, 240.0]
        );
        assert!(s.pc.power_guard_ui.journal_entry().is_none());

        s.ingest_power_guard_snapshot_for_test(make_fault(20_000));
        s.ingest_power_guard_snapshot_for_test(make_fault(22_000));
        assert!(s.pc.power_guard_ui.journal_entry().is_some());
        drop(s);

        let mut restarted = AppState::with_demo_games();
        restarted.pc.power_guard_ui.dev_mock_actions = true;
        restarted.pc.power_guard_ui.set_journal_path(jpath.clone());
        assert!(restarted.recover_power_guard_journal_if_needed());
        assert_eq!(
            restarted.pc.power_guard_ui.mock_restored_limits_w,
            vec![240.0]
        );
        assert!(restarted.pc.power_guard_ui.journal_entry().is_none());
        let _ = std::fs::remove_file(&jpath);
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
